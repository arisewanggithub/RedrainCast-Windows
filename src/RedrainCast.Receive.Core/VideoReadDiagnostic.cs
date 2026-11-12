using System.Diagnostics;

namespace RedrainCast.Receive.Core;

public sealed record VideoReadObservation(string Stage, long Qpc, long ReadId,
    uint Generation = 0, long PtsUs = -1, int Bytes = 0, int AsyncReads = 0,
    int SyncReads = 0, string? Outcome = null);

internal sealed class VideoReadDiagnostic(long readId, Action<VideoReadObservation> observe)
{
    private uint generation;
    private long ptsUs = -1;
    public int HeaderAsyncReads { get; private set; }
    public int HeaderSyncReads { get; private set; }
    public async ValueTask ReadHeaderAsync(Stream input, Memory<byte> bytes, CancellationToken token)
    {
        var read = Wire.ReadExactlyAsync(input, bytes, token);
        if (read.IsCompleted) HeaderSyncReads++; else HeaderAsyncReads++;
        await read;
    }
    public void Header(Packet packet) { generation = packet.Generation; ptsUs = packet.PtsUs; }
    public void Record(string stage, int bytes = 0, int asyncReads = 0, int syncReads = 0,
        string? outcome = null) =>
        observe(new(stage, Stopwatch.GetTimestamp(), readId, generation, ptsUs,
            bytes, asyncReads, syncReads, outcome));
}
