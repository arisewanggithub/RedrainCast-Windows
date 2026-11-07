using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class PresentationSlotTests
{
    [Fact]
    public void FirstFrameCanBootstrapWithoutAnInitialReadinessSignal()
    {
        var slot = new PresentationSlot();
        Assert.True(slot.TryAcquire(false));
        slot.Submitted();
        Assert.False(slot.IsHeld);
        Assert.False(slot.TryAcquire(false));
    }

    [Fact]
    public void RejectedNonblockingPresentRetainsItsSlotUntilActuallySubmitted()
    {
        var slot = new PresentationSlot();
        slot.Submitted();
        Assert.True(slot.TryAcquire(true));
        for (var attempt = 0; attempt < 100; attempt++)
            Assert.True(slot.TryAcquire(false));
        slot.Submitted();
        Assert.False(slot.TryAcquire(false));
        Assert.True(slot.TryAcquire(true));
    }

    [Fact]
    public void StartupOcclusionDoesNotConsumeTheOnlyPresentationOpportunity()
    {
        var slot = new PresentationSlot();
        Assert.True(slot.TryAcquire(false));
        Assert.True(slot.TryAcquire(false));
        slot.Submitted();
        Assert.False(slot.TryAcquire(false));
    }

    [Fact]
    public void ResizedSwapchainGetsANewBootstrapOpportunity()
    {
        var slot = new PresentationSlot();
        slot.Submitted();
        Assert.False(slot.TryAcquire(false));
        slot.Reset();
        Assert.True(slot.TryAcquire(false));
        slot.Submitted();
        Assert.False(slot.TryAcquire(false));
    }
}
