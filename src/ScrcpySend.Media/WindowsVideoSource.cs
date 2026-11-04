using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ScrcpySend.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace ScrcpySend.Media;

public sealed class WindowsVideoSource : IVideoSource
{
    public static GraphicsCaptureItem SelectDefaultDisplay() => CaptureInterop.DefaultDisplay();

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

    public WindowsVideoSource(GraphicsCaptureItem item, CaptureSettings settings)
    {
        settings.Validate();
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows graphics capture is unavailable.");
        this.item = item;
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
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
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
                processor = new GpuProcessor(device, size.Width, size.Height, Width, Height, settings.MaxFps);
                token.ThrowIfCancellationRequested();
                encoder.Start();
                session.StartCapture();
                var pacer = new DeferredFramePacer(settings.MaxFps);
                using var wait = new CaptureWait(token);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                SizeInt32? pendingSize = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    progress.Check(clock.Elapsed);
                    if (pendingSize is { } resized)
                    {
                        latest?.Dispose();
                        latest = null;
                        pacer.DiscardPending();
                        processor.Dispose();
                        processor = null;
                        pool.Recreate(captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, resized);
                        processor = new GpuProcessor(device, resized.Width, resized.Height, Width, Height, settings.MaxFps);
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
                                using var nv12 = processor.Convert(latest!, size.Width, size.Height);
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
                    using var source = CaptureInterop.Texture(frame.Surface);
                    latest ??= device.CreateTexture2D(source.Description);
                    device.ImmediateContext.CopyResource(latest, source);
                    pacer.Offer(timestamp);
                    var currentTime = (long)((Int128)System.Diagnostics.Stopwatch.GetTimestamp() *
                        TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency);
                    if (pacer.TryTake(currentTime, out var pendingTimestamp))
                    {
                        using var nv12 = processor.Convert(latest, size.Width, size.Height);
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
