using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using RedrainCast.Receive.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RedrainCast.Receive.Media;

public static class RotationDiagnostics
{
    public static unsafe string Run()
    {
        NativeRuntime.Initialize();
        using var presenter = new GpuPresenter(60);
        presenter.Prepare(320, 160);
        if (!presenter.SupportsVideoRotation)
            throw new NotSupportedException("The GPU does not support video-processor rotation; videoRotation must not be advertised.");
        const int width = 320, height = 160;
        byte[] luma = [50, 90, 150, 210];
        var pixels = new byte[width * height * 3 / 2];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            pixels[y * width + x] = luma[(y >= height / 2 ? 2 : 0) + (x >= width / 2 ? 1 : 0)];
        Array.Fill(pixels, (byte)128, width * height, width * height / 2);
        var maximumError = 0;
        fixed (byte* data = pixels)
        {
            using var texture = presenter.Device.CreateTexture2D(new Texture2DDescription(Format.NV12,
                width, height, 1, 1, BindFlags.RenderTarget), new SubresourceData((nint)data, width));
            int[][] expectedCorners = [[0, 1, 2, 3], [2, 0, 3, 1], [3, 2, 1, 0], [1, 3, 0, 2]];
            foreach (var viewport in new[] { (Width: 640, Height: 480), (Width: 480, Height: 640) })
            foreach (var turns in new[] { 0, 1, 2, 3, 0 })
            {
                presenter.SetViewport(viewport.Width, viewport.Height, 1, 1);
                var native = ffmpeg.av_frame_alloc();
                if (native == null) throw new OutOfMemoryException();
                native->buf[0] = ffmpeg.av_buffer_alloc(1);
                if (native->buf[0] == null) { ffmpeg.av_frame_free(&native); throw new OutOfMemoryException(); }
                native->format = (int)AVPixelFormat.AV_PIX_FMT_D3D11;
                native->width = width;
                native->height = height;
                native->data[0] = (byte*)texture.NativePointer;
                native->opaque = (void*)(nuint)(turns + 1);
                native->colorspace = AVColorSpace.AVCOL_SPC_BT709;
                native->color_range = AVColorRange.AVCOL_RANGE_MPEG;
                using var frame = new HardwareFrame(native);
                using var clone = frame.Clone();
                if (clone.RotationDegrees != turns * 90) throw new IOException("Cloning lost the picture rotation.");
                var inspected = false;
                presenter.InspectForDiagnostics(clone, output =>
                {
                    var description = output.Description;
                    description.BindFlags = BindFlags.None;
                    description.Usage = ResourceUsage.Staging;
                    description.CPUAccessFlags = CpuAccessFlags.Read;
                    using var staging = presenter.Device.CreateTexture2D(description);
                    var context = presenter.Device.ImmediateContext;
                    context.CopyResource(staging, output);
                    var mapped = context.Map(staging, 0, MapMode.Read);
                    try
                    {
                        var rect = VideoLayout.Fit(width, height, turns * 90, viewport.Width, viewport.Height);
                        for (var corner = 0; corner < 4; corner++)
                        {
                            var x = rect.X + rect.Width * (corner % 2 == 0 ? 1 : 3) / 4;
                            var y = rect.Y + rect.Height * (corner < 2 ? 1 : 3) / 4;
                            var expected = (int)Math.Round((luma[expectedCorners[turns][corner]] - 16) * 255.0 / 219);
                            for (var component = 0; component < 3; component++)
                            {
                                var actual = Marshal.ReadByte(mapped.DataPointer, checked(y * (int)mapped.RowPitch + x * 4 + component));
                                maximumError = Math.Max(maximumError, Math.Abs(actual - expected));
                            }
                        }
                        for (var component = 0; component < 3; component++)
                            if (Marshal.ReadByte(mapped.DataPointer, component) > 2)
                                throw new IOException("Rotated video did not preserve black letterboxing.");
                        inspected = true;
                    }
                    finally { context.Unmap(staging, 0); }
                });
                if (!inspected) throw new IOException("GPU rotation diagnostic did not render its test frame.");
            }
        }
        if (maximumError > 3) throw new IOException($"Clockwise GPU rotation differs from expected corner pixels by {maximumError} code values.");
        return $"GPU 0/90/180/270/0-degree rotation, portrait/landscape fit, frame cloning and black letterboxing passed; maximum RGB error={maximumError}. Synthetic readback only; not a presentation/latency measurement.";
    }
}
