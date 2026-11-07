using System.Text.Json;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class TcpTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestsUseOnlyTcpAndRawAudio(bool audio)
    {
        var settings = new CaptureSettings(EnableAudio: audio);
        var request = JsonSerializer.SerializeToElement(settings.Request("Hardware"));
        Assert.False(request.TryGetProperty("mediaTransport", out _));
        Assert.Equal("raw", request.GetProperty("audio").GetProperty("codec").GetString());
        Assert.Equal(1_536_000, request.GetProperty("audio").GetProperty("bitrate").GetInt32());
        var saved = JsonSerializer.SerializeToElement(settings);
        Assert.False(saved.TryGetProperty("MediaTransport", out _));
        Assert.Null(typeof(CaptureSettings).GetProperty("MediaTransport"));
    }

    [Fact]
    public void LegacyTcpSettingsPreserveSharingValuesWithoutRetainingTransportSelection()
    {
        var settings = CaptureSettings.ReadSaved(
            """{"MaxSize":1280,"MaxFps":30,"VideoBitrate":5000000,"EnableAudio":true,"VideoCodec":"h265","MediaTransport":"tcp"}""");
        Assert.Equal(new CaptureSettings(1280, 30, 5_000_000, true, VideoCodec: "h265"), settings);
        Assert.DoesNotContain("MediaTransport", JsonSerializer.Serialize(settings));
        Assert.Equal(settings, CaptureSettings.ReadSaved(JsonSerializer.Serialize(settings)));
    }

    [Theory]
    [InlineData("\"srt\"")]
    [InlineData("\"unknown\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void UnsupportedSavedTransportIsRejectedRatherThanSilentlyChangingBehavior(string transport)
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            CaptureSettings.ReadSaved($$"""{"MaxSize":1280,"MediaTransport":{{transport}}}"""));
        Assert.Contains("Only TCP is supported", error.Message);
        Assert.Contains("Open Settings", error.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void NonObjectSettingsAreRejected(string json) =>
        Assert.Throws<JsonException>(() => CaptureSettings.ReadSaved(json));
}
