using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RedrainCast.Send.Core;
using RedrainCast.Send.Media;
using Windows.UI;
using RedrainCast.Core;
using RedrainCast.Protocol;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    private Func<IAudioSource>? peerSelfTestAudioFactory;

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeerWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeerWindowMinimized(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint PeerWindowProcess(nint window, out uint process);

    internal async Task RunPeerSelfTestAsync(string role, string directory, bool receiverStops, IPAddress address,
        int durationSeconds = 0, bool silentPcm = false, int sampleSeconds = 5, bool timeline = false,
        bool staticSource = false)
    {
        if (!isolated) throw new InvalidOperationException("Peer tests require an isolated window.");
        if ((durationSeconds != 0 && durationSeconds is < 60 or > 600) || sampleSeconds != 5)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (timeline && (!silentPcm || durationSeconds != 60))
            throw new ArgumentException("Timing capture requires generated silent PCM and a bounded sixty-second peer test.");
        if (staticSource && (durationSeconds != 0 || !silentPcm || timeline || !IPAddress.IsLoopback(address)))
            throw new ArgumentException("Static startup requires isolated loopback, silent PCM and the bounded default duration.");
        var requiredFrames = staticSource ? 1 : 60;
        string? failure = null;
        JsonNode? measurements = null;
        Window? probe = null;
        DispatcherTimer? animation = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds == 0 ? 60 : durationSeconds + 75));
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var samples = new List<JsonNode>();
        var clock = Stopwatch.StartNew();
        long baselinePrivate = 0, peakPrivate = 0;
        var nextSample = TimeSpan.Zero;
        diagnosticLog = new DiagnosticLog(Path.Combine(directory, role + "-diagnostics"));
        diagnosticLogPath = diagnosticLog.FilePath;
        using var timing = timeline ? MediaTiming.Start() : null;
        MediaTimingSnapshot? timingSnapshot = null;
        string[]? timelineValidationErrors = null;
        try
        {
            peerSelfTestAudioFactory = silentPcm ? () => new RecoveryPcmSource() : null;
            Diagnostic($"Peer self-test: role={role}, durationSeconds={durationSeconds}, silentPcm={silentPcm}; production deadlines unchanged.");
            if (role == "receiver")
            {
                if (!IPAddress.IsLoopback(address))
                {
                    await StopReceiverAsync();
                    await StartReceiverAsync(0, address);
                }
                var server = receiver ?? throw new InvalidOperationException("Peer test receiver is not listening.");
                await WriteAsync("endpoint", JsonData.Object(("port", server.Port), ("instanceId", instanceId), ("address", address.ToString())));
                while (video.PresentedFrames < 1)
                {
                    if (Problem.IsOpen) throw new IOException(Problem.Message);
                    await Task.Delay(25, timeout.Token);
                }
                if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
                    throw new IOException("Separate-process playback did not enter fullscreen automatically.");
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                AppWindow.Resize(new(800, 600));
                AppWindow.Move(new(740, 400));
                while (video.PresentedFrames < requiredFrames || silentPcm &&
                       (video.PlayedAudioFrames < 4800 || video.FirstRecoverySourceAudioPtsUs is null))
                {
                    if (Problem.IsOpen) throw new IOException(Problem.Message);
                    await Task.Delay(25, timeout.Token);
                }
                if (!video.IsPlaying || video.DecodedFrames < requiredFrames || video.HardwareSurfaceCount < 1 ||
                    roles.Role != CastRole.Receiving || HomePage.Visibility != Visibility.Collapsed)
                    throw new IOException("Separate-process receiver did not show real hardware video fullscreen.");
                clock.Restart();
                await SampleAsync();
                var playback = Snapshot();
                await WriteAsync("playback", ReportJson.Value(playback)!);
                var expectedSession = server.RecoveryState?.Session ??
                    throw new IOException("Peer stability requires an owned recovery session.");
                var progress = new PeerStabilityProgress(expectedSession, silentPcm);
                var recoveryTransitions = new List<JsonNode>();
                RedrainCast.Receive.Core.ReceiverRecoveryState? previousState = null;
                long previousEpoch = -1;
                bool? previousAttached = null;
                while (clock.Elapsed < TimeSpan.FromSeconds(durationSeconds))
                {
                    var state = server.RecoveryState;
                    if (state != previousState || previousEpoch != video.PlaybackEpoch || previousAttached != video.IsAttached)
                    {
                        recoveryTransitions.Add(JsonData.Object(("elapsedSeconds", clock.Elapsed.TotalSeconds), ("state", ReportJson.Value(state)),
                            ("epoch", video.PlaybackEpoch), ("attached", video.IsAttached),
                            ("role", roles.Role.ToString()), ("problemOpen", Problem.IsOpen), ("problem", Problem.Message)));
                        previousState = state; previousEpoch = video.PlaybackEpoch; previousAttached = video.IsAttached;
                        await WriteAsync("receiver-transitions", JsonData.Value(recoveryTransitions));
                    }
                    try
                    {
                        progress.Observe(clock.ElapsedMilliseconds, video.PlaybackEpoch, video.DecodedFrames,
                            video.PresentedFrames, video.PlayedAudioFrames, roles.Role == CastRole.Receiving,
                            video.IsAttached, Problem.IsOpen, state);
                    }
                    catch (IOException error)
                    {
                        await SampleAsync();
                        throw new IOException($"{error.Message} Problem={Problem.Message}", error);
                    }
                    if (clock.Elapsed >= nextSample)
                    {
                        await SampleAsync();
                    }
                    await Task.Delay(25, timeout.Token);
                }
                await SampleAsync();
                playback = Snapshot();
                if (durationSeconds > 0) await WriteAsync("sustained", ReportJson.Value(playback)!);
                var stopping = Stopwatch.StartNew();
                if (receiverStops) await server.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
                else stopping.Stop();
                while (roles.Role != CastRole.Idle || video.IsAttached)
                {
                    await Task.Delay(25, timeout.Token);
                }
                using var restored = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                restored.CancelAfter(TimeSpan.FromSeconds(5));
                while (roles.Role != CastRole.Idle || video.IsAttached ||
                       ReadinessTitle.Text != L("Ready") || HomePage.Visibility != Visibility.Visible)
                    await Task.Delay(25, restored.Token);
                if (silentPcm && playback.playedAudioFrames < 4800)
                    throw new IOException("Retained pre-stop playback measurements lack real WASAPI source content.");
                measurements = JsonData.Object(("decoded", playback.decoded), ("presented", playback.presented), ("hardwareSurfaces", playback.hardwareSurfaces),
                    ("automaticFullscreenVerified", playback.automaticFullscreenVerified), ("playedAudioFrames", playback.playedAudioFrames),
                    ("firstSourceAudioPtsUs", playback.firstSourceAudioPtsUs), ("maximumAvSkewUs", playback.maximumAvSkewUs), ("maximumSteadySkewUs", playback.maximumSteadySkewUs),
                    ("avMeasurements", playback.avMeasurements), ("skippedFrames", playback.skippedFrames), ("audioSilenceFrames", playback.audioSilenceFrames),
                    ("audioDroppedFrames", playback.audioDroppedFrames), ("playbackDiagnostics", playback.playbackDiagnostics), ("elapsedSeconds", playback.elapsedSeconds),
                    ("playbackMetricsPhase", "before-stop"),
                    ("avWithin250ms", silentPcm ? playback.avMeasurements > 0 &&
                        playback.maximumSteadySkewUs <= 250_000 : (bool?)null),
                    ("ready", true), ("homeVisible", true), ("idle", true), ("detached", true),
                    ("receiverStopMilliseconds", receiverStops ? stopping.Elapsed.TotalMilliseconds : (double?)null));
            }
            else if (role == "sender")
            {
                await WaitForFileAsync("endpoint");
                using var endpoint = await ReadProofAsync("endpoint");
                if (endpoint.RootElement.GetProperty("address").GetString() != address.ToString())
                    throw new IOException("Peer test sender and receiver addresses do not match.");
                var target = new ReceiverEndpoint(address, endpoint.RootElement.GetProperty("port").GetInt32(),
                    "Separate-process receiver", false, endpoint.RootElement.GetProperty("instanceId").GetString());
                probe = new Window { Title = "RedrainCast owned synthetic peer source" };
                var text = new TextBlock { Text = "Static before capture", FontSize = 36,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)) };
                probe.Content = new Grid { Background = new SolidColorBrush(Color.FromArgb(255, 20, 70, 120)), Children = { text } };
                probe.AppWindow.Resize(new(640, 360));
                probe.AppWindow.Move(new(40, 40));
                if (probe.AppWindow.Presenter is OverlappedPresenter probePresenter) probePresenter.IsAlwaysOnTop = true;
                var ticks = 0;
                animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                animation.Tick += (_, _) =>
                {
                    text.Text = $"Synthetic frame {++ticks}";
                    if (ticks % 100 == 0) Diagnostic($"Peer synthetic animation ticks={ticks}");
                };
                probe.Activate();
                if (!staticSource) animation.Start();
                await Task.Delay(200, timeout.Token);
                var ownedWindow = WinRT.Interop.WindowNative.GetWindowHandle(probe);
                _ = PeerWindowProcess(ownedWindow, out var ownerPid);
                if (ownerPid != Environment.ProcessId || !PeerWindowVisible(ownedWindow) || PeerWindowMinimized(ownedWindow))
                    throw new IOException("Owned synthetic capture window is not visible or is minimized.");
                settings = new(MaxSize: 640, MaxFps: 20, VideoCodec: "h264", EnableAudio: silentPcm);
                SetSource(MediaDiagnostics.OwnWindow(ownedWindow));
                var run = StartSendingAsync(target);
                var proof = durationSeconds == 0 ? "playback" : "sustained";
                while (!File.Exists(FileName(proof)))
                {
                    if (run.IsCompleted)
                    {
                        await run;
                        if (!receiverStops) throw new IOException($"Peer cast ended early: {Problem.Message}");
                    }
                    if (clock.Elapsed >= nextSample) await SampleAsync();
                    await Task.Delay(25, timeout.Token);
                }
                using var playback = await ReadProofAsync(proof);
                if (playback.RootElement.GetProperty("decoded").GetInt64() < requiredFrames ||
                    playback.RootElement.GetProperty("presented").GetInt64() < requiredFrames ||
                    playback.RootElement.GetProperty("hardwareSurfaces").GetInt32() < 1 ||
                    !playback.RootElement.GetProperty("automaticFullscreenVerified").GetBoolean())
                    throw new IOException($"Peer receiver did not prove {requiredFrames} real hardware presentations.");
                if (silentPcm && (playback.RootElement.GetProperty("playedAudioFrames").GetInt64() < 4800 ||
                    playback.RootElement.GetProperty("firstSourceAudioPtsUs").ValueKind == JsonValueKind.Null))
                    throw new IOException("Peer receiver did not prove real WASAPI source-content playback.");
                if (durationSeconds > 0 && playback.RootElement.GetProperty("elapsedSeconds").GetDouble() < durationSeconds)
                    throw new IOException("Peer receiver stability proof ended before its requested duration.");
                if (!receiverStops)
                {
                    animation.Stop();
                    await Task.Delay(TimeSpan.FromSeconds(12), timeout.Token);
                    if (run.IsCompleted) throw new IOException("An unchanged captured window incorrectly ended the cast.");
                }
                await SampleAsync();
                var stopping = Stopwatch.StartNew();
                if (!receiverStops) StopSending();
                await run.WaitAsync(timeout.Token);
                if (Problem.IsOpen || roles.Role != CastRole.Idle || receiver is null || ReadinessTitle.Text != L("Ready"))
                    throw new IOException($"Separate-process sender did not restore READY: {Problem.Message}");
                var ended = diagnostics.LastOrDefault(line => line.Contains("Sender session ended:", StringComparison.Ordinal));
                if (ended is null || !ended.Contains("exited=True", StringComparison.Ordinal) ||
                    !diagnostics.Any(line => line.Contains("GPU surface pool stopped:", StringComparison.Ordinal) &&
                        line.Contains("inUse=0,", StringComparison.Ordinal)) || stopping.Elapsed > TimeSpan.FromSeconds(5))
                    throw new IOException("Peer sender did not release its tracked surfaces and capture child within five seconds.");
                measurements = JsonData.Object(("childExited", true), ("surfacesReturned", true), ("unchangedWindowSurvived", !receiverStops),
                    ("stopMilliseconds", stopping.Elapsed.TotalMilliseconds), ("sender", ended), ("ready", true), ("idle", true),
                    ("ownSourceWindowVisibleVerified", true), ("ownSourcePidVerified", true),
                    ("sourceWindowHandle", ownedWindow.ToInt64()), ("syntheticAudioFactory", silentPcm),
                    ("staticBeforeCapture", staticSource), ("animationTicks", ticks));
            }
            else throw new ArgumentException("Peer test role must be sender or receiver.");
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            StopSending();
            if (sending is not null)
            {
                try { await sending.WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (Exception error) { failure ??= $"Peer cleanup failed: {error}"; }
            }
            animation?.Stop();
            probe?.Close();
            peerSelfTestAudioFactory = null;
            try
            {
                await StopReceiverAsync();
                if (timing is not null)
                {
                    timingSnapshot = timing.Stop();
                    await WriteAsync(role + "-timeline", ReportJson.Value(timingSnapshot)!);
                    timelineValidationErrors = MediaTiming.CheckPeerTrace(timingSnapshot, role == "sender");
                    if (timelineValidationErrors.Length > 0)
                        failure ??= "Process timing trace incomplete: " + string.Join(" ", timelineValidationErrors);
                }
            }
            catch (Exception error) { failure ??= $"Peer timing/receiver cleanup failed: {error}"; }
        }
        await WriteAsync(role, JsonData.Object(("passed", failure is null), ("failure", failure), ("role", role), ("receiverStops", receiverStops), ("measurements", measurements),
            ("pid", Environment.ProcessId), ("loopbackOnly", IPAddress.IsLoopback(address)), ("receiverAddress", address.ToString()),
            ("syntheticOwnedWindowOnly", true), ("audioEnabled", silentPcm), ("generatedSilentPcmOnly", silentPcm), ("durationSeconds", durationSeconds), ("sampleSeconds", sampleSeconds),
            ("staticSource", staticSource), ("requiredFrames", requiredFrames),
            ("resourceSamples", samples.Count), ("baselinePrivateBytes", baselinePrivate == 0 ? (long?)null : baselinePrivate),
            ("peakPrivateBytes", peakPrivate), ("timelineEnabled", timeline), ("timelineComplete", timingSnapshot is null ? (bool?)null :
                timingSnapshot.Complete && timelineValidationErrors is { Length: 0 }),
            ("timelineValidationErrors", JsonData.Value(timelineValidationErrors)), ("timelineDropped", timingSnapshot?.Dropped),
            ("opticalLatencyMeasured", false), ("perceivedAudioQualityMeasured", false), ("diagnostics", JsonData.Value(diagnostics))));

        PeerPlaybackSnapshot Snapshot() => new(video.DecodedFrames, video.PresentedFrames, video.HardwareSurfaceCount,
            true, video.PlayedAudioFrames, video.FirstRecoverySourceAudioPtsUs, video.MaximumAvSkewUs,
            video.MaximumSteadySkewUs, video.AvMeasurements, video.SkippedFrames,
            video.AudioRecovery.Silence, video.AudioRecovery.Dropped, video.PlaybackDiagnostics, clock.Elapsed.TotalSeconds,
            video.PlaybackEpoch, receiver?.RecoveryState, video.LastAvTiming,
            video.LastAvTiming is { } timing ? Environment.TickCount64 - timing.ObservedAtMilliseconds : null,
            video.MaximumAvTiming);

        async Task SampleAsync()
        {
            using var process = Process.GetCurrentProcess();
            var memory = process.PrivateMemorySize64;
            if (memory > 2L * 1024 * 1024 * 1024) throw new IOException("Peer app exceeded its 2 GiB private-memory guard.");
            peakPrivate = Math.Max(peakPrivate, memory);
            if (clock.Elapsed.TotalSeconds >= 15 && baselinePrivate == 0) baselinePrivate = memory;
            samples.Add(JsonData.Object(("elapsedSeconds", clock.Elapsed.TotalSeconds), ("utc", DateTime.UtcNow),
                ("pid", Environment.ProcessId), ("privateBytes", memory), ("workingSetBytes", process.WorkingSet64),
                ("growthFromWarmupBytes", baselinePrivate == 0 ? (long?)null : memory - baselinePrivate),
                ("role", roles.Role.ToString()), ("playback", ReportJson.Value(role == "receiver" ? Snapshot() : null)),
                ("receiverDiagnostics", role == "receiver" ? receiver?.Diagnostics : null),
                ("scheduling", RedrainCast.Receive.Core.ReceiveDiagnostics.ThreadPoolStatus()),
                ("processCpuMilliseconds", process.TotalProcessorTime.TotalMilliseconds),
                ("gpuBytes", (long?)null), ("gpuUnavailableReason", "External harness queries PID-scoped GPU counters; managed process metrics do not expose GPU allocations.")));
            nextSample = clock.Elapsed + TimeSpan.FromSeconds(sampleSeconds);
            await WriteAsync(role + "-samples", JsonData.Value(samples));
        }

        string FileName(string name) => Path.Combine(directory, name + ".json");
        async Task<JsonDocument> ReadProofAsync(string name)
        {
            var waiting = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    await using var stream = new FileStream(FileName(name), new FileStreamOptions
                    {
                        Mode = FileMode.Open, Access = FileAccess.Read,
                        Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.Asynchronous
                    });
                    return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                }
                catch (IOException) when (waiting.Elapsed < TimeSpan.FromSeconds(5) && !timeout.IsCancellationRequested)
                {
                    await Task.Delay(25, timeout.Token);
                }
            }
        }
        async Task WriteAsync(string name, JsonNode value)
        {
            var file = FileName(name);
            await File.WriteAllTextAsync(file + ".tmp", JsonData.Text(value, indented: true));
            File.Move(file + ".tmp", file, true);
        }
        async Task WaitForFileAsync(string name)
        {
            while (!File.Exists(FileName(name))) await Task.Delay(25, timeout.Token);
        }
    }

}

internal sealed record PeerPlaybackSnapshot(long decoded, long presented, int hardwareSurfaces,
        bool automaticFullscreenVerified, long playedAudioFrames, long? firstSourceAudioPtsUs,
        long maximumAvSkewUs, long maximumSteadySkewUs, int avMeasurements, long skippedFrames,
        long audioSilenceFrames, long audioDroppedFrames, string playbackDiagnostics, double elapsedSeconds,
        long playbackEpoch, RedrainCast.Receive.Core.ReceiverRecoveryState? recoveryState,
        RedrainCast.Receive.Core.AvTimingSample? avTiming, long? avTimingAgeMilliseconds,
        RedrainCast.Receive.Core.AvTimingSample? maximumAvTiming);
