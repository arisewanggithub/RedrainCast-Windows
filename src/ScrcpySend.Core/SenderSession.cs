using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace ScrcpySend.Core;

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
        using var client = new TcpClient(endpoint.Address.AddressFamily) { NoDelay = true };
        using var videoClient = new TcpClient(endpoint.Address.AddressFamily) { NoDelay = true };
        using var audioClient = new TcpClient(endpoint.Address.AddressFamily) { NoDelay = true };
        using var lifetime = new CancellationTokenSource();
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(stopToken, lifetime.Token);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(capture.Token);
        using var writeGate = new SemaphoreSlim(1);
        var session = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        NetworkStream? stream = null;
        NetworkStream? videoStream = null, audioStream = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long lastRead = Stopwatch.GetTimestamp();
        Task? reader = null, heartbeat = null, audioPump = null, controlPump = null, videoMonitor = null, audioMonitor = null;
        var controls = Channel.CreateBounded<Message>(new BoundedChannelOptions(64)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
        Exception? terminal = null;
        var accepted = false;
        var brokenWrite = false;
        var configurationSent = 0;
        var readyMask = 0;
        var expectedMask = settings.EnableAudio ? 6 : 2;
        var failureCode = "CAPTURE_FAILED";
        var remoteStopping = 0;
        var localStopping = 0;
        var mediaAssociated = false;
        Exception? associationFailure = null;

        async Task Send(Message message, CancellationToken token)
        {
            var media = message.Type is MessageType.StreamConfig or MessageType.VideoFrame or MessageType.AudioPacket;
            var destination = media ? message.Track == 1 ? videoStream! : audioStream! : stream!;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            if (!media) await writeGate.WaitAsync(deadline.Token);
            try { await Wire.WriteAsync(destination, message, deadline.Token); }
            catch (Exception error)
            {
                if (!media) brokenWrite = true;
                if (error is OperationCanceledException && !token.IsCancellationRequested && !lifetime.IsCancellationRequested)
                    throw new TimeoutException("TCP writer exceeded its 10-second deadline.", error);
                throw;
            }
            finally { if (!media) writeGate.Release(); }
        }

        async Task<NetworkStream> ConnectMedia(TcpClient mediaClient, string channel, string token, CancellationToken cancellation)
        {
            await mediaClient.ConnectAsync(endpoint.Address, endpoint.Port, cancellation);
            var mediaStream = mediaClient.GetStream();
            await Wire.WriteAsync(mediaStream, Message.Control(MessageType.Hello, session,
                new { role = "media", channel, token }), cancellation);
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
            throw new IOException(count == 0 ? $"Receiver closed the {channel} connection." :
                $"Unexpected data on the receiver's {channel} connection.");
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
                            new { nonce = StrictJson.Integer(json, "nonce") }));
                        break;
                    case MessageType.Pong:
                        _ = StrictJson.Integer(json, "nonce");
                        break;
                    case MessageType.Status:
                        _ = StrictJson.String(json, "state", 80);
                        _ = StrictJson.String(json, "message", 2048);
                        break;
                    case MessageType.Error:
                        var code = StrictJson.String(json, "code", 80);
                        var detail = StrictJson.String(json, "message", 2048);
                        if (StrictJson.Boolean(json, "fatal")) throw new IOException($"{code}: {detail}");
                        state($"Receiver warning: {code}: {detail}");
                        break;
                    case MessageType.Stop:
                        var reason = StrictJson.String(json, "reason", 1024);
                        Interlocked.Exchange(ref remoteStopping, 1);
                        capture.Cancel();
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        {
                            try
                            {
                                await writeGate.WaitAsync(deadline.Token);
                                try
                                {
                                    if (!brokenWrite) await Wire.WriteAsync(stream!, Message.Control(MessageType.SessionEnd, session, new { reason }), deadline.Token);
                                }
                                finally { writeGate.Release(); }
                            }
                            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
                            {
                                state($"Receiver Stop closed the transport: {error.Message}");
                            }
                        }
                        ended.TrySetResult();
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
                await Send(message, capture.Token);
        }

        async Task Heartbeat()
        {
            var nonce = 0;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastRead)) >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException("Receiver sent no traffic for 10 seconds.");
                QueueControl(Message.Control(MessageType.Ping, session, new { nonce = ++nonce }));
            }
        }

        async Task Monitor(Task work, string code = "CAPTURE_FAILED")
        {
            try { await work; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested || capture.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (ended.Task.IsCompletedSuccessfully) return;
                if (Interlocked.CompareExchange(ref terminal, error, null) == null) failureCode = code;
                lifetime.Cancel();
            }
        }

        async Task SendAudio()
        {
            if (audio == null) return;
            Interlocked.Or(ref configurationSent, 4);
            await Send(new(MessageType.StreamConfig, session, 2, 1, -1, 0,
                JsonSerializer.SerializeToUtf8Bytes(audio.Configuration)), startup.Token);
            await ready.Task.WaitAsync(startup.Token);
            long previousPts = -1;
            await foreach (var packet in audio.ReadPacketsAsync(capture.Token))
            {
                if (packet.PtsUs < previousPts) throw new ProtocolException("PCM timestamps moved backwards.");
                previousPts = packet.PtsUs;
                await Send(new(MessageType.AudioPacket, session, 2, 1, packet.PtsUs, 0, packet.Data), capture.Token);
            }
            if (!capture.IsCancellationRequested) throw new IOException("AUDIO_FAILED: audio capture ended unexpectedly.");
        }

        try
        {
            state("Connecting");
            using (var setup = CancellationTokenSource.CreateLinkedTokenSource(capture.Token))
            {
                setup.CancelAfter(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(endpoint.Address, endpoint.Port, setup.Token);
                stream = client.GetStream();
                await Send(Message.Control(MessageType.Hello, 0,
                    new { role = "sender", versions = new[] { 3 }, name = "RedrainCast" }), setup.Token);
                var hello = await Wire.ReadAsync(stream, setup.Token);
                if (hello.Type == MessageType.Reject) ThrowRejected(hello);
                if (hello.Type != MessageType.Hello || hello.Session != 0)
                    throw new ProtocolException("Expected receiver HELLO.");
                ValidateHello(hello.Json());
                state("Negotiating");
                await Send(Message.Control(MessageType.SessionRequest, session, settings.Request(source.EncoderName)), setup.Token);
                var accept = await Wire.ReadAsync(stream, setup.Token);
                if (accept.Session != session) throw new ProtocolException("Acceptance has the wrong session.");
                if (accept.Type == MessageType.Reject) ThrowRejected(accept);
                if (accept.Type != MessageType.SessionAccept) throw new ProtocolException("Expected SESSION_ACCEPT.");
                var json = accept.Json();
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
                reader = Monitor(ReadControls());
                heartbeat = Monitor(Heartbeat());
                videoStream = await ConnectMedia(videoClient, "video", mediaToken, setup.Token);
                audioStream = await ConnectMedia(audioClient, "audio", mediaToken, setup.Token);
                mediaAssociated = true;
            }
            videoMonitor = Monitor(WatchMedia(videoStream!, "video"));
            audioMonitor = Monitor(WatchMedia(audioStream!, "audio"), "AUDIO_FAILED");
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
                    var payload = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        codec = settings.VideoCodec, width = source.Width, height = source.Height,
                        csd = new[] { Convert.ToBase64String(csd) }
                    });
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
            Interlocked.CompareExchange(ref terminal, new TimeoutException("Capture/decoder startup exceeded 10 seconds.", error), null);
        }
        catch (Exception error)
        {
            if (accepted && !mediaAssociated && error is IOException or SocketException)
                associationFailure = error;
            Interlocked.CompareExchange(ref terminal, error, null);
        }
        finally
        {
            state("Stopping");
            controls.Writer.TryComplete();
            capture.Cancel();
            if (Volatile.Read(ref remoteStopping) != 0 && reader != null) await reader;
            if (accepted && stream != null && !brokenWrite && !ended.Task.IsCompleted && !lifetime.IsCancellationRequested &&
                Volatile.Read(ref remoteStopping) == 0)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    if (terminal != null && associationFailure is null)
                    {
                        await Send(Message.Control(MessageType.Error, session,
                            new { code = failureCode, message = terminal.Message[..Math.Min(terminal.Message.Length, 2048)], fatal = true }),
                            deadline.Token);
                    }
                    else
                    {
                        Interlocked.Exchange(ref localStopping, 1);
                        await Send(Message.Control(MessageType.Stop, session,
                            new { reason = associationFailure is null ? "user" : "Media association failed" }), deadline.Token);
                        await ended.Task.WaitAsync(deadline.Token);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
                {
                    Interlocked.CompareExchange(ref terminal, new IOException($"Stop acknowledgement failed: {error.Message}", error), null);
                }
            }
            lifetime.Cancel();
            client.Close();
            videoClient.Close();
            audioClient.Close();
            if (reader != null) await reader;
            if (heartbeat != null) await heartbeat;
            if (audioPump != null) await audioPump;
            if (controlPump != null) await controlPump;
            if (videoMonitor != null) await videoMonitor;
            if (audioMonitor != null) await audioMonitor;
        }
        // Only an explicit rejection of this ending session can race a normal receiver Stop.
        if (terminal is ReceiverRejectionException { Code: "SESSION_ENDED" } rejection && rejection.Session == session &&
            ReferenceEquals(terminal, associationFailure) && Volatile.Read(ref remoteStopping) != 0 &&
            ended.Task.IsCompletedSuccessfully)
            terminal = null;
        if (terminal != null) throw new IOException(terminal.Message, terminal);
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
