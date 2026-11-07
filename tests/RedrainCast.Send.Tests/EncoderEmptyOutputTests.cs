using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class EncoderEmptyOutputTests
{
    private const long Timestamp = 13_271_715_333_965;
    private static EncoderSampleAccounting.OutputInfo Valid =>
        new(0, 3_115_008, 0, 0, 0, true, true, true, false, Timestamp);

    [Theory]
    [InlineData(0x5143u, "h264")]
    [InlineData(0x4D4F4351u, "h264")]
    [InlineData(0x5143u, "h265")]
    [InlineData(0x4D4F4351u, "h265")]
    public void VerifiedEmptyShapeRetiresOnlyMatchedInputsAndNeverManufacturesCredits(uint vendor, string codec)
    {
        Assert.True(EncoderSampleAccounting.Supports(vendor, codec));
        var samples = new EncoderSampleAccounting();
        var flow = new EncoderInputFlow();
        for (var i = 0; i < 18; i++) flow.InputRequested();
        samples.Submitted(Timestamp);
        flow.InputSubmitted();
        var completed = samples.CompleteEmpty(vendor, codec, Valid);
        Assert.Equal((1, 1), completed);
        for (var i = 0; i < completed.Completed; i++) flow.OutputProduced();
        Assert.Equal(0, samples.PendingCount);
        Assert.Equal(0, flow.InFlight);
        Assert.Equal(17, flow.InputRequests);
        Assert.Throws<IOException>(() => samples.CompleteEmpty(vendor, codec, Valid));
    }

    [Theory]
    [InlineData(0x10DEu, "h264")]
    [InlineData(0x8086u, "h264")]
    [InlineData(0x1002u, "h265")]
    [InlineData(0x5143u, "av1")]
    [InlineData(0x4D4F4351u, "av1")]
    [InlineData(0x5143u, "unknown")]
    public void OtherVendorsAndCodecsRemainFatal(uint vendor, string codec)
    {
        Assert.False(EncoderSampleAccounting.Supports(vendor, codec));
        AssertRejectedWithoutConsumption(vendor, codec, Valid);
    }

    public static IEnumerable<object[]> InvalidMetadata()
    {
        foreach (var codec in new[] { "h264", "h265" })
            foreach (var output in new[]
            {
                Valid with { Length = 1 }, Valid with { Length = -1 },
                Valid with { Capacity = 0 }, Valid with { Capacity = -1 },
                Valid with { SampleStatus = 1 }, Valid with { TransformStatus = 1 },
                Valid with { ResultCode = 1 }, Valid with { ResultCode = unchecked((int)0x80004005) },
                Valid with { DriverProvided = false }, Valid with { WholeSamples = false },
                Valid with { SingleSamplePerBuffer = false }, Valid with { CallerAllocated = true },
                Valid with { Timestamp100ns = null }, Valid with { Timestamp100ns = -1 },
                Valid with { Timestamp100ns = Timestamp + 1 }
            })
                yield return [codec, output];
    }

    [Theory]
    [MemberData(nameof(InvalidMetadata))]
    public void InvalidOrUnknownEmptyMetadataIsFatalAndDoesNotEvictPendingInput(
        string codec, EncoderSampleAccounting.OutputInfo output) =>
        AssertRejectedWithoutConsumption(0x4D4F4351, codec, output);

    [Theory]
    [InlineData("h264")]
    [InlineData("h265")]
    public void EmptyAndValidOutputsRetireSkippedInputsStrictlyInFifoOrder(string codec)
    {
        var samples = new EncoderSampleAccounting();
        foreach (var timestamp in new[] { Timestamp - 2, Timestamp - 1, Timestamp, Timestamp + 1 })
            samples.Submitted(timestamp);
        Assert.Equal((3, 3), samples.CompleteEmpty(0x4D4F4351, codec, Valid));
        Assert.Equal(1, samples.PendingCount);
        Assert.Throws<IOException>(() => samples.Complete(Timestamp - 1, empty: false));
        Assert.Equal((1, 0), samples.Complete(Timestamp + 1, empty: false));
        Assert.Equal(0, samples.PendingCount);
    }

    [Fact]
    public void BookkeepingBoundFailsExplicitlyWithoutBecomingAnInputCreditThrottle()
    {
        var samples = new EncoderSampleAccounting();
        for (var i = 0; i < EncoderSampleAccounting.MaximumPending; i++) samples.Submitted(i);
        Assert.Throws<IOException>(() => samples.ValidateSubmission(EncoderSampleAccounting.MaximumPending));
        Assert.Throws<IOException>(() => samples.Submitted(EncoderSampleAccounting.MaximumPending));
        Assert.Equal(EncoderSampleAccounting.MaximumPending, samples.PendingCount);
        Assert.Equal((1, 0), samples.Complete(0, empty: false));
        samples.ValidateSubmission(EncoderSampleAccounting.MaximumPending);
        samples.Submitted(EncoderSampleAccounting.MaximumPending);
        Assert.Equal(EncoderSampleAccounting.MaximumPending, samples.PendingCount);
    }

    [Fact]
    public void RepeatedSubmittedTimestampsStillRequireOneCompletionPerActualInput()
    {
        var samples = new EncoderSampleAccounting();
        samples.Submitted(Timestamp);
        samples.Submitted(Timestamp);
        Assert.Equal((1, 1), samples.CompleteEmpty(0x5143, "h264", Valid));
        Assert.Equal((1, 0), samples.Complete(Timestamp, empty: false));
        Assert.Throws<IOException>(() => samples.CompleteEmpty(0x5143, "h264", Valid));
    }

    private static void AssertRejectedWithoutConsumption(uint vendor, string codec, EncoderSampleAccounting.OutputInfo output)
    {
        var samples = new EncoderSampleAccounting();
        samples.Submitted(Timestamp);
        Assert.Throws<IOException>(() => samples.CompleteEmpty(vendor, codec, output));
        Assert.Equal(1, samples.PendingCount);
        Assert.Equal((1, 0), samples.Complete(Timestamp, empty: false));
    }
}
