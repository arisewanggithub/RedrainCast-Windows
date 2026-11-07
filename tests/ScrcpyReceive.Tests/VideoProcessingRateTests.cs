using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class VideoProcessingRateTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(240)]
    public void SessionRatePreservesTheExactRequestedContentRate(int fps)
    {
        foreach (var codec in new[] { "h264", "h265", "av1" })
            Assert.Equal(fps, new CastRequest(codec, null, fps, "auto").VideoProcessingFramesPerSecond);
        var settings = System.Text.Json.JsonSerializer.SerializeToNode(RequestTests.Settings(false))!;
        settings["video"]!["maxFps"] = fps;
        var parsed = CastRequest.Parse(JsonObject.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(settings)));
        Assert.Equal(fps, parsed.VideoProcessingFramesPerSecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(241)]
    [InlineData(int.MaxValue)]
    public void DirectRequestsCannotBypassRateBounds(int fps) =>
        Assert.Equal("UNSUPPORTED_CONFIG", Assert.Throws<ProtocolException>(() =>
            new CastRequest("h264", null, fps, "auto").VideoProcessingFramesPerSecond).Code);

    [Theory]
    [InlineData("vp9", "auto")]
    [InlineData("h264", "landscape")]
    [InlineData("h264", "portrait")]
    [InlineData("h265", "invalid")]
    public void CodecAndAutoOrientationRestrictionsRemain(string codec, string orientation) =>
        Assert.Throws<ProtocolException>(() =>
            new CastRequest(codec, null, 120, orientation).VideoProcessingFramesPerSecond);
}
