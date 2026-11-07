using System.Buffers.Binary;

namespace ScrcpySend.Core;

public static class CaptureWorkerHandshake
{
    public const string PreparationArgument = "--prepare-source-v2";
    public static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(30);

    public static string[] MetadataArguments(string[] arguments)
    {
        if (arguments.Length != 8 || arguments[0] != PreparationArgument)
            throw new ProtocolException("Capture worker requires --prepare-source-v2 before its seven metadata arguments.");
        return arguments[1..];
    }

    public static async Task WriteReadyAsync(Stream stream, int width, int height, string codec, CancellationToken token)
    {
        ValidateDimensions(width, height);
        var codecId = CodecId(codec);
        var header = new byte[16];
        "RCW2"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), height);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), codecId);
        await stream.WriteAsync(header, token);
        await stream.FlushAsync(token);
    }

    public static async Task ReadReadyAsync(Stream stream, int width, int height, string codec, CancellationToken token)
    {
        ValidateDimensions(width, height);
        var codecId = CodecId(codec);
        var magic = new byte[4];
        await stream.ReadExactlyAsync(magic, token);
        if (!magic.AsSpan().SequenceEqual("RCW2"u8))
            throw new ProtocolException("Capture worker preparation returned an invalid magic or revision.");
        var metadata = new byte[12];
        await stream.ReadExactlyAsync(metadata, token);
        if (BinaryPrimitives.ReadInt32BigEndian(metadata) != width ||
            BinaryPrimitives.ReadInt32BigEndian(metadata.AsSpan(4)) != height ||
            BinaryPrimitives.ReadUInt32BigEndian(metadata.AsSpan(8)) != codecId)
            throw new ProtocolException("Capture worker preparation changed its fixed codec or dimensions.");
    }

    private static uint CodecId(string codec) => codec switch
    {
        "h264" => 1,
        "h265" => 2,
        "av1" => 3,
        _ => throw new ProtocolException("Capture worker preparation requires a supported codec.")
    };

    private static void ValidateDimensions(int width, int height)
    {
        if (width is < 2 or > 3840 || height is < 2 or > 3840 || (width & 1) != 0 || (height & 1) != 0)
            throw new ProtocolException("Capture worker preparation requires valid even dimensions.");
    }
}
