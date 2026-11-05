using System.Numerics;
using System.Text.Json;
using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class HdrLuminanceTests
{
    private static uint[] Histogram(float nits, uint count)
    {
        var bins = new uint[HdrLuminance.HistogramBins];
        bins[Math.Clamp((int)(HdrLuminance.ToPq(nits) * 255), 0, 255)] = count;
        return bins;
    }

    [Fact]
    public void IsolatedExtremeHighlightsDoNotSetTheWholeScenesExposure()
    {
        var bins = Histogram(800, 100000);
        bins[255] = 4;
        var peak = new HdrLuminance().Observe(bins);
        Assert.InRange(peak, 800, 900);
    }

    [Fact]
    public void SmallChangesAreSmoothedButSceneCutsReactImmediately()
    {
        var analysis = new HdrLuminance();
        var first = analysis.Observe(Histogram(500, 1000));
        var smoothed = analysis.Observe(Histogram(550, 1000));
        var direct = new HdrLuminance().Observe(Histogram(550, 1000));
        Assert.InRange(smoothed, first, direct);
        Assert.True(smoothed < direct);
        Assert.InRange(analysis.Observe(Histogram(4000, 1000)), 4000, 4300);
    }

    [Fact]
    public void InvalidHistogramCannotSilentlyChooseADefaultPeak()
    {
        Assert.Throws<ArgumentException>(() => new HdrLuminance().Observe(new uint[10]));
        Assert.Throws<InvalidDataException>(() => new HdrLuminance().Observe(new uint[256]));
    }

    [Fact]
    public void LookupSelectionIsBoundedAndContinuousAtBankBoundaries()
    {
        Assert.Equal((0, 0, 0f), ToneMapTables.Select(50));
        var boundary = ToneMapTables.Select(800);
        Assert.Equal(800, ToneMapTables.Peaks[boundary.High]);
        Assert.Equal(1, boundary.Weight);
        var middle = ToneMapTables.Select(600);
        Assert.InRange(middle.Weight, 0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => ToneMapTables.Select(float.NaN));
    }

    [Fact]
    public void NearWhiteGraysDoNotDarkenAsHdrLuminanceIncreases()
    {
        var previous = 0f;
        for (var nits = 0; nits <= 800; nits += 5)
        {
            var value = ToneMapTables.Sample(new Vector3(nits / 80f), 800);
            Assert.True(value.X >= previous - 0.002f);
            Assert.InRange(Math.Abs(value.X - value.Y), 0, 0.005f);
            Assert.InRange(Math.Abs(value.X - value.Z), 0, 0.005f);
            previous = value.X;
        }
    }

    [Fact]
    public void TablesMatchIndependentNonVertexReferenceSwatches()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "bt2390-reference.json")));
        foreach (var sample in document.RootElement.GetProperty("fixture").EnumerateArray())
        {
            var input = sample.GetProperty("input").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var expected = sample.GetProperty("expected").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var actual = ToneMapTables.Sample(new(input[0], input[1], input[2]), sample.GetProperty("peak").GetSingle());
            Assert.InRange(Vector3.Distance(actual, new(expected[0], expected[1], expected[2])), 0, 0.015f);
        }
    }
}
