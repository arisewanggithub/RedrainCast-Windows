using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class ListenerTests
{
    [Fact]
    public async Task IdleDiagnosticsDoNotInventAnActiveSession()
    {
        await using var server = Server();
        server.Start(false);
        Assert.Equal("session=none", server.Diagnostics);
    }

    private static ReceiverServer Server(IPlaybackFactory? factory = null) =>
        new("Test receiver", factory ?? new UnavailablePlaybackFactory(), _ => { }, 0, IPAddress.Loopback);

    [Fact]
    public async Task QueuedMediaDoesNotBlockThreeTcpStop()
    {
        var presenting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new TestFactory();
        factory.Playback.OnPresent = async (_, token) =>
        {
            presenting.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        await using var server = new ReceiverServer("Synthetic test", factory,
            _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(peer)).Type);
        await SendAsync(peer, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(peer)).Type);
        await SendAsync(peer, QueueAndTrackTests.Media(100));
        await presenting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await SendAsync(peer, QueueAndTrackTests.Media(200));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.Statistics!.Video.Packets < 2) await Task.Delay(5, timeout.Token);
        Assert.Empty(factory.Playback.Presented);
        await SendAsync(peer, Packet.Control(PacketType.Stop, 1, new { reason = "test complete" }));
        Assert.Equal(PacketType.SessionEnd, (await ReadAsync(peer)).Type);
        while (server.IsBusy) await Task.Delay(5, timeout.Token);
    }

    private sealed class TestClient : TcpClient
    {
        public TcpClient Video { get; } = new() { NoDelay = true };
        public TcpClient Audio { get; } = new() { NoDelay = true };
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Video.Dispose(); Audio.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private static async Task<TestClient> ConnectAsync(ReceiverServer server)
    {
        var client = new TestClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        return client;
    }

    private static async Task<Packet> ReadAsync(TcpClient client)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var packet = await Wire.ReadAsync(client.GetStream(), cancellation.Token);
        if (packet.Type == PacketType.SessionAccept && client is TestClient peer)
        {
            var endpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
            var token = JsonObject.Text(packet.Object(), "mediaToken", 64);
            foreach (var (socket, channel) in new[] { (peer.Video, "video"), (peer.Audio, "audio") })
            {
                await socket.ConnectAsync(endpoint.Address, endpoint.Port, cancellation.Token);
                await Wire.WriteAsync(socket.GetStream(), Packet.Control(PacketType.Hello, packet.Session,
                    new { role = "media", channel, token }), cancellation.Token);
                Assert.Equal(PacketType.Hello, (await Wire.ReadAsync(socket.GetStream(), cancellation.Token)).Type);
            }
        }
        return packet;
    }

    private static Task SendAsync(TcpClient client, Packet packet) =>
        Wire.WriteAsync(client is TestClient peer && packet.Type is
            (PacketType.StreamConfig or PacketType.VideoFrame or PacketType.AudioPacket)
                ? (packet.Track == 1 ? peer.Video : peer.Audio).GetStream() : client.GetStream(), packet).AsTask();

    private static async Task HelloAsync(TcpClient client)
    {
        await SendAsync(client, Packet.Control(PacketType.Hello, 0,
            new { role = "sender", versions = new[] { 3 }, name = "Test sender", sessionCode = "ignored" }));
        Assert.Equal(PacketType.Hello, (await ReadAsync(client)).Type);
    }

    private static async Task RequestAsync(TcpClient client, long session = 1, bool audio = false)
    {
        await SendAsync(client, Packet.Control(PacketType.SessionRequest, session, RequestTests.Settings(audio)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(240)]
    public async Task HighFpsAdmissionStopAndReconnectKeepTheExactRequest(int fps)
    {
        var factory = new TestFactory { FreshAfterFirstOpen = true };
        await using var server = Server(factory);
        server.Start(false);
        for (var session = 1; session <= 2; session++)
        {
            using var peer = await ConnectAsync(server);
            await HelloAsync(peer);
            var settings = System.Text.Json.JsonSerializer.SerializeToNode(RequestTests.Settings(false))!;
            settings["video"]!["maxFps"] = fps;
            await SendAsync(peer, Packet.Control(PacketType.SessionRequest, session, settings));
            Assert.Equal(PacketType.SessionAccept, (await ReadAsync(peer)).Type);
            Assert.Equal(fps, factory.Requests.Last().VideoProcessingFramesPerSecond);
            await SendAsync(peer, Packet.Control(PacketType.Stop, session, new { reason = "high FPS test" }));
            Assert.Equal(PacketType.SessionEnd, (await ReadAsync(peer)).Type);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (server.IsBusy) await Task.Delay(10, timeout.Token);
            Assert.False(server.IsBusy);
        }
        Assert.Equal(2, factory.Opens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncappedSupportIsAdvertisedEvenWithoutAnOffer(bool? offer)
    {
        await using var server = Server();
        server.Start(false);
        using var peer = await ConnectAsync(server);
        var hello = new Dictionary<string, object>
        {
            ["role"] = "sender", ["versions"] = new[] { 3 }, ["name"] = "Android uncapped test"
        };
        if (offer is { } value) hello["uncappedFps"] = value;
        await SendAsync(peer, Packet.Control(PacketType.Hello, 0, hello));
        var response = await ReadAsync(peer);
        Assert.Equal(PacketType.Hello, response.Type);
        Assert.True(JsonObject.Boolean(response.Object(), "uncappedFps"));
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true,\"uncappedFps\":false")]
    public async Task MalformedUncappedOfferIsRejectedBeforeOpeningPlayback(string value)
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var peer = await ConnectAsync(server);
        var payload = System.Text.Encoding.UTF8.GetBytes(
            "{\"role\":\"sender\",\"versions\":[3],\"name\":\"Malformed\",\"uncappedFps\":" + value + "}");
        // Write raw bytes so malformed JSON reaches the receiver rather than the writer's validator.
        var valid = Packet.Control(PacketType.Hello, 0, new { role = "sender", versions = new[] { 3 }, name = "Malformed" });
        using var bytes = new MemoryStream();
        await Wire.WriteAsync(bytes, valid);
        var header = bytes.ToArray()[..Wire.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)payload.Length);
        await peer.GetStream().WriteAsync(header);
        await peer.GetStream().WriteAsync(payload);
        var response = await ReadAsync(peer);
        Assert.Equal(PacketType.Reject, response.Type);
        Assert.Equal("PROTOCOL", JsonObject.Text(response.Object(), "code"));
        Assert.Equal(0, factory.Opens);
    }

    [Theory]
    [InlineData(null, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task RotationRequiresBothPeerOfferAndPlaybackSupport(bool? offer, bool supported, bool expected)
    {
        var factory = new TestFactory { SupportsVideoRotation = supported };
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        var hello = new Dictionary<string, object>
        {
            ["role"] = "sender", ["versions"] = new[] { 3 }, ["name"] = "Android rotation test"
        };
        if (offer is { } offered) hello["videoRotation"] = offered;
        await SendAsync(client, Packet.Control(PacketType.Hello, 0, hello));
        var response = (await ReadAsync(client)).Object();
        Assert.Equal(expected, response.TryGetProperty("videoRotation", out var value) && value.GetBoolean());
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Media(100) with { Flags = 3 });
        if (expected)
        {
            await SendAsync(client, QueueAndTrackTests.Media(100) with { Flags = 6 });
            await factory.Playback.TwoPresented.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 90, 270 }, factory.Playback.Rotations.ToArray());
            Assert.Equal(new[] { 100L, 100L }, factory.Playback.Presented.ToArray());
            await SendAsync(client, Packet.Control(PacketType.Stop, 1, new { reason = "test" }));
            Assert.Equal(PacketType.SessionEnd, (await ReadAsync(client)).Type);
        }
        else
        {
            Assert.Equal(PacketType.Error, (await ReadAsync(client)).Type);
            Assert.Empty(factory.Playback.Presented);
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData(1)]
    public async Task MalformedRotationCapabilityIsRejected(object capability)
    {
        await using var server = Server(new TestFactory { SupportsVideoRotation = true });
        server.Start(false);
        using var client = await ConnectAsync(server);
        await SendAsync(client, Packet.Control(PacketType.Hello, 0,
            new { role = "sender", versions = new[] { 3 }, name = "Test", videoRotation = capability }));
        Assert.Equal(PacketType.Reject, (await ReadAsync(client)).Type);
    }

    [Fact]
    public async Task FoundationRejectsBeforeCaptureWithoutPretendReadiness()
    {
        await using var server = Server();
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        var rejected = await ReadAsync(client);
        Assert.Equal(PacketType.Reject, rejected.Type);
        Assert.Equal(1, rejected.Session);
        Assert.Equal("UNSUPPORTED_CONFIG", JsonObject.Text(rejected.Object(), "code"));
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task TransportOverrideRejectsBeforeOpeningPlayback()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await SendAsync(client, Packet.Control(PacketType.SessionRequest, 1, RequestTests.Settings(false, "aac", new
        {
            kind = "external", url = "udp://127.0.0.1:1234"
        })));
        var rejected = await ReadAsync(client);
        Assert.Equal("UNSUPPORTED_CONFIG", JsonObject.Text(rejected.Object(), "code"));
        Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public async Task BusyDoesNotEvictAndStopAcknowledgesMatchingSession()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var first = await ConnectAsync(server);
        await HelloAsync(first);
        await RequestAsync(first);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(first)).Type);
        Assert.True(server.IsBusy);
        using var other = await ConnectAsync(server);
        await SendAsync(other, Packet.Control(PacketType.Hello, 0,
            new { role = "sender", versions = new[] { 3 }, name = "Other sender" }));
        Assert.Equal("BUSY", JsonObject.Text((await ReadAsync(other)).Object(), "code"));
        await SendAsync(first, Packet.Control(PacketType.Stop, 1, new { reason = "user" }));
        var ended = await ReadAsync(first);
        Assert.Equal(PacketType.SessionEnd, ended.Type);
        Assert.Equal(1, ended.Session);
        Assert.True(factory.Playback.Disposed);
    }

    [Fact]
    public async Task ConcurrentHelloDoesNotAllowTwoActiveSessions()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var first = await ConnectAsync(server);
        using var second = await ConnectAsync(server);
        await HelloAsync(first);
        await HelloAsync(second);
        await RequestAsync(first);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(first)).Type);
        await RequestAsync(second, 2);
        var rejected = await ReadAsync(second);
        Assert.Equal(2, rejected.Session);
        Assert.Equal("BUSY", JsonObject.Text(rejected.Object(), "code"));
        Assert.Equal(1, factory.Opens);
        await SendAsync(first, Packet.Control(PacketType.Stop, 1, new { reason = "test" }));
        Assert.Equal(PacketType.SessionEnd, (await ReadAsync(first)).Type);
    }

    [Fact]
    public async Task ReadyFollowsActualConfigurationAndMediaRemainsOrdered()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client, audio: true);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Configuration());
        var ready = await ReadAsync(client);
        Assert.Equal(PacketType.StreamReady, ready.Type);
        Assert.Contains(1u, factory.Playback.Configured);
        await SendAsync(client, QueueAndTrackTests.Configuration(2));
        Assert.Equal(2u, (await ReadAsync(client)).Track);
        await SendAsync(client, QueueAndTrackTests.Media(100));
        await SendAsync(client, QueueAndTrackTests.Media(200));
        await factory.Playback.TwoPresented.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 100L, 200L }, factory.Playback.Presented.ToArray());
        await SendAsync(client, Packet.Control(PacketType.Stop, 1, new { reason = "done" }));
        Assert.Equal(PacketType.SessionEnd, (await ReadAsync(client)).Type);
    }

    [Fact]
    public async Task PingsEchoSignedNonceAndReceiverStopIsAcknowledged()
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, Packet.Control(PacketType.Ping, 1, new { nonce = int.MinValue }));
        var pong = await ReadAsync(client);
        Assert.Equal(PacketType.Pong, pong.Type);
        Assert.Equal(int.MinValue, JsonObject.Number(pong.Object(), "nonce"));
        var disconnect = server.DisconnectAsync();
        Assert.Equal(PacketType.Stop, (await ReadAsync(client)).Type);
        await SendAsync(client, Packet.Control(PacketType.SessionEnd, 1, new { reason = "user" }));
        await disconnect.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task MediaBeforeConfigurationFailsExplicitly()
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Media(0));
        var error = await ReadAsync(client);
        Assert.Equal(PacketType.Error, error.Type);
        Assert.Equal("PROTOCOL", JsonObject.Text(error.Object(), "code"));
    }

    [Fact]
    public async Task ShutdownCancelsTrickledSetupAndReleasesPort()
    {
        var server = Server();
        server.Start(false);
        var port = server.Port;
        using var client = await ConnectAsync(server);
        await client.GetStream().WriteAsync("SC"u8.ToArray());
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        using var replacement = new TcpListener(IPAddress.Loopback, port);
        replacement.Start();
    }

    [Fact]
    public async Task SetupSupportsPingAndStopBeforeRequest()
    {
        await using var server = Server();
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await SendAsync(client, Packet.Control(PacketType.Ping, 0, new { nonce = -7 }));
        var pong = await ReadAsync(client);
        Assert.Equal(PacketType.Pong, pong.Type);
        Assert.Equal(-7, JsonObject.Number(pong.Object(), "nonce"));
        await SendAsync(client, Packet.Control(PacketType.Stop, 0, new { reason = "cancel setup" }));
        Assert.Equal(PacketType.SessionEnd, (await ReadAsync(client)).Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferedStopAfterAcceptanceRetiresTracksWithoutFatalPlaybackError(bool audio)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var factory = new TestFactory();
            await using var server = Server(factory);
            server.Start(false);
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            await HelloAsync(client);
            using var packets = new MemoryStream();
            await Wire.WriteAsync(packets, Packet.Control(PacketType.SessionRequest, 1, RequestTests.Settings(audio)),
                timeout.Token);
            await Wire.WriteAsync(packets, Packet.Control(PacketType.Stop, 1, new { reason = "cancel before tracks start" }),
                timeout.Token);
            await client.GetStream().WriteAsync(packets.ToArray(), timeout.Token);
            Assert.Equal(PacketType.SessionAccept, (await Wire.ReadAsync(client.GetStream(), timeout.Token)).Type);
            var ended = await Wire.ReadAsync(client.GetStream(), timeout.Token);
            Assert.Equal(PacketType.SessionEnd, ended.Type);
            Assert.Equal("cancel before tracks start", JsonObject.Text(ended.Object(), "reason", 1024));
            Assert.True(factory.Playback.Disposed);
            Assert.Equal(1, factory.Playback.DisposeCalls);
            Assert.Empty(factory.Playback.Configured);
            Assert.Empty(factory.Playback.Presented);
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], timeout.Token));
        }
    }

    [Fact]
    public async Task UnacknowledgedReceiverStopClosesWithinTwoSecondBudget()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        var elapsed = Stopwatch.StartNew();
        var disconnect = server.DisconnectAsync();
        Assert.Equal(PacketType.Stop, (await ReadAsync(client)).Type);
        await disconnect.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 1.8, 3.5);
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task TrickledSetupDoesNotExtendOverallTenSecondDeadline()
    {
        await using var server = Server();
        server.Start(false);
        using var client = await ConnectAsync(server);
        var elapsed = Stopwatch.StartNew();
        await client.GetStream().WriteAsync("S"u8.ToArray());
        await Task.Delay(TimeSpan.FromSeconds(7));
        await client.GetStream().WriteAsync("C"u8.ToArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var rejected = await Wire.ReadAsync(client.GetStream(), timeout.Token);
        Assert.Equal("TIMEOUT", JsonObject.Text(rejected.Object(), "code"));
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 9.0, 13.0);
    }

    [Fact]
    public async Task UnsolicitedSessionEndIsAProtocolError()
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, Packet.Control(PacketType.SessionEnd, 1, new { reason = "not a STOP acknowledgement" }));
        Assert.Equal("PROTOCOL", JsonObject.Text((await ReadAsync(client)).Object(), "code"));
    }

    [Fact]
    public async Task SimultaneousShutdownCallersAwaitTheSameCleanup()
    {
        var server = Server();
        server.Start(false);
        using var client = await ConnectAsync(server);
        await client.GetStream().WriteAsync("SC"u8.ToArray());
        await Task.WhenAll(server.DisposeAsync().AsTask(), server.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AbortDoesNotHoldTheClientLockWhileWaitingForTheServerGate()
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(peer)).Type);
        static object Field(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
        var connection = Field(server, "active");
        var clientLock = Field(connection, "client");
        var serverGate = Field(server, "gate");
        var abort = connection.GetType().GetMethod("Abort")!.CreateDelegate<Action>(connection);
        using var entered = new ManualResetEventSlim();
        Exception? abortFailure = null;
        var thread = new Thread(() =>
        {
            entered.Set();
            try { abort(); }
            catch (Exception error) { abortFailure = error; }
        }) { IsBackground = true };
        var clientLockAvailable = false;
        try
        {
            lock (serverGate)
            {
                thread.Start();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "Abort thread did not start.");
                Assert.True(SpinWait.SpinUntil(() => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(2)), "Abort did not reach the held server gate.");
                clientLockAvailable = Monitor.TryEnter(clientLock, TimeSpan.FromMilliseconds(200));
                if (clientLockAvailable) Monitor.Exit(clientLock);
            }
        }
        finally
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "Abort did not retire after releasing the server gate.");
        }
        Assert.Null(abortFailure);
        Assert.True(clientLockAvailable, "Abort held the client lock while waiting for the server gate, reversing shutdown's lock order.");
    }

    [Fact]
    public async Task ReadyTracksAndLivePongsDoNotMasqueradeAsRenderedVideo()
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        var elapsed = Stopwatch.StartNew();
        await SendAsync(client, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(13));
        while (true)
        {
            var packet = await Wire.ReadAsync(client.GetStream(), deadline.Token);
            if (packet.Type == PacketType.Ping)
            {
                await SendAsync(client, Packet.Control(PacketType.Pong, 1, new { nonce = JsonObject.Number(packet.Object(), "nonce") }));
                continue;
            }
            Assert.Equal(PacketType.Error, packet.Type);
            Assert.Equal("TIMEOUT", JsonObject.Text(packet.Object(), "code"));
            break;
        }
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 9.0, 12.0);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public async Task TcpReconfigurationCannotExtendInitialVideoDeadline(uint track)
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client, audio: track == 2);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        var elapsed = Stopwatch.StartNew();
        await SendAsync(client, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        if (track == 2)
        {
            await SendAsync(client, QueueAndTrackTests.Configuration(2));
            Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(13));
        uint generation = 1;
        var pings = 0;
        while (true)
        {
            var packet = await Wire.ReadAsync(client.GetStream(), deadline.Token);
            if (packet.Type == PacketType.StreamReady)
            {
                Assert.Equal(track, packet.Track);
                Assert.Equal(generation, packet.Generation);
                continue;
            }
            if (packet.Type == PacketType.Ping)
            {
                pings++;
                await SendAsync(client, Packet.Control(PacketType.Pong, 1, new { nonce = JsonObject.Number(packet.Object(), "nonce") }));
                await SendAsync(client, QueueAndTrackTests.Configuration(track, ++generation));
                continue;
            }
            Assert.Equal(PacketType.Error, packet.Type);
            Assert.Equal("TIMEOUT", JsonObject.Text(packet.Object(), "code"));
            Assert.True(JsonObject.Boolean(packet.Object(), "fatal"));
            break;
        }
        Assert.True(pings >= 3);
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 9, 12);
        var cleanup = Stopwatch.StartNew();
        while (server.IsBusy && cleanup.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
        Assert.False(server.IsBusy);
        Assert.True(factory.Playback.Disposed);
    }

    [Fact]
    public async Task TcpPresentationHistorySurvivesSubsequentReconfiguration()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Media(0));
        await SendAsync(client, QueueAndTrackTests.Media(33_333));
        await factory.Playback.TwoPresented.Task.WaitAsync(TimeSpan.FromSeconds(2));
        factory.Playback.SuppressCurrentPresentation = true;
        Assert.False(factory.Playback.HasPresentedVideo);
        Assert.True(factory.Playback.HasEverPresentedVideo);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var elapsed = Stopwatch.StartNew();
        uint generation = 2;
        await SendAsync(client, QueueAndTrackTests.Configuration(generation: generation));
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(10_500))
        {
            var packet = await Wire.ReadAsync(client.GetStream(), deadline.Token);
            if (packet.Type == PacketType.StreamReady) continue;
            Assert.Equal(PacketType.Ping, packet.Type);
            await SendAsync(client, Packet.Control(PacketType.Pong, 1, new { nonce = JsonObject.Number(packet.Object(), "nonce") }));
            await SendAsync(client, QueueAndTrackTests.Configuration(generation: ++generation));
        }
        Assert.True(server.IsBusy);
        await SendAsync(client, Packet.Control(PacketType.Stop, 1, new { reason = "post-start-reconfiguration" }));
        while (true)
        {
            var packet = await ReadAsync(client);
            if (packet.Type == PacketType.StreamReady) continue;
            Assert.Equal(PacketType.SessionEnd, packet.Type);
            break;
        }
        Assert.True(factory.Playback.Disposed);
    }

    [Fact]
    public async Task PlaybackHealthFailureIsTerminalEvenWithoutNewMedia()
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        factory.Playback.HealthFailure = new IOException("GPU resize failed");
        var error = await ReadAsync(client);
        Assert.Equal(PacketType.Error, error.Type);
        Assert.Equal("DECODER_FAILED", JsonObject.Text(error.Object(), "code"));
        Assert.Contains("GPU resize failed", JsonObject.Text(error.Object(), "message", 2048));
    }

    [Fact]
    public async Task SenderFatalErrorIncludesReceiverWorkerAndQueueEvidence()
    {
        var factory = new TestFactory();
        var presenting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Playback.OnPresent = async (_, token) =>
        {
            presenting.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        await using var server = new ReceiverServer("Test", factory, notice =>
        {
            if (notice.State == "Sender error") failure.TrySetResult(notice.Detail);
        }, 0, IPAddress.Loopback);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(client)).Type);
        await SendAsync(client, QueueAndTrackTests.Media(0));
        await presenting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await Task.Delay(450);
            await SendAsync(client, Packet.Control(PacketType.Error, 1,
                new { code = "CONGESTION", message = "Synthetic sender admission timeout", fatal = true }));
            var detail = await failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("Synthetic sender admission timeout", detail);
            Assert.Contains("Video=VideoFrame generation=1", detail);
            Assert.Contains("videoInFlight=VideoFrame", detail);
            Assert.Contains("socketAvailableBytes=", detail);
            Assert.Contains("receive handlingMaxMs=", detail);
            Assert.Contains("transportStalls=[atMs=", detail);
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains("kernelBytesIn=", detail);
                Assert.Contains("receiveWindow=", detail);
            }
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MediaEofDrainsCurrentSessionFatalOrNormalStopBeforeClosingControl(bool fatal)
    {
        var notices = new ConcurrentQueue<ReceiverNotice>();
        var factory = new TestFactory();
        await using var server = new ReceiverServer("Test", factory, notices.Enqueue, 0, IPAddress.Loopback);
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(peer)).Type);
        peer.Video.Client.Shutdown(SocketShutdown.Send);
        var watch = Stopwatch.StartNew();
        await Task.Delay(350);
        await SendAsync(peer, Packet.Control(fatal ? PacketType.Error : PacketType.Stop, 1,
            fatal ? (object)new { code = "CAPTURE_FAILED", message = "Exact sender terminal message", fatal = true }
                : new { reason = "normal sender stop" }));
        if (!fatal) Assert.Equal(PacketType.SessionEnd, (await ReadAsync(peer)).Type);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], timeout.Token));
        while (server.IsBusy) await Task.Delay(10, timeout.Token);
        Assert.True(factory.Playback.Disposed);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(notices, notice => notice.State == "PROTOCOL");
        if (fatal)
            Assert.Contains(notices, notice => notice.State == "Sender error" &&
                notice.Detail.Contains("CAPTURE_FAILED: Exact sender terminal message"));
    }

    [Fact]
    public async Task HandledPlaybackFatalRetiresMediaAndPlaybackBeforeFinalControlClose()
    {
        var factory = new TestFactory();
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Playback.OnPresent = (_, _) => throw new IOException("Synthetic decoder failure");
        factory.Playback.OnDispose = async () =>
        {
            disposing.TrySetResult();
            await release.Task;
        };
        await using var server = Server(factory);
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(peer)).Type);
        await SendAsync(peer, QueueAndTrackTests.Configuration());
        Assert.Equal(PacketType.StreamReady, (await ReadAsync(peer)).Type);
        await SendAsync(peer, QueueAndTrackTests.Media(0));
        var error = await ReadAsync(peer);
        Assert.Equal(PacketType.Error, error.Type);
        Assert.Equal("DECODER_FAILED", JsonObject.Text(error.Object(), "code"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            await disposing.Task.WaitAsync(timeout.Token);
            Assert.Equal(0, await peer.Video.GetStream().ReadAsync(new byte[1], timeout.Token));
            Assert.Equal(0, await peer.Audio.GetStream().ReadAsync(new byte[1], timeout.Token));
            var controlEof = peer.GetStream().ReadAsync(new byte[1], timeout.Token).AsTask();
            await Task.Delay(100, timeout.Token);
            Assert.False(controlEof.IsCompleted);
            release.TrySetResult();
            Assert.Equal(0, await controlEof);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StalledPlaybackRetirementHasOneBoundedBudgetAndClosesAllSockets(bool remoteStop)
    {
        var factory = new TestFactory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Playback.OnDispose = async () => await release.Task;
        await using var server = Server(factory);
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(peer)).Type);
        var watch = Stopwatch.StartNew();
        await SendAsync(peer, Packet.Control(remoteStop ? PacketType.Stop : PacketType.Error, 1,
            remoteStop ? (object)new { reason = "normal stop" }
                : new { code = "CAPTURE_FAILED", message = "Synthetic sender failure", fatal = true }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            if (remoteStop)
            {
                var error = await ReadAsync(peer);
                Assert.Equal(PacketType.Error, error.Type);
                Assert.Equal("TIMEOUT", JsonObject.Text(error.Object(), "code"));
            }
            Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], timeout.Token));
            Assert.Equal(0, await peer.Video.GetStream().ReadAsync(new byte[1], timeout.Token));
            Assert.Equal(0, await peer.Audio.GetStream().ReadAsync(new byte[1], timeout.Token));
            Assert.InRange(watch.Elapsed.TotalSeconds, 1.8, 3.2);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task TruncatedMediaHeaderIsARealProtocolFailureNotProvisionalEof()
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(peer)).Type);
        var watch = Stopwatch.StartNew();
        await peer.Video.GetStream().WriteAsync(new byte[] { 0x53 });
        peer.Video.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(PacketType.Error, (await ReadAsync(peer)).Type);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimedOutRetirementOwnsResourcesUntilTheLastCallbackFinishes(bool blockDisposal)
    {
        var notices = new ConcurrentQueue<ReceiverNotice>();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leasesDisposed = 0;
        var factory = new TestFactory { FreshAfterFirstOpen = true };
        Action probe = () => { };
        if (blockDisposal) factory.Playback.OnDispose = BlockAsync;
        else factory.Playback.OnPresent = (_, _) => BlockAsync();
        await using var server = new ReceiverServer("Test", factory, notice =>
        {
            notices.Enqueue(notice);
            if (notice.State == "Cleanup pending") pending.TrySetResult();
        }, 0, IPAddress.Loopback, () => new TestLease(() => Interlocked.Increment(ref leasesDisposed)));
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(peer)).Type);
        var connection = Field(server, "active");
        var writer = (SemaphoreSlim)Field(connection, "writer");
        var transport = (CancellationTokenSource)Field(connection, "cancellation");
        var playbackCancellation = (CancellationTokenSource)Field(connection, "playbackCancellation");
        var signals = (SemaphoreSlim[])Field(Field(connection, "queue"), "signals");
        probe = () =>
        {
            _ = writer.AvailableWaitHandle;
            _ = transport.Token;
            _ = playbackCancellation.Token;
            foreach (var signal in signals) _ = signal.AvailableWaitHandle;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        if (!blockDisposal)
        {
            await SendAsync(peer, QueueAndTrackTests.Configuration());
            Assert.Equal(PacketType.StreamReady, (await ReadAsync(peer)).Type);
            await SendAsync(peer, QueueAndTrackTests.Media(0));
            await blocked.Task.WaitAsync(timeout.Token);
        }
        var watch = Stopwatch.StartNew();
        await SendAsync(peer, Packet.Control(PacketType.Error, 1,
            new { code = "CAPTURE_FAILED", message = "Synthetic sender terminal failure", fatal = true }));
        try
        {
            await blocked.Task.WaitAsync(timeout.Token);
            Assert.Equal(0, await peer.Video.GetStream().ReadAsync(new byte[1], timeout.Token));
            Assert.Equal(0, await peer.Audio.GetStream().ReadAsync(new byte[1], timeout.Token));
            Assert.Equal(0, await peer.GetStream().ReadAsync(new byte[1], timeout.Token));
            await pending.Task.WaitAsync(timeout.Token);
            Assert.InRange(watch.Elapsed.TotalSeconds, 1.8, 3.5);
            probe();
            Assert.Equal(0, Volatile.Read(ref leasesDisposed));
            Assert.Equal(blockDisposal ? 1 : 0, factory.Playback.DisposeCalls);
            Assert.True(server.IsBusy);
            var disposingResources = (Task)Field(connection, "resourceDisposal");
            Assert.False(disposingResources.IsCompleted);
            release.TrySetResult();
            await callbackFinished.Task.WaitAsync(timeout.Token);
            await disposingResources.WaitAsync(timeout.Token);
            Assert.Equal(1, factory.Playback.DisposeCalls);
            Assert.Equal(1, Volatile.Read(ref leasesDisposed));
            Assert.Throws<ObjectDisposedException>(() => _ = writer.AvailableWaitHandle);
            Assert.Throws<ObjectDisposedException>(() => _ = transport.Token);
            Assert.Throws<ObjectDisposedException>(() => _ = playbackCancellation.Token);
            foreach (var signal in signals)
                Assert.Throws<ObjectDisposedException>(() => _ = signal.AvailableWaitHandle);
            Assert.False(server.IsBusy);
            Assert.Contains(notices, notice => notice.State == "Cleanup failed" &&
                notice.Detail.Contains("origin=connection-retirement"));
            Assert.Contains(notices, notice => notice.State == "Cleanup complete");
            Assert.DoesNotContain(notices, notice => notice.Detail.Contains("ObjectDisposedException"));

            using var next = await ConnectAsync(server);
            await HelloAsync(next);
            await RequestAsync(next, session: 2);
            Assert.Equal(PacketType.SessionAccept, (await ReadAsync(next)).Type);
            await SendAsync(next, Packet.Control(PacketType.Stop, 2, new { reason = "next session" }));
            Assert.Equal(PacketType.SessionEnd, (await ReadAsync(next)).Type);
            Assert.Equal(1, factory.Playback.DisposeCalls);
        }
        finally { release.TrySetResult(); }

        async ValueTask BlockAsync()
        {
            blocked.TrySetResult();
            await release.Task;
            try { probe(); callbackFinished.TrySetResult(); }
            catch (Exception error) { callbackFinished.TrySetException(error); throw; }
        }

        static object Field(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    }

    private sealed class TestLease(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Fact]
    public void DiscoveryMatchesPairedContractWithoutHostOrCodes()
    {
        var json = JsonObject.Parse(Discovery.Advertisement("Test", 27184, true));
        Assert.Equal("screencopy", JsonObject.Text(json, "protocol"));
        Assert.Equal(3, JsonObject.Number(json, "version"));
        Assert.Equal("busy", JsonObject.Text(json, "state"));
        Assert.Equal(5, json.EnumerateObject().Count());
    }

    [Fact]
    public async Task RepeatedConnectionsAndListenerRestartsReleaseOwnership()
    {
        for (var index = 0; index < 10; index++)
        {
            await using var server = Server();
            server.Start(false);
            using var client = await ConnectAsync(server);
            await HelloAsync(client);
            await RequestAsync(client);
            Assert.Equal(PacketType.Reject, (await ReadAsync(client)).Type);
        }
    }

    private sealed class TestFactory : IPlaybackFactory
    {
        public bool FreshAfterFirstOpen { get; init; }
        public bool SupportsVideoRotation { get; init; }
        public string Description => "TEST ONLY - no real rendering.";
        public TestPlayback Playback { get; } = new();
        public int Opens { get; private set; }
        public ConcurrentQueue<CastRequest> Requests { get; } = new();
        public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            Opens++;
            return ValueTask.FromResult<IPlayback>(FreshAfterFirstOpen && Opens > 1 ? new TestPlayback() : Playback);
        }
    }

    [Fact]
    public async Task RemoteStopSuppressesHealthChecksWhilePlaybackRetires()
    {
        var factory = new TestFactory();
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Playback.OnDispose = () =>
        {
            factory.Playback.HealthFailure = new ProtocolException("Retired output must not be health-checked.", "AUDIO_FAILED");
            retiring.TrySetResult();
            return new ValueTask(release.Task);
        };
        await using var server = Server(factory);
        server.Start(false);
        using var client = await ConnectAsync(server);
        await HelloAsync(client);
        await RequestAsync(client);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(client)).Type);
        await SendAsync(client, Packet.Control(PacketType.Stop, 1, new { reason = "user" }));
        await retiring.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try { await Task.Delay(250); }
        finally { release.TrySetResult(); }
        Assert.Equal(PacketType.SessionEnd, (await ReadAsync(client)).Type);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    public async Task ClosingEitherMediaChannelRetiresPlaybackAndReleasesTheSession(string channel)
    {
        var factory = new TestFactory();
        await using var server = Server(factory);
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer);
        Assert.Equal(PacketType.SessionAccept, (await ReadAsync(peer)).Type);
        (channel == "video" ? peer.Video : peer.Audio).Dispose();
        var error = await ReadAsync(peer);
        Assert.Equal(PacketType.Error, error.Type);
        Assert.Contains(channel, JsonObject.Text(error.Object(), "message", 2048), StringComparison.OrdinalIgnoreCase);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (server.IsBusy) await Task.Delay(10, timeout.Token);
        Assert.True(factory.Playback.Disposed);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("session")]
    [InlineData("duplicate")]
    public async Task InvalidOrDuplicateMediaAttachmentDoesNotEvictTheActiveCast(string fault)
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer);
        var accepted = await ReadAsync(peer);
        using var rogue = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await rogue.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        await Wire.WriteAsync(rogue.GetStream(), Packet.Control(PacketType.Hello, fault == "session" ? 2 : 1,
            new { role = "media", channel = "video", token = fault == "token" ? new string('0', 64) :
                JsonObject.Text(accepted.Object(), "mediaToken", 64) }), timeout.Token);
        Assert.Equal(PacketType.Reject, (await Wire.ReadAsync(rogue.GetStream(), timeout.Token)).Type);
        Assert.True(server.IsBusy);
        await SendAsync(peer, Packet.Control(PacketType.Ping, 1, new { nonce = 42 }));
        Assert.Equal(PacketType.Pong, (await ReadAsync(peer)).Type);
        await SendAsync(peer, Packet.Control(PacketType.Stop, 1, new { reason = "done" }));
        Assert.Equal(PacketType.SessionEnd, (await ReadAsync(peer)).Type);
    }

    [Theory]
    [InlineData("matching", "SESSION_ENDED")]
    [InlineData("token", "PROTOCOL")]
    [InlineData("session", "PROTOCOL")]
    public async Task EndingSessionRejectionRequiresTheMatchingSessionAndToken(string attachment, string expectedCode)
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer);
        var accepted = await ReadAsync(peer);
        var disconnect = server.DisconnectAsync();
        Assert.Equal(PacketType.Stop, (await ReadAsync(peer)).Type);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var media = new TcpClient();
            await media.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            var requestedSession = attachment == "session" ? 2 : 1;
            await Wire.WriteAsync(media.GetStream(), Packet.Control(PacketType.Hello, requestedSession,
                new { role = "media", channel = "video", token = attachment == "token" ? new string('0', 64) :
                    JsonObject.Text(accepted.Object(), "mediaToken", 64) }), timeout.Token);
            var reply = await Wire.ReadAsync(media.GetStream(), timeout.Token);
            Assert.Equal(PacketType.Reject, reply.Type);
            Assert.Equal(requestedSession, reply.Session);
            Assert.Equal(expectedCode, JsonObject.Text(reply.Object(), "code"));
        }
        finally
        {
            await SendAsync(peer, Packet.Control(PacketType.SessionEnd, 1, new { reason = "receiver stopped" }));
            await disconnect.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MediaOnTheWrongConnectionFailsExplicitly(bool control)
    {
        await using var server = Server(new TestFactory());
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer, audio: true);
        _ = await ReadAsync(peer);
        await Wire.WriteAsync(control ? peer.GetStream() : peer.Audio.GetStream(), QueueAndTrackTests.Configuration());
        var error = await ReadAsync(peer);
        Assert.Equal(PacketType.Error, error.Type);
        Assert.Equal("PROTOCOL", JsonObject.Text(error.Object(), "code"));
    }

    [Fact]
    public async Task PartialVideoPacketDoesNotBlockAudioOrControl()
    {
        var factory = new TestFactory();
        var audioPresented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Playback.OnPresent = (packet, _) =>
        {
            if (packet.Track == 2) audioPresented.TrySetResult();
            return ValueTask.CompletedTask;
        };
        await using var server = Server(factory);
        server.Start(false);
        using var peer = await ConnectAsync(server);
        await HelloAsync(peer);
        await RequestAsync(peer, audio: true);
        _ = await ReadAsync(peer);
        foreach (var track in new[] { 1u, 2u })
        {
            await SendAsync(peer, QueueAndTrackTests.Configuration(track));
            Assert.Equal(PacketType.StreamReady, (await ReadAsync(peer)).Type);
        }
        using var serialized = new MemoryStream();
        await Wire.WriteAsync(serialized, QueueAndTrackTests.Media(0));
        await peer.Video.GetStream().WriteAsync(serialized.ToArray().AsMemory(0, Wire.HeaderSize));
        var watch = Stopwatch.StartNew();
        await SendAsync(peer, QueueAndTrackTests.Media(0, track: 2));
        await audioPresented.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await SendAsync(peer, Packet.Control(PacketType.Ping, 1, new { nonce = 42 }));
        Assert.Equal(PacketType.Pong, (await ReadAsync(peer)).Type);
        await SendAsync(peer, Packet.Control(PacketType.Stop, 1, new { reason = "done" }));
        Assert.Equal(PacketType.SessionEnd, (await ReadAsync(peer)).Type);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    private sealed class TestPlayback : IPlayback, IVideoPlaybackProgress
    {
        public int DisposeCalls;
        public bool Disposed { get; private set; }
        public bool SuppressCurrentPresentation { get; set; }
        public bool HasPresentedVideo => !SuppressCurrentPresentation && !Presented.IsEmpty;
        public bool HasEverPresentedVideo => !Presented.IsEmpty;
        public Exception? HealthFailure { get; set; }
        public Func<ValueTask>? OnDispose { get; set; }
        public Func<Packet, CancellationToken, ValueTask>? OnPresent { get; set; }
        public void CheckHealth()
        {
            if (HealthFailure is { } failure) throw failure;
        }
        public ConcurrentBag<uint> Configured { get; } = [];
        public ConcurrentQueue<long> Presented { get; } = [];
        public ConcurrentQueue<int> Rotations { get; } = [];
        public TaskCompletionSource TwoPresented { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask ConfigureAsync(Packet configuration, CancellationToken cancellationToken)
        {
            Configured.Add(configuration.Track);
            return ValueTask.CompletedTask;
        }
        public async ValueTask PresentAsync(Packet media, CancellationToken cancellationToken)
        {
            if (OnPresent is not null) await OnPresent(media, cancellationToken);
            Rotations.Enqueue(media.VideoRotationDegrees);
            Presented.Enqueue(media.PtsUs);
            if (Presented.Count == 2) TwoPresented.TrySetResult();
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            Disposed = true;
            return OnDispose?.Invoke() ?? ValueTask.CompletedTask;
        }
    }
}
