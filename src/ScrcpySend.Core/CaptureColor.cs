using System.Text.Json.Serialization;

namespace ScrcpySend.Core;

public sealed record CaptureColor(bool Hdr, float? SdrWhiteNits = null, float? PeakNits = null)
{
    public static CaptureColor Sdr { get; } = new(false, 80, 80);

    public static CaptureColor FromHdrDisplayMetadata(float white, float peak) =>
        new(true, Valid(white) ? white : null, Valid(peak) ? peak : null);

    private static bool Valid(float value) => float.IsFinite(value) && value is >= 1 and <= 10000;

    [JsonIgnore]
    public string LuminanceDescription =>
        $"display white={SdrWhiteNits?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} nits, " +
        $"display peak={PeakNits?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} nits";

    public void Validate()
    {
        if (SdrWhiteNits is { } white && !Valid(white) || PeakNits is { } peak && !Valid(peak))
            throw new NotSupportedException("Provided display luminance metadata is outside the supported range.");
    }

    [JsonIgnore]
    public float WhiteScale => (SdrWhiteNits ??
        throw new InvalidOperationException("Display SDR white luminance is unknown.")) / 80;
    [JsonIgnore]
    public float PeakScale => Math.Max(1, (PeakNits ??
        throw new InvalidOperationException("Display peak luminance is unknown.")) /
        (SdrWhiteNits ?? throw new InvalidOperationException("Display SDR white luminance is unknown.")));
}
