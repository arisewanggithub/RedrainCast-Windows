using RedrainCast.Protocol;
using RedrainCast.Receive.Core;

namespace RedrainCast.Receive.Media;

internal sealed record PresentationRequest(
    int Width, int Height, float ScaleX, float ScaleY, HdrDisplayPolicy Display);

internal sealed class PresentationRequestState
{
    private readonly object gate = new();
    private PresentationRequest desired = new(640, 360, 1, 1, HdrDisplayPolicy.Sdr);
    private PresentationRequest? successful;
    private readonly VideoPresentationStatisticsCache statistics = new();

    public PresentationRequest Desired => Volatile.Read(ref desired);
    public PresentationRequest? Successful => Volatile.Read(ref successful);
    public bool HasPending => !ReferenceEquals(Desired, Successful);
    public VideoPresentationStatistics? Statistics => statistics.Snapshot;
    public void ResetStatistics() => statistics.Reset();

    public bool SetViewport(int width, int height, float scaleX, float scaleY)
    {
        if (width <= 0 || height <= 0) return false; // Hidden/minimized surfaces retain the last real viewport.
        if (width > 16384 || height > 16384) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(scaleX) || !float.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleX));
        lock (gate)
        {
            var current = desired;
            if (current.Width == width && current.Height == height && current.ScaleX == scaleX && current.ScaleY == scaleY)
                return false;
            Volatile.Write(ref desired, current with { Width = width, Height = height, ScaleX = scaleX, ScaleY = scaleY });
            return true;
        }
    }

    public bool SetDisplayPolicy(HdrDisplayPolicy policy)
    {
        policy.Validate();
        lock (gate)
        {
            if (desired.Display == policy) return false;
            Volatile.Write(ref desired, desired with { Display = policy });
            return true;
        }
    }

    // The native render gate serializes callers, but never UI updates. Only the exact
    // snapshot used by a successful presentation is acknowledged; newer requests survive.
    public bool TryRender(HdrVideoFormat format, Func<PresentationRequest, bool> render)
    {
        var request = Desired;
        if (!render(request)) return false;
        statistics.Presented(format, request.Display.NativeHdr);
        Volatile.Write(ref successful, request);
        return true;
    }
}
