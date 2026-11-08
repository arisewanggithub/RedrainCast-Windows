using System.Net;
using System.Net.Sockets;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class RecoverySenderAssociationRejectionTests
{
    public static TheoryData<string, string> Cases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var channel in new[] { "video", "audio" })
                foreach (var scenario in new[] { "reject-first", "stop-first", "local-stop", "no-stop", "end-only", "ack-write-failure",
                    "fatal", "control-protocol", "control-session", "unknown", "protocol", "wrong-session",
                    "empty", "oversized", "duplicate", "malformed", "hello-channel", "hello-type", "hello-csd" })
                    cases.Add(channel, scenario);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AssociationRejectionRequiresMatchingStopAndAcknowledgementWithoutHidingFailures(
        string channel, string scenario)
    {
        await using var fixture = new Fixture(scenario);
        await fixture.AcceptAsync(channel);
        if (scenario == "stop-first")
        {
            await fixture.SendAsync(MessageType.Stop, new { reason = "receiver association stop" });
            await fixture.Stopping.Task.WaitAsync(fixture.Limit.Token);
            // Hold teardown until the valid response is written, avoiding a peer-close write race.
            await fixture.RejectAsync(scenario);
            fixture.ReleaseStopping.TrySetResult();
            var acknowledgement = await fixture.ReadAsync();
            Assert.Equal(MessageType.SessionEnd, acknowledgement.Type);
            Assert.Equal("receiver association stop", StrictJson.String(acknowledgement.Json(), "reason", 1024));
        }
        else
        {
            await fixture.RejectAsync(scenario);
            // This callback runs only after AttachAsync has consumed its response and entered teardown.
            await fixture.Stopping.Task.WaitAsync(fixture.Limit.Token);
            if (scenario == "local-stop") fixture.Stop.Cancel();
            var expectedRejection = scenario is "reject-first" or "local-stop" or "no-stop" or "end-only" or "ack-write-failure" or
                "fatal" or "control-protocol" or "control-session";
            if (!expectedRejection)
                await fixture.SendAsync(MessageType.Stop, new { reason = "receiver association stop" });
            fixture.ReleaseStopping.TrySetResult();
            var first = await fixture.ReadAsync();
            if (expectedRejection)
            {
                Assert.Equal(MessageType.Stop, first.Type);
                if (scenario == "fatal")
                    await fixture.SendFailureThenStopAsync(MessageType.Error,
                        new { code = "DECODER_FAILED", message = "independent association decoder failure", fatal = true });
                else if (scenario == "control-protocol")
                    await fixture.SendFailureThenStopAsync(MessageType.Pong, new { invalid = "missing nonce" });
                else if (scenario == "control-session")
                    await fixture.SendAsync(MessageType.Stop, new { reason = "wrong control session" }, wrongSession: true);
                else if (scenario == "end-only")
                    await fixture.SendAsync(MessageType.SessionEnd, new { reason = "not STOP proof" });
                else if (scenario != "no-stop")
                {
                    await fixture.SendAsync(MessageType.Stop, new { reason = "receiver association stop" });
                    if (scenario != "ack-write-failure")
                    {
                        var acknowledgement = await fixture.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2), fixture.Limit.Token);
                        Assert.Equal(MessageType.SessionEnd, acknowledgement.Type);
                        Assert.Equal("receiver association stop", StrictJson.String(acknowledgement.Json(), "reason", 1024));
                    }
                }
            }
            else
                Assert.Equal(MessageType.Error, first.Type);
        }

        if (scenario is "reject-first" or "stop-first" or "local-stop")
            await fixture.Run.WaitAsync(TimeSpan.FromSeconds(2), fixture.Limit.Token);
        else
        {
            var error = await Assert.ThrowsAsync<IOException>(
                () => fixture.Run.WaitAsync(TimeSpan.FromSeconds(3), fixture.Limit.Token));
            Assert.Contains(scenario switch
            {
                "fatal" => "DECODER_FAILED: independent association decoder failure",
                "control-protocol" => "nonce",
                "control-session" => "control session changed",
                "no-stop" or "end-only" => "SESSION_ENDED: association is retiring",
                "ack-write-failure" => "independent SESSION_END write failure",
                "unknown" => "Unknown receiver rejection code",
                "protocol" => "PROTOCOL: association is retiring",
                "wrong-session" => "rejection session changed",
                "empty" => "Invalid receiver rejection message",
                "oversized" => "message",
                "duplicate" => "Duplicate",
                "malformed" => "JSON",
                _ => $"Recovery {channel} socket failed to attach"
            }, error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(fixture.Source.Started);
        Assert.True(fixture.Source.Disposed);
        foreach (var lane in fixture.Lanes)
        {
            try { Assert.Equal(0, await lane.GetStream().ReadAsync(new byte[1], fixture.Limit.Token)); }
            catch (IOException error) when (scenario == "stop-first" &&
                error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
            {
                // STOP cancellation leaves the later rejection unread; TCP retires that lane with RST.
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly CancellationTokenSource Limit = new(TimeSpan.FromSeconds(8));
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource Stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseStopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TestSource Source = new();
        public readonly List<TcpClient> Lanes = [];
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private TcpClient? control;
        private long session;
        public Task Run { get; }

        public Fixture(string scenario)
        {
            listener.Start();
            var endpoint = new ReceiverEndpoint(IPAddress.Loopback,
                ((IPEndPoint)listener.LocalEndpoint).Port, "Recovery association test", false);
            Run = scenario == "ack-write-failure" || SynchronizesStopAndError(scenario)
                ? RunWithControlStreamAsync(endpoint, scenario)
                : new SenderSession().RunAsync(endpoint, new(), Source, Status, Stop.Token);
        }

        private static bool SynchronizesStopAndError(string scenario) =>
            scenario is "unknown" or "protocol" or "wrong-session" or "empty" or "oversized" or
                "duplicate" or "malformed" or "hello-channel" or "hello-type" or "hello-csd";

        private void Status(string text)
        {
            if (text != "Stopping") return;
            Stopping.TrySetResult();
            ReleaseStopping.Task.Wait(Limit.Token);
        }

        private async Task RunWithControlStreamAsync(ReceiverEndpoint endpoint, string scenario)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Address, endpoint.Port, Limit.Token);
            var synchronizeStopAndError = SynchronizesStopAndError(scenario);
            using var stream = new AssociationControlStream(client.Client, scenario == "ack-write-failure",
                synchronizeStopAndError, Limit.Token);
            await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0, new { role = "sender" }), Limit.Token);
            Assert.Equal(MessageType.Hello, (await Wire.ReadAsync(stream, Limit.Token)).Type);
            var type = typeof(SenderSession).Assembly.GetType("RedrainCast.Send.Core.RecoverySender", true)!;
            var run = (Task)type.GetMethod("RunAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
                .Invoke(null, [endpoint, new CaptureSettings(), Source, null, (Action<string>)Status, 1L, stream, Stop.Token, null])!;
            try { await run; }
            finally
            {
                await Source.DisposeAsync();
                if (synchronizeStopAndError)
                {
                    Assert.True(stream.ErrorAttempted.Task.IsCompletedSuccessfully);
                    Assert.True(stream.StopConsumed.Task.IsCompletedSuccessfully);
                }
            }
        }

        private sealed class AssociationControlStream(Socket socket, bool failAcknowledgement,
            bool synchronizeStopAndError, CancellationToken limit) : NetworkStream(socket, ownsSocket: false)
        {
            public readonly TaskCompletionSource ErrorAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource StopConsumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly byte[] header = new byte[Wire.HeaderSize];
            private int headerBytes, payloadRemaining;
            private bool readingStop;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                // Force the slow-reader interleaving, then consume STOP before the terminal write can close TCP.
                if (synchronizeStopAndError && readingStop)
                    await ErrorAttempted.Task.WaitAsync(limit);
                var read = await base.ReadAsync(buffer, cancellationToken);
                if (payloadRemaining > 0)
                {
                    payloadRemaining -= read;
                    if (payloadRemaining == 0 && readingStop) StopConsumed.TrySetResult();
                }
                else
                {
                    buffer.Span[..read].CopyTo(header.AsSpan(headerBytes));
                    headerBytes += read;
                    if (headerBytes == Wire.HeaderSize)
                    {
                        readingStop = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6)) ==
                            (ushort)MessageType.Stop;
                        payloadRemaining = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
                        headerBytes = 0;
                    }
                }
                return read;
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var type = buffer.Length == Wire.HeaderSize && buffer.Span[..4].SequenceEqual("SCPY"u8)
                    ? (MessageType)System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(buffer.Span[6..])
                    : (MessageType)0;
                if (failAcknowledgement && type == MessageType.SessionEnd)
                    throw new IOException("independent SESSION_END write failure");
                if (synchronizeStopAndError && type == MessageType.Error)
                {
                    ErrorAttempted.TrySetResult();
                    await StopConsumed.Task.WaitAsync(limit);
                }
                await base.WriteAsync(buffer, cancellationToken);
            }
        }

        public async Task AcceptAsync(string pending)
        {
            control = await listener.AcceptTcpClientAsync(Limit.Token);
            Assert.Equal(MessageType.Hello, (await ReadAsync()).Type);
            await SendAsync(MessageType.Hello, new { role = "receiver", versions = new[] { 3 },
                name = "Test receiver", uncappedFps = true, streamRecovery = true, streamRecoveryVersion = 1 });
            var request = await ReadAsync();
            Assert.Equal(MessageType.SessionRequest, request.Type);
            session = request.Session;
            await SendAsync(MessageType.SessionAccept, new { tracks = new[] { 1 }, mediaToken = new string('A', 64) });
            foreach (var channel in new[] { "video", "audio" })
            {
                var lane = await listener.AcceptTcpClientAsync(Limit.Token);
                Lanes.Add(lane);
                var hello = await Wire.ReadAsync(lane.GetStream(), Limit.Token);
                Assert.Equal(session, hello.Session);
                Assert.Equal("media", StrictJson.String(hello.Json(), "role", 16));
                Assert.Equal(channel, StrictJson.String(hello.Json(), "channel", 16));
                Assert.Equal(new string('A', 64), StrictJson.String(hello.Json(), "token", 64));
                if (channel == pending) return;
                await Wire.WriteAsync(lane.GetStream(), Message.Control(MessageType.Hello, session,
                    new { channel }), Limit.Token);
            }
        }

        public Task<Message> ReadAsync() => Wire.ReadAsync(control!.GetStream(), Limit.Token).AsTask();
        public Task SendAsync(MessageType type, object json, bool wrongSession = false) =>
            Wire.WriteAsync(control!.GetStream(), Message.Control(type, session + (wrongSession ? 1 : 0), json),
                Limit.Token).AsTask();

        public async Task SendFailureThenStopAsync(MessageType type, object json)
        {
            using var bytes = new MemoryStream();
            await Wire.WriteAsync(bytes, Message.Control(type, session, json), Limit.Token);
            await Wire.WriteAsync(bytes, Message.Control(MessageType.Stop, session,
                new { reason = "receiver association stop" }), Limit.Token);
            await control!.GetStream().WriteAsync(bytes.ToArray(), Limit.Token);
        }

        public async Task RejectAsync(string scenario)
        {
            var channel = Lanes.Count == 1 ? "video" : "audio";
            var response = scenario switch
            {
                "hello-channel" => Message.Control(MessageType.Hello, session, new { channel = "control" }),
                "hello-type" => Message.Control(MessageType.SessionAccept, session, new { channel }),
                "hello-csd" => Message.Control(MessageType.Hello, session, new { channel, csd = new[] { "AA==" } }),
                _ => Message.Control(MessageType.Reject, session + (scenario == "wrong-session" ? 1 : 0),
                    new { code = scenario == "unknown" ? "UNKNOWN" : scenario == "protocol" ? "PROTOCOL" : "SESSION_ENDED",
                        message = scenario == "empty" ? "" : scenario == "oversized" ? new string('x', 2049) : "association is retiring" })
            };
            if (scenario is "duplicate" or "malformed")
            {
                // Construct raw framing because Wire.WriteAsync intentionally rejects malformed JSON.
                using var bytes = new MemoryStream();
                await Wire.WriteAsync(bytes, response, Limit.Token);
                var payload = System.Text.Encoding.UTF8.GetBytes(scenario == "duplicate"
                    ? "{\"code\":\"SESSION_ENDED\",\"code\":\"SESSION_ENDED\",\"message\":\"association is retiring\"}" : "{");
                var header = bytes.ToArray()[..Wire.HeaderSize];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8, 4), payload.Length);
                await Lanes[^1].GetStream().WriteAsync(header, Limit.Token);
                await Lanes[^1].GetStream().WriteAsync(payload, Limit.Token);
            }
            else
                await Wire.WriteAsync(Lanes[^1].GetStream(), response, Limit.Token);
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseStopping.TrySetResult();
            Stop.Cancel();
            foreach (var lane in Lanes) lane.Dispose();
            control?.Dispose();
            listener.Stop();
            try { await Run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) { }
            finally { Stop.Dispose(); Limit.Dispose(); }
        }
    }

    private sealed class TestSource : IRecoverableVideoSource
    {
        public bool Started, Disposed;
        public string EncoderName => "Synthetic association source";
        public int Width => 640;
        public int Height => 360;
        public bool SupportsStreamRecovery => true;
        public ValueTask RequestKeyFrameAsync(CancellationToken token) => ValueTask.CompletedTask;
        public IAsyncEnumerable<EncodedVideo> ReadFramesAsync(CancellationToken token)
        {
            Started = true;
            throw new InvalidOperationException("Association retirement must not start capture.");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
