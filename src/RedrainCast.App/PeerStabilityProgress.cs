using ScrcpyReceive.Core;

namespace RedrainCast.App;

internal sealed class PeerStabilityProgress(long session, bool audioRequired)
{
    private long epoch = -1;
    private long decoded, presented, played;
    private long decodedAt, presentedAt, playedAt;
    private bool initialized;

    public void Observe(long nowMilliseconds, long playbackEpoch, long decodedFrames, long presentedFrames,
        long playedFrames, bool receiving, bool attached, bool problem, ReceiverRecoveryState? state)
    {
        var owned = state?.Session == session;
        if (problem || !receiving || !owned || !attached && state?.Recovering != true)
            throw new IOException($"Peer playback ended during stability: problemOpen={problem}, receiving={receiving}, " +
                $"attached={attached}, expectedSession={session}, state={state}.");
        if (!initialized)
        {
            decodedAt = presentedAt = playedAt = nowMilliseconds;
            initialized = true;
        }
        if (attached)
        {
            var changed = epoch != playbackEpoch;
            if (decodedFrames > (changed ? 0 : decoded)) decodedAt = nowMilliseconds;
            if (presentedFrames > (changed ? 0 : presented)) presentedAt = nowMilliseconds;
            if (playedFrames > (changed ? 0 : played)) playedAt = nowMilliseconds;
            epoch = playbackEpoch;
            decoded = decodedFrames; presented = presentedFrames; played = playedFrames;
        }
        // A reconstruction is not progress and cannot restart the five-second stall bound.
        if (nowMilliseconds - decodedAt >= 5000 || nowMilliseconds - presentedAt >= 5000 ||
            audioRequired && nowMilliseconds - playedAt >= 5000)
            throw new IOException($"Real decode/presentation/audio counters stopped advancing: epoch={epoch}, " +
                $"decodeIdleMs={nowMilliseconds - decodedAt}, presentIdleMs={nowMilliseconds - presentedAt}, " +
                $"audioIdleMs={nowMilliseconds - playedAt}, state={state}.");
    }
}
