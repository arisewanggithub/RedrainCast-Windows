using Xunit;

namespace RedrainCast.Tests;

public sealed class HighFpsWiringTests
{
    private static string Source(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".cs.txt"));

    [Fact]
    public void NormalAndRecoveryFactoryUseValidatedSessionRateAndNeverClampToSixty()
    {
        Assert.Contains("new GpuPresenter(request.VideoProcessingFramesPerSecond", Source("VideoHost"));
        Assert.Contains("_ = request.VideoProcessingFramesPerSecond;", Source("VideoPlayback"));
        Assert.DoesNotContain("request.FramesPerSecond > 60", Source("VideoPlayback"));
        var gpu = Source("GpuPresenter");
        Assert.Contains("InputFrameRate = new((uint)NominalFramesPerSecond, 1)", gpu);
        Assert.Contains("OutputFrameRate = new((uint)NominalFramesPerSecond, 1)", gpu);
        Assert.DoesNotContain("FrameRate = new(60, 1)", gpu);
    }

    [Fact]
    public void UncappedUsesOnlyANominalProcessorHintAndNoDecoderRateHintOrFpsPacing()
    {
        var gpu = Source("GpuPresenter");
        Assert.Contains("framesPerSecond is < 0 or > 240", gpu);
        Assert.Contains("NominalFramesPerSecond => FramesPerSecond == 0 ? 60 : FramesPerSecond", gpu);
        Assert.Contains("FramesPerSecond == 0 ? \"Uncapped\"", gpu);
        Assert.DoesNotContain("/ FramesPerSecond", gpu);
        Assert.DoesNotContain("/ NominalFramesPerSecond", gpu);
        var decoder = Source("HardwareDecoder");
        Assert.Contains("context->pkt_timebase = new AVRational { num = 1, den = 1_000_000 }", decoder);
        Assert.DoesNotContain("context->framerate =", decoder);
        Assert.DoesNotContain("FramesPerSecond", decoder);
        Assert.DoesNotContain("/ FramesPerSecond", Source("VideoPlayback"));
    }

    [Fact]
    public void NativeProbeExercisesUncappedRecoveryWithoutDividingByZero()
    {
        var probe = Source("HighFpsSelfTest");
        Assert.Contains("foreach (var fps in new[] { 0, 90, 120 })", probe);
        Assert.Contains("var sourceFps = fps == 0 ? 120 : fps;", probe);
        Assert.Contains("var nominalFps = fps == 0 ? 60 : fps;", probe);
        Assert.Contains("emitted / (double)sourceFps", probe);
        Assert.DoesNotContain("emitted / (double)fps", probe);
        Assert.Contains("decoded / seconds <= 60", probe);
        Assert.Contains("snapshot.Samples.Skip(samplesBefore.Samples.Length)", probe);
        Assert.Contains("measurements.Count == 6", probe);
        Assert.DoesNotContain("measurements.Count == 4", probe);
    }

    [Fact]
    public void NativeProbeUsesExistingStartupJournalWithoutEnablingItForNormalLaunch()
    {
        var app = Source("App.xaml");
        Assert.Contains("var diagnosticIndex = uiIndex >= 0 ? uiIndex : highFpsIndex;", app);
        Assert.Contains("using var uiTestJournal = diagnosticIndex >= 0 ? new DiagnosticLog", app);
        Assert.Contains("UnhandledException += (_, failure) => uiTestJournal.Write", app);
    }

    [Fact]
    public void CaptureSettingsDoNotAdvertiseAUniversalReceiverOrMonitorCap()
    {
        var source = Source("MainWindow.xaml");
        Assert.Contains("fps.Header = InputHeader(\"MaximumFPS\")", source);
        Assert.Equal("Maximum capture FPS", RedrainCast.Core.EnglishCatalog.Get("MaximumFPS"));
        Assert.Equal("Uncapped (default)", RedrainCast.Core.EnglishCatalog.Get("UncappedDefault"));
        Assert.DoesNotContain("this receiver supports up to 60", source);
        Assert.Contains("CaptureSettings.FpsPresets.Append(settings.MaxFps)", source);
        Assert.DoesNotContain("Capture, decode and display performance", source);
        Assert.Contains("SelectedValuePath = nameof(ComboBoxItem.Tag)", source);
        Assert.Contains("fps.SelectedValue is int cap ? cap : null", source);
        Assert.Contains("item.Tag is 0 ? L(\"UncappedDefault\")", source);
        Assert.Contains("defaultFps != 0", source);
    }

    [Fact]
    public void SharingSettingsProbeIsIsolatedAndReusesTheFullUiTestAssertionsWithoutInputInjection()
    {
        var app = Source("App.xaml");
        Assert.Contains("\"--sharing-settings-self-test\"", app);
        Assert.Contains("sharingSettingsIndex >= 0 ? sharingSettingsIndex : testIndex", app);
        Assert.Contains("window.RunSharingSettingsSelfTestAsync", app);
        var window = Source("MainWindow.xaml");
        Assert.Contains("if (!isolated) throw new InvalidOperationException(\"Sharing settings verification requires an isolated app.\")", window);
        Assert.Contains("checks.Add(await VerifySharingSettingsAsync(CancellationToken.None))", window);
        Assert.Contains("deadline.CancelAfter(TimeSpan.FromSeconds(20))", window);
        Assert.Contains("(\"inputInjected\", false)", window);
        Assert.Contains("(\"settingsPersisted\", false)", window);
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
