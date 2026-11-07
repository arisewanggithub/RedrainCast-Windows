using System.Buffers.Binary;

namespace RedrainCast.Send.Core;

public sealed class FfmpegPacketReader(Stream stream, int width, int height)
{
    private bool initialized;
    private byte[] configuration = [];
    private long previousPts = -1;

    public async Task InitializeAsync(CancellationToken token)
    {
        if (initialized) throw new InvalidOperationException("FFmpeg packet stream was already initialized.");
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, token);
        if (!header.AsSpan(0, 4).SequenceEqual("RCF1"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) != width ||
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)) != height)
            throw new ProtocolException("FFmpeg packet header has the wrong format or dimensions.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        if (length > 65536) throw new ProtocolException("FFmpeg codec configuration exceeds 64 KiB.");
        configuration = new byte[(int)length];
        await stream.ReadExactlyAsync(configuration, token);
        initialized = true;
    }

    public async Task<EncodedVideo> ReadAsync(CancellationToken token)
    {
        if (!initialized) throw new InvalidOperationException("Initialize the FFmpeg packet stream first.");
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        var pts = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(4));
        var flags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        if (length is 0 or > 4 * 1024 * 1024 || pts < 0 || pts < previousPts || (flags & ~1u) != 0)
            throw new ProtocolException("FFmpeg packet has an invalid size, timestamp or flags.");
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, token);
        previousPts = pts;
        return new(payload, pts, configuration);
    }
}
