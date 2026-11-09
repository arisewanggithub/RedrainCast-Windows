Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:Repository = 'arisewanggithub/RedrainCast-Windows'
$script:Product = '9P1153MP6R32'
$script:CliVersion = '0.4.4'

function Assert-StoreRun {
    param($Run, [string]$RunId, [string]$WorkflowPath)
    if ($RunId -notmatch '^[1-9][0-9]*$' -or [string]$Run.id -cne $RunId -or
        $Run.repository.full_name -cne $script:Repository -or
        $Run.head_repository.full_name -cne $script:Repository -or
        $Run.head_branch -cne 'main' -or $Run.status -cne 'completed' -or
        $Run.conclusion -cne 'success' -or $Run.event -cnotin @('push', 'workflow_dispatch', 'workflow_run') -or
        ($Run.event -ceq 'workflow_run' -and $WorkflowPath -cne '.github/workflows/store-release.yml') -or
        $Run.path -cne $WorkflowPath -or [string]$Run.head_sha -notmatch '^[a-f0-9]{40}$' -or
        [string]$Run.run_attempt -notmatch '^[1-9][0-9]*$' -or
        [string]$Run.run_number -notmatch '^[1-9][0-9]*$') {
        throw 'Source must be a successful, completed main run of the expected workflow in this repository (not a PR or fork).'
    }
}

function Assert-StoreReleaseTrigger {
    param($Event, $Run)
    Assert-StoreRun $Run ([string]$Event.workflow_run.id) '.github/workflows/windows.yml'
    $source = $Event.workflow_run
    if ($Event.repository.full_name -cne $script:Repository -or
        $source.repository.full_name -cne $script:Repository -or
        $source.head_repository.full_name -cne $script:Repository -or
        $source.head_branch -cne 'main' -or $source.event -cne 'push' -or
        $source.status -cne 'completed' -or $source.conclusion -cne 'success' -or
        $Run.event -cne 'push' -or $source.head_sha -cne $Run.head_sha -or
        $source.run_attempt -ne $Run.run_attempt) {
        throw 'Automatic Store publication requires the exact successful main push Windows build event.'
    }
}

function Get-StoreLatestBuild {
    param([string]$Directory)
    $raw = & gh api "repos/$script:Repository/releases/latest" 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve latest official release.' }
    $release = $raw -join "`n" | ConvertFrom-Json
    if ($release.draft -or $release.prerelease -or $release.tag_name -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') {
        throw 'Latest release must be published, official and non-prerelease.'
    }
    $folder = Join-Path $Directory 'latest-manifest'
    New-Item -ItemType Directory -Path $folder | Out-Null
    & gh release download $release.tag_name --repo $script:Repository --pattern ci-manifest.json --dir $folder 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Latest official release manifest is unavailable.' }
    $path = Join-Path $folder 'ci-manifest.json'
    if ((Get-Item -LiteralPath $path).Length -gt 64KB) { throw 'Release manifest is too large.' }
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $run = Get-StoreRun ([string]$manifest.runId) '.github/workflows/windows.yml'
    if ($run.event -cne 'push' -or $release.target_commitish -cne $run.head_sha -or
        $manifest.headSha -cne $run.head_sha -or $release.tag_name -cne "v$($manifest.version)") {
        throw 'Latest official release must originate from a successful main push.'
    }
    # Receive-CiBuild subsequently verifies the complete manifest, version, assets and hashes.
    return $run
}

function Get-StoreRun {
    param([string]$RunId, [string]$WorkflowPath)
    if ($RunId -notmatch '^[1-9][0-9]*$') { throw 'Run ID must be a positive integer.' }
    $raw = & gh api "repos/$script:Repository/actions/runs/$RunId" 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read source workflow run.' }
    $run = $raw -join "`n" | ConvertFrom-Json
    Assert-StoreRun $run $RunId $WorkflowPath
    return $run
}

function Receive-StoreArtifact {
    param([string]$RunId, [string]$Name, [string]$Destination)
    & gh run download $RunId --repo $script:Repository --name $Name --dir $Destination 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Required private Actions artifact is missing, expired, or could not be downloaded.' }
}

function ConvertTo-StoreCanonicalJson {
    param($Value)
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [Collections.IDictionary] -or $Value -is [pscustomobject]) {
        $keys = @(if ($Value -is [Collections.IDictionary]) { $Value.Keys } else { $Value.PSObject.Properties | ForEach-Object { $_.Name } })
        $keys = [string[]]$keys
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        $pairs = foreach ($key in $keys) {
            $child = if ($Value -is [Collections.IDictionary]) { $Value[$key] } else { $Value.PSObject.Properties[$key].Value }
            ($key | ConvertTo-Json -Compress) + ':' + (ConvertTo-StoreCanonicalJson $child)
        }
        return '{' + ($pairs -join ',') + '}'
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = foreach ($item in $Value) { ConvertTo-StoreCanonicalJson $item }
        return '[' + ($items -join ',') + ']'
    }
    return ConvertTo-Json -InputObject $Value -Compress -Depth 100
}

function Get-StoreHash {
    param([string]$Text)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-StoreMetadataFingerprint {
    param($Submission)
    $metadata = [ordered]@{}
    foreach ($property in $Submission.PSObject.Properties) {
        if ($property.Name -in @('id', 'status', 'statusDetails', 'fileUploadUrl', 'applicationPackages', 'targetPublishMode', 'targetPublishDate')) { continue }
        $metadata[$property.Name.ToLowerInvariant()] = $property.Value
    }
    return Get-StoreHash (ConvertTo-StoreCanonicalJson $metadata)
}

function Invoke-StoreCli {
    param([string[]]$Arguments, [switch]$Json)
    # CLI logs can contain credentials, SAS URLs and metadata. Never forward either stream.
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& msstore @Arguments --output-stream stderr 2>$null)
        $exitCode = $LASTEXITCODE
    } catch { throw 'Microsoft Store CLI could not start. Raw output suppressed.' }
    finally { $ErrorActionPreference = $previous }
    if ($exitCode -ne 0) { throw "Microsoft Store CLI failed (exit $exitCode). Raw output suppressed; inspect the unchanged/pending draft before retrying." }
    if ($Json) {
        try {
            $result = ($output | ForEach-Object { [string]$_ }) -join "`n" | ConvertFrom-Json -ErrorAction Stop
            if ($null -eq $result -or $result -isnot [pscustomobject]) { throw 'Not an object.' }
            return $result
        } catch { throw 'Microsoft Store CLI returned invalid JSON. Raw output suppressed.' }
    }
    return ($output | ForEach-Object { [string]$_ }) -join "`n"
}

function Initialize-StoreCli {
    $version = Invoke-StoreCli @('--version')
    # The v0.4.4 Windows release's informational version includes its build height and commit.
    if ($version.Trim() -cnotmatch '^0\.4\.4(?:\.3)?(?:\+4fa163e6db[a-f0-9]{0,30})?$') {
        throw 'Microsoft Store CLI must be the pinned version 0.4.4.'
    }
    foreach ($name in @('PARTNER_CENTER_TENANT_ID', 'PARTNER_CENTER_CLIENT_ID')) {
        $guid = [guid]::Empty
        if (-not [guid]::TryParse([Environment]::GetEnvironmentVariable($name), [ref]$guid) -or $guid -eq [guid]::Empty) {
            throw "Missing or invalid $name GUID."
        }
    }
    foreach ($name in @('PARTNER_CENTER_CLIENT_SECRET', 'PARTNER_CENTER_SELLER_ID')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Missing $name." }
    }
    $null = Invoke-StoreCli @('reconfigure', '--tenantId', $env:PARTNER_CENTER_TENANT_ID,
        '--sellerId', $env:PARTNER_CENTER_SELLER_ID, '--clientId', $env:PARTNER_CENTER_CLIENT_ID,
        '--clientSecret', $env:PARTNER_CENTER_CLIENT_SECRET)
}

function Clear-StoreCliCredentials {
    param([string]$ClientId = $env:PARTNER_CENTER_CLIENT_ID,
        [string]$LocalApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData))
    $id = [guid]::Empty
    if (-not [guid]::TryParse($ClientId, [ref]$id) -or $id -eq [guid]::Empty) {
        throw 'Credential cleanup requires the exact configured client ID.'
    }
    if (-not ('StoreCliCredentialCleanup' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class StoreCliCredentialCleanup {
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Delete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Read(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
    public static void Clear(string clientId) {
        string target = "MicrosoftStoreCli:user=" + clientId;
        if (!Delete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new InvalidOperationException("The exact CLI credential could not be deleted.");
        IntPtr credential;
        if (Read(target, 1, 0, out credential)) {
            CredFree(credential);
            throw new InvalidOperationException("The CLI credential remains after deletion.");
        }
        if (Marshal.GetLastWin32Error() != 1168)
            throw new InvalidOperationException("The CLI credential removal could not be verified.");
    }
}
'@
    }
    # v0.4.4 ResetAsync always prompts. Match its Windows credential target without
    # invoking an interactive command or enumerating unrelated credentials/files.
    try { [StoreCliCredentialCleanup]::Clear($id.ToString()) }
    catch { throw 'CLI credential cleanup failed; dispose of this hosted runner.' }
    $folder = Join-Path $LocalApplicationData 'Microsoft\MSStore.CLI'
    foreach ($relative in @('settings.json', 'Cache\msstore_msal_cache.txt')) {
        $path = Join-Path $folder $relative
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction Stop }
        if (Test-Path -LiteralPath $path) { throw 'CLI configuration cleanup could not be verified.' }
    }
}

function Assert-StoreApplication {
    param($Application, $Identity)
    if ($Application.id -cne $script:Product -or $Identity.StoreId -cne $script:Product -or
        $Application.packageIdentityName -cne $Identity.PackageName -or $Application.publisherName -cne $Identity.Publisher) {
        throw 'Partner Center application does not match StoreIdentity.json and the pinned product.'
    }
}

function Get-StoreState {
    param($Identity, [scriptblock]$Cli)
    $app = & $Cli @('apps', 'get', $script:Product) $true
    Assert-StoreApplication $app $Identity
    $pending = $app.pendingApplicationSubmission
    $published = $app.lastPublishedApplicationSubmission
    if ($null -eq $pending -and $null -eq $published) {
        throw 'First publication must be completed before automated updates. No Store mutation was performed.'
    }
    $submission = & $Cli @('submission', 'get', $script:Product) $true
    $expectedId = if ($null -ne $pending) { $pending.id } else { $published.id }
    if ([string]$expectedId -cnotmatch '^[1-9][0-9]*$' -or [string]$submission.id -cne [string]$expectedId) {
        throw 'Application and submission pointers do not match. No Store mutation was performed.'
    }
    return [pscustomobject]@{ application = $app; submission = $submission }
}

function Get-StoreSafeStatus {
    param($Submission)
    $known = @('PendingCommit', 'CommitStarted', 'CommitFailed', 'PreProcessing', 'PreProcessingFailed',
        'Certification', 'CertificationFailed', 'PendingCertification', 'Publishing', 'PublishStarted',
        'PublishFailed', 'Published', 'PendingPublication', 'ReadyToPublish', 'InStore', 'Canceled', 'Release')
    if ([string]$Submission.status -cin $known) { return [string]$Submission.status }
    return 'Other (suppressed)'
}

function Get-StoreInspection {
    param($State, [string]$SubmissionId, [string]$RunId, [string]$SourceSha)
    if ($SubmissionId -cnotmatch '^[1-9][0-9]*$' -or $null -eq $State.application.pendingApplicationSubmission -or
        [string]$State.application.pendingApplicationSubmission.id -cne $SubmissionId -or
        [string]$State.submission.id -cne $SubmissionId -or $State.submission.status -cne 'PendingCommit') {
        throw 'Inspection requires the exact existing PendingCommit draft. Other submissions are preserved.'
    }
    return [pscustomobject]@{
        schemaVersion = 3; applicationId = $script:Product; submissionId = $SubmissionId
        inspectionRunId = $RunId; sourceSha = $SourceSha
        fingerprint = Get-StoreHash (ConvertTo-StoreCanonicalJson $State.submission)
    }
}

function Remove-StoreInspectedDraft {
    param($Identity, $Inspection, [string]$SubmissionId, [scriptblock]$Cli)
    $state = Get-StoreState $Identity $Cli
    $current = Get-StoreInspection $state $SubmissionId '' ''
    if ($Inspection.schemaVersion -ne 3 -or $Inspection.applicationId -cne $script:Product -or
        $Inspection.submissionId -cne $SubmissionId -or $Inspection.fingerprint -cne $current.fingerprint) {
        throw 'Draft changed or inspection does not match. Existing draft is preserved.'
    }
    # The dedicated owner-dispatched discard stage is the only allowed draft deletion.
    $null = & $Cli @('submission', 'delete', $script:Product, '--no-confirm') $false
    $app = & $Cli @('apps', 'get', $script:Product) $true
    Assert-StoreApplication $app $Identity
    if ($null -ne $app.pendingApplicationSubmission) { throw 'A pending draft remains after the CLI discard. Inspect before any further operation; no automatic retry.' }
}

function Get-StorePriceClassification {
    param($Submission)
    $pricing = $Submission.PSObject.Properties['pricing']
    if ($null -eq $pricing -or $null -eq $pricing.Value) { return 'Missing' }
    $price = [string]$pricing.Value.priceId
    if ($price -ieq 'Base') { return 'Base (not round-trippable; not evidence of Free)' }
    if ($price -ceq 'Free') { return 'Free' }
    if ($price -ceq 'NotAvailable') { return 'NotAvailable' }
    if ($price -cmatch '^Tier[1-9][0-9]*$') { return 'Paid tier' }
    return 'Other or missing'
}

function Assert-StorePricing {
    param($Submission, [string]$ApprovedBasePriceId)
    if ($ApprovedBasePriceId -and $ApprovedBasePriceId -cnotmatch '^(Free|NotAvailable|Tier[1-9][0-9]*)$') {
        throw 'Explicit base-price approval must be Free, NotAvailable or a real Tier number.'
    }
    $pricing = $Submission.PSObject.Properties['pricing']
    $price = if ($null -ne $pricing -and $null -ne $pricing.Value) { [string]$pricing.Value.priceId } else { '' }
    if (-not $ApprovedBasePriceId -and ([string]::IsNullOrWhiteSpace($price) -or $price -ieq 'Base')) {
        throw 'Price cannot safely round-trip. Base does not mean Free. Explicit owner-approved base price is required before upload.'
    }
}

function Read-StorePackage {
    param([string]$Path, [string]$Architecture, $Identity)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @('AppxManifest.xml', 'AppxBlockMap.xml', '[Content_Types].xml',
            'resources.pri', 'RedrainCast.App.pri', 'RedrainCast.App.exe', 'RedrainCast.App.dll', 'coreclr.dll',
            'capture-worker/RedrainCast.CaptureWorker.exe', 'capture-worker/RedrainCast.CaptureWorker.dll',
            'capture-worker/coreclr.dll', 'PRIVACY.md', 'THIRD_PARTY_NOTICES.txt')) {
            if (@($names | Where-Object { $_ -ceq $required }).Count -ne 1) { throw 'MSIX is missing a required shipping file or has duplicate entries.' }
        }
        if (@($names | Where-Object {
            $_ -match '(?i)\.pdb$|(^|/)(CaptureProbe|ffmpeg|ffprobe|RecordExport)([^/]*)(\.exe|\.dll)$' -and
            $_ -cnotin @('FFmpeg.AutoGen.dll', 'capture-worker/FFmpeg.AutoGen.dll')
        }).Count) { throw 'MSIX contains debug symbols or forbidden standalone helpers.' }
        $reader = New-Object IO.StreamReader($archive.GetEntry('AppxManifest.xml').Open())
        try { $xmlText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $settings = New-Object Xml.XmlReaderSettings
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $xmlReader = [Xml.XmlReader]::Create((New-Object IO.StringReader($xmlText)), $settings)
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        try { $document.Load($xmlReader) } finally { $xmlReader.Dispose() }
        $package = $document.DocumentElement
        if ($package.LocalName -cne 'Package' -or $package.NamespaceURI -cne 'http://schemas.microsoft.com/appx/manifest/foundation/windows10') {
            throw 'Unexpected MSIX manifest schema.'
        }
        $ns = New-Object Xml.XmlNamespaceManager($document.NameTable)
        $ns.AddNamespace('p', $package.NamespaceURI)
        $id = $document.SelectSingleNode('/p:Package/p:Identity', $ns)
        if ($null -eq $id -or $id.Name -cne $Identity.PackageName -or $id.Publisher -cne $Identity.Publisher -or
            $id.ProcessorArchitecture -cne $Architecture -or $id.Version -notmatch '^\d+\.\d+\.\d+\.0$') {
            throw 'MSIX package identity, publisher, architecture, or zero-revision version is incorrect.'
        }
        $version = [version]$id.Version
        if ($version.Major -gt 65535 -or $version.Minor -gt 65535 -or $version.Build -gt 65535) { throw 'Invalid MSIX version range.' }
        $parts = @()
        foreach ($name in @('Resources', 'Dependencies', 'Applications', 'Capabilities')) {
            $node = $document.SelectSingleNode("/p:Package/p:$name", $ns)
            if ($null -eq $node) { throw 'Incomplete MSIX manifest.' }
            if ($name -ceq 'Resources') {
                foreach ($resource in $node.SelectNodes('p:Resource[@Language]', $ns)) {
                    $resource.SetAttribute('Language', $resource.GetAttribute('Language').ToLowerInvariant())
                }
            }
            $parts += $node.OuterXml
        }
        return [pscustomobject]@{
            fileName = [IO.Path]::GetFileName($Path); sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
            version = $id.Version; architecture = $Architecture; manifestFingerprint = Get-StoreHash ($parts -join '')
        }
    } finally { $archive.Dispose() }
}

function Get-StorePackageSet {
    param([string]$Directory, $Identity, [string]$BuildRunId)
    if ($BuildRunId -cnotmatch '^[1-9][0-9]*$') { throw 'Invalid build run ID.' }
    $packages = @()
    $inputDirectory = Join-Path $Directory 'cli-input'
    New-Item -ItemType Directory -Path $inputDirectory -WhatIf:$false | Out-Null
    foreach ($arch in @('arm64', 'x64')) {
        $found = @(Get-ChildItem -LiteralPath (Join-Path $Directory $arch) -Filter '*.msix' -Recurse -File)
        if ($found.Count -ne 1) { throw 'Exactly one MSIX is required in each architecture release download.' }
        $package = Read-StorePackage $found[0].FullName $arch $Identity
        $folder = Join-Path $inputDirectory $arch
        New-Item -ItemType Directory -Path $folder -WhatIf:$false | Out-Null
        $package.fileName = "RedrainCast-$BuildRunId-$arch.msix"
        $path = Join-Path $folder $package.fileName
        Copy-Item -LiteralPath $found[0].FullName -Destination $path -WhatIf:$false
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $package.sha256) { throw 'Staged package hash changed.' }
        $packages += $package
    }
    if ($packages[0].version -cne $packages[1].version -or $packages[0].manifestFingerprint -cne $packages[1].manifestFingerprint) {
        throw 'ARM64 and x64 versions and non-architecture manifest metadata must match.'
    }
    return [pscustomobject]@{ directory = (Get-Item -LiteralPath $inputDirectory).FullName; packages = $packages }
}

function Assert-StoreUploadedPackages {
    param($Submission, $Packages)
    $active = @($Submission.applicationPackages | Where-Object { $_.fileStatus -cne 'PendingDelete' })
    if ($active.Count -ne 2) { throw 'Draft must contain exactly the two validated release packages.' }
    foreach ($expected in $Packages) {
        $found = @($active | Where-Object { $_.fileName -ceq $expected.fileName })
        if ($found.Count -ne 1 -or $found[0].fileStatus -cnotin @('PendingUpload', 'Uploaded')) {
            throw 'Draft package names or upload states do not match the validated release.'
        }
        foreach ($field in @('version', 'architecture')) {
            $property = $found[0].PSObject.Properties[$field]
            $value = if ($null -ne $property) { [string]$property.Value } else { '' }
            # PendingUpload may precede ingestion; never accept contradictory populated metadata.
            if ([string]::IsNullOrWhiteSpace($value)) {
                if ($found[0].fileStatus -ceq 'Uploaded') { throw 'Uploaded package ingestion metadata is incomplete; inspect before certification.' }
                continue
            }
            $matches = if ($field -ceq 'architecture') { $value -ieq [string]$expected.$field } else { $value -ceq [string]$expected.$field }
            if (-not $matches) { throw 'Draft package version or architecture differs from the validated release.' }
        }
    }
}

function New-StoreCliDraft {
    param($Identity, $PackageSet, [string]$BuildRunId, [string]$BuildHeadSha,
        [string]$ProjectPath, [string]$PayloadPath, [string]$ApprovedBasePriceId, [scriptblock]$Cli)
    $state = Get-StoreState $Identity $Cli
    if ($null -ne $state.application.pendingApplicationSubmission) { throw 'Existing draft is preserved. Inspect and explicitly discard it before upload; --noCommit is not non-destructive.' }
    if ($null -eq $state.application.lastPublishedApplicationSubmission) { throw 'First publication must be completed before automated updates.' }
    $version = [version]$PackageSet.packages[0].version
    foreach ($package in @($state.submission.applicationPackages | Where-Object { $_.fileStatus -cne 'PendingDelete' })) {
        $publishedVersion = $null
        if (-not [version]::TryParse([string]$package.version, [ref]$publishedVersion) -or $version -le $publishedVersion) {
            throw 'Release must be newer than every published package; duplicates and downgrades are not uploaded.'
        }
    }
    Assert-StorePricing $state.submission $ApprovedBasePriceId
    if ($ApprovedBasePriceId) {
        if ($null -eq $state.submission.pricing) { $state.submission.pricing = [pscustomobject]@{ priceId = $ApprovedBasePriceId } }
        else { $state.submission.pricing.priceId = $ApprovedBasePriceId }
    }
    $metadataHash = Get-StoreMetadataFingerprint $state.submission
    $args = @('publish', $ProjectPath, '--appId', $script:Product, '--inputDirectory', $PackageSet.directory,
        '--noCommit', '--uploadTimeout', '600')
    if ($ApprovedBasePriceId) { $args += @('--priceId', $ApprovedBasePriceId) }
    $null = & $Cli $args $false
    $uploaded = Get-StoreState $Identity $Cli
    $draft = $uploaded.submission
    if ($null -eq $uploaded.application.pendingApplicationSubmission -or $draft.status -cne 'PendingCommit' -or
        $uploaded.application.lastPublishedApplicationSubmission.id -cne $state.application.lastPublishedApplicationSubmission.id -or
        (Get-StoreMetadataFingerprint $draft) -cne $metadataHash) { throw 'CLI changed preserved metadata or submission pointers. No certification was started.' }
    # v0.4.4 matches old packages by extension, not architecture. Explicitly retire every old package.
    foreach ($package in $draft.applicationPackages) {
        if ($package.fileName -cnotin @($PackageSet.packages.fileName)) { $package.fileStatus = 'PendingDelete' }
    }
    $draft.targetPublishMode = 'Immediate'
    $draft.targetPublishDate = $null
    Assert-StoreUploadedPackages $draft $PackageSet.packages
    try {
        [IO.File]::WriteAllText($PayloadPath, (ConvertTo-Json -InputObject $draft -Depth 100), (New-Object Text.UTF8Encoding($false)))
        $null = & $Cli @('submission', 'update', $script:Product, '--payload', $PayloadPath) $true
    } finally {
        if (Test-Path -LiteralPath $PayloadPath) { Remove-Item -LiteralPath $PayloadPath -Force }
    }
    $saved = (Get-StoreState $Identity $Cli).submission
    Assert-StoreUploadedPackages $saved $PackageSet.packages
    if ($saved.id -cne $draft.id -or $saved.status -cne 'PendingCommit' -or $saved.targetPublishMode -cne 'Immediate' -or
        (Get-StoreMetadataFingerprint $saved) -cne $metadataHash) { throw 'Saved draft metadata changed. No certification was started.' }
    return [pscustomobject]@{
        schemaVersion = 3; cliVersion = $script:CliVersion; applicationId = $script:Product
        repository = $script:Repository; submissionId = [string]$saved.id
        sourceBuildRunId = $BuildRunId; buildHeadSha = $BuildHeadSha
        priorLastPublishedId = [string]$state.application.lastPublishedApplicationSubmission.id
        packages = $PackageSet.packages; metadataFingerprint = $metadataHash
        submissionFingerprint = Get-StoreHash (ConvertTo-StoreCanonicalJson $saved)
        targetPublishMode = 'Immediate'
    }
}

function Submit-StoreCliDraft {
    param($Identity, $Receipt, $PackageSet, [string]$BuildRunId, [string]$BuildHeadSha, [scriptblock]$Cli)
    if ($Receipt.schemaVersion -ne 3 -or $Receipt.cliVersion -cne $script:CliVersion -or
        $Receipt.repository -cne $script:Repository -or $Receipt.applicationId -cne $script:Product -or
        $Receipt.sourceBuildRunId -cne $BuildRunId -or $Receipt.buildHeadSha -cne $BuildHeadSha -or
        $Receipt.targetPublishMode -cne 'Immediate' -or
        (ConvertTo-StoreCanonicalJson $Receipt.packages) -cne (ConvertTo-StoreCanonicalJson $PackageSet.packages)) {
        throw 'Receipt is not bound to this CLI migration and exact validated official release.'
    }
    $state = Get-StoreState $Identity $Cli
    $draft = $state.submission
    if ($null -eq $state.application.pendingApplicationSubmission -or
        $state.application.pendingApplicationSubmission.id -cne $Receipt.submissionId -or
        $state.application.lastPublishedApplicationSubmission.id -cne $Receipt.priorLastPublishedId -or
        $draft.status -cne 'PendingCommit' -or $draft.targetPublishMode -cne 'Immediate' -or
        (Get-StoreMetadataFingerprint $draft) -cne $Receipt.metadataFingerprint -or
        (Get-StoreHash (ConvertTo-StoreCanonicalJson $draft)) -cne $Receipt.submissionFingerprint) {
        throw 'Draft changed since upload. No certification was started.'
    }
    Assert-StoreUploadedPackages $draft $PackageSet.packages
    $null = & $Cli @('submission', 'publish', $script:Product) $false
    $null = & $Cli @('submission', 'status', $script:Product) $false
    $accepted = (Get-StoreState $Identity $Cli).submission
    if ([string]$accepted.id -cne $Receipt.submissionId -or $accepted.targetPublishMode -cne 'Immediate' -or
        $accepted.status -cnotin @('CommitStarted', 'PreProcessing', 'PendingCertification', 'Certification',
            'PendingPublication', 'Publishing', 'PublishStarted', 'Published', 'InStore', 'Release')) {
        throw 'Certification acceptance and Immediate publication were not verified. Inspect status; never automatically retry.'
    }
    return Get-StoreSafeStatus $accepted
}

Export-ModuleMember -Function Assert-StoreRun, Assert-StoreReleaseTrigger, Get-StoreLatestBuild, Get-StoreRun, Receive-StoreArtifact,
    ConvertTo-StoreCanonicalJson, Get-StoreHash, Get-StoreMetadataFingerprint, Invoke-StoreCli,
    Initialize-StoreCli, Clear-StoreCliCredentials, Assert-StoreApplication, Get-StoreState, Get-StoreSafeStatus, Get-StoreInspection,
    Remove-StoreInspectedDraft, Get-StorePriceClassification, Assert-StorePricing, Read-StorePackage, Get-StorePackageSet,
    Assert-StoreUploadedPackages, New-StoreCliDraft, Submit-StoreCliDraft
