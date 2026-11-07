using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedrainCast.Protocol;

namespace RedrainCast.Send.Core;

public sealed record CaptureSettings(int MaxSize = 1920, int MaxFps = 60, int VideoBitrate = 8_000_000,
    bool EnableAudio = false, string ReceiverOrientation = "auto", string VideoCodec = "h264",
    bool ShowPlaybackStatistics = false)
{
    public static IReadOnlyList<int> FpsPresets { get; } = Array.AsReadOnly<int>([15, 24, 25, 30, 48, 50, 60, 90, 120]);

    public void Validate()
    {
        if (MaxSize is < 64 or > 3840 || MaxFps is < 1 or > 120 || VideoBitrate is < 100_000 or > 100_000_000)
            throw new ArgumentException("Size must be 64-3840, FPS 1-120 and bitrate 100000-100000000 bits/s.");
        if (ReceiverOrientation is not ("auto" or "landscape" or "portrait"))
            throw new ArgumentException("Invalid receiver orientation.");
        if (VideoCodec is not ("h264" or "h265" or "av1")) throw new ArgumentException("Choose H.264, H.265 or AV1.");
    }

    public static CaptureSettings ReadSaved(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Saved sharing settings must be an object.");
        if (document.RootElement.TryGetProperty("MediaTransport", out var transport) &&
            (transport.ValueKind != JsonValueKind.String || transport.GetString() != "tcp"))
            throw new NotSupportedException("Saved settings select an unsupported transport. Only TCP is supported. Open Settings and save to replace them.");
        var settings = document.RootElement.Deserialize(CaptureSettingsJsonContext.Default.CaptureSettings)
            ?? throw new InvalidDataException("Saved sharing settings are empty.");
        settings.Validate();
        return settings;
    }

    public static CaptureInput ParseInput(string maximumDimension, string bitrateMbps, int? fps,
        string receiverOrientation, CultureInfo culture, string videoCodec = "h264", bool enableAudio = false,
        bool showPlaybackStatistics = false)
    {
        string? dimensionError = null, bitrateError = null, fpsError = null;
        if (!int.TryParse(maximumDimension.Trim(), NumberStyles.None, culture, out var size) || size is < 64 or > 3840)
            dimensionError = "Enter a whole number from 64 to 3840 pixels.";
        const NumberStyles decimalStyle = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite |
                                          NumberStyles.AllowTrailingWhite;
        if (!decimal.TryParse(bitrateMbps, decimalStyle, culture, out var mbps) || mbps is < 0.1m or > 100m)
            bitrateError = $"Enter a bitrate from {0.1m.ToString(culture)} to 100 Mbps.";
        else if (mbps * 1_000_000 != decimal.Truncate(mbps * 1_000_000))
            bitrateError = "Bitrate must represent a whole number of bits/s (up to six fractional digits in Mbps).";
        if (fps is null or < 1 or > 120) fpsError = "Choose an FPS cap from 1 to 120.";
        CaptureSettings? settings = null;
        if (dimensionError == null && bitrateError == null && fpsError == null)
        {
            settings = new(size, fps!.Value, (int)(mbps * 1_000_000), enableAudio, receiverOrientation, videoCodec, showPlaybackStatistics);
            settings.Validate();
        }
        return new(settings, dimensionError, bitrateError, fpsError);
    }

    public JsonObject Request(string hardwareEncoder, ReceiverOptions? effectiveOptions = null)
    {
        Validate();
        var request = JsonData.Object(
            ("video", JsonData.Object(
                ("codec", VideoCodec), ("encoder", hardwareEncoder), ("bitrate", VideoBitrate),
                ("maxSize", MaxSize), ("maxFps", MaxFps), ("displayId", 0), ("orientation", "0"),
                ("crop", null), ("codecOptions", ""))),
            ("receiverOrientation", ReceiverOrientation),
            ("audio", JsonData.Object(
                ("enabled", EnableAudio), ("codec", "raw"), ("encoder", ""), ("bitrate", 1_536_000),
                ("source", "output"), ("codecOptions", ""))));
        if (effectiveOptions?.PlaybackStats == true)
            request["receiverOptions"] = ReceiverOptionProtocol.WireValue(effectiveOptions);
        return request;
    }

    public sealed record CaptureInput(CaptureSettings? Settings, string? DimensionError, string? BitrateError, string? FpsError);

    public (int Width, int Height) OutputSize(int width, int height)
    {
        Validate();
        if (width <= 0 || height <= 0) throw new ArgumentException("Invalid capture dimensions.");
        var scale = Math.Min(1.0, (double)MaxSize / Math.Max(width, height));
        return (Math.Max(2, (int)(width * scale) & ~1), Math.Max(2, (int)(height * scale) & ~1));
    }
}
