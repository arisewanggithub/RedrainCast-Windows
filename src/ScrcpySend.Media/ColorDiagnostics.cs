using System.Numerics;
using System.Runtime.InteropServices;
using ScrcpySend.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScrcpySend.Media;

public sealed record ColorProbeReport(CaptureColor DisplayColor, CaptureColor TestColor, int MaximumRgbError, int MaximumHdrLumaError,
    int MaximumChromaError, int MaximumSdrLumaError, int[] HdrLuma, int[] SdrLuma);

public static class ColorDiagnostics
{
    public static unsafe (byte[] Bgra, float PeakNits) RenderFrame(byte[] rgba16f, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba16f.Length != checked(width * height * 8))
            throw new ArgumentException("Expected a complete RGBA float16 frame.");
        using var device = WindowsVideoSource.CreateHardwareDevice();
        using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        fixed (byte* pointer = rgba16f)
        {
            using var input = device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float,
                (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource), new SubresourceData((nint)pointer, (uint)width * 8));
            using var mapper = new HdrToneMapper(device, width, height);
            var output = mapper.Convert(input, new(true));
            var description = output.Description;
            description.BindFlags = BindFlags.None;
            description.Usage = ResourceUsage.Staging;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = device.CreateTexture2D(description);
            context.CopyResource(staging, output);
            var mapped = context.Map(staging, 0, MapMode.Read);
            try
            {
                var result = new byte[width * height * 4];
                for (var y = 0; y < height; y++)
                    Marshal.Copy(mapped.DataPointer + checked((int)mapped.RowPitch * y), result, y * width * 4, width * 4);
                return (result, mapper.PeakNits);
            }
            finally { context.Unmap(staging, 0); }
        }
    }

    public static unsafe ColorProbeReport Run()
    {
        var displayColor = WindowsVideoSource.SelectDefaultDisplay().ReadColor();
        var color = new CaptureColor(true, 180, 1000);
        float[] levels = [0, 0.01f, 0.05f, 0.18f, 0.5f, 0.75f, 1, 1.5f, 2, 3, 4, color.PeakScale];
        var patches = levels.Select(level => new Vector3(level * color.WhiteScale))
            .Concat([new Vector3(4, -0.2f, 0.2f), new Vector3(-0.2f, 0.7f, 1.3f), new Vector3(0, 0, 12), new Vector3(1, 0.4f, 0.1f)]).ToArray();
        const int patchWidth = 16, height = 128;
        var width = patches.Length * patchWidth;
        var pixels = new Half[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = patches[x / patchWidth];
            var offset = (y * width + x) * 4;
            pixels[offset] = (Half)value.X;
            pixels[offset + 1] = (Half)value.Y;
            pixels[offset + 2] = (Half)value.Z;
            pixels[offset + 3] = (Half)1;
        }
        using var device = WindowsVideoSource.CreateHardwareDevice();
        using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        fixed (Half* data = pixels)
        {
            using var input = device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float,
                (uint)width, height, 1, 1, BindFlags.ShaderResource), new SubresourceData((nint)data, (uint)width * 8));
            using var mapper = new HdrToneMapper(device, width, height);
            var mapped = mapper.Convert(input, color);
            var rgb = ReadPatches(device, context, mapped, patches.Length, patchWidth, 4);
            var rgbError = 0;
            for (var index = 0; index < patches.Length; index++)
            {
                var expected = ToneMapTables.Sample(patches[index], mapper.PeakNits);
                rgbError = Math.Max(rgbError, Math.Abs(rgb[index * 4] - (int)MathF.Round(expected.Z * 255)));
                rgbError = Math.Max(rgbError, Math.Abs(rgb[index * 4 + 1] - (int)MathF.Round(expected.Y * 255)));
                rgbError = Math.Max(rgbError, Math.Abs(rgb[index * 4 + 2] - (int)MathF.Round(expected.X * 255)));
            }
            if (rgbError > 2) throw new IOException($"GPU HDR mapping differs from the reference by {rgbError} RGB code values (maximum 2).");
            var adjusted = color with { SdrWhiteNits = 360, PeakNits = 2000 };
            var adjustedRgb = ReadPatches(device, context, mapper.Convert(input, adjusted), patches.Length, patchWidth, 4);
            for (var index = 0; index < patches.Length; index++)
            {
                var expected = ToneMapTables.Sample(patches[index], mapper.PeakNits);
                rgbError = Math.Max(rgbError, Math.Abs(adjustedRgb[index * 4] - (int)MathF.Round(expected.Z * 255)));
                rgbError = Math.Max(rgbError, Math.Abs(adjustedRgb[index * 4 + 1] - (int)MathF.Round(expected.Y * 255)));
                rgbError = Math.Max(rgbError, Math.Abs(adjustedRgb[index * 4 + 2] - (int)MathF.Round(expected.X * 255)));
            }
            if (rgbError > 2) throw new IOException($"Updating HDR white-level metadata differs by {rgbError} RGB code values (maximum 2).");
            using var hdrProcessor = new GpuProcessor(device, width, height, width, height, 30, true);
            using var nv12 = hdrProcessor.Convert(input, width, height, color);
            var hdrY = ReadPatches(device, context, nv12, patches.Length, patchWidth, 1);
            var chroma = ReadPatches(device, context, nv12, patches.Length, patchWidth, 2, chroma: true);
            var hdrError = 0;
            var chromaError = 0;
            for (var index = 0; index < patches.Length; index++)
            {
                var expected = ToneMapTables.Sample(patches[index], mapper.PeakNits);
                var luma = Vector3.Dot(expected, new(0.2126f, 0.7152f, 0.0722f));
                hdrError = Math.Max(hdrError, Math.Abs(hdrY[index] - (int)MathF.Round(16 + 219 * luma)));
                chromaError = Math.Max(chromaError, Math.Abs(chroma[index * 2] - (int)MathF.Round(128 + 224 * (expected.Z - luma) / 1.8556f)));
                chromaError = Math.Max(chromaError, Math.Abs(chroma[index * 2 + 1] - (int)MathF.Round(128 + 224 * (expected.X - luma) / 1.5748f)));
            }
            if (hdrError > 3 || hdrY[6] >= hdrY[7] || hdrY[7] >= hdrY[levels.Length - 1])
                throw new IOException($"HDR luma conversion failed: maximum error {hdrError}; highlights must remain distinguishable above SDR white.");
            if (chromaError > 3) throw new IOException($"HDR BT.709 chroma differs by {chromaError} code values (maximum 3).");

            byte[] sdrLevels = [0, 16, 64, 128, 192, 235, 255];
            var sdr = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var level = sdrLevels[x / patchWidth % sdrLevels.Length];
                sdr[offset] = sdr[offset + 1] = sdr[offset + 2] = level;
                sdr[offset + 3] = 255;
            }
            fixed (byte* sdrData = sdr)
            {
                using var sdrInput = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                    (uint)width, height, 1, 1, BindFlags.RenderTarget), new SubresourceData((nint)sdrData, (uint)width * 4));
                using var sdrProcessor = new GpuProcessor(device, width, height, width, height, 30);
                using var sdrOutput = sdrProcessor.Convert(sdrInput, width, height);
                var sdrY = ReadPatches(device, context, sdrOutput, sdrLevels.Length, patchWidth, 1);
                var sdrError = sdrY.Select((value, index) => Math.Abs(value - (int)MathF.Round(16 + 219 * sdrLevels[index] / 255f))).Max();
                if (sdrError > 3) throw new IOException($"SDR passthrough luma differs by {sdrError} code values (maximum 3).");
                return new(displayColor, color, rgbError, hdrError, chromaError, sdrError,
                    hdrY.Take(levels.Length).Select(value => (int)value).ToArray(), sdrY.Select(value => (int)value).ToArray());
            }
        }
    }

    private static byte[] ReadPatches(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source,
        int count, int patchWidth, int bytesPerPixel, bool chroma = false)
    {
        var description = source.Description;
        description.BindFlags = BindFlags.None;
        description.Usage = ResourceUsage.Staging;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        using var staging = device.CreateTexture2D(description);
        context.CopyResource(staging, source);
        var mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            var result = new byte[count * bytesPerPixel];
            for (var index = 0; index < count; index++)
                Marshal.Copy(mapped.DataPointer + checked((int)mapped.RowPitch * (chroma ? (int)description.Height + 4 : 8) +
                    (index * patchWidth + patchWidth / 2) * (chroma ? 1 : bytesPerPixel)),
                    result, index * bytesPerPixel, bytesPerPixel);
            return result;
        }
        finally { context.Unmap(staging, 0); }
    }
}
