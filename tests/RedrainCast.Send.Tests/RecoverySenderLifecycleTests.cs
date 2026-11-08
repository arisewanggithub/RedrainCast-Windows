using System.Net;
using System.Net.Sockets;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class RecoverySenderLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeAcceptanceClosesControlWithoutStopOrAcknowledgementWait(bool partialAcceptance)
    {
        await using var fixture = new Fixture();
        await fixture.HandshakeAsync();
        if (partialAcceptance)
        {
            using var bytes = new MemoryStream();
            await Wire.WriteAsync(bytes, fixture.Acceptance(), fixture.Limit.Token);
            await fixture.Control!.WriteAsync(bytes.ToArray().AsMemory(0, 7), fixture.Limit.Token);
        }
        fixture.Stop.Cancel();
        await fixture.Run.WaitAsync(TimeSpan.FromSeconds(1), fixture.Limit.Token);
        try { Assert.Equal(0, await fixture.Control!.ReadAsync(new byte[1], fixture.Limit.Token)); }
        catch (IOException) when (partialAcceptance) { }
        fixture.AssertSourceRetired();
    }

    [Theory]
    [InlineData("video", false)]
    [InlineData("audio", false)]
    [InlineData("video", true)]
    [InlineData("audio", true)]
    public async Task LocalStopDuringAssociationKeepsReaderForAcknowledgementAndCrossedStop(string channel, bool crossedStop)
    {
        await using var fixture = new Fixture();
        await fixture.HandshakeAsync();
        await fixture.AssociateUntilAsync(channel);
        fixture.Stop.Cancel();
        var stop = await fixture.ReadControlAsync();
        Assert.Equal(MessageType.Stop, stop.Type);
        Assert.Equal("user", StrictJson.String(stop.Json(), "reason", 1024));
        await fixture.SendControlAsync(MessageType.Ping, new { nonce = 42 });
        var pong = await fixture.ReadControlAsync();
        Assert.Equal(MessageType.Pong, pong.Type);
        Assert.Equal(42, StrictJson.Integer(pong.Json(), "nonce"));
        if (crossedStop)
        {
            await fixture.SendControlAsync(MessageType.Stop, new { reason = "receiver crossed stop" });
            var ended = await fixture.ReadControlAsync().WaitAsync(TimeSpan.FromSeconds(1), fixture.Limit.Token);
            Assert.Equal(MessageType.SessionEnd, ended.Type);
            Assert.Equal("receiver crossed stop", StrictJson.String(ended.Json(), "reason", 1024));
        }
        else
            await fixture.SendControlAsync(MessageType.SessionEnd, new { reason = "user" });
        await fixture.Run.WaitAsync(TimeSpan.FromSeconds(1), fixture.Limit.Token);
        fixture.AssertSourceRetired();
    }

    [Theory]
    [InlineData("fatal")]
    [InlineData("protocol")]
    [InlineData("eof")]
    public async Task ControlFailureDuringLocalStopIsNotConvertedToSuccess(string failure)
    {
        await using var fixture = new Fixture();
        await fixture.HandshakeAsync();
        await fixture.AssociateUntilAsync("audio");
        fixture.Stop.Cancel();
        Assert.Equal(MessageType.Stop, (await fixture.ReadControlAsync()).Type);
        if (failure == "fatal")
            await fixture.SendControlAsync(MessageType.Error,
                new { code = "DECODER_FAILED", message = "independent decoder failure", fatal = true });
        else if (failure == "eof")
            fixture.CloseControl();
        else
            await fixture.SendControlAsync(MessageType.Pong, new { invalid = "missing nonce" });
        var error = await Assert.ThrowsAsync<IOException>(
            () => fixture.Run.WaitAsync(TimeSpan.FromSeconds(1), fixture.Limit.Token));
        if (failure == "eof")
        {
            Assert.IsAssignableFrom<IOException>(error.InnerException);
            Assert.DoesNotContain("Recovery Stop acknowledgement failed", error.Message);
        }
        else Assert.Contains(failure == "fatal" ? "independent decoder failure" : "nonce", error.Message);
        fixture.AssertSourceRetired();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly CancellationTokenSource Limit = new(TimeSpan.FromSeconds(8));
        public readonly CancellationTokenSource Stop = new();
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> peers = [];
        private readonly Source source = new();
        private long session;
        public Task Run { get; }
        public NetworkStream? Control { get; private set; }

        public Fixture()
        {
            listener.Start();
            Run = new SenderSession().RunAsync(new(IPAddress.Loopback,
                ((IPEndPoint)listener.LocalEndpoint).Port, "Recovery test", false),
                new(), source, _ => { }, Stop.Token);
        }

        public async Task HandshakeAsync()
        {
            var client = await listener.AcceptTcpClientAsync(Limit.Token);
            peers.Add(client);
            Control = client.GetStream();
            Assert.Equal(MessageType.Hello, (await Wire.ReadAsync(Control, Limit.Token)).Type);
            await Wire.WriteAsync(Control, Message.Control(MessageType.Hello, 0,
                new { role = "receiver", versions = new[] { 3 }, name = "Test receiver",
                    uncappedFps = true, streamRecovery = true, streamRecoveryVersion = 1 }), Limit.Token);
            var request = await ReadControlAsync();
            Assert.Equal(MessageType.SessionRequest, request.Type);
            session = request.Session;
        }

        public Message Acceptance() => Message.Control(MessageType.SessionAccept, session,
            new { tracks = new[] { 1 }, mediaToken = new string('A', 64) });
        public void CloseControl() => peers[0].Close();
        public Task<Message> ReadControlAsync() => Wire.ReadAsync(Control!, Limit.Token).AsTask();
        public Task SendControlAsync(MessageType type, object payload) =>
            Wire.WriteAsync(Control!, Message.Control(type, session, payload), Limit.Token).AsTask();

        public async Task AssociateUntilAsync(string pendingChannel)
        {
            await Wire.WriteAsync(Control!, Acceptance(), Limit.Token);
            foreach (var channel in new[] { "video", "audio" })
            {
                var client = await listener.AcceptTcpClientAsync(Limit.Token);
                peers.Add(client);
                var hello = await Wire.ReadAsync(client.GetStream(), Limit.Token);
                Assert.Equal(MessageType.Hello, hello.Type);
                Assert.Equal(channel, StrictJson.String(hello.Json(), "channel", 16));
                if (channel == pendingChannel) return;
                await Wire.WriteAsync(client.GetStream(), Message.Control(MessageType.Hello, session,
                    new { channel }), Limit.Token);
            }
        }

        public void AssertSourceRetired()
        {
            Assert.False(source.Started);
            Assert.True(source.Disposed);
        }

        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            foreach (var peer in peers) peer.Dispose();
            listener.Stop();
            try { await Run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) { }
            finally { Stop.Dispose(); Limit.Dispose(); }
        }
    }

    private sealed class Source : IRecoverableVideoSource
    {
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public string EncoderName => "Test recovery source";
        public int Width => 640;
        public int Height => 360;
        public bool SupportsStreamRecovery => true;
        public ValueTask RequestKeyFrameAsync(CancellationToken token) => ValueTask.CompletedTask;
        public IAsyncEnumerable<EncodedVideo> ReadFramesAsync(CancellationToken token)
        {
            Started = true;
            throw new InvalidOperationException("Association cancellation must not start capture.");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
