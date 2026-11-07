using RedrainCast.App;
using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class PeerStabilityProgressTests
{
    private static ReceiverRecoveryState State(uint generation = 1, bool recovering = false) =>
        new(42, generation, generation, recovering);

    [Fact]
    public void OwnedRecoveryDetachAndNewEpochCountersAreNotSessionEnd()
    {
        var progress = new PeerStabilityProgress(42, true);
        progress.Observe(0, 1, 100, 100, 48000, true, true, false, State());
        progress.Observe(1000, 1, 0, 0, 0, true, false, false, State(2, true));
        progress.Observe(2000, 2, 1, 1, 480, true, true, false, State(2));
        progress.Observe(6000, 2, 20, 20, 10000, true, true, false, State(2));
    }

    [Theory]
    [InlineData(true, true, true, 42, true)]
    [InlineData(false, true, false, 42, true)]
    [InlineData(true, false, false, 42, false)]
    [InlineData(true, false, false, 43, true)]
    public void AbortPredicatesRemainExplicit(bool receiving, bool attached, bool problem, long session, bool recovering)
    {
        var progress = new PeerStabilityProgress(42, true);
        var error = Assert.Throws<IOException>(() => progress.Observe(0, 1, 1, 1, 480,
            receiving, attached, problem, new(session, 2, 1, recovering)));
        Assert.Contains("expectedSession=42", error.Message);
        Assert.Contains("problemOpen=", error.Message);
        Assert.Contains("attached=", error.Message);
    }

    [Fact]
    public void RepeatedRecoveryDoesNotExtendStallBound()
    {
        var progress = new PeerStabilityProgress(42, true);
        progress.Observe(0, 1, 1, 1, 480, true, true, false, State());
        progress.Observe(3000, 1, 0, 0, 0, true, false, false, State(2, true));
        Assert.Throws<IOException>(() => progress.Observe(5000, 2, 0, 0, 0, true, true, false, State(3, true)));
    }

    [Fact]
    public void VideoProgressCannotHideStalledSourceAudio()
    {
        var progress = new PeerStabilityProgress(42, true);
        progress.Observe(0, 1, 1, 1, 480, true, true, false, State());
        Assert.Throws<IOException>(() => progress.Observe(5000, 1, 100, 100, 480, true, true, false, State()));
    }

    [Fact]
    public void MissingSessionIsNotSuccess()
    {
        var progress = new PeerStabilityProgress(42, false);
        Assert.Throws<IOException>(() => progress.Observe(0, 1, 1, 1, 0, true, true, false, null));
    }

    [Fact]
    public void StartingDetachedCannotRefreshProgressDeadline()
    {
        var progress = new PeerStabilityProgress(42, true);
        progress.Observe(0, 1, 0, 0, 0, true, false, false, State(2, true));
        progress.Observe(4000, 1, 0, 0, 0, true, false, false, State(3, true));
        Assert.Throws<IOException>(() => progress.Observe(5000, 1, 0, 0, 0, true, false, false, State(4, true)));
    }
}
