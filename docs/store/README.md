# Store listing preparation

The [en-US draft](listings/en-US.json) is prepared for **RedrainCast**
(Store ID `9P1153MP6R32`), not for Cloud Drive Player. Files are ready for review
in this repository; the listing assets are committed.

On 2026-11-07, owner-authorized browser preparation created
[Submission 1](https://partner.microsoft.com/en-us/dashboard/products/9P1153MP6R32/submissions/1152921505702342229)
in Partner Center. The verified package identity, privacy/support/website
properties, fresh ARM64/x64 0.1.20.0 packages and all 22 listing texts were
saved to this **unpublished draft**. All 22 listings are Complete with two
captioned screenshots each and the shared 300 x 300 Store icon. A fresh official
export verified all texts, captions, icon references and the original 44 image
references in Home-then-Sharing-settings order. This is not certification or
release acceptance. Owner-authorized **Free** pricing and **all worldwide
markets** were saved and read back on 2026-11-07; the per-market review showed
zero retail prices in all 240 supported markets. Owner-authorized IARC ratings
were saved and the Age ratings section marked Complete: Microsoft/IARC 12+,
ESRB Teen, PEGI Parental Guidance and USK 16+ in Germany. Exact-package
physical-device acceptance still requires owner checks. On 2026-11-07, the
owner changed this initial submission to **publish as soon as certification
passes**, replacing the earlier manual hold. Certification has not been
submitted; the separately guarded REST update pipeline retains its Manual
publication mode.

## Localized listing drafts

The [listings folder](listings) contains one draft for each of the app's
22 supported languages. The en-US marketing text is unchanged; each other language was
prepared by a separate translation agent and checked for schema, field lengths,
keywords, product identity, requirements and privacy meaning.

| Language | Draft |
|---|---|
| Arabic | [ar.json](listings/ar.json) |
| German | [de.json](listings/de.json) |
| English (US) | [en-US.json](listings/en-US.json) |
| Spanish | [es.json](listings/es.json) |
| French | [fr.json](listings/fr.json) |
| Hindi | [hi.json](listings/hi.json) |
| Indonesian | [id.json](listings/id.json) |
| Italian | [it.json](listings/it.json) |
| Japanese | [ja.json](listings/ja.json) |
| Korean | [ko.json](listings/ko.json) |
| Malay | [ms.json](listings/ms.json) |
| Dutch | [nl.json](listings/nl.json) |
| Polish | [pl.json](listings/pl.json) |
| Portuguese (Brazil) | [pt-BR.json](listings/pt-BR.json) |
| Russian | [ru.json](listings/ru.json) |
| Swedish | [sv.json](listings/sv.json) |
| Thai | [th.json](listings/th.json) |
| Turkish | [tr.json](listings/tr.json) |
| Ukrainian | [uk.json](listings/uk.json) |
| Vietnamese | [vi.json](listings/vi.json) |
| Chinese (Simplified) | [zh-Hans.json](listings/zh-Hans.json) |
| Chinese (Traditional) | [zh-Hant.json](listings/zh-Hant.json) |

These are reviewable content files, not a Partner Center import format. The
[guarded REST pipeline](pipeline.md) has an explicit `upload-listings-draft`
mode that maps supported fields and uploads the 44 screenshots after exact
reviewed-commit confirmation. It applies only after the first manual
publication and never adopts an existing pending draft. Optional empty fields
preserve existing values; product URLs and Store branding artwork still
require Partner Center review. No live upload was performed while implementing
the pipeline.

For manual preparation, select
the corresponding supported listing language in Partner Center before copying
fields; do not infer a numeric portal language ID from a file name. Read JSON as
UTF-8 when validating or scripting it, including in Windows PowerShell 5.
Two screenshots are prepared for every listing; screenshot approval and all
drafts still need owner review before upload. Public URLs, brand names,
empty first-release fields and internal
submission-review metadata are intentionally shared across locales.

### Initial-submission CSV import

Partner Center's official **Export listings / Import listings** can populate
the first submission; the REST update pipeline intentionally cannot. Use the
CSV exported from this exact product/submission as the schema rather than
inventing field IDs. The actual export uses the same 22 lowercase language
keys as the packages, including neutral `ar`, `de`, `id`, `zh-hans` and
`zh-hant`, not region-specific replacement keys.

The CSV contains both `ID` (field identifier) and `id` (Indonesian).
PowerShell's default `Import-Csv` cannot distinguish them. Use unique temporary
column aliases with the native CSV parser, then restore the exact headers.
Preserve quoted multiline descriptions, empty paragraphs, UTF-8, separator
rows and the package-generated `OverrideLogosForWin10=False` values.
Round-trip every field before importing; `TextFieldParser` alone can discard
blank paragraphs inside quoted descriptions.

An image preview and a hidden progressbar can precede persistence in the
saved listing model. Verify a fresh official export, including icon references,
instead of accepting preview dimensions or an overall Complete badge alone.
For icon-only CSV changes, omit unchanged screenshot rows: reimporting existing
image URLs can reorder complete screenshot/caption pairs during asynchronous
rehydration. Compare the ordered asset references and their captions afterward.

CSV-only import saved all 22 texts in this draft while leaving listings
incomplete until screenshots were supplied. The automated browser's directory
upload did not work; individual file uploads did. Do not change browser
permissions or portal upload attributes as a workaround. For a normal manual
folder import, follow Microsoft's root-folder-relative asset path format.
Never import the internal `submissionReview` fields or overwrite unrelated
existing listing content.

[Microsoft's CSV import/export documentation](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/import-and-export-store-listings)

## Fields checked in Partner Center

The user-provided Cloud Drive Player en-US listing was inspected read-only on
2026-11-06 as a form reference. Its text, screenshots, identity and product
claims are not reused. Only the form's field names, limits and asset dimensions
were used. These are form constraints, not proof of RedrainCast certification.

| Field | Observed constraint | RedrainCast preparation |
|---|---|---|
| Product name | Required; select a reserved name | `RedrainCast`; confirm on the correct product |
| Description | Required; 10,000 characters | Draft includes features, requirements and trusted-LAN privacy information |
| What's new in this version | 1,500 characters; page says leave blank for first submission | Empty |
| Product features | Eight visible entries; 200 characters each | Eight entries |
| Short title | Optional; 50 characters | Empty; full product name is already short |
| Voice title | Optional; 255 characters; Xbox-oriented | Empty |
| Short description | Input limit 1,000; page recommends 270 or fewer | Draft under 270 |
| Keywords | Up to seven; 40 characters each; at most 21 words total | Seven relevant terms |
| Copyright and trademark info | Optional; 200 characters | Empty pending owner review |
| Additional license terms | Optional; 10,000 characters | Empty; no additional legal terms invented |
| Developed by | Optional; 255 characters | `RedRain`, matching the configured publisher display name |

`submissionReview` in the JSON is local tracking metadata, not a Partner Center
upload field. Public URLs are prepared for the applicable product settings:

- Home: https://redraincast.redrain.ccwu.cc/
- Privacy: https://redraincast.redrain.ccwu.cc/privacy/
- Support: https://redraincast.redrain.ccwu.cc/support/

These pages are published on the configured custom domain with HTTPS enforced.
Enter the URLs in Partner Center after reviewing the policy.
A local Markdown policy is not a hosted privacy-policy URL.
Review the draft before copying it into the **RedrainCast** listing.

## Images

PNG assets are generated directly from the Android logo's vector geometry at
each output size, rather than enlarging a small bitmap. The shared brand is a
white cast glyph on `#9F2036`. Package icons and Store images have different
roles; uploading a listing icon does not change installed-app icons.

| Listing slot | Prepared dimensions | Status for this Windows desktop app |
|---|---|---|
| [App tile icon](../../assets/store/StoreLogo300x300.png) | 300 x 300 | Recommended; otherwise Store can use package artwork |
| [Store display 150](../../assets/store/StoreLogo150x150.png), [Store display 71](../../assets/store/StoreLogo71x71.png) | 150 x 150; 71 x 71 | Optional overrides shown in the inspected form |
| [Box art](../../assets/store/BoxArt2160x2160.png) | 2160 x 2160 | Optional |
| [Poster art](../../assets/store/PosterArt1440x2160.png) | 1440 x 2160 | Optional; **2:3** by dimensions |
| [Super hero art](../../assets/store/HeroArt3840x2160.png) | 3840 x 2160 | Optional promotional artwork |
| Desktop screenshots | Real PNG screenshots, at least one | **44 prepared: Home + Sharing settings for all 22 locales; owner approval pending** |

The inspected form calls 1440 x 2160 poster art "9:16"; that label is
mathematically inconsistent. Use the specified pixel dimensions. Current
Microsoft documentation correctly calls it **2:3** and distinguishes
game-oriented poster/box art from the recommended 300 x 300 app icon.
Xbox-only artwork and trailers are not required for this desktop-only app.

Regenerate or verify every size from the checked-in
[canonical vector](../../assets/branding/RedrainCast.svg):

```powershell
powershell.exe -NoProfile -STA -File .\tools\Generate-BrandAssets.ps1
powershell.exe -NoProfile -STA -File .\tools\Generate-BrandAssets.ps1 -Verify
```

The [asset manifest](../../assets/branding/asset-manifest.json) records dimensions,
hashes, palette and Android source provenance. The generator needs only the
checked-in vector, not an Android checkout. The 44, 50 and 150 pixel Windows
package icons are under [assets/package](../../assets/package).
`tools\Package.ps1` verifies the generated assets before packaging, uses those
three package icons and the shared burgundy background, and checks their hashes
again inside the completed MSIX. A new package is needed to change installed
icons; existing candidate packages are not overwritten.

The same vector also generates [RedrainCast.ico](../../assets/package/RedrainCast.ico):
16, 20, 24, 32, 40, 48, 64, 128 and 256 pixel PNG frames, each rendered directly
at its native resolution (not resized from a package PNG). `ApplicationIcon`
embeds these frames in the app EXE; `MainWindow` sets `AppWindow.SetIcon` from
the required published `Assets\RedrainCast.ico` for the title bar and taskbar.
Both portable and MSIX builds carry that file. Publication validates its source
hash, every embedded EXE frame and actual Win32 `LoadImage`/`ExtractIconEx`
pixels without launching the app. Packaging also verifies the ICO inside MSIX.
Manifest artwork alone does not set the portable EXE/window icon. Already
running older builds keep their old icon until reopened from a new build;
no system icon-cache reset is required.

In the inspected form, Store display icons must be under 5 MB; other Store
logos and screenshots must be under 50 MB. Desktop screenshot guidance is
1366 x 768 or larger, including 3840 x 2160. The form advertised up to 30 uploads,
while Microsoft documentation describes up to 10 Desktop screenshots. Two real
screenshots per locale are prepared, safely below either limit; confirm the
current RedrainCast form before uploading.

### Screenshot plan

Prepared: **44 actual native WinUI screenshots across all 22 supported locales**,
with one Home image and one open Sharing settings image per locale. The PNGs
are under [assets/store/screenshots](../../assets/store/screenshots); each
locale's [listing JSON](listings) contains its two relative file references,
`Desktop` device family and captions copied from that locale's app resources.
These JSON files are preparation metadata, **not a Partner Center import format**.

The [capture manifest](screenshots/manifest.json) records every image's SHA-256,
native physical dimensions (1902 × 1431), byte size, UI/MRT language and caption,
plus candidate/source hashes, isolated-fixture provenance and validation results.
[Screenshot preparation status](screenshots/preparation-status.json) tracks
readiness separately from owner approval. All 44 images were reviewed in
internal contact sheets, with full-size representative checks; owner review
before upload remains pending.

Capture used Windows Graphics Capture for the new fixture's exact same-process
HWND only, at native resolution and 150% DPI, without bitmap resizing, overlays
or redaction. An artifact-only copy of the current production UI reused the
isolated localization fixture's injected preferred-language reader and real
Home/ContentDialog controls. The copy added capture orchestration, not a shipping
app flag or feature; production sources and the corrected candidate were unchanged.
The earlier blocked attempt's evidence remains in the internal artifact report.

All device/source names are generic fixture data, and `192.0.2.10` / `192.0.2.20`
are RFC 5737 documentation addresses set **before** native rendering. The real
isolated receiver listened on loopback only; the nearby receiver and selected-source
label are synthetic, no source was opened, and no peer connection or casting
success is claimed. No user desktop, other window, media, source enumeration,
audio, OS language settings or persisted app settings were used. This is UI/asset
proof, not packaged-app, audio, native-picker or physical casting acceptance.

Do not substitute fabricated screens or another app's screenshots. Keep captions
at most 200 characters and avoid added marketing overlays or logos. Upload neither
the internal contact sheets nor fixture reports as Store screenshots.

## Before submission

- Confirm the draft and published support/privacy details, then enter their
  prepared URLs in the applicable product settings.
- Review and approve the prepared localized screenshots before uploading.
- Review [the release acceptance checklist](../../STORE.md).
- Keep known issues and engineering acceptance findings in internal
  documentation, not the customer-facing listing.
- Upload only to the correct RedrainCast product, with separate authorization.
- Do not claim stable 60 FPS, fixed A/V timing, generic Miracast/Google Cast
  support, encryption, or recording features absent from the shipping app.

References:

- [Microsoft: screenshots, images and trailers](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images)
- [Store identity](../../StoreIdentity.json)
- [Privacy policy source](../../PRIVACY.md)
- CloudDrivePlayer's local `scripts/generate-store-logos.ps1` demonstrates
  native-resolution WPF vector rendering. Its product-specific artwork and
  marketing copy are not part of RedrainCast.
