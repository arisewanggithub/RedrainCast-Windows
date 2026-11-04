using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class CaptureProgressTests
{
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
