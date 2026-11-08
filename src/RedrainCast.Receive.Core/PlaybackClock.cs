namespace RedrainCast.Receive.Core;

public sealed class PlaybackClock(Func<long>? milliseconds = null, bool audioExpected = false)
{
    private readonly object gate = new();
    private readonly Func<long> now = milliseconds ?? (() => Environment.TickCount64);
    private long? anchor, last;
    private long anchoredAt;
    private bool audioAttached;
    private bool audioEverPlayed, recoveringAudio;

    public long? CurrentUs
    {
        get
        {
            lock (gate)
            {
                if (anchor is not { } pts) return null;
                var current = audioAttached ? pts : checked(pts + Math.Max(0, now() - anchoredAt) * 1000);
                last = Math.Max(last ?? current, current);
                return last;
            }
        }
    }

    public void AnchorVideo(long ptsUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ptsUs);
        lock (gate)
        {
            if (audioEverPlayed || CurrentUs is { } current && ptsUs <= current) return;
            anchor = ptsUs;
            last = ptsUs;
            anchoredAt = checked(now() + (audioExpected ? AudioBuffer.TargetFrames * 1000 / AudioFormat.Rate : 0));
        }
    }

    public void ObserveAudio(long ptsUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ptsUs);
        lock (gate)
        {
            anchor = Math.Max(ptsUs, CurrentUs ?? ptsUs);
            last = anchor; anchoredAt = now(); audioAttached = true;
            audioEverPlayed = true; recoveringAudio = false;
        }
    }

    public void DetachAudio()
    {
        lock (gate)
        {
            anchor = CurrentUs;
            anchoredAt = now(); audioAttached = false;
        }
    }

    public void BeginAudioRecovery()
    {
        lock (gate)
        {
            DetachAudio();
            recoveringAudio = true;
        }
    }

    public void VideoPresented(long ptsUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ptsUs);
        lock (gate)
        {
            if (!recoveringAudio || audioEverPlayed || CurrentUs is { } current && ptsUs <= current) return;
            anchor = last = ptsUs;
            anchoredAt = now();
        }
    }
}
