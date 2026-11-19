using System.Text.Json;
using RedrainCast.Protocol;

namespace RedrainCast.Receive.Core;

public sealed record CastRequest(string VideoCodec, string? AudioCodec, int FramesPerSecond,
    string ReceiverOrientation, ReceiverOptions? Options = null, int? ConfiguredVideoBitrate = null,
    HdrVideoFormat? VideoColor = null)
{
    public HdrVideoFormat ColorFormat => VideoColor ?? HdrVideoFormat.Sdr;
    public ReceiverOptions EffectiveOptions => Options ?? ReceiverOptions.None;
    public uint[] Tracks => AudioCodec is null ? [1u] : [1u, 2u];
    public PresentationOrientation Presentation => ParsePresentation(ReceiverOrientation);

    public void ValidatePresentation(bool supportsSdrRotation)
    {
        if (Presentation != PresentationOrientation.Auto && !ColorFormat.Hdr10 && !supportsSdrRotation)
            throw new ProtocolException(
                "Forced Landscape or Portrait requires hardware SDR video rotation. Select Automatic for this receiver.",
                "UNSUPPORTED_CONFIG");
    }

    private static PresentationOrientation ParsePresentation(string orientation) => orientation switch
    {
        "auto" => PresentationOrientation.Auto,
        "landscape" => PresentationOrientation.Landscape,
        "portrait" => PresentationOrientation.Portrait,
        _ => throw new ProtocolException("Invalid receiver orientation.", "UNSUPPORTED_CONFIG")
    };

    // Capture cap; zero means uncapped/unknown, not a zero-rate stream.
    public int VideoProcessingFramesPerSecond
    {
        get
        {
            try { ColorFormat.Validate(VideoCodec); }
            catch (FormatException error) { throw new ProtocolException(error.Message, "UNSUPPORTED_CONFIG"); }
            _ = Presentation;
            JsonObject.Require(VideoCodec is "h264" or "h265" or "av1" && FramesPerSecond is >= 0 and <= 240,
                "Supports hardware-only H.264, HEVC Main/Main10 and AV1 Profile 0, and uncapped or 1 to 240 FPS requests. HDR10 requires explicit negotiation. Actual codec/raster support and throughput depend on hardware. No substitution.",
                "UNSUPPORTED_CONFIG");
            return FramesPerSecond;
        }
    }

    public static CastRequest Parse(JsonElement root, ReceiverOptionCapabilities? negotiated = null,
        HdrReceiverCapabilities? hdrCapabilities = null)
    {
        ReceiverOptions options;
        try { options = ReceiverOptionProtocol.Parse(root, negotiated ?? ReceiverOptionCapabilities.None); }
        catch (FormatException error) { throw new ProtocolException(error.Message); }
        JsonObject.Require(!root.TryGetProperty("mediaTransport", out _),
            "Only built-in TCP media is supported; transport overrides are not accepted.", "UNSUPPORTED_CONFIG");
        var video = JsonObject.Member(root, "video", JsonValueKind.Object);
        var codec = JsonObject.Text(video, "codec");
        JsonObject.Require(codec is "h264" or "h265" or "av1", "Unsupported video codec.", "UNSUPPORTED_CONFIG");
        HdrVideoFormat color;
        try
        {
            color = HdrVideoProtocol.Format(root);
            color.Validate(codec);
        }
        catch (FormatException error) { throw new ProtocolException(error.Message, "UNSUPPORTED_CONFIG"); }
        JsonObject.Require(!color.Hdr10 || hdrCapabilities?.CanReceiveHdr10 == true,
            "HDR10 was not negotiated or HDR playback is unavailable.", "UNSUPPORTED_CONFIG");
        _ = JsonObject.Text(video, "encoder", 256);
        _ = JsonObject.Text(video, "codecOptions", 8192);
        JsonObject.Require(JsonObject.Number(video, "bitrate") > 0 &&
            JsonObject.Number(video, "maxSize") is >= 0 and <= 16384 &&
            JsonObject.Number(video, "displayId") >= 0, "Invalid video limits.", "UNSUPPORTED_CONFIG");
        var fps = JsonObject.Number(video, "maxFps");
        JsonObject.Require(fps is >= 0 and <= 240, "Invalid FPS: use 0 for uncapped or an integer cap from 1 to 240.", "UNSUPPORTED_CONFIG");
        JsonObject.Require(JsonObject.Text(video, "orientation") is "0" or "90" or "180" or "270" or
            "@0" or "@90" or "@180" or "@270", "Invalid capture orientation.", "UNSUPPORTED_CONFIG");
        JsonObject.Require(video.TryGetProperty("crop", out var crop), "Missing crop.", "UNSUPPORTED_CONFIG");
        if (crop.ValueKind != JsonValueKind.Null)
        {
            JsonObject.Require(crop.ValueKind == JsonValueKind.Object &&
                JsonObject.Number(crop, "width") > 0 && JsonObject.Number(crop, "height") > 0 &&
                JsonObject.Number(crop, "x") >= 0 && JsonObject.Number(crop, "y") >= 0,
                "Invalid crop.", "UNSUPPORTED_CONFIG");
        }
        var orientation = root.TryGetProperty("receiverOrientation", out _)
            ? JsonObject.Text(root, "receiverOrientation") : "auto";
        _ = ParsePresentation(orientation);
        var audio = JsonObject.Member(root, "audio", JsonValueKind.Object);
        var enabled = JsonObject.Boolean(audio, "enabled");
        var audioCodec = JsonObject.Text(audio, "codec");
        JsonObject.Require(audioCodec is "raw" or "opus" or "aac", "Unsupported audio codec.", "UNSUPPORTED_CONFIG");
        _ = JsonObject.Text(audio, "encoder", 256);
        _ = JsonObject.Text(audio, "source");
        _ = JsonObject.Text(audio, "codecOptions", 8192);
        JsonObject.Require(JsonObject.Number(audio, "bitrate") > 0, "Invalid audio bitrate.", "UNSUPPORTED_CONFIG");
        return new(codec, enabled ? audioCodec : null, fps, orientation, options, JsonObject.Number(video, "bitrate"),
            color.Hdr10 ? color : null);
    }
}
