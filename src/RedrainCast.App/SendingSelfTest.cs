using System.Net;
using System.Net.Sockets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RedrainCast.Core;
using ScrcpySend.Core;
using ScrcpySend.Media;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    private async Task RunInlineSendingSelfTestAsync(List<string> checks)
    {
        settings = new(MaxSize: 640, MaxFps: 30);
        SetSource(MediaDiagnostics.OwnWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)));
        await ExerciseAsync(cast: false);
        checks.Add("A manually entered destination exposes inline Cancel during real TCP negotiation and clears after cancellation.");
        await ExerciseAsync(cast: true);
        checks.Add("Own-window hardware casting shows inline Casting/Stop and actual encoder details; discovery expiry retains Stop through acknowledged cleanup.");

        async Task ExerciseAsync(bool cast)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var target = new ReceiverEndpoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, "Manual test receiver", false);
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var peer = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                var stream = client.GetStream();
                if ((await Wire.ReadAsync(stream, timeout.Token)).Type != MessageType.Hello)
                    throw new InvalidOperationException("Inline UI test expected sender HELLO.");
                await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0,
                    new { role = "receiver", versions = new[] { 3 }, name = "Inline UI test" }), timeout.Token);
                var request = await Wire.ReadAsync(stream, timeout.Token);
                if (request.Type != MessageType.SessionRequest)
                    throw new InvalidOperationException("Inline UI test expected SESSION_REQUEST.");
                requested.SetResult();
                if (!cast)
                {
                    if (await stream.ReadAsync(new byte[1], timeout.Token) != 0)
                        throw new InvalidOperationException("Cancelled negotiation unexpectedly started media.");
                    return;
                }
                var mediaToken = new string('A', 64);
                await Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
                    new { tracks = new[] { 1 }, mediaToken }), timeout.Token);
                using var videoPeer = await AttachAsync("video");
                using var audioPeer = await AttachAsync("audio");
                using var mediaStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                using var controlWriter = new SemaphoreSlim(1);
                var videoPump = ReadVideoAsync();
                try
                {
                    while (true)
                    {
                        var message = await Wire.ReadAsync(stream, timeout.Token);
                        Diagnostic($"Inline peer control: {message.Type}");
                        switch (message.Type)
                        {
                            case MessageType.Ping:
                                await WriteControlAsync(Message.Control(MessageType.Pong, request.Session,
                                    new { nonce = StrictJson.Integer(message.Json(), "nonce") }));
                                break;
                            case MessageType.Stop:
                                await WriteControlAsync(Message.Control(MessageType.SessionEnd, request.Session,
                                    new { reason = "user" }));
                                return;
                            default:
                                throw new InvalidOperationException($"Unexpected inline UI test control: {message.Type}.");
                        }
                    }
                }
                finally
                {
                    mediaStop.Cancel();
                    try { await videoPump; }
                    catch (OperationCanceledException) when (mediaStop.IsCancellationRequested) { }
                }

                async Task<TcpClient> AttachAsync(string channel)
                {
                    var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                    try
                    {
                        var hello = await Wire.ReadAsync(socket.GetStream(), timeout.Token);
                        if (hello.Type != MessageType.Hello || hello.Session != request.Session ||
                            StrictJson.String(hello.Json(), "channel", 16) != channel ||
                            StrictJson.String(hello.Json(), "token", 64) != mediaToken)
                            throw new InvalidOperationException("Invalid inline test media attachment.");
                        await Wire.WriteAsync(socket.GetStream(), Message.Control(MessageType.Hello, request.Session,
                            new { channel }), timeout.Token);
                        return socket;
                    }
                    catch { socket.Dispose(); throw; }
                }

                async Task WriteControlAsync(Message message)
                {
                    await controlWriter.WaitAsync(timeout.Token);
                    try { await Wire.WriteAsync(stream, message, timeout.Token); }
                    finally { controlWriter.Release(); }
                }

                async Task ReadVideoAsync()
                {
                    while (true)
                    {
                        var message = await Wire.ReadAsync(videoPeer.GetStream(), mediaStop.Token);
                        if (message.Type == MessageType.StreamConfig)
                            await WriteControlAsync(new(MessageType.StreamReady, request.Session, 1, 1, -1, 0,
                                """{"ok":true}"""u8.ToArray()));
                        else if (message.Type != MessageType.VideoFrame)
                            throw new InvalidOperationException($"Unexpected inline test video packet: {message.Type}.");
                    }
                }
            }, timeout.Token);
            discovered = [];
            var session = StartSendingAsync(target);
            try
            {
                await requested.Task.WaitAsync(timeout.Token);
                if (cast)
                {
                    while (outgoing?.Phase != OutgoingPhase.Casting)
                    {
                        if (session.IsCompleted)
                            throw new InvalidOperationException($"Outgoing UI test stopped before casting: {Problem.Message}");
                        await Task.Delay(25, timeout.Token);
                    }
                }
                var other = new ReceiverEndpoint(IPAddress.Parse("192.0.2.10"), 27184, "Other busy receiver", true);
                discovered = [other];
                RenderDevices();
                ReceiverList.UpdateLayout();
                if (ReceiverList.ItemsSource is not ReceiverRow[] rows || rows.Length != 2 || !rows[0].IsActive ||
                    rows[0].Endpoint != target || rows[1].Status != "Busy" ||
                    cast && (rows[0].Status != "Casting" || string.IsNullOrEmpty(rows[0].Parameters) || string.IsNullOrEmpty(rows[0].Encoder)))
                    throw new InvalidOperationException("Active receiver row was lost, duplicated, mislabeled or missing encoder details.");
                var button = FindAction(ReceiverList) ?? throw new InvalidOperationException("Inline action was not realized.");
                if (!button.IsEnabled || button.Content?.ToString() != (cast ? "Stop" : "Cancel"))
                    throw new InvalidOperationException("Inline Stop/Cancel binding is incorrect.");
                DeviceAction_Click(button, new RoutedEventArgs());
                if (ReceiverList.ItemsSource is not ReceiverRow[] stopping || stopping[0].Status != "Stopping..." || stopping[0].CanAct)
                    throw new InvalidOperationException("Stopping must retain the active row with its action disabled.");
                await session.WaitAsync(timeout.Token);
                await peer.WaitAsync(timeout.Token);
                if (outgoing is not null || receiver is null || ReadinessTitle.Text != "READY" ||
                    ReceiverList.ItemsSource is not ReceiverRow[] idle || idle.Any(row => row.IsActive))
                    throw new InvalidOperationException("Session cleanup did not remove the manual row and restore receiving.");
            }
            catch (Exception error)
            {
                Diagnostic($"Inline session check failed: {error}");
                throw;
            }
            finally
            {
                StopSending();
                await session;
                timeout.Cancel();
                try { await peer; }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            }
        }
    }

    private static Button? FindAction(DependencyObject root)
    {
        if (root is Button { Tag: ReceiverRow { IsActive: true } } button) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = FindAction(VisualTreeHelper.GetChild(root, index));
            if (found is not null) return found;
        }
        return null;
    }
}
