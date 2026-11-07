using System.Threading.Channels;

namespace ScrcpySend.Core;

public sealed class CaptureProgress
{
    private readonly Channel<bool> arrivals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true
    });
    private Exception? failure;
    private int outstanding;
    private TimeSpan lastProgress;
    private int keyRequested, completed;
    private TimeSpan? lastKeyRequest;
    private bool refreshPending;

    public void Arrived() => arrivals.Writer.TryWrite(true);
    public void RequestKeyFrame()
    {
        if (Volatile.Read(ref completed) == 0 && Interlocked.Exchange(ref keyRequested, 1) == 0)
            arrivals.Writer.TryWrite(true);
    }
    public TimeSpan? KeyRequestDelay(TimeSpan now) => Volatile.Read(ref keyRequested) == 0 ? null :
        lastKeyRequest is { } last ? TimeSpan.FromTicks(Math.Max(0, (last + TimeSpan.FromMilliseconds(500) - now).Ticks)) :
        TimeSpan.Zero;
    public bool TryTakeKeyRequest(TimeSpan now)
    {
        if (KeyRequestDelay(now) is not { } delay || delay > TimeSpan.Zero) return false;
        if (Interlocked.Exchange(ref keyRequested, 0) == 0) return false;
        lastKeyRequest = now;
        refreshPending = true;
        return true;
    }
    public bool TryTakeIdleRefresh()
    {
        if (!refreshPending || outstanding != 0) return false;
        refreshPending = false;
        return true;
    }
    public void Closed()
    {
        Interlocked.CompareExchange(ref failure, new IOException("The selected capture source was closed."), null);
        Arrived();
    }
    public void ThrowIfClosed()
    {
        if (Volatile.Read(ref failure) is { } error) throw error;
    }
    public async ValueTask WaitAsync(CancellationToken token, TimeSpan? maximumWait = null)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfClosed();
        if (maximumWait is { } delay)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(delay.TotalMilliseconds))));
            try { await arrivals.Reader.ReadAsync(timeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        else await arrivals.Reader.ReadAsync(token);
        token.ThrowIfCancellationRequested();
        ThrowIfClosed();
    }
    public void Submitted(TimeSpan now)
    {
        refreshPending = false;
        if (outstanding++ == 0) lastProgress = now;
    }
    public void Produced(TimeSpan now)
    {
        if (outstanding <= 0) throw new InvalidOperationException("Encoder output has no submitted input.");
        outstanding--;
        lastProgress = now;
    }
    public void Dropped()
    {
        if (outstanding <= 0) throw new InvalidOperationException("Encoder drop has no submitted input.");
        outstanding--;
    }
    public void Check(TimeSpan now)
    {
        ThrowIfClosed();
        if (outstanding > 0 && now - lastProgress > TimeSpan.FromSeconds(10))
            throw new TimeoutException("Hardware encoder produced no video for 10 seconds.");
    }
    public void Complete()
    {
        Interlocked.Exchange(ref completed, 1);
        arrivals.Writer.TryComplete();
    }
}
