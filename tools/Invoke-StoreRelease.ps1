[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][ValidateSet('preflight', 'upload-draft', 'upload-listings-draft', 'submit-draft')][string]$Stage,
    [string]$BuildRunId,
    [string]$DraftRunId,
    [Parameter(Mandatory = $true)][string]$ConfirmStore,
    [string]$ConfirmListingsCommit,
    [string]$WorkDirectory = '.\artifacts\store-release'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'StoreRelease.psm1') -Force
if ($env:GITHUB_REPOSITORY -cne 'arisewanggithub/RedrainCast-Windows' -or
    $env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch') {
    throw 'Live Store operations are restricted to manual main dispatches in the private source repository.'
}
if ($ConfirmStore -cne '9P1153MP6R32') { throw 'Exact Store ID confirmation is required.' }
if ($Stage -ceq 'upload-listings-draft') {
    Assert-StoreListingApproval $ConfirmListingsCommit $env:GITHUB_SHA $env:GITHUB_RUN_ID
} elseif ($ConfirmListingsCommit) { throw 'Listing commit confirmation is only valid for upload-listings-draft.' }
$identity = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'StoreIdentity.json') -Raw | ConvertFrom-Json
if ($identity.StoreId -cne $ConfirmStore) { throw 'Store identity does not match the confirmation.' }
if ($Stage -ceq 'preflight') {
    if ($BuildRunId -or $DraftRunId) { throw 'Preflight does not accept build or draft run inputs.' }
    if (-not $PSCmdlet.ShouldProcess($ConfirmStore, 'Read-only Store authentication and readiness check')) { return }
    $headers = @{ Authorization = 'Bearer ' + (Get-StoreToken) }
    $request = {
        param($method, $path, $body, $inFile)
        if ($method -cne 'GET' -or $null -ne $body -or $inFile) { throw 'Preflight only permits read-only Store GET requests.' }
        Invoke-StoreHttp GET "https://manage.devcenter.microsoft.com/v1.0/my/$path" $headers
    }.GetNewClosure()
    $readiness = Get-StoreReadiness $identity $request
    Write-Host "Store authentication and product identity verified. First publication complete: $($readiness.firstPublicationComplete). Pending draft exists: $($readiness.pendingDraftExists). Automated update allowed: $($readiness.automatedUpdateAllowed)."
    return
}
if (Test-Path -LiteralPath $WorkDirectory) { throw 'Work directory must be new; existing files are preserved.' }
New-Item -ItemType Directory -Path $WorkDirectory -WhatIf:$false | Out-Null
try {
    if ($Stage -ceq 'submit-draft') {
        $draftRun = Get-StoreRun $DraftRunId '.github/workflows/store-release.yml'
        $receiptFolder = Join-Path $WorkDirectory 'receipt'
        Receive-StoreArtifact $DraftRunId 'store-draft-receipt' $receiptFolder
        $receipts = @(Get-ChildItem -LiteralPath $receiptFolder -Recurse -File)
        if ($receipts.Count -ne 1 -or $receipts[0].Name -cne 'store-draft-receipt.json' -or $receipts[0].Length -gt 64KB) {
            throw 'Expected exactly one small trusted upload receipt.'
        }
        $receipt = Get-Content -LiteralPath $receipts[0].FullName -Raw | ConvertFrom-Json
        if ($receipt.schemaVersion -eq 2 -and
            ($receipt.listingUpdate.sourceSha -cne $draftRun.head_sha -or $receipt.listingUpdate.uploadRunId -cne $DraftRunId)) {
            throw 'Listing receipt does not match the trusted upload workflow commit and run.'
        }
        if ($BuildRunId -and $BuildRunId -cne $receipt.sourceBuildRunId) { throw 'Build input differs from the receipt.' }
        $BuildRunId = [string]$receipt.sourceBuildRunId
    } elseif ($DraftRunId) { throw 'Draft run input is only valid for submit-draft.' }
    $build = Get-StoreRun $BuildRunId '.github/workflows/windows.yml'
    foreach ($arch in @('arm64', 'x64')) {
        Receive-StoreArtifact $BuildRunId "RedrainCast-$arch" (Join-Path $WorkDirectory $arch)
    }
    $listingUpdate = $null
    if ($Stage -ceq 'upload-listings-draft') {
        $listingUpdate = Read-StoreListings (Split-Path -Parent $PSScriptRoot) $env:GITHUB_SHA $env:GITHUB_RUN_ID
    }
    $archive = New-StorePackageArchive $WorkDirectory $identity $listingUpdate
    # -WhatIf downloads/validates private GitHub artifacts only; it never authenticates to or calls Store/Azure.
    if (-not $PSCmdlet.ShouldProcess('9P1153MP6R32 (Manual publication hold)', $Stage)) { return }
    $headers = @{ Authorization = 'Bearer ' + (Get-StoreToken) }
    $request = {
        param($method, $path, $body, $inFile)
        if ($inFile) {
            Invoke-StoreHttp $method $path @{ 'x-ms-blob-type' = 'BlockBlob'; 'x-ms-version' = '2019-12-12' } $null $inFile 'application/zip'
        } else {
            $json = if ($null -ne $body) { [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $body -Depth 100 -Compress -WarningAction Stop)) } else { $null }
            Invoke-StoreHttp $method "https://manage.devcenter.microsoft.com/v1.0/my/$path" $headers $json
        }
    }.GetNewClosure()
    if ($Stage -cin @('upload-draft', 'upload-listings-draft')) {
        $receipt = New-StoreDraft $identity $archive $BuildRunId $build.head_sha $request
        $receiptPath = Join-Path $WorkDirectory 'store-draft-receipt.json'
        [IO.File]::WriteAllText($receiptPath, (ConvertTo-Json -InputObject $receipt -Depth 100), (New-Object Text.UTF8Encoding($false)))
        Write-Host "API-owned draft $($receipt.submissionId) uploaded. Certification has NOT been requested."
    } else {
        $status = Submit-StoreDraft $identity $receipt $archive.packages $BuildRunId $build.head_sha $request
        Write-Host "Commit accepted; observed processing state: $status. Publication remains Manual; finish in Partner Center."
    }
} finally {
    # Do not leave downloaded binaries in later artifact steps. Only the sanitized receipt may survive.
    foreach ($name in @('arm64', 'x64', 'receipt', 'store-packages.zip')) {
        $path = Join-Path $WorkDirectory $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -WhatIf:$false }
    }
}
