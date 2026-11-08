using RedrainCast.Protocol;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class HdrDisplayMetadataTests
{
    private static HdrStaticMetadata? Create(double maximum, double minimum, Action<string>? diagnostic = null) =>
        HdrDisplayMetadata.Create(maximum, minimum, (0.708, 0.292), (0.17, 0.797),
            (0.131, 0.046), (0.3127, 0.329), diagnostic);

    [Fact]
    public void UnrepresentableMinimumOmitsOnlyMetadataAndLogsExplicitly()
    {
        var notices = new List<string>();
        var metadata = Create(1000, 7, notices.Add);
        Assert.Null(metadata);
        var notice = Assert.Single(notices);
        Assert.Contains("not representable", notice);
        Assert.Contains("minimum=70000/10000", notice);
        Assert.Contains("omitting desktop color-volume metadata", notice);
        Assert.Contains("Native HDR10 remains enabled", notice);
        var format = new HdrVideoFormat(true, metadata);
        format.Validate("h265");
        Assert.True(format.Hdr10);
        Assert.False(format.WireValue.ContainsKey("staticMetadata"));
    }

    [Theory]
    [InlineData(6.5535, 65535)]
    [InlineData(6.55354, 65535)]
    [InlineData(6.55356, null)]
    [InlineData(6.5536, null)]
    public void MinimumIsValidatedAfterQuantization(double minimum, int? expected)
    {
        var notices = new List<string>();
        var metadata = Create(1000, minimum, notices.Add);
        Assert.Equal(expected, metadata?.MinMasteringTenThousandths);
        Assert.Single(notices);
        if (expected is null) Assert.Contains("not representable", notices[0]);
        else Assert.Contains("using the queried", notices[0]);
    }

    [Fact]
    public void WindowsFloatMinimumBoundaryRemainsRepresentable() =>
        Assert.Equal(65535, Create(1000f, 6.5535f)?.MinMasteringTenThousandths);

    [Fact]
    public void ValidDisplayDataKeepsQueriedPrimariesAndDoesNotInventContentPeaks()
    {
        var metadata = Create(2353, 0);
        Assert.Equal(new HdrStaticMetadata(2353, 0, 0, 0,
            35400, 14600, 8500, 39850, 6550, 2300, 15635, 16450), metadata);
        Assert.Equal(2354, Create(2353.01, 0)?.MaxMasteringNits);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(-1, 0)]
    [InlineData(0.99, 0)]
    [InlineData(10000.01, 0)]
    [InlineData(double.MaxValue, 0)]
    [InlineData(1000, double.NaN)]
    [InlineData(1000, double.PositiveInfinity)]
    [InlineData(1000, double.NegativeInfinity)]
    [InlineData(1000, -0.0001)]
    [InlineData(1000, 1000.01)]
    [InlineData(1000, double.MaxValue)]
    [InlineData(1, 1.00001)]
    public void InvalidRawLuminanceIsLoggedWithoutConversionOverflow(double maximum, double minimum)
    {
        var notices = new List<string>();
        Assert.Null(Create(maximum, minimum, notices.Add));
        Assert.Contains("unavailable/invalid", Assert.Single(notices));
    }

    [Theory]
    [InlineData(double.NaN, 0.292)]
    [InlineData(0.708, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 0.292)]
    [InlineData(-0.01, 0.292)]
    [InlineData(0, 0.292)]
    [InlineData(1, 0.292)]
    [InlineData(0.708, -0.01)]
    [InlineData(0.708, 0.4)]
    public void InvalidRawChromaticityIsLogged(double x, double y)
    {
        var notices = new List<string>();
        Assert.Null(HdrDisplayMetadata.Create(1000, 0, (x, y), (0.17, 0.797),
            (0.131, 0.046), (0.3127, 0.329), notices.Add));
        Assert.Contains("unavailable/invalid", Assert.Single(notices));
    }

    [Theory]
    [InlineData(0.600012, 0.399996)]
    [InlineData(0.000001, 0.000001)]
    public void QuantizedChromaticityPairBoundsAreValidated(double x, double y)
    {
        foreach (var index in Enumerable.Range(0, 4))
        {
            var points = new[] { (0.708, 0.292), (0.17, 0.797), (0.131, 0.046), (0.3127, 0.329) };
            points[index] = (x, y);
            var notices = new List<string>();
            Assert.Null(HdrDisplayMetadata.Create(1000, 0, points[0], points[1], points[2], points[3], notices.Add));
            Assert.Contains("not representable", Assert.Single(notices));
        }
    }

    [Fact]
    public void DegenerateRawPrimariesAreNotReplacedWithDefaults()
    {
        var notices = new List<string>();
        Assert.Null(HdrDisplayMetadata.Create(1000, 0, (0.708, 0.292), (0.708, 0.292),
            (0.131, 0.046), (0.3127, 0.329), notices.Add));
        Assert.Contains("unavailable/invalid", Assert.Single(notices));
    }

    [Theory]
    [InlineData(1000, 7)]
    [InlineData(1000, 0)]
    [InlineData(double.NaN, 0)]
    public void UnrelatedDiagnosticErrorsAreNotHidden(double maximum, double minimum)
    {
        var error = new IOException("Diagnostic sink unavailable.");
        Assert.Same(error, Assert.Throws<IOException>(() => Create(maximum, minimum, _ => throw error)));
        var formatError = new FormatException("Diagnostic sink format error.");
        Assert.Same(formatError, Assert.Throws<FormatException>(() => Create(maximum, minimum, _ => throw formatError)));
    }
}
