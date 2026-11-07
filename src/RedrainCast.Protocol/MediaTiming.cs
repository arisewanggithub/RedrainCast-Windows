using System.Diagnostics;

namespace RedrainCast.Protocol;

public enum MediaStage
{
    SourcePcm, TcpWriteBegin, TcpWriteEnd, SocketRead, AdmissionBegin, AdmissionEnd,
    QueueAdmit, QueueTake, DecodePushBegin, DecodePushEnd,
    OutputWake, OutputPullBegin, OutputPullEnd, OutputSpan, OutputWriteBegin, OutputWriteEnd,
    VideoPresent, OutputPosition, OutputWaitBegin, OutputWaitEnd
}

public readonly record struct MediaTimingEvent(long Sequence, long Qpc, int ThreadId, MediaStage Stage,
    long Session, uint Track, uint Generation, long Pts, long EndPts, long A, long B, long C, long D);

public sealed record MediaTimingSnapshot(int ProcessId, long Frequency, long StartedQpc, long StoppedQpc,
    int Capacity, long Attempted, long Dropped, bool Complete, long RecordingTicks, long MaximumRecordingTicks,
    MediaTimingEvent[] Events);

public sealed class MediaTiming : IDisposable
{
    public const int DefaultCapacity = 262144;
    private static MediaTiming? active;
    private readonly MediaTimingEvent[] events;
    private readonly long started = Stopwatch.GetTimestamp();
    private long next, ticks, maximum;
    private int writers, stopped;
    private MediaTimingSnapshot? snapshot;

    private MediaTiming(int capacity) => events = new MediaTimingEvent[capacity];

    public static bool Enabled => Volatile.Read(ref active) is not null;

    public static MediaTiming Start(int capacity = DefaultCapacity)
    {
        if (capacity is < 1 or > DefaultCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        var capture = new MediaTiming(capacity);
        if (Interlocked.CompareExchange(ref active, capture, null) is not null)
            throw new InvalidOperationException("A process timing capture is already active.");
        return capture;
    }

    public static void Record(MediaStage stage, long session, uint track, uint generation, long pts = -1,
        long endPts = -1, long a = 0, long b = 0, long c = 0, long d = 0)
    {
        var capture = Volatile.Read(ref active);
        if (capture is null) return;
        Interlocked.Increment(ref capture.writers);
        try
        {
            if (Volatile.Read(ref capture.stopped) != 0) return;
            capture.Append(stage, session, track, generation, pts, endPts, a, b, c, d);
        }
        finally { Interlocked.Decrement(ref capture.writers); }
    }

    private void Append(MediaStage stage, long session, uint track, uint generation, long pts, long endPts,
        long a, long b, long c, long d)
    {
        var begin = Stopwatch.GetTimestamp();
        var index = Interlocked.Increment(ref next) - 1;
        if (index < events.Length)
            events[index] = new(index, begin, Environment.CurrentManagedThreadId, stage,
                session, track, generation, pts, endPts, a, b, c, d);
        var elapsed = Stopwatch.GetTimestamp() - begin;
        Interlocked.Add(ref ticks, elapsed);
        var previous = Volatile.Read(ref maximum);
        while (elapsed > previous)
        {
            var observed = Interlocked.CompareExchange(ref maximum, elapsed, previous);
            if (observed == previous) break;
            previous = observed;
        }
    }

    public MediaTimingSnapshot Stop()
    {
        lock (events)
        {
            if (snapshot is not null) return snapshot;
            Interlocked.Exchange(ref stopped, 1);
            Interlocked.CompareExchange(ref active, null, this);
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref writers) == 0, TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timing writers did not quiesce; no complete trace is available.");
            var attempted = Interlocked.Read(ref next);
            var count = (int)Math.Min(attempted, events.Length);
            var dropped = Math.Max(0, attempted - events.Length);
            snapshot = new(Environment.ProcessId, Stopwatch.Frequency, started, Stopwatch.GetTimestamp(),
                events.Length, attempted, dropped, dropped == 0,
                ticks, maximum, events[..count]);
            return snapshot;
        }
    }

    public void Dispose() => Stop();

    public static string[] CheckPeerTrace(MediaTimingSnapshot trace, bool sender)
    {
        var errors = new List<string>();
        if (!trace.Complete || trace.Dropped != 0 || trace.Events.Length == 0)
            errors.Add("Trace is empty, overflowed or not quiescent.");
        var required = sender
            ? new[] { MediaStage.SourcePcm, MediaStage.TcpWriteBegin, MediaStage.TcpWriteEnd }
            : [MediaStage.SocketRead, MediaStage.AdmissionBegin, MediaStage.AdmissionEnd,
                MediaStage.QueueAdmit, MediaStage.QueueTake, MediaStage.DecodePushBegin, MediaStage.DecodePushEnd,
                MediaStage.OutputWake, MediaStage.OutputPosition, MediaStage.OutputPullBegin, MediaStage.OutputPullEnd,
                MediaStage.OutputSpan, MediaStage.OutputWriteBegin, MediaStage.OutputWriteEnd,
                MediaStage.OutputWaitBegin, MediaStage.OutputWaitEnd, MediaStage.VideoPresent];
        foreach (var stage in required)
            if (!trace.Events.Any(item => item.Stage == stage)) errors.Add($"Missing stage: {stage}.");
        foreach (var (begin, end) in sender
            ? new[] { (MediaStage.TcpWriteBegin, MediaStage.TcpWriteEnd) }
            : [(MediaStage.AdmissionBegin, MediaStage.AdmissionEnd),
                (MediaStage.DecodePushBegin, MediaStage.DecodePushEnd),
                (MediaStage.OutputWriteBegin, MediaStage.OutputWriteEnd),
                (MediaStage.OutputWaitBegin, MediaStage.OutputWaitEnd)])
        {
            var balance = trace.Events.Where(item => item.Stage == begin || item.Stage == end)
                .GroupBy(item => (item.Session, item.Track, item.Generation,
                    Pts: begin == MediaStage.OutputWaitBegin ? -1 : item.Pts,
                    Output: begin == MediaStage.OutputWriteBegin ? item.A : 0));
            if (balance.Any(group => group.Count(item => item.Stage == begin) != group.Count(item => item.Stage == end)))
                errors.Add($"Unpaired events: {begin}/{end}.");
        }
        return errors.ToArray();
    }
}
