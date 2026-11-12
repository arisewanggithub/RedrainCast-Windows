using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RedrainCast.Protocol;

namespace RedrainCast.Receive.Core;

public sealed record ReceiverNotice(string State, string Detail, IReadOnlyList<string>? ListeningEndpoints = null,
    IReadOnlyDictionary<string, string>? AdapterLabels = null);

public sealed class ReceiverServer : IAsyncDisposable
{
    private static long nextVideoReadId;
    private readonly object gate = new();
    private readonly string name;
    private readonly string instanceId;
    private readonly IPlaybackFactory factory;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Connection> connections = [];
    private readonly HashSet<Connection> retiringConnections = [];
    private readonly Action<ReceiverNotice> notify;
    private readonly Func<IDisposable?>? acquireSession;
    private Connection? active;
    private Task? acceptTask;
    private Task? discoveryTask;
    private Task? shutdownTask;
    private bool started;
    private bool stopped;
    private int boundPort;

    public ReceiverServer(string name, IPlaybackFactory factory, Action<ReceiverNotice> notify,
        int port = 0, IPAddress? bindAddress = null, Func<IDisposable?>? acquireSession = null, string? instanceId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException("Receiver name requires 1..80 characters without controls.", nameof(name));
        this.name = name;
        this.instanceId = instanceId is null ? Guid.NewGuid().ToString("N") :
            Guid.TryParse(instanceId, out var identity) ? identity.ToString("N") :
            throw new ArgumentException("Receiver instance ID must be a UUID.", nameof(instanceId));
        this.factory = factory;
        this.notify = notify;
        this.acquireSession = acquireSession;
        listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
    }

    public int Port
    {
        get
        {
            var port = Volatile.Read(ref boundPort);
            return port > 0 ? port : throw new InvalidOperationException("The listener has not bound a TCP port.");
        }
    }
    public bool IsBusy { get { lock (gate) return active is not null || retiringConnections.Any(item => item.HasSessionResources); } }
    public ReceiverRecoveryState? RecoveryState
    {
        get
        {
            Connection? connection;
            lock (gate) connection = active;
            return connection?.RecoveryState;
        }
    }
    public string Diagnostics
    {
        get
        {
            Connection? connection;
            lock (gate) connection = active;
            return connection?.ProgressDiagnostics ?? "session=none";
        }
    }
    public byte[] Advertisement => Discovery.Advertisement(name, Port, IsBusy, instanceId, factory.HdrCapabilities);
    public ReceiveStatistics? Statistics
    {
        get
        {
            Connection? connection;
            lock (gate) connection = active;
            return connection?.Statistics;
        }
    }

    public void Start(bool advertise = true)
    {
        lock (gate)
        {
            if (started || stopped) throw new InvalidOperationException("Create a fresh listener for each start.");
            listener.Start(32);
            boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            started = true;
            acceptTask = Task.Run(AcceptAsync);
            discoveryTask = advertise ? Task.Run(AdvertiseAsync) : Task.CompletedTask;
        }
        notify(new("Listening", $"TCP {Port}. {factory.Description}"));
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                client.NoDelay = true;
                Connection connection;
                lock (gate)
                {
                    if (connections.Count >= 32)
                    {
                        client.Dispose();
                        notify(new("Connection limit", "At most 32 TCP connections are allowed, including control and media."));
                        continue;
                    }
                    connection = new Connection(this, client);
                    connections.Add(connection);
                }
                connection.Completion = RunConnectionAsync(connection);
            }
        }
        catch (Exception error) when (lifetime.IsCancellationRequested &&
            error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (SocketException error)
        {
            notify(new("Listener failed", error.Message));
            lifetime.Cancel();
            listener.Stop();
        }
    }

    private async Task RunConnectionAsync(Connection connection)
    {
        try { await connection.RunAsync(); }
        finally
        {
            connection.Dispose();
            bool released;
            lock (gate)
            {
                released = active == connection;
                if (released) active = null;
                connections.Remove(connection);
            }
            if (released && !lifetime.IsCancellationRequested)
                notify(connection.ResourcesDisposed
                    ? new("Listening", "Cast resources released; ready for a new sender.")
                    : new("Cleanup pending", "Transport closed; receiver resources remain owned until retirement completes."));
        }
    }

    private bool Claim(Connection connection)
    {
        lock (gate)
        {
            if (active is not null || retiringConnections.Any(item => item.HasSessionResources)) return false;
            if (acquireSession is not null)
            {
                var lease = acquireSession();
                if (lease is null) return false;
                connection.SessionLease = lease;
            }

            active = connection;
            return true;
        }
    }

    private async Task AdvertiseAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        using var broadcaster = new DiscoveryBroadcaster(error =>
            notify(new("Discovery warning", $"UDP advertisement failed: {error.SocketErrorCode}. Manual TCP connection remains available.")));
        string? network = null;
        string? addressDisplay = null;
        try
        {
            do
            {
                var addresses = Discovery.Addresses();
                var signature = string.Join(",", addresses.Select(item => item.Address));
                var displaySignature = string.Join("\n", addresses.Select(item => $"{item.Address}\t{item.AdapterName}"));
                var changed = addressDisplay != displaySignature;
                if (network is not null && signature != network)
                {
                    notify(new("Network changed", "Current addresses changed; disconnecting the active cast."));
                    await DisconnectAsync("Receiver network changed");
                }
                network = signature;
                addressDisplay = displaySignature;
                if (changed)
                {
                    notify(new("Listening", "Current listener addresses refreshed.",
                        addresses.Select(item => Discovery.DisplayEndpoint(item.Address, Port)).ToArray(),
                        addresses.ToDictionary(item => Discovery.DisplayEndpoint(item.Address, Port), item => item.AdapterName)));
                    notify(new("Listening", "UDP discovery targets: " +
                        string.Join(", ", addresses.Select(item => $"{item.Address} -> {Discovery.DisplayEndpoint(item.Broadcast, Discovery.Port)}"))));
                }
                var bytes = Advertisement;
                await broadcaster.PublishAsync(addresses, bytes, lifetime.Token);
            } while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (NetworkInformationException error)
        {
            notify(new("Discovery failed", $"Network enumeration failed: {error.Message}. Stop and restart the listener."));
        }
    }

    public async Task DisconnectAsync(string reason = "user")
    {
        Connection? connection;
        lock (gate) connection = active;
        if (connection is not null) await connection.StopAsync(reason);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (shutdownTask is not null) return new(shutdownTask);
            stopped = true;
            shutdownTask = ShutdownAsync();
            return new(shutdownTask);
        }
    }

    private async Task ShutdownAsync()
    {
        try { await DisconnectAsync(); }
        finally
        {
            lifetime.Cancel();
            listener.Stop();
            if (acceptTask is not null) await acceptTask;
            Connection[] remaining;
            lock (gate) remaining = connections.ToArray();
            foreach (var connection in remaining) connection.Abort();
            await Task.WhenAll(remaining.Select(item => item.Completion));
            if (discoveryTask is not null) await discoveryTask;
            lifetime.Dispose();
        }
        notify(new("Stopped", "Listener and owned connections closed."));
    }

    private sealed class Connection : IDisposable
    {
        private sealed class MediaEofException(string message) : IOException(message);
        public IDisposable? SessionLease { get; set; }
        private bool videoRotation;
        private bool streamRecovery;
        private ReceiverOptionCapabilities receiverOptions = ReceiverOptionCapabilities.None;
        private HdrReceiverCapabilities hdrCapabilities = HdrReceiverCapabilities.None;
        private readonly ReceiverServer owner;
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly BoundaryReadStream input;
        private readonly CancellationTokenSource cancellation;
        private readonly CancellationTokenSource playbackCancellation;
        private readonly SemaphoreSlim writer = new(1, 1);
        private readonly ReceiveDiagnostics receive = new();
        private readonly ControlLatency controlLatency = new();
        private readonly ReceiverActivity activity = new();
        private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource mediaAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource peerTerminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private MediaEofException? mediaEof;
        // Reserve the remaining encoded budget for in-progress packets on both media sockets.
        private ReceiveQueue queue = new(4 * 1024 * 1024);
        private long session;
        private long lastInput = Environment.TickCount64;
        private long startup = Environment.TickCount64;
        private int tracksReady;
        private int expectedTracks;
        private bool accepted;
        private bool disposed;
        private int stopping;
        private int failing;
        private bool brokenWriter;
        private Task<Packet>? pendingControlRead;
        private readonly HashSet<Task> pendingWrites = [];
        private Task retirementWorkers = Task.CompletedTask;
        private Task resourceDisposal = Task.CompletedTask;
        private bool resourcesDisposed;
        public bool HasSessionResources => accepted && mediaTrack == 0;
        public bool ResourcesDisposed => Volatile.Read(ref resourcesDisposed);
        public ReceiverRecoveryState? RecoveryState => (Volatile.Read(ref playbackForMedia) as RecoveryPlayback)?.State;
        private readonly string mediaToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        private readonly Connection?[] mediaChannels = new Connection?[2];
        private uint mediaTrack;
        private IPlayback? playbackForMedia;
        public Task Completion { get; set; } = Task.CompletedTask;

        public Connection(ReceiverServer owner, TcpClient client)
        {
            this.owner = owner;
            this.client = client;
            stream = client.GetStream();
            input = new(stream);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.lifetime.Token);
            playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        }

        private Connection AttachMedia(Packet hello)
        {
            var json = hello.Object();
            var kind = JsonObject.Text(json, "channel", 16);
            JsonObject.Require(kind is "video" or "audio", "Expected a video or audio channel.");
            var token = JsonObject.Text(json, "token", 64);
            lock (owner.gate)
            {
                var target = owner.active;
                if (target is null || !target.accepted || hello.Session != target.session || token != target.mediaToken)
                    throw new ProtocolException("Unknown or retired media session.");
                if (target.cancellation.IsCancellationRequested || Volatile.Read(ref target.stopping) != 0)
                    throw new ProtocolException("Receiver is ending this media session.", "SESSION_ENDED");
                var track = kind == "video" ? 1u : 2u;
                JsonObject.Require(target.mediaChannels[track - 1] is null, "Duplicate media channel.");
                mediaTrack = track;
                target.mediaChannels[track - 1] = this;
                if (target.mediaChannels.All(channel => channel is not null)) target.mediaAttached.TrySetResult();
                return target;
            }
        }

        private Task SendAsync(Packet packet, CancellationToken extraCancellation = default, RecoveryPlayback? recovery = null)
        {
            lock (client)
            {
                if (disposed) return Task.FromException(new IOException("Receiver connection has retired."));
                var sending = SendOwnedAsync(packet, extraCancellation, recovery);
                pendingWrites.Add(sending);
                _ = sending.ContinueWith(completed =>
                {
                    lock (client) pendingWrites.Remove(completed);
                    if (completed.Exception is { } failure)
                        NotifyCleanupFailure("control-write", failure.GetBaseException());
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return sending;
            }
        }

        private async Task SendOwnedAsync(Packet packet, CancellationToken extraCancellation, RecoveryPlayback? recovery)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, extraCancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await writer.WaitAsync(timeout.Token);
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (brokenWriter) throw new IOException("Control writer is unusable.");
                activity.Begin(ReceiverOperation.Write, packet.Type.ToString());
                // Epoch/Stop cancellation may retire a queued frame, never truncate a started control frame.
                using var writing = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                writing.CancelAfter(TimeSpan.FromSeconds(10));
                var started = false;
                try
                {
                    Task Start()
                    {
                        started = true;
                        return Wire.WriteAsync(stream, packet, writing.Token).AsTask();
                    }
                    await (recovery is null ? Start() : recovery.StartControlWrite(packet, Start, timeout.Token));
                }
                catch when (!started) { throw; }
                catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
                {
                    brokenWriter = true;
                    throw new ProtocolException($"Control writer exceeded its 10-second deadline: {error.GetType().Name}", "TIMEOUT");
                }
                catch { brokenWriter = true; throw; }
                finally { activity.Complete(ReceiverOperation.Write); }
            }
            finally { writer.Release(); }
        }

        private async Task<Packet> ReadAsync(CancellationToken token,
            Action<VideoReadObservation>? diagnostic = null, long readId = 0)
        {
            var begin = Environment.TickCount64;
            var bytes = input.BytesRead;
            activity.Begin(ReceiverOperation.Read, "header");
            try
            {
                var packet = await Wire.ReadAsync(input, token, (incoming, received, total) =>
                    activity.Progress(ReceiverOperation.Read,
                        $"{incoming.Type} track={incoming.Track} generation={incoming.Generation} payload={received}/{total}"),
                    diagnostic, readId);
                receive.Received(packet, Environment.TickCount64 - begin);
                Interlocked.Exchange(ref lastInput, Environment.TickCount64);
                return packet;
            }
            catch (EndOfStreamException) when (mediaTrack != 0 && input.BytesRead == bytes)
            {
                throw new MediaEofException($"{(mediaTrack == 1 ? "Video" : "Audio")} connection failed: Sender closed a media connection.");
            }
            finally { activity.Complete(ReceiverOperation.Read); }
        }

        public async Task RunAsync()
        {
            IPlayback? playback = null;
            Task? pump = null;
            Task? watchdog = null;
            Task? playbackRetirement = null;
            long retirementStarted = 0;
            try
            {
                using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                setup.CancelAfter(TimeSpan.FromSeconds(10));
                var hello = await ReadAsync(setup.Token);
                JsonObject.Require(hello.Type == PacketType.Hello, "HELLO required first.");
                var json = hello.Object();
                if (JsonObject.Text(json, "role") == "media")
                {
                    session = hello.Session;
                    var target = AttachMedia(hello);
                    if (target.playbackForMedia is { AffineMediaReaders: true } mediaPlayback)
                        await TrackThread.Run(() => target.ReadMediaAsync(this, setup.Token),
                            () => mediaPlayback.EnterMediaReader(mediaTrack));
                    else
                        await target.ReadMediaAsync(this, setup.Token);
                    return;
                }
                JsonObject.Require(hello.Session == 0, "Control HELLO requires session zero.");
                JsonObject.Require(JsonObject.Text(json, "role") == "sender", "Sender role required.");
                var peerName = JsonObject.Text(json, "name");
                JsonObject.Require(peerName.Length > 0 && !peerName.Any(char.IsControl), "Invalid sender name.");
                var versions = JsonObject.Member(json, "versions", JsonValueKind.Array);
                JsonObject.Require(versions.GetArrayLength() is >= 1 and <= 16 &&
                    versions.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Number &&
                        item.TryGetInt32(out _)), "Invalid version list.");
                JsonObject.Require(versions.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Number &&
                    item.TryGetInt32(out var value) && value == Wire.Version), "Version 3 required.", "VERSION");
                if (json.TryGetProperty("uncappedFps", out _)) _ = JsonObject.Boolean(json, "uncappedFps");
                if (owner.IsBusy)
                {
                    await SendAsync(Packet.Control(PacketType.Reject, 0, JsonData.Object(("code", "BUSY"), ("message", "Receiver already in use"))));
                    return;
                }
                var offersRotation = json.TryGetProperty("videoRotation", out _) && JsonObject.Boolean(json, "videoRotation");
                videoRotation = offersRotation && owner.factory.SupportsVideoRotation;
                var response = JsonData.Object(("role", "receiver"), ("versions", JsonData.Value(new[] { 3 })),
                    ("name", owner.name), ("uncappedFps", true));
                if (json.TryGetProperty("hdrVideoVersion", out _))
                {
                    var hdrVersion = JsonObject.Number(json, "hdrVideoVersion");
                    JsonObject.Require(hdrVersion > 0, "Invalid HDR negotiation version.");
                    if (hdrVersion == HdrVideoProtocol.Version)
                    {
                        hdrCapabilities = owner.factory.HdrCapabilities;
                        response["receiveCapabilities"] = hdrCapabilities.WireValue;
                        setup.CancelAfter(TimeSpan.FromSeconds(45));
                    }
                }
                if (videoRotation) response["videoRotation"] = true;
                try { receiverOptions = ReceiverOptionProtocol.Capabilities(json).Intersect(owner.factory.ReceiverOptions); }
                catch (FormatException error) { throw new ProtocolException(error.Message); }
                if (receiverOptions.PlaybackStats) response["receiverOptions"] = receiverOptions.WireValue;
                try { streamRecovery = StreamRecovery.Negotiated(json) && owner.factory.SupportsStreamRecovery; }
                catch (FormatException error) { throw new ProtocolException(error.Message); }
                if (streamRecovery)
                {
                    response["streamRecovery"] = true;
                    response["streamRecoveryVersion"] = StreamRecovery.Version;
                }
                await SendAsync(Packet.Control(PacketType.Hello, 0, response), setup.Token);
                var requestPacket = await ReadAsync(setup.Token);
                while (requestPacket.Type != PacketType.SessionRequest)
                {
                    JsonObject.Require(requestPacket.Session == 0, "Setup control requires session zero.");
                    if (requestPacket.Type == PacketType.Ping)
                        await SendAsync(Packet.Control(PacketType.Pong, 0,
                            JsonData.Object(("nonce", JsonObject.Number(requestPacket.Object(), "nonce")))), setup.Token);
                    else if (requestPacket.Type == PacketType.Pong)
                        _ = JsonObject.Number(requestPacket.Object(), "nonce");
                    else if (requestPacket.Type == PacketType.Stop)
                    {
                        await SendAsync(Packet.Control(PacketType.SessionEnd, 0,
                            JsonData.Object(("reason", JsonObject.Text(requestPacket.Object(), "reason", 1024)))), setup.Token);
                        return;
                    }
                    else throw new ProtocolException("Expected SESSION_REQUEST or setup liveness/STOP.");
                    requestPacket = await ReadAsync(setup.Token);
                }
                JsonObject.Require(requestPacket.Type == PacketType.SessionRequest && requestPacket.Session > 0,
                    "Positive SESSION_REQUEST required.");
                session = requestPacket.Session;
                if (!owner.Claim(this))
                {
                    await SendAsync(Packet.Control(PacketType.Reject, session, JsonData.Object(("code", "BUSY"), ("message", "Receiver already in use"))));
                    return;
                }
                CastRequest request;
                var requestJson = requestPacket.Object();
                try { _ = ReceiverOptionProtocol.Parse(requestJson, receiverOptions); }
                catch (FormatException error) { throw new ProtocolException(error.Message); }
                try
                {
                    var currentHdr = owner.factory.HdrCapabilities;
                    var usableHdr = hdrCapabilities.CanReceiveHdr10 && currentHdr.CanReceiveHdr10
                        ? currentHdr : HdrReceiverCapabilities.None;
                    request = CastRequest.Parse(requestJson, receiverOptions, usableHdr);
                }
                catch (ProtocolException error)
                {
                    throw new ProtocolException(error.Message, "UNSUPPORTED_CONFIG");
                }
                controlLatency.Valid(Environment.TickCount64);
                if (streamRecovery || request.AudioCodec is not null)
                {
                    queue.Dispose();
                    queue = streamRecovery
                        ? new ReceiveQueue(ReceiveQueue.MaximumIndependentBytes, ReceiveQueue.AudioTrackBytes, true)
                        : new ReceiveQueue(4 * 1024 * 1024 - 256 * 1024, audioReserveBytes: 256 * 1024);
                }
                playback = await owner.factory.OpenAsync(request, setup.Token);
                if (streamRecovery)
                    playback = new RecoveryPlayback(owner.factory, request, playback, queue, session,
                        (packet, token) => SendAsync(packet, token, playbackForMedia as RecoveryPlayback),
                        detail => owner.notify(new("Recovery", detail)), playbackCancellation.Token);
                playbackForMedia = playback;
                expectedTracks = request.Tracks.Length;
                accepted = true;
                var acceptance = JsonData.Object(("tracks", JsonData.Value(request.Tracks)), ("mediaToken", mediaToken));
                HdrVideoProtocol.AddFormat(acceptance, request.ColorFormat);
                await SendAsync(Packet.Control(PacketType.SessionAccept, session, acceptance), setup.Token);
                if (playback is RecoveryPlayback preparing) preparing.BeginSetup();
                Interlocked.Exchange(ref startup, Environment.TickCount64);
                owner.notify(new("Initializing", $"{request.VideoCodec}, {request.AudioCodec ?? "audio off"}"));
                var trackState = new TrackState(request);
                var workerPlayback = playback;
                pump = Task.WhenAll(request.Tracks.Select(track =>
                    TrackThread.Run(() => PumpAsync(workerPlayback, trackState, track))));
                watchdog = WatchdogAsync(workerPlayback);
                while (!cancellation.IsCancellationRequested)
                {
                    pendingControlRead = ReadAsync(cancellation.Token);
                    if (await Task.WhenAny(pendingControlRead, retirement.Task) != pendingControlRead) return;
                    var packet = await pendingControlRead;
                    pendingControlRead = null;
                    JsonObject.Require(packet.Session == session, "Session mismatch.");
                    if (Volatile.Read(ref stopping) != 0 && packet.Type is not (PacketType.SessionEnd or PacketType.Stop))
                        continue;
                    switch (packet.Type)
                    {
                        case PacketType.StreamConfig:
                        case PacketType.VideoFrame:
                        case PacketType.AudioPacket:
                            throw new ProtocolException("Media and configuration require their dedicated connections.");
                        case PacketType.Ping:
                            controlLatency.Valid(Environment.TickCount64);
                            await SendAsync(Packet.Control(PacketType.Pong, session, JsonData.Object(("nonce", JsonObject.Number(packet.Object(), "nonce")))));
                            break;
                        case PacketType.Pong:
                            controlLatency.Valid(Environment.TickCount64, JsonObject.Number(packet.Object(), "nonce"));
                            break;
                        case PacketType.Stop:
                            var reason = JsonObject.Text(packet.Object(), "reason", 1024);
                            peerTerminal.TrySetResult();
                            Interlocked.Exchange(ref stopping, 1);
                            playbackCancellation.Cancel();
                            retirementStarted = Environment.TickCount64;
                            var retiring = playback;
                            playback = null;
                            playbackRetirement = RetirePlaybackAsync(retiring);
                            try { await playbackRetirement.WaitAsync(TimeSpan.FromSeconds(2)); }
                            catch (TimeoutException)
                            {
                                throw new ProtocolException("Playback retirement exceeded its 2-second deadline.", "TIMEOUT");
                            }
                            RetireMedia();
                            await SendAsync(Packet.Control(PacketType.SessionEnd, session, JsonData.Object(("reason", reason))));
                            owner.notify(new("Disconnected", "Sender stopped the cast."));
                            return;
                        case PacketType.SessionEnd:
                            JsonObject.Require(Volatile.Read(ref stopping) != 0, "Unexpected SESSION_END without local STOP.");
                            _ = JsonObject.Text(packet.Object(), "reason", 1024);
                            owner.notify(new("Disconnected", "Sender ended the session."));
                            return;
                        case PacketType.Error:
                            var body = packet.Object();
                            var code = JsonObject.Text(body, "code");
                            JsonObject.Require(code is "VERSION" or "BUSY" or "UNSUPPORTED_CONFIG" or "PROTOCOL" or
                                "CAPTURE_FAILED" or "AUDIO_FAILED" or "DECODER_FAILED" or "TIMEOUT" or "CONGESTION" or "INTERNAL",
                                "Unknown sender error code.");
                            var message = JsonObject.Text(body, "message", 2048);
                            JsonObject.Require(message.Length > 0, "Invalid sender error message.");
                            var fatal = JsonObject.Boolean(body, "fatal");
                            owner.notify(new("Sender error", $"{code}: {message}; {DiagnosticSnapshot(playback)}"));
                            if (fatal)
                            {
                                peerTerminal.TrySetResult();
                                return;
                            }
                            break;
                        case PacketType.Status:
                            _ = JsonObject.Text(packet.Object(), "state");
                            _ = JsonObject.Text(packet.Object(), "message", 2048);
                            break;
                        default:
                            throw new ProtocolException("Unexpected sender packet.");
                    }
                    controlLatency.Valid(Environment.TickCount64);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
            {
                await FailAsync(error, playback);
            }
            catch (Exception error)
            {
                // Backend failures must be visible and terminal, never converted into readiness.
                await FailAsync(new ProtocolException($"Unexpected receiver failure: {error.GetType().Name}: {error.Message}", "INTERNAL"), playback);
            }
            finally
            {
                Interlocked.Exchange(ref stopping, 1);
                peerTerminal.TrySetResult();
                playbackCancellation.Cancel();
                if (retirementStarted == 0) retirementStarted = Environment.TickCount64;
                var channels = RetireMedia();
                var retiringResources = RetireAsync();
                retirementWorkers = retiringResources;
                try
                {
                    var remaining = Math.Max(0, 2000 - (Environment.TickCount64 - retirementStarted));
                    await retiringResources.WaitAsync(TimeSpan.FromMilliseconds(remaining));
                    // SendAsync serializes whole frames; acquire after all producers have retired.
                    using var flushing = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await writer.WaitAsync(flushing.Token);
                    writer.Release();
                }
                catch (Exception error)
                {
                    NotifyCleanupFailure("connection-retirement", error);
                }
                finally
                {
                    cancellation.Cancel();
                    client.Dispose();
                    if (pendingControlRead is not null)
                    {
                        try { await pendingControlRead; }
                        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                    }
                    ended.TrySetResult();
                }

                async Task RetireAsync()
                {
                    await Task.WhenAll(channels.Select(channel => channel.Completion)
                        .Concat(new[] { watchdog, playbackRetirement ?? RetirePlaybackAsync(playback) }.OfType<Task>()));
                }
            }

            async Task RetirePlaybackAsync(IPlayback? retiring)
            {
                var channels = RetireMedia();
                try
                {
                    await Task.WhenAll(channels.Select(channel => channel.Completion)
                        .Concat(new[] { pump, watchdog }.OfType<Task>()));
                }
                finally { if (retiring is not null) await retiring.DisposeAsync(); }
            }
        }

        private async Task ReadMediaAsync(Connection channel, CancellationToken setup)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, channel.cancellation.Token);
            try
            {
                await channel.SendAsync(Packet.Control(PacketType.Hello, session,
                    JsonData.Object(("channel", channel.mediaTrack == 1 ? "video" : "audio"))), setup);
                await mediaAttached.Task.WaitAsync(linked.Token);
                while (true)
                {
                    var diagnostic = channel.mediaTrack == 1 ? playbackForMedia?.VideoReadDiagnostics : null;
                    var currentReadId = diagnostic is null ? 0 : Interlocked.Increment(ref nextVideoReadId);
                    var packet = await channel.ReadAsync(linked.Token, diagnostic, currentReadId);
                    if (Wire.IsMedia(packet.Type)) playbackForMedia?.ObserveReceived(packet);
                    MediaTiming.Record(MediaStage.SocketRead, packet.Session, packet.Track, packet.Generation,
                        packet.PtsUs, a: packet.Payload.Length);
                    if (Volatile.Read(ref stopping) != 0 || Volatile.Read(ref mediaEof) is not null) continue;
                    JsonObject.Require(packet.Session == session && packet.Track == channel.mediaTrack &&
                        (packet.Type == PacketType.StreamConfig ||
                         packet.Type == (channel.mediaTrack == 1 ? PacketType.VideoFrame : PacketType.AudioPacket)),
                        "Packet does not belong to this media channel.");
                    JsonObject.Require(channel.mediaTrack == 1 || expectedTracks == 2, "Audio was not enabled.");
                    var handlingAt = Environment.TickCount64;
                    var admission = diagnostic is null ? null : new VideoReadDiagnostic(currentReadId, diagnostic);
                    admission?.Header(packet);
                    admission?.Record("admission_begin");
                    var admissionOutcome = "error";
                    MediaTiming.Record(MediaStage.AdmissionBegin, packet.Session, packet.Track, packet.Generation, packet.PtsUs);
                    channel.activity.Begin(ReceiverOperation.Admission,
                        $"{packet.Type} track={packet.Track} generation={packet.Generation} bytes={packet.Payload.Length}");
                    try
                    {
                        JsonObject.Require(videoRotation || packet.Flags <= 1, "Video rotation was not negotiated.");
                        if (playbackForMedia is RecoveryPlayback recovery)
                        {
                            if (!recovery.Admit(packet)) { admissionOutcome = "recovery_filtered"; continue; }
                            try { queue.Add(packet); }
                            catch (ProtocolException error) when (error.Code == "CONGESTION" && Wire.IsMedia(packet.Type))
                            { admissionOutcome = "recovery_pressure"; recovery.Pressure(packet.Track); continue; }
                        }

                        else await queue.AddAsync(packet, linked.Token);
                        if (Wire.IsMedia(packet.Type)) playbackForMedia?.Observe(packet);
                        admissionOutcome = "accepted";
                    }
                    catch (OperationCanceledException)
                    {
                        admissionOutcome = "cancelled";
                        throw;
                    }
                    finally
                    {
                        admission?.Record("admission_end", outcome: admissionOutcome);
                        MediaTiming.Record(MediaStage.AdmissionEnd, packet.Session, packet.Track, packet.Generation, packet.PtsUs);
                        channel.activity.Complete(ReceiverOperation.Admission);
                        channel.receive.Handled(Environment.TickCount64 - handlingAt);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested && Volatile.Read(ref stopping) == 0)
                {
                    if (error is MediaEofException eof)
                    {
                        Interlocked.CompareExchange(ref mediaEof, eof, null);
                        playbackCancellation.Cancel();
                        // Control is deliberately still readable while the peer finishes terminal reporting.
                        await Task.WhenAny(peerTerminal.Task, Task.Delay(TimeSpan.FromSeconds(2)));
                        if (peerTerminal.Task.IsCompleted || cancellation.IsCancellationRequested) return;
                    }
                    var code = error is ProtocolException protocol ? protocol.Code :
                        error is OperationCanceledException ? "TIMEOUT" :
                        error is IOException or SocketException ? "PROTOCOL" : "INTERNAL";
                    await FailAsync(new ProtocolException(
                        $"{(channel.mediaTrack == 1 ? "Video" : "Audio")} connection failed: {error.Message}", code), playbackForMedia);
                    retirement.TrySetResult();
                }
            }
        }

        private string DiagnosticSnapshot(IPlayback? playback)
        {
            string available;
            try { available = client.Client is { } socket ? socket.Available.ToString() : "closed"; }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                available = $"unavailable({error.GetType().Name})";
            }
            return $"socketAvailableBytes={available}; {TcpSocketDiagnostics.Read(client.Client)}; " +
                ProgressDiagnostics +
                (playback is null ? "" : $" {playback.Diagnostics}");
        }

        public string ProgressDiagnostics
        {
            get
            {
                Connection[] channels;
                lock (owner.gate) channels = mediaChannels.OfType<Connection>().ToArray();
                return $"session={session}; {activity.Diagnostics}; {receive.Diagnostics}; {queue.Diagnostics}" +
                    string.Concat(channels.Select(channel =>
                        $" channel={channel.mediaTrack} [{channel.activity.Diagnostics}; {channel.receive.Diagnostics}]"));
            }
        }

        public ReceiveStatistics? Statistics
        {
            get
            {
                if (!accepted || Volatile.Read(ref stopping) != 0 || disposed) return null;
                Connection? video, audio;
                lock (owner.gate) { video = mediaChannels[0]; audio = mediaChannels[1]; }
                var now = Environment.TickCount64;
                var latency = controlLatency.Snapshot(now);
                var result = new ReceiveStatistics(session,
                    video?.receive.Snapshot(1) ?? new(0, 0, null),
                    audio?.receive.Snapshot(2) ?? new(0, 0, null),
                    queue.Snapshot(1), queue.Snapshot(2), latency.RttMs, latency.AgeMs, RecoveryState, queue.MediaByteBudget,
                    queue.WaitingSnapshot(1), queue.WaitingSnapshot(2));
                return Volatile.Read(ref stopping) != 0 || disposed ? null : result;
            }
        }

        private async Task FailAsync(Exception error, IPlayback? playback = null)
        {
            if (Interlocked.Exchange(ref failing, 1) != 0) return;
            error = Volatile.Read(ref mediaEof) ?? error;
            peerTerminal.TrySetResult();
            Interlocked.Exchange(ref stopping, 1);
            playbackCancellation.Cancel();
            var code = error is ProtocolException protocol ? protocol.Code :
                error is OperationCanceledException ? "TIMEOUT" : "PROTOCOL";
            owner.notify(new(code, error.Message + " " + DiagnosticSnapshot(playback)));
            try
            {
                await SendAsync(accepted
                    ? Packet.Control(PacketType.Error, session, JsonData.Object(("code", code), ("message", error.Message[..Math.Min(2048, error.Message.Length)]), ("fatal", true)))
                    : Packet.Control(PacketType.Reject, session, JsonData.Object(("code", code), ("message", error.Message[..Math.Min(2048, error.Message.Length)]))));
            }
            catch (Exception sendError) when (sendError is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                owner.notify(new("Control closed", $"Could not deliver {code}: {sendError.GetType().Name}"));
            }
            finally { retirement.TrySetResult(); }
        }

        private Connection[] RetireMedia()
        {
            Connection[] channels;
            lock (owner.gate) channels = mediaChannels.OfType<Connection>().ToArray();
            foreach (var channel in channels) channel.Abort();
            return channels;
        }

        private async Task PumpAsync(IPlayback playback, TrackState state, uint track)
        {
            var ready = false;
            try
            {
                using var scheduling = playback.EnterTrack(track);
                while (true)
                {
                    var packet = await queue.TakeAsync(track, playbackCancellation.Token);
                    if (Wire.IsMedia(packet.Type)) playback.ObserveDequeued(packet);
                    var operation = track == 1 ? ReceiverOperation.Video : ReceiverOperation.Audio;
                    activity.Begin(operation, $"{packet.Type} generation={packet.Generation} bytes={packet.Payload.Length}");
                    try
                    {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(playbackCancellation.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    if (packet.Type == PacketType.StreamConfig)
                    {
                        if (ready)
                        {
                            if (playback is not RecoveryPlayback)
                                Interlocked.Exchange(ref startup, Environment.TickCount64);
                            Interlocked.Decrement(ref tracksReady);
                            ready = false;
                        }
                        state.Configure(packet);
                        await playback.ConfigureAsync(packet, timeout.Token);
                        if (playback is RecoveryPlayback recovering && !recovering.IsCurrentConfiguration(packet)) continue;
                        state.MarkReady(packet.Track);
                        if (playback is RecoveryPlayback recovered) recovered.MarkReady(packet);
                        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token,
                            playback is RecoveryPlayback independent ? independent.ReadinessToken(packet) : default);
                        try
                        {
                            await SendAsync(new(PacketType.StreamReady, session, packet.Track, packet.Generation, -1, 0,
                                JsonData.Bytes(JsonData.Object(("ok", true)))), readiness.Token, playback as RecoveryPlayback);
                        }
                        catch (OperationCanceledException) when (playback is RecoveryPlayback retired &&
                            !retired.IsCurrentConfiguration(packet) && !playbackCancellation.IsCancellationRequested)
                        {
                            continue;
                        }
                        if (playback is RecoveryPlayback configured && !configured.IsCurrentConfiguration(packet)) continue;
                        if (playback is RecoveryPlayback initialized) initialized.ReadyWritten(packet);
                        ready = true;
                        Interlocked.Increment(ref tracksReady);
                    }
                    else
                    {
                        if (playback is not RecoveryPlayback) state.Media(packet);
                        await playback.PresentAsync(packet, timeout.Token);
                    }
                    }
                    finally
                    {
                        activity.Complete(operation);
                        queue.Complete(packet);
                    }
                }
            }
            catch (OperationCanceledException) when (playbackCancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                await FailAsync(error is ProtocolException ? error :
                    new ProtocolException($"Playback failed: {error.Message}",
                        error is TimeoutException ? "TIMEOUT" : track == 2 ? "AUDIO_FAILED" : "DECODER_FAILED"), playback);
                retirement.TrySetResult();
            }
        }

        private async Task WatchdogAsync(IPlayback playback)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            var nextPing = Environment.TickCount64 + 2000;
            var nextTransportProbe = Environment.TickCount64;
            var presentationDeadline = new PresentationDeadline(Interlocked.Read(ref startup));
            var presentationSuspended = false;
            try
            {
                while (await timer.WaitForNextTickAsync(playbackCancellation.Token))
                {
                    if (Volatile.Read(ref stopping) != 0) continue;
                    try
                    {
                        if (playback is RecoveryPlayback recovery)
                            recovery.ScheduleProofPolling(playbackCancellation.Token);
                        playback.CheckHealth();
                    }
                    catch (ProtocolException) { throw; }
                    catch (TimeoutException error) { throw new ProtocolException(error.Message, "TIMEOUT"); }
                    catch (Exception error) { throw new ProtocolException($"Playback health failed: {error.Message}", "DECODER_FAILED"); }
                    var now = Environment.TickCount64;
                    if (now >= nextTransportProbe && activity.ReadNoProgressMs >= 250)
                    {
                        activity.RecordTransport(TcpSocketDiagnostics.Read(client.Client));
                        nextTransportProbe = now + 250;
                    }
                    var tracksConfigured = Volatile.Read(ref tracksReady) == expectedTracks;
                    var generationStarted = Interlocked.Read(ref startup);
                    var independent = playback as RecoveryPlayback;
                    if (independent is not null) generationStarted = independent.VideoReadyAtMilliseconds;
                    var presentationExpired = (independent is null || !independent.State.Recovering) &&
                        playback is IVideoPlaybackProgress progress &&
                        presentationDeadline.HasExpired(now, generationStarted, tracksConfigured, progress);
                    if (presentationSuspended != presentationDeadline.IsSuspended)
                    {
                        presentationSuspended = presentationDeadline.IsSuspended;
                        owner.notify(new("Video", presentationSuspended
                            ? "Receiver window is occluded; presentation deadline suspended while decoding and liveness continue."
                            : "Receiver window is visible; presentation deadline resumed."));
                    }
                    if (now - Interlocked.Read(ref lastInput) >= 10_000 ||
                        playback is not RecoveryPlayback && !tracksConfigured && now - generationStarted >= 10_000 || presentationExpired)
                        throw new ProtocolException("Session startup or incoming traffic timed out.", "TIMEOUT");
                    if (now >= nextPing)
                    {
                        if (controlLatency.Begin(now) is { } nonce)
                            await SendAsync(Packet.Control(PacketType.Ping, session, JsonData.Object(("nonce", nonce))));
                        nextPing = now + 2000;
                    }
                }
            }
            catch (OperationCanceledException) when (playbackCancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
            {
                await FailAsync(error, playback);
                retirement.TrySetResult();
            }
        }

        public async Task StopAsync(string reason)
        {
            lock (client)
            {
                if (disposed || ended.Task.IsCompleted) return;
                Interlocked.Exchange(ref stopping, 1);
                playbackCancellation.Cancel();
            }
            // Recovery drains started media/control frames before sending SESSION_END.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(
                streamRecovery ? StreamRecovery.StopAcknowledgementTimeoutSeconds : 2));
            try
            {
                if (accepted)
                {
                    await SendAsync(Packet.Control(PacketType.Stop, session, JsonData.Object(("reason", reason))), deadline.Token).WaitAsync(deadline.Token);
                    if (streamRecovery)
                        deadline.CancelAfter(TimeSpan.FromSeconds(StreamRecovery.StopAcknowledgementTimeoutSeconds));
                    await ended.Task.WaitAsync(deadline.Token);
                }
            }
            catch (Exception error) when (error is TimeoutException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                owner.notify(new("Disconnect", $"Closing control connection: {error.GetType().Name}"));
            }
            finally
            {
                retirement.TrySetResult();
                try { await Completion.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { Abort(); }
            }
        }

        public void Abort()
        {
            // Media retirement acquires the server gate; never nest it inside the client lock.
            RetireMedia();
            lock (client)
            {
                if (disposed) return;
                cancellation.Cancel();
                client.Dispose();
            }
        }

        public void Dispose()
        {
            Task workers;
            lock (client)
            {
                if (disposed) return;
                disposed = true;
                client.Dispose();
                workers = Task.WhenAll(pendingWrites.Concat(new[] { retirementWorkers, pendingControlRead }.OfType<Task>()));
            }
            lock (owner.gate) owner.retiringConnections.Add(this);
            resourceDisposal = DisposeResourcesAsync(workers);
        }

        private async Task DisposeResourcesAsync(Task workers)
        {
            var deferred = !workers.IsCompleted;
            var failed = false;
            try { await workers; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                failed = true;
                NotifyCleanupFailure("connection-workers", error);
            }
            foreach (var (resource, origin) in new (IDisposable?, string)[]
                { (queue, "queue"), (writer, "control-writer"), (playbackCancellation, "playback-cancellation"),
                    (cancellation, "transport-cancellation"), (SessionLease, "session-lease") })
            {
                try { resource?.Dispose(); }
                catch (Exception error)
                {
                    failed = true;
                    NotifyCleanupFailure(origin, error);
                }
            }
            Volatile.Write(ref resourcesDisposed, true);
            lock (owner.gate) owner.retiringConnections.Remove(this);
            if (deferred && !failed)
                owner.notify(new("Cleanup complete", "origin=connection-retirement; receiver resources released."));
        }

        private void NotifyCleanupFailure(string origin, Exception error)
        {
            var type = error.GetType().Name;
            owner.notify(new("Cleanup failed", $"origin={origin}; code=INTERNAL; type={type[..Math.Min(80, type.Length)]}"));
        }
    }
}
