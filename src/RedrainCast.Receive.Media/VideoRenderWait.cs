using RedrainCast.Receive.Core;

namespace RedrainCast.Receive.Media;

internal static class VideoRenderWait
{
    // Never consume another auto-reset signal while retaining a slot after rejection.
    public static int Wait(WaitHandle publication, WaitHandle cancellation, WaitHandle readiness,
        PresentationSlot slot, int timeoutMilliseconds, bool readinessEligible,
        Action? signalObserved = null)
    {
        var wake = readinessEligible && !slot.IsHeld
            ? WaitHandle.WaitAny([publication, cancellation, readiness], timeoutMilliseconds)
            : WaitHandle.WaitAny([publication, cancellation], timeoutMilliseconds);
        if (wake == 2)
        {
            slot.TryAcquire(true);
            signalObserved?.Invoke();
        }
        return wake;
    }
}
