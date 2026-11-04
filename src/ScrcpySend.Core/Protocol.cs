using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace ScrcpySend.Core;

public enum MessageType : ushort
{
    Hello = 1, SessionRequest = 3, SessionAccept = 4, Reject = 5,
    StreamConfig = 6, StreamReady = 7, VideoFrame = 8, AudioPacket = 9,
    Status = 10, Error = 11, Ping = 12, Pong = 13, Stop = 14, SessionEnd = 15
}

public sealed class ProtocolException(string message) : IOException(message);

public sealed record Message(MessageType Type, long Session, uint Track, uint Generation,
    long PtsUs, uint Flags, byte[] Payload)
{
    public static Message Control(MessageType type, long session, object payload) =>
        new(type, session, 0, 0, -1, 0, JsonSerializer.SerializeToUtf8Bytes(payload));

    public JsonElement Json() => StrictJson.Parse(Payload);
}

public static class StrictJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static JsonElement Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var text = Utf8.GetString(bytes);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProtocolException("Expected a JSON object.");
            Validate(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw new ProtocolException($"Invalid protocol JSON: {error.Message}");
        }
    }

    private static void Validate(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw new ProtocolException("Duplicate JSON property.");
                    Validate(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) Validate(item);
                break;
            case JsonValueKind.Number:
                if (!value.TryGetInt32(out _)) throw new ProtocolException("Expected a signed 32-bit integer.");
                break;
            case JsonValueKind.String:
                _ = value.GetString();
                break;
        }
    }

    public static string String(JsonElement json, string name, int maxLength)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ProtocolException($"Missing or invalid {name}.");
        var text = value.GetString()!;
        if (text.Length > maxLength) throw new ProtocolException($"{name} is too long.");
        return text;
    }

    public static int Integer(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number))
            throw new ProtocolException($"Missing or invalid {name}.");
        return number;
    }

    public static bool Boolean(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ProtocolException($"Missing or invalid {name}.");
        return value.GetBoolean();
    }
}

public static class Wire
{
    public const int HeaderSize = 40;
    public const int Version = 3;

    public static async ValueTask<Message> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header.AsMemory(0, 4), cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual("SCPY"u8)) throw new ProtocolException("Invalid SCPY magic.");
        await stream.ReadExactlyAsync(header.AsMemory(4, 2), cancellationToken);
        if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) != Version)
            throw new ProtocolException("Unsupported protocol version.");
        await stream.ReadExactlyAsync(header.AsMemory(6), cancellationToken);
        var type = (MessageType)BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6));
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        var flags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        var session = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16));
        var track = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24));
        var generation = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
        var pts = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(32));
        Validate(type, length, flags, session, track, generation, pts);
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        var message = new Message(type, session, track, generation, pts, flags, payload);
        if (type is not (MessageType.VideoFrame or MessageType.AudioPacket)) _ = message.Json();
        return message;
    }

    public static async ValueTask WriteAsync(Stream stream, Message message, CancellationToken cancellationToken)
    {
        Validate(message.Type, (uint)message.Payload.Length, message.Flags, message.Session,
            message.Track, message.Generation, message.PtsUs);
        if (message.Type is not (MessageType.VideoFrame or MessageType.AudioPacket)) _ = message.Json();
        var header = new byte[HeaderSize];
        "SCPY"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)message.Type);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)message.Payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), message.Flags);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16), message.Session);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), message.Track);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), message.Generation);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(32), message.PtsUs);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(message.Payload, cancellationToken);
    }

    private static void Validate(MessageType type, uint length, uint flags, long session,
        uint track, uint generation, long pts)
    {
        if (!Enum.IsDefined(type)) throw new ProtocolException("Unknown or reserved message type.");
        var media = type is MessageType.VideoFrame or MessageType.AudioPacket;
        var tracked = media || type is MessageType.StreamConfig or MessageType.StreamReady;
        var limit = type switch
        {
            MessageType.VideoFrame => 4 * 1024 * 1024,
            MessageType.AudioPacket => 256 * 1024,
            _ => 65536
        };
        if (length > limit || (media && length == 0)) throw new ProtocolException("Invalid payload length.");
        if (session < 0 || (tracked && session == 0)) throw new ProtocolException("Invalid session.");
        if (tracked ? track is not (1 or 2) || generation is 0 or > int.MaxValue : track != 0 || generation != 0)
            throw new ProtocolException("Invalid track or generation.");
        if (type == MessageType.VideoFrame && track != 1 || type == MessageType.AudioPacket && track != 2)
            throw new ProtocolException("Media is on the wrong track.");
        if (media ? pts < 0 : pts != -1) throw new ProtocolException("Invalid timestamp.");
        if (flags != 0 && !(type == MessageType.VideoFrame && flags == 1))
            throw new ProtocolException("Invalid flags.");
    }
}
