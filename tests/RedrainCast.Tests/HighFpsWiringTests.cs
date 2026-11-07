using Xunit;

namespace RedrainCast.Tests;

public sealed class HighFpsWiringTests
{
    private static string Source(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".cs.txt"));

    [Fact]
    public void NormalAndRecoveryFactoryUseValidatedSessionRateAndNeverClampToSixty()
    {
        Assert.Contains("new GpuPresenter(request.VideoProcessingFramesPerSecond)", Source("VideoHost"));
        Assert.Contains("_ = request.VideoProcessingFramesPerSecond;", Source("VideoPlayback"));
        Assert.DoesNotContain("request.FramesPerSecond > 60", Source("VideoPlayback"));
        var gpu = Source("GpuPresenter");
        Assert.Contains("InputFrameRate = new((uint)FramesPerSecond, 1)", gpu);
        Assert.Contains("OutputFrameRate = new((uint)FramesPerSecond, 1)", gpu);
        Assert.DoesNotContain("FrameRate = new(60, 1)", gpu);
    }

    [Fact]
    public void CaptureSettingsDoNotAdvertiseAUniversalReceiverOrMonitorCap()
    {
        var source = Source("MainWindow.xaml");
        Assert.Contains("fps.Header = InputHeader(\"MaximumFPS\")", source);
        Assert.Equal("Maximum capture FPS", RedrainCast.Core.EnglishCatalog.Get("MaximumFPS"));
        Assert.DoesNotContain("this receiver supports up to 60", source);
        Assert.Contains("CaptureSettings.FpsPresets.Append(settings.MaxFps)", source);
        Assert.DoesNotContain("Capture, decode and display performance", source);
    }

    [Fact]
    public void PresentationPtsSnapshotsAreBoundedOptInAndDoNotRequireAudio()
    {
        var host = Source("VideoHost");
        Assert.Contains("if (CollectPresentationSamples) presenter.EnablePresentationDiagnostics();", host);
        var gpu = Source("GpuPresenter");
        Assert.Contains("if (samples.Count < 2048)", gpu);
        Assert.Contains("samples.Add(new(frame.PtsUs, Stopwatch.GetTimestamp(), frame.RotationDegrees))", gpu);
        Assert.DoesNotContain("Audio", gpu);
    }
}
