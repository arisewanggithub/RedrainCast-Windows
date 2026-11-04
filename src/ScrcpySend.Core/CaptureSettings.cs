using System.Globalization;

namespace ScrcpySend.Core;

public sealed record CaptureSettings(int MaxSize = 1920, int MaxFps = 60, int VideoBitrate = 8_000_000,
    bool EnableAudio = false, string ReceiverOrientation = "auto", string VideoCodec = "h264", string MediaTransport = "tcp")
{
    public static IReadOnlyList<int> FpsPresets { get; } = Array.AsReadOnly<int>([15, 24, 25, 30, 50, 60, 90, 120]);

    public void Validate()
    {
        if (MaxSize is < 64 or > 3840 || MaxFps is < 1 or > 120 || VideoBitrate is < 100_000 or > 100_000_000)
            throw new ArgumentException("Size must be 64-3840, FPS 1-120 and bitrate 100000-100000000 bits/s.");
        if (ReceiverOrientation is not ("auto" or "landscape" or "portrait"))
            throw new ArgumentException("Invalid receiver orientation.");
        if (VideoCodec is not ("h264" or "h265" or "av1")) throw new ArgumentException("Choose H.264, H.265 or AV1.");
        if (MediaTransport is not ("tcp" or "srt")) throw new ArgumentException("Choose custom TCP or SRT/fMP4.");
    }

    public static CaptureInput ParseInput(string maximumDimension, string bitrateMbps, int? fps,
        string receiverOrientation, CultureInfo culture, string videoCodec = "h264", bool enableAudio = false, string mediaTransport = "tcp")
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
            settings = new(size, fps!.Value, (int)(mbps * 1_000_000), enableAudio, receiverOrientation, videoCodec, mediaTransport);
            settings.Validate();
        }
        return new(settings, dimensionError, bitrateError, fpsError);
    }

    public object Request(string hardwareEncoder, SrtDescriptor? srt = null)
    {
        Validate();
        if ((MediaTransport == "srt") != (srt != null)) throw new ArgumentException("SRT requires an explicitly prepared media descriptor.");
        var request = new Dictionary<string, object>
        {
            ["video"] = new
            {
                codec = VideoCodec, encoder = hardwareEncoder, bitrate = VideoBitrate,
                maxSize = MaxSize, maxFps = MaxFps, displayId = 0, orientation = "0",
                crop = (object?)null, codecOptions = ""
            },
            ["receiverOrientation"] = ReceiverOrientation,
            ["audio"] = new { enabled = EnableAudio, codec = MediaTransport == "srt" ? "aac" : "raw", encoder = "", bitrate = MediaTransport == "srt" ? 192000 : 1_536_000, source = "output", codecOptions = "" }
        };
        if (srt != null) request["mediaTransport"] = srt.Json();
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
