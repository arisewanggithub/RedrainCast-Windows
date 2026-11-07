param(
    [string]$App = (Join-Path $PSScriptRoot 'win-arm64\RedrainCast.App.exe'),
    [string]$ReportDirectory = (Join-Path $env:LOCALAPPDATA ("RedrainCast\PeerTests\" + [Guid]::NewGuid().ToString('N'))),
    [ValidateScript({ $_ -eq 0 -or ($_ -ge 60 -and $_ -le 600) })][int]$DurationSeconds = 0,
    [switch]$SilentPcm,
    [switch]$Timeline,
    [switch]$StaticSource,
    [ValidateSet('sender-stop', 'receiver-stop')][string[]]$StopSide = @('sender-stop', 'receiver-stop'),
    [ValidateSet(5)][int]$SampleSeconds = 5
)
$ErrorActionPreference = 'Stop'
if ($Timeline -and (-not $SilentPcm -or $DurationSeconds -ne 60)) {
    throw 'Timeline requires -SilentPcm -DurationSeconds 60.'
}
if ($StaticSource -and (-not $SilentPcm -or $DurationSeconds -ne 0 -or $Timeline)) {
    throw 'StaticSource requires -SilentPcm and the bounded default duration without Timeline.'
}
$App = (Resolve-Path -LiteralPath $App).Path
if (-not [IO.Path]::IsPathRooted($ReportDirectory)) { $ReportDirectory = Join-Path (Get-Location).Path $ReportDirectory }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
foreach ($stopDirection in $StopSide) {
    $run = Join-Path $ReportDirectory $stopDirection
    if (Test-Path -LiteralPath $run) { throw "Use a fresh report directory: $run" }
    New-Item -ItemType Directory -Path $run -Force | Out-Null
    $owned = @()
    try {
        foreach ($role in @('receiver', 'sender')) {
            $arguments = @('--peer-self-test', $role, ('"' + $run + '"'), $stopDirection,
                '--peer-duration-seconds', $DurationSeconds, '--peer-sample-seconds', $SampleSeconds)
            if ($SilentPcm) { $arguments += '--peer-silent-pcm' }
            if ($Timeline) { $arguments += '--peer-timeline' }
            if ($StaticSource) { $arguments += '--peer-static-source' }
            $child = Start-Process -FilePath $App -ArgumentList $arguments -PassThru
            $null = $child.Handle
            $owned += $child
            if ($role -eq 'receiver') {
                $deadline = [DateTime]::UtcNow.AddSeconds(15)
                while (-not (Test-Path -LiteralPath (Join-Path $run 'endpoint.json'))) {
                    if ($child.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw "Receiver did not become ready. Reports: $run" }
                    Start-Sleep -Milliseconds 100
                }
            }
        }
        foreach ($child in $owned) {
            $waitMilliseconds = if ($DurationSeconds -eq 0) { 75000 } else { ($DurationSeconds + 90) * 1000 }
            if (-not $child.WaitForExit($waitMilliseconds)) { throw "Owned peer PID $($child.Id) timed out. Reports: $run" }
        }
        foreach ($role in @('sender', 'receiver')) {
            $report = Join-Path $run "$role.json"
            if (-not (Test-Path -LiteralPath $report)) { throw "Missing $role report: $report" }
            $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
            if (-not $result.passed) { throw "$role failed: $($result.failure). Report: $report" }
            Write-Output "$stopDirection $role passed (PID $($result.pid)): $($result.measurements | ConvertTo-Json -Compress)"
        }
    }
    finally {
        foreach ($child in $owned) {
            if (-not $child.HasExited) {
                $null = $child.CloseMainWindow()
                if (-not $child.WaitForExit(5000)) { Stop-Process -Id $child.Id; $null = $child.WaitForExit(5000) }
            }
            $child.Dispose()
        }
    }
}
Write-Output "Separate app instances passed Stop directions: $($StopSide -join ', '). Reports: $ReportDirectory"
