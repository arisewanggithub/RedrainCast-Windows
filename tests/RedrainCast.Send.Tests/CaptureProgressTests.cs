using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class CaptureProgressTests
{
    [Fact]
    public async Task KeyControlWakesIdleCaptureWithoutAFrameArrival()
    {
        var progress = new CaptureProgress();
        var waiting = progress.WaitAsync(default).AsTask();
        Assert.False(waiting.IsCompleted);
        progress.RequestKeyFrame();
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(progress.TryTakeKeyRequest(TimeSpan.Zero));
        Assert.True(progress.TryTakeIdleRefresh());
        Assert.False(progress.TryTakeIdleRefresh());
        Assert.False(progress.TryTakeKeyRequest(TimeSpan.Zero));
        progress.Check(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task PreStartAndDuplicateKeyRequestsCoalesceAndRespectTheRefreshInterval()
    {
        var progress = new CaptureProgress();
        for (var index = 0; index < 1000; index++) progress.RequestKeyFrame();
        await progress.WaitAsync(default);
        Assert.True(progress.TryTakeKeyRequest(TimeSpan.Zero));
        for (var index = 0; index < 1000; index++) progress.RequestKeyFrame();
        Assert.False(progress.TryTakeKeyRequest(TimeSpan.FromMilliseconds(499)));
        Assert.True(progress.TryTakeIdleRefresh());
        Assert.False(progress.TryTakeIdleRefresh());
        Assert.Equal(TimeSpan.FromMilliseconds(1), progress.KeyRequestDelay(TimeSpan.FromMilliseconds(499)));
        Assert.True(progress.TryTakeKeyRequest(TimeSpan.FromMilliseconds(500)));
        Assert.True(progress.TryTakeIdleRefresh());
        Assert.False(progress.TryTakeIdleRefresh());
        Assert.Null(progress.KeyRequestDelay(TimeSpan.FromSeconds(1)));
        await progress.WaitAsync(default);
        using var stop = new CancellationTokenSource();
        var waiting = progress.WaitAsync(stop.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task KeyWakeDoesNotHideClosureCompletionOrCancellation()
    {
        var progress = new CaptureProgress();
        progress.RequestKeyFrame();
        progress.Closed();
        await Assert.ThrowsAsync<IOException>(() => progress.WaitAsync(default).AsTask());
        var completed = new CaptureProgress();
        completed.Complete();
        completed.RequestKeyFrame();
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(() => completed.WaitAsync(default).AsTask());
        using var stop = new CancellationTokenSource();
        var idle = new CaptureProgress();
        var wait = idle.WaitAsync(stop.Token, TimeSpan.FromMilliseconds(10)).AsTask();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(idle.TryTakeKeyRequest(TimeSpan.Zero));
        var cancelled = idle.WaitAsync(stop.Token).AsTask();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task ActiveCaptureAndKeyControlKeepEncoderAccountingIndependent()
    {
        var progress = new CaptureProgress();
        progress.Submitted(TimeSpan.Zero);
        progress.Arrived();
        progress.RequestKeyFrame();
        await progress.WaitAsync(default);
        Assert.True(progress.TryTakeKeyRequest(TimeSpan.Zero));
        Assert.False(progress.TryTakeIdleRefresh());
        Assert.Throws<TimeoutException>(() => progress.Check(TimeSpan.FromSeconds(11)));
        progress.Produced(TimeSpan.FromSeconds(12));
        Assert.True(progress.TryTakeIdleRefresh());
        Assert.False(progress.TryTakeIdleRefresh());
        progress.Check(TimeSpan.FromHours(1));
    }

    [Fact]
    public void GenuineSubmittedInputConsumesThePendingRefreshWithoutRearmingActiveCapture()
    {
        var progress = new CaptureProgress();
        progress.RequestKeyFrame();
        Assert.True(progress.TryTakeKeyRequest(TimeSpan.Zero));
        progress.Submitted(TimeSpan.Zero);
        Assert.False(progress.TryTakeIdleRefresh());
        progress.Produced(TimeSpan.FromMilliseconds(10));
        Assert.False(progress.TryTakeIdleRefresh());
    }

    [Fact]
    public async Task FiniteControlWaitDoesNotLeaveAnOrphanReaderAndCompletionWakesIdleCapture()
    {
        var progress = new CaptureProgress();
        await progress.WaitAsync(default, TimeSpan.FromMilliseconds(1));
        progress.Arrived();
        await progress.WaitAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        var waiting = progress.WaitAsync(default).AsTask();
        Assert.False(waiting.IsCompleted);
        progress.Complete();
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(() => waiting);
        progress.RequestKeyFrame();
        Assert.False(progress.TryTakeKeyRequest(TimeSpan.Zero));
    }

    [Fact]
    public void DroppedInputDoesNotLeavePhantomWorkOrHideAnotherStalledInput()
    {
        var progress = new CaptureProgress();
        progress.Submitted(TimeSpan.Zero);
        progress.Submitted(TimeSpan.FromSeconds(1));
        progress.Dropped();
        Assert.Throws<TimeoutException>(() => progress.Check(TimeSpan.FromSeconds(10.001)));
        progress.Produced(TimeSpan.FromSeconds(11));
        progress.Check(TimeSpan.FromMinutes(1));
        Assert.Throws<InvalidOperationException>(progress.Dropped);
    }

    [Fact]
    public void AnExplicitlyDroppedIdleFrameAllowsFreshWorkLater()
    {
        var progress = new CaptureProgress();
        progress.Submitted(TimeSpan.Zero);
        progress.Dropped();
        progress.Check(TimeSpan.FromMinutes(1));
        progress.Submitted(TimeSpan.FromMinutes(1));
        progress.Check(TimeSpan.FromSeconds(70));
        Assert.Throws<TimeoutException>(() => progress.Check(TimeSpan.FromSeconds(70.001)));
    }

    [Fact]
    public void IdleForMoreThanTenSecondsDoesNotCountAsEncoderStallWhenInputResumes()
    {
        var progress = new CaptureProgress();
        progress.Submitted(TimeSpan.Zero);
        progress.Produced(TimeSpan.FromMilliseconds(20));
        progress.Check(TimeSpan.FromMinutes(1));
        progress.Submitted(TimeSpan.FromMinutes(1));
        progress.Check(TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(2));
        progress.Check(TimeSpan.FromSeconds(70));
        Assert.Throws<TimeoutException>(() => progress.Check(TimeSpan.FromSeconds(70.001)));
    }

    [Fact]
    public void OutputProgressResetsStallDeadlineButAdditionalQueuedInputDoesNotHideAStall()
    {
        var progress = new CaptureProgress();
        progress.Submitted(TimeSpan.Zero);
        progress.Submitted(TimeSpan.FromSeconds(9));
        Assert.Throws<TimeoutException>(() => progress.Check(TimeSpan.FromSeconds(10.001)));
        progress.Produced(TimeSpan.FromSeconds(11));
        progress.Check(TimeSpan.FromSeconds(20));
        Assert.Throws<TimeoutException>(() => progress.Check(TimeSpan.FromSeconds(21.001)));
        progress.Produced(TimeSpan.FromSeconds(22));
        progress.Check(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ClosingAnIdleSourceWakesAnAlreadyBlockedWorkerAndPropagatesTheError()
    {
        var progress = new CaptureProgress();
        var wait = progress.WaitAsync(default).AsTask();
        Assert.False(wait.IsCompleted);
        progress.Closed();
        var error = await Assert.ThrowsAsync<IOException>(() => wait.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Contains("source was closed", error.Message);
        Assert.Throws<IOException>(() => progress.Check(TimeSpan.Zero));
    }

    [Fact]
    public async Task ClosureBeforeWaitAndCoalescedArrivalsNeverLoseTerminalState()
    {
        var progress = new CaptureProgress();
        for (var index = 0; index < 10; index++) progress.Arrived();
        progress.Closed();
        progress.Arrived();
        await Assert.ThrowsAsync<IOException>(() => progress.WaitAsync(default).AsTask());
    }
}
