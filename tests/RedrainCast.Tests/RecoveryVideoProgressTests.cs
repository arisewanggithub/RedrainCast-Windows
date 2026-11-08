using RedrainCast.App;
using Xunit;

namespace RedrainCast.Tests;

public sealed class RecoveryVideoProgressTests
{
    [Fact]
    public void LifetimeFramesAndDecodedProofCannotReplaceRecoveredPresentation()
    {
        var progress = new RecoveryVideoProgress(2, 100, 80, 1_000_000);
        Assert.False(progress.HasOutput(100, 80, 900_000, 16, 1));
        Assert.False(progress.HasOutput(116, 80, 900_000, 16, 1));
        Assert.False(progress.HasOutput(116, 81, 999_999, 16, 1));
        Assert.True(progress.HasOutput(116, 81, 1_000_000, 16, 1));
    }

    [Fact]
    public void RecoveryRequiresCurrentKeyAndExactGenerationLocalThresholds()
    {
        var progress = new RecoveryVideoProgress(2, 100, 80);
        Assert.False(progress.HasOutput(116, 90, 1_000_000, 16, 10));
        progress = progress with { KeyPtsUs = 1_000_000 };
        Assert.False(progress.HasOutput(115, 90, 1_000_000, 16, 10));
        Assert.False(progress.HasOutput(116, 89, 1_000_000, 16, 10));
        Assert.True(progress.HasOutput(116, 90, 1_000_000, 16, 10));
        progress = new(3, 116, 90, 2_000_000);
        Assert.False(progress.HasOutput(116, 90, 2_000_000, 16, 10));
        Assert.False((progress with { Generation = 1 }).HasOutput(132, 100, 2_000_000, 16, 10));
    }
}
