using System.Net;
using System.Net.Sockets;
using System.Reflection;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class RecoverySenderSetupCancellationTests
{
    [Theory]
    [InlineData("request")]
    [InlineData("late-acceptance")]
    [InlineData("reject")]
    [InlineData("invalid-acceptance")]
    [InlineData("write-failure")]
    public async Task CancellationAtSetupOwnershipBoundaryRetainsOnlyAcceptedControlExchanges(string boundary)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, limit.Token);
        using var peer = await listener.AcceptTcpClientAsync(limit.Token);
        await connecting;
        using var reply = new MemoryStream();
        await Wire.WriteAsync(reply, boundary == "reject"
            ? Message.Control(MessageType.Reject, 1, new { code = "BUSY", message = "independent rejection" })
            : Message.Control(MessageType.SessionAccept, 1,
                new { tracks = new[] { 1 }, mediaToken = new string(boundary == "invalid-acceptance" ? 'X' : 'A', 64) }),
            limit.Token);
        using var control = new BoundaryControlStream(client.Client, reply.ToArray(), boundary, stop);
        var source = new SetupSource();
        var type = typeof(SenderSession).Assembly.GetType("RedrainCast.Send.Core.RecoverySender", true)!;
        var run = (Task)type.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)!.Invoke(null,
            [new ReceiverEndpoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
                new CaptureSettings(), source, null, (Action<string>)(_ => { }), 1L, control, stop.Token, null])!;
        try
        {
            if (boundary == "request")
            {
                await control.RequestPayloadStarted.Task.WaitAsync(limit.Token);
                stop.Cancel();
            }
            if (boundary == "late-acceptance")
            {
                Assert.Equal(MessageType.SessionRequest, (await Wire.ReadAsync(peer.GetStream(), limit.Token)).Type);
                var stopping = await Wire.ReadAsync(peer.GetStream(), limit.Token);
                Assert.Equal(MessageType.Stop, stopping.Type);
                Assert.Equal("user", StrictJson.String(stopping.Json(), "reason", 1024));
                await Wire.WriteAsync(peer.GetStream(), Message.Control(MessageType.SessionEnd, 1, new { reason = "user" }), limit.Token);
                await run.WaitAsync(TimeSpan.FromSeconds(1), limit.Token);
                Assert.Equal(4, control.WriteCalls);
            }
            else if (boundary == "request")
            {
                await run.WaitAsync(TimeSpan.FromSeconds(1), limit.Token);
                Assert.Equal(2, control.WriteCalls);
            }
            else
            {
                var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(1), limit.Token));
                Assert.Contains(boundary switch
                {
                    "reject" => "independent rejection",
                    "invalid-acceptance" => "Invalid media token",
                    _ => "independent request write failure"
                }, error.Message);
                Assert.Equal(2, control.WriteCalls);
            }
            Assert.True(stop.IsCancellationRequested);
            Assert.False(source.Started);
        }
        finally
        {
            stop.Cancel();
            peer.Close();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) { }
            await source.DisposeAsync();
        }
        Assert.True(source.Disposed);
    }

    private sealed class BoundaryControlStream(Socket socket, byte[] reply, string boundary, CancellationTokenSource stop)
        : NetworkStream(socket, ownsSocket: false)
    {
        private readonly MemoryStream acceptance = new(reply);
        public TaskCompletionSource RequestPayloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCalls { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++WriteCalls == 2 && boundary is "request" or "write-failure")
            {
                RequestPayloadStarted.TrySetResult();
                if (boundary == "write-failure")
                {
                    stop.Cancel();
                    throw new IOException("independent request write failure");
                }
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (acceptance.Position == acceptance.Length) return base.ReadAsync(buffer, cancellationToken);
            var count = acceptance.Read(buffer.Span);
            // A completed acceptance/rejection may already own the read when cancellation wins.
            if (acceptance.Position == acceptance.Length) stop.Cancel();
            return ValueTask.FromResult(count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) acceptance.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class SetupSource : IRecoverableVideoSource
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
            throw new InvalidOperationException("Setup cancellation must not start capture.");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
