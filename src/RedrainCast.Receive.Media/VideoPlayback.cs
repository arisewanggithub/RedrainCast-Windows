using System.Text.Json;
using System.Diagnostics;
using RedrainCast.Receive.Core;
using RedrainCast.Protocol;

namespace RedrainCast.Receive.Media;

public sealed class VideoPlayback : IPlayback, IVideoPlaybackProgress, IStreamRecoveryPlayback
{
    private readonly GpuPresenter presenter;
    private HardwareDecoder decoder;
    private readonly Func<ValueTask> detach;
    private readonly Func<string, ValueTask> status;
    private readonly VideoFrameTrace trace;
    private readonly AudioPlayback? audio;
    private readonly PlaybackClock clock;
    private bool independentRecovery;
    private bool videoAnchored;
    private volatile bool videoSuspended;
    private volatile bool sessionStopped;
    private readonly string codecName;
    private readonly HdrVideoFormat expectedFormat;
    private int rendered;
    private int decodedCurrentGeneration;
    // Order for lifecycle changes: decoderGate -> renderGate -> mailbox/device lock.
    // Neither lane acquires the other's gate during ordinary decoding/presentation.
    private readonly object decoderGate = new();
    private readonly object renderGate = new();
    // Publication tracing may race consumer retirement; cache metadata before transferring ownership.
    private sealed record VideoFrame(HardwareFrame Surface, long Session, uint Generation) : IDisposable
    {
        public long PtsUs { get; } = Surface.PtsUs;
        public int RotationDegrees { get; } = Surface.RotationDegrees;
        public void Dispose() => Surface.Dispose();
    }
    private VideoFrame? lastFrame;
    private readonly VideoFrameMailbox<VideoFrame> mailbox;
    private readonly AutoResetEvent renderWake = new(false);
    private readonly CancellationTokenSource renderCancellation = new();
    private readonly Task renderTask;
    private readonly PlaybackAwake awake = new(() => new PlaybackPowerRequest());
    private Exception? renderFailure;
    private Exception? resizeFailure;
    private bool disposed;
    private long maximumAvSkewUs;
    private long timingSession;
    private int avMeasurements;
    private long maximumSteadySkewUs;
    private AvTimingSample? lastAvTiming;
    private AvTimingSample? maximumAvTiming;
    private uint configuredGeneration;
    private sealed record StatisticsConfiguration(uint Generation, int Width, int Height);
    private StatisticsConfiguration? statisticsConfiguration;
    private DecodedVideoStatistics? statisticsVideo;
    private long statisticsPeakSkewUs;
    public PlaybackStatistics? Statistics(long epoch)
    {
        var configuration = Volatile.Read(ref statisticsConfiguration);
        if (Volatile.Read(ref disposed) || renderCancellation.IsCancellationRequested || configuration is null) return null;
        var result = new PlaybackStatistics(epoch, configuration.Generation,
            Interlocked.Read(ref receivedVideoPackets), DecodedFrames, presenter.PresentedFrames, SkippedFrames,
            configuration.Width, configuration.Height, LastPresentedRotationDegrees,
            IsPresentationSuspended, audio?.Statistics, LastAvTiming, Interlocked.Read(ref statisticsPeakSkewUs),
            Volatile.Read(ref statisticsVideo), presenter.Statistics);
        return Volatile.Read(ref disposed) || renderCancellation.IsCancellationRequested ||
            !ReferenceEquals(configuration, Volatile.Read(ref statisticsConfiguration))
            ? null : result;
    }
    public AvTimingSample? LastAvTiming => Volatile.Read(ref lastAvTiming);
    public Action<VideoReadObservation>? VideoReadDiagnostics => trace.ReadsActive
        ? observation => trace.ReadObservation(Interlocked.Read(ref timingSession), observation) : null;
    public void RecordStatisticsSample(long session, PlaybackStatistics sample, long sampledAtMs, bool baselineReset) =>
        trace.StatisticsSample(session, sample, sampledAtMs, baselineReset);
    public AvTimingSample? MaximumAvTiming => Volatile.Read(ref maximumAvTiming);
    private long? firstVideoPresentationPts;
    private long receivedVideoPackets, decodedVideoPackets, decodedFrames, skippedFrames;
    private long maximumDecodeUs, maximumGateWaitUs, totalDecodeUs;
    public long DecodedFrames => Interlocked.Read(ref decodedFrames);
    public long SkippedFrames => Interlocked.Read(ref skippedFrames);
    public long LastPresentedPtsUs => Interlocked.Read(ref lastVideoPts);
    private int lastPresentedRotation;
    private long recoveryKey = -1, decodedRecoveryKey = -1;
    public long? DecodedRecoveryKeyPtsUs => Interlocked.Read(ref decodedRecoveryKey) is var pts && pts >= 0 ? pts : null;
    public long? FirstRecoverySourceAudioPtsUs => audio?.FirstRecoverySourcePtsUs;
    public long? SourcePositionUs => clock.CurrentUs;
    public void SetRecoveryKey(long keyPtsUs)
    {
        Interlocked.Exchange(ref recoveryKey, keyPtsUs);
    }
    public void SetAudioRecoveryCutoff(long cutoffPtsUs)
    {
        if (audio is null) throw new ProtocolException("Audio recovery was not requested.");
        audio.SetRecoveryCutoff(cutoffPtsUs);
    }
    public void UpdateEncodedQueueDepth(uint track, int packets)
    {
        if (track == 2) audio?.SetEncodedQueueDepth(packets);
    }
    public void BeginTrackRecovery(uint track)
    {
        if (track == 1) videoSuspended = true;
        else audio?.SuspendForRecovery();
    }
    public void BeginSessionStop()
    {
        sessionStopped = true;
        videoSuspended = true;
        audio?.StopSession();
    }
    public void CheckTrackHealth(uint track)
    {
        if (track == 1) CheckVideoHealth();
        else audio?.CheckHealth();
    }
    public async ValueTask ConfigureRecoveryTrackAsync(Packet configuration, bool midstream, CancellationToken token)
    {
        independentRecovery = true;
        if (configuration.Track == 2)
        {
            if (audio is null) throw new ProtocolException("Audio recovery was not requested.");
            await audio.ConfigureAsync(configuration, token, midstream);
            return;
        }
        if (midstream)
        {
            lock (decoderGate)
            lock (renderGate)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed || renderCancellation.IsCancellationRequested, this);
                mailbox.Reset("recovery_reset");
                lastFrame?.Dispose(); lastFrame = null;
                decoder.Dispose();
                decoder = new HardwareDecoder(presenter.Device, codecName, expectedFormat);
            }
        }
        Interlocked.Exchange(ref recoveryKey, -1);
        Interlocked.Exchange(ref decodedRecoveryKey, -1);
        await ConfigureAsync(configuration, token);
        token.ThrowIfCancellationRequested();
        if (sessionStopped) throw new OperationCanceledException("Video session has stopped.", token);
        videoSuspended = false;
    }
    public async ValueTask ConfigureRecoveryAsync(Packet video, Packet? audioConfiguration, bool midstream, CancellationToken token)
    {
        if (audioConfiguration is not null)
        {
            if (audio is null) throw new ProtocolException("Recovery audio was not requested.");
            await audio.ConfigureAsync(audioConfiguration, token, midstream);
        }
        await ConfigureAsync(video, token);
        token.ThrowIfCancellationRequested();
    }
    public int LastPresentedRotationDegrees => Volatile.Read(ref lastPresentedRotation);
    public string DecoderDiagnostics => decoder.Diagnostics;
    public int HardwareSurfaceCount => decoder.HardwareSurfaceCount;
    public int MinimumHardwareSurfaces => decoder.MinimumHardwareSurfaces;
    public (long Silence, long Dropped) AudioRecovery => audio?.Recovery ?? (0, 0);
    public int AudioTargetFrames => audio?.TargetFrames ?? 0;
    public bool KeepsDisplayAwake => awake.IsActive;
    public bool HasPresentedVideo => Volatile.Read(ref rendered) != 0;
    public bool HasEverPresentedVideo => presenter.PresentedFrames > 0;
    public bool HasDecodedVideo => Volatile.Read(ref decodedCurrentGeneration) != 0;
    public bool IsPresentationSuspended => presenter.IsOccluded;
    public long MaximumAvSkewUs => Interlocked.Read(ref maximumAvSkewUs);
    public int AvMeasurements => Volatile.Read(ref avMeasurements);
    public long MaximumSteadySkewUs => Interlocked.Read(ref maximumSteadySkewUs);
    public void Observe(Packet packet)
    {
        if (packet.Track == 1) Interlocked.Increment(ref receivedVideoPackets);
    }
    public void ObserveReceived(Packet packet)
    {
        if (packet.Track == 1) trace.Record("receive", packet.Session, packet.Generation, packet.PtsUs);
    }
    public void ObserveDequeued(Packet packet)
    {
        if (packet.Track == 1) trace.Stage("queue_take", packet.Session, packet.Generation, packet.PtsUs);
    }
    private long lastVideoPts;
    public string Diagnostics => $"video received={Interlocked.Read(ref receivedVideoPackets)}, decoded={DecodedFrames}, rendered={presenter.PresentedFrames}, skipped={SkippedFrames}, lastPts={Interlocked.Read(ref lastVideoPts)}, maxSkewUs={MaximumAvSkewUs}, decodeMaxUs={Interlocked.Read(ref maximumDecodeUs)}, decodeAvgUs={Interlocked.Read(ref totalDecodeUs) / Math.Max(1, Interlocked.Read(ref decodedVideoPackets))}, gateWaitMaxUs={Interlocked.Read(ref maximumGateWaitUs)}; {decoder.Diagnostics}; {presenter.Diagnostics}; {audio?.Diagnostics}";

    public long PlayedAudioFrames => audio?.PlayedFrames ?? 0;
    public bool AffineMediaReaders { get; } =
        MediaLaneScheduling.IsEnabled(Environment.GetEnvironmentVariable("REDRAIN_MEDIA_MMCSS_LANES"));
    private void ThreadDiagnostic(string message) => status(message).AsTask().GetAwaiter().GetResult();
    public IDisposable? EnterTrack(uint track) => track == 2 && audio is not null
        ? new MediaLaneScheduling("audio_decode", "Audio", WasapiOutput.AudioThread, ThreadDiagnostic)
        : track == 1 && AffineMediaReaders
            ? PlaybackLane("video_decode")
            : null;
    public IDisposable? EnterMediaReader(uint track) => track == 1
        ? PlaybackLane("video_read")
        : track == 2 && audio is not null
            ? new MediaLaneScheduling("audio_read", "Audio", WasapiOutput.AudioThread, ThreadDiagnostic)
            : throw new InvalidOperationException("Unknown media reader lane.");
    private IDisposable PlaybackLane(string lane) => new MediaLaneScheduling(lane, "Playback",
        () => new RedrainCast.Media.MediaThreadScheduling(RedrainCast.Media.MediaThreadTask.Playback, null),
        ThreadDiagnostic);
    public VideoPlayback(GpuPresenter presenter, HardwareDecoder decoder, Func<ValueTask> detach, Func<string, ValueTask> status,
        AudioPlayback? audio, string codecName = "h264", HdrVideoFormat? color = null, PlaybackClock? clock = null)
    {
        this.presenter = presenter;
        this.decoder = decoder;
        this.detach = detach;
        this.status = status;
        trace = new VideoFrameTrace(status);
        mailbox = new VideoFrameMailbox<VideoFrame>(() => renderWake.Set(), ReleasePending,
            frame => trace.Stage("pending", frame.Session, frame.Generation, frame.PtsUs));
        this.audio = audio;
        if (audio is not null) audio.ThreadDiagnostic = ThreadDiagnostic;
        this.clock = clock ?? new PlaybackClock(audioExpected: audio is not null);
        this.codecName = codecName;
        expectedFormat = color ?? HdrVideoFormat.Sdr;
        expectedFormat.Validate(codecName);
        renderTask = Task.Factory.StartNew(RenderLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void ReleasePending(VideoFrame frame, string reason)
    {
        try { trace.Record("skip", frame.Session, frame.Generation, frame.PtsUs, reason); }
        finally { frame.Dispose(); Interlocked.Increment(ref skippedFrames); }
    }

    public static void Validate(CastRequest request)
    {
        _ = request.VideoProcessingFramesPerSecond;
    }

    public void CheckHealth()
    {
        CheckVideoHealth();
        audio?.CheckHealth();
    }
    private void CheckVideoHealth()
    {
        if (Volatile.Read(ref resizeFailure) is { } failure)
            throw new IOException($"GPU resize/presentation failed: {failure.Message}", failure);
        if (Volatile.Read(ref renderFailure) is { } renderError)
            throw new IOException($"GPU renderer failed: {renderError.Message}", renderError);
    }

    public void Resize(int width, int height, float scaleX, float scaleY)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
        try
        {
            if (presenter.SetViewport(width, height, scaleX, scaleY))
            {
                renderWake.Set();
            }
        }
        catch (Exception error) { Interlocked.CompareExchange(ref resizeFailure, error, null); }
    }

    public void RefreshDisplay(HdrDisplayPolicy policy)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed), this);
        if (!presenter.SetDisplayPolicy(policy)) return;
        renderWake.Set();
    }

    public void FailDisplay(Exception error) => Interlocked.CompareExchange(ref resizeFailure, error, null);

    public async ValueTask ConfigureAsync(Packet configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.Track == 2)
        {
            if (audio is null) throw new ProtocolException("Audio was not requested.");
            await audio.ConfigureAsync(configuration, cancellationToken);
            await status("Audio decoder and WASAPI output initialized.");
            return;
        }
        var json = configuration.Object();
        JsonObject.Require(HdrVideoProtocol.Format(json) == expectedFormat,
            "STREAM_CONFIG videoColor differs from the negotiated video format.", "UNSUPPORTED_CONFIG");
        var buffers = JsonObject.Member(json, "csd", JsonValueKind.Array).EnumerateArray()
            .Select(item => Convert.FromBase64String(item.GetString()!)).ToArray();
        CheckVideoHealth();
        lock (decoderGate)
        lock (renderGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed || renderCancellation.IsCancellationRequested, this);
            Volatile.Write(ref statisticsConfiguration, null);
            Volatile.Write(ref statisticsVideo, null);
            presenter.ResetStatistics();
            Interlocked.Exchange(ref statisticsPeakSkewUs, 0);
            Volatile.Write(ref rendered, 0);
            mailbox.Reset("configuration_reset");
            configuredGeneration = configuration.Generation;
            videoAnchored = false;
            timingSession = configuration.Session;
            Volatile.Write(ref lastAvTiming, null);
            Volatile.Write(ref decodedCurrentGeneration, 0);
            lastFrame?.Dispose(); lastFrame = null;
            decoder.Configure(CodecConfiguration.VideoData(codecName, buffers),
                JsonObject.Number(json, "width"), JsonObject.Number(json, "height"));
            presenter.Prepare(JsonObject.Number(json, "width"), JsonObject.Number(json, "height"));
            Volatile.Write(ref statisticsConfiguration, new(configuration.Generation,
                JsonObject.Number(json, "width"), JsonObject.Number(json, "height")));
        }
        await status($"Hardware {codecName} decoder configured; waiting for a real random-access picture.");
    }

    public async ValueTask PresentAsync(Packet media, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (media.Track == 2)
        {
            if (audio is null) throw new ProtocolException("Audio was not requested.");
            await audio.PresentAsync(media, cancellationToken);
            return;
        }
        CheckVideoHealth();
        IReadOnlyList<HardwareFrame> frames;
        long revision, totalDecoded;
        var waitingAt = Stopwatch.GetTimestamp();
        trace.Stage("decode_wait", media.Session, media.Generation, media.PtsUs);
        lock (decoderGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed || renderCancellation.IsCancellationRequested, this);
            revision = mailbox.Revision;
            trace.Stage("decode_enter", media.Session, media.Generation, media.PtsUs);
            if (!videoAnchored)
            {
                clock.AnchorVideo(media.PtsUs);
                videoAnchored = true;
            }
            Interlocked.Exchange(ref maximumGateWaitUs, Math.Max(Interlocked.Read(ref maximumGateWaitUs), (long)Stopwatch.GetElapsedTime(waitingAt).TotalMicroseconds));
            var begin = Stopwatch.GetTimestamp();
            frames = decoder.Decode(media);
            var elapsed = (long)Stopwatch.GetElapsedTime(begin).TotalMicroseconds;
            Interlocked.Exchange(ref maximumDecodeUs, Math.Max(Interlocked.Read(ref maximumDecodeUs), elapsed));
            Interlocked.Add(ref totalDecodeUs, elapsed);
            trace.Stage("decode_end", media.Session, media.Generation, media.PtsUs);
            Interlocked.Increment(ref decodedVideoPackets);
            totalDecoded = Interlocked.Add(ref decodedFrames, frames.Count);
            if (frames.Count > 0) Volatile.Write(ref decodedCurrentGeneration, 1);
            var expectedKey = Interlocked.Read(ref recoveryKey);
            if (expectedKey >= 0 && frames.Any(frame => frame.PtsUs >= expectedKey &&
                (!independentRecovery || clock.CurrentUs is not { } source || source - frame.PtsUs <= StreamRecovery.MaximumKeyAgeUs)))
                Interlocked.CompareExchange(ref decodedRecoveryKey, expectedKey, -1);
        }
        foreach (var frame in frames)
            trace.Record("decode", media.Session, media.Generation, frame.PtsUs);
        try
        {
            if (frames.Count > 0 && totalDecoded == frames.Count)
                await status("Video ready: first hardware-decoded surface available for presentation.");
            foreach (var frame in frames)
            {
                if (frame.PtsUs < 0) throw new ProtocolException("Hardware decoder lost the source timestamp.");
                cancellationToken.ThrowIfCancellationRequested();
                if (independentRecovery && clock.CurrentUs is { } source &&
                    source - frame.PtsUs > StreamRecovery.MaximumKeyAgeUs)
                {
                    trace.Record("skip", media.Session, media.Generation, frame.PtsUs, "stale_recovery");
                    Interlocked.Increment(ref skippedFrames);
                    continue;
                }
                trace.Stage("pending_wait", media.Session, media.Generation, frame.PtsUs);
                lock (decoderGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(disposed || renderCancellation.IsCancellationRequested, this);
                    CheckVideoHealth();
                    if (revision == mailbox.Revision)
                        Volatile.Write(ref statisticsVideo, frame.Statistics);
                    mailbox.Publish(new(frame.Clone(), media.Session, media.Generation), revision);
                }
            }
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private void RenderLoop()
    {
        VideoRenderWorker.Run(RenderFrames, () => awake.SetPlaying(false), renderCancellation.Token,
            error => Interlocked.CompareExchange(ref renderFailure, error, null),
            message => status(message).AsTask().GetAwaiter().GetResult());
    }

    private void RenderFrames()
    {
        var token = renderCancellation.Token;
        while (true)
        {
            var hasWork = mailbox.HasPending ||
                Volatile.Read(ref lastFrame) is not null && presenter.HasPendingRedraw;
            var occluded = presenter.IsOccluded;
            var delay = occluded ? 100 : hasWork ? 5 : 250;
            var wake = WaitForRender(token, delay, hasWork && !occluded && !videoSuspended && !sessionStopped);
            if (wake == 1) token.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            trace.Stage("render_wake", Interlocked.Read(ref timingSession), Volatile.Read(ref configuredGeneration),
                -1, wake == 0 ? "signaled" : wake == 2 ? "readiness" : "timeout");
            if (videoSuspended || sessionStopped) continue;
            var first = false;
            (long Session, uint Generation, long PtsUs, long Revision)? presentedFrame = null;
            trace.ReadinessStage("render_gate_wait", Interlocked.Read(ref timingSession),
                Volatile.Read(ref configuredGeneration), -1);
            lock (renderGate)
            {
                trace.ReadinessStage("render_gate_enter", timingSession, configuredGeneration, -1);
                token.ThrowIfCancellationRequested();
                if (videoSuspended || sessionStopped) continue;
                var frame = mailbox.Take(out var revision);
                if (frame is not null)
                {
                    trace.ReadinessStage("mailbox_take", frame.Session, frame.Generation, frame.PtsUs);
                    bool presented;
                    try
                    {
                        trace.Stage("render_begin", frame.Session, frame.Generation, frame.PtsUs);
                        presented = trace.ReadinessActive
                            ? presenter.Present(frame.Surface, (stage, reason) =>
                                trace.ReadinessStage(stage, frame.Session, frame.Generation, frame.PtsUs, reason))
                            : presenter.Present(frame.Surface);
                        trace.Stage("render_end", frame.Session, frame.Generation, frame.PtsUs, presenter.LastRenderOutcome);
                    }
                    catch { frame.Dispose(); throw; }
                    if (!presented)
                    {
                        trace.ReadinessStage("mailbox_return", frame.Session, frame.Generation, frame.PtsUs, "begin");
                        mailbox.Return(frame, revision);
                        trace.ReadinessStage("mailbox_return", frame.Session, frame.Generation, frame.PtsUs, "end");
                        continue;
                    }
                    lastFrame?.Dispose();
                    lastFrame = frame;
                    trace.Record("present", frame.Session, frame.Generation, frame.PtsUs);
                    clock.VideoPresented(frame.PtsUs);
                    awake.SetPlaying(true);
                    Volatile.Write(ref lastPresentedRotation, frame.RotationDegrees);
                    Interlocked.Exchange(ref lastVideoPts, frame.PtsUs);
                    presentedFrame = (frame.Session, frame.Generation, frame.PtsUs, revision);
                    first = Interlocked.Exchange(ref rendered, 1) == 0;
                }
                else if (presenter.HasPendingRedraw && lastFrame is not null)
                {
                    trace.Stage("render_begin", timingSession, configuredGeneration, lastFrame.PtsUs, "redraw");
                    if (trace.ReadinessActive)
                        presenter.Redraw(lastFrame.Surface, (stage, reason) =>
                            trace.ReadinessStage(stage, timingSession, configuredGeneration, lastFrame.PtsUs, reason));
                    else
                        presenter.Redraw(lastFrame.Surface);
                    trace.Stage("render_end", timingSession, configuredGeneration, lastFrame.PtsUs, presenter.LastRenderOutcome);
                }
            }
            // Audio's position getter can wait for audio decoding; never nest it inside video locks.
            if (presentedFrame is { } sample &&
                audio?.Position is { ContentFrames: > 0, PtsUs: { } audioPts } position)
            {
                lock (renderGate)
                {
                    if (sample.Revision == mailbox.Revision && !token.IsCancellationRequested)
                        RecordAvTiming(sample.Session, sample.Generation, sample.PtsUs, audioPts, position.ContentFrames);
                }
            }
            if (first) status("Playing: first hardware-decoded D3D11 surface presented" +
                (audio is null ? " (video only)." : " with low-latency audio recovery.")).AsTask().GetAwaiter().GetResult();
        }
    }

    private int WaitForRender(CancellationToken token, int delay, bool readinessEligible)
    {
        trace.ReadinessStage("render_wait", Interlocked.Read(ref timingSession),
            Volatile.Read(ref configuredGeneration), -1, $"timeoutMs={delay};pending={mailbox.HasPending}");
        return presenter.WaitForRender(renderWake, token.WaitHandle, delay, readinessEligible,
            trace.ReadinessActive ? () =>
                trace.ReadinessStage("readiness_signal", Interlocked.Read(ref timingSession),
                    Volatile.Read(ref configuredGeneration), -1, mailbox.HasPending ? "pending" : "idle")
                : null);
    }

    private void RecordAvTiming(long session, uint generation, long videoPts, long audioPts, long contentFrames)
    {
        var signedSkew = videoPts - audioPts;
        MediaTiming.Record(MediaStage.VideoPresent, session, 1, generation,
            videoPts, audioPts, contentFrames, signedSkew);
        var skew = Math.Abs(signedSkew);
        Interlocked.Exchange(ref statisticsPeakSkewUs, Math.Max(Interlocked.Read(ref statisticsPeakSkewUs), skew));
        var timing = new AvTimingSample(generation, videoPts, audioPts,
            contentFrames, signedSkew, Environment.TickCount64);
        Volatile.Write(ref lastAvTiming, timing);
        if (skew > maximumAvSkewUs) Volatile.Write(ref maximumAvTiming, timing);
        maximumAvSkewUs = Math.Max(maximumAvSkewUs, skew);
        firstVideoPresentationPts ??= videoPts;
        if (videoPts - firstVideoPresentationPts.Value >= 1_000_000)
            maximumSteadySkewUs = Math.Max(maximumSteadySkewUs, skew);
        Interlocked.Increment(ref avMeasurements);
    }

    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref statisticsConfiguration, null);
        try
        {
            renderCancellation.Cancel();
            try { await renderTask; }
            finally { if (audio is not null) await audio.DisposeAsync(); }
        }
        finally
        {
            try { await trace.DisposeAsync(); }
            finally
            {
                try { await detach(); }
                finally
                {
                    lock (decoderGate)
                    lock (renderGate)
                    {
                        disposed = true;
                        mailbox.Reset("disposed", close: true);
                        lastFrame?.Dispose();
                        lastFrame = null;
                        try { decoder.Dispose(); }
                        finally
                        {
                            try { presenter.Dispose(); }
                            finally
                            {
                                try { awake.Dispose(); }
                                finally { renderWake.Dispose(); renderCancellation.Dispose(); }
                            }
                        }
                    }
                }
            }
        }
    }
}
