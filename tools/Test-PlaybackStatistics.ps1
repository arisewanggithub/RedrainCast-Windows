param(
    [Parameter(Mandatory = $true)][string]$App,
    [Parameter(Mandatory = $true)][string]$ReportDirectory
)
$ErrorActionPreference = 'Stop'
$App = (Resolve-Path -LiteralPath $App).Path
if (-not [IO.Path]::IsPathRooted($ReportDirectory)) { $ReportDirectory = Join-Path (Get-Location) $ReportDirectory }
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Use a fresh proof directory; previous artifacts are immutable.' }
New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PlaybackStatisticsGate {
    [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, int bytes, out int needed);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
}
'@
$form = New-Object Windows.Forms.Form
$form.Text = 'RedrainCast owned playback-statistics UI gate'
$form.Width = 480
$form.Height = 120
$process = $null
$cleanup = @{ ownProcessStarted = $false; ownProcessExited = $false; forcedTermination = $false; userAppsTouched = $false }
try {
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 300
    [Windows.Forms.Application]::DoEvents()
    $desktop = [PlaybackStatisticsGate]::OpenInputDesktop(0, $false, 1)
    $name = New-Object Text.StringBuilder 256
    $needed = 0
    try {
        $read = $desktop -ne [IntPtr]::Zero -and [PlaybackStatisticsGate]::GetUserObjectInformation($desktop, 2, $name, 512, [ref]$needed)
    } finally { if ($desktop -ne [IntPtr]::Zero) { [void][PlaybackStatisticsGate]::CloseDesktop($desktop) } }
    $foreground = [PlaybackStatisticsGate]::GetForegroundWindow()
    [uint32]$owner = 0
    [uint32]$foregroundOwner = 0
    [void][PlaybackStatisticsGate]::GetWindowThreadProcessId($form.Handle, [ref]$owner)
    [void][PlaybackStatisticsGate]::GetWindowThreadProcessId($foreground, [ref]$foregroundOwner)
    $ac = [Windows.Forms.SystemInformation]::PowerStatus.PowerLineStatus -eq 'Online'
    $apps = @(Get-Process | Where-Object { $_.ProcessName -eq 'RedrainCast.App' -or $_.ProcessName -eq 'RedrainCast.CaptureWorker' })
    $workers = @($apps | Where-Object ProcessName -eq 'RedrainCast.CaptureWorker').Count
    $activeConnections = @(Get-NetTCPConnection -State Established -ErrorAction Stop |
        Where-Object { $apps.Id -contains $_.OwningProcess }).Count
    $gate = @{
        utc = [DateTime]::UtcNow.ToString('o')
        inputDesktop = $name.ToString(); ownHelperPid = $PID; ownHelperHwnd = $form.Handle.ToInt64()
        foregroundPid = $foregroundOwner; foregroundHwnd = $foreground.ToInt64()
        passed = ($read -and $name.ToString() -eq 'Default' -and $owner -eq $PID -and
            [PlaybackStatisticsGate]::IsWindowVisible($form.Handle) -and -not [PlaybackStatisticsGate]::IsIconic($form.Handle) -and
            $foreground -ne [IntPtr]::Zero -and [PlaybackStatisticsGate]::IsWindowVisible($foreground) -and
            -not [PlaybackStatisticsGate]::IsIconic($foreground) -and $ac -and $workers -eq 0 -and $activeConnections -eq 0)
        acPower = $ac; activeWorkers = $workers; activeAppConnections = $activeConnections
        inputInjection = $false; capture = $false; audio = $false; unlockWakeOrPermissionChanges = $false
    }
    $gate | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $ReportDirectory 'interactive-gate.json')
    if (-not $gate.passed) { throw 'Default desktop / visible foreground / visible owned helper / AC / no active app peer or worker gate blocked. No workload started.' }
    $form.Close()
    $reportPath = Join-Path $ReportDirectory 'native-playback-statistics.json'
    $process = Start-Process -FilePath $App -ArgumentList "--playback-stats-self-test `"$reportPath`"" -PassThru
    $cleanup.ownProcessStarted = $true
    $cleanup.pid = $process.Id
    if (-not $process.WaitForExit(60000)) { throw 'Owned statistics UI proof exceeded its deadline.' }
    $cleanup.ownProcessExited = $true
    if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Owned app exited without a proof report.' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $report.passed) { throw "Native statistics UI proof failed: $($report.failure)" }
    Write-Output "Native statistics UI passed: $($report.checks.Count) checks; owned PID $($process.Id) exited."
} finally {
    $form.Close()
    $form.Dispose()
    if ($process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id; $cleanup.forcedTermination = $true }
        $cleanup.ownProcessExited = $process.HasExited
    }
    $cleanup | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $ReportDirectory 'cleanup.json')
}
