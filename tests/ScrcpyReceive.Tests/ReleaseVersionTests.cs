using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class ReleaseVersionTests
{
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
