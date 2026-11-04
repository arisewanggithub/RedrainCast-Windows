namespace ScrcpySend.Core;

public static class Av1
{
    private sealed record Obu(int Type, byte[] Complete, byte[] Payload);

    private static IReadOnlyList<Obu> Units(byte[] data)
    {
        var units = new List<Obu>();
        var offset = 0;
        while (offset < data.Length)
        {
            var start = offset;
            var header = data[offset++];
            if ((header & 0x81) != 0 || (header & 2) == 0) throw new ProtocolException("AV1 requires sized low-overhead OBUs.");
            if ((header & 4) != 0)
            {
                if (offset == data.Length || (data[offset++] & 7) != 0) throw new ProtocolException("Invalid AV1 OBU extension.");
            }
            ulong size = 0;
            var complete = false;
            for (var index = 0; index < 8; index++)
            {
                if (offset == data.Length) throw new ProtocolException("Truncated AV1 OBU length.");
                var value = data[offset++];
                size |= (ulong)(value & 127) << (index * 7);
                if ((value & 128) == 0) { complete = true; break; }
            }
            if (!complete || size > (ulong)(data.Length - offset)) throw new ProtocolException("Invalid AV1 OBU length.");
            var end = offset + (int)size;
            units.Add(new(header >> 3 & 15, data[start..end], data[offset..end]));
            offset = end;
        }
        return units;
    }

    public static byte[] SequenceHeader(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x81) bytes = bytes[4..];
        var header = Units(bytes).FirstOrDefault(unit => unit.Type == 1)
            ?? throw new ProtocolException("AV1 encoder did not provide its actual sequence-header OBU.");
        if (header.Payload.Length == 0) throw new ProtocolException("Empty AV1 sequence header.");
        return header.Complete;
    }

    public static bool HasSequenceHeader(byte[] bytes) => Units(bytes).Any(unit => unit.Type == 1);

    private static bool Reduced(byte[] configuration) =>
        (Units(SequenceHeader(configuration)).Single().Payload[0] & 8) != 0;

    public static void ValidateConfiguration(byte[] configuration, int width, int height)
    {
        var bits = new CodecBits(Units(SequenceHeader(configuration)).Single().Payload);
        if (bits.Read(3) != 0) throw new ProtocolException("AV1 encoder did not honor Main profile.");
        bits.Skip(1);
        var reduced = bits.Read(1) != 0;
        if (reduced) bits.Skip(5);
        else
        {
            var decoderModel = false;
            var delayBits = 0;
            if (bits.Read(1) != 0)
            {
                bits.Skip(64);
                if (bits.Read(1) != 0) bits.Unsigned();
                decoderModel = bits.Read(1) != 0;
                if (decoderModel) { delayBits = (int)bits.Read(5) + 1; bits.Skip(42); }
            }
            var initialDisplayDelay = bits.Read(1) != 0;
            var operatingPoints = bits.Read(5) + 1;
            for (var index = 0; index < operatingPoints; index++)
            {
                bits.Skip(12);
                if (bits.Read(5) > 7) bits.Skip(1);
                if (decoderModel && bits.Read(1) != 0) bits.Skip(delayBits * 2 + 1);
                if (initialDisplayDelay && bits.Read(1) != 0) bits.Skip(4);
            }
        }
        var widthBits = (int)bits.Read(4) + 1;
        var heightBits = (int)bits.Read(4) + 1;
        var actualWidth = bits.Read(widthBits) + 1;
        var actualHeight = bits.Read(heightBits) + 1;
        // Sequence dimensions are upper bounds; a fixed-size frame may legally be smaller.
        if (width <= 0 || height <= 0 || width > actualWidth || height > actualHeight)
            throw new ProtocolException($"AV1 sequence limits {actualWidth}x{actualHeight} cannot contain {width}x{height}.");
    }

    public static bool IsKeyFrame(byte[] data, byte[] configuration)
    {
        var frame = Units(data).FirstOrDefault(unit => unit.Type is 3 or 6)
            ?? throw new ProtocolException("AV1 output has no frame header.");
        if (Reduced(configuration)) return true;
        var bits = new CodecBits(frame.Payload);
        if (bits.Read(1) != 0) return false;
        return bits.Read(2) == 0;
    }

    public static void ValidateAccessUnit(byte[] data, byte[] configuration)
    {
        var frames = Units(data).Where(unit => unit.Type is 3 or 6).ToArray();
        if (frames.Length != 1) throw new ProtocolException("AV1 output must contain exactly one displayed frame.");
        if (Reduced(configuration)) return;
        var bits = new CodecBits(frames[0].Payload);
        if (bits.Read(1) != 0) throw new ProtocolException("AV1 show-existing/reordered output is unsupported.");
        bits.Skip(2);
        if (bits.Read(1) == 0) throw new ProtocolException("AV1 hidden/reordered frames are unsupported.");
    }
}
