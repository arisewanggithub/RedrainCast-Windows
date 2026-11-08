using System.Text.Json.Nodes;
using JsonObject = RedrainCast.Receive.Core.JsonObject;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RedrainCast.Core;
using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using RedrainCast.Send.Media;
using ReceiverWire = RedrainCast.Receive.Core.Wire;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    internal async Task RunHighFpsSelfTestAsync(string reportFile)
    {
        if (!isolated) throw new InvalidOperationException("High-FPS probing requires an isolated receiver.");
        string? failure = null;
        var phase = "starting";
        var measurements = new List<JsonNode>();
        var presentationEvidence = new List<JsonNode>();
        var controlJournals = new List<JsonNode>();
        var recordedPresentations = 0;
        var droppedPresentationSamples = 0;
        var originalPresenter = AppWindow.Presenter as OverlappedPresenter;
        var originalOnTop = originalPresenter?.IsAlwaysOnTop;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var keyProofs = new List<long>();
        try
        {
            if (originalPresenter is not null) originalPresenter.IsAlwaysOnTop = true;
            video.CollectPresentationSamples = true;
            Activate();
            // One independently decodable, generated 640x360 H.264 picture. No capture or audio source.
            var picture = await Task.Run(() => MediaDiagnostics.EncodeColorProbeAsync("h264", deadline.Token, checkGpuBudget: true), deadline.Token);
            if (!RedrainCast.Send.Core.VideoBitstream.IsKeyFrame("h264", picture.Data, picture.Configuration))
                throw new IOException("Synthetic fixture did not begin with a random-access H.264 picture.");
            foreach (var fps in new[] { 0, 90, 120 })
            {
                var sourceFps = fps == 0 ? 120 : fps;
                var nominalFps = fps == 0 ? 60 : fps;
                var server = receiver ?? throw new IOException("Isolated listener is unavailable.");
                using var control = new TcpClient { NoDelay = true };
                using var videoPeer = new TcpClient { NoDelay = true };
                using var audioPeer = new TcpClient { NoDelay = true };
                var session = fps == 0 ? 1L : fps;
                phase = $"{fps} control connect/hello";
                await control.ConnectAsync(IPAddress.Loopback, server.Port, deadline.Token);
                var protocol = new HighFpsProbeControl(control.GetStream(), session);
                try
                {
                    await protocol.SendAsync(Packet.Control(PacketType.Hello, 0,
                        JsonData.Object(("role", "sender"), ("versions", JsonData.Value(new[] { 3 })),
                            ("name", "Synthetic high-FPS receiver probe"), ("uncappedFps", true), ("streamRecovery", true),
                            ("streamRecoveryVersion", StreamRecovery.Version))), deadline.Token);
                    var hello = await ReadControlAsync(PacketType.Hello);
                    if (!StreamRecovery.Negotiated(hello.Object(), acknowledgement: true))
                        throw new IOException("Native recovery support was not negotiated.");
                    if (!JsonObject.Boolean(hello.Object(), "uncappedFps"))
                        throw new IOException("Uncapped FPS support was not advertised.");
                    phase = $"{fps} session request";
                    var request = new RedrainCast.Send.Core.CaptureSettings(MaxSize: 640, MaxFps: sourceFps, EnableAudio: false)
                        .Request("Generated H.264 key picture");
                    request["video"]!["maxFps"] = fps;
                    await protocol.SendAsync(Packet.Control(PacketType.SessionRequest, session,
                        request), deadline.Token);
                    var accepted = await ReadControlAsync(PacketType.SessionAccept);
                    var token = JsonObject.Text(accepted.Object(), "mediaToken", 64);
                    foreach (var (socket, channel) in new[] { (videoPeer, "video"), (audioPeer, "audio") })
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, server.Port, deadline.Token);
                        await ReceiverWire.WriteAsync(socket.GetStream(), Packet.Control(PacketType.Hello, session,
                            JsonData.Object(("role", "media"), ("channel", channel), ("token", token))), deadline.Token);
                        if ((await ReceiverWire.ReadAsync(socket.GetStream(), deadline.Token)).Type != PacketType.Hello)
                            throw new IOException("Synthetic media socket attachment failed.");
                    }
                    uint currentGeneration = 0;
                    var sentPts = new HashSet<long>();
                    for (uint generation = 1; generation <= 2; generation++)
                    {
                        phase = $"{fps} generation {generation} configuration";
                        sentPts.Clear();
                        currentGeneration = generation;
                        await ReceiverWire.WriteAsync(videoPeer.GetStream(), new(PacketType.StreamConfig, session, 1, generation, -1, 0,
                            JsonData.Bytes(JsonData.Object(("codec", "h264"), ("width", 640), ("height", 360),
                                ("csd", JsonData.Value(new[] { Convert.ToBase64String(picture.Configuration) }))))), deadline.Token);
                        await ReadControlAsync(PacketType.StreamReady);
                        if (video.RequestedFramesPerSecond != fps || video.ProcessorInputFramesPerSecond != nominalFps ||
                            video.ProcessorOutputFramesPerSecond != nominalFps)
                            throw new IOException("Initial or rebuilt presenter did not preserve the requested content rate.");
                        video.CheckDiagnosticGpuBudget();
                        var decodedBefore = video.DecodedFrames;
                        var presentedBefore = video.PresentedFrames;
                        var skippedBefore = video.SkippedFrames;
                        var samplesBefore = video.PresentationSnapshot();
                        var first = QpcUs();
                        await SendPictureAsync(first);
                        foreach (var state in new[] { StreamRecovery.Key, StreamRecovery.Complete })
                        {
                            var proof = (await ReadControlAsync(PacketType.Status)).Object();
                            if (proof.GetProperty("state").GetString() != state ||
                                JsonObject.Number(proof, "track") != 1 ||
                                StreamRecovery.Timestamp(proof, "keyPtsUs") != first)
                                throw new IOException("Native decoded-key proof changed the synthetic source PTS.");
                        }
                        keyProofs.Add(first);
                        phase = $"{fps} generation {generation} synthetic media";
                        var emitted = 1;
                        var clock = Stopwatch.StartNew();
                        var nextBudgetCheck = 0.0;
                        while (clock.Elapsed.TotalSeconds < 2.5)
                        {
                            if (Problem.IsOpen) throw new IOException(Problem.Message);
                            if (clock.Elapsed.TotalSeconds >= nextBudgetCheck)
                            {
                                video.CheckDiagnosticGpuBudget();
                                nextBudgetCheck += 0.5;
                            }
                            var remaining = emitted / (double)sourceFps - clock.Elapsed.TotalSeconds;
                            if (remaining > 0) { await Task.Delay(TimeSpan.FromSeconds(remaining), deadline.Token); continue; }
                            await SendPictureAsync(QpcUs());
                            emitted++;
                        }
                        var pumpSeconds = clock.Elapsed.TotalSeconds;
                        await Task.Delay(150, deadline.Token);
                        var seconds = (QpcUs() - first) / 1_000_000.0;
                        var decoded = video.DecodedFrames - decodedBefore;
                        var presented = video.PresentedFrames - presentedBefore;
                        var skipped = video.SkippedFrames - skippedBefore;
                        if (fps == 0 && (decoded / seconds <= 60 ||
                            !video.PresentationDiagnostics.Contains("requestedFps=Uncapped", StringComparison.Ordinal)))
                            throw new IOException("Uncapped decoding did not exceed the nominal hint or diagnostics lost the uncapped request.");
                        if (presented < 5 || decoded < 5 || video.HardwareSurfaceCount < 1 ||
                            HomePage.Visibility != Visibility.Collapsed || roles.Role != CastRole.Receiving ||
                            AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen || video.PlayedAudioFrames != 0 ||
                            video.IsPresentationSuspended)
                            throw new IOException("Synthetic reception did not expose hardware fullscreen video without audio.");
                        var snapshot = video.PresentationSnapshot();
                        var samples = snapshot.Samples.Skip(samplesBefore.Samples.Length).ToArray();
                        var dropped = snapshot.Dropped - samplesBefore.Dropped;
                        var ptsUntouched = samples.All(sample => sentPts.Contains(sample.PtsUs));
                        var monotonic = samples.Zip(samples.Skip(1), (a, b) => b.PtsUs >= a.PtsUs).All(value => value);
                        if (samples.Length < 5 || dropped != 0 || !ptsUntouched || !monotonic)
                            throw new IOException("Bounded real-presenter snapshots did not prove unchanged monotonic source PTS.");
                        recordedPresentations += samples.Length;
                        droppedPresentationSamples += dropped;
                        var sourceSeconds = (sentPts.Max() - first) / 1_000_000.0;
                        var presentationSeconds = Stopwatch.GetElapsedTime(samples[0].Qpc, samples[^1].Qpc).TotalSeconds;
                        presentationEvidence.Add(JsonData.Object(("fps", fps), ("generation", generation),
                            ("sourcePtsUs", JsonData.Value(sentPts.Order().ToArray())), ("samples", ReportJson.Value(samples)), ("Dropped", dropped)));
                        measurements.Add(JsonData.Object(
                            ("fps", fps), ("generation", generation), ("PlaybackEpoch", video.PlaybackEpoch),
                            ("processorInputFps", video.ProcessorInputFramesPerSecond),
                            ("processorOutputFps", video.ProcessorOutputFramesPerSecond),
                            ("denominator", 1), ("configurationCorrect", true), ("ProcessorConfigurations", video.ProcessorConfigurations),
                            ("seconds", seconds), ("pumpSeconds", pumpSeconds), ("emitted", emitted),
                            ("sourceWindowSeconds", sourceSeconds), ("presentationWindowSeconds", presentationSeconds),
                            ("sourceFps", (emitted - 1) / sourceSeconds), ("decoded", decoded), ("decodedFps", decoded / seconds),
                            ("presented", presented), ("presentedFps", (samples.Length - 1) / presentationSeconds),
                            ("skipped", skipped), ("notPresentedAtSample", decoded - presented),
                            ("lastPresentedPtsUs", video.LastPresentedPtsUs), ("HardwareSurfaceCount", video.HardwareSurfaceCount),
                            ("DecoderDiagnostics", video.DecoderDiagnostics), ("PresentationDiagnostics", video.PresentationDiagnostics),
                            ("PlaybackDiagnostics", video.PlaybackDiagnostics), ("ptsUntouched", ptsUntouched), ("monotonic", monotonic),
                            ("recordedPresentations", samples.Length), ("presentationSamplesDropped", dropped), ("throughputCertified", false)));
                    }
                    phase = $"{fps} Stop/SESSION_END";
                    await protocol.SendAsync(Packet.Control(PacketType.Stop, session,
                        JsonData.Object(("reason", "bounded synthetic receiver probe complete"))), deadline.Token);
                    await ReadControlAsync(PacketType.SessionEnd);
                    while (roles.Role != CastRole.Idle || video.IsAttached || server.IsBusy)
                        await Task.Delay(20, deadline.Token);
                    Render();
                    if (ReadinessTitle.Text != L("Ready") || HomePage.Visibility != Visibility.Visible || video.KeepsDisplayAwake)
                        throw new IOException("Stop did not restore READY home and release owned surfaces/power requests.");

                    async Task SendPictureAsync(long pts)
                    {
                        sentPts.Add(pts);
                        await ReceiverWire.WriteAsync(videoPeer.GetStream(),
                            new(PacketType.VideoFrame, session, 1, currentGeneration, pts, 1, picture.Data), deadline.Token);
                    }

                    Task<Packet> ReadControlAsync(PacketType expected) => protocol.ReadAsync(expected, deadline.Token);
                }
                finally
                {
                    // Retire media before the control reader/socket; never leave an unobserved control task.
                    videoPeer.Dispose();
                    audioPeer.Dispose();
                    await protocol.DisposeAsync();
                    control.Dispose();
                    controlJournals.Add(JsonData.Object(("fps", fps), ("events", JsonData.Value(protocol.Journal)), ("mediaRetiredBeforeControl", true)));
                }
            }
        }
        catch (Exception error) { failure = $"{phase}: {error}"; }
        finally
        {
            deadline.Cancel();
            try
            {
                if (receiver is not null) await receiver.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (roles.Role != CastRole.Idle || video.IsAttached) await Task.Delay(20, cleanup.Token);
                Render();
            }
            catch (Exception error) { failure = $"{failure}\nCleanup: {error}"; }
            video.CollectPresentationSamples = false;
            if (originalPresenter is not null && originalOnTop is { } onTop) originalPresenter.IsAlwaysOnTop = onTop;
        }
        await File.WriteAllTextAsync(reportFile, JsonData.Text(JsonData.Object(
            ("passed", failure is null && measurements.Count == 6), ("failure", failure),
            ("measurements", JsonData.Value(measurements)), ("keyProofs", JsonData.Value(keyProofs)),
            ("ptsUntouched", measurements.Count == 6 ? true : (bool?)null), ("monotonic", measurements.Count == 6 ? true : (bool?)null),
            ("recordedPresentations", recordedPresentations), ("droppedPresentationSamples", droppedPresentationSamples),
            ("presentationEvidence", JsonData.Value(presentationEvidence)), ("qpcFrequency", Stopwatch.Frequency),
            ("controlJournals", JsonData.Value(controlJournals)),
            ("ptsEvidence", "Opt-in bounded successful native-present snapshots; independent of audio and MediaTiming."),
            ("rateWindows", "Source/presentation FPS use first-to-last event intervals; decode FPS uses first-source-to-post-drain observation time. No physical display-refresh guarantee."),
            ("ready", ReadinessTitle.Text == L("Ready")), ("detached", !video.IsAttached), ("awakeReleased", !video.KeepsDisplayAwake),
            ("syntheticOnly", true), ("width", 640), ("height", 360), ("codec", "h264"), ("audioOff", true),
            ("captureChildrenExpected", 0), ("captureUsed", false), ("microphoneUsed", false), ("userAppsTouched", false),
            ("processId", Environment.ProcessId), ("throughputCertified", false), ("diagnostics", JsonData.Value(diagnostics))),
            indented: true));

        static long QpcUs() => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);
    }
}
