using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RedrainCast.Receive.Core;

namespace RedrainCast.Receive.Media;

internal sealed record VideoTraceEvent(long Sequence, string Stage, long Session, uint Generation,
    long PtsUs, long Qpc, string? Reason);

internal sealed record VideoTraceSnapshot(long Session, long QpcFrequency, long StartQpc, long EndQpc,
    string EndReason, bool Complete, bool Overflow, long Dropped,
    Dictionary<string, long> Counts, VideoTraceEvent[] Events,
    int DurationSeconds = 30, int Capacity = 16384);

internal sealed record VideoTraceSettings(bool Enabled, int DurationSeconds, bool Detailed = false,
    bool Readiness = false, bool Reads = false)
{
    public int Capacity => Reads ? Math.Max(65536, DurationSeconds * 12288) : Readiness
        ? Math.Max(65536, DurationSeconds * 8192)
        : Detailed
        ? Math.Max(65536, DurationSeconds * 4096)
        : DurationSeconds <= 30 ? 16384 : DurationSeconds * 1024;

    public static VideoTraceSettings Parse(string? enabled, string? seconds, string? stages = null,
        string? readiness = null, string? reads = null)
    {
        if (enabled != "1") return new(false, 30);
        if (seconds is null) return new(true, 30, stages == "1", stages == "1" && readiness == "1",
            stages == "1" && reads == "1");
        if (!int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var duration)
            || duration is < 1 or > 600)
            throw new ArgumentException("REDRAIN_VIDEO_TRACE_SECONDS must be an integer from 1 through 600.");
        return new(true, duration, stages == "1", stages == "1" && readiness == "1",
            stages == "1" && reads == "1");
    }
}

internal sealed class VideoTraceBuffer(bool enabled, int capacity = 16384, long? durationTicks = null,
    int durationSeconds = 30)
{
    private readonly object gate = new();
    private readonly List<VideoTraceEvent> events = new();
    private readonly Dictionary<string, long> counts = new();
    private readonly long duration = durationTicks ?? durationSeconds * Stopwatch.Frequency;
    private long session, start, dropped, sequence;
    private bool started, stopped;

    public bool IsActive(long qpc)
    {
        lock (gate) return enabled && started && !stopped && qpc - start < duration;
    }

    public bool Add(string stage, long session, uint generation, long ptsUs, long qpc, string? reason = null)
    {
        if (!enabled) return false;
        lock (gate)
        {
            if (stopped) return false;
            if (!started)
            {
                if (stage != "receive") return false;
                started = true;
                start = qpc;
                this.session = session;
            }
            if (qpc - start >= duration) return false;
            counts.TryGetValue(stage, out var count);
            counts[stage] = count + 1;
            var ordinal = ++sequence;
            if (events.Count < capacity)
                events.Add(new(ordinal, stage, session, generation, ptsUs, qpc, reason));
            else dropped++;
            return ordinal == 1;
        }
    }

    public VideoTraceSnapshot? Stop(string reason, long qpc)
    {
        lock (gate)
        {
            if (stopped || !started) { stopped = true; return null; }
            stopped = true;
            var end = Math.Min(qpc, start + duration);
            var complete = qpc - start >= duration && dropped == 0;
            return new(session, Stopwatch.Frequency, start, end, reason, complete, dropped > 0, dropped,
                new(counts), events.ToArray(), durationSeconds, capacity);
        }
    }
}

internal static class VideoTraceJson
{
    public static string StatisticsSample(PlaybackStatistics sample, long sampledAtMs, bool baselineReset) =>
        JsonSerializer.Serialize(new
        {
            sampledAtMs, epoch = sample.Epoch, baselineReset,
            received = sample.Received, decoded = sample.Decoded,
            presented = sample.Presented, skipped = sample.Skipped
        });

    public static string Header(VideoTraceSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        kind = "header", version = 1, session = snapshot.Session, frequency = 1_000_000_000L,
        qpcFrequency = snapshot.QpcFrequency, startQpc = snapshot.StartQpc,
        durationSeconds = snapshot.DurationSeconds, capacity = snapshot.Capacity
    });

    public static string Event(VideoTraceSnapshot snapshot, VideoTraceEvent item) => JsonSerializer.Serialize(new
    {
        kind = "event", sequence = item.Sequence,
        stage = item.Stage switch
        {
            "receive" => "RECEIVED", "decode" => "DECODED",
            "skip" => "SKIPPED", "present" => "PRESENTED",
            "queue_take" => "QUEUE_TAKE", "decode_wait" => "DECODE_GATE_WAIT",
            "decode_enter" => "DECODE_GATE_ACQUIRED", "decode_end" => "DECODE_END",
            "pending_wait" => "PENDING_GATE_WAIT", "pending" => "PENDING",
            "render_wake" => "RENDER_WAKE", "render_gate_miss" => "RENDER_GATE_MISS",
            "render_begin" => "RENDER_BEGIN", "render_end" => "RENDER_END",
            "render_wait" => "RENDER_WAIT", "readiness_signal" => "READINESS_SIGNAL",
            "readiness_check" => "READINESS_CHECK",
            "render_gate_wait" => "RENDER_GATE_WAIT", "render_gate_enter" => "RENDER_GATE_ACQUIRED",
            "mailbox_take" => "MAILBOX_TAKE", "mailbox_return" => "MAILBOX_RETURN",
            "context_wait" => "GPU_CONTEXT_WAIT", "context_enter" => "GPU_CONTEXT_ACQUIRED",
            "context_leave" => "GPU_CONTEXT_RELEASED",
            "statistics_sample" => "STATISTICS_SAMPLE",
            "read_begin" => "VIDEO_READ_BEGIN", "header_begin" => "VIDEO_HEADER_BEGIN",
            "header_end" => "VIDEO_HEADER_END", "payload_begin" => "VIDEO_PAYLOAD_BEGIN",
            "payload_end" => "VIDEO_PAYLOAD_END", "read_end" => "VIDEO_READ_END",
            "admission_begin" => "VIDEO_ADMISSION_BEGIN", "admission_end" => "VIDEO_ADMISSION_END",
            _ => throw new InvalidOperationException("Unknown video trace stage.")
        },
        session = item.Session, generation = item.Generation, ptsUs = item.PtsUs,
        atNs = (long)((Int128)(item.Qpc - snapshot.StartQpc) * 1_000_000_000 / snapshot.QpcFrequency),
        qpc = item.Qpc, reason = item.Reason
    });

    public static string Summary(VideoTraceSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        kind = "summary", endQpc = snapshot.EndQpc, endReason = snapshot.EndReason,
        windowComplete = snapshot.Complete, overflow = snapshot.Overflow, dropped = snapshot.Dropped,
        events = snapshot.Events.Length, counts = snapshot.Counts
    });
}

internal sealed class VideoFrameTrace : IAsyncDisposable
{
    private readonly Func<string, ValueTask> status;
    private readonly VideoTraceSettings settings;
    private readonly VideoTraceBuffer buffer;
    private readonly object gate = new();
    private Timer? timer;
    private Task? dump;
    private bool stopped;

    public VideoFrameTrace(Func<string, ValueTask> status)
    {
        this.status = status;
        settings = VideoTraceSettings.Parse(Environment.GetEnvironmentVariable("REDRAIN_VIDEO_TRACE"),
            Environment.GetEnvironmentVariable("REDRAIN_VIDEO_TRACE_SECONDS"),
            Environment.GetEnvironmentVariable("REDRAIN_VIDEO_TRACE_STAGES"),
            Environment.GetEnvironmentVariable("REDRAIN_VIDEO_TRACE_READINESS"),
            Environment.GetEnvironmentVariable("REDRAIN_VIDEO_TRACE_READS"));
        buffer = new(settings.Enabled, settings.Capacity, durationSeconds: settings.DurationSeconds);
    }

    public bool ReadinessActive => settings.Readiness && buffer.IsActive(Stopwatch.GetTimestamp());
    public bool ReadsActive => settings.Reads && buffer.IsActive(Stopwatch.GetTimestamp());

    public void ReadObservation(long session, VideoReadObservation observation)
    {
        if (!ReadsActive) return;
        buffer.Add(observation.Stage, session, observation.Generation, observation.PtsUs, observation.Qpc,
            JsonSerializer.Serialize(new { readId = observation.ReadId, bytes = observation.Bytes,
                asyncReads = observation.AsyncReads, syncReads = observation.SyncReads, outcome = observation.Outcome }));
    }

    public void StatisticsSample(long session, PlaybackStatistics sample,
        long sampledAtMs, bool baselineReset)
    {
        if (!ReadinessActive) return;
        ReadinessStage("statistics_sample", session, sample.Generation, -1,
            VideoTraceJson.StatisticsSample(sample, sampledAtMs, baselineReset));
    }

    public void ReadinessStage(string stage, long session, uint generation, long ptsUs, string? reason = null)
    {
        if (ReadinessActive) Record(stage, session, generation, ptsUs, reason);
    }

    public void Stage(string stage, long session, uint generation, long ptsUs, string? reason = null)
    {
        if (settings.Detailed) Record(stage, session, generation, ptsUs, reason);
    }

    public void Record(string stage, long session, uint generation, long ptsUs, string? reason = null)
    {
        if (!settings.Enabled) return;
        if (!buffer.Add(stage, session, generation, ptsUs, Stopwatch.GetTimestamp(), reason)) return;
        lock (gate)
        {
            if (!stopped) timer = new Timer(_ => Finish("duration"), null,
                TimeSpan.FromSeconds(settings.DurationSeconds), Timeout.InfiniteTimeSpan);
        }
    }

    private Task Finish(string reason)
    {
        lock (gate)
        {
            if (stopped) return dump ?? Task.CompletedTask;
            stopped = true;
            timer?.Dispose();
            var snapshot = buffer.Stop(reason, Stopwatch.GetTimestamp());
            if (snapshot is null) return Task.CompletedTask;
            return dump = Task.Run(() => WriteAsync(snapshot));
        }
    }

    private async Task WriteAsync(VideoTraceSnapshot snapshot)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RedrainCast", "Logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"video-trace-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(VideoTraceJson.Header(snapshot));
            foreach (var item in snapshot.Events)
                await writer.WriteLineAsync(VideoTraceJson.Event(snapshot, item));
            await writer.WriteLineAsync(VideoTraceJson.Summary(snapshot));
            await writer.FlushAsync();
            await status($"Video timestamp trace saved: {path}; complete={snapshot.Complete}, overflow={snapshot.Overflow}, dropped={snapshot.Dropped}.");
        }
        catch (Exception error)
        {
            try { await status($"Video timestamp trace could not be saved: {error.Message}"); }
            catch { }
        }
    }

    public async ValueTask DisposeAsync() => await Finish("dispose");
}
