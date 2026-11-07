namespace ScrcpyReceive.Core;

public sealed class PresentationDeadline(long startedAt)
{
    private long firstPresentationStartedAt = startedAt;
    private long visibleStartedAt = startedAt;
    private bool everPresented;
    public bool IsSuspended { get; private set; }

    public bool HasExpired(long now, long generationStartedAt, bool tracksReady, IVideoPlaybackProgress progress)
    {
        everPresented |= progress.HasEverPresentedVideo;
        if (tracksReady && progress.HasDecodedVideo && progress.IsPresentationSuspended)
        {
            IsSuspended = true;
            return false;
        }
        if (IsSuspended)
        {
            firstPresentationStartedAt = visibleStartedAt = now;
            IsSuspended = false;
        }
        return !everPresented && now - firstPresentationStartedAt >= 10_000 ||
            !progress.HasPresentedVideo && now - Math.Max(generationStartedAt, visibleStartedAt) >= 10_000;
    }
}
