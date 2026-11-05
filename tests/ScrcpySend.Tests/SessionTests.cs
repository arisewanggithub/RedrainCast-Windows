using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class SessionTests
{
    private sealed class MediaPeers(TcpClient video, TcpClient audio) : IDisposable
    {
        public NetworkStream Video => video.GetStream();
        public NetworkStream Audio => audio.GetStream();
        public void Dispose() { video.Dispose(); audio.Dispose(); }
    }

    private static async Task<MediaPeers> AcceptMedia(TcpListener listener, NetworkStream control, long session,
        CancellationToken token, bool audio = false)
    {
        var mediaToken = new string('A', 64);
        await Wire.WriteAsync(control, Message.Control(MessageType.SessionAccept, session,
            new { tracks = audio ? new[] { 1, 2 } : [1], mediaToken }), token);
        var video = await listener.AcceptTcpClientAsync(token);
        TcpClient? sound = null;
        try
        {
            foreach (var channel in new[] { "video", "audio" })
            {
                var client = channel == "video" ? video : sound = await listener.AcceptTcpClientAsync(token);
                var hello = await Wire.ReadAsync(client.GetStream(), token);
                Assert.Equal(MessageType.Hello, hello.Type);
                Assert.Equal(session, hello.Session);
                Assert.Equal("media", StrictJson.String(hello.Json(), "role", 16));
                Assert.Equal(channel, StrictJson.String(hello.Json(), "channel", 16));
                Assert.Equal(mediaToken, StrictJson.String(hello.Json(), "token", 64));
                await Wire.WriteAsync(client.GetStream(), Message.Control(MessageType.Hello, session, new { channel }), token);
            }
            return new(video, sound!);
        }
        catch { video.Dispose(); sound?.Dispose(); throw; }
    }

    private sealed class Audio : IAudioSource
    {
        public bool Started, Muted, Disposed;
        public object Configuration => new { codec = "raw", sampleRate = 48000, channels = 2, csd = Array.Empty<string>(), sampleFormat = "s16le" };
        public void BeginTransmission() { Started = true; Muted = true; }
        public async IAsyncEnumerable<AudioPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken token)
        {
            Assert.True(Muted);
            yield return new([0, 0, 0, 0], 1000);
            await Task.Delay(Timeout.Infinite, token);
        }
        public ValueTask DisposeAsync() { Disposed = true; Muted = false; return ValueTask.CompletedTask; }
    }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LateFinalReadinessAfterStopDoesNotRetireTheAcknowledgementReader(bool audioEnabled)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var stop = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var source = new Source();
            var audio = audioEnabled ? new Audio() : null;
            var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
                ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
                new(EnableAudio: audioEnabled), source, _ => { }, stop.Token, audio);
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            var request = await Handshake(stream, timeout.Token);
            using var media = await AcceptMedia(listener, stream, request.Session, timeout.Token, audioEnabled);
            Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Video, timeout.Token)).Type);
            if (audioEnabled)
                Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Audio, timeout.Token)).Type);
            if (audioEnabled)
                await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, 1, 1, -1, 0, """{"ok":true}"""u8.ToArray()), timeout.Token);
            stop.Cancel();
            Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
            await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, audioEnabled ? 2u : 1u, 1,
                -1, 0, """{"ok":true}"""u8.ToArray()), timeout.Token);
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, request.Session, new { reason = "user" }), timeout.Token);
            await run.WaitAsync(timeout.Token);
            Assert.True(source.Disposed);
            if (audio != null) { Assert.False(audio.Started); Assert.True(audio.Disposed); }
        }
    [Theory]
    [InlineData("video", false, false)]
    [InlineData("audio", false, false)]
    [InlineData("video", true, false)]
    [InlineData("audio", true, false)]
    [InlineData("video", false, true)]
    [InlineData("audio", false, true)]
    [InlineData("video", true, true)]
    [InlineData("audio", true, true)]
    public async Task ReceiverStopDuringMediaAssociationIsAcknowledgedWithoutStartingCapture(
        string channel, bool rejectAttachment, bool probeControl)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var audio = new Audio();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(EnableAudio: true), source, _ => { }, default, audio);
        using var control = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = control.GetStream();
        var request = await Handshake(stream, timeout.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
            new { tracks = new[] { 1, 2 }, mediaToken = new string('A', 64) }), timeout.Token);
        using var video = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.Equal(MessageType.Hello, (await Wire.ReadAsync(video.GetStream(), timeout.Token)).Type);
        TcpClient? sound = null;
        try
        {
            if (channel == "audio")
            {
                await Wire.WriteAsync(video.GetStream(), Message.Control(MessageType.Hello, request.Session,
                    new { channel = "video" }), timeout.Token);
                sound = await listener.AcceptTcpClientAsync(timeout.Token);
                Assert.Equal(MessageType.Hello, (await Wire.ReadAsync(sound.GetStream(), timeout.Token)).Type);
            }
            if (probeControl)
            {
                await Wire.WriteAsync(stream, Message.Control(MessageType.Ping, request.Session, new { nonce = 42 }), timeout.Token);
                Assert.Equal(MessageType.Pong, (await Wire.ReadAsync(stream, timeout.Token)).Type);
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Wire.WriteAsync(stream, Message.Control(MessageType.Stop, request.Session,
                new { reason = "receiver stopped during setup" }), timeout.Token);
            if (rejectAttachment)
                await Wire.WriteAsync((sound ?? video).GetStream(), Message.Control(MessageType.Reject, request.Session,
                    new { code = "SESSION_ENDED", message = "Receiver is ending this media session." }), timeout.Token);
            Message ended;
            do
            {
                ended = await Wire.ReadAsync(stream, timeout.Token);
                Assert.True(ended.Type is MessageType.Stop or MessageType.SessionEnd);
            } while (ended.Type == MessageType.Stop);
            Assert.Equal(MessageType.SessionEnd, ended.Type);
            Assert.Equal("receiver stopped during setup", StrictJson.String(ended.Json(), "reason", 1024));
            await run.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.False(source.Started);
            Assert.True(source.Disposed);
            Assert.False(audio.Started);
            Assert.False(audio.Muted);
            Assert.True(audio.Disposed);
        }
        finally { sound?.Dispose(); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task AssociationFailureOrLocalCancelRetiresTheAcceptedSession(
        bool reject, bool attachVideoFirst, bool endingRejection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(), source, _ => { }, stop.Token);
        using var control = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = control.GetStream();
        var request = await Handshake(stream, timeout.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
            new { tracks = new[] { 1 }, mediaToken = new string('A', 64) }), timeout.Token);
        using var video = await listener.AcceptTcpClientAsync(timeout.Token);
        _ = await Wire.ReadAsync(video.GetStream(), timeout.Token);
        TcpClient? sound = null;
        try
        {
            if (attachVideoFirst)
            {
                await Wire.WriteAsync(video.GetStream(), Message.Control(MessageType.Hello, request.Session,
                    new { channel = "video" }), timeout.Token);
                sound = await listener.AcceptTcpClientAsync(timeout.Token);
                _ = await Wire.ReadAsync(sound.GetStream(), timeout.Token);
            }
            if (reject)
                await Wire.WriteAsync((sound ?? video).GetStream(), Message.Control(MessageType.Reject, request.Session,
                    new { code = endingRejection ? "SESSION_ENDED" : "PROTOCOL", message = "Synthetic attachment rejection" }), timeout.Token);
            else stop.Cancel();
            Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, request.Session,
                new { reason = "accepted session retired" }), timeout.Token);
            if (reject)
            {
                var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
                Assert.Contains("Synthetic attachment rejection", error.Message);
            }
            else await run.WaitAsync(timeout.Token);
            Assert.False(source.Started);
            Assert.True(source.Disposed);
        }
        finally { sound?.Dispose(); }
    }

    [Theory]
    [InlineData("video", "channel")]
    [InlineData("audio", "channel")]
    [InlineData("video", "session")]
    [InlineData("audio", "session")]
    [InlineData("video", "type")]
    [InlineData("audio", "type")]
    [InlineData("video", "rejection")]
    [InlineData("audio", "rejection")]
    [InlineData("video", "wrong-session-ended")]
    [InlineData("audio", "wrong-session-ended")]
    [InlineData("video", "eof")]
    [InlineData("audio", "eof")]
    public async Task ReceiverStopDoesNotHideGenuineAssociationFailures(string channel, string failure)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false), new(), source, _ => { }, default);
        using var control = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = control.GetStream();
        var request = await Handshake(stream, timeout.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
            new { tracks = new[] { 1 }, mediaToken = new string('A', 64) }), timeout.Token);
        using var video = await listener.AcceptTcpClientAsync(timeout.Token);
        _ = await Wire.ReadAsync(video.GetStream(), timeout.Token);
        TcpClient? sound = null;
        try
        {
            if (channel == "audio")
            {
                await Wire.WriteAsync(video.GetStream(), Message.Control(MessageType.Hello, request.Session,
                    new { channel = "video" }), timeout.Token);
                sound = await listener.AcceptTcpClientAsync(timeout.Token);
                _ = await Wire.ReadAsync(sound.GetStream(), timeout.Token);
            }
            var socket = sound ?? video;
            if (failure == "eof") socket.Client.Shutdown(SocketShutdown.Send);
            else
            {
                var reply = failure switch
                {
                    "rejection" => Message.Control(MessageType.Reject, request.Session,
                        new { code = "PROTOCOL", message = "Unrelated attachment rejection" }),
                    "wrong-session-ended" => Message.Control(MessageType.Reject, request.Session + 1,
                        new { code = "SESSION_ENDED", message = "Another session ended" }),
                    _ => Message.Control(failure == "type" ? MessageType.Status : MessageType.Hello,
                        failure == "session" ? request.Session + 1 : request.Session,
                        new { channel = failure == "channel" ? "wrong" : channel })
                };
                await Wire.WriteAsync(socket.GetStream(), reply, timeout.Token);
            }
            Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
            await Wire.WriteAsync(stream, Message.Control(MessageType.Stop, request.Session,
                new { reason = "receiver stopped after malformed attachment" }), timeout.Token);
            Assert.Equal(MessageType.SessionEnd, (await Wire.ReadAsync(stream, timeout.Token)).Type);
            var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
            Assert.NotNull(error.InnerException);
            Assert.False(source.Started);
            Assert.True(source.Disposed);
        }
        finally { sound?.Dispose(); }
    }

    private sealed class LargeSource : IVideoSource
    {
        public string EncoderName => "Test hardware";
        public int Width => 640;
        public int Height => 480;
        public bool Disposed;
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            var data = new byte[4 * 1024 * 1024];
            new byte[] { 0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1 }.CopyTo(data, 0);
            yield return new(data, 1000, []);
            await Task.Delay(Timeout.Infinite, token);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Source : IVideoSource
    {
        public string EncoderName => "Test hardware";
        public int Width => 640;
        public int Height => 480;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            Started = true;
            yield return new([0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1], 1000, []);
            await Task.Delay(Timeout.Infinite, token);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static async Task<Message> Handshake(NetworkStream stream, CancellationToken token)
    {
        var hello = await Wire.ReadAsync(stream, token);
        Assert.Equal(MessageType.Hello, hello.Type);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0,
            new { role = "receiver", versions = new[] { 3 }, name = "Test receiver" }), token);
        var request = await Wire.ReadAsync(stream, token);
        Assert.Equal(MessageType.SessionRequest, request.Type);
        return request;
    }

    [Theory]
    [InlineData("srt")]
    [InlineData("tcp")]
    [InlineData("unknown")]
    public async Task TransportOverrideIsRejectedBeforeCaptureStarts(string kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(), source, _ => { }, deadline.Token);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var stream = peer.GetStream();
        var request = await Handshake(stream, deadline.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
            new { tracks = new[] { 1 }, mediaTransport = new { kind } }), deadline.Token);
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(deadline.Token));
        Assert.Contains("Only custom TCP", error.Message);
        Assert.False(source.Started);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task CaptureWaitsForAcceptanceMediaWaitsForReadinessAndStopIsAcknowledged()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(), source, _ => { }, stop.Token);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = socket.GetStream();
        var request = await Handshake(stream, timeout.Token);
        Assert.False(source.Started);
        using var media = await AcceptMedia(listener, stream, request.Session, timeout.Token);
        var config = await Wire.ReadAsync(media.Video, timeout.Token);
        Assert.Equal(MessageType.StreamConfig, config.Type);
        Assert.True(source.Started);
        Assert.False(media.Video.DataAvailable);
        await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, 1, 1, -1, 0, """{"ok":true}"""u8.ToArray()), timeout.Token);
        var frame = await Wire.ReadAsync(media.Video, timeout.Token);
        Assert.Equal(MessageType.VideoFrame, frame.Type);
        Assert.Equal(1u, frame.Flags);
        stop.Cancel();
        var terminal = await Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(MessageType.Stop, terminal.Type);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, request.Session, new { reason = "user" }), timeout.Token);
        await run.WaitAsync(timeout.Token);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task BusyReceiverNeverStartsCapture()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(), source, _ => { }, default);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = socket.GetStream();
        var request = await Handshake(stream, timeout.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Reject, request.Session,
            new { code = "BUSY", message = "Already playing" }), timeout.Token);
        var error = await Assert.ThrowsAsync<IOException>(() => run);
        Assert.Contains("BUSY", error.Message);
        Assert.False(source.Started);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task ReceiverStopIsEchoedAndRetiresCapture()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false), new(), source, _ => { }, default);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = socket.GetStream();
        var request = await Handshake(stream, timeout.Token);
        using var media = await AcceptMedia(listener, stream, request.Session, timeout.Token);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Video, timeout.Token)).Type);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Stop, request.Session, new { reason = "receiver user" }), timeout.Token);
        var ended = await Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(MessageType.SessionEnd, ended.Type);
        Assert.Equal("receiver user", ended.Json().GetProperty("reason").GetString());
        await run.WaitAsync(timeout.Token);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task WrongTrackReadinessFailsWithoutSendingAnyMedia()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false), new(), source, _ => { }, default);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = socket.GetStream();
        var request = await Handshake(stream, timeout.Token);
        using var media = await AcceptMedia(listener, stream, request.Session, timeout.Token);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Video, timeout.Token)).Type);
        await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, 1, 2, -1, 0, """{"ok":true}"""u8.ToArray()), timeout.Token);
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.Contains("readiness", error.Message);
        Assert.Equal(0, await stream.ReadAsync(new byte[1], timeout.Token));
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task UnsupportedAudioFailsBeforeConnectingAndDisposesItsSource()
    {
        var source = new Source();
        await Assert.ThrowsAsync<NotSupportedException>(() => new SenderSession().RunAsync(
            new(IPAddress.Loopback, 1, "Test", false), new(EnableAudio: true), source, _ => { }, default));
        Assert.False(source.Started);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task EnabledAudioRequiresBothTracksReadyAndRestoresOwnedMuteAfterStop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var video = new Source();
        var audio = new Audio();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(EnableAudio: true), video, _ => { }, stop.Token, audio);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = socket.GetStream();
        var request = await Handshake(stream, timeout.Token);
        Assert.True(request.Json().GetProperty("audio").GetProperty("enabled").GetBoolean());
        Assert.Equal("raw", request.Json().GetProperty("audio").GetProperty("codec").GetString());
        Assert.False(audio.Muted);
        using var media = await AcceptMedia(listener, stream, request.Session, timeout.Token, audio: true);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Video, timeout.Token)).Type);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Audio, timeout.Token)).Type);
        await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, 1, 1, -1, 0, """{"ok":true}"""u8.ToArray()), timeout.Token);
        await Task.Delay(40, timeout.Token);
        Assert.False(audio.Muted);
        Assert.False(media.Video.DataAvailable);
        Assert.False(media.Audio.DataAvailable);
        await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, 2, 1, -1, 0, """{"ok":true}"""u8.ToArray()), timeout.Token);
        var types = new HashSet<MessageType>();
        types.Add((await Wire.ReadAsync(media.Video, timeout.Token)).Type);
        types.Add((await Wire.ReadAsync(media.Audio, timeout.Token)).Type);
        Assert.Contains(MessageType.VideoFrame, types);
        Assert.Contains(MessageType.AudioPacket, types);
        Assert.True(audio.Muted);
        stop.Cancel();
        Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, request.Session, new { reason = "user" }), timeout.Token);
        await run.WaitAsync(timeout.Token);
        Assert.True(audio.Disposed);
        Assert.False(audio.Muted);
        Assert.True(video.Disposed);
    }

    [Fact]
    public async Task ReceiverCannotSilentlyRemoveEnabledAudio()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var video = new Source();
        var audio = new Audio();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(EnableAudio: true), video, _ => { }, default, audio);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = socket.GetStream();
        var request = await Handshake(stream, timeout.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session, new { tracks = new[] { 1 } }), timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.False(video.Started);
        Assert.False(audio.Started);
        Assert.True(audio.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiverStopCancelsBackpressuredVideoWithoutWaitingForTheTenSecondWriterDeadline(bool pingBeforeStop)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ReceiveBufferSize = 4096;
        listener.Start();
        var source = new LargeSource();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false), new(), source, _ => { }, default);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        socket.ReceiveBufferSize = 4096;
        var stream = socket.GetStream();
        var request = await Handshake(stream, timeout.Token);
        using var media = await AcceptMedia(listener, stream, request.Session, timeout.Token);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Video, timeout.Token)).Type);
        await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, 1, 1, -1, 0, """{"ok":true}"""u8.ToArray()), timeout.Token);
        var header = new byte[Wire.HeaderSize];
        await media.Video.ReadExactlyAsync(header, timeout.Token);
        Assert.Equal(4u * 1024 * 1024, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (pingBeforeStop)
            await Wire.WriteAsync(stream, Message.Control(MessageType.Ping, request.Session, new { nonce = 42 }), timeout.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Stop, request.Session, new { reason = "receiver user" }), timeout.Token);
        Message response;
        do { response = await Wire.ReadAsync(stream, timeout.Token); }
        while (response.Type == MessageType.Pong);
        Assert.Equal(MessageType.SessionEnd, response.Type);
        await run.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task QueuedPongEchoesTheNonceWhileReaderRemainsAvailable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false), new(), source, _ => { }, default);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        var request = await Handshake(stream, timeout.Token);
        using var media = await AcceptMedia(listener, stream, request.Session, timeout.Token);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Video, timeout.Token)).Type);
        foreach (var nonce in new[] { 42, 43 })
            await Wire.WriteAsync(stream, Message.Control(MessageType.Ping, request.Session, new { nonce }), timeout.Token);
        foreach (var nonce in new[] { 42, 43 })
        {
            var pong = await Wire.ReadAsync(stream, timeout.Token);
            Assert.Equal(MessageType.Pong, pong.Type);
            Assert.Equal(nonce, pong.Json().GetProperty("nonce").GetInt32());
        }
        await Wire.WriteAsync(stream, Message.Control(MessageType.Stop, request.Session, new { reason = "receiver user" }), timeout.Token);
        Assert.Equal(MessageType.SessionEnd, (await Wire.ReadAsync(stream, timeout.Token)).Type);
        await run.WaitAsync(timeout.Token);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task BackpressuredVideoDoesNotBlockAudioPongOrAcknowledgedLocalStop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ReceiveBufferSize = 4096;
        listener.Start();
        var source = new LargeSource();
        var audio = new Audio();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(EnableAudio: true), source, _ => { }, stop.Token, audio);
        using var control = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = control.GetStream();
        var request = await Handshake(stream, timeout.Token);
        using var media = await AcceptMedia(listener, stream, request.Session, timeout.Token, audio: true);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Video, timeout.Token)).Type);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Audio, timeout.Token)).Type);
        foreach (var track in new[] { 1u, 2u })
            await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, track, 1, -1, 0,
                """{"ok":true}"""u8.ToArray()), timeout.Token);
        var header = new byte[Wire.HeaderSize];
        await media.Video.ReadExactlyAsync(header, timeout.Token);
        Assert.Equal(4u * 1024 * 1024, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(MessageType.AudioPacket, (await Wire.ReadAsync(media.Audio, timeout.Token)).Type);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Ping, request.Session, new { nonce = 42 }), timeout.Token);
        Assert.Equal(MessageType.Pong, (await Wire.ReadAsync(stream, timeout.Token)).Type);
        stop.Cancel();
        Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, request.Session, new { reason = "user" }), timeout.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(source.Disposed);
        Assert.True(audio.Disposed);
        Assert.False(audio.Muted);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    public async Task ClosingEitherMediaConnectionRetiresTheWholeSender(string channel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false), new(), source, _ => { }, default);
        using var control = await listener.AcceptTcpClientAsync(timeout.Token);
        var request = await Handshake(control.GetStream(), timeout.Token);
        using var media = await AcceptMedia(listener, control.GetStream(), request.Session, timeout.Token);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Video, timeout.Token)).Type);
        (channel == "video" ? media.Video : media.Audio).Dispose();
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.Contains(channel, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(source.Disposed);
    }
}
