using System.Runtime.InteropServices;
using ScrcpySend.Core;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace ScrcpySend.Media;

public sealed class HardwareEncoder : IDisposable
{
    private static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid VideoMajorType = new("73646976-0000-0010-8000-00aa00389b71");
    private const int NoEvents = unchecked((int)0xc00d3e80);
    private const int NeedMoreInput = unchecked((int)0xc00d6d72);
    private readonly IMFActivateCollection activations;
    private readonly IMFActivate activation;
    private readonly IMFTransform transform;
    private readonly IMFMediaEventGenerator events;
    private readonly IMFDXGIDeviceManager manager;
    private readonly CaptureSettings settings;
    private int inputRequests;
    private int outputRequests;
    private int inFlight;
    private bool started;
    private bool disposed;
    private byte[] configuration = [];
    public string Name { get; }
    public int Width { get; }
    public int Height { get; }

    public static IReadOnlyList<string> EnumerateNames(string codec = "h264")
    {
        MFStartup(false).CheckError();
        try
        {
            using var candidates = Enumerate(codec);
            return candidates.Select(item => item.GetString(TransformAttributeKeys.MftFriendlyNameAttribute)).ToArray();
        }
        finally { MFShutdown(); }
    }

    private static Guid Subtype(string codec) => codec switch
    {
        "h264" => VideoFormatGuids.H264,
        "h265" => new Guid("43564548-0000-0010-8000-00aa00389b71"),
        "av1" => new Guid("31305641-0000-0010-8000-00aa00389b71"),
        _ => throw new ArgumentException("Unsupported video codec.")
    };

    private static IMFActivateCollection Enumerate(string codec) => MFTEnumEx(VideoEncoderCategory,
        (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
        new RegisterTypeInfo { GuidMajorType = VideoMajorType, GuidSubtype = VideoFormatGuids.NV12 },
        new RegisterTypeInfo { GuidMajorType = VideoMajorType, GuidSubtype = Subtype(codec) });

    public HardwareEncoder(ID3D11Device device, int width, int height, CaptureSettings settings)
    {
        settings.Validate();
        this.settings = settings;
        Width = width;
        Height = height;
        MFStartup(false).CheckError();
        IMFActivateCollection? candidates = null;
        IMFTransform? selected = null;
        IMFMediaEventGenerator? generator = null;
        IMFDXGIDeviceManager? deviceManager = null;
        IMFActivate? selectedActivation = null;
        try
        {
            candidates = Enumerate(settings.VideoCodec);
            selectedActivation = candidates.FirstOrDefault()
                ?? throw new NotSupportedException($"No native hardware {settings.VideoCodec} encoder supports NV12 input. Software encoding is not allowed.");
            Name = selectedActivation.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
            if (selectedActivation.GetString(TransformAttributeKeys.MftEnumHardwareUrlAttribute).Length == 0)
                throw new NotSupportedException("Encoder has no hardware registration.");
            selected = selectedActivation.ActivateObject<IMFTransform>();
            using var attributes = selected.Attributes;
            if (attributes.GetUInt32(TransformAttributeKeys.TransformAsync) != 1 ||
                attributes.GetUInt32(TransformAttributeKeys.D3D11Aware) != 1)
                throw new NotSupportedException("Hardware encoder must support asynchronous D3D11 surface input.");
            attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
            deviceManager = MFCreateDXGIDeviceManager();
            deviceManager.ResetDevice(device).CheckError();
            selected.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)deviceManager.NativePointer);
            using var outputType = MediaType(Subtype(settings.VideoCodec), width, height, settings.MaxFps);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)settings.VideoBitrate).CheckError();
            outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, settings.VideoCodec switch { "h264" => 66u, "h265" => 1u, _ => 0u }).CheckError();
            selected.SetOutputType(0, outputType, 0);
            using var inputType = MediaType(VideoFormatGuids.NV12, width, height, settings.MaxFps);
            selected.SetInputType(0, inputType, 0);
            using (var actualOutput = selected.GetOutputCurrentType(0)) ValidateFormat(actualOutput, width, height, settings.MaxFps);
            using (var actualInput = selected.GetInputCurrentType(0)) ValidateFormat(actualInput, width, height, settings.MaxFps);
            using (var codec = new CodecControl(selected)) codec.Configure();
            generator = selected.QueryInterface<IMFMediaEventGenerator>();
            activations = candidates;
            activation = selectedActivation;
            transform = selected;
            events = generator;
            manager = deviceManager;
        }
        catch (Exception initializationError)
        {
            try { selectedActivation?.ShutdownObject().CheckError(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Hardware initialization and cleanup failed.", initializationError, cleanupError);
            }
            finally
            {
                generator?.Dispose();
                selected?.Dispose();
                deviceManager?.Dispose();
                candidates?.Dispose();
                MFShutdown();
            }
            throw;
        }
    }

    private static void ValidateFormat(IMFMediaType type, int width, int height, int fps)
    {
        var size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        var rate = type.GetUInt64(MediaTypeAttributeKeys.FrameRate);
        if ((uint)(size >> 32) != width || (uint)size != height || (uint)rate == 0 ||
            (ulong)(uint)(rate >> 32) != (ulong)fps * (uint)rate)
            throw new NotSupportedException($"Hardware encoder did not accept {width}x{height} at {fps} FPS. No format/FPS substitution is allowed.");
        if (type.GetUInt32(MediaTypeAttributeKeys.VideoPrimaries) != (uint)VideoPrimaries.Bt709 ||
            type.GetUInt32(MediaTypeAttributeKeys.TransferFunction) != (uint)VideoTransferFunction.Func709 ||
            type.GetUInt32(MediaTypeAttributeKeys.YuvMatrix) != (uint)VideoTransferMatrix.Bt709 ||
            type.GetUInt32(MediaTypeAttributeKeys.VideoNominalRange) != (uint)NominalRange.Range16_235)
            throw new NotSupportedException("Hardware encoder did not retain the required BT.709 limited-range SDR color metadata.");
    }

    private static IMFMediaType MediaType(Guid subtype, int width, int height, int fps)
    {
        var type = MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, VideoMajorType).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)(uint)width << 32) | (uint)height).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)(uint)fps << 32) | 1).CheckError();
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1).CheckError();
            type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
            type.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)VideoPrimaries.Bt709).CheckError();
            type.Set(MediaTypeAttributeKeys.TransferFunction, (uint)VideoTransferFunction.Func709).CheckError();
            type.Set(MediaTypeAttributeKeys.YuvMatrix, (uint)VideoTransferMatrix.Bt709).CheckError();
            type.Set(MediaTypeAttributeKeys.VideoNominalRange, (uint)NominalRange.Range16_235).CheckError();
            return type;
        }
        catch { type.Dispose(); throw; }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started) throw new InvalidOperationException("Encoder already started.");
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
        started = true;
    }

    private void PollEvents()
    {
        while (true)
        {
            IMFMediaEvent item;
            try { item = events.GetEvent(1); }
            catch (SharpGenException error) when (error.ResultCode.Code == NoEvents) { return; }
            using (item)
            {
                item.Status.CheckError();
                switch (item.EventType)
                {
                    case MediaEventTypes.TransformNeedInput: inputRequests++; break;
                    case MediaEventTypes.TransformHaveOutput: outputRequests++; break;
                    case MediaEventTypes.Error: throw new IOException("Hardware encoder reported an error.");
                }
            }
        }
    }

    public bool CanAcceptInput
    {
        get { PollEvents(); return inputRequests > 0 && inFlight < 4; }
    }

    public void Submit(ID3D11Texture2D texture, long pts100ns)
    {
        if (!started || !CanAcceptInput) throw new InvalidOperationException("Encoder is not ready for input.");
        using var sample = MFCreateSample();
        using var buffer = MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false);
        sample.AddBuffer(buffer);
        sample.SampleTime = pts100ns;
        sample.SampleDuration = 10_000_000L / settings.MaxFps;
        transform.ProcessInput(0, sample, 0);
        inputRequests--;
        inFlight++;
    }

    public IReadOnlyList<EncodedVideo> TakeOutput()
    {
        PollEvents();
        var output = new List<EncodedVideo>();
        while (outputRequests > 0)
        {
            var info = transform.GetOutputStreamInfo(0);
            var provides = ((OutputStreamInfoFlags)info.Flags).HasFlag(OutputStreamInfoFlags.OutputStreamProvidesSamples);
            var data = new OutputDataBuffer { StreamID = 0 };
            if (!provides)
            {
                if (info.Size <= 0 || info.Size > 4 * 1024 * 1024)
                    throw new IOException("Hardware encoder requested an invalid output buffer.");
                data.Sample = MFCreateSample();
                using var buffer = MFCreateAlignedMemoryBuffer(info.Size, Math.Max(0, info.Alignment - 1));
                data.Sample.AddBuffer(buffer);
            }
            try
            {
                var result = transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
                if (result.Code == NeedMoreInput) { outputRequests = 0; break; }
                result.CheckError();
                outputRequests--;
                if (data.Sample == null) throw new IOException("Hardware encoder produced no output sample.");
                if (--inFlight < 0) throw new IOException("Hardware encoder produced output without a submitted input.");
                using var buffer = data.Sample.ConvertToContiguousBuffer();
                buffer.Lock(out var pointer, out _, out var length);
                byte[] bytes;
                try
                {
                    if (length is <= 0 or > 4 * 1024 * 1024) throw new IOException("Invalid hardware output size.");
                    bytes = new byte[length];
                    Marshal.Copy(pointer, bytes, 0, length);
                }
                finally { buffer.Unlock(); }
                if (configuration.Length == 0)
                {
                    using var actualType = transform.GetOutputCurrentType(0);
                    if (actualType.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader, out var size).Success && size > 0)
                        configuration = VideoBitstream.Configuration(settings.VideoCodec, actualType.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader));
                    else configuration = VideoBitstream.Configuration(settings.VideoCodec, bytes);
                    VideoBitstream.ValidateConfiguration(settings.VideoCodec, configuration, Width, Height);
                }
                VideoBitstream.ValidateFrame(settings.VideoCodec, bytes, configuration);
                if (VideoBitstream.HasConfiguration(settings.VideoCodec, bytes))
                {
                    var current = VideoBitstream.Configuration(settings.VideoCodec, bytes);
                    VideoBitstream.ValidateConfiguration(settings.VideoCodec, current, Width, Height);
                    if (!current.AsSpan().SequenceEqual(configuration))
                        throw new ProtocolException("Hardware encoder changed configuration during the fixed-format session.");
                }
                output.Add(new(bytes, data.Sample.SampleTime / 10, configuration));
            }
            finally
            {
                data.Events?.Dispose();
                data.Sample?.Dispose();
            }
        }
        return output;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var failures = new List<Exception>();
        try
        {
            if (started)
            {
                transform.ProcessMessage(TMessageType.MessageCommandFlush, 0);
                transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
            }
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { activation.ShutdownObject().CheckError(); }
            catch (Exception error) { failures.Add(error); }
            events.Dispose();
            transform.Dispose();
            manager.Dispose();
            activations.Dispose();
            MFShutdown();
        }
        if (failures.Count > 0) throw new AggregateException("Hardware encoder cleanup failed.", failures);
    }
}
