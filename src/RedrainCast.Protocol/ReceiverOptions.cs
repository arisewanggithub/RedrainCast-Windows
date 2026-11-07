using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedrainCast.Protocol;

public sealed record ReceiverOptions(bool PlaybackStats = false)
{
    public static ReceiverOptions None { get; } = new();
}

public sealed record ReceiverOptionCapabilities(bool PlaybackStats = false)
{
    public static ReceiverOptionCapabilities None { get; } = new();
    public static ReceiverOptionCapabilities Implemented { get; } = new(true);
    public ReceiverOptionCapabilities Intersect(ReceiverOptionCapabilities other) => new(PlaybackStats && other.PlaybackStats);
    public ReceiverOptions Select(bool playbackStats) => new(PlaybackStats && playbackStats);
    public JsonObject WireValue => JsonData.Object(("version", ReceiverOptionProtocol.Version),
        ("supported", JsonData.Value(PlaybackStats ? new[] { "playbackStats" } : [])));
}

public static class ReceiverOptionProtocol
{
    public const int Version = 1;
    public const int MaximumBytes = 2048;
    private static void Envelope(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumBytes)
            throw new FormatException("receiverOptions requires an object of at most 2048 UTF-8 bytes.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
            if (names.Count >= 16 || member.Name.Length is < 1 or > 64 || !names.Add(member.Name))
                throw new FormatException("receiverOptions has too many, oversized or duplicate members.");
    }

    private static int Revision(JsonElement value)
    {
        if (!value.TryGetProperty("version", out var revision) || revision.ValueKind != JsonValueKind.Number ||
            !revision.TryGetInt32(out var number) || number <= 0)
            throw new FormatException("receiverOptions.version requires a positive signed-int32 integer.");
        return number;
    }

    private static bool Member(JsonElement root, out JsonElement value)
    {
        value = default;
        var count = 0;
        foreach (var member in root.EnumerateObject())
            if (member.NameEquals("receiverOptions")) { value = member.Value; count++; }
        if (count > 1) throw new FormatException("Duplicate receiverOptions.");
        if (count == 0) return false;
        Envelope(value);
        return true;
    }

    public static ReceiverOptionCapabilities Capabilities(JsonElement hello)
    {
        if (!Member(hello, out var value)) return ReceiverOptionCapabilities.None;
        var version = Revision(value);
        if (!value.TryGetProperty("supported", out var supported) || supported.ValueKind != JsonValueKind.Array ||
            supported.GetArrayLength() > 16)
            throw new FormatException("receiverOptions.supported requires at most 16 names.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in supported.EnumerateArray())
        {
            if (name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: >= 1 and <= 64 } text ||
                text.Any(char.IsControl) || !names.Add(text))
                throw new FormatException("Invalid or duplicate receiver option name.");
        }
        return new(version == Version && names.Contains("playbackStats"));
    }

    public static ReceiverOptions Parse(JsonElement request, ReceiverOptionCapabilities negotiated)
    {
        if (!Member(request, out var value)) return ReceiverOptions.None;
        var version = Revision(value);
        var enabled = false;
        if (value.TryGetProperty("playbackStats", out var stats))
        {
            if (stats.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException("receiverOptions.playbackStats requires Boolean.");
            enabled = stats.GetBoolean();
        }
        return new(version == Version && negotiated.PlaybackStats && enabled);
    }

    public static JsonObject WireValue(ReceiverOptions options) =>
        JsonData.Object(("version", Version), ("playbackStats", options.PlaybackStats));
}
