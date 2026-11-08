using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using RedrainCast.Protocol;

namespace RedrainCast.Send.Core;

public sealed class UncappedFpsUnavailableException() : NotSupportedException(
    "Uncapped capture FPS requires an updated receiver. Update the receiver or select a positive FPS cap in Sharing settings.");

public sealed class SenderSession
{
    private sealed class ReceiverRejectionException(string code, string message, long session) : IOException($"{code}: {message}")
    {
        public string Code { get; } = code;
        public long Session { get; } = session;
    }

    public async Task RunAsync(ReceiverEndpoint endpoint, CaptureSettings settings, IVideoSource source,
        Action<string> state, CancellationToken stopToken, IAudioSource? audio = null)
    {
        Exception? failure = null;
        try { await RunOwnedAsync(endpoint, settings, source, state, stopToken, audio); }
        catch (Exception error) { failure = error; }
        var cleanupFailures = new List<Exception>();
        try { if (audio != null) await audio.DisposeAsync(); }
        catch (Exception error) { cleanupFailures.Add(error); }
        try { await source.DisposeAsync(); }
        catch (Exception error) { cleanupFailures.Add(error); }
        if (cleanupFailures.Count > 0)
        {
            if (failure != null) cleanupFailures.Insert(0, failure);
            throw new AggregateException("Casting/audio restoration cleanup failed.", cleanupFailures);
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task RunOwnedAsync(ReceiverEndpoint endpoint, CaptureSettings settings, IVideoSource source,
        Action<string> state, CancellationToken stopToken, IAudioSource? audio)
    {
        settings.Validate();
        if (settings.EnableAudio != (audio != null))
            throw new NotSupportedException("Enabled audio requires a receiver-only audio source; audio cannot silently disappear.");
        var client = new TcpClient(endpoint.Address.AddressFamily) { NoDelay = true };
        using var videoClient = new TcpClient(endpoint.Address.AddressFamily) { NoDelay = true };
        using var audioClient = new TcpClient(endpoint.Address.AddressFamily) { NoDelay = true };
        using var lifetime = new CancellationTokenSource();
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(stopToken, lifetime.Token);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(capture.Token);
        using var mediaWrites = new CancellationTokenSource();
        using var writeGate = new SemaphoreSlim(1);
        var session = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        NetworkStream? stream = null;
        NetworkStream? videoStream = null, audioStream = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long lastRead = Stopwatch.GetTimestamp();
        Task? reader = null, heartbeat = null, audioPump = null, controlPump = null, videoMonitor = null, audioMonitor = null;
        Task? videoWrite = null, audioWrite = null;
        var controls = Channel.CreateBounded<Message>(new BoundedChannelOptions(64)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
        var terminal = new TerminalFailure();
        var accepted = false;
        var brokenWrite = false;
        var configurationSent = 0;
        var readyMask = 0;
        var expectedMask = settings.EnableAudio ? 6 : 2;
        var remoteReason = "user";
        var remoteStopping = 0;
        var localStopping = 0;
        var mediaAssociated = false;
        Exception? associationFailure = null;

        async Task Send(Message message, CancellationToken token)
        {
            var media = message.Type is MessageType.StreamConfig or MessageType.VideoFrame or MessageType.AudioPacket;
            if (media)
            {
                token.ThrowIfCancellationRequested();
                // Each producer awaits its lane; cancellation detaches only that await, not the native write.
                var pending = WriteMedia(message);
                if (message.Track == 1) videoWrite = pending;
                else audioWrite = pending;
                await pending.WaitAsync(token);
                token.ThrowIfCancellationRequested();
                return;
            }
            var destination = stream!;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await writeGate.WaitAsync(deadline.Token);
            try
            {
                // A started control frame is not truncated by Stop's shorter acknowledgement timeout.
                using var writing = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                writing.CancelAfter(TimeSpan.FromSeconds(10));
                MediaTiming.Record(MediaStage.TcpWriteBegin, message.Session, message.Track, message.Generation,
                    message.PtsUs, a: message.Payload.Length);
                await Wire.WriteAsync(destination, message, writing.Token);
                MediaTiming.Record(MediaStage.TcpWriteEnd, message.Session, message.Track, message.Generation,
                    message.PtsUs, a: message.Payload.Length);
            }
            catch (Exception error)
            {
                brokenWrite = true;
                if (error is OperationCanceledException && !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
                    throw new TimeoutException("TCP writer exceeded its 10-second deadline.", error);
                throw;
            }
            finally { writeGate.Release(); }
        }

        async Task WriteMedia(Message message)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(mediaWrites.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                MediaTiming.Record(MediaStage.TcpWriteBegin, message.Session, message.Track, message.Generation,
                    message.PtsUs, a: message.Payload.Length);
                await Wire.WriteAsync(message.Track == 1 ? videoStream! : audioStream!, message, deadline.Token);
                MediaTiming.Record(MediaStage.TcpWriteEnd, message.Session, message.Track, message.Generation,
                    message.PtsUs, a: message.Payload.Length);
            }
            catch (OperationCanceledException) when (mediaWrites.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // Observe failures even after the producer stopped awaiting; an ACK never erases a real write error.
                terminal.Record(error is OperationCanceledException
                    ? new TimeoutException("TCP writer exceeded its 10-second deadline.", error) : error,
                    message.Track == 1 ? "CAPTURE_FAILED" : "AUDIO_FAILED");
                capture.Cancel();
                throw;
            }
        }

        async Task RetireMedia(Task? pending)
        {
            if (pending == null) return;
            try { await pending; }
            catch (OperationCanceledException) when (mediaWrites.IsCancellationRequested) { }
            catch (Exception error) { terminal.Record(error); }
        }

        async Task<NetworkStream> ConnectMedia(TcpClient mediaClient, string channel, string token, CancellationToken cancellation)
        {
            await mediaClient.ConnectAsync(endpoint.Address, endpoint.Port, cancellation);
            var mediaStream = mediaClient.GetStream();
            await Wire.WriteAsync(mediaStream, Message.Control(MessageType.Hello, session,
                JsonData.Object(("role", "media"), ("channel", channel), ("token", token))), cancellation);
            var reply = await Wire.ReadAsync(mediaStream, cancellation);
            if (reply.Type == MessageType.Reject) ThrowRejected(reply);
            if (reply.Type != MessageType.Hello || reply.Session != session ||
                StrictJson.String(reply.Json(), "channel", 16) != channel)
                throw new ProtocolException($"Receiver did not attach the {channel} connection.");
            return mediaStream;
        }

        async Task WatchMedia(NetworkStream mediaStream, string channel)
        {
            var buffer = new byte[1];
            var count = await mediaStream.ReadAsync(buffer, capture.Token);
            if (count == 0) throw new MediaEofException($"Receiver closed the {channel} connection.");
            throw new IOException($"Unexpected data on the receiver's {channel} connection.");
        }

        async Task ReadControls()
        {
            while (!lifetime.IsCancellationRequested)
            {
                var message = await Wire.ReadAsync(stream!, lifetime.Token);
                Interlocked.Exchange(ref lastRead, Stopwatch.GetTimestamp());
                if (message.Session != session) throw new ProtocolException("Receiver changed the session.");
                var json = message.Json();
                switch (message.Type)
                {
                    case MessageType.StreamReady:
                        var trackBit = 1 << (int)message.Track;
                        if ((Volatile.Read(ref configurationSent) & trackBit) == 0 || (expectedMask & trackBit) == 0 ||
                            message.Generation != 1 || !StrictJson.Boolean(json, "ok"))
                            throw new ProtocolException("Unexpected stream readiness.");
                        var previous = Interlocked.Or(ref readyMask, trackBit);
                        if ((previous & trackBit) != 0) throw new ProtocolException("Duplicate stream readiness.");
                        if ((previous | trackBit) == expectedMask)
                        {
                            if (capture.IsCancellationRequested) break;
                            audio?.BeginTransmission();
                            startup.CancelAfter(Timeout.InfiniteTimeSpan);
                            ready.SetResult();
                        }
                        break;
                    case MessageType.Ping:
                        QueueControl(Message.Control(MessageType.Pong, session,
                            JsonData.Object(("nonce", StrictJson.Integer(json, "nonce")))));
                        break;
                    case MessageType.Pong:
                        _ = StrictJson.Integer(json, "nonce");
                        break;
                    case MessageType.Status:
                        _ = StrictJson.String(json, "state", 80);
                        _ = StrictJson.String(json, "message", 2048);
                        break;
                    case MessageType.Error:
                        var peerError = TerminalFailure.PeerError(json);
                        if (peerError is PeerFatalException) throw peerError;
                        state($"Receiver warning: {peerError.Message}");
                        break;
                    case MessageType.Stop:
                        remoteReason = StrictJson.String(json, "reason", 1024);
                        Interlocked.Exchange(ref remoteStopping, 1);
                        terminal.RemoteStop();
                        capture.Cancel();
                        mediaWrites.Cancel();
                        // Let the owner retire media writes before closing their sockets.
                        return;
                    case MessageType.SessionEnd:
                        _ = StrictJson.String(json, "reason", 1024);
                        if (Volatile.Read(ref localStopping) == 0) throw new ProtocolException("Unexpected SESSION_END.");
                        ended.TrySetResult();
                        return;
                    default:
                        throw new ProtocolException("Unexpected receiver control message.");
                }
            }
        }

        void QueueControl(Message message)
        {
            if (capture.IsCancellationRequested) return;
            if (!controls.Writer.TryWrite(message))
                throw new IOException("CONGESTION: pending liveness controls exceeded the 64-message bound.");
        }

        async Task WriteControls()
        {
            await foreach (var message in controls.Reader.ReadAllAsync(capture.Token))
            {
                if (capture.IsCancellationRequested) return;
                // Finish any control frame already being written before acknowledging Stop.
                await Send(message, lifetime.Token);
            }
        }

        async Task Heartbeat()
        {
            var nonce = 0;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastRead)) >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException("Receiver sent no traffic for 10 seconds.");
                QueueControl(Message.Control(MessageType.Ping, session, JsonData.Object(("nonce", ++nonce))));
            }
        }

        async Task Monitor(Task work, string code = "CAPTURE_FAILED")
        {
            try { await work; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested || capture.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (ended.Task.IsCompletedSuccessfully) return;
                terminal.Record(error, code);
                capture.Cancel();
            }
        }

        async Task SendAudio()
        {
            if (audio == null) return;
            Interlocked.Or(ref configurationSent, 4);
            await Send(new(MessageType.StreamConfig, session, 2, 1, -1, 0,
                JsonData.Bytes(audio.Configuration)), startup.Token);
            await ready.Task.WaitAsync(startup.Token);
            long previousPts = -1;
            await foreach (var packet in audio.ReadPacketsAsync(capture.Token))
            {
                MediaTiming.Record(MediaStage.SourcePcm, session, 2, 1, packet.PtsUs,
                    packet.PtsUs + packet.Data.Length / 4 * 1_000_000L / 48000, packet.Data.Length / 4);
                if (packet.PtsUs < previousPts) throw new ProtocolException("PCM timestamps moved backwards.");
                previousPts = packet.PtsUs;
                foreach (var part in PcmPackets.Split(packet))
                    await Send(new(MessageType.AudioPacket, session, 2, 1, part.PtsUs, 0, part.Data), capture.Token);
            }
            if (!capture.IsCancellationRequested) throw new IOException("AUDIO_FAILED: audio capture ended unexpectedly.");
        }

        async Task PrepareCapture()
        {
            state("Preparing capture");
            using (var preparation = CancellationTokenSource.CreateLinkedTokenSource(capture.Token))
            {
                preparation.CancelAfter(CaptureWorkerHandshake.PreparationTimeout);
                try { await source.PrepareAsync(preparation.Token); }
                catch (OperationCanceledException error) when (!capture.IsCancellationRequested)
                {
                    throw new TimeoutException("Capture preparation exceeded 30 seconds; capture was not started.", error);
                }
            }
        }

        try
        {
            var hdrSource = source as IHdrVideoSource;
            var deferPreparation = hdrSource?.NativeHdrEligible == true;
            if (!deferPreparation)
            {
                if (hdrSource is not null) await hdrSource.ConfigureHdrAsync(false, capture.Token);
                await PrepareCapture();
            }
            state("Connecting");
            var setup = CancellationTokenSource.CreateLinkedTokenSource(capture.Token);
            try
            {
                setup.CancelAfter(TimeSpan.FromSeconds(10));
                var helloFields = JsonData.Object(("role", "sender"), ("versions", JsonData.Value(new[] { 3 })),
                    ("name", "RedrainCast"), ("uncappedFps", true));
                helloFields["receiverOptions"] = ReceiverOptionCapabilities.Implemented.WireValue;
                if (source is IHdrVideoSource) helloFields["hdrVideoVersion"] = HdrVideoProtocol.Version;
                if (source is IRecoverableVideoSource { SupportsStreamRecovery: true })
                {
                    helloFields["streamRecovery"] = true;
                    helloFields["streamRecoveryVersion"] = StreamRecovery.Version;
                }
                async Task<Message> ConnectHello()
                {
                    await client.ConnectAsync(endpoint.Address, endpoint.Port, setup.Token);
                    stream = client.GetStream();
                    await Send(Message.Control(MessageType.Hello, 0, helloFields), setup.Token);
                    var reply = await Wire.ReadAsync(stream, setup.Token);
                    if (reply.Type == MessageType.Reject) ThrowRejected(reply);
                    if (reply.Type != MessageType.Hello || reply.Session != 0)
                        throw new ProtocolException("Expected receiver HELLO.");
                    ValidateHello(reply.Json());
                    var uncappedFps = reply.Json().TryGetProperty("uncappedFps", out _) &&
                        StrictJson.Boolean(reply.Json(), "uncappedFps");
                    if (settings.MaxFps == 0 && !uncappedFps) throw new UncappedFpsUnavailableException();
                    return reply;
                }

                var hello = await ConnectHello();
                if (deferPreparation && hdrSource is not null)
                {
                    HdrReceiverCapabilities hdrCapabilities;
                    try { hdrCapabilities = HdrVideoProtocol.Capabilities(hello.Json()); }
                    catch (FormatException error) { throw new ProtocolException(error.Message); }
                    // Only a validated v1 payload acknowledges the receiver's extended setup deadline.
                    var extendedSetup = hello.Json().TryGetProperty("receiveCapabilities", out var hdrPayload) &&
                        hdrPayload.GetProperty("version").GetInt32() == HdrVideoProtocol.Version;
                    var useHdr = extendedSetup && hdrCapabilities.CanReceiveHdr10;
                    if (extendedSetup) setup.CancelAfter(TimeSpan.FromSeconds(45));
                    else
                    {
                        // A legacy peer's ten-second setup budget cannot include cold capture preparation.
                        stream!.Dispose();
                        stream = null;
                        client.Dispose();
                        client = new TcpClient(endpoint.Address.AddressFamily) { NoDelay = true };
                    }
                    await hdrSource.ConfigureHdrAsync(useHdr, extendedSetup ? setup.Token : capture.Token);
                    await PrepareCapture();
                    if (source.ColorFormat.Hdr10 != useHdr)
                        throw new ProtocolException("Capture source did not honor the negotiated HDR mode.");
                    if (useHdr) state("Native HDR10 negotiated: HEVC Main10, BT.2020/PQ.");
                    else
                        state("Receiver has no usable HDR10 playback mode; casting continues with HDR-to-SDR conversion.");
                    if (!extendedSetup)
                    {
                        setup.Dispose();
                        setup = CancellationTokenSource.CreateLinkedTokenSource(capture.Token);
                        setup.CancelAfter(TimeSpan.FromSeconds(10));
                        // The preflight connection never claimed a session. Renegotiate on the final connection,
                        // retaining prepared SDR resources even if its HELLO now offers HDR.
                        state("Connecting");
                        hello = await ConnectHello();
                        try { _ = HdrVideoProtocol.Capabilities(hello.Json()); }
                        catch (FormatException error) { throw new ProtocolException(error.Message); }
                    }
                }
                bool recovery;
                ReceiverOptions effectiveOptions;
                try
                {
                    recovery = StreamRecovery.Negotiated(hello.Json(), acknowledgement: true);
                    var capabilities = ReceiverOptionProtocol.Capabilities(hello.Json())
                        .Intersect(ReceiverOptionCapabilities.Implemented);
                    effectiveOptions = capabilities.Select(settings.ShowPlaybackStatistics);
                }
                catch (FormatException error) { throw new ProtocolException(error.Message); }
                if (recovery)
                {
                    if (source is not IRecoverableVideoSource { SupportsStreamRecovery: true } recoverable)
                        throw new ProtocolException("Unrequested recovery acknowledgement.");
                    if (settings.ShowPlaybackStatistics && !effectiveOptions.PlaybackStats)
                        state("Receiver option unavailable: Playback statistics require a compatible receiver; casting continues without the overlay.");
                    await RecoverySender.RunAsync(endpoint, settings, recoverable, audio, state, session, stream!, stopToken, effectiveOptions);
                    return;
                }
                state("Negotiating");
                if (settings.ShowPlaybackStatistics && !effectiveOptions.PlaybackStats)
                    state("Receiver option unavailable: Playback statistics require a compatible receiver; casting continues without the overlay.");
                await Send(Message.Control(MessageType.SessionRequest, session,
                    settings.Request(source.EncoderName, effectiveOptions, source.ColorFormat)), setup.Token);
                var accept = await Wire.ReadAsync(stream!, setup.Token);
                if (accept.Session != session) throw new ProtocolException("Acceptance has the wrong session.");
                if (accept.Type == MessageType.Reject) ThrowRejected(accept);
                if (accept.Type != MessageType.SessionAccept) throw new ProtocolException("Expected SESSION_ACCEPT.");
                var json = accept.Json();
                try { HdrVideoProtocol.RequireAcknowledgement(json, source.ColorFormat); }
                catch (FormatException error) { throw new ProtocolException(error.Message); }
                if (json.TryGetProperty("mediaTransport", out _))
                    throw new ProtocolException("Only custom TCP media is supported; transport overrides are not accepted.");
                if (!json.TryGetProperty("tracks", out var tracks) ||
                    tracks.ValueKind != JsonValueKind.Array ||
                    !tracks.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Number) ||
                    !tracks.EnumerateArray().Select(item => item.GetInt32()).Order().SequenceEqual(settings.EnableAudio ? [1, 2] : [1]))
                    throw new ProtocolException("Receiver did not accept exactly the requested TCP tracks.");
                var mediaToken = StrictJson.String(json, "mediaToken", 64);
                if (mediaToken.Length != 64 || !mediaToken.All(Uri.IsHexDigit))
                    throw new ProtocolException("Invalid media connection token.");
                accepted = true;
                Interlocked.Exchange(ref lastRead, Stopwatch.GetTimestamp());
                controlPump = Monitor(WriteControls(), "INTERNAL");
                reader = Monitor(ReadControls(), "PROTOCOL");
                heartbeat = Monitor(Heartbeat());
                videoStream = await ConnectMedia(videoClient, "video", mediaToken, setup.Token);
                audioStream = await ConnectMedia(audioClient, "audio", mediaToken, setup.Token);
                mediaAssociated = true;
            }
            finally { setup.Dispose(); }
            videoMonitor = Monitor(WatchMedia(videoStream!, "video"), "PROTOCOL");
            audioMonitor = Monitor(WatchMedia(audioStream!, "audio"), "PROTOCOL");
            capture.Token.ThrowIfCancellationRequested();
            state("Initializing capture");
            startup.CancelAfter(TimeSpan.FromSeconds(10));
            var configured = false;
            var streaming = false;
            byte[] activeConfiguration = [];
            long lastPts = -1;
            if (audio != null) audioPump = Monitor(SendAudio(), "AUDIO_FAILED");
            await foreach (var frame in source.ReadFramesAsync(startup.Token)
                               .WithCancellation(startup.Token))
            {
                if (frame.PtsUs < lastPts) throw new ProtocolException("Encoder timestamps moved backwards.");
                lastPts = frame.PtsUs;
                if (!configured)
                {
                    var csd = frame.Configuration.Length > 0
                        ? VideoBitstream.Configuration(settings.VideoCodec, frame.Configuration)
                        : VideoBitstream.Configuration(settings.VideoCodec, frame.Data);
                    activeConfiguration = csd;
                    if (!VideoBitstream.IsKeyFrame(settings.VideoCodec, frame.Data, csd))
                        throw new ProtocolException("First hardware encoder output is not a real random-access picture.");
                    var configuration = JsonData.Object(
                        ("codec", settings.VideoCodec), ("width", source.Width), ("height", source.Height),
                        ("csd", JsonData.Value(new[] { Convert.ToBase64String(csd) })));
                    HdrVideoProtocol.AddFormat(configuration, source.ColorFormat);
                    var payload = JsonData.Bytes(configuration);
                    Interlocked.Or(ref configurationSent, 2);
                    await Send(new Message(MessageType.StreamConfig, session, 1, 1, -1, 0, payload), startup.Token);
                    await ready.Task.WaitAsync(startup.Token);
                    configured = true;
                }
                await Send(new Message(MessageType.VideoFrame, session, 1, 1, frame.PtsUs,
                    VideoBitstream.IsKeyFrame(settings.VideoCodec, frame.Data, activeConfiguration) ? 1u : 0u, frame.Data), capture.Token);
                if (!streaming) { streaming = true; state("Streaming"); }
            }
            if (!capture.IsCancellationRequested) throw new IOException("Capture ended unexpectedly.");
        }
        catch (OperationCanceledException) when (capture.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException error)
        {
            terminal.Record(new TimeoutException("Capture/decoder startup exceeded 10 seconds.", error));
        }
        catch (Exception error)
        {
            if (accepted && !mediaAssociated && error is IOException or SocketException)
                associationFailure = error;
            terminal.Record(error);
        }
        finally
        {
            state("Stopping");
            controls.Writer.TryComplete();
            capture.Cancel();
            if (audioPump != null) await audioPump;
            if (videoMonitor != null) await videoMonitor;
            if (audioMonitor != null) await audioMonitor;
            if (terminal.IsProvisional && reader != null)
            {
                try { await reader.WaitAsync(terminal.DrainRemaining); }
                catch (TimeoutException) { }
            }
            if (Volatile.Read(ref remoteStopping) != 0 && reader != null) await reader;
            if (controlPump != null) await controlPump;
            if (accepted && stream != null && !brokenWrite && !ended.Task.IsCompleted &&
                !lifetime.IsCancellationRequested && terminal.Error is not PeerFatalException)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    if (Volatile.Read(ref remoteStopping) != 0)
                    {
                        await Send(Message.Control(MessageType.SessionEnd, session, JsonData.Object(("reason", remoteReason))), deadline.Token);
                        ended.TrySetResult();
                    }
                    else if (terminal.Error is { } failure && associationFailure is null)
                    {
                        await Send(Message.Control(MessageType.Error, session,
                            JsonData.Object(("code", terminal.Code), ("message", failure.Message[..Math.Min(failure.Message.Length, 2048)]), ("fatal", true))),
                            deadline.Token);
                    }
                    else
                    {
                        Interlocked.Exchange(ref localStopping, 1);
                        await Send(Message.Control(MessageType.Stop, session,
                            JsonData.Object(("reason", associationFailure is null ? "user" : "Media association failed"))), deadline.Token);
                        await Task.WhenAny(ended.Task, reader!).WaitAsync(deadline.Token);
                        if (Volatile.Read(ref remoteStopping) != 0)
                        {
                            await Send(Message.Control(MessageType.SessionEnd, session, JsonData.Object(("reason", remoteReason))), deadline.Token);
                            ended.TrySetResult();
                        }
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
                {
                    terminal.Record(new IOException($"Stop acknowledgement failed: {error.Message}", error));
                }
            }
            // Local Stop must be admitted/acknowledged before a partial media packet can be abandoned.
            mediaWrites.Cancel();
            await RetireMedia(videoWrite);
            await RetireMedia(audioWrite);
            videoClient.Close();
            audioClient.Close();
            lifetime.Cancel();
            client.Dispose();
            if (reader != null) await reader;
            if (heartbeat != null) await heartbeat;
            if (audioPump != null) await audioPump;
            if (controlPump != null) await controlPump;
            if (videoMonitor != null) await videoMonitor;
            if (audioMonitor != null) await audioMonitor;
        }
        // Only an explicit rejection of this ending session can race a normal receiver Stop.
        var terminalError = terminal.Error;
        if (terminalError is ReceiverRejectionException { Code: "SESSION_ENDED" } rejection && rejection.Session == session &&
            ReferenceEquals(terminalError, associationFailure) && Volatile.Read(ref remoteStopping) != 0 &&
            ended.Task.IsCompletedSuccessfully)
            terminalError = null;
        if (terminalError is UncappedFpsUnavailableException) ExceptionDispatchInfo.Capture(terminalError).Throw();
        if (terminalError != null) throw new IOException(terminalError.Message, terminalError);
    }

    private static void ValidateHello(JsonElement json)
    {
        if (StrictJson.String(json, "role", 80) != "receiver" || StrictJson.String(json, "name", 80).Length == 0 ||
            !json.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array ||
            versions.GetArrayLength() is < 1 or > 16 ||
            versions.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Number) ||
            !versions.EnumerateArray().Any(item => item.GetInt32() == 3))
            throw new ProtocolException("Invalid receiver HELLO.");
    }

    private static void ThrowRejected(Message message)
    {
        var json = message.Json();
        throw new ReceiverRejectionException(StrictJson.String(json, "code", 80),
            StrictJson.String(json, "message", 2048), message.Session);
    }
}
