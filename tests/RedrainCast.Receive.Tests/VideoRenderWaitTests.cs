using RedrainCast.Receive.Core;
using RedrainCast.Receive.Media;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class VideoRenderWaitTests
{
    [Fact]
    public void UnavailableSlotFallsBackToTimeoutWithoutInventingReadiness()
    {
        using var publication = new AutoResetEvent(false);
        using var cancellation = new ManualResetEvent(false);
        using var readiness = new AutoResetEvent(false);
        var slot = new PresentationSlot();
        slot.Submitted();
        Assert.Equal(WaitHandle.WaitTimeout, VideoRenderWait.Wait(
            publication, cancellation, readiness, slot, 0, true,
            () => Assert.Fail("Unsignaled readiness must not dispatch.")));
        Assert.False(slot.IsHeld);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadinessDispatchesBeforeTimeoutWithOrWithoutDiagnostics(bool diagnostics)
    {
        using var publication = new AutoResetEvent(false);
        using var cancellation = new ManualResetEvent(false);
        using var readiness = new AutoResetEvent(true);
        var slot = new PresentationSlot();
        slot.Submitted();
        var observations = 0;
        Assert.Equal(2, VideoRenderWait.Wait(publication, cancellation, readiness, slot,
            0, true, diagnostics ? () => observations++ : null));
        Assert.True(slot.IsHeld);
        Assert.False(readiness.WaitOne(0));
        Assert.Equal(diagnostics ? 1 : 0, observations);
    }

    [Fact]
    public void IdleOrOccludedWorkDoesNotConsumeReadinessOrBusyDispatch()
    {
        using var publication = new AutoResetEvent(false);
        using var cancellation = new ManualResetEvent(false);
        using var readiness = new AutoResetEvent(true);
        var slot = new PresentationSlot();
        slot.Submitted();
        for (var attempt = 0; attempt < 10; attempt++)
            Assert.Equal(WaitHandle.WaitTimeout,
                VideoRenderWait.Wait(publication, cancellation, readiness, slot, 0, false));
        Assert.False(slot.IsHeld);
        Assert.True(readiness.WaitOne(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BootstrapOrRetainedRejectedSlotCannotSpinOnAnotherReadinessSignal(bool retained)
    {
        using var publication = new AutoResetEvent(false);
        using var cancellation = new ManualResetEvent(false);
        using var readiness = new AutoResetEvent(true);
        var slot = new PresentationSlot();
        if (retained) { slot.Submitted(); slot.TryAcquire(true); }
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(WaitHandle.WaitTimeout,
                VideoRenderWait.Wait(publication, cancellation, readiness, slot, 0, true));
            Assert.True(slot.TryAcquire(false));
        }
        Assert.True(readiness.WaitOne(0));
        slot.Submitted();
        Assert.False(slot.TryAcquire(false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicationAndCancellationHavePriorityOverSimultaneousReadiness(bool cancel)
    {
        using var publication = new AutoResetEvent(!cancel);
        using var cancellation = new ManualResetEvent(cancel);
        using var readiness = new AutoResetEvent(true);
        var slot = new PresentationSlot();
        slot.Submitted();
        Assert.Equal(cancel ? 1 : 0,
            VideoRenderWait.Wait(publication, cancellation, readiness, slot, 0, true));
        Assert.False(slot.IsHeld);
        Assert.True(readiness.WaitOne(0));
    }

    [Fact]
    public void CancellationAfterReceiptIsObservedBeforeRenderingAndSlotIsRetained()
    {
        using var publication = new AutoResetEvent(false);
        using var cancellation = new CancellationTokenSource();
        using var readiness = new AutoResetEvent(true);
        var slot = new PresentationSlot();
        slot.Submitted();
        Assert.Equal(2, VideoRenderWait.Wait(publication, cancellation.Token.WaitHandle, readiness,
            slot, 0, true, cancellation.Cancel));
        Assert.Throws<OperationCanceledException>(() => cancellation.Token.ThrowIfCancellationRequested());
        Assert.True(slot.IsHeld);
    }

    [Fact]
    public void ReplacementBeforeDispatchStillTakesLatestAndResetRetiresOldGeneration()
    {
        using var publication = new AutoResetEvent(false);
        using var cancellation = new ManualResetEvent(false);
        using var readiness = new AutoResetEvent(true);
        var retired = new List<string>();
        var mailbox = new VideoFrameMailbox<string>(() => publication.Set(), (frame, _) => retired.Add(frame));
        var slot = new PresentationSlot();
        slot.Submitted();
        mailbox.Publish("old", mailbox.Revision);
        Assert.True(publication.WaitOne(0));
        Assert.Equal(2, VideoRenderWait.Wait(publication, cancellation, readiness, slot, 0, true,
            () => mailbox.Publish("latest", mailbox.Revision)));
        Assert.Equal("latest", mailbox.Take(out var revision));
        Assert.Equal(["old"], retired);
        mailbox.Reset("generation_reset");
        slot.Reset();
        mailbox.Return("latest", revision);
        Assert.Equal(["old", "latest"], retired);
        Assert.Null(mailbox.Take());
        Assert.True(slot.TryAcquire(false));
    }

    [Fact]
    public void PublicationWinsCancellationButCallerCancellationCheckPreventsRender()
    {
        using var publication = new AutoResetEvent(true);
        using var cancellation = new CancellationTokenSource();
        using var readiness = new AutoResetEvent(true);
        cancellation.Cancel();
        var slot = new PresentationSlot();
        slot.Submitted();
        Assert.Equal(0, VideoRenderWait.Wait(publication, cancellation.Token.WaitHandle, readiness, slot, 0, true));
        Assert.Throws<OperationCanceledException>(() => cancellation.Token.ThrowIfCancellationRequested());
        Assert.True(readiness.WaitOne(0));
    }
}
