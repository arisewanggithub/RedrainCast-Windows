using System.Runtime.InteropServices;
using System.Diagnostics;
using FFmpeg.AutoGen;
using RedrainCast.Receive.Core;
using RedrainCast.Protocol;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;

namespace RedrainCast.Receive.Media;

public sealed unsafe class HardwareDecoder : IDisposable
{
    private const int HardwareDeviceContextMethod = 1; // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX in codec.h.
    private static readonly Guid H264Profile = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
    private readonly string codecName;
    private readonly Guid profile;
    private readonly HdrVideoFormat color;
    private Format SurfaceFormat => color.Hdr10 ? Format.P010 : Format.NV12;
    private readonly AVCodecContext_get_format formatCallback = SelectHardwareFormat;
    private readonly ID3D11VideoDevice videoDevice;
    private AVBufferRef* hardware;
    private AVCodecContext* context;
    private AVCodec* codec;
    private bool needsKeyframe = true;
    private bool disposed;
    private int width;
    private int height;
    private byte[] configurationData = [];
    private string stage = "idle";
    private long stageAt, validationMaxUs, copyMaxUs, submissionMaxUs, receiveMaxUs, pauseMaxUs;
    private int surfaceCount;
    public const int AdditionalHardwareFrames = 3;
    public int MinimumHardwareSurfaces => (codecName == "av1" ? 9 : 17) + AdditionalHardwareFrames;
    public int HardwareSurfaceCount => Volatile.Read(ref surfaceCount);
    public string Diagnostics => $"decoder stage={Volatile.Read(ref stage)}, stageMs={(Volatile.Read(ref stage) == "idle" ? 0 : Environment.TickCount64 - Interlocked.Read(ref stageAt))}, " +
        $"validateMaxUs={Interlocked.Read(ref validationMaxUs)}, copyMaxUs={Interlocked.Read(ref copyMaxUs)}, " +
        $"submitMaxUs={Interlocked.Read(ref submissionMaxUs)}, receiveMaxUs={Interlocked.Read(ref receiveMaxUs)}, " +
        $"gcPauseMaxUs={Interlocked.Read(ref pauseMaxUs)}, surfaces={HardwareSurfaceCount}";

    private long Stage(string value)
    {
        Interlocked.Exchange(ref stageAt, Environment.TickCount64);
        Volatile.Write(ref stage, value);
        return Stopwatch.GetTimestamp();
    }

    private static void Measure(ref long maximum, long started) =>
        Interlocked.Exchange(ref maximum, Math.Max(Interlocked.Read(ref maximum), (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds));

    public HardwareDecoder(ID3D11Device device, string codecName = "h264", HdrVideoFormat? color = null)
    {
        this.color = color ?? HdrVideoFormat.Sdr;
        this.color.Validate(codecName);
        this.codecName = codecName;
        profile = codecName switch
        {
            "h264" => H264Profile,
            "h265" when this.color.Hdr10 => new Guid("107af0e0-ef1a-4d19-aba8-67a163073d13"),
            "h265" => new Guid("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0"),
            "av1" => new Guid("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a"),
            _ => throw new NotSupportedException("Unsupported hardware video codec.")
        };
        NativeRuntime.Initialize();
        videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        try
        {
            videoDevice.CheckVideoDecoderFormat(profile, SurfaceFormat, out var supported).CheckError();
            if (!supported) throw new NotSupportedException($"Hardware {codecName}/{SurfaceFormat} decoding is unavailable. No software fallback.");
            codec = ffmpeg.avcodec_find_decoder_by_name(codecName == "h265" ? "hevc" : codecName);
            if (codec == null) throw new NotSupportedException($"Native {codecName} hardware decoder is missing.");
            var eligible = false;
            for (var index = 0; ; index++)
            {
                var configuration = ffmpeg.avcodec_get_hw_config(codec, index);
                if (configuration == null) break;
                eligible |= configuration->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA &&
                    configuration->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11 &&
                    (configuration->methods & HardwareDeviceContextMethod) != 0;
            }
            if (!eligible) throw new NotSupportedException($"The pinned {codecName} decoder lacks D3D11VA device support.");
            hardware = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
            if (hardware == null) throw new OutOfMemoryException();
            var hardwareContext = (AVHWDeviceContext*)hardware->data;
            var d3d = (AVD3D11VADeviceContext*)hardwareContext->hwctx;
            Marshal.AddRef(device.NativePointer);
            d3d->device = (FFmpeg.AutoGen.ID3D11Device*)device.NativePointer;
            D3D11ContextSynchronization.Attach(hardwareContext, device);
            NativeRuntime.Check(ffmpeg.av_hwdevice_ctx_init(hardware), "Initialize hardware D3D11VA decoder device");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Configure(byte[] configuration, int newWidth, int newHeight)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CodecConfiguration.ValidateVideo(codecName, configuration, newWidth, newHeight, color);
        configurationData = configuration;
        var description = new VideoDecoderDescription
        {
            Guid = profile,
            SampleWidth = (uint)newWidth,
            SampleHeight = (uint)newHeight,
            OutputFormat = SurfaceFormat
        };
        videoDevice.GetVideoDecoderConfigCount(ref description, out var configurations).CheckError();
        if (configurations == 0) throw new NotSupportedException($"Hardware {codecName} decoder cannot handle {newWidth}x{newHeight}.");
        if (context != null)
        {
            NativeRuntime.Check(ffmpeg.avcodec_send_packet(context, null), "Drain old decoder generation");
            using var remaining = Receive();
            if (remaining is not null) throw new ProtocolException("Decoder retained a delayed frame during reconfiguration.");
            FreeContext();
        }

        context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null) throw new OutOfMemoryException();
        context->hw_device_ctx = ffmpeg.av_buffer_ref(hardware);
        if (context->hw_device_ctx == null) throw new OutOfMemoryException();
        context->get_format = formatCallback;
        context->thread_count = 1;
        // Displayed, mailbox and renderer-owned frames need reserves beyond the codec's reference/current-picture surfaces.
        context->extra_hw_frames = AdditionalHardwareFrames;
        context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        context->flags |= ffmpeg.AV_CODEC_FLAG_COPY_OPAQUE;
        context->pkt_timebase = new AVRational { num = 1, den = 1_000_000 };
        context->extradata = (byte*)ffmpeg.av_mallocz((ulong)(configuration.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        if (context->extradata == null) throw new OutOfMemoryException();
        configuration.CopyTo(new Span<byte>(context->extradata, configuration.Length));
        context->extradata_size = configuration.Length;
        NativeRuntime.Check(ffmpeg.avcodec_open2(context, codec, null), $"Open D3D11VA {codecName} decoder");
        width = newWidth;
        height = newHeight;
        needsKeyframe = true;
    }

    public void ProbeSize(int width, int height)
    {
        var description = new VideoDecoderDescription
        {
            Guid = profile,
            SampleWidth = (uint)width,
            SampleHeight = (uint)height,
            OutputFormat = SurfaceFormat
        };
        videoDevice.GetVideoDecoderConfigCount(ref description, out var count).CheckError();
        if (count == 0) throw new NotSupportedException($"Hardware {codecName}/{SurfaceFormat} cannot decode {width}x{height}.");
        videoDevice.GetVideoDecoderConfig(ref description, 0, out var configuration).CheckError();
        using var decoder = videoDevice.CreateVideoDecoder(description, configuration);
    }

    public IReadOnlyList<HardwareFrame> Decode(Packet packet)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (context == null) throw new InvalidOperationException("Decoder not configured.");
        var pauseBefore = GC.GetTotalPauseDuration();
        try
        {
            var started = Stage("validate");
            var idr = CodecConfiguration.ValidateVideoFrame(codecName, packet.Payload, configurationData);
            JsonObject.Require(idr == ((packet.Flags & 1) != 0), $"{codecName} key flag differs from the actual random-access picture.");
            JsonObject.Require(!needsKeyframe || idr, "A new generation must start with a real random-access picture.");
            needsKeyframe = false;
            Measure(ref validationMaxUs, started);
            started = Stage("packet-copy");
            var input = ffmpeg.av_packet_alloc();
            if (input == null) throw new OutOfMemoryException();
            try
            {
                NativeRuntime.Check(ffmpeg.av_new_packet(input, packet.Payload.Length), "Allocate encoded packet");
                packet.Payload.CopyTo(new Span<byte>(input->data, packet.Payload.Length));
                input->pts = input->dts = packet.PtsUs;
                // FFmpeg carries this tag with the decoded picture, including delayed or repeated PTS.
                input->opaque = (void*)(nuint)(packet.VideoRotationDegrees / 90 + 1);
                input->flags = idr ? ffmpeg.AV_PKT_FLAG_KEY : 0;
                Measure(ref copyMaxUs, started);
                started = Stage("submit");
                int result;
                try { result = ffmpeg.avcodec_send_packet(context, input); }
                finally { Measure(ref submissionMaxUs, started); }
                NativeRuntime.Check(result, "Submit hardware video packet");
            }
            finally { ffmpeg.av_packet_free(&input); }
            var frames = new List<HardwareFrame>();
            Stage("receive");
            try
            {
                while (Receive() is { } frame)
                {
                    frames.Add(frame);
                    JsonObject.Require(frames.Count <= 1, "A progressive VIDEO_FRAME produced more than one picture.", "PROTOCOL");
                    JsonObject.Require(context->has_b_frames == 0 && frame.Width == width && frame.Height == height,
                        "Unexpected decoder reordering or unannounced dimensions.");
                }
                return frames;
            }
            catch
            {
                foreach (var frame in frames) frame.Dispose();
                throw;
            }
        }
        finally
        {
            Interlocked.Exchange(ref pauseMaxUs, Math.Max(Interlocked.Read(ref pauseMaxUs),
                (long)(GC.GetTotalPauseDuration() - pauseBefore).TotalMicroseconds));
            Stage("idle");
        }
    }

    private HardwareFrame? Receive()
    {
        var frame = ffmpeg.av_frame_alloc();
        if (frame == null) throw new OutOfMemoryException();
        try
        {
            var started = Stopwatch.GetTimestamp();
            int result;
            try { result = ffmpeg.avcodec_receive_frame(context, frame); }
            finally { Measure(ref receiveMaxUs, started); }
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return null;
            NativeRuntime.Check(result, "Receive hardware video frame");
            if (frame->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11 || frame->hw_frames_ctx == null || frame->data[0] == null)
                throw new NotSupportedException("Decoder did not produce a hardware D3D11 surface. Software output is forbidden.");
            var pool = (AVHWFramesContext*)frame->hw_frames_ctx->data;
            Volatile.Write(ref surfaceCount, pool->initial_pool_size);
            JsonObject.Require(pool->initial_pool_size >= MinimumHardwareSurfaces && pool->initial_pool_size <= 64,
                "Hardware decoder did not allocate the bounded reserved surface pool.", "DECODER_FAILED");
            if (pool->sw_format != (color.Hdr10 ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_NV12))
                throw new NotSupportedException("Hardware decoder surface bit depth differs from negotiation.");
            if (color.Hdr10)
            {
                JsonObject.Require(frame->color_primaries == AVColorPrimaries.AVCOL_PRI_BT2020 &&
                    frame->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 &&
                    frame->colorspace == AVColorSpace.AVCOL_SPC_BT2020_NCL &&
                    frame->color_range == AVColorRange.AVCOL_RANGE_MPEG,
                    "Decoded HDR picture color differs from negotiated limited BT.2020/PQ.", "DECODER_FAILED");
                ValidateMetadata(frame);
            }
            JsonObject.Require((nuint)frame->opaque is >= 1 and <= 4, "Hardware decoder lost the picture's rotation metadata.", "DECODER_FAILED");
            var output = new HardwareFrame(frame, color, ffmpeg.avcodec_profile_name(context->codec_id, context->profile));
            frame = null;
            return output;
        }

        finally { if (frame != null) ffmpeg.av_frame_free(&frame); }
    }

    private void ValidateMetadata(AVFrame* frame)
    {
        var mastering = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        var content = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        if (color.Metadata is not { } metadata)
        {
            JsonObject.Require(mastering == null && content == null,
                "Decoded HDR static metadata was not negotiated.", "DECODER_FAILED");
            return;
        }
        if (mastering != null)
        {
            var value = (AVMasteringDisplayMetadata*)mastering->data;
            static double Rational(AVRational v) => (double)v.num / v.den;
            JsonObject.Require(value->has_luminance != 0 && value->has_primaries != 0 &&
                Math.Abs(Rational(value->max_luminance) - metadata.MaxMasteringNits) < .001 &&
                Math.Abs(Rational(value->min_luminance) * 10000 - metadata.MinMasteringTenThousandths) < .01,
                "HDR mastering luminance differs from negotiation.", "DECODER_FAILED");
            var coordinates = new[] { metadata.RedX, metadata.RedY, metadata.GreenX, metadata.GreenY,
                    metadata.BlueX, metadata.BlueY, metadata.WhiteX, metadata.WhiteY };
            for (var index = 0; index < 8; index++)
            {
                var coordinate = index < 6 ? value->display_primaries[(uint)(index / 2)][(uint)(index % 2)] : value->white_point[(uint)(index - 6)];
                JsonObject.Require(HdrStaticMetadata.MatchesEncodedChromaticity(coordinates[index],
                    coordinate.num, coordinate.den),
                    $"HDR mastering primaries differ from negotiation: coordinate={index}, " +
                    $"expected={coordinates[index]}/50000, decoded={coordinate.num}/{coordinate.den}.", "DECODER_FAILED");
            }
        }
        if (content != null)
        {
            var value = (AVContentLightMetadata*)content->data;
            JsonObject.Require(value->MaxCLL == metadata.MaxContentLightNits && value->MaxFALL == metadata.MaxFrameAverageLightNits,
                "HDR content light metadata differs from negotiation.", "DECODER_FAILED");
        }
        if (mastering == null)
        {
            var value = ffmpeg.av_mastering_display_metadata_create_side_data(frame);
            if (value == null) throw new OutOfMemoryException();
            var coordinates = new[] { metadata.RedX, metadata.RedY, metadata.GreenX, metadata.GreenY,
                    metadata.BlueX, metadata.BlueY, metadata.WhiteX, metadata.WhiteY };
            for (uint i = 0; i < 8; i++)
            {
                var rational = new AVRational { num = coordinates[i], den = 50000 };
                if (i < 6)
                {
                    var pair = value->display_primaries[i / 2];
                    pair[i % 2] = rational;
                    value->display_primaries[i / 2] = pair;
                }
                else value->white_point[i - 6] = rational;
            }
            value->min_luminance = new() { num = metadata.MinMasteringTenThousandths, den = 10000 };
            value->max_luminance = new() { num = metadata.MaxMasteringNits, den = 1 };
            value->has_luminance = value->has_primaries = 1;
        }
        if (content == null)
        {
            var value = ffmpeg.av_content_light_metadata_create_side_data(frame);
            if (value == null) throw new OutOfMemoryException();
            value->MaxCLL = (uint)metadata.MaxContentLightNits;
            value->MaxFALL = (uint)metadata.MaxFrameAverageLightNits;
        }
    }
    private static AVPixelFormat SelectHardwareFormat(AVCodecContext* context, AVPixelFormat* choices)
    {
        for (var index = 0; choices[index] != AVPixelFormat.AV_PIX_FMT_NONE; index++)
            if (choices[index] == AVPixelFormat.AV_PIX_FMT_D3D11) return choices[index];
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        FreeContext();
        var buffer = hardware;
        hardware = null;
        ffmpeg.av_buffer_unref(&buffer);
        videoDevice.Dispose();
        GC.KeepAlive(formatCallback);
    }

    private void FreeContext()
    {
        var value = context;
        context = null;
        ffmpeg.avcodec_free_context(&value);
    }
}

public sealed unsafe class HardwareFrame : IDisposable
{
    private AVFrame* frame;
    internal HardwareFrame(AVFrame* frame, HdrVideoFormat? color = null, string? codecProfile = null)
    {
        this.frame = frame;
        ColorFormat = color ?? HdrVideoFormat.Sdr;
        var pool = frame->hw_frames_ctx == null ? null : (AVHWFramesContext*)frame->hw_frames_ctx->data;
        var descriptor = pool == null ? null : ffmpeg.av_pix_fmt_desc_get(pool->sw_format);
        int? depth = descriptor != null && descriptor->nb_components > 0 && descriptor->comp[0].depth > 0
            ? descriptor->comp[0].depth : null;
        VideoTransfer? transfer = frame->color_trc switch
        {
            AVColorTransferCharacteristic.AVCOL_TRC_BT709 => VideoTransfer.Bt709,
            AVColorTransferCharacteristic.AVCOL_TRC_SMPTE170M => VideoTransfer.Smpte170M,
            AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22 => VideoTransfer.Gamma22,
            AVColorTransferCharacteristic.AVCOL_TRC_GAMMA28 => VideoTransfer.Gamma28,
            AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1 => VideoTransfer.Srgb,
            AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => VideoTransfer.Pq,
            AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => VideoTransfer.Hlg,
            _ => null
        };
        Statistics = new(ColorFormat, transfer, codecProfile, depth);
    }
    public HdrVideoFormat ColorFormat { get; }
    public DecodedVideoStatistics Statistics { get; }
    public nint Texture => (nint)frame->data[0];
    public uint ArraySlice => checked((uint)(nuint)frame->data[1]);
    public int Width => frame->width;
    public int Height => frame->height;
    public long PtsUs => frame->pts;
    public int RotationDegrees => checked(((int)(nuint)frame->opaque - 1) * 90);
    public int PrimariesCode => (int)frame->color_primaries;
    public int TransferCode => (int)frame->color_trc;
    public int MatrixCode => (int)frame->colorspace;
    public int RangeCode => (int)frame->color_range;
    public uint ColorMatrix => frame->colorspace switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => 1,
        AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => 0,
        AVColorSpace.AVCOL_SPC_UNSPECIFIED => frame->height > 576 ? 1u : 0u,
        _ => throw new NotSupportedException("Only SDR BT.601/709 video color matrices are supported.")
    };
    public uint NominalRange => frame->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 2u : 1u;
    public HardwareFrame Clone()
    {
        var copy = ffmpeg.av_frame_clone(frame);
        if (copy == null) throw new OutOfMemoryException();
        return new HardwareFrame(copy, ColorFormat, Statistics.CodecProfile);
    }
    public void Dispose()
    {
        var value = frame;
        frame = null;
        ffmpeg.av_frame_free(&value);
    }
}
