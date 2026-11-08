using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class HdrAdmissionTests
{
    private sealed class Playback : IPlayback
    {
        public ValueTask ConfigureAsync(Packet configuration, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask PresentAsync(Packet media, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Factory : IPlaybackFactory
    {
        public string Description => "HDR admission fixture";
        public HdrReceiverCapabilities HdrCapabilities { get; set; } = new(true, true, true);
        public CastRequest? Request { get; private set; }
        public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken token)
        {
            Request = request;
            return ValueTask.FromResult<IPlayback>(new Playback());
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task HdrAdmissionRequiresOfferAndCurrentCapability(bool offered, bool stillAvailable, bool accepted)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var factory = new Factory();
        await using var server = new ReceiverServer("HDR fixture", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var peer = new TcpClient { NoDelay = true };
        await peer.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = peer.GetStream();
        var hello = JsonData.Object(("role", "sender"), ("versions", JsonData.Value(new[] { 3 })), ("name", "HDR sender"));
        if (offered) hello["hdrVideoVersion"] = HdrVideoProtocol.Version;
        await Wire.WriteAsync(stream, Packet.Control(PacketType.Hello, 0, hello), timeout.Token);
        var response = await Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(PacketType.Hello, response.Type);
        Assert.Equal(offered, response.Object().TryGetProperty("receiveCapabilities", out _));
        if (!stillAvailable) factory.HdrCapabilities = HdrReceiverCapabilities.None;
        var request = JsonNode.Parse(JsonSerializer.Serialize(RequestTests.Settings(false)))!.AsObject();
        request["video"]!["codec"] = "h265";
        var color = new HdrVideoFormat(true, new(1000, 50, 1000, 400));
        HdrVideoProtocol.AddFormat(request, color);
        await Wire.WriteAsync(stream, Packet.Control(PacketType.SessionRequest, 1, request), timeout.Token);
        var admission = await Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(accepted ? PacketType.SessionAccept : PacketType.Reject, admission.Type);
        if (accepted)
        {
            Assert.Equal(color, factory.Request!.ColorFormat);
            HdrVideoProtocol.RequireAcknowledgement(admission.Object(), color);
            await Wire.WriteAsync(stream, Packet.Control(PacketType.Stop, 1,
                JsonData.Object(("reason", "fixture complete"))), timeout.Token);
            Assert.Equal(PacketType.SessionEnd, (await Wire.ReadAsync(stream, timeout.Token)).Type);
        }
        else
        {
            Assert.Null(factory.Request);
            Assert.Equal("UNSUPPORTED_CONFIG", admission.Object().GetProperty("code").GetString());
        }
    }
}
