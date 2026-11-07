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
        var logDirectory = Directory.CreateTempSubdirectory("RedrainCast-ui-diagnostics-");
        var previousLog = diagnosticLog;
        var previousPath = diagnosticLogPath;
        try
        {
            string path;
            using (var log = new DiagnosticLog(logDirectory.FullName))
            {
                diagnosticLog = log;
                diagnosticLogPath = path = log.FilePath;
                const string recoveredDrop = "Encoder warning: self-test skipped 1 input frame; casting continues.";
                var previousTitle = Problem.Title;
                var previousMessage = Problem.Message;
                var previousSeverity = Problem.Severity;
                var previousOpen = Problem.IsOpen;
                Diagnostic(recoveredDrop);
                if (Problem.Title != previousTitle || Problem.Message != previousMessage ||
                    Problem.Severity != previousSeverity || Problem.IsOpen != previousOpen ||
                    !diagnostics.Any(line => line.Contains(recoveredDrop, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Recovered encoder drops must stay in diagnostics without changing the user notification.");
                checks.Add("Recovered encoder frame drops are logged without opening or changing a user notification.");
                settings = new(MaxSize: 640, MaxFps: 30);
                SetSource(MediaDiagnostics.OwnWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)));
                await ExerciseAsync(cast: false);
                checks.Add("A manually entered destination exposes inline Cancel during real TCP negotiation and clears after cancellation.");
                await ExerciseAsync(cast: true);
                checks.Add("Own-window hardware casting shows inline Casting/Stop and actual encoder details; discovery expiry retains Stop through acknowledged cleanup.");
                if (!diagnostics.Any(line => line.Contains("Sender video startup:", StringComparison.Ordinal)) ||
                    !diagnostics.Any(line => line.Contains("Sender session ended:", StringComparison.Ordinal) &&
                        line.Contains("notifications=", StringComparison.Ordinal) && line.Contains("encoder=", StringComparison.Ordinal) &&
                        !line.Contains("encoded=0;", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Hardware capture did not report startup and final capture/encoder counters.");
            }
            var persisted = File.ReadAllText(path);
            if (!persisted.Contains("Sender video startup:", StringComparison.Ordinal) ||
                !persisted.Contains("Sender session ended:", StringComparison.Ordinal) ||
                !persisted.Contains("Encoder warning: self-test skipped 1 input frame;", StringComparison.Ordinal) ||
                !persisted.Contains("needInputEvents=", StringComparison.Ordinal))
                throw new InvalidOperationException("Actual sender capture/encoder diagnostics were not persisted to the isolated log.");
            checks.Add("Actual hardware sender diagnostics persist startup/final frame counters and encoder events to an isolated local log.");
        }
        finally
        {
            diagnosticLog = previousLog;
            diagnosticLogPath = previousPath;
            logDirectory.Delete(true);
        }

        async Task ExerciseAsync(bool cast)
        {
            previousLog?.Write($"UI self-test phase: inline sending {(cast ? "hardware capture" : "negotiation cancellation")}");
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
                var videoInput = new InlineVideoReadStream(videoPeer.GetStream());
                var stopSeen = 0;
                var videoEof = false;
                var encodedFrames = 0;
                long encodedBytes = 0;
                var videoPump = ReadVideoAsync();
                try
                {
                    while (true)
                    {
                        var message = await Wire.ReadAsync(stream, timeout.Token);
                        if (message.Session != request.Session)
                            throw new InvalidOperationException("Inline UI test control changed the session.");
                        Diagnostic($"Inline peer control: {message.Type}");
                        switch (message.Type)
                        {
                            case MessageType.Ping:
                                await WriteControlAsync(Message.Control(MessageType.Pong, request.Session,
                                    new { nonce = StrictJson.Integer(message.Json(), "nonce") }));
                                break;
                            case MessageType.Stop:
                                var reason = StrictJson.String(message.Json(), "reason", 1024);
                                Interlocked.Exchange(ref stopSeen, 1);
                                await videoPump.WaitAsync(timeout.Token);
                                if (!videoEof || encodedFrames == 0 || encodedBytes == 0)
                                    throw new InvalidOperationException("Inline UI test Stop requires prior encoded frames and frame-boundary media EOF.");
                                await WriteControlAsync(Message.Control(MessageType.SessionEnd, request.Session,
                                    new { reason }));
                                Diagnostic($"Inline peer terminal: stop=true; sessionMatched=true; mediaEofBeforeSessionEnd=true; " +
                                    $"sessionEndWritten=true; encodedFrames={encodedFrames}; encodedBytes={encodedBytes}.");
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
                    byte[] configuration = [];
                    long previousPts = -1;
                    var configured = false;
                    while (true)
                    {
                        var boundary = videoInput.BytesRead;
                        Message message;
                        try { message = await Wire.ReadAsync(videoInput, mediaStop.Token); }
                        catch (EndOfStreamException) when (videoInput.BytesRead == boundary)
                        {
                            if (!configured || encodedFrames == 0 || encodedBytes == 0)
                                throw new InvalidOperationException("Inline UI test media ended before valid encoded video.");
                            videoEof = true;
                            Diagnostic($"Inline peer media EOF: frameBoundary=true; encodedFrames={encodedFrames}; " +
                                $"encodedBytes={encodedBytes}; stopSeen={Volatile.Read(ref stopSeen) != 0}; controlReaderRetained=true.");
                            return;
                        }
                        if (message.Session != request.Session || message.Track != 1 || message.Generation != 1)
                            throw new InvalidOperationException("Inline UI test video changed its session, track, or generation.");
                        if (message.Type == MessageType.StreamConfig)
                        {
                            if (configured) throw new InvalidOperationException("Inline UI test received duplicate video configuration.");
                            var json = message.Json();
                            if (StrictJson.String(json, "codec", 16) != settings.VideoCodec ||
                                !json.TryGetProperty("csd", out var csd) ||
                                csd.ValueKind != System.Text.Json.JsonValueKind.Array || csd.GetArrayLength() != 1)
                                throw new InvalidOperationException("Inline UI test received invalid video configuration.");
                            configuration = Convert.FromBase64String(csd[0].GetString()!);
                            VideoBitstream.ValidateConfiguration(settings.VideoCodec, configuration,
                                StrictJson.Integer(json, "width"), StrictJson.Integer(json, "height"));
                            configured = true;
                            await WriteControlAsync(new(MessageType.StreamReady, request.Session, 1, 1, -1, 0,
                                """{"ok":true}"""u8.ToArray()));
                        }
                        else if (message.Type != MessageType.VideoFrame)
                            throw new InvalidOperationException($"Unexpected inline test video packet: {message.Type}.");
                        else
                        {
                            if (!configured || message.PtsUs < previousPts)
                                throw new InvalidOperationException("Inline UI test video lacks configuration or monotonic timestamps.");
                            VideoBitstream.ValidateFrame(settings.VideoCodec, message.Payload, configuration);
                            var key = VideoBitstream.IsKeyFrame(settings.VideoCodec, message.Payload, configuration);
                            if (key != (message.Flags == 1) || encodedFrames == 0 && !key)
                                throw new InvalidOperationException("Inline UI test video has invalid random-access flags or first frame.");
                            previousPts = message.PtsUs;
                            encodedFrames++;
                            encodedBytes += message.Payload.Length;
                        }
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
                    rows[0].Endpoint != target || rows[1].Status != L("Busy") ||
                    cast && (rows[0].Status != L("Casting") || string.IsNullOrEmpty(rows[0].Parameters) || string.IsNullOrEmpty(rows[0].Encoder)))
                    throw new InvalidOperationException("Active receiver row was lost, duplicated, mislabeled or missing encoder details.");
                var button = FindAction(ReceiverList) ?? throw new InvalidOperationException("Inline action was not realized.");
                if (!button.IsEnabled || button.Content?.ToString() != L(cast ? "Stop" : "Cancel"))
                    throw new InvalidOperationException("Inline Stop/Cancel binding is incorrect.");
                DeviceAction_Click(button, new RoutedEventArgs());
                if (ReceiverList.ItemsSource is not ReceiverRow[] stopping || stopping[0].Status != L("Stopping") || stopping[0].CanAct)
                    throw new InvalidOperationException("Stopping must retain the active row with its action disabled.");
                await session.WaitAsync(timeout.Token);
                await peer.WaitAsync(timeout.Token);
                if (outgoing is not null || receiver is null || ReadinessTitle.Text != L("Ready") ||
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

    private sealed class InlineVideoReadStream(Stream source) : Stream
    {
        public long BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
