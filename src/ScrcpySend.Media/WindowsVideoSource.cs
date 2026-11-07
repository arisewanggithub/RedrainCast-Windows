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

public sealed class WindowsVideoSource : IRecoverableVideoSource
{
    public static CaptureSource SelectDefaultDisplay() => CaptureInterop.DefaultDisplay();

    public static uint ReadCaptureAdapterVendorId()
    {
        using var device = CreateHardwareDevice();
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        return adapter.Description.VendorId;
    }

    private readonly CaptureSource source;
    private readonly Action<string>? diagnostic;
    private readonly GraphicsCaptureItem item;
    private readonly CaptureSettings settings;
    private readonly ID3D11Device device;
    private readonly IDirect3DDevice captureDevice;
    private readonly HardwareEncoder encoder;
    private readonly GpuMemoryDiagnostics memory;
    private GpuSurfacePool? surfaces;
    private readonly CancellationTokenSource lifetime = new();
    private Task? worker;
    private bool started;
    private bool disposed;
    private int capturedFrames;
    private int submittedFrames;
    private int encodedFrames;
    private int frameNotifications;
    private int convertedFrames;
    private string stage = "not started";
    private readonly CaptureProgress progress = new();
    public bool SupportsStreamRecovery => true;
    public ValueTask RequestKeyFrameAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        progress.RequestKeyFrame();
        return ValueTask.CompletedTask;
    }
    private long stageAt = Environment.TickCount64;
    public string EncoderName => encoder.Name;
    public int Width => encoder.Width;
    public int Height => encoder.Height;
    public string Statistics => $"stage={Volatile.Read(ref stage)}, stageAgeMs={Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref stageAt))}; " +
        $"notifications={Volatile.Read(ref frameNotifications)}, captured={Volatile.Read(ref capturedFrames)}, converted={Volatile.Read(ref convertedFrames)}, " +
        $"submitted={Volatile.Read(ref submittedFrames)}, encoded={Volatile.Read(ref encodedFrames)}; dropped={encoder.DroppedFrames}; " +
        $"output={Width}x{Height}, codec={settings.VideoCodec}, fps={settings.MaxFps}; {encoder.Diagnostics}; {surfaces?.Diagnostics}";

    private void Stage(string value)
    {
        if (Volatile.Read(ref stage) == value) return;
        Interlocked.Exchange(ref stageAt, Environment.TickCount64);
        Volatile.Write(ref stage, value);
    }

    public WindowsVideoSource(CaptureSource source, CaptureSettings settings, Action<string>? diagnostic = null)
    {
        settings.Validate();
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows graphics capture is unavailable.");
        this.source = source;
        this.diagnostic = diagnostic;
        item = source.Item;
        diagnostic?.Invoke("Capture worker bootstrap: reading source display color.");
        var color = source.ReadColor(diagnostic);
        this.settings = settings;
        var (width, height) = settings.OutputSize(item.Size.Width, item.Size.Height);
        diagnostic?.Invoke("Capture worker bootstrap: creating hardware D3D11 device.");
        device = CreateHardwareDevice();
        try
        {
            diagnostic?.Invoke("Capture worker bootstrap: creating WinRT capture device.");
            captureDevice = CaptureInterop.CreateDevice(device);
            diagnostic?.Invoke("Capture worker bootstrap: checking GPU memory budget.");
            memory = new GpuMemoryDiagnostics(device);
            EncoderCompatibility.ValidateCapture(memory.VendorId, color.Hdr, settings.VideoCodec);
            memory.CheckBudget();
            diagnostic?.Invoke("Capture worker bootstrap: constructing hardware encoder.");
            encoder = new HardwareEncoder(device, width, height, settings, diagnostic);
        }
        catch
        {
            memory?.Dispose();
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
        worker = Task.Factory.StartNew(() => Capture(queue, signals.Writer, linked.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var reporter = ReportProgressAsync(linked);
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
            await Task.WhenAll(worker, reporter);
            diagnostic?.Invoke($"Sender video final: {Statistics}");
            diagnostic?.Invoke($"Sender memory final: {memory.Snapshot()}");
        }
    }

    private async Task ReportProgressAsync(CancellationTokenSource stop)
    {
        var token = stop.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var ticks = 0;
        try
        {
            diagnostic?.Invoke($"Sender video startup: {Statistics}");
            diagnostic?.Invoke($"Sender memory startup: {memory.Snapshot()}");
            while (await timer.WaitForNextTickAsync(token))
            {
                memory.CheckBudget();
                if (++ticks % 5 == 0 || Volatile.Read(ref encodedFrames) == 0)
                {
                    diagnostic?.Invoke($"Sender video progress: {Statistics}");
                    diagnostic?.Invoke($"Sender memory progress: {memory.Snapshot()}");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { stop.Cancel(); throw; }
    }

    private void Capture(VideoQueue queue, ChannelWriter<bool> signals, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            using var dispatcher = new CaptureDispatcher();
            Stage("reading display color");
            var size = item.Size;
            var color = source.ReadColor(diagnostic);
            EncoderCompatibility.ValidateCapture(memory.VendorId, color.Hdr, settings.VideoCodec);
            surfaces = new GpuSurfacePool(device, Width, Height, detail => diagnostic?.Invoke(detail));
            diagnostic?.Invoke($"Capture color: {(color.Hdr ? "HDR scRGB -> content-adaptive BT.2390 SDR" : "SDR direct")}; {color.LuminanceDescription}. HDR content peak is measured separately on the GPU.");
            var pixelFormat = color.Hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
            Stage("creating capture frame pool");
            // Notifications coalesce; raw capture frames are paced before encoding.
            void FrameArrived(Direct3D11CaptureFramePool sender, object args)
            {
                Interlocked.Increment(ref frameNotifications);
                progress.Arrived();
            }
            using var frames = new CaptureFrameSession(captureDevice, item, FrameArrived);
            frames.Recreate(pixelFormat, size);
            GpuProcessor? processor = null;
            ID3D11Texture2D? latest = null;
            void Closed(GraphicsCaptureItem sender, object args) => progress.Closed();
            item.Closed += Closed;
            try
            {
                Stage("creating GPU converter");
                processor = new GpuProcessor(device, size.Width, size.Height, Width, Height, settings.MaxFps, color.Hdr);
                token.ThrowIfCancellationRequested();
                Stage("starting hardware encoder");
                encoder.Start();
                Stage("starting Windows capture");
                frames.StartCapture();
                var pacer = new DeferredFramePacer(settings.MaxFps);
                using var wait = new CaptureWait(token);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var nextColorCheck = TimeSpan.FromMilliseconds(500);
                SizeInt32? pendingSize = null;
                var keyInputPending = false;
                var rearmSession = false;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    dispatcher.Pump();
                    progress.Check(clock.Elapsed);
                    if (progress.TryTakeKeyRequest(clock.Elapsed))
                    {
                        encoder.RequestKeyFrame();
                        keyInputPending = true;
                        diagnostic?.Invoke("Capture worker requested a hardware random-access picture.");
                    }
                    if (clock.Elapsed >= nextColorCheck)
                    {
                        nextColorCheck = clock.Elapsed + TimeSpan.FromMilliseconds(500);
                        var nextColor = source.ReadColor(diagnostic);
                        EncoderCompatibility.ValidateCapture(memory.VendorId, nextColor.Hdr, settings.VideoCodec);
                        if (nextColor != color)
                        {
                            var formatChanged = nextColor.Hdr != color.Hdr;
                            color = nextColor;
                            if (formatChanged)
                            {
                                pixelFormat = color.Hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
                                pendingSize ??= size;
                            }
                            diagnostic?.Invoke($"Capture color changed: HDR={color.Hdr}; {color.LuminanceDescription}. Format changed={formatChanged}.");
                        }
                    }
                    if (pendingSize is { } resized)
                    {
                        latest?.Dispose();
                        latest = null;
                        pacer.DiscardPending();
                        processor.Dispose();
                        processor = null;
                        frames.Recreate(pixelFormat, resized, rearmSession);
                        processor = new GpuProcessor(device, resized.Width, resized.Height, Width, Height, settings.MaxFps, color.Hdr);
                        size = resized;
                        pendingSize = null;
                        if (rearmSession)
                        {
                            token.ThrowIfCancellationRequested();
                            frames.StartCapture();
                            rearmSession = false;
                        }
                    }
                    Stage("polling encoder output");
                    var droppedBefore = encoder.DroppedFrames;
                    foreach (var output in encoder.TakeOutput())
                    {
                        Interlocked.Increment(ref encodedFrames);
                        queue.Enqueue(output);
                        if (!signals.TryWrite(true)) throw new IOException("CONGESTION: video consumer is not keeping up.");
                        progress.Produced(clock.Elapsed);
                    }
                    for (var index = droppedBefore; index < encoder.DroppedFrames; index++) progress.Dropped();
                    Stage("waiting for encoder input request");
                    var canAcceptInput = encoder.CanAcceptInput;
                    if (!canAcceptInput)
                    {
                        wait.Pause();
                        continue;
                    }
                    using var surface = surfaces.TryRent();
                    if (surface is null)
                    {
                        wait.Pause();
                        continue;
                    }
                    Stage("reading capture frame");
                    using var frame = frames.TryGetNextFrame();
                    if (frame == null)
                    {
                        if (pacer.HasPending)
                        {
                            var now = (long)((Int128)System.Diagnostics.Stopwatch.GetTimestamp() *
                                TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency);
                            if (pacer.TryTake(now, out var sourceTimestamp))
                            {
                                Stage("converting captured frame");
                                processor.ConvertInto(latest!, size.Width, size.Height, surface.Texture, color);
                                Interlocked.Increment(ref convertedFrames);
                                Stage("submitting encoder input");
                                encoder.Submit(surface, sourceTimestamp);
                                keyInputPending = false;
                                progress.Submitted(clock.Elapsed);
                                Interlocked.Increment(ref submittedFrames);
                            }
                            else
                            {
                                wait.Pause();
                            }
                        }
                        else if (submittedFrames == encodedFrames + encoder.DroppedFrames)
                        {
                            if (progress.TryTakeIdleRefresh())
                            {
                                // An encoder key request cannot create input for an unchanged WGC source.
                                var observedColor = source.ReadColor(diagnostic);
                                EncoderCompatibility.ValidateCapture(memory.VendorId, observedColor.Hdr, settings.VideoCodec);
                                var observedSize = item.Size;
                                if (observedSize.Width <= 0 || observedSize.Height <= 0)
                                    throw new IOException("Capture source has no visible content.");
                                color = observedColor;
                                pixelFormat = color.Hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
                                pendingSize = observedSize;
                                rearmSession = true;
                                diagnostic?.Invoke("Capture worker rearming idle WGC session for pending random-access input.");
                                continue;
                            }
                            Stage("waiting for capture notification");
                            dispatcher.Wait(progress.WaitAsync(token, progress.KeyRequestDelay(clock.Elapsed)));
                        }
                        else
                        {
                            wait.Pause();
                        }
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
                    if (keyInputPending)
                        diagnostic?.Invoke($"Capture worker fresh random-access input: systemRelativeTime100ns={timestamp}; " +
                            $"readQpcUs={(long)((Int128)System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000 / System.Diagnostics.Stopwatch.Frequency)}.");
                    using var captured = CaptureInterop.Texture(frame.Surface);
                    if (latest is null)
                    {
                        var description = captured.Description;
                        if (color.Hdr) description = new Texture2DDescription(Format.R16G16B16A16_Float,
                            description.Width, description.Height, 1, 1, BindFlags.ShaderResource);
                        latest = device.CreateTexture2D(description);
                    }
                    Stage("copying captured texture");
                    device.ImmediateContext.CopyResource(latest, captured);
                    pacer.Offer(timestamp);
                    var currentTime = (long)((Int128)System.Diagnostics.Stopwatch.GetTimestamp() *
                        TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency);
                    if (pacer.TryTake(currentTime, out var pendingTimestamp))
                    {
                        Stage("converting captured frame");
                        processor.ConvertInto(latest, size.Width, size.Height, surface.Texture, color);
                        Interlocked.Increment(ref convertedFrames);
                        Stage("submitting encoder input");
                        encoder.Submit(surface, pendingTimestamp);
                        keyInputPending = false;
                        progress.Submitted(clock.Elapsed);
                        Interlocked.Increment(ref submittedFrames);
                    }
                }
            }
            finally
            {
                item.Closed -= Closed;
                progress.Complete();
                latest?.Dispose();
                processor?.Dispose();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            failure = error;
            diagnostic?.Invoke($"Sender capture failure: {error}\n{Statistics}\n{memory.Snapshot()}");
        }
        finally { progress.Complete(); signals.TryComplete(failure); }
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
            finally
            {
                try { if (surfaces is not null) await surfaces.DisposeAsync(); }
                finally { memory.Dispose(); captureDevice.Dispose(); device.Dispose(); lifetime.Dispose(); }
            }
        }
    }
}
