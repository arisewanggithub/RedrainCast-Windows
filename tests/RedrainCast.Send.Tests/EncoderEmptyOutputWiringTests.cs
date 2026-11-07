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
}
