# RedrainCast — certification test notes

Draft for the Partner Center certification-notes field. Copy only the section
below after the submission owner resolves the internal checklist.

## PASTE: certification notes

RedrainCast is a native Windows app with sender and receiver roles. No login,
test account or cloud service is required.

Setup:
- Use two compatible RedrainCast devices on the same trusted local network:
  Windows 11 22H2+ (ARM64 or x64), or a supported Android peer.
- Obtain Android only from the public releases repository:
  https://github.com/arisewanggithub/RedrainCast-Releases
- Windows distribution is through Microsoft Store; public availability is
  subject to publication. If the Windows candidate is private, obtain a second
  Windows peer through the submission owner's Partner Center-approved test
  distribution. Do not install an unsigned submission artifact or grant trust
  to a self-signed certificate as a workaround.
- Sending requires supported hardware video encoding; Windows receiving requires
  supported D3D11 hardware decoding. There is no software video fallback.
  Start with H.264 and an SDR source.
- With user consent, allow the app through Windows Firewall on the trusted
  Private network. Do not disable the firewall or configure internet port
  forwarding. Discovery uses UDP 27183; Windows assigns available TCP listener
  ports, shown on the receiver as IP : Port.

Test:
1. Open RedrainCast on the receiving device. Confirm READY and its current
   IP : Port. Available receivers accept incoming casts automatically; there
   is no incoming approval prompt.
2. On the Windows sender, choose a display or window to share, then use the
   nearby receiver's Send button. Complete any Windows permission prompts
   yourself; permissions are not automatically granted.
3. If discovery fails, connect manually using the receiver's current numeric
   IP and TCP port (placeholder: <receiver IP>:<current port>). The port may
   change when listening restarts.
4. Confirm the selected content appears. Windows receiving opens fullscreen;
   Enter or F toggles fullscreen, Esc exits fullscreen. End reception using
   Stop on the sender. Receiving pauses while that Windows app sends and
   automatically resumes afterward. Reverse roles to test both directions.
5. Optionally enable output-audio sharing and play an owned test sound.
   This shares output audio, not microphone input.

Use only trusted devices/subnets: screen/audio traffic and discovery are
unauthenticated and unencrypted. This is not Miracast or Google Cast.
The packaged full-trust desktop app legitimately starts a packaged capture
worker child process when sending. The standard app does not record media.

## INTERNAL — DO NOT PASTE

- **Not release acceptance or proof of Store publication.** Reviewed-source
  portable regression results: 1,134 tests passed after deterministic CI-fixture
  synchronization. The separate Store guard suite passed 61 offline checks on
  Windows PowerShell 5.1, including read-only preflight and alignment with the
  actual package-language export. These checks do not establish
  native x64 playback or human capture acceptance.
- **Fresh unsigned packaging passed only, 2026-11-07.** Both architectures
  passed publication, branding, localization, manifest and shipping-boundary
  validation at version 0.1.20.0:
  - ARM64:
    `artifacts\store-initial-20261107-0925\package-arm64\RedrainCast-arm64.msix`.
    SHA-256:
    `4389c15c5f1a9784be595f970bbd2b53ae18aa21c7565b1363540bf55a5c2d2f`.
  - x64:
    `artifacts\store-initial-20261107-0925-x64-restore\package-x64\RedrainCast-x64.msix`.
    SHA-256:
    `a3999e40958d7e147d3fb8acc1fa5c2092d9e6aff20c94d1cb9959b0b620f250`.
  Both were uploaded and saved in the owner-authorized initial Partner Center
  draft, Submission 1 (`1152921505702342229`). The portal marked Packages
  Complete; this is not installed-package or certification acceptance.
- Owner-authorized Free pricing and all worldwide markets were saved and
  verified in the draft on 2026-11-07. All 240 supported market rows showed
  zero retail prices; this does not constitute publication.
- Owner-authorized IARC ratings were finalized in the draft on 2026-11-07;
  the portal marked Age ratings Complete. Generated classifications include
  Microsoft/IARC 12+, ESRB Teen, PEGI Parental Guidance and Germany USK 16+,
  with user-interaction descriptors. The questionnaire declares native
  screen/audio content exchange and does not claim reporting, moderation,
  user blocking, or invitation-only controls. The first IARC rating ID remains
  pending publication. These ratings do not replace physical-device acceptance.
  The owner subsequently selected automatic publication after certification
  for this initial submission. No certification was submitted.
- **NOT ACCEPTED — human physical-device checks required:** install/launch
  under the intended Store identity; update/settings preservation where
  applicable; consented firewall flow, discovery and manual connection;
  bidirectional physical casting with audible audio, mute/silencing and
  restoration, synchronization, Stop/reconnect/close cleanup; sustained
  session responsiveness and resource behavior. Use the exact final package.
- Known uneven motion/bumpiness and A/V timing remain deferred quality work,
  not verified fixes. Keep these notes internal, not in public Store listing
  copy. A reproducible hang, crash, broken Stop or unusable audio still requires
  triage before submission. Preserve Qualcomm AV1 and HDR-to-AV1 guards.
- Submission owner must confirm peer availability and approved Windows test
  distribution. No Windows binary upload or public Store availability is
  established by this document.
- Windows privacy source: [PRIVACY.md](../../PRIVACY.md).
  Published cross-platform policy:
  https://redraincast.redrain.ccwu.cc/privacy/.
  Home: https://redraincast.redrain.ccwu.cc/.
  Support: https://redraincast.redrain.ccwu.cc/support/.
  These URLs were saved and read back in this product's initial draft
  properties on 2026-11-07. The submission remains unpublished; no Store API
  workflow, certification submission or final publication was executed.
- Follow the existing [screenshot plan](README.md#screenshot-plan); no app
  screenshots were captured here. Review [listing preparation](README.md),
  [release acceptance](../../STORE.md), [product behavior](../../README.md)
  and [Store identity](../../StoreIdentity.json) before submission.

The browser draft preparation and package checks above are recorded separately
from physical-device acceptance. No signing, installation or capture operation
was performed as part of that Store preparation.
