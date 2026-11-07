using RedrainCast.Protocol;

namespace RedrainCast.Send.Core;

public sealed record AudioPacket(byte[] Data, long PtsUs);

public static class PcmPackets
{
    public static IEnumerable<AudioPacket> Split(AudioPacket packet)
    {
        if (packet.Data.Length is 0 or > 262144 || packet.Data.Length % 4 != 0 || packet.PtsUs < 0)
            throw new ProtocolException("Invalid source stereo s16le audio packet.");
        var frames = packet.Data.Length / 4;
        _ = checked(packet.PtsUs + frames * 1_000_000L / RecoveryAudio.Rate);
        if (frames <= RecoveryAudio.MaximumPacketFrames) { yield return packet; yield break; }
        for (var offset = 0; offset < frames; offset += RecoveryAudio.MaximumPacketFrames)
        {
            var take = Math.Min(RecoveryAudio.MaximumPacketFrames, frames - offset);
            yield return new(packet.Data.AsSpan(offset * 4, take * 4).ToArray(),
                checked(packet.PtsUs + offset * 1_000_000L / RecoveryAudio.Rate));
        }
    }
}

public interface IAudioSource : IAsyncDisposable
{
    System.Text.Json.Nodes.JsonNode Configuration { get; }
    void BeginTransmission();
    IAsyncEnumerable<AudioPacket> ReadPacketsAsync(CancellationToken cancellationToken);
}

public sealed class AudioQueue(int maxBytes = 1024 * 1024)
{
    private readonly Queue<AudioPacket> packets = [];
    private readonly object gate = new();
    private int bytes;

    public void Enqueue(AudioPacket packet)
    {
        lock (gate)
        {
            if (packet.Data.Length is 0 or > 262144 || packet.Data.Length % 4 != 0 || packet.PtsUs < 0)
                throw new ProtocolException("Invalid stereo s16le audio packet.");
            if (packets.Count > 0 && packet.PtsUs < packets.Last().PtsUs)
                throw new ProtocolException("PCM timestamps moved backwards.");
            if (packets.Count >= 100 || bytes + packet.Data.Length > maxBytes ||
                packets.Count > 0 && packet.PtsUs - packets.Peek().PtsUs > 500_000)
                throw new IOException("CONGESTION: audio queue exceeded 1 MiB, 100 packets or 500 ms.");
            packets.Enqueue(packet);
            bytes += packet.Data.Length;
        }
    }

    public AudioPacket Dequeue()
    {
        lock (gate)
        {
            var packet = packets.Dequeue();
            bytes -= packet.Data.Length;
            return packet;
        }
    }
}

public sealed record AudioOutputState(bool Muted, float MasterVolume, IReadOnlyList<float> ChannelVolumes);

public sealed class MuteOwnership
{
    public Guid Context { get; } = Guid.NewGuid();
    private readonly object gate = new();
    private AudioOutputState? state;
    private bool desiredMute, changedByApp, pendingRemute;
    private bool initialized, appWritePending;
    private long externalRevision, appWriteRevision;

    public MuteOwnership() { }
    public MuteOwnership(bool originallyMuted) : this(new AudioOutputState(originallyMuted, 1, [1])) { }
    public MuteOwnership(AudioOutputState initial)
    {
        state = Snapshot(initial);
        desiredMute = initial.Muted;
        initialized = true;
    }

    public void Initialize(Func<AudioOutputState> readState)
    {
        long revision;
        lock (gate)
        {
            if (initialized) throw new InvalidOperationException("Mute ownership is already initialized.");
            revision = externalRevision;
        }
        var initial = Snapshot(readState());
        lock (gate)
        {
            if (initialized) throw new InvalidOperationException("Mute ownership is already initialized.");
            // A callback received during the native read is newer than that snapshot.
            if (externalRevision == revision)
            {
                state = initial;
                desiredMute = initial.Muted;
            }
            initialized = true;
        }
    }

    public void MarkAppMute()
    {
        lock (gate)
        {
            if (!initialized || state == null) throw new InvalidOperationException("Mute ownership requires a subscribed initial snapshot.");
            changedByApp |= !state.Muted;
            appWritePending = true;
            appWriteRevision = externalRevision;
            pendingRemute = false;
        }
    }

    public bool Observe(AudioOutputState notification, Guid context)
    {
        var current = Snapshot(notification);
        lock (gate)
        {
            if (context == Context)
            {
                if (appWritePending && appWriteRevision == externalRevision && state is { } observed)
                    state = observed with { Muted = current.Muted };
                appWritePending = false;
                return false;
            }
            externalRevision++;
            if (state == null)
            {
                state = current;
                desiredMute = current.Muted;
                pendingRemute = !current.Muted;
                return pendingRemute;
            }
            var volumeChanged = current.MasterVolume != state.MasterVolume ||
                                !current.ChannelVolumes.SequenceEqual(state.ChannelVolumes);
            if (!current.Muted) desiredMute = false;
            // An unchanged external muted state can be an explicit SetMute(true), not just a no-op.
            else if (!volumeChanged) desiredMute = true;
            state = current;
            pendingRemute = !current.Muted;
            return pendingRemute;
        }
    }

    public bool TakeRemuteRequest()
    {
        lock (gate)
        {
            var pending = pendingRemute;
            pendingRemute = false;
            return pending;
        }
    }

    public long ObservationRevision
    {
        get { lock (gate) return externalRevision; }
    }

    public void ConfirmLocalMute(long revision)
    {
        lock (gate)
        {
            if (state == null) throw new InvalidOperationException("Mute ownership has no observed state.");
            if (externalRevision == revision)
            {
                // The verified native mute may have overwritten an unmute received after MarkAppMute.
                changedByApp |= !desiredMute;
                state = state with { Muted = true };
            }
        }
    }

    public bool ShouldRestore(bool currentlyMuted)
    {
        lock (gate) return changedByApp && currentlyMuted && !desiredMute;
    }

    private static AudioOutputState Snapshot(AudioOutputState value)
    {
        if (!float.IsFinite(value.MasterVolume) || value.MasterVolume is < 0 or > 1 ||
            value.ChannelVolumes.Count is < 1 or > 64 ||
            value.ChannelVolumes.Any(volume => !float.IsFinite(volume) || volume is < 0 or > 1))
            throw new ArgumentException("Invalid Windows output volume notification.");
        return value with { ChannelVolumes = Array.AsReadOnly(value.ChannelVolumes.ToArray()) };
    }
}
