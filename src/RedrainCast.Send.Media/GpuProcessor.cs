using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using RedrainCast.Send.Core;
using SharpGen.Runtime;

namespace RedrainCast.Send.Media;

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
    private long convertedFrames;
    private long allocatedTextures;
    private ID3D11VideoProcessorInputView? cachedInput;
    private nint cachedSource;
    private readonly Dictionary<nint, ID3D11VideoProcessorOutputView> cachedOutputs = [];
    public string Diagnostics => $"conversionFrames={Interlocked.Read(ref convertedFrames)}, " +
        $"nv12TexturesAllocated={Interlocked.Read(ref allocatedTextures)}, " +
        $"nv12NominalBytesPerTexture={checked((long)width * height * 3 / 2)} (allocation count is cumulative, not live)";

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
        var output = device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height,
            1, 1, BindFlags.RenderTarget));
        Interlocked.Increment(ref allocatedTextures);
        try { ConvertInto(source, sourceWidth, sourceHeight, output, color, cacheViews: false); return output; }
        catch { output.Dispose(); throw; }
    }

    internal void ConvertInto(ID3D11Texture2D source, int sourceWidth, int sourceHeight, ID3D11Texture2D output,
        CaptureColor? color = null, bool cacheViews = true)
    {
        var operation = "HDR tone mapping";
        ID3D11VideoProcessorInputView? inputView = null;
        ID3D11VideoProcessorOutputView? outputView = null;
        try
        {
            if (toneMapper is not null)
                source = toneMapper.Convert(source, color ?? throw new ArgumentException("HDR conversion requires display color metadata."));
            else if (source.Description.Format != Format.B8G8R8A8_UNorm)
                throw new ArgumentException("SDR conversion requires BGRA8 capture.");
            operation = "creating video processor input view";
            if (cacheViews && (cachedInput is null || cachedSource != source.NativePointer))
            {
                cachedInput?.Dispose();
                cachedInput = null;
                cachedSource = source.NativePointer;
                cachedInput = CreateInput(source);
            }
            inputView = cacheViews ? cachedInput! : CreateInput(source);
            operation = "creating video processor output view";
            if (cacheViews && !cachedOutputs.TryGetValue(output.NativePointer, out outputView))
            {
                outputView = CreateOutput(output);
                cachedOutputs.Add(output.NativePointer, outputView);
            }
            if (!cacheViews) outputView = CreateOutput(output);
            var scale = Math.Min((double)width / sourceWidth, (double)height / sourceHeight);
            var fitWidth = (int)(sourceWidth * scale) & ~1;
            var fitHeight = (int)(sourceHeight * scale) & ~1;
            var x = (width - fitWidth) / 2 & ~1;
            var y = (height - fitHeight) / 2 & ~1;
            videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(0, 0, sourceWidth, sourceHeight));
            videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(x, y, x + fitWidth, y + fitHeight));
            operation = "VideoProcessorBlt";
            videoContext.VideoProcessorBlt(processor, outputView!, 0,
                [new VideoProcessorStream { Enable = true, InputSurface = inputView }]).CheckError();
            context.Flush();
            Interlocked.Increment(ref convertedFrames);
        }
        catch (SharpGenException error)
        {
            throw new IOException($"GPU conversion failed during {operation}: HRESULT=0x{unchecked((uint)error.ResultCode.Code):X8}; " +
                $"input={sourceWidth}x{sourceHeight}, output={width}x{height}, HDR={toneMapper is not null}; {Diagnostics}.", error);
        }
        finally
        {
            if (!cacheViews) { outputView?.Dispose(); inputView?.Dispose(); }
        }
    }

    private ID3D11VideoProcessorInputView CreateInput(ID3D11Texture2D source) =>
        videoDevice.CreateVideoProcessorInputView(source, enumerator, new()
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new() { MipSlice = 0, ArraySlice = 0 }
        });

    private ID3D11VideoProcessorOutputView CreateOutput(ID3D11Texture2D output) =>
        videoDevice.CreateVideoProcessorOutputView(output, enumerator, new()
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new() { MipSlice = 0 }
        });

    public void Dispose()
    {
        cachedInput?.Dispose();
        foreach (var view in cachedOutputs.Values) view.Dispose();
        toneMapper?.Dispose();
        processor.Dispose();
        enumerator.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
        context.Dispose();
    }
}
