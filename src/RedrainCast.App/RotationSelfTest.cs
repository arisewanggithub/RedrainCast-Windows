using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RedrainCast.Core;
using ScrcpyReceive.Core;
using ScrcpySend.Media;
using ReceiverWire = ScrcpyReceive.Core.Wire;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    internal async Task RunRotationSelfTestAsync(string reportFile)
    {
        string? failure = null;
        var observed = new List<object>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var control = new TcpClient { NoDelay = true };
        using var videoPeer = new TcpClient { NoDelay = true };
        using var audioPeer = new TcpClient { NoDelay = true };
        const long session = 73;
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        var wasOnTop = presenter?.IsAlwaysOnTop;
        try
        {
            if (presenter is not null) presenter.IsAlwaysOnTop = true;
            var server = receiver ?? throw new IOException("Rotation test listener is unavailable.");
            var frames = new List<ScrcpySend.Core.EncodedVideo>();
            await Task.Run(() => MediaDiagnostics.SelfTestAsync(timeout.Token, frameCount: 12, observeOutput: frames.Add));
            await control.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            await ReceiverWire.WriteAsync(control.GetStream(), Packet.Control(PacketType.Hello, 0,
                new { role = "sender", versions = new[] { 3 }, name = "Synthetic Android rotation", videoRotation = true }), timeout.Token);
            var hello = await ReadControlAsync(PacketType.Hello);
            if (!JsonObject.Boolean(hello.Object(), "videoRotation")) throw new IOException("GPU rotation was not negotiated.");
            await ReceiverWire.WriteAsync(control.GetStream(), Packet.Control(PacketType.SessionRequest, session,
                new ScrcpySend.Core.CaptureSettings(MaxSize: 640, MaxFps: 30).Request("Synthetic hardware H.264")), timeout.Token);
            var accepted = await ReadControlAsync(PacketType.SessionAccept);
            var token = JsonObject.Text(accepted.Object(), "mediaToken", 64);
            foreach (var (socket, channel) in new[] { (videoPeer, "video"), (audioPeer, "audio") })
            {
                await socket.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
                await ReceiverWire.WriteAsync(socket.GetStream(), Packet.Control(PacketType.Hello, session,
                    new { role = "media", channel, token }), timeout.Token);
                if ((await ReceiverWire.ReadAsync(socket.GetStream(), timeout.Token)).Type != PacketType.Hello)
                    throw new IOException("Rotation test media socket did not attach.");
            }
            foreach (var generation in new[] { 1u, 2u })
            {
                await ReceiverWire.WriteAsync(videoPeer.GetStream(), new(PacketType.StreamConfig, session, 1, generation, -1, 0,
                    JsonSerializer.SerializeToUtf8Bytes(new { codec = "h264", width = 640, height = 360,
                        csd = new[] { Convert.ToBase64String(frames[0].Configuration) } })), timeout.Token);
                await ReadControlAsync(PacketType.StreamReady);
                var index = 0;
                foreach (var turns in new[] { 0, 1, 2, 3, 0 })
                {
                    var frame = frames[index++];
                    var key = ScrcpySend.Core.VideoBitstream.IsKeyFrame("h264", frame.Data, frame.Configuration);
                    await ReceiverWire.WriteAsync(videoPeer.GetStream(), new(PacketType.VideoFrame, session, 1, generation,
                        frame.PtsUs, (key ? 1u : 0u) | (uint)turns << 1, frame.Data), timeout.Token);
                    while (video.LastPresentedPtsUs != frame.PtsUs || video.LastPresentedRotationDegrees != turns * 90 ||
                           !video.IsPlaying)
                    {
                        if (Problem.IsOpen) throw new IOException(Problem.Message);
                        await Task.Delay(20, timeout.Token);
                    }
                    observed.Add(new { generation, rotation = turns * 90, frame.PtsUs, key, presented = video.PresentedFrames });
                }
            }
            if (HomePage.Visibility != Visibility.Collapsed || video.HardwareSurfaceCount < 1 ||
                AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
                throw new IOException("Rotated pictures were not presented as real hardware fullscreen video.");
            await ReceiverWire.WriteAsync(control.GetStream(), Packet.Control(PacketType.Stop, session, new { reason = "test" }), timeout.Token);
            await ReadControlAsync(PacketType.SessionEnd);
            while (roles.Role != CastRole.Idle || video.IsAttached || ReadinessTitle.Text != L("Ready"))
                await Task.Delay(20, timeout.Token);
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            if (receiver is not null) await receiver.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (presenter is not null && wasOnTop is { } original) presenter.IsAlwaysOnTop = original;
        }
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
        {
            passed = failure is null && observed.Count == 10, failure, observed, diagnostics,
            syntheticOnly = true, loopbackOnly = true, physicalAndroidTested = false,
            ready = ReadinessTitle.Text == L("Ready"), detached = !video.IsAttached
        }, new JsonSerializerOptions { WriteIndented = true }));

        async Task<Packet> ReadControlAsync(PacketType expected)
        {
            while (true)
            {
                var packet = await ReceiverWire.ReadAsync(control.GetStream(), timeout.Token);
                if (packet.Type == PacketType.Ping)
                {
                    await ReceiverWire.WriteAsync(control.GetStream(), Packet.Control(PacketType.Pong, packet.Session,
                        new { nonce = JsonObject.Number(packet.Object(), "nonce") }), timeout.Token);
                    continue;
                }
                if (packet.Type != expected) throw new IOException($"Expected {expected}; received {packet.Type}: {packet.Object()}");
                return packet;
            }
        }
    }
}
