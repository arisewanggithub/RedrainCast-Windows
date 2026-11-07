using System.Text.Json;
using RedrainCast.Protocol;

namespace RedrainCast.Receive.Core;

internal sealed class RecoveryPlayback : IPlayback, IVideoPlaybackProgress
{
    private readonly object gate = new();
    private readonly SemaphoreSlim videoLane = new(1), audioLane = new(1);
    private readonly IPlaybackFactory factory;
    private readonly CastRequest request;
    private readonly ReceiveQueue queue;
    private readonly Func<Packet, Task> send;
    private readonly Action<string> diagnostic;
    private readonly long session;
    private readonly CancellationTokenSource lifetime;
    private readonly RecoveryDeadline deadline = new();
    private IStreamRecoveryPlayback? current;
    private Packet? videoConfiguration, audioConfiguration;
    private readonly TaskCompletionSource audioConfig = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource installed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? rebuild, proofWrite;
    private uint wireGeneration;
    private uint activeGeneration;
    private bool discardOld, nativeReady, disposed, keyAcknowledged, completeAcknowledged;
    private long keyPts = -1, lastReset = long.MinValue;
    private readonly Dictionary<uint, long> timestamps = [];
    private Exception? failure;
    private readonly Queue<Packet> preroll = new();
    private AudioFormat? audioFormat;
    private bool audioStarted;
    public ReceiverRecoveryState State
    {
        get { lock (gate) return new(session, wireGeneration, activeGeneration,
            !disposed && !lifetime.IsCancellationRequested &&
            (!nativeReady || discardOld || activeGeneration != wireGeneration || !completeAcknowledged)); }
    }
    public bool HasPresentedVideo => CurrentProgress?.HasPresentedVideo ?? false;
    public bool HasDecodedVideo => CurrentProgress?.HasDecodedVideo ?? false;
    public bool HasEverPresentedVideo => CurrentProgress?.HasEverPresentedVideo ?? false;
    public bool IsPresentationSuspended => CurrentProgress?.IsPresentationSuspended ?? false;
    private IVideoPlaybackProgress? CurrentProgress => Volatile.Read(ref current) as IVideoPlaybackProgress;
    public string Diagnostics
    {
        get { lock (gate) return $"recovery generation={wireGeneration}, active={activeGeneration}, discardOld={discardOld}, ready={nativeReady}, key={keyPts}, keyAck={keyAcknowledged}, complete={completeAcknowledged}; {current?.Diagnostics}"; }
    }

    public RecoveryPlayback(IPlaybackFactory factory, CastRequest request, IPlayback initial, ReceiveQueue queue,
        long session, Func<Packet, Task> send, Action<string> diagnostic, CancellationToken token)
    {
        this.factory = factory; this.request = request; this.queue = queue; this.session = session; this.send = send;
        this.diagnostic = diagnostic;
        current = initial as IStreamRecoveryPlayback ??
            throw new ProtocolException("Playback advertised recovery without native epoch/proof support.", "UNSUPPORTED_CONFIG");
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.Begin(Environment.TickCount64);
        if (request.AudioCodec is null) audioConfig.SetResult();
    }

    public bool Admit(Packet packet)
    {
        lock (gate)
        {
            if (disposed || lifetime.IsCancellationRequested) return false;
            if (packet.Type == PacketType.StreamConfig)
            {
                if (packet.Track == 1)
                {
                    JsonObject.Require(packet.Generation == wireGeneration + 1 && packet.Generation <= int.MaxValue,
                        "Nonsequential recovery video generation.");
                    if (videoConfiguration is not null)
                    {
                        var before = videoConfiguration.Object();
                        var next = packet.Object();
                        JsonObject.Require(JsonObject.Text(next, "codec") == request.VideoCodec &&
                            JsonObject.Number(next, "width") == JsonObject.Number(before, "width") &&
                            JsonObject.Number(next, "height") == JsonObject.Number(before, "height"),
                            "Recovery cannot change codec or raster.");
                    }
                    wireGeneration = packet.Generation;
                    nativeReady = false; discardOld = false; keyAcknowledged = false; completeAcknowledged = false;
                    audioStarted = false; keyPts = -1; preroll.Clear(); timestamps.Remove(1);
                    deadline.Begin(Environment.TickCount64);
                }
                else JsonObject.Require(audioConfiguration is null && packet.Generation == 1, "Audio format changes require a new session.");
                return true;
            }
            JsonObject.Require(packet.Track == 1 ? packet.Generation <= wireGeneration && wireGeneration > 0 :
                packet.Generation == 1 && request.AudioCodec is not null, "Future/unknown recovery media generation.");
            JsonObject.Require(packet.PtsUs >= 0, "Invalid recovery media PTS.");
            if (packet.Track == 1 && packet.Generation < wireGeneration) return false;
            if (timestamps.TryGetValue(packet.Track, out var last) && (packet.Track == 1 || request.AudioCodec != "opus"))
                JsonObject.Require(packet.PtsUs >= last, "Recovery media timestamps moved backwards.");
            timestamps[packet.Track] = packet.PtsUs;
            if (packet.Track == 2 && audioFormat is not null)
                _ = Frames(packet);
            if (discardOld) return false;
            if (!nativeReady)
            {
                JsonObject.Require(packet.Track == 2 && activeGeneration > 0,
                    "Media arrived before recovery outputs were ready.");
                return false;
            }
            return packet.Track == 1 || keyAcknowledged;
        }
    }

    public void Pressure(string reason = "bounded receiver queue pressure")
    {
        lock (gate)
        {
            if (disposed || lifetime.IsCancellationRequested || discardOld || wireGeneration == 0) return;
            discardOld = true; nativeReady = false; preroll.Clear(); audioStarted = false;
            deadline.Begin(Environment.TickCount64);
            queue.DiscardQueuedMedia();
            var generation = wireGeneration;
            diagnostic($"Recovery requested: generation={generation}, keyPtsUs={keyPts}, keyAck={keyAcknowledged}, audioStarted={audioStarted}, reason={reason}");
            proofWrite = SendProofAsync(StreamRecovery.Request, generation, null, null);
        }
    }

    public void CheckHealth()
    {
        lock (gate)
        {
            if (disposed || lifetime.IsCancellationRequested) return;
            if (failure is not null) throw new ProtocolException($"Recovery epoch failed: {failure.Message}", "DECODER_FAILED");
            deadline.Check(Environment.TickCount64);
            if (nativeReady) current?.CheckHealth();
        }
    }

    public IDisposable? EnterTrack(uint track)
    {
        lock (gate) return current?.EnterTrack(track);
    }
    public void Observe(Packet packet) { }

    public async ValueTask ConfigureAsync(Packet packet, CancellationToken token)
    {
        if (packet.Track == 2)
        {
            Task ready;
            lock (gate)
            {
                audioFormat = AudioFormat.Parse(packet.Object(), request.AudioCodec!);
                audioConfiguration = packet; audioConfig.TrySetResult(); ready = installed.Task;
            }
            await ready.WaitAsync(token);
            return;
        }
        Task job;
        lock (gate)
        {
            if (activeGeneration != 0) installed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            videoConfiguration = packet;
            var generation = packet.Generation;
            job = rebuild = TrackThread.Run(() => RebuildAsync(packet, generation, token));
        }
        await job;
    }

    private async Task RebuildAsync(Packet video, uint generation, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await audioConfig.Task.WaitAsync(linked.Token);
        await videoLane.WaitAsync(linked.Token);
        try
        {
            await audioLane.WaitAsync(linked.Token);
            try
            {
                bool midstream;
                IStreamRecoveryPlayback? old;
                lock (gate) { midstream = activeGeneration != 0; old = current; nativeReady = false; }
                if (midstream)
                {
                    if (lastReset != long.MinValue)
                        await Task.Delay((int)Math.Max(0, 1000 - (Environment.TickCount64 - lastReset)), linked.Token);
                    lastReset = Environment.TickCount64;
                    lock (gate) current = null;
                    if (old is not null) await old.DisposeAsync();
                    old = null;
                }
                else lastReset = Environment.TickCount64;
                IStreamRecoveryPlayback? next = old;
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (next is null)
                    {
                        var opened = await factory.OpenAsync(request, linked.Token);
                        next = opened as IStreamRecoveryPlayback;
                        if (next is null) { await opened.DisposeAsync(); throw new ProtocolException("Rebuilt playback lost recovery support."); }
                    }
                    await next.ConfigureRecoveryAsync(video, audioConfiguration, midstream, linked.Token);
                    lock (gate)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        JsonObject.Require(!disposed && generation == wireGeneration, "Retired recovery reconstruction.");
                        current = next; next = null;
                        activeGeneration = generation; nativeReady = true;
                        diagnostic($"Recovery native outputs configured: generation={generation}, midstream={midstream}");
                        installed.TrySetResult();
                    }
                }
                finally { if (next is not null && next != current) await next.DisposeAsync(); }
            }
            finally { audioLane.Release(); }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (!lifetime.IsCancellationRequested && generation == wireGeneration) failure ??= error;
                installed.TrySetException(error);
            }
            throw;
        }
        finally { videoLane.Release(); }
    }

    public async ValueTask PresentAsync(Packet packet, CancellationToken token)
    {
        var lane = packet.Track == 1 ? videoLane : audioLane;
        await lane.WaitAsync(token);
        try
        {
            IStreamRecoveryPlayback playback;
            uint generation;
            lock (gate)
            {
                if (disposed || !nativeReady || discardOld || activeGeneration != wireGeneration ||
                    packet.Track == 1 && packet.Generation != activeGeneration) return;
                generation = activeGeneration;
                playback = current ?? throw new IOException("Missing rebuilt playback.");
                if (packet.Track == 1 && keyPts < 0)
                {
                    JsonObject.Require((packet.Flags & 1) != 0, "A recovered generation must start with a real random-access picture.");
                    keyPts = packet.PtsUs;
                    playback.SetRecoveryKey(keyPts);
                }
            }
            if (packet.Track == 2)
            {
                List<Packet> release;
                lock (gate)
                {
                    if (!keyAcknowledged) return;
                    release = AdmitAudio(packet);
                }
                foreach (var retained in release)
                {
                    token.ThrowIfCancellationRequested();
                    lock (gate) if (disposed || generation != wireGeneration || discardOld) return;
                    if (!await PresentOwnedAsync(playback, retained, generation, token)) return;
                }
            }
            else if (!await PresentOwnedAsync(playback, packet, generation, token)) return;
            await ProveAsync(playback, generation);
        }
        finally { lane.Release(); }
    }

    private async ValueTask<bool> PresentOwnedAsync(IStreamRecoveryPlayback playback, Packet packet, uint generation, CancellationToken token)
    {
        try { await playback.PresentAsync(packet, token); return true; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            bool retired;
            lock (gate) retired = disposed || lifetime.IsCancellationRequested || generation != wireGeneration || discardOld;
            if (!retired) throw;
            diagnostic($"Retired recovery track {packet.Track} epoch {generation} completed with {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    private int Frames(Packet packet)
    {
        try { return RecoveryAudio.Frames(request.AudioCodec!, packet.Payload, audioFormat!.SamplesPerFrame); }
        catch (FormatException error) { throw new ProtocolException(error.Message); }
    }

    private List<Packet> AdmitAudio(Packet packet)
    {
        var format = audioFormat ?? throw new ProtocolException("Recovery audio was not configured.");
        var frames = Frames(packet);
        if (RecoveryAudio.End(packet.PtsUs, frames) <= keyPts - RecoveryAudio.PrerollUs(format.Codec, format.SamplesPerFrame))
            return [];
        if (format.Codec == "raw")
        {
            if (!audioStarted && packet.PtsUs - keyPts > 250_000)
            {
                Pressure("source PCM prefix no longer covers the key window");
                return [];
            }
            audioStarted = true;
            return [packet];
        }
        if (audioStarted) return [packet];
        if (preroll.Count >= 64 || packet.Payload.Length > 256 * 1024 - preroll.Sum(p => p.Payload.Length))
        {
            Pressure("bounded compressed preroll cannot cover the key");
            return [];
        }
        preroll.Enqueue(packet);
        var start = Math.Max(0, keyPts - RecoveryAudio.PrerollUs(format.Codec, format.SamplesPerFrame));
        while (preroll.TryPeek(out var first) && RecoveryAudio.End(first.PtsUs, Frames(first)) <= start) preroll.Dequeue();
        var coverage = RecoveryAudio.Coverage(preroll.Select(item => (item.PtsUs, Frames(item))), start, keyPts,
            allowsPtsReversal: format.Codec == "opus");
        if (coverage == RecoveryAudioCoverage.Lost)
        {
            Pressure("monotonic compressed audio prefix or continuity is missing");
            return [];
        }
        if (coverage == RecoveryAudioCoverage.Wait) return [];
        audioStarted = true;
        var result = preroll.ToList(); preroll.Clear();
        return result;
    }

    private async Task ProveAsync(IStreamRecoveryPlayback playback, uint generation)
    {
        Task? pending = null;
        lock (gate)
        {
            if (disposed || lifetime.IsCancellationRequested || generation != wireGeneration || discardOld) return;
            deadline.Check(Environment.TickCount64);
            if (!keyAcknowledged && playback.DecodedRecoveryKeyPtsUs is { } decoded && keyPts >= 0 && decoded >= keyPts)
            {
                keyAcknowledged = true;
                pending = proofWrite = SendProofAsync(StreamRecovery.Key, generation, keyPts, null);
            }
        }
        if (pending is not null) await pending;
        lock (gate)
        {
            if (disposed || lifetime.IsCancellationRequested || generation != wireGeneration || discardOld || !keyAcknowledged || completeAcknowledged) return;
            deadline.Check(Environment.TickCount64);
            var first = playback.FirstRecoverySourceAudioPtsUs;
            if (request.AudioCodec is not null && first is null) return;
            if (first is { } pts) JsonObject.Require(pts >= keyPts && pts - keyPts <= 250_000, "Recovery actual audio exceeds the key window.");
            completeAcknowledged = true;
            pending = proofWrite = SendProofAsync(StreamRecovery.Complete, generation, keyPts, first);
        }
        if (pending is not null) await pending;
        lock (gate) if (generation == wireGeneration && !discardOld) deadline.Complete(Environment.TickCount64);
    }

    private async Task SendProofAsync(string state, uint generation, long? key, long? audio)
    {
        var body = JsonData.Object(("state", state), ("message", "Bounded stream recovery v1"), ("generation", generation));
        if (key is { } pts) body["keyPtsUs"] = StreamRecovery.Timestamp(pts);
        if (audio is { } audioPts) body["audioPtsUs"] = StreamRecovery.Timestamp(audioPts);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            await send(Packet.Control(PacketType.Status, session, body));
            diagnostic($"Recovery proof sent: state={state}, generation={generation}, keyPtsUs={key}, audioPtsUs={audio}");
        }
        catch (Exception error)
        {
            lock (gate) if (!lifetime.IsCancellationRequested && generation == wireGeneration) failure ??= error;
            if (!lifetime.IsCancellationRequested) throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? reset, proof;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; lifetime.Cancel(); reset = rebuild; proof = proofWrite;
        }
        try
        {
            if (reset is not null)
            {
                try { await reset; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
            if (proof is not null)
            {
                try { await proof; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }
        finally
        {
            await videoLane.WaitAsync();
            try
            {
                await audioLane.WaitAsync();
                try
                {
                    var owned = current; current = null;
                    if (owned is not null) await owned.DisposeAsync();
                }
                finally { audioLane.Release(); }
            }
            finally { videoLane.Release(); lifetime.Dispose(); }
        }
    }
}
