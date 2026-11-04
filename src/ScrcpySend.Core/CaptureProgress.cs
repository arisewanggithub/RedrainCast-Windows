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

    public void Arrived() => arrivals.Writer.TryWrite(true);
    public void Closed()
    {
        Interlocked.CompareExchange(ref failure, new IOException("The selected capture source was closed."), null);
        Arrived();
    }
    public void ThrowIfClosed()
    {
        if (Volatile.Read(ref failure) is { } error) throw error;
    }
    public async ValueTask WaitAsync(CancellationToken token)
    {
        ThrowIfClosed();
        await arrivals.Reader.ReadAsync(token);
        ThrowIfClosed();
    }
    public void Submitted(TimeSpan now)
    {
        if (outstanding++ == 0) lastProgress = now;
    }
    public void Produced(TimeSpan now)
    {
        if (outstanding <= 0) throw new InvalidOperationException("Encoder output has no submitted input.");
        outstanding--;
        lastProgress = now;
    }
    public void Check(TimeSpan now)
    {
        ThrowIfClosed();
        if (outstanding > 0 && now - lastProgress > TimeSpan.FromSeconds(10))
            throw new TimeoutException("Hardware encoder produced no video for 10 seconds.");
    }
    public void Complete() => arrivals.Writer.TryComplete();
}
