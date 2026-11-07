using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class PresentationDeadlineTests
{
    private sealed class Progress : IVideoPlaybackProgress
    {
        public bool HasPresentedVideo { get; set; }
        public bool HasEverPresentedVideo { get; set; }
        public bool HasDecodedVideo { get; set; }
        public bool IsPresentationSuspended { get; set; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MissingPresentationStillExpiresWithoutDecodedOcclusion(bool decoded, bool occluded)
    {
        var deadline = new PresentationDeadline(0);
        var progress = new Progress { HasDecodedVideo = decoded, IsPresentationSuspended = occluded };
        Assert.False(deadline.HasExpired(9999, 0, true, progress));
        Assert.True(deadline.HasExpired(10_000, 0, true, progress));
    }

    [Fact]
    public void DecodedOcclusionCanWaitAndVisibilityGetsAFreshPresentationDeadline()
    {
        var deadline = new PresentationDeadline(0);
        var progress = new Progress { HasDecodedVideo = true, IsPresentationSuspended = true };
        Assert.False(deadline.HasExpired(60_000, 0, true, progress));
        Assert.True(deadline.IsSuspended);
        Assert.False(progress.HasEverPresentedVideo);
        progress.IsPresentationSuspended = false;
        Assert.False(deadline.HasExpired(90_000, 0, true, progress));
        Assert.False(deadline.IsSuspended);
        Assert.False(deadline.HasExpired(99_999, 0, true, progress));
        Assert.True(deadline.HasExpired(100_000, 0, true, progress));
    }

    [Fact]
    public void OcclusionDoesNotExcuseMissingTrackConfiguration()
    {
        var deadline = new PresentationDeadline(0);
        var progress = new Progress { HasDecodedVideo = true, IsPresentationSuspended = true };
        Assert.True(deadline.HasExpired(10_000, 0, false, progress));
        Assert.False(deadline.IsSuspended);
    }

    [Fact]
    public void ActualPresentationAndNewGenerationsKeepTheirOriginalSemantics()
    {
        var deadline = new PresentationDeadline(0);
        var progress = new Progress { HasPresentedVideo = true, HasEverPresentedVideo = true, HasDecodedVideo = true };
        Assert.False(deadline.HasExpired(50_000, 0, true, progress));
        progress.HasPresentedVideo = progress.HasDecodedVideo = false;
        Assert.False(deadline.HasExpired(59_999, 50_000, true, progress));
        Assert.True(deadline.HasExpired(60_000, 50_000, true, progress));
    }
}
