namespace RedrainCast.Receive.Core;

public sealed class ReceiveDiagnostics(Func<long>? nowMs = null)
{
    private sealed class Track
    {
        public long Count, Bytes, Pts, MaximumGapMs, MaximumReadMs;
        public long? ArrivedAt;
        public string MaximumGapScheduling = "none";
    }

    private readonly object gate = new();
    private readonly Func<long> now = nowMs ?? (() => Environment.TickCount64);
    private readonly Track[] tracks = [new(), new()];
    private long maximumHandlingMs;
    public TrackStatistics Snapshot(uint id)
    {
        if (id is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(id));
        lock (gate)
        {
            var track = tracks[id - 1];
            return new(track.Count, track.Bytes, track.ArrivedAt is { } at ? Math.Max(0, now() - at) : null);
        }
    }

    public void Received(Packet packet, long readMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(readMs);
        if (!Wire.IsMedia(packet.Type)) return;
        JsonObject.Require(packet.Track is 1 or 2, "Received media requires a valid track.");
        lock (gate)
        {
            var at = now();
            var track = tracks[packet.Track - 1];
            if (track.ArrivedAt is { } previous && at - previous > track.MaximumGapMs)
            {
                track.MaximumGapMs = at - previous;
                track.MaximumGapScheduling = track.MaximumGapMs >= 100 ? ThreadPoolStatus() : "below100ms";
            }
            track.ArrivedAt = at;
            track.Count++;
            track.Bytes = checked(track.Bytes + packet.Payload.Length);
            track.Pts = packet.PtsUs;
            track.MaximumReadMs = Math.Max(track.MaximumReadMs, readMs);
        }
    }

    public void Handled(long elapsedMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMs);
        lock (gate) maximumHandlingMs = Math.Max(maximumHandlingMs, elapsedMs);
    }

    public string Diagnostics
    {
        get
        {
            lock (gate)
            {
                var at = now();
                return $"receive handlingMaxMs={maximumHandlingMs}; " +
                    string.Join("; ", tracks.Select((track, index) =>
                        $"track={index + 1} received={track.Count} bytes={track.Bytes} lastPts={track.Pts} " +
                        $"ageMs={(track.ArrivedAt is { } arrived ? at - arrived : -1)} " +
                        $"gapMaxMs={track.MaximumGapMs} readMaxMs={track.MaximumReadMs} " +
                        $"gapScheduling=[{track.MaximumGapScheduling}]"));
            }

        }
    }

    public static string ThreadPoolStatus()
    {
        ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
        ThreadPool.GetMinThreads(out var minimumWorkers, out var minimumIo);
        ThreadPool.GetMaxThreads(out var maximumWorkers, out var maximumIo);
        return $"processors={Environment.ProcessorCount}, threads={ThreadPool.ThreadCount}, " +
            $"pending={ThreadPool.PendingWorkItemCount}, completed={ThreadPool.CompletedWorkItemCount}, " +
            $"availableWorkers={availableWorkers}, minWorkers={minimumWorkers}, maxWorkers={maximumWorkers}, " +
            $"availableIo={availableIo}, minIo={minimumIo}, maxIo={maximumIo}";
    }
}
