using System.Threading.Channels;
using RedrainCast.Receive.Core;

namespace RedrainCast.Receive.Media;

// Link the production coordinator against deterministic native-boundary doubles.
public sealed class AudioDecoder : IDisposable
{
    public static readonly List<AudioDecoder> Instances = [];
    public Action? Decoding;
    public int Disposals;
    private bool decoding;
    public static void ValidateCodec(string codec) { }
    public AudioDecoder(AudioFormat format) => Instances.Add(this);
    public DecodedAudio Decode(Packet packet)
    {
        ObjectDisposedException.ThrowIf(Disposals != 0, this);
        decoding = true;
        try { Decoding?.Invoke(); return new(packet.Payload, packet.PtsUs); }
        finally { decoding = false; }
    }
    public void Dispose()
    {
        if (decoding) throw new InvalidOperationException("Decoder disposed during decode.");
        Disposals++;
    }
}

public sealed record DecodedAudio(byte[] Data, long PtsUs);

public sealed class AudioRegulator : IDisposable
{
    public static readonly List<AudioRegulator> Instances = [];
    public static Action? Creating;
    public AudioBuffer Buffer { get; } = new();
    public string Diagnostics => "";
    public Action? Converting;
    public int Disposals, Drains;
    private bool converting;
    public AudioRegulator() { Instances.Add(this); Creating?.Invoke(); }
    public void Push(DecodedAudio decoded)
    {
        ObjectDisposedException.ThrowIf(Disposals != 0, this);
        converting = true;
        try
        {
            Converting?.Invoke();
            Buffer.Push(decoded.Data, decoded.PtsUs, decoded.Data.Length / 4);
        }
        finally { converting = false; }
    }
    public void BeginDrain()
    {
        if (converting) throw new InvalidOperationException("Regulator drained during conversion.");
        Drains++; Buffer.BeginDrain();
    }
    public void Dispose()
    {
        if (converting) throw new InvalidOperationException("Regulator disposed during conversion.");
        Disposals++;
    }
}

public sealed class WasapiOutput : IDisposable
{
    public static readonly List<WasapiOutput> Instances = [];
    private readonly AutoResetEvent ready = new(false);
    private readonly object gate = new();
    public readonly Channel<int> Writes = Channel.CreateBounded<int>(64);
    public readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action? BeforePadding;
    public Action? ReadingPosition;
    public bool Started { get; private set; }
    public long Written { get; private set; }
    public uint Capacity => 480;
    private uint padding;
    private long played;
    public int Disposals, Drains, WritesAfterDispose;
    public int? WorkerThread;
    public string Diagnostics => "";
    public (uint Padding, long Played)? CachedStatistics { get { lock (gate) return (padding, played); } }
    public WasapiOutput() => Instances.Add(this);
    public static IDisposable AudioThread() => new Lease();
    private sealed class Lease : IDisposable { public void Dispose() { } }
    public long PlayedFrames() { ReadingPosition?.Invoke(); lock (gate) return played; }
    public uint BufferedFrames
    {
        get { BeforePadding?.Invoke(); lock (gate) return padding; }
    }
    public int Write(byte[] data, int offset, int frames)
    {
        lock (gate)
        {
            if (Disposals != 0) { WritesAfterDispose++; throw new ObjectDisposedException(nameof(WasapiOutput)); }
            if (WorkerThread is { } worker && worker != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Output changed threads.");
            WorkerThread = Environment.CurrentManagedThreadId;
            Written += frames;
            padding += (uint)frames;
            Writes.Writer.TryWrite(frames);
            return frames;
        }
    }
    public void Start() => Started = true;
    public int WaitForBuffer(CancellationToken token)
    {
        Waiting.TrySetResult();
        var result = WaitHandle.WaitAny([ready, token.WaitHandle]);
        if (result == 1) token.ThrowIfCancellationRequested();
        return result;
    }
    public void Consume()
    {
        lock (gate) { played = Written; padding = 0; }
        ready.Set();
    }
    public void Wake() => ready.Set();
    public void CheckHealth() { }
    public void BeginDrain() { Drains++; }
    public void StopForRecovery() => Stopped.TrySetResult();
    public void Dispose() { Disposals++; ready.Dispose(); }
}
