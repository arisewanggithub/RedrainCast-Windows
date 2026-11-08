namespace RedrainCast.Receive.Core;

public sealed record AudioSpan(int Offset, int Frames, long FirstPtsUs, long EndPtsUs);
public sealed record AudioBlock(byte[] Data, AudioSpan[] Spans)
{
    public int Frames => Data.Length / 4;
}

public sealed class AudioBuffer
{
    public const int TargetFrames = AudioFormat.Rate / 20;
    public const int InitialMaximumFrames = TargetFrames + AudioFormat.Rate / 100;
    public const int PlayingMaximumFrames = TargetFrames * 11 / 10 + AudioFormat.Rate * 60 / 1000;
    public const int MaximumTargetFrames = AudioFormat.Rate * 150 / 1000;
    public const int MaximumBufferedFrames = MaximumTargetFrames * 11 / 10 + AudioFormat.Rate * 60 / 1000;
    public const int MaximumConvertedFrames = AudioFormat.MaximumFrames + AudioFormat.Rate / 100;
    private sealed class Chunk(byte[] data, long pts, int sourceFrames, long? sourceEndPtsUs)
    {
        public readonly byte[] Data = data;
        public readonly int Frames = data.Length / 4;
        public int Offset;
        public long Pts(int offset) => sourceEndPtsUs is { } end
            ? checked(pts + (end - pts) * offset / Frames)
            : checked(pts + (long)sourceFrames * offset * 1_000_000 / Frames / AudioFormat.Rate);
    }

    private readonly object gate = new();
    private readonly Queue<Chunk> chunks = new();
    private int frames;
    private bool playing, draining;
    private long silence, dropped;
    private int targetFrames = TargetFrames;
    private long stableFrames;
    private bool preservingSource;
    public void PreserveStartupSource(bool preserve) { lock (gate) preservingSource = preserve; }
    public int Frames { get { lock (gate) return frames; } }
    public bool Draining { get { lock (gate) return draining; } }
    public int Target { get { lock (gate) return targetFrames; } }
    public (int Frames, long Silence, long Dropped, int Target, bool PreservingSource) Snapshot
    {
        get { lock (gate) return (frames, silence, dropped, targetFrames, preservingSource); }
    }
    public void Start() { lock (gate) playing = true; }
    public void BeginDrain() { lock (gate) draining = true; }

    public void Push(byte[] data, long ptsUs, int sourceFrames, long? sourceEndPtsUs = null)
    {
        JsonObject.Require(data.Length % 4 == 0 && data.Length / 4 is > 0 and <= MaximumConvertedFrames &&
            sourceFrames is > 0 and <= AudioFormat.MaximumFrames && ptsUs >= 0,
            "Invalid converted audio block.", "AUDIO_FAILED");
        JsonObject.Require(sourceEndPtsUs is null || sourceEndPtsUs >= ptsUs,
            "Invalid converted source interval.", "AUDIO_FAILED");
        lock (gate)
        {
            JsonObject.Require(!draining, "Audio arrived during generation drain.", "AUDIO_FAILED");
            chunks.Enqueue(new(data, ptsUs, sourceFrames, sourceEndPtsUs));
            frames += data.Length / 4;
            var excess = frames - (preservingSource ? MaximumBufferedFrames :
                playing ? targetFrames * 11 / 10 + AudioFormat.Rate * 60 / 1000 :
                targetFrames + AudioFormat.Rate / 100);
            if (excess > 0)
            {
                JsonObject.Require(!preservingSource, "Startup source FIFO exceeded its bounded capacity.", "CONGESTION");
                Discard(excess);
            }
            JsonObject.Require(chunks.Count <= ReceiveQueue.MaximumEntries,
                "Decoded audio segment count exceeds its bound.", "CONGESTION");
        }
    }

    private void Discard(int count)
    {
        dropped += count;
        frames -= count;
        while (count > 0)
        {
            var chunk = chunks.Peek();
            var take = Math.Min(count, chunk.Frames - chunk.Offset);
            chunk.Offset += take;
            count -= take;
            if (chunk.Offset == chunk.Frames) chunks.Dequeue();
        }
    }

    public void DiscardBefore(long cutoffPtsUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cutoffPtsUs);
        lock (gate)
        {
            while (chunks.TryPeek(out var chunk))
            {
                if (chunk.Pts(chunk.Offset) >= cutoffPtsUs) return;
                var low = chunk.Offset;
                var high = chunk.Frames;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (chunk.Pts(middle) < cutoffPtsUs) low = middle + 1;
                    else high = middle;
                }
                var count = low - chunk.Offset;
                if (count == 0) return;
                Discard(count);
            }
        }
    }

    public AudioBlock Pull(int requestedFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedFrames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requestedFrames, AudioFormat.MaximumFrames);
        lock (gate)
        {
            var count = draining ? Math.Min(requestedFrames, frames) : requestedFrames;
            var data = new byte[count * 4];
            var spans = new List<AudioSpan>();
            var offset = 0;
            while (offset < count && chunks.TryPeek(out var chunk))
            {
                var take = Math.Min(count - offset, chunk.Frames - chunk.Offset);
                Buffer.BlockCopy(chunk.Data, chunk.Offset * 4, data, offset * 4, take * 4);
                spans.Add(new(offset, take, chunk.Pts(chunk.Offset), chunk.Pts(chunk.Offset + take)));
                chunk.Offset += take;
                frames -= take;
                offset += take;
                if (chunk.Offset == chunk.Frames) chunks.Dequeue();
            }
            silence += count - offset;
            if (playing && !draining)
            {
                var missing = count - offset;
                if (missing > 0)
                {
                    var step = AudioFormat.Rate / 100;
                    var growth = ((missing + step - 1) / step) * step;
                    targetFrames = Math.Min(MaximumTargetFrames, targetFrames + growth);
                    stableFrames = 0;
                }
                else
                {
                    stableFrames += count;
                    if (stableFrames >= AudioFormat.Rate * 10L)
                    {
                        targetFrames = Math.Max(TargetFrames, targetFrames - AudioFormat.Rate / 100);
                        stableFrames = 0;
                        var excess = frames - (targetFrames * 11 / 10 + AudioFormat.Rate * 60 / 1000);
                        if (excess > 0 && !preservingSource) Discard(excess);
                    }
                }
            }
            return new(data, spans.ToArray());
        }
    }
}
