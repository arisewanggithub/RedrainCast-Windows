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
$tag = Get-CiBuildTag $runId $attempt

if ($Stage -eq 'Prepare') {
    $null = Invoke-CiGh @('release', 'create', $tag, '--repo', $script:CiRepository, '--target', $sha,
        '--draft', '--prerelease', '--title', "Windows CI build $runId/$attempt",
        '--notes', "Private main CI build of $sha. Unsigned MSIX packages; not a production release or Store approval.")
    Write-Output "Created private CI draft $tag"
    return
}

$release = Get-CiRelease $tag $sha $true
$directory = Join-Path $env:RUNNER_TEMP "ci-build-$runId-$attempt-$Stage-$Architecture"
if (Test-Path -LiteralPath $directory) { throw 'CI output directory already exists; existing work is preserved.' }
New-Item -ItemType Directory -Path $directory | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)

if ($Stage -eq 'Upload') {
    if (-not $Architecture) { throw 'CI upload requires an architecture.' }
    $published = Join-Path $ArtifactsDirectory "win-$Architecture"
    $msix = @(Get-ChildItem -LiteralPath (Join-Path $ArtifactsDirectory "msix-$Architecture") -Filter '*.msix' -File)
    $files = @(Get-ChildItem -LiteralPath $published -Recurse -File -Force)
    if ($files.Count -eq 0 -or $msix.Count -ne 1 -or
        ($msix[0].Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'CI package requires binaries and one unlinked unsigned MSIX.'
    }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $name = "RedrainCast-$Architecture.zip"
    $zipPath = Join-Path $directory $name
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $root = (Resolve-Path -LiteralPath $published).Path.TrimEnd('\') + '\'
        foreach ($file in $files) {
            if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'CI packages cannot contain linked files.' }
            $relative = $file.FullName.Substring($root.Length).Replace('\', '/')
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName,
                "win-$Architecture/$relative", [IO.Compression.CompressionLevel]::Optimal)
        }
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $msix[0].FullName,
            "msix-$Architecture/$($msix[0].Name)", [IO.Compression.CompressionLevel]::Optimal)
    } finally { $zip.Dispose() }
    if ((Get-Item -LiteralPath $zipPath).Length -ge 2GB) { throw 'CI ZIP exceeds the release asset size limit.' }
    Assert-CiZip $zipPath $Architecture
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksum = Join-Path $directory "RedrainCast-$Architecture.sha256"
    [IO.File]::WriteAllText($checksum, "$hash  $name`n", $utf8)
    $null = Invoke-CiGh @('release', 'upload', $tag, '--repo', $script:CiRepository, $zipPath, $checksum)
    Write-Output "Uploaded and locally verified $Architecture package: $hash"
    return
}

Assert-CiReleaseAssets $release $false
$null = Invoke-CiGh @('release', 'download', $tag, '--repo', $script:CiRepository, '--dir', $directory)
$packages = @()
foreach ($arch in @('arm64', 'x64')) {
    $name = "RedrainCast-$arch.zip"
    $path = Join-Path $directory $name
    $checksum = (Get-Content -LiteralPath (Join-Path $directory "RedrainCast-$arch.sha256") -Raw).Trim()
    if ($checksum -cnotmatch "^([0-9a-f]{64})  RedrainCast-$arch\.zip$") { throw 'Invalid CI package checksum file.' }
    $package = [pscustomobject]@{ architecture = $arch; fileName = $name; sha256 = $Matches[1]; sizeBytes = (Get-Item -LiteralPath $path).Length }
    Test-CiPackageFiles $package $directory
    $packages += $package
}
$manifest = [pscustomobject]@{
    schemaVersion = 1; repository = $script:CiRepository; runId = [string]$runId; runAttempt = $attempt
    headSha = $sha; headBranch = $run.head_branch; event = $run.event; packages = $packages
}
Assert-CiManifest $manifest $runId $attempt $sha
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
$null = Invoke-CiGh @('release', 'edit', $tag, '--repo', $script:CiRepository, '--draft=false', '--prerelease', '--latest=false')
$null = Get-CiRelease $tag $sha $false
Write-Output "Published verified private CI build: $tag"
