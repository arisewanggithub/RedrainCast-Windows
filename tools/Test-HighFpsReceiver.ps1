param(
    [Parameter(Mandatory = $true)][string]$App,
    [Parameter(Mandatory = $true)][string]$ReportDirectory
)
$ErrorActionPreference = 'Stop'
$App = (Resolve-Path -LiteralPath $App).Path
if (-not [IO.Path]::IsPathRooted($ReportDirectory)) { $ReportDirectory = Join-Path (Get-Location) $ReportDirectory }
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$gatePath = Join-Path $ReportDirectory 'interactive-gate.json'
$reportPath = Join-Path $ReportDirectory 'native-high-fps.json'
if ((Test-Path $gatePath) -or (Test-Path $reportPath)) { throw 'Use a fresh report directory; previous proof is immutable.' }

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class HighFpsGate {
    [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, int bytes, out int needed);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
}
'@
function Read-Desktop {
    $desktop = [HighFpsGate]::OpenInputDesktop(0, $false, 1)
    $name = New-Object Text.StringBuilder 256
    $needed = 0
    try {
        if ($desktop -eq [IntPtr]::Zero -or -not [HighFpsGate]::GetUserObjectInformation($desktop, 2, $name, 512, [ref]$needed)) { return 'unavailable' }
        return $name.ToString()
    } finally { if ($desktop -ne [IntPtr]::Zero) { [void][HighFpsGate]::CloseDesktop($desktop) } }
}
$form = $null
$process = $null
$cleanup = @{ ownProcessStarted = $false; ownProcessExited = $false; forcedTermination = $false; userAppsTouched = $false; inputInjected = $false; captureChildrenExpected = 0; helperDisposed = $false }
try {
    $desktop = Read-Desktop
    if ($desktop -ne 'Default') {
        @{ passed = $false; inputDesktop = $desktop; failure = 'Fresh Default input desktop required; no wake/unlock attempted.' } |
            ConvertTo-Json | Set-Content -LiteralPath $gatePath
        throw 'Interactive gate blocked before helper creation.'
    }
    $form = New-Object Windows.Forms.Form
    $form.Text = 'RedrainCast owned synthetic receiver-only gate'
    $form.Width = 560
    $form.Height = 160
    $form.ShowInTaskbar = $false
    [void][HighFpsGate]::ShowWindow($form.Handle, 4)
    [Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 600
    [Windows.Forms.Application]::DoEvents()
    $desktop = Read-Desktop
    [uint32]$owner = 0
    [uint32]$foregroundOwner = 0
    $foreground = [HighFpsGate]::GetForegroundWindow()
    [void][HighFpsGate]::GetWindowThreadProcessId($form.Handle, [ref]$owner)
    [void][HighFpsGate]::GetWindowThreadProcessId($foreground, [ref]$foregroundOwner)
    $visible = [HighFpsGate]::IsWindowVisible($form.Handle)
    $minimized = [HighFpsGate]::IsIconic($form.Handle)
    $foregroundVisible = $foreground -ne [IntPtr]::Zero -and [HighFpsGate]::IsWindowVisible($foreground)
    $foregroundMinimized = $foreground -eq [IntPtr]::Zero -or [HighFpsGate]::IsIconic($foreground)
    $gate = @{
        utc = [DateTime]::UtcNow.ToString('o')
        rule = 'Fresh Default input desktop; any visible/non-minimized foreground; owned helper visible/non-minimized and verified owner PID. No input injection or foreground ownership requirement.'
        passed = ($desktop -eq 'Default' -and $visible -and -not $minimized -and $owner -eq $PID -and $foregroundVisible -and -not $foregroundMinimized -and $foregroundOwner -ne 0)
        inputDesktop = $desktop; ownHelperPid = $PID; ownHelperHwnd = $form.Handle.ToInt64()
        ownerPid = $owner; foregroundPid = $foregroundOwner; foregroundHwnd = $foreground.ToInt64(); visible = $visible; minimized = $minimized
        foregroundVisible = $foregroundVisible; foregroundMinimized = $foregroundMinimized
        helperShownWithoutActivation = $true; inputInjected = $false
        session = (Get-Process -Id $PID).SessionId
        userAppsTouched = $false; unlockWakeOrPermissionChanges = $false
    }
    $gate | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $gatePath
    if (-not $gate.passed) { throw 'Fresh Default desktop, visible non-minimized foreground and visible owned helper required.' }
    $form.Close()
    if ((Read-Desktop) -ne 'Default') { throw 'Input desktop changed before launch.' }
    $process = Start-Process -FilePath $App -ArgumentList "--high-fps-self-test `"$reportPath`"" -PassThru
    $cleanup.ownProcessStarted = $true
    $cleanup.pid = $process.Id
    Start-Sleep -Milliseconds 800
    $process.Refresh()
    $cleanup.runningAfterLaunch = -not $process.HasExited
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)")
    $cleanup.observedCaptureChildren = @($children | Where-Object { $_.Name -match 'CaptureWorker|RedrainCast.App' }).Count
    $cleanup.observedChildren = $children.Count
    if ($children.Count -ne 0) { throw 'Receiver-only probe unexpectedly created a child process.' }
    if (-not $process.WaitForExit(45000)) { throw 'Owned synthetic receiver exceeded its 45-second external deadline.' }
    $cleanup.ownProcessExited = $true
    $cleanup.exitCode = $process.ExitCode
    if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Owned app exited without a native high-FPS report.' }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $report.passed) { throw "Native high-FPS probe failed: $($report.failure)" }
    Write-Output "Native high-FPS receiver passed: $($report.measurements.Count) rate/generation cases; own PID $($process.Id) exited."
} finally {
    if ($form) { $form.Close(); $form.Dispose(); $cleanup.helperDisposed = $true }
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
