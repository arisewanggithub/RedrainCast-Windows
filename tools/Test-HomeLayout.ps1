param(
    [Parameter(Mandatory = $true)][string]$App,
    [Parameter(Mandatory = $true)][string]$ReportDirectory
)
$ErrorActionPreference = 'Stop'
$App = (Resolve-Path -LiteralPath $App).Path
if (-not [IO.Path]::IsPathRooted($ReportDirectory)) {
    $ReportDirectory = Join-Path (Get-Location) $ReportDirectory
}
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$gatePath = Join-Path $ReportDirectory 'interactive-gate.json'
$reportPath = Join-Path $ReportDirectory 'native-layout.json'
if ((Test-Path $gatePath) -or (Test-Path $reportPath)) { throw 'Use a fresh report directory; previous proof is immutable.' }

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class HomeLayoutGate {
    [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, int bytes, out int needed);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
}
'@
function Read-Desktop {
    $desktop = [HomeLayoutGate]::OpenInputDesktop(0, $false, 1)
    $name = New-Object Text.StringBuilder 256
    $needed = 0
    try {
        $read = $desktop -ne [IntPtr]::Zero -and [HomeLayoutGate]::GetUserObjectInformation($desktop, 2, $name, 512, [ref]$needed)
        if (-not $read) { return 'unavailable' }
        return $name.ToString()
    } finally {
        if ($desktop -ne [IntPtr]::Zero) { [void][HomeLayoutGate]::CloseDesktop($desktop) }
    }
}
$form = New-Object Windows.Forms.Form
$form.Text = 'RedrainCast owned layout-only gate'
$form.Width = 560
$form.Height = 160
$form.TopMost = $true
$process = $null
$cleanup = @{ ownProcessStarted = $false; ownProcessExited = $false; forcedTermination = $false; userAppsTouched = $false }
try {
    $form.Show()
    $form.Activate()
    [Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 600
    [Windows.Forms.Application]::DoEvents()
    $desktop = Read-Desktop
    [uint32]$owner = 0
    [uint32]$foregroundOwner = 0
    $foreground = [HomeLayoutGate]::GetForegroundWindow()
    [void][HomeLayoutGate]::GetWindowThreadProcessId($form.Handle, [ref]$owner)
    [void][HomeLayoutGate]::GetWindowThreadProcessId($foreground, [ref]$foregroundOwner)
    $visible = [HomeLayoutGate]::IsWindowVisible($form.Handle)
    $minimized = [HomeLayoutGate]::IsIconic($form.Handle)
    $ac = [Windows.Forms.SystemInformation]::PowerStatus.PowerLineStatus -eq 'Online'
    $apps = @(Get-Process | Where-Object { $_.ProcessName -eq 'RedrainCast.App' -or $_.ProcessName -eq 'RedrainCast.CaptureWorker' })
    $workerProcesses = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'RedrainCast.App.exe'" -ErrorAction Stop |
        Where-Object { $_.CommandLine -match '(?:^|\s)--capture-worker(?:\s|$)' })
    $workers = @($apps | Where-Object ProcessName -eq 'RedrainCast.CaptureWorker').Count + $workerProcesses.Count
    $activeConnections = @(Get-NetTCPConnection -State Established -ErrorAction Stop |
        Where-Object { $apps.Id -contains $_.OwningProcess }).Count
    $gate = @{
        utc = [DateTime]::UtcNow.ToString('o')
        passed = ($desktop -eq 'Default' -and $visible -and -not $minimized -and $owner -eq $PID -and $foregroundOwner -eq $PID -and $foreground -eq $form.Handle -and $ac -and $workers -eq 0 -and $activeConnections -eq 0)
        acPower = $ac; activeWorkers = $workers; activeAppConnections = $activeConnections
        inputDesktop = $desktop; ownHelperPid = $PID; ownHelperHwnd = $form.Handle.ToInt64()
        ownerPid = $owner; foregroundPid = $foregroundOwner; foregroundHwnd = $foreground.ToInt64(); visible = $visible; minimized = $minimized
        session = (Get-Process -Id $PID).SessionId
        identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        userAppsTouched = $false; unlockWakeOrPermissionChanges = $false
    }
    $gate | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $gatePath
    if (-not $gate.passed) { throw 'Native layout gate blocked: Default desktop, foreground owned helper, AC power and no active app peer/worker required.' }
    $form.Close()
    if ((Read-Desktop) -ne 'Default') { throw 'Input desktop changed before launch.' }
    $process = Start-Process -FilePath $App -ArgumentList "--layout-self-test `"$reportPath`"" -PassThru
    $cleanup.ownProcessStarted = $true
    $cleanup.pid = $process.Id
    if (-not $process.WaitForExit(60000)) { throw 'Owned layout-only app exceeded the 60-second external deadline.' }
    $cleanup.ownProcessExited = $true
    $cleanup.exitCode = $process.ExitCode
    if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Owned app exited without a native layout report.' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $report.passed) { throw "Native home layout failed: $($report.failure)" }
    Write-Output "Native layout passed: $($report.measurements.Count) cases; own PID $($process.Id) exited."
} finally {
    $form.Close()
    $form.Dispose()
    if ($process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            Stop-Process -Id $process.Id
            [void]$process.WaitForExit(5000)
            $cleanup.forcedTermination = $true
        }
        $cleanup.ownProcessExited = $process.HasExited
    }
    $cleanup | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'cleanup.json')
}
