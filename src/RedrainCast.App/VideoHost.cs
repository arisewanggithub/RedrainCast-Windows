using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RedrainCast.Receive.Core;
using RedrainCast.Receive.Media;
using RedrainCast.Protocol;

namespace RedrainCast.App;

internal sealed class VideoHost : IPlaybackFactory, IDisposable
{
    private readonly SwapChainPanel panel;
    private readonly DispatcherQueue dispatcher;
    private readonly Action<string> status;
    private GpuPresenter? active;
    private VideoPlayback? playback;
    private XamlRoot? root;
    private nint displayWindow;
    private readonly WindowSubclass windowSubclass;
    private int displayRefreshQueued;
    private const nuint DisplaySubclassId = 0x52434844;
    private bool disposed;
    private bool hdrVerified;
    private HdrDisplayPolicy displayPolicy = HdrDisplayPolicy.MetadataUnavailable("The UI-owned display window is not available.");
    private HdrReceiverCapabilities hdrCapabilities = HdrReceiverCapabilities.None;
    public HdrReceiverCapabilities HdrCapabilities => Volatile.Read(ref hdrCapabilities);
    public event Action? HdrCapabilitiesChanged;
    public long PresentedFrames => active?.PresentedFrames ?? 0;
    public int RequestedFramesPerSecond => active?.FramesPerSecond ?? 0;
    public int ProcessorInputFramesPerSecond => active?.ProcessorInputFramesPerSecond ?? 0;
    public int ProcessorOutputFramesPerSecond => active?.ProcessorOutputFramesPerSecond ?? 0;
    public int ProcessorConfigurations => active?.ProcessorConfigurations ?? 0;
    internal bool CollectPresentationSamples { get; set; }
    internal PresentationDiagnosticSnapshot PresentationSnapshot() =>
        (active ?? throw new InvalidOperationException("No diagnostic presenter is attached.")).PresentationSnapshot();
    internal void CheckDiagnosticGpuBudget() => RedrainCast.Send.Media.MediaDiagnostics.CheckGpuBudget(
        (active ?? throw new InvalidOperationException("No diagnostic video device is attached.")).Device);
    public long RedrawnFrames => active?.RedrawnFrames ?? 0;
    public long PlayedAudioFrames => playback?.PlayedAudioFrames ?? 0;
    public long? FirstRecoverySourceAudioPtsUs => playback?.FirstRecoverySourceAudioPtsUs;
    public string PlaybackDiagnostics => playback?.Diagnostics ?? "";
    public long MaximumAvSkewUs => playback?.MaximumAvSkewUs ?? 0;
    public int AvMeasurements => playback?.AvMeasurements ?? 0;
    public long MaximumSteadySkewUs => playback?.MaximumSteadySkewUs ?? 0;
    public AvTimingSample? LastAvTiming => playback?.LastAvTiming;
    public AvTimingSample? MaximumAvTiming => playback?.MaximumAvTiming;
    public long PlaybackEpoch { get; private set; }
    public long DecodedFrames => playback?.DecodedFrames ?? 0;
    public long SkippedFrames => playback?.SkippedFrames ?? 0;
    public long LastPresentedPtsUs => playback?.LastPresentedPtsUs ?? 0;
    public int LastPresentedRotationDegrees => playback?.LastPresentedRotationDegrees ?? 0;
    public string DecoderDiagnostics => playback?.DecoderDiagnostics ?? "";
    public string PresentationDiagnostics => active?.Diagnostics ?? "";
    public int HardwareSurfaceCount => playback?.HardwareSurfaceCount ?? 0;
    public int MinimumHardwareSurfaces => playback?.MinimumHardwareSurfaces ?? 0;
    public (long Silence, long Dropped) AudioRecovery => playback?.AudioRecovery ?? (0, 0);
    public int AudioTargetFrames => playback?.AudioTargetFrames ?? 0;
    public bool KeepsDisplayAwake => playback?.KeepsDisplayAwake ?? false;
    public bool IsAttached => active is not null;
    public bool IsPlaying => PresentedFrames > 0;
    public bool HasDecodedVideo => DecodedFrames > 0;
    public bool IsPresentationSuspended => playback?.IsPresentationSuspended ?? false;
    private readonly Lazy<bool> rotationSupport = new(GpuPresenter.ProbeVideoRotation);
    public bool SupportsVideoRotation => rotationSupport.Value;
    public bool SupportsStreamRecovery => true;
    public RedrainCast.Protocol.ReceiverOptionCapabilities ReceiverOptions => RedrainCast.Protocol.ReceiverOptionCapabilities.Implemented;
    public CastRequest? StatisticsRequest { get; private set; }
    public PlaybackStatistics? Statistics => playback?.Statistics(PlaybackEpoch);
    public void RecordStatisticsSample(long session, PlaybackStatistics sample, long sampledAtMs, bool baselineReset) =>
        playback?.RecordStatisticsSample(session, sample, sampledAtMs, baselineReset);
    public event Action? StatisticsAvailabilityChanged;
    public string Description => "Hardware-only H.264, HEVC Main/Main10 HDR10 and AV1 Profile 0 over custom TCP with PCM/AAC-LC/Opus audio. HDR10 availability is hardware-verified; local output uses native scRGB on HDR displays or GPU BT.2390 SDR mapping. Immediate video and adaptive 50-150 ms audio buffering. Auto orientation, uncapped or 1 to 240 FPS requests; actual decode/display performance depends on codec, resolution and hardware. Brief audio gaps remain possible.";

    public VideoHost(SwapChainPanel panel, Action<string> status)
    {
        this.panel = panel;
        this.status = status;
        dispatcher = panel.DispatcherQueue;
        windowSubclass = DisplayWindowMessage;
        panel.SizeChanged += SizeChanged;
        panel.Loaded += Loaded;
        panel.CompositionScaleChanged += CompositionScaleChanged;
        _ = ProbeHdrAsync();
    }

    private async Task ProbeHdrAsync()
    {
        string diagnostic;
        var verified = false;
        try
        {
            diagnostic = await Task.Run(HdrDiagnostics.Run);
            verified = true;
        }
        catch (Exception error) { diagnostic = $"HDR receive disabled: {error.Message}"; }
        await OnUiAsync(() =>
        {
            if (disposed) return;
            hdrVerified = verified;
            status(diagnostic);
            UpdateHdrCapabilities();
        });
    }

    public void SetDisplayWindow(nint hwnd)
    {
        if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("Display window must be set on its UI thread.");
        if (displayWindow != 0) RemoveWindowSubclass(displayWindow, windowSubclass, DisplaySubclassId);
        displayWindow = hwnd;
        if (hwnd != 0 && !SetWindowSubclass(hwnd, windowSubclass, DisplaySubclassId, 0))
            throw new InvalidOperationException("Native display-notification window hook could not be installed.");
        RefreshDisplay();
    }

    private nint DisplayWindowMessage(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data)
    {
        if (message is 0x0047 or 0x007e or 0x001a && Interlocked.Exchange(ref displayRefreshQueued, 1) == 0)
        {
            if (!dispatcher.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref displayRefreshQueued, 0);
                RefreshDisplay();
            })) Interlocked.Exchange(ref displayRefreshQueued, 0);
        }
        if (message == 0x0082)
        {
            RemoveWindowSubclass(hwnd, windowSubclass, DisplaySubclassId);
            if (displayWindow == hwnd) displayWindow = 0;
        }
        return DefSubclassProc(hwnd, message, wparam, lparam);
    }

    public void RefreshDisplay()
    {
        if (!dispatcher.HasThreadAccess) throw new InvalidOperationException("Display policy must be refreshed on its UI thread.");
        if (disposed) return;
        var next = HdrDisplayPolicy.Query(displayWindow);
        if (next != displayPolicy)
        {
            displayPolicy = next;
            try
            {
                if (playback is not null) playback.RefreshDisplay(next);
                else active?.SetDisplayPolicy(next);
            }
            catch (Exception error)
            {
                status($"GPU display policy update failed: {error.Message}");
                playback?.FailDisplay(error);
                return;
            }
            status(next.Diagnostic);
        }
        UpdateHdrCapabilities();
    }

    private void UpdateHdrCapabilities()
    {
        var next = displayPolicy.Capabilities(hdrVerified);
        if (next == HdrCapabilities) return;
        Volatile.Write(ref hdrCapabilities, next);
        HdrCapabilitiesChanged?.Invoke();
    }

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (root is not null) root.Changed -= RootChanged;
        root = panel.XamlRoot;
        root.Changed += RootChanged;
        Resize();
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();
    private void SizeChanged(object sender, SizeChangedEventArgs args) => Resize();
    private void CompositionScaleChanged(SwapChainPanel sender, object args) => Resize();
    private void Resize()
    {
        var width = (int)Math.Ceiling(panel.ActualWidth * panel.CompositionScaleX);
        var height = (int)Math.Ceiling(panel.ActualHeight * panel.CompositionScaleY);
        if (playback is not null) playback.Resize(width, height, panel.CompositionScaleX, panel.CompositionScaleY);
        else active?.SetViewport(width, height, panel.CompositionScaleX, panel.CompositionScaleY);
    }

    public async ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken cancellationToken)
    {
        VideoPlayback.Validate(request);
        GpuPresenter? presenter = null;
        HardwareDecoder? decoder = null;
        AudioPlayback? audio = null;
        VideoPlayback? preparedPlayback = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.ColorFormat.Validate(request.VideoCodec);
            if (request.ColorFormat.Hdr10 && !HdrCapabilities.CanReceiveHdr10)
                throw new NotSupportedException("Native Main10/P010 decode and local HDR renderer have not passed hardware preflight.");
            presenter = new GpuPresenter(request.VideoProcessingFramesPerSecond, request.ColorFormat);
            if (CollectPresentationSamples) presenter.EnablePresentationDiagnostics();
            decoder = new HardwareDecoder(presenter.Device, request.VideoCodec, request.ColorFormat);
            var clock = new PlaybackClock(audioExpected: request.AudioCodec is not null);
            if (request.AudioCodec is not null) audio = new AudioPlayback(request.AudioCodec, clock);
            var ownedPresenter = presenter;
            await OnUiAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (active is not null) throw new InvalidOperationException("A video surface is already attached.");
                if (request.ColorFormat.Hdr10 && !HdrCapabilities.CanReceiveHdr10)
                    throw new NotSupportedException("HDR display policy changed during playback preflight.");
                Attach(panel, ownedPresenter.SwapChain.NativePointer);
                active = ownedPresenter;
                ownedPresenter.SetDisplayPolicy(displayPolicy);
                Resize();
            });
            cancellationToken.ThrowIfCancellationRequested();
            var ownedPlayback = new VideoPlayback(presenter, decoder, () => new ValueTask(OnUiAsync(() =>
            {
                if (active != ownedPresenter) throw new InvalidOperationException("Video surface ownership changed.");
                try { Attach(panel, 0); }
                finally
                {
                    playback = null;
                    active = null;
                    StatisticsRequest = null;
                    StatisticsAvailabilityChanged?.Invoke();
                    status("Playback ended.");
                }
            })), detail => new ValueTask(OnUiAsync(() =>
            {
                if (active == ownedPresenter) status(detail);
            })), audio, request.VideoCodec, request.ColorFormat, clock);
            preparedPlayback = ownedPlayback;
            await OnUiAsync(() =>
            {
                playback = ownedPlayback;
                ownedPlayback.RefreshDisplay(displayPolicy);
                PlaybackEpoch++;
                StatisticsRequest = request.EffectiveOptions.PlaybackStats ? request : null;
                StatisticsAvailabilityChanged?.Invoke();
                Resize();
            });
            ownedPlayback.CheckHealth();
            return ownedPlayback;
        }
        catch (Exception error)
        {
            try
            {
                if (preparedPlayback is not null)
                {
                    presenter = null; decoder = null; audio = null;
                    await preparedPlayback.DisposeAsync();
                }
                else if (presenter is not null)
                {
                    var failedPresenter = presenter;
                    await OnUiAsync(() =>
                    {
                        if (active != failedPresenter) return;
                        Attach(panel, 0);
                        playback = null;
                        active = null;
                    });
                }
            }
            catch (Exception cleanupError) { throw new AggregateException("Hardware preflight and detach failed.", error, cleanupError); }
            finally
            {
                try { if (audio is not null) await audio.DisposeAsync(); }
                finally { decoder?.Dispose(); presenter?.Dispose(); }
            }
            if (error is OperationCanceledException) throw;
            throw new ProtocolException($"Playback preflight failed: {error.Message}", "UNSUPPORTED_CONFIG");
        }
    }

    private Task OnUiAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        })) completion.SetException(new InvalidOperationException("Video UI dispatcher is unavailable."));
        return completion.Task;
    }

    private static unsafe void Attach(SwapChainPanel panel, nint swapChain)
    {
        var inspectable = WinRT.MarshalInspectable<object>.FromManaged(panel);
        nint native = 0;
        try
        {
            var id = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in id, out native));
            var set = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)native)[3];
            Marshal.ThrowExceptionForHR(set(native, swapChain));
        }
        finally
        {
            if (native != 0) Marshal.Release(native);
            Marshal.Release(inspectable);
        }
    }

    public void Dispose()
    {
        disposed = true;
        if (displayWindow != 0) RemoveWindowSubclass(displayWindow, windowSubclass, DisplaySubclassId);
        displayWindow = 0;
        panel.SizeChanged -= SizeChanged;
        panel.Loaded -= Loaded;
        panel.CompositionScaleChanged -= CompositionScaleChanged;
        if (root is not null) root.Changed -= RootChanged;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowSubclass(nint hwnd, uint message, nuint wparam, nint lparam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, WindowSubclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, WindowSubclass callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wparam, nint lparam);
}
