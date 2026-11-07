using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class EncoderInputFlowTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(17)]
    [InlineData(32)]
    public void EncoderCanConsumeItsRequestedStartupDepthBeforeProducingOutput(int startupDepth)
    {
        var flow = new EncoderInputFlow();
        Assert.False(flow.CanAcceptInput);
        for (var index = 0; index < startupDepth; index++) flow.InputRequested();
        for (var index = 0; index < startupDepth; index++)
        {
            Assert.True(flow.CanAcceptInput);
            flow.InputSubmitted();
            Assert.Equal(index + 1, flow.InFlight);
        }
        Assert.Equal(0, flow.InputRequests);
        Assert.False(flow.CanAcceptInput);
        Assert.Throws<InvalidOperationException>(flow.InputSubmitted);
        for (var index = 0; index < startupDepth; index++) flow.OutputProduced();
        Assert.Equal(0, flow.InFlight);
        Assert.Throws<IOException>(flow.OutputProduced);
    }

    [Fact]
    public void NewCreditsAllowProgressWhileEarlierFramesAreStillBuffered()
    {
        var flow = new EncoderInputFlow();
        for (var index = 0; index < 17; index++) flow.InputRequested();
        for (var index = 0; index < 4; index++) flow.InputSubmitted();
        Assert.Equal(4, flow.InFlight);
        Assert.Equal(13, flow.InputRequests);
        Assert.True(flow.CanAcceptInput);
        for (var index = 4; index < 17; index++) flow.InputSubmitted();
        flow.OutputProduced();
        Assert.False(flow.CanAcceptInput);
        flow.InputRequested();
        Assert.True(flow.CanAcceptInput);
        flow.InputSubmitted();
        Assert.Equal(17, flow.InFlight);
        Assert.Equal(0, flow.InputRequests);
    }
}
