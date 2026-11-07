namespace RedrainCast.Send.Core;

public sealed class EncoderSampleAccounting
{
    public const int MaximumPending = 256;
    public readonly record struct OutputInfo(int Length, int Capacity, int SampleStatus, int TransformStatus,
        int ResultCode, bool DriverProvided, bool WholeSamples, bool SingleSamplePerBuffer,
        bool CallerAllocated, long? Timestamp100ns);

    private readonly Queue<long> pending = new();
    private long previousInput = -1;
    public int PendingCount => pending.Count;

    public static bool Supports(uint vendorId, string codec) =>
        EncoderCompatibility.IsQualcomm(vendorId) && codec is "h264" or "h265";

    public void ValidateSubmission(long timestamp)
    {
        if (timestamp < 0 || timestamp < previousInput)
            throw new ArgumentException("Encoder input timestamps must be nonnegative and monotonic.", nameof(timestamp));
        if (pending.Count >= MaximumPending)
            throw new IOException($"Encoder timestamp bookkeeping exceeded {MaximumPending} pending inputs without matched output.");
    }

    public void Submitted(long timestamp)
    {
        ValidateSubmission(timestamp);
        pending.Enqueue(timestamp);
        previousInput = timestamp;
    }

    public (int Completed, int Dropped) CompleteEmpty(uint vendorId, string codec, OutputInfo output)
    {
        if (!Supports(vendorId, codec) || output.Length != 0 || output.Capacity <= 0 ||
            output.ResultCode != 0 || output.SampleStatus != 0 || output.TransformStatus != 0 ||
            !output.DriverProvided || !output.WholeSamples || !output.SingleSamplePerBuffer ||
            output.CallerAllocated || output.Timestamp100ns is not >= 0)
            throw new IOException($"Invalid empty hardware output: vendor=0x{vendorId:X4}, codec={codec}, metadata={output}.");
        return Complete(output.Timestamp100ns.Value, empty: true);
    }

    public (int Completed, int Dropped) Complete(long timestamp, bool empty)
    {
        if (!pending.Contains(timestamp))
            throw new IOException($"Encoder output timestamp {timestamp} does not match a pending input.");
        var completed = 0;
        var dropped = 0;
        while (pending.Dequeue() != timestamp)
        {
            completed++;
            dropped++;
        }
        completed++;
        if (empty) dropped++;
        return (completed, dropped);
    }

}
