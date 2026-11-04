using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ScrcpySend.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScrcpySend.Media;

public static class MediaDiagnostics
{
    public static async Task<EncodedVideo> EncodeColorProbeAsync(string codec, CancellationToken token)
    {
        await using var video = new GeneratedVideo(new(MaxSize: 640, MaxFps: 30, VideoCodec: codec));
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
                new { role = "receiver", versions = new[] { 3 }, name = "Local diagnostic receiver" }), cancellationToken);
            var request = await Wire.ReadAsync(stream, cancellationToken);
            if (request.Type != MessageType.SessionRequest ||
                request.Json().GetProperty("audio").GetProperty("enabled").GetBoolean() != settings.EnableAudio ||
                request.Json().GetProperty("video").GetProperty("codec").GetString() != settings.VideoCodec)
                throw new ProtocolException("Self-test received a different codec/audio request.");
            var session = request.Session;
            await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, session,
                new { tracks = settings.EnableAudio ? new[] { 1, 2 } : [1] }), cancellationToken);
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
                            new { nonce = StrictJson.Integer(message.Json(), "nonce") }), cancellationToken);
                        break;
                    case MessageType.Stop:
                        await Wire.WriteAsync(stream, Message.Control(MessageType.SessionEnd, session,
                            new { reason = StrictJson.String(message.Json(), "reason", 1024) }), cancellationToken);
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
        public object Configuration => source.Configuration;
        public void BeginTransmission() { }
        public IAsyncEnumerable<AudioPacket> ReadPacketsAsync(CancellationToken token) => source.ReadPacketsAsync(token);
        public ValueTask DisposeAsync() => source.DisposeAsync();
    }

    internal sealed class GeneratedVideo(CaptureSettings settings, int width = 640, int height = 360) : IVideoSource
    {
        public string EncoderName => "Diagnostic hardware source";
        public int Width => width;
        public int Height => height;
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            using var device = WindowsVideoSource.CreateHardwareDevice();
            using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
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
                    context.ClearRenderTargetView(target, new Color4((frame++ % 30) / 30f, 0.3f, 0.7f, 1));
                    using var nv12 = processor.Convert(texture, Width, Height);
                    encoder.Submit(nv12, now);
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

    public static async Task<string> SelfTestAsync(CancellationToken cancellationToken, int fps = 60, string codec = "h264")
    {
        using var device = WindowsVideoSource.CreateHardwareDevice();
        using var context = device.ImmediateContext.QueryInterface<ID3D11DeviceContext>();
        using var encoder = new HardwareEncoder(device, 640, 360, new(MaxSize: 640, MaxFps: fps, VideoCodec: codec));
        using var processor = new GpuProcessor(device, 640, 360, 640, 360, fps);
        using var texture = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 640, 360,
            1, 1, BindFlags.RenderTarget));
        using var target = device.CreateRenderTargetView(texture);
        encoder.Start();
        var sent = 0;
        var received = 0;
        var sawIdr = false;
        var lastPts = -1L;
        var watch = Stopwatch.StartNew();
        while (received < 30)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Hardware encode self-test timed out.");
            foreach (var output in encoder.TakeOutput())
            {
                VideoBitstream.ValidateConfiguration(codec, output.Configuration, 640, 360);
                if (output.PtsUs < lastPts) throw new IOException("Hardware output timestamps moved backwards.");
                lastPts = output.PtsUs;
                sawIdr |= VideoBitstream.IsKeyFrame(codec, output.Data, output.Configuration);
                received++;
            }
            if (sent < 40 && encoder.CanAcceptInput)
            {
                context.ClearRenderTargetView(target, new Color4((sent % 30) / 30f, 0.3f, 0.7f, 1));
                using var nv12 = processor.Convert(texture, 640, 360);
                encoder.Submit(nv12, sent * 10_000_000L / fps);
                sent++;
            }
            await Task.Delay(2, cancellationToken);
        }
        if (!sawIdr) throw new IOException("Hardware encoder produced no authentic IDR.");
        return $"{encoder.Name}: GPU BGRA -> NV12 -> hardware {codec} configured at {fps} FPS; {received} access units, real codec configuration/keyframe, monotonic PTS, {watch.Elapsed.TotalMilliseconds:F0} ms. This synthetic test does not measure live capture FPS.";
    }

}
