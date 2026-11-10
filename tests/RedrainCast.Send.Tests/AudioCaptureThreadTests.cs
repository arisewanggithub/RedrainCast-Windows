using RedrainCast.Send.Media;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class AudioCaptureThreadTests
{
    [Fact]
    public async Task WindowsMmcssRegistersNormalAudioAndRevertsOnTheCaptureThread()
    {
        if (!OperatingSystem.IsWindows()) return;
        var messages = new List<string>();
        await AudioCaptureThread.RunAsync(_ => { }, CancellationToken.None, () => new Lease(() => { }),
            () => new AudioThreadScheduling(messages.Add), _ => Assert.Fail());
        Assert.Equal(4, messages.Count);
        Assert.Contains("stage=thread_start ", messages[0]);
        Assert.Contains("stage=mmcss_registered ", messages[1]);
        Assert.Contains("stage=mmcss_priority_set ", messages[2]);
        Assert.Contains("stage=mmcss_reverted ", messages[3]);
        Assert.All(messages, message => Assert.Contains("mmcss_task=Audio mmcss_priority=AVRT_PRIORITY_NORMAL", message));
        Assert.Single(messages.Select(message => message.Split("native_tid=")[1].Split(' ')[0]).Distinct());
    }

    [Fact]
    public async Task NativeLifetimeUsesOneDedicatedNonPoolThread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var calls = new List<(string Stage, int Thread, bool Pool)>();
        void Record(string stage) => calls.Add((stage, Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));
        await AudioCaptureThread.RunAsync(_ => Record("capture"), CancellationToken.None,
            () => { Record("apartment"); return new Lease(() => Record("uninitialize")); },
            () => { Record("register"); return new Lease(() => Record("revert")); }, _ => Assert.Fail());
        Assert.Equal(["apartment", "register", "capture", "revert", "uninitialize"], calls.Select(x => x.Stage));
        Assert.Single(calls.Select(x => x.Thread).Distinct());
        Assert.All(calls, x => { Assert.NotEqual(caller, x.Thread); Assert.False(x.Pool); });
    }

    [Fact]
    public async Task AlreadyCanceledDoesNotAcquireNativeResources()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await AudioCaptureThread.RunAsync(_ => Assert.Fail(), stop.Token,
            () => throw new Exception("unexpected apartment"), () => throw new Exception("unexpected MMCSS"), _ => Assert.Fail());
    }

    [Fact]
    public async Task CancelWakesSynchronousCaptureAndRevertsBeforeCompletion()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reverted = false;
        var task = AudioCaptureThread.RunAsync(token =>
        {
            entered.SetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
        }, stop.Token, () => new Lease(() => { }), () => new Lease(() => reverted = true), _ => Assert.Fail());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(reverted);
    }

    [Fact]
    public async Task ApartmentFailureReachesAwaiterWithoutStartingCapture()
    {
        var expected = new IOException("COM setup");
        var actual = await Assert.ThrowsAsync<IOException>(() => AudioCaptureThread.RunAsync(_ => Assert.Fail(),
            CancellationToken.None, () => throw expected, () => throw new Exception("unexpected MMCSS"), _ => Assert.Fail()));
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task RegistrationOrPriorityFailureReleasesApartment()
    {
        var released = false;
        var expected = new IOException("MMCSS setup");
        var actual = await Assert.ThrowsAsync<IOException>(() => AudioCaptureThread.RunAsync(_ => Assert.Fail(),
            CancellationToken.None, () => new Lease(() => released = true), () => throw expected, _ => Assert.Fail()));
        Assert.True(released);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task RuntimeFailureIsPropagatedAfterNativeCleanup()
    {
        var releases = new List<string>();
        var expected = new IOException("GetBuffer");
        var actual = await Assert.ThrowsAsync<IOException>(() => AudioCaptureThread.RunAsync(_ => throw expected,
            CancellationToken.None, () => new Lease(() => releases.Add("COM")),
            () => new Lease(() => releases.Add("MMCSS")), _ => Assert.Fail()));
        Assert.Equal(["MMCSS", "COM"], releases);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task RevertFailureIsReportedAndDoesNotStrandCompletionOrApartment()
    {
        var released = false;
        var reported = new List<Exception>();
        var expected = new IOException("revert");
        var actual = await Assert.ThrowsAsync<IOException>(() => AudioCaptureThread.RunAsync(_ => { },
            CancellationToken.None, () => new Lease(() => released = true),
            () => new Lease(() => throw expected), reported.Add).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(released);
        Assert.Same(expected, actual);
        Assert.Equal([expected], reported);
    }

    [Fact]
    public async Task RevertFailureDoesNotMaskRuntimeFailure()
    {
        var primary = new IOException("GetBuffer");
        var cleanup = new IOException("revert");
        var actual = await Assert.ThrowsAsync<AggregateException>(() => AudioCaptureThread.RunAsync(_ => throw primary,
            CancellationToken.None, () => new Lease(() => { }), () => new Lease(() => throw cleanup), _ => { }));
        Assert.Equal([primary, cleanup], actual.Flatten().InnerExceptions);
    }

    [Fact]
    public async Task BothCleanupFailuresAreRetainedAndReported()
    {
        var first = new IOException("revert");
        var second = new IOException("COM cleanup");
        var reported = new List<Exception>();
        var actual = await Assert.ThrowsAsync<AggregateException>(() => AudioCaptureThread.RunAsync(_ => { },
            CancellationToken.None, () => new Lease(() => throw second), () => new Lease(() => throw first), reported.Add));
        Assert.Equal([first, second], actual.Flatten().InnerExceptions);
        Assert.Equal([first, second], reported);
    }

    [Fact]
    public async Task CleanupObserverFailureDoesNotStrandCompletion()
    {
        var released = false;
        var actual = await Assert.ThrowsAsync<AggregateException>(() => AudioCaptureThread.RunAsync(_ => { },
            CancellationToken.None, () => new Lease(() => released = true),
            () => new Lease(() => throw new IOException("revert")),
            _ => throw new InvalidOperationException("observer")).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(released);
        Assert.Equal(2, actual.Flatten().InnerExceptions.Count);
    }

    [Fact]
    public async Task CancellationDoesNotHideRevertFailure()
    {
        using var stop = new CancellationTokenSource();
        var expected = new IOException("revert");
        var actual = await Assert.ThrowsAsync<IOException>(() => AudioCaptureThread.RunAsync(token =>
        {
            stop.Cancel();
            token.ThrowIfCancellationRequested();
        }, stop.Token, () => new Lease(() => { }), () => new Lease(() => throw expected), _ => { }));
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task CancellationDuringSetupStillRevertsWithoutStartingCapture()
    {
        using var stop = new CancellationTokenSource();
        var reverted = false;
        await AudioCaptureThread.RunAsync(_ => Assert.Fail(), stop.Token, () => new Lease(() => { }),
            () => { stop.Cancel(); return new Lease(() => reverted = true); }, _ => Assert.Fail());
        Assert.True(reverted);
    }

    [Fact]
    public async Task UnrelatedCancellationIsAnError()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => AudioCaptureThread.RunAsync(
            _ => throw new OperationCanceledException(), CancellationToken.None,
            () => new Lease(() => { }), () => new Lease(() => { }), _ => Assert.Fail()));
    }

    private sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
