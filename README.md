# RedrainCast

Send and receive screens in one native Windows application. Windows 11 22H2+,
.NET 10, WinUI 3, native ARM64 and x64.

## Experience

- The home screen automatically listens for incoming casts and prominently
  displays actual readiness and adapter-labeled `IP : Port` endpoints.
  Copy buttons produce canonical `IP:Port` values without display spaces.
- Nearby receivers update automatically. Each device has its own Send button;
  there is no duplicate destination dropdown or Refresh button.
- The active destination is pinned first, including manually entered devices
  and devices whose discovery advertisement expires. Its row shows Connecting
  with Cancel, Casting with Stop, or Stopping with a disabled control.
  Codec, actual encoded dimensions and encoder details appear beneath its status.
  Other occupied receivers still show Busy; there is no separate session bar.
- Choose a display or window to share, or connect by a numeric IP and TCP port.
  Manual input accepts spaced endpoints and bracketed IPv6 addresses.
- Incoming casts are accepted automatically. Once a real video frame is
  presented, the entire client area displays video, not sending controls.
  Enter/F toggles fullscreen; Esc exits fullscreen. End reception from the sender.
- Receiving is paused while connecting/sending. The inline Stop action, a remote stop,
  or a handled outgoing failure automatically restores the listener.
  READY appears only after listening succeeds and a network address is known.
- If another receiver owns TCP 27184, the receive panel reports the bind error
  without pretending to be ready. Sending remains usable. Close that other
  receiver yourself and reopen RedrainCast to receive; this app never stops it.
- Incoming/outgoing setup races have one atomic winner; additional sessions
  cannot evict an active cast. This app excludes its own listener from discovery.
- Settings retain maximum dimensions, FPS, bitrate, codec, orientation and audio.
  Capture targets and active sessions are not persisted.
- Narrow windows stack the two panels; light, dark and high-contrast themes
  follow Windows. Starting a cast brings its inline session controls into view.

## Network and media

**Trusted local networks only.** Incoming casts require no approval and custom
TCP/discovery are unauthenticated and unencrypted. Use a trusted Private network
and appropriate Windows Firewall rules. Do not expose TCP 27184 or UDP 27183 to
the internet. Network access does not grant remote mouse/keyboard control.

The receive engine supports hardware H.264, HEVC and AV1, up to 60 FPS, with
optional PCM/AAC/Opus audio. The send engine uses Windows Graphics Capture,
hardware Media Foundation H.264/HEVC/AV1, and optional receiver-only audio.
There is no software video or codec fallback. Hardware support is device/driver
dependent. Enabled outgoing audio suppresses local playback and restores owned
mute changes on normal Stop/close/handled failures. A crash or force kill cannot
guarantee mute restoration.

### HDR displays

Windows HDR can stay enabled. HDR sources use floating-point scRGB capture,
then a GPU shader normalizes the source display's Windows SDR white level,
compresses highlights with a smooth shoulder and fits wide-gamut colors into
SDR before NV12 conversion. All codecs explicitly signal BT.709 primaries,
transfer, matrix and limited range. This sends SDR video, not HDR passthrough.
SDR sources retain the existing BGRA8 conversion path.

The display/window selector is target-aware instead of using the opaque system
picker: it retains the selected monitor/window identity so HDR settings are read
from the correct display, including a captured window moved between monitors.
The capture pipeline checks color settings every 500 ms and recreates its frame
pool/converter when needed. It never toggles the user's HDR setting. Missing or
invalid HDR metadata fails explicitly, rather than silently reverting to clipped
8-bit capture. Protected/minimized windows remain subject to Windows capture
restrictions.

Custom TCP is the only media transport. Outgoing audio uses raw stereo PCM;
receiving AAC/Opus audio remains supported. There is no transport selector,
alternate transport backend or MP4 muxer. Old settings selecting an unsupported
transport report an explicit load error; open Settings and save to replace them.
Legacy TCP settings retain their existing sharing values.
Discovery is IPv4; manual destinations also support IPv6.
Android interoperability requires a compatible protocol-v3 companion, not
Miracast, Google Cast or an arbitrary smart TV receiver.

## Build and test

```powershell
dotnet test .\tests\RedrainCast.Tests.slnx -c Release
.\tools\PrepareNative.ps1 -Architecture arm64
dotnet build .\src\RedrainCast.App\RedrainCast.App.csproj -c Release -p:Platform=ARM64 -r win-arm64
.\src\RedrainCast.App\bin\ARM64\Release\net10.0-windows10.0.26100.0\win-arm64\RedrainCast.App.exe
```

For x64, prepare `-Architecture x64` and build with `-p:Platform=x64 -r win-x64`.
The pinned native preparation script downloads the FFmpeg decoder/audio runtime
in a user-local cache. No native compiler or network-transport library is needed.
It does not install drivers or change system PATH.

```powershell
.\tools\Publish.ps1 -Architecture arm64 -Version 0.1.3
.\tools\Package.ps1 -Architecture arm64 -PublishedDirectory .\artifacts\win-arm64 `
  -OutputDirectory .\artifacts\msix-arm64 -Version 0.1.3.0
```

Publication/package output directories must be new or empty. Packaging checks
PE architecture, required notices, assembly/package version and the exact
Partner Center identity. See [STORE.md](STORE.md).

Interactive Windows self-tests use an isolated loopback listener without
discovery or persisted settings. The UI self-test also selects the default
display and preflights its hardware encoder to verify outgoing connection
failure recovery, but never captures that display. It then captures only its
own RedrainCast window to an in-process loopback test peer to verify the real
inline Cancel/Stop controls, discovery expiry and acknowledged cleanup.
No captured content is recorded or sent to another device:

```powershell
RedrainCast.App.exe --self-test C:\temp\redrain-ui.json
RedrainCast.App.exe --color-self-test C:\temp\redrain-color.json
RedrainCast.App.exe --media-self-test `
  E:\gitpersonal\RedrainCast\tests\ScrcpyReceive.Tests\fixtures\synthetic-h264.json `
  C:\temp\redrain-media.json
```

Media self-tests transmit labeled synthetic content over real TCP through the
sender protocol, receiver admission, hardware decoder and WinUI video surface.
They are not a substitute for testing two physical devices and real audio.
The color self-test uses synthetic HDR/SDR patches and diagnostic-only GPU
readback to check RGB within 2 code values, NV12 luma/chroma within 3 code values and
distinct HDR highlights above SDR white. It also checks each available hardware
encoder and verifies the encoded color metadata through hardware decoding.
No CPU readback or software video processing is used in the production path.

## Source provenance

This is an independent repository. Neither existing Windows app is modified.
The internal engine project names are preserved to keep the imports traceable:

- [ScrcpyReceive-Windows](https://github.com/arisewanggithub/ScrcpyReceive-Windows),
  revision `e148c3e26d0ed7998c249a87fc111b284287d548`.
- [ScrcpySend-Windows](https://github.com/arisewanggithub/ScrcpySend-Windows),
  revision `76032580c678b51dc04f9b252ccd4c6c6e7cc029`.

Receiver source retains its [MIT license](licenses/ScrcpyReceive-Windows-LICENSE.txt).
Sender source was copied at the repository owner's direction; its upstream
revision had no standalone license. No new redistribution license is inferred
for that code or for the merged application. Dependency licenses and provenance
are described in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).

The app is not affiliated with or endorsed by the scrcpy project.
Existing apps have separate identities and are not replaced by this package.

## Status

Initial development implementation. Store signing, certification, listing
screenshots and physical multi-device/audio acceptance remain release steps.
