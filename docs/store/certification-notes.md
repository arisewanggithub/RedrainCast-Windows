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

- **Not release acceptance or proof of Store publication.** Latest reported
  automated acceptance: ARM64, 989 tests passed, zero warnings; 16 shipping
  boundary tests passed after the package-icon update. These are supplied
  release-preparation results, not tests rerun for this document. Do not claim
  x64 or native user-capture acceptance from this evidence. Source targets
  ARM64 and x64.
- **Unsigned packaging passed only.** Clean ARM64 publication:
  `artifacts\v0.1.20-first-release\candidate-20261106-213539-clean\win-arm64`.
  Branded unsigned candidate:
  `artifacts\v0.1.20-first-release\package-brand-20261106-214155\RedrainCast-arm64.msix`.
  SHA-256:
  `55587ab6aeb08b99ccf7d0c1e9f29ecc7543f5bf1fb9bcae11eb74bbf3d75169`.
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
  Enter the reviewed URLs in the applicable Partner Center product settings;
  no listing or submission settings have been changed automatically.
- Follow the existing [screenshot plan](README.md#screenshot-plan); no app
  screenshots were captured here. Review [listing preparation](README.md),
  [release acceptance](../../STORE.md), [product behavior](../../README.md)
  and [Store identity](../../StoreIdentity.json) before submission.

Documentation-only change; no builds, installations or tests were performed.
