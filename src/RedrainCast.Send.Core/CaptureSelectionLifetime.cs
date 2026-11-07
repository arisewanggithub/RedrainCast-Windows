namespace RedrainCast.Send.Core;

public sealed class CaptureSelectionLifetime : IDisposable
{
    private readonly CancellationTokenSource closed = new();
    private readonly CancellationToken token;
    private readonly object gate = new();
    private int invalidated;
    private int disposed;

    public CaptureSelectionLifetime() => token = closed.Token;
    public CancellationToken Closed => token;
    public bool IsClosed => Volatile.Read(ref invalidated) != 0;

    public void Invalidate()
    {
        lock (gate)
            if (Interlocked.Exchange(ref invalidated, 1) == 0) closed.Cancel();
    }

    public void EnsureAvailable(bool available)
    {
        if (!available) Invalidate();
        if (IsClosed) throw new IOException("The selected source closed or changed. Choose a source again.");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Invalidate();
            closed.Dispose();
        }
    }
}
