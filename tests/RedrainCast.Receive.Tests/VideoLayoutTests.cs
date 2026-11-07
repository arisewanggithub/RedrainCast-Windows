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
    [InlineData(-90)]
    [InlineData(45)]
    [InlineData(360)]
    public void UnsupportedAnglesAreNotSilentlyNormalized(int rotation) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoLayout.Fit(320, 160, rotation, 640, 480));
}
