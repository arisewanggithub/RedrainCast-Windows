using RedrainCast.Protocol;

namespace RedrainCast.Send.Core;

public static class HdrDisplayMetadata
{
    public static HdrStaticMetadata? Create(double maximum, double minimum,
        (double X, double Y) red, (double X, double Y) green,
        (double X, double Y) blue, (double X, double Y) white, Action<string>? diagnostic = null)
    {
        const string omission = "omitting desktop color-volume metadata. Native HDR10 remains enabled. " +
            "No BT.2020 mastering defaults or content MaxCLL/MaxFALL are invented.";
        static bool ValidPoint((double X, double Y) point) =>
            double.IsFinite(point.X) && double.IsFinite(point.Y) && point.X > 0 && point.Y > 0 &&
            point.X < 1 && point.Y < 1 && point.X + point.Y <= 1.00001;
        var triangle = (green.X - red.X) * (blue.Y - red.Y) - (green.Y - red.Y) * (blue.X - red.X);
        if (!ValidPoint(red) || !ValidPoint(green) || !ValidPoint(blue) || !ValidPoint(white) ||
            Math.Abs(triangle) < 0.000001 || !double.IsFinite(maximum) || maximum is < 1 or > 10000 ||
            !double.IsFinite(minimum) || minimum < 0 || minimum > maximum)
        {
            diagnostic?.Invoke("Native HDR10: the display-reported primaries, white point or luminance bounds are unavailable/invalid; " +
                omission);
            return null;
        }

        // Raw domain checks bound every scaled value within Int32 before conversion.
        static int Coordinate(double value) => (int)Math.Round(value * 50000);
        var metadata = new HdrStaticMetadata((int)Math.Ceiling(maximum), (int)Math.Round(minimum * 10000),
            RedX: Coordinate(red.X), RedY: Coordinate(red.Y), GreenX: Coordinate(green.X), GreenY: Coordinate(green.Y),
            BlueX: Coordinate(blue.X), BlueY: Coordinate(blue.Y), WhiteX: Coordinate(white.X), WhiteY: Coordinate(white.Y));
        string? invalid = null;
        try { metadata.Validate(); }
        catch (FormatException error) { invalid = error.Message; }
        if (invalid is not null)
        {
            diagnostic?.Invoke($"Native HDR10: the quantized display color volume is not representable ({invalid}), " +
                $"maximum={metadata.MaxMasteringNits} nits, minimum={metadata.MinMasteringTenThousandths}/10000 nits; " + omission);
            return null;
        }
        diagnostic?.Invoke($"Native HDR10: using the queried desktop display color volume, maximum={metadata.MaxMasteringNits} nits, " +
            $"minimum={metadata.MinMasteringTenThousandths}/10000 nits. These are desktop display bounds, not original video mastering data; content MaxCLL/MaxFALL are unknown (0).");
        return metadata;
    }
}
