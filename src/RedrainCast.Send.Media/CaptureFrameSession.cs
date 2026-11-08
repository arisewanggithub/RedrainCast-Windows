using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace RedrainCast.Send.Media;

internal sealed class CaptureFrameSession(
    IDirect3DDevice device, GraphicsCaptureItem item,
    TypedEventHandler<Direct3D11CaptureFramePool, object> arrived,
    Action<string>? diagnostic = null) : IDisposable
{
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private bool disposed;

    public void Recreate(DirectXPixelFormat format, SizeInt32 size, bool rearm = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pool is not null && !rearm)
        {
            pool.Recreate(device, format, 2, size);
            return;
        }
        // A pool cannot create a second session after its original session closes.
        Close();
        try
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, format, 2, size);
            pool.FrameArrived += arrived;
            session = pool.CreateCaptureSession(item);
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
            {
                // Newer Windows builds throttle WGC independently of our capture FPS pacer.
                var previous = session.MinUpdateInterval;
                session.MinUpdateInterval = TimeSpan.Zero;
                diagnostic?.Invoke($"Capture WGC timing: previousMinUpdateIntervalTicks={previous.Ticks}, " +
                    $"requestedMinUpdateIntervalTicks=0, effectiveMinUpdateIntervalTicks={session.MinUpdateInterval.Ticks}. " +
                    "Positive FPS caps remain enforced by the application pacer.");
            }
            else
            {
                diagnostic?.Invoke("Capture WGC timing: MinUpdateInterval is unavailable on this Windows build; " +
                    "the operating system controls capture update cadence.");
            }
        }
        catch { Close(); throw; }
    }

    public void StartCapture() => (session ?? throw new ObjectDisposedException(nameof(CaptureFrameSession))).StartCapture();
    public Direct3D11CaptureFrame? TryGetNextFrame() =>
        (pool ?? throw new ObjectDisposedException(nameof(CaptureFrameSession))).TryGetNextFrame();

    private void Close()
    {
        var oldSession = session;
        var oldPool = pool;
        session = null;
        pool = null;
        try { oldSession?.Dispose(); }
        finally
        {
            if (oldPool is not null)
            {
                try { oldPool.FrameArrived -= arrived; }
                finally { oldPool.Dispose(); }
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Close();
    }
}
