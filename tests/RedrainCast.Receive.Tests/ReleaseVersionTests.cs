using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class ReleaseVersionTests
{
    [Fact]
    public void ReceiverProjectAndTestOutputsUseRedrainCastNames()
    {
        Assert.Equal("RedrainCast.Receive.Core", typeof(ReceiverServer).Assembly.GetName().Name);
        Assert.Equal("RedrainCast.Receive.Tests", typeof(ReleaseVersionTests).Assembly.GetName().Name);
    }

    [Theory]
    [InlineData("0.1.0.0", "v0.1.0")]
    [InlineData("2.7.12.0", "v2.7.12")]
    [InlineData("2.7.12.9", "v2.7.12.9")]
    [InlineData("0.1.0", "v0.1.0")]
    public void ActualReleaseComponentsArePreserved(string version, string expected) =>
        Assert.Equal(expected, ReleaseVersion.Label(Version.Parse(version)));

    [Fact]
    public void MissingBuildComponentFailsInsteadOfInventingAVersion() =>
        Assert.Throws<ArgumentException>(() => ReleaseVersion.Label(new Version(1, 0)));
}
