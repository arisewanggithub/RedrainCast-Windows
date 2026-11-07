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
- Choose a display or window using the Windows system capture picker. Windows owns
  the selection UI and its confirmation button; choosing a source does **not**
  send a cast. Sharing still starts only with **Send**, and Cancel keeps the
  previous source. The returned native item is retained and public SDK handle
  getters provide its exact HWND/HMONITOR, never a title/size match. Existing
  source-monitor color/HDR checks and the isolated capture worker remain in use.
  Original-item closure cancels preparation/sending; source availability and
  window process/thread identity are checked again before the worker starts.
  Getter availability is detected at runtime, not assumed for every Windows
  11 version. Runtimes without these public getters use the compatibility visual
  chooser: separate display/window groups, one-click tile selection, at most six
  visible sampled previews with one capture session at a time. Neither route
  saves thumbnails, starts preview capture at startup, or saves source selections.
  The getters are declared in Microsoft's public
  [Windows.Graphics.Capture.Interop.h](https://github.com/microsoft/win32metadata/blob/6d6617a8eee57d7c4f6c55d8c23e35064ea69b9e/generation/WinSDK/RecompiledIdlHeaders/um/Windows.Graphics.Capture.Interop.h#L31-L47);
  the app queries those interfaces rather than assuming a runtime build number.
  You can also connect by a numeric IP and TCP port.
  Manual input accepts spaced endpoints and bracketed IPv6 addresses.
- Incoming casts are accepted automatically. Once a real video frame is
  hardware-decoded, the video surface is exposed and playback enters fullscreen
  before its first Present, avoiding a covered-swapchain startup deadlock. When playback
  ends, the previous window presentation is restored.
  Enter/F still toggles fullscreen; Esc exits fullscreen. End reception from the sender.
- Receiving is paused while connecting/sending. The inline Stop action, a remote stop,
  or a handled outgoing failure automatically restores the listener.
  READY appears only after listening succeeds and a network address is known.
- Windows assigns each listener an available TCP port atomically. Multiple app
  instances can receive independently without competing for a fixed TCP port.
  The current port is displayed and advertised; it may change after listening
  restarts. Manual connections must use the receiver's current IP : Port.
- Incoming/outgoing setup races have one atomic winner; additional sessions
  cannot evict an active cast. This app excludes its own listener from discovery.
- Settings retain maximum dimensions, FPS, bitrate, codec, orientation and audio.
  Maximum FPS uses a dropdown with 15, 24, 25, 30, 48, 50, 60 (default), 90 and 120.
  Existing saved custom caps remain selectable. This is the sender's capture limit,
  not a guaranteed capture, decode or display rate.
  Capture targets and active sessions are not persisted.
- Narrow windows stack the two panels; light, dark and high-contrast themes
  follow Windows. Starting a cast brings its inline session controls into view.
- Sharing settings include **Show playback statistics on receiver**, off by default.
  Compatible receivers display a compact, noninteractive video overlay. Unsupported
  receivers keep casting normally; the sender's active row explains why no overlay
  is available. The saved checkbox is not cleared by an unsupported destination.
  The receiver uses a native, translucent, click-through statistics panel, separate
  from the home-page scroll area: four rate KPIs, right-aligned video/audio values,
  a five-column connections table, and a session footer. Text is 16 DIP normally
  and at least 14 DIP in compact windows; very large numbers use scientific
  notation (earlier in compact mode), not smaller text. Exact totals remain in
  the typed snapshot. It refreshes once per second from cached snapshots.
  Zero FPS on static content is not by itself a health warning. Skipped · recent
  is amber only when nonzero. Decoded/Presented/Skipped totals use three equal-width
  blocks, each with its label above its left-aligned count. These remain
  playback-epoch/generation totals, not recent percentages. Compact mode combines the clock
  caveats into one line without removing the source-PTS, sign, 1-Hz peak or latency
  qualifications.

### System language

The app's presentation follows the first supported entry in Windows' ordered
**Preferred languages** list, read from
`Windows.System.UserProfile.GlobalizationPreferences.Languages`, independently of
Windows' display language and cached .NET thread culture. The property is queried
afresh on setting-change notifications and window activation, visibility and
resume reconciliation. The app recreates its MRT context with an explicit
Language qualifier and refreshes existing controls without a restart.
For example, placing Simplified Chinese above English should select Chinese in
the app while Windows' display language remains English. No sign-out is required
for this app preference-order refresh. Changing Windows' display language itself
may require sign-out; that is a separate OS operation. No language override is
persisted and no OS/profile setting is changed by the app.

The canonical catalog is `src\RedrainCast.App\Strings\en-US\Resources.resw`.
The localization infrastructure targets the reference application's 22 locales:
ar, de, en-US, es, fr, hi, id, it, ja, ko, ms, nl, pl, pt-BR, ru, sv, th, tr,
uk, vi, zh-Hans and zh-Hant. All 22 catalogs are included. Unsupported preferences fall back to English;
Chinese regions resolve to the appropriate script and pt-PT uses pt-BR.

Home controls, open app dialogs and cached playback-statistics presentation refresh
without restarting capture or discarding form values. An open numeric form retains
its original parsing culture. Arabic label containers use right-to-left layout;
video and thumbnails are never mirrored, and addresses/codec/numeric values retain
left-to-right presentation. The native Windows source picker is OS-owned, so its
language cannot be rewritten by the app. Original peer/window names and technical
error details are retained verbatim; localized wrappers explain the action.
Protocol strings and the shared English statistics contract are not translated.

Raw OS/native diagnostic details can remain in their original language; the app's
actionable headings and wrappers are localized.

Catalog tests enforce exact keys, valid UTF-8/XML, nonempty values, and unchanged
indexed placeholders. Missing runtime resources log a diagnostic and explicitly
fall back to canonical English; unknown canonical keys are programming errors.
No new package/framework or periodic localization polling is used.
`tools\Validate-Localization.ps1` compares every compiled PRI candidate and Unicode
value with its source catalog and verifies the MSIX language declarations.
The existing isolated statistics UI harness also checks live language notifications,
open dialogs, RTL/LTR boundaries and preservation of sampler/peak/session state;
it never needs to capture a screen or open the OS picker.
Its first phase observes the real production preference reader and rendered UI
before any synthetic language provider is installed, then verifies fresh real
API reads after owned-window setting notifications and activation.

### Shared playback-statistics names

The portable display contract is
`tests\RedrainCast.Tests\fixtures\playback-statistics-display-contract.json`.
Windows and Android use the same ordered names and units, not necessarily
identical native APIs. Missing measurements are `N/A`; inapplicable table cells
are `—`. No receiver-option or media wire format changes are involved.

| Category | Common meaning / intentional native difference |
|---|---|
| Received / Decoded / Presented FPS | Actual one-second deltas; first or reset sample is N/A. Windows Presented counts successful swap-chain submissions excluding redraws; Android counts frame-rendered callbacks, not scheduled releases. Neither proves physical scanout. |
| Codec / Encoded size / Capture limit / Receiver canvas / Rotation | Actual encoded raster; requested FPS maximum, not observed rate; canvas in physical pixels, not DIP; applied rotation and requested orientation. |
| Video bitrate / Audio bitrate / Configured bitrate / Network loss | Received payload Mbps versus configured video target; network loss remains N/A rather than treating renderer skips as lost packets. |
| Audio format / PCM buffer / target / Device padding | PCM/Opus/AAC/Off; buffer/target and cached padding in ms, without new UI-thread device queries. |
| Inserted silence / Discarded PCM | Total ms plus `(+recent)` one-second delta; these are PCM counters, not acoustic silence or encoded packet loss. |
| A/V skew / Peak observed skew | Video source PTS minus played-source audio PTS; positive means video ahead, not end-to-end latency. Peak is the absolute maximum of valid 1-Hz overlay samples; reset on session/generation/playback epoch, counter regression or hide/Stop. Native high-frequency diagnostic peaks remain unchanged. |
| Channel / Queued / Span / Last activity / RTT | Video/audio encoded **waiting** packets and payload bytes (`count · bytes B`), excluding dequeued/in-flight work; waiting PTS range in ms; age since actual received packet, not oldest queue entry. Control has no queue/span; media has no measured RTT. Control RTT requires a locally generated matching nonce. Inclusive diagnostic snapshots and admission bounds are unchanged. |
| State / Generation / Playback epoch / Occluded | Playing or Recovering, actual generation, Windows native playback-instance/rebuild epoch and cached occlusion. Android uses N/A without equivalent measurements; Audio epoch must not be renamed Playback epoch. |

Scheduled frames, Audio epoch, Device played and Configured audio bitrate are
optional platform-specific details, not substitutes for shared measurements.
Windows does not display unmeasured configured audio bitrate or Android-only
counters. Synthetic statistics UI proofs verify native layout and lifecycle,
not live decoder performance, physical scanout, or phone interoperability.

## Network and media

**Trusted local networks only.** Incoming casts require no approval and custom
TCP/discovery are unauthenticated and unencrypted. Use a trusted Private network
and application-based Windows Firewall rules. TCP listener ports are OS-assigned;
UDP 27183 remains the sender's known discovery listening port. Do not expose
either to the internet. Network access does not grant remote mouse/keyboard control.

An optional protocol-v3 `instanceId` UUID identifies a receiver across port
changes. New peers replace old advertisements from that instance on the same IP,
and each app filters its own identity without hiding other local app instances.
Legacy advertisements without an ID remain supported and expire normally.
This identifier is not authentication.

The receive engine accepts 1–240 FPS requests for hardware H.264, HEVC and AV1, with
optional PCM/AAC/Opus audio. The send engine uses Windows Graphics Capture,
hardware Media Foundation H.264/HEVC/AV1, and optional receiver-only audio.
There is no software video or codec fallback. Hardware support is device/driver
dependent. Enabled outgoing audio suppresses local playback and restores owned
mute changes on normal Stop/close/handled failures. A crash or force kill cannot
guarantee mute restoration.

Receiver admission is not a throughput certification: actual codec, 8-bit SDR
profile, raster and hardware decoding/processing must configure successfully,
and failures stay visible without software fallback or FPS coercion.
The D3D11 video processor uses the session's requested content rate for both
input/output descriptors, including rebuilds and resize/rotation resets; these
descriptors are not measured presentation FPS or a conversion to display Hz.
A 60 Hz panel can present the latest available picture while decoding a faster
stream. Capture, decode and display rates depend on codec, resolution, source
and hardware. The Windows sender still offers at most 120 FPS, defaults to 60
and preserves existing saved custom limits. Discovery does not advertise an FPS
capability. HDR AV1 and Qualcomm AV1 sending remain unsupported.

`tools\Test-HighFpsReceiver.ps1 -App <published-exe> -ReportDirectory <new-directory>`
runs a bounded, isolated, loopback-only native receiver proof. Its fresh interactive
desktop gate must pass without waking or unlocking the machine: any visible,
non-minimized foreground window and a visible, non-minimized owned helper are
required. The receiver-only probe injects no input; foreground ownership is
required only for input injection, which this probe does not perform.
It generates one small 640x360 SDR H.264 key picture without capture, replays that
synthetic picture for five seconds each at requested 90/120 FPS with source-clock
timestamps, and rebuilds native playback halfway through each rate. No audio,
capture worker, user-window capture, remote receiver or HDR/AV1 is involved.
The existing 1 GiB/80%-budget GPU guard, 35-second internal/45-second external
deadlines and owned-process cleanup bound the work. The native JSON reports actual
descriptor rates, decoded/presented rates, drops, original PTS proofs and READY/
surface cleanup. Bounded opt-in successful-present snapshots prove PTS independently
of audio/timeline recording. Control heartbeats are continuously serviced; STOP
serializes with PONG, requires a matching SESSION_END, and retires media before
the control reader/socket. Transport reset or a missing acknowledgement is failure.
Consult that run's report for measured evidence; admission,
configuration and repeated synthetic pictures do not certify sustained capture
or arbitrary codec/resolution throughput, nor require a 60 Hz display to present
90/120 distinct pictures per second.

### Additive receiver options (protocol v3)

This session-only extension does not change framing, message types, three TCP
channels, media tokens, media flags, generation rules, media quality, permissions
or remote input. It cannot change OS preferences or execute commands.

An implementing sender offers this optional HELLO member:
`"receiverOptions":{"version":1,"supported":["playbackStats"]}`.
The receiver acknowledges only the intersection of that offer and its playback
factory's implemented options, with the same shape. An empty intersection omits
the member entirely. Foundation/unavailable factories advertise nothing; the
Windows video host implements `playbackStats`.

Only a checked setting **and** a mutual acknowledgement produce the optional
SESSION_REQUEST member:
`"receiverOptions":{"version":1,"playbackStats":true}`.
Otherwise the request omits the extension entirely, including when a legacy
receiver did not acknowledge it. The receiver defaults to off for each new
session and applies only negotiated options. Recovery rebuilds retain the same
immutable request; Stop, failure and detach clear the overlay and rate baseline.
The sender's persisted setting is `ShowPlaybackStatistics` (strict JSON Boolean,
default `false`); it is not automatically serialized into a session request.

Revision 1 is additive: future stable names and typed request members can be
added without changing it. Unknown supported names and unknown optional request
members are ignored, never activated or treated as authority to change other
configuration. An unknown positive revision is well-formed but negotiates no
options; its request options are ignored after validating known fields. A breaking
schema must use a new revision and fall back when no common revision exists.
Known malformed fields remain `PROTOCOL` errors, even without negotiation or
under a future revision. `version` must be a positive exact signed-int32 JSON
integer (`1.0`, strings, overflow and zero are invalid); `playbackStats` must be
Boolean, not truthy/coerced. Capabilities require a `supported` array. Each
envelope is at most 2048 UTF-8 bytes, 16 unique members, and names are 1–64
characters; supported arrays contain at most 16 unique non-control-character
strings. Duplicate envelopes/members/names are rejected. Existing required-field
and transport-override validation remains in force.

| Sender → receiver | Negotiation / request | Result / evidence |
|---|---|---|
| New Windows → new Windows, checked | Mutual `playbackStats`; explicit request | On; portable three-socket tests, both legacy-session and recovery paths |
| New Windows → new Windows, unchecked | Capability may be mutual; request extension absent | Off; same portable tests |
| Legacy Windows/current Android sender → new Windows | No option offer; no option acknowledgement | Normal cast, off; raw TCP fixtures, including an unnegotiated option that cannot activate |
| New Windows → legacy Windows/current Android receiver, checked | No option acknowledgement; entire request extension absent | Normal streaming and Stop; raw TCP legacy receiver fixtures, persistent nonfatal notice |
| Future peer, revision 1 with unknown name/member | Known intersection only | Known statistics work; unknown option ignored; raw TCP and parser tests |
| Future peer, unknown revision | No common option schema | Legacy cast accepted, off; raw TCP and parser tests |
| Malformed known fields / duplicate / oversized envelope | Reject `PROTOCOL` | Strict parser and raw TCP rejection tests |

Android evidence is **source inspection plus Windows-hosted wire fixtures**, not
physical-phone validation. Inspected current Android `ReceiverLink.kt` offers
rotation/recovery and validates their known types while ignoring other HELLO
members; `ReceiverServer.kt` echoes only those capabilities. `Configuration.kt`
accepts FPS 1–240, ignores unrelated request members, and explicitly denies
`mediaTransport`. No Android source, phone or ADB was modified or exercised.

#### Statistics definitions and limits

The top-left translucent panel is above the video surface, outside HomeScroll,
cannot take focus or intercept input, and is bounded to the client area.
Text is now 16 DIP (formerly 11), with a maximum panel width of 1100 DIP and
height bounded by the available viewport minus 24 DIP. Windows below 800 DIP
wide or 600 DIP tall use 14-DIP text with shorter labels, retaining all statistic
categories and units. Compact V/A means video/audio; Δ means recent delta.
The panel does not scroll or intercept the video's input.
A one-second UI timer runs only for requested, visible reception; off/default
sessions have no statistics timer. No per-frame UI, JSON, extra network traffic,
diagnostic-string parsing, timing-trace enablement or native device polling is
introduced. WASAPI padding/played frames come from already cached output-thread
samples; the overlay never calls COM, DXGI or the playback-position method.

* **Stream:** codec, actual STREAM_CONFIG encoded raster, requested FPS cap
  (not measured FPS), configured video bitrate separately from actual traffic.
* **Playback:** received media-packet, decoded-picture and successful-present
  counter deltas divided by elapsed monotonic time. The first sample is N/A;
  unchanged counters yield zero on subsequent samples. Baselines reset for a
  session, playback epoch, generation, counter regression or unavailable epoch.
  Native redraws are not new presented pictures. Canvas pixels use composition
  scale; DIP dimensions and stream rotation/orientation are labeled separately.
* **Throughput:** payload-byte deltas × 8 / elapsed time in decimal Mbps,
  excluding TCP/framing overhead; not network capacity or a throughput promise.
* **Video:** decoded/presented/renderer-skipped epoch totals; recent skipped
  percentage = skipped / (presented + skipped). Empty denominators are N/A.
  Skips are not network loss; receive-minus-decode is not loss.
* **Audio:** buffered/target PCM and cached device padding in milliseconds at
  48,000 sample frames/s; inserted-silence/discarded-PCM totals and recent deltas
  use the same units. These are sample frames, not encoded packets. Source
  content PTS and device-played frames are distinct; unavailable values are N/A.
* **Sync:** last cached video source PTS minus WASAPI-played source-audio PTS,
  signed (positive = video ahead), with the current-generation absolute peak.
  It is not end-to-end latency; no AV timing issue is claimed fixed.
* **Queues/connections:** per-track encoded media bytes/entries/PTS span
  (including in-flight media), actual combined queue byte budget and existing
  count/span bounds; decoded PCM is separate. Media age is since the last
  complete received media packet. Control RTT matches one outstanding locally
  generated heartbeat nonce to its own monotonic timestamp; peer ping echoes
  cannot create RTT. Last valid control-message age is separate.
  Recovery generation/state, playback epoch and cached occlusion are labeled;
  an empty queue is not a green health assertion.

`tools\Test-PlaybackStatistics.ps1 -App <immutable-published-exe>
-ReportDirectory <fresh-directory>` runs a fresh Default-desktop/visible-helper/
visible-foreground gate, then a new owned isolated XAML window. It uses typed
synthetic snapshots to check default-off, on, resize, own fullscreen, simulated
recovery baseline reset and Stop/timer cleanup. Native text trimming, unconstrained
desired height and the last character's rectangle are checked at 560×420 and
1100×760 DIP and fullscreen with long synthetic counters. It does not capture any window,
start MFT/audio, inject input, write user settings, or validate real media rates.
See `artifacts\v0.1.20-playback-stats\playback-stats-validation.json` for exact
test/build/native evidence and release limitations. Do not deploy or restart
existing user apps based solely on these bounded proofs.

### Terminal errors and shutdown

Control connects first and is retired last. If a media socket closes cleanly at
a packet boundary, the sender keeps that EOF provisional for at most two seconds
so a valid fatal receiver error on the control connection can explain it.
Only that provisional EOF can be superseded: an earlier capture, congestion,
protocol or transport failure keeps precedence. Peer fatal errors must belong
to the current session and carry a recognized code, a strict Boolean fatal flag
and a bounded, nonempty message. Malformed, wrong-session and nonfatal messages
cannot turn a failure into success or replace a genuine earlier error.

Normal Stop acknowledgements and playback retirement remain separately bounded;
the diagnostic grace does not add a delay to every normal Stop. Started control
frames finish under their existing write deadline instead of being truncated and
reusing a partial stream. Receiver shutdown retires media and playback before
closing control. EOF between packets is distinguished from truncated packet
headers/payloads, which remain protocol failures.

If native retirement outlasts its socket-close budget, connection resources and
the session lease remain owned until the pending operations and actual disposal
finish. The receiver reports failed/busy, not READY, during that period. A
noncooperative native component can still retain resources; error delivery after
a native crash or broken control transport is not guaranteed. Late cleanup
diagnostics identify bounded origin/code/type without adding raw peer-message
logging.

### Android video rotation

Receivers negotiate the optional Boolean `videoRotation:true` HELLO capability
when the sender offers it and the local GPU supports video-processor rotation.
Legacy senders keep zero-rotation behavior; nonzero rotation flags without
negotiation, malformed capability values, and reserved flag bits are rejected.

On each `VIDEO_FRAME`, flag bit 0 remains the keyframe bit; bits 1-2 encode
clockwise quarter-turns (0, 90, 180, 270 degrees). The encoded raster, codec
configuration and original PTS are unchanged. FFmpeg copies a per-packet opaque
rotation tag onto the decoded picture, so duplicate timestamps, frame cloning,
redraws and reconfiguration cannot pair a picture with another picture's angle.
Direct3D rotates the NV12 stream on the GPU and letterboxes using the rotated
aspect ratio. No CPU rotation or ignored-angle fallback is used.
The color self-test additionally checks all four angles and the reset to zero
using asymmetric synthetic corner pixels in portrait/landscape viewports, and
verifies per-picture rotation/PTS propagation through all available decoders.
`RedrainCast.App.exe --rotation-self-test <report.json>` exercises negotiated
rotation on real three-socket loopback transport through the hardware decoder
and WinUI presenter, including non-key pictures, a second configuration generation,
reset to zero, fullscreen, Stop and return to READY. It uses only generated video;
it does not claim that a physical Android cast has been tested.

### Optional bounded stream recovery v1

Version 0.1.17 implements the corrected Android recovery v1 policy for normal Windows
hardware-worker sending and receiving. Both control HELLOs must explicitly
acknowledge Boolean `streamRecovery:true` and integer `streamRecoveryVersion:1`.
Malformed capabilities/revisions fail; absent/false, missing revision or an
unsupported positive offer revision leaves the strict legacy queues unchanged.
An explicit true acknowledgement with a missing/incompatible revision is rejected.
Version 0.1.15 still never acknowledges recovery.

Recovery uses existing session-scoped STATUS packets (track/generation zero,
PTS -1, flags zero), with required `state`, `message`, and positive signed-int32
`generation` referring to the video generation:

| Direction | State | Additional fields |
|---|---|---|
| Receiver to sender | `stream-recovery-request` | None |
| Receiver to sender | `stream-recovery-key` | `keyPtsUs` |
| Receiver to sender | `stream-recovery-complete` | `keyPtsUs`; `audioPtsUs` when audio is enabled |

Timestamps are canonical nonnegative signed-int64 decimal **strings**, not JSON
numbers. Future/invalid generations and conflicting proofs fail; valid stale
proofs are ignored and matching duplicate proofs are idempotent. Duplicate
STREAM_READY is an error. Generic STATUS reception is not an acknowledgement.

Capture and the video/audio writers run independently under the existing byte,
entry and 500 ms backlog caps. Pressure retires only **unsent complete obsolete
chains**, never a started packet. A serialized video writer places cached same-
codec/raster STREAM_CONFIG at the next sequential generation behind any already-
started frame. Receiver admission keeps consuming obsolete framed data while a
dedicated serialized worker retires/recreates both native outputs. Audio wire
generation/format stay unchanged. No software, codec, quality or timestamp
fallback occurs, and genuine current-epoch codec/transport failures stay fatal.

READY follows actual configuration of both outputs. The sender drains/discards
capture while waiting, requests a Media Foundation hardware sync frame no faster
than every 500 ms, and sends only a verified fresh random-access picture at/after
READY, at most 250 ms old at admission. Cached CSD and decoder-visible raster
must match. Rotation and original PTS remain picture-scoped. Dependent pictures
resume after the key; KEY proves decoded output, not merely admission. KEY must
match the live generation and exact sent random-access PTS. Its receipt age is
logged, but transit, decode and return-control delay is not mistaken for capture
staleness: a valid proof received after 250 ms remains eligible within the original
ten-second transaction deadline. Versions through 0.1.16 RC3 incorrectly retried
such proofs and could repeatedly reconstruct outputs until the cast timed out.

The isolated Windows capture worker is prepared before connecting to the receiver,
under a separate 30-second bootstrap bound. Its private `RCW2` READY handshake
validates the fixed codec and raster and confirms console/COM, source metadata,
GPU and encoder initialization only: no WGC
capture session or audio capture starts during preparation. The parent sends START
only after receiver acceptance and media attachment; initial CSD and picture PTS
come from real encoder output after START. Rejection, cancellation and parent exit
retire the prepared worker through the existing owned job and bounded Stop path.
Cold runtime loading (including launch from a network share) therefore does not
consume the unchanged ten-second first-unresolved media/recovery deadline.
The private worker command also requires `--prepare`; older helpers reject the
extra argument before opening a capture source rather than starting capture early.

Audio remains bounded and gated until KEY. Windows sends PCM and trims a
captured buffer into packets of at most 120 ms (5760 stereo sample frames),
preserving every byte and each subpacket's source sample-aligned timestamp.
This packetization also applies to unnegotiated sending for Android compatibility.
The receiver trims a
straddling packet on sample-frame boundaries; it never retimes the remaining
samples. Receiving Opus retains contiguous duration-aware 80 ms preroll (including
60/120 ms packets), and AAC-LC retains one 960/1024-sample access unit. Retained
compressed packets are decoded in order; decoded samples before the key are
discarded. Midstream Opus resets do not reapply initial pre-skip. A fresh output/
clock epoch skips the retired backlog rather than filling it with silence.
AAC preroll is rounded upward (1024 frames = 21334 us). The first retained
compressed packet must start at/before the preroll window; one-sample timestamp
rounding tolerance applies only between subsequent packets. Malformed Opus CBR
and truncated VBR/count headers fail instead of supplying a plausible duration.
COMPLETE proves an actual source-sample WASAPI submission at/after the key and
within 250 ms, not inserted silence. Video-only COMPLETE needs no audio proof.
This 250 ms range is a source-timeline alignment requirement, not a wall-clock
delivery deadline. The receiver does not reset just because audio takes more than
250 ms to arrive after KEY. It waits for real bounded retained coverage within
the unresolved transaction deadline. If the sender's capped queue actually loses
the needed audio prefix and the first eligible sample is beyond that source
window, it starts an explicit bounded retry; it does not grow buffers or rewrite
timestamps. Missing source audio still fails after ten seconds, without a
wall-clock-driven reconstruction loop.
Coverage distinguishes missing-but-still-possible input from irrecoverable
monotonic gaps. RAW/AAC prefixes or continuity that can no longer cover the source
window request a bounded retry. Opus is different: its existing protocol allows
equal/reversed source-PTS observations in arrival/decode order. An apparent
timestamp gap alone therefore does not prove loss; retain bounded preroll and
wait rather than sorting packets or rejecting permitted reversals. Exhausting
that preroll budget requests recovery, and the original deadline still applies.

During native audio startup, reaching the existing FIFO target waits for the
first actual source-sample output submission before admitting more decoded
blocks. Network admission remains independent and bounded. This prevents a
released backlog burst from dropping the required key-aligned prefix before
WASAPI first writes it; FIFO targets and packet budgets are not enlarged.

One ten-second deadline starts at the first unresolved recovery and covers every
retry, configuration, READY, sync, key and completion; retries never extend it.
KEY/COMPLETE handlers also check this deadline synchronously, including before
clearing it: a late proof cannot slip past expiry between watchdog ticks.
The initial capture/recovery deadline starts after bounded session/media-socket
setup, rather than consuming its budget while negotiating/accepting a connection.
Connection setup retains its own ten-second bound. RC2 corrects RC1's premature
startup timeout when those individually bounded stages together exceed ten seconds.
Native resets are at least one second apart. Generation exhaustion is fatal.
Stop prevents stale workers, failures, READY or proofs from reviving a retired
epoch. Started control/media frames finish before the next frame/Stop; any
failed partial write retires the transport rather than reusing it.

`RedrainCast.App.exe --recovery-self-test <report.json>` uses an owned animated
window, native H.264 encoder/decoder, generated silent PCM and real WASAPI output.
A bounded test-only decoder delay forces queue pressure and verifies same-session
video/audio reconstruction, KEY/COMPLETE, resumed presentation and child cleanup.
An additional 350 ms delay on the first picture of each native epoch verifies
that a legitimately late decoded-KEY proof does not cause extra reconstructions.
Real-socket regressions also test 450 ms delayed-but-covered PCM, actual capped
audio-prefix eviction, and absent audio reaching the unchanged ten-second limit.
Portable real-socket tests use explicitly fake codecs; neither test claims
physical Android interoperability or full-resolution/HEVC stability.
The policy follows the exact v1 contract in the Android protocol documentation.
Prior art inspected: scrcpy's [demuxer](https://github.com/Genymobile/scrcpy/blob/a60891aea193d92e7e5c3942700eca63f9d19a5f/app/src/demuxer.c),
[decoder](https://github.com/Genymobile/scrcpy/blob/a60891aea193d92e7e5c3942700eca63f9d19a5f/app/src/decoder.c)
and [frame buffer](https://github.com/Genymobile/scrcpy/blob/a60891aea193d92e7e5c3942700eca63f9d19a5f/app/src/frame_buffer.c).
Independent demux/decode and replacing already-decoded pending frames are useful;
this negotiated recovery protocol is not claimed to be scrcpy's protocol/policy.

### HDR displays

Windows HDR can stay enabled. HDR sources use floating-point scRGB capture
(80 nits per linear 1.0), followed by reference-generated BT.2390 tone mapping
and perceptual gamut conversion on Direct3D. A GPU histogram measures the
content's peak rather than assuming the monitor's peak is the video peak.
The 99.995th percentile rejects isolated extreme pixels; small changes are
smoothed while scene cuts react immediately. Eight 65-cubed transform banks
cover 100-10,000 nits, with tetrahedral color interpolation and smooth
interpolation between peak banks.

The old fixed shoulder and blanket division by Windows' SDR-white setting
are removed. SDR sources retain their existing BGRA8 conversion path.
All codecs signal BT.709 primaries, transfer, matrix and limited range.
This sends SDR video, not HDR passthrough.

Video pixels stay on the GPU. Every fourth frame, the GPU copies only a
256-bin luminance histogram (1 KiB of aggregate metadata) for asynchronous
CPU analysis. The initial histogram is awaited to avoid an incorrectly exposed
first frame. Subsequent reads are nonblocking; no video pixels are read back
or processed on the CPU in production.

The display/window selector is target-aware instead of using the opaque system
picker: it retains the selected monitor/window identity so HDR settings are read
from the correct display, including a captured window moved between monitors.
The capture pipeline checks color settings every 500 ms and recreates its frame
pool/converter when needed. It never toggles the user's HDR setting. Missing or
out-of-range display white/peak values are logged and represented as unknown;
they are not used by the content-measured tone mapper. HDR mode and floating-point
capture are preserved, never replaced by clipped 8-bit capture. Explicit invalid
values passed directly to the color model still fail validation.
Protected/minimized windows remain subject to Windows capture restrictions.

### Isolated production capture

Worker bootstrap diagnostics identify entry into managed capture, source opening,
display-color/device initialization and each hardware encoder setup stage.
If no bootstrap entry appears after the child starts, the log does not establish
an encoder failure: the child may be stalled before managed worker entry.

Normal Send uses a dedicated console capture/encoding executable under
`capture-worker`, with its own self-contained .NET runtime and no WinUI/App SDK
startup. The entire published folder, including that subfolder, is required.
This removes unnecessary UI-framework initialization from the worker process;
the remote pre-worker-entry stall still needs a real-device confirmation before
claiming its underlying cause is fixed. The existing native WGC/D3D11/Media Foundation path and BT.2390 mapping
are preserved; standalone FFmpeg capture is not part of the sender.
Only compressed packets cross the pipe, with original absolute QPC timestamps.
Audio still uses the normal sender's WASAPI path and shares that clock.
Capture owns a persistent thread with a WinRT dispatcher and message pump,
including while waiting for frames. This fixes the reproduced first-frame-only
stall in the headless worker; waiting never abandons the capture thread's queue.

NV12 surfaces are recycled only after Media Foundation releases a tracked input
sample, not merely when an encoded output arrives. The pool is bounded to 32
surfaces, targeting 128 MiB of nominal pixel storage with a minimum of one surface
(a single large surface can exceed that target); video-processor views are cached
only for those surfaces and the current stable input. This reduces allocation
churn but does not prove that the earlier HDR/AV1 watchdog reset is fixed.
The worker checks DXGI usage against 80% of its segment budget and a 1 GiB total
GPU-usage limit; the parent checks worker private memory against 1 GiB. Guard
failures report errors, rather than lowering quality or changing codecs.
These guards and process isolation cannot prevent a kernel graphics-driver hang.
DXGI checks observe current usage at startup and once per second; they are not
allocation reservations. Raw capture buffers, the latest-frame copy, HDR
conversion resources and encoder/driver allocations are outside the NV12 pool's
nominal-storage target and can increase usage between checks.

The parent bounds startup and stalled-video waits, allows idle unchanged windows
only with fresh capture-status reports, and bounds Stop before terminating its
owned worker. A kill-on-close Windows job also prevents an orphaned capture worker
when the parent exits. Qualcomm AV1 is rejected because of incompatible hidden/
reordered frames. HDR-to-SDR AV1 sending is temporarily rejected on every adapter
pending stability validation; H.264 and HEVC remain available. No codec is silently
substituted, including when a previously saved setting is unsupported.
The codec picker and outgoing session display use **H.264 / H.265 (HEVC) / AV1**;
the internal HEVC protocol value remains `h265`.
Sharing settings hide AV1 for both Qualcomm vendor IDs and for a selected HDR
source on any adapter. Without a selected source, other adapters may still list
AV1 with an SDR-only notice. This filters known incompatibilities, not actual
hardware encoder availability or codec/FPS/resolution certification. Adapter
metadata comes from the same default hardware D3D11 device path as capture;
metadata-query failures are reported rather than assumed to mean SDR/support.
An unavailable saved AV1 choice leaves the selection empty and requires an
explicit supported choice before Save; opening settings or Cancel never rewrites
it. Save refreshes adapter/source metadata, and capture retains its own guards.
The manually verified NVIDIA SDR AV1-to-Surface direction does not establish HDR
stability or Qualcomm AV1 sending support. HDR AV1 remains disabled; the earlier
watchdog failure is not proven fixed.
Capture-start color is revalidated before creating its frame pool or converter
and starting the encoder, closing an SDR-to-HDR admission gap after construction.
Periodic color-change validation still precedes pool recreation and encoder reuse.
This closes a policy bypass, not the unproven HDR/AV1 watchdog failure.
Stopping media does not cancel an in-progress control frame: a queued Pong must
finish intact before the Stop acknowledgement uses that same control connection.

The entire home page scrolls at short window heights, including long error
details, both responsive cards and the version/help footer. Headers and footers
can scroll out of view; receiving video stays outside this scroller. Keyboard
Tab navigation brings home controls into view without changing playback shortcuts.
`tools\Test-HomeLayout.ps1 -App <published RedrainCast.App.exe> -ReportDirectory <fresh directory>`
requires a fresh Default-desktop/visible foreground owned-helper gate, AC power,
no capture workers or active app peer connections, and launches
`--layout-self-test <report.json>`. This opens only an isolated loopback-ready window,
checks short stacked/two-column layouts with the error banner closed and with
32 synthetic AV1 failure lines. It also checks the public capture-item HWND getter,
retention of the original item, source-monitor color metadata, source-closed
cancellation and stale-handle rejection using one generated 640×360 owned window.
It never opens the system picker or compatibility chooser and never starts a
capture session. Natural picker selection, native picker cancellation UI and real
display selection remain manual checks. No encoder, audio or saved settings are touched.
Cancellation requests the native picker's cancellation, but retains ownership
until its actual WinRT operation completes before calling `Close`. A late result
after role/lifetime cancellation is discarded. WinRT does not promise immediate
native UI dismissal; app shutdown waits for that tracked operation rather than
closing it prematurely or abandoning it.
Its 45-second deadline is supplemented by a 60-second external launcher timeout;
it tests native scrolling and injected Tab navigation (only while its exact HWND/PID
owns foreground), not physical wheel/touch input. It aborts without injecting keys
if another window takes foreground.

`tools\Test-AppPeers.ps1 -App <published RedrainCast.App.exe>` opens two separate
app UIs and the sender's capture worker, exercising normal Send, real three-TCP
hardware playback, fullscreen, both Stop directions, released surfaces/child exit,
and return to READY. The test captures only its own animated window, sends no
audio and uses loopback by default; reports go to a fresh per-run local directory.
The acceptance-only harness optionally supports `-SilentPcm -DurationSeconds 300
-SampleSeconds 5`. It sends generated zero PCM (48 kHz stereo s16le), never desktop
audio or a microphone, through the real receiver WASAPI output. Normal Send is
unchanged. Duration is zero (the original sixty-frame proof) or 60–600 seconds;
only the self-test outer deadline becomes duration + 75 seconds. Production
preparation/media deadlines and quality settings are unchanged.

For a bounded process-only timing investigation, add `-Timeline -SilentPcm
-DurationSeconds 60` (direct CLI: `--peer-timeline`). This flag is rejected outside
the isolated peer test, or without generated PCM and a sixty-second duration.
Each process preallocates at most 262144 numeric events (about 22 MiB), without
payloads, disk I/O, JSON, observer callbacks or waiting in the recording path. Recording
uses atomic reservations; overflow is counted and makes the trace incomplete.
After media workers stop, each role saves its own `*-timeline.json`, including
PID, managed thread ID, QPC frequency/timestamps, attempted/dropped events and
aggregate/maximum recording ticks. These instrumentation times are not proof
of global OS saturation, and do not include all call-site/atomic tail overhead.
QPC differences are comparable only on the same host. Use session/track/generation
and source PTS, not unrelated lifetime maxima, to correlate stages.

### Known issue: receiver A/V timing

Surface playback can exceed the 250 ms A/V skew check. The 2026-11-06 fixed-order
Audio/Pro Audio localhost experiment showed no improvement: absolute skew p95 was
201586/239385 us and peak was 245450/294187 us. The experimental role option and
its code have been removed; all audio workers retain `Audio` scheduling.
Historical A/B reports and immutable candidates remain evidence, not supported
runtime options or release acceptance.

The historical sampling experiment's largest output intervals consumed the worker's own CPU with no recorded
GC pause, but the exact consuming code remains unidentified. The earlier 109 ms
post-wait gap did not recur; do not treat these as the same event. The A/V timing
issue remains open; no buffer, timestamp, codec or quality-limit workaround has
been applied.

Stages cover source PCM receipt, TCP write begin/end, complete socket reads,
admission, queue admit/take, decode/push and output wake/pull/write. Output pull
begin A/B/C/D are requested/buffered/padding/target frames; pull end are
total/content/inserted-silence/output-start frames. Each output span carries
first/end source PTS and output-start/count, retaining discontinuities. Write
events carry output-start/count. Wake precedes the played-position query;
position carries played source PTS/content frames. Wait begin/end delimit the
WASAPI event wait (end A: 0 signaled, 258 timeout, -1 cancellation);
real video presentation carries video PTS, played audio PTS, content frames and
signed skew. Stage enum values are serialized numerically. A failed/interrupted
run or missing stage/partner remains incomplete evidence, even without overflow.
The sender's separate twelve-second unchanged-window survival phase is retained.
For isolated static-before-capture startup coverage, run `tools\Test-AppPeers.ps1`
with `-StaticSource -SilentPcm -StopSide sender-stop`. Only this bounded loopback
probe uses a one-frame hardware decode/presentation requirement; the normal
animated sixty-frame proof is unchanged. The owned 640-pixel H.264/20-fps source
never animates, and audio is generated silent stereo PCM (not device capture).
Random-access control requests wake idle capture independently of WGC arrival
counters. If no real input is pending, capture replaces its owned WGC session and
frame pool together once per admitted request after fresh
color/size admission, retaining the new WGC frame's original SystemRelativeTime.
Duplicate requests coalesce with the existing 500-ms sync limit; no cached-frame
retimestamping, encoder restart, quality changes or recovery deadline relaxation
is used.
The flag changes no media queues, deadlines, quality settings or firewall rules.

The one-off native output-thread CPU/cycle/GC sampling experiment has been
removed. Bounded wake/wait/source-PTS stage markers remain for reusable peer A/V
timelines, without native thread probes or changes to audio scheduling.

Direct cross-PC roles additionally accept the exact receiver IPv4 after the Stop
side, then `--peer-duration-seconds 300 --peer-silent-pcm --peer-sample-seconds 5`.
Use fresh shared report directories, exact endpoint instance IDs, interactive
owners, and sequential sessions. The sender checks that its synthetic capture
HWND belongs to its PID and is visible/not minimized before selecting it. An
unlocked Default input desktop and visible owned test window are sufficient for
the helper gate; Windows foreground-focus arbitration is not a failure.

Reports retain actual presentations, decoded/hardware frames, WASAPI content
frames, first source audio PTS, clock-source diagnostics, AV skew, skipped frames,
audio silence/drop counts, five-second private/working-set samples with a
15-second warmup baseline, and Stop/READY/child/surface cleanup evidence. The app
guard is 2 GiB private memory and the existing worker cap remains 1 GiB. GPU bytes
are explicitly null with a reason when unavailable; an external LAN orchestrator
may query PID-scoped GPU counters and enforce a combined GPU cap. Never interpret
missing counters as zero. Keep acceptance builds in separate output directories,
not the immutable release candidate.

Stability reports also retain session/generation ownership, playback epoch
transitions, and signed A/V samples taken at a real video presentation against
the WASAPI-played source-content PTS (positive means video is ahead). Sample age
is explicit; received audio packet PTS is not a played position. A temporary
surface detach is allowed only during recovery of the same receiving session.
Native counter resets are epoch-aware, but reconstruction alone never resets
the five-second decode/presentation/source-audio progress bound. Sender transport
diagnostics record queue depth/bytes/PTS, capture receipt gaps, gate waits and
TCP write durations, including a snapshot before a pressure-triggered reset.
These measurements do not compare wall clocks between computers.
The isolated silent-PCM source samples its pacing clock once per packet, so
crossing a scheduled deadline cannot pass a negative duration to `Task.Delay`.
Late packets retain their original source PTS; pacing does not rewrite timestamps
or expand the media queues.
Playback measurements are retained before Stop; disposed audio counters cannot
replace live content/silence/drop totals with zeros. The separate
`avWithin250ms` field distinguishes measured A/V quality from functional session
survival. Samples include receiver read/admission/queue timing, process CPU time
and thread-pool state. Receive gaps of at least 100 ms retain scheduling context
at the largest gap, without introducing additional live socket-stat queries.
Recovery preserves the native audio-decode worker's existing MMCSS thread scope.
That scope belongs to the track pump, survives native output reconstruction, and
is reverted on the same worker thread at shutdown; it is not an audio-device lease.

The 2026-11-06 controlled validation is **not release acceptance**. Although all
695 portable tests and ARM64/x64 publications passed, a Surface local-loopback
300-second run still reached +258,092 us steady A/V skew, exceeding the 250 ms
check. The synthetic pacing race and lost recovery audio-thread scope are fixed,
but the broader physical A/V degradation remains unresolved. Fresh cross-PC
validation is also blocked by missing approval for the new receiver executable
paths; no firewall permissions were changed. Silent PCM does not establish
audible quality or optical synchronization.

`tools\Test-AppLanPeers.ps1` provides that controlled LAN orchestration. Specify
`-App`, `-RemoteApp`, `-LocalAddress`, `-RemoteAddress`, a new `-ReportDirectory`,
and its existing interactive-user-accessible UNC `-SharedReportDirectory`.
The remote bundle must already be deployed; the script checks matching app,
protocol and worker SHA-256 hashes, resolves the current Explorer owner, and
uses least-privilege interactive scheduled tasks. It never grants firewall
permissions, unlocks a desktop, captures existing user windows, or scans ports.
Default `-Repetitions 3` runs sender/receiver/sender Stop in each direction.
After short sessions pass, `-SilentPcm -DurationSeconds 300 -Repetitions 1`
runs five-minute A/V sessions sequentially. PID-scoped dedicated plus shared GPU
counters have a 1 GiB combined per-host test guard when available; unavailable
counters retain null and the query error, while existing native GPU guards remain
unchanged. External samples target five-second intervals; their recorded UTC
timestamps remain authoritative if management/counter queries take longer.

This does not validate LAN discovery/firewall, different GPU drivers,
cross-machine clock alignment, audible quality, desktop muting, optical latency,
or full-resolution HDR stability. Silent PCM proves servicing, not audibility.
Local ARM64 and x64 runs each presented at least 60 frames per Stop direction,
returned all tracked surfaces, exited the worker, and restored READY. Observed
Stop times were 155-352 ms. The x64 run also recorded more than 12 seconds waiting
for an unchanged source without incorrectly ending the session. These are local
release-candidate checks, not a substitute for physical cross-device validation.

For an explicitly controlled two-PC check, run
`--peer-self-test receiver <fresh-shared-report-directory> sender-stop <receiver-IP>`
on the receiver and the matching `sender` command on the other PC. Both instances
must use the same report directory and receiver IP. The optional IP changes only
this owned-window test destination; reports identify non-loopback runs explicitly.
Capture remains limited to the test's own small H.264 window, with audio disabled.
Use `receiver-stop` with a separate fresh directory for the opposite Stop direction.
An acknowledged receiver Stop may precede shared-file visibility; the sender still
requires the actual sixty-frame playback report within the existing test deadline.

Custom TCP is the only media transport. Outgoing audio uses raw stereo PCM;
receiving AAC/Opus audio remains supported. There is no transport selector,
alternate transport backend or MP4 muxer. Old settings selecting an unsupported
transport report an explicit load error; open Settings and save to replace them.
Legacy TCP settings retain their existing sharing values.

Each cast opens three TCP connections to the same OS-assigned receiver port:
control (bidirectional), video and audio (sender to receiver). The audio connection
stays idle when audio is disabled. Media configuration travels on its matching
media connection; readiness, heartbeat and Stop acknowledgements stay on control.
Independent socket readers/writers keep a blocked video transfer from directly
blocking audio or control. All connections still share available network bandwidth.
Timestamps, not cross-socket arrival order, synchronize audio and video.

The control connection negotiates the session first. `SESSION_ACCEPT` includes
a random 256-bit `mediaToken`. Control remains responsive while media connections
attach, so a receiver Stop cancels setup and is acknowledged before cleanup.
Each media connection starts with a `HELLO` using
the accepted session ID and `{ role: "media", channel: "video" | "audio", token }`.
The receiver acknowledges the matching channel before media starts, rejects
unknown/duplicate attachments, and waits for both media connections. Losing any
connection retires the whole session; stopping closes all three. The token pairs
sockets only: it is not user authentication and traffic remains unencrypted.
This replaces the previous single-connection layout without a legacy fallback;
both peers must use this layout even though SCPY framing remains version 3.
An attachment matching a session that is stopping is rejected with
`SESSION_ENDED` and that session's ID. Only this rejection may be treated as
normal cleanup after an acknowledged receiver Stop; malformed replies, unrelated
rejections and connection failures remain visible errors.
During recovery setup, a valid matching attachment rejection is held separately
from terminal failures for the existing two-second Stop acknowledgement budget.
Success requires the control reader to confirm that session's receiver Stop and
successfully send SESSION_END; SESSION_END alone is not Stop proof, and independent
control or acknowledgement failures are never suppressed.

Discovery is IPv4; manual destinations also support IPv6.
Android interoperability requires a companion implementing this three-connection
layout and protocol-v3 framing, not
Miracast, Google Cast or an arbitrary smart TV receiver.

## Diagnosing a failed cast

Each normal app instance writes metadata-only logs in
`%LOCALAPPDATA%\RedrainCast\Logs`. The exact file is included in sender failure
messages, and Connection help shows the directory. Copy the newest logs from
**both devices** after reproducing the problem. Logs include local addresses,
device/encoder names, settings, error stacks and timestamps; review them before
sharing. No captured images, video payloads or audio recordings are logged.
The directory is local to the account running the app on each device, even when
the executable is launched from a shared/network artifacts folder. Nothing is
written alongside the executable, and the two devices' logs are not combined.

Sender startup snapshots report the current capture stage and its duration,
capture notification/frame counts, GPU-converted frames, submitted encoder
inputs, encoded outputs and hardware encoder input/output events. Snapshots
arrive every second until the first encoded frame, then every five seconds;
a final snapshot is recorded on termination and included in sender errors.
This distinguishes absent Windows capture frames from an encoder that accepts
input but produces no output, independently of control-channel liveness.
Sender memory snapshots also report process private/working-set bytes, managed
heap/commit and collection counts, and per-process DXGI local/nonlocal GPU usage
and budgets. Unavailable budget APIs are explicitly marked. GPU conversion
failures identify the specific allocation/view/tone-map/blit operation and the
cumulative NV12 allocation count (not a count of still-live textures).
These diagnostics do not establish that a previous out-of-memory event was an
application leak. Do not reproduce an HDR/AV1 workload that caused a system hang
without a separately agreed, resource-bounded diagnostic plan.
Hardware input admission follows Media Foundation `METransformNeedInput` credits,
not a fixed number of frames in flight. Some encoders buffer more than four
inputs before their first output (for example Qualcomm AV1); an extra client-side
four-frame cap can deadlock their startup. No input is submitted without a credit,
and the existing startup/stall deadlines and encoded-queue budgets still apply.
DXGI input buffers also explicitly set and verify their valid length using the
buffer's reported maximum before submission. Qualcomm hardware encoders can
depend on this metadata even though the pixels remain on the GPU. Diagnostics
include the input buffer length before/after initialization and its capacity.
The hardware self-test checks these actual buffer lengths for every tested codec.
Invalid encoder output errors retain the payload-size limit and report the
actual byte count, buffer capacity and both Media Foundation output statuses.
On Qualcomm GPUs (vendor IDs `0x5143` and DXGI FourCC `0x4D4F4351`), the encoder also requests
`AVScenarioInfo=CameraRecord` and zero B frames. This follows the documented
Qualcomm frame-dropping/timestamp workaround used by Chromium. Other vendors'
codec policy is unchanged. Diagnostics record the GPU vendor and requested policy;
failure to apply a required property is explicit, and empty encoded output is
rejected except for the narrowly identified Qualcomm H.264/HEVC no-payload behavior described
below.

Qualcomm H.264 and HEVC have reported a successful, driver-provided whole-sample
output with a valid submitted-input timestamp but zero payload. Only an exact
`S_OK`, zero sample/transform statuses, positive buffer capacity, provided/whole/
single-sample flags, no caller allocation and a known pending nonnegative PTS
qualify. That exact case is retired as a driver-dropped input: it is counted and
reported as a nonfatal encoder warning, never sent as an empty media packet.
Timestamp accounting also retires inputs skipped by the driver without an output
event. Recovered encoder frame drops remain in diagnostics and counters only;
they do not open a user-facing warning, because casting continues and no user
action is required. Fatal encoder/session failures still show an explicit error.
Capture progress accounts for both real outputs and drops, while missing
or unrelated timestamps, malformed nonempty data, oversized payloads and native
errors remain fatal. First-frame readiness still requires real encoded data.
The same FIFO policy applies only to both Qualcomm vendor IDs and these two
codecs. Timestamp bookkeeping is bounded at 256 entries and validated before
submission; exceeding that bookkeeping bound is an explicit fault, not a new
MF credit limit, throttle or FPS cap. Tracked surface callbacks, not timestamp
accounting, continue to own surface recycling.

The legacy MPEG profile attribute is left unspecified for AV1; its actual
bitstream must still satisfy the supported AV1 Main/low-latency constraints.
The tested Qualcomm AV1 driver emits hidden/reordered frames that the current
parser rejects. AV1 sending is conservatively rejected for both Qualcomm vendor
IDs above, not just one driver version; select
H.264 or HEVC rather than relying on an automatic codec fallback.
Hardware-MFT enumeration proves neither this displayed-frame contract nor
driver stability. Low-latency, CameraRecord and zero-B-frame settings do not
guarantee AV1 output without hidden or show-existing frames. Supporting those
frames requires a decode/display-order, timestamp, random-access and recovery
design, not removing the parser's displayed-frame check. Small SDR H.264 tests
cannot establish Qualcomm AV1 compatibility or full-resolution HDR stability.
Receiver GPU diagnostics distinguish readiness waits, rejected/occluded Presents
and actual presentation attempts. An empty/resized swapchain can submit its first
frame without a readiness signal, and rejected nonblocking Presents retain their
slot until a frame is successfully submitted. Only real presentations count as
playback progress. If Windows reports occlusion after actual frames are decoded
and all tracks are configured, only the presentation deadline is suspended.
Network liveness, configuration deadlines and decoder/audio health checks remain
active; visibility restoration starts a fresh ten-second presentation deadline.
Incoming video requests window activation, but Windows focus policy may still
require bringing the receiver to the foreground manually.
See [Chromium's Media Foundation encoder](https://github.com/chromium/chromium/blob/main/media/gpu/windows/media_foundation_video_encode_accelerator_win.cc)
for the same GPU-buffer length requirement.

Each instance has a separate UTF-8 log capped at 2 MiB, plus one rolling backup.
Startup retains ten closed log files in addition to active instances' files;
oversized individual entries are marked as truncated. Logging failures appear
as visible app errors rather than silently discarding diagnostic data.
Isolated self-tests do not write production logs.

For an already-restored offline candidate, `tools\Publish.ps1 -NoRestore` keeps
both app and worker publication from initiating a restore; publication validation
and the requirement for a new/empty output directory are unchanged.

## Build and test

### Production and regression boundaries

The production backend remains Windows Graphics Capture plus direct Media
Foundation encoding in the dedicated capture worker. The disposable recording
archive/export feature, sender-cadence probe, native thread/CPU sampling,
constructed native empty-output experiment and standalone FFmpeg capture prototype have been removed from source,
not retained behind a build flight. Historical bundles/reports are unchanged.
There is no recording option, receive tap, mux helper or diagnostic publish switch.
Unsupported command-line options fail locally before normal window startup.

Reusable isolated UI/layout/Statistics, encoder/color, media/occlusion,
Stop/peer, recovery, rotation and high-FPS self-tests remain available for native
regression acceptance. Their local flags are explicit; normal startup does not
run them. Peer timeline/long-session tools and bounded numeric timing diagnostics
remain useful for reproducible A/V/recovery acceptance. These do not replace real
audible/manual synchronization, mute, installed-package or long-session checks.
Production capture cadence/PTS, credit/surface ownership, strict Qualcomm repair,
GPU budgets, runtime errors and aligned Statistics are unchanged.

Publishing and packaging enforce a file/dependency denylist: no recording-export
helper, standalone FFmpeg/test launcher, mux/new media ABI, FFmpeg.AutoGen 8.x or
PDBs. The production decoder remains FFmpeg.AutoGen 7.1.1 with avcodec 61,
avutil 59 and swresample 5; no extra licensing/runtime dependencies are introduced.

`RCF1` remains the internal production worker format, not a network protocol change. Its
16-byte big-endian header contains magic, width, height and codec-configuration
length, followed by that configuration. Each packet has a 16-byte header:
payload length (`u32`), microsecond capture PTS (`i64`) and keyframe flag (`u32`),
then encoded H.264 bytes. The reader rejects dimension changes, configuration
over 64 KiB, payloads outside 1..4 MiB, unsupported flags, reversed timestamps
and truncated data. Actual SPS dimensions/profile, access-unit syntax and the
first random-access picture are also checked before transport.

```powershell
dotnet test .\tests\RedrainCast.Tests.slnx -c Release
.\tools\PrepareNative.ps1 -Architecture arm64
dotnet build .\src\RedrainCast.App\RedrainCast.App.csproj -c Release -p:Platform=ARM64 -r win-arm64
.\src\RedrainCast.App\bin\ARM64\Release\net10.0-windows10.0.26100.0\win-arm64\RedrainCast.App.exe
```

For x64, prepare `-Architecture x64` and build with `-p:Platform=x64 -r win-x64`.
The development build also builds and copies the self-contained capture worker.
Its runtime configuration remains self-contained in both development and
publication, avoiding a framework-dependent configuration mixed with previously
copied local runtime files.
The pinned native preparation script downloads the FFmpeg decoder/audio runtime
in a user-local cache. No native compiler or network-transport library is needed.
It does not install drivers or change system PATH.

```powershell
.\tools\Publish.ps1 -Architecture arm64
.\tools\Package.ps1 -Architecture arm64 -PublishedDirectory .\artifacts\win-arm64 `
  -OutputDirectory .\artifacts\msix-arm64
```

Publication/package output directories must be new or empty. Packaging checks
PE architecture, required notices, assembly/package version and the exact
Partner Center identity. See [STORE.md](STORE.md).
The optional [Store update pipeline](docs/store/pipeline.md) consumes paired
private CI artifacts only after a first manual publication, with separate
upload/certification dispatches and manual final publication. An explicit
listing-enabled upload can include all 22 translations and 44 screenshots
after confirmation of the exact reviewed commit. Package-only uploads preserve
listings. Both modes retain the supported self-contained, untrimmed MSIX format;
single-file executables are not used for Store distribution.
The repository release version in `Directory.Build.props` supplies the default
publication version; packaging infers it from the published app instead of an
independently hard-coded default. An explicit `Publish.ps1 -Version` is still
supported. Both executable manifests are generated from that build's assembly
version. Publication checks the app, worker and internal library assembly/file
versions and both embedded executable manifest versions against one release;
mixed-version bundles fail rather than displaying a misleading version.

Interactive Windows self-tests use an isolated loopback listener without
discovery or persisted settings. The UI self-test also selects the default
display and preflights its hardware encoder to verify outgoing connection
failure recovery, but never captures that display. It then captures only its
own RedrainCast window to an in-process loopback test peer to verify the real
inline Cancel/Stop controls, discovery expiry and acknowledged cleanup.
No captured content is recorded or sent to another device:

The UI self-test writes a bounded, flushed phase journal beside its requested
report, in `<report-name>-diagnostics`. It starts before window/receiver
initialization and records the last phase even when the process times out before
writing its final JSON. This journal is isolated from normal app logs. Its final
report also records the actual displayed version; a timeout without JSON is not
a successful test.

```powershell
RedrainCast.App.exe --self-test C:\temp\redrain-ui.json
RedrainCast.App.exe --color-self-test C:\temp\redrain-color.json
RedrainCast.App.exe --encoder-self-test C:\temp\redrain-encoder.json
RedrainCast.App.exe --media-self-test `
  E:\gitpersonal\RedrainCast-Windows\tests\ScrcpyReceive.Tests\fixtures\synthetic-h264.json `
  C:\temp\redrain-media.json
RedrainCast.App.exe --occlusion-self-test `
  E:\gitpersonal\RedrainCast-Windows\tests\ScrcpyReceive.Tests\fixtures\synthetic-h264.json `
  C:\temp\redrain-occlusion.json
```

Media self-tests transmit labeled synthetic video and silent stereo PCM over
the three TCP connections through receiver admission, the hardware decoder,
WinUI video surface and actual audio playback. They require at least five video
frames and 4,800 played audio frames, and verify fullscreen entry/restoration.
The isolated media test temporarily raises its own synthetic-video window to
ensure presentation can actually be observed, then restores its prior topmost
state. Normal receiving does not force an always-on-top window.
The separate occlusion test hides only its own synthetic receiver window and
requires decoded video, explicit DXGI occlusion and a healthy connection for
eleven seconds, beyond the normal startup deadline. Its report distinguishes
occlusion survival from actual visible presentation; it is not a substitute for
the normal media presentation check. If DXGI keeps accepting Presents while the
test window is hidden, the occlusion check reports that its prerequisite was not
observed rather than claiming success.
They are not a substitute for testing two physical devices and real audio.
The encoder-only self-test uses 600 generated GPU frames per available
hardware codec to test sustained encoding, actual input-buffer lengths and encoded color metadata. It
does not create a display capture item or require a network peer, so it can
isolate encoder problems from capture/network startup. Its report explicitly
marks the display-color probe as excluded; use the full color test for that check.
The color self-test uses synthetic HDR/SDR patches and diagnostic-only pixel
readback to check GPU lookup interpolation within 2 RGB code values, NV12
luma/chroma within 3 code values, and distinct HDR highlights above SDR white.
Independent non-vertex libplacebo swatches, not the old custom formula, provide
unit-test references. It also checks each available hardware encoder and verifies
encoded color metadata through hardware decoding. Real HDR frame comparisons
are a separate visual acceptance check, not inferred from these numeric tests.

### Regenerating color tables

Normal builds use the checked-in numeric tables; no FFmpeg executable,
Vulkan runtime or libplacebo library is needed by the app. Regeneration is a
developer-only operation requiring FFmpeg with libplacebo, 12-bit libx265 and a
working Vulkan reference device:

```powershell
.\tools\GenerateToneMaps.ps1 -FfmpegPath C:\tools\ffmpeg.exe -WorkDirectory C:\temp\tone-map-generation
```

The generator uses lossless 4:4:4 HDR reference transport, explicit per-bank
HDR10 peak metadata, BT.2390 knee offset 1.0, perceptual gamut mapping and no
spatial contrast recovery. It also regenerates independent test swatches.
The manifest records the reference executable hash, version, layout and table
hashes. It is not run by normal builds or CI.

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
