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
    private readonly object nativeGate = new();
    private HardwareFrame? lastFrame;
    private HardwareFrame? pendingFrame;
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
            lock (nativeGate)
            {
                token.ThrowIfCancellationRequested();
                pendingFrame?.Dispose(); pendingFrame = null;
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
    private long lastVideoPts;
    public string Diagnostics => $"video received={Interlocked.Read(ref receivedVideoPackets)}, decoded={DecodedFrames}, rendered={presenter.PresentedFrames}, skipped={SkippedFrames}, lastPts={Interlocked.Read(ref lastVideoPts)}, maxSkewUs={MaximumAvSkewUs}, decodeMaxUs={Interlocked.Read(ref maximumDecodeUs)}, decodeAvgUs={Interlocked.Read(ref totalDecodeUs) / Math.Max(1, Interlocked.Read(ref decodedVideoPackets))}, gateWaitMaxUs={Interlocked.Read(ref maximumGateWaitUs)}; {decoder.Diagnostics}; {presenter.Diagnostics}; {audio?.Diagnostics}";

    public long PlayedAudioFrames => audio?.PlayedFrames ?? 0;
    public IDisposable? EnterTrack(uint track) => track == 2 && audio is not null ? WasapiOutput.AudioThread() : null;
    public VideoPlayback(GpuPresenter presenter, HardwareDecoder decoder, Func<ValueTask> detach, Func<string, ValueTask> status,
        AudioPlayback? audio, string codecName = "h264", HdrVideoFormat? color = null, PlaybackClock? clock = null)
    {
        this.presenter = presenter;
        this.decoder = decoder;
        this.detach = detach;
        this.status = status;
        this.audio = audio;
        this.clock = clock ?? new PlaybackClock(audioExpected: audio is not null);
        this.codecName = codecName;
        expectedFormat = color ?? HdrVideoFormat.Sdr;
        expectedFormat.Validate(codecName);
        renderTask = Task.Factory.StartNew(RenderLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
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
        lock (nativeGate)
        {
            Volatile.Write(ref statisticsConfiguration, null);
            Volatile.Write(ref statisticsVideo, null);
            presenter.ResetStatistics();
            Interlocked.Exchange(ref statisticsPeakSkewUs, 0);
            Volatile.Write(ref rendered, 0);
            configuredGeneration = configuration.Generation;
            videoAnchored = false;
            timingSession = configuration.Session;
            Volatile.Write(ref lastAvTiming, null);
            Volatile.Write(ref decodedCurrentGeneration, 0);
            if (pendingFrame is not null) { pendingFrame.Dispose(); pendingFrame = null; Interlocked.Increment(ref skippedFrames); }
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
        var waitingAt = Stopwatch.GetTimestamp();
        lock (nativeGate)
        {
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
        }
        Interlocked.Increment(ref decodedVideoPackets);
        var totalDecoded = Interlocked.Add(ref decodedFrames, frames.Count);
        if (frames.Count > 0) Volatile.Write(ref decodedCurrentGeneration, 1);
        var expectedKey = Interlocked.Read(ref recoveryKey);
        if (expectedKey >= 0 && frames.Any(frame => frame.PtsUs >= expectedKey &&
            (!independentRecovery || clock.CurrentUs is not { } source || source - frame.PtsUs <= StreamRecovery.MaximumKeyAgeUs)))
            Interlocked.CompareExchange(ref decodedRecoveryKey, expectedKey, -1);
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
                    Interlocked.Increment(ref skippedFrames);
                    continue;
                }
                lock (nativeGate)
                {
                    CheckVideoHealth();
                    Volatile.Write(ref statisticsVideo, frame.Statistics);
                    if (pendingFrame is not null) { pendingFrame.Dispose(); Interlocked.Increment(ref skippedFrames); }
                    pendingFrame = frame.Clone();
                    renderWake.Set();
                }
            }
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private void RenderLoop()
    {
        var token = renderCancellation.Token;
        try
        {
            while (true)
            {
                var delay = presenter.IsOccluded ? 100 :
                    Volatile.Read(ref pendingFrame) is not null ||
                    Volatile.Read(ref lastFrame) is not null && presenter.HasPendingRedraw ? 5 : 250;
                if (WaitHandle.WaitAny([renderWake, token.WaitHandle], delay) == 1) token.ThrowIfCancellationRequested();
                token.ThrowIfCancellationRequested();
                if (videoSuspended || sessionStopped) continue;
                if (!Monitor.TryEnter(nativeGate)) continue;
                var first = false;
                try
                {
                    if (pendingFrame is { } frame)
                    {
                        if (!presenter.Present(frame)) continue;
                        clock.VideoPresented(frame.PtsUs);
                        awake.SetPlaying(true);
                        pendingFrame = null;
                        lastFrame?.Dispose();
                        lastFrame = frame;
                        Volatile.Write(ref lastPresentedRotation, frame.RotationDegrees);
                        Interlocked.Exchange(ref lastVideoPts, frame.PtsUs);
                        if (audio?.Position is { ContentFrames: > 0, PtsUs: { } audioPts } position)
                        {
                            var signedSkew = frame.PtsUs - audioPts;
                            MediaTiming.Record(MediaStage.VideoPresent, timingSession, 1, configuredGeneration,
                                frame.PtsUs, audioPts, position.ContentFrames, signedSkew);
                            var skew = Math.Abs(signedSkew);
                            Interlocked.Exchange(ref statisticsPeakSkewUs, Math.Max(Interlocked.Read(ref statisticsPeakSkewUs), skew));
                            var timing = new AvTimingSample(configuredGeneration, frame.PtsUs, audioPts,
                                position.ContentFrames, signedSkew, Environment.TickCount64);
                            Volatile.Write(ref lastAvTiming, timing);
                            if (skew > maximumAvSkewUs) Volatile.Write(ref maximumAvTiming, timing);
                            maximumAvSkewUs = Math.Max(maximumAvSkewUs, skew);
                            firstVideoPresentationPts ??= frame.PtsUs;
                            if (frame.PtsUs - firstVideoPresentationPts.Value >= 1_000_000)
                                maximumSteadySkewUs = Math.Max(maximumSteadySkewUs, skew);
                            Interlocked.Increment(ref avMeasurements);
                        }
                        first = Interlocked.Exchange(ref rendered, 1) == 0;
                    }
                    else if (presenter.HasPendingRedraw && lastFrame is not null)
                        presenter.Redraw(lastFrame);
                }
                finally { Monitor.Exit(nativeGate); }
                if (first) status("Playing: first hardware-decoded D3D11 surface presented" +
                    (audio is null ? " (video only)." : " with low-latency audio recovery.")).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Interlocked.CompareExchange(ref renderFailure, error, null); throw; }
        finally { awake.SetPlaying(false); }
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
            try { await detach(); }
            finally
            {
                lock (nativeGate)
                {
                    disposed = true;
                    pendingFrame?.Dispose();
                    pendingFrame = null;
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
