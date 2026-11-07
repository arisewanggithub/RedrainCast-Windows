using RedrainCast.App;
using Xunit;

namespace RedrainCast.Tests;

public sealed class RecoveryPcmSourceTests
{
    [Fact]
    public async Task DeadlineCrossingReadsElapsedOnceAndKeepsOriginalPts()
    {
        var time = new CrossingClock(TimeSpan.Zero, TimeSpan.FromMilliseconds(9), TimeSpan.FromMilliseconds(22));
        await using var source = new RecoveryPcmSource(time);
        await using var packets = source.ReadPacketsAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await packets.MoveNextAsync());
        var first = packets.Current;
        Assert.True(await packets.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, time.Reads);
        Assert.Equal(TimeSpan.FromMilliseconds(1), Assert.Single(time.Delays));
        Assert.Equal(first.PtsUs + 10_000, packets.Current.PtsUs);
        Assert.Equal(1920, packets.Current.Data.Length);
        Assert.All(packets.Current.Data, value => Assert.Equal((byte)0, value));
        Assert.True(await packets.MoveNextAsync());
        Assert.Equal(first.PtsUs + 20_000, packets.Current.PtsUs);
        Assert.Single(time.Delays);
    }

    [Fact]
    public async Task LateSourceDoesNotRequestNegativeDelayOrRewritePts()
    {
        var time = new CrossingClock(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        await using var source = new RecoveryPcmSource(time);
        await using var packets = source.ReadPacketsAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await packets.MoveNextAsync());
        var first = packets.Current.PtsUs;
        Assert.True(await packets.MoveNextAsync());
        Assert.Equal(first + 10_000, packets.Current.PtsUs);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task CancellationStopsCatchupWithoutWaitingForNextTimer()
    {
        using var stop = new CancellationTokenSource();
        var time = new CrossingClock(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        await using var source = new RecoveryPcmSource(time);
        await using var packets = source.ReadPacketsAsync(stop.Token).GetAsyncEnumerator();
        Assert.True(await packets.MoveNextAsync());
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => packets.MoveNextAsync().AsTask());
        Assert.Empty(time.Delays);
    }

    private sealed class CrossingClock(params TimeSpan[] timestamps) : TimeProvider
    {
        public int Reads { get; private set; }
        public List<TimeSpan> Delays { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamps[Math.Min(Reads++, timestamps.Length - 1)].Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            Assert.True(dueTime > TimeSpan.Zero);
            return new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
}
