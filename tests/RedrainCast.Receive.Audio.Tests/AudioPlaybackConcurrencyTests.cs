using System.Text;
using RedrainCast.Receive.Core;
using RedrainCast.Receive.Media;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RedrainCast.Receive.Audio.Tests;

public sealed class AudioPlaybackConcurrencyTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static Packet Configuration(uint generation = 1) => new(PacketType.StreamConfig, 1, 2, generation, 0, 0,
        Encoding.UTF8.GetBytes("""{"codec":"raw","sampleRate":48000,"channels":2,"sampleFormat":"s16le","csd":[]}"""));
    private static Packet Pcm(long pts = 0, int frames = AudioBuffer.TargetFrames) =>
        new(PacketType.AudioPacket, 1, 2, 1, pts, 0, new byte[frames * 4]);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<int> Written(WasapiOutput output) =>
        await output.Writes.Reader.ReadAsync().AsTask().WaitAsync(Deadline);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedPcmRefillsAndPositionReadsWhileNativeProducerIsPaused(bool resampling)
    {
        await using var playback = new AudioPlayback("raw");
        await playback.ConfigureAsync(Configuration(), default);
        var output = WasapiOutput.Instances[^1];
        await playback.PresentAsync(Pcm(), default);
        Assert.Equal(480, await Written(output));
        await output.Waiting.Task.WaitAsync(Deadline);
        var entered = Signal();
        var release = Signal();
        Action pause = () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        if (resampling) AudioRegulator.Instances[^1].Converting = pause;
        else AudioDecoder.Instances[^1].Decoding = pause;
        var producer = Task.Run(async () => await playback.PresentAsync(Pcm(50_000, 480), default));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            output.Consume();
            Assert.Equal(480, await Written(output));
            var position = await Task.Run(() => playback.Position).WaitAsync(Deadline);
            Assert.Equal(480, position.ContentFrames);
            Assert.Equal(10_000, position.PtsUs);
            Assert.False(producer.IsCompleted);
            Assert.NotNull(playback.Statistics);
        }
        finally { release.TrySetResult(); await producer.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task CancellationDuringDecodeDoesNotPublishAndDisposesExactlyOnce()
    {
        var playback = new AudioPlayback("raw");
        await playback.ConfigureAsync(Configuration(), default);
        var output = WasapiOutput.Instances[^1];
        var decoder = AudioDecoder.Instances[^1];
        var regulator = AudioRegulator.Instances[^1];
        var entered = Signal();
        var release = Signal();
        decoder.Decoding = () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        using var stop = new CancellationTokenSource();
        var producer = Task.Run(async () => await playback.PresentAsync(Pcm(), stop.Token));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            stop.Cancel();
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer.WaitAsync(Deadline));
        Assert.Equal(0, regulator.Buffer.Frames);
        await playback.DisposeAsync();
        await playback.DisposeAsync();
        Assert.Equal(1, decoder.Disposals);
        Assert.Equal(1, regulator.Disposals);
        Assert.Equal(1, output.Disposals);
        Assert.Equal(0, output.WritesAfterDispose);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await playback.ConfigureAsync(Configuration(), default));
    }

    [Fact]
    public async Task RetirementCancelsPulledBlockBeforeItCanWrite()
    {
        var playback = new AudioPlayback("raw");
        await playback.ConfigureAsync(Configuration(), default);
        var output = WasapiOutput.Instances[^1];
        await playback.PresentAsync(Pcm(), default);
        await Written(output);
        var entered = Signal();
        var release = Signal();
        output.BeforePadding = () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        output.Consume();
        await entered.Task.WaitAsync(Deadline);
        var retirement = playback.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            Assert.Equal((0L, (long?)null), playback.Position);
        }
        finally { release.TrySetResult(); }
        await retirement.WaitAsync(Deadline);
        Assert.Equal(480, output.Written);
        Assert.Equal(1, output.Disposals);
        Assert.Equal(0, output.WritesAfterDispose);
    }

    [Fact]
    public async Task MidstreamResetRetiresOldWorkerBeforeNewGenerationStarts()
    {
        await using var playback = new AudioPlayback("raw");
        await playback.ConfigureAsync(Configuration(), default);
        var oldOutput = WasapiOutput.Instances[^1];
        var oldDecoder = AudioDecoder.Instances[^1];
        var oldRegulator = AudioRegulator.Instances[^1];
        await playback.PresentAsync(Pcm(), default);
        await Written(oldOutput);
        await playback.ConfigureAsync(Configuration(2), default, midstream: true);
        Assert.Equal(1, oldOutput.Disposals);
        Assert.Equal(1, oldDecoder.Disposals);
        Assert.Equal(1, oldRegulator.Disposals);
        Assert.Equal(0, oldRegulator.Drains);
        var current = WasapiOutput.Instances[^1];
        Assert.NotSame(oldOutput, current);
        await playback.PresentAsync(Pcm(1_000_000), default);
        await Written(current);
        current.Consume();
        await Written(current);
        Assert.Equal(1_010_000, playback.Position.PtsUs);
        Assert.Equal(480, oldOutput.Written);
    }

    [Fact]
    public async Task StartupWakeCoalescingKeepsTargetAndDeviceEventsRefillQueuedPcm()
    {
        await using var playback = new AudioPlayback("raw");
        await playback.ConfigureAsync(Configuration(), default);
        var output = WasapiOutput.Instances[^1];
        for (var index = 0; index < 4; index++)
            await playback.PresentAsync(Pcm(index * 10_000, 480), default);
        Assert.False(output.Started);
        await playback.PresentAsync(Pcm(40_000, 480), default);
        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(480, await Written(output));
            output.Consume();
        }
        Assert.True(output.Started);
        Assert.Equal(2_400, playback.Position.ContentFrames);
        Assert.Equal(50_000, playback.Position.PtsUs);
    }

    [Fact]
    public async Task SuspendStopsQueuedOutputWithoutWaitingForDecode()
    {
        await using var playback = new AudioPlayback("raw");
        await playback.ConfigureAsync(Configuration(), default);
        var output = WasapiOutput.Instances[^1];
        await playback.PresentAsync(Pcm(), default);
        await Written(output);
        var entered = Signal();
        var release = Signal();
        AudioDecoder.Instances[^1].Decoding = () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        var producer = Task.Run(async () => await playback.PresentAsync(Pcm(50_000), default));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            playback.SuspendForRecovery();
            output.Consume();
            await output.Stopped.Task.WaitAsync(Deadline);
            Assert.Equal(480, output.Written);
        }
        finally { release.TrySetResult(); await producer.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task NormalResetDrainsPartialStartupBlockBeforeDisposingOldGeneration()
    {
        await using var playback = new AudioPlayback("raw");
        await playback.ConfigureAsync(Configuration(), default);
        var oldOutput = WasapiOutput.Instances[^1];
        var oldDecoder = AudioDecoder.Instances[^1];
        var oldRegulator = AudioRegulator.Instances[^1];
        await playback.PresentAsync(Pcm(frames: 240), default);
        var reset = playback.ConfigureAsync(Configuration(2), default).AsTask();
        Assert.Equal(240, await Written(oldOutput));
        Assert.False(reset.IsCompleted);
        oldOutput.Consume();
        await reset.WaitAsync(Deadline);
        Assert.Equal(240, oldOutput.Written);
        Assert.Equal(1, oldOutput.Drains);
        Assert.Equal(1, oldRegulator.Drains);
        Assert.Equal(1, oldOutput.Disposals);
        Assert.Equal(1, oldDecoder.Disposals);
        Assert.Equal(1, oldRegulator.Disposals);
        Assert.Equal((0L, (long?)null), playback.Position);
    }

    [Fact]
    public async Task CancellationDuringConfigurationReleasesUnpublishedNativeResourcesOnce()
    {
        await using var playback = new AudioPlayback("raw");
        var output = WasapiOutput.Instances[^1];
        using var stop = new CancellationTokenSource();
        AudioRegulator.Creating = stop.Cancel;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await playback.ConfigureAsync(Configuration(), stop.Token));
        }
        finally { AudioRegulator.Creating = null; }
        Assert.Equal(1, AudioDecoder.Instances[^1].Disposals);
        Assert.Equal(1, AudioRegulator.Instances[^1].Disposals);
        Assert.Equal(0, output.Disposals);
        Assert.Equal((0L, (long?)null), playback.Position);
        await playback.DisposeAsync();
        Assert.Equal(1, output.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementWaitsForNativeOwnerWithoutStarvingExistingPcm(bool resampling)
    {
        var playback = new AudioPlayback("raw");
        await playback.ConfigureAsync(Configuration(), default);
        var output = WasapiOutput.Instances[^1];
        var decoder = AudioDecoder.Instances[^1];
        var regulator = AudioRegulator.Instances[^1];
        await playback.PresentAsync(Pcm(), default);
        await Written(output);
        var entered = Signal();
        var release = Signal();
        Action pause = () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        if (resampling) regulator.Converting = pause;
        else decoder.Decoding = pause;
        var producer = Task.Run(async () => await playback.PresentAsync(Pcm(50_000, 480), default));
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            retirement = Task.Run(async () => await playback.DisposeAsync());
            output.Consume();
            await Written(output);
            Assert.Equal(0, decoder.Disposals);
            Assert.Equal(0, regulator.Disposals);
        }
        finally
        {
            release.TrySetResult();
            await producer.WaitAsync(Deadline);
            if (retirement is not null) await retirement.WaitAsync(Deadline);
            else await playback.DisposeAsync();
        }
        Assert.Equal(1, decoder.Disposals);
        Assert.Equal(1, regulator.Disposals);
        Assert.Equal(1, output.Disposals);
    }

    [Fact]
    public async Task DevicePositionFailureDuringRetirementStillReleasesEveryOwner()
    {
        var playback = new AudioPlayback("raw");
        await playback.ConfigureAsync(Configuration(), default);
        var output = WasapiOutput.Instances[^1];
        var decoder = AudioDecoder.Instances[^1];
        var regulator = AudioRegulator.Instances[^1];
        await playback.PresentAsync(Pcm(), default);
        await Written(output);
        await output.Waiting.Task.WaitAsync(Deadline);
        output.ReadingPosition = () => throw new IOException("Device lost.");
        await Assert.ThrowsAsync<IOException>(async () => await playback.DisposeAsync());
        await playback.DisposeAsync();
        Assert.Equal(1, decoder.Disposals);
        Assert.Equal(1, regulator.Disposals);
        Assert.Equal(1, output.Disposals);
    }

    [Fact]
    public async Task CancelledDrainSerializesConcurrentDisposeWithoutPublishingReplacement()
    {
        var playback = new AudioPlayback("raw");
        await playback.ConfigureAsync(Configuration(), default);
        var output = WasapiOutput.Instances[^1];
        var decoder = AudioDecoder.Instances[^1];
        var regulator = AudioRegulator.Instances[^1];
        var decoderCount = AudioDecoder.Instances.Count;
        await playback.PresentAsync(Pcm(frames: 240), default);
        using var stop = new CancellationTokenSource();
        var reset = playback.ConfigureAsync(Configuration(2), stop.Token).AsTask();
        await Written(output);
        var disposal = playback.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset.WaitAsync(Deadline));
        await disposal.WaitAsync(Deadline);
        Assert.Equal(decoderCount, AudioDecoder.Instances.Count);
        Assert.Equal(1, decoder.Disposals);
        Assert.Equal(1, regulator.Disposals);
        Assert.Equal(1, output.Disposals);
        Assert.Equal(0, output.WritesAfterDispose);
    }
}
