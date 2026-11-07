using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using RedrainCast.Core;
using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class SessionTests
{
    [Theory]
    [InlineData(CastRole.Sending)]
    [InlineData(CastRole.Receiving)]
    public void SessionRolesAreExclusiveAndLeasesAreIdempotent(CastRole role)
    {
        var roles = new SessionRoles();
        var first = roles.TryAcquire(role);
        Assert.NotNull(first);
        Assert.Equal(role, roles.Role);
        Assert.Null(roles.TryAcquire(CastRole.Sending));
        Assert.Null(roles.TryAcquire(CastRole.Receiving));
        first.Dispose();
        Assert.Equal(CastRole.Idle, roles.Role);
        using var next = roles.TryAcquire(CastRole.Receiving);
        Assert.NotNull(next);
        first.Dispose();
        Assert.Equal(CastRole.Receiving, roles.Role);
    }

    [Fact]
    public async Task SimultaneousIncomingAndOutgoingAttemptsHaveExactlyOneWinner()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var roles = new SessionRoles();
            using var start = new ManualResetEventSlim();
            var send = Task.Run(() => { start.Wait(); return roles.TryAcquire(CastRole.Sending); });
            var receive = Task.Run(() => { start.Wait(); return roles.TryAcquire(CastRole.Receiving); });
            start.Set();
            var leases = await Task.WhenAll(send, receive);
            Assert.Single(leases.OfType<IDisposable>());
            foreach (var lease in leases) lease?.Dispose();
            Assert.Equal(CastRole.Idle, roles.Role);
        }
    }

    [Theory]
    [InlineData("192.168.1.42 : 27184", "192.168.1.42:27184")]
    [InlineData(" 192.168.1.42:27184 ", "192.168.1.42:27184")]
    [InlineData("[::1] : 27184", "[::1]:27184")]
    public void DisplayHasSpacesButCopyIsCanonical(string input, string canonical)
    {
        var endpoint = EndpointText.Parse(input);
        Assert.Equal(canonical, EndpointText.Format(endpoint.Address, endpoint.Port, false));
        Assert.Contains(" : ", EndpointText.Format(endpoint.Address, endpoint.Port));
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.42")]
    [InlineData("hostname:27184")]
    [InlineData("192.168.1.42 : 0")]
    [InlineData("192.168.1.42 : 65536")]
    [InlineData("0.0.0.0:27184")]
    [InlineData("255.255.255.255:27184")]
    [InlineData("224.0.0.1:27184")]
    [InlineData("::1:27184")]
    public void InvalidManualDestinationsExplainTheProblem(string input) =>
        Assert.Throws<ArgumentException>(() => EndpointText.Parse(input));

    [Fact]
    public async Task RealReceiverRejectsIncomingRequestWhileOutgoingRoleIsReserved()
    {
        var roles = new SessionRoles();
        using var outgoing = roles.TryAcquire(CastRole.Sending);
        var opened = false;
        await using var receiver = new ReceiverServer("RedrainCast test", new TestFactory(() => opened = true), _ => { },
            0, IPAddress.Loopback, () => roles.TryAcquire(CastRole.Receiving));
        receiver.Start(false);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = client.GetStream();
        await Wire.WriteAsync(stream, Packet.Control(PacketType.Hello, 0,
            new { role = "sender", versions = new[] { 3 }, name = "Other" }), deadline.Token);
        Assert.Equal(PacketType.Hello, (await Wire.ReadAsync(stream, deadline.Token)).Type);
        await Wire.WriteAsync(stream, Packet.Control(PacketType.SessionRequest, 1, new { }), deadline.Token);
        var reply = await Wire.ReadAsync(stream, deadline.Token);
        Assert.Equal(PacketType.Reject, reply.Type);
        Assert.Equal("BUSY", JsonObject.Text(reply.Object(), "code"));
        Assert.False(opened);
        Assert.Equal(CastRole.Sending, roles.Role);
    }

    [Fact]
    public async Task RejectedPreflightReleasesIncomingRole()
    {
        var roles = new SessionRoles();
        var notices = new System.Collections.Concurrent.ConcurrentQueue<ReceiverNotice>();
        await using var receiver = new ReceiverServer("RedrainCast test", new UnavailablePlaybackFactory(), notices.Enqueue,
            0, IPAddress.Loopback, () => roles.TryAcquire(CastRole.Receiving));
        receiver.Start(false);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = client.GetStream();
        await Wire.WriteAsync(stream, Packet.Control(PacketType.Hello, 0,
            new { role = "sender", versions = new[] { 3 }, name = "Other" }), deadline.Token);
        _ = await Wire.ReadAsync(stream, deadline.Token);
        await Wire.WriteAsync(stream, Packet.Control(PacketType.SessionRequest, 1,
            new RedrainCast.Send.Core.CaptureSettings().Request("Test encoder")), deadline.Token);
        Assert.Equal(PacketType.Reject, (await Wire.ReadAsync(stream, deadline.Token)).Type);
        while (roles.Role != CastRole.Idle) await Task.Delay(10, deadline.Token);
        using var sending = roles.TryAcquire(CastRole.Sending);
        Assert.NotNull(sending);
    }

    [Fact]
    public void HomeHasOneDevicePickerAndNoManualListenerOrRefreshControls()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        var clicks = document.Descendants().Select(element => element.Attribute("Click")?.Value).OfType<string>().ToArray();
        Assert.DoesNotContain(clicks, value => value.Contains("Refresh", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("StopReceiving", StringComparison.Ordinal) || value.Contains("StartReceiving", StringComparison.Ordinal));
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName is "ComboBox" or "ToggleSwitch");
        var name = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        Assert.Single(document.Descendants(), element => element.Attribute(name)?.Value == "ReceiverList");
        Assert.Contains(document.Descendants(), element => element.Attribute(name)?.Value == "ReadinessTitle" &&
            int.Parse(element.Attribute("FontSize")!.Value) >= 24);
    }

    private sealed class TestFactory(Action open) : IPlaybackFactory
    {
        public string Description => "Admission test";
        public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken cancellationToken)
        {
            open();
            return ValueTask.FromException<IPlayback>(new InvalidOperationException("Should not be reached."));
        }
    }
}
