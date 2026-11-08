using System.Text.Json.Nodes;
using RedrainCast.Protocol;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using RedrainCast.Send.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace RedrainCast.Send.Media;

public static class MediaDiagnostics
{
    private static string FrameRateDescription(int fps) => fps == 0 ? "Uncapped" : $"{fps} FPS";

    public sealed record HdrColorProbeReport(EncodedVideo Frame, HdrVideoFormat Color,
        int Width, int Height, int MaximumCodeError, string Encoder);

    public static async Task<HdrColorProbeReport> EncodeHdrColorProbeAsync(CancellationToken token,
        bool checkGpuBudget = false)
    {
        token.ThrowIfCancellationRequested();
        const int inputWidth = 1280, inputHeight = 720;
        var color = new HdrVideoFormat(true, new HdrStaticMetadata(1000));
        var settings = new CaptureSettings(MaxSize: 640, MaxFps: 30, VideoBitrate: 4_000_000,
            VideoCodec: "h265", VideoColor: color);
        settings.Validate();
        var (width, height) = settings.OutputSize(inputWidth, inputHeight);
        var sourceColor = new CaptureColor(true, 80, 1000);
        using var device = WindowsVideoSource.CreateHardwareDevice();
        if (checkGpuBudget) CheckGpuBudget(device);
        using var input = CreateHdrProbeTexture(device, inputWidth, inputHeight);
        using var surface = device.CreateTexture2D(new Texture2DDescription(Format.P010, (uint)width, (uint)height,
            1, 1, BindFlags.RenderTarget));
        using var converter = new GpuProcessor(device, inputWidth, inputHeight, width, height, settings.NominalFramesPerSecond,
            hdr: sourceColor.Hdr, nativeHdr10: settings.VideoColor?.Hdr10 == true);
        converter.ConvertInto(input, inputWidth, inputHeight, surface, sourceColor);
        var codeError = ValidateHdrProbeSurface(device, surface, width, height);
        if (codeError > 1) throw new IOException($"Native HDR GPU color probe exceeded one 10-bit code value: error={codeError}.");
        using var encoder = new HardwareEncoder(device, width, height, settings);
        encoder.Start();
        var elapsed = Stopwatch.StartNew();
        var submitted = 0;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            token.ThrowIfCancellationRequested();
            if (submitted < 24 && encoder.CanAcceptInput)
                encoder.Submit(surface, submitted++ * 10_000_000L / settings.NominalFramesPerSecond);
            foreach (var frame in encoder.TakeOutput())
            {
                VideoBitstream.ValidateConfiguration("h265", frame.Configuration, width, height, color);
                if (!VideoBitstream.IsKeyFrame("h265", frame.Data, frame.Configuration))
                    throw new IOException("Native HDR color probe did not start with a random-access picture.");
                return new(frame, color, width, height, codeError, encoder.Name);
            }
            await Task.Delay(1, token);
        }
        throw new IOException($"Native HDR color probe produced no packet in 15 seconds: {encoder.Diagnostics}");
    }

    private static readonly Vector3[] HdrProbeColors =
        [new(12.5f), new(12.5f, 0, 0), new(0, 12.5f, 0), new(0, 0, 12.5f)];

    private static unsafe ID3D11Texture2D CreateHdrProbeTexture(ID3D11Device device, int width, int height)
    {
        var data = new Half[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var rgb = HdrProbeColors[(x >= width / 2 ? 1 : 0) + (y >= height / 2 ? 2 : 0)];
                var offset = (y * width + x) * 4;
                data[offset] = (Half)rgb.X; data[offset + 1] = (Half)rgb.Y;
                data[offset + 2] = (Half)rgb.Z; data[offset + 3] = (Half)1;
            }
        fixed (Half* pointer = data)
            return device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, (uint)width, (uint)height,
                1, 1, BindFlags.ShaderResource), new SubresourceData((nint)pointer, (uint)width * 8));
    }

    private static unsafe int ValidateHdrProbeSurface(ID3D11Device device, ID3D11Texture2D surface, int width, int height)
    {
        using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        using var staging = device.CreateTexture2D(new Texture2DDescription(Format.P010, (uint)width, (uint)height,
            1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        context.CopyResource(staging, surface);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var maximumError = 0;
            for (var index = 0; index < HdrProbeColors.Length; index++)
            {
                var rgb = HdrProbeColors[index];
                var wide = new Vector3(Vector3.Dot(rgb, new(.627404f, .329283f, .0433136f)),
                    Vector3.Dot(rgb, new(.069097f, .919540f, .0113612f)),
                    Vector3.Dot(rgb, new(.0163916f, .0880132f, .895595f)));
                var pq = new Vector3(HdrLuminance.ToPq(wide.X * 80), HdrLuminance.ToPq(wide.Y * 80),
                    HdrLuminance.ToPq(wide.Z * 80));
                var luma = Vector3.Dot(pq, new(.2627f, .6780f, .0593f));
                int[] expected = [(int)MathF.Round(64 + 876 * luma),
                    (int)MathF.Round(512 + 896 * (pq.Z - luma) / 1.8814f),
                    (int)MathF.Round(512 + 896 * (pq.X - luma) / 1.4746f)];
                var x = width / 4 + (index % 2) * width / 2;
                var y = height / 4 + (index / 2) * height / 2;
                var actualY = *(ushort*)((byte*)mapped.DataPointer + y * mapped.RowPitch + x * 2);
                var uv = (ushort*)((byte*)mapped.DataPointer + height * mapped.RowPitch + y / 2 * mapped.RowPitch + (x & ~1) * 2);
                int[] actual = [actualY, uv[0], uv[1]];
                for (var component = 0; component < 3; component++)
                {
                    if ((actual[component] & 63) != 0) throw new IOException("Native HDR P010 contains nonzero padding bits.");
                    maximumError = Math.Max(maximumError, Math.Abs((actual[component] >> 6) - expected[component]));
                }
            }
            return maximumError;
        }
        finally { context.Unmap(staging, 0); }
    }

    public static void CheckGpuBudget(ID3D11Device device)
    {
        using var memory = new GpuMemoryDiagnostics(device);
        memory.CheckBudget();
    }

    public static async Task<EncodedVideo> EncodeColorProbeAsync(string codec, CancellationToken token, bool checkGpuBudget = false)
    {
        await using var video = new GeneratedVideo(new(MaxSize: 640, MaxFps: 30, VideoCodec: codec), checkGpuBudget: checkGpuBudget);
        await using var frames = video.ReadFramesAsync(token).GetAsyncEnumerator(token);
        if (!await frames.MoveNextAsync()) throw new IOException("Hardware color probe produced no encoded video.");
        return frames.Current;
    }

    public static CaptureSource OwnWindow(nint window) =>
        CaptureInterop.OwnWindowForDiagnostics(window);

    public static async Task<int> LoopbackAsync(IVideoSource source, CaptureSettings settings,
        Action<string> state, CancellationToken cancellationToken, IAudioSource? audio = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, "Local diagnostic receiver", false),
            settings, source, state, stop.Token, audio);
        TcpClient? peer = null;
        var mediaPeers = new List<TcpClient>();
        using var reads = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pendingReads = new List<Task<Message>?>();
        try
        {
            peer = await listener.AcceptTcpClientAsync(cancellationToken);
            var stream = peer.GetStream();
            var hello = await Wire.ReadAsync(stream, cancellationToken);
            if (hello.Type != MessageType.Hello) throw new ProtocolException("Self-test expected HELLO.");
            await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0,
                JsonData.Object(("role", "receiver"), ("versions", JsonData.Value(new[] { 3 })), ("name", "Local diagnostic receiver"),
                    ("uncappedFps", true))), cancellationToken);
            var request = await Wire.ReadAsync(stream, cancellationToken);
            if (request.Type != MessageType.SessionRequest ||
                request.Json().GetProperty("audio").GetProperty("enabled").GetBoolean() != settings.EnableAudio ||
                request.Json().GetProperty("video").GetProperty("codec").GetString() != settings.VideoCodec ||
                request.Json().GetProperty("video").GetProperty("maxFps").GetInt32() != settings.MaxFps)
                throw new ProtocolException("Self-test received a different codec/audio request.");
            var session = request.Session;
            var mediaToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, session,
                JsonData.Object(("tracks", JsonData.Value(settings.EnableAudio ? new[] { 1, 2 } : [1])),
                    ("mediaToken", mediaToken))), cancellationToken);
            var streams = new List<NetworkStream> { stream };
            foreach (var channel in new[] { "video", "audio" })
            {
                var mediaPeer = await listener.AcceptTcpClientAsync(cancellationToken);
                mediaPeers.Add(mediaPeer);
                var media = mediaPeer.GetStream();
                var association = await Wire.ReadAsync(media, cancellationToken);
                if (association.Type != MessageType.Hello || association.Session != session ||
                    StrictJson.String(association.Json(), "role", 16) != "media" ||
                    StrictJson.String(association.Json(), "channel", 16) != channel ||
                    StrictJson.String(association.Json(), "token", 64) != mediaToken)
                    throw new ProtocolException("Self-test received an invalid media association.");
                await Wire.WriteAsync(media, Message.Control(MessageType.Hello, session,
                    JsonData.Object(("channel", channel))), cancellationToken);
                streams.Add(media);
            }
            foreach (var lane in streams) pendingReads.Add(Wire.ReadAsync(lane, reads.Token).AsTask());
            var configured = 0;
            var expected = settings.EnableAudio ? 6 : 2;
            byte[] configuration = [];
            var count = 0;
            var audioCount = 0;
            long lastPts = -1;
            long firstVideoPts = -1, firstAudioPts = -1, lastAudioPts = -1;
            while (true)
            {
                var completed = pendingReads[0]?.IsCompleted == true
                    ? pendingReads[0]!
                    : await Task.WhenAny(pendingReads.OfType<Task<Message>>());
                var lane = pendingReads.IndexOf(completed);
                Message message;
                try { message = await completed; }
                catch (EndOfStreamException) when (lane != 0)
                {
                    pendingReads[lane] = null;
                    continue;
                }
                pendingReads[lane] = Wire.ReadAsync(streams[lane], reads.Token).AsTask();
                if (message.Session != session) throw new ProtocolException("Self-test session changed.");
                switch (message.Type)
                {
                    case MessageType.StreamConfig:
                        var bit = 1 << (int)message.Track;
                        if ((configured & bit) != 0) throw new ProtocolException("Self-test saw a second configuration.");
                        var config = message.Json();
                        if (message.Track == 1)
                        {
                            configuration = Convert.FromBase64String(config.GetProperty("csd")[0].GetString()!);
                            VideoBitstream.ValidateConfiguration(settings.VideoCodec, configuration,
                                config.GetProperty("width").GetInt32(), config.GetProperty("height").GetInt32());
                        }
                        else if (message.Track != 2 || !settings.EnableAudio || config.GetProperty("codec").GetString() != "raw" ||
                                 config.GetProperty("sampleRate").GetInt32() != 48000 || config.GetProperty("channels").GetInt32() != 2 ||
                                 config.GetProperty("sampleFormat").GetString() != "s16le")
                            throw new ProtocolException("Self-test audio configuration differs from raw stereo 48 kHz PCM.");
                        await Wire.WriteAsync(stream, new(MessageType.StreamReady, session, message.Track, 1, -1, 0,
                            """{"ok":true}"""u8.ToArray()), cancellationToken);
                        configured |= bit;
                        break;
                    case MessageType.VideoFrame:
                        if (configured != expected || message.PtsUs < lastPts ||
                            count == 0 && !VideoBitstream.IsKeyFrame(settings.VideoCodec, message.Payload, configuration))
                            throw new ProtocolException("Self-test saw media before readiness, reversed PTS or no initial IDR.");
                        VideoBitstream.ValidateFrame(settings.VideoCodec, message.Payload, configuration);
                        if (firstVideoPts < 0) firstVideoPts = message.PtsUs;
                        lastPts = message.PtsUs;
                        count++;
                        if (count >= 10 && (!settings.EnableAudio || audioCount >= 10)) stop.Cancel();
                        break;
                    case MessageType.AudioPacket:
                        if (configured != expected || !settings.EnableAudio || message.Payload.Length % 4 != 0 ||
                            message.PtsUs < lastAudioPts)
                            throw new ProtocolException("Self-test saw invalid or premature PCM audio.");
                        lastAudioPts = message.PtsUs;
                        if (firstAudioPts < 0) firstAudioPts = message.PtsUs;
                        if (firstVideoPts >= 0 && Math.Abs(firstAudioPts - firstVideoPts) > 10_000_000)
                            throw new ProtocolException("Audio/video timestamps do not share the expected monotonic source clock.");
                        audioCount++;
                        if (count >= 10 && audioCount >= 10) stop.Cancel();
                        break;
                    case MessageType.Ping:
                        await Wire.WriteAsync(stream, Message.Control(MessageType.Pong, session,
                            JsonData.Object(("nonce", StrictJson.Integer(message.Json(), "nonce")))), cancellationToken);
                        break;
                    case MessageType.Stop:
                        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, session,
                            JsonData.Object(("reason", StrictJson.String(message.Json(), "reason", 1024)))), cancellationToken);
                        if (count < 10 || settings.EnableAudio && audioCount < 10)
                            throw new IOException("Self-test stopped before ten real video/audio packets.");
                        await cast;
                        return count;
                    case MessageType.Error:
                        throw new IOException($"Self-test sender error: {message.Json()}");
                    default:
                        throw new ProtocolException($"Unexpected self-test message: {message.Type}.");
                }

            }
        }
        finally
        {
            stop.Cancel();
            reads.Cancel();
            foreach (var mediaPeer in mediaPeers) mediaPeer.Dispose();
            peer?.Dispose();
            foreach (var pending in pendingReads.OfType<Task<Message>>())
                try { await pending; }
                catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
            await cast;
        }
    }

    private sealed class ProbeAudio : IAudioSource
    {
        private readonly WindowsAudioSource source = new();
        public JsonNode Configuration => source.Configuration;
        public void BeginTransmission() { }
        public IAsyncEnumerable<AudioPacket> ReadPacketsAsync(CancellationToken token) => source.ReadPacketsAsync(token);
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }

    internal sealed class GeneratedVideo(CaptureSettings settings, int width = 640, int height = 360, bool checkGpuBudget = false) : IVideoSource
    {
        public string EncoderName => "Diagnostic hardware source";
        public int Width => width;
        public int Height => height;
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            using var device = WindowsVideoSource.CreateHardwareDevice();
            if (checkGpuBudget) CheckGpuBudget(device);
            using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
            await using var surfaces = new GpuSurfacePool(device, Width, Height, detail => System.Diagnostics.Trace.TraceError(detail));
            using var encoder = new HardwareEncoder(device, Width, Height, settings);
            using var processor = new GpuProcessor(device, Width, Height, Width, Height, settings.NominalFramesPerSecond);
            using var texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, 1, 1, BindFlags.RenderTarget));
            using var target = device.CreateRenderTargetView(texture);
            var pacer = new FramePacer(settings.MaxFps);
            using var wait = new CaptureWait(token);
            encoder.Start();
            var frame = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                foreach (var output in encoder.TakeOutput())
                {
                    yield return output;
                }
                var now = (long)((Int128)Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
                if (encoder.CanAcceptInput && pacer.ShouldCapture(now))
                {
                    using var surface = surfaces.TryRent();
                    if (surface is not null)
                    {
                        context.ClearRenderTargetView(target, new Color4((frame++ % 30) / 30f, 0.3f, 0.7f, 1));
                        processor.ConvertInto(texture, Width, Height, surface.Texture);
                        encoder.Submit(surface, now);
                    }
                }
                wait.Pause();
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public static async Task<string> AudioVideoProbeAsync(string codec, CancellationToken token, int fps = 60)
    {
        var settings = new CaptureSettings(MaxSize: 640, MaxFps: fps, EnableAudio: true, VideoCodec: codec);
        await using var video = new GeneratedVideo(settings);
        await using var audio = new ProbeAudio();
        var count = await LoopbackAsync(video, settings, _ => { }, token, audio);
        return $"{codec} configured at {FrameRateDescription(fps)}: {count} real hardware video access units plus at least 10 real WASAPI PCM packets through localhost; both-track readiness and Stop acknowledged. Diagnostic intentionally does not mute any output, record content or contact LAN peers.";
    }

    public static async Task<string> SelfTestAsync(CancellationToken cancellationToken, int fps = 60, string codec = "h264",
        int frameCount = 30, int width = 640, int height = 360, int bitrate = 8_000_000, bool captureTiming = false,
        bool staticContent = false, bool duplicateTimestamps = false, bool highEntropy = false,
        Action<EncodedVideo>? observeOutput = null, int? sourceFramesPerSecond = null, bool verifyExactTiming = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameCount, 3600);
        if (width is < 64 or > 3840 || height is < 64 or > 3840 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Probe dimensions must be even values from 64 to 3840.");
        var settings = new CaptureSettings(MaxSize: Math.Max(width, height), MaxFps: fps, VideoBitrate: bitrate, VideoCodec: codec);
        settings.Validate();
        var sourceFps = sourceFramesPerSecond ?? (fps == 0 ? 240 : fps);
        if (sourceFps is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(sourceFramesPerSecond));
        if (verifyExactTiming && duplicateTimestamps)
            throw new ArgumentException("Exact timing verification requires fresh source timestamps.");
        using var device = WindowsVideoSource.CreateHardwareDevice();
        using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        await using var surfaces = new GpuSurfacePool(device, width, height, detail => System.Diagnostics.Trace.TraceError(detail));
        using var encoder = new HardwareEncoder(device, width, height, settings);
        using var processor = new GpuProcessor(device, width, height, width, height, settings.NominalFramesPerSecond);
        using var texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height,
            1, 1, BindFlags.RenderTarget));
        using var target = device.CreateRenderTargetView(texture);
        using var pattern = highEntropy ? new GeneratedPattern(device) : null;
        var pacer = new FramePacer(fps);
        using var proofWait = verifyExactTiming ? new CaptureWait(cancellationToken) : null;
        encoder.Start();
        var sent = 0;
        var received = 0;
        var sawIdr = false;
        var keyFrames = 0;
        var lastPts = -1L;
        var lastInputPts = -1L;
        var pendingTimes = new Queue<long>();
        var verifiedTiming = 0;
        var peakPending = 0;
        var firstOutputTime = -1L;
        var lastOutputTime = -1L;
        var minimumOutputInterval = long.MaxValue;
        var maximumOutputInterval = 0L;
        var expectedDuration = TimeSpan.TicksPerSecond / settings.NominalFramesPerSecond;
        if (verifyExactTiming)
            encoder.OutputTiming += (timestamp, duration) =>
            {
                if (!pendingTimes.TryDequeue(out var expected) || timestamp != expected || duration != expectedDuration)
                    throw new IOException($"Hardware changed sample timing: expectedTime100ns={expected}, actualTime100ns={timestamp}, " +
                        $"expectedNominalDuration100ns={expectedDuration}, actualDuration100ns={duration}.");
                if (firstOutputTime < 0) firstOutputTime = timestamp;
                if (lastOutputTime >= 0)
                {
                    minimumOutputInterval = Math.Min(minimumOutputInterval, timestamp - lastOutputTime);
                    maximumOutputInterval = Math.Max(maximumOutputInterval, timestamp - lastOutputTime);
                }
                lastOutputTime = timestamp;
                verifiedTiming++;
            };
        var watch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Math.Max(15, frameCount / (double)sourceFps * 3 + 15 + frameCount / 120.0 * 0.3));
        while (received < frameCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed > timeout) throw new TimeoutException(
                $"Hardware encode self-test timed out: sent={sent}, received={received}, target={frameCount}; {encoder.Diagnostics}");
            foreach (var output in encoder.TakeOutput())
            {
                VideoBitstream.ValidateConfiguration(codec, output.Configuration, width, height);
                if (output.PtsUs < lastPts) throw new IOException("Hardware output timestamps moved backwards.");
                lastPts = output.PtsUs;
                if (VideoBitstream.IsKeyFrame(codec, output.Data, output.Configuration))
                {
                    sawIdr = true;
                    keyFrames++;
                }
                observeOutput?.Invoke(output);
                received++;
            }
            var now = (long)((Int128)Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
            if ((!verifyExactTiming || sent < frameCount) &&
                encoder.CanAcceptInput && (!captureTiming || pacer.ShouldCapture(now)))
            {
                using var surface = surfaces.TryRent();
                if (surface is not null)
                {
                    if (pattern is not null) pattern.Render(target, width, height, sent);
                    else context.ClearRenderTargetView(target, new Color4(staticContent ? 0.3f : (sent % 30) / 30f, 0.3f, 0.7f, 1));
                    processor.ConvertInto(texture, width, height, surface.Texture);
                    var inputPts = captureTiming ? now : sent * TimeSpan.TicksPerSecond / sourceFps;
                    if (duplicateTimestamps && sent > 0 && sent % 60 == 0) inputPts = lastInputPts;
                    if (verifyExactTiming)
                    {
                        if (inputPts <= lastInputPts) throw new IOException("Synthetic input timestamp was not fresh.");
                        if (pendingTimes.Count >= 64) throw new IOException("Hardware timing verification exceeded its bounded pending-input budget.");
                        pendingTimes.Enqueue(inputPts);
                        peakPending = Math.Max(peakPending, pendingTimes.Count);
                        if (sent == frameCount / 2) encoder.RequestKeyFrame();
                    }
                    encoder.Submit(surface, inputPts);
                    lastInputPts = inputPts;
                    sent++;
                    if (captureTiming && !verifyExactTiming && sent % 120 == 0) await Task.Delay(300, cancellationToken);
                }
            }
            if (proofWait is not null) proofWait.Pause();
            else await Task.Delay(2, cancellationToken);
        }
        if (!sawIdr) throw new IOException("Hardware encoder produced no authentic IDR.");
        if (verifyExactTiming && frameCount >= 64 && keyFrames < 2)
            throw new IOException("Hardware encoder did not produce a second random-access picture after the keyframe request.");
        var encodedFps = received / watch.Elapsed.TotalSeconds;
        if (verifyExactTiming && (verifiedTiming != frameCount || pendingTimes.Count != 0 || encoder.DroppedFrames != 0))
            throw new IOException("Hardware did not preserve every synthetic input timestamp.");
        if (verifyExactTiming && fps == 0 && encodedFps <= 60)
            throw new IOException($"Uncapped hardware proof did not exceed the 60 FPS nominal hint: measured={encodedFps:F2} FPS; {encoder.Diagnostics}");
        if (frameCount >= 64 && surfaces.RecycledSamples < received - 32)
            throw new IOException($"Encoder input surfaces were not recycled: {surfaces.Diagnostics}");
        var bufferLengths = encoder.InputBufferLengths;
        if (bufferLengths.Current <= 0 || bufferLengths.Current != bufferLengths.Maximum)
            throw new IOException("Hardware encoder input did not have an explicit, full GPU-buffer length.");
        return $"{encoder.Name}: GPU BGRA -> NV12 -> hardware {codec} {width}x{height} at {FrameRateDescription(fps)}/{bitrate} bits/s, " +
            $"maxFps={fps}, nominalFps={settings.NominalFramesPerSecond}, syntheticSourceTiming={(captureTiming ? "variable QPC" : $"{sourceFps} FPS")}, " +
            $"captureTiming={captureTiming}, staticContent={staticContent}, duplicateTimestamps={duplicateTimestamps}, highEntropy={highEntropy}; " +
            $"sent={sent}, {received} access units, droppedInputs={encoder.DroppedFrames}, emptyOutputSamples={encoder.EmptyOutputSamples}, " +
            $"measuredEncodeFps={encodedFps:F2}, exactTimestampAndDurationSamples={verifiedTiming}, nominalDuration100ns={expectedDuration}, peakPendingTiming={peakPending}/64; " +
            $"keyFrames={keyFrames}, firstOutputTime100ns={firstOutputTime}, lastOutputTime100ns={lastOutputTime}, " +
            $"minimumOutputInterval100ns={(minimumOutputInterval == long.MaxValue ? 0 : minimumOutputInterval)}, maximumOutputInterval100ns={maximumOutputInterval}; " +
            $"real codec configuration/keyframe, monotonic PTS, {watch.Elapsed.TotalMilliseconds:F0} ms; " +
            $"inputBufferLengthBefore={bufferLengths.Before}, inputBufferLength={bufferLengths.Current}, inputBufferMaximum={bufferLengths.Maximum}; " +
            $"gpuVendor=0x{encoder.VendorId:X4}, requestedCompatibility={(EncoderCompatibility.ForVendor(encoder.VendorId).Count == 0 ? "default" : "CameraRecord/BFrames=0")}; {surfaces.Diagnostics}. " +
            "This synthetic test does not measure live capture FPS.";
    }

    public static Task<string> UncappedEncodeProbeAsync(CancellationToken cancellationToken, string codec = "h264",
        int frameCount = 600, int sourceFramesPerSecond = 240, Action<EncodedVideo>? observeOutput = null) =>
        SelfTestAsync(cancellationToken, fps: 0, codec: codec, frameCount: frameCount,
            captureTiming: true, sourceFramesPerSecond: sourceFramesPerSecond, verifyExactTiming: true,
            observeOutput: observeOutput);

    private sealed class GeneratedPattern : IDisposable
    {
        private const string Shader = """
            cbuffer Seed : register(b0) { uint Frame; uint3 Padding; };
            float4 VS(uint id : SV_VertexID) : SV_Position {
                float2 uv = float2((id << 1) & 2, id & 2);
                return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            }
            uint Hash(uint x) {
                x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15;
                x *= 0x846ca68b; return x ^ (x >> 16);
            }
            float4 PS(float4 p : SV_Position) : SV_Target {
                uint value = Hash((uint)p.x * 1973 + (uint)p.y * 9277 + Frame * 26699);
                return float4(value & 255, (value >> 8) & 255, (value >> 16) & 255, 255) / 255.0;
            }
            """;
        private readonly ID3D11DeviceContext context;
        private readonly ID3D11VertexShader vertex;
        private readonly ID3D11PixelShader pixel;
        private readonly ID3D11Buffer constants;
        private readonly ID3D11RasterizerState rasterizer;

        public GeneratedPattern(ID3D11Device device)
        {
            context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
            try
            {
                ReadOnlyMemory<byte> vertexCode = Compiler.Compile(Shader, "VS", "EncoderPattern", "vs_5_0");
                ReadOnlyMemory<byte> pixelCode = Compiler.Compile(Shader, "PS", "EncoderPattern", "ps_5_0");
                vertex = device.CreateVertexShader(vertexCode.Span);
                pixel = device.CreatePixelShader(pixelCode.Span);
                constants = device.CreateBuffer(16, BindFlags.ConstantBuffer);
                rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
            }
            catch
            {
                rasterizer?.Dispose(); constants?.Dispose(); pixel?.Dispose(); vertex?.Dispose(); context.Dispose();
                throw;
            }
        }

        public void Render(ID3D11RenderTargetView target, int width, int height, int frame)
        {
            var seed = new UInt4((uint)frame, 0, 0, 0);
            context.UpdateSubresource(in seed, constants);
            context.IASetInputLayout(null);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(vertex);
            context.PSSetShader(pixel);
            context.PSSetConstantBuffer(0, constants);
            context.RSSetState(rasterizer);
            context.RSSetViewport(0, 0, width, height);
            context.OMSetBlendState(null);
            context.OMSetRenderTargets(target);
            try { context.Draw(3, 0); }
            finally { context.OMSetRenderTargets(ReadOnlySpan<ID3D11RenderTargetView>.Empty); }
        }

        public void Dispose()
        {
            rasterizer.Dispose(); constants.Dispose(); pixel.Dispose(); vertex.Dispose(); context.Dispose();
        }
    }
}
