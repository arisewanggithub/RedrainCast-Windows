using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

const int size = 65;
Vector3[] samples = [new(0), new(0.1f), new(0.25f), new(1), new(2.25f), new(4), new(8), new(10),
    new(2, 0, 0), new(0, 2, 0), new(0, 0, 2), new(0.1f, 0.4f, 1),
    new(4, 2, 1), new(-0.1f, 0.5f, 1.5f), new(1.5f, 0.4f, 0.01f), new(0.1f, 1.5f, 0.4f)];
if (args.Length < 2 || args[0] is not ("cube" or "pack" or "samples" or "fixture"))
    throw new ArgumentException("Usage: ToneMapGenerator cube|pack|samples <file>, or fixture <raw> <json>.");
if (args[0] == "cube")
{
    using var file = File.Create(args[1]);
    using var writer = new BinaryWriter(file);
    for (var b = 0; b < size; b++)
    for (var g = 0; g < size; g++)
    for (var r = 0; r < size; r++)
    {
        writer.Write((ushort)Math.Round(r * 65535.0 / (size - 1)));
        writer.Write((ushort)Math.Round(g * 65535.0 / (size - 1)));
        writer.Write((ushort)Math.Round(b * 65535.0 / (size - 1)));
        writer.Write(ushort.MaxValue);
    }
}
else if (args[0] == "pack")
{
    var bytes = File.ReadAllBytes(args[1]);
    if (bytes.Length != size * size * size * 8) throw new InvalidDataException("Unexpected reference cube dimensions.");
    using var file = File.Create(args[1] + ".gz");
    using var compressed = new GZipStream(file, CompressionLevel.SmallestSize);
    using var writer = new BinaryWriter(compressed);
    for (var offset = 0; offset < bytes.Length; offset += 2)
        writer.Write(BitConverter.HalfToUInt16Bits((Half)(BitConverter.ToUInt16(bytes, offset) / 65535f)));
}
else if (args[0] == "samples")
{
    using var file = File.Create(args[1]);
    using var writer = new BinaryWriter(file);
    for (var y = 0; y < 64; y++)
    for (var x = 0; x < 64; x++)
    {
        var rgb = samples[y / 16 * 4 + x / 16];
        writer.Write(Pq(80 * (0.627404f * rgb.X + 0.329283f * rgb.Y + 0.0433136f * rgb.Z)));
        writer.Write(Pq(80 * (0.069097f * rgb.X + 0.919540f * rgb.Y + 0.0113612f * rgb.Z)));
        writer.Write(Pq(80 * (0.0163916f * rgb.X + 0.0880132f * rgb.Y + 0.895595f * rgb.Z)));
        writer.Write(ushort.MaxValue);
    }
}
else
{
    if (args.Length != 3) throw new ArgumentException("fixture requires raw and JSON paths.");
    var bytes = File.ReadAllBytes(args[1]);
    if (bytes.Length != 64 * 64 * 8) throw new InvalidDataException("Unexpected reference swatch dimensions.");
    var fixture = samples.Select((rgb, index) =>
    {
        var x = index % 4 * 16 + 8;
        var y = index / 4 * 16 + 8;
        var offset = (y * 64 + x) * 8;
        return new { input = new[] { rgb.X, rgb.Y, rgb.Z }, peak = 800,
            expected = new[] { BitConverter.ToUInt16(bytes, offset) / 65535f,
                BitConverter.ToUInt16(bytes, offset + 2) / 65535f, BitConverter.ToUInt16(bytes, offset + 4) / 65535f } };
    });
    File.WriteAllText(args[2], JsonSerializer.Serialize(new { method = "Independent libplacebo BT.2390 render, not LUT vertex samples", fixture },
        new JsonSerializerOptions { WriteIndented = true }));
}

static ushort Pq(float nits)
{
    var power = MathF.Pow(Math.Clamp(nits, 0, 10000) / 10000, 2610f / 16384);
    return (ushort)Math.Clamp((int)MathF.Round(65535 * MathF.Pow((3424f / 4096 + 2413f / 128 * power) /
        (1 + 2392f / 128 * power), 2523f / 32)), 0, 65535);
}
