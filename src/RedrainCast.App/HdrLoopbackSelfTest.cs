using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using RedrainCast.Core;
using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using RedrainCast.Receive.Media;
using RedrainCast.Send.Core;
using RedrainCast.Send.Media;

namespace RedrainCast.App;

internal sealed record HdrLoopbackReport(bool Recovery, int Configurations, uint ActiveGeneration,
    long DecodedPictures, long PresentedPictures, int HardwareSurfaces, bool StopAcknowledged,
    bool CaptureStartedAfterAcceptance, bool ExactColorAndMetadata, bool ExactSourceTimestamps,
    bool RecoveryCompleted);

public sealed partial class MainWindow
{
    private static async Task<HdrLoopbackReport> RunHdrLoopbackAsync(MediaDiagnostics.HdrColorProbeReport probe,
        bool recovery, DiagnosticLog journal)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var source = new HdrLoopbackSource(probe, recovery);
        var factory = new HdrLoopbackFactory(source, recovery, journal);
        var stopAcknowledged = false;
        await using var receiver = new ReceiverServer("Synthetic HDR loopback", factory,
            notice =>
            {
                journal.Write($"HDR loopback receiver: {notice.State}: {notice.Detail}");
                if (notice.State == "Disconnected" && notice.Detail == "Sender stopped the cast.")
                    Volatile.Write(ref stopAcknowledged, true);
            },
            bindAddress: IPAddress.Loopback);
        receiver.Start(advertise: false);
        var recoveryCompleted = false;
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, receiver.Port, "Synthetic HDR loopback", false),
            new(MaxSize: probe.Width, MaxFps: 30, VideoCodec: "h265"), source, state =>
            {
                journal.Write($"HDR loopback sender: {state}");
                if (state.StartsWith("Recovery COMPLETE: track=1, generation=2,", StringComparison.Ordinal))
                    Volatile.Write(ref recoveryCompleted, true);
            }, stop.Token);
        try
        {
            while (factory.Playback is not { } playback ||
                (recovery ? !playback.HasRecoveredOutput || !Volatile.Read(ref recoveryCompleted) :
                    playback.DecodedFrames < 8 || playback.PresentedFrames == 0))
            {
                if (cast.IsCompleted)
                {
                    await cast;
                    throw new IOException("HDR loopback ended before hardware decode/presentation and recovery proof.");
                }
                await Task.Delay(20, deadline.Token);
            }
            var active = factory.Playback!;
            active.CheckHealth();
            var generation = receiver.RecoveryState?.ActiveGeneration ?? 1;
            var decoded = recovery ? active.RecoveredDecodedFrames : active.DecodedFrames;
            var presented = recovery ? active.RecoveredPresentedFrames : active.PresentedFrames;
            var surfaces = active.HardwareSurfaceCount;
            if (recovery && (generation < 2 || factory.Configurations < 2))
                throw new IOException("HDR recovery loopback did not repeat exact color metadata in a new generation.");
            stop.Cancel();
            await cast.WaitAsync(deadline.Token);
            while (receiver.IsBusy) await Task.Delay(20, deadline.Token);
            if (!source.Disposed || !active.Disposed || !Volatile.Read(ref stopAcknowledged))
                throw new IOException("HDR loopback Stop was acknowledged before native/source resources retired.");
            return new(recovery, factory.Configurations, generation, decoded, presented, surfaces,
                true, source.Started && factory.AcceptedBeforeCapture, true, true, recoveryCompleted);
        }
        finally
        {
            stop.Cancel();
            await cast.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class HdrLoopbackSource(MediaDiagnostics.HdrColorProbeReport probe, bool recovery) :
        IHdrVideoSource, IRecoverableVideoSource
    {
        private readonly ConcurrentDictionary<long, byte> timestamps = new();
        private bool configured, prepared;
        private int started, disposed;
        public HdrVideoFormat ColorFormat => configured ? probe.Color : HdrVideoFormat.Sdr;
        public string EncoderName => probe.Encoder;
        public int Width => probe.Width;
        public int Height => probe.Height;
        public bool NativeHdrEligible => true;
        public bool SupportsStreamRecovery => recovery;
        public bool Started => Volatile.Read(ref started) != 0;
        public bool Disposed => Volatile.Read(ref disposed) != 0;
        public bool ContainsTimestamp(long ptsUs) => timestamps.ContainsKey(ptsUs);
        public ValueTask ConfigureHdrAsync(bool enabled, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!enabled || prepared) throw new IOException("Synthetic HDR loopback must negotiate HDR before preparation.");
            configured = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask PrepareAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!configured || Started) throw new IOException("HDR loopback preparation order changed.");
            prepared = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask RequestKeyFrameAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            if (!prepared) throw new IOException("HDR loopback started before preparation.");
            Interlocked.Exchange(ref started, 1);
            while (!token.IsCancellationRequested)
            {
                var pts = (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);
                if (!timestamps.TryAdd(pts, 0)) throw new IOException("Synthetic HDR loopback timestamps repeated.");
                yield return probe.Frame with { PtsUs = pts };
                await Task.Delay(33, token);
            }
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HdrLoopbackFactory(HdrLoopbackSource source, bool recovery, DiagnosticLog journal) : IPlaybackFactory
    {
        private HdrLoopbackPlayback? playback;
        private int configurations;
        public HdrLoopbackPlayback? Playback => Volatile.Read(ref playback);
        public int Configurations => Volatile.Read(ref configurations);
        public bool AcceptedBeforeCapture { get; private set; }
        public string Description => "Verified synthetic Main10/P010 HDR-to-SDR hardware loopback.";
        public bool SupportsStreamRecovery => recovery;
        public bool SupportsVideoRotation => true;
        public HdrReceiverCapabilities HdrCapabilities => new(true, false, true);
        public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var previous = Playback;
            if (request.ColorFormat != source.ColorFormat || previous is null && source.Started ||
                previous is not null && (!recovery || !previous.Disposed))
                throw new IOException("HDR loopback admission changed color or allowed capture before acceptance.");
            if (previous is null) AcceptedBeforeCapture = true;
            var presenter = new GpuPresenter(request.VideoProcessingFramesPerSecond, request.ColorFormat);
            HardwareDecoder? decoder = null;
            try
            {
                presenter.SetViewport(source.Width, source.Height, 1, 1);
                decoder = new HardwareDecoder(presenter.Device, request.VideoCodec, request.ColorFormat);
                var inner = new VideoPlayback(presenter, decoder, () => ValueTask.CompletedTask,
                    detail => { journal.Write($"HDR loopback playback: {detail}"); return ValueTask.CompletedTask; },
                    null, request.VideoCodec, request.ColorFormat);
                var result = new HdrLoopbackPlayback(inner, this, source, recovery);
                Volatile.Write(ref playback, result);
                return ValueTask.FromResult<IPlayback>(result);
            }
            catch
            {
                decoder?.Dispose();
                presenter.Dispose();
                throw;
            }
        }

        public void Configured(Packet packet)
        {
            if (packet.Track != 1 || HdrVideoProtocol.Format(packet.Object()) != source.ColorFormat)
                throw new IOException("HDR loopback configuration lost exact color/static metadata.");
            Interlocked.Increment(ref configurations);
        }
    }

    private sealed class HdrLoopbackPlayback(VideoPlayback inner, HdrLoopbackFactory owner,
        HdrLoopbackSource source, bool recovery) : IStreamRecoveryPlayback, IVideoPlaybackProgress
    {
        private int received, disposed;
        public long DecodedFrames => inner.DecodedFrames;
        public long PresentedFrames => inner.Statistics(1)?.Presented ?? 0;
        public int HardwareSurfaceCount => inner.HardwareSurfaceCount;
        private RecoveryVideoProgress? recoveryProgress;
        public long RecoveredDecodedFrames => Volatile.Read(ref recoveryProgress) is { } progress
            ? inner.DecodedFrames - progress.DecodedBaseline : 0;
        public long RecoveredPresentedFrames => Volatile.Read(ref recoveryProgress) is { } progress
            ? PresentedFrames - progress.PresentedBaseline : 0;
        public bool HasRecoveredOutput => Volatile.Read(ref recoveryProgress)?.HasOutput(
            inner.DecodedFrames, PresentedFrames, inner.LastPresentedPtsUs, 16, 1) == true;
        public bool Disposed => Volatile.Read(ref disposed) != 0;
        public long? SourcePositionUs => inner.SourcePositionUs;
        public long? DecodedRecoveryKeyPtsUs => inner.DecodedRecoveryKeyPtsUs;
        public long? FirstRecoverySourceAudioPtsUs => inner.FirstRecoverySourceAudioPtsUs;
        public bool HasPresentedVideo => inner.HasPresentedVideo;
        public bool HasDecodedVideo => inner.HasDecodedVideo;
        public bool HasEverPresentedVideo => inner.HasEverPresentedVideo;
        public bool IsPresentationSuspended => inner.IsPresentationSuspended;
        public string Diagnostics => inner.Diagnostics;
        public IDisposable? EnterTrack(uint track) => inner.EnterTrack(track);
        public void Observe(Packet packet) => inner.Observe(packet);
        public void CheckHealth() => inner.CheckHealth();
        public void CheckTrackHealth(uint track) => inner.CheckTrackHealth(track);
        public void BeginTrackRecovery(uint track) => inner.BeginTrackRecovery(track);
        public void BeginSessionStop() => inner.BeginSessionStop();
        public void UpdateEncodedQueueDepth(uint track, int packets) => inner.UpdateEncodedQueueDepth(track, packets);
        public void SetRecoveryKey(long ptsUs)
        {
            inner.SetRecoveryKey(ptsUs);
            if (Volatile.Read(ref recoveryProgress) is { } progress)
                Volatile.Write(ref recoveryProgress, progress with { KeyPtsUs = ptsUs });
        }
        public void SetAudioRecoveryCutoff(long ptsUs) => inner.SetAudioRecoveryCutoff(ptsUs);
        public async ValueTask ConfigureAsync(Packet configuration, CancellationToken token)
        {
            owner.Configured(configuration);
            await inner.ConfigureAsync(configuration, token);
        }
        public async ValueTask ConfigureRecoveryAsync(Packet video, Packet? audio, bool midstream, CancellationToken token)
        {
            owner.Configured(video);
            await inner.ConfigureRecoveryAsync(video, audio, midstream, token);
        }
        public async ValueTask ConfigureRecoveryTrackAsync(Packet configuration, bool midstream, CancellationToken token)
        {
            if (configuration.Track == 1) owner.Configured(configuration);
            await inner.ConfigureRecoveryTrackAsync(configuration, midstream, token);
            if (configuration.Track == 1 && midstream)
                Volatile.Write(ref recoveryProgress, new(configuration.Generation, inner.DecodedFrames, PresentedFrames));
        }
        public async ValueTask PresentAsync(Packet packet, CancellationToken token)
        {
            if (packet.Track != 1 || !source.ContainsTimestamp(packet.PtsUs))
                throw new IOException("HDR loopback transport changed a source timestamp or media track.");
            var count = Interlocked.Increment(ref received);
            if (recovery && owner.Configurations == 1 && count == 12) await Task.Delay(1000, token);
            await inner.PresentAsync(packet, token);
        }
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            Interlocked.Exchange(ref disposed, 1);
        }
    }
}
