using System.Numerics;

namespace ScrcpySend.Core;

public sealed record CaptureColor(bool Hdr, float SdrWhiteNits = 80, float PeakNits = 80)
{
    public static CaptureColor Sdr { get; } = new(false);

    public void Validate()
    {
        if (!float.IsFinite(SdrWhiteNits) || !float.IsFinite(PeakNits) ||
            SdrWhiteNits is < 1 or > 10000 || PeakNits is < 1 or > 10000)
            throw new NotSupportedException("The capture display did not supply valid SDR white and peak luminance metadata.");
    }

    public float WhiteScale => SdrWhiteNits / 80;
    public float PeakScale => Math.Max(1, PeakNits / SdrWhiteNits);

    public Vector3 MapHdrToSdr(Vector3 scRgb)
    {
        Validate();
        if (!Hdr) throw new InvalidOperationException("SDR capture must use the existing direct conversion path.");
        if (!float.IsFinite(scRgb.X) || !float.IsFinite(scRgb.Y) || !float.IsFinite(scRgb.Z))
            throw new ArgumentException("Capture pixels must be finite.", nameof(scRgb));
        var rgb = scRgb / WhiteScale;
        var luminance = Vector3.Dot(rgb, new(0.2126f, 0.7152f, 0.0722f));
        if (luminance <= 0) return Vector3.Zero;
        // The shoulder preserves midtones, joins with unit slope, and maps the display peak to SDR white.
        const float knee = 0.75f;
        var mapped = luminance <= knee ? luminance :
            knee + (luminance - knee) / (1 + (4 - 1 / (PeakScale - knee)) * (luminance - knee));
        mapped = Math.Clamp(mapped, 0, 1);
        rgb *= mapped / luminance;
        var gray = new Vector3(mapped);
        var minimum = Math.Min(rgb.X, Math.Min(rgb.Y, rgb.Z));
        if (minimum < 0) rgb = gray + (rgb - gray) * (mapped / (mapped - minimum));
        var maximum = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z));
        if (maximum > 1) rgb = gray + (rgb - gray) * ((1 - mapped) / (maximum - mapped));
        return new(Encode709(rgb.X), Encode709(rgb.Y), Encode709(rgb.Z));
    }

    public static float Encode709(float linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        return linear < 0.018f ? 4.5f * linear : 1.099f * MathF.Pow(linear, 0.45f) - 0.099f;
    }
}
