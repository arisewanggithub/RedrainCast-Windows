using System.Runtime.InteropServices;
using System.Numerics;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using RedrainCast.Receive.Core;
using RedrainCast.Protocol;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace RedrainCast.Receive.Media;

public readonly record struct PresentedVideoSample(long PtsUs, long Qpc, int RotationDegrees);
public sealed record PresentationDiagnosticSnapshot(PresentedVideoSample[] Samples, int Dropped);

public sealed class GpuPresenter : IDisposable
{
    public ID3D11Device Device { get; }
    public bool IsHdr => color.Hdr10;
    internal float SdrPeakNits => hdrRenderer?.SdrPeakNits ?? 0;
    public int FramesPerSecond { get; }
    // D3D11 requires positive descriptor rates; this hint never schedules or caps frames.
    public int NominalFramesPerSecond => FramesPerSecond == 0 ? 60 : FramesPerSecond;
    public int ProcessorInputFramesPerSecond { get; private set; }
    public int ProcessorOutputFramesPerSecond { get; private set; }
    public int ProcessorConfigurations { get; private set; }
    public IDXGISwapChain1 SwapChain { get; }
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11Multithread multithread;
    private readonly ID3D11VideoDevice videoDevice;
    private readonly ID3D11VideoContext videoContext;
    private readonly HdrVideoFormat color;
    private readonly HdrRenderer? hdrRenderer;
    private HdrDisplayPolicy appliedDisplay = HdrDisplayPolicy.Sdr;
    private readonly PresentationRequestState requests = new();
    internal bool HasPendingRedraw => requests.HasPending;
    public VideoPresentationStatistics? Statistics => requests.Statistics;
    public void ResetStatistics() => requests.ResetStatistics();
    private readonly IDXGISwapChain2 scaledSwapChain;
    private readonly WaitHandle frameReady;
    private readonly PresentationSlot presentationSlot = new();
    private sealed class FrameReady : WaitHandle
    {
        public FrameReady(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, true);
    }
    private ID3D11VideoProcessorEnumerator? enumerator;
    private ID3D11VideoProcessor? processor;
    private (int Width, int Height) input;
    private (int Width, int Height) output = (640, 360);
    private long presentedFrames;
    private long redrawnFrames;
    private string stage = "idle";
    private long stageAt, busyAttempts, maximumRenderUs;
    private long readinessWaits, stillDrawing, occluded, presentAttempts;
    private readonly object samplesGate = new();
    private List<PresentedVideoSample>? presentationSamples;
    private int droppedSamples;
    private int lastPresentResult;
    public string Diagnostics
    {
        get
        {
            var successfulDisplay = requests.Successful?.Display;
            var nativeHdr = color.Hdr10 && successfulDisplay?.NativeHdr == true;
            var successfulFormat = nativeHdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
            return $"gpu stage={Volatile.Read(ref stage)}, stageMs={(Volatile.Read(ref stage) == "idle" ? 0 : Environment.TickCount64 - Interlocked.Read(ref stageAt))}, " +
        $"busy={Interlocked.Read(ref busyAttempts)}, readinessWaits={Interlocked.Read(ref readinessWaits)}, " +
        $"stillDrawing={Interlocked.Read(ref stillDrawing)}, occluded={Interlocked.Read(ref occluded)}, " +
        $"presentAttempts={Interlocked.Read(ref presentAttempts)}, lastPresentResult=0x{unchecked((uint)Volatile.Read(ref lastPresentResult)):X8}, " +
        $"slotHeld={presentationSlot.IsHeld}, renderMaxUs={Interlocked.Read(ref maximumRenderUs)}, " +
        $"requestedFps={(FramesPerSecond == 0 ? "Uncapped" : FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture))}, " +
        $"processorNominalInputFps={ProcessorInputFramesPerSecond}/1, processorNominalOutputFps={ProcessorOutputFramesPerSecond}/1, " +
        $"hdr10={color.Hdr10}, outputFormat={(successfulDisplay is null ? "unpresented" : $"{successfulFormat}")}, nativeHdr={nativeHdr}, " +
        $"displayPeakNits={(successfulDisplay?.NativeHdr == true ? $"{successfulDisplay.PeakNits}" : "unknown")}, outputPeakNits={successfulDisplay?.PeakNits}, " +
        $"displayPolicy={successfulDisplay?.Diagnostic ?? "unpresented"}, " +
        $"sdrContentPeakNits={hdrRenderer?.SdrPeakNits ?? 0}, sdrPeakPolicy=shared-HdrLuminance-percentile-temporal, nativePeakPolicy=GPU-maximum";
        }
    }
    public long PresentedFrames => Interlocked.Read(ref presentedFrames);
    public long RedrawnFrames => Interlocked.Read(ref redrawnFrames);
    public bool IsOccluded => Volatile.Read(ref lastPresentResult) == 0x087A0001;
    public bool SupportsVideoRotation => color.Hdr10 || enumerator?.VideoProcessorCaps.FeatureCaps.HasFlag(VideoProcessorFeatureCaps.Rotation) == true;

    public void EnablePresentationDiagnostics()
    {
        lock (samplesGate)
        {
            if (PresentedFrames != 0 || presentationSamples is not null)
                throw new InvalidOperationException("Presentation diagnostics must be enabled once before playback.");
            Volatile.Write(ref presentationSamples, new List<PresentedVideoSample>(2048));
        }
    }

    public PresentationDiagnosticSnapshot PresentationSnapshot()
    {
        lock (samplesGate) return new(presentationSamples?.ToArray() ?? [], droppedSamples);
    }

    public static bool ProbeVideoRotation()
    {
        using var presenter = new GpuPresenter(60);
        presenter.Prepare(640, 360);
        return presenter.SupportsVideoRotation;
    }

    public GpuPresenter(int framesPerSecond, HdrVideoFormat? color = null)
    {
        this.color = color ?? HdrVideoFormat.Sdr;
        if (framesPerSecond is < 0 or > 240) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        FramesPerSecond = framesPerSecond;
        Device = D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        try
        {
            context = Device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
            multithread = context.QueryInterface<ID3D11Multithread>();
            multithread.SetMultithreadProtected(true);
            videoDevice = Device.QueryInterface<ID3D11VideoDevice>();
            videoContext = context.QueryInterface<ID3D11VideoContext>();
            if (this.color.Hdr10) hdrRenderer = new HdrRenderer(Device);
            using var dxgi = Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            SwapChain = factory.CreateSwapChainForComposition(Device, new SwapChainDescription1
            {
                Width = 640,
                Height = 360,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.FrameLatencyWaitableObject
            });
            scaledSwapChain = SwapChain.QueryInterface<IDXGISwapChain2>();
            using (var colorChain = SwapChain.QueryInterface<IDXGISwapChain3>())
                colorChain.SetColorSpace1(ColorSpaceType.RgbFullG22NoneP709);
            scaledSwapChain.MaximumFrameLatency = 1;
            var handle = scaledSwapChain.FrameLatencyWaitableObject;
            if (handle == 0) throw new NotSupportedException("GPU presentation readiness is unavailable.");
            frameReady = new FrameReady(handle);
        }
        catch
        {
            hdrRenderer?.Dispose();
            frameReady?.Dispose();
            scaledSwapChain?.Dispose();
            SwapChain?.Dispose();
            videoContext?.Dispose();
            videoDevice?.Dispose();
            context?.Dispose();
            multithread?.Dispose();
            Device.Dispose();
            throw;
        }
    }

    public bool SetViewport(int width, int height, float scaleX, float scaleY) =>
        requests.SetViewport(width, height, scaleX, scaleY);

    internal string LastRenderOutcome { get; private set; } = "request_rejected";
    public bool Present(HardwareFrame frame) =>
        TryRender(frame, true);
    public bool Redraw(HardwareFrame frame) =>
        TryRender(frame, false);

    internal bool Present(HardwareFrame frame, Action<string, string?> diagnostic) =>
        TryRender(frame, true, diagnostic);
    internal bool Redraw(HardwareFrame frame, Action<string, string?> diagnostic) =>
        TryRender(frame, false, diagnostic);

    internal int WaitForRender(WaitHandle publication, WaitHandle cancellation, int timeout,
        bool readinessEligible, Action? signalObserved) =>
        VideoRenderWait.Wait(publication, cancellation, frameReady, presentationSlot, timeout,
            readinessEligible, signalObserved);

    private bool TryRender(HardwareFrame frame, bool newFrame, Action<string, string?>? diagnostic = null)
    {
        LastRenderOutcome = "request_rejected";
        return requests.TryRender(frame.ColorFormat, request => RenderSynchronized(frame, newFrame, request,
            diagnostic: diagnostic));
    }

    public bool SetDisplayPolicy(HdrDisplayPolicy policy) => requests.SetDisplayPolicy(policy);

    public void Prepare(int width, int height)
    {
        multithread.Enter();
        try { Prepare(width, height, requests.Desired); }
        finally { multithread.Leave(); }
    }

    private void Prepare(int width, int height, PresentationRequest target)
    {
        var policy = target.Display;
        var size = (target.Width, target.Height);
        var changedColor = color.Hdr10 && appliedDisplay.NativeHdr != policy.NativeHdr;
        if (size != output || changedColor)
        {
            context.Flush();
            var format = color.Hdr10 && policy.NativeHdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
            SwapChain.ResizeBuffers(2, (uint)size.Item1, (uint)size.Item2, format, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
            using var colorChain = SwapChain.QueryInterface<IDXGISwapChain3>();
            var colorSpace = color.Hdr10 && policy.NativeHdr
                ? ColorSpaceType.RgbFullG10NoneP709 : ColorSpaceType.RgbFullG22NoneP709;
            if (!colorChain.CheckColorSpaceSupport(colorSpace).HasFlag(SwapChainColorSpaceSupportFlags.Present))
                throw new NotSupportedException($"Composition swapchain cannot present {colorSpace}.");
            colorChain.SetColorSpace1(colorSpace);
            presentationSlot.Reset();
            output = size;
            ResetProcessor();
        }
        scaledSwapChain.MatrixTransform = Matrix3x2.CreateScale(1 / target.ScaleX, 1 / target.ScaleY);
        if (changedColor && !policy.NativeHdr) hdrRenderer?.ResetLuminance();
        appliedDisplay = policy;
        if (input != (width, height))
        {
            hdrRenderer?.ResetLuminance();
            input = (width, height);
            ResetProcessor();
        }
        if (!color.Hdr10) EnsureProcessor();
    }

    private void Stage(string value)
    {
        Interlocked.Exchange(ref stageAt, Environment.TickCount64);
        Volatile.Write(ref stage, value);
    }

    internal void InspectForDiagnostics(HardwareFrame frame, Action<ID3D11Texture2D> inspect)
    {
        presentationSlot.TryAcquire(true);
        requests.TryRender(frame.ColorFormat, request => RenderSynchronized(frame, false, request, inspect));
    }

    private bool RenderSynchronized(HardwareFrame frame, bool newFrame, PresentationRequest request,
        Action<ID3D11Texture2D>? inspect = null, Action<string, string?>? diagnostic = null)
    {
        Device.DeviceRemovedReason.CheckError();
        var held = presentationSlot.IsHeld;
        var ready = held || frameReady.WaitOne(0);
        var acquired = presentationSlot.TryAcquire(ready);
        diagnostic?.Invoke("readiness_check", held ? "held" : ready ? "signal" : "not_ready");
        if (!acquired)
        {
            LastRenderOutcome = "not_ready";
            Interlocked.Increment(ref readinessWaits);
            Interlocked.Increment(ref busyAttempts);
            return false;
        }
        // FFmpeg uses this same recursive device lock only around its GPU-context operations.
        // Keep the entire HDR/video-processor state transaction atomic, not the decode call.
        diagnostic?.Invoke("context_wait", null);
        multithread.Enter();
        try
        {
            diagnostic?.Invoke("context_enter", null);
            return Render(frame, newFrame, request, inspect);
        }
        finally
        {
            multithread.Leave();
            diagnostic?.Invoke("context_leave", null);
        }
    }

    private bool Render(HardwareFrame frame, bool newFrame, PresentationRequest request, Action<ID3D11Texture2D>? inspect)
    {
        var begin = Stopwatch.GetTimestamp();
        Stage("prepare");
        try
        {
            Prepare(frame.Width, frame.Height, request);
            if (frame.ColorFormat != color) throw new ProtocolException("Presenter color differs from the decoded surface.");
            using var buffer = SwapChain.GetBuffer<ID3D11Texture2D>(0);
            if (hdrRenderer is not null)
            {
                Stage("hdr-shader");
                hdrRenderer.Draw(frame, buffer, request.Display);
            }
            else
            {
                if (frame.RotationDegrees != 0 && !SupportsVideoRotation)
                    throw new NotSupportedException("Hardware video-processor rotation is unavailable. Rotation will not be ignored or performed on the CPU.");
                if (SupportsVideoRotation)
                    videoContext.VideoProcessorSetStreamRotation(processor!, 0, frame.RotationDegrees != 0,
                        (VideoProcessorRotation)(frame.RotationDegrees / 90));
                videoContext.VideoProcessorSetStreamColorSpace(processor!, 0,
                    new() { YCbCr_Matrix = frame.ColorMatrix, Nominal_Range = frame.NominalRange });
                Marshal.AddRef(frame.Texture);
                using var source = new ID3D11Texture2D(frame.Texture);
                using var sourceView = videoDevice.CreateVideoProcessorInputView(source, enumerator!, new()
                {
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new() { MipSlice = 0, ArraySlice = frame.ArraySlice }
                });
                using var outputView = videoDevice.CreateVideoProcessorOutputView(buffer, enumerator!, new()
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new() { MipSlice = 0 }
                });
                var (x, y, width, height) = VideoLayout.Fit(input.Width, input.Height, frame.RotationDegrees, output.Width, output.Height);
                videoContext.VideoProcessorSetStreamSourceRect(processor!, 0, true, new RawRect(0, 0, input.Width, input.Height));
                videoContext.VideoProcessorSetStreamDestRect(processor!, 0, true, new RawRect(x, y, x + width, y + height));
                Stage("video-blt");
                videoContext.VideoProcessorBlt(processor!, outputView, 0,
                    [new VideoProcessorStream { Enable = true, InputSurface = sourceView }]).CheckError();
            }
            inspect?.Invoke(buffer);
            Stage("present");
            Interlocked.Increment(ref presentAttempts);
            var result = SwapChain.Present(0, PresentFlags.DoNotWait);
            Volatile.Write(ref lastPresentResult, result.Code);
            if (result == Vortice.DXGI.ResultCode.WasStillDrawing || result.Code == 0x087A0001)
            {
                LastRenderOutcome = result == Vortice.DXGI.ResultCode.WasStillDrawing ? "still_drawing" : "occluded";
                if (result == Vortice.DXGI.ResultCode.WasStillDrawing) Interlocked.Increment(ref stillDrawing);
                else Interlocked.Increment(ref occluded);
                Interlocked.Increment(ref busyAttempts);
                return false;
            }
            result.CheckError();
            presentationSlot.Submitted();
            Device.DeviceRemovedReason.CheckError();
            if (newFrame)
            {
                Interlocked.Increment(ref presentedFrames);
                if (Volatile.Read(ref presentationSamples) is { } samples)
                {
                    lock (samplesGate)
                    {
                        if (samples.Count < 2048) samples.Add(new(frame.PtsUs, Stopwatch.GetTimestamp(), frame.RotationDegrees));
                        else droppedSamples++;
                    }
                }
            }
            else Interlocked.Increment(ref redrawnFrames);
            LastRenderOutcome = newFrame ? "presented" : "redrawn";
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref maximumRenderUs, Math.Max(Interlocked.Read(ref maximumRenderUs), (long)Stopwatch.GetElapsedTime(begin).TotalMicroseconds));
            Stage("idle");
        }
    }

    private void EnsureProcessor()
    {
        if (processor is not null) return;
        var description = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new((uint)NominalFramesPerSecond, 1),
            OutputFrameRate = new((uint)NominalFramesPerSecond, 1),
            InputWidth = (uint)input.Width,
            InputHeight = (uint)input.Height,
            OutputWidth = (uint)output.Width,
            OutputHeight = (uint)output.Height,
            Usage = VideoUsage.PlaybackNormal
        };
        enumerator = videoDevice.CreateVideoProcessorEnumerator(description);
        if (!enumerator.CheckVideoProcessorFormat(Format.NV12).HasFlag(VideoProcessorFormatSupport.Input) ||
            !enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm).HasFlag(VideoProcessorFormatSupport.Output))
            throw new NotSupportedException("Hardware NV12 to BGRA presentation is unavailable; no CPU conversion.");
        processor = videoDevice.CreateVideoProcessor(enumerator, 0);
        ProcessorInputFramesPerSecond = checked((int)description.InputFrameRate.Numerator);
        ProcessorOutputFramesPerSecond = checked((int)description.OutputFrameRate.Numerator);
        ProcessorConfigurations++;
        videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
        videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
        videoContext.VideoProcessorSetStreamColorSpace(processor, 0, new() { YCbCr_Matrix = 1, Nominal_Range = 1 });
        videoContext.VideoProcessorSetOutputColorSpace(processor, new() { RGB_Range = 0 });
        videoContext.VideoProcessorSetOutputBackgroundColor(processor, false, new() { Rgba = new() { R = 0, G = 0, B = 0, A = 1 } });
        videoContext.VideoProcessorSetOutputTargetRect(processor, true, new RawRect(0, 0, output.Width, output.Height));
    }

    private void ResetProcessor()
    {
        processor?.Dispose();
        processor = null;
        enumerator?.Dispose();
        enumerator = null;
    }

    public void Dispose()
    {
        hdrRenderer?.Dispose();
        ResetProcessor();
        frameReady.Dispose();
        scaledSwapChain.Dispose();
        SwapChain.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
        context.Dispose();
        multithread.Dispose();
        Device.Dispose();
    }
}
