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
    private readonly RecoveryDeadline deadline = new();
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
    private uint generation = 1;
    private bool videoReady, audioReady, awaitingKey = true, keyProven, audioReleased;
    private long readyPts, sentKey = -1, completedKey = -1, completedAudio = -1, firstAudio = -1;
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
        return $"session={session}, generation={generation}, keyProven={keyProven}, audioReleased={audioReleased}; " +
            $"video [{videoTiming.Describe(video, now)}]; audio [{audioTiming.Describe(audio, now)}]";
    }

    private RecoverySender(CaptureSettings settings, IRecoverableVideoSource source, IAudioSource? audioSource,
        Action<string> status, long session, NetworkStream control)
    {
        this.settings = settings; this.source = source; this.audioSource = audioSource;
        this.status = status; this.session = session; this.control = control;
        audioReady = audioSource is null;
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
        using var registration = stop.Register(() => { media.Cancel(); finished.TrySetResult(); });
        try
        {
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(stop, transport.Token, media.Token);
            setup.CancelAfter(TimeSpan.FromSeconds(10));
            await WriteAsync(control, Message.Control(MessageType.SessionRequest, session, settings.Request(source.EncoderName, effectiveOptions)), setup.Token);
            var acceptance = await Wire.ReadAsync(control, setup.Token);
            if (acceptance.Type == MessageType.Reject) throw new IOException($"Receiver rejected recovery session: {acceptance.Json()}");
            if (acceptance.Type != MessageType.SessionAccept || acceptance.Session != session ||
                !acceptance.Json().GetProperty("tracks").EnumerateArray().Select(v => v.GetInt32()).Order()
                    .SequenceEqual(audioSource is null ? [1] : [1, 2]))
                throw new ProtocolException("Invalid recovery session acceptance.");
            var token = StrictJson.String(acceptance.Json(), "mediaToken", 64);
            if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new ProtocolException("Invalid media token.");
            // Once accepted, only this reader owns terminal replies, including during media association.
            accepted = true;
            reader = Monitor(ReadControlsAsync(), "PROTOCOL");
            workers.Add(reader);
            workers.Add(Monitor(HeartbeatAsync(), "TIMEOUT"));
            videoStream = await AttachAsync(videoClient, "video");
            audioStream = await AttachAsync(audioClient, "audio");
            lock (gate) deadline.Begin(Environment.TickCount64);
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
        catch (Exception error) { terminal.Record(error); }
        finally
        {
            media.Cancel();
            status("Stopping");
            try
            {
                await Task.WhenAll(new[] { videoWatch, audioWatch }.OfType<Task>());
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
            terminal.Record(error, code);
            media.Cancel();
            finished.TrySetResult();
        }
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
        while (true)
        {
            media.Token.ThrowIfCancellationRequested();
            Message? next;
            lock (gate)
            {
                next = ReferenceEquals(queue, audio) && queue.TryPeek(out var head) &&
                    head.Type == MessageType.AudioPacket && !audioReleased ? null :
                    queue.TryDequeue(out var queued) ? queued : null;
            }
            if (next is null) { await wake.WaitAsync(media.Token); continue; }
            wake.Wait(0);
            media.Token.ThrowIfCancellationRequested();
            var started = QpcUs();
            lock (gate) timing.WriteStartedUs = started;
            try { await WriteAsync(stream, next, laneWrites.Token); }
            finally
            {
                lock (gate)
                {
                    timing.MaximumWriteUs = Math.Max(timing.MaximumWriteUs, QpcUs() - started);
                    timing.WriteStartedUs = 0;
                }
            }
            lock (gate) timing.Written++;
        }
    }

    private static async Task WriteAsync(NetworkStream stream, Message packet, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
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
            using var writing = CancellationTokenSource.CreateLinkedTokenSource(token, transport.Token);
            try { await WriteAsync(control, packet, writing.Token); }
            catch { controlBroken = true; throw; }
        }
        finally { controlGate.Release(); }
    }

    private void Enqueue(Queue<Message> queue, SemaphoreSlim wake, Message message)
    {
        queue.Enqueue(message);
        wake.Release();
    }

    private static bool Full(Queue<Message> queue, Message incoming, int maximumBytes) =>
        queue.Count >= 64 || queue.Sum(p => p.Payload.Length) + incoming.Payload.Length > maximumBytes ||
        queue.Any(p => p.Type == incoming.Type && incoming.PtsUs - p.PtsUs > 500_000);

    private void BeginAttempt(string reason)
    {
        if (configuration is null) throw new ProtocolException("Recovery requested before video configuration.");
        if (generation == int.MaxValue) throw new ProtocolException("Video recovery generation exhausted.");
        status($"Recovery pressure timing: reason={reason}; {TransportTiming()}");
        generation++;
        deadline.Begin(Environment.TickCount64);
        videoReady = false; awaitingKey = true; keyProven = false; audioReleased = false;
        sentKey = -1; firstAudio = -1;
        video.Clear(); audio.Clear();
        while (videoWake.Wait(0)) { }
        while (audioWake.Wait(0)) { }
        Enqueue(video, videoWake, Configuration());
        status($"Recovering video generation {generation}: {reason}");
    }

    private Message Configuration() => new(MessageType.StreamConfig, session, 1, generation, -1, 0,
        JsonData.Bytes(JsonData.Object(("codec", settings.VideoCodec), ("width", source.Width), ("height", source.Height),
            ("csd", JsonData.Value(new[] { Convert.ToBase64String(configuration!) })))));

    private async Task CaptureVideoAsync()
    {
        long previous = -1;
        await foreach (var frame in source.ReadFramesAsync(media.Token))
        {
            var receipt = QpcUs();
            if (frame.PtsUs < 0 || frame.PtsUs < previous) throw new ProtocolException("Recovery capture timestamps are invalid or moved backwards.");
            previous = frame.PtsUs;
            var csd = VideoBitstream.Configuration(settings.VideoCodec, frame.Configuration.Length > 0 ? frame.Configuration : frame.Data);
            VideoBitstream.ValidateConfiguration(settings.VideoCodec, csd, source.Width, source.Height);
            var key = VideoBitstream.IsKeyFrame(settings.VideoCodec, frame.Data, csd);
            VideoBitstream.ValidateFrame(settings.VideoCodec, frame.Data, csd);
            lock (gate)
            {
                videoTiming.ReceivedAt(frame.PtsUs, receipt, QpcUs());
                if (configuration is null)
                {
                    if (!key) throw new ProtocolException("Initial capture configuration requires a real keyframe.");
                    configuration = csd;
                    Enqueue(video, videoWake, Configuration());
                }
                if (!configuration.AsSpan().SequenceEqual(csd))
                    throw new ProtocolException("Encoder changed recovery configuration without a declared generation.");
                if (!videoReady || !audioReady) continue;
                if (awaitingKey)
                {
                    var now = QpcUs();
                    if (!key || frame.PtsUs < readyPts || now - frame.PtsUs > StreamRecovery.MaximumKeyAgeUs ||
                        frame.PtsUs > now + 50_000) continue;
                    awaitingKey = false; sentKey = frame.PtsUs;
                    status($"Recovery admitted KEY: generation={generation}, keyPtsUs={sentKey}, " +
                        $"sourceReadyPtsUs={readyPts}, sourceAgeUs={now - sentKey}");
                }
                var packet = new Message(MessageType.VideoFrame, session, 1, generation, frame.PtsUs, key ? 1u : 0u, frame.Data);
                if (Full(video, packet, settings.EnableAudio ? 7 * 1024 * 1024 : 8 * 1024 * 1024)) { BeginAttempt("unsent video queue pressure"); continue; }
                Enqueue(video, videoWake, packet);
            }
        }
        if (!media.IsCancellationRequested) throw new IOException("Capture ended during recovery.");
    }

    private async Task CaptureAudioAsync()
    {
        lock (gate) Enqueue(audio, audioWake, new(MessageType.StreamConfig, session, 2, 1, -1, 0,
            JsonData.Bytes(audioSource!.Configuration)));
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
            lock (gate)
            {
                audioTiming.ReceivedAt(part.PtsUs, receipt, QpcUs());
                var message = new Message(MessageType.AudioPacket, session, 2, 1, part.PtsUs, 0, part.Data);
                while (Full(audio, message, 1024 * 1024) && audio.TryPeek(out var oldest) && oldest.Type == MessageType.AudioPacket)
                {
                    if (audioReleased) { BeginAttempt("unsent source PCM queue pressure"); break; }
                    audio.Dequeue();
                }
                if (Full(audio, message, 1024 * 1024)) throw new IOException("CONGESTION: source PCM recovery buffer exhausted.");
                Enqueue(audio, audioWake, message);
                if (keyProven) ReleaseAudio();
            }
            }
        }
    }

    private void ReleaseAudio()
    {
        if (audioReleased) return;
        while (audio.TryPeek(out var packet))
        {
            var frames = RecoveryAudio.Frames("raw", packet.Payload);
            if (RecoveryAudio.End(packet.PtsUs, frames) <= sentKey) { audio.Dequeue(); continue; }
            var skip = RecoveryAudio.Skip(packet.PtsUs, sentKey, frames);
            var pts = checked(packet.PtsUs + skip * 1_000_000L / RecoveryAudio.Rate);
            if (pts - sentKey > 250_000) { BeginAttempt($"source PCM starts {pts - sentKey}us beyond key"); return; }
            // The receiver trims decoded PCM; preserve the source packet's bytes and header PTS.
            firstAudio = pts; audioReleased = true;
            status($"Recovery audio released: generation={generation}, keyPtsUs={sentKey}, firstSourceSamplePtsUs={pts}, queuedPackets={audio.Count}");
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
                        if (packet.Track == 2 && packet.Generation == 1 && !audioReady) audioReady = true;
                        else if (packet.Track == 1 && packet.Generation == generation && !videoReady) videoReady = true;
                        else throw new ProtocolException("Duplicate/stale recovery readiness.");
                        if (videoReady && audioReady)
                        {
                            readyPts = QpcUs();
                            status($"Recovery outputs ready: generation={generation}, sourceReadyPtsUs={readyPts}");
                            if (!transmissionStarted)
                            {
                                audioSource?.BeginTransmission();
                                transmissionStarted = true;
                            }
                        }
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
                    media.Cancel();
                    laneWrites.Cancel();
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
            var proofGeneration = StreamRecovery.Generation(json);
            var key = state == StreamRecovery.Request ? -1 : StreamRecovery.Timestamp(json, "keyPtsUs");
            var audioPts = state == StreamRecovery.Complete && settings.EnableAudio ? StreamRecovery.Timestamp(json, "audioPtsUs") : -1;
            lock (gate)
            {
                if (proofGeneration > generation) throw new ProtocolException("Future recovery proof generation.");
                if (proofGeneration < generation) return;
                deadline.Check(Environment.TickCount64);
                if (state == StreamRecovery.Request)
                {
                    if (!videoReady || awaitingKey) return;
                    BeginAttempt("receiver requested epoch recovery"); return;
                }
                if (key != sentKey || sentKey < 0) throw new ProtocolException("Recovery proof does not match the sent keyframe.");
                if (state == StreamRecovery.Key)
                {
                    if (keyProven) return;
                    var age = QpcUs() - key;
                    status($"Recovery decoded KEY: generation={generation}, keyPtsUs={key}, sourceAgeUs={age}");
                    // Capture freshness was checked before sending. Transit/decode/ACK delay is
                    // bounded by the unresolved recovery deadline, not the capture-age limit.
                    keyProven = true;
                    if (audioSource is not null) ReleaseAudio();
                }
                else
                {
                    if (!keyProven || settings.EnableAudio && (!audioReleased || audioPts < firstAudio ||
                        audioPts < key || audioPts - key > 250_000))
                        throw new ProtocolException("Recovery COMPLETE lacks matching decoded-key/source-audio proof.");
                    if (completedKey == key && completedAudio != audioPts) throw new ProtocolException("Conflicting duplicate audio proof.");
                    completedKey = key; completedAudio = audioPts;
                    status($"Recovery COMPLETE: generation={generation}, keyPtsUs={key}, audioPtsUs={audioPts}");
                    deadline.Complete(Environment.TickCount64);
                    status("Streaming");
                }
            }
        }
        catch (FormatException error) { throw new ProtocolException(error.Message); }
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
                deadline.Check(now);
                sync = videoReady && audioReady && awaitingKey && (lastSync == long.MinValue || now - lastSync >= 500);
                if (sync) lastSync = now;
                if (now >= nextTiming)
                {
                    nextTiming = now + 5000;
                    status($"Recovery transport timing: {TransportTiming()}");
                }
            }
            if (sync) await source.RequestKeyFrameAsync(media.Token);
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
