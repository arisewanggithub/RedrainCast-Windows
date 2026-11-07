using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class EncoderCompatibilityTests
{
    public static IEnumerable<object[]> CaptureMatrix()
    {
        foreach (var vendor in new uint[] { 0, 0x5143, 0x4D4F4351, 0x10de, 0x8086, 0x1002, uint.MaxValue })
        foreach (var hdr in new[] { false, true })
        foreach (var codec in new[] { "h264", "h265", "av1" })
            yield return [vendor, hdr, codec];
    }

    [Theory]
    [MemberData(nameof(CaptureMatrix))]
    public void CapturePolicyCoversAllVendorColorAndCodecCombinations(uint vendor, bool hdr, string codec)
    {
        var rejected = codec == "av1" && (vendor is 0x5143 or 0x4D4F4351 || hdr);
        if (rejected)
            Assert.Throws<NotSupportedException>(() => EncoderCompatibility.ValidateCapture(vendor, hdr, codec));
        else
            EncoderCompatibility.ValidateCapture(vendor, hdr, codec);
    }

    [Fact]
    public void Av1LeavesTheLegacyMpegProfileAttributeUnspecified()
    {
        Assert.Equal(66u, EncoderCompatibility.OutputProfile("h264"));
        Assert.Equal(1u, EncoderCompatibility.OutputProfile("h265"));
        Assert.Null(EncoderCompatibility.OutputProfile("av1"));
        Assert.Throws<ArgumentException>(() => EncoderCompatibility.OutputProfile("unknown"));
    }

    [Theory]
    [InlineData(0x5143u)]
    [InlineData(0x4D4F4351u)]
    public void QualcommUsesRecordingScenarioAndNoBFrames(uint vendorId)
    {
        var settings = EncoderCompatibility.ForVendor(vendorId);
        Assert.Collection(settings,
            scenario =>
            {
                Assert.Equal(new Guid("b28a6e64-3ff9-446a-8a4b-0d7a53413236"), scenario.Property);
                Assert.Equal(5u, scenario.Value);
            },
            bFrames =>
            {
                Assert.Equal(new Guid("8d390aac-dc5c-4200-b57f-814d04babab2"), bFrames.Property);
                Assert.Equal(0u, bFrames.Value);
            });
    }

    [Theory]
    [InlineData(0x10de)]
    [InlineData(0x8086)]
    [InlineData(0x1002)]
    [InlineData(0)]
    public void OtherVendorsKeepTheirExistingCodecPolicy(int vendorId) =>
        Assert.Empty(EncoderCompatibility.ForVendor((uint)vendorId));

    [Theory]
    [InlineData(0x5143u, false)]
    [InlineData(0x4D4F4351u, false)]
    [InlineData(0x10deu, true)]
    [InlineData(0x8086u, true)]
    public void UnsupportedAv1IsRejectedWithoutSubstitutingACodec(uint vendor, bool hdr) =>
        Assert.Throws<NotSupportedException>(() => EncoderCompatibility.ValidateCapture(vendor, hdr, "av1"));

    [Theory]
    [InlineData(0x5143u, true, "h264")]
    [InlineData(0x4D4F4351u, true, "h265")]
    [InlineData(0x10deu, false, "av1")]
    [InlineData(0x10deu, true, "h264")]
    [InlineData(0x10deu, true, "h265")]
    public void OtherSupportedSelectionsKeepTheirRequestedCodec(uint vendor, bool hdr, string codec) =>
        EncoderCompatibility.ValidateCapture(vendor, hdr, codec);
}
