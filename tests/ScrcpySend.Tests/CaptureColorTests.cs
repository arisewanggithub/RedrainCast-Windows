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
            var mapped = ToneMapTables.Sample(new Vector3(level * color.WhiteScale), 1000);
            Assert.InRange(mapped.X, 0, 1);
            Assert.True(mapped.X >= previous);
            Assert.InRange(Math.Abs(mapped.X - mapped.Y), 0, 0.015f);
            Assert.InRange(Math.Abs(mapped.X - mapped.Z), 0, 0.015f);
            previous = mapped.X;
        }
        Assert.InRange(ToneMapTables.Sample(Vector3.Zero, 1000).X, 0, 0.005f);
        Assert.True(ToneMapTables.Sample(new Vector3(4 * color.WhiteScale), 1000).X >
            ToneMapTables.Sample(new Vector3(color.WhiteScale), 1000).X + 0.03f);
    }

    [Fact]
    public void PqUsesAbsoluteNitsInsteadOfTheWindowsSdrBrightnessSetting()
    {
        foreach (var nits in new[] { 0.1f, 1, 80, 100, 203, 1000, 4000, 10000 })
            Assert.InRange(Math.Abs(HdrLuminance.FromPq(HdrLuminance.ToPq(nits)) - nits), 0, Math.Max(0.01f, nits * 0.001f));
    }

    [Theory]
    [InlineData(4, -0.2f, 0.2f)]
    [InlineData(-0.2f, 0.7f, 1.3f)]
    [InlineData(0, 0, 12)]
    [InlineData(20, 20, 20)]
    public void WideGamutHighlightsStayWithinSdrGamut(float r, float g, float b)
    {
        var mapped = ToneMapTables.Sample(new(r, g, b), 1000);
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
    }

    [Theory]
    [InlineData(0, 1000, null, 1000f)]
    [InlineData(180, 0, 180f, null)]
    [InlineData(float.NaN, float.PositiveInfinity, null, null)]
    [InlineData(180, 1000, 180f, 1000f)]
    public void MissingDisplayMetadataPreservesHdrAndEachKnownValue(float white, float peak,
        float? expectedWhite, float? expectedPeak)
    {
        var color = CaptureColor.FromHdrDisplayMetadata(white, peak);
        color.Validate();
        Assert.True(color.Hdr);
        Assert.Equal(expectedWhite, color.SdrWhiteNits);
        Assert.Equal(expectedPeak, color.PeakNits);
        Assert.InRange(ToneMapTables.Sample(new Vector3(12.5f), 1000).X, 0, 1);
    }

    [Fact]
    public void UnknownMetadataIsNotFabricatedForDiagnostics()
    {
        var color = new CaptureColor(true);
        color.Validate();
        Assert.Null(color.SdrWhiteNits);
        Assert.Null(color.PeakNits);
        Assert.Contains("unknown", color.LuminanceDescription);
        Assert.Throws<InvalidOperationException>(() => color.WhiteScale);
        Assert.Throws<InvalidOperationException>(() => color.PeakScale);
        var json = System.Text.Json.JsonSerializer.Serialize(color);
        Assert.Contains("\"Hdr\":true", json);
        Assert.Contains("\"SdrWhiteNits\":null", json);
        Assert.Contains("\"PeakNits\":null", json);
    }
}
