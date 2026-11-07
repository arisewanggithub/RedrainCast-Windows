using System.Runtime.InteropServices;
using System.Numerics;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using ScrcpyReceive.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace ScrcpyReceive.Media;

public readonly record struct PresentedVideoSample(long PtsUs, long Qpc, int RotationDegrees);
public sealed record PresentationDiagnosticSnapshot(PresentedVideoSample[] Samples, int Dropped);

public sealed class GpuPresenter : IDisposable
{
    public ID3D11Device Device { get; }
    public int FramesPerSecond { get; }
    public int ProcessorInputFramesPerSecond { get; private set; }
    public int ProcessorOutputFramesPerSecond { get; private set; }
    public int ProcessorConfigurations { get; private set; }
    public IDXGISwapChain1 SwapChain { get; }
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VideoDevice videoDevice;
    private readonly ID3D11VideoContext videoContext;
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
    private readonly object viewportGate = new();
    private (int Width, int Height, float ScaleX, float ScaleY) viewport = (640, 360, 1, 1);
    private long presentedFrames;
    private long redrawnFrames;
    private string stage = "idle";
    private long stageAt, busyAttempts, maximumRenderUs;
    private long readinessWaits, stillDrawing, occluded, presentAttempts;
    private readonly object samplesGate = new();
    private List<PresentedVideoSample>? presentationSamples;
    private int droppedSamples;
    private int lastPresentResult;
    public string Diagnostics => $"gpu stage={Volatile.Read(ref stage)}, stageMs={(Volatile.Read(ref stage) == "idle" ? 0 : Environment.TickCount64 - Interlocked.Read(ref stageAt))}, " +
        $"busy={Interlocked.Read(ref busyAttempts)}, readinessWaits={Interlocked.Read(ref readinessWaits)}, " +
        $"stillDrawing={Interlocked.Read(ref stillDrawing)}, occluded={Interlocked.Read(ref occluded)}, " +
        $"presentAttempts={Interlocked.Read(ref presentAttempts)}, lastPresentResult=0x{unchecked((uint)Volatile.Read(ref lastPresentResult)):X8}, " +
        $"slotHeld={presentationSlot.IsHeld}, renderMaxUs={Interlocked.Read(ref maximumRenderUs)}, " +
        $"requestedFps={FramesPerSecond}, processorInputFps={ProcessorInputFramesPerSecond}/1, processorOutputFps={ProcessorOutputFramesPerSecond}/1";
    public long PresentedFrames => Interlocked.Read(ref presentedFrames);
    public long RedrawnFrames => Interlocked.Read(ref redrawnFrames);
    public bool IsOccluded => Volatile.Read(ref lastPresentResult) == 0x087A0001;
    public bool SupportsVideoRotation => enumerator?.VideoProcessorCaps.FeatureCaps.HasFlag(VideoProcessorFeatureCaps.Rotation) == true;

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

    public GpuPresenter(int framesPerSecond)
    {
        if (framesPerSecond is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        FramesPerSecond = framesPerSecond;
        Device = D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        try
        {
            context = Device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
            using (var multithread = context.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);
            videoDevice = Device.QueryInterface<ID3D11VideoDevice>();
            videoContext = context.QueryInterface<ID3D11VideoContext>();
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
            scaledSwapChain.MaximumFrameLatency = 1;
            var handle = scaledSwapChain.FrameLatencyWaitableObject;
            if (handle == 0) throw new NotSupportedException("GPU presentation readiness is unavailable.");
            frameReady = new FrameReady(handle);
        }
        catch
        {
            frameReady?.Dispose();
            scaledSwapChain?.Dispose();
            SwapChain?.Dispose();
            videoContext?.Dispose();
            videoDevice?.Dispose();
            context?.Dispose();
            Device.Dispose();
            throw;
        }
    }

    public bool SetViewport(int width, int height, float scaleX, float scaleY)
    {
        if (width <= 0 || height <= 0) return false; // Hidden/minimized surfaces retain the last real viewport.
        if (width > 16384 || height > 16384) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(scaleX) || !float.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleX));
        lock (viewportGate)
        {
            var next = (width, height, scaleX, scaleY);
            if (viewport == next) return false;
            viewport = next;
            return true;
        }
    }

    public bool Present(HardwareFrame frame) => Render(frame, true);
    public bool Redraw(HardwareFrame frame) => Render(frame, false);

    public void Prepare(int width, int height)
    {
        (int Width, int Height, float ScaleX, float ScaleY) target;
        lock (viewportGate) target = viewport;
        var size = (target.Width, target.Height);
        if (size != output)
        {
            context.Flush();
            SwapChain.ResizeBuffers(2, (uint)size.Item1, (uint)size.Item2, Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
            presentationSlot.Reset();
            output = size;
            ResetProcessor();
        }
        scaledSwapChain.MatrixTransform = Matrix3x2.CreateScale(1 / target.ScaleX, 1 / target.ScaleY);
        if (input != (width, height))
        {
            input = (width, height);
            ResetProcessor();
        }
        EnsureProcessor();
    }

    private void Stage(string value)
    {
        Interlocked.Exchange(ref stageAt, Environment.TickCount64);
        Volatile.Write(ref stage, value);
    }

    internal void InspectForDiagnostics(HardwareFrame frame, Action<ID3D11Texture2D> inspect)
    {
        presentationSlot.TryAcquire(true);
        Render(frame, false, inspect);
    }

    private bool Render(HardwareFrame frame, bool newFrame, Action<ID3D11Texture2D>? inspect = null)
    {
        Device.DeviceRemovedReason.CheckError();
        if (!presentationSlot.TryAcquire(presentationSlot.IsHeld || frameReady.WaitOne(0)))
        {
            Interlocked.Increment(ref readinessWaits);
            Interlocked.Increment(ref busyAttempts);
            return false;
        }
        var begin = Stopwatch.GetTimestamp();
        Stage("prepare");
        try
        {
            Prepare(frame.Width, frame.Height);
            if (frame.RotationDegrees != 0 && !SupportsVideoRotation)
                throw new NotSupportedException("Hardware video-processor rotation is unavailable. Rotation will not be ignored or performed on the CPU.");
            if (SupportsVideoRotation)
                videoContext.VideoProcessorSetStreamRotation(processor!, 0, frame.RotationDegrees != 0,
                    (VideoProcessorRotation)(frame.RotationDegrees / 90));
            videoContext.VideoProcessorSetStreamColorSpace(processor!, 0,
                new() { YCbCr_Matrix = frame.ColorMatrix, Nominal_Range = frame.NominalRange });
            Marshal.AddRef(frame.Texture);
            using var source = new ID3D11Texture2D(frame.Texture);
            using var buffer = SwapChain.GetBuffer<ID3D11Texture2D>(0);
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
            inspect?.Invoke(buffer);
            Stage("present");
            Interlocked.Increment(ref presentAttempts);
            var result = SwapChain.Present(0, PresentFlags.DoNotWait);
            Volatile.Write(ref lastPresentResult, result.Code);
            if (result == Vortice.DXGI.ResultCode.WasStillDrawing || result.Code == 0x087A0001)
            {
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
            InputFrameRate = new((uint)FramesPerSecond, 1),
            OutputFrameRate = new((uint)FramesPerSecond, 1),
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
        ResetProcessor();
        frameReady.Dispose();
        scaledSwapChain.Dispose();
        SwapChain.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
        context.Dispose();
        Device.Dispose();
    }
}
