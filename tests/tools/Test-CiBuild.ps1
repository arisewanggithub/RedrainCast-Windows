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
$scratch = Join-Path $root ("artifacts\RedrainCast-CiTest-" + [Guid]::NewGuid().ToString('N'))
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
function Msix([string]$Name, [string]$Architecture, [string]$Version = '0.2.4102', [string[]]$Members = @()) {
    $path = Join-Path $scratch $Name
    $identity = Get-Content -LiteralPath (Join-Path $root 'StoreIdentity.json') -Raw | ConvertFrom-Json
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('AppxManifest.xml')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try {
            $writer.Write("<Package><Identity Name=`"$($identity.PackageName)`" Publisher=`"$($identity.Publisher)`" ProcessorArchitecture=`"$Architecture`" Version=`"$Version.0`"/></Package>")
        } finally { $writer.Dispose() }
        foreach ($member in $Members) { $null = $zip.CreateEntry($member) }
    } finally { $zip.Dispose() }
    return $path
}
$sha = 'a' * 40
$script:ghCalls = @()
$script:private = $true
$script:releaseDraft = $false
$script:tamperDownload = $false
$script:ghFailure = $false
$script:latestTag = 'v0.2.4001'
$script:run = [pscustomobject]@{
    id = 42; run_number = 42; run_attempt = 2; head_sha = $sha; head_branch = 'main'; event = 'push'
    path = '.github/workflows/windows.yml'
}
$envNames = @('GITHUB_REPOSITORY', 'GITHUB_REF', 'GITHUB_EVENT_NAME', 'GITHUB_RUN_ID',
    'GITHUB_RUN_ATTEMPT', 'GITHUB_RUN_NUMBER', 'GITHUB_SHA', 'RUNNER_TEMP')
$previous = @{}
foreach ($name in $envNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    Assert ((Get-CiBuildTag '42' 2) -ceq 'v0.2.4102') 'Official tag must bind run number and attempt.'
    Assert ((Get-CiBuildVersion '1' 1) -ceq '0.2.1') 'First release must supersede installed 0.1.20.'
    Assert ((Get-CiBuildVersion '655' 99) -ceq '0.2.65499') 'Last patch block must remain in MSIX range.'
    Assert ((Get-CiBuildVersion '656' 1) -ceq '0.3.1') 'Patch overflow must carry to the minor component.'
    Assert ((Get-CiBuildVersion '42924770' 99) -ceq '0.65535.65499') 'Last supported version must remain in MSIX range.'
    Reject { Get-CiBuildVersion '42924771' 1 }
    Reject { Get-CiBuildVersion '42' 100 }
    Reject { Get-CiBuildTag '0' 1 }
    Reject { Get-CiBuildTag '42' 0 }
    Reject { Get-CiBuildTag '../42' 1 }
    $packages = @()
    foreach ($arch in @('arm64', 'x64')) {
        $path = Msix "RedrainCast-$arch.msix" $arch
        Assert-CiMsix $path $arch '0.2.4102'
        $hash = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText((Join-Path $scratch "RedrainCast-$arch.sha256"), "$hash  RedrainCast-$arch.msix`n")
        $packages += [pscustomobject]@{ architecture = $arch; fileName = "RedrainCast-$arch.msix"; sha256 = $hash; sizeBytes = (Get-Item $path).Length }
    }
    $manifest = [pscustomobject]@{
        schemaVersion = 2; repository = $script:CiRepository; runId = '42'; runAttempt = 2; runNumber = '42'; version = '0.2.4102'
        headSha = $sha; headBranch = 'main'; event = 'push'; packages = $packages
    }
    $serialized = $manifest | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $scratch 'ci-manifest.json'), $serialized)
    Assert-CiManifest $manifest '42' 2 $sha '42'
    Assert $true 'Valid CI manifest failed.'
    foreach ($field in @('schemaVersion', 'repository', 'runId', 'runNumber', 'runAttempt', 'version', 'headSha', 'headBranch', 'event', 'packages')) {
        $bad = $serialized | ConvertFrom-Json
        $bad.PSObject.Properties.Remove($field)
        Reject { Assert-CiManifest $bad '42' 2 $sha '42' }
    }
    foreach ($change in @(
        @('schemaVersion', '2'), @('runId', 42), @('runAttempt', '2'), @('runNumber', '43'), @('version', '0.2.4101'),
        @('headSha', ('b' * 40)), @('headBranch', 'feature'), @('event', 'pull_request'),
        @('repository', 'arisewanggithub/RedrainCast-Releases')
    )) {
        $bad = $serialized | ConvertFrom-Json
        $bad.($change[0]) = $change[1]
        Reject { Assert-CiManifest $bad '42' 2 $sha '42' }
    }
    foreach ($change in @(@('architecture', 'ARM64'), @('fileName', '../x.zip'), @('sha256', 'bad'), @('sizeBytes', 2GB))) {
        $bad = $serialized | ConvertFrom-Json
        $bad.packages[0].($change[0]) = $change[1]
        Reject { Assert-CiManifest $bad '42' 2 $sha '42' }
    }
    $bad = $serialized | ConvertFrom-Json
    $bad.packages[1] = $bad.packages[0]
    Reject { Assert-CiManifest $bad '42' 2 $sha '42' }
    $index = 0
    foreach ($member in @('../escape', 'win-x64/../escape', 'win-x64/.. /escape', 'win-x64/./app',
        '/win-x64/app', 'win-x64/file:stream', 'win-x64/file.')) {
        $path = Msix "unsafe-$index.msix" 'x64' '0.2.4102' @($member)
        Reject { Assert-CiMsix $path 'x64' '0.2.4102' }
        $index++
    }
    $path = Msix 'duplicate.msix' 'x64' '0.2.4102' @('app', 'APP')
    Reject { Assert-CiMsix $path 'x64' '0.2.4102' }
    $path = Zip 'missing-msix.zip' @('win-x64/app')
    Reject { Assert-CiMsix $path 'x64' '0.2.4102' }
    $path = Zip 'two-msix.zip' @('win-x64/app', 'msix-x64/one.msix', 'msix-x64/two.msix')
    Reject { Assert-CiMsix $path 'x64' '0.2.4102' }
    Reject { Assert-CiMsix (Join-Path $scratch 'RedrainCast-x64.msix') 'arm64' '0.2.4102' }
    Reject { Assert-CiMsix (Join-Path $scratch 'RedrainCast-x64.msix') 'x64' '0.2.4101' }
    $signedFixture = Msix 'signed.msix' 'x64' '0.2.4102' @('AppxSignature.p7x')
    Assert-CiMsix $signedFixture 'x64' '0.2.4102'
    Assert $true 'Signed packages must share the same release/Store artifact contract.'

    $assets = @('RedrainCast-arm64.msix', 'RedrainCast-x64.msix', 'RedrainCast-arm64.sha256', 'RedrainCast-x64.sha256', 'ci-manifest.json') |
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
        if ($Arguments[0] -eq 'api' -and $Arguments[1] -match '/releases/latest$') {
            return (@{ tag_name = $script:latestTag } | ConvertTo-Json)
        }
        if ($Arguments[0] -eq 'api' -and $Arguments[1] -match '/releases/tags/') {
            if ($script:releaseDraft) { throw 'GitHub HTTP 404 fixture: tag endpoint excludes drafts' }
            return (@{ tag_name = 'v0.2.4102'; target_commitish = $sha; draft = $script:releaseDraft;
                prerelease = $false; assets = $assets } | ConvertTo-Json -Depth 8)
        }
        if ($Arguments[0] -eq 'release' -and $Arguments[1] -eq 'view') {
            return (@{ tagName = 'v0.2.4102'; targetCommitish = $sha; isDraft = $script:releaseDraft;
                isPrerelease = $false; assets = $assets; url = 'https://github.com/fixture' } | ConvertTo-Json -Depth 8)
        }
        if ($Arguments[0] -eq 'release' -and $Arguments[1] -eq 'download') {
            $directory = $Arguments[[Array]::IndexOf($Arguments, '--dir') + 1]
            foreach ($asset in $assets) { Copy-Item -LiteralPath (Join-Path $scratch $asset.name) -Destination $directory }
            if ($script:tamperDownload) { Add-Content -LiteralPath (Join-Path $directory 'RedrainCast-x64.msix') -Value 'tamper' }
            return ''
        }
        throw 'Unexpected GitHub fixture call.'
    }
    $env:GITHUB_REPOSITORY = $script:CiRepository
    $env:GITHUB_REF = 'refs/heads/main'
    $env:GITHUB_EVENT_NAME = 'push'
    Assert (Test-CiPromoteLatest '0.2.4102') 'New releases should become latest.'
    $script:latestTag = 'v0.2.4201'
    Assert (-not (Test-CiPromoteLatest '0.2.4102')) 'Historic reruns must not downgrade latest.'
    $script:latestTag = 'non-versioned'
    Reject { Test-CiPromoteLatest '0.2.4102' }
    $script:latestTag = 'v0.2.4001'
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
    $received = Receive-CiBuild '42' 2 $sha (Join-Path $scratch 'received') '42'
    Assert ($received.headSha -ceq $sha) 'Receiver lost source binding.'
    Assert (-not (Test-Path (Join-Path $scratch 'received\.ci-build-42-2'))) 'Receiver must remove its binary download staging area.'
    foreach ($arch in @('arm64', 'x64')) {
        $receivedPath = Join-Path $scratch "received\$arch\RedrainCast-$arch.msix"
        Assert (Test-Path $receivedPath) 'Direct MSIX download layout changed.'
        Assert ((Get-FileHash $receivedPath).Hash -ceq (Get-FileHash (Join-Path $scratch "RedrainCast-$arch.msix")).Hash) 'Store must consume byte-identical release MSIX.'
    }
    Assert (-not @($script:ghCalls | Where-Object { $_ -join ' ' -match '/actions/artifacts/' }).Count) 'CI downloads must not use Actions artifacts.'
    $script:releaseDraft = $true
    $draft = Get-CiRelease 'v0.2.4102' $sha $true
    Assert $draft.draft 'Draft releases must use an authenticated draft-aware lookup.'
    Assert (($script:ghCalls[-1] -join ' ') -match '^release view ') 'Draft lookup used the published-only tag API.'
    Reject { Receive-CiBuild '42' 2 $sha (Join-Path $scratch 'draft') '42' }
    $script:releaseDraft = $false
    $script:tamperDownload = $true
    Reject { Receive-CiBuild '42' 2 $sha (Join-Path $scratch 'tampered') '42' }
    Assert (-not (Test-Path (Join-Path $scratch 'tampered\arm64'))) 'Both MSIX files must validate before copying either architecture.'
    Assert (-not (Test-Path (Join-Path $scratch 'tampered\.ci-build-42-2'))) 'Failed download staging must be removed.'
    $script:tamperDownload = $false
    $script:ghFailure = $true
    Reject { Receive-CiBuild '42' 2 $sha (Join-Path $scratch 'forbidden') '42' }

    $remote = Join-Path $scratch 'remote'
    $binaryRoot = Join-Path $scratch 'binaries'
    New-Item -ItemType Directory -Path $remote | Out-Null
    foreach ($arch in @('arm64', 'x64')) {
        New-Item -ItemType Directory -Path (Join-Path $binaryRoot "win-$arch") -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $binaryRoot "msix-$arch") -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $binaryRoot "win-$arch\RedrainCast.App.exe") -Value 'binary fixture'
        Copy-Item -LiteralPath (Join-Path $scratch "RedrainCast-$arch.msix") -Destination (Join-Path $binaryRoot "msix-$arch\fixture.msix")
    }
    $env:GITHUB_RUN_ID = '42'
    $env:GITHUB_RUN_ATTEMPT = '2'
    $env:GITHUB_RUN_NUMBER = '42'
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
            return (@{ id = 42; run_number = 42; run_attempt = 2; head_sha = $f.sha; head_branch = 'main'; event = 'push';
                path = '.github/workflows/windows.yml' } | ConvertTo-Json)
        }
        if ($a[0] -eq 'api' -and $a[1] -match '/releases/latest$') {
            return (@{ tag_name = 'v0.2.4001' } | ConvertTo-Json)
        }
        if ($a[0] -eq 'api' -and $a[1] -match '/releases/tags/v0.2.4102$') {
            if (-not $f.created) { throw 'Release was not created first.' }
            if ($f.draft) { throw 'GitHub HTTP 404 fixture: tag endpoint excludes drafts' }
            $uploaded = @(Get-ChildItem -LiteralPath $f.remote -File | ForEach-Object { @{ name = $_.Name; size = $_.Length } })
            return (@{ tag_name = 'v0.2.4102'; target_commitish = $f.sha; draft = $f.draft;
                prerelease = $false; assets = $uploaded } | ConvertTo-Json -Depth 8)
        }
        if ($a[0] -eq 'release' -and $a[1] -eq 'view') {
            if (-not $f.created) { throw 'Release was not created first.' }
            $uploaded = @(Get-ChildItem -LiteralPath $f.remote -File | ForEach-Object { @{ name = $_.Name; size = $_.Length } })
            return (@{ tagName = 'v0.2.4102'; targetCommitish = $f.sha; isDraft = $f.draft;
                isPrerelease = $false; assets = $uploaded; url = 'https://github.com/fixture' } | ConvertTo-Json -Depth 8)
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
        & $publisher -Stage Prepare -ArtifactsDirectory $binaryRoot
        & $publisher -Stage Upload -Architecture arm64 -ArtifactsDirectory $binaryRoot
        & $publisher -Stage Upload -Architecture x64 -ArtifactsDirectory $binaryRoot
        Assert ((Get-ChildItem -LiteralPath $remote -File).Count -eq 4) 'Partial paired packages must remain a draft.'
        Assert $global:ciPublisherFixture.draft 'Partial package upload published prematurely.'
        & $publisher -Stage Complete -ArtifactsDirectory $binaryRoot
        Assert (-not $global:ciPublisherFixture.draft) 'Verified complete build was not published.'
        Assert ((Get-ChildItem -LiteralPath $remote -File).Count -eq 5) 'CI publisher must upload exactly five private assets.'
        $actual = Get-Content -LiteralPath (Join-Path $remote 'ci-manifest.json') -Raw | ConvertFrom-Json
        Assert-CiManifest $actual '42' 2 $sha '42'
        foreach ($package in $actual.packages) { Test-CiPackageFiles $package $remote $actual.version }
        Assert (-not @($global:ciPublisherFixture.calls | Where-Object { $_ -contains '--prerelease' }).Count) 'Official releases must not be prereleases.'
        Assert (@($global:ciPublisherFixture.calls | Where-Object { $_ -contains '--prerelease=false' }).Count -eq 1) 'Completion must explicitly publish a non-prerelease.'
        Assert $true 'Real publisher archive or manifest validation failed.'
        Assert (-not @($global:ciPublisherFixture.calls | Where-Object { $_ -join ' ' -match '/actions/artifacts/' }).Count) 'Publisher must not create Actions artifacts.'
    } finally {
        Remove-Item -LiteralPath Function:\global:gh
        Remove-Variable ciPublisherFixture -Scope Global
    }
    $workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\windows.yml') -Raw
    foreach ($job in @('guards', 'test', 'prepare-release', 'package', 'release-package', 'complete-release')) {
        $body = [regex]::Match($workflow, ('(?ms)^  ' + [regex]::Escape($job) +
            ':\r?\n(?<body>.*?)(?=^  [a-z][a-z-]*:\r?\n|\z)')).Groups['body'].Value
        Assert ($body -match '(?m)^    runs-on: \[self-hosted, Windows, X64, arise-local\]\s*$') "$job must use the dedicated self-hosted Windows runners."
    }
    foreach ($job in @('guards', 'test', 'package')) {
        $body = [regex]::Match($workflow, ('(?ms)^  ' + [regex]::Escape($job) +
            ':\r?\n(?<body>.*?)(?=^  [a-z][a-z-]*:\r?\n|\z)')).Groups['body'].Value
        Assert ($body -match 'github\.event\.pull_request\.head\.repo\.full_name == github\.repository') "$job must not execute untrusted fork code on a self-hosted runner."
    }
    Assert ($workflow -notmatch 'name: RedrainCast-\$\{\{') 'Windows binaries must not be Actions artifacts.'
    Assert ($workflow -match 'if: failure\(\)' -and $workflow -match 'retention-days: 1') 'Diagnostics need failure-only one-day retention.'
    Assert ($workflow -match "if: github.event_name == 'pull_request'") 'PR packages must remain read-only and separate from release writes.'
    Assert ($workflow -match 'Publish-CiBuild.ps1 -Stage Complete') 'Private releases require complete paired verification.'
    $testJob = [regex]::Match($workflow, '(?ms)^  test:\r?\n(?<body>.*?)(?=^  [a-z][a-z-]*:\r?\n|\z)').Groups['body'].Value
    Assert ($testJob -match 'project: \[RedrainCast.Tests, RedrainCast.Receive.Tests, RedrainCast.Send.Tests\]') 'Every test project must run on its own matrix runner.'
    Assert ($testJob -match 'dotnet test \.\\tests\\\$\{\{ matrix.project \}\}\\\$\{\{ matrix.project \}\}\.csproj') 'Test matrix must execute each complete project.'
    Assert ($testJob -match 'xUnit.ParallelizeTestCollections=false') 'Timing-sensitive collections must remain isolated within each runner.'
    Assert ($testJob -notmatch 'RunConfiguration.MaxCpuCount=1| -m:1') 'Independent single-project runners do not need global build/test CPU limits.'
    Assert ($testJob -match 'name: test-results-\$\{\{ matrix.project \}\}') 'Parallel test jobs require unique diagnostic artifact names.'
    $packageJob = [regex]::Match($workflow, '(?ms)^  package:\r?\n(?<body>.*?)(?=^  [a-z][a-z-]*:\r?\n|\z)').Groups['body'].Value
    Assert ($packageJob -and $packageJob -notmatch '(?m)^    needs:') 'Read-only PR packaging must run alongside tests.'
    Assert ($packageJob -match '\.\\tools\\Publish.ps1 -Architecture \$\{\{ matrix.architecture \}\} -Version 0\.2\.1(?:\r?\n|$)') 'PR packaging must validate a non-default version so embedded manifest drift cannot be masked by the source baseline.'
    foreach ($job in @('package', 'release-package')) {
        $body = [regex]::Match($workflow, ('(?ms)^  ' + $job + ':\r?\n(?<body>.*?)(?=^  [a-z][a-z-]*:\r?\n|\z)')).Groups['body'].Value
        Assert ($body -match "if: matrix.architecture == 'x64'") "$job must restrict launch smoke to x64."
        Assert ($body -match 'Test-PackageLaunch.ps1 -StagingDirectory \.\\artifacts\\msix-x64\\staging -ReportPath \.\\artifacts\\msix-x64\\launch-validation.json') "$job must smoke-test the extracted package payload."
        Assert ($body.IndexOf('Package.ps1 ') -lt $body.IndexOf('Test-PackageLaunch.ps1 ')) "$job must package before launching the payload."
    }
    $releasePackageJob = [regex]::Match($workflow, '(?ms)^  release-package:\r?\n(?<body>.*?)(?=^  [a-z][a-z-]*:\r?\n|\z)').Groups['body'].Value
    Assert ($releasePackageJob -match '\.\\tools\\Publish.ps1 -Architecture \$\{\{ matrix.architecture \}\} -Version \$version(?:\r?\n|$)') 'Publishing must receive the three-component release version.'
    Assert ($releasePackageJob -match '\.\\tools\\Package.ps1 -Architecture \$\{\{ matrix.architecture \}\} -Version "\$version\.0" -PublishedDirectory') 'MSIX packaging must receive the exact release version with a zero revision.'
    Assert ($packageJob -notmatch 'MSIX_SIGNING_') 'PR packaging must never receive signing credentials.'
    $signingStep = [regex]::Match($releasePackageJob, '(?ms)      - name: Package MSIX with optional publisher signing\r?\n(?<body>.*?)(?=      - name:|\z)').Groups['body'].Value
    Assert ($signingStep -match 'MSIX_SIGNING_PFX_BASE64: \$\{\{ secrets.MSIX_SIGNING_PFX_BASE64 \}\}' -and
        $signingStep -match 'MSIX_SIGNING_PASSWORD: \$\{\{ secrets.MSIX_SIGNING_PASSWORD \}\}') 'Only trusted release packaging should receive both signing secrets.'
    $prepareJob = [regex]::Match($workflow, '(?ms)^  prepare-release:\r?\n(?<body>.*?)(?=^  [a-z][a-z-]*:\r?\n|\z)').Groups['body'].Value
    Assert ($prepareJob -notmatch '(?m)^    needs:') 'Draft preparation must start alongside guards and tests.'
    Assert ($releasePackageJob -match '(?m)^    needs: prepare-release\s*$') 'Packaging must wait only for its private draft, not tests.'
    $completeJob = [regex]::Match($workflow, '(?ms)^  complete-release:\r?\n(?<body>.*?)(?=^  [a-z][a-z-]*:\r?\n|\z)').Groups['body'].Value
    Assert ($completeJob -match 'needs: \[guards, test, release-package\]') 'Publishing must wait for successful guards, every test project and both package builds.'
    Assert ($completeJob -notmatch 'always\(\)|!cancelled\(\)|continue-on-error: true') 'Final release publication must not bypass failed prerequisites.'
    Assert ($workflow -notmatch 'dotnet test[^\r\n]*--filter') 'CI scheduling must not exclude application tests.'
    foreach ($job in @('prepare-release', 'release-package', 'complete-release')) {
        $block = [regex]::Match($workflow, ('(?ms)^  ' + [regex]::Escape($job) +
            ':\r?\n(?<body>.*?)(?=^  [a-z][a-z-]*:\r?\n|\z)'))
        Assert $block.Success "Missing private CI job $job."
        Assert ($block.Groups['body'].Value -match '(?m)^      actions: read\s*$') "$job must read the source workflow run for provenance."
        Assert ($block.Groups['body'].Value -notmatch '(?m)^      actions: write\s*$') "$job must not gain artifact deletion permissions."
    }
    Write-Output "$count CI build tests passed."
} finally {
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
