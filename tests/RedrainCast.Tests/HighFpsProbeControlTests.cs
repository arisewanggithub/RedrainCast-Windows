using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using RedrainCast.App;
using ScrcpyReceive.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class HighFpsProbeControlTests
{
    [Fact]
    public async Task QueuedHeartbeatCannotWritePongAfterStopAndMustReceiveSessionEnd()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var stream = new ScriptedStream();
        stream.Reply(Packet.Control(PacketType.Ping, 90, new { nonce = 7 }));
        await using var control = new HighFpsProbeControl(stream, 90);
        await control.SendAsync(Packet.Control(PacketType.Stop, 90, new { reason = "test" }), limit.Token);
        Assert.Equal(90, (await control.ReadAsync(PacketType.SessionEnd, limit.Token)).Session);
        var sent = stream.Received.ToArray();
        var stop = Array.FindIndex(sent, p => p.Type == PacketType.Stop);
        Assert.True(stop >= 0);
        Assert.DoesNotContain(sent.Skip(stop + 1), p => p.Type == PacketType.Pong);
    }

    [Fact]
    public async Task StreamingServicesHeartbeatWithoutAnExplicitControlRead()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var stream = new ScriptedStream();
        stream.Reply(Packet.Control(PacketType.Ping, 120, new { nonce = 8 }));
        await using var control = new HighFpsProbeControl(stream, 120);
        while (!stream.Received.Any(p => p.Type == PacketType.Pong)) await Task.Delay(10, limit.Token);
        await control.SendAsync(Packet.Control(PacketType.Stop, 120, new { reason = "test" }), limit.Token);
        Assert.Equal(PacketType.SessionEnd, (await control.ReadAsync(PacketType.SessionEnd, limit.Token)).Type);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrWrongSessionAcknowledgementCannotBecomeSuccess(bool wrongSession)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var stream = new ScriptedStream { Acknowledge = false };
        await using var control = new HighFpsProbeControl(stream, 90);
        await control.SendAsync(Packet.Control(PacketType.Stop, 90, new { reason = "test" }), limit.Token);
        if (wrongSession) stream.Reply(Packet.Control(PacketType.SessionEnd, 120, new { reason = "wrong" }));
        stream.End();
        await Assert.ThrowsAnyAsync<IOException>(() => control.ReadAsync(PacketType.SessionEnd, limit.Token));
    }

    [Fact]
    public async Task TransportResetIsNotAnAcknowledgement()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var stream = new ScriptedStream { Acknowledge = false };
        await using var control = new HighFpsProbeControl(stream, 120);
        await control.SendAsync(Packet.Control(PacketType.Stop, 120, new { reason = "test" }), limit.Token);
        stream.End(new IOException("Synthetic transport reset"));
        await Assert.ThrowsAnyAsync<IOException>(() => control.ReadAsync(PacketType.SessionEnd, limit.Token));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    public async Task RealReceiverHeartbeatAndStopRetireMediaBeforeControl(int fps)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        await using var receiver = new ReceiverServer("Probe control regression", new FakeFactory(), _ => { }, 0, IPAddress.Loopback);
        receiver.Start(false);
        using var controlSocket = new TcpClient { NoDelay = true };
        using var video = new TcpClient { NoDelay = true };
        using var audio = new TcpClient { NoDelay = true };
        await controlSocket.ConnectAsync(IPAddress.Loopback, receiver.Port, limit.Token);
        var control = new HighFpsProbeControl(controlSocket.GetStream(), fps);
        try
        {
            await control.SendAsync(Packet.Control(PacketType.Hello, 0,
                new { role = "sender", versions = new[] { 3 }, name = "Synthetic probe" }), limit.Token);
            await control.ReadAsync(PacketType.Hello, limit.Token);
            await control.SendAsync(Packet.Control(PacketType.SessionRequest, fps,
                new ScrcpySend.Core.CaptureSettings(MaxFps: fps, EnableAudio: false).Request("Explicit fake")), limit.Token);
            var accepted = await control.ReadAsync(PacketType.SessionAccept, limit.Token);
            var mediaToken = JsonObject.Text(accepted.Object(), "mediaToken", 64);
            foreach (var (socket, channel) in new[] { (video, "video"), (audio, "audio") })
            {
                await socket.ConnectAsync(IPAddress.Loopback, receiver.Port, limit.Token);
                await Wire.WriteAsync(socket.GetStream(), Packet.Control(PacketType.Hello, fps,
                    new { role = "media", channel, token = mediaToken }), limit.Token);
                Assert.Equal(PacketType.Hello, (await Wire.ReadAsync(socket.GetStream(), limit.Token)).Type);
            }
            // SESSION_ACCEPT precedes watchdog startup; wait for the heartbeat, not its nominal due time.
            while (!control.Journal.Any(entry => entry.StartsWith("TX Pong", StringComparison.Ordinal)))
                await Task.Delay(10, limit.Token);
            Assert.Contains(control.Journal, entry => entry.StartsWith("TX Pong", StringComparison.Ordinal));
            var videoEnded = video.GetStream().ReadAsync(new byte[1], limit.Token).AsTask();
            var audioEnded = audio.GetStream().ReadAsync(new byte[1], limit.Token).AsTask();
            await control.SendAsync(Packet.Control(PacketType.Stop, fps, new { reason = "test" }), limit.Token);
            Assert.Equal(0, await videoEnded);
            Assert.Equal(0, await audioEnded);
            Assert.Equal(fps, (await control.ReadAsync(PacketType.SessionEnd, limit.Token)).Session);
            while (receiver.IsBusy) await Task.Delay(10, limit.Token);
            var journal = control.Journal;
            var stop = Array.FindIndex(journal, entry => entry.StartsWith("TX Stop", StringComparison.Ordinal));
            Assert.DoesNotContain(journal.Skip(stop + 1), entry => entry.StartsWith("TX Pong", StringComparison.Ordinal));
        }
        finally
        {
            video.Dispose();
            audio.Dispose();
            await control.DisposeAsync();
        }
    }

    private sealed class FakeFactory : IPlaybackFactory
    {
        public string Description => "EXPLICIT FAKE outputs; no native media or capture";
        public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken token) =>
            ValueTask.FromResult<IPlayback>(new FakePlayback());
    }

    private sealed class FakePlayback : IPlayback
    {
        public ValueTask ConfigureAsync(Packet packet, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask PresentAsync(Packet packet, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedStream : Stream
    {
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        private readonly MemoryStream outgoing = new();
        private byte[] current = [];
        private int offset;
        public bool Acknowledge { get; init; } = true;
        public ConcurrentQueue<Packet> Received { get; } = new();
        public void Reply(Packet packet)
        {
            using var bytes = new MemoryStream();
            Wire.WriteAsync(bytes, packet).AsTask().GetAwaiter().GetResult();
            Assert.True(incoming.Writer.TryWrite(bytes.ToArray()));
        }
        public void End(Exception? error = null) => incoming.Writer.TryComplete(error);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (offset == current.Length)
            {
                try { current = await incoming.Reader.ReadAsync(token); }
                catch (ChannelClosedException error)
                {
                    if (error.InnerException is { } failure) throw new IOException("Synthetic connection failed.", failure);
                    return 0;
                }
                offset = 0;
            }
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            outgoing.Write(buffer.Span);
            var bytes = outgoing.ToArray();
            if (bytes.Length >= Wire.HeaderSize &&
                bytes.Length == Wire.HeaderSize + BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)))
            {
                using var packetBytes = new MemoryStream(bytes);
                var packet = Wire.ReadAsync(packetBytes).AsTask().GetAwaiter().GetResult();
                Received.Enqueue(packet);
                outgoing.SetLength(0);
                if (packet.Type == PacketType.Stop && Acknowledge)
                    Reply(Packet.Control(PacketType.SessionEnd, packet.Session, new { reason = "test" }));
            }
            return ValueTask.CompletedTask;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { End(); outgoing.Dispose(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
