using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedrainCast.Protocol;

public sealed record HdrStaticMetadata(int MaxMasteringNits, int MinMasteringTenThousandths = 0,
    int MaxContentLightNits = 0, int MaxFrameAverageLightNits = 0,
    int RedX = 35400, int RedY = 14600, int GreenX = 8500, int GreenY = 39850,
    int BlueX = 6550, int BlueY = 2300, int WhiteX = 15635, int WhiteY = 16450)
{
    public void Validate()
    {
        if (MaxMasteringNits is < 1 or > 10000 || MinMasteringTenThousandths is < 0 or > 65535 ||
            MinMasteringTenThousandths > MaxMasteringNits * 10000 ||
            MaxContentLightNits is < 0 or > 10000 || MaxFrameAverageLightNits is < 0 or > 10000 ||
            MaxContentLightNits > 0 && MaxFrameAverageLightNits > MaxContentLightNits ||
            new[] { RedX, RedY, GreenX, GreenY, BlueX, BlueY, WhiteX, WhiteY }.Any(value => value is < 0 or > 50000) ||
            RedX + RedY is < 1 or > 50000 || GreenX + GreenY is < 1 or > 50000 ||
            BlueX + BlueY is < 1 or > 50000 || WhiteX + WhiteY is < 1 or > 50000)
            throw new FormatException("Invalid HDR10 static metadata.");
    }

    public static bool MatchesEncodedChromaticity(int expected, int numerator, int denominator)
    {
        // Media Foundation's float primaries can lose one 1/50000 unit when hardware writes the SEI.
        return expected is >= 0 and <= 50000 && denominator > 0 &&
            numerator >= 0 && numerator <= denominator &&
            Math.Abs((long)numerator * 50000 - (long)expected * denominator) <= denominator;
    }

    public JsonObject WireValue
    {
        get
        {
            Validate();
            return JsonData.Object(("masteringPrimaries", JsonData.Value(new[]
                { RedX, RedY, GreenX, GreenY, BlueX, BlueY, WhiteX, WhiteY })),
                ("maxMasteringNits", MaxMasteringNits), ("minMasteringTenThousandths", MinMasteringTenThousandths),
                ("maxContentLightNits", MaxContentLightNits), ("maxFrameAverageLightNits", MaxFrameAverageLightNits));
        }
    }
}

public sealed record HdrVideoFormat(bool Hdr10 = false, HdrStaticMetadata? Metadata = null)
{
    public static HdrVideoFormat Sdr { get; } = new();
    public void Validate(string codec)
    {
        if (Hdr10 && codec != "h265" || !Hdr10 && Metadata is not null)
            throw new FormatException("HDR10 requires explicitly negotiated HEVC Main10.");
        Metadata?.Validate();
    }

    public JsonObject WireValue
    {
        get
        {
            if (!Hdr10) throw new InvalidOperationException("SDR uses the existing video configuration.");
            Validate("h265");
            var value = HdrVideoProtocol.Mode();
            value["version"] = HdrVideoProtocol.Version;
            if (Metadata is not null) value["staticMetadata"] = Metadata.WireValue;
            return value;
        }
    }
}

public sealed record HdrReceiverCapabilities(bool Hdr10 = false, bool? NativeHdrAvailable = null,
    bool HdrToSdr = false)
{
    public static HdrReceiverCapabilities None { get; } = new();
    public bool CanReceiveHdr10 => Hdr10 && (NativeHdrAvailable == true || HdrToSdr);
    public JsonObject WireValue => JsonData.Object(("version", HdrVideoProtocol.Version),
        ("videoModes", Hdr10 ? JsonData.Value(new JsonNode[] { HdrVideoProtocol.Mode() }) : new JsonArray()),
        ("nativeHdrAvailable", JsonData.Value(NativeHdrAvailable)), ("hdrToSdr", HdrToSdr));
}

public static class HdrVideoProtocol
{
    public const int Version = 1;
    public const int MaximumBytes = 2048;

    internal static JsonObject Mode() => JsonData.Object(("codec", "h265"), ("profile", "main10"),
        ("dynamicRange", "hdr10"), ("colorPrimaries", "bt2020"), ("transfer", "pq"),
        ("matrix", "bt2020nc"), ("range", "limited"), ("bitDepth", 10));

    private static void Envelope(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) > MaximumBytes)
            throw new FormatException("HDR extension requires an object of at most 2048 UTF-8 bytes.");
        CheckMembers(value);
    }

    private static void CheckMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                if (names.Count >= 32 || member.Name.Length is < 1 or > 64 || !names.Add(member.Name))
                    throw new FormatException("Invalid or duplicate HDR extension member.");
                CheckMembers(member.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > 16) throw new FormatException("HDR extension array exceeds 16 entries.");
            foreach (var item in value.EnumerateArray()) CheckMembers(item);
        }
    }

    private static bool Member(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("HDR envelope requires an object.");
        var count = 0;
        foreach (var member in root.EnumerateObject())
            if (member.NameEquals(name)) { value = member.Value; count++; }
        if (count > 1) throw new FormatException($"Duplicate {name}.");
        if (count == 0) return false;
        Envelope(value);
        return true;
    }

    private static int Integer(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number))
            throw new FormatException($"HDR {name} requires an integer.");
        return number;
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= 32 } text
            ? text : throw new FormatException($"Invalid HDR {name}.");

    private static bool IsHdr10Mode(JsonElement value)
    {
        Envelope(value);
        return Text(value, "codec") == "h265" && Text(value, "profile") == "main10" &&
            Text(value, "dynamicRange") == "hdr10" && Text(value, "colorPrimaries") == "bt2020" &&
            Text(value, "transfer") == "pq" && Text(value, "matrix") == "bt2020nc" &&
            Text(value, "range") == "limited" && Integer(value, "bitDepth") == 10;
    }

    public static HdrReceiverCapabilities Capabilities(JsonElement root)
    {
        if (!Member(root, "receiveCapabilities", out var value)) return HdrReceiverCapabilities.None;
        var revision = Integer(value, "version");
        if (revision <= 0) throw new FormatException("Invalid HDR capability version.");
        if (revision != Version) return HdrReceiverCapabilities.None;
        if (!value.TryGetProperty("videoModes", out var modes) || modes.ValueKind != JsonValueKind.Array)
            throw new FormatException("HDR videoModes requires an array.");
        var hdr = false;
        foreach (var mode in modes.EnumerateArray())
        {
            var supported = IsHdr10Mode(mode);
            if (supported && hdr) throw new FormatException("Duplicate HDR10 playback mode.");
            hdr |= supported;
        }
        bool? native = null;
        if (value.TryGetProperty("nativeHdrAvailable", out var available) && available.ValueKind != JsonValueKind.Null)
        {
            if (available.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException("nativeHdrAvailable requires Boolean or null.");
            native = available.GetBoolean();
        }
        if (!value.TryGetProperty("hdrToSdr", out var conversion) ||
            conversion.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException("hdrToSdr requires Boolean.");
        return new(hdr, native, conversion.GetBoolean());
    }

    public static HdrVideoFormat Format(JsonElement root)
    {
        if (!Member(root, "videoColor", out var value)) return HdrVideoFormat.Sdr;
        if (Integer(value, "version") != Version || !IsHdr10Mode(value))
            throw new FormatException("Unsupported HDR video format.");
        HdrStaticMetadata? metadata = null;
        if (value.TryGetProperty("staticMetadata", out var data))
        {
            Envelope(data);
            if (!data.TryGetProperty("masteringPrimaries", out var primaries) ||
                primaries.ValueKind != JsonValueKind.Array || primaries.GetArrayLength() != 8)
                throw new FormatException("HDR masteringPrimaries requires eight chromaticity coordinates.");
            var coordinates = primaries.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number)
                    ? number : throw new FormatException("Invalid HDR chromaticity coordinate.")).ToArray();
            metadata = new(Integer(data, "maxMasteringNits"), Integer(data, "minMasteringTenThousandths"),
                Integer(data, "maxContentLightNits"), Integer(data, "maxFrameAverageLightNits"),
                coordinates[0], coordinates[1], coordinates[2], coordinates[3],
                coordinates[4], coordinates[5], coordinates[6], coordinates[7]);
            metadata.Validate();
        }
        return new(true, metadata);
    }

    public static void AddFormat(JsonObject root, HdrVideoFormat format)
    {
        format.Validate("h265");
        if (format.Hdr10) root["videoColor"] = format.WireValue;
    }

    public static void RequireAcknowledgement(JsonElement root, HdrVideoFormat requested)
    {
        if (Format(root) != requested) throw new FormatException("Receiver did not acknowledge the exact video color format.");
    }
}
