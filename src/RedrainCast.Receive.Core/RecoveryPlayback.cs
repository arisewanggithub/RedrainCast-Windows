using RedrainCast.Protocol;

namespace RedrainCast.Receive.Core;

internal sealed class RecoveryPlayback : IPlayback, IVideoPlaybackProgress
{
    private sealed class Track
    {
        public uint Generation, Active;
        public bool Ready, ReadyWritten, Discard, Complete, KeySent;
        public Task? Proof;
        public long Key = -1, Cutoff = -1, LastPts = -1, LastReset = long.MinValue;
        public long ReadyAt;
        public Packet? Configuration;
        public readonly RecoveryDeadline Deadline = new();
        public readonly RecoveryDeadline Startup = new();
        public CancellationTokenSource Epoch = new();
        public readonly HashSet<Task> Jobs = [];
        public readonly SemaphoreSlim Lane = new(1);
        public Task Retirement = Task.CompletedTask;
    }

    private readonly object gate = new();
    private readonly Track[] tracks = [new(), new()];
    private readonly IStreamRecoveryPlayback playback;
    private readonly CastRequest request;
    private readonly ReceiveQueue queue;
    private readonly Func<Packet, CancellationToken, Task> send;
    private readonly Action<string> diagnostic;
    private readonly long session;
    private readonly Func<long> now;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationTokenRegistration stopping;
    private readonly Queue<Packet> preroll = new();
    private readonly RecoveryDeadline setupDeadline = new();
    private AudioFormat? audioFormat;
    private bool initialReady, initialPlayback, audioStarted, disposed;
    private Exception? failure;

    public ReceiverRecoveryState State
    {
        get
        {
            lock (gate)
            {
                var videoPending = tracks[0].Deadline.Pending;
                var audioPending = request.AudioCodec is not null && tracks[1].Deadline.Pending;
                return new(session, tracks[0].Generation, tracks[0].Active,
                    !disposed && !lifetime.IsCancellationRequested &&
                    (setupDeadline.Pending || videoPending || audioPending || tracks.Any(track => track.Startup.Pending)))
                {
                    AudioGeneration = tracks[1].Generation, ActiveAudioGeneration = tracks[1].Active,
                    VideoRecovering = videoPending, AudioRecovering = audioPending
                };
            }
        }
    }

    public bool HasPresentedVideo => (playback as IVideoPlaybackProgress)?.HasPresentedVideo ?? false;
    public bool HasDecodedVideo => (playback as IVideoPlaybackProgress)?.HasDecodedVideo ?? false;
    public bool HasEverPresentedVideo => (playback as IVideoPlaybackProgress)?.HasEverPresentedVideo ?? false;
    public bool IsPresentationSuspended => (playback as IVideoPlaybackProgress)?.IsPresentationSuspended ?? false;
    public long VideoReadyAtMilliseconds { get { lock (gate) return tracks[0].ReadyAt; } }
    public string Diagnostics
    {
        get
        {
            string state;
            lock (gate) state = string.Join("; ", tracks.Select((track, index) =>
                $"recovery track={index + 1} generation={track.Generation} active={track.Active} ready={track.Ready} discard={track.Discard} complete={track.Complete}")) +
                "; ";
            return state + playback.Diagnostics;
        }
    }

    public RecoveryPlayback(IPlaybackFactory factory, CastRequest request, IPlayback initial, ReceiveQueue queue,
        long session, Func<Packet, CancellationToken, Task> send, Action<string> diagnostic, CancellationToken token,
        Func<long>? milliseconds = null)
    {
        this.request = request; this.queue = queue; this.session = session; this.send = send;
        this.diagnostic = diagnostic;
        now = milliseconds ?? (() => Environment.TickCount64);
        playback = initial as IStreamRecoveryPlayback ??
            throw new ProtocolException("Playback advertised recovery without track-local native/proof support.", "UNSUPPORTED_CONFIG");
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        stopping = lifetime.Token.Register(playback.BeginSessionStop);
    }

    public void BeginSetup()
    {
        lock (gate)
        {
            if (disposed || lifetime.IsCancellationRequested || initialPlayback) return;
            setupDeadline.Begin(now());
        }
    }

    public bool Admit(Packet packet)
    {
        lock (gate)
        {
            if (disposed || lifetime.IsCancellationRequested) return false;
            JsonObject.Require(request.Tracks.Contains(packet.Track), "Recovery track was not enabled.");
            var track = tracks[packet.Track - 1];
            setupDeadline.Check(now());
            track.Startup.Check(now());
            track.Deadline.Check(now());
            if (packet.Type == PacketType.StreamConfig)
            {
                JsonObject.Require(packet.Generation == track.Generation + 1 && packet.Generation <= int.MaxValue,
                    "Nonsequential recovery track generation.");
                JsonObject.Require(track.Generation == 0 || initialReady, "Recovery preceded initial track readiness.");
                var json = packet.Object();
                if (packet.Track == 2)
                {
                    var format = AudioFormat.Parse(json, request.AudioCodec!);
                    if (audioFormat is { } before)
                        JsonObject.Require(format with { Configuration = before.Configuration } == before &&
                            format.Configuration.AsSpan().SequenceEqual(before.Configuration),
                            "Audio format changes require a new session.", "UNSUPPORTED_CONFIG");
                    audioFormat = format;
                    try
                    {
                        if (track.Generation > 0) track.Cutoff = StreamRecovery.Timestamp(json, "recoveryPtsUs");
                        else JsonObject.Require(!json.TryGetProperty("recoveryPtsUs", out _),
                            "Initial audio configuration cannot have a recovery cutoff.");
                    }
                    catch (FormatException error) { throw new ProtocolException(error.Message); }
                    ClearPreroll(); audioStarted = false;
                }
                else
                {
                    JsonObject.Require(JsonObject.Text(json, "codec") == request.VideoCodec, "Video codec substitution is forbidden.");
                    try { HdrVideoProtocol.RequireAcknowledgement(json, request.ColorFormat); }
                    catch (FormatException error) { throw new ProtocolException(error.Message, "UNSUPPORTED_CONFIG"); }
                    var buffers = CodecConfiguration.Buffers(json);
                    CodecConfiguration.ValidateVideo(request.VideoCodec,
                        CodecConfiguration.VideoData(request.VideoCodec, buffers),
                        JsonObject.Number(json, "width"), JsonObject.Number(json, "height"), request.ColorFormat);
                }
                RetireEpoch(track, track.Generation > 0 ? packet.Track : null);
                track.Generation = packet.Generation; track.Ready = false; track.ReadyWritten = false; track.Discard = false;
                track.Complete = false; track.KeySent = false; track.Key = -1; track.LastPts = -1;
                track.Configuration = packet;
                if (packet.Generation > 1) track.Deadline.Begin(now());
                queue.DiscardQueuedMedia(packet.Track);
                return true;
            }
            JsonObject.Require(packet.Generation > 0 && packet.Generation <= track.Generation, "Future/unknown recovery media generation.");
            JsonObject.Require(packet.PtsUs >= 0, "Invalid recovery media PTS.");
            if (packet.Generation < track.Generation) return false;
            JsonObject.Require(packet.Track == 2 && request.AudioCodec == "opus" || packet.PtsUs >= track.LastPts,
                "Recovery media timestamps moved backwards.");
            track.LastPts = packet.PtsUs;
            if (packet.Track == 2) _ = Frames(packet);
            if (track.Discard) return false;
            JsonObject.Require(track.Ready && initialReady, "Media arrived before receiver readiness.");
            if (!initialPlayback) BeginInitialPlayback();
            if (packet.Track == 1 && track.Key < 0)
            {
                if ((packet.Flags & 1) == 0) return false;
                var data = CodecConfiguration.VideoData(request.VideoCodec, CodecConfiguration.Buffers(track.Configuration!.Object()));
                JsonObject.Require(CodecConfiguration.ValidateVideoFrame(request.VideoCodec, packet.Payload, data),
                    "Recovery key is not a real random-access picture.");
                track.Key = packet.PtsUs;
            }
            return true;
        }
    }

    public void Pressure(uint affectedTrack, string reason = "bounded receiver track queue pressure")
    {
        lock (gate)
        {
            var track = tracks[affectedTrack - 1];
            if (disposed || lifetime.IsCancellationRequested || !initialReady || track.Discard || track.Generation == 0) return;
            track.Discard = true; track.Ready = false;
            RetireEpoch(track, affectedTrack);
            track.Deadline.Begin(now());
            queue.DiscardQueuedMedia(affectedTrack);
            if (affectedTrack == 2) { ClearPreroll(); audioStarted = false; }
            diagnostic($"Recovery requested: track={affectedTrack}, generation={track.Generation}, reason={reason}");
            var generation = track.Generation;
            track.Jobs.RemoveWhere(job => job.IsCompleted);
            track.Jobs.Add(Task.Run(() => SendRequestAsync(affectedTrack, generation)));
        }
    }

    private void RetireEpoch(Track track, uint? affectedTrack)
    {
        var epoch = track.Epoch;
        var previous = track.Retirement;
        track.Epoch = new();
        track.Proof = null;
        track.Retirement = Task.Run(async () =>
        {
            var cancellation = epoch.CancelAsync();
            try
            {
                if (affectedTrack is { } id) playback.BeginTrackRecovery(id);
            }
            finally
            {
                try { await Task.WhenAll(previous, cancellation); }
                finally { epoch.Dispose(); }
            }
        });
    }

    private async Task SendRequestAsync(uint track, uint generation)
    {
        try { await SendProofAsync(StreamRecovery.Request, track, generation, null, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) when (IsRetiredGeneration(track, generation))
        {
            diagnostic($"Retired recovery request: track={track}, generation={generation}");
        }
        catch (Exception error) when (!lifetime.IsCancellationRequested)
        {
            lock (gate) failure ??= error;
        }
    }

    public bool IsCurrentConfiguration(Packet packet) => IsCurrent(packet.Track, packet.Generation);

    private bool IsRetiredGeneration(uint id, uint generation)
    {
        lock (gate) return tracks[id - 1].Generation != generation;
    }

    private bool IsCurrent(uint id, uint generation)
    {
        lock (gate) return !disposed && !lifetime.IsCancellationRequested &&
            tracks[id - 1].Generation == generation && !tracks[id - 1].Discard;
    }

    public Task StartControlWrite(Packet packet, Func<Task> start, CancellationToken token)
    {
        // The ordered writer holds the epoch gate through frame start, never through TCP completion.
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var id = packet.Track;
            var generation = packet.Generation;
            var requested = false;
            if (packet.Type == PacketType.Status)
            {
                var json = packet.Object();
                id = checked((uint)JsonObject.Number(json, "track"));
                generation = checked((uint)JsonObject.Number(json, "generation"));
                requested = JsonObject.Text(json, "state") == StreamRecovery.Request;
            }
            var track = tracks[id - 1];
            if (disposed || lifetime.IsCancellationRequested || generation != track.Generation ||
                (requested ? !track.Discard : track.Discard || !track.Ready))
                throw new OperationCanceledException("Control frame belongs to a retired recovery epoch.", new CancellationToken(true));
            setupDeadline.Check(now());
            track.Startup.Check(now());
            track.Deadline.Check(now());
            return start();
        }
    }

    public CancellationToken ReadinessToken(Packet packet)
    {
        lock (gate) return IsCurrentConfiguration(packet) ? tracks[packet.Track - 1].Epoch.Token :
            new CancellationToken(canceled: true);
    }

    public void MarkReady(Packet packet)
    {
        lock (gate)
        {
            if (!IsCurrentConfiguration(packet)) return;
            var track = tracks[packet.Track - 1];
            setupDeadline.Check(now());
            track.Deadline.Check(now());
            track.Ready = true;
            track.ReadyAt = now();
            initialReady |= request.Tracks.All(id => tracks[id - 1].Ready);
        }
    }

    public void ReadyWritten(Packet packet)
    {
        lock (gate)
        {
            if (!IsCurrentConfiguration(packet)) return;
            var track = tracks[packet.Track - 1];
            track.ReadyWritten = true;
            track.ReadyAt = now();
            if (!initialPlayback && request.Tracks.All(id => tracks[id - 1].ReadyWritten))
                BeginInitialPlayback();
        }
    }

    private void BeginInitialPlayback()
    {
        setupDeadline.Complete(now());
        initialPlayback = true;
        foreach (var id in request.Tracks) tracks[id - 1].Startup.Begin(now());
        tracks[0].Deadline.Begin(now());
    }

    public void CheckHealth()
    {
        lock (gate)
        {
            if (disposed || lifetime.IsCancellationRequested) return;
            if (failure is not null) throw new ProtocolException($"Track recovery failed: {failure.Message}",
                failure is TimeoutException ? "TIMEOUT" : failure is ProtocolException protocol ? protocol.Code : "PROTOCOL");
            setupDeadline.Check(now());
            foreach (var id in request.Tracks)
            {
                var track = tracks[id - 1];
                track.Startup.Check(now());
                track.Deadline.Check(now());
                if (track.Retirement.IsFaulted)
                    throw new ProtocolException($"Track {id} retirement failed: {track.Retirement.Exception!.GetBaseException().Message}", "INTERNAL");
            }
        }
        foreach (var id in request.Tracks)
        {
            bool healthy;
            lock (gate) healthy = tracks[id - 1].Ready && !tracks[id - 1].Discard;
            if (healthy) playback.CheckTrackHealth(id);
        }
    }

    public IDisposable? EnterTrack(uint track) => playback.EnterTrack(track);
    public bool AffineMediaReaders => playback.AffineMediaReaders;
    public IDisposable? EnterMediaReader(uint track) => playback.EnterMediaReader(track);
    public void ObserveReceived(Packet packet) => playback.ObserveReceived(packet);
    public Action<VideoReadObservation>? VideoReadDiagnostics => playback.VideoReadDiagnostics;
    public void ObserveDequeued(Packet packet) => playback.ObserveDequeued(packet);
    public void Observe(Packet packet) => playback.Observe(packet);

    public async ValueTask ConfigureAsync(Packet packet, CancellationToken token)
    {
        var lane = tracks[packet.Track - 1].Lane;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await lane.WaitAsync(linked.Token);
        try { await ConfigureOwnedAsync(packet, token); }
        finally { lane.Release(); }
    }

    private async ValueTask ConfigureOwnedAsync(Packet packet, CancellationToken token)
    {
        Track track;
        CancellationToken epoch;
        bool midstream;
        Task retirement;
        lock (gate)
        {
            track = tracks[packet.Track - 1];
            if (!IsCurrentConfiguration(packet)) return;
            midstream = track.Active != 0;
            epoch = track.Epoch.Token;
            retirement = track.Retirement;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, epoch);
        try
        {
            await retirement.WaitAsync(linked.Token);
            if (midstream && track.LastReset != long.MinValue)
                await Task.Delay((int)Math.Max(0, 1000 - (now() - track.LastReset)), linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            track.LastReset = now();
            await playback.ConfigureRecoveryTrackAsync(packet, midstream, linked.Token);
            lock (gate)
            {
                if (!IsCurrentConfiguration(packet)) return;
                track.Deadline.Check(now());
                track.Active = packet.Generation;
                if (packet.Track == 2 && packet.Generation > 1)
                    playback.SetAudioRecoveryCutoff(track.Cutoff);
            }
        }
        catch (OperationCanceledException) when ((epoch.IsCancellationRequested || !IsCurrentConfiguration(packet)) &&
            !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            diagnostic($"Retired native configuration: track={packet.Track}, generation={packet.Generation}");
        }
        catch (Exception error) when ((epoch.IsCancellationRequested || !IsCurrentConfiguration(packet)) &&
            !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            diagnostic($"Retired native configuration failed: track={packet.Track}, generation={packet.Generation}, {error.GetType().Name}: {error.Message}");
        }
    }

    public async ValueTask PresentAsync(Packet packet, CancellationToken token)
    {
        var lane = tracks[packet.Track - 1].Lane;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await lane.WaitAsync(linked.Token);
        try { await PresentOwnedAsync(packet, token); }
        finally { lane.Release(); }
    }

    private async ValueTask PresentOwnedAsync(Packet packet, CancellationToken token)
    {
        Track track;
        CancellationToken epoch;
        List<Packet> release;
        lock (gate)
        {
            track = tracks[packet.Track - 1];
            if (disposed || track.Discard || !track.Ready || packet.Generation != track.Active ||
                packet.Generation != track.Generation) return;
            epoch = track.Epoch.Token;
            if (packet.Track == 1)
            {
                playback.SetRecoveryKey(track.Key);
                release = [packet];
            }
            else release = AudioRelease(packet, track);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, epoch);
        try
        {
            for (var index = 0; index < release.Count; index++)
            {
                playback.UpdateEncodedQueueDepth(packet.Track,
                    queue.WaitingSnapshot(packet.Track).Entries + release.Count - index - 1);
                await playback.PresentAsync(release[index], linked.Token);
            }
            await PollProofAsync(packet.Track, packet.Generation, linked.Token);
        }
        catch (OperationCanceledException) when ((epoch.IsCancellationRequested || !IsCurrentConfiguration(packet)) &&
            !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            diagnostic($"Retired source submission: track={packet.Track}, generation={packet.Generation}");
        }
        catch (Exception error) when ((epoch.IsCancellationRequested || !IsCurrentConfiguration(packet)) &&
            !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            diagnostic($"Retired source submission failed: track={packet.Track}, generation={packet.Generation}, {error.GetType().Name}: {error.Message}");
        }
        catch (ProtocolException error) when (error.Code == "CONGESTION")
        {
            Pressure(packet.Track, error.Message);
        }
        finally
        {
            if (packet.Track == 2 && request.AudioCodec != "raw")
                foreach (var retained in release) queue.ReleaseHeld(retained);
            playback.UpdateEncodedQueueDepth(packet.Track, queue.WaitingSnapshot(packet.Track).Entries);
        }
    }

    private int Frames(Packet packet)
    {
        try { return RecoveryAudio.Frames(request.AudioCodec!, packet.Payload, audioFormat!.SamplesPerFrame); }
        catch (FormatException error) { throw new ProtocolException(error.Message); }
    }

    private List<Packet> AudioRelease(Packet packet, Track track)
    {
        if (audioStarted || track.Generation == 1)
        {
            audioStarted = true;
            if (request.AudioCodec != "raw") queue.HoldMedia(packet);
            return [packet];
        }
        track.Cutoff = Math.Max(Math.Max(0, track.Cutoff), playback.SourcePositionUs ?? 0);
        playback.SetAudioRecoveryCutoff(track.Cutoff);
        var format = audioFormat!;
        var start = Math.Max(0, track.Cutoff - RecoveryAudio.PrerollUs(format.Codec, format.SamplesPerFrame));
        if (RecoveryAudio.End(packet.PtsUs, Frames(packet)) <= start) return [];
        if (format.Codec == "raw")
        {
            audioStarted = true; return [packet];
        }
        var retainedPts = preroll.Select(item => item.PtsUs).Append(packet.PtsUs).ToArray();
        if (preroll.Count >= 64 || packet.Payload.Length > 256 * 1024 - preroll.Sum(item => item.Payload.Length) ||
            retainedPts.Max() - retainedPts.Min() > ReceiveQueue.MaximumSpanUs)
        {
            Pressure(2, "compressed admission chain exceeded its bounded preroll"); return [];
        }
        preroll.Enqueue(packet);
        queue.HoldMedia(packet);
        while (preroll.TryPeek(out var first) && RecoveryAudio.End(first.PtsUs, Frames(first)) <= start)
            queue.ReleaseHeld(preroll.Dequeue());
        var coverage = RecoveryAudio.Coverage(preroll.Select(item => (item.PtsUs, Frames(item))), start, track.Cutoff,
            allowsPtsReversal: format.Codec == "opus");
        if (coverage == RecoveryAudioCoverage.Lost)
        {
            Pressure(2, "compressed audio prefix or continuity is missing"); return [];
        }
        if (coverage == RecoveryAudioCoverage.Wait) return [];
        audioStarted = true;
        var result = preroll.ToList(); preroll.Clear(); return result;
    }

    private void ClearPreroll()
    {
        while (preroll.TryDequeue(out var packet)) queue.ReleaseHeld(packet);
    }

    public async Task PollProofAsync(CancellationToken token)
    {
        foreach (var id in request.Tracks)
        {
            uint generation;
            lock (gate) generation = tracks[id - 1].Generation;
            await PollProofAsync(id, generation, token);
        }
    }

    public void ScheduleProofPolling(CancellationToken token)
    {
        foreach (var id in request.Tracks)
        {
            uint generation;
            lock (gate) generation = tracks[id - 1].Generation;
            _ = ObserveProofAsync(id, generation, token);
        }
    }

    private async Task ObserveProofAsync(uint id, uint generation, CancellationToken token)
    {
        try { await PollProofAsync(id, generation, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested || lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (gate) failure ??= error;
            diagnostic($"Recovery proof failed: track={id}, generation={generation}, {error.GetType().Name}: {error.Message}");
        }
    }

    private async Task PollProofAsync(uint id, uint generation, CancellationToken token)
    {
        Task proof;
        CancellationToken epoch;
        CancellationTokenSource linked;
        lock (gate)
        {
            var track = tracks[id - 1];
            if (track.Proof is not null || disposed || track.Discard || !track.Ready || track.Complete ||
                generation != track.Generation) return;
            epoch = track.Epoch.Token;
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, epoch);
            proof = Task.Run(() => ProveAsync(id, generation, linked.Token));
            track.Proof = proof;
            track.Jobs.Add(proof);
        }
        try { await proof; }
        catch (OperationCanceledException) when (epoch.IsCancellationRequested && !token.IsCancellationRequested && !lifetime.IsCancellationRequested) { }
        catch (Exception error) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested &&
            !IsCurrent(id, generation))
        {
            diagnostic($"Retired recovery proof failed: track={id}, generation={generation}, {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            linked.Dispose();
            lock (gate)
            {
                if (ReferenceEquals(tracks[id - 1].Proof, proof)) tracks[id - 1].Proof = null;
                tracks[id - 1].Jobs.Remove(proof);
            }
        }
    }

    private async Task ProveAsync(uint id, uint generation, CancellationToken token)
    {
        var track = tracks[id - 1];
        long pts;
        lock (gate)
        {
            if (disposed || track.Discard || generation != track.Generation || track.Complete) return;
            track.Startup.Check(now());
            track.Deadline.Check(now());
            if (id == 1)
            {
                if (track.Key < 0 || playback.DecodedRecoveryKeyPtsUs is not { } decoded || decoded < track.Key) return;
                pts = track.Key;
            }
            else
            {
                if (playback.FirstRecoverySourceAudioPtsUs is not { } first) return;
                JsonObject.Require(first >= track.Cutoff, "Audio proof preceded its source cutoff.");
                if (generation == 1)
                {
                    track.Startup.Complete(now());
                    track.Complete = true;
                    return;
                }
                pts = first;
            }
        }
        if (id == 1 && !track.KeySent)
        {
            await SendProofAsync(StreamRecovery.Key, id, generation, pts, token);
            lock (gate)
            {
                if (disposed || track.Discard || generation != track.Generation) return;
                track.Startup.Check(now());
                track.Deadline.Check(now());
                track.KeySent = true;
            }
        }
        await SendProofAsync(StreamRecovery.Complete, id, generation, pts, token);
        lock (gate)
        {
            if (disposed || track.Discard || generation != track.Generation) return;
            track.Deadline.Complete(now());
            track.Startup.Complete(now());
            track.Complete = true;
        }
    }

    private Task SendProofAsync(string state, uint track, uint generation, long? pts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var body = JsonData.Object(("state", state), ("message", "Independent track recovery v2"),
            ("track", track), ("generation", generation));
        if (pts is { } timestamp) body[track == 1 ? "keyPtsUs" : "audioPtsUs"] = StreamRecovery.Timestamp(timestamp);
        return send(Packet.Control(PacketType.Status, session, body), token);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ClearPreroll();
            foreach (var track in tracks) RetireEpoch(track, null);
            pending = tracks.SelectMany(track => track.Jobs.Append(track.Retirement)).ToArray();
        }
        var cancellation = lifetime.CancelAsync();
        try
        {
            try { await Task.WhenAll(pending.Append(cancellation)); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        finally
        {
            await tracks[0].Lane.WaitAsync();
            await tracks[1].Lane.WaitAsync();
            try
            {
                stopping.Dispose();
                await playback.DisposeAsync();
            }
            finally
            {
                foreach (var track in tracks)
                {
                    track.Epoch.Dispose();
                    track.Lane.Release();
                }
                lifetime.Dispose();
            }
        }
    }
}
