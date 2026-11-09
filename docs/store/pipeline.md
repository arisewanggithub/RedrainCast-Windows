# Guarded Microsoft Store update pipeline

**Guarded automation, not an executed Store API submission.** The manual
`.github/workflows/store-release.yml` workflow is prepared for `main`.
Owner-authorized setup created the `microsoft-store` GitHub environment with
a deployment policy restricted to the `main` branch. The existing authorized
`StoreAppPublisher` Entra application is reused without changing its roles or
existing credentials. At the owner's request, the dedicated RedrainCast client
secret uses Azure's maximum **730 days (24 months)** and expires **2028-11-06**.
The initial 180-day credential was retired only after replacement authentication
and the hosted read-only preflight passed. Rotate the replacement before expiry;
Microsoft recommends shorter lifetimes, and custom lifetimes cannot exceed
24 months.
All three environment secrets have been configured privately. The client
secret was transferred using Azure's value-copy button and encrypted GitHub
secret input, never placed in source, logs or artifacts; its clipboard entry
was cleared. Read-only token acquisition and exact product identity access
were verified without modifying the initial draft.
No API upload or certification request has been executed by this workflow.
The initial submission was prepared separately through Partner Center.

## Supported Store format

The pipeline consumes the existing Windows build's **self-contained MSIX**
packages for ARM64 and x64. Both the app and its separate capture worker retain
their existing self-contained runtimes. `Package.ps1` orchestrates and validates
the pinned official `Microsoft.Windows.SDK.BuildTools.MSIX` MSBuild targets,
which generate the manifest, complete root PRI and MSIX; it does not assemble
packages using handwritten MakePri/MakeAppx commands. Validation checks compiled
WinUI/framework resources, every supported language/key and shell resource
lookup, plus byte-identity of the published app/helper payload in the package.
PR and release packaging also run a bounded 90-second x64 extracted-payload
startup smoke check using `--package-startup-self-test`, requiring exit zero and
`passed=true` in its JSON report. It checks an isolated window, a finite positive
XAML root, resources and loopback receiver initialization without input injection,
capture or settings changes. The hosted x64 runner cannot execute ARM64. This
does not validate input/layout acceptance, install a package, exercise Store
identity, alter certificate trust, or replace installed-device testing.

The normal local packaging script uses these same settings: self-contained
publishing is enabled, trimming is disabled, and single-file publishing is not
enabled. MSIX contains the executable, worker, runtime files, resources and
notices in one package. Direct installation additionally requires a trusted
signature; an unsigned submission package is not directly installable.

Self-contained deployment is valid for both local and Store distribution.
Trimming is an optional optimization for compatible apps, not a Store
requirement. A single-file executable is primarily a portable/direct-distribution
option; it is not needed for this Store route. The separate experimental
trimmed/single-file attempt is not used by the normal release pipeline.

Successful main CI builds also publish official **non-prerelease `vX.Y.Z`**
GitHub releases in `arisewanggithub/RedrainCast-Windows`, with ARM64/x64 MSIX
downloads only (no portable ZIP). Repository visibility still controls access.
The [release version and signing contract](../../README.md#official-msix-releases-and-actions-storage)
documents deterministic run/attempt versions and optional persistent signing.
Unsigned MSIX downloads are submission inputs, **not directly installable**;
signed direct downloads require certificate trust. Store distribution is
independently signed by Microsoft. Actions retains only the small sanitized Store receipt,
not Windows build binaries. Never upload Windows binaries to `RedrainCast-Releases`.
Optional signing credentials reach only trusted main release packaging.
The same final signed MSIX bytes are uploaded to GitHub and packaged for Store
submission; the Store applies its own distribution signature. Testing these
same-identity packages can interact with an existing Store installation and its
data: installation, replacement and certificate trust always require deliberate
owner action, never an automatic pipeline step.

## Owner prerequisites (not automatically configured)

1. Complete the **first publication manually in Partner Center**, including
   age ratings, listing, privacy/support properties, price and availability,
   package review and physical-device acceptance in [STORE.md](../../STORE.md).
   `9P1153MP6R32` is not yet published at implementation time. This workflow
   intentionally fails without a last-published submission and leaves the
   current first-release draft untouched.
2. Associate the seller's Partner Center account with its Microsoft Entra
   tenant. Add an Entra application to Partner Center with Microsoft's
   required **Manager** role. An authorized account administrator must do
   this; the workflow cannot register applications or change tenant roles.
3. The GitHub environment **`microsoft-store`** has been created and its
   deployment branches restricted to `main`. Review repository access, branch protection,
   workflow changes, private CI release access and Actions receipt access. Add these environment secrets:

   | Name | Value |
   | --- | --- |
   | `PARTNER_CENTER_TENANT_ID` | Associated tenant GUID |
   | `PARTNER_CENTER_CLIENT_ID` | Entra application/client GUID |
   | `PARTNER_CENTER_CLIENT_SECRET` | Client secret **value**, not secret ID |

   Never place their values in source, command-line arguments, artifacts,
   screenshots or workflow inputs. Rotate/expire the client secret through
   the authorized owner. This direct REST integration does **not** require a
   seller ID or Microsoft Store CLI.
4. If your repository plan supports environment required reviewers, configure
   them explicitly. **An environment name does not enforce approval.** Required
   reviewers for a private repository are not available on ordinary
   Free/Pro/Team plans; availability depends on GitHub's plan/features (for
   example Enterprise). Where unavailable, restrict dispatch/write access,
   review changes and use the explicit stage/product confirmation below.
   Certification dispatch authorizes automatic publication after successful
   certification. A typed input is an
   operator acknowledgment, **not** an independent security boundary.
5. Confirm the API supports your account/product features. Microsoft documents
   restrictions including mandatory updates. This product uses **Pricing
   Version 2** (its pricing page has **Review price per market**).
   Microsoft permits non-pricing modules to be updated but returns an unknown
   pricing tier; the workflow preserves returned pricing rather than editing
   it, and live round-trip validation is still required after first publication.
   Sales and
   some privacy/support properties are not fully represented by this legacy
   API. Review those settings manually; preserving returned JSON cannot verify
   fields Microsoft does not expose. Unsupported features or unexpected
   normalization fail closed rather than silently changing settings.

## Explicit upload and certification dispatches

Both jobs run only for a manual `main` dispatch in the private
`arisewanggithub/RedrainCast-Windows` repository. The workflow token has only
`contents: read` and `actions: read`; Store secrets reach only the environment
job, never PR jobs. All action references reuse the pinned official SHAs in
`windows.yml`.

### `inspect-draft` (read-only; exact existing pending draft)

Dispatch on `main` with the product confirmation and `inspect_submission_id`
set to the exact draft ID. Leave build, draft-run and listing-commit inputs empty.
The environment job GETs the application, validates its identity and current
pending pointer, then GETs only that exact submission. A mismatch fails closed.
It never creates, updates, uploads, deletes or commits a draft and creates no
receipt. Ownership remains **Unknown**: a successful GET is not proof of API
ownership, and inspection does not authorize later upload or certification.

The successful run retains a small private `store-inspection` artifact with
the exact ID, prior published pointer and metadata/package fingerprints bound
to the inspection run and checkout SHA. It contains no metadata or upload URLs.
Logs contain only the submission ID, allowlisted status/publication mode,
up to 20 package rows (known RedrainCast filenames, file status, version and
architecture), and allowlisted validation error codes. Unknown fields are
suppressed; no listings, service messages, upload URLs or full responses are
logged. This cannot recreate an earlier failed PUT response or prove that
the draft is valid. Existing-draft refusal and all publication gates remain.

Transport failures now identify authentication, Store application lookup,
draft creation, submission metadata, archive upload, commit or status operations
and HTTP status. JSON and Azure XML errors are limited to 16 KiB and 20
allowlisted error entries; free-form messages and unknown codes are suppressed.
For recognized JSON validation errors, fixed API schema labels and fixed reason
categories mentioned by the response can be logged; arbitrary paths, indices,
parameter values and message text remain suppressed.
Nested legacy `details`/`innerError` envelopes are traversed at most five levels
and 100 nodes; output remains limited to the same fixed labels and categories.
No retry or draft cleanup is automatic. This distinguishes a Store metadata
PUT from an Azure archive PUT without exposing credentials, SAS or metadata.
Workflow run titles include the selected stage to distinguish read-only runs.

### `resume-draft` (explicit package-only recovery; never automatic adoption)

Use only after independently establishing why this exact draft should be
recovered. Specify the exact `inspect_submission_id`, successful main
`inspect_run_id`, reviewed successful main `build_run_id`, and product
confirmation. The trusted inspection artifact must match its run and commit.
Recovery GETs the current application and exact draft, requiring unchanged
pending/prior-published pointers, `PendingCommit`, `Immediate`, and every
inspected metadata/package fingerprint (including file states). Any drift
requires a fresh inspection and deliberate review; no human changes are adopted.

Recovery then performs the same package replacement and ZIP upload as a new
draft, on that exact ID. It never POSTs a new draft, deletes a draft, changes
listings, retries automatically or starts certification. A successful recovery
produces the normal `store-draft-receipt`; use its run ID with `submit-draft`,
which retains all existing build, pointer, metadata and publication checks.
Normal upload still refuses **any** existing pending draft.

New uploads and explicit recovery persist a separate safe
`store-draft-provenance` artifact **before** the metadata PUT, even if a later
transport step fails. This records IDs and fingerprints only, not a successful
upload receipt and not permission to submit. Reinspect before another recovery.

### `preflight` (default; read-only and valid before first publication)

Leave build/draft inputs empty and type the product ID confirmation. This stage
uses the environment credentials to obtain a token and performs only a Store
application **GET**. It validates the pinned product/package/publisher identity
and reports first-publication completion, pending-draft existence and update
eligibility. An unpublished app or existing human draft is reported as ineligible,
not overwritten. No packages are downloaded, no draft/ZIP/receipt is created,
and there is no Store POST/PUT/DELETE. `-WhatIf` does not authenticate or make
any Store or GitHub request. This stage validates setup without waiting for
release; it does not prove that a later package submission will be accepted.

### 1. `upload-draft` (packages only; no certification)

Choose a successful completed `main` run of `.github/workflows/windows.yml`.
Supply its numeric `build_run_id` and type `9P1153MP6R32` in `confirm_store`.
Leave `draft_run_id` empty. Review that build's exact SHA/version and acceptance
results yourself before authorizing an upload.

The script checks the source repository and head repository, workflow path,
event (`push` or `workflow_dispatch`), branch, conclusion, completion, SHA and
positive run attempt. `tools\CiBuild.ps1` is dot-sourced as a library, then
`Receive-CiBuild` receives that exact run ID, attempt and SHA. It verifies the
signed-in source repository is private and derives the official `vX.Y.Z`
release tag from the trusted run number and attempt. It requires a complete
non-prerelease, matching run/attempt/SHA/version manifest provenance and exact
MSIX/checksum asset set. Each MSIX's embedded package identity and architecture
must match `StoreIdentity.json`; its version must equal the release version
with revision zero. The same validated MSIX bytes are copied into the local
architecture folder without extraction or rebuilding.
A missing historical release or validation failure stops processing with an
instruction to rebuild the reviewed main commit and use the resulting
successful Windows run. There is **no fallback to legacy Actions build
artifacts**, another run, another attempt or a latest release.
It requires exactly one MSIX per architecture release download, validates manifest identity,
publisher, architecture, matching versions with zero revision, matching
non-architecture manifest metadata, PRI/runtime/worker/notice presence and
absence of debug symbols/forbidden helpers. The trusted build's existing
packaging checks remain responsible for native binary and resource validation;
this stage does not run the app or recompile it.

Only after these checks does it authenticate via Microsoft's client-credentials
REST endpoint. It reads the exact application and refuses first publication or
**any** existing pending submission. It never adopts or deletes human drafts.
The server's create operation must also reject a racing pending submission.

It creates one API-owned draft copied from the last published submission,
marks all previous package entries `PendingDelete` (preserving their fields),
adds both new packages as `PendingUpload`, and sets `targetPublishMode` to
**Immediate** (automatic publication after successful certification). All
other returned metadata is retained, including all languages, images, pricing,
availability, certification notes and delivery settings.
The complete JSON uses depth 100 and fails on serialization-depth warnings.

For the default package-only mode, one ZIP containing only
`RedrainCast-arm64.msix` and `RedrainCast-x64.msix` is
uploaded once to the server-supplied HTTPS Azure Blob SAS destination using
`Put Blob`. A bounded **1 GiB (1,073,741,824 bytes)** ZIP limit accommodates
both already-compressed MSIX files. Read-only measurements of the existing
`v0.1.20-av-diagnostics\candidate-pcmfix-20261105-230350` pair are
173,985,017 bytes (ARM64) plus 183,646,525 bytes (x64): **357,631,542 input
bytes**, about 341 MiB before ZIP overhead. The old 256 MiB bound could not
accommodate that pair. Do not assume the outer ZIP substantially compresses
the inner MSIX files.

Azure documents **5,000 MiB** for a single `Put Blob` using the already-selected
`x-ms-version: 2019-12-12` (not the 256 MiB limit of older service versions).
The pipeline's 1 GiB bound stays well below that platform limit. The final
ZIP's actual length is checked before any Store authentication or request;
anything larger must be handled manually, not partially uploaded.
It re-reads the draft and verifies metadata/package fingerprints.

The only new Actions artifact is `store-draft-receipt` (14-day retention),
containing `store-draft-receipt.json`: product/draft IDs, previous published ID,
source build run/SHA, two package names/hashes/versions/architectures and
metadata fingerprints. It contains **no** token, SAS URL, full listing JSON,
contact details or binaries. Nothing is uploaded to public GitHub Releases.

### Optional `upload-listings-draft` (reviewed listings plus packages; no certification)

Use this instead of `upload-draft` when an update should also replace the
prepared base listing text and desktop screenshots. This is a separate,
explicitly selected upload mode, not a command to edit an existing pending
draft. It uses the same successful Windows build input and all the same
first-publication, human-draft refusal, package and Immediate-publication guards.
The initial publication still requires Partner Center.

Review all [22 listing drafts](listings) and their **44 screenshots** at the
exact `main` commit that will be checked out by the dispatch. Enter its full
40-character lowercase SHA in `confirm_listings_commit`. A missing, abbreviated
or stale SHA fails before artifact access or Store authentication. This input
acknowledges review of both text and screenshots; it is not independent reviewer
enforcement. The content comes from that workflow checkout, not the older
package build. The receipt records both commits independently.

The upload maps repository locales to the exact lowercase package-language
keys observed in this product's Partner Center CSV export on 2026-11-07:
`ar`, `de`, `en-us`, `es`, `fr`, `hi`, `id`, `it`, `ja`, `ko`, `ms`, `nl`,
`pl`, `pt-br`, `ru`, `sv`, `th`, `tr`, `uk`, `vi`, `zh-hans`, `zh-hant`.
Neutral languages stay neutral rather than creating regional duplicates.
All 22 JSON drafts must be present,
UTF-8, correctly branded and within the checked field/keyword limits.
Screenshot references must point to the expected two local files for that
language, with Desktop family and valid captions. PNG decoding, dimensions
(1366 x 768 through 3840 x 2160) and a size below 50 MiB are checked locally.
The actual ZIP image bytes are hash-checked against the validated files before
any Store call.

Only supported base listing fields are mapped: title, description, features,
keywords, short description, developer studio, and nonempty optional titles,
release notes and legal text. Empty optional draft fields preserve existing
values; this mode cannot clear them. `submissionReview` is never uploaded.
Privacy, support and website URL properties must still be reviewed and set in
Partner Center; Microsoft's legacy listing URL fields are obsolete.

Selected languages retain platform overrides and unknown base fields. Missing
selected languages are added; languages outside these 22 remain unchanged.
Only old Desktop `Screenshot` images in selected base listings are marked
`PendingDelete`; icons, other device-family images and promotional artwork are
retained. Store branding artwork is not uploaded by this stage. Two new images
per language use `imageType: Screenshot`, with the localized caption in
`description` and unique upload-run-qualified file names.

Both MSIX packages and all 44 images go into **one 46-entry ZIP**, uploaded once
with the same 1 GiB bound. There is no second blob upload that could replace
the packages. Pricing, availability, delivery settings and all unrelated
metadata are preserved.

The schema-2 sanitized receipt additionally records the reviewed listing
commit, upload run ID, content fingerprint and image names/languages/SHA256
hashes. It contains no listing text, captions, image bytes, credentials or
URLs. After upload, select `submit-draft` using this successful upload run.
Do not run either upload mode again while that draft exists.

### 2. `submit-draft` (certification request; automatic publication after passing)

Review the upload run's sanitized receipt and the exact package acceptance.
Dispatch again, select `submit-draft`, supply that successful **upload workflow
run ID** in `draft_run_id`, and type the exact Store ID again. `build_run_id` may
be omitted; if supplied it must match the receipt.

The script first validates the receipt run as a successful completed `main`
run of `.github/workflows/store-release.yml` in this same repository. It
downloads only its receipt artifact, not an arbitrary URL or local receipt,
then re-validates the referenced Windows build, receives its exact private CI
archive and verifies both MSIX hashes against the receipt. The receipt remains
an Actions artifact; it is not read from the GitHub release.
For a listing-enabled receipt, its listing commit and upload run must match
that trusted upload workflow run; malformed or incomplete image bindings fail.

It re-reads the application and exact submission, verifies the pending ID,
previous published ID, `PendingCommit` status, Immediate publication mode and all
metadata/package fingerprints. Canonical hashes sort object keys ordinally,
preserve array order and include unknown metadata. Only submission ID, status,
status details, SAS URL and the separately checked package collection are
excluded from the metadata hash. For the 44 explicitly bound new screenshots,
only server-assigned image IDs and upload status are normalized; names, types,
captions and unknown fields remain fingerprinted. Each new screenshot must
still exist in the correct language and be `PendingUpload` or `Uploaded`.
Old image metadata/status is not normalized. New packages may remain `PendingUpload` until
ingestion; `Uploaded` is also accepted. Changed package names, uploaded
versions/architectures, old package metadata, any active-package fields other
than upload status, or human listing/pricing edits block commit. Unexpected
server-side package metadata normalization also fails closed. There is **no
second metadata PUT** that could overwrite edits.

It rechecks the application pointer immediately before one REST `/commit`
request. This starts processing/certification, **not** an immediate public
release. One bounded read-only status request reports the observed processing
state; a successful workflow is not evidence of certification acceptance.
Unknown or failed states cause a failure. Keep monitoring in Partner Center.
The submission is configured to publish automatically after successful
certification; there is no manual-hold or scheduled-publication option here.

## Draft ownership and recovery

- All Store dispatches share `microsoft-store-9P1153MP6R32` concurrency with
  `cancel-in-progress: false`. This serializes this workflow, not Partner
  Center users, other repositories or other API clients.
- Do **not edit an API-created pending draft in Partner Center** between
  upload and API commit. Microsoft warns that this can invalidate API
  ownership. The API has no documented atomic metadata compare-and-swap:
  fingerprint checks narrow, but cannot eliminate, simultaneous human edits.
  Coordinate exclusive ownership; a human draft always takes precedence.
- Failed POST/PUT/upload/commit calls stop. Transport errors report only the
  method and HTTP status, never secret URLs, response bodies or original
  credential-bearing exceptions. A failed upload may leave the newly created
  pending draft; inspect it manually. The workflow has **no deletion requests,
  no adoption, force-overwrite, cleanup submission or automatic retry**.
- A failed upload or receipt-artifact failure has no usable successful receipt;
  it cannot be retried as a new upload over the existing draft. The owner must
  resolve it in Partner Center. A missing private build archive, expired receipt
  artifact or changed metadata similarly requires explicit manual resolution.
  Rebuild unavailable historical inputs; do not hand-edit a receipt or select
  different build bytes to bypass a guard.
- Do not use Microsoft Store CLI `publish` as a shortcut: it can delete pending
  drafts. Its `--noCommit` still mutates Store state and is **not a dry run**.

## Offline validation and preflight

```powershell
.\tests\tools\Test-StoreRelease.ps1
```

The workflow explicitly uses `shell: powershell` (Windows PowerShell 5.1), the
same runtime used for local validation. The dependency-free tests use local,
non-executable ZIP fixtures and fake HTTP/GitHub adapters. They test
unpublished/pending gates, trusted-run filtering, paired MSIX identity/version
checks, metadata preservation, listing field/locale mapping, all 44 real PNGs,
invalid inputs, screenshot byte-change detection, image ingestion transitions,
receipt binding, exact CI downloader run/attempt/SHA wiring, missing or
mismatched private archive rejection without Actions fallback, receipt-only
Actions download, human-edit rejection,
transport redaction and positive upload/commit paths without live credentials.
The entrypoint wiring fixture uses an unchanged script copy and a CI-library
test double; the shared CI tests cover real archive transport and provenance.
They do not establish real Store API acceptance or installed-app behavior.

For an authorized manual-main Actions context, the entrypoint also supports:

```powershell
.\tools\Invoke-StoreRelease.ps1 -Stage upload-draft -BuildRunId 123456789 `
  -ConfirmStore 9P1153MP6R32 -WorkDirectory .\artifacts\store-preflight -WhatIf
```

Replace the example run ID with a real trusted run. `-WhatIf` performs only
private CI archive download/local validation (and the tiny Actions receipt for
`submit-draft`), **no Store authentication or
Store/Azure request**, and creates no upload receipt. It does not check live
Store eligibility. The entrypoint intentionally refuses an ordinary local
shell/non-main/PR invocation; run the portable offline tests locally instead.

## Official references

- [Store submission API prerequisites, Manager role and authentication](https://learn.microsoft.com/en-us/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services)
- [Submission lifecycle and resources](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions)
- [Get application and published/pending pointers](https://learn.microsoft.com/en-us/windows/uwp/monetize/get-an-app)
- [Create draft from last publication](https://learn.microsoft.com/en-us/windows/uwp/monetize/create-an-app-submission)
- [Update complete submission and package fields](https://learn.microsoft.com/en-us/windows/uwp/monetize/update-an-app-submission)
- [Listing fields, image types and obsolete URL properties](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions#base-listing-resource)
- [Official CLI metadata support and pending-draft behavior](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands)
- [Commit for processing](https://learn.microsoft.com/en-us/windows/uwp/monetize/commit-an-app-submission)
- [GitHub deployment environments and plan limitations](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments)
- [Azure Put Blob limits and headers](https://learn.microsoft.com/en-us/rest/api/storageservices/put-blob)
- [.NET trimming incompatibilities](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)
