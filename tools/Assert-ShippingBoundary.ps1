param([Parameter(Mandatory = $true)][string]$PublishedDirectory)
$ErrorActionPreference = 'Stop'
$removedMembers = @{
    'RedrainCast.App.dll' = @('RecordingLaunch', 'CaptureCadenceGate', 'RunRecordingSelfTestAsync', 'RunSenderCadenceSelfTestAsync', 'RunFfmpegSelfTestAsync')
    'RedrainCast.Receive.Core.dll' = @('ReceivedRecording', 'ReceivedRecordingOptions', 'RecordingArchive')
    'RedrainCast.Send.Core.dll' = @('CaptureCadenceAnalysis')
    'RedrainCast.Send.Media.dll' = @('FfmpegVideoSource', 'CreateCadenceSource', 'VerifyEmptyOutputModel')
    'RedrainCast.Receive.Media.dll' = @('OutputThreadProbe')
    'RedrainCast.Protocol.dll' = @('ThreadTiming', 'IThreadTimingSource', 'RecordOutputBoundary')
}
foreach ($assembly in $removedMembers.Keys) {
    foreach ($file in @(Get-ChildItem -LiteralPath $PublishedDirectory -Recurse -File -Filter $assembly)) {
        # Inspect null-terminated metadata names without loading architecture-specific assemblies.
        $metadata = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($file.FullName))
        foreach ($member in $removedMembers[$assembly]) {
            if ($metadata.Contains("$member`0")) {
                throw "Removed experimental member $member remains in $($file.FullName)"
            }
        }
    }
}
foreach ($file in @(Get-ChildItem -LiteralPath $PublishedDirectory -Recurse -Force)) {
    if ($file.Name -match '(?i)^(onnxruntime.*|DirectML|Microsoft\.Windows\.AI.*|Microsoft\.AI.*|Microsoft\.Asg\..*|Microsoft\.Windows\.Search.*|Microsoft\.Windows\.Widgets.*)\.dll$') {
        throw "Unused Windows App SDK component is forbidden: $($file.FullName)"
    }
    if ($file.Name -match '(?i)^Scrcpy.*\.(dll|exe|deps\.json|runtimeconfig\.json)$') {
        throw "Obsolete project output is forbidden: $($file.FullName)"
    }
    if ($file.Name -match '(?i)^(recording-helper|RedrainCast\.RecordingExport.*|Start-Recording\.cmd|.*TestHarness.*|.*SelfTest.*|ffmpeg(?:.*\.exe)?|ffprobe(?:.*\.exe)?|ffplay(?:.*\.exe)?|avformat.*|avdevice.*|avfilter.*|avcodec-(?!61\.dll$).+|avutil-(?!59\.dll$).+|swresample-(?!5\.dll$).+|swscale.*|postproc.*)$' -or
        $file.Name -match '(?i)\.(pdb|cmd|ps1|bat)$') {
        throw "Diagnostic/nonshipping file is forbidden: $($file.FullName)"
    }
}
foreach ($file in @(Get-ChildItem -LiteralPath $PublishedDirectory -Recurse -File -Filter '*.deps.json')) {
    $deps = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if (@($deps.libraries.PSObject.Properties.Name | Where-Object {
        $_ -match '(?i)^Scrcpy|RecordingExport|FFmpeg\.AutoGen/(?!7\.1\.1$)' }).Count -ne 0) {
        throw "Nonshipping dependency in $($file.FullName)"
    }
}
foreach ($file in @(Get-ChildItem -LiteralPath $PublishedDirectory -Recurse -File -Filter '*.runtimeconfig.json')) {
    $configuration = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($framework in @($configuration.runtimeOptions.includedFrameworks)) {
        if ($framework -and $framework.name -eq 'Microsoft.NETCore.App' -and ([version]$framework.version).Major -ne 10) {
            throw "Unexpected .NET runtime in $($file.FullName)"
        }
    }
}
