namespace ScrcpySend.Core;

public sealed record EncodedVideo(byte[] Data, long PtsUs, byte[] Configuration);

public interface IVideoSource : IAsyncDisposable
{
    string EncoderName { get; }
    int Width { get; }
    int Height { get; }
    IAsyncEnumerable<EncodedVideo> ReadFramesAsync(CancellationToken cancellationToken);
}

public sealed class VideoQueue(int maxBytes = 8 * 1024 * 1024)
{
    private readonly Queue<EncodedVideo> frames = [];
    private int bytes;
    private readonly object gate = new();

    public void Enqueue(EncodedVideo frame)
    {
        lock (gate)
        {
            var size = checked(bytes + frame.Data.Length + frame.Configuration.Length);
            if (size > maxBytes || frames.Count >= 64 ||
                frames.Count > 0 && frame.PtsUs - frames.Peek().PtsUs > 500_000)
                throw new IOException($"CONGESTION: encoded video queue exceeded its 64-frame, {maxBytes}-byte or 500 ms bound.");
            if (frames.Count > 0 && frame.PtsUs < frames.Last().PtsUs)
                throw new ProtocolException("Video timestamps moved backwards.");
            frames.Enqueue(frame);
            bytes = size;
        }
    }

    public bool TryDequeue(out EncodedVideo? frame)
    {
        lock (gate)
        {
            if (!frames.TryDequeue(out frame)) return false;
            bytes -= frame.Data.Length + frame.Configuration.Length;
            return true;
        }
    }
}
