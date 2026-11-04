using System.Net;
using System.Runtime.InteropServices;
using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class ArchitectureTests
{
    [Theory]
    [InlineData(Architecture.Arm64, "win-arm64")]
    [InlineData(Architecture.X64, "win-x64")]
    public void NativeLibrariesFollowTheProcessArchitecture(Architecture architecture, string expected) =>
        Assert.Equal(expected, NativeArchitecture.RuntimeIdentifier(architecture));

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    public void UnsupportedArchitecturesFailWithoutLoadingAnotherArchitecture(Architecture architecture) =>
        Assert.Throws<PlatformNotSupportedException>(() => NativeArchitecture.RuntimeIdentifier(architecture));

    [Fact]
    public void CurrentProcessLibraryDirectoryUsesTheMatchingRuntimeIdentifier() =>
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "native",
            NativeArchitecture.RuntimeIdentifier(RuntimeInformation.ProcessArchitecture)), NativeArchitecture.LibraryDirectory);

    [Theory]
    [InlineData(false, "ScrcpyRecv - 192.0.2.124 : 27184")]
    [InlineData(true, "ScrcpyRecv - 192.0.2.124 : 27184 (busy)")]
    public void ReceiverDisplayUsesSpacesWithoutChangingTheEndpointIdentity(bool busy, string expected)
    {
        var endpoint = new ReceiverEndpoint(IPAddress.Parse("192.0.2.124"), 27184, "ScrcpyRecv", busy);
        Assert.Equal(expected, endpoint.ToString());
        Assert.Equal("192.0.2.124:27184", endpoint.Key);
        Assert.Equal(expected, new ReceiverChoice(endpoint).ToString());
    }
}
