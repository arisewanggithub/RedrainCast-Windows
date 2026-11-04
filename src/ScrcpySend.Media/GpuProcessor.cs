using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ScrcpySend.Core;

namespace ScrcpySend.Media;

internal sealed class GpuProcessor : IDisposable
{
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VideoDevice videoDevice;
    private readonly ID3D11VideoContext videoContext;
    private readonly ID3D11VideoProcessorEnumerator enumerator;
    private readonly ID3D11VideoProcessor processor;
    private readonly int width;
    private readonly int height;
    private readonly HdrToneMapper? toneMapper;

    public GpuProcessor(ID3D11Device device, int inputWidth, int inputHeight, int outputWidth, int outputHeight, int fps, bool hdr = false)
    {
        this.device = device;
        width = outputWidth;
        height = outputHeight;
        // ImmediateContext is device-owned and cached; dispose only a separate COM reference.
        context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        try
        {
            videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            videoContext = context.QueryInterface<ID3D11VideoContext>();
            enumerator = videoDevice.CreateVideoProcessorEnumerator(new()
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new((uint)fps, 1), OutputFrameRate = new((uint)fps, 1),
                InputWidth = (uint)inputWidth, InputHeight = (uint)inputHeight,
                OutputWidth = (uint)outputWidth, OutputHeight = (uint)outputHeight,
                Usage = VideoUsage.OptimalSpeed
            });
            var inputSupport = enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm);
            var outputSupport = enumerator.CheckVideoProcessorFormat(Format.NV12);
            if (!inputSupport.HasFlag(VideoProcessorFormatSupport.Input) ||
                !outputSupport.HasFlag(VideoProcessorFormatSupport.Output))
                throw new NotSupportedException("GPU video processor does not support BGRA to NV12. CPU conversion is not used.");
            processor = videoDevice.CreateVideoProcessor(enumerator, 0);
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
            videoContext.VideoProcessorSetStreamColorSpace(processor, 0, new() { RGB_Range = 0 });
            videoContext.VideoProcessorSetOutputColorSpace(processor, new() { YCbCr_Matrix = 1, Nominal_Range = 1 });
            videoContext.VideoProcessorSetOutputBackgroundColor(processor, false, new()
            {
                Rgba = new() { R = 0, G = 0, B = 0, A = 1 }
            });
            videoContext.VideoProcessorSetOutputTargetRect(processor, true, new RawRect(0, 0, width, height));
            if (hdr) toneMapper = new(device, inputWidth, inputHeight);
        }
        catch
        {
            toneMapper?.Dispose();
            processor?.Dispose();
            enumerator?.Dispose();
            videoContext?.Dispose();
            videoDevice?.Dispose();
            context.Dispose();
            throw;
        }
    }

    public ID3D11Texture2D Convert(ID3D11Texture2D source, int sourceWidth, int sourceHeight, CaptureColor? color = null)
    {
        if (toneMapper is not null)
            source = toneMapper.Convert(source, color ?? throw new ArgumentException("HDR conversion requires display color metadata."));
        else if (source.Description.Format != Format.B8G8R8A8_UNorm)
            throw new ArgumentException("SDR conversion requires BGRA8 capture.");
        var output = device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height,
            1, 1, BindFlags.RenderTarget));
        try
        {
            using var inputView = videoDevice.CreateVideoProcessorInputView(source, enumerator, new()
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new() { MipSlice = 0, ArraySlice = 0 }
            });
            using var outputView = videoDevice.CreateVideoProcessorOutputView(output, enumerator, new()
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new() { MipSlice = 0 }
            });
            var scale = Math.Min((double)width / sourceWidth, (double)height / sourceHeight);
            var fitWidth = (int)(sourceWidth * scale) & ~1;
            var fitHeight = (int)(sourceHeight * scale) & ~1;
            var x = (width - fitWidth) / 2 & ~1;
            var y = (height - fitHeight) / 2 & ~1;
            videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(0, 0, sourceWidth, sourceHeight));
            videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(x, y, x + fitWidth, y + fitHeight));
            videoContext.VideoProcessorBlt(processor, outputView, 0,
                [new VideoProcessorStream { Enable = true, InputSurface = inputView }]).CheckError();
            context.Flush();
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    public void Dispose()
    {
        toneMapper?.Dispose();
        processor.Dispose();
        enumerator.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
        context.Dispose();
    }
}
