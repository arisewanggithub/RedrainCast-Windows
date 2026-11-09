using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class IndependentRecoveryTests
{
    private sealed class NativePlayback : IStreamRecoveryPlayback, IVideoPlaybackProgress
    {
        public long? SourcePositionUs { get; set; }
        public long? DecodedRecoveryKeyPtsUs { get; set; }
        public long? FirstRecoverySourceAudioPtsUs { get; set; }
        public long AudioCutoff { get; private set; } = -1;
        public ConcurrentQueue<long> AudioCutoffs { get; } = new();
        public bool HasPresentedVideo => Presented.Any(packet => packet.Track == 1);
        public ConcurrentQueue<Packet> Presented { get; } = new();
        public ConcurrentQueue<(uint Track, uint Generation, bool Midstream)> Configured { get; } = new();
        public Func<Packet, CancellationToken, Task>? OnConfigure { get; set; }
        public Func<Packet, CancellationToken, Task>? OnPresent { get; set; }
        public bool Disposed { get; private set; }
        public bool SessionStopped { get; private set; }
        public bool PublishDecodedProof { get; set; } = true;
        public void BeginSessionStop() => SessionStopped = true;
        private long key = -1;
        private string audioCodec = "raw";

        public async ValueTask ConfigureRecoveryTrackAsync(Packet configuration, bool midstream, CancellationToken token)
        {
            if (OnConfigure is { } callback) await callback(configuration, token);
            token.ThrowIfCancellationRequested();
            Configured.Enqueue((configuration.Track, configuration.Generation, midstream));
            if (configuration.Track == 1) DecodedRecoveryKeyPtsUs = null;
            else
            {
                FirstRecoverySourceAudioPtsUs = null;
                AudioCutoff = -1;
                audioCodec = JsonObject.Text(configuration.Object(), "codec");
            }
        }
        public ValueTask ConfigureRecoveryAsync(Packet video, Packet? audio, bool midstream, CancellationToken token) =>
            throw new InvalidOperationException("Shared native reset must never be used.");
        public ValueTask ConfigureAsync(Packet configuration, CancellationToken token) =>
            throw new InvalidOperationException("v2 uses independent track configuration.");
        public void SetRecoveryKey(long pts) => key = pts;
        public void SetAudioRecoveryCutoff(long pts)
        {
            AudioCutoff = pts;
            AudioCutoffs.Enqueue(pts);
        }
        public async ValueTask PresentAsync(Packet packet, CancellationToken token)
        {
            if (OnPresent is { } callback) await callback(packet, token);
            token.ThrowIfCancellationRequested();
            Presented.Enqueue(packet);
            if (packet.Track == 1 && PublishDecodedProof) DecodedRecoveryKeyPtsUs = key;
            else if (packet.Track == 1) return;
            else
            {
                var frames = RecoveryAudio.Frames(audioCodec, packet.Payload);
                var skip = AudioCutoff < 0 ? 0 : RecoveryAudio.Skip(packet.PtsUs, AudioCutoff, frames);
                if (skip < frames)
                    FirstRecoverySourceAudioPtsUs ??= packet.PtsUs + skip * 1_000_000L / 48000;
            }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Factory : IPlaybackFactory
    {
        public string Description => "Synthetic native track outputs";
        public bool SupportsStreamRecovery => true;
        public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken token) =>
            throw new InvalidOperationException("Recovery must not open the whole session again.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        public long Time;
        public NativePlayback Native { get; } = new();
        public ReceiveQueue Queue { get; } = new(1024 * 1024, 256 * 1024, true);
        public ConcurrentQueue<Packet> Proofs { get; } = new();
        public RecoveryPlayback Playback { get; }
        public Func<Packet, CancellationToken, Task>? OnSend;
        public Harness(string? audio = "raw", bool accepted = true)
        {
            Playback = new(new Factory(), new("h264", audio, 60, "auto"), Native, Queue, 1,
                async (packet, token) =>
                {
                    if (OnSend is { } callback) await callback(packet, token);
                    token.ThrowIfCancellationRequested();
                    Proofs.Enqueue(packet);
                }, _ => { }, default, () => Time);
            if (accepted) Playback.BeginSetup();
        }
        public async Task Configure(uint track, uint generation = 1, long cutoff = 0, string codec = "raw")
        {
            var packet = Configuration(track, generation, cutoff, codec);
            Assert.True(Playback.Admit(packet));
            await Playback.ConfigureAsync(packet, default);
            Playback.MarkReady(packet);
            Playback.ReadyWritten(packet);
        }
        public async Task Present(Packet packet)
        {
            Assert.True(Playback.Admit(packet));
            Queue.Add(packet);
            Assert.Same(packet, await Queue.TakeAsync(packet.Track, default));
            try { await Playback.PresentAsync(packet, default); }
            finally { Queue.Complete(packet); }
        }
        public async Task Start()
        {
            await Configure(1);
            await Configure(2);
            await Present(Video(0));
            await Present(Audio(0));
            Assert.False(Playback.State.Recovering);
            Proofs.Clear();
        }
        public async ValueTask DisposeAsync() { await Playback.DisposeAsync(); Queue.Dispose(); }
    }

    private static Packet Configuration(uint track, uint generation = 1, long cutoff = 0, string codec = "raw")
    {
        if (track == 2)
        {
            var original = QueueAndTrackTests.Configuration(2, generation, codec);
            if (generation == 1) return original;
            var json = JsonSerializer.SerializeToNode(original.Object())!;
            json["recoveryPtsUs"] = StreamRecovery.Timestamp(cutoff);
            return original with { Payload = JsonSerializer.SerializeToUtf8Bytes(json) };
        }
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-h264.json")));
        var segment = document.RootElement.GetProperty("generations")[0];
        var csd = segment.GetProperty("frames")[0].GetProperty("configuration").GetString()!;
        return Packet.Control(PacketType.StreamConfig, 1, new
        {
            codec = "h264", width = segment.GetProperty("width").GetInt32(),
            height = segment.GetProperty("height").GetInt32(), csd = new[] { csd }
        }) with { Track = 1, Generation = generation };
    }
    private static Packet Video(long pts, uint generation = 1, bool key = true, ushort rotation = 0) =>
        new(PacketType.VideoFrame, 1, 1, generation, pts, (ushort)((key ? 1 : 0) | rotation << 1),
            key ? [0, 0, 0, 1, 0x65, 0xB0] : [0, 0, 0, 1, 0x41, 0xC0]);
    private static Packet Audio(long pts, uint generation = 1, int frames = 960) =>
        new(PacketType.AudioPacket, 1, 2, generation, pts, 0, new byte[frames * 4]);

    [Fact]
    public async Task RealReceiverRetiresQueuedReadyWithoutBreakingOrderedControlWriter()
    {
        var factory = new LoopbackFactory();
        await using var server = new ReceiverServer("Queued recovery ready test", factory, _ => { },
            bindAddress: IPAddress.Loopback);
        server.Start(false);
        using var control = new TcpClient();
        using var video = new TcpClient();
        using var audio = new TcpClient();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await control.ConnectAsync(IPAddress.Loopback, server.Port, stop.Token);
        await Wire.WriteAsync(control.GetStream(), Packet.Control(PacketType.Hello, 0,
            new { role = "sender", name = "queued-ready", versions = new[] { Wire.Version }, streamRecovery = true,
                streamRecoveryVersion = StreamRecovery.Version }), stop.Token);
        Assert.Equal(PacketType.Hello, (await Wire.ReadAsync(control.GetStream(), stop.Token)).Type);
        await Wire.WriteAsync(control.GetStream(), Packet.Control(PacketType.SessionRequest, 1, RequestTests.Settings(true)), stop.Token);
        var accepted = await Wire.ReadAsync(control.GetStream(), stop.Token);
        Assert.Equal(PacketType.SessionAccept, accepted.Type);
        var token = JsonObject.Text(accepted.Object(), "mediaToken", 64);
        foreach (var (socket, channel) in new[] { (video, "video"), (audio, "audio") })
        {
            await socket.ConnectAsync(IPAddress.Loopback, server.Port, stop.Token);
            await Wire.WriteAsync(socket.GetStream(), Packet.Control(PacketType.Hello, 1,
                new { role = "media", channel, token }), stop.Token);
            Assert.Equal(PacketType.Hello, (await Wire.ReadAsync(socket.GetStream(), stop.Token)).Type);
        }
        await Wire.WriteAsync(video.GetStream(), Configuration(1), stop.Token);
        await Wire.WriteAsync(audio.GetStream(), Configuration(2), stop.Token);
        for (var index = 0; index < 2; index++)
            Assert.Equal(PacketType.StreamReady, (await Wire.ReadAsync(control.GetStream(), stop.Token)).Type);
        await Wire.WriteAsync(video.GetStream(), Video(0), stop.Token);
        await Wire.WriteAsync(audio.GetStream(), Audio(0), stop.Token);
        while (server.RecoveryState?.Recovering != false) await Task.Delay(5, stop.Token);
        var connection = typeof(ReceiverServer).GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var core = (RecoveryPlayback)connection.GetType().GetField("playbackForMedia", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        var writer = (SemaphoreSlim)connection.GetType().GetField("writer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        await writer.WaitAsync(stop.Token);
        try
        {
            await Wire.WriteAsync(video.GetStream(), Configuration(1, 2), stop.Token);
            while (!core.Diagnostics.Contains("track=1 generation=2 active=2 ready=True", StringComparison.Ordinal))
                await Task.Delay(5, stop.Token);
            await Wire.WriteAsync(video.GetStream(), Configuration(1, 3), stop.Token);
            while (!core.Diagnostics.Contains("track=1 generation=3 active=3 ready=True", StringComparison.Ordinal))
                await Task.Delay(5, stop.Token);
        }
        finally { writer.Release(); }
        Packet response;
        do
        {
            response = await Wire.ReadAsync(control.GetStream(), stop.Token);
            Assert.NotEqual(PacketType.Error, response.Type);
        } while (response.Type != PacketType.StreamReady);
        Assert.Equal((1u, 3u), (response.Track, response.Generation));
        await Wire.WriteAsync(video.GetStream(), Video(100_000, 3), stop.Token);
        while (server.RecoveryState?.Recovering != false) await Task.Delay(5, stop.Token);
        await Wire.WriteAsync(control.GetStream(), Packet.Control(PacketType.Stop, 1, new { reason = "queued-ready test finished" }), stop.Token);
        do
        {
            response = await Wire.ReadAsync(control.GetStream(), stop.Token);
            Assert.NotEqual(PacketType.Error, response.Type);
            Assert.False(response.Type == PacketType.StreamReady && response.Generation == 2);
        } while (response.Type != PacketType.SessionEnd);
    }

    [Theory]
    [InlineData(1u, 2u)]
    [InlineData(2u, 1u)]
    public async Task SlowNativeReconstructionDoesNotStopHealthySourceTrack(uint affected, uint healthy)
    {
        await using var harness = new Harness();
        await harness.Start();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Native.OnConfigure = async (packet, token) =>
        {
            if (packet.Track == affected && packet.Generation == 2)
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        harness.Time = 2000;
        var configuration = Configuration(affected, 2, 100_000);
        Assert.True(harness.Playback.Admit(configuration));
        var rebuild = harness.Playback.ConfigureAsync(configuration, default).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var before = harness.Native.Presented.Count;
        for (var index = 1; index <= 5; index++)
            await harness.Present(healthy == 1 ? Video(index * 20_000, key: false) : Audio(index * 20_000));
        Assert.Equal(before + 5, harness.Native.Presented.Count);
        Assert.Equal(1u, healthy == 1 ? harness.Playback.State.Generation : harness.Playback.State.AudioGeneration);
        Assert.DoesNotContain(harness.Native.Configured, item => item.Track == healthy && item.Generation != 1);
        release.SetResult();
        await rebuild;
        harness.Playback.MarkReady(configuration);
        Assert.Contains(harness.Native.Configured, item => item == (affected, 2u, true));
        Assert.False(harness.Native.Disposed);
    }

    [Fact]
    public async Task ProofShapesAreTrackLocalAndVideoDoesNotWaitForAudio()
    {
        await using var harness = new Harness();
        await harness.Configure(1);
        await harness.Configure(2);
        await harness.Present(Video(0));
        var states = harness.Proofs.Select(packet => JsonObject.Text(packet.Object(), "state")).ToArray();
        Assert.Equal(new[] { StreamRecovery.Key, StreamRecovery.Complete }, states);
        Assert.False(harness.Playback.State.AudioRecovering);
        Assert.True(harness.Playback.State.Recovering);
        Assert.False(harness.Playback.State.VideoRecovering);
        foreach (var proof in harness.Proofs)
        {
            Assert.Equal((0u, 0u, -1L, 0u), (proof.Track, proof.Generation, proof.PtsUs, proof.Flags));
            Assert.Equal(1, JsonObject.Number(proof.Object(), "track"));
            Assert.Equal("0", proof.Object().GetProperty("keyPtsUs").GetString());
            Assert.False(proof.Object().TryGetProperty("audioPtsUs", out _));
        }
        harness.Proofs.Clear();
        await harness.Present(Audio(0));
        Assert.Empty(harness.Proofs);
        Assert.False(harness.Playback.State.Recovering);
        harness.Time = 2000;
        await harness.Configure(2, 2, 100_000);
        await harness.Present(Audio(100_000, 2));
        var audio = Assert.Single(harness.Proofs).Object();
        Assert.Equal(StreamRecovery.Complete, JsonObject.Text(audio, "state"));
        Assert.Equal(2, JsonObject.Number(audio, "track"));
        Assert.True(audio.TryGetProperty("audioPtsUs", out _));
        Assert.False(audio.TryGetProperty("keyPtsUs", out _));
    }

    [Fact]
    public async Task RepeatedAttemptAndOtherCompletionCannotExtendOriginalAudioDeadline()
    {
        await using var harness = new Harness();
        await harness.Start();
        harness.Time = 100;
        harness.Playback.Pressure(2);
        harness.Time = 1000;
        await harness.Configure(2, 2, 100_000);
        harness.Time = 3000;
        harness.Playback.Pressure(2);
        await harness.Configure(2, 3, 200_000);
        await harness.Present(Video(200_000, key: false));
        harness.Time = 10_099;
        harness.Playback.CheckHealth();
        harness.Time = 10_100;
        Assert.Throws<TimeoutException>(harness.Playback.CheckHealth);
        Assert.False(harness.Playback.State.VideoRecovering);
    }

    [Fact]
    public async Task InitialReadinessBarrierIsNotReintroducedOnAudioRecovery()
    {
        await using var harness = new Harness();
        await harness.Configure(1);
        Assert.Throws<ProtocolException>(() => harness.Playback.Admit(Video(0)));
        await harness.Configure(2);
        await harness.Present(Video(0));
        await harness.Present(Audio(0));
        harness.Time = 2000;
        harness.Playback.Pressure(2);
        await harness.Present(Video(40_000, key: false));
        Assert.Equal(40_000, harness.Native.Presented.Last().PtsUs);
    }

    [Fact]
    public async Task StaleTrackPacketsAreDiscardedAndFutureGenerationFails()
    {
        await using var harness = new Harness();
        await harness.Start();
        harness.Time = 2000;
        await harness.Configure(2, 2, 20_000);
        Assert.False(harness.Playback.Admit(Audio(20_000)));
        Assert.Throws<ProtocolException>(() => harness.Playback.Admit(Audio(20_000, 3)));
        await harness.Present(Video(40_000, key: false));
        Assert.True(harness.Playback.State.AudioRecovering);
    }

    [Fact]
    public async Task RetiredNativeFailureCannotPoisonNewGenerationOrHealthyAudio()
    {
        await using var harness = new Harness();
        await harness.Start();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Native.OnConfigure = async (packet, _) =>
        {
            if (packet.Track == 1 && packet.Generation == 2)
            {
                started.SetResult();
                await release.Task;
                throw new IOException("Retired synthetic decoder failed.");
            }
        };
        harness.Time = 2000;
        var old = Configuration(1, 2);
        Assert.True(harness.Playback.Admit(old));
        var rebuilding = harness.Playback.ConfigureAsync(old, default).AsTask();
        await started.Task;
        var next = Configuration(1, 3);
        Assert.True(harness.Playback.Admit(next));
        await harness.Present(Audio(20_000));
        release.SetResult();
        await rebuilding;
        Assert.False(harness.Playback.IsCurrentConfiguration(old));
        harness.Time = 4000;
        await harness.Playback.ConfigureAsync(next, default);
        harness.Playback.MarkReady(next);
        await harness.Present(Video(40_000, 3));
        harness.Playback.CheckHealth();
        Assert.Equal(3u, harness.Playback.State.ActiveGeneration);
        Assert.Equal(1u, harness.Playback.State.ActiveAudioGeneration);
    }

    [Fact]
    public async Task AudioCompletionRequiresActualSourceRatherThanSilenceAndIsIdempotent()
    {
        await using var harness = new Harness();
        await harness.Start();
        harness.Time = 2000;
        await harness.Configure(2, 2, 100_001);
        await harness.Playback.PollProofAsync(default);
        Assert.Empty(harness.Proofs);
        await harness.Present(Audio(90_000, 2));
        var proof = Assert.Single(harness.Proofs).Object();
        Assert.Equal("100020", proof.GetProperty("audioPtsUs").GetString());
        await harness.Playback.PollProofAsync(default);
        Assert.Single(harness.Proofs);
        Assert.False(harness.Playback.State.AudioRecovering);
    }

    [Fact]
    public async Task StopCancelsSlowRebuildAndNeverPublishesReadyOrProof()
    {
        await using var harness = new Harness();
        await harness.Start();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Native.OnConfigure = async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        harness.Time = 2000;
        var config = Configuration(2, 2, 100_000);
        Assert.True(harness.Playback.Admit(config));
        var rebuild = harness.Playback.ConfigureAsync(config, default).AsTask();
        await started.Task;
        await harness.Playback.DisposeAsync();
        Assert.True(harness.Native.SessionStopped);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rebuild);
        Assert.False(harness.Playback.IsCurrentConfiguration(config));
        Assert.Empty(harness.Proofs);
    }

    [Fact]
    public async Task ProofWriteMustCompleteBeforeDeadlineCanClear()
    {
        await using var harness = new Harness();
        await harness.Configure(1);
        await harness.Configure(2);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnSend = async (packet, token) =>
        {
            if (JsonObject.Text(packet.Object(), "state") == StreamRecovery.Complete)
            {
                writing.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var presenting = harness.Present(Video(0));
        await writing.Task;
        Assert.True(harness.Playback.State.VideoRecovering);
        harness.Time = 10_000;
        Assert.Throws<TimeoutException>(harness.Playback.CheckHealth);
        release.SetResult();
        await Assert.ThrowsAsync<TimeoutException>(() => presenting);
        Assert.True(harness.Playback.State.VideoRecovering);
    }

    [Fact]
    public async Task RotationAndOriginalPtsRemainAssociatedWithDecodedKey()
    {
        await using var harness = new Harness(audio: null);
        await harness.Configure(1);
        var frame = Video(123_456, rotation: 3);
        await harness.Present(frame);
        Assert.Same(frame, Assert.Single(harness.Native.Presented));
        Assert.All(harness.Proofs, proof => Assert.Equal("123456", proof.Object().GetProperty("keyPtsUs").GetString()));
    }

    [Fact]
    public async Task VideoRecoveryDoesNotRejectAKeyBecauseTheAudioClockIsAhead()
    {
        await using var harness = new Harness();
        await harness.Start();
        harness.Native.SourcePositionUs = 1_000_000;
        await harness.Configure(1, 2);
        Assert.False(harness.Playback.Admit(Video(700_000, 2, key: false)));
        await harness.Present(Video(700_001, 2));
        Assert.Contains(harness.Proofs, proof =>
            proof.Object().GetProperty("state").GetString() == StreamRecovery.Complete &&
            proof.Object().GetProperty("keyPtsUs").GetString() == "700001");
    }

    [Fact]
    public async Task InitialAudioAtMonotonicSourceTimeDoesNotTriggerRecovery()
    {
        await using var harness = new Harness();
        await harness.Configure(1);
        await harness.Configure(2);
        await harness.Present(Video(10_000_000));
        await harness.Present(Audio(10_000_000));
        Assert.False(harness.Playback.State.AudioRecovering);
        Assert.Contains(harness.Native.Presented, packet => packet.Track == 2);
        Assert.DoesNotContain(harness.Proofs, proof =>
            proof.Object().GetProperty("state").GetString() == StreamRecovery.Request);
    }

    [Fact]
    public async Task AudioRecoveryCanStartLaterThanItsCutoff()
    {
        await using var harness = new Harness();
        await harness.Start();
        await harness.Configure(2, 2, 100_000);
        await harness.Present(Audio(500_001, 2));
        Assert.Contains(harness.Proofs, proof =>
            proof.Object().GetProperty("state").GetString() == StreamRecovery.Complete &&
            proof.Object().GetProperty("audioPtsUs").GetString() == "500001");
    }

    [Fact]
    public async Task OpusPrerollPreservesAdmissionOrderDespiteReversedPts()
    {
        await using var harness = new Harness("opus");
        await harness.Configure(1);
        await harness.Configure(2, codec: "opus");
        harness.Time = 2000;
        await harness.Present(Video(0));
        await harness.Configure(2, 2, 100_000, "opus");
        harness.Native.OnPresent = (packet, _) =>
        {
            if (packet.Track == 2) harness.Native.FirstRecoverySourceAudioPtsUs = 100_000;
            return Task.CompletedTask;
        };
        var packets = new[]
        {
            new Packet(PacketType.AudioPacket, 1, 2, 2, 20_000, 0, [0x18, 1]),
            new Packet(PacketType.AudioPacket, 1, 2, 2, 10_000, 0, [0x18, 2]),
            new Packet(PacketType.AudioPacket, 1, 2, 2, 70_000, 0, [0x18, 3])
        };
        foreach (var packet in packets) await harness.Present(packet);
        Assert.Equal(packets, harness.Native.Presented.Where(packet => packet.Track == 2));
    }

    [Fact]
    public async Task UnprovenOpusPrerollSpanPressureRetriesOnlyAudioUnderOriginalDeadline()
    {
        await using var harness = new Harness("opus");
        await harness.Configure(1);
        await harness.Configure(2, codec: "opus");
        await harness.Present(Video(0));
        harness.Time = 2000;
        await harness.Configure(2, 2, 500_000, "opus");
        await harness.Present(new(PacketType.AudioPacket, 1, 2, 2, 421_000, 0, [0x18, 1]));
        var next = new Packet(PacketType.AudioPacket, 1, 2, 2, 1_000_000, 0, [0x18, 2]);
        Assert.True(harness.Playback.Admit(next));
        Assert.Throws<ProtocolException>(() => harness.Queue.Add(next));
        harness.Playback.Pressure(2, "held Opus chain exceeded the source span budget");
        using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!harness.Proofs.Any(packet => JsonObject.Text(packet.Object(), "state") == StreamRecovery.Request))
            await Task.Delay(1, requestTimeout.Token);
        var request = Assert.Single(harness.Proofs, packet =>
            JsonObject.Text(packet.Object(), "state") == StreamRecovery.Request).Object();
        Assert.Equal(2, JsonObject.Number(request, "track"));
        Assert.DoesNotContain(harness.Native.Presented, packet => packet.Track == 2);
        await harness.Present(Video(1_000_000, key: false));
        Assert.False(harness.Playback.State.VideoRecovering);
        harness.Time = 10_000;
        Assert.Throws<TimeoutException>(harness.Playback.CheckHealth);
    }

    [Fact]
    public async Task SlowSetupDoesNotConsumeInitialUsableSourceBudget()
    {
        await using var harness = new Harness(accepted: false);
        harness.Time = 50_000;
        harness.Playback.CheckHealth();
        harness.Playback.BeginSetup();
        harness.Time = 58_000;
        await harness.Configure(1);
        await harness.Configure(2);
        Assert.False(harness.Playback.State.AudioRecovering);
        harness.Time = 67_999;
        await harness.Present(Video(0));
        await harness.Present(Audio(0));
        harness.Time = 68_000;
        harness.Playback.CheckHealth();
        Assert.False(harness.Playback.State.Recovering);
        Assert.DoesNotContain(harness.Proofs, packet => JsonObject.Number(packet.Object(), "track") == 2);
    }

    [Fact]
    public async Task SetupStillHasItsOwnAbsoluteTenSecondLimit()
    {
        await using var harness = new Harness(accepted: false);
        harness.Time = 50_000;
        harness.Playback.BeginSetup();
        harness.Time = 59_999;
        harness.Playback.CheckHealth();
        harness.Time = 60_000;
        Assert.Throws<TimeoutException>(harness.Playback.CheckHealth);
    }

    [Fact]
    public async Task InitialUsableMediaDeadlineStartsAfterAllReadyFramesComplete()
    {
        await using var harness = new Harness();
        var video = Configuration(1);
        Assert.True(harness.Playback.Admit(video));
        await harness.Playback.ConfigureAsync(video, default);
        harness.Playback.MarkReady(video);
        await harness.Configure(2);
        harness.Time = 9000;
        harness.Playback.CheckHealth();
        harness.Playback.ReadyWritten(video);
        harness.Time = 18_999;
        await harness.Present(Video(0));
        await harness.Present(Audio(0));
        harness.Time = 19_000;
        harness.Playback.CheckHealth();
    }

    [Fact]
    public async Task InitialAudioWithoutActualSourceStillTimesOutWithoutSendingRecoveryProof()
    {
        await using var harness = new Harness();
        await harness.Configure(1);
        await harness.Configure(2);
        await harness.Present(Video(0));
        harness.Time = 9999;
        await harness.Playback.PollProofAsync(default);
        harness.Playback.CheckHealth();
        Assert.False(harness.Playback.State.AudioRecovering);
        Assert.True(harness.Playback.State.Recovering);
        Assert.DoesNotContain(harness.Proofs, packet => JsonObject.Number(packet.Object(), "track") == 2);
        harness.Time = 10_000;
        Assert.Throws<TimeoutException>(harness.Playback.CheckHealth);
    }

    [Fact]
    public async Task BlockingEpochCancellationCallbackDoesNotHoldHealthyLaneOrLifecycleGate()
    {
        await using var harness = new Harness();
        await harness.Start();
        var configured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Native.OnConfigure = async (packet, token) =>
        {
            if (packet.Track != 1 || packet.Generation != 2) return;
            using var registration = token.Register(() =>
            {
                cancelling.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            });
            configured.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        harness.Time = 2000;
        var old = Configuration(1, 2);
        Assert.True(harness.Playback.Admit(old));
        var rebuilding = harness.Playback.ConfigureAsync(old, default).AsTask();
        await configured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var next = Configuration(1, 3);
        var admission = Task.Run(() => harness.Playback.Admit(next));
        Task? latest = null;
        try
        {
            await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var before = harness.Native.Presented.Count(packet => packet.Track == 2);
            for (var index = 1; index <= 5; index++)
                await harness.Present(Audio(index * 20_000)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(before + 5, harness.Native.Presented.Count(packet => packet.Track == 2));
            Assert.True(await admission.WaitAsync(TimeSpan.FromSeconds(2)));
            latest = harness.Playback.ConfigureAsync(next, default).AsTask();
            Assert.False(latest.IsCompleted);
            Assert.DoesNotContain(harness.Native.Configured, item => item.Track == 1 && item.Generation == 3);
            Assert.Equal(1u, harness.Playback.State.ActiveAudioGeneration);
        }
        finally
        {
            harness.Time = 4000;
            release.TrySetResult();
        }
        await rebuilding;
        if (latest is not null) await latest;
        harness.Playback.MarkReady(next);
        harness.Playback.ReadyWritten(next);
        await harness.Present(Video(120_000, 3));
        harness.Playback.CheckHealth();
        Assert.False(harness.Playback.State.Recovering);
    }

    [Fact]
    public async Task StopJoinsRequestWriteEvenAfterNewGenerationProofSupersedesIt()
    {
        await using var harness = new Harness();
        await harness.Start();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnSend = async (packet, _) =>
        {
            if (JsonObject.Text(packet.Object(), "state") != StreamRecovery.Request) return;
            writing.TrySetResult();
            await release.Task;
        };
        harness.Time = 2000;
        harness.Playback.Pressure(2);
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task? stopping = null;
        try
        {
            await harness.Configure(2, 2, 100_000);
            await harness.Present(Audio(100_000, 2));
            stopping = harness.Playback.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            Assert.False(harness.Native.Disposed);
        }
        finally { release.TrySetResult(); }
        if (stopping is not null) await stopping;
        Assert.True(harness.Native.Disposed);
    }

    [Fact]
    public async Task FirstAudioRecoveryCannotProveAfterOriginalInitialUsableMediaDeadline()
    {
        await using var harness = new Harness();
        await harness.Configure(1);
        await harness.Configure(2);
        await harness.Present(Video(0));
        harness.Time = 100;
        harness.Playback.Pressure(2);
        harness.Time = 2000;
        await harness.Configure(2, 2, 100_000);
        harness.Native.OnPresent = (_, _) =>
        {
            harness.Time = 10_000;
            return Task.CompletedTask;
        };
        harness.Time = 9999;
        await Assert.ThrowsAsync<TimeoutException>(() => harness.Present(Audio(100_000, 2)));
        Assert.DoesNotContain(harness.Proofs, packet => JsonObject.Number(packet.Object(), "track") == 2 &&
            JsonObject.Text(packet.Object(), "state") == StreamRecovery.Complete);
    }

    [Fact]
    public async Task RetiredProofFinallyCannotClearNewGenerationProofOwner()
    {
        await using var harness = new Harness();
        await harness.Start();
        var oldWriting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newWriting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newAttempts = 0;
        harness.OnSend = async (packet, _) =>
        {
            var json = packet.Object();
            if (JsonObject.Text(json, "state") != StreamRecovery.Key) return;
            if (JsonObject.Number(json, "generation") == 2)
            {
                oldWriting.TrySetResult();
                await releaseOld.Task;
            }
            else if (JsonObject.Number(json, "generation") == 3)
            {
                Interlocked.Increment(ref newAttempts);
                newWriting.TrySetResult();
                await releaseNew.Task;
            }
        };
        harness.Time = 2000;
        await harness.Configure(1, 2);
        harness.Native.PublishDecodedProof = false;
        await harness.Present(Video(100_000, 2));
        harness.Native.DecodedRecoveryKeyPtsUs = 100_000;
        var oldPolling = harness.Playback.PollProofAsync(default);
        Task? presenting = null;
        try
        {
            await oldWriting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            harness.Time = 4000;
            await harness.Configure(1, 3);
            harness.Native.PublishDecodedProof = true;
            presenting = harness.Present(Video(200_000, 3));
            await newWriting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            releaseOld.TrySetResult();
            await oldPolling.WaitAsync(TimeSpan.FromSeconds(2));
            await harness.Playback.PollProofAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, newAttempts);
        }
        finally
        {
            releaseOld.TrySetResult();
            releaseNew.TrySetResult();
        }
        if (presenting is not null) await presenting;
        Assert.DoesNotContain(harness.Proofs, packet => JsonObject.Number(packet.Object(), "generation") == 2);
        Assert.Equal(2, harness.Proofs.Count(packet => JsonObject.Number(packet.Object(), "generation") == 3));
    }

    [Theory]
    [InlineData(PacketType.StreamReady)]
    [InlineData(PacketType.Status)]
    public async Task RetirementRejectsControlFrameAtOrderedWriteAdmission(PacketType type)
    {
        await using var harness = new Harness();
        await harness.Start();
        harness.Time = 2000;
        await harness.Configure(1, 2);
        var ready = type == PacketType.StreamReady
            ? new Packet(type, 1, 1, 2, -1, 0, JsonData.Bytes(JsonData.Object(("ok", true))))
            : Packet.Control(type, 1, new { state = StreamRecovery.Key, track = 1, generation = 2, keyPtsUs = "0" });
        using var writer = new SemaphoreSlim(0);
        var started = false;
        async Task QueuedWrite()
        {
            await writer.WaitAsync();
            await harness.Playback.StartControlWrite(ready, () =>
            {
                started = true;
                return Task.CompletedTask;
            }, default);
        }
        var queued = QueuedWrite();
        harness.Time = 4000;
        await harness.Configure(1, 3);
        writer.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(started);
        var current = new Packet(PacketType.StreamReady, 1, 1, 3, -1, 0, []);
        await harness.Playback.StartControlWrite(current, () => Task.CompletedTask, default);
        harness.Playback.CheckHealth();
    }

    [Fact]
    public async Task RetiringEpochNeverCancelsAnAlreadyStartedControlFrame()
    {
        await using var harness = new Harness();
        await harness.Start();
        harness.Time = 2000;
        await harness.Configure(1, 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new Packet(PacketType.StreamReady, 1, 1, 2, -1, 0, []);
        var writing = harness.Playback.StartControlWrite(ready, () => release.Task,
            harness.Playback.ReadinessToken(Configuration(1, 2)));
        try
        {
            harness.Time = 4000;
            await harness.Configure(1, 3);
            Assert.False(writing.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await writing;
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("opus")]
    [InlineData("aac")]
    public async Task InitialLargeSourceTimelineUsesNormalAudioAndUnblocksVideoReconstruction(string codec)
    {
        const long source = 1_234_567_890_000;
        await using var harness = new Harness(codec);
        harness.Native.SourcePositionUs = source + 2_000_000;
        await harness.Configure(1);
        await harness.Configure(2, codec: codec);
        await harness.Present(Video(source + 2_000_000));
        Packet Sound(long pts) => codec == "raw" ? Audio(pts) :
            new(PacketType.AudioPacket, 1, 2, 1, pts, 0, codec == "opus" ? [0x18, 1] : [1, 2, 3]);
        var initial = Sound(source);
        await harness.Present(initial);
        Assert.Same(initial, harness.Native.Presented.Last());
        Assert.Empty(harness.Native.AudioCutoffs);
        Assert.Equal(source, harness.Native.FirstRecoverySourceAudioPtsUs);
        Assert.False(harness.Playback.State.Recovering);

        var constructing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthySource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Native.OnConfigure = async (packet, token) =>
        {
            if (packet.Track != 1 || packet.Generation != 2) return;
            constructing.TrySetResult();
            await healthySource.Task.WaitAsync(token);
        };
        harness.Native.OnPresent = (packet, _) =>
        {
            if (packet.Track == 2) healthySource.TrySetResult();
            return Task.CompletedTask;
        };
        harness.Time = 2000;
        var recovery = Configuration(1, 2);
        Assert.True(harness.Playback.Admit(recovery));
        var building = harness.Playback.ConfigureAsync(recovery, default).AsTask();
        try
        {
            await constructing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await harness.Present(Sound(source + 60_000)).WaitAsync(TimeSpan.FromSeconds(2));
            await building.WaitAsync(TimeSpan.FromSeconds(2));
            harness.Playback.MarkReady(recovery);
            harness.Playback.ReadyWritten(recovery);
            await harness.Present(Video(source + 3_000_000, 2));
        }
        finally { healthySource.TrySetResult(); }
        Assert.Empty(harness.Native.AudioCutoffs);
        Assert.Equal(1u, harness.Playback.State.ActiveAudioGeneration);
        Assert.False(harness.Playback.State.Recovering);
        Assert.DoesNotContain(harness.Proofs, packet => JsonObject.Number(packet.Object(), "track") == 2);
    }

    [Fact]
    public async Task RawRecoveryTrimsToCurrentClockButAcceptsLateFirstSourceWithoutVideoRelativeReset()
    {
        const long source = 1_234_567_890_000;
        await using var harness = new Harness();
        await harness.Start();
        harness.Native.SourcePositionUs = source + 1_000_000;
        harness.Time = 2000;
        await harness.Configure(2, 2, source + 500_000);
        var obsolete = Audio(source + 900_000, 2);
        await harness.Present(obsolete);
        Assert.DoesNotContain(obsolete, harness.Native.Presented);
        Assert.Equal(source + 1_000_000, harness.Native.AudioCutoff);
        var late = Audio(source + 2_000_000, 2);
        await harness.Present(late);
        Assert.Same(late, harness.Native.Presented.Last());
        Assert.False(harness.Playback.State.AudioRecovering);
        Assert.Equal(1u, harness.Playback.State.ActiveGeneration);
        var proof = Assert.Single(harness.Proofs).Object();
        Assert.Equal(StreamRecovery.Complete, JsonObject.Text(proof, "state"));
        Assert.Equal(StreamRecovery.Timestamp(late.PtsUs), proof.GetProperty("audioPtsUs").GetString());
    }

    [Fact]
    public async Task RepeatedAudioEpochConstructionAndRetirementCannotBlockHealthyVideoWorker()
    {
        await using var harness = new Harness();
        await harness.Start();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Native.OnConfigure = async (packet, token) =>
        {
            if (packet.Track != 2) return;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var builds = new List<Task>();
        for (uint generation = 2; generation <= 6; generation++)
        {
            harness.Time = generation * 1000;
            started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var configuration = Configuration(2, generation, generation * 100_000);
            Assert.True(harness.Playback.Admit(configuration));
            builds.Add(harness.Playback.ConfigureAsync(configuration, default).AsTask());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await harness.Present(Video(generation * 20_000, key: false)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1u, harness.Playback.State.ActiveGeneration);
        }
        harness.Native.OnConfigure = null;
        harness.Time = 7000;
        await harness.Configure(2, 7, 700_000);
        await Task.WhenAll(builds).WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Present(Audio(800_000, 7));
        Assert.Equal(6, harness.Native.Presented.Count(packet => packet.Track == 1));
        Assert.Single(harness.Native.Configured, item => item.Track == 1);
        Assert.False(harness.Playback.State.Recovering);
    }

    private sealed class LoopbackFactory : IPlaybackFactory
    {
        public NativePlayback Native { get; } = new();
        public string Description => "Synthetic independent native playback";
        public bool SupportsStreamRecovery => true;
        public int Opens;
        public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken token)
        {
            Opens++;
            return ValueTask.FromResult<IPlayback>(Native);
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public async Task ReceiverStopAllowsStartedMediaToDrainBeyondTheLegacyAcknowledgementLimit(uint track)
    {
        var factory = new LoopbackFactory();
        var notices = new ConcurrentQueue<ReceiverNotice>();
        await using var server = new ReceiverServer("Independent Stop test", factory, notices.Enqueue,
            bindAddress: IPAddress.Loopback);
        server.Start(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var control = new TcpClient { NoDelay = true };
        using var video = new TcpClient { NoDelay = true };
        using var audio = new TcpClient { NoDelay = true };
        await control.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        await Wire.WriteAsync(control.GetStream(), Packet.Control(PacketType.Hello, 0,
            new { role = "sender", name = "Stop test", versions = new[] { 3 },
                streamRecovery = true, streamRecoveryVersion = StreamRecovery.Version }), timeout.Token);
        Assert.Equal(PacketType.Hello, (await Wire.ReadAsync(control.GetStream(), timeout.Token)).Type);
        await Wire.WriteAsync(control.GetStream(), Packet.Control(PacketType.SessionRequest, 1, RequestTests.Settings(true)), timeout.Token);
        var accepted = await Wire.ReadAsync(control.GetStream(), timeout.Token);
        Assert.Equal(PacketType.SessionAccept, accepted.Type);
        var token = JsonObject.Text(accepted.Object(), "mediaToken", 64);
        foreach (var (socket, channel) in new[] { (video, "video"), (audio, "audio") })
        {
            await socket.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
            await Wire.WriteAsync(socket.GetStream(), Packet.Control(PacketType.Hello, 1,
                new { role = "media", channel, token }), timeout.Token);
            Assert.Equal(PacketType.Hello, (await Wire.ReadAsync(socket.GetStream(), timeout.Token)).Type);
        }
        await Wire.WriteAsync(video.GetStream(), Configuration(1), timeout.Token);
        await Wire.WriteAsync(audio.GetStream(), Configuration(2), timeout.Token);
        for (var index = 0; index < 2; index++)
            Assert.Equal(PacketType.StreamReady, (await Wire.ReadAsync(control.GetStream(), timeout.Token)).Type);
        await Wire.WriteAsync(video.GetStream(), Video(0), timeout.Token);
        await Wire.WriteAsync(audio.GetStream(), Audio(0), timeout.Token);
        while (server.RecoveryState?.Recovering != false) await Task.Delay(5, timeout.Token);

        var packet = track == 1 ? Video(20_000, key: false) : Audio(20_000);
        using var frame = new MemoryStream();
        await Wire.WriteAsync(frame, packet, timeout.Token);
        var bytes = frame.ToArray();
        var prefixLength = bytes.Length - packet.Payload.Length / 2;
        var media = (track == 1 ? video : audio).GetStream();
        await media.WriteAsync(bytes.AsMemory(0, prefixLength), timeout.Token);
        var disconnect = server.DisconnectAsync("drain started payload");
        Packet terminal;
        do { terminal = await Wire.ReadAsync(control.GetStream(), timeout.Token); }
        while (terminal.Type != PacketType.Stop);
        await Task.Delay(2250, timeout.Token);
        Assert.False(disconnect.IsCompleted);
        Assert.True(server.IsBusy);
        await media.WriteAsync(bytes.AsMemory(prefixLength), timeout.Token);
        await Wire.WriteAsync(control.GetStream(), Packet.Control(PacketType.SessionEnd, 1,
            new { reason = "started payload drained" }), timeout.Token);
        await disconnect.WaitAsync(timeout.Token);
        Assert.False(server.IsBusy);
        Assert.DoesNotContain(notices, notice => notice.State is "Disconnect" or "TIMEOUT" or "Sender error");
        Assert.DoesNotContain(factory.Native.Presented, presented => presented.PtsUs == packet.PtsUs);
    }

    [Theory]
    [InlineData(1u, 2u)]
    [InlineData(2u, 1u)]
    public async Task RealReceiverKeepsHealthySourceLaneAndIgnoresOldSetupPhaseDuringRebuild(uint affected, uint healthy)
    {
        var factory = new LoopbackFactory();
        var notices = new ConcurrentQueue<ReceiverNotice>();
        await using var server = new ReceiverServer("Independent recovery test", factory, notices.Enqueue,
            bindAddress: IPAddress.Loopback);
        server.Start(false);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var control = new TcpClient { NoDelay = true };
        using var video = new TcpClient { NoDelay = true };
        using var audio = new TcpClient { NoDelay = true };
        await control.ConnectAsync(IPAddress.Loopback, server.Port, stop.Token);
        await Wire.WriteAsync(control.GetStream(), Packet.Control(PacketType.Hello, 0,
            new { role = "sender", name = "Independent test", versions = new[] { 3 },
                streamRecovery = true, streamRecoveryVersion = StreamRecovery.Version }), stop.Token);
        var hello = await Wire.ReadAsync(control.GetStream(), stop.Token);
        Assert.True(JsonObject.Boolean(hello.Object(), "streamRecovery"));
        await Wire.WriteAsync(control.GetStream(), Packet.Control(PacketType.SessionRequest, 1, RequestTests.Settings(true)), stop.Token);
        var accepted = await Wire.ReadAsync(control.GetStream(), stop.Token);
        Assert.Equal(PacketType.SessionAccept, accepted.Type);
        var token = JsonObject.Text(accepted.Object(), "mediaToken", 64);
        foreach (var (socket, channel) in new[] { (video, "video"), (audio, "audio") })
        {
            await socket.ConnectAsync(IPAddress.Loopback, server.Port, stop.Token);
            await Wire.WriteAsync(socket.GetStream(), Packet.Control(PacketType.Hello, 1,
                new { role = "media", channel, token }), stop.Token);
            Assert.Equal(PacketType.Hello, (await Wire.ReadAsync(socket.GetStream(), stop.Token)).Type);
        }
        await Wire.WriteAsync(video.GetStream(), Configuration(1), stop.Token);
        await Wire.WriteAsync(audio.GetStream(), Configuration(2), stop.Token);
        for (var index = 0; index < 2; index++)
            Assert.Equal(PacketType.StreamReady, (await Wire.ReadAsync(control.GetStream(), stop.Token)).Type);
        await Wire.WriteAsync(video.GetStream(), Video(0), stop.Token);
        await Wire.WriteAsync(audio.GetStream(), Audio(0), stop.Token);
        while (server.RecoveryState?.Recovering != false) await Task.Delay(5, stop.Token);

        var connection = typeof(ReceiverServer).GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        connection.GetType().GetField("startup", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(connection, Environment.TickCount64 - 20_000);
        var reconstructing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Native.OnConfigure = async (packet, cancellation) =>
        {
            if (packet.Track != affected || packet.Generation != 2) return;
            reconstructing.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
        };
        await Wire.WriteAsync((affected == 1 ? video : audio).GetStream(), Configuration(affected, 2, 100_000), stop.Token);
        await reconstructing.Task.WaitAsync(stop.Token);
        var before = factory.Native.Presented.Where(packet => packet.Track == healthy).Sum(packet => packet.Payload.Length);
        for (var index = 1; index <= 5; index++)
            await Wire.WriteAsync((healthy == 1 ? video : audio).GetStream(),
                healthy == 1 ? Video(index * 20_000, key: false) : Audio(index * 20_000), stop.Token);
        var expected = before + 5 * (healthy == 1 ? 6 : 960 * 4);
        while (factory.Native.Presented.Where(packet => packet.Track == healthy).Sum(packet => packet.Payload.Length) < expected)
            await Task.Delay(5, stop.Token);
        await Task.Delay(300, stop.Token);
        Assert.DoesNotContain(notices, notice => notice.State == "TIMEOUT");
        Assert.DoesNotContain(factory.Native.Configured, item => item.Track == healthy && item.Generation != 1);
        Assert.Equal(1, factory.Opens);
        Assert.False(factory.Native.Disposed);
        await Wire.WriteAsync(control.GetStream(), Packet.Control(PacketType.Stop, 1, new { reason = "independent test finished" }), stop.Token);
        Packet response;
        do
        {
            response = await Wire.ReadAsync(control.GetStream(), stop.Token);
            Assert.NotEqual(PacketType.Error, response.Type);
            Assert.False(response.Type == PacketType.StreamReady && response.Track == affected && response.Generation == 2);
        } while (response.Type != PacketType.SessionEnd);
        while (server.IsBusy) await Task.Delay(5, stop.Token);
        Assert.True(factory.Native.Disposed);
    }
}
