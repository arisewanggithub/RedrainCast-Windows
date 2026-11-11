$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $root 'tools\StoreCli.psm1') -Force
$identity = Get-Content -LiteralPath (Join-Path $root 'StoreIdentity.json') -Raw | ConvertFrom-Json
$script:passed = 0
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Copy-Json($Value) { return ConvertTo-Json -InputObject $Value -Depth 100 | ConvertFrom-Json }
function Test([string]$Name, [scriptblock]$Body) {
    & $Body
    $script:passed++
    Write-Host "PASS $Name"
}
function Reject([scriptblock]$Body, [string]$Pattern = '.') {
    $message = $null
    try { & $Body | Out-Null } catch { $message = $_.Exception.Message }
    Assert ($message -notmatch 'fixture-SAS|fixture-token|fixture-secret') 'Sensitive fixture value escaped into error output.'
    Assert ($null -ne $message -and $message -match $Pattern) "Expected guard did not reject: $message"
}
function New-Run {
    return [pscustomobject]@{
        id = 123; repository = @{ full_name = 'arisewanggithub/RedrainCast-Windows' }
        head_repository = @{ full_name = 'arisewanggithub/RedrainCast-Windows' }
        head_branch = 'main'; head_sha = ('a' * 40); run_number = 42; run_attempt = 2
        status = 'completed'; conclusion = 'success'; event = 'push'; path = '.github/workflows/windows.yml'
    }
}
function New-Packages {
    return [pscustomobject]@{
        directory = 'offline-input'
        packages = @(
            [pscustomobject]@{ fileName = 'RedrainCast-123-arm64.msix'; sha256 = ('a' * 64); architecture = 'arm64'; version = '1.2.3.0'; manifestFingerprint = ('c' * 64) }
            [pscustomobject]@{ fileName = 'RedrainCast-123-x64.msix'; sha256 = ('b' * 64); architecture = 'x64'; version = '1.2.3.0'; manifestFingerprint = ('c' * 64) }
        )
    }
}
function New-Mock {
    $copy = ${function:Copy-Json}
    $assert = ${function:Assert}
    $packages = (New-Packages).packages
    $state = [pscustomobject]@{
        app = [pscustomobject]@{ id = $identity.StoreId; packageIdentityName = $identity.PackageName; publisherName = $identity.Publisher
            lastPublishedApplicationSubmission = @{ id = '50' }; pendingApplicationSubmission = $null }
        submission = [pscustomobject]@{
            id = '50'; status = 'Published'; fileUploadUrl = 'https://fixture.invalid/?sig=fixture-SAS'
            targetPublishMode = 'Manual'; targetPublishDate = '1601-01-01T00:00:00Z'
            friendlyName = 'Retain friendly name'
            pricing = @{ priceId = 'Free'; marketSpecificPricings = @{ US = 'Base' }; sales = @() }
            listings = @{ 'en-us' = @{ baseListing = @{ title = 'Retain title'; images = @(@{ fileName = 'persisted.png'; fileStatus = 'Uploaded' }) }
                platformOverrides = @{ Windows81 = @{ description = 'retain override' } } } }
            notesForCertification = 'retain notes'; unknownFixtureField = @{ nested = @('retain', 'all') }
            applicationPackages = @(
                [pscustomobject]@{ fileName = 'old-arm64.msix'; fileStatus = 'Uploaded'; id = '1'; version = '1.2.2.0' }
                [pscustomobject]@{ fileName = 'old-x64.msix'; fileStatus = 'Uploaded'; id = '2'; version = '1.2.2.0' }
            )
        }
        calls = New-Object 'Collections.Generic.List[string]'
        changedMetadata = $false; changedPricingFlag = $false; failPublish = $false; failUpdate = $false; changedPointer = $false
        commits = 0; deletes = 0; uploads = 0; updates = 0
    }
    $cli = {
        param([string[]]$Arguments, [bool]$Json)
        $operation = ($Arguments | Select-Object -First 2) -join ' '
        $state.calls.Add($operation)
        switch ($operation) {
            'apps get' { return & $copy $state.app }
            'submission get' { return & $copy $state.submission }
            'publish offline-project' {
                if ($state.failPublish) { throw 'Simulated CLI upload failure.' }
                & $assert ($Arguments -ccontains '--noCommit') 'Upload must use --noCommit.'
                & $assert ($Arguments -ccontains '--appId') 'Upload must explicitly pin the product.'
                $state.uploads++
                $state.app.pendingApplicationSubmission = @{ id = '60' }
                $state.submission.id = '60'
                $state.submission.status = 'PendingCommit'
                $state.submission.applicationPackages[0].fileStatus = 'PendingDelete'
                # Model v0.4.4's extension-only replacement: the second old MSIX is left active.
                $state.submission.applicationPackages += @($packages | ForEach-Object {
                    [pscustomobject]@{ fileName = $_.fileName; fileStatus = 'PendingUpload' }
                })
                $priceIndex = [Array]::IndexOf($Arguments, '--priceId')
                if ($priceIndex -ge 0) { $state.submission.pricing.priceId = $Arguments[$priceIndex + 1] }
                if ($state.changedMetadata) {
                    $state.submission.friendlyName = $null
                    $state.submission.pricing.priceId = 'NotAvailable'
                    $state.submission.listings.'en-us'.baseListing.title = 'Unexpected change'
                    $state.submission | Add-Member -MemberType NoteProperty -Name cliAddedField -Value @{ internal = 'must be removed' }
                }
                if ($state.changedPricingFlag) { $state.submission.pricing.isAdvancedPricingModel = $false }
                if ($state.changedPointer) { $state.app.lastPublishedApplicationSubmission.id = '51' }
                return
            }
            'submission update' {
                & $assert ($Arguments[3] -ceq '--payload') 'Large private metadata must use --payload.'
                if ($state.failUpdate) { throw 'Simulated submission update failure.' }
                $state.submission = Get-Content -LiteralPath $Arguments[4] -Raw | ConvertFrom-Json
                $state.updates++
                return & $copy $state.submission
            }
            'submission delete' {
                & $assert ($Arguments -ccontains '--no-confirm') 'Deletion must be explicitly noninteractive.'
                $state.deletes++
                $state.app.pendingApplicationSubmission = $null
                return
            }
            'submission publish' { $state.commits++; $state.submission.status = 'CommitStarted'; return }
            'submission status' { return }
            default { throw "Unexpected offline command $operation" }
        }
    }.GetNewClosure()
    return [pscustomobject]@{ state = $state; cli = $cli }
}
function Upload($Mock, [string]$Price = '') {
    return New-StoreCliDraft $identity (New-Packages) '123' ('a' * 40) 'offline-project' $payload $Price $Mock.cli
}
function Set-Pending($Mock) {
    $Mock.state.app.pendingApplicationSubmission = @{ id = '60' }
    $Mock.state.submission.id = '60'
    $Mock.state.submission.status = 'PendingCommit'
}
function New-FixturePackage {
    param([string]$Path, [string]$Arch, [string]$Version = '1.2.3.0',
        [string]$Name = $identity.PackageName, [string]$Publisher = $identity.Publisher,
        [switch]$Duplicate, [switch]$Pdb, [string]$Language = 'en-US',
        [string[]]$ExtraFiles = @())
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path }
    $manifest = @"
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
<Identity Name="$Name" Publisher="$Publisher" ProcessorArchitecture="$Arch" Version="$Version"/>
<Resources><Resource Language="$Language"/></Resources>
<Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22621.0"/></Dependencies>
<Applications><Application Id="App" Executable="RedrainCast.App.exe"/></Applications>
<Capabilities><Capability Name="internetClient"/></Capabilities>
</Package>
"@
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = @('AppxManifest.xml', 'AppxBlockMap.xml', '[Content_Types].xml', 'resources.pri',
            'RedrainCast.App.pri', 'RedrainCast.App.exe', 'RedrainCast.App.dll', 'coreclr.dll',
            'FFmpeg.AutoGen.dll',
            'PRIVACY.md', 'THIRD_PARTY_NOTICES.txt') + $ExtraFiles
        if ($Duplicate) { $files += 'AppxManifest.xml' }
        if ($Pdb) { $files += 'debug.pdb' }
        foreach ($file in $files) {
            $writer = New-Object IO.StreamWriter($zip.CreateEntry($file).Open())
            try { $writer.Write($(if ($file -ceq 'AppxManifest.xml') { $manifest } else { 'offline non-executable fixture' })) }
            finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
}
$work = Join-Path $root ("artifacts\store-cli-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$payload = Join-Path $work 'private-submission.json'
try {
    Test 'trusted successful main run accepted' { Assert-StoreRun (New-Run) '123' '.github/workflows/windows.yml' }
    Test 'automatic trigger requires exact successful main push provenance' {
        $run = New-Run
        $event = [pscustomobject]@{ repository = @{ full_name = 'arisewanggithub/RedrainCast-Windows' }; workflow_run = Copy-Json $run }
        Assert-StoreReleaseTrigger $event $run
        foreach ($field in @('head_branch', 'head_sha', 'conclusion', 'event', 'run_attempt', 'id')) {
            $changed = Copy-Json $event
            $changed.workflow_run.$field = 'invalid'
            Reject { Assert-StoreReleaseTrigger $changed $run }
        }
        $event.workflow_run.head_repository.full_name = 'fork/repo'
        Reject { Assert-StoreReleaseTrigger $event $run }
        $run.event = 'workflow_run'
        Reject { Assert-StoreRun $run '123' '.github/workflows/windows.yml' }
        $run.path = '.github/workflows/store-release.yml'
        Assert-StoreRun $run '123' '.github/workflows/store-release.yml'
    }
    Test 'latest resolver requires current published release and successful main push source' {
        $global:storeLatestFixture = [pscustomobject]@{
            release = [pscustomobject]@{ tag_name = 'v0.2.4102'; draft = $false; prerelease = $false; target_commitish = ('a' * 40) }
            run = New-Run
        }
        function global:gh {
            $global:LASTEXITCODE = 0
            if ($args[0] -ceq 'api') {
                if ($args[1] -match '/releases/latest$') { return $global:storeLatestFixture.release | ConvertTo-Json -Depth 10 }
                return $global:storeLatestFixture.run | ConvertTo-Json -Depth 10
            }
            $index = [Array]::IndexOf($args, '--dir')
            @{ runId = '123'; version = '0.2.4102'; headSha = ('a' * 40) } |
                ConvertTo-Json | Set-Content -LiteralPath (Join-Path $args[$index + 1] 'ci-manifest.json')
        }
        try {
            $folder = Join-Path $work 'latest-valid'
            New-Item -ItemType Directory -Path $folder | Out-Null
            $run = Get-StoreLatestBuild $folder
            Assert ([string]$run.id -ceq '123') 'Latest manifest run was not selected.'
            foreach ($kind in @('draft', 'prerelease', 'tag', 'sha', 'event', 'failed')) {
                $global:storeLatestFixture.release.draft = $false
                $global:storeLatestFixture.release.prerelease = $false
                $global:storeLatestFixture.release.tag_name = 'v0.2.4102'
                $global:storeLatestFixture.release.target_commitish = ('a' * 40)
                $global:storeLatestFixture.run = New-Run
                switch ($kind) {
                    'draft' { $global:storeLatestFixture.release.draft = $true }
                    'prerelease' { $global:storeLatestFixture.release.prerelease = $true }
                    'tag' { $global:storeLatestFixture.release.tag_name = 'v0.2.4601' }
                    'sha' { $global:storeLatestFixture.release.target_commitish = ('b' * 40) }
                    'event' { $global:storeLatestFixture.run.event = 'workflow_dispatch' }
                    'failed' { $global:storeLatestFixture.run.conclusion = 'failure' }
                }
                $folder = Join-Path $work "latest-$kind"
                New-Item -ItemType Directory -Path $folder | Out-Null
                Reject { Get-StoreLatestBuild $folder }
            }
        } finally {
            Remove-Item Function:\gh
            Remove-Variable storeLatestFixture -Scope Global
        }
    }
    Test 'noninteractive cleanup deletes only exact native credential and known files without poisoning native exit' {
        $client = [guid]::NewGuid().ToString()
        $target = "MicrosoftStoreCli:user=$client"
        $profile = Join-Path $work 'isolated-localappdata'
        $folder = Join-Path $profile 'Microsoft\MSStore.CLI'
        New-Item -ItemType Directory -Path (Join-Path $folder 'Cache') -Force | Out-Null
        $settings = Join-Path $folder 'settings.json'
        $cache = Join-Path $folder 'Cache\msstore_msal_cache.txt'
        $keep = Join-Path $folder 'unrelated.json'
        Set-Content -LiteralPath $settings -Value '{"ClientId":"offline-fixture"}'
        Set-Content -LiteralPath $cache -Value 'offline-fixture'
        Set-Content -LiteralPath $keep -Value 'keep'
        $priorExitVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
        $priorExit = if ($null -eq $priorExitVariable) { 0 } else { $priorExitVariable.Value }
        try {
            $null = & cmdkey "/generic:$target" '/user:offline-fixture' '/pass:offline-fixture-value' 2>$null
            Assert ($LASTEXITCODE -eq 0) 'Could not create isolated native credential fixture.'
            $listing = @(& cmdkey "/list:$target" 2>$null) -join "`n"
            Assert ($listing -match 'offline-fixture') 'Native credential fixture was not persisted.'
            $global:LASTEXITCODE = 0
            Clear-StoreCliCredentials -ClientId $client -LocalApplicationData $profile
            Assert ($LASTEXITCODE -eq 0) 'Cleanup poisoned the successful native exit code.'
            Assert (-not (Test-Path $settings) -and -not (Test-Path $cache)) 'Known private CLI files survived cleanup.'
            Assert (Test-Path $keep) 'Cleanup deleted an unrelated file.'
            $listing = @(& cmdkey "/list:$target" 2>$null) -join "`n"
            Assert ($listing -notmatch 'offline-fixture') 'Exact native credential survived cleanup.'
            Clear-StoreCliCredentials -ClientId $client -LocalApplicationData $profile
            Reject { Clear-StoreCliCredentials -ClientId 'invalid' -LocalApplicationData $profile } 'exact configured client'
            $invoke = Get-Content -LiteralPath (Join-Path $root 'tools\Invoke-StoreRelease.ps1') -Raw
            Assert ($invoke -match 'finally\s*\{\s*try \{ Clear-StoreCliCredentials \}' -and
                $invoke -notmatch "Invoke-StoreCli @\('reconfigure', '--reset'\)") 'Workflow still invokes interactive reset.'
        } finally {
            $null = & cmdkey "/delete:$target" 2>$null
            $global:LASTEXITCODE = $priorExit
        }
    }
    Test 'CLI boundary separates JSON, suppresses secret output and pins real Windows version' {
        $names = @('PARTNER_CENTER_TENANT_ID', 'PARTNER_CENTER_CLIENT_ID', 'PARTNER_CENTER_CLIENT_SECRET', 'PARTNER_CENTER_SELLER_ID')
        $saved = @{}; foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
        $global:storeCliMockMode = 'success'
        function global:msstore {
            $global:LASTEXITCODE = 0
            if ($args -notcontains '--output-stream' -or $args[-1] -cne 'stderr') { throw 'Output stream not explicitly selected.' }
            switch ($args[0]) {
                '--version' { return '0.4.4.3+4fa163e6db' }
                'reconfigure' { return }
                default {
                    if ($global:storeCliMockMode -ceq 'failure') {
                        $global:LASTEXITCODE = 1
                        Write-Error 'fixture-secret fixture-SAS' -ErrorAction Continue
                        return 'fixture-token'
                    }
                    if ($global:storeCliMockMode -ceq 'invalid') { return 'fixture-secret' }
                    return '{"Id":"60","Status":"PendingCommit"}'
                }
            }
        }
        try {
            $env:PARTNER_CENTER_TENANT_ID = '11111111-1111-1111-1111-111111111111'
            $env:PARTNER_CENTER_CLIENT_ID = '22222222-2222-2222-2222-222222222222'
            $env:PARTNER_CENTER_CLIENT_SECRET = 'fixture-secret'
            $env:PARTNER_CENTER_SELLER_ID = 'fixture-seller'
            Initialize-StoreCli
            $json = Invoke-StoreCli @('submission', 'get', $identity.StoreId) -Json
            Assert ($json.id -ceq '60') 'JSON command output was not parsed.'
            $env:PARTNER_CENTER_SELLER_ID = ''
            Reject { Initialize-StoreCli } 'SELLER_ID'
            $global:storeCliMockMode = 'failure'
            Reject { Invoke-StoreCli @('submission', 'get', $identity.StoreId) -Json } 'exit 1'
            $global:storeCliMockMode = 'invalid'
            Reject { Invoke-StoreCli @('submission', 'get', $identity.StoreId) -Json } 'invalid JSON'
        } finally {
            foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
            Remove-Item Function:\msstore
            Remove-Variable storeCliMockMode -Scope Global
        }
    }
    Test 'fork, PR, branch, failure, attempt and workflow rejected' {
        foreach ($field in @('head_branch', 'conclusion', 'event', 'path', 'run_attempt', 'head_sha')) {
            $run = New-Run; $run.$field = 'invalid'
            Reject { Assert-StoreRun $run '123' '.github/workflows/windows.yml' }
        }
        $run = New-Run; $run.head_repository.full_name = 'fork/repo'
        Reject { Assert-StoreRun $run '123' '.github/workflows/windows.yml' }
    }
    Test 'canonical fingerprints retain nested metadata' {
        Assert ((ConvertTo-StoreCanonicalJson @{ b = 2; a = 1 }) -ceq '{"a":1,"b":2}') 'Canonical JSON changed.'
        Assert ((ConvertTo-StoreCanonicalJson ('{}' | ConvertFrom-Json)) -ceq '{}') 'Empty service objects must fingerprint under strict mode.'
        Assert ((ConvertTo-StoreCanonicalJson ('{"nested":{}}' | ConvertFrom-Json)) -ceq '{"nested":{}}') 'Nested empty service objects must fingerprint.'
        $m = New-Mock; $hash = Get-StoreMetadataFingerprint $m.state.submission
        $m.state.submission.unknownFixtureField.nested[1] = 'changed'
        Assert ((Get-StoreMetadataFingerprint $m.state.submission) -cne $hash) 'Nested metadata was ignored.'
    }
    Test 'advanced pricing model is non-authoritative but actual prices remain fingerprinted' {
        $before = '{"Id":"50","Pricing":{"PriceId":"Tier1012","TrialPeriod":"NoFreeTrial","MarketSpecificPricings":{"US":"Tier1012"},"IsAdvancedPricingModel":true}}' | ConvertFrom-Json
        $after = Copy-Json $before
        $after.Pricing.IsAdvancedPricingModel = $false
        Assert ((Get-StoreMetadataFingerprint $before) -ceq (Get-StoreMetadataFingerprint $after)) 'Non-authoritative dashboard flag changed business metadata.'
        Assert ((Get-StoreSubmissionFingerprint $before) -ceq (Get-StoreSubmissionFingerprint $after)) 'Non-authoritative dashboard flag changed the stable submission fingerprint.'
        $diff = Get-StoreMetadataDiffSummary $before $after
        Assert ($diff.changedPropertyCount -eq 0 -and $diff.pricingChanges.Count -eq 0) 'Non-authoritative dashboard flag appeared as a pricing change.'
        foreach ($field in @('PriceId', 'TrialPeriod', 'MarketSpecificPricings')) {
            $changed = Copy-Json $after
            if ($field -ceq 'MarketSpecificPricings') { $changed.Pricing.MarketSpecificPricings.US = 'Free' }
            elseif ($field -ceq 'TrialPeriod') { $changed.Pricing.TrialPeriod = 'ThirtyDays' }
            else { $changed.Pricing.PriceId = 'Free' }
            Assert ((Get-StoreMetadataFingerprint $after) -cne (Get-StoreMetadataFingerprint $changed)) "$field changed without affecting business metadata."
            $diff = Get-StoreMetadataDiffSummary $after $changed
            Assert ($diff.pricingChanges.Count -eq 1 -and $diff.pricingChanges[0].propertyPath -ceq "pricing.$($field.Substring(0,1).ToLowerInvariant())$($field.Substring(1))") "$field change was not classified."
        }
        $evidence = @(Get-StorePricingEvidence $after | Where-Object { $_.propertyPath -ceq 'pricing.isAdvancedPricingModel' })
        Assert ($evidence.Count -eq 1 -and $evidence[0].classification -ceq 'non-authoritative API flag') 'Dashboard flag was not identified as non-authoritative.'
    }
    Test 'metadata diff is bounded, allowlisted and contains hashes rather than values' {
        $before = [pscustomobject]@{
            listings = @{ 'en-us' = @{ baseListing = @{ title = 'Private title'; description = 'Private description' } } }
            pricing = @{ priceId = 'Free' }; fileUploadUrl = 'https://fixture.invalid/?sig=fixture-SAS'
            unknownFixtureField = 'Private unknown value'; targetPublishMode = 'Manual'
        }
        $after = Copy-Json $before
        $after.listings.'en-us'.baseListing.title = 'Changed private title'
        $after.pricing.priceId = 'Tier1012'
        $after.unknownFixtureField = 'Changed private unknown value'
        $after.targetPublishMode = 'Immediate'
        $summary = Get-StoreMetadataDiffSummary $before $after
        $json = ConvertTo-Json $summary -Compress -Depth 6
        Assert ($summary.changedPropertyCount -eq 2 -and $summary.unclassifiedPropertyChangeCount -eq 1) 'Diff counts include ignored or unknown paths incorrectly.'
        Assert ($json -match '"propertyPath":"listings"' -and $json -match '"changeClass":"value-changed"') 'Allowlisted listing change is missing.'
        Assert ($json -match '"propertyPath":"pricing"' -and $json -match '[a-f0-9]{64}') 'Allowlisted pricing hash is missing.'
        Assert ($json -notmatch 'Private title|Private description|Tier1012|fixture-SAS|Private unknown value|Changed private') 'Diff leaked field values.'
        Reject { Get-StoreMetadataDiffSummary $before $after 21 } 'between 1 and 20'
    }
    Test 'first publication guard is explicit and read-only' {
        $m = New-Mock
        $m.state.app.lastPublishedApplicationSubmission = $null
        Reject { Get-StoreState $identity $m.cli } 'First publication'
        Assert ($m.state.calls.Count -eq 1 -and $m.state.calls[0] -ceq 'apps get') 'Unpublished product reached submission operations.'
    }
    Test 'pending ingestion permits unknown metadata but populated version and architecture must match' {
        $expected = (New-Packages).packages
        foreach ($status in @('PendingUpload', 'Uploaded')) {
            $submission = [pscustomobject]@{ applicationPackages = @($expected | ForEach-Object {
                [pscustomobject]@{ fileName = $_.fileName; fileStatus = $status; version = $_.version; architecture = $_.architecture.ToUpperInvariant() }
            }) }
            Assert-StoreUploadedPackages $submission $expected
            foreach ($field in @('version', 'architecture')) {
                $changed = Copy-Json $submission
                $changed.applicationPackages[0].$field = 'wrong'
                Reject { Assert-StoreUploadedPackages $changed $expected } 'version or architecture'
                $changed.applicationPackages[0].$field = $null
                if ($status -ceq 'Uploaded') {
                    Reject { Assert-StoreUploadedPackages $changed $expected } 'metadata is incomplete'
                } else {
                    Assert-StoreUploadedPackages $changed $expected
                    $changed.applicationPackages[0].PSObject.Properties.Remove($field)
                    Assert-StoreUploadedPackages $changed $expected
                }
            }
        }
    }
    Test 'package-only upload uses official CLI, preserves listings and retires all old packages' {
        $m = New-Mock; $listings = ConvertTo-StoreCanonicalJson $m.state.submission.listings
        $receipt = Upload $m
        Assert ($m.state.uploads -eq 1 -and $m.state.updates -eq 1 -and $m.state.commits -eq 0 -and $m.state.deletes -eq 0) 'Wrong operation counts.'
        Assert ((ConvertTo-StoreCanonicalJson $m.state.submission.listings) -ceq $listings) 'Listings changed.'
        Assert ($receipt.schemaVersion -eq 3 -and $receipt.targetPublishMode -ceq 'Immediate') 'Receipt or mode incorrect.'
        Assert (@($m.state.submission.applicationPackages | Where-Object { $_.fileStatus -ceq 'PendingDelete' }).Count -eq 2) 'Not all old packages retired.'
        Assert (-not (Test-Path -LiteralPath $payload)) 'Private payload was retained.'
        Assert ((ConvertTo-Json $receipt -Depth 20) -notmatch 'fixture-SAS|Retain title') 'Receipt contains private metadata.'
    }
    Test 'existing human or legacy API draft is never replaced by publish' {
        $m = New-Mock; Set-Pending $m
        Reject { Upload $m } 'Existing draft'
        Assert ($m.state.uploads -eq 0 -and $m.state.deletes -eq 0) 'Existing draft mutated.'
    }
    Test 'automatic cleanup requires trusted run provenance and unchanged live draft ownership' {
        $m = New-Mock; Set-Pending $m
        $run = New-Run
        $run.path = '.github/workflows/store-release.yml'; $run.event = 'workflow_run'
        $ownership = Get-StoreInspection (Get-StoreState $identity $m.cli) '60' '123' ('a' * 40)
        $ownership | Add-Member -NotePropertyName workflowRunId -NotePropertyValue '123'
        $ownership | Add-Member -NotePropertyName priorLastPublishedId -NotePropertyValue '50'
        Assert-StoreDraftOwner $run $ownership (Get-StoreState $identity $m.cli) '123'
        foreach ($field in @('head_branch', 'path', 'event', 'status', 'head_sha', 'conclusion', 'id')) {
            $bad = Copy-Json $run; $bad.$field = 'wrong'
            Reject { Assert-StoreDraftOwner $bad $ownership (Get-StoreState $identity $m.cli) '123' } 'ownership'
        }
        foreach ($field in @('workflowRunId', 'sourceSha', 'submissionId', 'priorLastPublishedId')) {
            $bad = Copy-Json $ownership; $bad.$field = 'wrong'
            Reject { Assert-StoreDraftOwner $run $bad (Get-StoreState $identity $m.cli) '123' } 'ownership'
        }
        $run.conclusion = 'failure'
        Assert-StoreDraftOwner $run $ownership (Get-StoreState $identity $m.cli) '123'
        Reject { Remove-StoreInspectedDraft $identity $ownership '60' $m.cli '49' } 'Published submission changed'
        Assert ($m.state.deletes -eq 0) 'Concurrent published-pointer change allowed deletion.'
        $m.state.submission.notesForCertification = 'human edit'
        Reject { Remove-StoreInspectedDraft $identity $ownership '60' $m.cli } 'changed'
        Assert ($m.state.deletes -eq 0) 'Edited owned draft was deleted.'
        $m.state.submission.status = 'CommitStarted'
        Reject { Remove-StoreInspectedDraft $identity $ownership '60' $m.cli } 'PendingCommit'
        Assert ($m.state.deletes -eq 0) 'Submitted owned draft was deleted.'
    }
    Test 'automation ownership is captured before failed package verification and after successful updates' {
        $records = New-Object 'Collections.Generic.List[object]'
        $copy = ${function:Copy-Json}
        $save = {
            param($inspection, $published)
            $records.Add([pscustomobject]@{ inspection = & $copy $inspection; published = $published })
        }.GetNewClosure()
        $m = New-Mock
        $receipt = New-StoreCliDraft $identity (New-Packages) '123' ('a' * 40) 'offline-project' $payload '' $m.cli $save
        Assert ($records.Count -eq 2) 'Creation and final saved draft must both have ownership snapshots.'
        Assert ($records[-1].inspection.submissionId -ceq $receipt.submissionId -and $records[-1].published -ceq '50') 'Ownership identity mismatch.'
        Remove-StoreInspectedDraft $identity $records[-1].inspection $receipt.submissionId $m.cli
        Assert ($m.state.deletes -eq 1) 'Unchanged owned draft was not deleted.'
        $m = New-Mock
        $records.Clear()
        $packages = New-Packages
        $packages.packages[0].fileName = 'missing.msix'
        Reject { New-StoreCliDraft $identity $packages '123' ('a' * 40) 'offline-project' $payload '' $m.cli $save } 'exactly the two'
        Assert ($records.Count -eq 1 -and $m.state.deletes -eq 0 -and $m.state.commits -eq 0) 'Failed upload must retain creation evidence without automatic deletion.'
        $m = New-Mock
        $records.Clear()
        $m.state.changedPointer = $true
        Reject { New-StoreCliDraft $identity (New-Packages) '123' ('a' * 40) 'offline-project' $payload '' $m.cli $save } 'Safe structural diff'
        Assert ($records.Count -eq 0) 'Concurrent published-pointer changes must not establish ownership.'
    }
    Test 'duplicate, older and unversioned releases never reach upload' {
        foreach ($version in @('1.2.3.0', '1.2.4.0', 'invalid')) {
            $m = New-Mock
            $m.state.submission.applicationPackages[0].version = $version
            Reject { Upload $m } 'newer than'
            Assert ($m.state.uploads -eq 0 -and $m.state.deletes -eq 0) 'Duplicate or downgrade mutated Store.'
        }
    }
    Test 'Base is not Free and cannot silently reach upload' {
        foreach ($price in @('Base', '', ' ')) {
            $m = New-Mock; $m.state.submission.pricing.priceId = $price
            Reject { Upload $m } 'Price cannot'
            Assert ($m.state.uploads -eq 0) 'Unsafe price reached upload.'
        }
        $m = New-Mock; $m.state.submission.pricing = $null
        Reject { Upload $m } 'Price cannot'
        Assert ((Get-StorePriceClassification $m.state.submission) -ceq 'Missing') 'Missing pricing summary changed.'
        $m = New-Mock; $m.state.submission.pricing.priceId = 'Base'
        Assert ((Get-StorePriceClassification $m.state.submission) -match 'not evidence of Free') 'Base pricing summary is misleading.'
    }
    Test 'explicit owner-approved price override is passed only when supplied' {
        $m = New-Mock; $m.state.submission.pricing.priceId = 'Base'
        $null = Upload $m 'Free'
        Assert ($m.state.submission.pricing.priceId -ceq 'Free') 'Approved price not saved.'
        $m = New-Mock
        Reject { Upload $m 'Base' } 'approval'
        Assert ($m.state.uploads -eq 0) 'Invalid override reached Store.'
    }
    Test 'published pointer changes block follow-up update and receipt' {
        $m = New-Mock; $m.state.changedPointer = $true
        Reject { Upload $m } 'Safe structural diff'
        Assert ($m.state.updates -eq 0 -and $m.state.commits -eq 0) 'Unexpected follow-up mutation.'
    }
    Test 'CLI metadata changes are restored from the private published snapshot' {
        $m = New-Mock; $m.state.changedMetadata = $true
        $baselineHash = Get-StoreMetadataFingerprint $m.state.submission
        $receipt = Upload $m
        Assert ((Get-StoreMetadataFingerprint $m.state.submission) -ceq $baselineHash) 'Published metadata was not restored exactly.'
        Assert ($null -eq $m.state.submission.friendlyName -and
            $m.state.submission.pricing.priceId -ceq 'Free' -and
            $m.state.submission.listings.'en-us'.baseListing.title -ceq 'Retain title') 'Business metadata was not preserved.'
        Assert ($null -eq $m.state.submission.PSObject.Properties['cliAddedField']) 'An unrecognized CLI-added metadata field was retained.'
        Assert ($m.state.updates -eq 2 -and $m.state.commits -eq 0 -and $receipt.targetPublishMode -ceq 'Immediate') 'Metadata restore or final draft save did not follow the expected path.'
    }
    Test 'CLI dashboard flag changes do not trigger metadata restoration or block certification' {
        $m = New-Mock
        $m.state.submission.pricing.isAdvancedPricingModel = $true
        $m.state.changedPricingFlag = $true
        $baselineHash = Get-StoreMetadataFingerprint $m.state.submission
        $receipt = Upload $m
        Assert ($m.state.submission.pricing.priceId -ceq 'Free' -and
            (ConvertTo-StoreCanonicalJson $m.state.submission.pricing.marketSpecificPricings) -ceq '{"US":"Base"}') 'Business pricing changed while normalizing the dashboard flag.'
        Assert ((Get-StoreMetadataFingerprint $m.state.submission) -ceq $baselineHash) 'Dashboard flag changed the saved business pricing fingerprint.'
        Assert ($m.state.updates -eq 1) 'Dashboard flag triggered an unnecessary metadata restore.'
        $m.state.submission.pricing.isAdvancedPricingModel = $true
        $status = Submit-StoreCliDraft $identity $receipt (New-Packages) '123' ('a' * 40) $m.cli
        Assert ($m.state.commits -eq 1 -and $status.status -ceq 'CommitStarted') 'Inconsistent dashboard flag blocked certification.'
    }
    Test 'metadata restoration failure never requests certification' {
        $m = New-Mock; $m.state.changedMetadata = $true; $m.state.failUpdate = $true
        Reject { Upload $m } 'Simulated submission update failure'
        Assert ($m.state.commits -eq 0 -and $m.state.updates -eq 0) 'Failed restoration continued toward certification.'
    }
    Test 'official CLI PascalCase server fields are excluded and pricing children remain guarded' {
        $before = '{"Id":"50","Status":"Published","StatusDetails":{},"FileUploadUrl":"fixture-SAS","ApplicationPackages":[],"TargetPublishMode":"Manual","TargetPublishDate":null,"FriendlyName":"Submission 1","Pricing":{"PriceId":"Free","TrialPeriod":"NoFreeTrial","MarketSpecificPricings":{},"IsAdvancedPricingModel":true},"HardwarePreferences":[]}' | ConvertFrom-Json
        $after = Copy-Json $before
        $after.Id = '60'; $after.Status = 'PendingCommit'; $after.FileUploadUrl = 'fixture-token'
        $after.ApplicationPackages = @([pscustomobject]@{ FileName = 'RedrainCast-x64.msix' })
        $after.FriendlyName = $null; $after.TargetPublishMode = 'Immediate'
        Assert ((Get-StoreMetadataFingerprint $before) -ceq (Get-StoreMetadataFingerprint $after)) 'Server-generated fields changed business fingerprint.'
        $diff = Get-StoreMetadataDiffSummary $before $after
        Assert ($diff.changedPropertyCount -eq 0 -and $diff.unclassifiedPropertyChangeCount -eq 0) 'PascalCase server fields were classified as business changes.'
        $after.Pricing.IsAdvancedPricingModel = $false
        Assert ((Get-StoreMetadataFingerprint $before) -ceq (Get-StoreMetadataFingerprint $after)) 'Inconsistent advanced pricing flag changed the metadata fingerprint.'
        $diff = Get-StoreMetadataDiffSummary $before $after
        Assert ($diff.changedPropertyCount -eq 0 -and $diff.pricingChanges.Count -eq 0) 'Inconsistent advanced pricing flag appeared in the metadata diff.'
        $after.Pricing.TrialPeriod = '15Days'
        $diff = Get-StoreMetadataDiffSummary $before $after
        Assert ($diff.pricingChanges.Count -eq 1 -and $diff.pricingChanges[0].propertyPath -ceq 'pricing.trialPeriod') 'Pricing leaf change was not classified.'
        Assert ((Get-StoreMetadataFingerprint $before) -cne (Get-StoreMetadataFingerprint $after)) 'Pricing change bypassed fingerprint.'
    }
    Test 'restoration preserves empty and single-item arrays without replacing package data' {
        $before = '{"HardwarePreferences":[],"GamingOptions":["value"],"Pricing":{"PriceId":"Free"}}' | ConvertFrom-Json
        $after = '{"HardwarePreferences":null,"GamingOptions":null,"Pricing":{"PriceId":"NotAvailable"},"ApplicationPackages":[{"FileName":"RedrainCast-new.msix"}]}' | ConvertFrom-Json
        $packageHash = ConvertTo-StoreCanonicalJson $after.ApplicationPackages
        & (Get-Module StoreCli) { param($Before, $After) Restore-StoreSubmissionMetadata $Before $After } $before $after
        Assert ($after.HardwarePreferences -is [array] -and $after.HardwarePreferences.Count -eq 0) 'Empty array became null.'
        Assert ($after.GamingOptions -is [array] -and $after.GamingOptions.Count -eq 1) 'Single-item array became scalar.'
        Assert ((ConvertTo-StoreCanonicalJson $after.ApplicationPackages) -ceq $packageHash) 'Restoration overwrote uploaded packages.'
    }
    Test 'inspection projects only safe package names versions architectures and states' {
        $m = New-Mock; Set-Pending $m
        $m.state.submission.applicationPackages[0].fileName = 'RedrainCast-arm64.msix'
        $m.state.submission.applicationPackages[0] | Add-Member -NotePropertyName architecture -NotePropertyValue 'ARM64'
        $m.state.submission.applicationPackages[1].fileName = 'https://fixture.invalid/?sig=fixture-SAS'
        $inspection = Get-StoreInspection (Get-StoreState $identity $m.cli) '60' '456' ('b' * 40)
        Assert ($inspection.packages.Count -eq 2 -and $inspection.packages[0].version -ceq '1.2.2.0' -and
            $inspection.packages[0].architecture -ceq 'ARM64' -and $inspection.packages[0].fileStatus -ceq 'Uploaded') 'Package evidence missing.'
        Assert ($inspection.pricingFields.Count -eq 4 -and $inspection.pricingFields[0].classification -ceq 'Free') 'Safe pricing field evidence missing.'
        Assert ((ConvertTo-Json $inspection -Depth 10) -notmatch 'fixture-SAS|Retain title|fixture.invalid') 'Unsafe package metadata escaped.'
    }
    Test 'read-only status logs the safe package evidence projection' {
        $invoke = Get-Content -LiteralPath (Join-Path $root 'tools\Invoke-StoreRelease.ps1') -Raw
        Assert ($invoke -match "Current package evidence: .*Get-StorePackageEvidence") 'Status does not expose safe saved package version and architecture evidence.'
        Assert ($invoke -match 'packages = \$certification\.packages' -and
            $invoke -notmatch 'Validated packages uploaded to draft') 'Certification receipt overstates package ingestion.'
    }
    Test 'failed upload is never automatically retried or deleted' {
        $m = New-Mock; $m.state.failPublish = $true
        Reject { Upload $m } 'Simulated'
        Assert ($m.state.deletes -eq 0 -and $m.state.commits -eq 0) 'Failure recovery mutated a draft.'
    }
    Test 'inspection contains fingerprints only and explicit unchanged discard is separate' {
        $m = New-Mock; Set-Pending $m
        $inspection = Get-StoreInspection (Get-StoreState $identity $m.cli) '60' '456' ('b' * 40)
        Assert ((ConvertTo-Json $inspection) -notmatch 'fixture-SAS|Retain title') 'Inspection contains metadata.'
        Remove-StoreInspectedDraft $identity $inspection '60' $m.cli
        Assert ($m.state.deletes -eq 1 -and $m.state.uploads -eq 0) 'Discard uploaded a replacement.'
    }
    Test 'read-only inspection can resolve the current pending submission ID' {
        $m = New-Mock; Set-Pending $m
        $state = Get-StoreState $identity $m.cli
        $inspection = Get-StoreInspection $state '' '456' ('b' * 40)
        Assert ($inspection.submissionId -ceq '60') 'Inspection did not resolve the current pending ID.'
        Assert ($m.state.deletes -eq 0 -and $m.state.uploads -eq 0 -and $m.state.commits -eq 0) 'Read-only inspection mutated Store state.'
    }
    Test 'stale inspection, wrong ID, nonpending draft and legacy snapshots never delete' {
        foreach ($change in @('metadata', 'id', 'status', 'schema')) {
            $m = New-Mock; Set-Pending $m
            $inspection = Get-StoreInspection (Get-StoreState $identity $m.cli) '60' '456' ('b' * 40)
            switch ($change) {
                'metadata' { $m.state.submission.notesForCertification = 'changed' }
                'id' { $inspection.submissionId = '61' }
                'status' { $m.state.submission.status = 'CommitStarted' }
                'schema' { $inspection.schemaVersion = 2 }
            }
            Reject { Remove-StoreInspectedDraft $identity $inspection '60' $m.cli }
            Assert ($m.state.deletes -eq 0) 'Unsafe discard happened.'
        }
    }
    Test 'trusted receipt commits once via CLI and obtains official status' {
        $m = New-Mock; $receipt = Upload $m
        $status = Submit-StoreCliDraft $identity $receipt (New-Packages) '123' ('a' * 40) $m.cli
        Assert ($m.state.commits -eq 1 -and $m.state.calls.Contains('submission status')) 'Official commit/status missing.'
        Assert ($status.status -ceq 'CommitStarted') 'Safe current state not reported.'
        $newPackages = @($status.packages | Where-Object { $_.fileName -like 'RedrainCast-123-*' })
        Assert ($newPackages.Count -eq 2 -and @($newPackages | Where-Object { $_.fileStatus -ceq 'PendingUpload' -and $null -eq $_.version -and $null -eq $_.architecture }).Count -eq 2) 'Actual post-commit package evidence was not projected without inferring ingestion.'
        $m.state.submission.status = 'fixture-secret'
        Assert ((Get-StoreSafeStatus $m.state.submission) -ceq 'Other (suppressed)') 'Untrusted status leaked.'
    }
    Test 'receipt, pricing, listing, package or publish-mode changes block certification' {
        foreach ($change in @('receipt', 'price', 'listing', 'package', 'mode', 'pointer')) {
            $m = New-Mock; $receipt = Upload $m
            switch ($change) {
                'receipt' { $receipt.schemaVersion = 1 }
                'price' { $m.state.submission.pricing.priceId = 'Tier1012' }
                'listing' { $m.state.submission.listings.'en-us'.baseListing.title = 'changed' }
                'package' { $m.state.submission.applicationPackages[-1].fileName = 'other.msix' }
                'mode' { $m.state.submission.targetPublishMode = 'Manual' }
                'pointer' { $m.state.app.pendingApplicationSubmission.id = '61' }
            }
            Reject { Submit-StoreCliDraft $identity $receipt (New-Packages) '123' ('a' * 40) $m.cli }
            Assert ($m.state.commits -eq 0) 'Unsafe certification started.'
        }
    }
    Test 'commit is verified accepted and is never retried' {
        foreach ($status in @('CommitFailed', 'CertificationFailed', 'PendingCommit', 'PublishFailed')) {
            $m = New-Mock; $receipt = Upload $m
            $inner = $m.cli
            $cli = {
                param([string[]]$Arguments, [bool]$Json)
                $result = & $inner $Arguments $Json
                if ((($Arguments | Select-Object -First 2) -join ' ') -ceq 'submission publish') {
                    $m.state.submission.status = $status
                }
                return $result
            }
            Reject { Submit-StoreCliDraft $identity $receipt (New-Packages) '123' ('a' * 40) $cli } 'acceptance'
            Assert ($m.state.commits -eq 1) 'Commit was retried.'
        }
    }
    Test 'MSIX identity, unified executable, duplicate, debug and standalone helper guards retained' {
        $path = Join-Path $work 'package.msix'
        New-FixturePackage $path 'arm64'
        $null = Read-StorePackage $path 'arm64' $identity
        foreach ($kind in @('Name', 'Publisher', 'Version', 'Architecture', 'SeparateWorkerExecutable',
            'WorkerRuntimeDuplicate', 'Duplicate', 'Pdb', 'ExtraFiles')) {
            $args = @{ Path = $path; Arch = 'arm64' }
            switch ($kind) {
                'Name' { $args.Name = 'wrong' }
                'Publisher' { $args.Publisher = 'CN=wrong' }
                'Version' { $args.Version = '1.2.3.4' }
                'Architecture' { $args.Arch = 'x64' }
                'SeparateWorkerExecutable' { $args.ExtraFiles = @('RedrainCast.CaptureWorker.exe') }
                'WorkerRuntimeDuplicate' { $args.ExtraFiles = @('capture-worker/coreclr.dll') }
                'ExtraFiles' { $args.ExtraFiles = @('ffmpeg.exe') }
                default { $args[$kind] = $true }
            }
            New-FixturePackage @args
            Reject { Read-StorePackage $path 'arm64' $identity }
        }
    }
    Test 'exact architecture pair copied without rebuilding, BCP47 casing normalized' {
        $folder = Join-Path $work 'release'
        foreach ($arch in @('arm64', 'x64')) {
            $dir = Join-Path $folder $arch; New-Item -ItemType Directory -Path $dir -Force | Out-Null
            $language = if ($arch -ceq 'x64') { 'en-us' } else { 'en-US' }
            New-FixturePackage (Join-Path $dir 'release.msix') $arch -Language $language
        }
        $set = Get-StorePackageSet $folder $identity '123'
        Assert ($set.packages.Count -eq 2) 'Architecture pair missing.'
        foreach ($package in $set.packages) {
            $original = Join-Path (Join-Path $folder $package.architecture) 'release.msix'
            $staged = Join-Path (Join-Path $set.directory $package.architecture) $package.fileName
            Assert ((Get-FileHash $original).Hash -ceq (Get-FileHash $staged).Hash) 'Package bytes changed.'
        }
    }
    Test 'restore-only publish adapter preserves CLI WinUI detection without shipping umbrella dependencies' {
        $app = Join-Path $root 'src\RedrainCast.App'
        $adapter = New-StorePublishProject $app (Join-Path $work 'publish-project')
        [xml]$source = Get-Content -LiteralPath (Join-Path $app 'RedrainCast.App.csproj') -Raw
        [xml]$project = Get-Content -LiteralPath (Join-Path $adapter 'StorePublish.csproj') -Raw
        $reference = $project.SelectSingleNode('/Project/ItemGroup/PackageReference')
        Assert ($reference.Include -ceq 'Microsoft.WindowsAppSDK') 'Pinned CLI will not recognize the WinUI publisher.'
        Assert ($reference.Version -ceq $source.SelectSingleNode('/Project/ItemGroup/PackageReference[@Include="Microsoft.WindowsAppSDK.WinUI"]').Version) 'Adapter SDK version differs from the app.'
        Assert ($reference.ExcludeAssets -ceq 'all' -and $reference.PrivateAssets -ceq 'all') 'Restore-only dependency assets are not excluded.'
        Assert ($project.Project.PropertyGroup.TargetFramework -ceq $source.Project.PropertyGroup[0].TargetFramework) 'Adapter framework differs from the app.'
        Assert ((Get-FileHash (Join-Path $adapter 'Package.appxmanifest')).Hash -ceq
            (Get-FileHash (Join-Path $app 'Package.appxmanifest')).Hash) 'Adapter changed the manifest.'
        Assert ($null -eq $source.SelectSingleNode('/Project/ItemGroup/PackageReference[@Include="Microsoft.WindowsAppSDK"]')) 'Shipping app must not reference the umbrella SDK.'
        Reject { New-StorePublishProject $app $adapter } 'must be new'
        $invoke = Get-Content -LiteralPath (Join-Path $root 'tools\Invoke-StoreRelease.ps1') -Raw
        Assert ($invoke -match 'New-StorePublishProject' -and $invoke -match '\$publishProject `') 'Live uploads must use the adapter rather than classify the shipping project.'
    }
    Test 'workflow pins official CLI and has no custom transport or legacy recovery stage' {
        $workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\store-release.yml') -Raw
        Assert ([regex]::Matches($workflow, '(?m)^    runs-on: windows-latest\s*$').Count -eq 2) 'Store jobs must use standard GitHub-hosted Windows runners.'
        Assert ($workflow -match '(?m)^          DOTNET_INSTALL_DIR: \$\{\{ runner\.tool_cache \}\}\\dotnet\s*$') 'The Store CLI build must install .NET under the runner-writable tool cache.'
        Assert ($workflow -match 'concurrency:\s*\r?\n  group: microsoft-store-9P1153MP6R32\s*\r?\n  cancel-in-progress: false') 'Store operations must remain serialized on the shared runner credential store.'
        Assert ($workflow -match 'microsoft/microsoft-store-apppublisher@cc9910a8d59f2eb55cbb83df0a3800cf3b5300e0') 'Official action not pinned.'
        Assert ($workflow -match 'version: v0.4.4' -and $workflow -match 'PARTNER_CENTER_SELLER_ID') 'CLI pin or seller ID missing.'
        Assert ($workflow -match 'workflow_run:' -and $workflow -match 'workflows: \[Windows build\]' -and
            $workflow -match "workflow_run.event == 'push'" -and $workflow -match 'release-latest') 'Automatic release provenance gate missing.'
        Assert ($workflow -match "store:\s+needs: offline-checks\s+if: github.event_name == 'workflow_dispatch' &&") 'Store credentials and publication must require explicit manual dispatch.'
        Assert ($workflow -match 'always\(\) && steps.operation.outputs.draft-id' -and
            $workflow -match 'name: store-draft-ownership-\$\{\{ steps.operation.outputs.draft-id \}\}') 'Failed runs must persist draft-specific ownership evidence.'
        Assert ($workflow -notmatch 'resume-draft|upload-listings-draft|confirm_listings_commit') 'Legacy stage still exposed.'
        $module = Get-Content -LiteralPath (Join-Path $root 'tools\StoreCli.psm1') -Raw
        Assert ($module -notmatch 'Invoke-RestMethod|Invoke-WebRequest|Put.?Blob|oauth2/token') 'Custom transport remains.'
        $invoke = Get-Content -LiteralPath (Join-Path $root 'tools\Invoke-StoreRelease.ps1') -Raw
        Assert ($invoke -match 'Receive-CiBuild' -and $invoke -match 'ShouldProcess' -and $invoke -match "refs/heads/main") 'Provenance or live gate missing.'
    }
    Write-Host "$script:passed offline Store CLI guard groups passed."
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
