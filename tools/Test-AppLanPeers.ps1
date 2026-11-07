param(
    [Parameter(Mandatory=$true)][string]$App,
    [Parameter(Mandatory=$true)][string]$RemoteApp,
    [Parameter(Mandatory=$true)][string]$LocalAddress,
    [Parameter(Mandatory=$true)][string]$RemoteAddress,
    [Parameter(Mandatory=$true)][string]$ReportDirectory,
    [Parameter(Mandatory=$true)][string]$SharedReportDirectory,
    [string]$SshUser = 'sshuser',
    [string]$SshKey = "$HOME\.ssh\id_ed25519",
    [ValidateScript({ $_ -eq 0 -or ($_ -ge 60 -and $_ -le 600) })][int]$DurationSeconds = 0,
    [switch]$SilentPcm,
    [switch]$Timeline,
    [ValidateRange(1,3)][int]$Repetitions = 3
)
$ErrorActionPreference = 'Stop'
if ($Timeline -and (-not $SilentPcm -or $DurationSeconds -ne 60)) {
    throw 'Timeline requires -SilentPcm -DurationSeconds 60; normal LAN permission gates still apply.'
}
$App = (Resolve-Path -LiteralPath $App).Path
if (-not [IO.Path]::IsPathRooted($ReportDirectory)) { $ReportDirectory = Join-Path (Get-Location).Path $ReportDirectory }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $ReportDirectory) { throw 'LAN tests require a new report directory.' }
New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
$stamp = [Guid]::NewGuid().ToString('N')
$summary = @()
function ReadJson([string]$Path) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        try { return (Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop) }
        catch {
            if ([DateTime]::UtcNow -ge $deadline) { throw }
            Start-Sleep -Milliseconds 100
        }
    } while ($true)
}
function Remote([string]$Script) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('$ProgressPreference="SilentlyContinue";$ErrorActionPreference="Stop";'+$Script))
    $output = & ssh -i $SshKey -o BatchMode=yes -o ConnectTimeout=10 "$SshUser@$RemoteAddress" "powershell -NoProfile -EncodedCommand $encoded"
    if ($LASTEXITCODE -ne 0) { throw "Remote command failed: $output" }
    return ($output -join "`n")
}
function RegisterRemote([string]$Task, [string]$Exe, [string]$Arguments) {
    Remote @"
`$owners=@(Get-CimInstance Win32_Process -Filter "name='explorer.exe'" | ForEach-Object {`$o=Invoke-CimMethod -InputObject `$_ -MethodName GetOwner;if(`$o.ReturnValue -ne 0){throw 'Cannot resolve interactive owner'};"`$(`$o.Domain)\`$(`$o.User)"} | Select-Object -Unique)
if (`$owners.Count -ne 1) {throw 'Expected one unambiguous interactive owner'}
`$action=New-ScheduledTaskAction -Execute '$Exe' -Argument '$Arguments'
`$principal=New-ScheduledTaskPrincipal -UserId `$owners[0] -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName '$Task' -Action `$action -Principal `$principal | Out-Null
"@ | Out-Null
}
$identityScript = @'
@{ipv4=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object IPAddress,InterfaceAlias);architecture=(Get-CimInstance Win32_OperatingSystem).OSArchitecture;owners=@(Get-CimInstance Win32_Process -Filter "name='explorer.exe'" | ForEach-Object {$o=Invoke-CimMethod -InputObject $_ -MethodName GetOwner;@{pid=$_.ProcessId;session=$_.SessionId;owner="$($o.Domain)\$($o.User)"}})}
'@
$localIdentity = & ([scriptblock]::Create($identityScript))
$remoteIdentity = Remote "($identityScript) | ConvertTo-Json -Depth 5" | ConvertFrom-Json
if (-not @($localIdentity.ipv4 | Where-Object IPAddress -eq $LocalAddress).Count -or
    -not @($remoteIdentity.ipv4 | Where-Object IPAddress -eq $RemoteAddress).Count) { throw 'Exact host IPv4 gate failed.' }
$localIdentity.ipv4 = @($localIdentity.ipv4 | Where-Object IPAddress -eq $LocalAddress)
$remoteIdentity.ipv4 = @($remoteIdentity.ipv4 | Where-Object IPAddress -eq $RemoteAddress)
@{utc=[DateTime]::UtcNow.ToString('o');local=$localIdentity;remote=$remoteIdentity} |
    ConvertTo-Json -Depth 7 | Set-Content "$ReportDirectory\identities.json"
$hashScript = @'
$base=Split-Path -Parent $exe
@(foreach($name in @('RedrainCast.App.exe','RedrainCast.App.dll','RedrainCast.Protocol.dll','capture-worker\RedrainCast.CaptureWorker.exe')){
 $file=Join-Path $base $name
 @{relative=$name;sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash}
})
'@
$localHashes = @(& ([scriptblock]::Create("`$exe='$App';"+$hashScript)) | ForEach-Object { [pscustomobject]$_ })
$remoteHashes = Remote ("`$exe='$RemoteApp';`$hashes=@(& {"+$hashScript+
    '} | ForEach-Object { [pscustomobject]$_ }); ConvertTo-Json -InputObject $hashes') | ConvertFrom-Json
@{localPath=$App;remotePath=$RemoteApp;local=$localHashes;remote=$remoteHashes} |
    ConvertTo-Json -Depth 6 | Set-Content "$ReportDirectory\candidate-hashes.json"
if (@(Compare-Object $localHashes $remoteHashes -Property relative,sha256).Count) { throw 'Local and remote candidate hashes differ.' }
$gateScript = @'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class GateNative {
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
 [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
 [DllImport("user32.dll")] public static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder buffer,int bytes,out int needed);
 [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr handle);
}
"@
$form=New-Object Windows.Forms.Form
$form.Text='RedrainCast owned synthetic interactive gate'
$form.Width=640;$form.Height=200;$form.TopMost=$true
try {
 $form.Show();$form.Activate();[Windows.Forms.Application]::DoEvents();Start-Sleep -Milliseconds 750;[Windows.Forms.Application]::DoEvents()
 $desktop=[GateNative]::OpenInputDesktop(0,$false,1);$name=New-Object Text.StringBuilder 256;$needed=0
 $read=$desktop -ne [IntPtr]::Zero -and [GateNative]::GetUserObjectInformation($desktop,2,$name,512,[ref]$needed)
 if($desktop -ne [IntPtr]::Zero){[void][GateNative]::CloseDesktop($desktop)}
 [uint32]$foregroundPid=0;[void][GateNative]::GetWindowThreadProcessId([GateNative]::GetForegroundWindow(),[ref]$foregroundPid)
 [uint32]$ownerPid=0;[void][GateNative]::GetWindowThreadProcessId($form.Handle,[ref]$ownerPid)
 $visible=[GateNative]::IsWindowVisible($form.Handle);$minimized=[GateNative]::IsIconic($form.Handle)
 $result=@{utc=[DateTime]::UtcNow.ToString('o');passed=($read -and $name.ToString() -eq 'Default' -and $visible -and -not $minimized -and $ownerPid -eq $PID);
 inputDesktop=$name.ToString();ownPid=$PID;ownHwnd=$form.Handle.ToInt64();ownWindowVisible=$visible;ownWindowMinimized=$minimized;foregroundPid=$foregroundPid;
 session=(Get-Process -Id $PID).SessionId;identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name;architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString();
 foregroundIsNotEligibilityRequirement=$true}
} catch {$result=@{passed=$false;error=$_.Exception.ToString()}} finally {$form.Close();$form.Dispose()}
[IO.File]::WriteAllText($report+'.tmp',($result|ConvertTo-Json -Depth 4))
[IO.File]::Move($report+'.tmp',$report)
'@
function Gate([string]$Direction) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("`$report='$ReportDirectory\$Direction-main-gate.json';"+$gateScript))
    & powershell -NoProfile -EncodedCommand $encoded
    $gate = ReadJson "$ReportDirectory\$Direction-main-gate.json"
    if (-not $gate.passed) { throw 'Local unlocked/visible owned-window gate failed.' }
    $task = "RedrainCast-Owned-Gate-$Direction-$stamp"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("`$report='$SharedReportDirectory\$Direction-remote-gate.json';"+$gateScript))
    RegisterRemote $task 'powershell.exe' "-NoProfile -EncodedCommand $encoded"
    try {
        Remote "Start-ScheduledTask -TaskName '$task'" | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while (-not (Test-Path "$ReportDirectory\$Direction-remote-gate.json")) {
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Remote interactive gate timed out.' }
            Start-Sleep -Milliseconds 100
        }
        $gate = ReadJson "$ReportDirectory\$Direction-remote-gate.json"
        if (-not $gate.passed) { throw 'Remote unlocked/visible owned-window gate failed.' }
    } finally { Remote "Unregister-ScheduledTask -TaskName '$task' -Confirm:`$false" | Out-Null }
    Write-Output "Fresh unlocked/visible gates passed: $Direction"
}
$sampleScript = @'
function ReadGpu($ids) {
 try {
  $paths=@(foreach($id in $ids){"\GPU Process Memory(pid_${id}_*)\Dedicated Usage";"\GPU Process Memory(pid_${id}_*)\Shared Usage"})
  $counters=Get-Counter -Counter $paths -ErrorAction Stop
  if(@($counters.CounterSamples | Where-Object Status -ne 0).Count){throw 'PID-scoped GPU counters reported invalid status'}
  $bytes=($counters.CounterSamples | Measure-Object CookedValue -Sum).Sum
  if($null -eq $bytes){throw 'No PID-scoped GPU samples returned'}
  @{bytes=[long]$bytes;unavailableReason=$null}
 } catch {@{bytes=$null;unavailableReason=$_.Exception.Message}}
}
$apps=@(Get-CimInstance Win32_Process -Filter "name='RedrainCast.App.exe'" | Where-Object {$_.ExecutablePath -eq $exe -and $_.CommandLine.Contains($runName)})
$processes=@(foreach($app in $apps){
 $p=Get-Process -Id $app.ProcessId -ErrorAction SilentlyContinue
 $children=@(Get-CimInstance Win32_Process -Filter ('ParentProcessId='+$app.ProcessId) | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith((Split-Path -Parent $exe)+'\')})
 $childRows=@(foreach($child in $children){$cp=Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue;@{pid=$child.ProcessId;path=$child.ExecutablePath;privateBytes=$cp.PrivateMemorySize64;workingSetBytes=$cp.WorkingSet64}})
 $ids=@($app.ProcessId)+@($children | ForEach-Object {$_.ProcessId})
 $gpu=ReadGpu $ids
 @{pid=$app.ProcessId;path=$app.ExecutablePath;session=$app.SessionId;privateBytes=$p.PrivateMemorySize64;workingSetBytes=$p.WorkingSet64;children=$childRows;gpu=$gpu;
 sockets=@(Get-NetTCPConnection -OwningProcess $app.ProcessId -ErrorAction SilentlyContinue | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,@{n='state';e={$_.State.ToString()}})}
})
@{utc=[DateTime]::UtcNow.ToString('o');processes=$processes}
'@
function RemoteSnapshot([string]$Name) {
    Remote ("`$exe='$RemoteApp';`$runName='$Name';"+$sampleScript+' | ConvertTo-Json -Depth 8 -Compress') | ConvertFrom-Json
}
function Guard($Snapshot) {
    foreach ($process in $Snapshot.processes) {
        if ($process.privateBytes -gt 2GB -or @($process.children | Where-Object privateBytes -gt 1GB).Count -or
            ($null -ne $process.gpu.bytes -and $process.gpu.bytes -gt 1GB)) {
            throw 'Owned app/worker private-memory or combined PID-scoped GPU guard exceeded.'
        }
    }
}
try {
    foreach ($direction in @('main-to-remote','remote-to-main')) {
        Gate $direction
        foreach ($index in 1..$Repetitions) {
            $stop = if ($index -eq 2) { 'receiver-stop' } else { 'sender-stop' }
            $name = "$direction-$index-$stop"
            $run = Join-Path $ReportDirectory $name
            $sharedRun = "$SharedReportDirectory\$name"
            $task = "RedrainCast-Owned-$name-$stamp"
            $local = $null
            New-Item -ItemType Directory -Path $run | Out-Null
            $localRole = if ($direction -eq 'main-to-remote') { 'sender' } else { 'receiver' }
            $remoteRole = if ($localRole -eq 'sender') { 'receiver' } else { 'sender' }
            $address = if ($localRole -eq 'sender') { $RemoteAddress } else { $LocalAddress }
            $extra = ''
            if ($DurationSeconds -gt 0 -or $SilentPcm -or $Timeline) {
                $extra = " --peer-duration-seconds $DurationSeconds --peer-sample-seconds 5"
                if ($SilentPcm) { $extra += ' --peer-silent-pcm' }
                if ($Timeline) { $extra += ' --peer-timeline' }
            }
            try {
                RegisterRemote $task $RemoteApp "--peer-self-test $remoteRole `"$sharedRun`" $stop $address$extra"
                $localArguments = "--peer-self-test $localRole `"$run`" $stop $address$extra"
                if ($remoteRole -eq 'receiver') { Remote "Start-ScheduledTask -TaskName '$task'" | Out-Null }
                else { $local=Start-Process -FilePath $App -ArgumentList $localArguments -PassThru }
                $deadline = [DateTime]::UtcNow.AddSeconds(20)
                while (-not (Test-Path "$run\endpoint.json")) {
                    if ([DateTime]::UtcNow -gt $deadline) { throw 'Receiver endpoint timed out.' }
                    Start-Sleep -Milliseconds 100
                }
                if ($localRole -eq 'sender') { $local=Start-Process -FilePath $App -ArgumentList $localArguments -PassThru }
                else { Remote "Start-ScheduledTask -TaskName '$task'" | Out-Null }
                $deadline = [DateTime]::UtcNow.AddSeconds($DurationSeconds + 90)
                $samples = @()
                do {
                    $tick = [Diagnostics.Stopwatch]::StartNew()
                    $remoteSnapshot = RemoteSnapshot $name
                    $localSnapshot = & ([scriptblock]::Create("`$exe='$App';`$runName='$name';"+$sampleScript))
                    $samples += @{utc=[DateTime]::UtcNow.ToString('o');local=$localSnapshot;remote=$remoteSnapshot}
                    $samples | ConvertTo-Json -Depth 11 | Set-Content "$run\process-samples.json"
                    Guard $localSnapshot
                    Guard $remoteSnapshot
                    if ((Test-Path "$run\sender.json") -and (Test-Path "$run\receiver.json")) { break }
                    if ([DateTime]::UtcNow -gt $deadline) { throw 'Peer reports timed out.' }
                    $wait = [Math]::Max(0,5000-$tick.ElapsedMilliseconds)
                    if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
                } while ($true)
                $sender = ReadJson "$run\sender.json"
                $receiver = ReadJson "$run\receiver.json"
                if (-not $sender.passed -or -not $receiver.passed) { throw "Peer failure: sender=$($sender.failure); receiver=$($receiver.failure)" }
                if ($sender.loopbackOnly -or $receiver.loopbackOnly) { throw 'Loopback cannot establish LAN proof.' }
                if (-not $local.WaitForExit(10000)) { throw 'Local app failed to exit after report.' }
                $remaining = RemoteSnapshot $name
                if (@($remaining.processes).Count) { Start-Sleep -Seconds 2; $remaining=RemoteSnapshot $name }
                if (@($remaining.processes).Count) { throw 'Remote app failed to exit after report.' }
                $result = @{name=$name;passed=$true;senderPid=$sender.pid;receiverPid=$receiver.pid;receiverAddress=$address;durationSeconds=$DurationSeconds;
                    decoded=$receiver.measurements.decoded;presented=$receiver.measurements.presented;hardwareSurfaces=$receiver.measurements.hardwareSurfaces;
                    audioTested=[bool]$SilentPcm;playedAudioFrames=$receiver.measurements.playedAudioFrames;
                    stopMilliseconds=$sender.measurements.stopMilliseconds;static12Seconds=$sender.measurements.unchangedWindowSurvived;
                    childExited=$sender.measurements.childExited;surfacesReturned=$sender.measurements.surfacesReturned}
                $summary += $result
                $summary | ConvertTo-Json -Depth 6 | Set-Content "$ReportDirectory\summary.json"
                Write-Output ($result | ConvertTo-Json -Compress)
            } catch {
                @{name=$name;failure=$_.Exception.ToString();passed=$false;receiverExecutable= $(if($localRole -eq 'receiver'){$App}else{$RemoteApp});
                    permissionAction='If Windows reports this exact receiver executable blocked, only user approval on the trusted Private network may grant access; the harness never grants it.'} |
                    ConvertTo-Json -Depth 5 | Set-Content "$run\orchestrator-failure.json"
                throw
            } finally {
                if ($local -and -not $local.HasExited) {
                    [void]$local.CloseMainWindow()
                    if (-not $local.WaitForExit(10000)) { Stop-Process -Id $local.Id; [void]$local.WaitForExit(5000) }
                }
                Remote @"
`$apps=@(Get-CimInstance Win32_Process -Filter "name='RedrainCast.App.exe'" | Where-Object {`$_.ExecutablePath -eq '$RemoteApp' -and `$_.CommandLine.Contains('$name')})
foreach(`$app in `$apps){`$p=Get-Process -Id `$app.ProcessId -ErrorAction SilentlyContinue;if(`$p){[void]`$p.CloseMainWindow();if(-not `$p.WaitForExit(10000)){Stop-Process -Id `$app.ProcessId}}}
if(Get-ScheduledTask -TaskName '$task' -ErrorAction SilentlyContinue){Unregister-ScheduledTask -TaskName '$task' -Confirm:`$false}
"@ | Out-Null
                if ($local) { $local.Dispose() }
            }
        }
    }
} finally {
    @{utc=[DateTime]::UtcNow.ToString('o');ownedTaskStamp=$stamp;passedSessions=$summary.Count;expectedSessions=2*$Repetitions;
        audioRequested=[bool]$SilentPcm;durationSeconds=$DurationSeconds;
        immutableInputsNotModified=$true} |
        ConvertTo-Json | Set-Content "$ReportDirectory\completion.json"
}
