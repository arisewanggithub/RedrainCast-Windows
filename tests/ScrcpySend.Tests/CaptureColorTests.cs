using System.Numerics;
using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class CaptureColorTests
{
    [Fact]
    public void HdrHighlightsRemainOrderedInsteadOfClippingAtSdrWhite()
    {
        var color = new CaptureColor(true, 180, 1000);
        var previous = -1f;
        foreach (var level in new[] { 0, 0.01f, 0.18f, 0.5f, 0.75f, 1, 1.5f, 2, 3, 4, color.PeakScale })
        {
            var mapped = color.MapHdrToSdr(new Vector3(level * color.WhiteScale));
            Assert.InRange(mapped.X, 0, 1);
            Assert.True(mapped.X > previous);
            Assert.Equal(mapped.X, mapped.Y, 5);
            Assert.Equal(mapped.X, mapped.Z, 5);
            previous = mapped.X;
        }
        Assert.Equal(0, color.MapHdrToSdr(Vector3.Zero).X);
        Assert.Equal(1, color.MapHdrToSdr(new Vector3(color.PeakNits / 80)).X, 5);
    }

    [Fact]
    public void WindowsSdrWhiteLevelIsNormalizedBeforeMapping()
    {
        var first = new CaptureColor(true, 180, 1080);
        var second = new CaptureColor(true, 360, 2160);
        var rgb = new Vector3(0.18f, 0.3f, 0.5f);
        Assert.Equal(first.MapHdrToSdr(rgb * first.WhiteScale), second.MapHdrToSdr(rgb * second.WhiteScale));
        Assert.Equal(CaptureColor.Encode709(0.18f), first.MapHdrToSdr(new Vector3(0.18f * first.WhiteScale)).X, 5);
    }

    [Theory]
    [InlineData(4, -0.2f, 0.2f)]
    [InlineData(-0.2f, 0.7f, 1.3f)]
    [InlineData(0, 0, 12)]
    [InlineData(20, 20, 20)]
    public void WideGamutHighlightsStayWithinSdrGamut(float r, float g, float b)
    {
        var mapped = new CaptureColor(true, 180, 1000).MapHdrToSdr(new(r, g, b));
        Assert.InRange(mapped.X, 0, 1);
        Assert.InRange(mapped.Y, 0, 1);
        Assert.InRange(mapped.Z, 0, 1);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(-1, 1000)]
    [InlineData(180, 0)]
    [InlineData(float.NaN, 1000)]
    [InlineData(180, float.PositiveInfinity)]
    public void InvalidDisplayMetadataDoesNotSilentlySelectSdr(float white, float peak) =>
        Assert.Throws<NotSupportedException>(() => new CaptureColor(true, white, peak).Validate());

    [Fact]
    public void SdrUsesTheExistingDirectPath()
    {
        Assert.False(CaptureColor.Sdr.Hdr);
        Assert.Equal(1, CaptureColor.Sdr.WhiteScale);
        Assert.Throws<InvalidOperationException>(() => CaptureColor.Sdr.MapHdrToSdr(Vector3.One));
    }
}
