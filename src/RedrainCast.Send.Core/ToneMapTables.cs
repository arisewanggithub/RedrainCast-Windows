using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

namespace RedrainCast.Send.Core;

public static class ToneMapTables
{
    public const int Size = 65;
    public static IReadOnlyList<int> Peaks { get; } = Array.AsReadOnly<int>([100, 200, 400, 800, 1600, 3200, 6400, 10000]);
    private static readonly Lazy<Half[][]> Tables = new(() => Peaks.Select(Load).ToArray());

    public static (int Low, int High, float Weight) Select(float peak)
    {
        if (!float.IsFinite(peak) || peak <= 0) throw new ArgumentOutOfRangeException(nameof(peak));
        if (peak <= Peaks[0]) return (0, 0, 0);
        for (var high = 1; high < Peaks.Count; high++)
        {
            if (peak > Peaks[high]) continue;
            var low = high - 1;
            var weight = MathF.Log(peak / Peaks[low]) / MathF.Log((float)Peaks[high] / Peaks[low]);
            return (low, high, weight);
        }
        return (Peaks.Count - 1, Peaks.Count - 1, 0);
    }

    public static ReadOnlyMemory<Half> Data(int index) => Tables.Value[index];

    public static Vector3 Sample(Vector3 scRgb, float peak)
    {
        var rgb = new Vector3(
            0.627404f * scRgb.X + 0.329283f * scRgb.Y + 0.0433136f * scRgb.Z,
            0.069097f * scRgb.X + 0.919540f * scRgb.Y + 0.0113612f * scRgb.Z,
            0.0163916f * scRgb.X + 0.0880132f * scRgb.Y + 0.895595f * scRgb.Z);
        var pq = new Vector3(HdrLuminance.ToPq(rgb.X * 80), HdrLuminance.ToPq(rgb.Y * 80), HdrLuminance.ToPq(rgb.Z * 80));
        var selection = Select(peak);
        return Vector3.Lerp(SampleTable(selection.Low, pq), SampleTable(selection.High, pq), selection.Weight);
    }

    private static Vector3 SampleTable(int table, Vector3 pq)
    {
        var coordinate = pq * (Size - 1);
        var x = Math.Clamp((int)coordinate.X, 0, Size - 2);
        var y = Math.Clamp((int)coordinate.Y, 0, Size - 2);
        var z = Math.Clamp((int)coordinate.Z, 0, Size - 2);
        var fraction = coordinate - new Vector3(x, y, z);
        var values = Tables.Value[table];
        Vector3 Point(int dx, int dy, int dz)
        {
            var offset = (((z + dz) * Size + y + dy) * Size + x + dx) * 4;
            return new((float)values[offset], (float)values[offset + 1], (float)values[offset + 2]);
        }
        // Tetrahedral interpolation preserves the neutral axis instead of mixing off-gray cube corners.
        (int X, int Y, int Z) first, second;
        Vector3 weights;
        if (fraction.X >= fraction.Y)
        {
            if (fraction.Y >= fraction.Z) { first = (1, 0, 0); second = (1, 1, 0); weights = fraction; }
            else if (fraction.X >= fraction.Z) { first = (1, 0, 0); second = (1, 0, 1); weights = new(fraction.X, fraction.Z, fraction.Y); }
            else { first = (0, 0, 1); second = (1, 0, 1); weights = new(fraction.Z, fraction.X, fraction.Y); }
        }
        else
        {
            if (fraction.X >= fraction.Z) { first = (0, 1, 0); second = (1, 1, 0); weights = new(fraction.Y, fraction.X, fraction.Z); }
            else if (fraction.Y >= fraction.Z) { first = (0, 1, 0); second = (0, 1, 1); weights = new(fraction.Y, fraction.Z, fraction.X); }
            else { first = (0, 0, 1); second = (0, 1, 1); weights = new(fraction.Z, fraction.Y, fraction.X); }
        }
        return Point(0, 0, 0) * (1 - weights.X) + Point(first.X, first.Y, first.Z) * (weights.X - weights.Y) +
            Point(second.X, second.Y, second.Z) * (weights.Y - weights.Z) + Point(1, 1, 1) * weights.Z;
    }

    private static Half[] Load(int peak)
    {
        using var resource = typeof(ToneMapTables).Assembly.GetManifestResourceStream($"RedrainCast.Send.Core.ToneMaps.bt2390-{peak}.rgba16f.gz")
            ?? throw new InvalidDataException($"Missing BT.2390 table for {peak} nits.");
        using var compressed = new GZipStream(resource, CompressionMode.Decompress);
        var bytes = new byte[Size * Size * Size * 8];
        compressed.ReadExactly(bytes);
        if (compressed.ReadByte() != -1) throw new InvalidDataException("Tone-map table has unexpected trailing data.");
        var result = MemoryMarshal.Cast<byte, Half>(bytes).ToArray();
        if (result.Any(value => !Half.IsFinite(value) || value < (Half)0 || value > (Half)1))
            throw new InvalidDataException("Tone-map table contains invalid RGB values.");
        return result;
    }
}
