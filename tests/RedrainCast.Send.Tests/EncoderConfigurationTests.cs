using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class EncoderConfigurationTests
{
    // Qualcomm Adreno X1-85 output from a synthetic 640x360 color probe, not screen capture.
    private const string Parameters =
        "0000000140010C01FFFF016000000300B00000030000030099AC0C01000003001E0000030140" +
        "00000001420101016000000300B00000030000030099A005020181F1B96BB9324BAC05A8080808200800000300F0000005DA1425";
    private static readonly byte[] MediaTypeConfiguration = Convert.FromHexString(Parameters + "000000014401340F306604E03B0840");
    private static readonly byte[] Picture = Convert.FromHexString(Parameters +
        "000000014401C0F3C0EC21000000012601AFB0F54192302A23C8342095058C0C4FFCFE4C2DD974BAAB3CF74C1B5571142EEB91F633198D6B108D3E7C9CB85F5884946E92A96D57C92A9AC021C54198C1447415C6DC59C7B03D10867D8717EF222A0CC60A23A0AE36E2CE3D81E88433EC38BF7911506630511D3F80");

    [Fact]
    public void QualcommFirstPictureDeclaresItsActualPpsRatherThanTheMediaTypePps()
    {
        VideoBitstream.ValidateConfiguration("h265", MediaTypeConfiguration, 640, 360);
        Assert.Throws<ProtocolException>(() => VideoBitstream.ValidateFrame("h265", Picture, MediaTypeConfiguration));
        Assert.True(VideoBitstream.HasConfiguration("h265", Picture));
        var actual = VideoBitstream.Configuration("h265", Picture);
        Assert.False(actual.AsSpan().SequenceEqual(MediaTypeConfiguration));
        VideoBitstream.ValidateConfiguration("h265", actual, 640, 360);
        VideoBitstream.ValidateFrame("h265", Picture, actual);
        Assert.True(VideoBitstream.IsKeyFrame("h265", Picture, actual));
        Assert.Throws<ProtocolException>(() => VideoBitstream.ValidateConfiguration("h265", actual, 1280, 720));
    }

    [Fact]
    public void LaterUnannouncedParameterChangesRemainRejected()
    {
        var configuration = VideoBitstream.Configuration("h265", Picture);
        var changed = AnnexB.Join(AnnexB.Units(MediaTypeConfiguration)
            .Concat(AnnexB.Units(Picture).Where(unit => (unit[0] >> 1 & 63) <= 31)));
        Assert.Throws<ProtocolException>(() => VideoBitstream.ValidateFrame("h265", changed, configuration));
        var withoutHeaders = AnnexB.Join(AnnexB.Units(Picture).Where(unit => (unit[0] >> 1 & 63) <= 31));
        Assert.False(VideoBitstream.HasConfiguration("h265", withoutHeaders));
        VideoBitstream.ValidateFrame("h265", withoutHeaders, configuration);
    }

    [Fact]
    public void IncompleteInlineHeadersCannotSilentlyUseTheMediaTypeConfiguration()
    {
        var partial = AnnexB.Join(AnnexB.Units(Picture).Where(unit => (unit[0] >> 1 & 63) != 34));
        Assert.True(VideoBitstream.HasConfiguration("h265", partial));
        Assert.Throws<ProtocolException>(() => VideoBitstream.Configuration("h265", partial));
    }

    [Fact]
    public void HardwareSelectsInlineConfigurationOnlyBeforeTheFirstPictureIsPublished()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "HardwareEncoder.cs.txt"));
        var initial = source.IndexOf("if (configuration.Length == 0)", StringComparison.Ordinal);
        var inline = source.IndexOf("if (VideoBitstream.HasConfiguration(settings.VideoCodec, bytes))", initial, StringComparison.Ordinal);
        var metadata = source.IndexOf("transform.GetOutputCurrentType(0)", initial, StringComparison.Ordinal);
        var validate = source.IndexOf("VideoBitstream.ValidateFrame(settings.VideoCodec, bytes, configuration)", initial, StringComparison.Ordinal);
        Assert.True(initial >= 0 && inline > initial && metadata > inline && validate > metadata);
        Assert.Contains("Hardware encoder changed configuration during the fixed-format session.", source);
    }
}
