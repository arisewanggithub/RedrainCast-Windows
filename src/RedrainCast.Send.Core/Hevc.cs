using System.Buffers.Binary;

namespace RedrainCast.Send.Core;

public static class Hevc
{
    private static int Type(byte[] unit)
    {
        if (unit.Length < 3 || (unit[0] & 0x80) != 0 || (unit[1] & 7) == 0)
            throw new ProtocolException("Invalid HEVC NAL header.");
        return unit[0] >> 1 & 63;
    }

    public static byte[] ParameterSets(byte[] bytes)
    {
        IReadOnlyList<byte[]> units;
        if (bytes.Length >= 23 && bytes[0] == 1)
        {
            var extracted = new List<byte[]>();
            var offset = 23;
            for (var array = 0; array < bytes[22]; array++)
            {
                if (offset + 3 > bytes.Length) throw new ProtocolException("Truncated hvcC parameter array.");
                offset++;
                var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));
                offset += 2;
                for (var index = 0; index < count; index++)
                {
                    if (offset + 2 > bytes.Length) throw new ProtocolException("Truncated hvcC NAL size.");
                    var size = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));
                    offset += 2;
                    if (size == 0 || offset + size > bytes.Length) throw new ProtocolException("Invalid hvcC NAL size.");
                    extracted.Add(bytes[offset..(offset + size)]);
                    offset += size;
                }
            }
            if (offset != bytes.Length) throw new ProtocolException("Trailing hvcC data.");
            units = extracted;
        }
        else units = AnnexB.Units(bytes);
        var parameters = units.Where(unit => Type(unit) is 32 or 33 or 34).ToArray();
        if (!new[] { 32, 33, 34 }.All(type => parameters.Any(unit => Type(unit) == type)))
            throw new ProtocolException("HEVC requires actual VPS, SPS and PPS.");
        return AnnexB.Join(parameters);
    }

    public static bool IsKeyFrame(byte[] bytes) => AnnexB.Units(bytes).Any(unit => Type(unit) is >= 16 and <= 21);

    public static void ValidateConfiguration(byte[] configuration, int width, int height,
        RedrainCast.Protocol.HdrVideoFormat? color = null)
    {
        color?.Validate("h265");
        var hdr = color?.Hdr10 == true;
        var units = AnnexB.Units(ParameterSets(configuration));
        foreach (var sps in units.Where(unit => Type(unit) == 33))
        {
            var bits = new CodecBits(sps.AsSpan(2), true);
            bits.Skip(4);
            var layers = (int)bits.Read(3);
            bits.Skip(1);
            bits.Skip(3);
            if (bits.Read(5) != (hdr ? 2u : 1u))
                throw new ProtocolException($"HEVC hardware encoder did not honor {(hdr ? "Main10" : "Main")} profile.");
            bits.Skip(88);
            var profiles = new bool[layers];
            var levels = new bool[layers];
            for (var index = 0; index < layers; index++)
            {
                profiles[index] = bits.Read(1) != 0;
                levels[index] = bits.Read(1) != 0;
            }
            if (layers > 0) bits.Skip((8 - layers) * 2);
            for (var index = 0; index < layers; index++)
            {
                if (profiles[index]) bits.Skip(88);
                if (levels[index]) bits.Skip(8);
            }
            bits.Unsigned();
            if (bits.Unsigned() != 1) throw new ProtocolException("HEVC requires 4:2:0 chroma.");
            long actualWidth = bits.Unsigned(), actualHeight = bits.Unsigned();
            if (bits.Read(1) != 0)
            {
                actualWidth -= 2L * (bits.Unsigned() + (long)bits.Unsigned());
                actualHeight -= 2L * (bits.Unsigned() + (long)bits.Unsigned());
            }
            if (bits.Unsigned() != (hdr ? 2u : 0u) || bits.Unsigned() != (hdr ? 2u : 0u))
                throw new ProtocolException($"HEVC {(hdr ? "Main10 output must be 10-bit" : "Main output must be 8-bit")}.");
            if (actualWidth != width || actualHeight != height)
                throw new ProtocolException($"Actual HEVC SPS dimensions {actualWidth}x{actualHeight} differ from {width}x{height}.");
            if (hdr) ValidateHdrVui(bits, layers);
        }
    }

    private static uint Bounded(CodecBits bits, uint maximum)
    {
        var value = bits.Unsigned();
        if (value > maximum) throw new ProtocolException("Oversized HEVC SPS syntax.");
        return value;
    }

    private static void ValidateHdrVui(CodecBits bits, int layers)
    {
        var pocBits = checked((int)Bounded(bits, 12) + 4);
        var allLayers = bits.Read(1) != 0;
        for (var layer = allLayers ? 0 : layers; layer <= layers; layer++)
        {
            Bounded(bits, 16);
            if (bits.Unsigned() != 0) throw new ProtocolException("HEVC reordered pictures are forbidden in low-latency sessions.");
            bits.Unsigned();
        }
        for (var index = 0; index < 6; index++) Bounded(bits, 32);
        if (bits.Read(1) != 0 && bits.Read(1) != 0)
        {
            for (var size = 0; size < 4; size++)
                for (var matrix = 0; matrix < 6; matrix += size == 3 ? 3 : 1)
                    if (bits.Read(1) == 0) Bounded(bits, 6);
                    else
                    {
                        if (size > 1) bits.Unsigned();
                        for (var coefficient = 0; coefficient < Math.Min(64, 1 << (4 + 2 * size)); coefficient++)
                            bits.Unsigned();
                    }
        }
        bits.Skip(2); // AMP and SAO.
        if (bits.Read(1) != 0)
        {
            bits.Skip(8);
            Bounded(bits, 32);
            Bounded(bits, 32);
            bits.Skip(1);
        }
        var count = (int)Bounded(bits, 64);
        var deltaCounts = new int[count];
        for (var set = 0; set < count; set++)
        {
            if (set > 0 && bits.Read(1) != 0)
            {
                bits.Skip(1);
                bits.Unsigned();
                var deltas = 0;
                for (var index = 0; index <= deltaCounts[set - 1]; index++)
                {
                    var used = bits.Read(1) != 0;
                    if (used || bits.Read(1) != 0) deltas++;
                }
                deltaCounts[set] = deltas;
            }
            else
            {
                var negative = (int)Bounded(bits, 16);
                var positive = (int)Bounded(bits, 16);
                deltaCounts[set] = negative + positive;
                for (var index = 0; index < negative + positive; index++)
                {
                    bits.Unsigned();
                    bits.Skip(1);
                }
            }
        }
        if (bits.Read(1) != 0)
        {
            var longTermCount = Bounded(bits, 32);
            for (var index = 0; index < longTermCount; index++)
            {
                bits.Skip(pocBits);
                bits.Skip(1);
            }
        }
        bits.Skip(2);
        if (bits.Read(1) == 0) throw new ProtocolException("HDR10 HEVC requires explicit VUI color information.");
        if (bits.Read(1) != 0 && bits.Read(8) == 255) bits.Skip(32);
        if (bits.Read(1) != 0) bits.Skip(1);
        if (bits.Read(1) == 0) throw new ProtocolException("HDR10 HEVC lacks video signal type.");
        bits.Skip(3);
        if (bits.Read(1) != 0 || bits.Read(1) == 0 ||
            bits.Read(8) != 9 || bits.Read(8) != 16 || bits.Read(8) != 9)
            throw new ProtocolException("HDR10 HEVC requires BT.2020 primaries, PQ transfer, BT.2020 nonconstant matrix and limited range.");
    }

    public static void ValidateAccessUnit(byte[] data, byte[] configuration)
    {
        var firstPicture = true;
        var pps = AnnexB.Units(configuration).Where(unit => Type(unit) == 34).ToArray();
        foreach (var unit in AnnexB.Units(data))
        {
            var type = Type(unit);
            if (type > 31) continue;
            var bits = new CodecBits(unit.AsSpan(2), true);
            var firstSlice = bits.Read(1) != 0;
            if (firstPicture && !firstSlice) throw new ProtocolException("HEVC access unit lacks its first slice.");
            if (!firstSlice) continue;
            if (!firstPicture) throw new ProtocolException("HEVC access unit contains more than one picture.");
            if (type is >= 16 and <= 23) bits.Skip(1);
            var ppsId = bits.Unsigned();
            var found = false;
            foreach (var parameter in pps)
            {
                var header = new CodecBits(parameter.AsSpan(2), true);
                if (header.Unsigned() != ppsId) continue;
                header.Unsigned();
                header.Skip(2);
                bits.Skip((int)header.Read(3));
                var slice = bits.Unsigned();
                if (slice is not (1 or 2)) throw new ProtocolException("HEVC B slices are forbidden in low-latency sessions.");
                found = true;
                break;
            }
            if (!found) throw new ProtocolException("HEVC slice references an unknown PPS.");
            firstPicture = false;
        }
        if (firstPicture) throw new ProtocolException("HEVC output has no picture.");
    }
}
