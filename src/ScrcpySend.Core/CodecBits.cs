namespace ScrcpySend.Core;

internal sealed class CodecBits
{
    private readonly byte[] data;
    private int position;

    public CodecBits(ReadOnlySpan<byte> bytes, bool removeEmulationPrevention = false)
    {
        var result = new List<byte>(bytes.Length);
        var zeros = 0;
        foreach (var value in bytes)
        {
            if (removeEmulationPrevention && zeros >= 2 && value == 3) { zeros = 0; continue; }
            result.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        data = result.ToArray();
    }

    public uint Read(int count)
    {
        if (count < 0 || count > 32 || position + count > data.Length * 8)
            throw new ProtocolException("Truncated codec bitstream.");
        uint result = 0;
        while (count-- > 0) { result = result << 1 | (uint)(data[position / 8] >> (7 - position % 8) & 1); position++; }
        return result;
    }

    public void Skip(int count)
    {
        while (count > 0) { var part = Math.Min(32, count); Read(part); count -= part; }
    }

    public uint Unsigned()
    {
        var zeros = 0;
        while (Read(1) == 0)
            if (++zeros > 30) throw new ProtocolException("Oversized codec Exp-Golomb value.");
        return ((1u << zeros) - 1) + Read(zeros);
    }
}
