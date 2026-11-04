namespace ScrcpySend.Core;

public static class H264
{
    public static void ValidateConfiguration(byte[] configuration, int width, int height)
    {
        var units = AnnexB.Units(configuration);
        foreach (var sps in units.Where(unit => (unit[0] & 31) == 7))
        {
            var bits = new CodecBits(sps.AsSpan(1), true);
            if (bits.Read(8) != 66) throw new ProtocolException("Hardware encoder did not honor the requested H.264 Baseline profile.");
            bits.Read(8);
            bits.Read(8);
            bits.Unsigned();
            bits.Unsigned();
            var order = bits.Unsigned();
            if (order == 0) bits.Unsigned();
            else if (order == 1)
            {
                bits.Read(1);
                bits.Unsigned();
                bits.Unsigned();
                var cycle = bits.Unsigned();
                if (cycle > 255) throw new ProtocolException("Invalid H.264 POC cycle.");
                for (var i = 0; i < cycle; i++) bits.Unsigned();
            }
            else if (order != 2) throw new ProtocolException("Invalid H.264 picture order count.");
            bits.Unsigned();
            bits.Read(1);
            var codedWidth = ((long)bits.Unsigned() + 1) * 16;
            var codedHeight = ((long)bits.Unsigned() + 1) * 16;
            if (bits.Read(1) != 1) throw new ProtocolException("Interlaced hardware output is unsupported.");
            bits.Read(1);
            if (bits.Read(1) != 0)
            {
                var left = bits.Unsigned();
                var right = bits.Unsigned();
                var top = bits.Unsigned();
                var bottom = bits.Unsigned();
                codedWidth -= 2L * (left + (long)right);
                codedHeight -= 2L * (top + (long)bottom);
            }
            if (codedWidth != width || codedHeight != height)
                throw new ProtocolException($"Actual SPS size {codedWidth}x{codedHeight} differs from requested {width}x{height}.");
        }
        _ = AnnexB.ParameterSets(configuration);
    }

    public static void ValidateAccessUnit(byte[] accessUnit)
    {
        foreach (var unit in AnnexB.Units(accessUnit))
        {
            var type = unit[0] & 31;
            if (type is not (1 or 5)) continue;
            var bits = new CodecBits(unit.AsSpan(1), true);
            bits.Unsigned();
            var sliceType = bits.Unsigned();
            if (sliceType > 9 || sliceType % 5 is not (0 or 2))
                throw new ProtocolException("Hardware encoder produced a forbidden B/SP/SI slice.");
        }
    }

}
