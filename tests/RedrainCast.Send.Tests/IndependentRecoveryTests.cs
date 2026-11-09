using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using RedrainCast.Protocol;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class IndependentRecoveryTests
{
    [Theory]
    [InlineData(true, true, 2, true)]
    [InlineData(true, true, 1, false)]
    [InlineData(true, true, 3, false)]
    [InlineData(true, true, null, false)]
    [InlineData(true, false, 1, true)]
    [InlineData(true, false, null, true)]
    [InlineData(false, true, 2, false)]
    public async Task RecoveryAcknowledgementRequiresMutualV2WithoutSharedV1Fallback(
        bool offered, bool capability, int? version, bool requestsSession)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var source = new Video { SupportsStreamRecovery = offered };
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port,
            "Negotiation", false), new(), source, _ => { }, limit.Token);
        using var peer = await listener.AcceptTcpClientAsync(limit.Token);
        var stream = peer.GetStream();
        var offer = (await Wire.ReadAsync(stream, limit.Token)).Json();
        Assert.Equal(offered, offer.TryGetProperty("streamRecovery", out _));
        if (offered) Assert.Equal(2, StrictJson.Integer(offer, "streamRecoveryVersion"));
        var acknowledgement = JsonData.Object(("role", "receiver"), ("versions", JsonData.Value(new[] { 3 })),
            ("name", "Negotiation"), ("uncappedFps", true), ("streamRecovery", capability));
        if (version is { } revision) acknowledgement["streamRecoveryVersion"] = revision;
        await Wire.WriteAsync(stream, Message.Control(MessageType.Hello, 0, acknowledgement), limit.Token);
        if (requestsSession)
        {
            var request = await Wire.ReadAsync(stream, limit.Token);
            Assert.Equal(MessageType.SessionRequest, request.Type);
            await Wire.WriteAsync(stream, Message.Control(MessageType.Reject, request.Session,
                new { code = "BUSY", message = "negotiation complete" }), limit.Token);
        }
        var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(limit.Token));
        if (requestsSession) Assert.Contains("negotiation complete", error.Message);
        else Assert.IsType<ProtocolException>(error.InnerException);
    }

    [Fact]
    public async Task ThreeSocketsKeepHealthyAudioFlowingAcrossVideoRecoveryWithoutKeyProof()
    {
        await using var fixture = new SocketFixture();
        await fixture.Connect();
        await fixture.Ready(2, 1);
        Assert.False(fixture.Sound.Started);
        await fixture.Ready(1, 1);
        await fixture.Barrier();
        await fixture.SendAudio();
        var initialAudio = await fixture.ReadMedia(2);
        Assert.Equal(1u, initialAudio.Generation);
        await fixture.Proof(StreamRecovery.Request, 1, 1);
        var reset = await fixture.ReadMedia(1);
        Assert.Equal(MessageType.StreamConfig, reset.Type);
        Assert.Equal(2u, reset.Generation);
        await fixture.SendAudio();
        var healthyAudio = await fixture.ReadMedia(2);
        Assert.Equal(MessageType.AudioPacket, healthyAudio.Type);
        Assert.Equal(1u, healthyAudio.Generation);
        Assert.Equal(fixture.LastAudio!.PtsUs, healthyAudio.PtsUs);
        Assert.Equal(fixture.LastAudio.Data, healthyAudio.Payload);
        await fixture.Ready(1, 2);
        await fixture.Barrier();
        await fixture.SendVideo();
        var key = await fixture.ReadMedia(1);
        Assert.Equal(2u, key.Generation);
        await fixture.Proof(StreamRecovery.Key, 1, 2, key.PtsUs);
        await fixture.Proof(StreamRecovery.Complete, 1, 2, key.PtsUs);
        await fixture.Barrier();
        await fixture.SendAudio();
        Assert.Equal(1u, (await fixture.ReadMedia(2)).Generation);
        await fixture.Stop();
    }

    [Fact]
    public async Task ThreeSocketsAudioRecoveryHasOwnConfigCutoffAndLeavesVideoGenerationUnchanged()
    {
        await using var fixture = new SocketFixture();
        await fixture.Connect();
        await fixture.Ready(1, 1);
        await fixture.Ready(2, 1);
        await fixture.Barrier();
        await fixture.SendVideo();
        var firstKey = await fixture.ReadMedia(1);
        await fixture.Proof(StreamRecovery.Key, 1, 1, firstKey.PtsUs);
        await fixture.Proof(StreamRecovery.Complete, 1, 1, firstKey.PtsUs);
        await fixture.SendAudio();
        await fixture.ReadMedia(2);
        await fixture.Proof(StreamRecovery.Request, 2, 1);
        var config = await fixture.ReadMedia(2);
        Assert.Equal(MessageType.StreamConfig, config.Type);
        Assert.Equal(2u, config.Generation);
        var cutoff = StreamRecovery.Timestamp(config.Json(), "recoveryPtsUs");
        Assert.Equal("raw", config.Json().GetProperty("codec").GetString());
        await fixture.SendVideo();
        Assert.Equal(1u, (await fixture.ReadMedia(1)).Generation);
        await fixture.Ready(2, 2);
        await fixture.Barrier();
        await fixture.SendAudio();
        var recovered = await fixture.ReadMedia(2);
        Assert.Equal(2u, recovered.Generation);
        Assert.Equal(fixture.LastAudio!.PtsUs, recovered.PtsUs);
        Assert.Equal(fixture.LastAudio.Data, recovered.Payload);
        Assert.True(recovered.PtsUs >= cutoff);
        await fixture.Proof(StreamRecovery.Complete, 2, 2, recovered.PtsUs);
        await fixture.Barrier();
        await fixture.SendVideo();
        Assert.Equal(1u, (await fixture.ReadMedia(1)).Generation);
        await fixture.Stop();
    }

    [Fact]
    public async Task InitialAudioPrimingStaysBoundedAtGenerationOneAndVideoWaitsForAllInitialReadiness()
    {
        await using var fixture = new SocketFixture();
        await fixture.Connect();
        await fixture.Ready(2, 1);
        var first = NowUs() - 900_000;
        for (var index = 0; index < 40; index++) await fixture.SendAudio(first + index * 20_000);
        await fixture.SendVideo();
        await fixture.Barrier();
        Assert.False(fixture.Sound.Started);
        Assert.Equal(0, fixture.MediaAvailable(1));
        Assert.Equal(0, fixture.MediaAvailable(2));
        await fixture.Ready(1, 1);
        await fixture.Barrier();
        Assert.True(fixture.Sound.Started);
        await fixture.SendAudio();
        var audio = await fixture.ReadMedia(2);
        Assert.Equal(MessageType.AudioPacket, audio.Type);
        Assert.Equal(1u, audio.Generation);
        Assert.Equal(fixture.LastAudio!.PtsUs, audio.PtsUs);
        Assert.Equal(fixture.LastAudio.Data, audio.Payload);
        await fixture.SendVideo();
        var key = await fixture.ReadMedia(1);
        Assert.Equal(1u, key.Generation);
        await fixture.Stop();
    }

    [Fact]
    public async Task FreshVerifiedVideoIsRequiredAndHardwareSyncRequestsArePacedAcrossAttempts()
    {
        await using var fixture = new SocketFixture();
        await fixture.Connect();
        await fixture.Ready(1, 1);
        await fixture.Ready(2, 1);
        await fixture.Barrier();
        await fixture.SendVideo(NowUs() - 500_000, true);
        await fixture.SendVideo(NowUs(), false);
        var fresh = NowUs();
        await fixture.SendVideo(fresh, true);
        var key = await fixture.ReadMedia(1);
        Assert.Equal(fresh, key.PtsUs);
        Assert.Equal(1u, key.Flags);
        await fixture.Proof(StreamRecovery.Request, 1, 1);
        Assert.Equal(2u, (await fixture.ReadMedia(1)).Generation);
        await fixture.Ready(1, 2);
        await fixture.Barrier();
        await Task.Delay(1100);
        var requests = fixture.SyncRequests;
        Assert.InRange(requests.Length, 1, 3);
        for (var index = 1; index < requests.Length; index++) Assert.True(requests[index] - requests[index - 1] >= 500);
        await fixture.Stop();
    }

    [Fact]
    public async Task HardwareSyncCadenceUsesActualRequestTimeAfterRecoveryGateContention()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var fixture = new StateFixture();
        fixture.Set("initialReady", true);
        fixture.SetTrack(1, "Phase", "WaitSource");
        Task monitored;
        lock (fixture.Gate)
        {
            var heartbeat = fixture.CallAsync("HeartbeatAsync");
            monitored = fixture.CallAsync("Monitor", heartbeat, "TIMEOUT");
            Thread.Sleep(300);
        }
        try
        {
            await Task.Delay(1100, limit.Token);
            var requests = fixture.SyncRequestTimes;
            Assert.True(requests.Length >= 2);
            for (var index = 1; index < requests.Length; index++)
                Assert.True(requests[index] - requests[index - 1] >= 500);
        }
        finally
        {
            fixture.Call("StopMedia");
            await monitored.WaitAsync(limit.Token);
        }
        Assert.Null(fixture.TerminalError);
    }

    [Fact]
    public async Task DuplicateReadinessIsFatalAndCannotReactivateAnEpoch()
    {
        await using var fixture = new SocketFixture();
        await fixture.Connect();
        await fixture.Ready(1, 1);
        await fixture.Barrier();
        await fixture.Ready(1, 1);
        await fixture.AssertFailure("Duplicate/stale recovery readiness");
        Assert.False(fixture.Sound.Started);
    }

    [Fact]
    public void VideoFrameRequiresMatchingEmbeddedConfiguration()
    {
        var csd = Configuration();
        VideoBitstream.ValidateConfiguration("h264", csd, 640, 360);
        VideoBitstream.ValidateFrame("h264", [.. csd, .. Key], csd);
        var changed = csd.ToArray();
        changed[6] ^= 1;
        Assert.Throws<ProtocolException>(() => VideoBitstream.ValidateFrame("h264", [.. changed, .. Key], csd));
        VideoBitstream.ValidateFrame("h264", Key, csd);
    }

    [Theory]
    [InlineData(750_000, 750_000, 1_000_000, true)]
    [InlineData(749_999, 500_000, 1_000_000, false)]
    [InlineData(900_000, 900_001, 1_000_000, false)]
    [InlineData(1_050_000, 900_000, 1_000_000, true)]
    [InlineData(1_050_001, 900_000, 1_000_000, false)]
    public void FreshKeyAdmissionChecksExactSourceTimeBoundaries(long pts, long ready, long now, bool admitted)
    {
        var type = typeof(SenderSession).Assembly.GetType("RedrainCast.Send.Core.RecoverySender", true)!;
        var result = (bool)type.GetMethod("FreshKeyTimestamp", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [pts, ready, now])!;
        Assert.Equal(admitted, result);
    }

    [Fact]
    public void AudioProofRequiresWrittenExclusiveSourceIntervalsNotQueuedPacketsOrHoles()
    {
        using var fixture = new StateFixture();
        fixture.PrepareAudio();
        fixture.AudioQueue.Enqueue(fixture.Packet(1_000_000));
        Assert.Throws<ProtocolException>(() => fixture.Proof(StreamRecovery.Complete, 2, 2, 1_000_000));
        fixture.Call("Written", fixture.Packet(1_000_000));
        fixture.Call("Written", fixture.Packet(1_040_000));
        Assert.Throws<ProtocolException>(() => fixture.Proof(StreamRecovery.Complete, 2, 2, 1_020_000));
        Assert.Throws<ProtocolException>(() => fixture.Proof(StreamRecovery.Complete, 2, 2, 1_030_000));
        Assert.Throws<ProtocolException>(() => fixture.Proof(StreamRecovery.Complete, 2, 2, 1_100_000));
        fixture.Proof(StreamRecovery.Complete, 2, 2, 1_019_999);
        Assert.Equal("Active", fixture.AudioPhase);
        fixture.Proof(StreamRecovery.Complete, 2, 2, 1_019_999);
        Assert.Throws<ProtocolException>(() => fixture.Proof(StreamRecovery.Complete, 2, 2, 1_019_998));
    }

    [Fact]
    public void PcmRecoveryKeepsStraddlingSourcePacketAndWaitsForSourceInsteadOfWallClockRetry()
    {
        using var fixture = new StateFixture();
        fixture.PrepareAudio();
        fixture.Set("initialReady", true);
        fixture.SetTrack(2, "Phase", "WaitSource");
        fixture.Call("ReleaseAudio");
        Assert.Equal("WaitSource", fixture.AudioPhase);
        Assert.Equal(2u, fixture.TrackField<uint>(2, "Generation"));
        var packet = fixture.Packet(990_000);
        fixture.AudioQueue.Enqueue(packet);
        fixture.Call("ReleaseAudio");
        Assert.Equal("WaitProof", fixture.AudioPhase);
        Assert.Same(packet, Assert.Single(fixture.AudioQueue));
        Assert.Equal(990_000, packet.PtsUs);
        Assert.Equal(3840, packet.Payload.Length);
    }

    [Fact]
    public void LostPcmSourceWindowRestartsOnlyAudioAndDoesNotRefreshItsDeadline()
    {
        using var fixture = new StateFixture();
        fixture.PrepareAudio();
        fixture.Set("initialReady", true);
        fixture.SetTrack(2, "Phase", "WaitSource");
        fixture.VideoQueue.Enqueue(new(MessageType.VideoFrame, 73, 1, 1, 1_000_000, 1, Key));
        fixture.AudioQueue.Enqueue(fixture.Packet(1_250_001));
        fixture.Call("ReleaseAudio");
        Assert.Equal("WaitReady", fixture.AudioPhase);
        Assert.Equal(3u, fixture.TrackField<uint>(2, "Generation"));
        Assert.Single(fixture.VideoQueue);
        var configuration = Assert.Single(fixture.AudioQueue);
        Assert.Equal(MessageType.StreamConfig, configuration.Type);
        Assert.Equal(3u, configuration.Generation);
        Assert.True(StreamRecovery.Timestamp(configuration.Json(), "recoveryPtsUs") > 0);
        Assert.Throws<TimeoutException>(() => fixture.AudioDeadline.Check(fixture.AttemptAt + 10_000));
    }

    [Fact]
    public void InflightProofWaitsForSuccessfulWriteAndKeepsOriginalDeadline()
    {
        using var fixture = new StateFixture();
        fixture.PrepareAudio();
        fixture.SetTrack(2, "Writing", fixture.Packet(1_000_000));
        fixture.Proof(StreamRecovery.Complete, 2, 2, 1_010_000);
        Assert.Equal("WaitProof", fixture.AudioPhase);
        Assert.True(fixture.AudioDeadline.Pending);
        Assert.Throws<ProtocolException>(() => fixture.Proof(StreamRecovery.Complete, 2, 2, 1_010_001));
        fixture.Call("Written", fixture.Packet(1_000_000));
        Assert.Equal("Active", fixture.AudioPhase);
        Assert.False(fixture.AudioDeadline.Pending);
    }

    [Fact]
    public void RetiredInflightAudioWriteCannotCompleteOrRefreshTheNewAttempt()
    {
        using var fixture = new StateFixture();
        fixture.PrepareAudio();
        var old = fixture.Packet(1_000_000);
        fixture.SetTrack(2, "Writing", old);
        fixture.Proof(StreamRecovery.Complete, 2, 2, 1_010_000);
        fixture.Proof(StreamRecovery.Request, 2, 2);
        fixture.Call("Written", old);
        fixture.Proof(StreamRecovery.Complete, 2, 2, 1_010_000);
        Assert.Equal(3u, fixture.TrackField<uint>(2, "Generation"));
        Assert.Equal("WaitReady", fixture.AudioPhase);
        Assert.Throws<TimeoutException>(() => fixture.AudioDeadline.Check(fixture.AttemptAt + 10_000));
    }

    [Fact]
    public void RepeatedGenerationResetsRetainTheExactFirstUnresolvedDeadline()
    {
        using var fixture = new StateFixture();
        fixture.PrepareAudio();
        for (uint generation = 3; generation <= 5; generation++)
        {
            fixture.Call("BeginAttempt", 2u, "repeated source-window loss");
            Assert.Equal(generation, fixture.TrackField<uint>(2, "Generation"));
            fixture.AudioDeadline.Check(fixture.AttemptAt + 9_999);
            Assert.Throws<TimeoutException>(() => fixture.AudioDeadline.Check(fixture.AttemptAt + 10_000));
        }
    }

    [Fact]
    public void StaleProofIsIgnoredFutureProofAndMixedTimestampShapesFail()
    {
        using var fixture = new StateFixture();
        fixture.PrepareAudio();
        fixture.Proof(StreamRecovery.Complete, 2, 1, 0);
        Assert.Equal("WaitProof", fixture.AudioPhase);
        Assert.Throws<ProtocolException>(() => fixture.Proof(StreamRecovery.Complete, 2, 3, 1_000_000));
        Assert.Throws<ProtocolException>(() => fixture.Call("HandleProof", StreamRecovery.Complete,
            StrictJson.Parse("""{"track":2,"generation":1,"audioPtsUs":"0","keyPtsUs":"0"}"""u8)));
        Assert.Throws<ProtocolException>(() => fixture.Proof(StreamRecovery.Key, 2, 2, 1_000_000));
        Assert.Throws<ProtocolException>(() => fixture.Call("HandleProof", StreamRecovery.Complete,
            StrictJson.Parse("""{"track":2,"generation":2,"audioPtsUs":"01"}"""u8)));
        Assert.Throws<ProtocolException>(() => fixture.Call("HandleProof", StreamRecovery.Complete,
            StrictJson.Parse("""{"track":2,"generation":2,"audioPtsUs":1000000}"""u8)));
    }

    [Fact]
    public void DisabledAudioTrackCannotRequestOrProveRecovery()
    {
        using var fixture = new StateFixture(withAudio: false);
        Assert.Throws<ProtocolException>(() => fixture.Proof(StreamRecovery.Request, 2, 1));
        Assert.Throws<ProtocolException>(() => fixture.Proof(StreamRecovery.Complete, 2, 1, 0));
    }

    [Fact]
    public void RequestCoalescingAndProofCapacityRetireOnlyAudioUnderOriginalDeadline()
    {
        using var fixture = new StateFixture();
        fixture.PrepareAudio();
        fixture.VideoQueue.Enqueue(new(MessageType.VideoFrame, 73, 1, 1, 1_000_000, 1, Key));
        fixture.SetTrack(2, "Phase", "WaitReady");
        fixture.Proof(StreamRecovery.Request, 2, 2);
        Assert.Equal(2u, fixture.TrackField<uint>(2, "Generation"));
        fixture.SetTrack(2, "Phase", "WaitProof");
        for (var index = 0; index < 4096; index++)
            fixture.Call("Written", fixture.Packet(1_000_000 + index * 40_000L));
        fixture.Call("Written", fixture.Packet(1_000_000 + 4096 * 40_000L));
        Assert.Equal(3u, fixture.TrackField<uint>(2, "Generation"));
        Assert.Single(fixture.VideoQueue);
        Assert.Equal(1u, fixture.TrackField<uint>(1, "Generation"));
        Assert.Equal(MessageType.StreamConfig, Assert.Single(fixture.AudioQueue).Type);
        Assert.True(fixture.AudioDeadline.Pending);
        fixture.AudioDeadline.Check(fixture.AttemptAt + 9_999);
        Assert.Throws<TimeoutException>(() => fixture.AudioDeadline.Check(fixture.AttemptAt + 10_000));
    }

    [Fact]
    public void DelayedVideoProofIsNotRecheckedAgainstCaptureAgeButOriginalDeadlineIsEnforced()
    {
        using var fixture = new StateFixture();
        fixture.SetTrack(1, "Phase", "WaitProof");
        fixture.SetTrack(1, "SentKey", 0L);
        fixture.Proof(StreamRecovery.Key, 1, 1, 0);
        fixture.Proof(StreamRecovery.Key, 1, 1, 0);
        var diagnostic = Assert.Single(fixture.Diagnostics, text => text.StartsWith("Recovery decoded KEY:", StringComparison.Ordinal));
        Assert.Contains("generation=1", diagnostic);
        Assert.Contains("keyPtsUs=0", diagnostic);
        Assert.Contains("sourceAgeUs=", diagnostic);
        fixture.Proof(StreamRecovery.Complete, 1, 1, 0);
        Assert.Equal("Active", fixture.TrackField<object>(1, "Phase").ToString());
        fixture.SetTrack(1, "Phase", "WaitProof");
        fixture.TrackField<RecoveryDeadline>(1, "Deadline").Begin(Environment.TickCount64 - 10_000);
        Assert.Throws<TimeoutException>(() => fixture.Proof(StreamRecovery.Key, 1, 1, 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InitialStreamingNotificationHandlesEitherIndependentCompletionOrder(bool videoFirst)
    {
        using var fixture = new StateFixture();
        fixture.Set("initialReady", true);
        fixture.SetTrack(1, "Phase", "WaitProof");
        fixture.SetTrack(1, "SentKey", 0L);
        fixture.SetTrack(2, "Phase", "WaitSource");
        fixture.SetTrack(2, "Cutoff", 1_000_000L);
        fixture.AudioQueue.Enqueue(fixture.Packet(1_000_000));
        void VideoCompleted()
        {
            fixture.Proof(StreamRecovery.Key, 1, 1, 0);
            fixture.Proof(StreamRecovery.Complete, 1, 1, 0);
        }
        if (videoFirst) VideoCompleted();
        else fixture.Call("ReleaseAudio");
        Assert.DoesNotContain("Streaming", fixture.Diagnostics);
        Assert.Equal("Active", fixture.TrackField<object>(videoFirst ? 1 : 2, "Phase").ToString());
        if (videoFirst) fixture.Call("ReleaseAudio");
        else VideoCompleted();
        Assert.Single(fixture.Diagnostics, text => text == "Streaming");
        Assert.Equal("Active", fixture.AudioPhase);
        Assert.Equal("Active", fixture.TrackField<object>(1, "Phase").ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task IndependentByteBudgetIncludesBlockedWriteAtExactBoundaryAndResetsOnlyPressuredLane(int lane)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var fixture = new StateFixture();
        fixture.Set("initialReady", true);
        foreach (var track in new[] { 1, 2 })
        {
            fixture.SetTrack(track, "Phase", "Active");
            fixture.SetTrack(track, "Announced", 1u);
            fixture.SetTrack(track, "ReadyGeneration", 1u);
        }
        var writingBytes = lane == 1 ? 4 * 1024 * 1024 : 16 * 1024;
        var queuedPacketBytes = lane == 1 ? 1024 * 1024 : 16 * 1024;
        var maximum = lane == 1 ? 8 * 1024 * 1024 : 512 * 1024;
        Message Packet(int bytes, long pts = 1_000_000) => new(lane == 1 ? MessageType.VideoFrame : MessageType.AudioPacket,
            73, (uint)lane, 1, pts, 0, new byte[bytes]);
        var queue = lane == 1 ? fixture.VideoQueue : fixture.AudioQueue;
        var wake = lane == 1 ? fixture.VideoWake : fixture.AudioWake;
        var writingPacket = Packet(writingBytes, 0);
        queue.Enqueue(writingPacket);
        using var stream = new PendingPayloadStream(fixture.TransportSocket, "success");
        var writer = fixture.CallAsync("WriteLaneAsync", queue, wake, stream);
        var monitored = fixture.CallAsync("Monitor", writer, lane == 1 ? "CAPTURE_FAILED" : "AUDIO_FAILED");
        await stream.Started.Task.WaitAsync(limit.Token);
        for (var bytes = 0; bytes < maximum - writingBytes - queuedPacketBytes; bytes += queuedPacketBytes)
            queue.Enqueue(Packet(queuedPacketBytes));
        var otherQueue = lane == 1 ? fixture.AudioQueue : fixture.VideoQueue;
        var healthy = new Message(lane == 1 ? MessageType.AudioPacket : MessageType.VideoFrame, 73,
            lane == 1 ? 2u : 1u, 1, 1_000_000, 0, new byte[4096]);
        otherQueue.Enqueue(healthy);
        Assert.False(fixture.Full(lane, Packet(queuedPacketBytes), maximum));
        Assert.True(fixture.Full(lane, Packet(queuedPacketBytes + 4), maximum));
        Assert.Same(writingPacket, fixture.TrackField<Message>(lane, "Writing"));
        fixture.Call("BeginAttempt", (uint)lane, "blocked-write byte pressure");
        Assert.Equal(2u, fixture.TrackField<uint>(lane, "Generation"));
        Assert.Equal(1u, fixture.TrackField<uint>(lane == 1 ? 2 : 1, "Generation"));
        Assert.Same(healthy, Assert.Single(otherQueue));
        Assert.Same(writingPacket, fixture.TrackField<Message>(lane, "Writing"));
        Assert.Equal(MessageType.StreamConfig, Assert.Single(queue).Type);
        Assert.False(writer.IsCompleted);
        fixture.Call("StopMedia");
        stream.Release.TrySetResult();
        await monitored.WaitAsync(limit.Token);
        Assert.Null(fixture.TerminalError);
    }

    [Fact]
    public void InitialPhaseTimestampCannotExpireAnAlreadyActiveSession()
    {
        using var fixture = new StateFixture();
        fixture.Set("phaseAt", Environment.TickCount64 - 20_000);
        fixture.Set("initialReady", true);
        fixture.Call("CheckDeadlines");
        fixture.Set("initialReady", false);
        Assert.Throws<TimeoutException>(() => fixture.Call("CheckDeadlines"));
    }

    [Theory]
    [InlineData("success", true)]
    [InlineData("transport", true)]
    [InlineData("write-timeout", true)]
    [InlineData("success", false)]
    public async Task PostWriteCallbackIsSerializedWithStopWithoutHidingTransportOrRecoveryDeadlineFailure(
        string outcome, bool stopping)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var fixture = new StateFixture();
        fixture.PrepareAudio();
        fixture.Set("initialReady", true);
        fixture.SetTrack(2, "ReadyGeneration", 2u);
        fixture.SetTrack(2, "Announced", 2u);
        var packet = fixture.Packet(1_000_000);
        fixture.AudioQueue.Enqueue(packet);
        using var stream = new PendingPayloadStream(fixture.TransportSocket, outcome);
        var writer = fixture.CallAsync("WriteLaneAsync", fixture.AudioQueue, fixture.AudioWake, stream);
        var monitored = fixture.CallAsync("Monitor", writer, "AUDIO_FAILED");
        await stream.Started.Task.WaitAsync(limit.Token);
        fixture.Proof(StreamRecovery.Complete, 2, 2, 1_010_000);
        fixture.AudioDeadline.Complete(Environment.TickCount64);
        fixture.AudioDeadline.Begin(Environment.TickCount64 - 10_000);
        if (stopping) fixture.Call("StopMedia");
        stream.Release.TrySetResult();
        await monitored.WaitAsync(limit.Token);
        Assert.Equal(2, stream.WriteCalls);
        Assert.Equal("WaitProof", fixture.AudioPhase);
        Assert.True(fixture.AudioDeadline.Pending);
        Assert.Empty(fixture.AudioQueue);
        if (outcome == "success" && stopping) Assert.Null(fixture.TerminalError);
        else if (outcome == "transport") Assert.IsType<IOException>(fixture.TerminalError);
        else Assert.IsType<TimeoutException>(fixture.TerminalError);
    }

    [Fact]
    public async Task StartedControlFrameSurvivesCallerCancellationAndFinishesBeforeStop()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource();
        OrderedControlStream? stream = null;
        using var fixture = new StateFixture(createControl: socket => stream = new OrderedControlStream(socket));
        var ping = fixture.CallAsync("SendControlAsync", Message.Control(MessageType.Ping, 73, new { nonce = 11 }), caller.Token);
        await stream!.Started.Task.WaitAsync(limit.Token);
        caller.Cancel();
        fixture.Call("StopMedia");
        var terminal = fixture.CallAsync("SendControlAsync", Message.Control(MessageType.Stop, 73, new { reason = "user" }), limit.Token);
        Assert.False(ping.IsCompleted);
        Assert.False(terminal.IsCompleted);
        Assert.Equal(2, stream.WriteCalls);
        stream.Release.TrySetResult();
        await Task.WhenAll(ping, terminal).WaitAsync(limit.Token);
        Assert.Equal(4, stream.WriteCalls);
        using var packets = new MemoryStream(stream.Bytes.ToArray());
        Assert.Equal(MessageType.Ping, (await Wire.ReadAsync(packets, limit.Token)).Type);
        Assert.Equal(MessageType.Stop, (await Wire.ReadAsync(packets, limit.Token)).Type);
        Assert.Equal(packets.Length, packets.Position);
    }

    private sealed class OrderedControlStream(Socket socket) : NetworkStream(socket, ownsSocket: false)
    {
        public MemoryStream Bytes { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCalls { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++WriteCalls == 2)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await Bytes.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Bytes.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class PendingPayloadStream(Socket socket, string outcome) : NetworkStream(socket, ownsSocket: false)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCalls { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++WriteCalls == 1) return;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (outcome == "transport") throw new IOException("independent failed TCP payload");
            if (outcome == "write-timeout") throw new OperationCanceledException("independent TCP write deadline", cancellationToken);
        }
    }

    private static long NowUs() => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);
    private static readonly byte[] Key = [0, 0, 0, 1, 0x65, 0xb8];

    private static byte[] Configuration()
    {
        var bits = new List<int>();
        void Add(uint value, int count)
        {
            for (var index = count - 1; index >= 0; index--) bits.Add((int)(value >> index & 1));
        }
        void Unsigned(uint value)
        {
            var count = 32 - System.Numerics.BitOperations.LeadingZeroCount(value + 1);
            Add(0, count - 1); Add(value + 1, count);
        }
        Add(66, 8); Add(0, 8); Add(30, 8);
        Unsigned(0); Unsigned(0); Unsigned(0); Unsigned(0); Unsigned(1); Add(0, 1);
        Unsigned(39); Unsigned(22); Add(1, 1); Add(1, 1); Add(1, 1);
        Unsigned(0); Unsigned(0); Unsigned(0); Unsigned(4); Add(0, 1); Add(1, 1);
        var sps = new byte[1 + (bits.Count + 7) / 8];
        sps[0] = 0x67;
        for (var index = 0; index < bits.Count; index++) sps[1 + index / 8] |= (byte)(bits[index] << (7 - index % 8));
        return AnnexB.Join([sps, [0x68, 0xce, 0x06, 0xe2]]);
    }

    private sealed class Video : IRecoverableVideoSource
    {
        private readonly Channel<EncodedVideo> frames = Channel.CreateUnbounded<EncodedVideo>();
        public System.Collections.Concurrent.ConcurrentQueue<long> SyncRequests { get; } = new();
        public string EncoderName => "Synthetic baseline recovery";
        public int Width => 640;
        public int Height => 360;
        public bool SupportsStreamRecovery { get; init; } = true;
        public ValueTask RequestKeyFrameAsync(CancellationToken token)
        {
            SyncRequests.Enqueue(Environment.TickCount64);
            return ValueTask.CompletedTask;
        }
        public ValueTask Offer(long? pts = null, bool key = true) =>
            frames.Writer.WriteAsync(new(key ? Key : [0, 0, 0, 1, 0x41, 0xe0], pts ?? NowUs(), Configuration()));
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            yield return new(Key, NowUs() - 1_000_000, Configuration());
            await foreach (var frame in frames.Reader.ReadAllAsync(token)) yield return frame;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Sound : IAudioSource
    {
        private readonly Channel<AudioPacket> packets = Channel.CreateUnbounded<AudioPacket>();
        public bool Started { get; private set; }
        public System.Text.Json.Nodes.JsonNode Configuration => JsonData.Object(("codec", "raw"),
            ("sampleRate", 48000), ("channels", 2), ("sampleFormat", "s16le"), ("csd", JsonData.Value(Array.Empty<string>())));
        public void BeginTransmission() => Started = true;
        public ValueTask Offer(AudioPacket packet) => packets.Writer.WriteAsync(packet);
        public IAsyncEnumerable<AudioPacket> ReadPacketsAsync(CancellationToken token) => packets.Reader.ReadAllAsync(token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SocketFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource limit = new(TimeSpan.FromSeconds(12)), stop = new();
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> sockets = [];
        private readonly Video source = new();
        public Sound Sound { get; } = new();
        public AudioPacket? LastAudio { get; private set; }
        public long[] SyncRequests => source.SyncRequests.ToArray();
        private readonly Task run;
        private long session;
        private NetworkStream Control => sockets[0].GetStream();
        public SocketFixture()
        {
            listener.Start();
            run = new SenderSession().RunAsync(new(IPAddress.Loopback,
                ((IPEndPoint)listener.LocalEndpoint).Port, "Synthetic receiver", false),
                new(EnableAudio: true), source, _ => { }, stop.Token, Sound);
        }
        public async Task Connect()
        {
            sockets.Add(await listener.AcceptTcpClientAsync(limit.Token));
            var hello = await Wire.ReadAsync(Control, limit.Token);
            Assert.True(StrictJson.Boolean(hello.Json(), "streamRecovery"));
            Assert.Equal(2, StrictJson.Integer(hello.Json(), "streamRecoveryVersion"));
            await Wire.WriteAsync(Control, Message.Control(MessageType.Hello, 0,
                new { role = "receiver", versions = new[] { 3 }, name = "Synthetic receiver",
                    uncappedFps = true, streamRecovery = true, streamRecoveryVersion = 2 }), limit.Token);
            var request = await Wire.ReadAsync(Control, limit.Token);
            session = request.Session;
            await Wire.WriteAsync(Control, Message.Control(MessageType.SessionAccept, session,
                new { tracks = new[] { 1, 2 }, mediaToken = new string('A', 64) }), limit.Token);
            foreach (var channel in new[] { "video", "audio" })
            {
                var socket = await listener.AcceptTcpClientAsync(limit.Token);
                sockets.Add(socket);
                var association = await Wire.ReadAsync(socket.GetStream(), limit.Token);
                Assert.Equal(channel, StrictJson.String(association.Json(), "channel", 16));
                await Wire.WriteAsync(socket.GetStream(), Message.Control(MessageType.Hello, session, new { channel }), limit.Token);
            }
            Assert.Equal(MessageType.StreamConfig, (await ReadMedia(1)).Type);
            var audioConfig = await ReadMedia(2);
            Assert.Equal(MessageType.StreamConfig, audioConfig.Type);
            Assert.False(audioConfig.Json().TryGetProperty("recoveryPtsUs", out _));
        }
        public Task Ready(uint track, uint generation) => Wire.WriteAsync(Control,
            new(MessageType.StreamReady, session, track, generation, -1, 0, """{"ok":true}"""u8.ToArray()), limit.Token).AsTask();
        public Task<Message> ReadMedia(int track) => Wire.ReadAsync(sockets[track].GetStream(), limit.Token).AsTask();
        public int MediaAvailable(int track) => sockets[track].Available;
        public Task SendVideo(long? pts = null, bool key = true) => source.Offer(pts, key).AsTask();
        public Task SendAudio(long? pts = null)
        {
            LastAudio = new(Enumerable.Repeat((byte)0x35, 3840).ToArray(), pts ?? NowUs());
            return Sound.Offer(LastAudio).AsTask();
        }
        public Task Proof(string state, int track, int generation, long pts = -1)
        {
            var json = JsonData.Object(("state", state), ("message", "synthetic proof"), ("track", track), ("generation", generation));
            if (pts >= 0) json[track == 1 ? "keyPtsUs" : "audioPtsUs"] = StreamRecovery.Timestamp(pts);
            return Wire.WriteAsync(Control, Message.Control(MessageType.Status, session, json), limit.Token).AsTask();
        }
        public async Task Barrier()
        {
            await Wire.WriteAsync(Control, Message.Control(MessageType.Ping, session, new { nonce = 17 }), limit.Token);
            while (true)
            {
                var packet = await Wire.ReadAsync(Control, limit.Token);
                if (packet.Type == MessageType.Pong) break;
                Assert.Equal(MessageType.Ping, packet.Type);
            }
        }
        public async Task Stop()
        {
            stop.Cancel();
            while (true)
            {
                var packet = await Wire.ReadAsync(Control, limit.Token);
                if (packet.Type == MessageType.Stop) break;
                Assert.Equal(MessageType.Ping, packet.Type);
            }
            await Wire.WriteAsync(Control, Message.Control(MessageType.SessionEnd, session, new { reason = "user" }), limit.Token);
            await run.WaitAsync(limit.Token);
        }
        public async Task AssertFailure(string expected)
        {
            while (true)
            {
                var packet = await Wire.ReadAsync(Control, limit.Token);
                if (packet.Type == MessageType.Ping) continue;
                Assert.Equal(MessageType.Error, packet.Type);
                Assert.True(StrictJson.Boolean(packet.Json(), "fatal"));
                Assert.Equal("PROTOCOL", StrictJson.String(packet.Json(), "code", 80));
                break;
            }
            var error = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(limit.Token));
            Assert.Contains(expected, error.Message);
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            foreach (var socket in sockets) socket.Dispose();
            listener.Stop();
            try { await run.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (IOException) { }
            stop.Dispose(); limit.Dispose();
        }
    }

    private sealed class StateFixture : IDisposable
    {
        private static readonly Type Type = typeof(SenderSession).Assembly.GetType("RedrainCast.Send.Core.RecoverySender", true)!;
        private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Socket peer;
        private readonly NetworkStream control;
        private readonly object sender;
        public System.Collections.Concurrent.ConcurrentQueue<string> Diagnostics { get; } = new();
        public long AttemptAt { get; } = Environment.TickCount64;
        public StateFixture(bool withAudio = true, Func<Socket, NetworkStream>? createControl = null)
        {
            listener.Start();
            socket.Connect(listener.LocalEndpoint);
            peer = listener.AcceptSocket();
            control = createControl?.Invoke(socket) ?? new(socket, ownsSocket: false);
            sender = Activator.CreateInstance(Type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                [new CaptureSettings(EnableAudio: withAudio), new Video(), withAudio ? new Sound() : null,
                    (Action<string>)Diagnostics.Enqueue, 73L, control], null)!;
            Set("audioConfiguration", JsonData.Bytes(new Sound().Configuration));
            Set("configuration", Configuration());
        }
        public Queue<Message> AudioQueue => Field<Queue<Message>>("audio");
        public Queue<Message> VideoQueue => Field<Queue<Message>>("video");
        public object Gate => Field<object>("gate");
        public long[] SyncRequestTimes => Field<Video>("source").SyncRequests.ToArray();
        public SemaphoreSlim AudioWake => Field<SemaphoreSlim>("audioWake");
        public SemaphoreSlim VideoWake => Field<SemaphoreSlim>("videoWake");
        public Socket TransportSocket => socket;
        public Exception? TerminalError => (Exception?)Field<object>("terminal").GetType().GetProperty("Error")!.GetValue(Field<object>("terminal"));
        public string AudioPhase => TrackField<object>(2, "Phase").ToString()!;
        public RecoveryDeadline AudioDeadline => TrackField<RecoveryDeadline>(2, "Deadline");
        private object Track(int track) => Field<object>(track == 1 ? "videoRecovery" : "audioRecovery");
        public T TrackField<T>(int track, string name) => (T)Track(track).GetType().GetField(name)!.GetValue(Track(track))!;
        public void SetTrack(int track, string name, object value)
        {
            var field = Track(track).GetType().GetField(name)!;
            field.SetValue(Track(track), field.FieldType.IsEnum ? Enum.Parse(field.FieldType, (string)value) : value);
        }
        private T Field<T>(string name) => (T)Type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
        public void Set(string name, object value) => Type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sender, value);
        public void Call(string name, params object[] arguments)
        {
            try { Type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sender, arguments); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }
        public Task CallAsync(string name, params object[] arguments) =>
            (Task)Type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sender, arguments)!;
        public bool Full(int track, Message incoming, int maximumBytes) =>
            (bool)Type.GetMethod("Full", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sender,
                [track == 1 ? VideoQueue : AudioQueue, incoming, maximumBytes])!;
        public void PrepareAudio()
        {
            SetTrack(2, "Generation", 2u); SetTrack(2, "Phase", "WaitProof"); SetTrack(2, "Cutoff", 1_000_000L);
            AudioDeadline.Begin(AttemptAt);
        }
        public Message Packet(long pts) => new(MessageType.AudioPacket, 73, 2, TrackField<uint>(2, "Generation"),
            pts, 0, new byte[3840]);
        public void Proof(string state, int track, int generation, long pts = -1)
        {
            var json = JsonData.Object(("state", state), ("message", "proof"), ("track", track), ("generation", generation));
            if (pts >= 0) json[track == 1 ? "keyPtsUs" : "audioPtsUs"] = StreamRecovery.Timestamp(pts);
            Call("HandleProof", state, StrictJson.Parse(JsonData.Bytes(json)));
        }
        public void Dispose()
        {
            Field<CancellationTokenSource>("media").Dispose();
            Field<CancellationTokenSource>("transport").Dispose();
            Field<CancellationTokenSource>("laneWrites").Dispose();
            Field<SemaphoreSlim>("videoWake").Dispose();
            Field<SemaphoreSlim>("audioWake").Dispose();
            Field<SemaphoreSlim>("controlGate").Dispose();
            control.Dispose(); peer.Dispose(); socket.Dispose(); listener.Stop();
        }
    }
}
