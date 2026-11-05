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

}
