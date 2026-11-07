namespace ScrcpySend.Core;

public sealed class EncoderInputFlow
{
    private int inputRequests;
    private int inFlight;
    public int InputRequests => Volatile.Read(ref inputRequests);
    public int InFlight => Volatile.Read(ref inFlight);
    public bool CanAcceptInput => InputRequests > 0;

    public void InputRequested() => inputRequests = checked(inputRequests + 1);

    public void InputSubmitted()
    {
        if (!CanAcceptInput) throw new InvalidOperationException("Hardware encoder did not request another input.");
        inFlight = checked(inFlight + 1);
        inputRequests--;
    }

    public void OutputProduced()
    {
        if (inFlight == 0) throw new IOException("Hardware encoder produced output without a submitted input.");
        inFlight--;
    }
}
