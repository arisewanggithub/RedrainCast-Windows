using RedrainCast.Protocol;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RedrainCast.Core;
using RedrainCast.Receive.Core;
using RedrainCast.Send.Core;
using RedrainCast.Send.Media;
using RedrainCast.Receive.Media;
using Windows.UI;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    internal async Task RunRecoverySelfTestAsync(string reportFile)
    {
        diagnosticLog = new DiagnosticLog(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportFile))!,
            Path.GetFileNameWithoutExtension(reportFile) + "-diagnostics"));
        diagnosticLogPath = diagnosticLog.FilePath;
        Diagnostic("Native recovery probe started; capture preparation is separate from negotiation.");
        string? failure = null;
        var factory = new DelayedRecoveryFactory(video);
        var states = new List<string>();
        WorkerVideoSource? source = null;
        Task? cast = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var stop = new CancellationTokenSource();
        var probe = new Window { Title = "Owned synthetic H.264 recovery source" };
        var canvas = new Grid { Background = new SolidColorBrush(Color.FromArgb(255, 20, 80, 140)) };
        var text = new TextBlock { FontSize = 40, Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)) };
        canvas.Children.Add(text); probe.Content = canvas;
        probe.AppWindow.Resize(new(640, 360)); probe.AppWindow.Move(new(20, 20));
        if (probe.AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        var ticks = 0;
        timer.Tick += (_, _) => text.Text = $"Generated frame {++ticks}";
        long presented = 0, played = 0;
        var restored = false;
        var opusPreSkipVerified = false;
        var preparedWithoutCaptureVerified = false;
        var duplicatePrepareVerified = false;
        var sourceReuseRejected = false;
        double preparationMilliseconds = 0;
        long maximumDecodedKeyProofAgeUs = 0;
        try
        {
            await Task.Run(() =>
            {
                var format = new AudioFormat("opus", 2, Convert.FromBase64String("T3B1c0hlYWQBAjgBgLsAAAAAAA=="), 312, 0, 0);
                using var initial = new AudioDecoder(format);
                using var restarted = new AudioDecoder(format with { PreSkip = 0 });
                var packet = new RedrainCast.Receive.Core.Packet(PacketType.AudioPacket, 1, 2, 1, 0, 0, [248, 255, 254]);
                if (initial.Decode(packet).Data.Length / 4 != 648 || restarted.Decode(packet).Data.Length / 4 != 960)
                    throw new IOException("Native Opus midstream reconstruction reapplied initial pre-skip or changed packet duration.");
                opusPreSkipVerified = true;
            });
            await StopReceiverAsync();
            var generation = ++receiverGeneration;
            receiver = new ReceiverServer("Native H.264 recovery test", factory,
                notice => OnReceiverNotice(notice, generation), 0, IPAddress.Loopback, AcquireReceiving, instanceId);
            receiver.Start(false);
            probe.Activate(); timer.Start();
            await Task.Delay(200, timeout.Token);
            var capture = MediaDiagnostics.OwnWindow(WinRT.Interop.WindowNative.GetWindowHandle(probe));
            var settings = new CaptureSettings(MaxSize: 640, MaxFps: 20, EnableAudio: true);
            source = new WorkerVideoSource(Environment.ProcessPath!, capture, settings, Diagnostic);
            var preparation = Stopwatch.StartNew();
            Diagnostic("Native recovery probe: preparing worker before negotiation.");
            await source.PrepareAsync(timeout.Token);
            preparationMilliseconds = preparation.Elapsed.TotalMilliseconds;
            var preparedPid = source.ProcessId;
            await source.PrepareAsync(timeout.Token);
            duplicatePrepareVerified = preparedPid == source.ProcessId && !source.StartCommandSent;
            await Task.Delay(200, timeout.Token);
            preparedWithoutCaptureVerified = !source.StartCommandSent && !source.ChildExited &&
                source.PacketCount == 0 && !source.FirstTimestamp.IsCompleted;
            if (!preparedWithoutCaptureVerified || !duplicatePrepareVerified)
                throw new IOException("Prepared worker captured before receiver acceptance or exited before START.");
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            cast = Task.Run(() => new SenderSession().RunAsync(new(IPAddress.Loopback, receiver.Port, "Test", false),
                settings, source, state =>
                {
                    lock (states) states.Add(state);
                    Diagnostic($"Recovery sender: {state}");
                }, stop.Token, new RecoveryPcmSource()));
            while (factory.Configurations < 2 || !RecoveredStreaming() || video.PresentedFrames < 10 ||
                   video.PlayedAudioFrames < 4800)
            {
                if (cast.IsCompleted) { await cast; throw new IOException("Native recovery cast ended before proofs."); }
                if (Problem.IsOpen) throw new IOException(Problem.Message);
                // Keep the animated source visible on the same desktop as the receiver.
                if (video.PresentedFrames > 0 && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                {
                    AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                    AppWindow.Resize(new(800, 600)); AppWindow.Move(new(740, 400));
                }
                await Task.Delay(25, timeout.Token);
            }
            presented = video.PresentedFrames; played = video.PlayedAudioFrames;
            lock (states)
            {
                maximumDecodedKeyProofAgeUs = states.Where(state => state.StartsWith("Recovery decoded KEY:", StringComparison.Ordinal))
                    .Select(state => long.Parse(state.Split("sourceAgeUs=")[1], System.Globalization.CultureInfo.InvariantCulture))
                    .DefaultIfEmpty().Max();
                if (maximumDecodedKeyProofAgeUs < 300_000)
                    throw new IOException("Native recovery did not exercise the required delayed decoded-KEY proof.");
                if (factory.Configurations != 2)
                    throw new IOException($"Delayed KEY triggered extra native reconstructions: {factory.Configurations} configurations, expected 2.");
            }
            stop.Cancel();
            await cast.WaitAsync(timeout.Token);
            while (roles.Role != CastRole.Idle || video.IsAttached) await Task.Delay(20, timeout.Token);
            restored = !video.IsAttached && source.ChildExited;
            if (!restored) throw new IOException("Native recovery child/output cleanup failed.");
            await using var replay = source.ReadFramesAsync(timeout.Token).GetAsyncEnumerator();
            try { await replay.MoveNextAsync(); }
            catch (InvalidOperationException) { sourceReuseRejected = true; }
            if (!sourceReuseRejected) throw new IOException("A retired capture source accepted a second enumeration.");
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            stop.Cancel();
            if (cast is not null)
            {
                try { await cast.WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (Exception error) { failure ??= $"Native recovery cleanup failed: {error}"; }
            }
            if (source is not null)
            {
                try { await source.DisposeAsync(); }
                catch (Exception error) { failure ??= $"Prepared worker cleanup failed: {error}"; }
            }
            timer.Stop(); probe.Close();
            await StopReceiverAsync(); await StartReceiverAsync();
        }
        await File.WriteAllTextAsync(reportFile, JsonData.Text(JsonData.Object(
            ("passed", failure is null && restored), ("failure", failure), ("configurations", factory.Configurations),
            ("presented", presented), ("played", played), ("states", JsonData.Value(states)), ("childExited", source?.ChildExited), ("source", source?.Statistics),
            ("syntheticOwnedWindowOnly", true), ("audio", "generated silent stereo PCM, real WASAPI output"),
            ("hardwareCodec", "h264"), ("opusPreSkipVerified", opusPreSkipVerified), ("maximumDecodedKeyProofAgeUs", maximumDecodedKeyProofAgeUs),
            ("preparedWithoutCaptureVerified", preparedWithoutCaptureVerified), ("duplicatePrepareVerified", duplicatePrepareVerified), ("sourceReuseRejected", sourceReuseRejected),
            ("preparationMilliseconds", preparationMilliseconds), ("diagnosticLogPath", diagnosticLogPath),
            ("delayedDecodedKeyProofVerified", maximumDecodedKeyProofAgeUs >= 300_000), ("physicalDevicesTested", false), ("diagnostics", JsonData.Value(diagnostics))),
            indented: true));
        bool RecoveredStreaming()
        {
            lock (states) return states.Any(state => state.StartsWith("Recovery COMPLETE: generation=2,", StringComparison.Ordinal)) &&
                states.Contains("Streaming");
        }
    }

    private sealed class DelayedRecoveryFactory(VideoHost host) : IPlaybackFactory
    {
        public int Configurations;
        public string Description => "Real hardware playback with a test-only bounded decoder delay.";
        public bool SupportsStreamRecovery => true;
        public bool SupportsVideoRotation => host.SupportsVideoRotation;
        public async ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken token)
        {
            var playback = await host.OpenAsync(request, token);
            return new DelayedPlayback((IStreamRecoveryPlayback)playback, this);
        }
    }

    private sealed class DelayedPlayback(IStreamRecoveryPlayback inner, DelayedRecoveryFactory owner) :
        IStreamRecoveryPlayback, IVideoPlaybackProgress
    {
        private int seen;
        private bool first;
        public long? DecodedRecoveryKeyPtsUs => inner.DecodedRecoveryKeyPtsUs;
        public long? FirstRecoverySourceAudioPtsUs => inner.FirstRecoverySourceAudioPtsUs;
        public bool HasPresentedVideo => ((IVideoPlaybackProgress)inner).HasPresentedVideo;
        public bool HasDecodedVideo => ((IVideoPlaybackProgress)inner).HasDecodedVideo;
        public bool HasEverPresentedVideo => ((IVideoPlaybackProgress)inner).HasEverPresentedVideo;
        public bool IsPresentationSuspended => ((IVideoPlaybackProgress)inner).IsPresentationSuspended;
        public string Diagnostics => inner.Diagnostics;
        public void CheckHealth() => inner.CheckHealth();
        public void SetRecoveryKey(long pts) => inner.SetRecoveryKey(pts);
        public async ValueTask ConfigureRecoveryAsync(RedrainCast.Receive.Core.Packet video, RedrainCast.Receive.Core.Packet? audio, bool midstream, CancellationToken token)
        {
            first = Interlocked.Increment(ref owner.Configurations) == 1;
            await inner.ConfigureRecoveryAsync(video, audio, midstream, token);
        }
        public ValueTask ConfigureAsync(RedrainCast.Receive.Core.Packet packet, CancellationToken token) => inner.ConfigureAsync(packet, token);
        public async ValueTask PresentAsync(RedrainCast.Receive.Core.Packet packet, CancellationToken token)
        {
            if (packet.Track == 1)
            {
                if (++seen == 1) await Task.Delay(350, token);
                if (first && seen == 12) await Task.Delay(1000, token);
            }
            await inner.PresentAsync(packet, token);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
