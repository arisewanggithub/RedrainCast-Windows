using System.Collections.Concurrent;
using System.Threading.Channels;
using ScrcpyReceive.Core;

namespace RedrainCast.App;

internal sealed class HighFpsProbeControl : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly long session;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<Packet> received = Channel.CreateBounded<Packet>(16);
    private readonly ConcurrentQueue<string> journal = new();
    private readonly Task reader;
    private int stopping;
    public string[] Journal => journal.ToArray();

    public HighFpsProbeControl(Stream stream, long session)
    {
        this.stream = stream;
        this.session = session;
        reader = ReceiveAsync();
    }

    public async ValueTask SendAsync(Packet packet, CancellationToken token)
    {
        await writer.WaitAsync(token);
        try
        {
            if (Volatile.Read(ref stopping) != 0) throw new IOException("Probe control is already stopping.");
            if (packet.Type == PacketType.Stop) Volatile.Write(ref stopping, 1);
            await Wire.WriteAsync(stream, packet, token);
            journal.Enqueue($"TX {packet.Type} session={packet.Session}");
        }
        finally { writer.Release(); }
    }

    public async Task<Packet> ReadAsync(PacketType expected, CancellationToken token)
    {
        Packet packet;
        try { packet = await received.Reader.ReadAsync(token); }
        catch (ChannelClosedException error)
        {
            throw new IOException($"Probe control ended before {expected}.", error.InnerException ?? error);
        }
        if (packet.Type != expected)
            throw new IOException($"Expected {expected}; received {packet.Type} for {session}: {packet.Object()}");
        return packet;
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (true)
            {
                var packet = await Wire.ReadAsync(stream, lifetime.Token);
                journal.Enqueue($"RX {packet.Type} session={packet.Session}");
                JsonObject.Require(packet.Session == (packet.Type == PacketType.Hello ? 0 : session),
                    "Probe control session mismatch.");
                if (packet.Type == PacketType.Ping)
                {
                    var nonce = JsonObject.Number(packet.Object(), "nonce");
                    await writer.WaitAsync(lifetime.Token);
                    try
                    {
                        // Serialize with STOP: a queued heartbeat must never write after local termination.
                        if (Volatile.Read(ref stopping) == 0)
                        {
                            await Wire.WriteAsync(stream, Packet.Control(PacketType.Pong, session, new { nonce }), lifetime.Token);
                            journal.Enqueue($"TX Pong session={session}");
                        }
                        else journal.Enqueue($"Ping retired after Stop session={session}");
                    }
                    finally { writer.Release(); }
                    continue;
                }
                if (packet.Type == PacketType.SessionEnd)
                    JsonObject.Require(Volatile.Read(ref stopping) != 0, "Probe received an unsolicited SESSION_END.");
                await received.Writer.WriteAsync(packet, lifetime.Token);
                if (packet.Type == PacketType.SessionEnd) break;
            }
            received.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { received.Writer.TryComplete(); }
        catch (Exception error) { received.Writer.TryComplete(error); }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await reader;
        writer.Dispose();
        lifetime.Dispose();
    }
}
