using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using RedrainCast.Protocol;

namespace RedrainCast.Send.Core;

internal sealed class RecoverySender
{
    private readonly object gate = new();
    private readonly Queue<Message> video = new(), audio = new();
    private readonly SemaphoreSlim videoWake = new(0), audioWake = new(0), controlGate = new(1);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CaptureSettings settings;
    private ReceiverOptions effectiveOptions = ReceiverOptions.None;
    private readonly IRecoverableVideoSource source;
    private readonly IAudioSource? audioSource;
    private readonly Action<string> status;
    private readonly long session;
    private readonly NetworkStream control;
    private readonly CancellationTokenSource transport = new(), media = new(), laneWrites = new();
    private byte[]? configuration;
    private enum RecoveryPhase { WaitReady, WaitSource, WaitProof, Active }
    private sealed class TrackRecovery
    {
        public uint Generation = 1, Announced, ReadyGeneration;
        public RecoveryPhase Phase = RecoveryPhase.WaitReady;
        public readonly RecoveryDeadline Deadline = new();
        public long Cutoff, SentKey = -1, Completed = -1;
        public bool KeyProven;
        public Message? Writing;
    }
    private readonly TrackRecovery videoRecovery = new(), audioRecovery = new();
    private readonly List<(long Start, long End)> writtenAudio = [];
    private long? pendingAudioCompletion;
    private byte[]? audioConfiguration;
    private bool initialReady, initialAudioCutoffPending;
    private long phaseAt;
    private bool stopping;
    private long lastInput = Environment.TickCount64, lastSync = long.MinValue;
    private bool remoteStop;
    private bool transmissionStarted;
    private string stopReason = "user";
    private readonly TerminalFailure terminal = new();
    private bool controlBroken;
    private NetworkStream? videoStream, audioStream;
    private readonly LaneTiming videoTiming = new(), audioTiming = new();

    private sealed class AssociationRejectionException(string code, string message, long session, string channel)
        : IOException($"{code}: {message}")
    {
        public string Code { get; } = code;
        public long Session { get; } = session;
        public string Channel { get; } = channel;
    }

    private sealed class LaneTiming
    {
        public long Received, Written, LastReceiptUs, LastPtsUs, MaximumReceiptGapUs, MaximumGateWaitUs;
        public long WriteStartedUs, MaximumWriteUs;
        public string Describe(Queue<Message> queue, long now) =>
            $"queued={queue.Count}, bytes={queue.Sum(packet => (long)packet.Payload.Length)}, " +
            $"headPtsUs={(queue.TryPeek(out var head) ? head.PtsUs : -1)}, " +
            $"tailPtsUs={(queue.Count > 0 ? queue.Last().PtsUs : -1)}, received={Received}, written={Written}, " +
            $"lastPtsUs={LastPtsUs}, receiptIdleUs={(LastReceiptUs > 0 ? now - LastReceiptUs : -1)}, " +
            $"receiptGapMaxUs={MaximumReceiptGapUs}, gateWaitMaxUs={MaximumGateWaitUs}, " +
            $"writeBusyUs={(WriteStartedUs > 0 ? now - WriteStartedUs : 0)}, writeMaxUs={MaximumWriteUs}";
        public void ReceivedAt(long pts, long receipt, long entered)
        {
            if (LastReceiptUs > 0) MaximumReceiptGapUs = Math.Max(MaximumReceiptGapUs, receipt - LastReceiptUs);
            MaximumGateWaitUs = Math.Max(MaximumGateWaitUs, entered - receipt);
            LastReceiptUs = receipt; LastPtsUs = pts; Received++;
        }
    }

    private string TransportTiming()
    {
        var now = QpcUs();
        return $"session={session}, videoGeneration={videoRecovery.Generation}, audioGeneration={audioRecovery.Generation}, " +
            $"videoPhase={videoRecovery.Phase}, audioPhase={audioRecovery.Phase}; " +
            $"video [{videoTiming.Describe(video, now)}]; audio [{audioTiming.Describe(audio, now)}]";
    }

    private RecoverySender(CaptureSettings settings, IRecoverableVideoSource source, IAudioSource? audioSource,
        Action<string> status, long session, NetworkStream control)
    {
        this.settings = settings; this.source = source; this.audioSource = audioSource;
        this.status = status; this.session = session; this.control = control;
    }

    public static async Task RunAsync(ReceiverEndpoint endpoint, CaptureSettings settings, IRecoverableVideoSource source,
        IAudioSource? audio, Action<string> status, long session, NetworkStream control, CancellationToken stop,
        ReceiverOptions? effectiveOptions = null)
    {
        var sender = new RecoverySender(settings, source, audio, status, session, control);
        sender.effectiveOptions = effectiveOptions ?? ReceiverOptions.None;
        await sender.RunAsync(endpoint, stop);
    }

    private async Task RunAsync(ReceiverEndpoint endpoint, CancellationToken stop)
    {
        using var videoClient = new TcpClient { NoDelay = true };
        using var audioClient = new TcpClient { NoDelay = true };
        var workers = new List<Task>();
        Task? videoWriter = null, audioWriter = null, reader = null, videoWatch = null, audioWatch = null;
        var accepted = false;
        AssociationRejectionException? retiringAssociation = null;
        using var registration = stop.Register(() => { StopMedia(); finished.TrySetResult(); });
        try
        {
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(stop, transport.Token, media.Token);
            setup.CancelAfter(TimeSpan.FromSeconds(10));
            await WriteAsync(control, Message.Control(MessageType.SessionRequest, session,
                settings.Request(source.EncoderName, effectiveOptions, source.ColorFormat)), setup.Token);
            var acceptance = await Wire.ReadAsync(control, setup.Token);
            if (acceptance.Type == MessageType.Reject) throw new IOException($"Receiver rejected recovery session: {acceptance.Json()}");
            if (acceptance.Type != MessageType.SessionAccept || acceptance.Session != session ||
                !acceptance.Json().GetProperty("tracks").EnumerateArray().Select(v => v.GetInt32()).Order()
                    .SequenceEqual(audioSource is null ? [1] : [1, 2]))
                throw new ProtocolException("Invalid recovery session acceptance.");
            try { HdrVideoProtocol.RequireAcknowledgement(acceptance.Json(), source.ColorFormat); }
            catch (FormatException error) { throw new ProtocolException(error.Message); }
            var token = StrictJson.String(acceptance.Json(), "mediaToken", 64);
            if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new ProtocolException("Invalid media token.");
            // Once accepted, only this reader owns terminal replies, including during media association.
            accepted = true;
            reader = Monitor(ReadControlsAsync(), "PROTOCOL");
            workers.Add(reader);
            workers.Add(Monitor(HeartbeatAsync(), "TIMEOUT"));
            videoStream = await AttachAsync(videoClient, "video");
            audioStream = await AttachAsync(audioClient, "audio");
            lock (gate)
            {
                phaseAt = Environment.TickCount64;
                videoRecovery.Deadline.Begin(phaseAt);
                if (audioSource is not null) audioRecovery.Deadline.Begin(phaseAt);
            }
            status("Initializing capture");
            videoWriter = Monitor(WriteLaneAsync(video, videoWake, videoStream));
            audioWriter = Monitor(WriteLaneAsync(audio, audioWake, audioStream), "AUDIO_FAILED");
            workers.Add(videoWriter);
            workers.Add(audioWriter);
            workers.Add(Monitor(CaptureVideoAsync()));
            if (audioSource is not null) workers.Add(Monitor(CaptureAudioAsync(), "AUDIO_FAILED"));
            videoWatch = Monitor(WatchLaneAsync(videoStream), "PROTOCOL");
            audioWatch = Monitor(WatchLaneAsync(audioStream), "PROTOCOL");
            workers.Add(videoWatch);
            workers.Add(audioWatch);
            await finished.Task;

            async Task<NetworkStream> AttachAsync(TcpClient socket, string channel)
            {
                await socket.ConnectAsync(endpoint.Address, endpoint.Port, setup.Token);
                await Wire.WriteAsync(socket.GetStream(), Message.Control(MessageType.Hello, session,
                    JsonData.Object(("role", "media"), ("channel", channel), ("token", token))), setup.Token);
                var reply = await Wire.ReadAsync(socket.GetStream(), setup.Token);
                if (reply.Type == MessageType.Reject)
                {
                    if (reply.Session != session) throw new ProtocolException("Recovery association rejection session changed.");
                    var json = reply.Json();
                    var code = StrictJson.String(json, "code", 80);
                    if (code is not ("VERSION" or "BUSY" or "UNSUPPORTED_CONFIG" or "PROTOCOL" or "SESSION_ENDED" or
                        "CAPTURE_FAILED" or "AUDIO_FAILED" or "DECODER_FAILED" or "TIMEOUT" or "CONGESTION" or "INTERNAL"))
                        throw new ProtocolException("Unknown receiver rejection code.");
                    var detail = StrictJson.String(json, "message", 2048);
                    if (detail.Length == 0) throw new ProtocolException("Invalid receiver rejection message.");
                    throw new AssociationRejectionException(code, detail, reply.Session, channel);
                }
                if (reply.Type != MessageType.Hello || reply.Session != session ||
                    StrictJson.String(reply.Json(), "channel", 16) != channel ||
                    reply.Json().EnumerateObject().Any(property => property.Name != "channel"))
                    throw new ProtocolException($"Recovery {channel} socket failed to attach.");
                return socket.GetStream();
            }
        }
        catch (AssociationRejectionException error) when (accepted && error.Code == "SESSION_ENDED" &&
            error.Session == session && error.Channel is "video" or "audio")
        {
            // A trusted association response is not STOP proof. Keep it out of first-error arbitration
            // until the sole control reader confirms retirement, so independent failures retain precedence.
            retiringAssociation = error;
        }
        catch (OperationCanceledException) when (media.IsCancellationRequested || stop.IsCancellationRequested) { }
        catch (Exception error) { terminal.Record(error, error is ProtocolException ? "PROTOCOL" : "CAPTURE_FAILED"); }
        finally
        {
            StopMedia();
            status("Stopping");
            try
            {
                await Task.WhenAll(new[] { videoWatch, audioWatch }.OfType<Task>());
                // Retire started complete frames before terminal control, never reuse a partial frame.
                await Task.WhenAll(new[] { videoWriter, audioWriter }.OfType<Task>());
                if (terminal.IsProvisional && reader is not null)
                {
                    try { await reader.WaitAsync(terminal.DrainRemaining); }
                    catch (TimeoutException) { }
                }
                using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(retiringAssociation is null ? 10 : 2));
                if (retiringAssociation is not null && terminal.Error is null && !remoteStop)
                {
                    await SendControlAsync(Message.Control(MessageType.Stop, session, JsonData.Object(("reason", "user"))), stopping.Token);
                    try { await reader!.WaitAsync(stopping.Token); }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
                    if (!remoteStop) terminal.Record(retiringAssociation, "PROTOCOL");
                }
                if (accepted && terminal.Error is { } failure && failure is not PeerFatalException)
                    await SendControlAsync(Message.Control(MessageType.Error, session,
                        JsonData.Object(("code", failure is TimeoutException ? "TIMEOUT" : terminal.Code),
                            ("message", failure.Message[..Math.Min(2048, failure.Message.Length)]), ("fatal", true))), stopping.Token);
                else if (accepted && terminal.Error is null && remoteStop)
                {
                    await SendControlAsync(Message.Control(MessageType.SessionEnd, session, JsonData.Object(("reason", stopReason))), stopping.Token);
                    retiringAssociation = null;
                }
                else if (accepted && terminal.Error is null)
                {
                    await SendControlAsync(Message.Control(MessageType.Stop, session, JsonData.Object(("reason", "user"))), stopping.Token);
                    // A crossed receiver Stop ends the reader without completing our SessionEnd promise.
                    await Task.WhenAny(ended.Task, reader!).WaitAsync(TimeSpan.FromSeconds(2));
                    if (terminal.Error is null && remoteStop)
                        await SendControlAsync(Message.Control(MessageType.SessionEnd, session, JsonData.Object(("reason", stopReason))), stopping.Token);
                }
            }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or SocketException)
            {
                terminal.Record(new IOException($"Recovery Stop acknowledgement failed: {error.Message}", error));
            }
            finally
            {
                // A local Stop must be acknowledged before a partial packet is abandoned.
                laneWrites.Cancel();
                await Task.WhenAll(new[] { videoWriter, audioWriter }.OfType<Task>());
                transport.Cancel();
                videoClient.Close(); audioClient.Close();
                control.Close();
                foreach (var worker in workers)
                {
                    try { await worker; }
                    catch (OperationCanceledException) when (transport.IsCancellationRequested || media.IsCancellationRequested) { }
                }
                registration.Dispose();
                transport.Dispose(); media.Dispose(); laneWrites.Dispose();
                videoWake.Dispose(); audioWake.Dispose(); controlGate.Dispose();
            }
        }
        if (retiringAssociation is not null) terminal.Record(retiringAssociation, "PROTOCOL");
        if (terminal.Error is { } terminalError)
            throw new IOException($"Negotiated stream recovery failed: {terminalError.Message}", terminalError);
    }

    private async Task Monitor(Task work, string code = "CAPTURE_FAILED")
    {
        try { await work; }
        catch (OperationCanceledException) when (media.IsCancellationRequested || transport.IsCancellationRequested) { }
        catch (Exception error)
        {
            terminal.Record(error, error is ProtocolException ? "PROTOCOL" : code);
            StopMedia();
            finished.TrySetResult();
        }
    }

    private void StopMedia()
    {
        lock (gate) stopping = true;
        media.Cancel();
    }

    private async Task WatchLaneAsync(NetworkStream stream)
    {
        var data = new byte[1];
        var count = await stream.ReadAsync(data, media.Token);
        if (count == 0) throw new MediaEofException("Receiver closed a recovery media socket.");
        throw new IOException("Unexpected reverse media.");
    }

    private async Task WriteLaneAsync(Queue<Message> queue, SemaphoreSlim wake, NetworkStream stream)
    {
        var timing = ReferenceEquals(queue, audio) ? audioTiming : videoTiming;
        var recovery = ReferenceEquals(queue, audio) ? audioRecovery : videoRecovery;
        while (true)
        {
            media.Token.ThrowIfCancellationRequested();
            Message? next;
            lock (gate)
            {
                if (stopping) return;
                media.Token.ThrowIfCancellationRequested();
                next = null;
                if (queue.TryPeek(out var head) &&
                    (head.Type == MessageType.StreamConfig
                        ? recovery.Announced == recovery.ReadyGeneration
                        : initialReady && recovery.ReadyGeneration == head.Generation &&
                            recovery.Phase is RecoveryPhase.WaitProof or RecoveryPhase.Active))
                {
                    next = queue.Dequeue();
                    if (next.Type == MessageType.StreamConfig) recovery.Announced = next.Generation;
                    recovery.Writing = next;
                    timing.WriteStartedUs = QpcUs();
                }
            }
            if (next is null) { await wake.WaitAsync(media.Token); continue; }
            wake.Wait(0);
            var started = QpcUs();
            try
            {
                await WriteAsync(stream, next, laneWrites.Token);
                lock (gate)
                {
                    // Stop and successful post-write recovery callbacks share the lifecycle lock.
                    if (!stopping && !media.IsCancellationRequested) Written(next);
                    recovery.Writing = null;
                    timing.Written++;
                }
            }
            finally
            {
                lock (gate)
                {
                    recovery.Writing = null;
                    timing.MaximumWriteUs = Math.Max(timing.MaximumWriteUs, QpcUs() - started);
                    timing.WriteStartedUs = 0;
                }
            }
        }
    }

    private static async Task WriteAsync(NetworkStream stream, Message packet, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(StreamRecovery.WriteTimeoutSeconds));
        try
        {
            MediaTiming.Record(MediaStage.TcpWriteBegin, packet.Session, packet.Track, packet.Generation,
                packet.PtsUs, a: packet.Payload.Length);
            await Wire.WriteAsync(stream, packet, limit.Token);
            MediaTiming.Record(MediaStage.TcpWriteEnd, packet.Session, packet.Track, packet.Generation,
                packet.PtsUs, a: packet.Payload.Length);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("TCP writer exceeded its 10-second deadline.", error);
        }
    }

    private async Task SendControlAsync(Message packet, CancellationToken token)
    {
        await controlGate.WaitAsync(token);
        try
        {
            if (controlBroken) throw new IOException("Recovery control writer is unusable.");
            if (packet.Type == MessageType.Ping && media.IsCancellationRequested) return;
            // Caller cancellation can abandon a queued frame, not one already started.
            try { await WriteAsync(control, packet, transport.Token); }
            catch { controlBroken = true; throw; }
        }
        finally { controlGate.Release(); }
    }

    private void Enqueue(Queue<Message> queue, SemaphoreSlim wake, Message message)
    {
        if (message.Type == MessageType.StreamConfig)
        {
            var configurations = video.Concat(audio).Where(packet => packet.Type == MessageType.StreamConfig).ToArray();
            if (configurations.Length >= 64 || configurations.Sum(packet => (long)packet.Payload.Length) +
                message.Payload.Length > 256 * 1024 || queue.Count >= 1024)
                throw new IOException("CONGESTION: recovery configuration queue exceeded its control budget.");
        }
        queue.Enqueue(message);
        wake.Release();
    }

    private bool Full(Queue<Message> queue, Message incoming, int maximumBytes) =>
        queue.Count >= 1024 ||
        queue.Where(p => p.Type is MessageType.VideoFrame or MessageType.AudioPacket).Sum(p => (long)p.Payload.Length) +
            ((ReferenceEquals(queue, video) ? videoRecovery : audioRecovery).Writing?.Payload.Length ?? 0) +
            incoming.Payload.Length > maximumBytes ||
        queue.Any(p => p.Type == incoming.Type && Math.Abs(incoming.PtsUs - p.PtsUs) > 500_000);

    private void CheckDeadlines()
    {
        var now = Environment.TickCount64;
        videoRecovery.Deadline.Check(now);
        if (audioSource is not null) audioRecovery.Deadline.Check(now);
        if (!initialReady && phaseAt > 0 && now - phaseAt >= 10_000)
            throw new TimeoutException("Initial track readiness exceeded ten seconds.");
    }

    private void BeginAttempt(uint track, string reason)
    {
        CheckDeadlines();
        var recovery = Track(track);
        if (track == 1 ? configuration is null : audioConfiguration is null)
            throw new ProtocolException("Recovery requested before track configuration.");
        if (recovery.Generation == int.MaxValue) throw new ProtocolException("Track recovery generation exhausted.");
        status($"Recovery pressure timing: reason={reason}; {TransportTiming()}");
        recovery.Generation++;
        recovery.Deadline.Begin(Environment.TickCount64);
        recovery.Phase = RecoveryPhase.WaitReady;
        recovery.Completed = recovery.SentKey = -1;
        recovery.KeyProven = false;
        var queue = track == 1 ? video : audio;
        var wake = track == 1 ? videoWake : audioWake;
        var configurations = queue.Where(p => p.Type == MessageType.StreamConfig).ToArray();
        queue.Clear();
        foreach (var packet in configurations) queue.Enqueue(packet);
        if (track == 2)
        {
            audioRecovery.Cutoff = QpcUs();
            initialAudioCutoffPending = false;
            writtenAudio.Clear();
            pendingAudioCompletion = null;
        }
        Enqueue(queue, wake, Configuration(track));
        status($"Recovering track {track} generation {recovery.Generation}: {reason}");
    }

    private Message Configuration(uint track = 1)
    {
        if (track == 2)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(audioConfiguration!)!.AsObject();
            if (audioRecovery.Generation > 1)
                json["recoveryPtsUs"] = StreamRecovery.Timestamp(audioRecovery.Cutoff);
            return new(MessageType.StreamConfig, session, 2, audioRecovery.Generation, -1, 0, JsonData.Bytes(json));
        }
        var value = JsonData.Object(("codec", settings.VideoCodec), ("width", source.Width), ("height", source.Height),
            ("csd", JsonData.Value(new[] { Convert.ToBase64String(configuration!) })));
        HdrVideoProtocol.AddFormat(value, source.ColorFormat);
        return new(MessageType.StreamConfig, session, 1, videoRecovery.Generation, -1, 0, JsonData.Bytes(value));
    }

    private async Task CaptureVideoAsync()
    {
        long previous = -1;
        await foreach (var frame in source.ReadFramesAsync(media.Token))
        {
            var receipt = QpcUs();
            if (frame.PtsUs < 0 || frame.PtsUs < previous) throw new ProtocolException("Recovery capture timestamps are invalid or moved backwards.");
            previous = frame.PtsUs;
            var csd = frame.Configuration.Length > 0
                ? VideoBitstream.Configuration(settings.VideoCodec, frame.Configuration)
                : configuration ?? VideoBitstream.Configuration(settings.VideoCodec, frame.Data);
            VideoBitstream.ValidateConfiguration(settings.VideoCodec, csd, source.Width, source.Height, source.ColorFormat);
            var key = VideoBitstream.IsKeyFrame(settings.VideoCodec, frame.Data, csd);
            VideoBitstream.ValidateFrame(settings.VideoCodec, frame.Data, csd);
            lock (gate)
            {
                if (stopping || media.IsCancellationRequested) return;
                CheckDeadlines();
                videoTiming.ReceivedAt(frame.PtsUs, receipt, QpcUs());
                if (configuration is null)
                {
                    if (!key) throw new ProtocolException("Initial capture configuration requires a real keyframe.");
                    configuration = csd;
                    Enqueue(video, videoWake, Configuration());
                }
                if (!configuration.AsSpan().SequenceEqual(csd))
                    throw new ProtocolException("Encoder changed recovery configuration without a declared generation.");
                if (!initialReady || videoRecovery.Phase == RecoveryPhase.WaitReady) continue;
                if (videoRecovery.Phase == RecoveryPhase.WaitSource)
                {
                    var now = QpcUs();
                    if (!key || !FreshKeyTimestamp(frame.PtsUs, videoRecovery.Cutoff, now)) continue;
                }
                var packet = new Message(MessageType.VideoFrame, session, 1, videoRecovery.Generation, frame.PtsUs, key ? 1u : 0u, frame.Data);
                if (Full(video, packet, 8 * 1024 * 1024)) { BeginAttempt(1, "unsent video queue pressure"); continue; }
                Enqueue(video, videoWake, packet);
                if (videoRecovery.Phase == RecoveryPhase.WaitSource)
                {
                    videoRecovery.Phase = RecoveryPhase.WaitProof;
                    videoRecovery.SentKey = frame.PtsUs;
                    status($"Recovery admitted KEY: track=1, generation={videoRecovery.Generation}, keyPtsUs={frame.PtsUs}");
                }
            }
        }
        if (!media.IsCancellationRequested) throw new IOException("Capture ended during recovery.");
    }

    private async Task CaptureAudioAsync()
    {
        var config = JsonData.Bytes(audioSource!.Configuration);
        var json = StrictJson.Parse(config);
        if (StrictJson.String(json, "codec", 16) != "raw" ||
            StrictJson.Integer(json, "sampleRate") != RecoveryAudio.Rate || StrictJson.Integer(json, "channels") != 2 ||
            StrictJson.String(json, "sampleFormat", 16) != "s16le" ||
            json.TryGetProperty("recoveryPtsUs", out _))
            throw new ProtocolException("Windows recovery audio requires initial stereo 48 kHz PCM configuration.");
        lock (gate)
        {
            if (stopping || media.IsCancellationRequested) return;
            audioConfiguration = config;
            Enqueue(audio, audioWake, Configuration(2));
        }
        long previous = -1;
        await foreach (var packet in audioSource.ReadPacketsAsync(media.Token))
        {
            MediaTiming.Record(MediaStage.SourcePcm, session, 2, 1, packet.PtsUs,
                packet.PtsUs + packet.Data.Length / 4 * 1_000_000L / 48000, packet.Data.Length / 4);
            var receipt = QpcUs();
            if (packet.PtsUs < 0 || packet.PtsUs < previous) throw new ProtocolException("Source audio timestamps are invalid or moved backwards.");
            previous = packet.PtsUs;
            foreach (var part in PcmPackets.Split(packet))
            {
                _ = RecoveryAudio.Frames("raw", part.Data);
                if (part.PtsUs < previous) throw new ProtocolException("Split source PCM timestamps moved backwards.");
                previous = part.PtsUs;
                lock (gate)
                {
                    if (stopping || media.IsCancellationRequested) return;
                    CheckDeadlines();
                    audioTiming.ReceivedAt(part.PtsUs, receipt, QpcUs());
                    var message = new Message(MessageType.AudioPacket, session, 2, audioRecovery.Generation, part.PtsUs, 0, part.Data);
                    if (initialReady && audioRecovery.Generation == 1 && initialAudioCutoffPending)
                    {
                        audioRecovery.Cutoff = Math.Max(QpcUs(), part.PtsUs);
                        initialAudioCutoffPending = false;
                    }
                    if (audioRecovery.Phase is RecoveryPhase.WaitProof or RecoveryPhase.Active &&
                        Full(audio, message, 512 * 1024))
                    {
                        BeginAttempt(2, "unsent source PCM queue pressure");
                        continue;
                    }
                    // Initial/recovering audio retains bounded latest source packets, not an obsolete backlog.
                    while (Full(audio, message, 512 * 1024))
                    {
                        var retained = audio.Where(p => p.Type != MessageType.AudioPacket).ToArray();
                        var packets = audio.Where(p => p.Type == MessageType.AudioPacket).Skip(1).ToArray();
                        if (packets.Length + retained.Length == audio.Count)
                            throw new IOException("CONGESTION: source PCM recovery buffer exhausted.");
                        audio.Clear();
                        foreach (var entry in retained) audio.Enqueue(entry);
                        foreach (var entry in packets) audio.Enqueue(entry);
                    }
                    Enqueue(audio, audioWake, message);
                    ReleaseAudio();
                }
            }
        }
        if (!media.IsCancellationRequested) throw new IOException("Audio capture ended during recovery.");
    }

    private void ReleaseAudio()
    {
        if (!initialReady || audioRecovery.Phase != RecoveryPhase.WaitSource || initialAudioCutoffPending) return;
        while (audio.TryPeek(out var packet) && packet.Type == MessageType.AudioPacket)
        {
            var frames = RecoveryAudio.Frames("raw", packet.Payload);
            if (RecoveryAudio.End(packet.PtsUs, frames) <= audioRecovery.Cutoff) { audio.Dequeue(); continue; }
            var skip = RecoveryAudio.Skip(packet.PtsUs, audioRecovery.Cutoff, frames);
            var pts = checked(packet.PtsUs + skip * 1_000_000L / RecoveryAudio.Rate);
            if (pts - audioRecovery.Cutoff > 250_000)
            {
                BeginAttempt(2, $"source PCM starts {pts - audioRecovery.Cutoff}us beyond audio cutoff");
                return;
            }
            // The receiver trims decoded PCM; preserve the source packet's bytes and header PTS.
            audioRecovery.Phase = audioRecovery.Generation == 1 ? RecoveryPhase.Active : RecoveryPhase.WaitProof;
            status($"Recovery audio released: track=2, generation={audioRecovery.Generation}, firstSourceSamplePtsUs={pts}");
            StreamingIfActive();
            audioWake.Release();
            return;
        }
    }

    private async Task ReadControlsAsync()
    {
        while (true)
        {
            var packet = await Wire.ReadAsync(control, transport.Token);
            Interlocked.Exchange(ref lastInput, Environment.TickCount64);
            if (packet.Session != session) throw new ProtocolException("Recovery control session changed.");
            var json = packet.Json();
            if (media.IsCancellationRequested && packet.Type is not (MessageType.Error or MessageType.Stop or MessageType.SessionEnd or MessageType.Ping or MessageType.Pong))
                continue;
            switch (packet.Type)
            {
                case MessageType.StreamReady:
                    if (!StrictJson.Boolean(json, "ok")) throw new ProtocolException("Recovery readiness failed.");
                    lock (gate)
                    {
                        if (stopping || media.IsCancellationRequested) break;
                        CheckDeadlines();
                        var recovery = Track(packet.Track);
                        if (packet.Generation != recovery.Announced || packet.Generation == recovery.ReadyGeneration)
                            throw new ProtocolException("Duplicate/stale recovery readiness.");
                        recovery.ReadyGeneration = packet.Generation;
                        if (!initialReady && videoRecovery.ReadyGeneration == 1 &&
                            (audioSource is null || audioRecovery.ReadyGeneration == 1))
                        {
                            initialReady = true;
                            phaseAt = 0;
                            var first = audio.FirstOrDefault(p => p.Type == MessageType.AudioPacket);
                            initialAudioCutoffPending = first is null;
                            audioRecovery.Cutoff = Math.Max(QpcUs(), first?.PtsUs ?? 0);
                            if (audioSource is not null)
                                audioRecovery.Deadline.Complete(Environment.TickCount64);
                            if (!transmissionStarted)
                            {
                                audioSource?.BeginTransmission();
                                transmissionStarted = true;
                            }
                        }
                        if (initialReady)
                        {
                            foreach (var lane in new[] { videoRecovery, audioRecovery })
                            {
                                if (lane.Phase != RecoveryPhase.WaitReady || lane.ReadyGeneration != lane.Generation) continue;
                                lane.Phase = RecoveryPhase.WaitSource;
                                if (ReferenceEquals(lane, videoRecovery)) lane.Cutoff = QpcUs();
                            }
                            ReleaseAudio();
                        }
                        videoWake.Release();
                        audioWake.Release();
                    }
                    break;
                case MessageType.Status:
                    var state = StrictJson.String(json, "state", 80);
                    _ = StrictJson.String(json, "message", 2048);
                    if (state is not (StreamRecovery.Request or StreamRecovery.Key or StreamRecovery.Complete)) break;
                    HandleProof(state, json);
                    break;
                case MessageType.Ping:
                    await SendControlAsync(Message.Control(MessageType.Pong, session,
                        JsonData.Object(("nonce", StrictJson.Integer(json, "nonce")))), transport.Token);
                    break;
                case MessageType.Pong: _ = StrictJson.Integer(json, "nonce"); break;
                case MessageType.Stop:
                    stopReason = StrictJson.String(json, "reason", 1024); remoteStop = true;
                    terminal.RemoteStop();
                    StopMedia();
                    finished.TrySetResult(); return;
                case MessageType.SessionEnd:
                    _ = StrictJson.String(json, "reason", 1024);
                    if (!media.IsCancellationRequested) throw new ProtocolException("Unexpected recovery SESSION_END.");
                    ended.TrySetResult(); return;
                case MessageType.Error:
                    var peerError = TerminalFailure.PeerError(json);
                    if (peerError is PeerFatalException) throw peerError;
                    status($"Receiver warning: {peerError.Message}");
                    break;
                default: throw new ProtocolException("Unexpected recovery control packet.");
            }
        }
    }

    private void HandleProof(string state, JsonElement json)
    {
        try
        {
            var trackValue = StrictJson.Integer(json, "track");
            if (trackValue is not (1 or 2)) throw new ProtocolException("Invalid recovery track.");
            var track = (uint)trackValue;
            var proofGeneration = StreamRecovery.Generation(json);
            if (track == 1 && json.TryGetProperty("audioPtsUs", out _) ||
                track == 2 && json.TryGetProperty("keyPtsUs", out _) ||
                state == StreamRecovery.Request && (json.TryGetProperty("keyPtsUs", out _) || json.TryGetProperty("audioPtsUs", out _)) ||
                state == StreamRecovery.Key && track != 1)
                throw new ProtocolException("Recovery proof has an incompatible track timestamp.");
            var pts = state == StreamRecovery.Request ? -1 :
                StreamRecovery.Timestamp(json, track == 1 ? "keyPtsUs" : "audioPtsUs");
            lock (gate)
            {
                if (stopping || media.IsCancellationRequested) return;
                var recovery = Track(track);
                CheckDeadlines();
                if (proofGeneration > recovery.Generation) throw new ProtocolException("Future recovery proof generation.");
                if (proofGeneration < recovery.Generation) return;
                if (state == StreamRecovery.Request)
                {
                    if (recovery.Phase == RecoveryPhase.WaitReady) return;
                    BeginAttempt(track, "receiver requested epoch recovery"); return;
                }
                if (track == 1 && (pts != recovery.SentKey || recovery.SentKey < 0) ||
                    track == 2 && pts < recovery.Cutoff)
                    throw new ProtocolException("Recovery proof does not match the track source window.");
                if (state == StreamRecovery.Key)
                {
                    if (recovery.Phase is not (RecoveryPhase.WaitProof or RecoveryPhase.Active))
                        throw new ProtocolException("Unexpected decoded-key proof.");
                    if (!recovery.KeyProven)
                        status($"Recovery decoded KEY: generation={recovery.Generation}, keyPtsUs={pts}, sourceAgeUs={QpcUs() - pts}, track=1");
                    recovery.KeyProven = true;
                }
                else
                {
                    Complete(track, proofGeneration, pts);
                }
            }
        }
        catch (FormatException error) { throw new ProtocolException(error.Message); }
    }

    private TrackRecovery Track(uint track) => track switch
    {
        1 => videoRecovery,
        2 when audioSource is not null => audioRecovery,
        _ => throw new ProtocolException("Recovery track is not enabled.")
    };

    private void Complete(uint track, uint proofGeneration, long pts)
    {
        var recovery = Track(track);
        if (recovery.Phase == RecoveryPhase.Active)
        {
            if (recovery.Completed != pts) throw new ProtocolException("Conflicting recovery completion proof.");
            return;
        }
        if (recovery.Phase != RecoveryPhase.WaitProof || track == 1 && !recovery.KeyProven)
            throw new ProtocolException("Recovery COMPLETE lacks decoded-key/source-audio proof.");
        if (track == 2)
        {
            if (pendingAudioCompletion is { } pending && pending != pts)
                throw new ProtocolException("Conflicting in-flight audio completion proof.");
            if (!writtenAudio.Any(interval => pts >= interval.Start && pts < interval.End))
            {
                var writing = recovery.Writing;
                if (writing is null || writing.Type != MessageType.AudioPacket || writing.Generation != proofGeneration ||
                    pts < Math.Max(writing.PtsUs, recovery.Cutoff) || pts >= AudioEnd(writing))
                    throw new ProtocolException("Audio completion does not match successfully written source audio.");
                pendingAudioCompletion = pts;
                return;
            }
            pendingAudioCompletion = null;
            writtenAudio.Clear();
        }
        recovery.Deadline.Complete(Environment.TickCount64);
        recovery.Completed = pts;
        recovery.Phase = RecoveryPhase.Active;
        status($"Recovery COMPLETE: track={track}, generation={proofGeneration}, ptsUs={pts}");
        StreamingIfActive();
    }

    private void StreamingIfActive()
    {
        if (videoRecovery.Phase == RecoveryPhase.Active &&
            (audioSource is null || audioRecovery.Phase == RecoveryPhase.Active))
            status("Streaming");
    }

    private static long AudioEnd(Message packet) =>
        RecoveryAudio.End(packet.PtsUs, RecoveryAudio.Frames("raw", packet.Payload));

    private static bool FreshKeyTimestamp(long pts, long readyPts, long now) =>
        pts >= readyPts && now - pts <= StreamRecovery.MaximumKeyAgeUs && pts <= now + 50_000;

    private void Written(Message packet)
    {
        if (packet.Type != MessageType.AudioPacket) return;
        if (audioSource is null || packet.Track != 2 || packet.Session != session ||
            packet.Generation > audioRecovery.Generation)
            throw new ProtocolException("Invalid audio write completion.");
        if (packet.Generation != audioRecovery.Generation ||
            audioRecovery.Phase != RecoveryPhase.WaitProof) return;
        CheckDeadlines();
        var start = Math.Max(packet.PtsUs, audioRecovery.Cutoff);
        var end = AudioEnd(packet);
        if (start >= end) return;
        var index = 0;
        while (index < writtenAudio.Count && writtenAudio[index].End < start) index++;
        while (index < writtenAudio.Count && writtenAudio[index].Start <= end)
        {
            var merged = writtenAudio[index];
            start = Math.Min(start, merged.Start);
            end = Math.Max(end, merged.End);
            writtenAudio.RemoveAt(index);
        }
        if (writtenAudio.Count >= 4096)
        {
            BeginAttempt(2, "audio written-proof capacity exceeded 4096 disjoint intervals");
            return;
        }
        writtenAudio.Insert(index, (start, end));
        if (pendingAudioCompletion is { } pts) Complete(2, packet.Generation, pts);
    }

    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var nextPing = Environment.TickCount64 + 2000;
        var nextTiming = Environment.TickCount64 + 5000;
        while (await timer.WaitForNextTickAsync(media.Token))
        {
            var now = Environment.TickCount64;
            bool sync;
            lock (gate)
            {
                if (stopping || media.IsCancellationRequested) return;
                CheckDeadlines();
                ReleaseAudio();
                sync = initialReady && videoRecovery.Phase == RecoveryPhase.WaitSource &&
                    (lastSync == long.MinValue || now - lastSync >= 500);
                if (now >= nextTiming)
                {
                    nextTiming = now + 5000;
                    status($"Recovery transport timing: {TransportTiming()}");
                }
            }
            if (sync)
            {
                await source.RequestKeyFrameAsync(media.Token);
                // A stale timer sample can shorten the interval between actual native requests.
                lock (gate) lastSync = Environment.TickCount64;
            }
            if (now - Interlocked.Read(ref lastInput) >= 10_000) throw new TimeoutException("Receiver recovery liveness timed out.");
            if (now >= nextPing)
            {
                nextPing = now + 2000;
                await SendControlAsync(Message.Control(MessageType.Ping, session, JsonData.Object(("nonce", unchecked((int)now)))), transport.Token);
            }
        }
    }

    private static long QpcUs() => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);
}
