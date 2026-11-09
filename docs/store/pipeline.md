# Microsoft Store releases with the official CLI

The **Microsoft Store release** workflow uses Microsoft's
[`microsoft-store-apppublisher`](https://github.com/microsoft/microsoft-store-apppublisher)
setup action, pinned to **v1.4** (`cc9910a8d59f2eb55cbb83df0a3800cf3b5300e0`),
and [`msstore-cli` v0.4.4](https://github.com/microsoft/msstore-cli/releases/tag/v0.4.4)
(`4fa163e6dbe971359e0a03a6ac30477978a1e330`). It no longer implements OAuth,
submission REST transport, Azure uploads, or commit transport. Guarded recovery
only discards unchanged automation-owned pending drafts using the official CLI.
See Microsoft's [CLI commands](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/commands).

## Gates and credentials

Only trusted runs on `main` in `arisewanggithub/RedrainCast-Windows` may
operate on **9P1153MP6R32**. Owner-authorized automatic delivery starts after a
successful **Windows build** main **push** run completes. PRs, forks, failed runs
and manual Windows builds cannot trigger it. The script revalidates the event
against the GitHub run API before loading Store credentials. Manual Store
dispatches still require the exact product confirmation. Both paths retain
the `microsoft-store` environment's approval rules. Operations
are serialized. Do not approve another publisher/browser changing the draft
while a workflow is active: the Store API has no compare-and-swap protection.

The environment needs these four secrets:

- `PARTNER_CENTER_TENANT_ID`
- `PARTNER_CENTER_CLIENT_ID`
- `PARTNER_CENTER_CLIENT_SECRET`
- **`PARTNER_CENTER_SELLER_ID`** — the Partner Center seller/account ID, not the
  tenant ID or Store product ID. It is required for noninteractive CLI configuration.

All four secrets are configured; read-only workflow **42567665907** verified
authentication and noninteractive cleanup. Windows build and Store jobs use the
repository's dedicated self-hosted Windows runners (`self-hosted`, `Windows`,
`X64`, `arise-local`); the runners must stay online and idle when a job is
queued. Fork pull requests are excluded from these runners. Because the runner
profiles persist between jobs, Store operations are serialized and CLI
credentials/configuration are cleared in `finally`. .NET 10 setup uses each
runner's tool cache as a writable install directory instead of the default
machine-wide `Program Files` location. Never upload runner profiles, CLI logs,
raw responses, or the private payload.
CLI scratch files are redirected into the workflow's repository work directory.
The official v0.4.4 `reconfigure --reset` is **interactive**:
[`CLIConfigurator.ResetAsync`](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/Services/CLIConfigurator.cs)
unconditionally requests confirmation, defaulting to no. It has no `--no-confirm`
option. A redirected hosted-runner invocation fails before cleanup; merely
catching that failure also leaves a failing native exit code.

Instead, cleanup uses the exact official
[`CredentialManagerWindows`](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/Services/CredentialManager/Windows/CredentialManagerWindows.cs)
target `MicrosoftStoreCli:user=<configured-client-GUID>` and Windows generic
credential APIs to delete and verify absence without reading its secret.
It then removes only the known `%LOCALAPPDATA%\Microsoft\MSStore.CLI\settings.json`
and `Cache\msstore_msal_cache.txt` files. No directory enumeration or broad AppData
deletion occurs; unrelated settings/telemetry files are untouched. Cleanup errors
are fatal, not swallowed. Offline regression creates a random-GUID dummy Windows
credential and isolated repository-local config files, verifies exact deletion,
idempotence, unrelated-file preservation, and an unchanged successful native exit.
Both CLI output streams are suppressed except parsed JSON held in memory.
Only sanitized fingerprints/receipts are retained in private Actions artifacts.

## Pricing is an explicit gate

The official CLI [pricing preservation source](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/Helpers/IStorePackagedAPIExtensions.cs#L795-L857)
documents that `pricing.priceId = Base` is rejected by the legacy packaged
submission API. Removing/emptying `priceId` can instead silently make the app
free; omitting the pricing object is rejected. This is a plausible explanation
for earlier `InvalidParameterValue` failures, not proof of their actual cause.

The pinned CLI's [`PublishCommandPricingUnitTests`](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI.UnitTests/PublishCommandPricingUnitTests.cs)
explicitly states that `isAdvancedPricingModel` only describes the tier range
shown by the dashboard and that the API reports it inconsistently for the same
product. It is therefore excluded from business-metadata fingerprints and
pricing-change diagnostics; it never overrides or substitutes for the actual
base price, trial, or market-specific pricing, which remain guarded.

Ordinary uploads preserve a usable existing price without `--priceId`. `Base`,
missing pricing, or a missing/blank base price **stop before any upload**.
`preflight`, `inspect-draft` and `status` report only a safe price classification,
never market data. `inspect-draft` and read-only `status` also project saved
package filenames, versions, architectures, and upload states; missing ingestion
fields remain null rather than being inferred from CLI success. `status` remains
read-only even when pricing cannot round-trip. `Base` is not evidence that the
product is free.

The optional `approved_base_price_id` input is an **explicit pricing-change
authorization** (`Free`, `NotAvailable`, or a real `Tier` number). Only supply it
after the owner verifies the intended actual price in Partner Center or approves
persisted price evidence. It passes the exact value to official `--priceId`.
This sets a single base price; it is not a promise to preserve per-market pricing.
Never choose `Free` just to bypass an API error. Automatic delivery supplies no price override. The pinned CLI cannot export
authoritative per-market retail pricing; a reported `Base` therefore remains a
blocking explicit-price-approval gate, not a reason to use browser automation.

## Operations

1. **`preflight`**: official `apps get` and `submission get`; verify identity,
   consistent pointers, and round-trippable pricing. No mutation.
2. **`inspect-draft`**: supply the exact pending submission ID. Save a fingerprint
   of the CLI-visible `PendingCommit` draft, a safe price classification, and
   explicit package filenames, versions, architectures and upload states.
   Missing ingestion fields stay null; they are not evidence of ingestion.
   No mutation. This stage still works when pricing is `Base`.
3. **`discard-draft`** (only when separately authorized): supply that exact ID
   and successful `inspect_run_id`. Re-read and fingerprint the draft; refuse
   changes, another ID, a non-pending state, or an old REST inspection artifact.
   Only then call official `submission delete --no-confirm`.
   **This is destructive and never uploads a replacement.** Do not discard
   unknown human edits. Review the existing draft and inspection before approving.
4. **`upload-draft`**: supply a successful main Windows `build_run_id`.
   `Receive-CiBuild` retains the existing exact official-release provenance,
   completed-run/attempt, commit, asset-length and SHA-256 checks; there is no
   Actions build-artifact fallback. Validate both shipping MSIX identities,
   architectures, zero revision, resources, worker and payload exclusions.
   Copy (do not rebuild) the exact bytes into unique run-bound filenames.
   Preserve unknown or edited drafts. Automatically discard only an unchanged
   `PendingCommit` draft with a unique, unexpired draft-specific ownership artifact
   from a completed trusted main Store workflow. Verify run/commit provenance,
   product identity, the unchanged published submission pointer, and the full
   draft fingerprint immediately before deletion. Missing/expired evidence,
   human edits, concurrent pointer changes and submitted drafts block publication.
   Ownership snapshots are saved after creation and verified updates, before
   later validation can fail, and retained by Actions even on failure for 90 days.
   Legacy drafts without ownership evidence still require an explicitly approved
   inspection/discard; read-only inspection alone does not establish ownership.
   Invoke official `publish --appId 9P1153MP6R32 --noCommit` on the existing
   restore-only publishing project with the isolated two-architecture `--inputDirectory`.
   CLI v0.4.4 recognizes WinUI only when restored assets contain the umbrella
   `Microsoft.WindowsAppSDK` package. The shipping app references the smaller
   WinUI component instead, which the CLI misclassifies as UWP and then silently
   discovers no staged MSIX files. An isolated project copies the app manifest
   and references the umbrella at the app's WinUI version with all assets excluded.
   It exists only for CLI detection/restore: neither it nor its dependencies are
   built or copied into the validated shipping packages. The workflow supplies
   .NET 10 for this restore; release package bytes are never rebuilt.
   The workflow compares the CLI-created draft with the CLI-visible published
   metadata snapshot held only in process memory. If metadata differs, it
   restores that exact snapshot through official `submission update --payload`,
   retaining the new submission identity, package state, and publication
   fields; it then re-reads and requires the complete metadata fingerprint to
   match. Microsoft's [submission resource documentation](https://learn.microsoft.com/windows/uwp/monetize/manage-app-submissions#app-submission-resource)
   defines `friendlyName` as generated on submission creation; it is retained
   from the new draft rather than copied or compared as business metadata.
   Empty and single-item arrays are preserved during private snapshot cloning.
   An unverified restore stops before certification. Diagnostics contain
   only bounded allowlisted property paths, change classes, SHA-256 hashes, and
   pointer/status booleans, including separate pricing-field hashes—never values, listing text, signed URLs, or raw CLI
   output. A final official update retires all old packages and saves
   `Immediate` publication. Re-read and verify preserved metadata/listings and
   exactly the new pair; save a schema-3 receipt. No certification.
   Any populated server package version/architecture must match the validated
   MSIX. Unknown ingestion metadata is allowed only while `PendingUpload`;
   `Uploaded` packages require both fields before a receipt/certification.
5. **`submit-draft`**: supply the successful upload's `draft_run_id`. Download its
   trusted receipt, re-download and validate the identical official release,
   and verify exact draft/published pointers and full CLI-visible fingerprint.
   Call official `submission publish`, then `submission status`.
   **Certification is requested and publication is automatic when it passes.**
6. **`status`**: official read-only status request, suppressing raw service messages
   and projecting only saved package filenames, versions, architectures, and
   upload states. Missing ingestion fields stay null and are never inferred from
   CLI success.
7. **`release-latest`**: manual end-to-end operation, also selected by the automatic
   trigger. Takes no build or draft run input. Resolve GitHub's current latest
   published non-prerelease official release and its manifest; require successful
   main-push provenance, then perform all the normal release/manifest/MSIX checks.
   For automatic runs, the latest release must belong to the exact triggering
   build and attempt; a stale queued trigger fails without Store mutation.
   Upload once, persist the exact draft receipt, and immediately run the same
   receipt-bound certification checks in this trusted run. Verify the same
   submission ID, an accepted certification/publication state and persisted
   `Immediate` mode, then retain `store-certification-receipt`, including the
   safe package metadata returned by the Store after certification starts.
   Certification failures are not success. No certification is skipped and
   no retry is made.
   Unknown or edited drafts are preserved; duplicate/older published package
   versions are rejected before upload. A failed submit retains its sanitized
   receipt and ownership evidence; a later upload may clean up only an unchanged
   automation-owned draft that is still `PendingCommit`.

`-WhatIf` validates release/receipt inputs but never authenticates or calls Store.
Offline tests mock CLI boundaries; they do not prove live Store ingestion.

### Why upload never implicitly replaces an existing draft

In v0.4.4, `publish --noCommit` still **deletes and recreates** an existing draft
for an already-published product. `--noCommit` is not a preservation flag.
The old API-created draft **1152921505702349635** was last observed
`PendingCommit` / `Immediate` with only the old **0.1.20.0** packages.
It has not received the tested release and has not been submitted for certification.
The owner has authorized replacing only this exact unchanged failed-pipeline
draft if necessary. Run `inspect-draft`, review its fingerprint and current
state, then explicitly dispatch `discard-draft` with that inspection run ID.
It has no automation ownership artifact, so automatic cleanup does not delete it;
other human drafts must not be discarded
under this authorization. Then `release-latest` uploads and requests
certification in one run. An inspection fingerprint proves
unchanged CLI-visible state, not that a draft belongs to automation.

Do not hard-code the earlier tested v0.2.4601 release. `release-latest` resolves
the current official release at execution time; normal checksum and receipt
validation remains authoritative.

### Official CLI constraints

The v0.4.4 contracts were verified directly against its tagged implementation
(not inferred from our mocks):

- [`UpdateCommand.cs` lines 28–41, 164, 204](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/Commands/Submission/UpdateCommand.cs):
  the inline product argument is optional, `--payload` resolves the JSON file,
  and successful `submission update` writes serialized JSON via `StandardOutput`.
- [`PublishCommand.cs` lines 92–150](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/Commands/PublishCommand.cs):
  `--appId`, `--inputDirectory`, `--noCommit`, and `--uploadTimeout` are actual
  parser options; 600 seconds is within the accepted 100–100000 range.
- [`WinUIProjectConfigurator.cs` lines 26–28](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/ProjectConfigurators/WinUIProjectConfigurator.cs)
  and [`FileProjectConfigurator.cs` lines 169–179](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/ProjectConfigurators/FileProjectConfigurator.cs):
  recursive MSIX discovery is filtered to files whose parent directory's parent
  is the input root. Thus `cli-input\arm64\*.msix` and
  `cli-input\x64\*.msix` are both selected in one invocation.
- [`StoreHostBuilderExtensions.cs` lines 112, 126](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/StoreHostBuilderExtensions.cs)
  registers `--output-stream` on commands; [`StandardOutput.cs` lines 28–29](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/Helpers/StandardOutput.cs)
  writes JSON directly to stdout independently of progress output. The wrapper
  always passes `--output-stream stderr`, captures stdout, and suppresses stderr.

These are source-verified command contracts. Read-only authentication has been
verified; that is not proof of package ingestion or certification acceptance.
The pinned v0.4.4 `submission get` accepts only a product ID plus optional
module/language selectors; internally it selects the pending submission when
one exists, otherwise the last-published submission. `apps get` exposes the
published and pending submission IDs, not their complete metadata. Therefore,
with a pending draft present, the official CLI has no read-only option to fetch
both full submissions for a retrospective comparison. The safe structural
summary is captured at the upload boundary, where the pipeline still holds the
pre-upload published snapshot and the newly returned draft. Do not infer the
cause of an older mismatch from an inspection fingerprint alone. These limits
are confirmed in the pinned [`submission get` implementation](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/Commands/Submission/GetCommand.cs),
the [`GetAnySubmissionAsync` selector](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/Helpers/IStorePackagedAPIExtensions.cs#L25-L49),
and the [`DevCenterApplication` model](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.API/Packaged/Models/DevCenterApplication.cs).

- Loose-MSIX publishing takes one file. Use the existing WinUI project publisher,
  which finds both validated MSIX files exactly one directory below the isolated
  input directory. Do not publish ARM64 and x64 in two consecutive commands:
  the second would replace the first draft.
- v0.4.4 matches old package entries by extension, not architecture.
  The small official `submission update` follow-up marks **all** old entries
  `PendingDelete` and selects `Immediate`. It is not a custom HTTP uploader.
- The CLI uses typed `DevCenterSubmission` serialization. It can drop unknown
  server fields and serialize model defaults; the former REST claim of
  arbitrary unknown-field preservation no longer applies. Fingerprints protect
  everything visible through this pinned CLI, including listings, pricing,
  overrides and assets, but cannot detect fields the CLI does not expose.
  Review the pinned model/source when adding Store features or upgrading CLI.
  Do not use this workflow for unsupported metadata. A visible metadata change
  leaves the draft uncommitted for inspection; there is no automatic rollback.
- The workflow is intentionally **package-only**. The legacy
  `upload-listings-draft`/`resume-draft` paths and their custom mapper/recovery
  machinery were removed, rather than porting bespoke transport into a CLI shell.
  Localized JSON/screenshots remain review inputs; this workflow never rewrites
  them. Listing changes need a separately reviewed official tooling workflow.
- On any upload/update/commit failure, **inspect first**. Never automatically
  retry `publish`, delete a failed draft, or fall back to browser upload/submit.
  Receipts from the old REST workflow are intentionally rejected.

## Validation and release boundary

Run `.\tests\tools\Test-StoreRelease.ps1` and
`.\tests\tools\Test-CiBuild.ps1` without credentials/network. The workflow runs
Store guards before loading environment credentials.

Review one coherent PR before activating delivery. The owner has authorized
automatic package upload, certification and publication as soon as certification
passes, including manual `release-latest` for the current release. This does not
authorize an implicit pricing change or deleting an unknown draft. Any blocked
pricing/inspection gate must be resolved explicitly through the release pipeline;
never fall back to browser upload, submission or pricing inspection.
