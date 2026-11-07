using System.Text.Json;
using RedrainCast.Protocol;

namespace ScrcpyReceive.Core;

public sealed record CastRequest(string VideoCodec, string? AudioCodec, int FramesPerSecond,
    string ReceiverOrientation, ReceiverOptions? Options = null, int? ConfiguredVideoBitrate = null)
{
    public ReceiverOptions EffectiveOptions => Options ?? ReceiverOptions.None;
    public uint[] Tracks => AudioCodec is null ? [1u] : [1u, 2u];

    // Session content rate, not a monitor refresh rate or measured throughput guarantee.
    public int VideoProcessingFramesPerSecond
    {
        get
        {
            JsonObject.Require(VideoCodec is "h264" or "h265" or "av1" &&
                ReceiverOrientation == "auto" && FramesPerSecond is >= 1 and <= 240,
                "Supports hardware-only 8-bit H.264, HEVC Main and AV1 Profile 0, auto presentation, and requests from 1 to 240 FPS. Actual codec/raster support and throughput depend on hardware. No substitution.",
                "UNSUPPORTED_CONFIG");
            return FramesPerSecond;
        }
    }

    public static CastRequest Parse(JsonElement root, ReceiverOptionCapabilities? negotiated = null)
    {
        ReceiverOptions options;
        try { options = ReceiverOptionProtocol.Parse(root, negotiated ?? ReceiverOptionCapabilities.None); }
        catch (FormatException error) { throw new ProtocolException(error.Message); }
        JsonObject.Require(!root.TryGetProperty("mediaTransport", out _),
            "Only built-in TCP media is supported; transport overrides are not accepted.", "UNSUPPORTED_CONFIG");
        var video = JsonObject.Member(root, "video", JsonValueKind.Object);
        var codec = JsonObject.Text(video, "codec");
        JsonObject.Require(codec is "h264" or "h265" or "av1", "Unsupported video codec.", "UNSUPPORTED_CONFIG");
        _ = JsonObject.Text(video, "encoder", 256);
        _ = JsonObject.Text(video, "codecOptions", 8192);
        JsonObject.Require(JsonObject.Number(video, "bitrate") > 0 &&
            JsonObject.Number(video, "maxSize") is >= 0 and <= 16384 &&
            JsonObject.Number(video, "displayId") >= 0, "Invalid video limits.", "UNSUPPORTED_CONFIG");
        var fps = JsonObject.Number(video, "maxFps");
        JsonObject.Require(fps is >= 1 and <= 240, "Invalid FPS.", "UNSUPPORTED_CONFIG");
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
        JsonObject.Require(orientation is "auto" or "landscape" or "portrait",
            "Invalid receiver orientation.", "UNSUPPORTED_CONFIG");
        var audio = JsonObject.Member(root, "audio", JsonValueKind.Object);
        var enabled = JsonObject.Boolean(audio, "enabled");
        var audioCodec = JsonObject.Text(audio, "codec");
        JsonObject.Require(audioCodec is "raw" or "opus" or "aac", "Unsupported audio codec.", "UNSUPPORTED_CONFIG");
        _ = JsonObject.Text(audio, "encoder", 256);
        _ = JsonObject.Text(audio, "source");
        _ = JsonObject.Text(audio, "codecOptions", 8192);
        JsonObject.Require(JsonObject.Number(audio, "bitrate") > 0, "Invalid audio bitrate.", "UNSUPPORTED_CONFIG");
        return new(codec, enabled ? audioCodec : null, fps, orientation, options, JsonObject.Number(video, "bitrate"));
    }
}
