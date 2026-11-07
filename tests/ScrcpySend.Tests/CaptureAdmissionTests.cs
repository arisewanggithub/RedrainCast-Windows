using Xunit;

namespace ScrcpySend.Tests;

public sealed class CaptureAdmissionTests
{
    private static string Source => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "WindowsVideoSource.cs.txt"));

    [Fact]
    public void CapturePreservesOriginalSourceTimestampsWithoutDisposableProbeHooks()
    {
        var source = Source;
        Assert.DoesNotContain("captureCadence", source);
        Assert.DoesNotContain("CadenceCost", source);
        Assert.Contains("var timestamp = frame.SystemRelativeTime.Ticks;", source);
        Assert.Contains("pacer.Offer(timestamp);", source);
        Assert.Contains("encoder.Submit(surface, sourceTimestamp);", source);
        Assert.Contains("encoder.Submit(surface, pendingTimestamp);", source);
    }

    [Fact]
    public void CaptureStartRevalidatesItsFreshColorBeforeAllocatingOrStarting()
    {
        var source = Source;
        var constructor = source.IndexOf("public WindowsVideoSource(", StringComparison.Ordinal);
        var initialRead = source.IndexOf("var color = source.ReadColor(diagnostic);", constructor, StringComparison.Ordinal);
        var initialValidate = source.IndexOf(
            "EncoderCompatibility.ValidateCapture(memory.VendorId, color.Hdr, settings.VideoCodec);",
            initialRead, StringComparison.Ordinal);
        var start = source.IndexOf("private void Capture(", StringComparison.Ordinal);
        Assert.True(initialRead > constructor && initialValidate > initialRead && initialValidate < start);
        var read = source.IndexOf("var color = source.ReadColor(diagnostic);", start, StringComparison.Ordinal);
        Assert.True(read > start, "Capture must reread the color that may have changed since construction.");
        var validate = source.IndexOf(
            "EncoderCompatibility.ValidateCapture(memory.VendorId, color.Hdr, settings.VideoCodec);",
            read, StringComparison.Ordinal);
        Assert.True(validate > read, "The capture-start color snapshot must be validated, not just the constructor snapshot.");
        foreach (var operation in new[]
        {
            "new GpuSurfacePool(", "new CaptureFrameSession(", "frames.Recreate(",
            "new GpuProcessor(", "encoder.Start();", "frames.StartCapture();"
        })
            Assert.True(source.IndexOf(operation, start, StringComparison.Ordinal) > validate,
                $"{operation} must follow capture-start compatibility validation.");
    }

    [Fact]
    public void ColorChangesRemainValidatedBeforeRecreationOrEncoderReuse()
    {
        var source = Source;
        var read = source.IndexOf("var nextColor = source.ReadColor(diagnostic);", StringComparison.Ordinal);
        var validate = source.IndexOf(
            "EncoderCompatibility.ValidateCapture(memory.VendorId, nextColor.Hdr, settings.VideoCodec);",
            read, StringComparison.Ordinal);
        Assert.True(validate > read);
        foreach (var operation in new[] { "if (nextColor != color)", "frames.Recreate(", "encoder.TakeOutput()", "surfaces.TryRent()" })
            Assert.True(source.IndexOf(operation, read, StringComparison.Ordinal) > validate,
                $"{operation} must follow color-change compatibility validation.");
    }
}
