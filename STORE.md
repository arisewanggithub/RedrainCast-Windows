# Microsoft Store

The new product is **RedrainCast**, Store ID `9P1153MP6R32`.
[Store link](https://apps.microsoft.com/detail/9P1153MP6R32)
(the public page may not exist until publication).

[StoreIdentity.json](StoreIdentity.json) records the package name, publisher
subject and display names from this product's Partner Center identity page.
These are public package identifiers, not credentials. Do not reuse either
older Windows app's identity. Review the values against Partner Center before
the first submission.

The [Store preparation folder](docs/store/README.md) contains the reviewed en-US
draft, field requirements, screenshot plan and links to Android-consistent
vector-rendered logo sizes. These files are local preparation only; they do not
upload or submit a listing.

Public product URLs:

- Home: https://redraincast.redrain.ccwu.cc/
- Privacy: https://redraincast.redrain.ccwu.cc/privacy/
- Support: https://redraincast.redrain.ccwu.cc/support/

Windows downloads are distributed through Microsoft Store only.
`RedrainCast-Releases` is for Android binaries, not Windows packages.
The custom domain is configured and HTTPS is enforced.

1. Prepare native dependencies and publish each target architecture using the
   commands in [README.md](README.md).
2. Package each publication with `tools\Package.ps1`. The fourth MSIX version
   component must be zero and the package version must match the assembly.
   Packaging generates a separate root `resources.pri` with the Store package
   identity for localized manifest descriptions in all 22 languages, with
   en-US fallback. The application's `RedrainCast.App.pri` remains unchanged.
   `localization-validation.json` checks both indexes and the Shell's documented
   absolute-PRI-path lookup; it does not establish installed-package acceptance.
3. Keep signing keys/certificates and Store API secrets outside this repository.
   The generated MSIX files are unsigned submission artifacts; installation
   outside the Store requires a trusted signature and separate signing setup.
4. Upload to the new product, complete pricing/availability, age rating,
   privacy policy URL, description, screenshots, support details and notes.
   First publication must be completed manually. The optional
   [guarded update pipeline](docs/store/pipeline.md) supports explicitly
   dispatched package-only or reviewed listing-and-screenshot uploads and
   certification requests only after a publication exists; final publication
   remains manual. It has not been executed against
   Partner Center as part of its implementation.
5. Certification notes should describe the two-device trusted-LAN setup,
   automatic incoming acceptance, OS-assigned TCP listener ports, fixed UDP 27183
   discovery and application-based Private-network firewall permissions,
   hardware codec requirements, and automatic receive/send role switching.
   Do not claim compatibility with generic Miracast/Google Cast receivers.

Use RedrainCast consistently in the listing, package and screenshots. Scrcpy
is not the app title. Any required attribution belongs in notices/About text
without implying endorsement.

## First-release acceptance

### Trimming feasibility

Self-contained trimmed MSIX is a valid Store format, but no trimmed candidate
is accepted for the current dependency set. The measured untrimmed naming-cleanup
packages were 174,263,615 bytes (ARM64) and 183,929,284 bytes (x64).
Reflection-based JSON was replaced with trim-safe payloads and source-generated
settings/report metadata; contract and reflection-disabled tests passed.

Actual native ARM64 ILLink runs still failed with 35 WinRT `IL2081` diagnostics,
SharpGen `IL2067`/`IL2072`, and, when retaining required built-in audio COM
support, `ComActivator` `IL2026`. Full and partial trim experiments did not
suppress warnings or remove runtime/native dependencies. Resolving these
requires supported dependency or COM-interop work before measuring a successful
trimmed package. Normal publishing and the Store workflow remain untrimmed.

The JSON groundwork's untrimmed publications/packages passed validation on both
architectures, with 1,171 portable tests and 61 offline Store checks. Native
layout passed six cases, but a later fresh media gate had no visible foreground
owner (`HWND/PID = 0`) and blocked all media launches. This is not a trimmed
acceptance or a successful media/synchronization check. Existing uploaded Store
packages were not replaced.

Use the current normal publication, not historical recording or experimental
FFmpeg bundles. Their one-off sources and launchers have been removed; reusable
local regression self-tests remain. Package guards reject recording-export
helpers, standalone capture tools, new mux libraries and debug symbols.
Passing unit tests and creating an
unsigned MSIX do not establish installed-app or physical playback acceptance.

Before submission, check the exact final package:

- Install and launch under the intended Store identity; verify the displayed
  version. Test an update without losing settings if an earlier package exists.
- Check discovery and direct connection on a trusted LAN with the normal
  user-approved firewall flow. Do not silently change firewall permissions.
- Cast in both directions between supported devices, with audible output audio.
  Check synchronization, local playback silencing/restoration, Stop, reconnect,
  and closing either app.
- Run a sustained session and check responsiveness, resource growth and cleanup.
- Review the privacy policy, shipped dependency notices, listing and screenshots.
  Describe hardware requirements and automatic incoming acceptance accurately.

Uneven motion and A/V timing remain known preview-quality limitations; do not
claim they are fixed or that a selected maximum FPS is guaranteed. Retain the
existing Qualcomm AV1 and HDR-to-AV1 restrictions. A reproducible hang, crash,
broken Stop, or unusable audio in a supported workflow needs triage before
submission, not just a listing disclaimer.

This new package will not upgrade installations of ScrcpySend-Windows or
ScrcpyReceive-Windows. No migration or replacement of their Store listings is
performed.
