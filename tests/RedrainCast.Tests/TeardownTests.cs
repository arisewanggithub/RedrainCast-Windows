using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ScrcpySend.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class TeardownTests
{
    private sealed class Source(bool recovery) : IRecoverableVideoSource
    {
        private readonly JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-h264.json")));
        public readonly TaskCompletionSource Fail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public bool SupportsStreamRecovery => recovery;
        public string EncoderName => "Synthetic teardown test source";
        private JsonElement Generation => fixture.RootElement.GetProperty("generations")[0];
        public int Width => Generation.GetProperty("width").GetInt32();
        public int Height => Generation.GetProperty("height").GetInt32();
        public ValueTask RequestKeyFrameAsync(CancellationToken token) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            var frame = Generation.GetProperty("frames")[0];
            yield return new(Convert.FromBase64String(frame.GetProperty("data").GetString()!),
                (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency),
                Convert.FromBase64String(frame.GetProperty("configuration").GetString()!));
            await Fail.Task.WaitAsync(token);
            throw new IOException("Original capture failure");
        }
        public ValueTask DisposeAsync() { Disposed = true; fixture.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Peer : IDisposable
    {
        public readonly TcpClient Control, Video, Audio;
        public readonly long Session;
        public NetworkStream Stream => Control.GetStream();
        private Peer(TcpClient control, TcpClient video, TcpClient audio, long session) =>
            (Control, Video, Audio, Session) = (control, video, audio, session);

        public static async Task<Peer> Connect(TcpListener listener, bool recovery, CancellationToken token)
        {
            var control = await listener.AcceptTcpClientAsync(token);
            var stream = control.GetStream();
            Assert.Equal(MessageType.Hello, (await Wire.ReadAsync(stream, token)).Type);
            await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0,
                new { role = "receiver", versions = new[] { 3 }, name = "Test",
                    streamRecovery = recovery, streamRecoveryVersion = 1 }), token);
            var request = await Wire.ReadAsync(stream, token);
            Assert.Equal(MessageType.SessionRequest, request.Type);
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
                new { tracks = new[] { 1 }, mediaToken = new string('A', 64) }), token);
            var video = await listener.AcceptTcpClientAsync(token);
            await Attach(video, "video");
            var audio = await listener.AcceptTcpClientAsync(token);
            await Attach(audio, "audio");
            Assert.Equal(MessageType.StreamConfig, (await Wire.ReadAsync(video.GetStream(), token)).Type);
            await Wire.WriteAsync(stream, new(MessageType.StreamReady, request.Session, 1, 1, -1, 0,
                """{"ok":true}"""u8.ToArray()), token);
            if (!recovery)
                Assert.Equal(MessageType.VideoFrame, (await Wire.ReadAsync(video.GetStream(), token)).Type);
            return new(control, video, audio, request.Session);

            async Task Attach(TcpClient socket, string channel)
            {
                Assert.Equal(MessageType.Hello, (await Wire.ReadAsync(socket.GetStream(), token)).Type);
                await Wire.WriteAsync(socket.GetStream(), Message.Control(MessageType.Hello, request.Session,
                    new { channel }), token);
            }
        }

        public void Eof() => Video.Client.Shutdown(SocketShutdown.Send);
        public Task Error(string code, string message, bool fatal, CancellationToken token, long? session = null) =>
            Wire.WriteAsync(Stream, Message.Control(MessageType.Error, session ?? Session, new { code, message, fatal }), token).AsTask();
        public void Dispose() { Video.Dispose(); Audio.Dispose(); Control.Dispose(); }
    }

    [Theory]
    [InlineData(false, "Exact bounded receiver failure")]
    [InlineData(true, "Exact bounded receiver failure")]
    [InlineData(false, "First receiver line\r\nSecond\tline\nFinal")]
    [InlineData(true, "First receiver line\r\nSecond\tline\nFinal")]
    public async Task MediaEofPreservesFatalErrorArriving350MsLater(bool recovery, string detail)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source(recovery);
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(MaxSize: source.Width), source, _ => { }, default);
        using var peer = await Peer.Connect(listener, recovery, timeout.Token);
        var watch = Stopwatch.StartNew();
        peer.Eof();
        await Task.Delay(350, timeout.Token);
        Assert.False(run.IsCompleted);
        await peer.Error("DECODER_FAILED", detail, true, timeout.Token);
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.Contains($"DECODER_FAILED: {detail}", error.Message);
        Exception original = error;
        while (original.InnerException is { } inner) original = inner;
        Assert.Equal($"DECODER_FAILED: {detail}", original.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(source.Disposed);
    }

    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "wrong-session")]
    [InlineData(true, "wrong-session")]
    [InlineData(false, "nonfatal")]
    [InlineData(true, "nonfatal")]
    [InlineData(false, "oversized")]
    [InlineData(true, "oversized")]
    [InlineData(false, "wrong-type")]
    [InlineData(true, "wrong-type")]
    public async Task MalformedControlPreservesItsProtocolFailureWhileNonfatalWarningsCannotReplaceMediaEof(bool recovery, string invalid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source(recovery);
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(MaxSize: source.Width), source, _ => { }, default);
        using var peer = await Peer.Connect(listener, recovery, timeout.Token);
        var watch = Stopwatch.StartNew();
        peer.Eof();
        await Task.Delay(350, timeout.Token);
        if (invalid == "wrong-type")
            await Wire.WriteAsync(peer.Stream, Message.Control(MessageType.Error, peer.Session,
                new { code = "DECODER_FAILED", message = "Do not replace", fatal = "true" }), timeout.Token);
        else
            await peer.Error(invalid == "unknown" ? "UNRECOGNIZED" : "DECODER_FAILED",
                invalid == "oversized" ? new string('x', 2049) : "Do not replace", invalid != "nonfatal",
                timeout.Token, invalid == "wrong-session" ? peer.Session + 1 : null);
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.Contains(invalid switch
        {
            "unknown" => "Unknown receiver error code.",
            "wrong-session" => recovery ? "Recovery control session changed." : "Receiver changed the session.",
            "oversized" => "message is too long.",
            "wrong-type" => "Missing or invalid fatal.",
            _ => "closed"
        }, error.Message);
        if (invalid != "nonfatal")
        {
            Exception original = error;
            while (original.InnerException is { } inner) original = inner;
            Assert.IsType<ProtocolException>(original);
        }
        Assert.DoesNotContain("Do not replace", error.Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteStopAfterMediaEofIsNormalAndKeepsControlFraming(bool recovery)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source(recovery);
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(MaxSize: source.Width), source, _ => { }, default);
        using var peer = await Peer.Connect(listener, recovery, timeout.Token);
        peer.Eof();
        await Task.Delay(350, timeout.Token);
        await Wire.WriteAsync(peer.Stream, Message.Control(MessageType.Ping, peer.Session, new { nonce = 42 }), timeout.Token);
        await Wire.WriteAsync(peer.Stream, Message.Control(MessageType.Stop, peer.Session, new { reason = "normal remote stop" }), timeout.Token);
        Message response;
        do
        {
            response = await Wire.ReadAsync(peer.Stream, timeout.Token);
            Assert.True(response.Type is MessageType.Pong or MessageType.SessionEnd);
        } while (response.Type != MessageType.SessionEnd);
        await run.WaitAsync(timeout.Token);
        Assert.True(source.Disposed);
        Assert.Equal(0, await peer.Audio.GetStream().ReadAsync(new byte[1], timeout.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureFailurePrecedesAndSurvivesLatePeerFatal(bool recovery)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source(recovery);
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(MaxSize: source.Width), source, _ => { }, default);
        using var peer = await Peer.Connect(listener, recovery, timeout.Token);
        source.Fail.TrySetResult();
        var response = await Wire.ReadAsync(peer.Stream, timeout.Token);
        Assert.Equal(MessageType.Error, response.Type);
        Assert.Contains("Original capture failure", response.Json().GetProperty("message").GetString());
        await Task.Delay(350, timeout.Token);
        try { await peer.Error("DECODER_FAILED", "Late receiver failure", true, timeout.Token); }
        catch (IOException) { }
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.Contains("Original capture failure", error.Message);
        Assert.DoesNotContain("Late receiver", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentPeerCannotExtendTheTwoSecondMediaEofGrace(bool recovery)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source(recovery);
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(MaxSize: source.Width), source, _ => { }, default);
        using var peer = await Peer.Connect(listener, recovery, timeout.Token);
        var watch = Stopwatch.StartNew();
        peer.Eof();
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.8, 3);
    }
}
