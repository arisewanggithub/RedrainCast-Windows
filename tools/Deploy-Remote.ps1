[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$BuiltDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]*$')][string]$RemoteHost,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string]$SSHUser,
    [string]$KeyPath = "$HOME\.ssh\id_ed25519",
    [string]$RemoteDirectory,
    [ValidateRange(0, 65535)][int]$InteractiveSessionId = 0
)
$ErrorActionPreference = 'Stop'
$total = [Diagnostics.Stopwatch]::StartNew()
$BuiltDirectory = (Resolve-Path -LiteralPath $BuiltDirectory).Path
$KeyPath = (Resolve-Path -LiteralPath $KeyPath).Path
if ($RemoteDirectory) {
    if ($RemoteDirectory -notmatch '^[A-Za-z]:\\[A-Za-z0-9_. -]+(?:\\[A-Za-z0-9_. -]+)+$' -or
        @($RemoteDirectory.Split('\') | Where-Object { $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count) {
        throw 'RemoteDirectory must be an absolute, non-root Windows directory with safe path components.'
    }
}
$rid = Split-Path -Leaf $BuiltDirectory
if ($rid -notin @('win-arm64', 'win-x64')) { throw 'BuiltDirectory must be a win-arm64 or win-x64 self-contained app output.' }
$required = @(
    'RedrainCast.App.exe', 'RedrainCast.App.dll', 'RedrainCast.App.pri',
    'RedrainCast.App.deps.json', 'RedrainCast.App.runtimeconfig.json',
    'RedrainCast.Receive.Core.dll', 'RedrainCast.Receive.Media.dll',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
    'Microsoft.WindowsAppRuntime.dll',
    'capture-worker\RedrainCast.CaptureWorker.exe', 'capture-worker\RedrainCast.CaptureWorker.dll',
    'capture-worker\RedrainCast.CaptureWorker.deps.json', 'capture-worker\RedrainCast.CaptureWorker.runtimeconfig.json',
    'capture-worker\coreclr.dll', 'capture-worker\hostfxr.dll', 'capture-worker\hostpolicy.dll',
    'capture-worker\System.Private.CoreLib.dll',
    "native\$rid\avcodec-61.dll", "native\$rid\avutil-59.dll", "native\$rid\swresample-5.dll"
)
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $BuiltDirectory $name) -PathType Leaf)) { throw "Built runtime file missing: $name" }
}
foreach ($name in @('RedrainCast.App.runtimeconfig.json', 'capture-worker\RedrainCast.CaptureWorker.runtimeconfig.json')) {
    $runtime = Get-Content -LiteralPath (Join-Path $BuiltDirectory $name) -Raw | ConvertFrom-Json
    if (-not $runtime.runtimeOptions.includedFrameworks) { throw "Runtime is not self-contained: $name" }
}
$files = @(Get-ChildItem -LiteralPath $BuiltDirectory -Recurse -File)
if (@(Get-ChildItem -LiteralPath $BuiltDirectory -Recurse -Attributes ReparsePoint).Count) {
    throw 'Build output contains reparse points; package only real runtime files.'
}
foreach ($tool in @('tar.exe', 'ssh.exe', 'scp.exe')) { Get-Command $tool -ErrorAction Stop | Out-Null }
if (-not $PSCmdlet.ShouldProcess("$SSHUser@$RemoteHost", 'Close ALL RedrainCast instances (including active casts), copy existing build and launch in the interactive desktop')) {
    Write-Warning 'Remote safety prerequisite: use a distinct SSH controller account whose SID differs from the interactive desktop owner. Same-SID deployment is unsupported; offline validation cannot resolve remote SIDs.'
    return
}

$stamp = 'RedrainCast-Dev-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$temp = Join-Path ([IO.Path]::GetTempPath()) $stamp
New-Item -ItemType Directory -Path $temp | Out-Null
$archive = Join-Path $temp "$stamp.zip"
$helperName = "$stamp.ps1"
$helperPath = Join-Path $temp $helperName

function Invoke-NativeBounded([string]$File, [string[]]$Arguments, [int]$TimeoutSeconds, [string]$InputText) {
    $quoted = @($Arguments | ForEach-Object {
        if ($_ -match '["\r\n]') { throw 'Unexpected quote or newline in native argument.' }
        '"' + $_ + '"'
    })
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = (Get-Command $File -ErrorAction Stop).Source
    $start.Arguments = $quoted -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Could not start $File." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($InputText) { $process.StandardInput.Write($InputText) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            throw "$File exceeded $TimeoutSeconds seconds."
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "$File failed ($($process.ExitCode)): $errors`n$output" }
        if ($errors) { Write-Warning $errors.Trim() }
        return $output
    } finally {
        $process.Dispose()
    }
}

$lifecycle = @'
function Split-DeployCommandLine([string]$CommandLine) {
    if (-not ('RedrainCastDeployArguments' -as [type])) {
        Add-Type @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class RedrainCastDeployArguments {
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static string[] Split(string command) {
        int count;
        IntPtr memory = CommandLineToArgvW(command, out count);
        if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            string[] result = new string[count];
            for (int i = 0; i < count; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size));
            return result;
        } finally { LocalFree(memory); }
    }
}
"@
    }
    return [RedrainCastDeployArguments]::Split($CommandLine)
}
function Get-DeployHelperArguments($Request) {
    return @('-NoProfile', '-WindowStyle', 'Hidden', '-File', $Request.Helper,
        '-Report', $Request.Report, '-RequestId', $Request.RequestId,
        '-DeadlineTicks', [string]$Request.DeadlineTicks, '-RequestCancel', $Request.RequestCancel,
        '-ExpectedSessionId', [string]$Request.ExpectedSessionId, '-ExpectedSID', $Request.ExpectedSID)
}
function Test-DeployHelperCommandLine([string]$CommandLine, $Request) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    $actual = @(Split-DeployCommandLine $CommandLine)
    $expected = @($Request.Executable) + @(Get-DeployHelperArguments $Request)
    if ($actual.Count -ne $expected.Count) { return $false }
    for ($index = 0; $index -lt $expected.Count; $index++) {
        $comparison = if ($index -eq 0) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not [string]::Equals($actual[$index], $expected[$index], $comparison)) { return $false }
    }
    return $true
}
function Get-DeployHelperProcessRecords($Request, [int]$ProcessId = 0) {
    $filter = if ($ProcessId -gt 0) { "ProcessId=$ProcessId" } else { "Name='powershell.exe'" }
    $ownerProperty = if ($ProcessId -gt 0) {
        ',@{Name="OwnerSID";Expression={$owner=Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid -ErrorAction Stop; if ($owner.ReturnValue -ne 0) {throw "Cannot verify helper owner."}; $owner.Sid}}'
    } else { '' }
    $query = '$ErrorActionPreference="Stop"; Get-CimInstance Win32_Process -Filter "' + $filter +
        '" -OperationTimeoutSec 3 | Select-Object ProcessId,ExecutablePath,SessionId,CommandLine,@{Name="StartTicks";Expression={$_.CreationDate.ToUniversalTime().Ticks}}' +
        $ownerProperty + ' | ConvertTo-Json -Compress'
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $PSHOME 'powershell.exe'
    $start.Arguments = '-NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($query))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $queryProcess = New-Object Diagnostics.Process
    $queryProcess.StartInfo = $start
    $started = $false
    try {
        if (-not $queryProcess.Start()) { throw 'Helper retirement unconfirmed: command-line query could not start.' }
        $started = $true
        $null = $queryProcess.Handle
        $output = $queryProcess.StandardOutput.ReadToEndAsync()
        $errors = $queryProcess.StandardError.ReadToEndAsync()
        if (-not $queryProcess.WaitForExit(3000)) {
            # This child was created here, not selected from a writable PID report.
            Stop-DeployPinnedProcess $queryProcess
            throw 'Helper retirement unconfirmed: actual command-line query exceeded three seconds.'
        }
        if ($queryProcess.ExitCode -ne 0) {
            throw "Helper retirement unconfirmed: actual command-line query failed: $($errors.GetAwaiter().GetResult())"
        }
        $text = $output.GetAwaiter().GetResult()
        if ($text) {
            $records = @(foreach ($record in ($text | ConvertFrom-Json)) { $record })
            if ($ProcessId -eq 0 -and @($records | Where-Object { -not $_.CommandLine }).Count) {
                throw 'Helper retirement unconfirmed: discovery returned a process with unavailable actual command line.'
            }
            return $records
        }
    } finally {
        try {
            if ($started -and -not $queryProcess.HasExited) { Stop-DeployPinnedProcess $queryProcess }
        } finally { $queryProcess.Dispose() }
    }
}
function Assert-DeploySession([int]$ActualSession, [int]$ExpectedSession) {
    if ($ExpectedSession -le 0 -or $ActualSession -ne $ExpectedSession) {
        throw "Deployment helper interactive session mismatch: expected $ExpectedSession, actual $ActualSession; no close or launch permitted."
    }
}
function Invoke-DeployTask($RegisteredTask, [int]$SessionId, [string]$SID) {
    if ($SessionId -le 0 -or -not $SID) { throw 'Interactive task requires a resolved session and SID.' }
    # TASK_RUN_USE_SESSION_ID: RunEx otherwise ignores the requested desktop.
    $TASK_RUN_USE_SESSION_ID = 4
    $null = $RegisteredTask.RunEx($null, $TASK_RUN_USE_SESSION_ID, $SessionId, $SID)
}
function Get-DeployPinnedProcess($Identity, [long]$ToleranceTicks = 10000) {
    try {
        $process = Get-Process -Id $Identity.ProcessId -ErrorAction Stop
    } catch {
        if ($_.FullyQualifiedErrorId -like 'NoProcessFoundForGivenId,*') { return $null }
        throw
    }
    if (-not $process) { return $null }
    $retained = $false
    try {
        try { $null = $process.Handle } catch [InvalidOperationException] {
            if ($process.HasExited) { return $null }
            throw
        } catch [ComponentModel.Win32Exception] {
            if ($process.HasExited) { return $null }
            throw
        }
        if ($process.HasExited) { return $null }
        try {
            $difference = [Math]::Abs($process.StartTime.ToUniversalTime().Ticks - $Identity.StartTicks)
            $path = $process.Path
        } catch [InvalidOperationException] {
            if ($process.HasExited) { return $null }
            throw
        } catch [ComponentModel.Win32Exception] {
            if ($process.HasExited) { return $null }
            throw
        }
        if ($process.HasExited) { return $null }
        if ($path -ne $Identity.ExecutablePath -or $difference -gt $ToleranceTicks) {
            throw "Process identity changed for PID $($Identity.ProcessId); refusing termination."
        }
        $retained = $true
        return $process
    } finally {
        if (-not $retained) { $process.Dispose() }
    }
}
function Stop-DeployPinnedProcess($Process) {
    if ($Process.HasExited) { return }
    try { $Process.Kill() } catch [InvalidOperationException] {
        if (-not $Process.HasExited) { throw }
    } catch [ComponentModel.Win32Exception] {
        if (-not $Process.HasExited) { throw }
    }
    if (-not $Process.WaitForExit(5000)) { throw "Target PID $($Process.Id) did not exit within five seconds." }
}
function Assert-DeployController([string]$InteractiveSID) {
    $controllerSID = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if (-not $InteractiveSID -or $InteractiveSID -eq $controllerSID) {
        throw 'Deployment requires a distinct SSH controller account with a different SID from the interactive desktop owner. Same-SID deployment is unsupported: the Limited token can modify controller-owned requests and replace paths beneath its own profile.'
    }
}
function Resolve-DeployDesktop([int]$SessionId) {
    $explorers = @(Get-CimInstance Win32_Process -Filter "name='explorer.exe'" |
        Where-Object { $_.SessionId -gt 0 -and ($SessionId -eq 0 -or $_.SessionId -eq $SessionId) })
    if (@($explorers.SessionId | Select-Object -Unique).Count -ne 1) {
        throw 'Expected one unambiguous interactive desktop; specify InteractiveSessionId.'
    }
    $owner = Invoke-CimMethod -InputObject $explorers[0] -MethodName GetOwner
    if ($owner.ReturnValue -ne 0) { throw 'Cannot resolve interactive desktop owner.' }
    $account = "$($owner.Domain)\$($owner.User)"
    $sid = (New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier]).Value
    Assert-DeployController $sid
    return [pscustomobject]@{ SessionId = [int]$explorers[0].SessionId; Account = $account; SID = $sid }
}
function Assert-DeployRecoveryRequest($Request, $Config, $Desktop, [string]$RequestDirectory) {
    Assert-DeployController $Desktop.SID
    $profile = Get-CimInstance Win32_UserProfile -Filter "SID='$($Desktop.SID)'"
    if (-not $profile.LocalPath) { throw 'Helper retirement unconfirmed: interactive owner profile missing.' }
    $base = if ($Config.Directory) { $Config.Directory } else { Join-Path $profile.LocalPath 'RedrainCast-DevDeploy' }
    $expected = @{
        RequestId = $Config.Stamp
        Executable = (Join-Path $PSHOME 'powershell.exe')
        Helper = (Join-Path (Join-Path $base $Config.Stamp) ($Config.Stamp + '.ps1'))
        Report = (Join-Path (Join-Path $RequestDirectory 'reports') 'deployment-result.json')
        RequestCancel = (Join-Path $RequestDirectory 'cancel')
        ExpectedSID = $Desktop.SID
    }
    foreach ($name in $expected.Keys) {
        if ($Request.$name -isnot [string] -or
            -not [string]::Equals($Request.$name, $expected[$name], [StringComparison]::Ordinal)) {
            throw "Helper retirement unconfirmed: untrusted recovery request field $name."
        }
    }
    if ($Request.ExpectedSessionId -isnot [int] -or $Request.ExpectedSessionId -ne $Desktop.SessionId -or
        $Request.DeadlineTicks -isnot [long] -or $Request.DeadlineTicks -le 0 -or
        $Request.DeadlineTicks -gt [DateTime]::MaxValue.Ticks) {
        throw 'Helper retirement unconfirmed: invalid recovery session or deadline.'
    }
}
function Set-DeployDirectoryAccess([string]$Directory, [string]$InteractiveSID, [switch]$Writable) {
    Assert-DeployController $InteractiveSID
    if ((Get-Item -LiteralPath $Directory -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Deployment request directory must not be a reparse point.'
    }
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $controllerSID = [Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($identity in @($controllerSID,
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')),
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            $identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    $rights = if ($Writable) { 'Modify' } else { 'ReadAndExecute' }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier($InteractiveSID)),
        $rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $Directory -AclObject $acl -ErrorAction Stop
}
function Assert-DeployRequest {
    $self = Get-Process -Id $PID -ErrorAction Stop
    try { Assert-DeploySession $self.SessionId $ExpectedSessionId } finally { $self.Dispose() }
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $ExpectedSID) {
        throw 'Deployment helper account does not match the selected interactive owner; no close or launch permitted.'
    }
    if ((Test-Path -LiteralPath ($Report + '.cancel')) -or
        ($RequestCancel -and (Test-Path -LiteralPath $RequestCancel)) -or [DateTime]::UtcNow.Ticks -ge $DeadlineTicks) {
        throw 'Deployment request cancelled or expired; no further close or launch is permitted.'
    }
}
function Publish-DeployHelper {
    $self = Get-Process -Id $PID -ErrorAction Stop
    try {
        @{
            PID = $PID; StartTicks = $self.StartTime.ToUniversalTime().Ticks
            Executable = $self.Path; Script = $PSCommandPath; RequestId = $RequestId
            CommandLine = [Environment]::CommandLine
        } | ConvertTo-Json | Set-Content -LiteralPath ($Report + '.identity.tmp') -Encoding UTF8
    } finally { $self.Dispose() }
    Move-Item -LiteralPath ($Report + '.identity.tmp') -Destination ($Report + '.identity.json') -ErrorAction Stop
    Assert-DeployRequest
}
function Stop-DeployHelper($Request) {
    $ReportPath = $Request.Report
    Set-Content -LiteralPath ($ReportPath + '.cancel') -Value $Request.RequestId -Encoding UTF8
    $published = $null
    if (Test-Path -LiteralPath ($ReportPath + '.identity.json')) {
        $published = Get-Content -LiteralPath ($ReportPath + '.identity.json') -Raw | ConvertFrom-Json
    }
    # Writable reports supply only a PID/creation hint, never executable or argv authority.
    if ($published) {
        $helpers = @([pscustomobject]@{
            ProcessId = $published.PID; ExecutablePath = $Request.Executable
            StartTicks = $published.StartTicks; Published = $true
        })
    } else {
        $helpers = @(Get-DeployHelperProcessRecords $Request |
            Where-Object { Test-DeployHelperCommandLine $_.CommandLine $Request } |
            ForEach-Object {
                [pscustomobject]@{
                    ProcessId = $_.ProcessId; ExecutablePath = $Request.Executable
                    StartTicks = $_.StartTicks; Published = $false
                }
            })
    }
    foreach ($identity in $helpers) {
        $tolerance = if ($identity.Published) { 0 } else { 10000 }
        $process = Get-DeployPinnedProcess $identity $tolerance
        if (-not $process) { continue }
        try {
            if ($process.HasExited) { continue }
            $actual = @(Get-DeployHelperProcessRecords $Request $process.Id)
            if ($process.HasExited) { continue }
            if ($actual.Count -ne 1 -or $actual[0].ProcessId -ne $process.Id -or -not $actual[0].StartTicks -or
                [Math]::Abs($actual[0].StartTicks - $process.StartTime.ToUniversalTime().Ticks) -gt 10000 -or
                $actual[0].ExecutablePath -ne $Request.Executable -or
                $actual[0].SessionId -ne $process.SessionId -or
                $process.SessionId -ne $Request.ExpectedSessionId -or
                $actual[0].OwnerSID -ne $Request.ExpectedSID -or
                -not (Test-DeployHelperCommandLine $actual[0].CommandLine $Request)) {
                throw 'Deployment helper retirement unconfirmed: actual pinned process executable, creation, session or exact argv could not be authenticated.'
            }
            Stop-DeployPinnedProcess $process
        } finally {
            $process.Dispose()
        }
    }
}
'@
$interactive = @'
param([string]$Report, [string]$RequestId, [long]$DeadlineTicks, [string]$RequestCancel,
    [int]$ExpectedSessionId, [string]$ExpectedSID)
$ErrorActionPreference = 'Stop'
__LIFECYCLE__
$owned = @{}
$fresh = $null
try {
    Publish-DeployHelper
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $app = Join-Path $PSScriptRoot 'RedrainCast.App.exe'
    $snapshot = @(Get-CimInstance Win32_Process)
    Assert-DeployRequest
    $apps = @($snapshot | Where-Object { $_.Name -eq 'RedrainCast.App.exe' -and $_.SessionId -eq $ExpectedSessionId })
    $closeRequests = @()
    foreach ($item in $apps) {
        $owner = Invoke-CimMethod -InputObject $item -MethodName GetOwnerSid -ErrorAction Stop
        if ($owner.ReturnValue -ne 0 -or $owner.Sid -ne $ExpectedSID) {
            throw "Cannot verify selected interactive owner for RedrainCast PID $($item.ProcessId)."
        }
        if (-not $item.ExecutablePath -or
            -not (Test-Path -LiteralPath (Join-Path (Split-Path $item.ExecutablePath) 'RedrainCast.App.dll')) -or
            -not (Test-Path -LiteralPath (Join-Path (Split-Path $item.ExecutablePath) 'RedrainCast.Receive.Core.dll'))) {
            throw "Cannot verify RedrainCast app identity for PID $($item.ProcessId)."
        }
        $process = Get-DeployPinnedProcess ([pscustomobject]@{
            ProcessId = $item.ProcessId; ExecutablePath = $item.ExecutablePath
            StartTicks = $item.CreationDate.ToUniversalTime().Ticks
        })
        if (-not $process) { continue }
        $owned[[int]$item.ProcessId] = $process
        Assert-DeploySession $process.SessionId $ExpectedSessionId
        $parents = @([int]$item.ProcessId)
        $base = (Split-Path $item.ExecutablePath) + '\'
        do {
            $children = @($snapshot | Where-Object { $_.ParentProcessId -in $parents })
            foreach ($child in $children) {
                if ($child.Name -eq 'RedrainCast.CaptureWorker.exe' -and $child.SessionId -eq $ExpectedSessionId -and $child.ExecutablePath -and
                    $child.ExecutablePath.StartsWith($base, [StringComparison]::OrdinalIgnoreCase) -and
                    -not $owned.ContainsKey([int]$child.ProcessId)) {
                    $workerOwner = Invoke-CimMethod -InputObject $child -MethodName GetOwnerSid -ErrorAction Stop
                    if ($workerOwner.ReturnValue -ne 0 -or $workerOwner.Sid -ne $ExpectedSID) {
                        throw "Cannot verify selected interactive owner for capture worker PID $($child.ProcessId)."
                    }
                    $worker = Get-DeployPinnedProcess ([pscustomobject]@{
                        ProcessId = $child.ProcessId; ExecutablePath = $child.ExecutablePath
                        StartTicks = $child.CreationDate.ToUniversalTime().Ticks
                    })
                    if ($worker) { $owned[[int]$child.ProcessId] = $worker }
                }
            }
            $parents = @($children | ForEach-Object { [int]$_.ProcessId })
        } while ($parents.Count)
        if (-not $process.HasExited) {
            Assert-DeployRequest
            try {
                $requested = $process.CloseMainWindow()
            } catch [InvalidOperationException] {
                if (-not $process.HasExited) { throw }
                $requested = $false
            }
            $closeRequests += [pscustomobject]@{ PID = $item.ProcessId; Path = $item.ExecutablePath; CloseRequested = $requested }
        }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        $remaining = @($owned.Keys | Where-Object { -not $owned[$_].HasExited })
        if (-not $remaining.Count) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    $forced = @()
    foreach ($processId in $remaining) {
        Assert-DeployRequest
        Stop-DeployPinnedProcess $owned[$processId]
        $forced += $processId
    }
    foreach ($processId in $owned.Keys) {
        if (-not $owned[$processId].WaitForExit(5000)) { throw "Target PID $processId did not exit." }
    }
    $closeSeconds = $clock.Elapsed.TotalSeconds
    $clock.Restart()
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class RedrainCastDeployWindow {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
}
"@
    Assert-DeployRequest
    $fresh = Start-Process -FilePath $app -WorkingDirectory $PSScriptRoot -PassThru
    $null = $fresh.Handle
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Assert-DeployRequest
        $fresh.Refresh()
        if ($fresh.HasExited) { throw "New app exited with code $($fresh.ExitCode)." }
        if ($fresh.MainWindowHandle -ne [IntPtr]::Zero -and $fresh.MainWindowTitle -eq 'RedrainCast' -and
            [RedrainCastDeployWindow]::IsWindowVisible($fresh.MainWindowHandle)) { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'New app did not become visible within 30 seconds.' }
        Start-Sleep -Milliseconds 200
    } while ($true)
    if ($fresh.Path -ne $app -or $fresh.SessionId -ne $ExpectedSessionId) {
        throw 'New app path or interactive session mismatch.'
    }
    @{
        Success = $true; ProcessClose = $closeSeconds; LaunchReady = $clock.Elapsed.TotalSeconds
        OldApps = $closeRequests; OwnedPIDs = @($owned.Keys); ForcedPIDs = $forced
        GracefulPIDs = @($owned.Keys | Where-Object { $_ -notin $forced })
        NewPID = $fresh.Id; Session = $fresh.SessionId; HWND = $fresh.MainWindowHandle.ToInt64()
        Title = $fresh.MainWindowTitle; Visible = $true; AppPath = $fresh.Path
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath ($Report + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($Report + '.tmp') -Destination $Report -ErrorAction Stop
} catch {
    @{ Success = $false; Error = $_.Exception.ToString() } |
        ConvertTo-Json | Set-Content -LiteralPath ($Report + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($Report + '.tmp') -Destination $Report -ErrorAction Stop
    throw
} finally {
    foreach ($process in $owned.Values) { $process.Dispose() }
    if ($fresh) { $fresh.Dispose() }
}
'@
$interactive = $interactive.Replace('__LIFECYCLE__', $lifecycle)

try {
    $phase = [Diagnostics.Stopwatch]::StartNew()
    Set-Content -LiteralPath $helperPath -Value $interactive -Encoding UTF8
    Write-Host 'Package: compressing existing build (no build performed).'
    Invoke-NativeBounded 'tar.exe' @('-a', '-c', '-f', $archive, '-C', $BuiltDirectory, '.', '-C', $temp, $helperName) 180 | Out-Null
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    $proofNames = @('RedrainCast.App.dll', 'RedrainCast.Receive.Core.dll', 'RedrainCast.Receive.Media.dll')
    $proof = @(foreach ($name in $proofNames) {
        $path = Join-Path $BuiltDirectory $name
        [pscustomobject]@{ File = $name; SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; BuiltUtc = (Get-Item -LiteralPath $path).LastWriteTimeUtc.ToString('o') }
    })
    $packageSeconds = $phase.Elapsed.TotalSeconds
    $archiveBytes = (Get-Item -LiteralPath $archive).Length
    $sshOptions = @('-i', $KeyPath, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10', '-o', 'ServerAliveInterval=10', '-o', 'ServerAliveCountMax=3')
    $peer = "$SSHUser@$RemoteHost"
    $phase.Restart()
    Write-Host "Transfer: one ZIP, $archiveBytes bytes, $($files.Count) runtime files."
    Invoke-NativeBounded 'scp.exe' ($sshOptions + @($archive, "${peer}:$stamp.zip")) 180 | Out-Null
    $transferSeconds = $phase.Elapsed.TotalSeconds
    $remote = @'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
__LIFECYCLE__
$config = '__CONFIG__' | ConvertFrom-Json
if ($env:COMPUTERNAME -eq $config.LocalComputer) { throw 'Remote deployment cannot restart the local host.' }
$desktop = Resolve-DeployDesktop $config.Session
$selectedSession = $desktop.SessionId
$account = $desktop.Account
$sid = $desktop.SID
$requestDirectory = Join-Path $HOME $config.Stamp
New-Item -ItemType Directory -Path $requestDirectory -Force -ErrorAction Stop | Out-Null
$requestCancel = Join-Path $requestDirectory 'cancel'
$requestDeadline = [DateTime]::UtcNow.AddSeconds(100)
if (Test-Path -LiteralPath $requestCancel) { throw 'Deployment request was already cancelled.' }
Set-DeployDirectoryAccess $requestDirectory $sid
$reportDirectory = Join-Path $requestDirectory 'reports'
New-Item -ItemType Directory -Path $reportDirectory -ErrorAction Stop | Out-Null
Set-DeployDirectoryAccess $reportDirectory $sid -Writable
$profile = Get-CimInstance Win32_UserProfile -Filter "SID='$sid'"
if (-not $profile.LocalPath) { throw 'Interactive owner profile missing.' }
$base = if ($config.Directory) { $config.Directory } else { Join-Path $profile.LocalPath 'RedrainCast-DevDeploy' }
$destination = Join-Path $base $config.Stamp
if (Test-Path -LiteralPath $destination) { throw 'New deployment directory already exists.' }
$zip = Join-Path $HOME ($config.Stamp + '.zip')
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $config.ArchiveHash) { throw 'Transferred ZIP SHA256 mismatch.' }
$clock = [Diagnostics.Stopwatch]::StartNew()
New-Item -ItemType Directory -Path $destination -Force | Out-Null
& tar.exe -x -f $zip -C $destination
if ($LASTEXITCODE -ne 0) { throw "ZIP extraction failed: $LASTEXITCODE" }
$extractSeconds = $clock.Elapsed.TotalSeconds
$hashes = @(foreach ($expected in $config.Proof) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $destination $expected.File) -Algorithm SHA256).Hash
    if ($hash -ne $expected.SHA256) { throw "Deployed assembly hash mismatch: $($expected.File)" }
    @{ File = $expected.File; SHA256 = $hash; BuiltUtc = $expected.BuiltUtc }
})
$report = Join-Path $reportDirectory 'deployment-result.json'
$helper = Join-Path $destination ($config.Stamp + '.ps1')
$deadline = [DateTime]::UtcNow.AddSeconds(60)
if ($deadline -gt $requestDeadline) { $deadline = $requestDeadline }
$request = [pscustomobject]@{
    Helper = $helper; Report = $report; RequestId = $config.Stamp
    Executable = (Join-Path $PSHOME 'powershell.exe')
    DeadlineTicks = $deadline.Ticks; RequestCancel = $requestCancel
    ExpectedSessionId = $selectedSession; ExpectedSID = $sid
}
$request |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $requestDirectory 'request.tmp') -Encoding UTF8
Move-Item -LiteralPath (Join-Path $requestDirectory 'request.tmp') -Destination (Join-Path $requestDirectory 'request.json') -ErrorAction Stop
$task = $config.Stamp
if ((Test-Path -LiteralPath $requestCancel) -or [DateTime]::UtcNow -ge $deadline) { throw 'Deployment request cancelled or expired before launch.' }
$quotedArguments = @(@($request.Executable) + @(Get-DeployHelperArguments $request) | ForEach-Object {
    if ($_ -match '["\r\n]' -or $_.EndsWith('\')) { throw 'Unsafe helper launch argument.' }
    '"' + $_ + '"'
})
$arguments = '/c start "" /b ' + ($quotedArguments -join ' ')
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument $arguments
$principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal | Out-Null
try {
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $registeredTask = $scheduler.GetFolder('\').GetTask($task)
    Invoke-DeployTask $registeredTask $selectedSession $sid
    while (-not (Test-Path -LiteralPath $report)) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Interactive deployment timed out after 60 seconds.' }
        Start-Sleep -Milliseconds 200
    }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.Success) { throw "Interactive deployment failed: $($result.Error)" }
    if ($result.Session -ne $selectedSession) { throw 'Reported app session does not match the selected interactive desktop.' }
    $result | Add-Member -NotePropertyName Extract -NotePropertyValue $extractSeconds
    $result | Add-Member -NotePropertyName Owner -NotePropertyValue $account
    $result | Add-Member -NotePropertyName Hashes -NotePropertyValue $hashes
    $result | ConvertTo-Json -Depth 8 -Compress
} finally {
    Set-Content -LiteralPath $requestCancel -Value $config.Stamp -Encoding UTF8
    Set-Content -LiteralPath ($report + '.cancel') -Value $config.Stamp -Encoding UTF8
    Stop-ScheduledTask -TaskName $task -ErrorAction Stop
    Stop-DeployHelper $request
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction Stop
}
Remove-Item -LiteralPath $zip, $helper -ErrorAction Stop
'@
    $config = @{
        Stamp = $stamp; Directory = $RemoteDirectory; Session = $InteractiveSessionId
        ArchiveHash = $archiveHash; Proof = $proof; LocalComputer = $env:COMPUTERNAME
    } | ConvertTo-Json -Depth 5 -Compress
    $remote = $remote.Replace('__CONFIG__', $config.Replace("'", "''")).Replace('__LIFECYCLE__', $lifecycle)
    $bootstrap = '$ErrorActionPreference="Stop"; & ([scriptblock]::Create([Console]::In.ReadToEnd()))'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))
    Write-Host 'Remote: verify ZIP, extract, close target apps, launch interactive app and confirm one visible window.'
    try {
        $result = Invoke-NativeBounded 'ssh.exe' ($sshOptions + @($peer, "powershell -NoProfile -EncodedCommand $encoded")) 120 $remote | ConvertFrom-Json
    } catch {
        $deploymentError = $_
        $recovery = @'
$ErrorActionPreference = 'Stop'
__LIFECYCLE__
$config = '__CONFIG__' | ConvertFrom-Json
$requestDirectory = Join-Path $HOME '__STAMP__'
New-Item -ItemType Directory -Path $requestDirectory -Force -ErrorAction Stop | Out-Null
Set-Content -LiteralPath (Join-Path $requestDirectory 'cancel') -Value '__STAMP__' -Encoding UTF8
$descriptor = Join-Path $requestDirectory 'request.json'
if (Test-Path -LiteralPath $descriptor) {
    $desktop = Resolve-DeployDesktop $config.Session
    foreach ($path in @($requestDirectory, $descriptor)) {
        if ((Get-Item -LiteralPath $path -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Helper retirement unconfirmed: recovery request must not be a reparse point.'
        }
    }
    $request = Get-Content -LiteralPath $descriptor -Raw | ConvertFrom-Json
    Assert-DeployRecoveryRequest $request $config $desktop $requestDirectory
    Set-Content -LiteralPath ($request.Report + '.cancel') -Value $request.RequestId -Encoding UTF8
    $task = Get-ScheduledTask -TaskName $request.RequestId -ErrorAction SilentlyContinue
    if ($task) { Stop-ScheduledTask -TaskName $request.RequestId -ErrorAction Stop }
    Stop-DeployHelper $request
    if ($task) { Unregister-ScheduledTask -TaskName $request.RequestId -Confirm:$false -ErrorAction Stop }
}
'@
        $recovery = $recovery.Replace('__LIFECYCLE__', $lifecycle).Replace('__STAMP__', $stamp).Replace('__CONFIG__', $config.Replace("'", "''"))
        try {
            Invoke-NativeBounded 'ssh.exe' ($sshOptions + @($peer, "powershell -NoProfile -EncodedCommand $encoded")) 30 $recovery | Out-Null
        } catch {
            throw "Deployment failed: $($deploymentError.Exception.Message). Remote helper cancellation/termination could not be confirmed: $($_.Exception.Message)"
        }
        throw $deploymentError
    }
    $total.Stop()
    $timings = [ordered]@{
        ProcessClose = [Math]::Round($result.ProcessClose, 3)
        Package = [Math]::Round($packageSeconds, 3)
        Transfer = [Math]::Round($transferSeconds, 3)
        Extract = [Math]::Round($result.Extract, 3)
        LaunchReady = [Math]::Round($result.LaunchReady, 3)
        Total = [Math]::Round($total.Elapsed.TotalSeconds, 3)
    }
    $result | Add-Member -NotePropertyName TimingsSeconds -NotePropertyValue $timings
    $result | Add-Member -NotePropertyName ArchiveBytes -NotePropertyValue $archiveBytes
    $result | Add-Member -NotePropertyName RuntimeFileCount -NotePropertyValue $files.Count
    $result | Add-Member -NotePropertyName ArchiveSHA256 -NotePropertyValue $archiveHash
    $result | Add-Member -NotePropertyName BuildPerformed -NotePropertyValue $false
    Write-Host ('Elapsed seconds (build excluded): ' + (($timings.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))
    $result
} finally {
    foreach ($path in @($archive, $helperPath)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -ErrorAction Stop }
    }
    if (-not @(Get-ChildItem -LiteralPath $temp -Force).Count) { Remove-Item -LiteralPath $temp -ErrorAction Stop }
}
