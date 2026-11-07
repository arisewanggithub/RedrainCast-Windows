using System.Text.Json.Nodes;
using RedrainCast.Protocol;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RedrainCast.Core;
using RedrainCast.Send.Core;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    internal async Task RunMediaSelfTestAsync(string fixtureFile, string reportFile, bool verifyOcclusion = false)
    {
        string? failure = null;
        long presented = 0;
        long playedAudio = 0;
        var hardwareSurfaces = 0;
        var videoOnly = false;
        var enteredFullscreen = false;
        var exposedBeforePresentation = false;
        var presentationDiagnostics = "";
        var occlusionSurvivalVerified = false;
        var phase = "starting";
        var originalPresenter = AppWindow.Presenter;
        var originalPosition = AppWindow.Position;
        var originalSize = AppWindow.Size;
        var originalWindowState = (originalPresenter as OverlappedPresenter)?.State;
        var originalAlwaysOnTop = (originalPresenter as OverlappedPresenter)?.IsAlwaysOnTop;
        Task? sender = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            if (!verifyOcclusion && originalPresenter is OverlappedPresenter testPresenter) testPresenter.IsAlwaysOnTop = true;
            Activate();
            if (receiver is null) throw new InvalidOperationException("Listener did not start.");
            await using var frames = new SyntheticSource(fixtureFile);
            var audio = new SyntheticAudio(frames.FirstPtsUs);
            var endpoint = new ReceiverEndpoint(IPAddress.Loopback, receiver.Port, "Self-test", false);
            sender = Task.Run(() => new SenderSession().RunAsync(endpoint,
                new CaptureSettings(MaxFps: 30, VideoCodec: frames.Codec, EnableAudio: true), frames, Diagnostic, stop.Token, audio));
            phase = verifyOcclusion ? "waiting for decoded video/audio" : "waiting for visible video/audio";
            while ((verifyOcclusion ? video.DecodedFrames : video.PresentedFrames) < 5 || video.PlayedAudioFrames < 4800)
            {
                if (sender.IsCompleted) await sender;
                await Task.Delay(25, stop.Token);
            }
            if (verifyOcclusion)
            {
                AppWindow.Hide();
                phase = "waiting for DXGI occlusion";
                using (var occlusionWait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    occlusionWait.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        while (!video.IsPresentationSuspended)
                        {
                            if (sender.IsCompleted) await sender;
                            await Task.Delay(25, occlusionWait.Token);
                        }
                    }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                    {
                        throw new NotSupportedException("DXGI continued presenting the hidden test window; occlusion survival could not be verified in this environment.");
                    }
                }
                phase = "verifying eleven-second occlusion survival";
                var until = Environment.TickCount64 + 11_000;
                while (Environment.TickCount64 < until)
                {
                    if (sender.IsCompleted) await sender;
                    if (roles.Role != CastRole.Receiving || receiver?.IsBusy != true || !video.IsAttached)
                        throw new InvalidOperationException("The occluded receiver lost its healthy session.");
                    await Task.Delay(50, stop.Token);
                }
                occlusionSurvivalVerified = video.IsPresentationSuspended && video.DecodedFrames >= 5;
                if (!occlusionSurvivalVerified)
                    throw new InvalidOperationException("The test did not retain an explicitly occluded, decoding session beyond ten seconds.");
                AppWindow.Show();
            }
            CaptureMeasurements();
            if (!videoOnly || hardwareSurfaces == 0)
                throw new InvalidOperationException("Incoming cast did not show hardware-decoded video without the home controls.");
            if (!enteredFullscreen)
                throw new InvalidOperationException("Incoming playback did not automatically enter fullscreen.");
            if (!exposedBeforePresentation)
                throw new InvalidOperationException("The receiver did not expose its decoded video surface before the first presentation.");
            if (!verifyOcclusion)
            {
                AppWindow.SetPresenter(originalPresenter);
                Render();
                if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                    throw new InvalidOperationException("A manual fullscreen exit was overridden during playback.");
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
        }
        catch (Exception error) { failure = $"{phase}: {error}"; }
        finally
        {
            CaptureMeasurements();
            stop.Cancel();
            if (sender is not null)
            {
                try { await sender; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error) { failure = $"{failure}\nSender: {error}"; }
            }
        }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (roles.Role != CastRole.Idle || video.IsAttached)
                await Task.Delay(25, cleanup.Token);
            Render();
            if (HomePage.Visibility != Visibility.Visible || ReadinessTitle.Text != L("Ready") || video.KeepsDisplayAwake)
                throw new InvalidOperationException("Ending reception did not restore the ready home and release media resources.");
            if (AppWindow.Presenter.Kind != originalPresenter.Kind ||
                (AppWindow.Presenter as OverlappedPresenter)?.State != originalWindowState ||
                AppWindow.Position != originalPosition || AppWindow.Size != originalSize)
                throw new InvalidOperationException("Ending reception did not restore the previous window presentation, position and size.");
        }
        catch (Exception error) { failure = $"{failure}\nCleanup: {error}"; }
        try
        {
            if (originalPresenter is OverlappedPresenter testPresenter && originalAlwaysOnTop is { } alwaysOnTop)
                testPresenter.IsAlwaysOnTop = alwaysOnTop;
            if (verifyOcclusion) AppWindow.Show();
        }
        catch (Exception error) { failure = $"{failure}\nWindow test visibility cleanup: {error}"; }
        await File.WriteAllTextAsync(reportFile, JsonData.Text(JsonData.Object(
            ("passed", failure is null), ("failure", failure), ("presentedFrames", presented), ("playedAudioFrames", playedAudio), ("hardwareSurfaces", hardwareSurfaces),
            ("receivingShowsVideoOnly", videoOnly), ("restoredReadyHome", ReadinessTitle.Text == L("Ready")),
            ("enteredFullscreen", enteredFullscreen), ("restoredWindow", AppWindow.Presenter.Kind == originalPresenter.Kind &&
                AppWindow.Position == originalPosition && AppWindow.Size == originalSize),
            ("exposedBeforePresentation", exposedBeforePresentation), ("presentationDiagnostics", presentationDiagnostics),
            ("verifyOcclusion", verifyOcclusion), ("occlusionSurvivalVerified", occlusionSurvivalVerified), ("presentationVerified", presented >= 5),
            ("detached", !video.IsAttached), ("syntheticOnly", true), ("physicalDeviceTested", false), ("diagnostics", JsonData.Value(diagnostics))),
            indented: true));

        void CaptureMeasurements()
        {
            presented = video.PresentedFrames;
            playedAudio = video.PlayedAudioFrames;
            hardwareSurfaces = video.HardwareSurfaceCount;
            videoOnly = HomePage.Visibility == Visibility.Collapsed && roles.Role == CastRole.Receiving;
            enteredFullscreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            exposedBeforePresentation = diagnostics.Any(line =>
                line.Contains("Receiver video surface exposed:", StringComparison.Ordinal) &&
                line.Contains("presentedFrames=0", StringComparison.Ordinal));
            presentationDiagnostics = video.PresentationDiagnostics;
        }
    }

    private sealed class SyntheticSource : IVideoSource
    {
        private readonly EncodedVideo[] frames;
        public string Codec { get; }
        public string EncoderName => "Synthetic fixture (not captured content)";
        public int Width { get; }
        public int Height { get; }
        public long FirstPtsUs => frames[0].PtsUs;

        public SyntheticSource(string path)
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024)
                throw new ArgumentException("Synthetic fixture exceeds 16 MiB.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var fixture = document.RootElement;
            if (!fixture.GetProperty("syntheticOnly").GetBoolean())
                throw new ArgumentException("Only a labeled synthetic fixture is allowed.");
            Codec = fixture.TryGetProperty("videoCodec", out var codec) ? codec.GetString()! : "h264";
            var generation = fixture.GetProperty("generations")[0];
            Width = generation.GetProperty("width").GetInt32();
            Height = generation.GetProperty("height").GetInt32();
            frames = generation.GetProperty("frames").EnumerateArray().Select(frame => new EncodedVideo(
                Convert.FromBase64String(frame.GetProperty("data").GetString()!), frame.GetProperty("ptsUs").GetInt64(),
                Convert.FromBase64String(frame.GetProperty("configuration").GetString()!))).ToArray();
            if (frames.Length is < 1 or > 512) throw new ArgumentException("Invalid synthetic GOP.");
        }

        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = frames[index % frames.Length];
                yield return frame with { PtsUs = frames[0].PtsUs + index * 1_000_000L / 30 };
                index++;
                await Task.Delay(34, cancellationToken);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SyntheticAudio(long firstPtsUs) : IAudioSource
    {
        public JsonNode Configuration => JsonData.Object(("codec", "raw"), ("sampleRate", 48000), ("channels", 2),
            ("csd", JsonData.Value(Array.Empty<string>())), ("sampleFormat", "s16le"));
        public void BeginTransmission() { }
        public async IAsyncEnumerable<AudioPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken token)
        {
            long index = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                yield return new(new byte[480 * 4], firstPtsUs + index++ * 10_000);
                await Task.Delay(10, token);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
