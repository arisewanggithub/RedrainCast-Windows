$script:CiRepository = 'arisewanggithub/RedrainCast-Windows'

function Get-CiBuildVersion([string]$RunNumber, [int]$RunAttempt) {
    if ($RunNumber -notmatch '^[1-9][0-9]{0,7}$' -or [long]$RunNumber -gt 42924770 -or
        $RunAttempt -lt 1 -or $RunAttempt -gt 99) {
        throw 'Release versions require run numbers 1..42924770 and attempts 1..99; exhaustion requires a reviewed version epoch change.'
    }
    $index = [long]$RunNumber - 1
    return "0.$(2 + [long][Math]::Floor($index / 655)).$(100 * ($index % 655) + $RunAttempt)"
}

function Get-CiBuildTag([string]$RunNumber, [int]$RunAttempt) {
    return 'v' + (Get-CiBuildVersion $RunNumber $RunAttempt)
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
    if ($RunId -notmatch '^[1-9][0-9]{0,18}$' -or $RunAttempt -lt 1 -or
        $ExpectedSha -cnotmatch '^[0-9a-f]{40}$' -or $env:GITHUB_REPOSITORY -cne $script:CiRepository -or
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
    $null = Get-CiBuildVersion ([string]$run.run_number) $RunAttempt
    return $run
}

function Assert-CiManifest($Manifest, [string]$RunId, [int]$RunAttempt, [string]$ExpectedSha, [string]$RunNumber) {
    $version = Get-CiBuildVersion $RunNumber $RunAttempt
    foreach ($name in @('schemaVersion', 'repository', 'runId', 'runNumber', 'runAttempt', 'version', 'headSha', 'headBranch', 'event', 'packages')) {
        if (-not $Manifest.PSObject.Properties[$name]) { throw "CI manifest is missing $name." }
    }
    if (($Manifest.schemaVersion -isnot [int] -and $Manifest.schemaVersion -isnot [long]) -or
        $Manifest.schemaVersion -ne 2 -or $Manifest.repository -cne $script:CiRepository -or
        $Manifest.runId -isnot [string] -or $Manifest.runId -cne $RunId -or
        $Manifest.runNumber -isnot [string] -or $Manifest.runNumber -cne $RunNumber -or
        $Manifest.version -cne $version -or
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
            $package.fileName -cne "RedrainCast-$($package.architecture).msix" -or
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
            throw "Official release $Tag is unavailable; re-run all jobs or start a new reviewed main build instead of using expired Actions artifacts."
        }
        throw
    }
    if ($release.tag_name -cne $Tag -or $release.target_commitish -cne $ExpectedSha -or
        $release.draft -isnot [bool] -or $release.draft -ne $Draft -or
        $release.prerelease -isnot [bool] -or $release.prerelease) {
        throw 'Official release identity, source, draft state or non-prerelease status differs from the requested build.'
    }
    return $release
}

function Assert-CiReleaseAssets($Release, [bool]$WithManifest) {
    $expected = @('RedrainCast-arm64.msix', 'RedrainCast-x64.msix', 'RedrainCast-arm64.sha256', 'RedrainCast-x64.sha256')
    if ($WithManifest) { $expected += 'ci-manifest.json' }
    $names = @($Release.assets | ForEach-Object { $_.name } | Sort-Object)
    if (Compare-Object ($expected | Sort-Object) $names -CaseSensitive) { throw 'Official release has an incomplete or unexpected asset set.' }
}

function Assert-CiMsix([string]$Path, [string]$Architecture, [string]$Version) {
    if ($Architecture -notin @('arm64', 'x64')) { throw 'Invalid MSIX architecture.' }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name -match '(^|/)\.\.?(/|$)|(^|/)[^/]*[ .](/|$)|^/|:' -or
                -not $seen.Add($name) -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                throw "Unsafe or duplicate MSIX member: $name"
            }
        }
        $entry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $entry -or $entry.Length -gt 1MB) { throw 'Expected one bounded MSIX manifest.' }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $entry.Open()
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try {
            $xml = [Xml.XmlDocument]::new()
            $xml.XmlResolver = $null
            $xml.Load($reader)
        } finally { $reader.Dispose(); $stream.Dispose() }
        $identity = $xml.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
        $store = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'StoreIdentity.json') -Raw | ConvertFrom-Json
        if ($null -eq $identity -or $identity.Name -cne $store.PackageName -or
            $identity.Publisher -cne $store.Publisher -or $identity.ProcessorArchitecture -cne $Architecture -or
            $identity.Version -cne "$Version.0") { throw 'MSIX identity/version does not match the official release.' }
    } finally { $archive.Dispose() }
}

function Test-CiPackageFiles($Package, [string]$Directory, [string]$Version) {
    $path = Join-Path $Directory $Package.fileName
    if ((Get-Item -LiteralPath $path).Length -ne $Package.sizeBytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Package.sha256) {
        throw "Release MSIX checksum or size mismatch: $($Package.fileName)"
    }
    $checksum = Get-Content -LiteralPath (Join-Path $Directory "RedrainCast-$($Package.architecture).sha256") -Raw
    if ($checksum.Trim() -cne "$($Package.sha256)  $($Package.fileName)") {
        throw 'Release checksum file does not match the manifest.'
    }
    Assert-CiMsix $path $Package.architecture $Version
}

function Test-CiPromoteLatest([string]$Version) {
    try {
        $latest = Invoke-CiGh @('api', "repos/$script:CiRepository/releases/latest") | ConvertFrom-Json
    } catch {
        if ($_.Exception.Message -match 'HTTP 404') { return $true }
        throw
    }
    if ($latest.tag_name -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'Cannot compare the current latest release; review its version before promotion.'
    }
    return ([version]$Version -ge [version]$latest.tag_name.Substring(1))
}

function Receive-CiBuild(
    [string]$RunId, [int]$RunAttempt, [string]$ExpectedSha,
    [string]$DestinationDirectory, [string]$RunNumber
) {
    $ErrorActionPreference = 'Stop'
    if ($RunId -notmatch '^[1-9][0-9]{0,18}$') { throw 'Invalid trusted run ID.' }
    $tag = Get-CiBuildTag $RunNumber $RunAttempt
    Assert-CiRepositoryPrivate
    $release = Get-CiRelease $tag $ExpectedSha $false
    Assert-CiReleaseAssets $release $true
    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    $download = Join-Path $DestinationDirectory ".ci-build-$RunId-$RunAttempt"
    if (Test-Path -LiteralPath $download) { throw 'CI download directory already exists; existing work is preserved.' }
    foreach ($arch in @('arm64', 'x64')) {
        if (Test-Path -LiteralPath (Join-Path $DestinationDirectory $arch)) { throw 'Release package destination already exists.' }
    }
    New-Item -ItemType Directory -Path $download | Out-Null
    try {
        $null = Invoke-CiGh @('release', 'download', $tag, '--repo', $script:CiRepository, '--dir', $download)
        $manifest = Get-Content -LiteralPath (Join-Path $download 'ci-manifest.json') -Raw | ConvertFrom-Json
        Assert-CiManifest $manifest $RunId $RunAttempt $ExpectedSha $RunNumber
        foreach ($package in $manifest.packages) {
            $asset = @($release.assets | Where-Object { $_.name -ceq $package.fileName })
            if ($asset.Count -ne 1 -or $asset[0].size -ne $package.sizeBytes) { throw 'CI release package size differs from its manifest.' }
            Test-CiPackageFiles $package $download $manifest.version
        }
        foreach ($package in $manifest.packages) {
            $destination = Join-Path $DestinationDirectory $package.architecture
            New-Item -ItemType Directory -Path $destination | Out-Null
            Copy-Item -LiteralPath (Join-Path $download $package.fileName) -Destination $destination
        }
        return $manifest
    } finally {
        Remove-Item -LiteralPath $download -Recurse -Force
    }
}
