using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RedrainCast.Core;
using RedrainCast.Protocol;
using ScrcpyReceive.Core;
using ScrcpySend.Media;
using ReceiverWire = ScrcpyReceive.Core.Wire;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    internal async Task RunHighFpsSelfTestAsync(string reportFile)
    {
        if (!isolated) throw new InvalidOperationException("High-FPS probing requires an isolated receiver.");
        string? failure = null;
        var phase = "starting";
        var measurements = new List<object>();
        var presentationEvidence = new List<object>();
        var controlJournals = new List<object>();
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
            if (!ScrcpySend.Core.VideoBitstream.IsKeyFrame("h264", picture.Data, picture.Configuration))
                throw new IOException("Synthetic fixture did not begin with a random-access H.264 picture.");
            foreach (var fps in new[] { 90, 120 })
            {
                var server = receiver ?? throw new IOException("Isolated listener is unavailable.");
                using var control = new TcpClient { NoDelay = true };
                using var videoPeer = new TcpClient { NoDelay = true };
                using var audioPeer = new TcpClient { NoDelay = true };
                var session = (long)fps;
                phase = $"{fps} control connect/hello";
                await control.ConnectAsync(IPAddress.Loopback, server.Port, deadline.Token);
                var protocol = new HighFpsProbeControl(control.GetStream(), session);
                try
                {
                    await protocol.SendAsync(Packet.Control(PacketType.Hello, 0,
                        new
                        {
                            role = "sender",
                            versions = new[] { 3 },
                            name = "Synthetic high-FPS receiver probe",
                            streamRecovery = true,
                            streamRecoveryVersion = 1
                        }), deadline.Token);
                    var hello = await ReadControlAsync(PacketType.Hello);
                    if (!hello.Object().GetProperty("streamRecovery").GetBoolean())
                        throw new IOException("Native recovery support was not negotiated.");
                    phase = $"{fps} session request";
                    await protocol.SendAsync(Packet.Control(PacketType.SessionRequest, session,
                        new ScrcpySend.Core.CaptureSettings(MaxSize: 640, MaxFps: fps, EnableAudio: false)
                            .Request("Generated H.264 key picture")), deadline.Token);
                    var accepted = await ReadControlAsync(PacketType.SessionAccept);
                    var token = JsonObject.Text(accepted.Object(), "mediaToken", 64);
                    foreach (var (socket, channel) in new[] { (videoPeer, "video"), (audioPeer, "audio") })
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, server.Port, deadline.Token);
                        await ReceiverWire.WriteAsync(socket.GetStream(), Packet.Control(PacketType.Hello, session,
                            new { role = "media", channel, token }), deadline.Token);
                        if ((await ReceiverWire.ReadAsync(socket.GetStream(), deadline.Token)).Type != PacketType.Hello)
                            throw new IOException("Synthetic media socket attachment failed.");
                    }
                    var epoch = video.PlaybackEpoch;
                    uint currentGeneration = 0;
                    var sentPts = new HashSet<long>();
                    for (uint generation = 1; generation <= 2; generation++)
                    {
                        phase = $"{fps} generation {generation} configuration";
                        sentPts.Clear();
                        currentGeneration = generation;
                        await ReceiverWire.WriteAsync(videoPeer.GetStream(), new(PacketType.StreamConfig, session, 1, generation, -1, 0,
                            JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                codec = "h264",
                                width = 640,
                                height = 360,
                                csd = new[] { Convert.ToBase64String(picture.Configuration) }
                            })), deadline.Token);
                        await ReadControlAsync(PacketType.StreamReady);
                        if (video.RequestedFramesPerSecond != fps || video.ProcessorInputFramesPerSecond != fps ||
                            video.ProcessorOutputFramesPerSecond != fps || video.PlaybackEpoch != epoch + generation - 1)
                            throw new IOException("Initial or rebuilt presenter did not preserve the requested content rate.");
                        video.CheckDiagnosticGpuBudget();
                        var first = QpcUs();
                        await SendPictureAsync(first);
                        foreach (var state in new[] { StreamRecovery.Key, StreamRecovery.Complete })
                        {
                            var proof = (await ReadControlAsync(PacketType.Status)).Object();
                            if (proof.GetProperty("state").GetString() != state ||
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
                            var remaining = emitted / (double)fps - clock.Elapsed.TotalSeconds;
                            if (remaining > 0) { await Task.Delay(TimeSpan.FromSeconds(remaining), deadline.Token); continue; }
                            await SendPictureAsync(QpcUs());
                            emitted++;
                        }
                        var pumpSeconds = clock.Elapsed.TotalSeconds;
                        await Task.Delay(150, deadline.Token);
                        var seconds = (QpcUs() - first) / 1_000_000.0;
                        if (video.PresentedFrames < 5 || video.DecodedFrames < 5 || video.HardwareSurfaceCount < 1 ||
                            HomePage.Visibility != Visibility.Collapsed || roles.Role != CastRole.Receiving ||
                            AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen || video.PlayedAudioFrames != 0 ||
                            video.IsPresentationSuspended)
                            throw new IOException("Synthetic reception did not expose hardware fullscreen video without audio.");
                        var snapshot = video.PresentationSnapshot();
                        var samples = snapshot.Samples;
                        var ptsUntouched = samples.All(sample => sentPts.Contains(sample.PtsUs));
                        var monotonic = samples.Zip(samples.Skip(1), (a, b) => b.PtsUs >= a.PtsUs).All(value => value);
                        if (samples.Length < 5 || snapshot.Dropped != 0 || !ptsUntouched || !monotonic)
                            throw new IOException("Bounded real-presenter snapshots did not prove unchanged monotonic source PTS.");
                        recordedPresentations += samples.Length;
                        droppedPresentationSamples += snapshot.Dropped;
                        var sourceSeconds = (sentPts.Max() - first) / 1_000_000.0;
                        var presentationSeconds = Stopwatch.GetElapsedTime(samples[0].Qpc, samples[^1].Qpc).TotalSeconds;
                        presentationEvidence.Add(new { fps, generation, sourcePtsUs = sentPts.Order().ToArray(), samples, snapshot.Dropped });
                        measurements.Add(new
                        {
                            fps,
                            generation,
                            video.PlaybackEpoch,
                            processorInputFps = video.ProcessorInputFramesPerSecond,
                            processorOutputFps = video.ProcessorOutputFramesPerSecond,
                            denominator = 1,
                            configurationCorrect = true,
                            video.ProcessorConfigurations,
                            seconds,
                            pumpSeconds,
                            emitted,
                            sourceWindowSeconds = sourceSeconds,
                            presentationWindowSeconds = presentationSeconds,
                            sourceFps = (emitted - 1) / sourceSeconds,
                            decoded = video.DecodedFrames,
                            decodedFps = video.DecodedFrames / seconds,
                            presented = video.PresentedFrames,
                            presentedFps = (samples.Length - 1) / presentationSeconds,
                            skipped = video.SkippedFrames,
                            notPresentedAtSample = video.DecodedFrames - video.PresentedFrames,
                            lastPresentedPtsUs = video.LastPresentedPtsUs,
                            video.HardwareSurfaceCount,
                            video.DecoderDiagnostics,
                            video.PresentationDiagnostics,
                            video.PlaybackDiagnostics,
                            ptsUntouched,
                            monotonic,
                            recordedPresentations = samples.Length,
                            presentationSamplesDropped = snapshot.Dropped,
                            throughputCertified = false
                        });
                    }
                    phase = $"{fps} Stop/SESSION_END";
                    await protocol.SendAsync(Packet.Control(PacketType.Stop, session,
                        new { reason = "bounded synthetic receiver probe complete" }), deadline.Token);
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
                    controlJournals.Add(new { fps, events = protocol.Journal, mediaRetiredBeforeControl = true });
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
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
        {
            passed = failure is null && measurements.Count == 4,
            failure,
            measurements,
            keyProofs,
            ptsUntouched = measurements.Count == 4 ? true : (bool?)null,
            monotonic = measurements.Count == 4 ? true : (bool?)null,
            recordedPresentations,
            droppedPresentationSamples,
            presentationEvidence,
            qpcFrequency = Stopwatch.Frequency,
            controlJournals,
            ptsEvidence = "Opt-in bounded successful native-present snapshots; independent of audio and MediaTiming.",
            rateWindows = "Source/presentation FPS use first-to-last event intervals; decode FPS uses first-source-to-post-drain observation time. No physical display-refresh guarantee.",
            ready = ReadinessTitle.Text == L("Ready"),
            detached = !video.IsAttached,
            awakeReleased = !video.KeepsDisplayAwake,
            syntheticOnly = true,
            width = 640,
            height = 360,
            codec = "h264",
            audioOff = true,
            captureChildrenExpected = 0,
            captureUsed = false,
            microphoneUsed = false,
            userAppsTouched = false,
            processId = Environment.ProcessId,
            throughputCertified = false,
            diagnostics
        }, new JsonSerializerOptions { WriteIndented = true }));

        static long QpcUs() => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);
    }
}
