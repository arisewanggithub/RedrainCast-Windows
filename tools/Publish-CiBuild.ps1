param(
    [Parameter(Mandatory = $true)][ValidateSet('Prepare', 'Upload', 'Complete')][string]$Stage,
    [ValidateSet('arm64', 'x64')][string]$Architecture,
    [string]$ArtifactsDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CiBuild.ps1')
$runId = $env:GITHUB_RUN_ID
$attempt = [int]$env:GITHUB_RUN_ATTEMPT
$sha = $env:GITHUB_SHA
$run = Assert-CiBuildContext $runId $attempt $sha
$runNumber = [string]$run.run_number
if ($env:GITHUB_RUN_NUMBER -cne $runNumber) { throw 'Workflow run number does not match GitHub provenance.' }
$version = Get-CiBuildVersion $runNumber $attempt
$tag = Get-CiBuildTag $runNumber $attempt

if ($Stage -eq 'Prepare') {
    $null = Invoke-CiGh @('release', 'create', $tag, '--repo', $script:CiRepository, '--target', $sha,
        '--draft', '--title', "RedrainCast $tag",
        '--notes', "Official Windows release of $sha (run $runId, attempt $attempt). ARM64 and x64 MSIX files are identical to the Store submission inputs. If signing credentials were not configured, these files are unsigned and NOT directly installable. Signed direct installation requires trust in the configured publisher certificate; Microsoft Store signs packages independently on publication. No portable ZIP is distributed. Store publication remains separately owner-approved.")
    Write-Output "Created official release draft $tag"
    return
}

$release = Get-CiRelease $tag $sha $true
$directory = Join-Path $ArtifactsDirectory ".release-$runId-$attempt-$Stage-$Architecture"
if (Test-Path -LiteralPath $directory) { throw 'CI output directory already exists; existing work is preserved.' }
New-Item -ItemType Directory -Path $directory | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)

if ($Stage -eq 'Upload') {
    if (-not $Architecture) { throw 'CI upload requires an architecture.' }
    $msix = @(Get-ChildItem -LiteralPath (Join-Path $ArtifactsDirectory "msix-$Architecture") -Filter '*.msix' -File)
    if ($msix.Count -ne 1 -or
        ($msix[0].Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Release requires exactly one unlinked MSIX.'
    }
    $name = "RedrainCast-$Architecture.msix"
    $path = Join-Path $directory $name
    Copy-Item -LiteralPath $msix[0].FullName -Destination $path
    if ((Get-Item -LiteralPath $path).Length -ge 2GB) { throw 'MSIX exceeds the release asset size limit.' }
    Assert-CiMsix $path $Architecture $version
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksum = Join-Path $directory "RedrainCast-$Architecture.sha256"
    [IO.File]::WriteAllText($checksum, "$hash  $name`n", $utf8)
    $null = Invoke-CiGh @('release', 'upload', $tag, '--repo', $script:CiRepository, $path, $checksum)
    Write-Output "Uploaded and locally verified $Architecture package: $hash"
    return
}

Assert-CiReleaseAssets $release $false
$null = Invoke-CiGh @('release', 'download', $tag, '--repo', $script:CiRepository, '--dir', $directory)
$packages = @()
foreach ($arch in @('arm64', 'x64')) {
    $name = "RedrainCast-$arch.msix"
    $path = Join-Path $directory $name
    $checksum = (Get-Content -LiteralPath (Join-Path $directory "RedrainCast-$arch.sha256") -Raw).Trim()
    if ($checksum -cnotmatch "^([0-9a-f]{64})  RedrainCast-$arch\.msix$") { throw 'Invalid release package checksum file.' }
    $package = [pscustomobject]@{ architecture = $arch; fileName = $name; sha256 = $Matches[1]; sizeBytes = (Get-Item -LiteralPath $path).Length }
    Test-CiPackageFiles $package $directory $version
    $packages += $package
}
$manifest = [pscustomobject]@{
    schemaVersion = 2; repository = $script:CiRepository; runId = [string]$runId; runAttempt = $attempt
    runNumber = $runNumber; version = $version
    headSha = $sha; headBranch = $run.head_branch; event = $run.event; packages = $packages
}
Assert-CiManifest $manifest $runId $attempt $sha $runNumber
$manifestPath = Join-Path $directory 'ci-manifest.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), $utf8)
$null = Invoke-CiGh @('release', 'upload', $tag, '--repo', $script:CiRepository, $manifestPath)
$release = Get-CiRelease $tag $sha $true
Assert-CiReleaseAssets $release $true
$verification = Join-Path $directory 'verify'
New-Item -ItemType Directory -Path $verification | Out-Null
$null = Invoke-CiGh @('release', 'download', $tag, '--repo', $script:CiRepository, '--dir', $verification)
foreach ($file in Get-ChildItem -LiteralPath $directory -File) {
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -cne
        (Get-FileHash -LiteralPath (Join-Path $verification $file.Name)).Hash) {
        throw "Uploaded CI asset differs from the verified build: $($file.Name)"
    }
}
$latestArgument = if (Test-CiPromoteLatest $version) { '--latest' } else { '--latest=false' }
$null = Invoke-CiGh @('release', 'edit', $tag, '--repo', $script:CiRepository, '--draft=false', '--prerelease=false', $latestArgument)
$null = Get-CiRelease $tag $sha $false
Write-Output "Published verified official release: $tag"
