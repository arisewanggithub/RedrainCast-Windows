namespace RedrainCast.Send.Core;

public static class AnnexB
{
    public static IReadOnlyList<byte[]> Units(byte[] bytes)
    {
        var starts = new List<(int Offset, int Prefix)>();
        for (var i = 0; i <= bytes.Length - 3; i++)
        {
            if (bytes[i] != 0 || bytes[i + 1] != 0) continue;
            var prefix = bytes[i + 2] == 1 ? 3 :
                i + 3 < bytes.Length && bytes[i + 2] == 0 && bytes[i + 3] == 1 ? 4 : 0;
            if (prefix == 0) continue;
            starts.Add((i, prefix));
            i += prefix - 1;
        }
        if (starts.Count == 0 || starts[0].Offset != 0) throw new ProtocolException("Encoder output is not Annex B.");
        var units = new List<byte[]>();
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i].Offset + starts[i].Prefix;
            var end = i + 1 < starts.Count ? starts[i + 1].Offset : bytes.Length;
            if (end <= start) throw new ProtocolException("Empty H.264 NAL unit.");
            units.Add(bytes[start..end]);
        }
        return units;
    }

    public static byte[] ParameterSets(byte[] accessUnit)
    {
        var units = Units(accessUnit);
        var sps = units.Where(unit => (unit[0] & 31) == 7).ToArray();
        var pps = units.Where(unit => (unit[0] & 31) == 8).ToArray();
        if (sps.Length == 0 || pps.Length == 0) throw new ProtocolException("Hardware encoder did not provide real SPS and PPS.");
        return Join(sps.Concat(pps));
    }

    public static bool IsIdr(byte[] accessUnit) => Units(accessUnit).Any(unit => (unit[0] & 31) == 5);

    public static byte[] Join(IEnumerable<byte[]> units)
    {
        using var stream = new MemoryStream();
        foreach (var unit in units) { stream.Write([0, 0, 0, 1]); stream.Write(unit); }
        return stream.ToArray();
    }
}
