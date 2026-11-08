using RedrainCast.Protocol;

namespace RedrainCast.Receive.Core;

public static class HevcStream
{
    private static int Type(ReadOnlyMemory<byte> unit)
    {
        var data = unit.Span;
        JsonObject.Require(data.Length >= 3 && (data[0] & 0x80) == 0 && (data[1] & 7) != 0, "Invalid HEVC NAL header.");
        JsonObject.Require((data[0] & 1) == 0 && (data[1] >> 3) == 0, "HEVC multilayer video is unsupported.", "UNSUPPORTED_CONFIG");
        return data[0] >> 1 & 63;
    }

    public static ReadOnlyMemory<byte>[] Parameters(byte[] data) =>
        H264Stream.Units(data).Where(unit => Type(unit) is 32 or 33 or 34).ToArray();

    public static void ValidateConfiguration(byte[] data, int width, int height, HdrVideoFormat? color = null)
    {
        var actual = Dimensions(data, color);
        JsonObject.Require(actual.Width == width && actual.Height == height, "HEVC SPS differs from STREAM_CONFIG dimensions.");
    }

    public static (int Width, int Height) Dimensions(byte[] data, HdrVideoFormat? color = null)
    {
        try { (color ?? HdrVideoFormat.Sdr).Validate("h265"); }
        catch (FormatException error) { throw new ProtocolException(error.Message, "UNSUPPORTED_CONFIG"); }
        var hdr = color?.Hdr10 == true;
        (int Width, int Height)? dimensions = null;
        var parameters = Parameters(data);
        JsonObject.Require(new[] { 32, 33, 34 }.All(type => parameters.Any(unit => Type(unit) == type)),
            "HEVC requires VPS, SPS and PPS.");
        var vpsIds = parameters.Where(unit => Type(unit) == 32).Select(unit => new CodecBits(unit[2..], true).Read(4)).ToHashSet();
        var spsIds = new HashSet<uint>();
        foreach (var sps in parameters.Where(unit => Type(unit) == 33))
        {
            var bits = new CodecBits(sps[2..], true);
            JsonObject.Require(vpsIds.Contains(bits.Read(4)), "HEVC SPS references an unknown VPS.");
            var layers = (int)bits.Read(3);
            JsonObject.Require(layers <= 6, "Invalid HEVC sublayer count.");
            bits.Skip(1);
            bits.Skip(3);
            JsonObject.Require(bits.Read(5) == (hdr ? 2 : 1),
                hdr ? "Negotiated HDR10 requires HEVC Main10." : "Only HEVC Main profile is supported without HDR negotiation.",
                "UNSUPPORTED_CONFIG");
            bits.Skip(32);
            bits.Skip(1);
            JsonObject.Require(bits.Read(1) == 0, "Interlaced HEVC is unsupported.", "UNSUPPORTED_CONFIG");
            bits.Skip(46);
            bits.Skip(8);
            var profiles = new bool[layers];
            var levels = new bool[layers];
            for (var index = 0; index < layers; index++) { profiles[index] = bits.Read(1) != 0; levels[index] = bits.Read(1) != 0; }
            if (layers > 0) bits.Skip((8 - layers) * 2);
            for (var index = 0; index < layers; index++) { if (profiles[index]) bits.Skip(88); if (levels[index]) bits.Skip(8); }
            var spsId = bits.Unsigned();
            JsonObject.Require(spsId <= 15, "Invalid HEVC SPS id.");
            spsIds.Add(spsId);
            JsonObject.Require(bits.Unsigned() == 1, "HEVC requires 4:2:0 chroma.", "UNSUPPORTED_CONFIG");
            long actualWidth = bits.Unsigned(), actualHeight = bits.Unsigned();
            if (bits.Read(1) != 0)
            {
                actualWidth -= 2L * (bits.Unsigned() + (long)bits.Unsigned());
                actualHeight -= 2L * (bits.Unsigned() + (long)bits.Unsigned());
            }
            var depth = hdr ? 2u : 0u;
            JsonObject.Require(bits.Unsigned() == depth && bits.Unsigned() == depth,
                hdr ? "HDR10 requires 10-bit luma/chroma." : "HEVC requires 8-bit luma/chroma.", "UNSUPPORTED_CONFIG");
            if (hdr) ValidateHdrVui(bits, layers);
            JsonObject.Require(actualWidth is > 0 and <= 16384 && actualHeight is > 0 and <= 16384, "Invalid HEVC SPS dimensions.");
            var actual = ((int)actualWidth, (int)actualHeight);
            JsonObject.Require(dimensions is null || dimensions.Value == actual, "Conflicting HEVC SPS dimensions.");
            dimensions = actual;
        }
        foreach (var pps in parameters.Where(unit => Type(unit) == 34))
        {
            var bits = new CodecBits(pps[2..], true);
            JsonObject.Require(bits.Unsigned() <= 63 && spsIds.Contains(bits.Unsigned()), "Invalid or unknown HEVC PPS/SPS ids.");
            bits.Skip(5);
        }
        return dimensions ?? throw new ProtocolException("HEVC SPS is missing.");
    }

    private static void ValidateHdrVui(CodecBits bits, int layers)
    {
        var poc = bits.Unsigned();
        JsonObject.Require(poc <= 12, "Invalid HEVC picture-order bit count.");
        var allLayers = bits.Read(1) != 0;
        for (var layer = allLayers ? 0 : layers; layer <= layers; layer++)
        {
            bits.Unsigned();
            JsonObject.Require(bits.Unsigned() == 0, "HDR10 frame reordering is unsupported.", "UNSUPPORTED_CONFIG");
            bits.Unsigned();
        }
        for (var index = 0; index < 6; index++) bits.Unsigned();
        if (bits.Read(1) != 0 && bits.Read(1) != 0) SkipScalingLists(bits);
        bits.Skip(2);
        if (bits.Read(1) != 0)
        {
            bits.Skip(8);
            bits.Unsigned(); bits.Unsigned(); bits.Skip(1);
        }
        var count = bits.Unsigned();
        JsonObject.Require(count <= 64, "Too many HEVC reference picture sets.");
        var deltaPictures = new int[count];
        for (var index = 0; index < deltaPictures.Length; index++)
        {
            if (index > 0 && bits.Read(1) != 0)
            {
                bits.Skip(1); bits.Unsigned();
                for (var reference = 0; reference <= deltaPictures[index - 1]; reference++)
                {
                    var used = bits.Read(1) != 0;
                    if (used || bits.Read(1) != 0) deltaPictures[index]++;
                }
                JsonObject.Require(deltaPictures[index] <= 64, "Too many predicted HEVC reference pictures.");
            }
            else
            {
                var negative = bits.Unsigned();
                var positive = bits.Unsigned();
                JsonObject.Require(negative <= 64 && positive <= 64 && negative + positive <= 64,
                    "Too many HEVC reference pictures.");
                deltaPictures[index] = (int)(negative + positive);
                for (var reference = 0; reference < deltaPictures[index]; reference++)
                {
                    bits.Unsigned(); bits.Skip(1);
                }
            }
        }
        if (bits.Read(1) != 0)
        {
            var longTerm = bits.Unsigned();
            JsonObject.Require(longTerm <= 32, "Too many HEVC long-term references.");
            for (var index = 0; index < longTerm; index++) bits.Skip((int)poc + 5);
        }
        bits.Skip(2);
        JsonObject.Require(bits.Read(1) != 0, "HDR10 requires HEVC VUI color signaling.", "UNSUPPORTED_CONFIG");
        if (bits.Read(1) != 0 && bits.Read(8) == 255) bits.Skip(32);
        if (bits.Read(1) != 0) bits.Skip(1);
        JsonObject.Require(bits.Read(1) != 0, "HDR10 requires video signal type information.", "UNSUPPORTED_CONFIG");
        bits.Skip(3);
        JsonObject.Require(bits.Read(1) == 0 && bits.Read(1) != 0 &&
            bits.Read(8) == 9 && bits.Read(8) == 16 && bits.Read(8) == 9,
            "HDR10 requires limited-range BT.2020/PQ/BT.2020nc signaling.", "UNSUPPORTED_CONFIG");
    }

    private static void SkipScalingLists(CodecBits bits)
    {
        for (var size = 0; size < 4; size++)
        for (var matrix = 0; matrix < 6; matrix += size == 3 ? 3 : 1)
        {
            if (bits.Read(1) == 0) bits.Unsigned();
            else
            {
                if (size > 1) bits.Signed();
                var coefficients = Math.Min(64, 1 << (4 + 2 * size));
                for (var index = 0; index < coefficients; index++) bits.Signed();
            }
        }
    }

    public static bool ValidateFrame(byte[] data, byte[] configuration)
    {
        var parameters = Parameters(configuration);
        var picture = false;
        var key = false;
        foreach (var unit in H264Stream.Units(data))
        {
            var type = Type(unit);
            if (type is 32 or 33 or 34)
                JsonObject.Require(parameters.Any(known => known.Span.SequenceEqual(unit.Span)), "Undeclared HEVC parameter-set change.");
            if (type > 31) continue;
            JsonObject.Require(type is not (6 or 7 or 8 or 9) && type <= 21, "HEVC leading/reserved pictures are unsupported.", "UNSUPPORTED_CONFIG");
            var bits = new CodecBits(unit[2..], true);
            var first = bits.Read(1) != 0;
            JsonObject.Require(picture || first, "HEVC access unit lacks its first slice.");
            if (!first) continue;
            JsonObject.Require(!picture, "HEVC access unit contains more than one picture.");
            key = type is >= 16 and <= 21;
            if (key) bits.Skip(1);
            var id = bits.Unsigned();
            var found = false;
            foreach (var pps in parameters.Where(parameter => Type(parameter) == 34))
            {
                var header = new CodecBits(pps[2..], true);
                if (header.Unsigned() != id) continue;
                header.Unsigned();
                header.Skip(2);
                bits.Skip((int)header.Read(3));
                JsonObject.Require(bits.Unsigned() is 1 or 2, "HEVC B slices are unsupported.", "UNSUPPORTED_CONFIG");
                found = true;
                break;
            }
            JsonObject.Require(found, "HEVC slice references an unknown PPS.");
            picture = true;
        }
        JsonObject.Require(picture, "HEVC VIDEO_FRAME contains no picture.");
        return key;
    }
}
