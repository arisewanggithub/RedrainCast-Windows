using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class CodecSelectionTests
{
    [Fact]
    public void UserFacingLabelsKeepProtocolValues()
    {
        var selection = new CodecSelection(0x10de, false);
        Assert.Equal(new[] { "H.264", "H.265 (HEVC)", "AV1" }, selection.Choices.Select(choice => choice.Label));
        Assert.Equal("h265", selection.ValidateSelection(selection.Find("h265")));
    }

    [Theory]
    [InlineData(0x5143u, null)]
    [InlineData(0x5143u, false)]
    [InlineData(0x5143u, true)]
    [InlineData(0x4D4F4351u, null)]
    [InlineData(0x4D4F4351u, false)]
    [InlineData(0x4D4F4351u, true)]
    [InlineData(0x10deu, true)]
    [InlineData(0x8086u, true)]
    public void KnownUnsupportedAv1IsOmittedWithoutReplacingSavedSelection(uint vendor, bool? hdr)
    {
        var selection = new CodecSelection(vendor, hdr);
        Assert.Equal(new[] { "h264", "h265" }, selection.Choices.Select(choice => choice.Codec));
        Assert.Null(selection.Find("av1"));
        Assert.Contains("Saved AV1 unavailable", selection.SavedSelectionMessage("av1"));
        Assert.Throws<ArgumentException>(() => selection.ValidateSelection(null));
        foreach (var choice in selection.Choices)
            Assert.Equal(choice.Codec, selection.ValidateSelection(choice));
    }

    [Theory]
    [InlineData(0x10deu, false)]
    [InlineData(0x10deu, null)]
    [InlineData(0x8086u, false)]
    [InlineData(0u, false)]
    public void OtherSdrOrUnselectedSourcesKeepAv1WithoutCertifyingHardware(uint vendor, bool? hdr)
    {
        var selection = new CodecSelection(vendor, hdr);
        Assert.Equal(new[] { "h264", "h265", "av1" }, selection.Choices.Select(choice => choice.Codec));
        Assert.Equal("av1", selection.ValidateSelection(selection.Find("av1")));
        Assert.Empty(selection.SavedSelectionMessage("av1"));
    }

    [Fact]
    public void ContextChangeRequiresAnExplicitSupportedSelection()
    {
        var sdr = new CodecSelection(0x10de, false);
        var requested = sdr.Find("av1");
        var hdr = new CodecSelection(0x10de, true);
        var error = Assert.Throws<ArgumentException>(() => hdr.ValidateSelection(requested));
        Assert.Contains("HDR-to-SDR", error.Message);
        Assert.Throws<NotSupportedException>(() => EncoderCompatibility.ValidateCapture(0x10de, true, "av1"));
        Assert.Equal("h265", hdr.ValidateSelection(hdr.Find("h265")));
        Assert.Equal("av1", requested!.Codec);
    }

    [Fact]
    public void InvalidSelectionsProduceExplicitInputErrors()
    {
        var selection = new CodecSelection(0x10de, false);
        Assert.Throws<ArgumentException>(() => selection.ValidateSelection(null));
        Assert.Throws<ArgumentException>(() => selection.ValidateSelection(new CodecChoice("unknown", "Unknown")));
        Assert.Throws<ArgumentException>(() => selection.Find("unknown"));
    }

    [Theory]
    [MemberData(nameof(EncoderCompatibilityTests.CaptureMatrix), MemberType = typeof(EncoderCompatibilityTests))]
    public void PickerAndCapturePolicyAgree(uint vendor, bool hdr, string codec)
    {
        var selection = new CodecSelection(vendor, hdr);
        var reason = EncoderCompatibility.UnsupportedCaptureReason(vendor, hdr, codec);
        Assert.Equal(reason is null, selection.Find(codec) is not null);
        if (reason is not null)
            Assert.Equal(reason, Assert.Throws<NotSupportedException>(
                () => EncoderCompatibility.ValidateCapture(vendor, hdr, codec)).Message);
    }
}
