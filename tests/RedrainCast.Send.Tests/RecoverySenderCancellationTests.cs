using System.Net;
using System.Net.Sockets;
using System.Reflection;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class RecoverySenderCancellationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReceiverStopCancelsAnInProgressMediaPayloadWithoutCancelingControl(bool audioLane, bool localStopPending)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, limit.Token);
        using var peer = await listener.AcceptTcpClientAsync(limit.Token);
        await connecting;
        using var stopBytes = new MemoryStream();
        await Wire.WriteAsync(stopBytes, Message.Control(MessageType.Stop, 1, new { reason = "receiver stop" }), limit.Token);
        using var control = new StopControlStream(client.Client, stopBytes.ToArray());
        using var lane = new BlockedPayloadStream(client.Client);
        var type = typeof(SenderSession).Assembly.GetType("RedrainCast.Send.Core.RecoverySender", throwOnError: true)!;
        var sender = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [new CaptureSettings(), new UnusedSource(), null, (Action<string>)(_ => { }), 1L, control], null)!;
        T Field<T>(string name) => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
        Task Invoke(string name, params object[] arguments) =>
            (Task)type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sender, arguments)!;

        var media = Field<CancellationTokenSource>("media");
        var transport = Field<CancellationTokenSource>("transport");
        var laneWrites = Field<CancellationTokenSource>("laneWrites");
        var queue = Field<Queue<Message>>(audioLane ? "audio" : "video");
        var wake = Field<SemaphoreSlim>(audioLane ? "audioWake" : "videoWake");
        var track = audioLane ? 2u : 1u;
        queue.Enqueue(new(MessageType.StreamConfig, 1, track, 1, -1, 0, """{"codec":"fixture"}"""u8.ToArray()));
        queue.Enqueue(new(MessageType.StreamConfig, 1, track, 1, -1, 0, """{"codec":"must-not-write"}"""u8.ToArray()));
        var writing = Invoke("WriteLaneAsync", queue, wake, lane);
        try
        {
            await lane.PayloadStarted.Task.WaitAsync(limit.Token);
            Assert.False(writing.IsCompleted);
            if (localStopPending)
            {
                media.Cancel();
                Assert.False(laneWrites.IsCancellationRequested);
                Assert.False(writing.IsCompleted);
            }
            await Invoke("ReadControlsAsync").WaitAsync(limit.Token);
            Assert.True(media.IsCancellationRequested);
            Assert.True(laneWrites.IsCancellationRequested);
            Assert.False(transport.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => writing.WaitAsync(TimeSpan.FromSeconds(1), limit.Token));
            await Invoke("Monitor", Task.FromCanceled(media.Token), "CAPTURE_FAILED");
            var terminal = Field<object>("terminal");
            Assert.Null(terminal.GetType().GetProperty("Error")!.GetValue(terminal));
            Assert.Equal(2, lane.WriteCalls);
            Assert.Single(queue);
        }
        finally
        {
            media.Cancel();
            laneWrites.Cancel();
            transport.Cancel();
            try { await writing.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (OperationCanceledException) { }
            media.Dispose();
            transport.Dispose();
            laneWrites.Dispose();
            Field<SemaphoreSlim>("videoWake").Dispose();
            Field<SemaphoreSlim>("audioWake").Dispose();
            Field<SemaphoreSlim>("controlGate").Dispose();
        }
    }

    private sealed class StopControlStream(Socket socket, byte[] bytes) : NetworkStream(socket, ownsSocket: false)
    {
        private readonly MemoryStream input = new(bytes);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            input.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) input.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class BlockedPayloadStream(Socket socket) : NetworkStream(socket, ownsSocket: false)
    {
        public TaskCompletionSource PayloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCalls { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++WriteCalls == 1) return;
            PayloadStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class UnusedSource : IRecoverableVideoSource
    {
        public string EncoderName => "Unused";
        public int Width => 640;
        public int Height => 360;
        public bool SupportsStreamRecovery => true;
        public ValueTask RequestKeyFrameAsync(CancellationToken token) => throw new InvalidOperationException("No capture expected.");
        public IAsyncEnumerable<EncodedVideo> ReadFramesAsync(CancellationToken token) =>
            throw new InvalidOperationException("No capture expected.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
