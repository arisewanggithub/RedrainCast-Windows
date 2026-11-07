namespace ScrcpyReceive.Core;

public sealed class PresentationSlot
{
    // Bootstrap an empty swapchain; a rejected Present must retain its opportunity to retry.
    private bool held = true;
    public bool IsHeld => Volatile.Read(ref held);

    public bool TryAcquire(bool ready)
    {
        if (ready) held = true;
        return held;
    }

    public void Submitted() => held = false;
    public void Reset() => held = true;
}
