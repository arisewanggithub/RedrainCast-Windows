using System.Buffers.Binary;

namespace RedrainCast.Send.Core;

public sealed class PacketPipeWriter(Stream stream, int width, int height)
{
    private byte[]? configuration;
    private long previousPts = -1;

    public async Task WriteAsync(EncodedVideo frame, CancellationToken token)
    {
        if (width is < 2 or > 3840 || height is < 2 or > 3840 || (width & 1) != 0 || (height & 1) != 0 ||
            frame.Data.Length is 0 or > 4 * 1024 * 1024 || frame.Configuration.Length > 65536 ||
            frame.PtsUs < 0 || frame.PtsUs < previousPts)
            throw new ProtocolException("Capture packet has invalid dimensions, size, configuration or timestamp.");
        if (configuration is null)
        {
            if (frame.Configuration.Length == 0) throw new ProtocolException("Capture supplied no initial codec configuration.");
            configuration = frame.Configuration.ToArray();
            var header = new byte[16];
            "RCF1"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)height);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), (uint)configuration.Length);
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(configuration, token);
        }
        else if (!configuration.AsSpan().SequenceEqual(frame.Configuration))
            throw new ProtocolException("Capture changed its fixed-format codec configuration.");
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)frame.Data.Length);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(4), frame.PtsUs);
        await stream.WriteAsync(packet, token);
        await stream.WriteAsync(frame.Data, token);
        await stream.FlushAsync(token);
        previousPts = frame.PtsUs;
    }
}
