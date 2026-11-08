using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RedrainCast.Core;
using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using RedrainCast.Send.Core;
using Xunit;
using Wire = RedrainCast.Receive.Core.Wire;

namespace RedrainCast.Tests;

public sealed class ReceiverOptionsTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    [Theory]
    [InlineData("{}", false)]
    [InlineData("""{"receiverOptions":{"version":1,"supported":[]}}""", false)]
    [InlineData("""{"receiverOptions":{"version":1,"supported":["future","playbackStats"]}}""", true)]
    [InlineData("""{"receiverOptions":{"version":2,"supported":["playbackStats"]}}""", false)]
    public void CapabilitiesIntersectWithoutRejectingFutureRevision(string value, bool expected) =>
        Assert.Equal(expected, ReceiverOptionProtocol.Capabilities(Json(value)).PlaybackStats);

    [Theory]
    [InlineData("""{"version":1.0,"supported":[]}""")]
    [InlineData("""{"version":"1","supported":[]}""")]
    [InlineData("""{"version":2147483648,"supported":[]}""")]
    [InlineData("""{"version":0,"supported":[]}""")]
    [InlineData("""{"version":1,"supported":true}""")]
    [InlineData("""{"version":1,"supported":["playbackStats","playbackStats"]}""")]
    [InlineData("""{"version":1,"version":1,"supported":[]}""")]
    [InlineData("""{"version":1,"supported":[true]}""")]
    [InlineData("""{"version":1,"supported":[""]}""")]
    public void MalformedCapabilitiesFailStrictly(string value) =>
        Assert.Throws<FormatException>(() => ReceiverOptionProtocol.Capabilities(Json($"{{\"receiverOptions\":{value}}}")));

    [Theory]
    [InlineData("""{"version":1,"playbackStats":true}""", true)]
    [InlineData("""{"version":1,"playbackStats":false}""", false)]
    [InlineData("""{"version":1,"playbackStats":true,"futureOption":{"mode":42}}""", true)]
    [InlineData("""{"version":2,"playbackStats":true}""", false)]
    [InlineData("""{"version":1,"futureOption":true}""", false)]
    public void RequestsApplyOnlyNegotiatedKnownOptions(string value, bool expected)
    {
        var root = Json($"{{\"receiverOptions\":{value}}}");
        Assert.Equal(expected, ReceiverOptionProtocol.Parse(root, ReceiverOptionCapabilities.Implemented).PlaybackStats);
        Assert.False(ReceiverOptionProtocol.Parse(root, ReceiverOptionCapabilities.None).PlaybackStats);
    }

    [Theory]
    [InlineData("""{"version":1,"playbackStats":1}""")]
    [InlineData("""{"version":2,"playbackStats":"true"}""")]
    [InlineData("""{"version":1,"playbackStats":null}""")]
    [InlineData("""{"version":1,"playbackStats":true,"playbackStats":false}""")]
    [InlineData("""{"version":1.0,"playbackStats":true}""")]
    [InlineData("""{"playbackStats":true}""")]
    [InlineData("""[]""")]
    public void KnownMalformedRequestFieldsFailEvenWithoutNegotiation(string value)
    {
        var root = Json($"{{\"receiverOptions\":{value}}}");
        Assert.Throws<FormatException>(() => ReceiverOptionProtocol.Parse(root, ReceiverOptionCapabilities.None));
    }

    [Fact]
    public void EnvelopesAndListsAndNamesAreBounded()
    {
        foreach (var value in new[]
        {
            JsonSerializer.SerializeToElement(new { receiverOptions = new { version = 1, supported = Enumerable.Range(0, 17).Select(i => $"f{i}") } }),
            JsonSerializer.SerializeToElement(new { receiverOptions = new { version = 1, supported = new[] { new string('a', 65) } } }),
            Json($"{{\"receiverOptions\":{{\"version\":1,\"padding\":\"{new string('a', 2048)}\"}}}}"),
            Json("""{"receiverOptions":{"version":1},"receiverOptions":{"version":1}}""")
        })
            Assert.Throws<FormatException>(() => ReceiverOptionProtocol.Capabilities(value));
        var fields = Enumerable.Range(0, 17).ToDictionary(i => $"future{i}", _ => (object)true);
        fields["version"] = 1;
        Assert.Throws<FormatException>(() => ReceiverOptionProtocol.Parse(
            JsonSerializer.SerializeToElement(new { receiverOptions = fields }), ReceiverOptionCapabilities.None));
    }

    [Fact]
    public void SettingsPersistExplicitBooleanButDoNotImplicitlySerializeItIntoRequests()
    {
        var settings = new CaptureSettings(ShowPlaybackStatistics: true);
        Assert.True(CaptureSettings.ReadSaved(JsonSerializer.Serialize(settings)).ShowPlaybackStatistics);
        Assert.False(CaptureSettings.ReadSaved("{}").ShowPlaybackStatistics);
        Assert.False(JsonSerializer.SerializeToElement(settings.Request("fake")).TryGetProperty("receiverOptions", out _));
        Assert.True(JsonSerializer.SerializeToElement(settings.Request("fake", new(true)))
            .GetProperty("receiverOptions").GetProperty("playbackStats").GetBoolean());
        Assert.True(CaptureSettings.ParseInput("1920", "8", 60, "auto",
            System.Globalization.CultureInfo.InvariantCulture, showPlaybackStatistics: true).Settings!.ShowPlaybackStatistics);
        var cast = new OutgoingCast(new(IPAddress.Loopback, 1, "legacy", false))
            .ApplyStatus("Receiver option unavailable: compatible receiver required").ApplyStatus("Streaming");
        Assert.Equal(OutgoingPhase.Casting, cast.Phase);
        Assert.Contains("compatible receiver", cast.Notice);
        Assert.False(new CastRequest("h264", null, 60, "auto").EffectiveOptions.PlaybackStats);
        Assert.False(((IPlaybackFactory)new UnavailablePlaybackFactory()).ReceiverOptions.PlaybackStats);
    }

    // This wire shape follows current Android ReceiverLink.kt: only rotation/recovery are known capabilities.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CurrentAndroidAndLegacySenderWireNeverEnablesAnUnnegotiatedOption(bool android, bool smuggledOption)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var factory = new Factory(true);
        await using var server = new ReceiverServer("new receiver", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, limit.Token);
        var hello = android
            ? """{"role":"sender","versions":[3],"name":"RedrainCast","videoRotation":true,"streamRecovery":true,"streamRecoveryVersion":2}"""
            : """{"role":"sender","versions":[3],"name":"legacy Windows"}""";
        await Wire.WriteAsync(client.GetStream(), Packet.Control(PacketType.Hello, 0, Json(hello)), limit.Token);
        var response = await Wire.ReadAsync(client.GetStream(), limit.Token);
        Assert.Equal(PacketType.Hello, response.Type);
        Assert.False(response.Object().TryGetProperty("receiverOptions", out _));
        var request = JsonSerializer.SerializeToNode(new CaptureSettings().Request("Android hardware encoder"))!;
        request["unknownLegacyField"] = "ignored";
        if (smuggledOption) request["receiverOptions"] = JsonSerializer.SerializeToNode(new { version = 1, playbackStats = true });
        await Wire.WriteAsync(client.GetStream(), Packet.Control(PacketType.SessionRequest, 7, request), limit.Token);
        Assert.Equal(PacketType.SessionAccept, (await Wire.ReadAsync(client.GetStream(), limit.Token)).Type);
        Assert.False(factory.Requests.Single().EffectiveOptions.PlaybackStats);
        await Wire.WriteAsync(client.GetStream(), Packet.Control(PacketType.Stop, 7, new { reason = "wire fixture complete" }), limit.Token);
        Assert.Equal(PacketType.SessionEnd, (await Wire.ReadAsync(client.GetStream(), limit.Token)).Type);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task NewSenderNewReceiverBothSessionPathsUseExactlyTheMutualSelection(bool supported, bool requested, bool recovery)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var stop = new CancellationTokenSource();
        var factory = new Factory(supported, recovery);
        await using var server = new ReceiverServer("new receiver", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        var source = new Source(recovery);
        var statuses = new ConcurrentQueue<string>();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback, server.Port, "new", false),
            new(ShowPlaybackStatistics: requested), source, statuses.Enqueue, stop.Token);
        while (!statuses.Contains("Streaming"))
        {
            if (run.IsCompleted) await run;
            await Task.Delay(10, limit.Token);
        }
        Assert.True(source.Prepared);
        Assert.Equal(supported && requested, factory.Requests.Single().EffectiveOptions.PlaybackStats);
        Assert.Equal(requested && !supported, statuses.Any(s => s.StartsWith("Receiver option unavailable:", StringComparison.Ordinal)));
        stop.Cancel();
        await run.WaitAsync(limit.Token);
        Assert.True(source.Disposed);
        Assert.Null(server.Statistics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CappedNewSenderOmitsStatisticsExtensionForLegacyAndroidOrWindowsReceiver(bool android)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Source(false);
        var statuses = new ConcurrentQueue<string>();
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, "old", false),
            new(MaxFps: 60, ShowPlaybackStatistics: true), source, statuses.Enqueue, default);
        using var peer = await listener.AcceptTcpClientAsync(limit.Token);
        var stream = peer.GetStream();
        var hello = await Wire.ReadAsync(stream, limit.Token);
        Assert.True(ReceiverOptionProtocol.Capabilities(hello.Object()).PlaybackStats);
        // Legacy Android receivers do not acknowledge the statistics or uncapped-FPS extensions.
        await Wire.WriteAsync(stream, Packet.Control(PacketType.Hello, 0,
            Json(android ? """{"role":"receiver","versions":[3],"name":"Android"}""" :
                """{"role":"receiver","versions":[3],"name":"old Windows"}""")), limit.Token);
        var request = await Wire.ReadAsync(stream, limit.Token);
        Assert.Equal(PacketType.SessionRequest, request.Type);
        Assert.Equal(60, request.Object().GetProperty("video").GetProperty("maxFps").GetInt32());
        Assert.False(request.Object().TryGetProperty("receiverOptions", out _));
        Assert.True(source.Prepared);
        Assert.False(source.Started);
        Assert.Single(statuses, s => s.StartsWith("Receiver option unavailable:", StringComparison.Ordinal));
        await Wire.WriteAsync(stream, Packet.Control(PacketType.SessionAccept, request.Session,
            new { tracks = new[] { 1 }, mediaToken = new string('A', 64) }), limit.Token);
        using var video = await listener.AcceptTcpClientAsync(limit.Token);
        var mediaHello = await Wire.ReadAsync(video.GetStream(), limit.Token);
        Assert.Equal("video", mediaHello.Object().GetProperty("channel").GetString());
        await Wire.WriteAsync(video.GetStream(), Packet.Control(PacketType.Hello, request.Session, new { channel = "video" }), limit.Token);
        using var audio = await listener.AcceptTcpClientAsync(limit.Token);
        await Wire.ReadAsync(audio.GetStream(), limit.Token);
        await Wire.WriteAsync(audio.GetStream(), Packet.Control(PacketType.Hello, request.Session, new { channel = "audio" }), limit.Token);
        Assert.Equal(PacketType.StreamConfig, (await Wire.ReadAsync(video.GetStream(), limit.Token)).Type);
        await Wire.WriteAsync(stream, new(PacketType.StreamReady, request.Session, 1, 1, -1, 0, """{"ok":true}"""u8.ToArray()), limit.Token);
        Assert.Equal(PacketType.VideoFrame, (await Wire.ReadAsync(video.GetStream(), limit.Token)).Type);
        await Wire.WriteAsync(stream, Packet.Control(PacketType.Stop, request.Session, new { reason = "fixture complete" }), limit.Token);
        Assert.Equal(PacketType.SessionEnd, (await Wire.ReadAsync(stream, limit.Token)).Type);
        await run.WaitAsync(limit.Token);
        Assert.True(source.Started);
        Assert.True(source.Disposed);
    }

    [Theory]
    [InlineData(true, """{"version":1.0,"supported":["playbackStats"]}""")]
    [InlineData(true, """{"version":1,"supported":["playbackStats","playbackStats"]}""")]
    [InlineData(true, """{"version":2147483648,"supported":[]}""")]
    [InlineData(false, """{"version":1,"playbackStats":"true"}""")]
    [InlineData(false, """{"version":2,"playbackStats":1}""")]
    [InlineData(false, """{"version":1,"playbackStats":true,"playbackStats":false}""")]
    public async Task RawMalformedKnownOptionsRejectAsProtocolNotUnsupportedConfig(bool helloInvalid, string extension)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new ReceiverServer("strict receiver", new Factory(true), _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, limit.Token);
        var hello = helloInvalid
            ? Json($"{{\"role\":\"sender\",\"versions\":[3],\"name\":\"fixture\",\"receiverOptions\":{extension}}}")
            : Json("""{"role":"sender","versions":[3],"name":"fixture","receiverOptions":{"version":1,"supported":["playbackStats","futureOption"]}}""");
        await RawJsonAsync(client.GetStream(), PacketType.Hello, 0, hello.GetRawText(), limit.Token);
        var reply = await Wire.ReadAsync(client.GetStream(), limit.Token);
        if (!helloInvalid)
        {
            Assert.Equal(PacketType.Hello, reply.Type);
            var request = JsonSerializer.Serialize(new CaptureSettings().Request("fixture"));
            request = request[..^1] + ",\"receiverOptions\":" + extension + "}";
            await RawJsonAsync(client.GetStream(), PacketType.SessionRequest, 7, request, limit.Token);
            reply = await Wire.ReadAsync(client.GetStream(), limit.Token);
        }
        Assert.Equal(PacketType.Reject, reply.Type);
        Assert.Equal("PROTOCOL", reply.Object().GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task FutureCapabilityWireNegotiatesIntersectionOrFallsBackWithoutCastFailure(int revision, bool expected)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var factory = new Factory(true);
        await using var server = new ReceiverServer("future receiver", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, limit.Token);
        await Wire.WriteAsync(client.GetStream(), Packet.Control(PacketType.Hello, 0, new
        {
            role = "sender", versions = new[] { 3 }, name = "future sender",
            receiverOptions = new { version = revision, supported = new[] { "playbackStats", "futureOption" } }
        }), limit.Token);
        var hello = (await Wire.ReadAsync(client.GetStream(), limit.Token)).Object();
        Assert.Equal(expected, ReceiverOptionProtocol.Capabilities(hello).PlaybackStats);
        var request = JsonSerializer.SerializeToNode(new CaptureSettings().Request("fixture"))!;
        request["receiverOptions"] = JsonSerializer.SerializeToNode(new { version = revision, playbackStats = true, futureOption = true });
        await Wire.WriteAsync(client.GetStream(), Packet.Control(PacketType.SessionRequest, 7, request), limit.Token);
        Assert.Equal(PacketType.SessionAccept, (await Wire.ReadAsync(client.GetStream(), limit.Token)).Type);
        Assert.Equal(expected, factory.Requests.Single().EffectiveOptions.PlaybackStats);
        await Wire.WriteAsync(client.GetStream(), Packet.Control(PacketType.Stop, 7, new { reason = "future fixture complete" }), limit.Token);
        Assert.Equal(PacketType.SessionEnd, (await Wire.ReadAsync(client.GetStream(), limit.Token)).Type);
    }

    private static async Task RawJsonAsync(Stream stream, PacketType type, long session, string json, CancellationToken token)
    {
        using var valid = new MemoryStream();
        await Wire.WriteAsync(valid, Packet.Control(type, session, new { }), token);
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[Wire.HeaderSize + payload.Length];
        valid.ToArray().AsSpan(0, Wire.HeaderSize).CopyTo(frame);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(Wire.HeaderSize));
        await stream.WriteAsync(frame, token);
    }

    private sealed class Factory(bool supported, bool recovery = false) : IPlaybackFactory
    {
        public string Description => "portable typed statistics fixture";
        public bool SupportsStreamRecovery => recovery;
        public ReceiverOptionCapabilities ReceiverOptions => new(supported);
        public ConcurrentQueue<CastRequest> Requests { get; } = new();
        public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken token)
        {
            Requests.Enqueue(request);
            return ValueTask.FromResult<IPlayback>(new Output());
        }
    }
    private sealed class Output : IStreamRecoveryPlayback, IVideoPlaybackProgress
    {
        public bool HasPresentedVideo { get; private set; }
        public long? DecodedRecoveryKeyPtsUs { get; private set; }
        public long? FirstRecoverySourceAudioPtsUs => null;
        public void SetRecoveryKey(long pts) { }
        public void SetAudioRecoveryCutoff(long pts) { }
        public ValueTask ConfigureAsync(Packet packet, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRecoveryTrackAsync(Packet configuration, bool midstream, CancellationToken token) =>
            ConfigureAsync(configuration, token);
        public ValueTask ConfigureRecoveryAsync(Packet video, Packet? audio, bool midstream, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask PresentAsync(Packet packet, CancellationToken token)
        {
            HasPresentedVideo = true;
            DecodedRecoveryKeyPtsUs = packet.PtsUs;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Source(bool recovery) : IRecoverableVideoSource
    {
        public bool Prepared, Started, Disposed;
        public bool SupportsStreamRecovery => recovery;
        public string EncoderName => "portable fake encoder";
        public int Width => 320;
        public int Height => 192;
        public ValueTask PrepareAsync(CancellationToken token) { Prepared = true; return ValueTask.CompletedTask; }
        public ValueTask RequestKeyFrameAsync(CancellationToken token) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            Assert.True(Prepared);
            Started = true;
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-h264.json")));
            var frame = fixture.RootElement.GetProperty("generations")[0].GetProperty("frames")[0];
            var data = Convert.FromBase64String(frame.GetProperty("data").GetString()!);
            var configuration = Convert.FromBase64String(frame.GetProperty("configuration").GetString()!);
            while (true)
            {
                var pts = (long)((Int128)System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000 /
                    System.Diagnostics.Stopwatch.Frequency);
                yield return new(data, pts, configuration);
                await Task.Delay(50, token);
            }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
