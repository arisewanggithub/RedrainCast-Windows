using System.Text.Json.Nodes;
using RedrainCast.Protocol;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RedrainCast.Send.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace RedrainCast.Send.Media;

public static class MediaDiagnostics
{
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
        try
        {
            peer = await listener.AcceptTcpClientAsync(cancellationToken);
            var stream = peer.GetStream();
            var hello = await Wire.ReadAsync(stream, cancellationToken);
            if (hello.Type != MessageType.Hello) throw new ProtocolException("Self-test expected HELLO.");
            await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0,
                JsonData.Object(("role", "receiver"), ("versions", JsonData.Value(new[] { 3 })), ("name", "Local diagnostic receiver"))), cancellationToken);
            var request = await Wire.ReadAsync(stream, cancellationToken);
            if (request.Type != MessageType.SessionRequest ||
                request.Json().GetProperty("audio").GetProperty("enabled").GetBoolean() != settings.EnableAudio ||
                request.Json().GetProperty("video").GetProperty("codec").GetString() != settings.VideoCodec)
                throw new ProtocolException("Self-test received a different codec/audio request.");
            var session = request.Session;
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, session,
                JsonData.Object(("tracks", JsonData.Value(settings.EnableAudio ? new[] { 1, 2 } : [1])))), cancellationToken);
            var configured = 0;
            var expected = settings.EnableAudio ? 6 : 2;
            byte[] configuration = [];
            var count = 0;
            var audioCount = 0;
            long lastPts = -1;
            long firstVideoPts = -1, firstAudioPts = -1, lastAudioPts = -1;
            while (true)
            {
                var message = await Wire.ReadAsync(stream, cancellationToken);
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
            peer?.Dispose();
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
            using var processor = new GpuProcessor(device, Width, Height, Width, Height, settings.MaxFps);
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
        return $"{codec} configured at {fps} FPS: {count} real hardware video access units plus at least 10 real WASAPI PCM packets through localhost; both-track readiness and Stop acknowledged. Diagnostic intentionally does not mute any output, record content or contact LAN peers.";
    }

    public static async Task<string> SelfTestAsync(CancellationToken cancellationToken, int fps = 60, string codec = "h264",
        int frameCount = 30, int width = 640, int height = 360, int bitrate = 8_000_000, bool captureTiming = false,
        bool staticContent = false, bool duplicateTimestamps = false, bool highEntropy = false,
        Action<EncodedVideo>? observeOutput = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameCount, 3600);
        if (width is < 64 or > 3840 || height is < 64 or > 3840 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Probe dimensions must be even values from 64 to 3840.");
        var settings = new CaptureSettings(MaxSize: Math.Max(width, height), MaxFps: fps, VideoBitrate: bitrate, VideoCodec: codec);
        settings.Validate();
        using var device = WindowsVideoSource.CreateHardwareDevice();
        using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        await using var surfaces = new GpuSurfacePool(device, width, height, detail => System.Diagnostics.Trace.TraceError(detail));
        using var encoder = new HardwareEncoder(device, width, height, settings);
        using var processor = new GpuProcessor(device, width, height, width, height, fps);
        using var texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height,
            1, 1, BindFlags.RenderTarget));
        using var target = device.CreateRenderTargetView(texture);
        using var pattern = highEntropy ? new GeneratedPattern(device) : null;
        var pacer = new FramePacer(fps);
        encoder.Start();
        var sent = 0;
        var received = 0;
        var sawIdr = false;
        var lastPts = -1L;
        var lastInputPts = -1L;
        var watch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Math.Max(15, frameCount / (double)fps * 3 + 15 + frameCount / 120.0 * 0.3));
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
                sawIdr |= VideoBitstream.IsKeyFrame(codec, output.Data, output.Configuration);
                observeOutput?.Invoke(output);
                received++;
            }
            var now = (long)((Int128)Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
            if (encoder.CanAcceptInput && (!captureTiming || pacer.ShouldCapture(now)))
            {
                using var surface = surfaces.TryRent();
                if (surface is not null)
                {
                    if (pattern is not null) pattern.Render(target, width, height, sent);
                    else context.ClearRenderTargetView(target, new Color4(staticContent ? 0.3f : (sent % 30) / 30f, 0.3f, 0.7f, 1));
                    processor.ConvertInto(texture, width, height, surface.Texture);
                    var inputPts = captureTiming ? now : sent * 10_000_000L / fps;
                    if (duplicateTimestamps && sent > 0 && sent % 60 == 0) inputPts = lastInputPts;
                    encoder.Submit(surface, inputPts);
                    lastInputPts = inputPts;
                    sent++;
                    if (captureTiming && sent % 120 == 0) await Task.Delay(300, cancellationToken);
                }
            }
            await Task.Delay(2, cancellationToken);
        }
        if (!sawIdr) throw new IOException("Hardware encoder produced no authentic IDR.");
        if (frameCount >= 64 && surfaces.RecycledSamples < received - 32)
            throw new IOException($"Encoder input surfaces were not recycled: {surfaces.Diagnostics}");
        var bufferLengths = encoder.InputBufferLengths;
        if (bufferLengths.Current <= 0 || bufferLengths.Current != bufferLengths.Maximum)
            throw new IOException("Hardware encoder input did not have an explicit, full GPU-buffer length.");
        return $"{encoder.Name}: GPU BGRA -> NV12 -> hardware {codec} {width}x{height} at {fps} FPS/{bitrate} bits/s, " +
            $"captureTiming={captureTiming}, staticContent={staticContent}, duplicateTimestamps={duplicateTimestamps}, highEntropy={highEntropy}; " +
            $"sent={sent}, {received} access units, droppedInputs={encoder.DroppedFrames}, emptyOutputSamples={encoder.EmptyOutputSamples}, " +
            $"real codec configuration/keyframe, monotonic PTS, {watch.Elapsed.TotalMilliseconds:F0} ms; " +
            $"inputBufferLengthBefore={bufferLengths.Before}, inputBufferLength={bufferLengths.Current}, inputBufferMaximum={bufferLengths.Maximum}; " +
            $"gpuVendor=0x{encoder.VendorId:X4}, requestedCompatibility={(EncoderCompatibility.ForVendor(encoder.VendorId).Count == 0 ? "default" : "CameraRecord/BFrames=0")}; {surfaces.Diagnostics}. " +
            "This synthetic test does not measure live capture FPS.";
    }

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
