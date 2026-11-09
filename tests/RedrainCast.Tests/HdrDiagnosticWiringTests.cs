using Xunit;

namespace RedrainCast.Tests;

public sealed class HdrDiagnosticWiringTests
{
    private static string Source(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".cs.txt"));

    [Fact]
    public void DisplayMetadataFallbackUsesPlaybackWakePathWithoutFatalHealthLatch()
    {
        var host = Source("VideoHost");
        Assert.DoesNotContain("displayValid", host);
        Assert.DoesNotContain("HDR display detection failed", host);
        Assert.Contains("var next = HdrDisplayPolicy.Query(displayWindow);", host);
        Assert.Contains("if (playback is not null) playback.RefreshDisplay(next);", host);
        Assert.Contains("var next = displayPolicy.Capabilities(hdrVerified);", host);
        Assert.Contains("if (next == HdrCapabilities) return;", host);
        Assert.Contains("ownedPlayback.RefreshDisplay(displayPolicy);", host);
        Assert.Contains("GPU display policy update failed:", host);
        var playback = Source("VideoPlayback");
        Assert.Contains("if (!presenter.SetDisplayPolicy(policy)) return;", playback);
        Assert.Contains("renderWake.Set();", playback);
        Assert.Contains("else if (presenter.HasPendingRedraw && lastFrame is not null)", playback);
        Assert.Contains("presenter.Redraw(lastFrame);", playback);
        Assert.DoesNotContain("resizeRequested", playback);
        var presenter = Source("GpuPresenter");
        Assert.Contains("internal bool HasPendingRedraw => requests.HasPending;", presenter);
        Assert.Contains("requests.TryRender(frame.ColorFormat, request => Render(frame, true, request));", presenter);
        Assert.Contains("requests.TryRender(frame.ColorFormat, request => Render(frame, false, request));", presenter);
        Assert.Contains("Prepare(frame.Width, frame.Height, request);", presenter);
        Assert.Contains("hdrRenderer.Draw(frame, buffer, request.Display);", presenter);
        Assert.Contains("Interlocked.CompareExchange(ref resizeFailure, error, null)", playback);
        Assert.Contains("Interlocked.CompareExchange(ref renderFailure, error, null)", playback);
    }

    [Fact]
    public void UiRequestsNeverWaitForNativeWorkAndDisposalDetachesBeforeDisposingWake()
    {
        var playback = Source("VideoPlayback");
        var begin = playback.IndexOf("public void Resize(", StringComparison.Ordinal);
        var end = playback.IndexOf("public void FailDisplay(", begin, StringComparison.Ordinal);
        Assert.DoesNotContain("nativeGate", playback[begin..end]);
        var renderExit = playback.IndexOf("finally { Monitor.Exit(nativeGate); }", StringComparison.Ordinal);
        var notify = playback.IndexOf("if (first) status(", renderExit, StringComparison.Ordinal);
        Assert.True(renderExit >= 0 && notify > renderExit);
        var cancel = playback.IndexOf("renderCancellation.Cancel();", StringComparison.Ordinal);
        var awaitRender = playback.IndexOf("await renderTask;", cancel, StringComparison.Ordinal);
        var detach = playback.IndexOf("await detach();", awaitRender, StringComparison.Ordinal);
        var disposeGate = playback.IndexOf("lock (nativeGate)", detach, StringComparison.Ordinal);
        var disposeWake = playback.IndexOf("renderWake.Dispose();", disposeGate, StringComparison.Ordinal);
        Assert.True(cancel >= 0 && awaitRender > cancel && detach > awaitRender &&
            disposeGate > detach && disposeWake > disposeGate);
    }

    [Fact]
    public void HdrDiagnosticIsExplicitAndDoesNotExpandLegacyEncoderWorkload()
    {
        var app = Source("App.xaml");
        Assert.Contains("\"--hdr-self-test\"", app);
        Assert.Contains("MainWindow.RunHdrSelfTestAsync", app);
        Assert.Contains("--hdr-self-test requires a report path.", app);
        Assert.Contains("Environment.ExitCode = await MainWindow.RunHdrSelfTestAsync(arguments[hdrIndex + 1]) ? 0 : 1", app);
        var legacy = Source("ColorSelfTest");
        Assert.Contains("frameCount: includeDisplayColorProbe ? 30 : 600", legacy);
        Assert.DoesNotContain("HdrDiagnostics", legacy);
        Assert.DoesNotContain("EncodeHdrColorProbeAsync", legacy);
    }

    [Fact]
    public void HdrDiagnosticUsesProductionHardwareAndTypedReportsWithExplicitLimitations()
    {
        var diagnostic = Source("HdrSelfTest");
        Assert.Contains("ColorDiagnostics.Run", diagnostic);
        Assert.Contains("RunHdrLoopbackAsync", diagnostic);
        Assert.Contains("MediaDiagnostics.EncodeHdrColorProbeAsync", diagnostic);
        Assert.Contains("HdrDiagnostics.ValidateHdrColorProbe", diagnostic);
        Assert.Contains("HdrDiagnostics.RunColorDiagnostics", diagnostic);
        Assert.Contains("HdrVideoProtocol.RequireAcknowledgement", diagnostic);
        Assert.Contains("VideoBitstream.ValidateConfiguration", diagnostic);
        Assert.Contains("CodecConfiguration.ValidateVideo", diagnostic);
        Assert.Contains("(\"physicalHdrPanelVerified\", false)", diagnostic);
        Assert.Contains("(\"androidInteropVerified\", false)", diagnostic);
        Assert.Contains("(\"videoPixelReadbackForDiagnosticsOnly\", true)", diagnostic);
        var json = Source("ReportJson");
        Assert.Contains("[JsonSerializable(typeof(HdrRenderDiagnosticReport))]", json);
        Assert.Contains("[JsonSerializable(typeof(HdrDecodedColorProbeReport))]", json);
        Assert.Contains("[JsonSerializable(typeof(HdrLoopbackReport[]))]", json);
        Assert.DoesNotContain("GetType(", json);
    }

    [Fact]
    public void HdrLoopbackExercisesNativePlaybackBothTransportsAndCleanupWithoutDiscovery()
    {
        var loopback = Source("HdrLoopbackSelfTest");
        Assert.Contains("new SenderSession().RunAsync", loopback);
        Assert.Contains("new ReceiverServer", loopback);
        Assert.Contains("bindAddress: IPAddress.Loopback", loopback);
        Assert.Contains("receiver.Start(advertise: false)", loopback);
        Assert.Contains("new VideoPlayback", loopback);
        Assert.Contains("new HardwareDecoder", loopback);
        Assert.Contains("new GpuPresenter", loopback);
        Assert.Contains("Recovery COMPLETE: track=1, generation=2,", loopback);
        Assert.Contains("!playback.HasRecoveredOutput", loopback);
        Assert.Contains("inner.LastPresentedPtsUs, 16, 1", loopback);
        Assert.Contains("new(configuration.Generation, inner.DecodedFrames, PresentedFrames)", loopback);
        Assert.Contains("progress with { KeyPtsUs = ptsUs }", loopback);
        Assert.Contains("HdrVideoProtocol.Format(packet.Object()) != source.ColorFormat", loopback);
        Assert.Contains("!source.Disposed || !active.Disposed", loopback);
    }
}
