using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class FramePacerTests
{
    [Fact]
    public void FinalUpdateInsideTheIntervalIsSubmittedWhenTheSourceBecomesStatic()
    {
        var pacer = new DeferredFramePacer(60);
        pacer.Offer(1000);
        Assert.True(pacer.TryTake(0, out var first));
        Assert.Equal(1000, first);
        pacer.Offer(51000);
        Assert.False(pacer.TryTake(50000, out _));
        Assert.True(pacer.HasPending);
        Assert.True(pacer.TryTake(166667, out var final));
        Assert.Equal(51000, final);
        Assert.False(pacer.HasPending);
        Assert.False(pacer.TryTake(333334, out _));
    }

    [Fact]
    public void BurstCoalescesToLatestSourceTimestampAndResizeDropsOnlyPendingOldGeometry()
    {
        var pacer = new DeferredFramePacer(30);
        pacer.Offer(1);
        Assert.True(pacer.TryTake(0, out _));
        pacer.Offer(2);
        pacer.Offer(3);
        Assert.False(pacer.TryTake(1000, out _));
        Assert.True(pacer.TryTake(333334, out var newest));
        Assert.Equal(3, newest);
        pacer.Offer(4);
        pacer.DiscardPending();
        Assert.False(pacer.TryTake(666667, out _));
        Assert.Throws<ArgumentException>(() => pacer.Offer(3));
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(60, 30)]
    [InlineData(60, 60)]
    [InlineData(90, 90)]
    [InlineData(120, 120)]
    [InlineData(120, 60)]
    [InlineData(120, 30)]
    [InlineData(144, 120)]
    [InlineData(240, 120)]
    [InlineData(30, 60)]
    [InlineData(60, 120)]
    [InlineData(1, 1)]
    [InlineData(240, 1)]
    public void IntegerTickQuantizationRetainsTheRequestedCadence(int sourceFps, int cap)
    {
        var pacer = new FramePacer(cap);
        var accepted = 0;
        for (var index = 0; index < sourceFps * 10; index++)
            if (pacer.ShouldCapture(index * TimeSpan.TicksPerSecond / sourceFps)) accepted++;
        Assert.Equal(Math.Min(sourceFps, cap) * 10, accepted);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    public void SmallTimestampJitterDoesNotCompoundIntoDroppedFrames(int fps)
    {
        var pacer = new FramePacer(fps);
        for (var index = 0; index < fps * 10; index++)
        {
            var jitter = index == 0 ? 0 : (index % 2 == 0 ? -1000 : 1000);
            Assert.True(pacer.ShouldCapture(TimeSpan.TicksPerSecond + index * TimeSpan.TicksPerSecond / fps + jitter));
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    public void DenseInputStaysWithinOneFrameOfTheLongTermCap(int fps)
    {
        var pacer = new FramePacer(fps);
        var accepted = 0;
        for (var index = 0; index < 100_000; index++)
            if (pacer.ShouldCapture(index * 1000L)) accepted++;
        Assert.InRange(accepted, fps * 10, fps * 10 + 1);
    }

    [Fact]
    public void IdlePeriodsResetPhaseWithoutBurstCatchUp()
    {
        var pacer = new FramePacer(30);
        Assert.True(pacer.ShouldCapture(0));
        Assert.True(pacer.ShouldCapture(100_999_999));
        Assert.False(pacer.ShouldCapture(101_000_000));
        Assert.True(pacer.ShouldCapture(101_333_332));
    }

    [Fact]
    public void InvalidTimestampsAndRatesAreNotSilentlyAccepted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(121));
        var pacer = new FramePacer(120);
        Assert.Throws<ArgumentException>(() => pacer.ShouldCapture(-1));
        Assert.True(pacer.ShouldCapture(100));
        Assert.Throws<ArgumentException>(() => pacer.ShouldCapture(99));
    }

    [Fact]
    public void UncappedAcceptsEveryFreshFrameWithoutADeadline()
    {
        var pacer = new FramePacer(0);
        for (var index = 0; index < 2400; index++)
            Assert.True(pacer.ShouldCapture(index * TimeSpan.TicksPerSecond / 240));
        Assert.Throws<ArgumentException>(() => pacer.ShouldCapture(-1));
        Assert.Throws<ArgumentException>(() => pacer.ShouldCapture(0));
        Assert.True(pacer.ShouldCapture(long.MaxValue));
    }

    [Fact]
    public void UncappedPendingInputCoalescesWithoutDuplicatesOrClockDelay()
    {
        var pacer = new DeferredFramePacer(0);
        pacer.Offer(1);
        Assert.True(pacer.TryTake(0, out var first));
        Assert.Equal(1, first);
        Assert.False(pacer.TryTake(0, out _));
        pacer.Offer(1);
        Assert.False(pacer.HasPending);
        pacer.Offer(2);
        pacer.Offer(3);
        pacer.Offer(3);
        Assert.True(pacer.TryTake(0, out var newest));
        Assert.Equal(3, newest);
        pacer.Offer(4);
        pacer.DiscardPending();
        Assert.False(pacer.TryTake(0, out _));
        Assert.Throws<ArgumentException>(() => pacer.Offer(3));
        Assert.Throws<ArgumentException>(() => pacer.Offer(-1));
        pacer.Offer(5);
        Assert.Throws<ArgumentException>(() => pacer.TryTake(-1, out _));
        Assert.True(pacer.HasPending);
        Assert.True(pacer.TryTake(1, out _));
        pacer.Offer(6);
        Assert.Throws<ArgumentException>(() => pacer.TryTake(0, out _));
    }

    [Fact]
    public void LargeMonotonicClocksDoNotOverflowPacingArithmetic()
    {
        var pacer = new FramePacer(120);
        Assert.True(pacer.ShouldCapture(0));
        Assert.True(pacer.ShouldCapture(long.MaxValue - 1));
        Assert.False(pacer.ShouldCapture(long.MaxValue));
    }
}
