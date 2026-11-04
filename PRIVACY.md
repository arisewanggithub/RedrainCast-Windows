# RedrainCast privacy

RedrainCast sends screen/window content and optional output audio directly to a
receiver chosen by the user. It also receives screen/audio streams from peers
that can reach its listening port. Incoming casts are accepted automatically.

Use only on trusted local networks. Custom TCP screen/audio traffic and UDP
discovery are not authenticated or encrypted. Discovery broadcasts this PC's
device name, control port and availability. No account or cloud relay is used.
The app does not intentionally record media, upload telemetry or retain
screen/audio payloads. Runtime media dependencies do not introduce a cloud
service.

Sharing settings are stored under the current user's local application data
in `RedrainCast\settings.json`. Capture selections, connection targets and active
sessions are not persisted. A bounded diagnostic log remains in memory and may
contain device/network addresses, codec details and errors, never media payloads.
Explicit developer self-tests write reports only to a caller-specified path.

Receiving pauses while this app sends and automatically resumes when sending
ends. Closing the app normally stops listeners/capture and releases owned media
resources. No remote keyboard/mouse control is granted by receiving video.
