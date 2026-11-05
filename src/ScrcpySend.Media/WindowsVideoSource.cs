using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ScrcpySend.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace ScrcpySend.Media;

public sealed class WindowsVideoSource : IVideoSource
{
    public static CaptureSource SelectDefaultDisplay() => CaptureInterop.DefaultDisplay();

    private readonly CaptureSource source;
    private readonly Action<string>? diagnostic;
    private readonly GraphicsCaptureItem item;
    private readonly CaptureSettings settings;
    private readonly ID3D11Device device;
    private readonly IDirect3DDevice captureDevice;
    private readonly HardwareEncoder encoder;
    private readonly CancellationTokenSource lifetime = new();
    private Task? worker;
    private bool started;
    private bool disposed;
    private int capturedFrames;
    private int submittedFrames;
    private int encodedFrames;
    public string EncoderName => encoder.Name;
    public int Width => encoder.Width;
    public int Height => encoder.Height;
    public string Statistics => $"Captured {Volatile.Read(ref capturedFrames)}, submitted {Volatile.Read(ref submittedFrames)}, encoded {Volatile.Read(ref encodedFrames)}; output {Width}x{Height}.";

    public WindowsVideoSource(CaptureSource source, CaptureSettings settings, Action<string>? diagnostic = null)
    {
        settings.Validate();
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows graphics capture is unavailable.");
        this.source = source;
        this.diagnostic = diagnostic;
        item = source.Item;
        // Validate HDR metadata before negotiating a session with the receiver.
        _ = source.ReadColor();
        this.settings = settings;
        var (width, height) = settings.OutputSize(item.Size.Width, item.Size.Height);
        device = CreateHardwareDevice();
        try
        {
            captureDevice = CaptureInterop.CreateDevice(device);
            encoder = new HardwareEncoder(device, width, height, settings);
        }
        catch
        {
            captureDevice?.Dispose();
            device.Dispose();
            lifetime.Dispose();
            throw;
        }
    }

    internal static ID3D11Device CreateHardwareDevice()
    {
        var device = D3D11CreateDevice(DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
        try
        {
            using var multithread = device.ImmediateContext.QueryInterface<ID3D11Multithread>();
            multithread.SetMultithreadProtected(true);
            return device;
        }
        catch { device.Dispose(); throw; }
    }

    public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (started) throw new InvalidOperationException("Capture cannot be reused.");
        started = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var queue = new VideoQueue((settings.EnableAudio ? 7 : 8) * 1024 * 1024);
        var signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(64)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        worker = Task.Run(() => Capture(queue, signals.Writer, linked.Token), CancellationToken.None);
        try
        {
            await foreach (var _ in signals.Reader.ReadAllAsync(linked.Token))
            {
                if (!queue.TryDequeue(out var frame)) throw new InvalidOperationException("Capture queue signal mismatch.");
                yield return frame!;
            }
            await worker;
        }
        finally
        {
            linked.Cancel();
            await worker;
        }
    }

    private async Task Capture(VideoQueue queue, ChannelWriter<bool> signals, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var size = item.Size;
            var color = source.ReadColor();
            diagnostic?.Invoke($"Capture color: {(color.Hdr ? "HDR scRGB -> content-adaptive BT.2390 SDR" : "SDR direct")}; display white={color.SdrWhiteNits:F1} nits, display peak={color.PeakNits:F1} nits. HDR content peak is measured separately on the GPU.");
            var pixelFormat = color.Hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice,
                pixelFormat, 2, size);
            var progress = new CaptureProgress();
            // Notifications coalesce; raw capture frames are paced before encoding.
            void FrameArrived(Direct3D11CaptureFramePool sender, object args) => progress.Arrived();
            pool.FrameArrived += FrameArrived;
            using var session = pool.CreateCaptureSession(item);
            GpuProcessor? processor = null;
            ID3D11Texture2D? latest = null;
            void Closed(GraphicsCaptureItem sender, object args) => progress.Closed();
            item.Closed += Closed;
            try
            {
                processor = new GpuProcessor(device, size.Width, size.Height, Width, Height, settings.MaxFps, color.Hdr);
                token.ThrowIfCancellationRequested();
                encoder.Start();
                session.StartCapture();
                var pacer = new DeferredFramePacer(settings.MaxFps);
                using var wait = new CaptureWait(token);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var nextColorCheck = TimeSpan.FromMilliseconds(500);
                SizeInt32? pendingSize = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    progress.Check(clock.Elapsed);
                    if (clock.Elapsed >= nextColorCheck)
                    {
                        nextColorCheck = clock.Elapsed + TimeSpan.FromMilliseconds(500);
                        var nextColor = source.ReadColor();
                        if (nextColor != color)
                        {
                            var formatChanged = nextColor.Hdr != color.Hdr;
                            color = nextColor;
                            if (formatChanged)
                            {
                                pixelFormat = color.Hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
                                pendingSize ??= size;
                            }
                            diagnostic?.Invoke($"Capture color changed: HDR={color.Hdr}; white={color.SdrWhiteNits:F1} nits, peak={color.PeakNits:F1} nits. Format changed={formatChanged}.");
                        }
                    }
                    if (pendingSize is { } resized)
                    {
                        latest?.Dispose();
                        latest = null;
                        pacer.DiscardPending();
                        processor.Dispose();
                        processor = null;
                        pool.Recreate(captureDevice, pixelFormat, 2, resized);
                        processor = new GpuProcessor(device, resized.Width, resized.Height, Width, Height, settings.MaxFps, color.Hdr);
                        size = resized;
                        pendingSize = null;
                    }
                    foreach (var output in encoder.TakeOutput())
                    {
                        Interlocked.Increment(ref encodedFrames);
                        queue.Enqueue(output);
                        if (!signals.TryWrite(true)) throw new IOException("CONGESTION: video consumer is not keeping up.");
                        progress.Produced(clock.Elapsed);
                    }
                    if (!encoder.CanAcceptInput)
                    {
                        wait.Pause();
                        continue;
                    }
                    using var frame = pool.TryGetNextFrame();
                    if (frame == null)
                    {
                        if (pacer.HasPending)
                        {
                            var now = (long)((Int128)System.Diagnostics.Stopwatch.GetTimestamp() *
                                TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency);
                            if (pacer.TryTake(now, out var sourceTimestamp))
                            {
                                using var nv12 = processor.Convert(latest!, size.Width, size.Height, color);
                                encoder.Submit(nv12, sourceTimestamp);
                                progress.Submitted(clock.Elapsed);
                                Interlocked.Increment(ref submittedFrames);
                            }
                            else wait.Pause();
                        }
                        else if (submittedFrames == encodedFrames) await progress.WaitAsync(token);
                        else wait.Pause();
                        continue;
                    }
                    Interlocked.Increment(ref capturedFrames);
                    if (frame.ContentSize.Width <= 0 || frame.ContentSize.Height <= 0)
                        throw new IOException("Capture source has no visible content.");
                    if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
                    {
                        pendingSize = frame.ContentSize;
                        continue;
                    }
                    var timestamp = frame.SystemRelativeTime.Ticks;
                    using var captured = CaptureInterop.Texture(frame.Surface);
                    if (latest is null)
                    {
                        var description = captured.Description;
                        if (color.Hdr) description = new Texture2DDescription(Format.R16G16B16A16_Float,
                            description.Width, description.Height, 1, 1, BindFlags.ShaderResource);
                        latest = device.CreateTexture2D(description);
                    }
                    device.ImmediateContext.CopyResource(latest, captured);
                    pacer.Offer(timestamp);
                    var currentTime = (long)((Int128)System.Diagnostics.Stopwatch.GetTimestamp() *
                        TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency);
                    if (pacer.TryTake(currentTime, out var pendingTimestamp))
                    {
                        using var nv12 = processor.Convert(latest, size.Width, size.Height, color);
                        encoder.Submit(nv12, pendingTimestamp);
                        progress.Submitted(clock.Elapsed);
                        Interlocked.Increment(ref submittedFrames);
                    }
                }
            }
            finally
            {
                item.Closed -= Closed;
                pool.FrameArrived -= FrameArrived;
                progress.Complete();
                latest?.Dispose();
                processor?.Dispose();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally { signals.TryComplete(failure); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        try { if (worker != null) await worker; }
        finally
        {
            try { encoder.Dispose(); }
            finally { captureDevice.Dispose(); device.Dispose(); lifetime.Dispose(); }
        }
    }
}
