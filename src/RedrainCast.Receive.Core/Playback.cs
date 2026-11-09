namespace RedrainCast.Receive.Core;

public sealed record ReceiverRecoveryState(long Session, uint Generation, uint ActiveGeneration, bool Recovering)
{
    public uint AudioGeneration { get; init; }
    public uint ActiveAudioGeneration { get; init; }
    public bool VideoRecovering { get; init; }
    public bool AudioRecovering { get; init; }
}

public sealed record AvTimingSample(uint Generation, long VideoPtsUs, long PlayedSourceAudioPtsUs,
    long PlayedSourceFrames, long SignedSkewUs, long ObservedAtMilliseconds);

public interface IPlaybackFactory
{
    string Description { get; }
    bool SupportsVideoRotation => false;
    bool SupportsStreamRecovery => false;
    RedrainCast.Protocol.ReceiverOptionCapabilities ReceiverOptions => RedrainCast.Protocol.ReceiverOptionCapabilities.None;
    RedrainCast.Protocol.HdrReceiverCapabilities HdrCapabilities => RedrainCast.Protocol.HdrReceiverCapabilities.None;
    ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken cancellationToken);
}

public interface IStreamRecoveryPlayback : IPlayback
{
    long? SourcePositionUs => null;
    long? DecodedRecoveryKeyPtsUs { get; }
    // Actual source written by this audio epoch, including initial playback; never inserted silence.
    long? FirstRecoverySourceAudioPtsUs { get; }
    ValueTask ConfigureRecoveryTrackAsync(Packet configuration, bool midstream, CancellationToken token);
    void SetAudioRecoveryCutoff(long cutoffPtsUs);
    void UpdateEncodedQueueDepth(uint track, int packets) { }
    void BeginTrackRecovery(uint track) { }
    void BeginSessionStop() { BeginTrackRecovery(1); BeginTrackRecovery(2); }
    void CheckTrackHealth(uint track) => CheckHealth();
    ValueTask ConfigureRecoveryAsync(Packet video, Packet? audio, bool midstream, CancellationToken token);
    void SetRecoveryKey(long keyPtsUs);
}
public interface IPlayback : IAsyncDisposable
{
    void CheckHealth() { }
    string Diagnostics => "";
    // The caller owns this worker-thread scope across configuration/native epoch changes.
    IDisposable? EnterTrack(uint track) => null;
    void Observe(Packet packet) { }
    // Completion means the real resources exist, not merely that configuration was queued.
    ValueTask ConfigureAsync(Packet configuration, CancellationToken cancellationToken);
    ValueTask PresentAsync(Packet media, CancellationToken cancellationToken);
}

public interface IVideoPlaybackProgress
{
    bool HasPresentedVideo { get; }
    bool HasDecodedVideo => HasPresentedVideo;
    bool IsPresentationSuspended => false;
    // Override when current-generation presentation resets on reconfiguration.
    bool HasEverPresentedVideo => HasPresentedVideo;
}

public sealed class UnavailablePlaybackFactory : IPlaybackFactory
{
    public string Description => "Playback unavailable in this foundation build. Hardware-only decoding, GPU presentation and audio are not yet connected.";

    public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<IPlayback>(new ProtocolException(
            $"Requested {request.VideoCodec}/{request.AudioCodec ?? "audio off"} over TCP. " +
            Description + " No capture was accepted.", "UNSUPPORTED_CONFIG"));
}
