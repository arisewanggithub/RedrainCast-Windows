# Microsoft Store releases with the official CLI

The manually dispatched **Microsoft Store release** workflow uses Microsoft's
[`microsoft-store-apppublisher`](https://github.com/microsoft/microsoft-store-apppublisher)
setup action, pinned to **v1.4** (`cc9910a8d59f2eb55cbb83df0a3800cf3b5300e0`),
and [`msstore-cli` v0.4.4](https://github.com/microsoft/msstore-cli/releases/tag/v0.4.4)
(`4fa163e6dbe971359e0a03a6ac30477978a1e330`). It no longer implements OAuth,
submission REST transport, Azure uploads, commit transport, or retry/recovery.
See Microsoft's [CLI commands](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/commands).

## Gates and credentials

Only manual dispatches on `main` in `arisewanggithub/RedrainCast-Windows` may
operate on **9P1153MP6R32**. Every dispatch requires that exact product
confirmation and the `microsoft-store` environment's approval rules. Operations
are serialized. Do not approve another publisher/browser changing the draft
while a workflow is active: the Store API has no compare-and-swap protection.

The environment needs these four secrets:

- `PARTNER_CENTER_TENANT_ID`
- `PARTNER_CENTER_CLIENT_ID`
- `PARTNER_CENTER_CLIENT_SECRET`
- **`PARTNER_CENTER_SELLER_ID`** — the Partner Center seller/account ID, not the
  tenant ID or Store product ID. It is required for noninteractive CLI configuration.

The migration's secret-name-only check found the first three, but **no seller ID**.
The owner must add it; no credential values were retrieved. Run only on a fresh
GitHub-hosted Windows runner. CLI configuration is reset in `finally`; never
upload runner profiles, CLI logs, raw responses, or the private payload.
CLI scratch files are redirected into the workflow's repository work directory.
Both CLI output streams are suppressed except parsed JSON held in memory.
Only sanitized fingerprints/receipts are retained in private Actions artifacts.

## Pricing is an explicit gate

The official CLI [pricing preservation source](https://github.com/microsoft/msstore-cli/blob/v0.4.4/MSStore.CLI/Helpers/IStorePackagedAPIExtensions.cs#L795-L857)
documents that `pricing.priceId = Base` is rejected by the legacy packaged
submission API. Removing/emptying `priceId` can instead silently make the app
free; omitting the pricing object is rejected. This is a plausible explanation
for earlier `InvalidParameterValue` failures, not proof of their actual cause.

Ordinary uploads preserve a usable existing price without `--priceId`. `Base`,
missing pricing, or a missing/blank base price **stop before any upload**.
`inspect-draft` reports only a safe price classification, never market data.
`Base` is not evidence that the product is free.

The optional `approved_base_price_id` input is an **explicit pricing-change
authorization** (`Free`, `NotAvailable`, or a real `Tier` number). Only supply it
after the owner verifies the intended actual price in Partner Center or approves
persisted price evidence. It passes the exact value to official `--priceId`.
This sets a single base price; it is not a promise to preserve per-market pricing.
Never choose `Free` just to bypass an API error. No pricing change is authorized
by implementing or reviewing this migration.

## Operations

1. **`preflight`**: official `apps get` and `submission get`; verify identity,
   consistent pointers, and round-trippable pricing. No mutation.
2. **`inspect-draft`**: supply the exact pending submission ID. Save a fingerprint
   of the CLI-visible `PendingCommit` draft and a safe price classification.
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
   Refuse **every** existing pending draft, including a prior automation draft.
   Invoke official `publish --appId 9P1153MP6R32 --noCommit` on the existing
   WinUI project with the isolated two-architecture `--inputDirectory`.
   This does not rebuild the packages. Official project detection may restore
   the WinUI project's dependencies; the workflow supplies .NET 10 for this.
   Official `submission update --payload` then retires all old packages and
   saves `Immediate` publication. Re-read and verify preserved metadata/listings
   and exactly the new pair; save a schema-3 receipt. No certification.
5. **`submit-draft`**: supply the successful upload's `draft_run_id`. Download its
   trusted receipt, re-download and validate the identical official release,
   and verify exact draft/published pointers and full CLI-visible fingerprint.
   Call official `submission publish`, then `submission status`.
   **Certification is requested and publication is automatic when it passes.**
6. **`status`**: official read-only status request, suppressing raw service messages.

`-WhatIf` validates release/receipt inputs but never authenticates or calls Store.
Offline tests mock CLI boundaries; they do not prove live Store ingestion.

### Why upload does not accept an existing draft

In v0.4.4, `publish --noCommit` still **deletes and recreates** an existing draft
for an already-published product. `--noCommit` is not a preservation flag.
The old API-created draft **1152921505702349635** was last observed
`PendingCommit` / `Immediate` with only the old **0.1.20.0** packages.
It has not received the tested release and has not been submitted for certification.
The migration must inspect it and obtain explicit discard authorization; it
must not silently adopt or destroy it. An inspection fingerprint proves
unchanged CLI-visible state, not that a draft belongs to automation.

The tested release is **v0.2.4601**, main Windows build **42551625236**,
commit **24256a9d49d015051db14bbefe409aa2e90d5a7b**. Previously validated
ARM64/x64 asset lengths are **175194142 / 184858073 bytes**; the normal release
receipt validation remains authoritative. Nothing was uploaded during migration.

### Official CLI constraints

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

Migration implementation and offline validation do **not** authorize a live
discard, price change, upload, certification request, or merge. Review one
coherent PR first. Remaining live inputs: seller ID, independently verified
pricing/explicit approval if required, inspected-draft discard authorization,
then separate upload/receipt-bound certification approvals.
