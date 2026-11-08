using System.ComponentModel;
using System.Runtime.InteropServices;
using RedrainCast.Receive.Core;
using RedrainCast.Receive.Media;
using RedrainCast.Protocol;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class HdrDisplayPolicyTests
{
    public static TheoryData<Exception> MetadataFailures => new()
    {
        new HdrDisplayMetadataException("The window's current DXGI display could not be identified."),
        new COMException("CreateDXGIFactory1 failed", unchecked((int)0x80004005)),
        new COMException("EnumAdapters1 failed", unchecked((int)0x887A0022)),
        new COMException("EnumOutputs failed", unchecked((int)0x887A0026)),
        new COMException("GetDesc failed", unchecked((int)0x887A0005)),
        new COMException("GetDesc1 failed", unchecked((int)0x80004002)),
        new Win32Exception(1167, "Monitor disconnected")
    };

    [Theory]
    [MemberData(nameof(MetadataFailures))]
    public void TypedMetadataFailuresPreserveVerifiedConversionAndRecoverWithoutChangingSource(Exception failure)
    {
        var source = new HdrVideoFormat(true);
        var statistics = new VideoPresentationStatisticsCache();
        var native = HdrDisplayPolicy.ReadMetadata(() => HdrDisplayPolicy.FromMonitor(true, 250));
        statistics.Presented(source, native.NativeHdr);
        var unavailable = HdrDisplayPolicy.ReadMetadata(() => throw failure);
        Assert.Null(unavailable.DesktopHdrActive);
        Assert.False(unavailable.NativeHdr);
        Assert.Equal(100, unavailable.PeakNits);
        Assert.Contains(failure.Message, unavailable.Diagnostic);
        Assert.Equal(new HdrReceiverCapabilities(true, false, true), unavailable.Capabilities(true));
        Assert.Equal(HdrReceiverCapabilities.None, unavailable.Capabilities(false));
        Assert.Equal(VideoPresentationOutput.Hdr, statistics.Snapshot!.Output);
        statistics.Presented(source, unavailable.NativeHdr);
        Assert.Equal(VideoPresentationOutput.SdrToneMapped, statistics.Snapshot!.Output);
        Assert.Equal(unavailable, HdrDisplayPolicy.ReadMetadata(() => throw failure));
        var recovered = HdrDisplayPolicy.ReadMetadata(() => HdrDisplayPolicy.FromMonitor(true, 400));
        Assert.True(recovered.Capabilities(true).NativeHdrAvailable);
        Assert.Equal(400, recovered.PeakNits);
        Assert.Equal(VideoPresentationOutput.SdrToneMapped, statistics.Snapshot!.Output);
        statistics.Presented(source, recovered.NativeHdr);
        Assert.Equal(VideoPresentationOutput.Hdr, statistics.Snapshot!.Output);
        Assert.True(source.Hdr10);
        Assert.Null(source.Metadata);
    }

    [Theory]
    [InlineData(0x80004005)]
    [InlineData(0x80004002)]
    [InlineData(0x887A0005)]
    [InlineData(0x887A0022)]
    [InlineData(0x887A0026)]
    [InlineData(0x8007048F)]
    public void NativeQueryBoundaryRecognizesExpectedHResults(uint result) =>
        Assert.True(HdrDisplayPolicy.IsMetadataQueryFailure(unchecked((int)result)));

    [Fact]
    public void MetadataBoundaryDoesNotHideProgrammingErrors()
    {
        var errors = new Exception[]
        {
            new ArgumentException("Invalid query argument"),
            new InvalidOperationException("Invalid query state"),
            new NotSupportedException("Unexpected code path"),
            new COMException("E_INVALIDARG", unchecked((int)0x80070057)),
            new COMException("E_POINTER", unchecked((int)0x80004003)),
            new Win32Exception(87, "Invalid parameter")
        };
        foreach (var error in errors)
            Assert.Same(error, Assert.Throws(error.GetType(), () => HdrDisplayPolicy.ReadMetadata(() => throw error)));
        Assert.Throws<ArgumentNullException>(() => HdrDisplayPolicy.ReadMetadata(null!));
    }

    public static TheoryData<float> InvalidPeaks => new()
    {
        0, -1, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 79.99f, 10000.01f
    };

    [Theory]
    [MemberData(nameof(InvalidPeaks))]
    public void UnknownMonitorPeakUsesExplicitVerifiedSdrFallback(float peak)
    {
        var policy = HdrDisplayPolicy.FromMonitor(true, peak);
        Assert.False(policy.NativeHdr);
        Assert.True(policy.DesktopHdrActive);
        Assert.Equal(100, policy.PeakNits);
        Assert.Contains("DXGI monitor peak luminance is unavailable or invalid", policy.Diagnostic);
        Assert.Contains("Local HDR-to-SDR", policy.Diagnostic);
        Assert.Contains("Windows HDR setting unchanged", policy.Diagnostic);
        var capability = policy.Capabilities(true);
        Assert.False(capability.NativeHdrAvailable);
        Assert.True(capability.HdrToSdr);
        Assert.True(capability.CanReceiveHdr10);
        Assert.Equal(HdrReceiverCapabilities.None, policy.Capabilities(false));
        Assert.Throws<NotSupportedException>(() => new HdrDisplayPolicy(true, peak, "invalid").Validate());
    }

    [Theory]
    [InlineData(80)]
    [InlineData(250)]
    [InlineData(400)]
    [InlineData(10000)]
    public void NativeOutputUsesExactValidatedMonitorPeak(float peak)
    {
        var policy = HdrDisplayPolicy.FromMonitor(true, peak);
        Assert.True(policy.NativeHdr);
        Assert.Equal(peak, policy.PeakNits);
        Assert.True(policy.Capabilities(true).NativeHdrAvailable);
        policy.Validate();
    }

    [Fact]
    public void MetadataQueryFailureIsExplicitAndDoesNotInventDesktopHdrState()
    {
        var policy = HdrDisplayPolicy.MetadataUnavailable("DXGI GetDesc1 failure");
        Assert.Null(policy.DesktopHdrActive);
        Assert.False(policy.NativeHdr);
        Assert.Contains("DXGI GetDesc1 failure", policy.Diagnostic);
        Assert.Contains("Local HDR-to-SDR", policy.Diagnostic);
        Assert.True(policy.Capabilities(true).CanReceiveHdr10);
        Assert.False(policy.Capabilities(true).NativeHdrAvailable);
        Assert.False(policy.Capabilities(false).CanReceiveHdr10);
    }

    [Theory]
    [MemberData(nameof(InvalidPeaks))]
    public void ValidUnknownValidTransitionsKeepHdrSourceAndSuccessfulPresentationTruthful(float peak)
    {
        var source = new HdrVideoFormat(true);
        var statistics = new VideoPresentationStatisticsCache();
        var policies = new[]
        {
            HdrDisplayPolicy.FromMonitor(true, 250),
            HdrDisplayPolicy.FromMonitor(true, peak),
            HdrDisplayPolicy.FromMonitor(true, 400)
        };
        var outputs = new[]
        {
            VideoPresentationOutput.Hdr, VideoPresentationOutput.SdrToneMapped, VideoPresentationOutput.Hdr
        };
        for (var i = 0; i < policies.Length; i++)
        {
            if (i != 0) Assert.Equal(outputs[i - 1], statistics.Snapshot!.Output);
            statistics.Presented(source, policies[i].NativeHdr);
            Assert.Equal(outputs[i], statistics.Snapshot!.Output);
            Assert.True(source.Hdr10);
            Assert.Null(source.Metadata);
        }
        Assert.Equal(policies[1], HdrDisplayPolicy.FromMonitor(true, float.NaN));
        statistics.Presented(HdrVideoFormat.Sdr, policies[2].NativeHdr);
        Assert.Equal(VideoPresentationOutput.Sdr, statistics.Snapshot!.Output);
    }

    [Fact]
    public void SdrDesktopDoesNotInterpretAnyPeakAsNativeHdr()
    {
        Assert.Same(HdrDisplayPolicy.Sdr, HdrDisplayPolicy.FromMonitor(false, 400));
        Assert.False(HdrDisplayPolicy.Sdr.DesktopHdrActive);
        Assert.False(HdrDisplayPolicy.Sdr.Capabilities(true).NativeHdrAvailable);
    }
}
