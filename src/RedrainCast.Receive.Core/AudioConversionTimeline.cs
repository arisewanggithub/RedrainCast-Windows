namespace RedrainCast.Receive.Core;

public sealed class AudioConversionTimeline
{
    private sealed class Source(long ptsUs, int frames)
    {
        public readonly long PtsUs = ptsUs;
        public readonly int Frames = frames;
        public int Offset;
    }
    private readonly Queue<Source> sources = new();
    public int PendingFrames { get; private set; }

    public void Append(long ptsUs, int frames)
    {
        JsonObject.Require(ptsUs >= 0 && frames is > 0 and <= AudioFormat.MaximumFrames,
            "Invalid regulation source interval.", "AUDIO_FAILED");
        JsonObject.Require(sources.Count < ReceiveQueue.MaximumEntries,
            "Regulation source history exceeded its bound.", "CONGESTION");
        sources.Enqueue(new(ptsUs, frames));
        PendingFrames = checked(PendingFrames + frames);
    }

    public void Submit(AudioBuffer buffer, byte[] converted, int retainedSourceFrames)
    {
        var outputFrames = converted.Length / 4;
        JsonObject.Require(converted.Length % 4 == 0 && outputFrames > 0 &&
            retainedSourceFrames >= 0 && retainedSourceFrames < PendingFrames,
            "Converted audio lost its source interval.", "AUDIO_FAILED");
        var consumed = PendingFrames - retainedSourceFrames;
        var mappedSource = 0;
        var mappedOutput = 0;
        while (mappedSource < consumed)
        {
            var source = sources.Peek();
            var count = Math.Min(source.Frames - source.Offset, consumed - mappedSource);
            var end = (int)((long)(mappedSource + count) * outputFrames / consumed);
            if (end > mappedOutput)
            {
                var pts = checked(source.PtsUs + source.Offset * 1_000_000L / AudioFormat.Rate);
                var sourceEnd = checked(source.PtsUs + (source.Offset + count) * 1_000_000L / AudioFormat.Rate);
                buffer.Push(converted.AsSpan(mappedOutput * 4, (end - mappedOutput) * 4).ToArray(), pts, count, sourceEnd);
            }
            source.Offset += count;
            mappedSource += count; mappedOutput = end;
            if (source.Offset == source.Frames) sources.Dequeue();
        }
        PendingFrames = retainedSourceFrames;
    }
}
