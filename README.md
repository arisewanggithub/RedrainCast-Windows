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
  Maximum capture FPS uses a dropdown with **Uncapped (default)**, 15, 24, 25,
  30, 48, 50, 60, 90 and 120.
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
| Stream range / Profile / depth / Output | Incoming validated HDR10 or SDR, plus actual decoded transfer (e.g. `HDR10 · PQ`); decoder profile and surface bit depth (e.g. `Main 10 · 10-bit`). Output is separately `HDR output`, `SDR (tone-mapped)`, `SDR output`, or N/A before successful presentation. Windows caches the last successful submission/redraw policy and refreshes it after monitor changes, without extra 1-Hz device queries. FP16 scRGB HDR output does not claim the display's physical HDR wire protocol. SDR streams remain SDR on HDR displays; absent metadata remains unknown. |
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

The receive engine accepts uncapped (0) or 1–240 FPS requests for hardware H.264, HEVC and AV1, with
optional PCM/AAC/Opus audio. The send engine uses Windows Graphics Capture,
hardware Media Foundation H.264/HEVC/AV1, and optional receiver-only audio.
There is no software video or codec fallback. Hardware support is device/driver
dependent. Enabled outgoing audio suppresses local playback and restores owned
mute changes on normal Stop/close/handled failures. A crash or force kill cannot
guarantee mute restoration.

Receiver admission is not a throughput certification: actual codec, negotiated SDR
or HDR10 profile, raster and hardware decoding/processing must configure successfully,
and failures stay visible without software fallback or FPS coercion.
The D3D11 video processor uses the session's positive requested content rate for both
input/output descriptors, including rebuilds and resize/rotation resets; these
descriptors are not measured presentation FPS or a conversion to display Hz.
For uncapped requests it uses a nominal 60/1 descriptor hint only; this does not
pace capture, decoding or presentation. The decoder uses packet timestamps and
does not set a frame-rate hint. Statistics show **Uncapped** for the capture limit
while received, decoded and presented FPS remain independently measured.
A 60 Hz panel can present the latest available picture while decoding a faster
stream. Capture, decode and display rates depend on codec, resolution, source
and hardware. The Windows sender defaults to uncapped capture, offers positive
caps through 120 FPS, and preserves existing saved limits. Discovery does not advertise an FPS
capability. HDR AV1 and Qualcomm AV1 sending remain unsupported.

`tools\Test-HighFpsReceiver.ps1 -App <published-exe> -ReportDirectory <new-directory>`
runs a bounded, isolated, loopback-only native receiver proof. Its fresh interactive
desktop gate must pass without waking or unlocking the machine: any visible,
non-minimized foreground window and a visible, non-minimized owned helper are
required. The receiver-only probe injects no input; foreground ownership is
required only for input injection, which this probe does not perform.
It generates one small 640x360 SDR H.264 key picture without capture, replays that
synthetic picture for five seconds each at requested uncapped/90/120 FPS with source-clock
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
| New Windows → legacy Windows/Android receiver, checked, positive FPS cap selected | No option acknowledgement; entire statistics request extension absent | Normal streaming and Stop; raw TCP legacy receiver fixtures, persistent nonfatal notice |
| Future peer, revision 1 with unknown name/member | Known intersection only | Known statistics work; unknown option ignored; raw TCP and parser tests |
| Future peer, unknown revision | No common option schema | Legacy cast accepted, off; raw TCP and parser tests |
| Malformed known fields / duplicate / oversized envelope | Reject `PROTOCOL` | Strict parser and raw TCP rejection tests |

Android evidence is **source inspection plus Windows-hosted wire fixtures**, not
physical-phone validation. Inspected current Android `ReceiverLink.kt` offers
rotation/recovery and validates their known types while ignoring other HELLO
members; `ReceiverServer.kt` echoes only those capabilities. `Configuration.kt`
at the time accepted FPS 1–240, ignored unrelated request members, and explicitly denied
`mediaTransport`. No Android source, phone or ADB was modified or exercised.

#### Uncapped capture FPS (optional wire-v3 extension)

Control sender HELLO may offer `"uncappedFps":true`. The Windows receiver always
advertises `"uncappedFps":true` in its control HELLO, even if the sender omitted
the offer or offered false. A present offer must be Boolean; null, numbers,
strings, arrays, objects and duplicate properties are rejected as `PROTOCOL`.
`SESSION_REQUEST.video.maxFps` must be an exact JSON integer from 0 through 240:
0 means uncapped/unknown capture rate, and 1–240 remain capture caps. Negative,
over-range, fractional/exponent, string/coerced and duplicate values are rejected.
Senders must send zero only when the receiver explicitly acknowledges true;
otherwise they must report an update/select-cap error, not silently choose 60.
The Windows sender offers this capability and defaults to zero for new settings;
existing saved positive caps remain unchanged. A legacy receiver requires the user
to update it or select a positive cap before any SESSION_REQUEST is sent.
Both normal and recovery sessions use the same capability gate. Windows capture
pacing is disabled at zero, but encoder credits, bounded surfaces and transport
backpressure remain active. Media Foundation and D3D11 receive a positive nominal
60 FPS media-type hint for uncapped streams, not a capture/encoding throttle.
On Windows builds exposing `GraphicsCaptureSession.MinUpdateInterval`, every
new or rearmed capture session explicitly requests zero update interval. Recent
Windows versions otherwise apply a separate WGC default interval that can limit
capture to roughly 50–60 FPS even on high-refresh displays. Positive FPS caps
remain enforced by the application pacer. Diagnostics report the previous and
effective OS interval, or explicitly report when the API is unavailable.
Actual capture/encoding rates depend on source updates and hardware/driver support;
uncapped does not promise unlimited FPS or physical display refresh rates.
Audio, bitrate and resolution defaults are unchanged.
Updated Android peers implement this extension; the earlier Android inspection
above describes the legacy baseline, not the updated Android behavior.
`--sharing-settings-self-test <report.json>` checks the real isolated settings
dialog, including the default label, positive presets and a saved custom cap,
without injected input, capture, audio or persisted settings changes. It shares
the assertions used by the full UI self-test.

Native sender validation uses generated GPU frames, not a user's display, audio,
or network receiver. On the tested ARM64/NVIDIA setup, uncapped 640x360 H.264 and
HEVC each encoded 600 frames above 300 FPS with nominal 60 FPS media types,
exact source timestamps, no dropped inputs, and bounded surface/timing queues.
Separate 30/60/120 FPS probes retained their pacing caps. This proves the nominal
hint is not an application encoding throttle on that hardware; it does not
certify other drivers, AV1, larger rasters, or live capture rates.

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

### Optional independent track recovery v2

Both control HELLOs must explicitly acknowledge Boolean `streamRecovery:true`
and integer `streamRecoveryVersion:2`. An unsupported offer does not negotiate
recovery; an explicit true acknowledgement with a missing or incompatible
revision is rejected. There is no shared-reset v1 fallback.

Recovery uses session-scoped STATUS packets with header track/generation zero,
PTS -1 and flags zero. The JSON includes `state`, `message`, a track (1 video,
2 enabled audio), and that track's positive sequential signed-int32 generation.
REQUEST has no proof. Video KEY and COMPLETE carry the exact `keyPtsUs` sent
for that generation; audio COMPLETE carries `audioPtsUs` and no video-key
dependency. Timestamps are canonical nonnegative signed-int64 decimal strings.
Valid stale generations are ignored, future or conflicting proofs fail, and
matching duplicate proofs are idempotent.

Video and audio have independent bounded queues, generations, workers and
first-unresolved ten-second deadlines. Pressure or reconstruction on one track
does not intentionally reset or gate the healthy track. The initial all-track
READY barrier applies only once; subsequent recovery waits only for the affected
track. Retrying, configuration, READY and proof receipt do not extend a pending
deadline. A started transport frame completes before another frame or Stop;
failed writes retire the transport.
For v2, a receiver-initiated Stop allows up to 22 seconds after its STOP frame
is written for started media/control writes (each bounded to ten seconds) and
SESSION_END to finish. Legacy sessions retain their two-second acknowledgement
bound. This does not extend any media recovery deadline.

Video resumes from a verified fresh random-access picture with matching codec
configuration; decoded KEY and COMPLETE prove the exact sent key. A valid proof
is not rejected merely because decoding or transport delayed its receipt. Audio
recovery uses the original source timeline and an explicit `recoveryPtsUs`, with
unchanged negotiated format, ordered compressed preroll and sample-frame PCM
trimming. Audio COMPLETE proves actual current-generation source audio written
to the output, not silence or a queued packet. Temporary A/V mismatch is not
itself a reason to reset either track.
Resampling may consume tiny source intervals without producing an output frame;
those intervals are retired without inventing output spans or failing playback.

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

Windows sends PCM in packets of at most 120 ms, preserving source sample
timestamps. Midstream Opus reconstruction does not reapply initial pre-skip.
The native recovery self-test uses an owned animated window, H.264 hardware and
generated PCM; portable socket regressions use explicitly fake codecs. Neither
substitutes for a physical Android interoperability test.
Native recovery probes require generation-local decode/presentation deltas and
a presented source timestamp at or after that generation's verified key; prior
generation lifetime counters cannot satisfy recovery success.

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
The SDR path signals BT.709 primaries, transfer, matrix and limited range.

For a desktop (not a window), selecting HEVC enables native HDR10 when the
source display currently has HDR enabled and the receiver explicitly acknowledges
a usable HDR10 playback mode. Capture remains FP16 scRGB; a GPU conversion produces
limited-range P010 with BT.2020/PQ signaling for hardware HEVC Main10 encoding.
No codec is silently substituted. H.264, AV1, window capture and older/incompatible
receivers retain the existing SDR behavior. Known AV1 exclusions still apply.

Native HDR10 sharing remains enabled when display-reported desktop color-volume
metadata is missing, invalid, or cannot be represented after quantization. The
sender logs the omission and sends HDR10 without optional static metadata; it
does not clamp luminance, substitute BT.2020 mastering primaries, or invent
mastering/content peaks. Source closure, unavailable displays, and HDR-active
state changes remain errors requiring restart rather than silently switching
native sharing to SDR.

HDR is an optional version-1 extension of wire version 3. Discovery carries
`receiveCapabilities`: complete codec/profile/color tuples, current
`nativeHdrAvailable` (Boolean or unknown), and verified `hdrToSdr` capability.
The connection HELLO rechecks support, the request and acceptance agree on an exact
`videoColor`, and every video configuration/recovery generation repeats it.
Missing support or acknowledgement never permits sending HDR optimistically.
Discovery remains limited to 1024 bytes; decoder and display probes are cached,
not repeated on every broadcast. HDR-eligible capture sources negotiate color mode
from the live receiver HELLO before preparation. A validated HDR capability v1
reply allows the current receiver's 45-second setup deadline. Legacy receivers
(or unknown capability revisions) use an SDR-only preflight: the sender closes
that connection without SESSION_REQUEST or capture, prepares once within the
separate 30-second capture-preparation budget, then reconnects and renegotiates
recovery and receiver options under a fresh 10-second setup deadline. Late HDR
capabilities do not upgrade already-prepared SDR resources. Non-HDR-eligible
sources retain preparation before connecting; capture starts only after SESSION_ACCEPT.
The shared [HDR wire fixture](protocol/fixtures/hdr-video.json) gives the exact
tuple and integer metadata units, including chromaticities normalized to 50,000
and minimum mastering luminance in 0.0001-nit units.
Wire metadata must agree exactly. Decoded mastering chromaticities allow at most
one 1/50,000 unit of hardware requantization from Media Foundation's float
primaries; larger differences remain fatal, as do mismatched luminance bounds,
content light levels or coded BT.2020/PQ color information.
Minimum mastering luminance is also limited to 65,535 of those units (and cannot
exceed the maximum), preserving Android's CTA-861.3 unsigned 16-bit descriptor
without truncation; the usual 0.005-nit value remains 50.

Windows receiving uses hardware HEVC Main10/P010 decoding and high-precision
Direct3D presentation. HDR-enabled displays use native HDR presentation; SDR outputs
use explicit GPU tone/gamut mapping rather than relying on Windows' clipping.
Moving the receiver or toggling HDR changes the local output policy without
changing the encoded stream. A window spanning displays uses its primary output;
identical native HDR on mixed HDR/SDR displays is not promised.
Turning HDR off on the sending display during a native-HDR cast requires restarting
the cast rather than reinterpreting SDR frames as HDR.

Native HDR output requires Windows HDR to be active on the current monitor and a
finite DXGI monitor peak luminance between 80 and 10,000 nits. If that peak is
missing or invalid, RedrainCast does not guess a display limit: it explicitly uses
the hardware-verified local HDR-to-SDR BT.2390/BT.709 renderer, reports the reason,
and advertises `nativeHdrAvailable=false` while HDR10 reception remains available
only after the local decoder/converter preflight succeeds. This changes only the
application's output; Windows HDR settings, encoded network video, and decoded HDR
source data remain unchanged. Existing monitor/display notifications refresh the
cached policy and restore native output when a valid peak becomes available.
Physical HDR panel appearance is not verified by these automated checks.

Transient DXGI display-detection or monitor-metadata failures (for example while
moving the window between monitors) leave the Windows HDR setting unknown and
disable native HDR output without guessing monitor luminance. Once the GPU HDR
renderer has passed preflight, HDR10 reception remains available and an active
HDR stream uses explicit BT.2390/BT.709 SDR tone-mapping; an unverified renderer
is not advertised as supported. A later valid display query restores native HDR
with the validated 80-10000-nit DXGI peak without restarting the decoder or
session. Actual GPU rendering, device-removal, or swapchain failures remain fatal.
Playback statistics reflect the output only after successful rendering; these
checks do not verify physical HDR-panel appearance.

Display-policy and viewport updates share an immutable pending presentation request.
The renderer captures that request before presenting or redrawing and acknowledges
only the exact request successfully submitted; busy, occluded, failed, or superseded
attempts cannot consume newer updates. On a static desktop, the retained last decoded
frame is redrawn with the latest HDR/SDR mode, validated monitor peak, and viewport
without requiring another network frame or repeated metadata query. Display queries
retain cached renderer verification, output statistics describe the last successful
presentation, and UI updates never wait for the native decode/render gate. The
encoded HDR source and Windows HDR settings are unchanged.

Static metadata, when available, describes the desktop's source display color
volume, not the original video's mastering display. Unknown content-light levels
remain unspecified. Composed desktop capture cannot recover original Dolby Vision
or HDR10+ dynamic metadata. Native HDR still depends on the actual device/driver,
decoder, output display and rendering path; capability lists are not a guarantee
of real-time throughput or visually identical output. Android receivers need the
compatible negotiated HDR implementation and an HDR-preserving SurfaceView path;
universal uninterrupted Android HDR-to-SDR fallback is not assumed.

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

### Official MSIX releases and Actions storage

Pull requests still test and package both architectures, but do not upload large
Actions artifacts or receive release-write permissions. Successful main pushes
and manual main builds publish official **non-prerelease** `vX.Y.Z` GitHub
releases in this source repository (access still follows repository visibility).
The only download assets are `RedrainCast-arm64.msix`, `RedrainCast-x64.msix`,
their `.sha256` files, and `ci-manifest.json`; there is **no portable ZIP**.
Both packages are downloaded and verified before the run/attempt/commit/version-bound
manifest is uploaded and the draft is published. Store automation downloads
these exact MSIX bytes, never a rebuild or an Actions binary artifact.
Release status is not Store approval. Failed packaging can leave an incomplete draft, which
is never accepted by the Store workflow. To retry, use **Re-run all jobs** or a
new main build so the draft and both architectures share the new run attempt.

Download release files with authorized `gh release download vX.Y.Z` access.
GitHub Release assets do not consume Actions artifact storage. Successful
tests retain no TRX artifact; failed-test diagnostics expire after one day.
Small sanitized Store receipts remain private Actions artifacts for 14 days
because explicit draft submission depends on them. Store publication remains
manual and separately confirmed; nothing automatically dispatches a Store upload.

CI overrides the local default version for **both architectures**: publishing
receives `X.Y.Z`, packaging receives `X.Y.Z.0` with the same version identity.
PR packaging uses an explicit non-default `0.2.1` version to verify that both
executable manifests, file versions and assemblies follow version overrides.
The source executable manifest is versioned before Windows App SDK captures
and merges its self-contained activation declarations.
With workflow run number
`N` and run attempt `A`, its three-component version is
`0.(2 + floor((N - 1) / 655)).(100 * ((N - 1) % 655) + A)`.
Thus run 42/attempt 2 is `v0.2.4102`; MSIX uses `0.2.4102.0`.
Every version exceeds the previous `0.1.20` baseline. Supported run numbers
are 1–42,924,770 and attempts 1–99, keeping every MSIX component at most 65535.
Exhaustion fails closed and requires a reviewed version-epoch change; values
never wrap. Versions increase by run number then attempt, not completion time:
rerunning an older run yields a unique historical version, but cannot demote
the current higher version marked **Latest**. Use **Re-run all jobs**, not only
failed jobs, so preparation and both architectures use the same attempt.

#### Optional signing for direct installation

Without signing credentials the release MSIX files are **unsigned and not
directly installable**. Store submission accepts these inputs and the Store
signs the published packages independently. For installable GitHub downloads,
the owner can configure repository Actions secrets `MSIX_SIGNING_PFX_BASE64`
(a persistent code-signing PFX encoded as base64) and `MSIX_SIGNING_PASSWORD`.
Both secrets are supplied only to the trusted main release packaging step,
never PR builds. The certificate subject must exactly match the `Publisher`
in `StoreIdentity.json`; retain the same certificate across updates. Do not
generate a new throwaway publisher certificate per build. Signed releases and
Store submission still consume the **same signed MSIX bytes**; the Store's
final distribution signature may differ.

For private testing with a self-signed certificate, manually trust only its
public certificate after independently confirming its origin and fingerprint;
never import a private key on test machines or blindly trust a downloaded
certificate. The pipeline does not install packages or alter certificate trust.
These packages use the same identity as the installed Store app: testing can
interact with or update that app and its data. Review the installed identity
and version first; never automatically replace a Store installation.

`Package.ps1` only orchestrates and validates the pinned official
`Microsoft.Windows.SDK.BuildTools.MSIX` SDK. Its MSBuild targets create the
package manifest, root `resources.pri`, and MSIX; no handwritten MakePri/MakeAppx
packaging path is used. Validation checks the complete compiled WinUI/framework
resources, every supported language/key, and shell resource lookup, and verifies
the app/helper published payload remains byte-identical inside the package.
Both PR and main release builds also launch the extracted x64 staging payload
with `--package-startup-self-test` through a bounded 90-second owned-process
startup smoke check, requiring exit code zero and a JSON report with `passed=true`.
It verifies an isolated window, a finite positive XAML root, resources and an
initialized loopback receiver without input injection, capture or settings changes.
The x64 hosted runner cannot launch ARM64. This is an **extracted-payload startup
smoke test**, not input/layout acceptance, installation, an installed-identity/Store
test, or physical-device acceptance.
The three Windows test projects run in parallel on separate CI runners.
Within each project, xUnit collections remain serialized to avoid CPU scheduling
noise in wall-clock protocol deadline tests; global single-CPU/build limits
are unnecessary for these single-project jobs. Read-only PR packaging for ARM64
and x64 starts alongside the tests. On main, private draft preparation and
ARM64/x64 signing/packaging also run alongside tests. Only final release
publication waits for all test projects, the CI/Store guards and both package
builds to pass; a failed prerequisite leaves the release unpublished as a draft.
All test cases, concurrent scenarios inside tests, assertions and timeout
bounds remain unchanged; no test filter or skip is applied.
The video-recovery isolation fixture runs its synthetic sender on a dedicated
serial context so shared test-runner scheduling does not stall the healthy audio
source and writer. Production queue-pressure limits and recovery assertions
remain unchanged.

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
dotnet build .\src\RedrainCast.App\RedrainCast.App.csproj -c Release -p:Platform=ARM64 -r win-arm64 -p:SelfContained=true
.\src\RedrainCast.App\bin\ARM64\Release\net10.0-windows10.0.26100.0\win-arm64\RedrainCast.App.exe
```

For x64, prepare `-Architecture x64` and build with `-p:Platform=x64 -r win-x64`.

### Remote development deployment (existing build only)

Deploy and launch an already-built, self-contained Windows output with one command:

```powershell
.\tools\Deploy-Remote.ps1 `
  -BuiltDirectory .\src\RedrainCast.App\bin\ARM64\Release\net10.0-windows10.0.26100.0\win-arm64 `
  -RemoteHost <remote-host> -SSHUser <ssh-user> -KeyPath "$HOME\.ssh\id_ed25519"
```

Packaging, the single ZIP transfer, integrity verification and extraction to a
fresh directory happen **while the existing remote app remains running**. Only
then does one interactive launch command close **all verified RedrainCast instances in the selected desktop**,
including active casts, and start the new app. Closure uses an eight-second
graceful wait followed by verified-process force closure if necessary. Each target
handle is pinned before checking its executable path and creation time, and that
same handle is retained through graceful waiting, forced termination and exit
confirmation. Already-exited targets are retired benignly; PID reuse never
redirects closure to another process.
Owned capture workers are included; unrelated applications and the local app
are untouched. No active-cast check or rebuild is performed. One ZIP preserves
the complete runtime and is transferred once, SHA256-verified, and extracted to
a fresh directory beneath the interactive owner's `RedrainCast-DevDeploy` folder.
Old deployments and user preferences are retained.

The SSH account needs permission to register an interactive scheduled task and
must have a **different SID from the interactive desktop owner**. Same-account
elevated SSH plus a Limited interactive token is unsupported: that token still
owns the controller's profile and can rewrite requests or replace protected child
paths through writable ancestors. Deployment resolves the desktop owner from the
OS and rejects this configuration before creating a request or scheduled task.
Use a distinct SSH controller account; changing only the task's RunLevel or a
read-only ACE does not make a shared SID safe.
The detached task bootstrap uses Task Scheduler `IRegisteredTask.RunEx` with
`TASK_RUN_USE_SESSION_ID` (4), the resolved desktop ID and its owner's SID, not
account-only task startup or the SSH session. The helper checks its actual session
and account against that selection before any close or launch, and the new app
must be in the selected session with one visible RedrainCast window within 30 seconds.
Use `-InteractiveSessionId` when multiple desktops exist, `-RemoteDirectory` to
choose a deployment parent, or `-WhatIf` to validate inputs without changing the
remote host. Offline `-WhatIf` also displays the distinct-SID prerequisite but
cannot resolve remote accounts without connecting. Commands have bounded timeouts
and failures are explicit.
The executable stays in the chosen payload directory, including a read-only
`Program Files` destination. Helper identity/results and their temporary files
instead use a unique request's `reports` directory under the SSH account's profile.
Protected ACLs grant only the resolved interactive SID inherited Modify access
there, alongside the controller, SYSTEM and Administrators; the request descriptor
and parent cancellation marker remain read-only to the interactive user. No payload
or global parent write permissions are broadened.
Failure cancels the unique deployment request before any later close or launch.
The detached helper publishes its PID, creation time and request/script identity
before querying app processes; cancellation and expiry are checked before each
close or launch. Writable identity reports supply only a PID/creation hint.
Cleanup verifies the actual executable, creation time and session on the same
pinned handle against the controller-owned read-only request descriptor. An
independent CIM query runs in an owned child with a three-second deadline, using
the controller's `PSHOME\powershell.exe`, never a descriptor-supplied executable;
`CommandLineToArgvW` tokenization must match the descriptor's complete launcher
arguments, script path and request parameters exactly. Forged report command lines,
substring matches, extra arguments and unavailable queries never authorize a kill.
Unconfirmed authentication or retirement is an explicit failure. Cleanup
terminates only that verified helper and waits up to five seconds for its exit
before removing the scheduled task. The newly launched app is not killed by helper
cleanup. An SSH failure also attempts bounded cancellation through a separate
connection; if termination cannot be confirmed, the command reports that explicitly
rather than claiming successful cleanup. Cancelled request markers are retained
in request-specific directories to reject late-starting helpers.
Recovery independently resolves the desktop from the original caller's session
selection and the OS, not stored SID/session/controller/version claims. It rejects
shared SIDs before reading descriptor authority and checks executable, helper,
report, cancellation path, request ID, SID and session against trusted caller
configuration and that resolved desktop before any task/query/retirement action.
Malformed, mismatched or unverifiable old requests fail explicitly with cleanup
unconfirmed; they never authorize a descriptor-controlled executable.
`tests\tools\Test-DeployRemote.ps1` exercises isolated fake-helper stalls and races,
identity mismatches, independent dummy-app survival, script parsing and offline
`-WhatIf`/unsafe-input guards; it never connects to a peer or closes a user app.
It also checks pinned-target exit/reuse races and report-directory SID permissions,
and runs synthetic helpers against a temporary payload with a real write-deny ACL.
Forged writable-report tests include an unrelated executable, a same-executable
wrong request, embedded/extra arguments and unavailable/error queries; only exact
actual argv permits same-handle retirement. Session tests cover requested desktop
2 versus actual desktop 7 with the same SID, zero wrong-session close/launch
markers, and the RunEx flag/session/SID contract.
Same-SID controller tests use the real current SID and exercise pre-request remote
rejection plus old on-disk recovery with forged SID/session/version claims. A real
owned marker executable proves forged descriptor executables never start.
Distinct-SID recovery, a mocked remote success path retaining Limited/RunEx and
extraction-before-task ordering, and malformed/canonical-field validation are
also covered.
These checks do not impersonate a real Limited interactive user, exercise real
multi-session Terminal Services or deploy remotely.
The result reports old/forced PIDs, new PID/session/window, assembly and archive
hashes, archive bytes/runtime file count, and measured ProcessClose, Package,
Transfer, Extract, LaunchReady and Total seconds. Total is deployment-command
wall time, excludes rebuilding, and is separate from first-time script implementation.

### Project naming and runtime

The send libraries and tests use `RedrainCast.Send.Core`,
`RedrainCast.Send.Media` and `RedrainCast.Send.Tests`; the receive equivalents
use `RedrainCast.Receive.*`. Project folders, namespaces and assembly outputs
follow those names. Shipping checks reject obsolete Scrcpy project binaries
and dependency entries while retaining genuine upstream license notices.
This naming cleanup does not change protocol identifiers or existing settings
and diagnostic locations.

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
verified private CI release packages only after a first manual publication, with separate
upload/certification dispatches and manual final publication. An explicit
listing-enabled upload can include all 22 translations and 44 screenshots
after confirmation of the exact reviewed commit. Package-only uploads preserve
listings. Both modes retain the supported self-contained, untrimmed MSIX format;
single-file executables are not used for Store distribution.

Store submission can use trimmed self-contained MSIX, but no trimmed RedrainCast
candidate is accepted with the current dependency set. Native ARM64 full and
partial trim experiments exposed unresolved WinRT/SharpGen linker diagnostics
and built-in COM requirements for audio, even after moving JSON serialization
to trim-safe metadata. Normal publication and the Store workflow therefore
remain untrimmed; no failing trimmed publish option is exposed.

Do not suppress linker warnings, remove native media/WinUI dependencies, or
disable audio COM support to make a smaller build pass. Any future candidate
needs package, localization, worker, synthetic hardware media and lifecycle
checks before Store submission; a smaller successful build alone is not
release acceptance.

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
RedrainCast.App.exe --hdr-self-test C:\temp\redrain-hdr.json
RedrainCast.App.exe --media-self-test `
  E:\gitpersonal\RedrainCast-Windows\tests\RedrainCast.Receive.Tests\fixtures\synthetic-h264.json `
  C:\temp\redrain-media.json
RedrainCast.App.exe --occlusion-self-test `
  E:\gitpersonal\RedrainCast-Windows\tests\RedrainCast.Receive.Tests\fixtures\synthetic-h264.json `
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

The separate `--hdr-self-test` requires a real HEVC Main10 hardware encoder and
decoder; unavailable hardware is an explicit failed report, not an SDR substitution.
It generates synthetic FP16 scRGB white and saturated color quadrants, checks the
GPU limited-range P010 conversion, validates actual Main10/10-bit BT.2020/PQ VUI
on both sender and receiver, and roundtrips the exact wire color/static metadata.
The hardware-decoded pictures are checked numerically through the production
SDR tone/gamut mapper and native FP16 scRGB presentation, including all four
rotations, decoder/display configuration and local SDR/HDR output transitions.
Additional synthetic renderer checks cover black, SDR white, highlights,
saturated colors and content-peak histogram/temporal policy.
The capture GPU processor's existing synthetic HDR-to-SDR and SDR conversion
checks run too, without adding a sustained HDR or multi-codec benchmark.
The native encode probe runs FP16 scRGB through the actual capture GPU processor
and settings-derived downscale into P010 before Main10 encoding.
The diagnostic also uses the production sender, loopback TCP receiver and native
video playback for both ordinary and recovery transports. It checks acceptance
before synthetic capture, exact source PTS/color/static metadata, generation-2
recovery after a bounded test-only decoder delay, and acknowledged Stop/resource
retirement. This listener binds only loopback and never advertises discovery.
Its typed JSON report includes measured errors and explicit failures; its process
exit code is 0 on success and 1 on diagnostic failure. Pixel readback is confined
to this synthetic diagnostic. It does not capture a desktop, change Windows HDR,
contact a LAN peer or verify physical HDR-panel appearance or Android interoperability.
The existing color and 600-frame encoder-only tests do not automatically add this
Main10 workload.
The HDR and color/encoder self-tests also flush bounded phase journals beside
their reports in `<report-name>-diagnostics`; a native crash or timeout without
a completed report is not a successful test.

Windows ARM64 integration validation on 2026-11-07 used an NVIDIA RTX Spark N1X:
the Release app built with zero warnings/errors. The full core suite passed 1,212
tests; late metadata-bound and loopback changes then passed 94 targeted tests.
The app HDR diagnostic measured zero P010 code error; eight decoded color pictures
covered four rotations in SDR/native policies, with maximum SDR error 1.112 codes
and native error 5.625 nits. Synthetic renderer errors were below 0.929 SDR codes
and 0.00766 scRGB units; the 400-nit mapping error was 0.313 nits, with four
swapchain-policy transitions. Additional checks verified point-sampled P010
up/downscaling, 2x2 chroma and SDR dither (two distinct codes, one-code span,
mean error 0.053 codes). The coded probe covered three decoder reconfigurations
and four generations. Two final app runs passed both TCP paths: ordinary playback
decoded eight pictures; recovery decoded 16 in generation 2, with exact metadata,
source PTS, presentation and acknowledged native/source cleanup.
Production decoder/presenter replay of the original 1080p and 4K fixtures passed
360 metadata-free and 76 full-metadata pictures per raster, with exact assigned
PTS/rotation/color, 23 bounded hardware surfaces and decoder reconfiguration.
Legacy synthetic SDR H.264, HEVC and AV1 each passed 30 access units with no dropped
inputs and all rotation/reconfiguration checks. An initial uninstrumented color
test had a native `ntdll.dll` access violation (`0xC0000005`, offset `0x36e48`);
seven isolated reruns passed, including five further runs bounded to 90 seconds
each. This CLI branch returns before constructing the main window/video host, so
their new asynchronous HDR capability startup probe does not run in this check.
The original crash phase and native/driver/lifetime cause remain unisolated;
the reruns do not prove it fixed or long-term SDR stability.
These numeric results do not verify physical HDR-panel
appearance, Android interoperability or live desktop capture/performance.

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
The original engine provenance is retained below; active project and output
names use `RedrainCast.Send.*` and `RedrainCast.Receive.*`:

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
