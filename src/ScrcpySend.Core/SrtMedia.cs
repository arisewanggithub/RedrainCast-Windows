using System.Net;

namespace ScrcpySend.Core;

public sealed record SrtDescriptor(string Url, string StreamId, int LatencyMs = 100)
{
    public object Json() => new { kind = "srt", container = "fmp4", url = Url, latencyMs = LatencyMs, streamId = StreamId };
}

public interface ISrtMedia : IAsyncDisposable
{
    SrtDescriptor Prepare(IPAddress localAddress, IPAddress receiverAddress, long session);
    Task RunAsync(IVideoSource video, IAudioSource? audio, CaptureSettings settings, CancellationToken token);
    void Abort();
}
