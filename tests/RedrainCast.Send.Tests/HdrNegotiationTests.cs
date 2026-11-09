using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RedrainCast.Protocol;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class HdrNegotiationTests
{
    private sealed class Source : IHdrVideoSource, IRecoverableVideoSource
    {
        public string EncoderName => "HDR fixture";
        public int Width => 16;
        public int Height => 16;
        public bool NativeHdrEligible { get; init; } = true;
        public Task? PreparationDelay { get; init; }
        public Func<CancellationToken, Task>? Preparation { get; init; }
        public TaskCompletionSource PreparationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PreparationRetired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConfigureCalls, PrepareCalls;
        public bool SupportsStreamRecovery { get; init; }
        public ValueTask RequestKeyFrameAsync(CancellationToken token) => ValueTask.CompletedTask;
        public HdrVideoFormat ColorFormat { get; private set; } = HdrVideoFormat.Sdr;
        public bool Configured, Prepared, Started, Disposed;
        public ValueTask ConfigureHdrAsync(bool enabled, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.False(Prepared);
            Assert.Equal(1, ++ConfigureCalls);
            Configured = true;
            ColorFormat = enabled ? new(true, new(1000, 50, 1000, 400)) : HdrVideoFormat.Sdr;
            return ValueTask.CompletedTask;
        }
        public async ValueTask PrepareAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.True(Configured);
            Assert.Equal(1, ++PrepareCalls);
            Prepared = true;
            PreparationStarted.TrySetResult();
            try
            {
                if (PreparationDelay is not null) await PreparationDelay.WaitAsync(token);
                if (Preparation is not null) await Preparation(token);
            }
            finally { PreparationRetired.TrySetResult(); }
        }
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            Started = true;
            CaptureStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            yield break;
        }
        public ValueTask DisposeAsync()
        {
            if (Prepared) Assert.True(PreparationRetired.Task.IsCompleted);
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static JsonObject Hello() => JsonData.Object(("role", "receiver"),
        ("versions", JsonData.Value(new[] { 3 })), ("name", "Fixture peer"));

    private static async Task ReplyHello(NetworkStream stream, JsonObject response, CancellationToken token)
    {
        var hello = await Wire.ReadAsync(stream, token);
        Assert.Equal(MessageType.Hello, hello.Type);
        Assert.Equal(0, hello.Session);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0, response), token);
    }

    [Fact]
    public async Task NonHdrEligibleSourcesRetainPreparationBeforeConnecting()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { NativeHdrEligible = false, PreparationDelay = prepared.Task };
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "HDR peer", false),
            new(MaxFps: 60, VideoCodec: "h265"), source, _ => { }, timeout.Token);
        Assert.True(source.Configured);
        Assert.True(source.Prepared);
        Assert.False(listener.Pending());
        prepared.SetResult();
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        await Wire.ReadAsync(stream, timeout.Token);
        var response = JsonData.Object(("role", "receiver"), ("versions", JsonData.Value(new[] { 3 })), ("name", "HDR peer"));
        response["receiveCapabilities"] = new HdrReceiverCapabilities(true, true, true).WireValue;
        await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0, response), timeout.Token);
        var request = await Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(HdrVideoFormat.Sdr, HdrVideoProtocol.Format(request.Json()));
        await Wire.WriteAsync(stream, Message.Control(MessageType.Reject, request.Session,
            JsonData.Object(("code", "BUSY"), ("message", "Fixture rejection"))), timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.True(source.Disposed);
        Assert.False(source.Started);
    }

    [Theory]
    [InlineData(false, false, false, false, 60)]
    [InlineData(true, false, true, false, 60)]
    [InlineData(false, true, true, false, 60)]
    [InlineData(false, false, false, true, 60)]
    [InlineData(true, false, true, true, 60)]
    [InlineData(false, true, true, true, 60)]
    [InlineData(false, false, false, false, 0)]
    [InlineData(true, false, true, false, 0)]
    [InlineData(false, true, true, false, 0)]
    [InlineData(false, false, false, true, 0)]
    [InlineData(true, false, true, true, 0)]
    [InlineData(false, true, true, true, 0)]
    public async Task HdrModeIsChosenFromHelloBeforePreparingCapture(bool native, bool conversion, bool expectedHdr, bool recovery, int fps)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source { SupportsStreamRecovery = recovery };
        var notices = new List<string>();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "HDR peer", false),
            new(MaxFps: fps, VideoCodec: "h265"), source, notices.Add, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        var hello = await Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(1, hello.Json().GetProperty("hdrVideoVersion").GetInt32());
        Assert.True(StrictJson.Boolean(hello.Json(), "uncappedFps"));
        Assert.False(source.Prepared);
        var response = JsonData.Object(("role", "receiver"), ("versions", JsonData.Value(new[] { 3 })), ("name", "HDR peer"));
        response["receiveCapabilities"] = new HdrReceiverCapabilities(true, native, conversion).WireValue;
        response["uncappedFps"] = true;
        if (recovery)
        {
            response["streamRecovery"] = true;
            response["streamRecoveryVersion"] = 2;
        }
        await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0, response), timeout.Token);
        var request = await Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(MessageType.SessionRequest, request.Type);
        Assert.Equal(fps, request.Json().GetProperty("video").GetProperty("maxFps").GetInt32());
        Assert.Equal(expectedHdr, HdrVideoProtocol.Format(request.Json()).Hdr10);
        Assert.Equal(source.ColorFormat, HdrVideoProtocol.Format(request.Json()));
        Assert.True(source.Prepared);
        Assert.False(source.Started);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Reject, request.Session,
            JsonData.Object(("code", "BUSY"), ("message", "Fixture rejection"))), timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.True(source.Disposed);
        Assert.False(source.Started);
    }

    [Fact]
    public async Task LegacyHelloKeepsSdrEvenWhenDiscoveryAdvertisedHdr()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Legacy peer", false,
            Capabilities: new(true, true, true)), new(MaxFps: 60, VideoCodec: "h265"), source, _ => { }, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        await Wire.ReadAsync(stream, timeout.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0,
            JsonData.Object(("role", "receiver"), ("versions", JsonData.Value(new[] { 3 })), ("name", "Legacy peer"))), timeout.Token);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync(stream, timeout.Token).AsTask());
        using var finalPeer = await listener.AcceptTcpClientAsync(timeout.Token);
        var finalStream = finalPeer.GetStream();
        await ReplyHello(finalStream, Hello(), timeout.Token);
        var request = await Wire.ReadAsync(finalStream, timeout.Token);
        Assert.Equal(HdrVideoFormat.Sdr, HdrVideoProtocol.Format(request.Json()));
        await Wire.WriteAsync(finalStream, Message.Control(MessageType.Reject, request.Session,
            JsonData.Object(("code", "BUSY"), ("message", "Fixture rejection"))), timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.True(source.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwelveSecondPreparationRespectsLegacyTenSecondSetupAndModernExtendedSetup(bool modern)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var elapsed = new Stopwatch();
        var source = new Source
        {
            Preparation = async token =>
            {
                elapsed.Start();
                var minimumPreparation = TimeSpan.FromSeconds(12);
                while (elapsed.Elapsed < minimumPreparation)
                {
                    var remaining = minimumPreparation - elapsed.Elapsed;
                    await Task.Delay(remaining > TimeSpan.FromMilliseconds(1)
                        ? remaining : TimeSpan.FromMilliseconds(1), token);
                }
                elapsed.Stop();
            }
        };
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Manual peer", false),
            new(MaxFps: 60, VideoCodec: "h265"), source, _ => { }, stop.Token);
        using var preflight = await listener.AcceptTcpClientAsync(timeout.Token);
        using var firstSetup = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        firstSetup.CancelAfter(TimeSpan.FromSeconds(modern ? 45 : 10));
        var response = Hello();
        if (modern) response["receiveCapabilities"] = new HdrReceiverCapabilities(true, true, true).WireValue;
        await ReplyHello(preflight.GetStream(), response, firstSetup.Token);
        TcpClient? reconnect = null;
        try
        {
            NetworkStream stream;
            using var finalSetup = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            if (modern) stream = preflight.GetStream();
            else
            {
                // A real legacy peer expires the whole initial setup at ten seconds, not just each read.
                // Closing without any SESSION_REQUEST is required before twelve-second preparation finishes.
                await Assert.ThrowsAsync<EndOfStreamException>(() =>
                    Wire.ReadAsync(preflight.GetStream(), firstSetup.Token).AsTask());
                await source.PreparationStarted.Task.WaitAsync(timeout.Token);
                Assert.False(source.PreparationRetired.Task.IsCompleted);
                Assert.False(source.Started);
                reconnect = await listener.AcceptTcpClientAsync(timeout.Token);
                finalSetup.CancelAfter(TimeSpan.FromSeconds(10));
                stream = reconnect.GetStream();
                await ReplyHello(stream, Hello(), finalSetup.Token);
            }
            var request = await Wire.ReadAsync(stream, modern ? firstSetup.Token : finalSetup.Token);
            Assert.Equal(MessageType.SessionRequest, request.Type);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(12), $"Actual preparation was {elapsed.Elapsed}.");
            Assert.Equal(modern, HdrVideoProtocol.Format(request.Json()).Hdr10);
            if (!modern) Assert.False(request.Json().TryGetProperty("videoColor", out _));
            Assert.False(source.Started);
            var acceptance = JsonData.Object(("tracks", JsonData.Value(new[] { 1 })), ("mediaToken", new string('A', 64)));
            if (modern) HdrVideoProtocol.AddFormat(acceptance, source.ColorFormat);
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session, acceptance), timeout.Token);
            using var video = await AcceptMedia("video");
            using var audio = await AcceptMedia("audio");
            await source.CaptureStarted.Task.WaitAsync(timeout.Token);
            stop.Cancel();
            Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(stream, timeout.Token)).Type);
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, request.Session,
                new { reason = "user" }), timeout.Token);
            await run.WaitAsync(timeout.Token);
            Assert.False(listener.Pending());
            Assert.True(source.Disposed);
            Assert.Equal(1, source.ConfigureCalls);
            Assert.Equal(1, source.PrepareCalls);

            async Task<TcpClient> AcceptMedia(string channel)
            {
                var media = await listener.AcceptTcpClientAsync(timeout.Token);
                try
                {
                    var hello = await Wire.ReadAsync(media.GetStream(), timeout.Token);
                    Assert.Equal("media", hello.Json().GetProperty("role").GetString());
                    Assert.Equal(request.Session, hello.Session);
                    Assert.Equal(channel, hello.Json().GetProperty("channel").GetString());
                    await Wire.WriteAsync(media.GetStream(), Message.Control(MessageType.Hello, request.Session,
                        new { channel }), timeout.Token);
                    return media;
                }
                catch { media.Dispose(); throw; }
            }
        }
        finally
        {
            stop.Cancel();
            reconnect?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedLegacyPreparationClosesPreflightAndRetiresResourcesWithoutReconnecting(bool fail)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Preparation = token => preparation.Task.WaitAsync(token) };
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Legacy peer", false),
            new(MaxFps: 60, VideoCodec: "h265"), source, _ => { }, stop.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        await ReplyHello(peer.GetStream(), Hello(), timeout.Token);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync(peer.GetStream(), timeout.Token).AsTask());
        await source.PreparationStarted.Task.WaitAsync(timeout.Token);
        if (fail)
        {
            preparation.SetException(new IOException("Fixture preparation failed"));
            var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
            Assert.Contains("Fixture preparation failed", error.Message);
        }
        else
        {
            stop.Cancel();
            await run.WaitAsync(timeout.Token);
        }
        Assert.True(source.PreparationRetired.Task.IsCompletedSuccessfully);
        Assert.True(source.Disposed);
        Assert.False(source.Started);
        Assert.False(listener.Pending());
        Assert.Equal(1, source.ConfigureCalls);
        Assert.Equal(1, source.PrepareCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyPreflightRenegotiatesRecoveryAndOptionsWithoutUpgradingPreparedSdr(bool finalRecovery, bool finalHdr)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source { SupportsStreamRecovery = true };
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Manual peer", false),
            new(MaxFps: 60, VideoCodec: "h265", ShowPlaybackStatistics: true), source, _ => { }, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var response = Hello();
        response["streamRecovery"] = !finalRecovery;
        response["streamRecoveryVersion"] = 2;
        if (!finalRecovery) response["receiverOptions"] = ReceiverOptionCapabilities.Implemented.WireValue;
        await ReplyHello(peer.GetStream(), response, timeout.Token);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync(peer.GetStream(), timeout.Token).AsTask());
        using var finalPeer = await listener.AcceptTcpClientAsync(timeout.Token);
        response = Hello();
        response["streamRecovery"] = finalRecovery;
        response["streamRecoveryVersion"] = 2;
        if (finalRecovery) response["receiverOptions"] = ReceiverOptionCapabilities.Implemented.WireValue;
        if (finalHdr) response["receiveCapabilities"] = new HdrReceiverCapabilities(true, true, true).WireValue;
        await ReplyHello(finalPeer.GetStream(), response, timeout.Token);
        var request = await Wire.ReadAsync(finalPeer.GetStream(), timeout.Token);
        Assert.Equal(MessageType.SessionRequest, request.Type);
        Assert.Equal(HdrVideoFormat.Sdr, HdrVideoProtocol.Format(request.Json()));
        Assert.Equal(finalRecovery, request.Json().TryGetProperty("receiverOptions", out _));
        if (finalRecovery) Assert.True(request.Json().GetProperty("receiverOptions").GetProperty("playbackStats").GetBoolean());
        await Wire.WriteAsync(finalPeer.GetStream(), Message.Control(MessageType.Reject, request.Session,
            new { code = "BUSY", message = "Final rejection" }), timeout.Token);
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.Equal(finalRecovery, error.Message.Contains("Receiver rejected recovery session", StringComparison.Ordinal));
        Assert.True(source.Disposed);
        Assert.False(source.Started);
        Assert.Equal(1, source.ConfigureCalls);
        Assert.Equal(1, source.PrepareCalls);
    }

    [Fact]
    public async Task UnknownHdrCapabilityRevisionUsesLegacyPreflightInsteadOfExtendedSetup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { PreparationDelay = prepared.Task };
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Future peer", false),
            new(MaxFps: 60, VideoCodec: "h265"), source, _ => { }, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var response = Hello();
        response["receiveCapabilities"] = new HdrReceiverCapabilities(true, true, true).WireValue;
        response["receiveCapabilities"]!["version"] = 2;
        await ReplyHello(peer.GetStream(), response, timeout.Token);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync(peer.GetStream(), timeout.Token).AsTask());
        await source.PreparationStarted.Task.WaitAsync(timeout.Token);
        Assert.False(listener.Pending());
        prepared.SetResult();
        using var finalPeer = await listener.AcceptTcpClientAsync(timeout.Token);
        await ReplyHello(finalPeer.GetStream(), Hello(), timeout.Token);
        var request = await Wire.ReadAsync(finalPeer.GetStream(), timeout.Token);
        Assert.Equal(HdrVideoFormat.Sdr, HdrVideoProtocol.Format(request.Json()));
        await Wire.WriteAsync(finalPeer.GetStream(), Message.Control(MessageType.Reject, request.Session,
            new { code = "BUSY", message = "Future rejection" }), timeout.Token);
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.True(source.Disposed);
        Assert.False(source.Started);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"videoModes":[],"hdrToSdr":"true"}""")]
    public async Task MissingOrMalformedHdrCapabilityPayloadNeverAcknowledgesExtendedSetup(string payload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Invalid peer", false),
            new(MaxFps: 60, VideoCodec: "h265"), source, _ => { }, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var response = Hello();
        response["receiveCapabilities"] = JsonNode.Parse(payload);
        await ReplyHello(peer.GetStream(), response, timeout.Token);
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.IsType<ProtocolException>(error.InnerException);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync(peer.GetStream(), timeout.Token).AsTask());
        Assert.False(source.Configured);
        Assert.False(source.Prepared);
        Assert.False(source.Started);
        Assert.True(source.Disposed);
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyHelloOnPreflightOrFinalConnectionNeverRequestsSessionOrStartsCapture(bool finalConnection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Busy peer", false),
            new(MaxFps: 60, VideoCodec: "h265"), source, _ => { }, timeout.Token);
        using var preflight = await listener.AcceptTcpClientAsync(timeout.Token);
        TcpClient? finalPeer = null;
        try
        {
            var stream = preflight.GetStream();
            if (finalConnection)
            {
                await ReplyHello(stream, Hello(), timeout.Token);
                await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync(stream, timeout.Token).AsTask());
                finalPeer = await listener.AcceptTcpClientAsync(timeout.Token);
                stream = finalPeer.GetStream();
            }
            Assert.Equal(MessageType.Hello, (await Wire.ReadAsync(stream, timeout.Token)).Type);
            await Wire.WriteAsync(stream, Message.Control(MessageType.Reject, 0,
                new { code = "BUSY", message = "Fixture busy" }), timeout.Token);
            var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
            Assert.Contains("BUSY: Fixture busy", error.Message);
            await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync(stream, timeout.Token).AsTask());
            Assert.Equal(finalConnection ? 1 : 0, source.ConfigureCalls);
            Assert.Equal(finalConnection ? 1 : 0, source.PrepareCalls);
            Assert.False(source.Started);
            Assert.True(source.Disposed);
            Assert.False(listener.Pending());
        }
        finally { finalPeer?.Dispose(); }
    }

    [Fact]
    public async Task MissingAcceptanceAcknowledgementNeverStartsHdrCapture()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "HDR peer", false),
            new(MaxFps: 60, VideoCodec: "h265"), source, _ => { }, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = peer.GetStream();
        await Wire.ReadAsync(stream, timeout.Token);
        var response = JsonData.Object(("role", "receiver"), ("versions", JsonData.Value(new[] { 3 })), ("name", "HDR peer"));
        response["receiveCapabilities"] = new HdrReceiverCapabilities(true, true, true).WireValue;
        await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0, response), timeout.Token);
        var request = await Wire.ReadAsync(stream, timeout.Token);
        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
            JsonData.Object(("tracks", JsonData.Value(new[] { 1 })), ("mediaToken", new string('A', 64)))), timeout.Token);
        var failure = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(timeout.Token));
        Assert.IsType<ProtocolException>(failure.InnerException);
        Assert.False(source.Started);
        Assert.True(source.Disposed);
    }
}
