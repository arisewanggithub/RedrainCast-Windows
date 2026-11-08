$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'tools\CiBuild.ps1')
$count = 0
function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:count++
}
function Reject([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Assert $failed 'Expected invalid CI build data to fail.'
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("RedrainCast-CiTest-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function Zip([string]$Name, [string[]]$Members) {
    $path = Join-Path $scratch $Name
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($member in $Members) {
            $entry = $zip.CreateEntry($member)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('fixture') } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
    return $path
}
$sha = 'a' * 40
$script:ghCalls = @()
$script:private = $true
$script:releaseDraft = $false
$script:tamperDownload = $false
$script:ghFailure = $false
$script:run = [pscustomobject]@{
    id = 42; run_attempt = 2; head_sha = $sha; head_branch = 'main'; event = 'push'
    path = '.github/workflows/windows.yml'
}
$envNames = @('GITHUB_REPOSITORY', 'GITHUB_REF', 'GITHUB_EVENT_NAME', 'GITHUB_RUN_ID',
    'GITHUB_RUN_ATTEMPT', 'GITHUB_SHA', 'RUNNER_TEMP')
$previous = @{}
foreach ($name in $envNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    Assert ((Get-CiBuildTag '42' 2) -ceq 'ci-build-42-2') 'CI tag must bind run ID and attempt.'
    Reject { Get-CiBuildTag '0' 1 }
    Reject { Get-CiBuildTag '42' 0 }
    Reject { Get-CiBuildTag '../42' 1 }
    $packages = @()
    foreach ($arch in @('arm64', 'x64')) {
        $path = Zip "RedrainCast-$arch.zip" @("win-$arch/RedrainCast.App.exe", "msix-$arch/app.msix")
        Assert-CiZip $path $arch
        $hash = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText((Join-Path $scratch "RedrainCast-$arch.sha256"), "$hash  RedrainCast-$arch.zip`n")
        $packages += [pscustomobject]@{ architecture = $arch; fileName = "RedrainCast-$arch.zip"; sha256 = $hash; sizeBytes = (Get-Item $path).Length }
    }
    $manifest = [pscustomobject]@{
        schemaVersion = 1; repository = $script:CiRepository; runId = '42'; runAttempt = 2
        headSha = $sha; headBranch = 'main'; event = 'push'; packages = $packages
    }
    $serialized = $manifest | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $scratch 'ci-manifest.json'), $serialized)
    Assert-CiManifest $manifest '42' 2 $sha
    Assert $true 'Valid CI manifest failed.'
    foreach ($field in @('schemaVersion', 'repository', 'runId', 'runAttempt', 'headSha', 'headBranch', 'event', 'packages')) {
        $bad = $serialized | ConvertFrom-Json
        $bad.PSObject.Properties.Remove($field)
        Reject { Assert-CiManifest $bad '42' 2 $sha }
    }
    foreach ($change in @(
        @('schemaVersion', '1'), @('runId', 42), @('runAttempt', '2'),
        @('headSha', ('b' * 40)), @('headBranch', 'feature'), @('event', 'pull_request'),
        @('repository', 'arisewanggithub/RedrainCast-Releases')
    )) {
        $bad = $serialized | ConvertFrom-Json
        $bad.($change[0]) = $change[1]
        Reject { Assert-CiManifest $bad '42' 2 $sha }
    }
    foreach ($change in @(@('architecture', 'ARM64'), @('fileName', '../x.zip'), @('sha256', 'bad'), @('sizeBytes', 2GB))) {
        $bad = $serialized | ConvertFrom-Json
        $bad.packages[0].($change[0]) = $change[1]
        Reject { Assert-CiManifest $bad '42' 2 $sha }
    }
    $bad = $serialized | ConvertFrom-Json
    $bad.packages[1] = $bad.packages[0]
    Reject { Assert-CiManifest $bad '42' 2 $sha }
    $index = 0
    foreach ($member in @('../escape', 'win-x64/../escape', 'win-x64/.. /escape', 'win-x64/./app',
        '/win-x64/app', 'win-x64/file:stream', 'other/app', 'win-arm64/app', 'win-x64/file.')) {
        $path = Zip "unsafe-$index.zip" @('win-x64/app', 'msix-x64/app.msix', $member)
        Reject { Assert-CiZip $path 'x64' }
        $index++
    }
    $path = Zip 'duplicate.zip' @('win-x64/app', 'win-x64/APP', 'msix-x64/app.msix')
    Reject { Assert-CiZip $path 'x64' }
    $path = Zip 'missing-msix.zip' @('win-x64/app')
    Reject { Assert-CiZip $path 'x64' }
    $path = Zip 'two-msix.zip' @('win-x64/app', 'msix-x64/one.msix', 'msix-x64/two.msix')
    Reject { Assert-CiZip $path 'x64' }

    $assets = @('RedrainCast-arm64.zip', 'RedrainCast-x64.zip', 'RedrainCast-arm64.sha256', 'RedrainCast-x64.sha256', 'ci-manifest.json') |
        ForEach-Object { [pscustomobject]@{ name = $_; size = (Get-Item -LiteralPath (Join-Path $scratch $_)).Length } }
    function Invoke-CiGh([string[]]$Arguments) {
        $script:ghCalls += ,$Arguments
        if ($script:ghFailure) { throw 'GitHub HTTP 403 fixture' }
        if ($Arguments[0] -eq 'api' -and $Arguments[1] -eq "repos/$script:CiRepository") {
            return (@{ full_name = $script:CiRepository; private = $script:private } | ConvertTo-Json)
        }
        if ($Arguments[0] -eq 'api' -and $Arguments[1] -match '/actions/runs/') {
            return ($script:run | ConvertTo-Json)
        }
        if ($Arguments[0] -eq 'api' -and $Arguments[1] -match '/releases/tags/') {
            return (@{ tag_name = 'ci-build-42-2'; target_commitish = $sha; draft = $script:releaseDraft;
                prerelease = $true; assets = $assets } | ConvertTo-Json -Depth 8)
        }
        if ($Arguments[0] -eq 'release' -and $Arguments[1] -eq 'download') {
            $directory = $Arguments[[Array]::IndexOf($Arguments, '--dir') + 1]
            foreach ($asset in $assets) { Copy-Item -LiteralPath (Join-Path $scratch $asset.name) -Destination $directory }
            if ($script:tamperDownload) { Add-Content -LiteralPath (Join-Path $directory 'RedrainCast-x64.zip') -Value 'tamper' }
            return ''
        }
        throw 'Unexpected GitHub fixture call.'
    }
    $env:GITHUB_REPOSITORY = $script:CiRepository
    $env:GITHUB_REF = 'refs/heads/main'
    $env:GITHUB_EVENT_NAME = 'push'
    $null = Assert-CiBuildContext '42' 2 $sha
    Assert $true 'Trusted CI context failed.'
    $env:GITHUB_EVENT_NAME = 'pull_request'
    Reject { Assert-CiBuildContext '42' 2 $sha }
    $env:GITHUB_EVENT_NAME = 'push'
    $script:private = $false
    Reject { Assert-CiRepositoryPrivate }
    $script:private = $true
    $script:run.head_sha = 'b' * 40
    Reject { Assert-CiBuildContext '42' 2 $sha }
    $script:run.head_sha = $sha
    $received = Receive-CiBuild '42' 2 $sha (Join-Path $scratch 'received')
    Assert ($received.headSha -ceq $sha) 'Receiver lost source binding.'
    foreach ($arch in @('arm64', 'x64')) {
        Assert (Test-Path (Join-Path $scratch "received\$arch\win-$arch\RedrainCast.App.exe")) 'Application ZIP layout changed.'
        Assert (Test-Path (Join-Path $scratch "received\$arch\msix-$arch\app.msix")) 'MSIX ZIP layout changed.'
    }
    Assert (-not @($script:ghCalls | Where-Object { $_ -join ' ' -match '/actions/artifacts/' }).Count) 'CI downloads must not use Actions artifacts.'
    $script:releaseDraft = $true
    Reject { Receive-CiBuild '42' 2 $sha (Join-Path $scratch 'draft') }
    $script:releaseDraft = $false
    $script:tamperDownload = $true
    Reject { Receive-CiBuild '42' 2 $sha (Join-Path $scratch 'tampered') }
    Assert (-not (Test-Path (Join-Path $scratch 'tampered\arm64'))) 'Both ZIPs must validate before extracting either architecture.'
    $script:tamperDownload = $false
    $script:ghFailure = $true
    Reject { Receive-CiBuild '42' 2 $sha (Join-Path $scratch 'forbidden') }

    $remote = Join-Path $scratch 'remote'
    $binaryRoot = Join-Path $scratch 'binaries'
    New-Item -ItemType Directory -Path $remote | Out-Null
    foreach ($arch in @('arm64', 'x64')) {
        New-Item -ItemType Directory -Path (Join-Path $binaryRoot "win-$arch") -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $binaryRoot "msix-$arch") -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $binaryRoot "win-$arch\RedrainCast.App.exe") -Value 'binary fixture'
        Set-Content -LiteralPath (Join-Path $binaryRoot "msix-$arch\fixture.msix") -Value 'MSIX fixture'
    }
    $env:GITHUB_RUN_ID = '42'
    $env:GITHUB_RUN_ATTEMPT = '2'
    $env:GITHUB_SHA = $sha
    $env:RUNNER_TEMP = Join-Path $scratch 'runner'
    New-Item -ItemType Directory -Path $env:RUNNER_TEMP | Out-Null
    $global:ciPublisherFixture = @{
        remote = $remote; sha = $sha; created = $false; draft = $true; calls = @()
    }
    function global:gh {
        $a = $args
        $f = $global:ciPublisherFixture
        $f.calls += ,$a
        $global:LASTEXITCODE = 0
        if ($a[0] -eq 'api' -and $a[1] -eq 'repos/arisewanggithub/RedrainCast-Windows') {
            return (@{ full_name = 'arisewanggithub/RedrainCast-Windows'; private = $true } | ConvertTo-Json)
        }
        if ($a[0] -eq 'api' -and $a[1] -match '/actions/runs/42$') {
            return (@{ id = 42; run_attempt = 2; head_sha = $f.sha; head_branch = 'main'; event = 'push';
                path = '.github/workflows/windows.yml' } | ConvertTo-Json)
        }
        if ($a[0] -eq 'api' -and $a[1] -match '/releases/tags/ci-build-42-2$') {
            if (-not $f.created) { throw 'Release was not created first.' }
            $uploaded = @(Get-ChildItem -LiteralPath $f.remote -File | ForEach-Object { @{ name = $_.Name; size = $_.Length } })
            return (@{ tag_name = 'ci-build-42-2'; target_commitish = $f.sha; draft = $f.draft;
                prerelease = $true; assets = $uploaded } | ConvertTo-Json -Depth 8)
        }
        if ($a[0] -eq 'release' -and $a[1] -eq 'create') { $f.created = $true; return 'created' }
        if ($a[0] -eq 'release' -and $a[1] -eq 'upload') {
            foreach ($file in $a[5..($a.Count - 1)]) {
                Copy-Item -LiteralPath $file -Destination $f.remote
            }
            return 'uploaded'
        }
        if ($a[0] -eq 'release' -and $a[1] -eq 'download') {
            $to = $a[[Array]::IndexOf($a, '--dir') + 1]
            Get-ChildItem -LiteralPath $f.remote -File | Copy-Item -Destination $to
            return 'downloaded'
        }
        if ($a[0] -eq 'release' -and $a[1] -eq 'edit') { $f.draft = $false; return 'published' }
        throw "Unexpected publisher fixture call: $a"
    }
    try {
        $publisher = Join-Path $root 'tools\Publish-CiBuild.ps1'
        & $publisher -Stage Prepare
        & $publisher -Stage Upload -Architecture arm64 -ArtifactsDirectory $binaryRoot
        & $publisher -Stage Upload -Architecture x64 -ArtifactsDirectory $binaryRoot
        Assert ((Get-ChildItem -LiteralPath $remote -File).Count -eq 4) 'Partial paired packages must remain a draft.'
        Assert $global:ciPublisherFixture.draft 'Partial package upload published prematurely.'
        & $publisher -Stage Complete
        Assert (-not $global:ciPublisherFixture.draft) 'Verified complete build was not published.'
        Assert ((Get-ChildItem -LiteralPath $remote -File).Count -eq 5) 'CI publisher must upload exactly five private assets.'
        $actual = Get-Content -LiteralPath (Join-Path $remote 'ci-manifest.json') -Raw | ConvertFrom-Json
        Assert-CiManifest $actual '42' 2 $sha
        foreach ($package in $actual.packages) { Test-CiPackageFiles $package $remote }
        Assert $true 'Real publisher archive or manifest validation failed.'
        Assert (-not @($global:ciPublisherFixture.calls | Where-Object { $_ -join ' ' -match '/actions/artifacts/' }).Count) 'Publisher must not create Actions artifacts.'
    } finally {
        Remove-Item -LiteralPath Function:\global:gh
        Remove-Variable ciPublisherFixture -Scope Global
    }
    $workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\windows.yml') -Raw
    Assert ($workflow -notmatch 'name: RedrainCast-\$\{\{') 'Windows binaries must not be Actions artifacts.'
    Assert ($workflow -match 'if: failure\(\)' -and $workflow -match 'retention-days: 1') 'Diagnostics need failure-only one-day retention.'
    Assert ($workflow -match "if: github.event_name == 'pull_request'") 'PR packages must remain read-only and separate from release writes.'
    Assert ($workflow -match 'Publish-CiBuild.ps1 -Stage Complete') 'Private releases require complete paired verification.'
    Write-Output "$count CI build tests passed."
} finally {
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
