namespace RedrainCast.Send.Core;

public sealed class HdrLuminance
{
    public const int HistogramBins = 256;
    private float peak;
    private float averagePq;
    public float PeakNits => peak;

    public float Observe(ReadOnlySpan<uint> histogram)
    {
        if (histogram.Length != HistogramBins) throw new ArgumentException("Expected 256 luminance bins.");
        long count = 0;
        double weighted = 0;
        for (var index = 0; index < histogram.Length; index++)
        {
            count += histogram[index];
            weighted += histogram[index] * (double)index / (HistogramBins - 1);
        }
        if (count == 0) throw new InvalidDataException("HDR luminance analysis produced no valid pixels.");
        var threshold = (long)Math.Ceiling(count * 0.99995);
        long cumulative = 0;
        var bin = 0;
        for (; bin < histogram.Length - 1; bin++)
        {
            cumulative += histogram[bin];
            if (cumulative >= threshold) break;
        }
        var measured = Math.Clamp(FromPq(Math.Min(1, (bin + 1f) / (HistogramBins - 1))), 100, 10000);
        var mean = (float)(weighted / count);
        var sceneChange = peak == 0 || Math.Abs(mean - averagePq) >= 0.03f;
        var weight = sceneChange ? 1f : 0.18126925f;
        peak += weight * (measured - peak);
        averagePq += weight * (mean - averagePq);
        return peak;
    }

    public static float ToPq(float nits)
    {
        var power = MathF.Pow(Math.Clamp(nits, 0, 10000) / 10000, 2610f / 16384);
        return MathF.Pow((3424f / 4096 + 2413f / 128 * power) / (1 + 2392f / 128 * power), 2523f / 32);
    }

    public static float FromPq(float pq)
    {
        var power = MathF.Pow(Math.Clamp(pq, 0, 1), 32f / 2523);
        return 10000 * MathF.Pow(Math.Max(0, power - 3424f / 4096) / Math.Max(1e-8f, 2413f / 128 - 2392f / 128 * power), 16384f / 2610);
    }
}
