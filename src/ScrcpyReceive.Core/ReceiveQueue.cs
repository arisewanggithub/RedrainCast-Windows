using RedrainCast.Protocol;

namespace ScrcpyReceive.Core;

public sealed class ReceiveQueue : IDisposable
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumEntries = 512;
    public const long MaximumSpanUs = 500_000;
    private sealed record Entry(Packet Packet, long AdmittedAt);
    private readonly object gate = new();
    private readonly Queue<Entry>[] packets = [new(), new()];
    private readonly SemaphoreSlim[] signals = [new(0), new(0)];
    private readonly Packet?[] inFlight = new Packet?[2];
    private int mediaBytes;
    private int controlBytes;
    private int controls;
    private readonly int maximumMediaBytes;
    public int MediaByteBudget => maximumMediaBytes;
    private readonly int audioReserveBytes;
    private int videoBytes;
    private TaskCompletionSource space = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long[] waitTotalUs = new long[2], waitMaximumUs = new long[2], mediaTaken = new long[2];
    public EncodedQueueStatistics Snapshot(uint track) => Snapshot(track, includeInFlight: true);
    public EncodedQueueStatistics WaitingSnapshot(uint track) => Snapshot(track, includeInFlight: false);
    private EncodedQueueStatistics Snapshot(uint track, bool includeInFlight)
    {
        if (track is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(track));
        lock (gate)
        {
            var count = 0;
            long bytes = 0, minimum = long.MaxValue, maximum = 0;
            void Include(Packet? packet)
            {
                if (packet is null || !Wire.IsMedia(packet.Type)) return;
                count++; bytes += packet.Payload.Length;
                minimum = Math.Min(minimum, packet.PtsUs);
                maximum = Math.Max(maximum, packet.PtsUs);
            }
            foreach (var entry in packets[track - 1]) Include(entry.Packet);
            if (includeInFlight) Include(inFlight[track - 1]);
            return new(count, bytes, count == 0 ? 0 : maximum - minimum);
        }
    }
    public string Diagnostics
    {
        get
        {
            lock (gate)
                return $"queue videoWaitAvgUs={waitTotalUs[0] / Math.Max(1, mediaTaken[0])}, videoWaitMaxUs={waitMaximumUs[0]}, " +
                    $"audioWaitAvgUs={waitTotalUs[1] / Math.Max(1, mediaTaken[1])}, audioWaitMaxUs={waitMaximumUs[1]}, " +
                    $"mediaBytes={mediaBytes}/{maximumMediaBytes}, videoQueued={packets[0].Count}, audioQueued={packets[1].Count}, " +
                    $"videoInFlight={inFlight[0]?.Type.ToString() ?? "none"}, audioInFlight={inFlight[1]?.Type.ToString() ?? "none"}";
        }
    }

    public ReceiveQueue(int maximumMediaBytes = MaximumBytes, int audioReserveBytes = 0)
    {
        if (maximumMediaBytes is <= 0 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumMediaBytes));
        if (audioReserveBytes < 0 || audioReserveBytes >= maximumMediaBytes)
            throw new ArgumentOutOfRangeException(nameof(audioReserveBytes));
        this.maximumMediaBytes = maximumMediaBytes;
        this.audioReserveBytes = audioReserveBytes;
    }

    public void Add(Packet packet)
    {
        lock (gate)
        {
            var media = Wire.IsMedia(packet.Type);
            JsonObject.Require(packet.Track is 1 or 2, "Queued packets require an enabled track.");
            JsonObject.Require(packets.Sum(items => items.Count) + inFlight.Count(item => item is not null) < MaximumEntries,
                "Receiver queue entry limit reached.", "CONGESTION");
            if (media)
            {
                JsonObject.Require(packet.Payload.Length <= maximumMediaBytes - mediaBytes,
                    $"Receiver media exceeds its {maximumMediaBytes}-byte queue budget.", "CONGESTION");
                JsonObject.Require(packet.Track != 1 || packet.Payload.Length <= maximumMediaBytes - audioReserveBytes - videoBytes,
                    "Receiver video exceeds its queue budget reserved for audio.", "CONGESTION");
                var minimum = packet.PtsUs;
                var maximum = packet.PtsUs;
                foreach (var queued in packets.SelectMany(items => items).Select(entry => entry.Packet))
                    if (Wire.IsMedia(queued.Type) && queued.Track == packet.Track)
                    {
                        minimum = Math.Min(minimum, queued.PtsUs);
                        maximum = Math.Max(maximum, queued.PtsUs);
                    }
                JsonObject.Require(maximum - minimum <= MaximumSpanUs,
                    $"Receiver track {packet.Track} queue exceeds 500 ms: min={minimum}, max={maximum}, bytes={mediaBytes}, entries={packets.Sum(items => items.Count)}.", "CONGESTION");
                mediaBytes += packet.Payload.Length;
                if (packet.Track == 1) videoBytes += packet.Payload.Length;
            }
            else
            {
                JsonObject.Require(controls < 64 && packet.Payload.Length <= 256 * 1024 - controlBytes,
                    "Receiver configuration queue limit reached.", "CONGESTION");
                controls++;
                controlBytes += packet.Payload.Length;
            }
            packets[packet.Track - 1].Enqueue(new(packet, System.Diagnostics.Stopwatch.GetTimestamp()));
            MediaTiming.Record(MediaStage.QueueAdmit, packet.Session, packet.Track, packet.Generation,
                packet.PtsUs, a: packet.Payload.Length, b: packets[packet.Track - 1].Count, c: mediaBytes);
            signals[packet.Track - 1].Release();
        }
    }

    public async ValueTask AddAsync(Packet packet, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Wire.IsMedia(packet.Type) && packet.Payload.Length > maximumMediaBytes) { Add(packet); return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
        var until = Environment.TickCount64 + 500;
        ProtocolException? lastCongestion = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (lastCongestion is not null && Environment.TickCount64 >= until) throw lastCongestion;
            Task changed;
            ProtocolException congestion;
            lock (gate)
            {
                changed = space.Task;
                try { Add(packet); return; }
                catch (ProtocolException error) when (error.Code == "CONGESTION") { congestion = error; lastCongestion = error; }
            }
            try { await changed.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw congestion; }
        }
    }

    private void SpaceChanged()
    {
        var previous = space;
        space = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    public async ValueTask<Packet> TakeAsync(uint track, CancellationToken cancellationToken)
    {
        if (track is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(track));
        while (true)
        {
        await signals[track - 1].WaitAsync(cancellationToken);
        lock (gate)
        {
            if (inFlight[track - 1] is not null) throw new InvalidOperationException("Each track has one serial consumer.");
            if (!packets[track - 1].TryDequeue(out var entry)) continue;
            var packet = entry.Packet;
            if (Wire.IsMedia(packet.Type))
            {
                var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(entry.AdmittedAt).TotalMicroseconds;
                mediaTaken[track - 1]++;
                waitTotalUs[track - 1] += elapsed;
                waitMaximumUs[track - 1] = Math.Max(waitMaximumUs[track - 1], elapsed);
            }
            inFlight[track - 1] = packet;
            MediaTiming.Record(MediaStage.QueueTake, packet.Session, packet.Track, packet.Generation,
                packet.PtsUs, a: packet.Payload.Length, b: packets[track - 1].Count, c: mediaBytes);
            SpaceChanged();
            return packet;
        }
        }
    }

    public void Complete(Packet packet)
    {
        lock (gate)
        {
            if (packet.Track is not (1 or 2) || !ReferenceEquals(inFlight[packet.Track - 1], packet))
                throw new InvalidOperationException("Completing an unowned track packet.");
            inFlight[packet.Track - 1] = null;
            if (Wire.IsMedia(packet.Type))
            {
                mediaBytes -= packet.Payload.Length;
                if (packet.Track == 1) videoBytes -= packet.Payload.Length;
            }
            else { controls--; controlBytes -= packet.Payload.Length; }
            SpaceChanged();
        }
    }

    public void DiscardQueuedMedia()
    {
        lock (gate)
        {
            for (var track = 0; track < packets.Length; track++)
            {
                var keep = new Queue<Entry>();
                while (packets[track].TryDequeue(out var entry))
                {
                    if (Wire.IsMedia(entry.Packet.Type))
                    {
                        mediaBytes -= entry.Packet.Payload.Length;
                        if (track == 0) videoBytes -= entry.Packet.Payload.Length;
                        // A waiting consumer may already own the semaphore signal.
                    }
                    else keep.Enqueue(entry);
                }
                foreach (var entry in keep) packets[track].Enqueue(entry);
                while (signals[track].Wait(0)) { }
                if (packets[track].Count > 0) signals[track].Release(packets[track].Count);
            }
            SpaceChanged();
        }
    }
    public void Dispose()
    {
        foreach (var signal in signals) signal.Dispose();
        foreach (var queue in packets) queue.Clear();
        Array.Clear(inFlight);
    }
}
