using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class EncoderSampleAccountingTests
{
    [Fact]
    public void EmptySampleRetiresItsMatchedInputWithoutProducingMedia()
    {
        var samples = new EncoderSampleAccounting();
        samples.Submitted(10);
        Assert.Equal((1, 1), samples.Complete(10, empty: true));
        Assert.Throws<IOException>(() => samples.Complete(10, empty: true));
    }

    [Fact]
    public void LaterOutputRetiresInputsSilentlyDroppedByTheDriver()
    {
        var samples = new EncoderSampleAccounting();
        foreach (var time in new long[] { 10, 20, 30 }) samples.Submitted(time);
        Assert.Equal((3, 2), samples.Complete(30, empty: false));
        Assert.Throws<IOException>(() => samples.Complete(20, empty: false));
    }

    [Fact]
    public void RepeatedInputTimestampsRemainIndividuallyAccountedFor()
    {
        var samples = new EncoderSampleAccounting();
        foreach (var time in new long[] { 10, 10, 20 }) samples.Submitted(time);
        Assert.Equal((1, 0), samples.Complete(10, empty: false));
        Assert.Equal((2, 1), samples.Complete(20, empty: false));
    }

    [Fact]
    public void UnknownOutputDoesNotConsumePendingInputs()
    {
        var samples = new EncoderSampleAccounting();
        samples.Submitted(10);
        samples.Submitted(20);
        Assert.Throws<IOException>(() => samples.Complete(15, empty: true));
        Assert.Equal((1, 0), samples.Complete(10, empty: false));
        Assert.Equal((1, 0), samples.Complete(20, empty: false));
    }

    [Fact]
    public void InvalidInputOrderIsRejected()
    {
        var samples = new EncoderSampleAccounting();
        Assert.Throws<ArgumentException>(() => samples.Submitted(-1));
        samples.Submitted(20);
        Assert.Throws<ArgumentException>(() => samples.Submitted(10));
        Assert.Equal((1, 0), samples.Complete(20, empty: false));
    }

}
