# RedrainCast

Send and receive screens in one native Windows application. Windows 11 22H2+,
.NET 10, WinUI 3, native ARM64 and x64.

## Experience

- The home screen automatically listens for incoming casts and prominently
  displays actual readiness and adapter-labeled `IP : Port` endpoints.
  Copy buttons produce canonical `IP:Port` values without display spaces.
- Nearby receivers update automatically. Each device has its own Send button;
  there is no duplicate destination dropdown or Refresh button.
- Choose a display or window to share, or connect by a numeric IP and TCP port.
  Manual input accepts spaced endpoints and bracketed IPv6 addresses.
- Incoming casts are accepted automatically. Once a real video frame is
  presented, the entire client area displays video, not sending controls.
  Enter/F toggles fullscreen; Esc exits fullscreen. End reception from the sender.
- Receiving is paused while connecting/sending. Stop sharing, a remote stop,
  or a handled outgoing failure automatically restores the listener.
  READY appears only after listening succeeds and a network address is known.
- If another receiver owns TCP 27184, the receive panel reports the bind error
  without pretending to be ready. Sending remains usable. Close that other
  receiver yourself and reopen RedrainCast to receive; this app never stops it.
- Incoming/outgoing setup races have one atomic winner; additional sessions
  cannot evict an active cast. This app excludes its own listener from discovery.
- Settings retain maximum dimensions, FPS, bitrate, codec, orientation, audio,
  and sender transport. Capture targets and active sessions are not persisted.
- Narrow windows stack the two panels; light, dark and high-contrast themes
  follow Windows. Session status and Stop sharing stay outside scrolling cards.

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

Custom TCP is the default and is the transport supported by this receiver.
SRT/fMP4 sending is retained for compatible external receivers; RedrainCast
does not receive SRT. Discovery is IPv4; manual destinations also support IPv6.
Android interoperability requires a compatible protocol-v3 companion, not
Miracast, Google Cast or an arbitrary smart TV receiver.

## Build and test

```powershell
dotnet test .\tests\RedrainCast.Tests.slnx -c Release
.\tools\PrepareSrt.ps1 -Architecture arm64
dotnet build .\src\RedrainCast.App\RedrainCast.App.csproj -c Release -p:Platform=ARM64 -r win-arm64
.\src\RedrainCast.App\bin\ARM64\Release\net10.0-windows10.0.26100.0\win-arm64\RedrainCast.App.exe
```

For x64, prepare `-Architecture x64` and build with `-p:Platform=x64 -r win-x64`.
The pinned native preparation script downloads/builds FFmpeg/SRT dependencies
in a user-local cache. It does not install drivers or change system PATH.

```powershell
.\tools\Publish.ps1 -Architecture arm64 -Version 0.1.0
.\tools\Package.ps1 -Architecture arm64 -PublishedDirectory .\artifacts\win-arm64 `
  -OutputDirectory .\artifacts\msix-arm64 -Version 0.1.0.0
```

Publication/package output directories must be new or empty. Packaging checks
PE architecture, required notices, assembly/package version and the exact
Partner Center identity. See [STORE.md](STORE.md).

Interactive Windows self-tests use an isolated loopback listener without
discovery or persisted settings. The UI self-test also selects the default
display and preflights its hardware encoder to verify outgoing connection
failure recovery, but never starts capturing it:

```powershell
RedrainCast.App.exe --self-test C:\temp\redrain-ui.json
RedrainCast.App.exe --media-self-test `
  E:\gitpersonal\RedrainCast\tests\ScrcpyReceive.Tests\fixtures\synthetic-h264.json `
  C:\temp\redrain-media.json
```

Media self-tests transmit labeled synthetic content over real TCP through the
sender protocol, receiver admission, hardware decoder and WinUI video surface.
They are not a substitute for testing two physical devices and real audio.

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
