using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class VideoLayoutTests
{
    [Theory]
    [InlineData(0, 0, 80, 640, 320)]
    [InlineData(90, 200, 0, 240, 480)]
    [InlineData(180, 0, 80, 640, 320)]
    [InlineData(270, 200, 0, 240, 480)]
    public void RotatedAspectFitsViewportWithoutStretching(int rotation, int x, int y, int width, int height) =>
        Assert.Equal((x, y, width, height), VideoLayout.Fit(320, 160, rotation, 640, 480));

    [Theory]
    [InlineData(PresentationOrientation.Portrait, 0, 270, 0, 0, 1920, 1080)]
    [InlineData(PresentationOrientation.Portrait, 90, 0, 656, 0, 607, 1080)]
    [InlineData(PresentationOrientation.Portrait, 270, 180, 656, 0, 607, 1080)]
    [InlineData(PresentationOrientation.Auto, 0, 0, 656, 0, 607, 1080)]
    [InlineData(PresentationOrientation.Landscape, 0, 0, 656, 0, 607, 1080)]
    public void PortraitStreamUsesLandscapeMonitorLongEdgeOnlyWhenForced(
        PresentationOrientation mode, int streamRotation, int expectedRotation, int x, int y, int width, int height)
    {
        var rotation = VideoLayout.OutputRotation(streamRotation, mode, 1920, 1080);
        Assert.Equal(expectedRotation, rotation);
        Assert.Equal((x, y, width, height), VideoLayout.Fit(720, 1280, rotation, 1920, 1080));
    }

    [Theory]
    [InlineData(PresentationOrientation.Landscape, 270, 1080, 1920)]
    [InlineData(PresentationOrientation.Portrait, 0, 1080, 1920)]
    [InlineData(PresentationOrientation.Portrait, 0, 1080, 1080)]
    public void CorrectlyOrientedAndSquareOutputsDoNotReceiveAnExtraTurn(
        PresentationOrientation mode, int rotation, int width, int height) =>
        Assert.Equal(rotation, VideoLayout.OutputRotation(0, mode, width, height));

    [Theory]
    [InlineData(-90)]
    [InlineData(45)]
    [InlineData(360)]
    public void UnsupportedAnglesAreNotSilentlyNormalized(int rotation) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoLayout.Fit(320, 160, rotation, 640, 480));
}
