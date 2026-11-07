using System.Globalization;
using System.Text.Json;

namespace RedrainCast.Protocol;

public static class StreamRecovery
{
    public const int Version = 1;
    public const long MaximumKeyAgeUs = 250_000;
    public const string Request = "stream-recovery-request";
    public const string Key = "stream-recovery-key";
    public const string Complete = "stream-recovery-complete";

    public static bool Negotiated(JsonElement hello, bool acknowledgement = false)
    {
        var enabled = false;
        if (hello.TryGetProperty("streamRecovery", out var capability))
        {
            if (capability.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException("streamRecovery must be Boolean.");
            enabled = capability.GetBoolean();
        }
        int? version = null;
        if (hello.TryGetProperty("streamRecoveryVersion", out var revision))
        {
            if (revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt32(out var value) || value <= 0)
                throw new FormatException("streamRecoveryVersion must be a positive signed-int32 integer.");
            version = value;
        }
        if (acknowledgement && enabled && version != Version)
            throw new FormatException("Receiver acknowledged an incompatible recovery revision.");
        return enabled && version == Version;
    }

    public static long Timestamp(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new FormatException($"{name} requires a canonical nonnegative int64 decimal string.");
        var text = value.GetString()!;
        if (text.Length == 0 || text.Length > 19 || text.Length > 1 && text[0] == '0' ||
            text.Any(c => c is < '0' or > '9') ||
            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            throw new FormatException($"Invalid canonical timestamp {name}.");
        return result;
    }

    public static string Timestamp(long value) => value >= 0 ? value.ToString(CultureInfo.InvariantCulture) :
        throw new ArgumentOutOfRangeException(nameof(value));

    public static uint Generation(JsonElement json)
    {
        if (!json.TryGetProperty("generation", out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var generation) || generation <= 0)
            throw new FormatException("Recovery generation requires a positive signed-int32 integer.");
        return (uint)generation;
    }
}

public sealed class RecoveryDeadline
{
    private long? started;
    public bool Pending => started is not null;
    public void Begin(long now) => started ??= now;
    public void Complete(long now)
    {
        Check(now);
        started = null;
    }
    public void Check(long now)
    {
        if (started is { } first && now - first >= 10_000)
            throw new TimeoutException("Stream recovery exceeded ten seconds from its first unresolved attempt.");
    }
}

public enum RecoveryAudioCoverage { Wait, Covered, Lost }

public static class RecoveryAudio
{
    public const int Rate = 48000;
    public const int MaximumPacketFrames = Rate * 120 / 1000;
    public static int Frames(string codec, ReadOnlySpan<byte> payload, int aacFrames = 1024)
    {
        if (codec == "raw")
        {
            if (payload.Length == 0 || payload.Length % 4 != 0 || payload.Length / 4 > MaximumPacketFrames)
                throw new FormatException("Recovery PCM must be aligned stereo s16le and at most 120 ms.");
            return payload.Length / 4;
        }
        if (codec == "aac") return aacFrames is 960 or 1024 ? aacFrames : throw new FormatException("Unsupported AAC frame duration.");
        if (codec != "opus" || payload.Length == 0) throw new FormatException("Missing/unsupported recovery audio.");
        var code = payload[0] & 3;
        if (code == 1 && (payload.Length - 1) % 2 != 0) throw new FormatException("Malformed Opus CBR packet.");
        if (code == 2 && payload.Length < 2) throw new FormatException("Truncated Opus VBR packet.");
        var config = payload[0] >> 3;
        var samples = config < 12 ? new[] { 480, 960, 1920, 2880 }[config & 3] :
            config < 16 ? new[] { 480, 960 }[config & 1] : new[] { 120, 240, 480, 960 }[config & 3];
        var count = (payload[0] & 3) switch
        {
            0 => 1, 1 or 2 => 2,
            _ => payload.Length >= 2 ? payload[1] & 63 : 0
        };
        if (count is <= 0 or > 48 || samples * count > MaximumPacketFrames) throw new FormatException("Invalid Opus duration.");
        return samples * count;
    }

    public static long End(long pts, int frames) => checked(pts + frames * 1_000_000L / Rate);
    public static int Skip(long pts, long cutoff, int frames) =>
        cutoff <= pts ? 0 : (int)Int128.Min(frames, ((Int128)(cutoff - pts) * Rate + 999_999) / 1_000_000);
    public static long PrerollUs(string codec, int aacFrames = 1024) =>
        codec switch { "raw" => 0, "opus" => 80_000, "aac" => aacFrames is 960 or 1024
                ? (aacFrames * 1_000_000L + Rate - 1) / Rate : throw new FormatException("Unsupported AAC frame duration."),
            _ => throw new FormatException("Unsupported recovery audio codec.") };

    public static bool Covers(IEnumerable<(long Pts, int Frames)> packets, long start, long key) =>
        Coverage(packets, start, key) == RecoveryAudioCoverage.Covered;

    public static RecoveryAudioCoverage Coverage(IEnumerable<(long Pts, int Frames)> packets, long start, long key,
        bool allowsPtsReversal = false)
    {
        long? end = null;
        foreach (var packet in packets)
        {
            if (end is null)
            {
                if (packet.Pts > start) return allowsPtsReversal ? RecoveryAudioCoverage.Wait : RecoveryAudioCoverage.Lost;
                end = packet.Pts;
            }
            if (packet.Pts > end.Value + 21) return allowsPtsReversal ? RecoveryAudioCoverage.Wait : RecoveryAudioCoverage.Lost;
            end = Math.Max(end.Value, End(packet.Pts, packet.Frames));
        }
        return end > key ? RecoveryAudioCoverage.Covered : RecoveryAudioCoverage.Wait;
    }
}
