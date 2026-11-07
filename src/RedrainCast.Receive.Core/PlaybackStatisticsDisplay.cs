using System.Globalization;

namespace RedrainCast.Receive.Core;

public sealed record StatisticsField(string Id, string Label, string? Unit = null);
public sealed record StatisticsCell(StatisticsField Field, string Value, double? Number = null, bool Highlight = false,
    long? Counter = null);
public sealed record StatisticsGroup(string Id, string Title, IReadOnlyList<StatisticsCell> Cells);
public sealed record StatisticsConnection(string Channel, string Queued, string Span, string LastActivity, string Rtt);
public sealed record StatisticsDisplay(IReadOnlyList<StatisticsCell> Kpis, IReadOnlyList<StatisticsCell> Totals,
    IReadOnlyList<StatisticsGroup> Groups, IReadOnlyList<StatisticsConnection> Connections,
    IReadOnlyList<StatisticsCell> Footer);

/// <summary>One-Hz UI observation, deliberately independent of the presenter's diagnostic maximum.</summary>
public sealed class StatisticsObservedPeak
{
    private (long Session, uint Generation, long Epoch)? identity;
    private double? peak;
    public void Reset() { identity = null; peak = null; }
    public double? Sample(PlaybackStatistics playback, ReceiveStatistics receive, bool baselineReset)
    {
        var current = (receive.Session, playback.Generation, playback.Epoch);
        if (baselineReset || identity != current) { peak = null; identity = current; }
        if (playback.Sync is { } sync && sync.Generation == playback.Generation)
            peak = Math.Max(peak ?? 0, Math.Abs(sync.SignedSkewUs / 1000.0));
        return peak;
    }
}

public static class PlaybackStatisticsDisplay
{
    public const string Title = "Playback statistics";
    public const string ConnectionsTitle = "CONNECTIONS & QUEUES";
    public static readonly string[] ConnectionColumns = ["Channel", "Queued", "Span", "Last activity", "RTT"];
    public static readonly string[] Notes = ["Positive skew = video ahead",
        "Source timestamps · not end-to-end latency", "Peak observed skew uses 1-Hz samples."];
    public const string QueueNote = "Encoded queues: waiting packets";
    public static readonly StatisticsField[] KpiFields =
    [
        new("receivedFps", "Received FPS", "FPS"), new("decodedFps", "Decoded FPS", "FPS"),
        new("presentedFps", "Presented FPS", "FPS"), new("recentSkippedPercent", "Skipped · recent", "%")
    ];
    public static readonly StatisticsField[] VideoFields =
    [
        new("videoCodec", "Codec"), new("encodedSize", "Encoded size", "px"),
        new("captureLimit", "Capture limit", "FPS"), new("canvasSize", "Receiver canvas", "px"),
        new("rotation", "Rotation"), new("videoBitrate", "Video bitrate", "Mbps"),
        new("configuredVideoBitrate", "Configured bitrate", "Mbps"), new("networkLoss", "Network loss")
    ];
    public static readonly StatisticsField[] AudioFields =
    [
        new("audioCodec", "Audio format"), new("audioBitrate", "Audio bitrate", "Mbps"),
        new("pcmBufferTarget", "PCM buffer / target", "ms"), new("devicePadding", "Device padding", "ms"),
        new("insertedSilence", "Inserted silence", "ms"), new("discardedPcm", "Discarded PCM", "ms"),
        new("avSkew", "A/V skew", "ms"), new("peakObservedSkew", "Peak observed skew", "ms")
    ];
    private static readonly StatisticsField[] TotalFields =
        [new("decodedTotal", "Decoded"), new("presentedTotal", "Presented"), new("skippedTotal", "Skipped")];
    private static readonly StatisticsField[] FooterFields =
        [new("state", "State"), new("generation", "Generation"), new("playbackEpoch", "Playback epoch"), new("occluded", "Occluded")];

    public static string Number(double? value, bool compact = false, bool signed = false, CultureInfo? culture = null)
    {
        if (value is not { } v || !double.IsFinite(v)) return "N/A";
        var format = Math.Abs(v) >= (compact ? 1000 : 1_000_000_000) ? "0.#E0" : compact ? "0.#" : "0.0";
        return (signed && v > 0 ? "+" : "") + v.ToString(format, culture ?? CultureInfo.InvariantCulture);
    }

    public static StatisticsDisplay Create(CastRequest request, PlaybackStatistics p, ReceiveStatistics r,
        PlaybackRates rates, int canvasWidth, int canvasHeight, double? observedPeakMs, bool compact = false,
        Func<string, string>? localize = null, CultureInfo? culture = null)
    {
        var formatCulture = culture ?? CultureInfo.InvariantCulture;
        string T(string key, string english) => localize?.Invoke(key) ?? english;
        var unavailable = T("NotAvailable", "N/A");
        string N(double? value) => value is null || !double.IsFinite(value.Value) ? unavailable : Number(value, compact, culture: formatCulture);
        string Unit(string unit) => unit switch
        {
            "ms" => T("UnitMilliseconds", unit), "Mbps" => T("UnitMegabits", unit),
            "px" => T("UnitPixels", unit), "FPS" => T("UnitFrames", unit), "B" => T("UnitBytes", unit), _ => unit
        };
        string WithUnit(double? value, string unit) => value is null ? unavailable : $"{N(value)} {Unit(unit)}";
        string Count(long value) => value >= (compact ? 1_000_000 : 1_000_000_000_000) ? N(value) : value.ToString(formatCulture);
        string Size(int width, int height) => width <= 0 || height <= 0 ? unavailable :
            $"{width.ToString(formatCulture)} × {height.ToString(formatCulture)} {Unit("px")}";
        StatisticsCell[] Cells(StatisticsField[] fields, string[] values) =>
            fields.Select((f, i) => new StatisticsCell(f, values[i])).ToArray();
        string AudioTotal(long? frames, double? delta) => frames is null ? unavailable :
            $"{N(frames / 48.0)} (+{N(delta)}) {Unit("ms")}";
        StatisticsConnection Media(string channel, EncodedQueueStatistics? queue, long? age) =>
            new(channel, queue is null ? unavailable : $"{queue.Entries.ToString(formatCulture)} · {queue.Bytes.ToString(formatCulture)} {Unit("B")}",
                WithUnit(queue?.PtsSpanUs / 1000.0, "ms"), WithUnit(age, "ms"), "—");
        var kpiNumbers = new[] { rates.ReceivedFps, rates.DecodedFps, rates.PresentedFps, rates.SkippedPercent };
        var kpis = KpiFields.Select((f, i) => new StatisticsCell(f,
            N(kpiNumbers[i]) + (i == 3 && kpiNumbers[i] is not null ? formatCulture.NumberFormat.PercentSymbol : ""), kpiNumbers[i],
            i == 3 && kpiNumbers[i] > 0)).ToArray();
        var codec = request.VideoCodec switch { "h264" => "H.264", "h265" => "H.265 (HEVC)", "av1" => "AV1", _ => unavailable };
        var audioCodec = request.AudioCodec?.ToLowerInvariant() switch
        { null => T("Off", "Off"), "raw" or "pcm" => "PCM", "opus" => "Opus", "aac" => "AAC", _ => unavailable };
        var orientation = request.ReceiverOrientation switch
        {
            "auto" => T("Auto", "Auto"), "landscape" => T("Landscape", "Landscape"), "portrait" => T("Portrait", "Portrait"),
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(request.ReceiverOrientation)
        };
        var audio = p.Audio;
        var skew = p.Sync is { } sync && sync.Generation == p.Generation ? sync.SignedSkewUs / 1000.0 : (double?)null;
        var totals = new[] { p.Decoded, p.Presented, p.Skipped };
        return new(kpis, TotalFields.Select((field, i) => new StatisticsCell(field, Count(totals[i]), Counter: totals[i])).ToArray(),
            [
                new("video", "VIDEO STREAM", Cells(VideoFields,
                    [codec, Size(p.EncodedWidth, p.EncodedHeight), $"≤{request.FramesPerSecond.ToString(formatCulture)} {Unit("FPS")}",
                     Size(canvasWidth, canvasHeight),
                     $"{p.RotationDegrees.ToString(formatCulture)}° · {orientation}",
                     WithUnit(rates.VideoMbps, "Mbps"), WithUnit(request.ConfiguredVideoBitrate / 1_000_000.0, "Mbps"), unavailable])),
                new("audioSync", "AUDIO & SYNC", Cells(AudioFields,
                    [audioCodec, request.AudioCodec is null ? unavailable : WithUnit(rates.AudioMbps, "Mbps"),
                     audio is null ? unavailable : $"{N(audio.BufferedFrames / 48.0)} / {N(audio.TargetFrames / 48.0)} {Unit("ms")}",
                     WithUnit(audio?.DevicePaddingFrames / 48.0, "ms"),
                     AudioTotal(audio?.SilenceFrames, rates.SilenceMs), AudioTotal(audio?.DiscardedFrames, rates.DiscardedMs),
                     skew is null ? unavailable : $"{Number(skew, compact, signed: true, culture: formatCulture)} {Unit("ms")}",
                     WithUnit(observedPeakMs, "ms")]))
            ],
            [Media(T("ChannelVideo", "Video"), r.VideoWaitingQueue, r.Video.LastPacketAgeMs),
             Media(T("ChannelAudio", "Audio"), r.AudioWaitingQueue, r.Audio.LastPacketAgeMs),
             new(T("ChannelControl", "Control"), "—", "—", WithUnit(r.ControlAgeMs, "ms"), WithUnit(r.ControlRttMs, "ms"))],
            Cells(FooterFields, [r.Recovery?.Recovering == true ? T("Recovering", "Recovering") : T("Playing", "Playing"),
                p.Generation.ToString(formatCulture), p.Epoch.ToString(formatCulture),
                p.Occluded ? T("Yes", "Yes") : T("No", "No")]));
    }
}
