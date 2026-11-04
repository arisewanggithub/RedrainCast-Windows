using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class SessionTests
{
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
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
                new { tracks = audioEnabled ? new[] { 1, 2 } : [1] }), timeout.Token);
            for (var index = 0; index < (audioEnabled ? 2 : 1); index++)
                Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(stream, timeout.Token)).Type);
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
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session, new { tracks = new[] { 1 } }), timeout.Token);
        var config = await Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(MessageType.StreamConfig, config.Type);
        Assert.True(source.Started);
        Assert.False(stream.DataAvailable);
        await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, 1, 1, -1, 0, """{"ok":true}"""u8.ToArray()), timeout.Token);
        var frame = await Wire.ReadAsync(stream, timeout.Token);
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
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session, new { tracks = new[] { 1 } }), timeout.Token);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(stream, timeout.Token)).Type);
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
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session, new { tracks = new[] { 1 } }), timeout.Token);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(stream, timeout.Token)).Type);
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
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session, new { tracks = new[] { 1, 2 } }), timeout.Token);
        for (var index = 0; index < 2; index++)
            Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(stream, timeout.Token)).Type);
        await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, 1, 1, -1, 0, """{"ok":true}"""u8.ToArray()), timeout.Token);
        await Task.Delay(40, timeout.Token);
        Assert.False(audio.Muted);
        Assert.False(stream.DataAvailable);
        await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, 2, 1, -1, 0, """{"ok":true}"""u8.ToArray()), timeout.Token);
        var types = new HashSet<MessageType>();
        for (var index = 0; index < 2; index++) types.Add((await Wire.ReadAsync(stream, timeout.Token)).Type);
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
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session, new { tracks = new[] { 1 } }), timeout.Token);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(stream, timeout.Token)).Type);
        await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, 1, 1, -1, 0, """{"ok":true}"""u8.ToArray()), timeout.Token);
        var header = new byte[Wire.HeaderSize];
        await stream.ReadExactlyAsync(header, timeout.Token);
        Assert.Equal(4u * 1024 * 1024, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (pingBeforeStop)
            await Wire.WriteAsync(stream, Message.Control(MessageType.Ping, request.Session, new { nonce = 42 }), timeout.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Stop, request.Session, new { reason = "receiver user" }), timeout.Token);
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
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session, new { tracks = new[] { 1 } }), timeout.Token);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(stream, timeout.Token)).Type);
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
}
