using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class EncoderEmptyOutputWiringTests
{
    [Fact]
    public void H264AndHevcShareTheStrictProfileAndNativeMetadataClassifier()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "HardwareEncoder.cs.txt"));
        Assert.Contains("EncoderSampleAccounting.Supports(VendorId, settings.VideoCodec)", source);
        Assert.Contains("samples.CompleteEmpty(VendorId, settings.VideoCodec,", source);
        Assert.Contains("new(length, maximumLength,", source);
        Assert.Contains("samples?.ValidateSubmission(pts100ns);", source);
        Assert.True(source.IndexOf("samples?.ValidateSubmission(pts100ns);", StringComparison.Ordinal) <
            source.IndexOf("transform.ProcessInput(0, sample, 0);", StringComparison.Ordinal));
        Assert.Contains("finally { buffer.Unlock(); }", source);
        Assert.Contains("data.Sample?.Dispose();", source);
        Assert.DoesNotContain("Thread.Sleep", source);
    }

    [Fact]
    public void UncappedHardwareUsesOnlyPositiveNominalFormatHintsAndPreservesSourcePts()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "HardwareEncoder.cs.txt"));
        Assert.Contains("MediaType(Subtype(settings.VideoCodec), width, height, settings.NominalFramesPerSecond, color)", source);
        Assert.Contains("MediaType(InputSubtype, width, height, settings.NominalFramesPerSecond, color)", source);
        Assert.Contains("ValidateFormat(actualOutput, Subtype(settings.VideoCodec), width, height, settings.NominalFramesPerSecond, color, profile)", source);
        Assert.Contains("ValidateFormat(actualInput, InputSubtype, width, height, settings.NominalFramesPerSecond, color)", source);
        Assert.Contains("private Guid InputSubtype => color.Hdr10 ? P010 : VideoFormatGuids.NV12;", source);
        Assert.Contains("var profile = color.Hdr10 ? 2u : EncoderCompatibility.OutputProfile(settings.VideoCodec);", source);
        Assert.Contains("type.GetGUID(MediaTypeAttributeKeys.Subtype) != subtype", source);
        Assert.Contains("type.GetUInt32(MediaTypeAttributeKeys.Mpeg2Profile) != requiredProfile", source);
        Assert.Contains("(ulong)(uint)(rate >> 32) != (ulong)fps * (uint)rate", source);
        Assert.Contains("No format/FPS substitution is allowed.", source);
        Assert.Contains("Software encoding and automatic codec/color substitution are not allowed.", source);
        Assert.Contains("sample.SampleTime = pts100ns;", source);
        Assert.Contains("sample.SampleDuration = 10_000_000L / settings.NominalFramesPerSecond;", source);
        Assert.Contains("output.Add(new(bytes, outputTime / 10, configuration));", source);
        Assert.DoesNotContain("/ settings.MaxFps", source);
        Assert.DoesNotContain("FramePacer", source);
    }

    [Fact]
    public void CaptureKeepsUncappedPacingSeparateFromGpuFormatAndBackpressure()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "WindowsVideoSource.cs.txt"));
        Assert.Contains("new DeferredFramePacer(settings.MaxFps)", source);
        Assert.Contains("size.Width, size.Height, Width, Height, settings.NominalFramesPerSecond, color.Hdr, ColorFormat.Hdr10)", source);
        Assert.Contains("resized.Width, resized.Height, Width, Height, settings.NominalFramesPerSecond, color.Hdr, ColorFormat.Hdr10)", source);
        Assert.DoesNotContain("Width, Height, settings.MaxFps, color.Hdr", source);
        Assert.Contains("var timestamp = frame.SystemRelativeTime.Ticks;", source);
        Assert.Contains("pacer.Offer(timestamp);", source);
        Assert.Contains("encoder.Submit(surface, pendingTimestamp);", source);
        Assert.Contains("var canAcceptInput = encoder.CanAcceptInput;", source);
        Assert.Contains("using var surface = surfaces.TryRent();", source);
        Assert.Contains("pacer.DiscardPending();", source);
        Assert.Contains("new CaptureFrameSession(captureDevice, item, FrameArrived, diagnostic)", source);
    }

    [Fact]
    public void NewlyCreatedAndRearmedCaptureSessionsRemoveTheOsThrottleBeforeStarting()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "CaptureFrameSession.cs.txt"));
        Assert.Contains("ApiInformation.IsPropertyPresent(\"Windows.Graphics.Capture.GraphicsCaptureSession\", \"MinUpdateInterval\")", source);
        Assert.Contains("session.MinUpdateInterval = TimeSpan.Zero;", source);
        Assert.True(source.IndexOf("session = pool.CreateCaptureSession(item);", StringComparison.Ordinal) <
            source.IndexOf("session.MinUpdateInterval = TimeSpan.Zero;", StringComparison.Ordinal));
        Assert.Contains("if (pool is not null && !rearm)", source);
        Assert.Contains("pool.Recreate(device, format, 2, size);", source);
        Assert.Contains("effectiveMinUpdateIntervalTicks={session.MinUpdateInterval.Ticks}", source);
        Assert.Contains("MinUpdateInterval is unavailable on this Windows build", source);
        Assert.Contains("catch { Close(); throw; }", source);
        Assert.DoesNotContain("TimeSpan.FromMilliseconds(16)", source);
    }

    [Fact]
    public void DiagnosticRateDescriptionsNeverDescribeUncappedAsZeroFps()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "MediaDiagnostics.cs.txt"));
        Assert.Contains("fps == 0 ? \"Uncapped\" : $\"{fps} FPS\"", source);
        Assert.Contains("configured at {FrameRateDescription(fps)}:", source);
        Assert.Contains("at {FrameRateDescription(fps)}/{bitrate} bits/s", source);
        Assert.DoesNotContain("at {fps} FPS", source);
        Assert.DoesNotContain("/ settings.MaxFps", source);
    }
}
