using RedrainCast.Protocol;

namespace RedrainCast.Receive.Core;

public enum VideoTransfer { Bt709, Smpte170M, Gamma22, Gamma28, Srgb, Pq, Hlg }
public enum VideoPresentationOutput { Sdr, Hdr, SdrToneMapped }
public sealed record DecodedVideoStatistics(HdrVideoFormat StreamFormat, VideoTransfer? Transfer,
    string? CodecProfile, int? BitDepth);
public sealed record VideoPresentationStatistics(VideoPresentationOutput Output)
{
    private static readonly VideoPresentationStatistics sdr = new(VideoPresentationOutput.Sdr);
    private static readonly VideoPresentationStatistics hdr = new(VideoPresentationOutput.Hdr);
    private static readonly VideoPresentationStatistics toneMapped = new(VideoPresentationOutput.SdrToneMapped);
    public static VideoPresentationStatistics ForStream(HdrVideoFormat format, bool nativeHdr) =>
        !format.Hdr10 ? sdr : nativeHdr ? hdr : toneMapped;
}
public sealed class VideoPresentationStatisticsCache
{
    private VideoPresentationStatistics? snapshot;
    public VideoPresentationStatistics? Snapshot => Volatile.Read(ref snapshot);
    public void Presented(HdrVideoFormat format, bool nativeHdr) =>
        Volatile.Write(ref snapshot, VideoPresentationStatistics.ForStream(format, nativeHdr));
    public void Reset() => Volatile.Write(ref snapshot, null);
}

public sealed record TrackStatistics(long Packets, long PayloadBytes, long? LastPacketAgeMs);
public sealed record EncodedQueueStatistics(int Entries, long Bytes, long PtsSpanUs);
public sealed record ReceiveStatistics(long Session, TrackStatistics Video, TrackStatistics Audio,
    EncodedQueueStatistics VideoQueue, EncodedQueueStatistics AudioQueue, double? ControlRttMs,
    long? ControlAgeMs, ReceiverRecoveryState? Recovery, int QueueByteBudget = ReceiveQueue.MaximumBytes,
    EncodedQueueStatistics? VideoWaitingQueue = null, EncodedQueueStatistics? AudioWaitingQueue = null);
public sealed record AudioStatistics(int BufferedFrames, int TargetFrames, uint DevicePaddingFrames,
    long DevicePlayedFrames, long SilenceFrames, long DiscardedFrames);
public sealed record PlaybackStatistics(long Epoch, uint Generation, long Received, long Decoded, long Presented,
    long Skipped, int EncodedWidth, int EncodedHeight, int RotationDegrees, bool Occluded,
    AudioStatistics? Audio, AvTimingSample? Sync, long PeakAbsoluteSkewUs,
    DecodedVideoStatistics? Video = null, VideoPresentationStatistics? Presentation = null);
public sealed record PlaybackRates(double? ReceivedFps, double? DecodedFps, double? PresentedFps,
    double? VideoMbps, double? AudioMbps, double? SkippedPercent, double? SilenceMs, double? DiscardedMs);

public sealed class PlaybackRateSampler
{
    private (long At, PlaybackStatistics Playback, ReceiveStatistics Receive)? previous;
    public bool BaselineReset { get; private set; }
    public void Reset() { previous = null; BaselineReset = true; }
    public static double? Rate(long current, long prior, long elapsedMs, double scale = 1000) =>
        elapsedMs <= 0 || current < prior || prior < 0 ? null : (current - prior) / (double)elapsedMs * scale;
    public static double? Percent(long part, long total) => part < 0 || total <= 0 || part > total ? null : part * 100.0 / total;
    public static double? SkippedPercent(long presented, long skipped) =>
        presented < 0 || skipped < 0 || presented == 0 && skipped == 0 ? null :
            skipped * 100.0 / ((double)presented + skipped);
    public PlaybackRates Sample(long now, PlaybackStatistics playback, ReceiveStatistics receive)
    {
        var old = previous;
        previous = (now, playback, receive);
        if (old is not { } last || last.Playback.Epoch != playback.Epoch ||
            last.Playback.Generation != playback.Generation || last.Receive.Session != receive.Session ||
            now <= last.At || playback.Received < last.Playback.Received || playback.Decoded < last.Playback.Decoded ||
            playback.Presented < last.Playback.Presented || playback.Skipped < last.Playback.Skipped ||
            receive.Video.PayloadBytes < last.Receive.Video.PayloadBytes ||
            receive.Video.Packets < last.Receive.Video.Packets ||
            receive.Audio.PayloadBytes < last.Receive.Audio.PayloadBytes ||
            playback.Audio is { } currentAudio && last.Playback.Audio is { } priorAudio &&
                (currentAudio.SilenceFrames < priorAudio.SilenceFrames || currentAudio.DiscardedFrames < priorAudio.DiscardedFrames))
        {
            BaselineReset = true;
            return new(null, null, null, null, null, null, null, null);
        }
        BaselineReset = false;
        var ms = now - last.At;
        double? audioDelta(Func<AudioStatistics, long> selector) =>
            playback.Audio is { } audio && last.Playback.Audio is { } prior && selector(audio) >= selector(prior)
                ? (selector(audio) - selector(prior)) / 48.0 : null;
        return new(Rate(receive.Video.Packets, last.Receive.Video.Packets, ms),
            Rate(playback.Decoded, last.Playback.Decoded, ms), Rate(playback.Presented, last.Playback.Presented, ms),
            Rate(receive.Video.PayloadBytes, last.Receive.Video.PayloadBytes, ms, .008),
            Rate(receive.Audio.PayloadBytes, last.Receive.Audio.PayloadBytes, ms, .008),
            SkippedPercent(playback.Presented - last.Playback.Presented, playback.Skipped - last.Playback.Skipped),
            audioDelta(a => a.SilenceFrames), audioDelta(a => a.DiscardedFrames));
    }
}

public sealed class ControlLatency
{
    private readonly object gate = new();
    private (int Nonce, long At)? pending;
    private int sequence;
    private double? rtt;
    private long? lastValid;
    public int? Begin(long now)
    {
        lock (gate)
        {
            if (pending is { } old && now - old.At < 10_000) return null;
            var nonce = unchecked(++sequence);
            pending = (nonce, now);
            return nonce;
        }
    }
    public void Valid(long now, int? pong = null)
    {
        lock (gate)
        {
            lastValid = now;
            if (pending is { } sent && pong == sent.Nonce && now >= sent.At)
            {
                rtt = now - sent.At;
                pending = null;
            }
        }
    }
    public (double? RttMs, long? AgeMs) Snapshot(long now)
    {
        lock (gate) return (rtt, lastValid is { } at ? Math.Max(0, now - at) : null);
    }
}

public static class PlaybackStatisticsText
{
    public static string FormatCompact(CastRequest request, PlaybackStatistics p, ReceiveStatistics r, PlaybackRates rates,
        int canvasWidth, int canvasHeight, double dipWidth, double dipHeight) =>
        FormatDisplay(PlaybackStatisticsDisplay.Create(request, p, r, rates, canvasWidth, canvasHeight, null, compact: true));
    public static string Format(CastRequest request, PlaybackStatistics p, ReceiveStatistics r, PlaybackRates rates,
        int canvasWidth, int canvasHeight, double dipWidth, double dipHeight) =>
        FormatDisplay(PlaybackStatisticsDisplay.Create(request, p, r, rates, canvasWidth, canvasHeight, null));
    private static string FormatDisplay(StatisticsDisplay display) => string.Join("\n",
        new[] { PlaybackStatisticsDisplay.Title }
            .Concat(display.Kpis.Concat(display.Totals).Select(c => $"{c.Field.Label}: {c.Value}"))
            .Concat(display.Groups.SelectMany(g => new[] { g.Title }.Concat(g.Cells.Select(c => $"{c.Field.Label}: {c.Value}"))))
            .Concat(new[] { PlaybackStatisticsDisplay.ConnectionsTitle })
            .Concat(display.Connections.Select(c => $"{c.Channel}: {c.Queued} / {c.Span} / {c.LastActivity} / {c.Rtt}"))
            .Concat(PlaybackStatisticsDisplay.Notes).Append(PlaybackStatisticsDisplay.QueueNote)
            .Concat(display.Footer.Select(c => $"{c.Field.Label}: {c.Value}")));
}
