using System.Runtime.InteropServices;
using RedrainCast.Protocol;
using RedrainCast.Send.Core;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using static Vortice.MediaFoundation.MediaFactory;

namespace RedrainCast.Send.Media;

public sealed class HardwareEncoder : IDisposable
{
    private static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid VideoMajorType = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid P010 = new("30313050-0000-0010-8000-00aa00389b71");
    private static readonly Lazy<bool> NativeHdrCandidates = new(() =>
    {
        MFStartup(false).CheckError();
        try
        {
            using var candidates = Enumerate("h265", hdr10: true);
            return candidates.Any(candidate => candidate.GetString(TransformAttributeKeys.MftEnumHardwareUrlAttribute).Length > 0);
        }
        finally { MFShutdown().CheckError(); }
    });
    public static bool HasNativeHdrCandidate => NativeHdrCandidates.Value;
    private const int NoEvents = unchecked((int)0xc00d3e80);
    private const int NeedMoreInput = unchecked((int)0xc00d6d72);
    private readonly IMFActivateCollection activations;
    private readonly IMFActivate activation;
    private readonly IMFTransform transform;
    private readonly IMFMediaEventGenerator events;
    private readonly IMFDXGIDeviceManager manager;
    private readonly CaptureSettings settings;
    private readonly HdrVideoFormat color;
    private Guid InputSubtype => color.Hdr10 ? P010 : VideoFormatGuids.NV12;
    private readonly Action<string>? diagnostic;
    private readonly EncoderSampleAccounting? samples;
    private readonly EncoderInputFlow inputFlow = new();
    private int outputRequests;
    private int needInputEvents;
    private int haveOutputEvents;
    private int inputBufferLengthBefore;
    private int inputBufferLength;
    private int inputBufferMaximum;
    private long firstInputTime = -1;
    private long lastInputTime;
    private int droppedFrames;
    private int emptyOutputSamples;
    private long lastDropWarningAt;
    private bool started;
    private bool disposed;
    private byte[] configuration = [];
    public string Name { get; } = "";
    public int Width { get; }
    public int Height { get; }
    public uint VendorId { get; }
    public int DroppedFrames => Volatile.Read(ref droppedFrames);
    public int EmptyOutputSamples => Volatile.Read(ref emptyOutputSamples);
    internal (int Before, int Current, int Maximum) InputBufferLengths => (
        Volatile.Read(ref inputBufferLengthBefore), Volatile.Read(ref inputBufferLength), Volatile.Read(ref inputBufferMaximum));
    public string Diagnostics => $"encoder={Name}; gpuVendor=0x{VendorId:X4}, " +
        $"requestedCompatibility={(EncoderCompatibility.ForVendor(VendorId).Count == 0 ? "default" : "CameraRecord/BFrames=0")}; " +
        $"needInputEvents={Volatile.Read(ref needInputEvents)}, " +
        $"haveOutputEvents={Volatile.Read(ref haveOutputEvents)}, inputRequests={inputFlow.InputRequests}, " +
        $"outputRequests={Volatile.Read(ref outputRequests)}, inFlight={inputFlow.InFlight}, droppedInputs={DroppedFrames}, " +
        $"emptyOutputSamples={EmptyOutputSamples}; " +
        $"inputBufferLengthBefore={InputBufferLengths.Before}, inputBufferLength={InputBufferLengths.Current}, inputBufferMaximum={InputBufferLengths.Maximum}; " +
        $"firstInputTime100ns={Interlocked.Read(ref firstInputTime)}, lastInputTime100ns={Interlocked.Read(ref lastInputTime)}";

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

    private static IMFActivateCollection Enumerate(string codec, bool hdr10 = false) => MFTEnumEx(VideoEncoderCategory,
        (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
        new RegisterTypeInfo { GuidMajorType = VideoMajorType, GuidSubtype = hdr10 ? P010 : VideoFormatGuids.NV12 },
        new RegisterTypeInfo { GuidMajorType = VideoMajorType, GuidSubtype = Subtype(codec) });

    public HardwareEncoder(ID3D11Device device, int width, int height, CaptureSettings settings, Action<string>? diagnostic = null)
    {
        settings.Validate();
        this.settings = settings;
        color = settings.VideoColor ?? HdrVideoFormat.Sdr;
        this.diagnostic = diagnostic;
        Width = width;
        Height = height;
        using (var dxgi = device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter())
            VendorId = adapter.Description.VendorId;
        EncoderCompatibility.ValidateCapture(VendorId, false, settings.VideoCodec);
        if (EncoderSampleAccounting.Supports(VendorId, settings.VideoCodec))
            samples = new EncoderSampleAccounting();
        diagnostic?.Invoke("Capture worker bootstrap: starting Media Foundation.");
        MFStartup(false).CheckError();
        IMFActivateCollection? candidates = null;
        IMFTransform? selected = null;
        IMFMediaEventGenerator? generator = null;
        IMFDXGIDeviceManager? deviceManager = null;
        IMFActivate? selectedActivation = null;
        try
        {
            diagnostic?.Invoke($"Capture worker bootstrap: enumerating hardware {settings.VideoCodec} encoders.");
            candidates = Enumerate(settings.VideoCodec, color.Hdr10);
            var rejected = new List<Exception>();
            foreach (var candidate in candidates)
            {
                selectedActivation = candidate;
                Name = candidate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
                try
                {
                    if (candidate.GetString(TransformAttributeKeys.MftEnumHardwareUrlAttribute).Length == 0)
                        throw new NotSupportedException("Encoder has no hardware registration.");
                    diagnostic?.Invoke($"Capture worker bootstrap: activating hardware encoder {Name}; input={(color.Hdr10 ? "P010/Main10 HDR10" : "NV12 SDR")}.");
                    selected = candidate.ActivateObject<IMFTransform>();
                    using var attributes = selected.Attributes;
                    if (attributes.GetUInt32(TransformAttributeKeys.TransformAsync) != 1 ||
                        attributes.GetUInt32(TransformAttributeKeys.D3D11Aware) != 1)
                        throw new NotSupportedException("Hardware encoder must support asynchronous D3D11 surface input.");
                    attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
                    deviceManager = MFCreateDXGIDeviceManager();
                    deviceManager.ResetDevice(device).CheckError();
                    selected.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)deviceManager.NativePointer);
                    using var outputType = MediaType(Subtype(settings.VideoCodec), width, height, settings.MaxFps, color);
                    outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)settings.VideoBitrate).CheckError();
                    var profile = color.Hdr10 ? 2u : EncoderCompatibility.OutputProfile(settings.VideoCodec);
                    if (profile is { } requiredProfile)
                        outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, requiredProfile).CheckError();
                    diagnostic?.Invoke("Capture worker bootstrap: setting hardware encoder output type.");
                    selected.SetOutputType(0, outputType, 0);
                    using var inputType = MediaType(InputSubtype, width, height, settings.MaxFps, color);
                    diagnostic?.Invoke("Capture worker bootstrap: setting hardware encoder input type.");
                    selected.SetInputType(0, inputType, 0);
                    using (var actualOutput = selected.GetOutputCurrentType(0))
                        ValidateFormat(actualOutput, Subtype(settings.VideoCodec), width, height, settings.MaxFps, color, profile);
                    using (var actualInput = selected.GetInputCurrentType(0))
                        ValidateFormat(actualInput, InputSubtype, width, height, settings.MaxFps, color);
                    using var codec = new CodecControl(selected);
                    diagnostic?.Invoke("Capture worker bootstrap: configuring hardware encoder controls.");
                    codec.Configure(VendorId);
                    generator = selected.QueryInterface<IMFMediaEventGenerator>();
                    break;
                }
                catch (Exception error) when (error is NotSupportedException or COMException or SharpGenException)
                {
                    diagnostic?.Invoke($"Hardware encoder candidate {Name} rejected required format/low-latency controls: {error}");
                    rejected.Add(new NotSupportedException($"Hardware candidate {Name} rejected {settings.VideoCodec}/{InputSubtype}.", error));
                    candidate.ShutdownObject().CheckError();
                    generator?.Dispose(); generator = null;
                    selected?.Dispose(); selected = null;
                    deviceManager?.Dispose(); deviceManager = null;
                    selectedActivation = null;
                }
            }
            if (selected is null || selectedActivation is null || generator is null || deviceManager is null)
                throw new NotSupportedException($"No native asynchronous D3D11 hardware {settings.VideoCodec} encoder accepts " +
                    $"{(color.Hdr10 ? "P010 Main10 HDR10" : "NV12 SDR")} with required low-latency controls. " +
                    "Software encoding and automatic codec/color substitution are not allowed.", new AggregateException(rejected));
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

    private static void ValidateFormat(IMFMediaType type, Guid subtype, int width, int height, int fps,
        HdrVideoFormat color, uint? profile = null)
    {
        var size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        var rate = type.GetUInt64(MediaTypeAttributeKeys.FrameRate);
        if (type.GetGUID(MediaTypeAttributeKeys.Subtype) != subtype ||
            profile is { } requiredProfile && type.GetUInt32(MediaTypeAttributeKeys.Mpeg2Profile) != requiredProfile ||
            (uint)(size >> 32) != width || (uint)size != height || (uint)rate == 0 ||
            (ulong)(uint)(rate >> 32) != (ulong)fps * (uint)rate)
            throw new NotSupportedException($"Hardware encoder did not accept {width}x{height} at {fps} FPS. No format/FPS substitution is allowed.");
        if (type.GetUInt32(MediaTypeAttributeKeys.VideoPrimaries) != (uint)(color.Hdr10 ? VideoPrimaries.Bt2020 : VideoPrimaries.Bt709) ||
            type.GetUInt32(MediaTypeAttributeKeys.TransferFunction) != (uint)(color.Hdr10 ? VideoTransferFunction.Func2084 : VideoTransferFunction.Func709) ||
            type.GetUInt32(MediaTypeAttributeKeys.YuvMatrix) != (uint)(color.Hdr10 ? VideoTransferMatrix.Bt202010 : VideoTransferMatrix.Bt709) ||
            type.GetUInt32(MediaTypeAttributeKeys.VideoNominalRange) != (uint)NominalRange.Range16_235)
            throw new NotSupportedException($"Hardware encoder did not retain required {(color.Hdr10 ? "BT.2020/PQ limited HDR10" : "BT.709 limited SDR")} color metadata.");
    }

    private static IMFMediaType MediaType(Guid subtype, int width, int height, int fps, HdrVideoFormat color)
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
            type.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)(color.Hdr10 ? VideoPrimaries.Bt2020 : VideoPrimaries.Bt709)).CheckError();
            type.Set(MediaTypeAttributeKeys.TransferFunction, (uint)(color.Hdr10 ? VideoTransferFunction.Func2084 : VideoTransferFunction.Func709)).CheckError();
            type.Set(MediaTypeAttributeKeys.YuvMatrix, (uint)(color.Hdr10 ? VideoTransferMatrix.Bt202010 : VideoTransferMatrix.Bt709)).CheckError();
            type.Set(MediaTypeAttributeKeys.VideoNominalRange, (uint)NominalRange.Range16_235).CheckError();
            if (color.Metadata is { } metadata) SetHdrMetadata(type, metadata);
            return type;
        }
        catch { type.Dispose(); throw; }
    }

    private static void SetHdrMetadata(IMFMediaType type, HdrStaticMetadata metadata)
    {
        metadata.Validate();
        float[] primaries = [metadata.RedX / 50000f, metadata.RedY / 50000f,
            metadata.GreenX / 50000f, metadata.GreenY / 50000f, metadata.BlueX / 50000f,
            metadata.BlueY / 50000f, metadata.WhiteX / 50000f, metadata.WhiteY / 50000f];
        type.SetBlob(MediaTypeAttributeKeys.CustomVideoPrimaries, MemoryMarshal.AsBytes<float>(primaries.AsSpan()).ToArray()).CheckError();
        type.Set(new Guid("d6c6b997-272f-4ca1-8d00-8042111a0ff6"), (uint)metadata.MaxMasteringNits).CheckError();
        type.Set(new Guid("839a4460-4e7e-4b4f-ae79-cc08905c7b27"), (uint)metadata.MinMasteringTenThousandths).CheckError();
        type.Set(new Guid("50253128-c110-4de4-98ae-46a324fae6da"), (uint)metadata.MaxContentLightNits).CheckError();
        type.Set(new Guid("58d4bf57-6f52-4733-a195-a9e29ecf9e27"), (uint)metadata.MaxFrameAverageLightNits).CheckError();
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
                    case MediaEventTypes.TransformNeedInput: inputFlow.InputRequested(); needInputEvents++; break;
                    case MediaEventTypes.TransformHaveOutput: outputRequests++; haveOutputEvents++; break;
                    case MediaEventTypes.Error: throw new IOException("Hardware encoder reported an error.");
                }
            }
        }
    }

    public bool CanAcceptInput
    {
        // Each METransformNeedInput event permits one input, even before the first output.
        get { PollEvents(); return inputFlow.CanAcceptInput; }
    }

    public void Submit(ID3D11Texture2D texture, long pts100ns)
    {
        if (!started || !CanAcceptInput) throw new InvalidOperationException("Encoder is not ready for input.");
        using var sample = MFCreateSample();
        SubmitCore(texture, pts100ns, sample);
    }

    internal void RequestKeyFrame()
    {
        using var control = new CodecControl(transform);
        control.RequestKeyFrame();
    }

    internal void Submit(GpuSurfacePool.Lease lease, long pts100ns)
    {
        if (!started || !CanAcceptInput) throw new InvalidOperationException("Encoder is not ready for input.");
        MFCreateTrackedSample(out var tracked).CheckError();
        using (tracked)
        using (var sample = tracked.QueryInterface<IMFSample>())
        {
            lease.Track(tracked);
            SubmitCore(lease.Texture, pts100ns, sample);
        }
    }

    private void SubmitCore(ID3D11Texture2D texture, long pts100ns, IMFSample sample)
    {
        var description = texture.Description;
        if (description.Format != (color.Hdr10 ? Format.P010 : Format.NV12) ||
            description.Width != Width || description.Height != Height)
            throw new NotSupportedException("Hardware encoder input surface format/raster differs from the negotiated format.");
        samples?.ValidateSubmission(pts100ns);
        using var buffer = MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false);
        inputBufferLengthBefore = buffer.CurrentLength;
        inputBufferMaximum = buffer.MaxLength;
        if (inputBufferMaximum <= 0) throw new IOException("Hardware input surface has no buffer capacity.");
        // Qualcomm and other MFTs require a valid-byte count even for GPU-backed buffers.
        buffer.CurrentLength = inputBufferMaximum;
        inputBufferLength = buffer.CurrentLength;
        if (inputBufferLength != inputBufferMaximum)
            throw new IOException("Hardware input surface did not retain its valid buffer length.");
        sample.AddBuffer(buffer);
        sample.SampleTime = pts100ns;
        sample.SampleDuration = 10_000_000L / settings.MaxFps;
        transform.ProcessInput(0, sample, 0);
        Interlocked.CompareExchange(ref firstInputTime, pts100ns, -1);
        Interlocked.Exchange(ref lastInputTime, pts100ns);
        inputFlow.InputSubmitted();
        samples?.Submitted(pts100ns);
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
                    throw new IOException($"Hardware encoder requested an invalid output buffer: bytes={info.Size}, flags={info.Flags}, codec={settings.VideoCodec}.");
                data.Sample = MFCreateSample();
                using var buffer = MFCreateAlignedMemoryBuffer(info.Size, Math.Max(0, info.Alignment - 1));
                data.Sample.AddBuffer(buffer);
            }
            try
            {
                var result = transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out var outputStatus);
                if (result.Code == NeedMoreInput) { outputRequests = 0; break; }
                result.CheckError();
                outputRequests--;
                if (data.Sample == null)
                    throw new IOException($"Hardware encoder produced no output sample: codec={settings.VideoCodec}, sampleStatus={data.Status}, transformStatus={outputStatus}.");
                using var buffer = data.Sample.ConvertToContiguousBuffer();
                buffer.Lock(out var pointer, out var maximumLength, out var length);
                byte[] bytes;
                try
                {
                    if (length == 0 && samples is not null)
                    {
                        var flags = (OutputStreamInfoFlags)info.Flags;
                        var completion = samples.CompleteEmpty(VendorId, settings.VideoCodec,
                            new(length, maximumLength, (int)data.Status, (int)outputStatus, result.Code,
                                provides, flags.HasFlag(OutputStreamInfoFlags.OutputStreamWholeSamples),
                                flags.HasFlag(OutputStreamInfoFlags.OutputStreamSingleSamplePerBuffer),
                                CallerAllocated: !provides, Timestamp100ns: ReadOutputTimestamp(data.Sample)));
                        RecordCompletion(completion, empty: true);
                        continue;
                    }
                    if (length is <= 0 or > 4 * 1024 * 1024)
                        throw new IOException($"Invalid hardware output size: bytes={length}, capacity={maximumLength}, allowed=1..4194304, " +
                            $"codec={settings.VideoCodec}, result=0x{unchecked((uint)result.Code):X8}, sampleStatus={data.Status}, transformStatus={outputStatus}, " +
                            $"streamFlags={(OutputStreamInfoFlags)info.Flags}, allocatedByCaller={!provides}, " +
                            $"outputTime100ns={DescribeOutputTime(data.Sample)}, lastInputTime100ns={Interlocked.Read(ref lastInputTime)}.");
                    CompleteSample(ReadOutputTimestamp(data.Sample));
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
                    VideoBitstream.ValidateConfiguration(settings.VideoCodec, configuration, Width, Height, color);
                }
                VideoBitstream.ValidateFrame(settings.VideoCodec, bytes, configuration);
                if (VideoBitstream.HasConfiguration(settings.VideoCodec, bytes))
                {
                    var current = VideoBitstream.Configuration(settings.VideoCodec, bytes);
                    VideoBitstream.ValidateConfiguration(settings.VideoCodec, current, Width, Height, color);
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

    private static string DescribeOutputTime(IMFSample sample)
    {
        try { return sample.SampleTime.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (SharpGenException error) { return $"unavailable(HRESULT=0x{unchecked((uint)error.ResultCode.Code):X8})"; }
    }

    internal static long ReadOutputTimestamp(IMFSample sample)
    {
        try { return sample.SampleTime; }
        catch (SharpGenException error)
        {
            throw new IOException($"Hardware encoder output has no readable source timestamp: " +
                $"HRESULT=0x{unchecked((uint)error.ResultCode.Code):X8}.", error);
        }
    }

    private void CompleteSample(long timestamp)
    {
        if (timestamp < 0) throw new IOException("Hardware encoder produced a negative output timestamp.");
        var completion = samples?.Complete(timestamp, empty: false) ?? (Completed: 1, Dropped: 0);
        RecordCompletion(completion, empty: false);
    }

    private void RecordCompletion((int Completed, int Dropped) completion, bool empty)
    {
        for (var index = 0; index < completion.Completed; index++) inputFlow.OutputProduced();
        if (empty) Interlocked.Increment(ref emptyOutputSamples);
        if (completion.Dropped == 0) return;
        var count = Interlocked.Add(ref droppedFrames, completion.Dropped);
        var now = Environment.TickCount64;
        if (count == completion.Dropped || now - lastDropWarningAt >= 5000)
        {
            lastDropWarningAt = now;
            diagnostic?.Invoke($"Encoder warning: {Name} skipped {count} input frame(s), including empty/no-payload or unreported dropped samples. " +
                "Empty data was not sent; casting continues with valid encoded frames.");
        }
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
