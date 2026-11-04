using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Runtime.CompilerServices;
using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class SrtTests
{
    private sealed class Video : IVideoSource
    {
        public string EncoderName => "Test hardware";
        public int Width => 640;
        public int Height => 360;
        public bool Started, Disposed;
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            Started = true;
            await Task.Delay(Timeout.Infinite, token);
            yield break;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Media : ISrtMedia
    {
        public bool Prepared, Started, Aborted, Disposed;
        public TaskCompletionSource StartSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReturnOnCancellation;
        public SrtDescriptor Prepare(IPAddress local, IPAddress peer, long session)
        {
            Prepared = true;
            Assert.Equal(IPAddress.Loopback, local);
            Assert.Equal(IPAddress.Loopback, peer);
            return new($"srt://{local}:43517", $"sc-{session:x16}");
        }
        public async Task RunAsync(IVideoSource video, IAudioSource? audio, CaptureSettings settings, CancellationToken token)
        {
            Started = true;
            StartSignal.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (ReturnOnCancellation && token.IsCancellationRequested) { }
        }
        public void Abort() => Aborted = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

        private sealed class Audio : IAudioSource
        {
            public bool Started, Disposed;
            public object Configuration => new { codec = "raw", sampleRate = 48000, channels = 2, csd = Array.Empty<string>(), sampleFormat = "s16le" };
            public void BeginTransmission() => Started = true;
            public async IAsyncEnumerable<AudioPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken token)
            {
                await Task.Delay(Timeout.Infinite, token);
                yield break;
            }
            public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        }

        [Fact]
        public async Task LatePlayingStatusAfterStopSkipsMuteButStillConsumesSessionEnd()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var stop = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var video = new Video(); var audio = new Audio(); var media = new Media();
            var streaming = false;
            var cast = new SenderSession().RunAsync(new(IPAddress.Loopback,
                ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false), new(EnableAudio: true, MediaTransport: "srt"),
                video, state => { if (state == "Streaming (SRT/fMP4)") streaming = true; }, stop.Token, audio, media);
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            var request = await Handshake(stream, timeout.Token);
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
                new { tracks = new[] { 1, 2 }, mediaTransport = new { kind = "srt" } }), timeout.Token);
            await media.StartSignal.Task.WaitAsync(timeout.Token);
            stop.Cancel();
            Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
            await Wire.WriteAsync(stream, Message.Control(MessageType.Status, request.Session,
                new { state = "srt-playing", message = "Late renderer callback" }), timeout.Token);
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, request.Session, new { reason = "user" }), timeout.Token);
            await cast.WaitAsync(timeout.Token);
            Assert.False(audio.Started);
            Assert.False(streaming);
            Assert.True(audio.Disposed);
            Assert.True(media.Disposed);
            Assert.True(video.Disposed);
        }
    [Fact]
    public void SrtRequestHasExactlyThePairedDescriptorAndUsesExplicitAacInsteadOfPcm()
    {
        var settings = new CaptureSettings(EnableAudio: true, MediaTransport: "srt");
        var json = JsonSerializer.SerializeToElement(settings.Request("Hardware", new("srt://192.168.1.25:43517", "sc-0000000000000001")));
        var descriptor = json.GetProperty("mediaTransport");
        Assert.Equal(5, descriptor.EnumerateObject().Count());
        Assert.Equal("srt", descriptor.GetProperty("kind").GetString());
        Assert.Equal("fmp4", descriptor.GetProperty("container").GetString());
        Assert.Equal(100, descriptor.GetProperty("latencyMs").GetInt32());
        Assert.Equal("aac", json.GetProperty("audio").GetProperty("codec").GetString());
        Assert.Equal(192000, json.GetProperty("audio").GetProperty("bitrate").GetInt32());
        Assert.Throws<ArgumentException>(() => settings.Request("Hardware"));
        Assert.Throws<ArgumentException>(() => new CaptureSettings().Request("Hardware", new("srt://127.0.0.1:1", "sc-1")));
    }

    private static async Task<Message> Handshake(NetworkStream stream, CancellationToken token)
    {
        Assert.Equal(MessageType.Hello, (await Wire.ReadAsync(stream, token)).Type);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0,
            new { role = "receiver", versions = new[] { 3 }, name = "Test receiver" }), token);
        var request = await Wire.ReadAsync(stream, token);
        Assert.Equal(MessageType.SessionRequest, request.Type);
        Assert.Equal($"sc-{request.Session:x16}", request.Json().GetProperty("mediaTransport").GetProperty("streamId").GetString());
        return request;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompatibleSrtAcceptanceNeverFallsBackOrStartsCapture(bool explicitWrongKind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var video = new Video(); var media = new Media();
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(MediaTransport: "srt"), video, _ => { }, default, srt: media);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var stream = peer.GetStream();
        var request = await Handshake(stream, deadline.Token);
        Assert.True(media.Prepared);
        Assert.False(media.Started);
        object accept = explicitWrongKind ? new { tracks = new[] { 1 }, mediaTransport = new { kind = "tcp" } } : new { tracks = new[] { 1 } };
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session, accept), deadline.Token);
        await Assert.ThrowsAsync<IOException>(() => cast.WaitAsync(deadline.Token));
        Assert.False(video.Started);
        Assert.False(media.Started);
        Assert.True(media.Disposed);
        Assert.True(video.Disposed);
    }

    [Fact]
    public async Task SrtUsesOnlyControlFramesAndStillAcknowledgesStop()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var video = new Video(); var media = new Media();
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(MediaTransport: "srt"), video, state => { if (state == "Streaming (SRT/fMP4)") playing.SetResult(); }, stop.Token, srt: media);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        var stream = peer.GetStream();
        var request = await Handshake(stream, deadline.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
            new { tracks = new[] { 1 }, mediaTransport = new { kind = "srt" } }), deadline.Token);
        await media.StartSignal.Task.WaitAsync(deadline.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Status, request.Session,
            new { state = "srt-playing", message = "Real renderer ready" }), deadline.Token);
        await playing.Task.WaitAsync(deadline.Token);
        Assert.True(media.Started);
        Assert.False(video.Started);
        Assert.False(stream.DataAvailable);
        stop.Cancel();
        Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, deadline.Token)).Type);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, request.Session, new { reason = "user" }), deadline.Token);
        await cast.WaitAsync(deadline.Token);
        Assert.True(media.Aborted);
        Assert.True(media.Disposed);
    }

    [Theory]
    [InlineData("1190")]
    [InlineData("119056E500")]
    public void AacCoreUsesActualMetadataAndOnlyStripsTheInactiveSbrDeclaration(string hex) =>
        Assert.Equal(Convert.FromHexString("1190"), AacConfiguration.CanonicalCore(Convert.FromHexString(hex)));

    [Theory]
    [InlineData("1210")]
    [InlineData("119156E500")]
    [InlineData("119056E580")]
    public void UnsupportedAudioConfigurationsFailRatherThanInventingCsd(string hex) =>
        Assert.Throws<ProtocolException>(() => AacConfiguration.CanonicalCore(Convert.FromHexString(hex)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalSrtCancellationCompletionDoesNotSwallowPeerOrShutdownErrors(bool missingAcknowledgement)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var video = new Video();
        var media = new Media { ReturnOnCancellation = true };
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false), new(MediaTransport: "srt"),
            video, _ => { }, stop.Token, srt: media);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        var request = await Handshake(stream, timeout.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
            new { tracks = new[] { 1 }, mediaTransport = new { kind = "srt" } }), timeout.Token);
        await media.StartSignal.Task.WaitAsync(timeout.Token);
        if (missingAcknowledgement)
        {
            stop.Cancel();
            Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
        }
        else
        {
            await Wire.WriteAsync(stream, Message.Control(MessageType.Error, request.Session,
                new { code = "DECODER_FAILED", message = "Receiver decoder failed", fatal = true }), timeout.Token);
        }
        var error = await Assert.ThrowsAsync<IOException>(() => cast.WaitAsync(timeout.Token));
        Assert.Contains(missingAcknowledgement ? "Stop acknowledgement failed" : "DECODER_FAILED", error.Message);
        Assert.True(media.Disposed);
        Assert.True(video.Disposed);
    }

    [Fact]
    public async Task NormalSrtCancellationCompletionStillSucceedsWithAcknowledgedUserStop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var media = new Media { ReturnOnCancellation = true };
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false), new(MediaTransport: "srt"),
            new Video(), _ => { }, stop.Token, srt: media);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        var request = await Handshake(stream, timeout.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
            new { tracks = new[] { 1 }, mediaTransport = new { kind = "srt" } }), timeout.Token);
        await media.StartSignal.Task.WaitAsync(timeout.Token);
        stop.Cancel();
        Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, request.Session, new { reason = "user" }), timeout.Token);
        await cast.WaitAsync(timeout.Token);
        Assert.True(media.Disposed);
    }
}
