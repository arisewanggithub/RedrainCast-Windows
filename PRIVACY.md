# RedrainCast privacy

Public Windows and Android privacy policy:
https://redraincast.redrain.ccwu.cc/privacy/.
This document describes the Windows app; the public policy also explains
Android-specific permissions, storage and update checks.

RedrainCast sends screen/window content and optional output audio directly to a
receiver chosen by the user. It also receives screen/audio streams from peers
that can reach its listening port. Incoming casts are accepted automatically.

Use only on trusted local networks. Custom TCP screen/audio traffic and UDP
discovery are not authenticated or encrypted. Discovery broadcasts this PC's
device name, control port and availability. No account or cloud relay is used.
The app does not intentionally record media, upload telemetry or retain
screen/audio payloads. Runtime media dependencies do not introduce a cloud
service.

The normal source chooser is the Windows system capture picker; Windows manages
its local selection previews and consent UI. On runtimes without public native
source-handle getters, the compatibility chooser starts local, sampled previews
for at most six visible tiles, with one capture session at a time. Those previews
stop and release capture resources when the chooser closes, including Cancel,
incoming casts and app shutdown. The app never saves or sends thumbnails.
Selecting or confirming a source does not start sharing; **Send** starts the cast.

Sharing settings are stored under the current user's local application data
in `RedrainCast\settings.json`. Capture selections, connection targets and active
sessions are not persisted. The app keeps recent diagnostic entries in memory
and writes size-limited diagnostic files under the current user's local
application data in `RedrainCast\Logs`. Files rotate as they fill, and older
inactive log files are pruned when a new log starts. Diagnostics may contain
device/network addresses, capture/codec details, counters, errors and local file
paths, but not screen images or audio recordings. Logs remain local unless you
choose to share them; review and redact identifying information before sharing.
Explicit developer self-tests write reports only to a caller-specified path.

Receiving pauses while this app sends and automatically resumes when sending
ends. Closing the app normally stops listeners/capture and releases owned media
resources. No remote keyboard/mouse control is granted by receiving video.
