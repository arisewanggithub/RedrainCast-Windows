[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][ValidateSet('preflight', 'inspect-draft', 'discard-draft', 'upload-draft', 'submit-draft', 'status')][string]$Stage,
    [string]$InspectSubmissionId,
    [string]$InspectRunId,
    [string]$BuildRunId,
    [string]$DraftRunId,
    [Parameter(Mandatory = $true)][string]$ConfirmStore,
    [string]$ApprovedBasePriceId,
    [string]$WorkDirectory = '.\artifacts\store-release'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'StoreCli.psm1') -Force
if ($env:GITHUB_REPOSITORY -cne 'arisewanggithub/RedrainCast-Windows' -or
    $env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch') {
    throw 'Live Store operations are restricted to manual main dispatches in the private source repository.'
}
if ($ConfirmStore -cne '9P1153MP6R32') { throw 'Exact Store ID confirmation is required.' }
if ($ApprovedBasePriceId -and $Stage -cne 'upload-draft') { throw 'Base-price approval is only accepted for upload-draft.' }
if ($Stage -cnotin @('inspect-draft', 'discard-draft') -and ($InspectSubmissionId -or $InspectRunId)) {
    throw 'Inspection inputs are only accepted for inspect-draft or discard-draft.'
}
if ($Stage -ceq 'inspect-draft' -and $InspectRunId) { throw 'inspect-draft does not accept a prior inspection run.' }
if ($Stage -cin @('inspect-draft', 'discard-draft') -and $InspectSubmissionId -cnotmatch '^[1-9][0-9]*$') {
    throw 'An exact pending submission ID is required.'
}
if ($Stage -ceq 'discard-draft' -and $InspectRunId -cnotmatch '^[1-9][0-9]*$') {
    throw 'Discard requires a successful inspected draft run.'
}
if ($Stage -cnotin @('upload-draft', 'submit-draft') -and ($BuildRunId -or $DraftRunId)) {
    throw 'This stage does not accept build or draft run inputs.'
}
if ($Stage -cne 'submit-draft' -and $DraftRunId) { throw 'Draft receipt run is only accepted for submit-draft.' }
if (Test-Path -LiteralPath $WorkDirectory) { throw 'Work directory must be new; existing files are preserved.' }
$identity = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'StoreIdentity.json') -Raw | ConvertFrom-Json
if ($identity.StoreId -cne $ConfirmStore) { throw 'Store identity does not match the confirmation.' }
New-Item -ItemType Directory -Path $WorkDirectory -WhatIf:$false | Out-Null
$WorkDirectory = (Get-Item -LiteralPath $WorkDirectory).FullName
$cli = {
    param([string[]]$Arguments, [bool]$Json)
    Invoke-StoreCli -Arguments $Arguments -Json:$Json
}
function Read-TrustedStoreArtifact {
    param([string]$RunId, [string]$Name)
    $run = Get-StoreRun $RunId '.github/workflows/store-release.yml'
    $folder = Join-Path $WorkDirectory $Name
    Receive-StoreArtifact $RunId $Name $folder
    $files = @(Get-ChildItem -LiteralPath $folder -Recurse -File)
    if ($files.Count -ne 1 -or $files[0].Name -cne "$Name.json" -or $files[0].Length -gt 64KB) {
        throw 'Expected exactly one small trusted Store artifact.'
    }
    $value = Get-Content -LiteralPath $files[0].FullName -Raw | ConvertFrom-Json
    if ($value.sourceSha -cne $run.head_sha -or $value.workflowRunId -cne $RunId) {
        throw 'Store artifact is not bound to the trusted main workflow run and commit.'
    }
    return $value
}
if ($Stage -ceq 'discard-draft') {
    $inspection = Read-TrustedStoreArtifact $InspectRunId 'store-inspection'
}
if ($Stage -ceq 'submit-draft') {
    $receipt = Read-TrustedStoreArtifact $DraftRunId 'store-draft-receipt'
    if ($BuildRunId -and $BuildRunId -cne $receipt.sourceBuildRunId) { throw 'Build input differs from receipt.' }
    $BuildRunId = [string]$receipt.sourceBuildRunId
}
if ($Stage -cin @('upload-draft', 'submit-draft')) {
    $build = Get-StoreRun $BuildRunId '.github/workflows/windows.yml'
    . (Join-Path $PSScriptRoot 'CiBuild.ps1')
    $null = Receive-CiBuild -RunId $BuildRunId -RunAttempt $build.run_attempt -ExpectedSha $build.head_sha `
        -RunNumber ([string]$build.run_number) -DestinationDirectory $WorkDirectory
    $packages = Get-StorePackageSet $WorkDirectory $identity $BuildRunId
}
# -WhatIf validates the official release/receipt offline; no CLI authentication or Store calls.
if (-not $PSCmdlet.ShouldProcess($ConfirmStore, "$Stage (certification publishes automatically when approved)")) { return }
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$cliScratch = Join-Path $WorkDirectory 'cli-scratch'
New-Item -ItemType Directory -Path $cliScratch | Out-Null
$env:TEMP = $cliScratch
$env:TMP = $cliScratch
try {
    Initialize-StoreCli
    switch ($Stage) {
        'preflight' {
            $state = Get-StoreState $identity $cli
            Assert-StorePricing $state.submission ''
            Write-Host "Product identity and pricing verified. Pending draft exists: $($null -ne $state.application.pendingApplicationSubmission)."
        }
        'inspect-draft' {
            $state = Get-StoreState $identity $cli
            $inspection = Get-StoreInspection $state $InspectSubmissionId $env:GITHUB_RUN_ID $env:GITHUB_SHA
            $inspection | Add-Member -NotePropertyName workflowRunId -NotePropertyValue $env:GITHUB_RUN_ID
            $inspection | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $WorkDirectory 'store-inspection.json') -Encoding UTF8
            $price = [string]$state.submission.pricing.priceId
            $priceClass = if ($price -ieq 'Base') { 'Base (not round-trippable; not evidence of Free)' }
                elseif ($price -ceq 'Free') { 'Free' } elseif ($price -match '^Tier[0-9]+$') { 'Paid tier' } else { 'Other or missing' }
            Write-Host "PendingCommit draft fingerprint saved. Pricing classification: $priceClass. No metadata or signed URLs logged."
        }
        'discard-draft' {
            Remove-StoreInspectedDraft $identity $inspection $InspectSubmissionId $cli
            Write-Host 'Only the explicitly inspected unchanged pending draft was discarded. No replacement was uploaded.'
        }
        'upload-draft' {
            $receipt = New-StoreCliDraft $identity $packages $BuildRunId $build.head_sha `
                (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\RedrainCast.App') `
                (Join-Path $WorkDirectory 'private-submission.json') $ApprovedBasePriceId $cli
            $receipt | Add-Member -NotePropertyName sourceSha -NotePropertyValue $env:GITHUB_SHA
            $receipt | Add-Member -NotePropertyName workflowRunId -NotePropertyValue $env:GITHUB_RUN_ID
            $receipt | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $WorkDirectory 'store-draft-receipt.json') -Encoding UTF8
            Write-Host "Validated packages uploaded to draft $($receipt.submissionId). No certification requested. Saved publication mode: Immediate."
        }
        'submit-draft' {
            $status = Submit-StoreCliDraft $identity $receipt $packages $BuildRunId $build.head_sha $cli
            Write-Host "Official CLI certification request accepted. Current state: $status. Publication is automatic after certification passes."
        }
        'status' {
            $state = Get-StoreState $identity $cli
            $null = & $cli @('submission', 'status', $ConfirmStore) $false
            Write-Host "Current submission state: $(Get-StoreSafeStatus $state.submission). Raw service messages suppressed."
        }
    }
} finally {
    # Hosted runner only: remove persisted CLI credentials even after a failure.
    try { $null = Invoke-StoreCli @('reconfigure', '--reset') } catch { Write-Warning 'CLI credential reset failed; dispose of this hosted runner.' }
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
}
