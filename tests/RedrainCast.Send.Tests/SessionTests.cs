using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

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
        public System.Text.Json.Nodes.JsonNode Configuration => RedrainCast.Protocol.JsonData.Object(
            ("codec", "raw"), ("sampleRate", 48000), ("channels", 2),
            ("csd", RedrainCast.Protocol.JsonData.Value(Array.Empty<string>())), ("sampleFormat", "s16le"));
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
    public async Task AcknowledgedStopStillSurfacesSourceCleanupFailure(bool receiverStop)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var cleanupFailure = new IOException("Capture worker exited with exit code 1: fixtureCleanupFailure");
        var source = new Source { DisposalFailure = cleanupFailure };
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(EnableAudio: false), source, _ => { }, stop.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        var request = await Handshake(stream, timeout.Token);
        using var media = await AcceptMedia(listener, stream, request.Session, timeout.Token);
        Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Video, timeout.Token)).Type);
        if (receiverStop)
        {
            await Wire.WriteAsync(stream, Message.Control(MessageType.Stop, request.Session,
                new { reason = "receiver stopped" }), timeout.Token);
            Assert.Equal(MessageType.SessionEnd, (await Wire.ReadAsync(stream, timeout.Token)).Type);
        }
        else
        {
            stop.Cancel();
            Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, request.Session,
                new { reason = "user" }), timeout.Token);
        }
        var error = await Assert.ThrowsAsync<AggregateException>(() => run.WaitAsync(timeout.Token));
        Assert.Same(cleanupFailure, Assert.Single(error.InnerExceptions));
        Assert.True(source.Disposed);
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
            // STOP may retire the media socket immediately; finish its rejection before requesting retirement.
            if (rejectAttachment)
                await Wire.WriteAsync((sound ?? video).GetStream(), Message.Control(MessageType.Reject, request.Session,
                    new { code = "SESSION_ENDED", message = "Receiver is ending this media session." }), timeout.Token);
            await Wire.WriteAsync(stream, Message.Control(MessageType.Stop, request.Session,
                new { reason = "receiver stopped during setup" }), timeout.Token);
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
        public Func<Task>? BeforeStop { get; init; }
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            var data = new byte[4 * 1024 * 1024];
            new byte[] { 0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1 }.CopyTo(data, 0);
            try
            {
                yield return new(data, 1000, []);
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                if (BeforeStop is not null) await BeforeStop();
            }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Source : IRecoverableVideoSource
    {
        public string EncoderName => "Test hardware";
        public int Width => 640;
        public int Height => 480;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public bool Prepared { get; private set; }
        public bool SupportsStreamRecovery { get; init; }
        public Func<CancellationToken, Task>? Preparation { get; init; }
        public Exception? DisposalFailure { get; init; }
        public ValueTask RequestKeyFrameAsync(CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask PrepareAsync(CancellationToken token)
        {
            Prepared = true;
            if (Preparation is not null) await Preparation(token);
        }
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            Started = true;
            yield return new([0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1], 1000, []);
            await Task.Delay(Timeout.Infinite, token);
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposalFailure);
        }
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationPrecedesNegotiationAndRejectionNeverStartsCapture(bool recovery)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { SupportsStreamRecovery = recovery, Preparation = token => prepared.Task.WaitAsync(token) };
        var audio = new Audio();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(EnableAudio: true), source, _ => { }, timeout.Token, audio);
        Assert.True(source.Prepared);
        Assert.False(listener.Pending());
        Assert.False(source.Started);
        Assert.False(audio.Started);
        prepared.SetResult();
        using var control = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = control.GetStream();
        Assert.Equal(MessageType.Hello, (await Wire.ReadAsync(stream, timeout.Token)).Type);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0,
            new { role = "receiver", versions = new[] { 3 }, name = "Test receiver",
                streamRecovery = recovery, streamRecoveryVersion = 1 }), timeout.Token);
        var request = await Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(MessageType.SessionRequest, request.Type);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Reject, request.Session,
            new { code = "BUSY", message = "Receiver busy" }), timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.False(source.Started);
        Assert.False(audio.Started);
        Assert.True(source.Disposed);
        Assert.True(audio.Disposed);
    }

    [Fact]
    public async Task CancellingPreparationRetiresResourcesWithoutConnectingOrCapturing()
    {
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source { Preparation = token => Task.Delay(Timeout.Infinite, token) };
        var audio = new Audio();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(EnableAudio: true), source, _ => { }, stop.Token, audio);
        Assert.True(source.Prepared);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(listener.Pending());
        Assert.False(source.Started);
        Assert.False(audio.Started);
        Assert.True(source.Disposed);
        Assert.True(audio.Disposed);
    }

    [Fact]
    public async Task FailedPreparationIsReportedAndRetiredBeforeConnecting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source { Preparation = _ => Task.FromException(new IOException("No hardware encoder")) };
        var error = await Assert.ThrowsAsync<IOException>(() => new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false), new(), source, _ => { }, default));
        Assert.Contains("No hardware encoder", error.Message);
        Assert.False(listener.Pending());
        Assert.False(source.Started);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task ColdPreparationDoesNotConsumeTheTenSecondMediaBudget()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source { Preparation = token => Task.Delay(10_100, token) };
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(), source, _ => { }, timeout.Token);
        Assert.True(source.Prepared);
        Assert.False(listener.Pending());
        using var control = await listener.AcceptTcpClientAsync(timeout.Token);
        var request = await Handshake(control.GetStream(), timeout.Token);
        await Wire.WriteAsync(control.GetStream(), Message.Control(MessageType.Reject, request.Session,
            new { code = "BUSY", message = "Receiver busy" }), timeout.Token);
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.Contains("Receiver busy", error.Message);
        Assert.False(source.Started);
        Assert.True(source.Disposed);
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
        Assert.Equal(MessageType.Error, (await Wire.ReadAsync(stream, timeout.Token)).Type);
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
        => await VerifyReceiverStopWithBackpressuredVideoAsync(pingBeforeStop);

    [Fact]
    public async Task RepeatedReceiverStopWithPendingPongPreservesControlFraming()
    {
        for (var iteration = 0; iteration < 32; iteration++)
            await VerifyReceiverStopWithBackpressuredVideoAsync(pingBeforeStop: true);
    }

    private static async Task VerifyReceiverStopWithBackpressuredVideoAsync(bool pingBeforeStop)
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task FatalPeerErrorBeforeLocalStopAcknowledgementIsNotHidden(bool backpressureAudio)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ReceiveBufferSize = 4096;
        listener.Start();
        var source = new LargeSource();
        var audio = backpressureAudio ? new ContinuousAudio() : null;
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(EnableAudio: backpressureAudio), source, _ => { }, stop.Token, audio);
        using var control = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = control.GetStream();
        var request = await Handshake(stream, timeout.Token);
        using var media = await AcceptMedia(listener, stream, request.Session, timeout.Token, backpressureAudio);
        try
        {
            Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Video, timeout.Token)).Type);
            if (audio != null)
                Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Audio, timeout.Token)).Type);
            foreach (var track in backpressureAudio ? new[] { 1u, 2u } : [1u])
                await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, track, 1, -1, 0,
                    """{"ok":true}"""u8.ToArray()), timeout.Token);
            await (backpressureAudio ? media.Audio : media.Video).ReadExactlyAsync(new byte[Wire.HeaderSize], timeout.Token);
            stop.Cancel();
            Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
            await Wire.WriteAsync(stream, Message.Control(MessageType.Error, request.Session,
                new { code = "PROTOCOL", message = "genuine peer failure", fatal = true }), timeout.Token);
            var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token));
            Assert.Contains("PROTOCOL: genuine peer failure", error.Message);
            Assert.True(source.Disposed);
            if (audio != null) { Assert.True(audio.Disposed); Assert.False(audio.Muted); }
        }
        finally
        {
            stop.Cancel();
            timeout.Cancel();
            media.Dispose();
            control.Dispose();
            await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalStopKeepsPendingMediaAliveUntilPeerAcknowledges(bool backpressureAudio)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var stop = new CancellationTokenSource();
        var captureStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ReceiveBufferSize = 4096;
        listener.Start();
        var source = new LargeSource
        {
            BeforeStop = async () =>
            {
                captureStopped.TrySetResult();
                await allowStop.Task.WaitAsync(timeout.Token);
            }
        };
        var audio = backpressureAudio ? new ContinuousAudio() : null;
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(EnableAudio: backpressureAudio), source, _ => { }, stop.Token, audio);
        Task? drain = null;
        Task? acknowledgement = null;
        using var control = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = control.GetStream();
        var request = await Handshake(stream, timeout.Token);
        using var media = await AcceptMedia(listener, stream, request.Session, timeout.Token, backpressureAudio);
        try
        {
            Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Video, timeout.Token)).Type);
            if (audio != null)
                Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(media.Audio, timeout.Token)).Type);
            foreach (var track in backpressureAudio ? new[] { 1u, 2u } : [1u])
                await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, track, 1, -1, 0,
                    """{"ok":true}"""u8.ToArray()), timeout.Token);
            var pending = backpressureAudio ? media.Audio : media.Video;
            var header = new byte[Wire.HeaderSize];
            await pending.ReadExactlyAsync(header, timeout.Token);
            Assert.True(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)) > 0);
            stop.Cancel();
            await captureStopped.Task.WaitAsync(timeout.Token);
            var draining = DrainToEof(pending, timeout.Token);
            drain = draining;
            // Withhold control admission: a strict peer must not see media EOF/truncation first.
            // Hold capture teardown so this check cannot consume the Stop acknowledgement deadline.
            Assert.NotSame(drain, await Task.WhenAny(drain, Task.Delay(300, timeout.Token)));
            // The peer acknowledges on its own scheduler, not after the test continuation resumes.
            acknowledgement = Task.Run(async () =>
            {
                Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
                Assert.NotSame(draining, await Task.WhenAny(draining, Task.Delay(100, timeout.Token)));
                await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, request.Session,
                    new { reason = "user" }), timeout.Token);
            }, timeout.Token);
            allowStop.TrySetResult();
            await acknowledgement;
            await run.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token);
            await drain.WaitAsync(timeout.Token);
            Assert.True(source.Disposed);
            if (audio != null) { Assert.True(audio.Disposed); Assert.False(audio.Muted); }
        }
        finally
        {
            allowStop.TrySetResult();
            stop.Cancel();
            timeout.Cancel();
            media.Dispose();
            control.Dispose();
            await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
            if (drain != null) await Record.ExceptionAsync(() => drain);
            if (acknowledgement != null) await Record.ExceptionAsync(() => acknowledgement);
        }
    }

    private static async Task DrainToEof(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[65536];
        while (await stream.ReadAsync(buffer, token) != 0) { }
    }

    private sealed class ContinuousAudio : IAudioSource
    {
        public bool Muted, Disposed;
        public System.Text.Json.Nodes.JsonNode Configuration => RedrainCast.Protocol.JsonData.Object(
            ("codec", "raw"), ("sampleRate", 48000), ("channels", 2),
            ("csd", RedrainCast.Protocol.JsonData.Value(Array.Empty<string>())), ("sampleFormat", "s16le"));
        public void BeginTransmission() => Muted = true;
        public async IAsyncEnumerable<AudioPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken token)
        {
            var data = new byte[5760 * 4];
            for (long pts = 0; ; pts += 120000)
            {
                token.ThrowIfCancellationRequested();
                yield return new(data, pts);
                await Task.Yield();
            }
        }
        public ValueTask DisposeAsync() { Disposed = true; Muted = false; return ValueTask.CompletedTask; }
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
