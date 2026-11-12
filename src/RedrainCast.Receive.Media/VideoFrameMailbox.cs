namespace RedrainCast.Receive.Media;

// Only reference transfer happens under gate. Native work, release and notification never do.
internal sealed class VideoFrameMailbox<T>(Action notify, Action<T, string> release,
    Action<T>? published = null) where T : class
{
    private readonly object gate = new();
    private T? pending;
    private long revision;
    private bool closed;

    public long Revision { get { lock (gate) return revision; } }
    public bool HasPending { get { lock (gate) return pending is not null; } }

    // Both Publish and Return consume ownership, including on retirement/replacement.
    public bool Publish(T frame, long expectedRevision) => Store(frame, expectedRevision, replace: true);
    public void Return(T frame, long expectedRevision) => Store(frame, expectedRevision, replace: false);

    private bool Store(T frame, long expectedRevision, bool replace)
    {
        T? retired;
        string reason;
        bool accepted, signal;
        lock (gate)
        {
            accepted = !closed && revision == expectedRevision && (replace || pending is null);
            signal = accepted && replace && pending is null;
            retired = accepted ? pending : frame;
            reason = closed || revision != expectedRevision ? "generation_retired" : "pending_replaced";
            if (accepted) pending = frame;
        }
        try
        {
            if (retired is not null) release(retired, reason);
            if (accepted && replace) published?.Invoke(frame);
        }
        finally { if (signal) notify(); }
        return accepted;
    }

    public T? Take() => Take(out _);

    public T? Take(out long currentRevision)
    {
        lock (gate)
        {
            currentRevision = revision;
            var frame = pending;
            pending = null;
            return frame;
        }
    }

    public void Reset(string reason, bool close = false)
    {
        T? retired;
        lock (gate)
        {
            revision++;
            closed |= close;
            retired = pending;
            pending = null;
        }
        if (retired is not null) release(retired, reason);
    }
}
