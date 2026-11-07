using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace ScrcpySend.Media;

internal sealed class CaptureFrameSession(
    IDirect3DDevice device, GraphicsCaptureItem item,
    TypedEventHandler<Direct3D11CaptureFramePool, object> arrived) : IDisposable
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
