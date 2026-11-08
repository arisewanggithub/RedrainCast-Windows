namespace RedrainCast.Send.Core;

public sealed class FramePacer
{
    private const long TicksPerSecond = 10_000_000;
    private readonly int fps;
    private readonly long toleranceTicks;
    private long origin = -1;
    private long previousTimestamp = -1;
    private Int128 nextFrame = 1;

    public FramePacer(int fps)
    {
        if (fps is < 0 or > 120) throw new ArgumentOutOfRangeException(nameof(fps), "FPS must be 0 (uncapped) or from 1 to 120.");
        this.fps = fps;
        toleranceTicks = fps == 0 ? 0 : Math.Min(TimeSpan.TicksPerMillisecond, TicksPerSecond / (fps * 20));
    }

    public bool ShouldCapture(long timestamp100ns)
    {
        if (timestamp100ns < 0 || timestamp100ns < previousTimestamp)
            throw new ArgumentException("Capture timestamps must be nonnegative and monotonic.");
        previousTimestamp = timestamp100ns;
        if (fps == 0) return true;
        if (origin < 0) { origin = timestamp100ns; return true; }
        var elapsed = (Int128)(timestamp100ns - origin) * fps;
        var deadline = nextFrame * TicksPerSecond;
        // Keep the rational phase through small timestamp jitter; never rebase on each accepted frame.
        if (elapsed + (Int128)toleranceTicks * fps < deadline) return false;
        if (elapsed >= deadline + TicksPerSecond)
        {
            origin = timestamp100ns;
            nextFrame = 1;
        }
        else nextFrame++;
        return true;
    }

}

public sealed class DeferredFramePacer(int fps)
{
    private readonly FramePacer pacer = new(fps);
    private readonly bool uncapped = fps == 0;
    public bool HasPending { get; private set; }
    private long latestTimestamp = -1;

    public void Offer(long sourceTimestamp)
    {
        if (sourceTimestamp < 0 || sourceTimestamp < latestTimestamp)
            throw new ArgumentException("Capture timestamps must be nonnegative and monotonic.");
        if (uncapped && sourceTimestamp == latestTimestamp) return;
        latestTimestamp = sourceTimestamp;
        HasPending = true;
    }

    public bool TryTake(long clockTimestamp, out long sourceTimestamp)
    {
        sourceTimestamp = latestTimestamp;
        if (!HasPending || !pacer.ShouldCapture(clockTimestamp)) return false;
        HasPending = false;
        return true;
    }

    public void DiscardPending() => HasPending = false;
}
