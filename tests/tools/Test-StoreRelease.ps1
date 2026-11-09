$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Import-Module (Join-Path $root 'tools\StoreRelease.psm1') -Force
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
    Assert ($null -ne $message -and $message -match $Pattern) "Expected guard did not reject as intended. Observed: $message"
}
function New-Run {
    return [pscustomobject]@{
        id = 123; repository = @{ full_name = 'arisewanggithub/RedrainCast-Windows' }
        head_repository = @{ full_name = 'arisewanggithub/RedrainCast-Windows' }
        head_branch = 'main'; head_sha = ('a' * 40); run_number = 42; run_attempt = 2; status = 'completed'; conclusion = 'success'
        event = 'push'; path = '.github/workflows/windows.yml'
    }
}
function New-FixturePackage {
    param([string]$Path, [string]$Arch, [string]$Version = '1.2.3.0',
        [string]$Name = $identity.PackageName, [string]$Publisher = $identity.Publisher,
        [switch]$MissingWorker, [switch]$Duplicate, [switch]$Pdb,
        [string[]]$Languages = @('en-US'), [string[]]$ExtraFiles = @())
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path }
    $resourceXml = ($Languages | ForEach-Object { "<Resource Language=`"$_`"/>" }) -join ''
    $manifest = @"
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
<Identity Name="$Name" Publisher="$Publisher" ProcessorArchitecture="$Arch" Version="$Version"/>
<Resources>$resourceXml</Resources>
<Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22621.0"/></Dependencies>
<Applications><Application Id="App" Executable="RedrainCast.App.exe"/></Applications>
<Capabilities><Capability Name="internetClient"/></Capabilities>
</Package>
"@
    $archive = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = @('AppxManifest.xml', 'AppxBlockMap.xml', '[Content_Types].xml', 'resources.pri',
            'RedrainCast.App.pri', 'RedrainCast.App.exe', 'RedrainCast.App.dll', 'coreclr.dll',
            'capture-worker/RedrainCast.CaptureWorker.dll', 'capture-worker/coreclr.dll',
            'FFmpeg.AutoGen.dll', 'capture-worker/FFmpeg.AutoGen.dll', 'PRIVACY.md', 'THIRD_PARTY_NOTICES.txt') + $ExtraFiles
        if (-not $MissingWorker) { $files += 'capture-worker/RedrainCast.CaptureWorker.exe' }
        if ($Duplicate) { $files += 'AppxManifest.xml' }
        if ($Pdb) { $files += 'debug.pdb' }
        foreach ($file in $files) {
            $entry = $archive.CreateEntry($file)
            $writer = New-Object IO.StreamWriter($entry.Open())
            try { $writer.Write($(if ($file -ceq 'AppxManifest.xml') { $manifest } else { 'offline non-executable fixture' })) }
            finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
}
function New-Mock {
    $product = $identity.StoreId
    $listings = [ordered]@{}
    foreach ($locale in @('en-US', 'ar-SA', 'cs-CZ', 'da-DK', 'de-DE', 'el-GR', 'es-ES', 'fi-FI', 'fr-FR', 'he-IL',
        'hu-HU', 'it-IT', 'ja-JP', 'ko-KR', 'nl-NL', 'pl-PL', 'pt-BR', 'ro-RO', 'ru-RU', 'tr-TR', 'zh-CN', 'zh-TW')) {
        $listings[$locale] = @{ baseListing = @{ title = "Title $locale"; description = "Text $locale"
            privacyPolicy = 'https://example.invalid/privacy'; images = @(@{ fileName = "$locale.png"; fileStatus = 'Uploaded'; id = '20'; imageType = 'Screenshot' }) }
            platformOverrides = @{ Windows81 = @{ description = "Override $locale" } } }
    }
    $deep = @{ retained = 'deep value' }
    for ($i = 0; $i -lt 16; $i++) { $deep = @{ child = $deep } }
    $state = [pscustomobject]@{
        app = [pscustomobject]@{ id = $identity.StoreId; packageIdentityName = $identity.PackageName
            publisherName = $identity.Publisher; lastPublishedApplicationSubmission = @{ id = '50' }; pendingApplicationSubmission = $null }
        draft = [pscustomobject]@{ id = '60'; status = 'PendingCommit'; statusDetails = @{ errors = @(); warnings = @() }
            fileUploadUrl = 'https://fixture.blob.core.windows.net/ingestion/fixture?sig=fixture-SAS'
            targetPublishMode = 'Manual'; targetPublishDate = '1601-01-01T00:00:00Z'
            listings = $listings; pricing = @{ priceId = 'Tier0'; marketSpecificPricings = @{ US = 'Tier0' }; sales = @() }
            packageDeliveryOptions = @{ packageRollout = @{ isPackageRollout = $false; packageRolloutPercentage = 0 }; isMandatoryUpdate = $false }
            allowTargetFutureDeviceFamilies = @{ Desktop = $true; Mobile = $false }; unknownFutureMetadata = $deep
            applicationPackages = @([pscustomobject]@{ fileName = 'previous.msix'; fileStatus = 'Uploaded'; id = '15'; version = '1.0.0.0'; architecture = 'x64'; languages = @('en-US'); minimumSystemRam = 'None' }) }
        calls = New-Object 'Collections.Generic.List[string]'
        uploadCount = 0; commitCount = 0; failUpload = $false; failPost = $false; failCommit = $false
        modifyAfterUpload = $false; observedStatus = 'CommitStarted'; ingestImages = $false; modifyImageAfterUpload = $false
    }
    $request = {
        param($method, $path, $body, $inFile)
        $state.calls.Add($method + ' ' + $(if ($inFile) { 'blob (redacted)' } else { $path }))
        if ($inFile) {
            if ($state.failUpload) { throw 'Simulated ZIP upload failure.' }
            if ((Get-Item -LiteralPath $inFile).Length -le 0) { throw 'Upload requires a real fixture ZIP.' }
            $state.uploadCount++
            if ($state.modifyAfterUpload) { $state.draft.pricing.priceId = 'Tier9' }
            foreach ($listing in $state.draft.listings.PSObject.Properties) {
                foreach ($image in $listing.Value.baseListing.images) {
                    if ($image.fileStatus -ceq 'PendingUpload') {
                        if ($state.ingestImages) {
                            $image.fileStatus = 'Uploaded'
                            $image | Add-Member -NotePropertyName id -NotePropertyValue '200' -Force
                        }
                        if ($state.modifyImageAfterUpload) { $image.description = 'Unexpected server edit' }
                    }
                }
            }
            return
        }
        if ($method -ceq 'GET' -and $path -ceq "applications/$product") { return ConvertTo-Json -InputObject $state.app -Depth 100 | ConvertFrom-Json }
        if ($method -ceq 'POST' -and $path -ceq "applications/$product/submissions") {
            if ($state.failPost) { throw 'Simulated server-side pending-submission conflict.' }
            $state.app.pendingApplicationSubmission = @{ id = '60' }
            return ConvertTo-Json -InputObject $state.draft -Depth 100 | ConvertFrom-Json
        }
        if ($method -ceq 'PUT') {
            $state.draft = ConvertTo-Json -InputObject $body -Depth 100 | ConvertFrom-Json
            return ConvertTo-Json -InputObject $state.draft -Depth 100 | ConvertFrom-Json
        }
        if ($method -ceq 'POST' -and $path.EndsWith('/commit')) {
            if ($state.failCommit) { throw 'Simulated commit failure.' }
            $state.commitCount++; return @{}
        }
        if ($method -ceq 'GET' -and $path.EndsWith('/status')) { return @{ status = $state.observedStatus } }
        if ($method -ceq 'GET' -and $path.EndsWith('/60')) { return ConvertTo-Json -InputObject $state.draft -Depth 100 | ConvertFrom-Json }
        throw 'Unexpected mock request.'
    }.GetNewClosure()
    return [pscustomobject]@{ state = $state; request = $request }
}
$fixtures = Join-Path $PSScriptRoot ".store-release-fixtures-$PID"
if (Test-Path -LiteralPath $fixtures) { throw 'Fixture directory already exists.' }
New-Item -ItemType Directory -Path $fixtures | Out-Null
try {
    Test 'trusted completed main build' { Assert-StoreRun (New-Run) '123' '.github/workflows/windows.yml' }
    foreach ($field in @('head_branch', 'status', 'conclusion', 'event', 'path', 'head_sha', 'run_attempt', 'run_number')) {
        Test "reject untrusted run $field" {
            $run = New-Run; $run.$field = 'invalid'
            Reject { Assert-StoreRun $run '123' '.github/workflows/windows.yml' }
        }
    }
    Test 'reject PR, fork and wrong repository' {
        $run = New-Run; $run.event = 'pull_request'
        Reject { Assert-StoreRun $run '123' '.github/workflows/windows.yml' }
        $run = New-Run; $run.head_repository.full_name = 'fork/project'
        Reject { Assert-StoreRun $run '123' '.github/workflows/windows.yml' }
        $run = New-Run; $run.repository.full_name = 'other/project'
        Reject { Assert-StoreRun $run '123' '.github/workflows/windows.yml' }
    }
    Test 'reject shell-injection or mismatched run ID' {
        Reject { Assert-StoreRun (New-Run) '123; exit' '.github/workflows/windows.yml' }
        Reject { Assert-StoreRun (New-Run) '124' '.github/workflows/windows.yml' }
    }
    Test 'trusted run requires an exact positive run attempt' {
        foreach ($attempt in @($null, 0, -1, 1.5, '02', '2; exit')) {
            $run = New-Run; $run.run_attempt = $attempt
            Reject { Assert-StoreRun $run '123' '.github/workflows/windows.yml' }
        }
    }
    Test 'canonical JSON sorts keys and preserves arrays and deep Unicode values' {
        $unicode = [string][char]0x4e2d + [char]0x6587
        $a = [pscustomobject]@{ z = @($false, $null, 0, $unicode); a = @{ second = 2; first = 1 } }
        $b = [pscustomobject]@{ a = @{ first = 1; second = 2 }; z = @($false, $null, 0, $unicode) }
        Assert ((ConvertTo-StoreCanonicalJson $a) -ceq (ConvertTo-StoreCanonicalJson $b)) 'Canonical ordering differs.'
        $b.z = @(0, $null, $false, $unicode)
        Assert ((ConvertTo-StoreCanonicalJson $a) -cne (ConvertTo-StoreCanonicalJson $b)) 'Array order was lost.'
    }
    Test 'canonical JSON preserves empty Store objects under strict PowerShell 5' {
        Assert ((ConvertTo-StoreCanonicalJson ([pscustomobject]@{})) -ceq '{}') 'Empty object was not canonicalized.'
        Assert ((ConvertTo-StoreCanonicalJson ([pscustomobject]@{ platformOverrides = [pscustomobject]@{}; images = @() })) -ceq
            '{"images":[],"platformOverrides":{}}') 'Empty listing metadata was not preserved.'
    }
    foreach ($arch in @('arm64', 'x64')) {
        $folder = Join-Path $fixtures $arch; New-Item -ItemType Directory -Path $folder | Out-Null
        New-FixturePackage (Join-Path $folder 'package.msix') $arch
    }
    $archive = New-StorePackageArchive $fixtures $identity
    Test 'paired packages and a single two-entry ZIP' {
        Assert ($archive.packages.Count -eq 2) 'Missing paired architecture.'
        $zip = [IO.Compression.ZipFile]::OpenRead($archive.path)
        try { Assert ($zip.Entries.Count -eq 2) 'ZIP must contain only the two MSIX packages.' } finally { $zip.Dispose() }
    }
    Test 'SDK uppercase BCP47 resources preserve architecture metadata equivalence' {
        $languages = @('ar', 'de', 'en-US', 'es', 'fr', 'he', 'hi', 'id', 'it', 'ja', 'ko', 'ms',
            'nl', 'pl', 'pt-BR', 'ru', 'sv', 'th', 'tr', 'vi', 'zh-Hans', 'zh-Hant')
        $sdkPath = Join-Path $fixtures 'sdk-uppercase.msix'
        $mixedPath = Join-Path $fixtures 'sdk-mixed-case.msix'
        New-FixturePackage $sdkPath arm64 -Languages @($languages | ForEach-Object { $_.ToUpperInvariant() })
        New-FixturePackage $mixedPath x64 -Languages $languages
        $sdk = Read-StorePackage $sdkPath arm64 $identity
        $mixed = Read-StorePackage $mixedPath x64 $identity
        Assert ($sdk.manifestFingerprint -ceq $mixed.manifestFingerprint) 'Equivalent BCP47 casing must not reject SDK packages.'
        New-FixturePackage $mixedPath x64 -Languages @($languages | Where-Object { $_ -ne 'zh-Hant' })
        $missing = Read-StorePackage $mixedPath x64 $identity
        Assert ($sdk.manifestFingerprint -cne $missing.manifestFingerprint) 'A genuinely missing package language must remain a metadata mismatch.'
    }
    Test 'observed full-size ARM64 plus x64 package pair fits the archive bound' {
        # Read-only measurements of candidate-pcmfix-20261105-230350, not tiny ZIP fixture lengths.
        $pairBytes = [long]173985017 + [long]183646525
        Assert ($pairBytes -gt 256MB) 'Regression case must exceed the old broken limit.'
        Assert-StoreArchiveLength ($pairBytes + 1MB)
    }
    Test 'positive ZIP lengths up to exactly 1 GiB pass' {
        foreach ($length in @([long]1, [long](1GB - 1), [long]1GB)) { Assert-StoreArchiveLength $length }
    }
    Test 'ZIP lengths above 1 GiB fail before any Store call' {
        foreach ($length in @([long](1GB + 1), [long](5000MB), [long]::MaxValue)) {
            $mock = New-Mock
            Reject {
                Assert-StoreArchiveLength $length
                New-StoreDraft $identity $archive '123' ('a' * 40) $mock.request
            } '1 GiB'
            Assert ($mock.state.calls.Count -eq 0) 'Oversized archive reached Store.'
        }
    }
    Test 'empty or negative ZIP lengths fail before any Store call' {
        foreach ($length in @([long]0, [long]-1)) {
            $mock = New-Mock
            Reject {
                Assert-StoreArchiveLength $length
                New-StoreDraft $identity $archive '123' ('a' * 40) $mock.request
            } 'must not be empty'
            Assert ($mock.state.calls.Count -eq 0) 'Invalid archive reached Store.'
        }
    }
    Test 'wrong identity, publisher, architecture and version are rejected' {
        $file = Join-Path $fixtures 'invalid.msix'
        New-FixturePackage $file arm64 -Name 'wrong.product'
        Reject { Read-StorePackage $file arm64 $identity }
        New-FixturePackage $file arm64 -Publisher 'CN=wrong'
        Reject { Read-StorePackage $file arm64 $identity }
        New-FixturePackage $file x64
        Reject { Read-StorePackage $file arm64 $identity }
        New-FixturePackage $file arm64 -Version '1.2.3.1'
        Reject { Read-StorePackage $file arm64 $identity }
    }
    Test 'missing worker, duplicate entries and PDB are rejected' {
        $file = Join-Path $fixtures 'invalid.msix'
        New-FixturePackage $file arm64 -MissingWorker
        Reject { Read-StorePackage $file arm64 $identity }
        New-FixturePackage $file arm64 -Duplicate
        Reject { Read-StorePackage $file arm64 $identity }
        New-FixturePackage $file arm64 -Pdb
        Reject { Read-StorePackage $file arm64 $identity }
    }
    Test 'shipping AutoGen bindings are allowed but FFmpeg executables and lookalike DLLs remain forbidden' {
        $file = Join-Path $fixtures 'binding-check.msix'
        New-FixturePackage $file arm64
        $null = Read-StorePackage $file arm64 $identity
        foreach ($forbidden in @('ffmpeg.exe', 'capture-worker/ffprobe.exe', 'FFmpeg.Other.dll',
            'other/FFmpeg.AutoGen.dll', 'CaptureProbe.dll', 'RecordExport.exe')) {
            New-FixturePackage $file arm64 -ExtraFiles @($forbidden)
            Reject { Read-StorePackage $file arm64 $identity } 'forbidden standalone helpers'
        }
    }
    Test 'partial, duplicate and mismatched architecture artifacts are rejected' {
        $partial = Join-Path $fixtures 'partial'; New-Item -ItemType Directory -Path $partial | Out-Null
        Reject { New-StorePackageArchive $partial $identity }
        $extra = Join-Path $fixtures 'arm64\extra.msix'
        Copy-Item -LiteralPath (Join-Path $fixtures 'arm64\package.msix') -Destination $extra
        Reject { New-StorePackageArchive $fixtures $identity }
        Remove-Item -LiteralPath $extra
        New-FixturePackage (Join-Path $fixtures 'x64\package.msix') x64 -Version '2.0.0.0'
        Reject { New-StorePackageArchive $fixtures $identity } 'versions'
        New-FixturePackage (Join-Path $fixtures 'x64\package.msix') x64
    }
    Test 'diagnostics expose only allowlisted JSON codes, never messages or metadata' {
        $text = '{"error":{"code":"InvalidRequestBody","message":"fixture-token https://host/?sig=fixture-SAS","details":{"secret":"fixture-secret"}},"errors":[{"code":"InvalidParameter","message":"private listing"}]}'
        $safe = Get-StoreTransportDiagnostic $text
        Assert ($safe -match 'InvalidRequestBody' -and $safe -match 'InvalidParameter') 'Known codes were lost.'
        Assert ($safe -notmatch 'fixture-|https:|private listing|details') 'Response data leaked.'
        $safe = Get-StoreTransportDiagnostic '{"code":"fixture-secret","message":"fixture-token"}'
        Assert ($safe -match 'UnrecognizedServiceError' -and $safe -notmatch 'fixture-') 'Unknown code escaped.'
    }
    Test 'diagnostics handle Azure XML without echoing SAS, messages or headers' {
        $safe = Get-StoreTransportDiagnostic '<Error><Code>AuthenticationFailed</Code><Message>fixture-SAS fixture-token</Message></Error>'
        Assert ($safe -match 'AuthenticationFailed' -and $safe -notmatch 'fixture-') 'Unsafe Azure diagnostic.'
    }
    Test 'diagnostics bound and suppress malformed, oversized and entity-bearing responses' {
        foreach ($text in @('fixture-secret', '{"code":', ('x' * 16385),
            '<!DOCTYPE Error [<!ENTITY leak SYSTEM "file:///fixture-secret">]><Error><Code>&leak;</Code></Error>')) {
            $safe = Get-StoreTransportDiagnostic $text
            Assert ($safe -ceq 'Service details unavailable or suppressed.') 'Unsafe malformed diagnostic.'
        }
        $codes = @(Get-StoreSafeErrors @((1..30 | ForEach-Object { [pscustomobject]@{ code = 'InvalidRequest' } })))
        Assert ($codes.Count -eq 1) 'Error codes were not deduplicated.'
    }
    Test 'exact draft inspection performs only two GETs and never claims ownership' {
        $mock = New-Mock
        $mock.state.app.pendingApplicationSubmission = @{ id = '60' }
        $mock.state.draft.applicationPackages[0].fileName = 'RedrainCast-arm64.msix'
        $result = Get-StoreDraftInspection $identity '60' $mock.request
        Assert ($result.submissionId -ceq '60' -and $result.status -ceq 'PendingCommit' -and
            $result.targetPublishMode -ceq 'Manual' -and $result.ownership -ceq 'Unknown') 'Incorrect draft projection.'
        Assert ($result.applicationPackages[0].fileName -ceq 'RedrainCast-arm64.msix' -and
            $result.applicationPackages[0].version -ceq '1.0.0.0') 'Package projection lost safe metadata.'
        Assert (($mock.state.calls -join ',') -ceq 'GET applications/9P1153MP6R32,GET applications/9P1153MP6R32/submissions/60') 'Inspection made unexpected requests.'
        Assert ((ConvertTo-Json $result -Depth 5) -notmatch 'fixture-|pricing|listings|fileUploadUrl') 'Inspection leaked metadata.'
    }
    Test 'inspection rejects mismatched pointers, identities and response IDs without writes' {
        $mock = New-Mock
        Reject { Get-StoreDraftInspection $identity '60' $mock.request } 'current pending'
        Assert ($mock.state.calls.Count -eq 1) 'Absent pointer fetched a draft.'
        $mock = New-Mock; $mock.state.app.pendingApplicationSubmission = @{ id = '90' }
        Reject { Get-StoreDraftInspection $identity '60' $mock.request } 'current pending'
        $mock.state.app.id = 'wrong'
        Reject { Get-StoreDraftInspection $identity '90' $mock.request } 'does not match'
        $mock = New-Mock; $mock.state.app.pendingApplicationSubmission = @{ id = '60' }
        $mock.state.draft.id = '61'
        Reject { Get-StoreDraftInspection $identity '60' $mock.request } 'ID mismatch'
        Reject { Get-StoreDraftInspection $identity 'fixture-secret' $mock.request } 'numeric'
        Assert (@($mock.state.calls | Where-Object { -not $_.StartsWith('GET ') }).Count -eq 0) 'Inspection mutated Store.'
    }
    Test 'inspection suppresses unsafe status, mode, package and validation values' {
        $mock = New-Mock; $mock.state.app.pendingApplicationSubmission = @{ id = '60' }
        $mock.state.draft.status = 'fixture-token'
        $mock.state.draft.targetPublishMode = 'fixture-secret'
        foreach ($field in @('fileName', 'fileStatus', 'version', 'architecture')) {
            $mock.state.draft.applicationPackages[0].$field = 'https://host/?sig=fixture-SAS'
        }
        $mock.state.draft.statusDetails.errors = @(@{ code = 'InvalidRequest'; message = 'fixture-secret' },
            @{ code = 'fixture-token' })
        $result = Get-StoreDraftInspection $identity '60' $mock.request
        $json = ConvertTo-Json $result -Depth 5
        Assert ($json -notmatch 'fixture-|https:|message' -and $json -match 'InvalidRequest' -and
            $json -match 'UnknownOrSuppressed') 'Unsafe inspection output.'
    }
    Test 'read-only preflight reports readiness for a published product without mutations' {
        $mock = New-Mock
        $ready = Get-StoreReadiness $identity $mock.request
        Assert ($ready.firstPublicationComplete -and -not $ready.pendingDraftExists -and $ready.automatedUpdateAllowed) 'Published readiness incorrect.'
        Assert ($mock.state.calls.Count -eq 1 -and $mock.state.calls[0].StartsWith('GET ')) 'Preflight mutated Store.'
    }
    Test 'read-only preflight permits unpublished eligibility checks and preserves human drafts' {
        $mock = New-Mock
        $mock.state.app.lastPublishedApplicationSubmission = $null
        $mock.state.app.pendingApplicationSubmission = @{ id = '90' }
        $ready = Get-StoreReadiness $identity $mock.request
        Assert (-not $ready.firstPublicationComplete -and $ready.pendingDraftExists -and -not $ready.automatedUpdateAllowed) 'Initial-draft readiness incorrect.'
        Assert ($mock.state.calls.Count -eq 1) 'Preflight performed more than one GET.'
    }
    Test 'read-only preflight refuses wrong identities and malformed submission pointers' {
        $mock = New-Mock; $mock.state.app.id = 'wrong'
        Reject { Get-StoreReadiness $identity $mock.request } 'does not match'
        $mock = New-Mock; $mock.state.app.lastPublishedApplicationSubmission.id = 'invalid'
        Reject { Get-StoreReadiness $identity $mock.request } 'published submission pointer'
        $mock = New-Mock; $mock.state.app.pendingApplicationSubmission = @{ id = 'invalid' }
        Reject { Get-StoreReadiness $identity $mock.request } 'pending submission pointer'
    }
    Test 'first publication has no mutation' {
        $mock = New-Mock; $mock.state.app.lastPublishedApplicationSubmission = $null
        Reject { New-StoreDraft $identity $archive '123' ('a' * 40) $mock.request } 'First publication'
        Assert ($mock.state.calls.Count -eq 1 -and $mock.state.calls[0].StartsWith('GET ')) 'First publication mutated Store.'
    }
    Test 'missing optional published field still has the first-publication gate' {
        $mock = New-Mock; $mock.state.app.PSObject.Properties.Remove('lastPublishedApplicationSubmission')
        Reject { New-StoreDraft $identity $archive '123' ('a' * 40) $mock.request } 'First publication'
        Assert ($mock.state.calls.Count -eq 1) 'Missing published field caused mutation.'
    }
    Test 'existing human draft has no mutation' {
        $mock = New-Mock; $mock.state.app.pendingApplicationSubmission = @{ id = '90' }
        Reject { New-StoreDraft $identity $archive '123' ('a' * 40) $mock.request } 'pending submission'
        Assert ($mock.state.calls.Count -eq 1 -and $mock.state.calls[0].StartsWith('GET ')) 'Human draft was touched.'
    }
    Test 'wrong Store product has no mutation' {
        $mock = New-Mock; $mock.state.app.id = 'wrong'
        Reject { New-StoreDraft $identity $archive '123' ('a' * 40) $mock.request } 'does not match'
        Assert ($mock.state.calls.Count -eq 1) 'Wrong product was mutated.'
    }
    Test 'server conflict stops without deletion or PUT' {
        $mock = New-Mock; $mock.state.failPost = $true
        Reject { New-StoreDraft $identity $archive '123' ('a' * 40) $mock.request } 'conflict'
        Assert ($mock.state.calls.Count -eq 2 -and $mock.state.uploadCount -eq 0) 'Conflict continued into upload.'
    }
    $positive = New-Mock
    $before = Copy-Json $positive.state.draft
    $receipt = New-StoreDraft $identity $archive '123' ('a' * 40) $positive.request
    Test 'preserve all 22 listings, images, privacy, pricing, delivery and unknown deep metadata' {
        foreach ($field in @('listings', 'pricing', 'packageDeliveryOptions', 'allowTargetFutureDeviceFamilies', 'unknownFutureMetadata')) {
            Assert ((ConvertTo-StoreCanonicalJson $before.$field) -ceq (ConvertTo-StoreCanonicalJson $positive.state.draft.$field)) "Metadata changed: $field."
        }
        Assert (@($positive.state.draft.listings.PSObject.Properties).Count -eq 22) 'Listings were lost.'
        Assert ($positive.state.draft.targetPublishMode -ceq 'Immediate' -and $receipt.targetPublishMode -ceq 'Immediate') 'Automatic publication mode missing.'
        Assert ($positive.state.draft.applicationPackages[0].fileStatus -ceq 'PendingDelete') 'Old package was not marked for replacement.'
        Assert ($positive.state.uploadCount -eq 1 -and $positive.state.commitCount -eq 0) 'Upload stage committed or uploaded more than once.'
        Assert (@($positive.state.calls | Where-Object { $_.StartsWith('DELETE ') }).Count -eq 0) 'Deletion request exists.'
    }
    Test 'receipt contains no SAS, token, full metadata or contact information' {
        $json = ConvertTo-Json -InputObject $receipt -Depth 100
        Assert ($json -notmatch 'fileUploadUrl|fixture-SAS|Authorization|access_token|privacyPolicy|listings|pricing') 'Receipt contains sensitive or full metadata.'
    }
    Test 'upload failure leaves API-created draft without commit or deletion' {
        $mock = New-Mock; $mock.state.failUpload = $true
        Reject { New-StoreDraft $identity $archive '123' ('a' * 40) $mock.request } 'upload failure'
        Assert ($mock.state.app.pendingApplicationSubmission.id -ceq '60' -and $mock.state.commitCount -eq 0) 'Failed upload draft was not preserved.'
        Assert (@($mock.state.calls | Where-Object { $_.StartsWith('DELETE ') }).Count -eq 0) 'Failed upload draft was deleted.'
    }
    Test 'invalid SAS is redacted and prevents upload' {
        $mock = New-Mock; $mock.state.draft.fileUploadUrl = 'invalid fixture-SAS'
        Reject { New-StoreDraft $identity $archive '123' ('a' * 40) $mock.request } 'unexpected upload'
        Assert ($mock.state.uploadCount -eq 0 -and $mock.state.commitCount -eq 0) 'Invalid upload URL was used.'
    }
    Test 'server metadata change refuses to produce an upload receipt' {
        $mock = New-Mock; $mock.state.modifyAfterUpload = $true
        Reject { New-StoreDraft $identity $archive '123' ('a' * 40) $mock.request } 'metadata differs'
        Assert ($mock.state.commitCount -eq 0) 'Server metadata mismatch committed.'
    }
    Test 'receipt rejects wrong submission, build SHA, app and packages' {
        foreach ($field in @('submissionId', 'buildHeadSha', 'applicationId')) {
            $bad = Copy-Json $receipt; $bad.$field = 'wrong'
            Reject { Submit-StoreDraft $identity $bad $archive.packages '123' ('a' * 40) $positive.request }
        }
        $bad = Copy-Json $receipt; $bad.packages[0].sha256 = ('b' * 64)
        Reject { Submit-StoreDraft $identity $bad $archive.packages '123' ('a' * 40) $positive.request } 'packages'
        Assert ($positive.state.commitCount -eq 0) 'Invalid receipt committed.'
    }
    Test 'receipt rejects manual or scheduled publication before Store access' {
        $calls = $positive.state.calls.Count
        foreach ($mode in @('Manual', 'SpecificDate')) {
            $bad = Copy-Json $receipt; $bad.targetPublishMode = $mode
            Reject { Submit-StoreDraft $identity $bad $archive.packages '123' ('a' * 40) $positive.request } 'Receipt identity'
        }
        Assert ($positive.state.calls.Count -eq $calls -and $positive.state.commitCount -eq 0) 'Changed receipt publication mode reached Store.'
    }
    Test 'pending ID and last published ID changes reject commit' {
        $positive.state.app.pendingApplicationSubmission.id = '61'
        Reject { Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request } 'no longer matches'
        $positive.state.app.pendingApplicationSubmission.id = '60'
        $positive.state.app.lastPublishedApplicationSubmission.id = '51'
        Reject { Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request } 'no longer matches'
        $positive.state.app.lastPublishedApplicationSubmission.id = '50'
    }
    Test 'human listing or pricing changes reject commit' {
        $positive.state.draft.listings.'en-US'.baseListing.title = 'Human edit'
        Reject { Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request } 'metadata changed'
        $positive.state.draft.listings = Copy-Json $before.listings
        $positive.state.draft.pricing.priceId = 'Tier9'
        Reject { Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request } 'metadata changed'
        $positive.state.draft.pricing = Copy-Json $before.pricing
        Assert ($positive.state.commitCount -eq 0) 'Human-edited metadata committed.'
    }
    Test 'package names, old package metadata and publication mode changes reject commit' {
        $saved = Copy-Json $positive.state.draft
        $positive.state.draft.applicationPackages[1].fileName = 'different.msix'
        Reject { Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request }
        $positive.state.draft = Copy-Json $saved
        $positive.state.draft.applicationPackages[0].version = '99.0.0.0'
        Reject { Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request }
        $positive.state.draft = Copy-Json $saved
        $positive.state.draft.applicationPackages[1].minimumSystemRam = 'Memory2GB'
        Reject { Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request } 'package metadata changed'
        $positive.state.draft = Copy-Json $saved
        foreach ($mode in @('Manual', 'SpecificDate')) {
            $positive.state.draft.targetPublishMode = $mode
            Reject { Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request } 'metadata changed'
        }
        $positive.state.draft = Copy-Json $saved
    }
    Test 'normal PendingUpload to Uploaded state transition preserves package binding' {
        $saved = Copy-Json $positive.state.draft
        foreach ($package in $positive.state.draft.applicationPackages) {
            if ($package.fileStatus -ceq 'PendingUpload') { $package.fileStatus = 'Uploaded' }
        }
        Assert ((Get-StoreActivePackageFingerprint $positive.state.draft) -ceq $receipt.activePackagesFingerprint) 'Upload state transition changed package binding.'
        Assert-StorePackages $positive.state.draft $archive.packages $receipt.deletedPackagesFingerprint
        $positive.state.draft = Copy-Json $saved
    }
    Test 'commit failure is not reported as success' {
        $positive.state.failCommit = $true
        Reject { Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request } 'commit failure'
        $positive.state.failCommit = $false
    }
    Test 'exact owned receipt commits once with automatic publication after certification' {
        $status = Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request
        Assert ($status -ceq 'CommitStarted' -and $positive.state.commitCount -eq 1) 'Exact draft did not commit.'
        Assert ($positive.state.draft.targetPublishMode -ceq 'Immediate') 'Commit changed automatic publication mode.'
        Assert (@($positive.state.calls | Where-Object { $_.StartsWith('DELETE ') }).Count -eq 0) 'Unexpected deletion.'
    }
    Test 'failed and unknown processing states are not reported as success' {
        $positive.state.observedStatus = 'CommitFailed'
        Reject { Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request } 'processing failure'
        $positive.state.observedStatus = 'unexpected'
        Reject { Submit-StoreDraft $identity $receipt $archive.packages '123' ('a' * 40) $positive.request } 'Unrecognized'
    }
    Test 'listing approval requires the exact reviewed commit and a workflow run ID' {
        Assert-StoreListingApproval ('a' * 40) ('a' * 40) '456'
        Reject { Assert-StoreListingApproval ('b' * 40) ('a' * 40) '456' } 'reviewed'
        Reject { Assert-StoreListingApproval '' ('a' * 40) '456' } 'reviewed'
        Reject { Assert-StoreListingApproval ('a' * 40) ('a' * 40) '456; exit' } 'reviewed'
    }
    $listingUpdate = Read-StoreListings $root ('a' * 40) '456'
    Test 'listing locale keys match the actual Partner Center package-language export' {
        $expected = @('ar','de','en-us','es','fr','hi','id','it','ja','ko','ms','nl','pl','pt-br','ru','sv','th','tr','uk','vi','zh-hans','zh-hant')
        Assert ((ConvertTo-StoreCanonicalJson @($listingUpdate.entries.locale)) -ceq
            (ConvertTo-StoreCanonicalJson $expected)) 'Listing updates would create region-specific duplicates of the package languages.'
    }
    Test 'all actual 22 UTF-8 drafts and 44 native PNG screenshots validate' {
        Assert ($listingUpdate.entries.Count -eq 22 -and $listingUpdate.images.Count -eq 44) 'Actual listing inputs are incomplete.'
        Assert ($listingUpdate.contentFingerprint -match '^[a-f0-9]{64}$') 'Listing content fingerprint missing.'
        $chinese = @($listingUpdate.entries | Where-Object { $_.locale -ceq 'zh-hans' })[0]
        Assert ($chinese.fields.description -match '[^\x00-\x7f]') 'Unicode listing was not read as UTF-8.'
        Assert ($chinese.images[0].imageType -ceq 'Screenshot') 'Desktop screenshot API type is incorrect.'
        Assert ($chinese.fields.PSObject.Properties.Name -cnotcontains 'submissionReview') 'Internal review metadata was mapped.'
        Assert ($chinese.fields.PSObject.Properties.Name -cnotcontains 'releaseNotes') 'Empty optional notes would erase existing content.'
        foreach ($field in @('privacyPolicy', 'supportContact', 'websiteUrl')) {
            Assert ($chinese.fields.PSObject.Properties.Name -cnotcontains $field) 'Obsolete Properties URL was mapped.'
        }
    }
    $listingWork = Join-Path $fixtures 'listing-archive'
    foreach ($arch in @('arm64', 'x64')) {
        $folder = Join-Path $listingWork $arch
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $fixtures "$arch\package.msix") -Destination $folder
    }
    $listingArchive = New-StorePackageArchive $listingWork $identity $listingUpdate
    Test 'listing upload ZIP contains exactly two packages and 44 hash-matched images' {
        $zip = [IO.Compression.ZipFile]::OpenRead($listingArchive.path)
        try {
            Assert ($zip.Entries.Count -eq 46) 'Listing ZIP contents are incomplete.'
            foreach ($image in $listingUpdate.images) {
                Assert ($null -ne $zip.GetEntry($image.fileName)) 'Listing screenshot ZIP entry is missing.'
            }
        } finally { $zip.Dispose() }
    }
    $listingMock = New-Mock
    $listingMock.state.ingestImages = $true
    $listingMock.state.draft = Copy-Json $listingMock.state.draft
    $base = $listingMock.state.draft.listings.'en-US'.baseListing
    $base | Add-Member -NotePropertyName releaseNotes -NotePropertyValue 'Previous release notes'
    $base | Add-Member -NotePropertyName licenseTerms -NotePropertyValue 'Existing legal terms'
    $base.images += @(
        [pscustomobject]@{ fileName = 'existing-icon.png'; fileStatus = 'Uploaded'; id = '21'; imageType = 'Icon' },
        [pscustomobject]@{ fileName = 'existing-mobile.png'; fileStatus = 'Uploaded'; id = '22'; imageType = 'MobileScreenshot' }
    )
    $listingBefore = Copy-Json $listingMock.state.draft
    $listingReceipt = New-StoreDraft $identity $listingArchive '123' ('b' * 40) $listingMock.request
    Test 'listing upload changes only selected base fields and desktop screenshots' {
        $saved = $listingMock.state.draft
        foreach ($field in @('pricing', 'packageDeliveryOptions', 'allowTargetFutureDeviceFamilies', 'unknownFutureMetadata')) {
            Assert ((ConvertTo-StoreCanonicalJson $saved.$field) -ceq (ConvertTo-StoreCanonicalJson $listingBefore.$field)) "Listing upload changed unrelated $field."
        }
        Assert ((ConvertTo-StoreCanonicalJson $saved.listings.'cs-CZ') -ceq
            (ConvertTo-StoreCanonicalJson $listingBefore.listings.'cs-CZ')) 'An untouched language changed.'
        Assert ((ConvertTo-StoreCanonicalJson $saved.listings.'en-US'.platformOverrides) -ceq
            (ConvertTo-StoreCanonicalJson $listingBefore.listings.'en-US'.platformOverrides)) 'Platform overrides changed.'
        $base = $saved.listings.'en-US'.baseListing
        Assert ($base.title -ceq 'RedrainCast' -and $base.releaseNotes -ceq 'Previous release notes' -and
            $base.licenseTerms -ceq 'Existing legal terms' -and $base.privacyPolicy -ceq 'https://example.invalid/privacy') 'Blank optional fields or product properties changed.'
        Assert ($base.images[0].fileStatus -ceq 'PendingDelete' -and $base.images[1].fileStatus -ceq 'Uploaded' -and
            $base.images[2].fileStatus -ceq 'Uploaded') 'Image replacement did not preserve icons and other families.'
        Assert (@($base.images | Where-Object { $_.fileName -like 'listing-*' }).Count -eq 2) 'Desktop image replacement is incomplete.'
        Assert ($listingMock.state.uploadCount -eq 1 -and $listingMock.state.commitCount -eq 0) 'Listing upload committed or used multiple ZIP uploads.'
    }
    Test 'listing receipt binds reviewed source, upload run and image hashes without text or URLs' {
        Assert ($listingReceipt.schemaVersion -eq 2 -and $listingReceipt.listingUpdate.sourceSha -ceq ('a' * 40) -and
            $listingReceipt.buildHeadSha -ceq ('b' * 40) -and $listingReceipt.listingUpdate.uploadRunId -ceq '456') 'Independent build and listing source binding missing.'
        Assert ($listingReceipt.listingUpdate.contentFingerprint -ceq $listingUpdate.contentFingerprint) 'Content review fingerprint missing.'
        $json = ConvertTo-Json -InputObject $listingReceipt -Depth 100
        Assert ($json -notmatch 'fixture-SAS|fileUploadUrl|privacyPolicy|description|caption|https:|submissionReview') 'Listing receipt contains full text or secret URLs.'
    }
    Test 'normal image ingestion and server-assigned IDs preserve fingerprint without mutating inputs' {
        $saved = Copy-Json $listingMock.state.draft
        $beforeFingerprint = Get-StoreMetadataFingerprint $saved @($listingReceipt.listingUpdate.images.fileName)
        $saved.listings.'en-US'.baseListing.images[-1].id = '201'
        $saved.listings.'en-US'.baseListing.images[-1].fileStatus = 'PendingUpload'
        Assert ((Get-StoreMetadataFingerprint $saved @($listingReceipt.listingUpdate.images.fileName)) -ceq $beforeFingerprint) 'Image ingestion changed binding.'
        Assert ($saved.listings.'en-US'.baseListing.images[-1].id -ceq '201') 'Fingerprint normalization mutated input.'
        Assert-StoreListingImages $saved $listingReceipt.listingUpdate.images
    }
    Test 'listing caption edits reject commit before any certification request' {
        $saved = Copy-Json $listingMock.state.draft
        $listingMock.state.draft.listings.'en-US'.baseListing.images[-1].description = 'Human edit'
        Reject { Submit-StoreDraft $identity $listingReceipt $listingArchive.packages '123' ('b' * 40) $listingMock.request } 'metadata changed'
        Assert ($listingMock.state.commitCount -eq 0) 'Changed caption committed.'
        $listingMock.state.draft = $saved
    }
    Test 'invalid image ingestion states and missing screenshots reject commit' {
        foreach ($state in @('None', 'PendingDelete', 'Unexpected')) {
            $saved = Copy-Json $listingMock.state.draft
            $listingMock.state.draft.listings.'en-US'.baseListing.images[-1].fileStatus = $state
            Reject { Submit-StoreDraft $identity $listingReceipt $listingArchive.packages '123' ('b' * 40) $listingMock.request } 'ingestion state'
            $listingMock.state.draft = $saved
        }
        $saved = Copy-Json $listingMock.state.draft
        $listingMock.state.draft.listings.'en-US'.baseListing.images = @($listingMock.state.draft.listings.'en-US'.baseListing.images | Select-Object -SkipLast 1)
        Reject { Submit-StoreDraft $identity $listingReceipt $listingArchive.packages '123' ('b' * 40) $listingMock.request } 'metadata changed'
        $listingMock.state.draft = $saved
        Assert ($listingMock.state.commitCount -eq 0) 'Missing or invalid image committed.'
    }
    Test 'malformed listing receipt hashes, duplicate images and missing languages reject before Store access' {
        $calls = $listingMock.state.calls.Count
        $bad = Copy-Json $listingReceipt; $bad.listingUpdate.contentFingerprint = 'invalid'
        Reject { Submit-StoreDraft $identity $bad $listingArchive.packages '123' ('b' * 40) $listingMock.request } 'content binding'
        $bad = Copy-Json $listingReceipt; $bad.listingUpdate.images[0].sha256 = 'invalid'
        Reject { Submit-StoreDraft $identity $bad $listingArchive.packages '123' ('b' * 40) $listingMock.request } 'screenshot binding'
        $bad = Copy-Json $listingReceipt; $bad.listingUpdate.images[0] = $bad.listingUpdate.images[1]
        Reject { Submit-StoreDraft $identity $bad $listingArchive.packages '123' ('b' * 40) $listingMock.request } 'screenshot binding'
        $bad = Copy-Json $listingReceipt; $bad.listingUpdate.images = @($bad.listingUpdate.images | Select-Object -Skip 1)
        Reject { Submit-StoreDraft $identity $bad $listingArchive.packages '123' ('b' * 40) $listingMock.request } 'content binding'
        Assert ($listingMock.state.calls.Count -eq $calls) 'Malformed listing receipt reached Store.'
    }
    Test 'exact listing receipt commits once with automatic publication after certification' {
        $status = Submit-StoreDraft $identity $listingReceipt $listingArchive.packages '123' ('b' * 40) $listingMock.request
        Assert ($status -ceq 'CommitStarted' -and $listingMock.state.commitCount -eq 1 -and
            $listingMock.state.draft.targetPublishMode -ceq 'Immediate') 'Listing draft did not commit with automatic publication.'
    }
    Test 'listing uploads retain first-publication and human-draft refusal' {
        foreach ($blocked in @('unpublished', 'pending')) {
            $mock = New-Mock
            if ($blocked -ceq 'unpublished') { $mock.state.app.lastPublishedApplicationSubmission = $null }
            else { $mock.state.app.pendingApplicationSubmission = @{ id = '90' } }
            Reject { New-StoreDraft $identity $listingArchive '123' ('b' * 40) $mock.request } 'First publication|pending submission'
            Assert ($mock.state.calls.Count -eq 1 -and $mock.state.uploadCount -eq 0) 'Blocked listing upload mutated Store.'
        }
    }
    Test 'server caption changes refuse to produce a listing receipt' {
        $mock = New-Mock; $mock.state.modifyImageAfterUpload = $true
        Reject { New-StoreDraft $identity $listingArchive '123' ('b' * 40) $mock.request } 'metadata differs'
        Assert ($mock.state.commitCount -eq 0) 'Server caption change committed.'
    }
    $listingFixtures = Join-Path $fixtures 'listing-inputs'
    $draftFolder = Join-Path $listingFixtures 'docs\store\listings'
    New-Item -ItemType Directory -Path $draftFolder -Force | Out-Null
    Copy-Item -Path (Join-Path $root 'docs\store\listings\*.json') -Destination $draftFolder
    $screenshots = Join-Path $listingFixtures 'assets\store\screenshots'
    New-Item -ItemType Directory -Path (Split-Path -Parent $screenshots) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'assets\store\screenshots') -Destination $screenshots -Recurse
    $fixtureDraftPath = Join-Path $draftFolder 'en-US.json'
    $originalDraft = Get-Content -LiteralPath $fixtureDraftPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Test 'invalid locale, brand, text limits, arrays and unknown fields fail locally' {
        foreach ($case in @('locale', 'productName', 'description', 'keywords', 'productFeatures', 'unknown', 'required')) {
            $bad = Copy-Json $originalDraft
            switch ($case) {
                locale { $bad.locale = 'en' }
                productName { $bad.productName = 'Other app' }
                description { $bad.description = 'x' * 10001 }
                keywords { $bad.keywords = @('x' * 41) }
                productFeatures { $bad.productFeatures = @() }
                unknown { $bad | Add-Member -NotePropertyName typo -NotePropertyValue 'unexpected' }
                required { $bad.description = '' }
            }
            [IO.File]::WriteAllText($fixtureDraftPath, (ConvertTo-Json -InputObject $bad -Depth 100), (New-Object Text.UTF8Encoding($false)))
            Reject { Read-StoreListings $listingFixtures ('a' * 40) '456' } 'Listing|listing|draft'
        }
        [IO.File]::WriteAllText($fixtureDraftPath, (ConvertTo-Json -InputObject $originalDraft -Depth 100), (New-Object Text.UTF8Encoding($false)))
    }
    Test 'traversal, cross-language screenshots, unsupported family and overlong captions fail locally' {
        foreach ($case in @('path', 'locale', 'family', 'caption', 'missing')) {
            $bad = Copy-Json $originalDraft
            switch ($case) {
                path { $bad.screenshots[0].file = '../../../../outside.png' }
                locale { $bad.screenshots[0].file = '../../../assets/store/screenshots/de/01-home.png' }
                family { $bad.screenshots[0].deviceFamily = 'Mobile' }
                caption { $bad.screenshots[0].caption = 'x' * 201 }
                missing { $bad.screenshots = @($bad.screenshots[0]) }
            }
            [IO.File]::WriteAllText($fixtureDraftPath, (ConvertTo-Json -InputObject $bad -Depth 100), (New-Object Text.UTF8Encoding($false)))
            Reject { Read-StoreListings $listingFixtures ('a' * 40) '456' } 'Screenshot|screenshots'
        }
        [IO.File]::WriteAllText($fixtureDraftPath, (ConvertTo-Json -InputObject $originalDraft -Depth 100), (New-Object Text.UTF8Encoding($false)))
    }
    Test 'missing or additional listing drafts fail locally' {
        $extra = Join-Path $draftFolder 'extra.json'
        Copy-Item -LiteralPath $fixtureDraftPath -Destination $extra
        Reject { Read-StoreListings $listingFixtures ('a' * 40) '456' } 'Exactly 22'
        Remove-Item -LiteralPath $extra
        Remove-Item -LiteralPath $fixtureDraftPath
        Reject { Read-StoreListings $listingFixtures ('a' * 40) '456' } 'Exactly 22'
        [IO.File]::WriteAllText($fixtureDraftPath, (ConvertTo-Json -InputObject $originalDraft -Depth 100), (New-Object Text.UTF8Encoding($false)))
    }
    Test 'corrupt, undersized and oversized screenshots fail locally' {
        $path = Join-Path $screenshots 'en-US\01-home.png'
        $original = [IO.File]::ReadAllBytes($path)
        try {
            [IO.File]::WriteAllBytes($path, [byte[]]@(0, 1, 2))
            Reject { Read-StoreListings $listingFixtures ('a' * 40) '456' }
            $small = New-Object Drawing.Bitmap(10, 10)
            try { $small.Save($path, [Drawing.Imaging.ImageFormat]::Png) } finally { $small.Dispose() }
            Reject { Read-StoreListings $listingFixtures ('a' * 40) '456' } 'dimensions'
            $stream = [IO.File]::OpenWrite($path)
            try { $stream.SetLength(50MB) } finally { $stream.Dispose() }
            Reject { Read-StoreListings $listingFixtures ('a' * 40) '456' } 'size'
        } finally { [IO.File]::WriteAllBytes($path, $original) }
    }
    Test 'changed screenshot bytes fail after ZIP creation but before Store authentication' {
        $inputs = Read-StoreListings $listingFixtures ('a' * 40) '456'
        $path = Join-Path $screenshots 'en-US\01-home.png'
        $original = [IO.File]::ReadAllBytes($path)
        $changedWork = Join-Path $fixtures 'changed-image-archive'
        foreach ($arch in @('arm64', 'x64')) {
            $folder = Join-Path $changedWork $arch
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $fixtures "$arch\package.msix") -Destination $folder
        }
        try {
            Copy-Item -LiteralPath (Join-Path $screenshots 'en-US\02-sharing-settings.png') -Destination $path -Force
            Reject { New-StorePackageArchive $changedWork $identity $inputs } 'bytes changed'
        } finally { [IO.File]::WriteAllBytes($path, $original) }
    }
    Test 'transport identifies metadata PUT versus Azure PUT with sanitized HTTP details' {
        & (Get-Module StoreRelease) {
            function Invoke-RestMethod {
                $exception = New-Object Exception('fixture-secret')
                $exception | Add-Member -NotePropertyName Response -NotePropertyValue ([pscustomobject]@{ StatusCode = 400 })
                $record = New-Object Management.Automation.ErrorRecord($exception, 'fixture-token',
                    ([Management.Automation.ErrorCategory]::InvalidOperation), $null)
                $record.ErrorDetails = New-Object Management.Automation.ErrorDetails('{"error":{"code":"InvalidRequestBody","message":"fixture-SAS private metadata"}}')
                throw $record
            }
            function Invoke-WebRequest {
                $response = [pscustomobject]@{ StatusCode = 403 }
                $response | Add-Member -MemberType ScriptMethod -Name GetResponseStream -Value {
                    return New-Object IO.MemoryStream(,[Text.Encoding]::UTF8.GetBytes('<Error><Code>AuthenticationFailed</Code><Message>fixture-SAS fixture-token</Message></Error>'))
                }
                $exception = New-Object Exception('fixture-secret')
                $exception | Add-Member -NotePropertyName Response -NotePropertyValue $response
                throw $exception
            }
            $message = ''
            try { Invoke-StoreHttp PUT 'https://manage.devcenter.microsoft.com/v1.0/my/applications/9P1153MP6R32/submissions/60' @{} '{}' }
            catch { $message = $_.Exception.Message }
            if ($message -notmatch 'Store submission metadata, PUT, HTTP 400' -or
                $message -notmatch 'InvalidRequestBody' -or $message -match 'fixture-|private metadata|https:') {
                throw "Unsafe or missing metadata transport diagnostic: $message"
            }
            $message = ''
            try { Invoke-StoreHttp PUT 'https://fixture.blob.core.windows.net/path?sig=fixture-SAS' @{} $null 'ignored.zip' }
            catch { $message = $_.Exception.Message }
            if ($message -notmatch 'Azure archive upload, PUT, HTTP 403' -or
                $message -notmatch 'AuthenticationFailed' -or $message -match 'fixture-|https:') {
                throw "Unsafe or missing Azure transport diagnostic: $message"
            }
        }
    }
    Test 'transport errors redact response bodies and upload URL' {
        $module = Get-Module StoreRelease
        & $module {
            function Invoke-WebRequest { throw 'fixture-SAS fixture-token fixture-secret' }
            $message = $null
            try { Invoke-StoreHttp PUT 'https://fixture.blob.core.windows.net/ingestion?sig=fixture-SAS' @{} $null 'fixture' }
            catch { $message = $_.Exception.Message }
            if ($null -eq $message -or $message -match 'fixture-' -or $message -notmatch 'Store transport failed') { throw 'Transport error was not sanitized.' }
        }
    }
    Test 'PowerShell parses and static workflow guards restrict Store operations' {
        foreach ($path in @('tools\StoreRelease.psm1', 'tools\Invoke-StoreRelease.ps1', 'tests\tools\Test-StoreRelease.ps1')) {
            $tokens = $null; $parseErrors = $null
            [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $root $path), [ref]$tokens, [ref]$parseErrors)
            Assert (@($parseErrors).Count -eq 0) 'PowerShell parse failure.'
        }
        $workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\store-release.yml') -Raw
        Assert ($workflow -match 'workflow_dispatch:' -and $workflow -notmatch '(?m)^  (push|pull_request|schedule):') 'Unexpected automatic trigger.'
        Assert ($workflow -match 'cancel-in-progress: false' -and $workflow -match 'group: microsoft-store-9P1153MP6R32') 'Store concurrency missing.'
        Assert ($workflow -match 'shell: powershell' -and $workflow -notmatch 'shell: pwsh') 'Store workflow must use the locally validated Windows PowerShell runtime.'
        Assert ($workflow -notmatch 'contents: write|actions: write|msstore publish|--noCommit') 'Unsafe publication shortcut or token permissions.'
        $entrypoint = Get-Content -LiteralPath (Join-Path $root 'tools\Invoke-StoreRelease.ps1') -Raw
        Assert ($entrypoint.IndexOf('ShouldProcess(') -lt $entrypoint.IndexOf('(Get-StoreToken)')) 'WhatIf could authenticate to Store.'
        $uploadStart = $entrypoint.IndexOf('if (Test-Path -LiteralPath $WorkDirectory)')
        Assert ($uploadStart -ge 0) 'Upload entrypoint guard missing.'
        $uploadCode = $entrypoint.Substring($uploadStart)
        Assert ($uploadCode.IndexOf('ShouldProcess(') -lt $uploadCode.IndexOf('(Get-StoreToken)')) 'Upload WhatIf could authenticate to Store.'
        Assert ($uploadCode.IndexOf('New-StorePackageArchive ') -lt $uploadCode.IndexOf('(Get-StoreToken)')) 'Archive length validation must precede upload authentication.'
    }
    Test 'entrypoint wires trusted private CI provenance and receipt-only Actions reads without Store or Azure' {
        $environment = @{}
        foreach ($name in @('GITHUB_REPOSITORY', 'GITHUB_REF', 'GITHUB_EVENT_NAME', 'GITHUB_SHA', 'GITHUB_RUN_ID')) { $environment[$name] = [Environment]::GetEnvironmentVariable($name) }
        $oldGh = Get-Item Function:\gh -ErrorAction SilentlyContinue
        $global:StoreTestRun = New-Run
        $global:StoreTestFixturePath = $fixtures
        $global:StoreTestGhCount = 0
        $global:StoreTestCiCalls = New-Object 'Collections.Generic.List[object]'
        $global:StoreTestCiFailure = $false
        # Run an unchanged copy of the entrypoint with a bounded CI-library test double.
        # The shared CI suite covers the real release transport, manifests and extraction.
        $entryRoot = Join-Path $fixtures 'entrypoint'
        $entryTools = Join-Path $entryRoot 'tools'
        New-Item -ItemType Directory -Path $entryTools | Out-Null
        foreach ($file in @('Invoke-StoreRelease.ps1', 'StoreRelease.psm1')) {
            Copy-Item -LiteralPath (Join-Path $root "tools\$file") -Destination $entryTools
        }
        Copy-Item -LiteralPath (Join-Path $root 'StoreIdentity.json') -Destination $entryRoot
        foreach ($folder in @('docs\store', 'assets\store')) {
            $destination = Join-Path $entryRoot $folder
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $root $folder) -Destination $destination -Recurse
        }
        $entrypointPath = Join-Path $entryTools 'Invoke-StoreRelease.ps1'
        $ciStub = @'
function Receive-CiBuild {
    param([string]$RunId, [int]$RunAttempt, [string]$ExpectedSha, [string]$DestinationDirectory, [string]$RunNumber)
    $global:StoreTestCiCalls.Add([pscustomobject]@{
        runId = $RunId; runAttempt = $RunAttempt; sha = $ExpectedSha; destination = $DestinationDirectory
    })
    if ($global:StoreTestCiFailure -or $RunId -cne '123' -or $RunAttempt -ne 2 -or $RunNumber -cne '42' -or $ExpectedSha -cne ('a' * 40)) {
        throw 'Offline private CI archive unavailable or provenance mismatched.'
    }
    foreach ($arch in @('arm64', 'x64')) {
        $folder = Join-Path $DestinationDirectory $arch
        New-Item -ItemType Directory -Path $folder -WhatIf:$false | Out-Null
        Copy-Item -LiteralPath (Join-Path $global:StoreTestFixturePath "$arch\package.msix") -Destination $folder -WhatIf:$false
    }
    return [pscustomobject]@{ runId = $RunId; runAttempt = $RunAttempt; headSha = $ExpectedSha }
}
'@
        [IO.File]::WriteAllText((Join-Path $entryTools 'CiBuild.ps1'), $ciStub, (New-Object Text.UTF8Encoding($false)))
        $global:StoreTestDraftRun = Copy-Json $global:StoreTestRun
        $global:StoreTestDraftRun.id = 456
        $global:StoreTestDraftRun.path = '.github/workflows/store-release.yml'
        $global:StoreTestReceipt = Copy-Json $listingReceipt
        $global:StoreTestReceipt.buildHeadSha = 'a' * 40
        function global:gh {
            $global:StoreTestGhCount++
            $global:LASTEXITCODE = 0
            if ($args[0] -ceq 'api') {
                $run = if ($args[1].EndsWith('/456')) { $global:StoreTestDraftRun } else { $global:StoreTestRun }
                return ConvertTo-Json -InputObject $run -Depth 100
            }
            if ($args[0] -ceq 'run' -and $args[1] -ceq 'download') {
                $name = [string]$args[6]; $destination = [string]$args[8]
                New-Item -ItemType Directory -Path $destination -WhatIf:$false | Out-Null
                if ($name -ceq 'store-draft-receipt') {
                    [IO.File]::WriteAllText((Join-Path $destination 'store-draft-receipt.json'),
                        (ConvertTo-Json -InputObject $global:StoreTestReceipt -Depth 100), (New-Object Text.UTF8Encoding($false)))
                    return
                }
                throw 'Only the sanitized Store receipt may be downloaded from Actions.'
            }
            throw 'Unexpected offline GitHub request.'
        }
        try {
            $env:GITHUB_REPOSITORY = 'arisewanggithub/RedrainCast-Windows'
            $env:GITHUB_REF = 'refs/heads/main'
            $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
            $env:GITHUB_SHA = 'a' * 40
            $env:GITHUB_RUN_ID = '456'
            $preflight = Join-Path $fixtures 'preflight-whatif'
            & $entrypointPath -Stage preflight -ConfirmStore $identity.StoreId -WorkDirectory $preflight -WhatIf
            Assert ($global:StoreTestGhCount -eq 0 -and -not (Test-Path -LiteralPath $preflight)) 'Read-only WhatIf accessed artifacts or created files.'
            & $entrypointPath -Stage inspect-draft -InspectSubmissionId '60' -ConfirmStore $identity.StoreId -WorkDirectory $preflight -WhatIf
            Assert ($global:StoreTestGhCount -eq 0 -and -not (Test-Path -LiteralPath $preflight)) 'Inspection WhatIf accessed artifacts or created files.'
            Reject {
                & $entrypointPath -Stage inspect-draft -ConfirmStore $identity.StoreId -WhatIf
            } 'numeric'
            Reject {
                & $entrypointPath -Stage inspect-draft -InspectSubmissionId '60' -BuildRunId '123' -ConfirmStore $identity.StoreId -WhatIf
            } 'does not accept'
            Reject {
                & $entrypointPath -Stage upload-draft -InspectSubmissionId '60' -ConfirmStore $identity.StoreId -WhatIf
            } 'only valid'
            Reject {
                & $entrypointPath -Stage preflight -BuildRunId '123' -ConfirmStore $identity.StoreId -WhatIf
            } 'does not accept'
            $work = Join-Path $fixtures 'whatif'
            & $entrypointPath -Stage upload-draft -BuildRunId '123' -ConfirmStore $identity.StoreId -WorkDirectory $work -WhatIf
            Assert ($global:StoreTestGhCount -eq 1 -and $global:StoreTestCiCalls.Count -eq 1) 'Expected one trusted run read and one private CI archive download.'
            $call = $global:StoreTestCiCalls[0]
            Assert ($call.runId -ceq '123' -and $call.runAttempt -eq 2 -and $call.sha -ceq ('a' * 40) -and
                $call.destination -ceq $work) 'Private CI download was not bound to the exact reviewed run attempt, SHA and destination.'
            Assert (-not (Test-Path -LiteralPath (Join-Path $work 'store-draft-receipt.json'))) 'WhatIf produced a live upload receipt.'
            Reject {
                & $entrypointPath -Stage upload-listings-draft -BuildRunId '123' -ConfirmStore $identity.StoreId `
                    -WorkDirectory (Join-Path $fixtures 'unapproved-listings') -WhatIf
            } 'reviewed'
            Assert ($global:StoreTestGhCount -eq 1 -and $global:StoreTestCiCalls.Count -eq 1) 'Unapproved listing input accessed private builds.'
            $listingPreflight = Join-Path $fixtures 'whatif-listings'
            & $entrypointPath -Stage upload-listings-draft -BuildRunId '123' -ConfirmStore $identity.StoreId `
                -ConfirmListingsCommit ('a' * 40) -WorkDirectory $listingPreflight -WhatIf
            Assert ($global:StoreTestGhCount -eq 2 -and $global:StoreTestCiCalls.Count -eq 2) 'Listing WhatIf did not validate the private paired build.'
            Assert (-not (Test-Path -LiteralPath (Join-Path $listingPreflight 'store-draft-receipt.json')) -and
                -not (Test-Path -LiteralPath (Join-Path $listingPreflight 'store-packages.zip'))) 'Listing preflight left receipt or ZIP.'
            $global:StoreTestReceipt.listingUpdate.sourceSha = 'b' * 40
            Reject {
                & $entrypointPath -Stage submit-draft -DraftRunId '456' -ConfirmStore $identity.StoreId `
                    -WorkDirectory (Join-Path $fixtures 'wrong-listing-receipt') -WhatIf
            } 'trusted upload workflow'
            Assert ($global:StoreTestGhCount -eq 4 -and $global:StoreTestCiCalls.Count -eq 2) 'Wrong listing source was not rejected before private build access.'
            $global:StoreTestReceipt.listingUpdate.sourceSha = 'a' * 40
            & $entrypointPath -Stage submit-draft -DraftRunId '456' -ConfirmStore $identity.StoreId `
                -WorkDirectory (Join-Path $fixtures 'whatif-listing-submit') -WhatIf
            Assert ($global:StoreTestGhCount -eq 7 -and $global:StoreTestCiCalls.Count -eq 3) 'Listing submit preflight did not validate the Actions receipt and private build.'
            foreach ($field in @('head_sha', 'run_attempt', 'run_number')) {
                $original = $global:StoreTestRun.$field
                $global:StoreTestRun.$field = if ($field -ceq 'head_sha') { 'b' * 40 } else { 3 }
                Reject {
                    & $entrypointPath -Stage upload-draft -BuildRunId '123' -ConfirmStore $identity.StoreId `
                        -WorkDirectory (Join-Path $fixtures "mismatched-$field") -WhatIf
                } 'Rebuild.*no Actions build-artifact fallback'
                $global:StoreTestRun.$field = $original
            }
            $global:StoreTestCiFailure = $true
            Reject {
                & $entrypointPath -Stage upload-draft -BuildRunId '123' -ConfirmStore $identity.StoreId `
                    -WorkDirectory (Join-Path $fixtures 'missing-private-archive') -WhatIf
            } 'Rebuild.*no Actions build-artifact fallback'
            $global:StoreTestCiFailure = $false
            Assert ($global:StoreTestGhCount -eq 11 -and $global:StoreTestCiCalls.Count -eq 7) 'Release download failure fell back to Actions.'
            $global:StoreTestRun.event = 'pull_request'
            Reject {
                & $entrypointPath -Stage upload-draft -BuildRunId '123' -ConfirmStore $identity.StoreId `
                    -WorkDirectory (Join-Path $fixtures 'untrusted-private-source') -WhatIf
            } 'successful, completed main run'
            $global:StoreTestRun.event = 'push'
            Assert ($global:StoreTestCiCalls.Count -eq 7) 'Untrusted source reached the release downloader.'
            $env:GITHUB_REF = 'refs/heads/feature'
            Reject { & $entrypointPath -Stage upload-draft -BuildRunId '123' -ConfirmStore $identity.StoreId -WorkDirectory (Join-Path $fixtures 'badbranch') -WhatIf } 'manual main'
            Assert ($global:StoreTestGhCount -eq 12 -and $global:StoreTestCiCalls.Count -eq 7) 'Branch rejection accessed release downloads.'
        } finally {
            Remove-Item Function:\gh
            if ($null -ne $oldGh) { Set-Item Function:\gh $oldGh.ScriptBlock }
            foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name]) }
            Remove-Variable StoreTestRun, StoreTestFixturePath, StoreTestGhCount, StoreTestDraftRun, StoreTestReceipt,
                StoreTestCiCalls, StoreTestCiFailure -Scope Global
        }
    }
    Write-Host "$script:passed offline Store tests passed. No live Store, Azure, native build, signing, installation, or capture operations ran."
} finally {
    Remove-Item -LiteralPath $fixtures -Recurse -Force
}
