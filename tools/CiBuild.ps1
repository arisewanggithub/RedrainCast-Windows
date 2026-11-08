$script:CiRepository = 'arisewanggithub/RedrainCast-Windows'

function Get-CiBuildTag([string]$RunId, [int]$RunAttempt) {
    if ($RunId -notmatch '^[1-9][0-9]{0,18}$' -or $RunAttempt -lt 1) {
        throw 'CI builds require a positive run ID and attempt.'
    }
    return "ci-build-$RunId-$RunAttempt"
}

function Invoke-CiGh([string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $output = & gh @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CI operation failed: $($output -join "`n")"
    }
    return ($output -join "`n")
}

function Assert-CiRepositoryPrivate {
    $repository = Invoke-CiGh @('api', "repos/$script:CiRepository") | ConvertFrom-Json
    if ($repository.full_name -cne $script:CiRepository -or $repository.private -isnot [bool] -or
        -not $repository.private) {
        throw 'CI binaries may only be stored in the private Windows source repository.'
    }
}

function Assert-CiBuildContext([string]$RunId, [int]$RunAttempt, [string]$ExpectedSha) {
    $null = Get-CiBuildTag $RunId $RunAttempt
    if ($ExpectedSha -cnotmatch '^[0-9a-f]{40}$' -or $env:GITHUB_REPOSITORY -cne $script:CiRepository -or
        $env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_EVENT_NAME -notin @('push', 'workflow_dispatch')) {
        throw 'CI release writes require a trusted main push or manual main dispatch.'
    }
    Assert-CiRepositoryPrivate
    $run = Invoke-CiGh @('api', "repos/$script:CiRepository/actions/runs/$RunId") | ConvertFrom-Json
    if ([string]$run.id -cne $RunId -or $run.run_attempt -ne $RunAttempt -or $run.head_sha -cne $ExpectedSha -or
        $run.head_branch -cne 'main' -or $run.event -cne $env:GITHUB_EVENT_NAME -or
        $run.path -cne '.github/workflows/windows.yml') {
        throw 'CI release source does not match the dispatched main workflow run.'
    }
    return $run
}

function Assert-CiManifest($Manifest, [string]$RunId, [int]$RunAttempt, [string]$ExpectedSha) {
    $null = Get-CiBuildTag $RunId $RunAttempt
    foreach ($name in @('schemaVersion', 'repository', 'runId', 'runAttempt', 'headSha', 'headBranch', 'event', 'packages')) {
        if (-not $Manifest.PSObject.Properties[$name]) { throw "CI manifest is missing $name." }
    }
    if (($Manifest.schemaVersion -isnot [int] -and $Manifest.schemaVersion -isnot [long]) -or
        $Manifest.schemaVersion -ne 1 -or $Manifest.repository -cne $script:CiRepository -or
        $Manifest.runId -isnot [string] -or $Manifest.runId -cne $RunId -or
        ($Manifest.runAttempt -isnot [int] -and $Manifest.runAttempt -isnot [long]) -or
        $Manifest.runAttempt -ne $RunAttempt -or $ExpectedSha -cnotmatch '^[0-9a-f]{40}$' -or
        $Manifest.headSha -cne $ExpectedSha -or $Manifest.headBranch -cne 'main' -or
        $Manifest.event -notin @('push', 'workflow_dispatch') -or $Manifest.packages -isnot [array] -or
        $Manifest.packages.Count -ne 2) {
        throw 'CI manifest does not describe the exact trusted main build.'
    }
    $architectures = @($Manifest.packages | ForEach-Object { $_.architecture } | Sort-Object)
    if (Compare-Object @('arm64', 'x64') $architectures) { throw 'CI manifest needs exactly one package per architecture.' }
    foreach ($package in $Manifest.packages) {
        if ($package.architecture -isnot [string] -or $package.architecture -cnotin @('arm64', 'x64') -or
            $package.fileName -cne "RedrainCast-$($package.architecture).zip" -or
            $package.sha256 -isnot [string] -or $package.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            ($package.sizeBytes -isnot [int] -and $package.sizeBytes -isnot [long]) -or
            $package.sizeBytes -le 0 -or $package.sizeBytes -ge 2GB) {
            throw 'CI manifest contains an invalid package identity, size or checksum.'
        }
    }
}

function Get-CiRelease([string]$Tag, [string]$ExpectedSha, [bool]$Draft) {
    try {
        if ($Draft) {
            # The REST tag lookup excludes drafts; gh resolves them using authenticated release access.
            $view = Invoke-CiGh @('release', 'view', $Tag, '--repo', $script:CiRepository,
                '--json', 'tagName,targetCommitish,isDraft,isPrerelease,assets,url') | ConvertFrom-Json
            $release = [pscustomobject]@{
                tag_name = $view.tagName; target_commitish = $view.targetCommitish
                draft = $view.isDraft; prerelease = $view.isPrerelease
                assets = $view.assets; html_url = $view.url
            }
        } else {
            $release = Invoke-CiGh @('api', "repos/$script:CiRepository/releases/tags/$Tag") | ConvertFrom-Json
        }
    } catch {
        if ($_.Exception.Message -match 'HTTP 404|release not found') {
            throw "Private CI build $Tag is unavailable; re-run all jobs or start a new reviewed main build instead of using expired Actions artifacts."
        }
        throw
    }
    if ($release.tag_name -cne $Tag -or $release.target_commitish -cne $ExpectedSha -or
        $release.draft -isnot [bool] -or $release.draft -ne $Draft -or
        $release.prerelease -isnot [bool] -or -not $release.prerelease) {
        throw 'Private CI release identity, source, draft state or prerelease status differs from the requested build.'
    }
    return $release
}

function Assert-CiReleaseAssets($Release, [bool]$WithManifest) {
    $expected = @('RedrainCast-arm64.zip', 'RedrainCast-x64.zip', 'RedrainCast-arm64.sha256', 'RedrainCast-x64.sha256')
    if ($WithManifest) { $expected += 'ci-manifest.json' }
    $names = @($Release.assets | ForEach-Object { $_.name } | Sort-Object)
    if (Compare-Object ($expected | Sort-Object) $names -CaseSensitive) { throw 'Private CI release has an incomplete or unexpected asset set.' }
}

function Assert-CiZip([string]$Path, [string]$Architecture) {
    if ($Architecture -notin @('arm64', 'x64')) { throw 'Invalid CI archive architecture.' }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $msix = 0
        $files = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name -match '(^|/)\.\.?(/|$)|(^|/)[^/]*[ .](/|$)|^/|:' -or
                (-not $name.StartsWith("win-$Architecture/", [StringComparison]::Ordinal) -and
                 -not $name.StartsWith("msix-$Architecture/", [StringComparison]::Ordinal)) -or
                -not $seen.Add($name) -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                throw "Unsafe or unexpected CI ZIP member: $name"
            }
            if ($name -cmatch "^msix-$Architecture/[^/]+\.msix$") { $msix++ }
            if ($name.StartsWith("win-$Architecture/", [StringComparison]::Ordinal) -and -not $name.EndsWith('/')) {
                $files++
            }
        }
        if ($msix -ne 1 -or $files -eq 0) { throw 'CI archive needs application binaries and exactly one unsigned MSIX.' }
    } finally { $archive.Dispose() }
}

function Test-CiPackageFiles($Package, [string]$Directory) {
    $path = Join-Path $Directory $Package.fileName
    if ((Get-Item -LiteralPath $path).Length -ne $Package.sizeBytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Package.sha256) {
        throw "Private CI package checksum or size mismatch: $($Package.fileName)"
    }
    $checksum = Get-Content -LiteralPath (Join-Path $Directory "RedrainCast-$($Package.architecture).sha256") -Raw
    if ($checksum.Trim() -cne "$($Package.sha256)  $($Package.fileName)") {
        throw 'Private CI checksum file does not match the manifest.'
    }
    Assert-CiZip $path $Package.architecture
}

function Receive-CiBuild(
    [string]$RunId, [int]$RunAttempt, [string]$ExpectedSha,
    [string]$DestinationDirectory
) {
    $ErrorActionPreference = 'Stop'
    $tag = Get-CiBuildTag $RunId $RunAttempt
    Assert-CiRepositoryPrivate
    $release = Get-CiRelease $tag $ExpectedSha $false
    Assert-CiReleaseAssets $release $true
    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    $download = Join-Path $DestinationDirectory ".ci-build-$RunId-$RunAttempt"
    if (Test-Path -LiteralPath $download) { throw 'CI download directory already exists; existing work is preserved.' }
    foreach ($arch in @('arm64', 'x64')) {
        if (Test-Path -LiteralPath (Join-Path $DestinationDirectory $arch)) { throw 'CI extraction destination already exists.' }
    }
    New-Item -ItemType Directory -Path $download | Out-Null
    $null = Invoke-CiGh @('release', 'download', $tag, '--repo', $script:CiRepository, '--dir', $download)
    $manifest = Get-Content -LiteralPath (Join-Path $download 'ci-manifest.json') -Raw | ConvertFrom-Json
    Assert-CiManifest $manifest $RunId $RunAttempt $ExpectedSha
    foreach ($package in $manifest.packages) {
        $asset = @($release.assets | Where-Object { $_.name -ceq $package.fileName })
        if ($asset.Count -ne 1 -or $asset[0].size -ne $package.sizeBytes) { throw 'CI release package size differs from its manifest.' }
        Test-CiPackageFiles $package $download
    }
    foreach ($package in $manifest.packages) {
        $destination = Join-Path $DestinationDirectory $package.architecture
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $download $package.fileName), $destination)
    }
    return $manifest
}
