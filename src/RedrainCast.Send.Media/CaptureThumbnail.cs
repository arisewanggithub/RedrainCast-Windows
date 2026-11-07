using Vortice.Direct3D;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RedrainCast.Send.Media;

/// <summary>One short-lived capture at a time; no media pipeline, audio, or persistent output.</summary>
public sealed class CaptureThumbnail : IDisposable
{
    private readonly ID3D11Device device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
    private readonly IDirect3DDevice captureDevice;

    public CaptureThumbnail()
    {
        try
        {
            using var multithread = device.ImmediateContext.QueryInterface<ID3D11Multithread>();
            multithread.SetMultithreadProtected(true);
            captureDevice = CaptureInterop.CreateDevice(device);
        }
        catch { device.Dispose(); throw; }
    }

    public async Task<SoftwareBitmap> ReadAsync(CaptureTarget target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var source = target.Open();
        var item = source.Item;
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void FrameArrived(Direct3D11CaptureFramePool _, object args) => arrived.TrySetResult();
        pool.FrameArrived += FrameArrived;
        try
        {
            session.StartCapture();
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellation).ConfigureAwait(false);
            using var frame = pool.TryGetNextFrame() ?? throw new IOException("No preview frame was available.");
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied)
                .AsTask().ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return Scale(bitmap);
        }
        finally { pool.FrameArrived -= FrameArrived; }
    }

    private static SoftwareBitmap Scale(SoftwareBitmap bitmap)
    {
        var width = Math.Min(320, bitmap.PixelWidth);
        var height = Math.Max(1, (int)((long)bitmap.PixelHeight * width / bitmap.PixelWidth));
        var input = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        var buffer = new Windows.Storage.Streams.Buffer((uint)input.Length);
        bitmap.CopyToBuffer(buffer);
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(input);
        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var from = ((int)((long)y * bitmap.PixelHeight / height) * bitmap.PixelWidth +
                (int)((long)x * bitmap.PixelWidth / width)) * 4;
            var to = (y * width + x) * 4;
            output[to] = input[from];
            output[to + 1] = input[from + 1];
            output[to + 2] = input[from + 2];
            output[to + 3] = 255;
        }
        using var writer = new DataWriter();
        writer.WriteBytes(output);
        return SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8,
            width, height, BitmapAlphaMode.Premultiplied);
    }

    public void Dispose()
    {
        try { captureDevice.Dispose(); }
        finally { device.Dispose(); }
    }
}
