using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RedrainCast.Receive.Core;
using RedrainCast.Send.Core;
using Xunit;
using ReceiveWire = RedrainCast.Receive.Core.Wire;

namespace RedrainCast.Tests;

public sealed class DynamicReceiverTests
{
    [Fact]
    public async Task AssignedPortRemainsReadableDuringShutdown()
    {
        var server = new ReceiverServer("Shutdown", new UnavailablePlaybackFactory(), _ => { }, bindAddress: IPAddress.Loopback);
        Assert.Throws<InvalidOperationException>(() => server.Port);
        server.Start(false);
        var port = server.Port;
        await server.DisposeAsync();
        Assert.Equal(port, server.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    public void MalformedIdentityIsRejectedExplicitly(string identity)
    {
        var packet = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocol = "screencopy", version = 3, name = "Invalid", port = 50432, state = "available", instanceId = identity
        });
        Assert.Throws<RedrainCast.Send.Core.ProtocolException>(() =>
            new DiscoveryTable().Observe(packet, IPAddress.Loopback, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task DefaultListenersHaveIndependentAssignedPortsAndAcceptConnections()
    {
        await using var first = new ReceiverServer("First", new UnavailablePlaybackFactory(), _ => { }, bindAddress: IPAddress.Loopback);
        await using var second = new ReceiverServer("Second", new UnavailablePlaybackFactory(), _ => { }, bindAddress: IPAddress.Loopback);
        first.Start(false);
        second.Start(false);
        Assert.InRange(first.Port, 1, 65535);
        Assert.InRange(second.Port, 1, 65535);
        Assert.NotEqual(first.Port, second.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var server in new[] { first, second })
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            await ReceiveWire.WriteAsync(client.GetStream(), Packet.Control(PacketType.Hello, 0,
                new { role = "sender", versions = new[] { 3 }, name = "Dynamic port test" }), timeout.Token);
            Assert.Equal(PacketType.Hello, (await ReceiveWire.ReadAsync(client.GetStream(), timeout.Token)).Type);
        }
    }

    [Fact]
    public async Task AdvertisementsUseActualPortsAndReplaceRetiredEndpointsByIdentity()
    {
        var id = Guid.NewGuid().ToString("N");
        await using var old = new ReceiverServer("Same instance", new UnavailablePlaybackFactory(), _ => { },
            bindAddress: IPAddress.Loopback, instanceId: id);
        await using var current = new ReceiverServer("Same instance", new UnavailablePlaybackFactory(), _ => { },
            bindAddress: IPAddress.Loopback, instanceId: id);
        old.Start(false);
        current.Start(false);
        var table = new DiscoveryTable();
        var now = DateTimeOffset.UtcNow;
        Assert.True(table.Observe(old.Advertisement, IPAddress.Loopback, now));
        Assert.Equal(old.Port, Assert.Single(table.Snapshot(now)).Port);
        Assert.True(table.Observe(current.Advertisement, IPAddress.Loopback, now));
        var endpoint = Assert.Single(table.Snapshot(now));
        Assert.Equal(current.Port, endpoint.Port);
        Assert.Equal(id, endpoint.InstanceId);
    }

    [Fact]
    public void IdentityUpdatesDoNotHideOtherInterfacesOrOtherLocalInstances()
    {
        var table = new DiscoveryTable();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        table.Observe(RedrainCast.Receive.Core.Discovery.Advertisement("First", 51000, false, id), IPAddress.Loopback, now);
        table.Observe(RedrainCast.Receive.Core.Discovery.Advertisement("First", 51000, false, id), IPAddress.Parse("192.0.2.1"), now);
        table.Observe(RedrainCast.Receive.Core.Discovery.Advertisement("Second", 52000, false, Guid.NewGuid().ToString("N")), IPAddress.Loopback, now);
        table.Observe(RedrainCast.Receive.Core.Discovery.Advertisement("First", 53000, false, id), IPAddress.Loopback, now);
        var endpoints = table.Snapshot(now);
        Assert.Equal(3, endpoints.Count);
        Assert.Contains(endpoints, endpoint => endpoint.Address.Equals(IPAddress.Parse("192.0.2.1")) && endpoint.Port == 51000);
        Assert.Contains(endpoints, endpoint => endpoint.Port == 52000);
        Assert.Contains(endpoints, endpoint => endpoint.Port == 53000);
    }

    [Fact]
    public async Task MultipleDiscoveryListenersReceiveTheSameBroadcast()
    {
        using var reservation = new UdpClient(AddressFamily.InterNetwork);
        reservation.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        reservation.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        await using var first = new ReceiverServer("First", new UnavailablePlaybackFactory(), _ => { }, bindAddress: IPAddress.Loopback);
        await using var second = new ReceiverServer("Second", new UnavailablePlaybackFactory(), _ => { }, bindAddress: IPAddress.Loopback);
        first.Start(false);
        second.Start(false);
        using var stop = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(IReadOnlyList<ReceiverEndpoint> items, TaskCompletionSource completion)
        {
            if (items.Any(item => item.Port == first.Port) && items.Any(item => item.Port == second.Port))
                completion.TrySetResult();
        }
        var one = new ReceiverDiscovery().RunAsync(items => Observe(items, firstSeen), _ => { }, stop.Token, port);
        var two = new ReceiverDiscovery().RunAsync(items => Observe(items, secondSeen), _ => { }, stop.Token, port);
        try
        {
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)) { EnableBroadcast = true };
            var destination = new IPEndPoint(IPAddress.Parse("127.255.255.255"), port);
            await sender.SendAsync(first.Advertisement, destination, timeout.Token);
            await sender.SendAsync(second.Advertisement, destination, timeout.Token);
            await Task.WhenAll(firstSeen.Task, secondSeen.Task).WaitAsync(timeout.Token);
        }
        finally
        {
            stop.Cancel();
            foreach (var task in new[] { one, two })
            {
                try { await task; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
    }
}
