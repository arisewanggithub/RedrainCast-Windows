using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using RedrainCast.Send.Core;
using Xunit;
using Packet = RedrainCast.Receive.Core.Packet;
using ProtocolException = RedrainCast.Receive.Core.ProtocolException;

namespace RedrainCast.Tests;

public sealed class RecoveryTests
{
    [Theory]
    [InlineData("{}", false)]
    [InlineData("""{"streamRecovery":false,"streamRecoveryVersion":1}""", false)]
    [InlineData("""{"streamRecovery":true}""", false)]
    [InlineData("""{"streamRecovery":true,"streamRecoveryVersion":2}""", false)]
    [InlineData("""{"streamRecovery":true,"streamRecoveryVersion":1}""", true)]
    public void CapabilityRequiresExactMutualRevision(string json, bool expected) =>
        Assert.Equal(expected, StreamRecovery.Negotiated(JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("""{"streamRecovery":"true"}""")]
    [InlineData("""{"streamRecoveryVersion":0}""")]
    [InlineData("""{"streamRecoveryVersion":"1"}""")]
    public void InvalidCapabilityFailsExplicitly(string json) =>
        Assert.Throws<FormatException>(() => StreamRecovery.Negotiated(JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("0", true)]
    [InlineData("9223372036854775807", true)]
    [InlineData("01", false)]
    [InlineData("-1", false)]
    [InlineData("+1", false)]
    [InlineData(" 1", false)]
    [InlineData("9223372036854775808", false)]
    public void CanonicalTimestampContract(string value, bool valid)
    {
        var json = JsonSerializer.SerializeToElement(new { keyPtsUs = value });
        if (valid) Assert.Equal(value, StreamRecovery.Timestamp(StreamRecovery.Timestamp(json, "keyPtsUs")));
        else Assert.Throws<FormatException>(() => StreamRecovery.Timestamp(json, "keyPtsUs"));
    }

    [Fact]
    public void RepeatedReadyKeysAndResetsCannotExtendFirstUnresolvedDeadline()
    {
        var deadline = new RecoveryDeadline();
        deadline.Begin(100);
        foreach (var time in new[] { 1000L, 4000, 9000 }) { deadline.Begin(time); deadline.Check(time); }
        Assert.Throws<TimeoutException>(() => deadline.Check(10100));
        deadline.Complete(9000); deadline.Begin(12000); deadline.Check(15000);
    }

    [Fact]
    public void LateCompletionCannotClearAnExpiredDeadlineBetweenWatchdogTicks()
    {
        var deadline = new RecoveryDeadline();
        deadline.Begin(100);
        Assert.Throws<TimeoutException>(() => deadline.Complete(10100));
        Assert.True(deadline.Pending);
        Assert.Throws<TimeoutException>(() => deadline.Check(10101));
        var valid = new RecoveryDeadline();
        valid.Begin(100);
        valid.Complete(10099);
        Assert.False(valid.Pending);
    }

    [Fact]
    public void AudioCoverageDistinguishesWaitingFromIrrecoverableMonotonicGaps()
    {
        Assert.Equal(RecoveryAudioCoverage.Wait, RecoveryAudio.Coverage([], 0, 30_000));
        Assert.Equal(RecoveryAudioCoverage.Wait, RecoveryAudio.Coverage([(0, 960)], 0, 30_000));
        Assert.Equal(RecoveryAudioCoverage.Lost, RecoveryAudio.Coverage([(1, 960)], 0, 10_000));
        Assert.Equal(RecoveryAudioCoverage.Lost, RecoveryAudio.Coverage([(0, 960), (20_022, 960)], 0, 30_000));
        Assert.Equal(RecoveryAudioCoverage.Covered, RecoveryAudio.Coverage([(0, 960), (20_021, 960)], 0, 30_000));
        Assert.Equal(RecoveryAudioCoverage.Wait, RecoveryAudio.Coverage([(1, 960)], 0, 10_000, allowsPtsReversal: true));
        Assert.Equal(RecoveryAudioCoverage.Wait,
            RecoveryAudio.Coverage([(0, 960), (20_022, 960)], 0, 30_000, allowsPtsReversal: true));
        Assert.Equal(RecoveryAudioCoverage.Covered,
            RecoveryAudio.Coverage([(0, 960), (0, 960), (19_551, 960)], 0, 30_000, allowsPtsReversal: true));
    }

    [Fact]
    public void AudioUsesDurationAndSampleFrameBoundaries()
    {
        Assert.Equal(2880, RecoveryAudio.Frames("opus", [24]));
        Assert.Equal(5760, RecoveryAudio.Frames("opus", [27, 2]));
        Assert.Equal(1024, RecoveryAudio.Frames("aac", [1]));
        Assert.Equal(960, RecoveryAudio.Frames("aac", [1], 960));
        Assert.Equal(480, RecoveryAudio.Frames("raw", new byte[1920]));
        Assert.Equal(21, RecoveryAudio.Skip(0, 421, 480));
        Assert.Equal(60_000, RecoveryAudio.End(0, 2880));
        Assert.Throws<FormatException>(() => RecoveryAudio.Frames("opus", [27, 3]));
        Assert.Throws<FormatException>(() => RecoveryAudio.Frames("opus", [1, 0]));
        Assert.Throws<FormatException>(() => RecoveryAudio.Frames("opus", [2]));
        Assert.Equal(960, RecoveryAudio.Frames("opus", [1, 0, 0]));
        Assert.Equal(960, RecoveryAudio.Frames("opus", [2, 0]));
        Assert.Equal(5760, RecoveryAudio.Frames("raw", new byte[23040]));
        Assert.Throws<FormatException>(() => RecoveryAudio.Frames("raw", new byte[23044]));
        Assert.Equal(21334, RecoveryAudio.PrerollUs("aac", 1024));
        Assert.Equal(20000, RecoveryAudio.PrerollUs("aac", 960));
        Assert.False(RecoveryAudio.Covers(new[] { (1L, 960) }, 0, 10_000));
        Assert.True(RecoveryAudio.Covers(new[] { (0L, 960), (20_021L, 960) }, 0, 30_000));
        Assert.False(RecoveryAudio.Covers(new[] { (0L, 960), (20_022L, 960) }, 0, 30_000));
        Assert.True(RecoveryAudio.Covers(new[] { (0L, 2880), (60_000L, 2880) }, 20_000, 100_000));
        Assert.True(RecoveryAudio.Covers(new[] { (0L, 5760) }, 20_000, 100_000));
        Assert.False(RecoveryAudio.Covers(new[] { (0L, 2880), (65_000L, 2880) }, 20_000, 100_000));
        Assert.False(RecoveryAudio.Covers(new[] { (50_000L, 2880) }, 20_000, 100_000));
        Assert.True(RecoveryAudio.Covers(new[] { (0L, 1024), (21_333L, 1024) }, 5_000, 30_000));
    }

    [Theory]
    [InlineData(5760)]
    [InlineData(5761)]
    [InlineData(12000)]
    [InlineData(65536)]
    public void PcmSplittingPreservesAllSamplesAndSourceAlignedTimestamps(int frames)
    {
        var data = Enumerable.Range(0, frames * 4).Select(i => (byte)(i % 251)).ToArray();
        var original = new AudioPacket(data, 1234567890123);
        var parts = PcmPackets.Split(original).ToArray();
        Assert.Equal(data, parts.SelectMany(p => p.Data).ToArray());
        var offset = 0;
        foreach (var part in parts)
        {
            Assert.InRange(RecoveryAudio.Frames("raw", part.Data), 1, 5760);
            Assert.Equal(original.PtsUs + offset * 1_000_000L / 48000, part.PtsUs);
            offset += part.Data.Length / 4;
        }
        Assert.Equal(frames, offset);
        if (frames == 5760) Assert.Same(original, parts[0]);
    }

    [Theory]
    [InlineData(false, 480, 60, false)]
    [InlineData(true, 480, 60, false)]
    [InlineData(true, 12000, 60, false)]
    [InlineData(false, 480, 90, false)]
    [InlineData(false, 480, 120, false)]
    [InlineData(false, 480, 60, true)]
    public async Task ThreeSocketPressureReconstructsBothOutputsAndPreservesSession(bool audio, int sourceAudioFrames, int fps, bool statistics)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stop = new CancellationTokenSource();
        var factory = new FakeFactory(audio) { ReceiverOptions = new(statistics) };
        var notices = new List<string>();
        await using var server = new ReceiverServer("Recovery fake output", factory, notice =>
        {
            lock (notices) notices.Add($"{notice.State}: {notice.Detail}");
        }, 0, IPAddress.Loopback);
        server.Start(false);
        await using var source = new GeneratedSource();
        var sound = audio ? new GeneratedAudio(sourceAudioFrames) : null;
        if (sound is not null)
            factory.ValidateAudio = packet =>
            {
                Assert.True(sound.Original.TryGetValue(packet.PtsUs, out var bytes), "Sender retimed the source PCM packet.");
                Assert.Equal(bytes, packet.Payload);
            };
        var states = new List<string>();
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, server.Port, "Test", false),
            new(MaxSize: source.Width, MaxFps: fps, EnableAudio: audio, ShowPlaybackStatistics: statistics), source,
            state => { lock (states) states.Add(state); }, stop.Token, sound);
        try
        {
            while (factory.RecoveredAudio < (audio ? 1 : 0) || factory.RecoveredVideo < 1 ||
                   !TwiceStreaming())
            {
                if (cast.IsCompleted) { await cast; throw new IOException(string.Join("\n", notices)); }
                await Task.Delay(20, limit.Token);
            }
            Assert.True(factory.Opens >= 2);
            Assert.All(factory.Requests, request => Assert.Equal(fps, request.VideoProcessingFramesPerSecond));
            Assert.All(factory.Requests, request => Assert.Equal(statistics, request.EffectiveOptions.PlaybackStats));
            Assert.True(source.SyncRequests >= 2);
            Assert.True(server.IsBusy);
            if (audio)
            {
                Assert.Single(factory.AudioThreadEntries);
                Assert.Empty(factory.AudioThreadExits);
                Assert.True(factory.Outputs[0].Disposed);
            }
            stop.Cancel();
            await cast.WaitAsync(limit.Token);
            await server.DisposeAsync();
            Assert.All(factory.Outputs, p => Assert.True(p.Disposed));
            if (audio)
                Assert.Equal(Assert.Single(factory.AudioThreadEntries), Assert.Single(factory.AudioThreadExits));
            lock (states) Assert.Contains("Streaming", states);
        }
        finally
        {
            stop.Cancel();
            try { await cast.WaitAsync(limit.Token); }
            catch (Exception error) { throw new IOException(string.Join("\n", notices), error); }
        }
        bool TwiceStreaming() { lock (states) return states.Count(s => s == "Streaming") >= 2; }
    }

    [Fact]
    public async Task SlowBoundedSessionSetupDoesNotConsumeTheCaptureRecoveryDeadline()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        using var stop = new CancellationTokenSource();
        var factory = new FakeFactory(true)
        {
            OpenDelayMilliseconds = 6500,
            ConfigureDelayMilliseconds = 4000
        };
        await using var server = new ReceiverServer("Slow setup fake", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        await using var source = new GeneratedSource();
        var states = new List<string>();
        var watch = Stopwatch.StartNew();
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, server.Port, "Test", false),
            new(MaxSize: source.Width, EnableAudio: true), source,
            state => { lock (states) states.Add(state); }, stop.Token, new GeneratedAudio());
        try
        {
            while (!Recovered())
            {
                if (cast.IsCompleted) { await cast; throw new IOException("Slow setup ended before recovery completion."); }
                await Task.Delay(20, timeout.Token);
            }
            Assert.True(watch.Elapsed > TimeSpan.FromSeconds(10), "The test must exercise total setup+recovery exceeding the old shared ten-second deadline.");
            stop.Cancel();
            await cast.WaitAsync(timeout.Token);
        }
        finally { stop.Cancel(); await cast.WaitAsync(timeout.Token); }
        bool Recovered() { lock (states) return states.Count(s => s == "Streaming") >= 2; }
    }

    [Theory]
    [InlineData(false, 350, 0)]
    [InlineData(true, 350, 0)]
    [InlineData(true, 0, 450)]
    [InlineData(false, 1100, 0)]
    public async Task DelayedDecodedKeyAndCoveredAudioCompleteWithoutResetStorm(bool audio, int decodeDelay, int deliveryDelay)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var stop = new CancellationTokenSource();
        var factory = new FakeFactory(audio) { KeyDecodeDelayMilliseconds = decodeDelay, StallMilliseconds = 0 };
        var states = new ConcurrentQueue<string>();
        await using var server = new ReceiverServer("Delayed fake playback", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        // A delayed first decode is not a video-pressure test: keep fresh key requests live,
        // but withhold dependent pictures until the fake has actually decoded the key.
        await using var source = new GeneratedSource
        {
            CanEmitDependentFrames = () => factory.FirstOutput?.DecodedRecoveryKeyPtsUs is not null
        };
        var sound = audio ? new GeneratedAudio { InitialDeliveryDelayMilliseconds = deliveryDelay } : null;
        if (sound is not null)
            factory.ValidateAudio = packet =>
            {
                Assert.True(sound.Original.TryGetValue(packet.PtsUs, out var bytes));
                Assert.Equal(bytes, packet.Payload);
            };
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, server.Port, "Test", false),
            new(MaxSize: source.Width, EnableAudio: audio), source, states.Enqueue, stop.Token, sound);
        try
        {
            while (!states.Contains("Streaming"))
            {
                if (cast.IsCompleted) { await cast; throw new IOException("Delayed recovery ended before completion."); }
                await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(1, factory.Opens);
            Assert.DoesNotContain(states, s => s.StartsWith("Recovering video generation", StringComparison.Ordinal));
            if (decodeDelay > 0)
            {
                var proof = Assert.Single(states, s => s.StartsWith("Recovery decoded KEY:", StringComparison.Ordinal));
                var age = long.Parse(proof.Split("sourceAgeUs=")[1], System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(age >= 300_000, $"Test did not exercise a legitimately delayed decoded proof: {age}us.");
            }
            stop.Cancel();
            await cast.WaitAsync(timeout.Token);
        }
        finally { stop.Cancel(); await cast.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task AudioPrefixActuallyEvictedFromBoundedQueueStartsOneNewAttempt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var stop = new CancellationTokenSource();
        var factory = new FakeFactory(true) { FirstKeyProofDelayMilliseconds = 1100, StallMilliseconds = 0 };
        var states = new ConcurrentQueue<string>();
        await using var server = new ReceiverServer("Evicted PCM prefix fake", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        // Keep key requests live, but avoid unrelated video queue pressure while PCM ages out.
        await using var source = new GeneratedSource
        {
            CanEmitDependentFrames = () => factory.FirstOutput?.DecodedRecoveryKeyPtsUs is not null
        };
        var sound = new GeneratedAudio();
        factory.ValidateAudio = packet =>
        {
            Assert.True(sound.Original.TryGetValue(packet.PtsUs, out var bytes));
            Assert.Equal(bytes, packet.Payload);
        };
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, server.Port, "Test", false),
            new(MaxSize: source.Width, EnableAudio: true), source, states.Enqueue, stop.Token, sound);
        try
        {
            while (!states.Contains("Streaming"))
            {
                if (cast.IsCompleted) { await cast; throw new IOException("Evicted prefix recovery failed."); }
                await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(2, factory.Opens);
            var retry = Assert.Single(states, s => s.StartsWith("Recovering video generation", StringComparison.Ordinal));
            Assert.Contains("source PCM starts", retry);
            var gap = long.Parse(retry.Split("source PCM starts ")[1].Split("us")[0], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(gap > 250_000, $"Retained source PCM must actually miss the key window: {gap}us.");
            var proof = Assert.Single(states, s => s.StartsWith("Recovery decoded KEY: generation=1,", StringComparison.Ordinal));
            var firstKey = long.Parse(proof.Split("keyPtsUs=")[1].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture);
            var age = long.Parse(proof.Split("sourceAgeUs=")[1], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(age >= 1_000_000, $"The initial decoded proof must retain its real delay: {age}us.");
            Assert.Contains(sound.Original.Keys, pts => pts < firstKey + 250_000);
            Assert.DoesNotContain(states, s => s.Contains("decoded KEY proof is stale", StringComparison.Ordinal));
            stop.Cancel();
            await cast.WaitAsync(timeout.Token);
        }
        finally { stop.Cancel(); await cast.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task MissingSourceAudioTimesOutWithoutElapsedTimeResetLoop()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(13));
        var factory = new FakeFactory(true) { StallMilliseconds = 0 };
        var states = new ConcurrentQueue<string>();
        await using var server = new ReceiverServer("Missing PCM fake", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        // Missing PCM must leave the audio gate unresolved, not provoke independent video pressure.
        // Keep real-time key requests live; dependent pictures are irrelevant until audio completes.
        await using var source = new GeneratedSource
        {
            CanEmitDependentFrames = () => states.Contains("Streaming")
        };
        var clock = Stopwatch.StartNew();
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, server.Port, "Test", false),
            new(MaxSize: source.Width, EnableAudio: true), source, states.Enqueue, default,
            new GeneratedAudio { SuppressPackets = true });
        await Assert.ThrowsAsync<IOException>(() => cast.WaitAsync(timeout.Token));
        Assert.InRange(clock.Elapsed.TotalSeconds, 9, 12);
        Assert.Equal(1, factory.Opens);
        Assert.DoesNotContain(states, s => s.StartsWith("Recovering video generation", StringComparison.Ordinal));
        Assert.DoesNotContain("Streaming", states);
        Assert.Single(states, s => s.StartsWith("Recovery decoded KEY: generation=1,", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("raw", true)]
    [InlineData("aac", true)]
    [InlineData("opus", false)]
    public async Task ReceiverRequestsOnlyProvenLostAudioCoverage(string codec, bool requestsReset)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var factory = new FakeFactory(true) { StallMilliseconds = 0 };
        await using var server = new ReceiverServer("Coverage fake output", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var control = new TcpClient { NoDelay = true };
        await control.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = control.GetStream();
        await RedrainCast.Receive.Core.Wire.WriteAsync(stream, Packet.Control(PacketType.Hello, 0,
            new { role = "sender", versions = new[] { 3 }, name = "Test", streamRecovery = true, streamRecoveryVersion = 1 }), timeout.Token);
        Assert.Equal(PacketType.Hello, (await RedrainCast.Receive.Core.Wire.ReadAsync(stream, timeout.Token)).Type);
        var request = JsonSerializer.SerializeToNode(new CaptureSettings(EnableAudio: true).Request("Fake"))!;
        request["audio"]!["codec"] = codec;
        await RedrainCast.Receive.Core.Wire.WriteAsync(stream, Packet.Control(PacketType.SessionRequest, 1, request), timeout.Token);
        var accept = await RedrainCast.Receive.Core.Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(PacketType.SessionAccept, accept.Type);
        var mediaToken = accept.Object().GetProperty("mediaToken").GetString()!;
        using var video = new TcpClient { NoDelay = true };
        using var audio = new TcpClient { NoDelay = true };
        foreach (var (socket, channel) in new[] { (video, "video"), (audio, "audio") })
        {
            await socket.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            await RedrainCast.Receive.Core.Wire.WriteAsync(socket.GetStream(), Packet.Control(PacketType.Hello, 1,
                new { role = "media", channel, token = mediaToken }), timeout.Token);
            Assert.Equal(PacketType.Hello, (await RedrainCast.Receive.Core.Wire.ReadAsync(socket.GetStream(), timeout.Token)).Type);
        }
        await RedrainCast.Receive.Core.Wire.WriteAsync(video.GetStream(), new(PacketType.StreamConfig, 1, 1, 1, -1, 0,
            JsonSerializer.SerializeToUtf8Bytes(new { codec = "h264", width = 640, height = 360, csd = new[] { "AAAAAQ==" } })), timeout.Token);
        await RedrainCast.Receive.Core.Wire.WriteAsync(audio.GetStream(), new(PacketType.StreamConfig, 1, 2, 1, -1, 0,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                codec, sampleRate = 48000, channels = 2, sampleFormat = "s16le",
                csd = codec switch { "aac" => new[] { "EZA=" }, "opus" => ["T3B1c0hlYWQBAjgBgLsAAAAAAA=="], _ => [] },
                codecDelayNs = 6500000, seekPrerollNs = 80000000
            })), timeout.Token);
        for (var i = 0; i < 2; i++)
            Assert.Equal(PacketType.StreamReady, (await RedrainCast.Receive.Core.Wire.ReadAsync(stream, timeout.Token)).Type);
        const long keyPts = 1_000_000;
        await RedrainCast.Receive.Core.Wire.WriteAsync(video.GetStream(), new(PacketType.VideoFrame, 1, 1, 1, keyPts, 1, [1]), timeout.Token);
        Assert.Equal(StreamRecovery.Key, (await RedrainCast.Receive.Core.Wire.ReadAsync(stream, timeout.Token)).Object().GetProperty("state").GetString());
        var audioPts = codec == "raw" ? keyPts + 250_001 : keyPts;
        byte[] data = codec switch { "opus" => [248, 255, 254], "aac" => [1], _ => new byte[1920] };
        await RedrainCast.Receive.Core.Wire.WriteAsync(audio.GetStream(), new(PacketType.AudioPacket, 1, 2, 1, audioPts, 0, data), timeout.Token);
        if (requestsReset)
            Assert.Equal(StreamRecovery.Request, (await RedrainCast.Receive.Core.Wire.ReadAsync(stream, timeout.Token)).Object().GetProperty("state").GetString());
        else
        {
            using var noReset = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RedrainCast.Receive.Core.Wire.ReadAsync(stream, noReset.Token).AsTask());
        }
        await RedrainCast.Receive.Core.Wire.WriteAsync(stream, Packet.Control(PacketType.Stop, 1, new { reason = "test" }), timeout.Token);
        Assert.Equal(PacketType.SessionEnd, (await RedrainCast.Receive.Core.Wire.ReadAsync(stream, timeout.Token)).Type);
        Assert.All(factory.Outputs, output => Assert.True(output.Disposed));
    }

    [Theory]
    [InlineData("future-request")]
    [InlineData("missing-generation")]
    [InlineData("complete-before-key")]
    [InlineData("noncanonical-key")]
    [InlineData("wrong-key")]
    [InlineData("duplicate-ready")]
    public async Task MalformedRecoveryControlsFailWithoutClaimingCompletion(string invalid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var source = new GeneratedSource();
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, "Test", false),
            new(MaxSize: source.Width), source, _ => { }, default);
        using var control = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = control.GetStream();
        var hello = await RedrainCast.Send.Core.Wire.ReadAsync(stream, timeout.Token);
        Assert.True(hello.Json().GetProperty("streamRecovery").GetBoolean());
        await RedrainCast.Send.Core.Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0,
            new { role = "receiver", versions = new[] { 3 }, name = "Fake", streamRecovery = true, streamRecoveryVersion = 1 }), timeout.Token);
        var request = await RedrainCast.Send.Core.Wire.ReadAsync(stream, timeout.Token);
        var token = new string('A', 64);
        await RedrainCast.Send.Core.Wire.WriteAsync(stream, Message.Control(MessageType.SessionAccept, request.Session,
            new { tracks = new[] { 1 }, mediaToken = token }), timeout.Token);
        using var video = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.Equal(MessageType.Hello, (await RedrainCast.Send.Core.Wire.ReadAsync(video.GetStream(), timeout.Token)).Type);
        await RedrainCast.Send.Core.Wire.WriteAsync(video.GetStream(), Message.Control(MessageType.Hello, request.Session, new { channel = "video" }), timeout.Token);
        using var audio = await listener.AcceptTcpClientAsync(timeout.Token);
        Assert.Equal(MessageType.Hello, (await RedrainCast.Send.Core.Wire.ReadAsync(audio.GetStream(), timeout.Token)).Type);
        await RedrainCast.Send.Core.Wire.WriteAsync(audio.GetStream(), Message.Control(MessageType.Hello, request.Session, new { channel = "audio" }), timeout.Token);
        var config = await RedrainCast.Send.Core.Wire.ReadAsync(video.GetStream(), timeout.Token);
        Assert.Equal(MessageType.StreamConfig, config.Type);
        var ready = new Message(MessageType.StreamReady, request.Session, 1, 1, -1, 0, """{"ok":true}"""u8.ToArray());
        await RedrainCast.Send.Core.Wire.WriteAsync(stream, ready, timeout.Token);
        var key = await RedrainCast.Send.Core.Wire.ReadAsync(video.GetStream(), timeout.Token);
        Assert.Equal(MessageType.VideoFrame, key.Type);
        var body = new Dictionary<string, object>
        {
            ["state"] = invalid == "future-request" || invalid == "missing-generation" ? StreamRecovery.Request :
                invalid == "complete-before-key" ? StreamRecovery.Complete : StreamRecovery.Key,
            ["message"] = "Test", ["generation"] = invalid == "future-request" ? 2 : 1,
            ["keyPtsUs"] = invalid == "noncanonical-key" ? "01" : StreamRecovery.Timestamp(key.PtsUs + (invalid == "wrong-key" ? 1 : 0))
        };
        if (invalid == "missing-generation") body.Remove("generation");
        await RedrainCast.Send.Core.Wire.WriteAsync(stream, invalid == "duplicate-ready" ? ready :
            Message.Control(MessageType.Status, request.Session, body), timeout.Token);
        var response = await RedrainCast.Send.Core.Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(MessageType.Error, response.Type);
        await Assert.ThrowsAsync<IOException>(() => cast.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task StopDuringReconstructionCannotInstallLateOutputsOrReady()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var stop = new CancellationTokenSource();
        var constructing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeFactory(false) { Construction = async token =>
        {
            constructing.TrySetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
        } };
        await using var server = new ReceiverServer("Fake reconstruction", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        await using var source = new GeneratedSource();
        var states = new List<string>();
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, server.Port, "Test", false),
            new(MaxSize: source.Width), source, s => { lock (states) states.Add(s); }, stop.Token);
        try
        {
            await constructing.Task.WaitAsync(timeout.Token);
            var disconnect = server.DisconnectAsync();
            release.TrySetResult();
            await cast.WaitAsync(timeout.Token);
            await disconnect.WaitAsync(timeout.Token);
            Assert.All(factory.Outputs, p => Assert.True(p.Disposed));
            Assert.Equal(0, factory.RecoveredVideo);
            lock (states) Assert.Equal(1, states.Count(s => s == "Streaming"));
        }
        finally { release.TrySetResult(); stop.Cancel(); await cast.WaitAsync(timeout.Token); }
    }

    [Fact]
    public async Task NonAcknowledgingReceiverRetainsStrictCongestionFailure()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var factory = new FakeFactory(false) { SupportsStreamRecovery = false, StallMilliseconds = 2500 };
        var notices = new List<string>();
        await using var server = new ReceiverServer("Legacy strict fake", factory,
            n => { lock (notices) notices.Add($"{n.State}: {n.Detail}"); }, 0, IPAddress.Loopback);
        server.Start(false);
        await using var source = new GeneratedSource();
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, server.Port, "Test", false),
            new(MaxSize: source.Width), source, _ => { }, default);
        await Assert.ThrowsAsync<IOException>(() => cast.WaitAsync(timeout.Token));
        Assert.Equal(1, factory.Opens);
        Assert.Equal(0, source.SyncRequests);
        lock (notices) Assert.Contains(notices, n => n.Contains("CONGESTION", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoFreshRandomAccessCannotExtendRecoveryPastTenSeconds()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(13));
        var factory = new FakeFactory(false);
        await using var server = new ReceiverServer("No sync fake", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        await using var source = new GeneratedSource { IgnoreSync = true };
        var clock = Stopwatch.StartNew();
        var cast = new SenderSession().RunAsync(new(IPAddress.Loopback, server.Port, "Test", false),
            new(MaxSize: source.Width), source, _ => { }, default);
        await Assert.ThrowsAsync<IOException>(() => cast.WaitAsync(timeout.Token));
        Assert.InRange(clock.Elapsed.TotalSeconds, 9, 12);
        Assert.InRange(source.SyncRequests, 10, 22);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(240)]
    public async Task ReceiverReconstructionPreservesEvenTheMaximumAndroidRequest(int fps)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var factory = new FakeFactory(false) { StallMilliseconds = 0 };
        await using var server = new ReceiverServer("High-FPS recovery fake", factory, _ => { }, 0, IPAddress.Loopback);
        server.Start(false);
        using var control = new TcpClient { NoDelay = true };
        using var video = new TcpClient { NoDelay = true };
        using var audio = new TcpClient { NoDelay = true };
        await control.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = control.GetStream();
        await RedrainCast.Receive.Core.Wire.WriteAsync(stream, Packet.Control(PacketType.Hello, 0,
            new { role = "sender", versions = new[] { 3 }, name = "High-FPS fake",
                streamRecovery = true, streamRecoveryVersion = 1 }), timeout.Token);
        Assert.Equal(PacketType.Hello, (await RedrainCast.Receive.Core.Wire.ReadAsync(stream, timeout.Token)).Type);
        var request = JsonSerializer.SerializeToNode(new CaptureSettings(EnableAudio: false).Request("Fake"))!;
        request["video"]!["maxFps"] = fps;
        await RedrainCast.Receive.Core.Wire.WriteAsync(stream, Packet.Control(PacketType.SessionRequest, 1, request), timeout.Token);
        var accepted = await RedrainCast.Receive.Core.Wire.ReadAsync(stream, timeout.Token);
        Assert.Equal(PacketType.SessionAccept, accepted.Type);
        var mediaToken = accepted.Object().GetProperty("mediaToken").GetString()!;
        foreach (var (socket, channel) in new[] { (video, "video"), (audio, "audio") })
        {
            await socket.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            await RedrainCast.Receive.Core.Wire.WriteAsync(socket.GetStream(), Packet.Control(PacketType.Hello, 1,
                new { role = "media", channel, token = mediaToken }), timeout.Token);
            Assert.Equal(PacketType.Hello, (await RedrainCast.Receive.Core.Wire.ReadAsync(socket.GetStream(), timeout.Token)).Type);
        }
        for (uint generation = 1; generation <= 2; generation++)
        {
            await RedrainCast.Receive.Core.Wire.WriteAsync(video.GetStream(), new(PacketType.StreamConfig, 1, 1, generation, -1, 0,
                JsonSerializer.SerializeToUtf8Bytes(new { codec = "h264", width = 640, height = 360,
                    csd = new[] { "AAAAAQ==" } })), timeout.Token);
            var ready = await RedrainCast.Receive.Core.Wire.ReadAsync(stream, timeout.Token);
            Assert.Equal(PacketType.StreamReady, ready.Type);
            Assert.Equal(generation, ready.Generation);
        }
        Assert.Equal(2, factory.Opens);
        Assert.Equal(2, factory.Requests.Count);
        Assert.All(factory.Requests, captured =>
        {
            Assert.Equal(fps, captured.VideoProcessingFramesPerSecond);
            Assert.Equal("h264", captured.VideoCodec);
            Assert.Null(captured.AudioCodec);
        });
        await RedrainCast.Receive.Core.Wire.WriteAsync(stream, Packet.Control(PacketType.Stop, 1, new { reason = "test" }), timeout.Token);
        Assert.Equal(PacketType.SessionEnd, (await RedrainCast.Receive.Core.Wire.ReadAsync(stream, timeout.Token)).Type);
        Assert.All(factory.Outputs, output => Assert.True(output.Disposed));
    }

    private sealed class GeneratedSource : IRecoverableVideoSource
    {
        private readonly byte[] key, dependent, configuration;
        private int request = 1;
        public int SyncRequests;
        public bool IgnoreSync;
        public Func<bool> CanEmitDependentFrames { get; init; } = () => true;
        public string EncoderName => "EXPLICIT FAKE/SYNTHETIC source";
        public int Width { get; }
        public int Height { get; }
        public bool SupportsStreamRecovery => true;
        public GeneratedSource()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-h264.json")));
            var generation = json.RootElement.GetProperty("generations")[0];
            Width = generation.GetProperty("width").GetInt32(); Height = generation.GetProperty("height").GetInt32();
            var frames = generation.GetProperty("frames");
            key = Convert.FromBase64String(frames[0].GetProperty("data").GetString()!);
            dependent = Convert.FromBase64String(frames[1].GetProperty("data").GetString()!);
            configuration = Convert.FromBase64String(frames[0].GetProperty("configuration").GetString()!);
        }
        public ValueTask RequestKeyFrameAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref SyncRequests);
            if (!IgnoreSync) Interlocked.Exchange(ref request, 1);
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            while (true)
            {
                if (Volatile.Read(ref request) == 0 && !CanEmitDependentFrames())
                {
                    await Task.Delay(10, token);
                    continue;
                }
                yield return new(Interlocked.Exchange(ref request, 0) != 0 ? key : dependent, QpcUs(), configuration);
                await Task.Delay(10, token);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GeneratedAudio(int frames = 480) : IAudioSource
    {
        public int InitialDeliveryDelayMilliseconds { get; init; }
        public bool SuppressPackets { get; init; }
        public ConcurrentDictionary<long, byte[]> Original { get; } = new();
        public System.Text.Json.Nodes.JsonNode Configuration => RedrainCast.Protocol.JsonData.Object(
            ("codec", "raw"), ("sampleRate", 48000), ("channels", 2),
            ("csd", RedrainCast.Protocol.JsonData.Value(Array.Empty<string>())), ("sampleFormat", "s16le"));
        public void BeginTransmission() { }
        public async IAsyncEnumerable<AudioPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken token)
        {
            if (SuppressPackets) await Task.Delay(Timeout.Infinite, token);
            if (InitialDeliveryDelayMilliseconds > 0)
            {
                var first = QpcUs();
                await Task.Delay(InitialDeliveryDelayMilliseconds, token);
                for (var offset = 0; offset < InitialDeliveryDelayMilliseconds; offset += 10)
                {
                    var packet = new AudioPacket(new byte[480 * 4], first + offset * 1000L);
                    Original[packet.PtsUs] = packet.Data;
                    yield return packet;
                }
            }
            while (true)
            {
                var pts = QpcUs();
                var bytes = new byte[frames * 4];
                var packet = new AudioPacket(bytes, pts);
                foreach (var part in PcmPackets.Split(packet)) Original[part.PtsUs] = part.Data;
                yield return packet;
                await Task.Delay(frames * 1000 / 48000, token);
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFactory(bool audio) : IPlaybackFactory
    {
        private FakeOutput? firstOutput;
        public FakeOutput? FirstOutput => Volatile.Read(ref firstOutput);
        public Func<CancellationToken, Task>? Construction;
        public Action<Packet>? ValidateAudio;
        public int Opens, RecoveredAudio, RecoveredVideo;
        public ConcurrentQueue<int> AudioThreadEntries { get; } = new();
        public ConcurrentQueue<int> AudioThreadExits { get; } = new();
        public string Description => "EXPLICIT FAKE native outputs";
        public ReceiverOptionCapabilities ReceiverOptions { get; init; } = ReceiverOptionCapabilities.None;
        public bool SupportsStreamRecovery { get; init; } = true;
        public int StallMilliseconds { get; init; } = 900;
        public int KeyDecodeDelayMilliseconds { get; init; }
        public int FirstKeyProofDelayMilliseconds { get; init; }
        public int OpenDelayMilliseconds { get; init; }
        public int ConfigureDelayMilliseconds { get; init; } = 25;
        public List<FakeOutput> Outputs { get; } = [];
        public ConcurrentQueue<CastRequest> Requests { get; } = new();
        public async ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken token)
        {
            Requests.Enqueue(request);
            var output = new FakeOutput(this, ++Opens > 1, audio);
            if (Opens == 1) Volatile.Write(ref firstOutput, output);
            Outputs.Add(output);
            if (Opens == 1) await Task.Delay(OpenDelayMilliseconds, token);
            return output;
        }
    }

    private sealed class FakeOutput(FakeFactory owner, bool recovered, bool audio) : IStreamRecoveryPlayback, IVideoPlaybackProgress
    {
        private int videoCount;
        private long key = -1;
        private long keyObservedAt;
        public bool Disposed;
        public IDisposable? EnterTrack(uint track)
        {
            if (track != 2 || !audio) return null;
            owner.AudioThreadEntries.Enqueue(Environment.CurrentManagedThreadId);
            return new AudioThreadScope(owner.AudioThreadExits);
        }

        private sealed class AudioThreadScope(ConcurrentQueue<int> exits) : IDisposable
        {
            public void Dispose() => exits.Enqueue(Environment.CurrentManagedThreadId);
        }
        public bool HasPresentedVideo => videoCount > 0;
        private long decodedKey = -1;
        public long? DecodedRecoveryKeyPtsUs =>
            Interlocked.Read(ref decodedKey) is var pts && pts >= 0 &&
            (recovered || Environment.TickCount64 - Interlocked.Read(ref keyObservedAt) >= owner.FirstKeyProofDelayMilliseconds)
                ? pts : null;
        public long? FirstRecoverySourceAudioPtsUs { get; private set; }
        public async ValueTask ConfigureRecoveryAsync(Packet video, Packet? sound, bool midstream, CancellationToken token)
        {
            Assert.Equal(audio, sound is not null);
            Assert.Equal(recovered, midstream);
            if (recovered && owner.Construction is { } build) await build(token);
            await Task.Delay(recovered ? 25 : owner.ConfigureDelayMilliseconds, token);
            Assert.False(Disposed);
        }
        public void SetRecoveryKey(long pts) { key = pts; Interlocked.Exchange(ref keyObservedAt, Environment.TickCount64); }
        public ValueTask ConfigureAsync(Packet packet, CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask PresentAsync(Packet packet, CancellationToken token)
        {
            Assert.False(Disposed);
            if (packet.Track == 1)
            {
                videoCount++;
                if (videoCount == 1 && owner.KeyDecodeDelayMilliseconds > 0)
                    await Task.Delay(owner.KeyDecodeDelayMilliseconds, token);
                Interlocked.Exchange(ref decodedKey, key);
                if (!recovered && videoCount == 10 && owner.StallMilliseconds > 0)
                    await Task.Delay(owner.StallMilliseconds, token);
                if (recovered) Interlocked.Increment(ref owner.RecoveredVideo);
            }
            else
            {
                owner.ValidateAudio?.Invoke(packet);
                var skip = RecoveryAudio.Skip(packet.PtsUs, key, packet.Payload.Length / 4);
                var pts = packet.PtsUs + skip * 1_000_000L / 48000;
                Assert.True(pts >= key);
                FirstRecoverySourceAudioPtsUs ??= pts;
                if (recovered) Interlocked.Increment(ref owner.RecoveredAudio);
            }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private static long QpcUs() => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);
}
