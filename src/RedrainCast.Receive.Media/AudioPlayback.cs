using RedrainCast.Receive.Core;
using RedrainCast.Protocol;
using System.Diagnostics;

namespace RedrainCast.Receive.Media;

public sealed class AudioPlayback : IAsyncDisposable
{
    private readonly string codec;
    private readonly PlaybackClock clock;
    private bool recoveryCatchup;
    private long catchupPresentedStart;
    private int encodedQueueDepth;
    private volatile bool suspended;
    private bool recoveringEpoch;
    private volatile bool sessionStopped;
    private readonly object gate = new();
    private readonly object startGate = new();
    private WasapiOutput? output;
    private AudioDecoder? decoder;
    private AudioRegulator? regulator;
    private AudioPresentation? presentation;
    private CancellationTokenSource? outputCancellation;
    private AutoResetEvent? dataReady;
    private Task? outputTask;
    private bool configuring;
    private long? firstInputAt;
    private long lastPts, receivedFrames;
    private long maximumGateWaitUs, maximumDecodePushUs, lastPacketAt, maximumPacketGapMs;
    private long maximumOutputLoopGapMs, maximumOutputWriteUs;
    private long recoveryCutoff = -1, firstRecoverySourcePts = -1;
    private TaskCompletionSource firstSourceSubmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long? FirstRecoverySourcePtsUs => Interlocked.Read(ref firstRecoverySourcePts) is var pts && pts >= 0 ? pts : null;
    public void SetRecoveryCutoff(long pts)
    {
        Interlocked.Exchange(ref recoveryCutoff, pts);
        lock (gate)
        {
            if (firstRecoverySourcePts < 0)
            {
                recoveryCatchup = true;
                regulator?.Buffer.PreserveStartupSource(true);
            }
        }
    }
    public void SetEncodedQueueDepth(int packets) => Volatile.Write(ref encodedQueueDepth, packets);
    public void SuspendForRecovery()
    {
        suspended = true;
        clock.BeginAudioRecovery();
    }
    public void StopSession()
    {
        lock (startGate)
        {
            sessionStopped = true;
            suspended = true;
            clock.DetachAudio();
        }
    }

    public (long ContentFrames, long? PtsUs) Position
    {
        get
        {
            lock (gate)
                return configuring || output is null || presentation is null ? (0, null) : presentation.Current(output.PlayedFrames());
        }
    }
    public long PlayedFrames => Position.ContentFrames;
    public AudioStatistics? Statistics
    {
        get
        {
            lock (gate)
            {
                if (configuring || regulator is null || output?.CachedStatistics is not { } device) return null;
                var buffer = regulator.Buffer.Snapshot;
                return new(buffer.Frames, buffer.Target, device.Padding, device.Played, buffer.Silence, buffer.Dropped);
            }
        }
    }
    public int TargetFrames { get { lock (gate) return regulator?.Buffer.Target ?? AudioBuffer.TargetFrames; } }
    public (long Silence, long Dropped) Recovery
    {
        get { lock (gate) { var snapshot = regulator?.Buffer.Snapshot; return (snapshot?.Silence ?? 0, snapshot?.Dropped ?? 0); } }
    }
    public string Diagnostics
    {
        get { lock (gate) return $"audio lastPts={lastPts}, decodedFrames={receivedFrames}, " +
            $"packetIdleMs={(lastPacketAt > 0 ? Environment.TickCount64 - lastPacketAt : -1)}, packetGapMaxMs={maximumPacketGapMs}, " +
            $"gateWaitMaxUs={maximumGateWaitUs}, decodePushMaxUs={maximumDecodePushUs}, " +
            $"outputLoopGapMaxMs={Interlocked.Read(ref maximumOutputLoopGapMs)}, outputWriteMaxUs={Interlocked.Read(ref maximumOutputWriteUs)}; " +
            $"{regulator?.Diagnostics}; {output?.Diagnostics}"; }
    }

    public AudioPlayback(string codec, PlaybackClock? clock = null)
    {
        this.codec = codec;
        this.clock = clock ?? new PlaybackClock(audioExpected: true);
        AudioDecoder.ValidateCodec(codec);
        using (WasapiOutput.AudioThread()) { }
        output = new WasapiOutput();
    }

    public async ValueTask ConfigureAsync(Packet packet, CancellationToken token, bool midstream = false)
    {
        var format = AudioFormat.Parse(packet.Object(), codec);
        if (midstream) format = format with { PreSkip = 0 };
        lock (gate) configuring = true;
        try
        {
            if (decoder is not null) await RetireAsync(!midstream, token);
            AudioDecoder? nextDecoder = null;
            AudioRegulator? nextRegulator = null;
            WasapiOutput? nextOutput = null;
            CancellationTokenSource? nextStop = null;
            AutoResetEvent? nextSignal = null;
            try
            {
                nextDecoder = new AudioDecoder(format);
                nextRegulator = new AudioRegulator();
                nextRegulator.Buffer.PreserveStartupSource(midstream);
                nextOutput = output ?? new WasapiOutput();
                token.ThrowIfCancellationRequested();
                var stop = new CancellationTokenSource();
                var signal = new AutoResetEvent(false);
                nextStop = stop;
                nextSignal = signal;
                var position = new AudioPresentation();
                var ownedOutput = nextOutput;
                var ownedRegulator = nextRegulator;
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (sessionStopped) throw new OperationCanceledException("Audio session has stopped.", token);
                    decoder = nextDecoder;
                    regulator = nextRegulator;
                    output = nextOutput;
                    presentation = position;
                    outputCancellation = stop;
                    dataReady = signal;
                    firstInputAt = null;
                    suspended = false;
                    recoveryCutoff = -1; firstRecoverySourcePts = -1;
                    recoveryCatchup = midstream; catchupPresentedStart = 0;
                    recoveringEpoch = midstream;
                    firstSourceSubmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var submitted = firstSourceSubmitted;
                    outputTask = Task.Factory.StartNew(() => RunOutput(ownedOutput, ownedRegulator.Buffer, position, signal, submitted,
                        packet.Session, packet.Generation, stop.Token),
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                nextDecoder = null;
                nextRegulator = null;
                nextOutput = null;
                nextStop = null;
                nextSignal = null;
            }
            finally
            {
                nextDecoder?.Dispose();
                nextRegulator?.Dispose();
                if (nextOutput is not null && nextOutput != output) nextOutput.Dispose();
                nextStop?.Dispose();
                nextSignal?.Dispose();
            }
        }
        finally { lock (gate) configuring = false; }
    }

    public async ValueTask PresentAsync(Packet packet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (suspended || sessionStopped) return;
        while (true)
        {
            bool wait;
            lock (gate)
            {
                CheckWorker();
                wait = recoveryCatchup && regulator is { } current &&
                    current.Buffer.Frames >= current.Buffer.Target;
            }
            if (!wait) break;
            await Task.Delay(2, token);
        }
        MediaTiming.Record(MediaStage.DecodePushBegin, packet.Session, packet.Track, packet.Generation, packet.PtsUs);
        var waiting = Stopwatch.GetTimestamp();
        lock (gate)
        {
            var entered = Stopwatch.GetTimestamp();
            maximumGateWaitUs = Math.Max(maximumGateWaitUs, (long)Stopwatch.GetElapsedTime(waiting, entered).TotalMicroseconds);
            var now = Environment.TickCount64;
            if (lastPacketAt > 0) maximumPacketGapMs = Math.Max(maximumPacketGapMs, now - lastPacketAt);
            lastPacketAt = now;
            try
            {
                CheckWorker();
                var currentDecoder = decoder ?? throw new InvalidOperationException("Audio is not configured.");
                var currentRegulator = regulator ?? throw new InvalidOperationException("Audio regulation is not initialized.");
                var decoded = currentDecoder.Decode(packet);
                if (Interlocked.Read(ref recoveryCutoff) is var cutoff && cutoff >= 0)
                {
                    var skip = RecoveryAudio.Skip(decoded.PtsUs, cutoff, decoded.Data.Length / 4);
                    decoded = new(decoded.Data.AsSpan(skip * 4).ToArray(),
                        checked(decoded.PtsUs + skip * 1_000_000L / AudioFormat.Rate));
                }
                lastPts = packet.PtsUs;
                receivedFrames += decoded.Data.Length / 4;
                if (decoded.Data.Length == 0) return;
                firstInputAt ??= Environment.TickCount64;
                currentRegulator.Push(decoded);
                dataReady!.Set();
            }
            catch (ProtocolException) { throw; }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new ProtocolException($"Audio playback failed: {error.Message}", "AUDIO_FAILED");
            }
            finally
            {
                if (MediaTiming.Enabled)
                    MediaTiming.Record(MediaStage.DecodePushEnd, packet.Session, packet.Track, packet.Generation,
                        packet.PtsUs, a: regulator?.Buffer.Frames ?? -1, b: regulator?.Buffer.Target ?? -1);
                maximumDecodePushUs = Math.Max(maximumDecodePushUs, (long)Stopwatch.GetElapsedTime(entered).TotalMicroseconds);
            }
        }
    }

    private void RunOutput(WasapiOutput output, AudioBuffer buffer, AudioPresentation position,
        AutoResetEvent dataReady, TaskCompletionSource submitted, long session, uint generation, CancellationToken token)
    {
        try
        {
            using var scheduling = WasapiOutput.AudioThread();
            var previousLoop = Environment.TickCount64;
            long traceSourcePts = -1, traceContentFrames = 0;
            while (true)
            {
                var loopAt = Environment.TickCount64;
                Interlocked.Exchange(ref maximumOutputLoopGapMs,
                    Math.Max(Interlocked.Read(ref maximumOutputLoopGapMs), loopAt - previousLoop));
                previousLoop = loopAt;
                token.ThrowIfCancellationRequested();
                if (suspended || sessionStopped) return;
                MediaTiming.Record(MediaStage.OutputWake, session, 2, generation,
                    traceSourcePts, a: traceContentFrames, b: traceContentFrames);
                var played = position.Current(output.PlayedFrames());
                lock (gate)
                {
                    lock (startGate)
                    {
                        if (!token.IsCancellationRequested && !suspended && !sessionStopped &&
                            ReferenceEquals(this.output, output) && played.PtsUs is { } presentedPts)
                            clock.ObserveAudio(presentedPts);
                    }
                    if (recoveryCatchup && Volatile.Read(ref encodedQueueDepth) == 0 &&
                        played.ContentFrames - catchupPresentedStart >= buffer.Target &&
                        buffer.Frames < buffer.Target)
                    {
                        recoveryCatchup = false;
                        buffer.PreserveStartupSource(false);
                    }
                }
                traceSourcePts = played.PtsUs ?? -1;
                traceContentFrames = played.ContentFrames;
                MediaTiming.Record(MediaStage.OutputPosition, session, 2, generation, played.PtsUs ?? -1,
                    a: played.ContentFrames);
                if (!output.Started && buffer.Frames < buffer.Target && !buffer.Draining)
                {
                    if (WaitHandle.WaitAny([dataReady, token.WaitHandle], 250) == 1) token.ThrowIfCancellationRequested();
                    continue;
                }
                if (!output.Started && recoveryCutoff >= 0)
                {
                    var cutoff = Math.Max(Interlocked.Read(ref recoveryCutoff), clock.CurrentUs ?? 0);
                    buffer.DiscardBefore(cutoff);
                    if (buffer.Frames < buffer.Target && !buffer.Draining)
                    {
                        if (WaitHandle.WaitAny([dataReady, token.WaitHandle], 2) == 1) token.ThrowIfCancellationRequested();
                        continue;
                    }
                }
                var padding = output.BufferedFrames;
                if (buffer.Draining && buffer.Frames == 0 && padding == 0) return;
                var available = checked((int)(output.Capacity - padding));
                if (available > 0)
                {
                    if (MediaTiming.Enabled)
                    {
                        var before = buffer.Snapshot;
                        MediaTiming.Record(MediaStage.OutputPullBegin, session, 2, generation,
                            a: available, b: before.Frames, c: padding, d: before.Target);
                    }
                    var block = buffer.Pull(available);
                    if (MediaTiming.Enabled)
                    {
                        var content = 0;
                        var outputStart = output.Written;
                        foreach (var span in block.Spans)
                        {
                            content += span.Frames;
                            MediaTiming.Record(MediaStage.OutputSpan, session, 2, generation, span.FirstPtsUs,
                                span.EndPtsUs, outputStart + span.Offset, span.Frames);
                        }
                        MediaTiming.Record(MediaStage.OutputPullEnd, session, 2, generation,
                            block.Spans.Length > 0 ? block.Spans[0].FirstPtsUs : -1,
                            block.Spans.Length > 0 ? block.Spans[^1].EndPtsUs : -1,
                            block.Frames, content, block.Frames - content, outputStart);
                    }
                    if (block.Frames > 0)
                    {
                        if (!output.Started && recoveryCutoff >= 0 && block.Spans.Length > 0)
                        {
                            while (clock.CurrentUs is { } source &&
                                block.Spans[0].FirstPtsUs > source)
                            {
                                if (suspended || sessionStopped) return;
                                if (token.WaitHandle.WaitOne(2)) token.ThrowIfCancellationRequested();
                            }
                        }
                        if (suspended || sessionStopped) return;
                        var start = output.Written;
                        var writeStarted = Stopwatch.GetTimestamp();
                        MediaTiming.Record(MediaStage.OutputWriteBegin, session, 2, generation,
                            a: start, b: block.Frames);
                        var written = output.Write(block.Data, 0, block.Frames);
                        MediaTiming.Record(MediaStage.OutputWriteEnd, session, 2, generation,
                            a: start, b: written);
                        Interlocked.Exchange(ref maximumOutputWriteUs, Math.Max(Interlocked.Read(ref maximumOutputWriteUs),
                            (long)Stopwatch.GetElapsedTime(writeStarted).TotalMicroseconds));
                        if (written != block.Frames) throw new IOException("WASAPI did not accept the reserved output block.");
                        position.Submit(start, block);
                        lock (startGate)
                        {
                            if (!sessionStopped && !suspended && !token.IsCancellationRequested)
                            {
                                if (block.Spans.Length > 0)
                                {
                                    Interlocked.CompareExchange(ref firstRecoverySourcePts, block.Spans[0].FirstPtsUs, -1);
                                    submitted.TrySetResult();
                                }
                                if (!output.Started) { buffer.Start(); output.Start(); }
                            }
                        }
                    }
                }
                MediaTiming.Record(MediaStage.OutputWaitBegin, session, 2, generation);
                var wake = -1;
                try { wake = output.WaitForBuffer(token); }
                finally
                {
                    MediaTiming.Record(MediaStage.OutputWaitEnd, session, 2, generation,
                        traceSourcePts, a: wake, b: traceContentFrames);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (suspended || sessionStopped) output.StopForRecovery(); }
    }

    private void CheckWorker()
    {
        if (outputTask is { IsFaulted: true } failed)
            throw new ProtocolException($"Audio output worker failed: {failed.Exception!.GetBaseException().Message}", "AUDIO_FAILED");
    }

    public void CheckHealth()
    {
        lock (gate)
        {
            if (configuring) return;
            try
            {
                CheckWorker();
                output?.CheckHealth();
                if (!recoveringEpoch && firstInputAt is { } start && Environment.TickCount64 - start > 3000 && PlayedFrames == 0)
                    throw new ProtocolException("Real source audio did not start playing within three seconds.", "AUDIO_FAILED");
            }
            catch (ProtocolException) { throw; }
            catch (Exception error) { throw new ProtocolException($"Audio output failed: {error.Message}", "AUDIO_FAILED"); }
        }
    }

    private async Task RetireAsync(bool drain, CancellationToken token)
    {
        WasapiOutput? ownedOutput;
        AudioDecoder? ownedDecoder;
        AudioRegulator? ownedRegulator;
        CancellationTokenSource? stop;
        AutoResetEvent? signal;
        Task? worker;
        lock (gate)
        {
            ownedOutput = output; output = null;
            if (ownedOutput is not null && presentation?.Current(ownedOutput.PlayedFrames()).PtsUs is { } pts)
                clock.ObserveAudio(pts);
            clock.DetachAudio();
            ownedDecoder = decoder; decoder = null;
            ownedRegulator = regulator; regulator = null;
            stop = outputCancellation; outputCancellation = null;
            signal = dataReady; dataReady = null;
            worker = outputTask; outputTask = null;
            presentation = null;
            firstInputAt = null;
        }
        try
        {
            if (drain) { ownedOutput?.BeginDrain(); ownedRegulator?.BeginDrain(); signal?.Set(); }
            else stop?.Cancel();
            if (worker is not null) await worker.WaitAsync(TimeSpan.FromMilliseconds(500), token);
        }
        finally
        {
            stop?.Cancel();
            signal?.Set();
            try { if (worker is not null) await worker; }
            finally
            {
                try { ownedOutput?.Dispose(); }
                finally
                {
                    ownedDecoder?.Dispose();
                    ownedRegulator?.Dispose();
                    signal?.Dispose();
                    stop?.Dispose();
                }
            }
        }
    }

    public async ValueTask DisposeAsync() => await RetireAsync(false, CancellationToken.None);
}
