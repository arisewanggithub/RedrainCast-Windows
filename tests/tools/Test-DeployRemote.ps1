[CmdletBinding()]
param([string]$ScratchRoot = (Join-Path ([IO.Path]::GetTempPath()) ('RedrainCast-DeployTest-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $root 'tools\Deploy-Remote.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
Assert (-not $errors.Count) "Main script parser errors: $errors"
function Embedded([string]$Name) {
    $assignment = $ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq $Name -and
        $node.Right.Extent.Text.StartsWith("@'")
    }.GetNewClosure(), $true) | Select-Object -First 1
    Assert ($null -ne $assignment) "Missing embedded script $Name"
    return $assignment.Right.Extent.Text.Substring(3, $assignment.Right.Extent.Text.Length - 5).TrimStart("`r", "`n")
}
$lifecycle = Embedded 'lifecycle'
$interactive = (Embedded 'interactive').Replace('__LIFECYCLE__', $lifecycle)
$remote = (Embedded 'remote').Replace('__LIFECYCLE__', $lifecycle).Replace('__CONFIG__', '{}')
$recovery = (Embedded 'recovery').Replace('__LIFECYCLE__', $lifecycle).Replace('__STAMP__', 'fixture-request').Replace('__CONFIG__', '{}')
$source = Get-Content -LiteralPath $scriptPath -Raw
foreach ($text in @($lifecycle, $interactive, $remote, $recovery)) {
    $null = [Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
    Assert (-not $errors.Count) "Embedded parser errors: $errors"
}
. ([scriptblock]::Create($lifecycle))
Assert ($interactive -match 'try\s*\{\s*Publish-DeployHelper\s+\$clock' -and
    $interactive.IndexOf('    Publish-DeployHelper') -lt $interactive.IndexOf('$snapshot =')) 'Identity must precede the stalled process query.'
Assert ($interactive -match 'Assert-DeployRequest\s+try\s*\{\s*\$requested = \$process.CloseMainWindow\(\)') 'Graceful close lacks cancellation check.'
Assert ($interactive -match 'Assert-DeployRequest\s+Stop-DeployPinnedProcess') 'Force close lacks cancellation check.'
Assert ($interactive -notmatch 'Stop-Process|Get-CimInstance Win32_Process -Filter "ProcessId=') 'Target closure must not re-resolve a PID.'
Assert ($interactive -match '\$owned\[\[int\]\$child.ProcessId\] = \$worker' -and
    $interactive -match 'StartTicks = \$child.CreationDate.ToUniversalTime\(\).Ticks') 'Owned capture workers lack pinned identity.'
Assert ($interactive -match '\$remaining = @\(\$owned.Keys \| Where-Object \{ -not \$owned\[\$_\].HasExited \}\)' -and
    $interactive -match '\$owned\[\$processId\].WaitForExit\(5000\)' -and
    $interactive -match 'foreach \(\$process in \$owned.Values\) \{ \$process.Dispose\(\) \}') 'Targets must retain the same handle through wait, kill and disposal.'
Assert ($interactive -match 'Assert-DeployRequest\s+\$fresh = Start-Process') 'Launch lacks cancellation check.'
Assert ($remote -match 'Stop-DeployHelper \$request\s+Unregister-ScheduledTask') 'Helper must retire before task removal.'
Assert ($remote -notmatch 'Start-ScheduledTask' -and $remote -match 'Invoke-DeployTask \$registeredTask \$selectedSession \$sid') 'Task must use the resolved desktop and SID.'
Assert ($interactive -match '\$fresh.SessionId -ne \$ExpectedSessionId' -and
    $interactive -match '\$_.SessionId -eq \$ExpectedSessionId') 'App selection and final launch must bind to the expected desktop.'
Assert ($lifecycle -match 'WaitForExit\(3000\)' -and $lifecycle -notmatch '\$published.CommandLine|StartInfo.Arguments.*\$published') 'Actual argv query must be independently bounded, not read from the report.'
Assert ($remote -match 'Set-DeployDirectoryAccess \$requestDirectory \$sid' -and
    $remote -match 'Set-DeployDirectoryAccess \$reportDirectory \$sid -Writable' -and
    $remote -match '\$report = Join-Path \$reportDirectory ''deployment-result.json''') 'Reports must be writable outside the read-only payload.'
Assert ($remote -notmatch 'Remove-Item -LiteralPath \$requestCancel|Remove-Item -LiteralPath \$requestDirectory') 'Late startup cancellation must remain persistent.'
Assert ($source -notmatch 'Stop-Process -Name|taskkill|Kill\(\$true\)') 'Name or process-tree termination is forbidden.'
Assert ($remote -match 'AddSeconds\(60\)' -and $interactive -match 'AddSeconds\(30\)') 'Existing report/visible-window budgets changed.'
Assert ($recovery.IndexOf("Set-Content -LiteralPath (Join-Path `$requestDirectory 'cancel')") -lt
    $recovery.IndexOf('if (Test-Path -LiteralPath $descriptor)')) 'SSH recovery must cancel even before the request descriptor exists.'
Assert ($remote.IndexOf('$desktop = Resolve-DeployDesktop') -lt $remote.IndexOf('New-Item -ItemType Directory -Path $requestDirectory')) 'Controller/desktop collision must fail before request creation.'
Assert ($recovery.IndexOf('$desktop = Resolve-DeployDesktop') -lt $recovery.IndexOf('$request = Get-Content') -and
    $recovery.IndexOf('Assert-DeployRecoveryRequest $request') -lt $recovery.IndexOf("Set-Content -LiteralPath (`$request.Report")) 'Recovery must independently resolve the desktop and validate authority before using loaded fields.'
Assert ($lifecycle -match '\$start.FileName = Join-Path \$PSHOME ''powershell.exe''' -and
    $lifecycle -notmatch '\$start.FileName = \$Request.Executable') 'Query child executable must not come from a descriptor.'
$bootstrapNode = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$bootstrap'
}, $true)
$bootstrap = & ([scriptblock]::Create($bootstrapNode.Right.Extent.Text))
$null = [Management.Automation.Language.Parser]::ParseInput($bootstrap, [ref]$tokens, [ref]$errors)
Assert (-not $errors.Count) "SSH bootstrap parser errors: $errors"
Write-Host 'PASS main and all embedded parsers; cancellation/order/timeout/static contracts'

Assert (-not (Test-Path -LiteralPath $ScratchRoot)) 'Scratch directory must be new.'
New-Item -ItemType Directory -Path $ScratchRoot | Out-Null
$owned = New-Object 'Collections.Generic.List[Diagnostics.Process]'
$originalPayloadACL = $null
$clock = [Diagnostics.Stopwatch]::StartNew()
function Wait-File([string]$Path, [int]$Milliseconds = 6000) {
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path)) {
        if ($wait.ElapsedMilliseconds -ge $Milliseconds) { throw "Fixture did not publish $Path in $Milliseconds ms." }
        Start-Sleep -Milliseconds 25
    }
}
function New-HelperRequest([string]$Helper, [string]$Report, [string]$Name, [long]$DeadlineTicks,
    [int]$SessionId = ([Diagnostics.Process]::GetCurrentProcess().SessionId)) {
    return [pscustomobject]@{
        Helper = $Helper; Report = $Report; RequestId = $Name; DeadlineTicks = $DeadlineTicks
        Executable = (Get-Command powershell.exe).Source; RequestCancel = (Join-Path (Split-Path $Helper) 'cancel')
        ExpectedSessionId = $SessionId; ExpectedSID = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    }
}
function Format-HelperCommand($Request) {
    return ((@($Request.Executable) + @(Get-DeployHelperArguments $Request) | ForEach-Object { '"' + $_ + '"' }) -join ' ')
}
function Start-Fixture([string]$Name, [int]$BeforePublish = 0, [int]$QueryDelay = 0, [switch]$Launch,
    [int]$DeadlineMilliseconds = 15000, [switch]$AlreadyCancelled, [switch]$FailureReport,
    [int]$SessionId = ([Diagnostics.Process]::GetCurrentProcess().SessionId), [switch]$ActualHelper) {
    $directory = Join-Path $ScratchRoot $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $helper = Join-Path $directory 'helper.ps1'
    $report = Join-Path $directory 'result.json'
    $body = @'
param([string]$Report, [string]$RequestId, [long]$DeadlineTicks, [string]$RequestCancel,
    [int]$ExpectedSessionId, [string]$ExpectedSID)
$ErrorActionPreference = 'Stop'
__LIFECYCLE__
$fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fixture.json') -Raw | ConvertFrom-Json
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'entered') -Value $PID
if ($fixture.BeforePublish) { Start-Sleep -Milliseconds $fixture.BeforePublish }
Publish-DeployHelper
if ($fixture.FailureReport) { '{"Success":false,"Error":"synthetic failure"}' | Set-Content -LiteralPath $Report }
if ($fixture.QueryDelay) { Start-Sleep -Milliseconds $fixture.QueryDelay }
Assert-DeployRequest
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'close') -Value 'fake close'
Assert-DeployRequest
if ($fixture.Launch) {
    $app = Start-Process powershell.exe -ArgumentList ('-NoProfile -File "' + (Join-Path $PSScriptRoot 'dummy.ps1') + '"') -PassThru -WindowStyle Hidden
    @{ Success = $true; NewPID = $app.Id } | ConvertTo-Json | Set-Content -LiteralPath $Report
} else {
    Set-Content -LiteralPath (Join-Path $PSScriptRoot 'launch') -Value 'fake launch'
}
Start-Sleep -Seconds 15
'@
    if ($ActualHelper) { $body = $interactive }
    Set-Content -LiteralPath $helper -Value $body.Replace('__LIFECYCLE__', $lifecycle) -Encoding UTF8
    @{ BeforePublish = $BeforePublish; QueryDelay = $QueryDelay; Launch = [bool]$Launch; FailureReport = [bool]$FailureReport } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'fixture.json')
    Set-Content -LiteralPath (Join-Path $directory 'dummy.ps1') -Value 'Start-Sleep -Seconds 20'
    $cancel = Join-Path $directory 'cancel'
    if ($AlreadyCancelled) { Set-Content -LiteralPath $cancel -Value $Name }
    $request = New-HelperRequest $helper $report $Name ([DateTime]::UtcNow.AddMilliseconds($DeadlineMilliseconds).Ticks) $SessionId
    $arguments = (@(Get-DeployHelperArguments $request) | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process powershell.exe -ArgumentList $arguments -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $directory 'stdout') -RedirectStandardError (Join-Path $directory 'stderr')
    $null = $process.Handle
    $owned.Add($process)
    if ($ActualHelper) { Wait-File ($report + '.identity.json') } else { Wait-File (Join-Path $directory 'entered') }
    return [pscustomobject]@{ Process = $process; Helper = $helper; Report = $report; Directory = $directory; Request = $request }
}
Add-Type @'
using System;
public sealed class DeployPinnedProcessFixture {
    public int Id = 765432, Kills, Waits, Disposals, SessionId = 2;
    public bool HasExited, Pinned, ExitBeforePin, ExitDuringKill;
    public string ExpectedPath = @"C:\fixture\RedrainCast.App.exe";
    public DateTime Created = DateTime.UtcNow;
    public IntPtr Handle {
        get {
            if (ExitBeforePin) {
                HasExited = true;
                throw new InvalidOperationException("Fixture exited before pin.");
            }
            Pinned = true;
            return new IntPtr(1);
        }
    }
    public string Path {
        get {
            if (!Pinned) throw new InvalidOperationException("Path read before pin.");
            return ExpectedPath;
        }
    }
    public DateTime StartTime {
        get {
            if (!Pinned) throw new InvalidOperationException("Creation time read before pin.");
            return Created;
        }
    }
    public void Kill() {
        if (!Pinned) throw new InvalidOperationException("Kill before pin.");
        Kills++;
        HasExited = true;
        if (ExitDuringKill) throw new InvalidOperationException("Fixture exited during kill.");
    }
    public bool WaitForExit(int timeout) {
        if (!Pinned || timeout != 5000) throw new InvalidOperationException("Wrong wait handle or budget.");
        Waits++;
        return HasExited;
    }
    public void Dispose() { Disposals++; }
}
'@
function New-PinnedMock([switch]$ExitBeforePin, [switch]$ExitDuringKill) {
    $mock = New-Object DeployPinnedProcessFixture
    $mock.ExitBeforePin = [bool]$ExitBeforePin
    $mock.ExitDuringKill = [bool]$ExitDuringKill
    return $mock
}
try {
    $controllerSID = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $distinctSID = 'S-1-5-21-111111111-222222222-333333333-1001'
    $sideEffect = $false
    $rejected = $false
    try { Assert-DeployController $controllerSID; $sideEffect = $true } catch {
        Assert ($_.Exception.Message -match 'distinct SSH controller account.*Same-SID.*Limited token') 'Same-SID failure is not actionable.'
        $rejected = $true
    }
    Assert ($rejected -and -not $sideEffect) 'Same-SID controller accepted.'
    Assert-DeployController $distinctSID
    $rejected = $false
    try { Set-DeployDirectoryAccess (Join-Path $ScratchRoot 'must-not-exist') $controllerSID } catch {
        Assert ($_.Exception.Message -match 'Same-SID') 'ACL entry point did not reject collision before filesystem access.'
        $rejected = $true
    }
    Assert $rejected 'Same-SID ACL configuration accepted.'

    $startupMarker = Join-Path $ScratchRoot 'forged-executable-started'
    $forgedExecutable = Join-Path $ScratchRoot 'forged-query.exe'
    $markerLiteral = $startupMarker.Replace('\', '\\').Replace('"', '\"')
    Add-Type -TypeDefinition ('public static class ForgedDeployQuery { public static void Main() { System.IO.File.WriteAllText("' +
        $markerLiteral + '", "started"); } }') -OutputAssembly $forgedExecutable -OutputType ConsoleApplication
    $fixtureHome = Join-Path $ScratchRoot 'controller-profile'
    New-Item -ItemType Directory -Path $fixtureHome | Out-Null
    $fixtureConfig = @{ Stamp = 'fixture-request'; Session = 2; Directory = (Join-Path $ScratchRoot 'payload'); LocalComputer = 'not-this-host' }
    $fixtureConfigJSON = ($fixtureConfig | ConvertTo-Json -Compress).Replace("'", "''")
    $remoteFixture = (Embedded 'remote').Replace('__LIFECYCLE__', '').Replace('__CONFIG__', $fixtureConfigJSON).
        Replace('Join-Path $HOME', 'Join-Path $fixtureHome')
    $recoveryFixture = (Embedded 'recovery').Replace('__LIFECYCLE__', '').Replace('__CONFIG__', $fixtureConfigJSON).
        Replace('__STAMP__', 'fixture-request').Replace('Join-Path $HOME', 'Join-Path $fixtureHome')
    $originalResolver = ${function:Resolve-DeployDesktop}
    $originalRetire = ${function:Stop-DeployHelper}
    try {
        function Get-CimInstance { param($ClassName, $Filter) [pscustomobject]@{ SessionId = 2 } }
        function Invoke-CimMethod {
            param($InputObject, $MethodName)
            $name = [Security.Principal.WindowsIdentity]::GetCurrent().Name.Split('\')
            [pscustomobject]@{ ReturnValue = 0; Domain = $name[0]; User = $name[1] }
        }
        $rejected = $false
        try { & ([scriptblock]::Create($remoteFixture)) } catch {
            Assert ($_.Exception.Message -match 'Same-SID') "Unexpected remote collision failure: $_"
            $rejected = $true
        }
        $requestDirectory = Join-Path $fixtureHome 'fixture-request'
        Assert ($rejected -and -not (Test-Path -LiteralPath $requestDirectory)) 'Remote collision created a trusted descriptor/request/task.'
        New-Item -ItemType Directory -Path $requestDirectory | Out-Null
        $descriptor = Join-Path $requestDirectory 'request.json'
        @{
            Executable = $forgedExecutable; ExpectedSID = $distinctSID; ExpectedSessionId = 7
            ControllerSID = 'S-1-5-18'; Version = 999; RequestId = 'forged-task'
        } | ConvertTo-Json | Set-Content -LiteralPath $descriptor
        $script:recoveryActions = 0
        function Get-ScheduledTask { param($TaskName, $ErrorAction) $script:recoveryActions++; throw 'Unexpected recovery task lookup.' }
        function Stop-DeployHelper { param($Request) $script:recoveryActions++; throw 'Unexpected recovery child/retirement.' }
        $rejected = $false
        try { & ([scriptblock]::Create($recoveryFixture)) } catch {
            Assert ($_.Exception.Message -match 'Same-SID') "Forged SID bypassed independent recovery collision detection: $_"
            $rejected = $true
        }
        Assert ($rejected -and $recoveryActions -eq 0 -and -not (Test-Path -LiteralPath $startupMarker) -and
            (Test-Path -LiteralPath (Join-Path $requestDirectory 'cancel'))) 'Unsafe old request recovery acted or lost prior cancellation.'
        function Resolve-DeployDesktop {
            param([int]$SessionId)
            Assert-DeployController $distinctSID
            [pscustomobject]@{ SessionId = 2; Account = 'fixture\desktop'; SID = $distinctSID }
        }
        function Get-CimInstance { param($ClassName, $Filter) [pscustomobject]@{ LocalPath = (Join-Path $ScratchRoot 'desktop-profile') } }
        $trustedRequest = [pscustomobject]@{
            RequestId = $fixtureConfig.Stamp; Executable = (Join-Path $PSHOME 'powershell.exe')
            Helper = (Join-Path (Join-Path $fixtureConfig.Directory $fixtureConfig.Stamp) ($fixtureConfig.Stamp + '.ps1'))
            Report = (Join-Path (Join-Path $requestDirectory 'reports') 'deployment-result.json')
            RequestCancel = (Join-Path $requestDirectory 'cancel'); ExpectedSID = $distinctSID
            ExpectedSessionId = 2; DeadlineTicks = [DateTime]::UtcNow.AddSeconds(60).Ticks
        }
        foreach ($mode in @('executable', 'report', 'helper', 'request-id', 'cancel-path', 'sid', 'session', 'deadline', 'malformed')) {
            $request = $trustedRequest | ConvertTo-Json | ConvertFrom-Json
            switch ($mode) {
                'executable' { $request.Executable = $forgedExecutable }
                'report' { $request.Report = (Join-Path $ScratchRoot 'must-not-write') }
                'helper' { $request.Helper = (Join-Path $ScratchRoot 'untrusted.ps1') }
                'request-id' { $request.RequestId = 'other-task' }
                'cancel-path' { $request.RequestCancel = (Join-Path $ScratchRoot 'must-not-cancel') }
                'sid' { $request.ExpectedSID = $controllerSID }
                'session' { $request.ExpectedSessionId = 7 }
                'deadline' { $request.DeadlineTicks = 'untrusted' }
            }
            $text = if ($mode -eq 'malformed') { '{invalid json' } else { $request | ConvertTo-Json }
            Set-Content -LiteralPath $descriptor -Value $text
            $rejected = $false
            try { & ([scriptblock]::Create($recoveryFixture)) } catch { $rejected = $true }
            Assert ($rejected -and $recoveryActions -eq 0 -and -not (Test-Path -LiteralPath $startupMarker)) "Untrusted $mode recovery started a task/query/retirement."
        }
        New-Item -ItemType Directory -Path (Split-Path $trustedRequest.Report) | Out-Null
        function Get-ScheduledTask { param($TaskName, $ErrorAction) $script:recoveryActions++; return $null }
        function Stop-DeployHelper {
            param($Request)
            Assert ($Request.ExpectedSID -eq $distinctSID -and $Request.Executable -eq (Join-Path $PSHOME 'powershell.exe')) 'Distinct-SID recovery authority changed.'
            $script:recoveryActions++
        }
        $trustedRequest | ConvertTo-Json | Set-Content -LiteralPath $descriptor
        & ([scriptblock]::Create($recoveryFixture))
        Assert ($recoveryActions -eq 2 -and (Test-Path -LiteralPath ($trustedRequest.Report + '.cancel'))) 'Valid distinct-SID recovery did not cancel and retire.'

        $fixtureConfig.Stamp = 'fixture-happy'
        $fixtureConfig.Proof = @()
        $happyZip = Join-Path $fixtureHome ($fixtureConfig.Stamp + '.zip')
        Set-Content -LiteralPath $happyZip -Value 'offline archive fixture'
        $fixtureConfig.ArchiveHash = (Get-FileHash -LiteralPath $happyZip -Algorithm SHA256).Hash
        $happyReport = Join-Path (Join-Path (Join-Path $fixtureHome $fixtureConfig.Stamp) 'reports') 'deployment-result.json'
        $happyHelper = Join-Path (Join-Path $fixtureConfig.Directory $fixtureConfig.Stamp) ($fixtureConfig.Stamp + '.ps1')
        function tar.exe {
            param([switch]$x, [string]$f, [string]$C)
            Assert ($x -and $f -eq $happyZip -and $C -eq (Split-Path $happyHelper)) 'Extraction command changed.'
            Set-Content -LiteralPath $happyHelper -Value 'non-executable offline helper'
            $global:LASTEXITCODE = 0
        }
        function New-ScheduledTaskAction { param($Execute, $Argument) [pscustomobject]@{ Execute = $Execute; Argument = $Argument } }
        function New-ScheduledTaskPrincipal {
            param($UserId, $LogonType, $RunLevel)
            Assert ($UserId -eq 'fixture\desktop' -and $LogonType -eq 'Interactive' -and $RunLevel -eq 'Limited') 'Distinct-SID task principal changed.'
            [pscustomobject]@{ UserId = $UserId }
        }
        function Register-ScheduledTask { param($TaskName, $Action, $Principal) $script:recoveryActions++ }
        function Stop-ScheduledTask { param($TaskName, $ErrorAction) $script:recoveryActions++ }
        function Unregister-ScheduledTask { param($TaskName, $Confirm, $ErrorAction) $script:recoveryActions++ }
        $schedulerMock = [pscustomobject]@{ Calls = 0 }
        $schedulerMock | Add-Member ScriptMethod Connect {}
        $schedulerMock | Add-Member ScriptMethod GetFolder { param($Path) return $this }
        $schedulerMock | Add-Member ScriptMethod GetTask { param($Name) return $this }
        $schedulerMock | Add-Member ScriptMethod RunEx {
            param($Parameters, $Flags, $Session, $SID)
            Assert ($Flags -eq 4 -and $Session -eq 2 -and $SID -eq $distinctSID) 'Distinct-SID remote RunEx identity changed.'
            Assert (Test-Path -LiteralPath $happyHelper) 'Task ran before extraction.'
            $this.Calls++
            @{ Success = $true; Session = 2 } | ConvertTo-Json | Set-Content -LiteralPath $happyReport
        }
        $remoteHappy = (Embedded 'remote').Replace('__LIFECYCLE__', '').
            Replace('__CONFIG__', ($fixtureConfig | ConvertTo-Json -Compress).Replace("'", "''")).
            Replace('Join-Path $HOME', 'Join-Path $fixtureHome').
            Replace("New-Object -ComObject 'Schedule.Service'", '$schedulerMock')
        $happyResult = & ([scriptblock]::Create($remoteHappy)) | ConvertFrom-Json
        Assert ($happyResult.Success -and $happyResult.Session -eq 2 -and $schedulerMock.Calls -eq 1 -and
            $recoveryActions -eq 6 -and -not (Test-Path -LiteralPath $happyZip) -and
            -not (Test-Path -LiteralPath $happyHelper)) 'Distinct-SID remote success/retirement/cleanup contract failed.'
    } finally {
        Remove-Item Function:\Get-CimInstance, Function:\Invoke-CimMethod, Function:\Get-ScheduledTask
        foreach ($name in @('tar.exe', 'New-ScheduledTaskAction', 'New-ScheduledTaskPrincipal',
            'Register-ScheduledTask', 'Stop-ScheduledTask', 'Unregister-ScheduledTask')) {
            if (Test-Path "Function:\$name") { Remove-Item "Function:\$name" }
        }
        Set-Item Function:\Resolve-DeployDesktop -Value $originalResolver
        Set-Item Function:\Stop-DeployHelper -Value $originalRetire
    }
    Write-Host 'PASS real same-SID controller/ACL/remote rejection before request creation; old forged-SID recovery rejected before loaded authority; malformed/canonical-field recovery guards; distinct-SID recovery and mocked remote success with Limited/RunEx/extract-before-task/cleanup'

    $forgedRequest = [pscustomobject]@{ Executable = $forgedExecutable }
    $queryRecords = @(Get-DeployHelperProcessRecords $forgedRequest $PID)
    Assert ($queryRecords.Count -eq 1 -and $queryRecords[0].ProcessId -eq $PID -and
        -not (Test-Path -LiteralPath $startupMarker)) 'Descriptor executable started instead of trusted controller query executable.'
    Write-Host 'PASS real owned marker executable never starts when supplied as forged descriptor query executable'

    try {
        function Get-Process { param([int]$Id, $ErrorAction) $script:lookups++; return $script:mock }
        foreach ($mode in @('normal', 'before-pin', 'after-verify', 'during-kill', 'reused-start', 'reused-path', 'cim-rounding')) {
            $script:lookups = 0
            $script:mock = New-PinnedMock -ExitBeforePin:($mode -eq 'before-pin') -ExitDuringKill:($mode -eq 'during-kill')
            $identity = [pscustomobject]@{
                ProcessId = $mock.Id; ExecutablePath = $mock.ExpectedPath; StartTicks = $mock.Created.ToUniversalTime().Ticks
            }
            if ($mode -eq 'reused-start') { $identity.StartTicks -= 10000000 }
            if ($mode -eq 'reused-path') { $identity.ExecutablePath = 'C:\unrelated\Other.exe' }
            if ($mode -eq 'cim-rounding') { $identity.StartTicks -= 9999 }
            $pinned = $null
            try {
                $pinned = Get-DeployPinnedProcess $identity
                Assert ($mode -notlike 'reused-*') 'Reused PID identity was accepted.'
                if ($mode -eq 'before-pin') {
                    Assert ($null -eq $pinned) 'Disappearing target was not treated as retired.'
                } else {
                    Assert ([object]::ReferenceEquals($pinned, $mock)) 'Pin replaced the process object.'
                    if ($mode -eq 'after-verify') { $mock.HasExited = $true }
                    Stop-DeployPinnedProcess $pinned
                    Assert ($mock.Kills -eq [int]($mode -ne 'after-verify')) 'Retired target received a kill or active target was not killed.'
                    Assert ($mock.Waits -eq [int]($mode -ne 'after-verify')) 'Kill did not await the same handle.'
                }
            } catch {
                Assert ($mode -like 'reused-*' -and $_.Exception.Message -match 'identity changed') "Unexpected pin failure in ${mode}: $_"
                Assert ($mock.Kills -eq 0) 'Reused unrelated PID was killed.'
            } finally { if ($pinned) { $pinned.Dispose() } }
            Assert ($lookups -eq 1 -and $mock.Disposals -eq 1) "PID was re-resolved or handle leaked in $mode."
        }
    } finally { Remove-Item Function:\Get-Process -ErrorAction Stop }
    Write-Host 'PASS synthetic target exit-before-pin/after-verify/during-kill, reused path/start rejection, CIM rounding and same-handle kill/wait/disposal'

    $authenticationRequest = New-HelperRequest (Join-Path $ScratchRoot 'trusted-helper.ps1') (Join-Path $ScratchRoot 'forged.json') 'trusted-request' ([DateTime]::UtcNow.AddSeconds(15).Ticks) 2
    $originalQuery = ${function:Get-DeployHelperProcessRecords}
    try {
        function Get-Process { param([int]$Id, $ErrorAction) $script:lookups++; return $script:mock }
        function Get-DeployHelperProcessRecords {
            param($Request, [int]$ProcessId)
            Assert ($script:mock.Pinned) 'Actual argv queried before pinning the candidate.'
            Assert ($ProcessId -eq $script:mock.Id) 'Query changed the pinned PID.'
            if ($script:queryMode -eq 'error') { throw 'Helper retirement unconfirmed: synthetic actual query error' }
            if ($script:queryMode -eq 'unavailable') { return }
            return $script:actualRecord
        }
        foreach ($mode in @('unrelated-exe', 'wrong-request', 'embedded-command', 'extra-argument', 'error', 'unavailable', 'reused-query', 'wrong-session', 'wrong-owner', 'valid')) {
            $script:mock = New-PinnedMock
            $script:mock.ExpectedPath = $authenticationRequest.Executable
            if ($mode -eq 'unrelated-exe') { $script:mock.ExpectedPath = 'C:\Windows\System32\notepad.exe' }
            $script:lookups = 0
            $script:queryMode = $mode
            $commandLine = Format-HelperCommand $authenticationRequest
            $script:actualRecord = [pscustomobject]@{
                ProcessId = $mock.Id; ExecutablePath = $mock.ExpectedPath; SessionId = $mock.SessionId
                StartTicks = $mock.Created.ToUniversalTime().Ticks; CommandLine = $commandLine
                OwnerSID = $authenticationRequest.ExpectedSID
            }
            if ($mode -eq 'wrong-request') { $actualRecord.CommandLine = $commandLine.Replace('trusted-request', 'other-request') }
            if ($mode -eq 'embedded-command') { $actualRecord.CommandLine = '"' + $authenticationRequest.Executable + '" -Command "' + $authenticationRequest.Helper + '"' }
            if ($mode -eq 'extra-argument') { $actualRecord.CommandLine += ' -Command exit' }
            if ($mode -eq 'reused-query') { $actualRecord.StartTicks -= 10000000 }
            if ($mode -eq 'wrong-session') { $mock.SessionId = 7; $actualRecord.SessionId = 7 }
            if ($mode -eq 'wrong-owner') { $actualRecord.OwnerSID = 'S-1-5-18' }
            @{
                PID = $mock.Id; StartTicks = $mock.Created.ToUniversalTime().Ticks; Executable = $mock.ExpectedPath
                Script = $authenticationRequest.Helper; RequestId = $authenticationRequest.RequestId
                CommandLine = $commandLine
            } | ConvertTo-Json | Set-Content -LiteralPath ($authenticationRequest.Report + '.identity.json')
            $rejected = $false
            try { Stop-DeployHelper $authenticationRequest } catch {
                Assert ($_.Exception.Message -match 'identity changed|unconfirmed') "Unexpected authentication failure in ${mode}: $_"
                $rejected = $true
            }
            Assert ($rejected -eq ($mode -ne 'valid')) "Incorrect authentication result in $mode."
            Assert ($mock.Kills -eq [int]($mode -eq 'valid') -and $mock.Waits -eq [int]($mode -eq 'valid')) "Unverified candidate killed or valid helper not awaited in $mode."
            Assert ($lookups -eq 1 -and $mock.Disposals -eq 1) "Candidate re-resolved or pin leaked in $mode."
        }
    } finally {
        Remove-Item Function:\Get-Process
        Set-Item Function:\Get-DeployHelperProcessRecords -Value $originalQuery
    }
    Write-Host 'PASS forged writable report: unrelated exe, same-exe wrong request, embedded/extra argv, unavailable/error query, reused query, wrong session; valid helper killed/awaited/disposed on same pin'

    $taskMock = [pscustomobject]@{ Calls = 0; Flags = 0; Session = 0; SID = ''; Parameters = 'unset' }
    $taskMock | Add-Member ScriptMethod RunEx {
        param($Parameters, $Flags, $Session, $SID)
        $this.Calls++; $this.Flags = $Flags; $this.Session = $Session; $this.SID = $SID; $this.Parameters = $Parameters
    }
    Invoke-DeployTask $taskMock 2 $authenticationRequest.ExpectedSID
    Assert ($taskMock.Calls -eq 1 -and $taskMock.Flags -eq 4 -and $taskMock.Session -eq 2 -and
        $taskMock.SID -eq $authenticationRequest.ExpectedSID -and $null -eq $taskMock.Parameters) 'RunEx flag/session/account contract failed.'
    Assert-DeploySession 2 2
    Write-Host 'PASS RunEx exact flag=4, selected desktop=2 and resolved SID contract; correct-session guard'

    $dummyScript = Join-Path $ScratchRoot 'owned-target.ps1'
    Set-Content -LiteralPath $dummyScript -Value 'Start-Sleep -Seconds 15'
    $target = Start-Process powershell.exe -ArgumentList ('-NoProfile -File "' + $dummyScript + '"') -PassThru -WindowStyle Hidden
    $null = $target.Handle
    $owned.Add($target)
    $pinned = Get-DeployPinnedProcess ([pscustomobject]@{
        ProcessId = $target.Id; ExecutablePath = $target.Path; StartTicks = $target.StartTime.ToUniversalTime().Ticks
    }) 0
    try { Stop-DeployPinnedProcess $pinned } finally { $pinned.Dispose() }
    Assert ($target.HasExited) 'Real owned target survived pinned termination.'
    Assert ($null -eq (Get-DeployPinnedProcess ([pscustomobject]@{
        ProcessId = $target.Id; ExecutablePath = $target.Path; StartTicks = 0
    }))) 'Exited owned PID did not retire benignly.'
    Write-Host 'PASS real isolated owned process pinned, terminated and awaited; exited PID lookup retires benignly'

    $aclPayload = Join-Path $ScratchRoot 'locked-payload'
    $aclRequest = Join-Path $ScratchRoot 'acl-request'
    New-Item -ItemType Directory -Path $aclPayload, $aclRequest | Out-Null
    $aclReports = Join-Path $aclRequest 'reports'
    $limitedSID = 'S-1-5-21-111111111-222222222-333333333-1001'
    Set-DeployDirectoryAccess $aclRequest $limitedSID
    New-Item -ItemType Directory -Path $aclReports | Out-Null
    Set-DeployDirectoryAccess $aclReports $limitedSID -Writable
    $aclHelper = Join-Path $aclPayload 'helper.ps1'
    $aclBody = @'
param([string]$Report, [string]$RequestId, [long]$DeadlineTicks, [string]$RequestCancel,
    [int]$ExpectedSessionId, [string]$ExpectedSID)
$ErrorActionPreference = 'Stop'
__LIFECYCLE__
$Fail = $RequestId -eq 'acl-fixture-True'
$denied = $false
try { Set-Content -LiteralPath (Join-Path $PSScriptRoot 'forbidden') -Value 'must not write' } catch [UnauthorizedAccessException] {
    $denied = $true
}
if (-not $denied) { throw 'Locked payload unexpectedly writable.' }
try {
    Publish-DeployHelper
    if ($Fail) { throw 'synthetic report failure' }
    @{ Success = $true; PayloadWriteDenied = $denied } | ConvertTo-Json |
        Set-Content -LiteralPath ($Report + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($Report + '.tmp') -Destination $Report -ErrorAction Stop
} catch {
    @{ Success = $false; Error = $_.Exception.ToString(); PayloadWriteDenied = $denied } | ConvertTo-Json |
        Set-Content -LiteralPath ($Report + '.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($Report + '.tmp') -Destination $Report -ErrorAction Stop
    throw
}
'@
    Set-Content -LiteralPath $aclHelper -Value $aclBody.Replace('__LIFECYCLE__', $lifecycle) -Encoding UTF8
    $originalPayloadACL = Get-Acl -LiteralPath $aclPayload
    $payloadACL = Get-Acl -LiteralPath $aclPayload
    $payloadACL.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        ([Security.Principal.WindowsIdentity]::GetCurrent().User), 'Write', 'ContainerInherit,ObjectInherit', 'None', 'Deny')))
    Set-Acl -LiteralPath $aclPayload -AclObject $payloadACL
    foreach ($entry in @(@{ Directory = $aclRequest; Writable = $false }, @{ Directory = $aclReports; Writable = $true })) {
        $acl = Get-Acl -LiteralPath $entry.Directory
        Assert $acl.AreAccessRulesProtected 'Request ACL inherited broad write grants.'
        $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
        $targetRule = @($rules | Where-Object { $_.IdentityReference.Value -eq $limitedSID })
        Assert ($targetRule.Count -eq 1 -and $targetRule[0].AccessControlType -eq 'Allow') 'Intended interactive SID grant missing.'
        $rights = $targetRule[0].FileSystemRights
        Assert ((($rights -band [Security.AccessControl.FileSystemRights]::Modify) -eq
            [Security.AccessControl.FileSystemRights]::Modify) -eq $entry.Writable) 'Interactive SID writable scope incorrect.'
        Assert ($targetRule[0].InheritanceFlags -eq 'ContainerInherit,ObjectInherit') 'Identity/result files would not inherit interactive access.'
        $allowed = @($limitedSID, 'S-1-5-18', 'S-1-5-32-544', [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
        Assert (-not @($rules | Where-Object { $_.IdentityReference.Value -notin $allowed }).Count) 'Unexpected principal can access request reports.'
    }
    foreach ($failure in @($false, $true)) {
        $report = Join-Path $aclReports ("result-$failure.json")
        $requestId = "acl-fixture-$failure"
        $request = New-HelperRequest $aclHelper $report $requestId ([DateTime]::UtcNow.AddSeconds(15).Ticks)
        $arguments = (@(Get-DeployHelperArguments $request) | ForEach-Object { '"' + $_ + '"' }) -join ' '
        $helper = Start-Process powershell.exe -ArgumentList $arguments -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $aclReports "stdout-$failure") -RedirectStandardError (Join-Path $aclReports "stderr-$failure")
        $null = $helper.Handle
        $owned.Add($helper)
        Assert ($helper.WaitForExit(6000)) 'Locked-payload helper did not finish within six seconds.'
        Wait-File ($report + '.identity.json')
        Wait-File $report
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        Assert ($result.PayloadWriteDenied -and $result.Success -eq (-not $failure)) 'Identity/result publication failed outside locked payload.'
        Assert (($helper.ExitCode -eq 0) -eq (-not $failure)) 'Failure-report helper did not surface its error.'
        Stop-DeployHelper $request
        Assert (Test-Path -LiteralPath ($report + '.cancel')) 'Report relocation lost cancellation marker.'
    }
    Write-Host 'PASS protected ACL/SID Modify inspection and real current-token payload Write deny; actual helper identity + success/error result writes outside payload (no real Limited-user token tested)'

    $fixture = Start-Fixture 'delayed-query' -QueryDelay 3000
    Wait-File ($fixture.Report + '.identity.json')
    $reportWait = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $fixture.Report) -and $reportWait.ElapsedMilliseconds -lt 500) {
        Start-Sleep -Milliseconds 25
    }
    Assert ($reportWait.ElapsedMilliseconds -ge 500 -and -not (Test-Path -LiteralPath $fixture.Report)) 'Original report timeout was not reproduced.'
    $retire = [Diagnostics.Stopwatch]::StartNew()
    Stop-DeployHelper $fixture.Request
    $retire.Stop()
    Assert ($fixture.Process.HasExited) 'Timed-out helper survived failure return.'
    Assert ($retire.Elapsed.TotalSeconds -lt 6) 'Helper retirement exceeded its bounded exit wait.'
    Start-Sleep -Milliseconds 3200
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Directory 'close'))) 'Delayed query closed after failure return.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Directory 'launch'))) 'Delayed query launched after failure return.'
    Write-Host "PASS stalled query >500ms report deadline: helper exited; zero actions after return (retirement $([Math]::Round($retire.Elapsed.TotalSeconds, 3))s)"

    $fixture = Start-Fixture 'before-publication' -BeforePublish 3000
    Assert (-not (Test-Path -LiteralPath ($fixture.Report + '.identity.json'))) 'Race fixture already published.'
    Stop-DeployHelper $fixture.Request
    Assert ($fixture.Process.HasExited) 'Unpublished helper survived cancellation.'
    Start-Sleep -Milliseconds 3200
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Directory 'close'))) 'Cancellation-before-publication allowed a close.'
    Write-Host 'PASS cancellation before identity publication: exact-command helper retired, no late actions'

    $fixture = Start-Fixture 'reused-identity' -QueryDelay 10000
    Wait-File ($fixture.Report + '.identity.json')
    $record = Get-Content -LiteralPath ($fixture.Report + '.identity.json') -Raw | ConvertFrom-Json
    $record.StartTicks = [long]$record.StartTicks - 10000000
    $record | ConvertTo-Json | Set-Content -LiteralPath ($fixture.Report + '.identity.json')
    $rejected = $false
    try { Stop-DeployHelper $fixture.Request } catch {
        Assert ($_.Exception.Message -match 'identity changed') 'Unexpected mismatch rejection.'
        $rejected = $true
    }
    Assert ($rejected -and -not $fixture.Process.HasExited) 'Mismatched creation identity was terminated.'
    $fixture.Process.Kill()
    Assert ($fixture.Process.WaitForExit(5000)) 'Owned mismatch fixture did not exit.'
    Write-Host 'PASS stale/reused PID creation identity rejected without termination'

    $fixture = Start-Fixture 'success' -Launch
    Wait-File $fixture.Report
    $result = Get-Content -LiteralPath $fixture.Report -Raw | ConvertFrom-Json
    $app = Get-Process -Id $result.NewPID -ErrorAction Stop
    $null = $app.Handle
    $owned.Add($app)
    Stop-DeployHelper $fixture.Request
    Assert ($fixture.Process.HasExited -and -not $app.HasExited) 'Success retirement killed the independent dummy app.'
    Write-Host 'PASS report-existing success/partial-success helper retirement leaves independent dummy app alive'

    $fixture = Start-Fixture 'forged-dummy-report' -QueryDelay 10000
    Wait-File ($fixture.Report + '.identity.json')
    $realRecord = Get-Content -LiteralPath ($fixture.Report + '.identity.json') -Raw | ConvertFrom-Json
    @{
        PID = $app.Id; StartTicks = $app.StartTime.ToUniversalTime().Ticks; Executable = $fixture.Request.Executable
        Script = $fixture.Helper; RequestId = $fixture.Request.RequestId
        CommandLine = (Format-HelperCommand $fixture.Request)
    } | ConvertTo-Json | Set-Content -LiteralPath ($fixture.Report + '.identity.json')
    $rejected = $false
    try { Stop-DeployHelper $fixture.Request } catch {
        Assert ($_.Exception.Message -match 'unconfirmed') 'Forged dummy report did not fail actual argv authentication.'
        $rejected = $true
    }
    Assert ($rejected -and -not $app.HasExited -and -not $fixture.Process.HasExited) 'Forged writable identity killed a dummy or an unauthenticated helper.'
    $realRecord | ConvertTo-Json | Set-Content -LiteralPath ($fixture.Report + '.identity.json')
    Stop-DeployHelper $fixture.Request
    Assert ($fixture.Process.HasExited -and -not $app.HasExited) 'Restored genuine identity did not retire only the helper.'
    Write-Host 'PASS forged on-disk identity targets an owned same-exe dummy with spoofed report argv: actual query rejects, dummy survives; genuine helper retires'

    $fixture = Start-Fixture 'expired-query' -QueryDelay 2000 -DeadlineMilliseconds 1000
    Assert ($fixture.Process.WaitForExit(6000)) 'Expired delayed-query helper did not exit.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Directory 'close'))) 'Expired delayed query closed an app.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Directory 'launch'))) 'Expired delayed query launched an app.'
    Write-Host 'PASS actual delayed-query helper exits on absolute expiry without close/launch'

    $fixture = Start-Fixture 'late-start' -AlreadyCancelled
    Assert ($fixture.Process.WaitForExit(6000)) 'Late helper did not acknowledge prior cancellation by exiting.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Directory 'close'))) 'Late-starting helper ignored the persistent request marker.'
    Write-Host 'PASS helper started after request cancellation exits without actions'

    $fixture = Start-Fixture 'failure-report' -QueryDelay 3000 -FailureReport
    Wait-File $fixture.Report
    Assert (-not (Get-Content -LiteralPath $fixture.Report -Raw | ConvertFrom-Json).Success) 'Failure-report fixture did not fail.'
    Stop-DeployHelper $fixture.Request
    Assert ($fixture.Process.HasExited) 'Report-existing failed helper was not retired.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Directory 'close'))) 'Failed report allowed a subsequent close.'
    Write-Host 'PASS existing failure report still cancels and awaits helper termination'

    $fixture = Start-Fixture 'wrong-desktop' -SessionId 65535
    Assert ($fixture.Process.WaitForExit(6000) -and $fixture.Process.ExitCode -ne 0) 'Wrong-desktop helper did not fail.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture.Directory 'close')) -and
        -not (Test-Path -LiteralPath (Join-Path $fixture.Directory 'launch'))) 'Wrong-desktop helper performed a destructive side effect.'
    Assert ((Get-Content -LiteralPath (Join-Path $fixture.Directory 'stderr') -Raw) -match 'interactive session mismatch') 'Wrong-desktop failure was not explicit.'
    $sideEffect = $false
    $rejected = $false
    try { Assert-DeploySession 7 2; $sideEffect = $true } catch { $rejected = $true }
    Assert ($rejected -and -not $sideEffect) 'Same-SID desktop 7 accepted requested desktop 2.'
    Write-Host 'PASS wrong expected desktop exits before close/launch markers; same-SID requested 2/actual 7 fails closed (no real Terminal Services tested)'

    $fixture = Start-Fixture 'actual-helper-wrong-desktop' -SessionId 65535 -ActualHelper
    Assert ($fixture.Process.WaitForExit(6000) -and $fixture.Process.ExitCode -ne 0) 'Actual deployment helper did not reject the wrong desktop.'
    Wait-File $fixture.Report
    $failure = Get-Content -LiteralPath $fixture.Report -Raw | ConvertFrom-Json
    Assert (-not $failure.Success -and $failure.Error -match 'interactive session mismatch') 'Actual helper did not publish an explicit wrong-session failure before app discovery.'
    Write-Host 'PASS actual embedded deployment helper wrong-session publishes failure before app discovery/closure/launch'

    $built = Join-Path $ScratchRoot 'win-x64'
    New-Item -ItemType Directory -Path $built | Out-Null
    $rid = 'win-x64'
    $requiredNode = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$required'
    }, $true)
    $required = & ([scriptblock]::Create($requiredNode.Right.Extent.Text))
    foreach ($name in $required) {
        $path = Join-Path $built $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Set-Content -LiteralPath $path -Value 'non-executable offline fixture'
    }
    Set-Content -LiteralPath (Join-Path $built 'RedrainCast.App.runtimeconfig.json') -Value '{"runtimeOptions":{"includedFrameworks":[{"name":"fixture"}]}}'
    $key = Join-Path $ScratchRoot 'fixture-key'
    Set-Content -LiteralPath $key -Value 'offline fixture'
    $offlineWarnings = @()
    & $scriptPath -BuiltDirectory $built -RemoteHost fixture.invalid -SSHUser fixture -KeyPath $key -WhatIf -WarningVariable offlineWarnings
    Assert (($offlineWarnings -join ' ') -match 'distinct SSH controller account.*Same-SID.*offline validation cannot resolve remote SIDs') 'WhatIf omitted the same-SID safety prerequisite.'
    & $scriptPath -BuiltDirectory $built -RemoteHost fixture.invalid -SSHUser fixture -KeyPath $key -RemoteDirectory 'C:\Program Files\RedrainCast-DevDeploy' -WhatIf
    foreach ($unsafe in @('C:\', 'C:\safe\..', 'C:\safe\bad&path', 'C:\safe\bad"quote')) {
        $rejected = $false
        try { & $scriptPath -BuiltDirectory $built -RemoteHost fixture.invalid -SSHUser fixture -KeyPath $key -RemoteDirectory $unsafe -WhatIf } catch { $rejected = $true }
        Assert $rejected "Unsafe path accepted: $unsafe"
    }
    foreach ($parameter in @(@{ RemoteHost = 'bad;host'; SSHUser = 'fixture' }, @{ RemoteHost = 'fixture.invalid'; SSHUser = 'bad&user' })) {
        $rejected = $false
        try { & $scriptPath -BuiltDirectory $built -KeyPath $key @parameter -WhatIf } catch { $rejected = $true }
        Assert $rejected 'Unsafe SSH parameter accepted.'
    }
    Write-Host 'PASS offline complete-payload WhatIf and unsafe remote path/host/user guards; no network'
    Write-Host "PASS all deployment regressions in $([Math]::Round($clock.Elapsed.TotalSeconds, 3)) seconds"
} finally {
    foreach ($process in $owned) {
        try {
            if (-not $process.HasExited) {
                $process.Kill()
                Assert ($process.WaitForExit(5000)) "Owned fixture PID $($process.Id) did not exit."
            }
        } finally { $process.Dispose() }
    }
    if ($originalPayloadACL) { Set-Acl -LiteralPath $aclPayload -AclObject $originalPayloadACL -ErrorAction Stop }
    Remove-Item -LiteralPath $ScratchRoot -Recurse -Force -ErrorAction Stop
}
