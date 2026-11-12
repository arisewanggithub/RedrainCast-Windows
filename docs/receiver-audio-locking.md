# Receiver audio ownership and refill

`AudioPlayback` keeps native packet decoding and `AudioRegulator.Push` on the
producer gate. The WASAPI worker never takes that gate: previously its
clock/catch-up update waited behind the whole decode and swresample operation,
preventing already queued PCM from reaching an otherwise ready device.

The short control gate (`startGate`) protects generation publication, coherent
device/presentation reads, recovery controls, clock observation, and device
write/submit/start. It contains no decode or swresample work. `AudioBuffer`
retains its own bounded FIFO guard; the producer and worker share only those
short FIFO operations. Lock order is producer, control, then FIFO/device or
presentation. No path acquires the producer gate from the control gate.

Configure/dispose operations are serialized separately. Retirement first waits
for the native producer owner, then detaches the generation under the control
gate. Immediate retirement also cancels there, so a block already pulled by the
worker cannot write after detachment. Normal reconfiguration explicitly permits
the retired worker to drain its owned generation, without reattaching its clock.
Native drain occurs only after producer detachment; cancellation and worker
completion precede device, decoder, regulator and event disposal. Repeated
disposal is idempotent and configuration after disposal is rejected.

WASAPI refill/start remain on the existing dedicated audio worker; position reads
continue using the existing synchronized device clock calculation. Event-driven
device waits, coalesced startup signals, FIFO limits, target adaptation, nominal
48 kHz rate, FFmpeg resampler source-interval accounting, and late/monotonic clock
semantics are unchanged. Suspension/session stop synchronize with write/start;
no try-lock refill skipping, fallback renderer or extra PCM queue is introduced.

The audio coordinator tests link the production `AudioPlayback` against
deterministic native-boundary doubles and run as a separate CI test project:

```powershell
dotnet test tests\RedrainCast.Receive.Audio.Tests\RedrainCast.Receive.Audio.Tests.csproj
dotnet test tests\RedrainCast.Receive.Tests\RedrainCast.Receive.Tests.csproj
dotnet build src\RedrainCast.Receive.Media\RedrainCast.Receive.Media.csproj -r win-arm64 -p:WindowsArchitecture=arm64 -o artifacts\audio-lock-validation\media
dotnet build src\RedrainCast.App\RedrainCast.App.csproj -r win-arm64 -p:Platform=ARM64 -p:WindowsArchitecture=arm64 -o artifacts\audio-lock-validation\app
```

Tests pause decoding/conversion while queued PCM refills, read source position,
retire in-flight output, cancel configuration/decode, check exact-once ownership,
reset/drain partial startup PCM, and exercise coalesced startup/device wakes.
Real-device verification still requires a separately authorized deployment and
cast: inspect output-loop/write gaps and FIFO silence during decode pressure,
then verify audible startup, recovery, normal drain, stop and endpoint errors.
No live application needs to be stopped or redeployed for the automated checks.
