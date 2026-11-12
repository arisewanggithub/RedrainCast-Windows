using Xunit;

namespace RedrainCast.Tests;

public sealed class HdrDiagnosticWiringTests
{
    private static string Source(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".cs.txt"));

    [Fact]
    public void SharedContextLockCallbacksAreRootedUntilFinalNativeDeviceReleaseNotDecoderDisposal()
    {
        var synchronization = Source("D3D11ContextSynchronization");
        Assert.Contains("static readonly AVD3D11VADeviceContext_lock EnterCallback", synchronization);
        Assert.Contains("static readonly AVD3D11VADeviceContext_unlock LeaveCallback", synchronization);
        Assert.Contains("static readonly AVHWDeviceContext_free FreeCallback", synchronization);
        Assert.Contains("GCHandle.Alloc(multithread)", synchronization);
        Assert.Contains("hardware->free = FreeCallback;", synchronization);
        Assert.Contains("d3d->@lock = EnterCallback;", synchronization);
        Assert.Contains("d3d->unlock = LeaveCallback;", synchronization);
        Assert.Contains("Target(value).Enter()", synchronization);
        Assert.Contains("Target(value).Leave()", synchronization);
        var free = synchronization[synchronization.IndexOf("private static void Free(", StringComparison.Ordinal)..];
        Assert.Contains("((ID3D11Multithread)handle.Target!).Dispose();", free);
        Assert.Contains("handle.Free();", free);
        var decoder = Source("HardwareDecoder");
        Assert.True(decoder.IndexOf("D3D11ContextSynchronization.Attach(", StringComparison.Ordinal) <
            decoder.IndexOf("ffmpeg.av_hwdevice_ctx_init(", StringComparison.Ordinal));
        Assert.DoesNotContain("multithread.Enter()", decoder);
        var presenter = Source("GpuPresenter");
        Assert.Contains("multithread.SetMultithreadProtected(true);", presenter);
        var render = presenter[presenter.IndexOf("private bool RenderSynchronized(", StringComparison.Ordinal)..
            presenter.IndexOf("private bool Render(HardwareFrame", StringComparison.Ordinal)];
        Assert.Contains("return Render(frame, newFrame, request, inspect);", render);
        Assert.Contains("finally", render);
        Assert.Contains("multithread.Leave();", render);
        Assert.True(render.IndexOf("multithread.Enter();", StringComparison.Ordinal) <
            render.IndexOf("return Render(", StringComparison.Ordinal));
        Assert.True(render.IndexOf("multithread.Leave();", StringComparison.Ordinal) <
            render.IndexOf("diagnostic?.Invoke(\"context_leave\"", StringComparison.Ordinal));
    }

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
        Assert.Contains("presenter.Redraw(lastFrame.Surface);", playback);
        Assert.DoesNotContain("resizeRequested", playback);
        var presenter = Source("GpuPresenter");
        Assert.Contains("internal bool HasPendingRedraw => requests.HasPending;", presenter);
        Assert.Contains("TryRender(frame, true);", presenter);
        Assert.Contains("TryRender(frame, false);", presenter);
        Assert.Contains("requests.TryRender(frame.ColorFormat, request => RenderSynchronized(frame, newFrame, request,", presenter);
        Assert.Contains("diagnostic: diagnostic)", presenter);
        Assert.Contains("Prepare(frame.Width, frame.Height, request);", presenter);
        Assert.Contains("hdrRenderer.Draw(frame, buffer, request.Display);", presenter);
        Assert.Contains("Interlocked.CompareExchange(ref resizeFailure, error, null)", playback);
        Assert.Contains("Interlocked.CompareExchange(ref renderFailure, error, null)", playback);
        Assert.Contains("trace.StatisticsSample(session, sample, sampledAtMs, baselineReset)", playback);
        Assert.Contains("playback?.RecordStatisticsSample(session, sample, sampledAtMs, baselineReset)", host);
    }

    [Fact]
    public void UiRequestsNeverWaitForNativeWorkAndDisposalDetachesBeforeDisposingWake()
    {
        var playback = Source("VideoPlayback");
        var begin = playback.IndexOf("public void Resize(", StringComparison.Ordinal);
        var end = playback.IndexOf("public void FailDisplay(", begin, StringComparison.Ordinal);
        Assert.DoesNotContain("lock (", playback[begin..end]);
        Assert.Contains("            }\n            if (first) status(", playback.Replace("\r\n", "\n"));
        var cancel = playback.IndexOf("renderCancellation.Cancel();", StringComparison.Ordinal);
        var awaitRender = playback.IndexOf("await renderTask;", cancel, StringComparison.Ordinal);
        var detach = playback.IndexOf("await detach();", awaitRender, StringComparison.Ordinal);
        var disposeGate = playback.IndexOf("lock (decoderGate)", detach, StringComparison.Ordinal);
        var renderGate = playback.IndexOf("lock (renderGate)", disposeGate, StringComparison.Ordinal);
        var disposeWake = playback.IndexOf("renderWake.Dispose();", disposeGate, StringComparison.Ordinal);
        Assert.True(cancel >= 0 && awaitRender > cancel && detach > awaitRender &&
            disposeGate > detach && renderGate > disposeGate && disposeWake > renderGate);
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
