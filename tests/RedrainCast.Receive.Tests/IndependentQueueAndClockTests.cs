using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class IndependentQueueAndClockTests
{
    [Theory]
    [InlineData(1u, 2u, 768)]
    [InlineData(2u, 1u, 256)]
    public async Task TrackBytePressureDoesNotConsumeHealthyTrackPartition(uint saturated, uint healthy, int bytes)
    {
        using var queue = new ReceiveQueue(1024, 256, true);
        var started = QueueAndTrackTests.Media(0, saturated, size: bytes);
        queue.Add(started);
        Assert.Same(started, await queue.TakeAsync(saturated, default));
        Assert.Equal("CONGESTION", Assert.Throws<ProtocolException>(() =>
            queue.Add(QueueAndTrackTests.Media(0, saturated, size: 1))).Code);
        var other = QueueAndTrackTests.Media(0, healthy, size: healthy == 1 ? 768 : 256);
        queue.Add(other);
        Assert.Same(other, await queue.TakeAsync(healthy, default));
        queue.Complete(other);
        queue.Complete(started);
    }

    [Theory]
    [InlineData(1u, 2u)]
    [InlineData(2u, 1u)]
    public async Task RetirementRetainsOtherLaneAndAffectedStartedFrame(uint retired, uint healthy)
    {
        using var queue = new ReceiveQueue();
        var started = QueueAndTrackTests.Media(0, retired);
        queue.Add(started);
        Assert.Same(started, await queue.TakeAsync(retired, default));
        queue.Add(QueueAndTrackTests.Media(10, retired));
        var configuration = QueueAndTrackTests.Configuration(retired, 2);
        queue.Add(configuration);
        var other = QueueAndTrackTests.Media(20, healthy);
        queue.Add(other);
        queue.DiscardQueuedMedia(retired);
        Assert.Equal(1, queue.Snapshot(retired).Entries);
        Assert.Same(other, await queue.TakeAsync(healthy, default));
        queue.Complete(other);
        queue.Complete(started);
        Assert.Same(configuration, await queue.TakeAsync(retired, default));
        queue.Complete(configuration);
    }

    [Fact]
    public async Task SpanIncludesStartedFrameAtExactHalfSecondBoundary()
    {
        using var queue = new ReceiveQueue(1024, 256, true);
        var started = QueueAndTrackTests.Media(0, 2);
        queue.Add(started);
        await queue.TakeAsync(2, default);
        queue.Add(QueueAndTrackTests.Media(500_000, 2));
        Assert.Throws<ProtocolException>(() => queue.Add(QueueAndTrackTests.Media(500_001, 2)));
        queue.Add(QueueAndTrackTests.Media(9_000_000, 1));
        queue.Complete(started);
    }

    [Fact]
    public void DetachedAudioClockAdvancesAndResumedSourceCannotRewind()
    {
        long time = 0;
        var clock = new PlaybackClock(() => time);
        clock.AnchorVideo(1_000_000);
        time = 100;
        Assert.Equal(1_100_000, clock.CurrentUs);
        clock.ObserveAudio(1_200_000);
        time = 200;
        Assert.Equal(1_200_000, clock.CurrentUs);
        clock.DetachAudio();
        time = 1200;
        Assert.Equal(2_200_000, clock.CurrentUs);
        clock.ObserveAudio(1_500_000);
        Assert.Equal(2_200_000, clock.CurrentUs);
        clock.ObserveAudio(2_300_000);
        Assert.Equal(2_300_000, clock.CurrentUs);
        clock.AnchorVideo(0);
        Assert.Equal(2_300_000, clock.CurrentUs);
    }

    [Fact]
    public void InitialAudioClockAllowsOneTargetToAccumulateWithoutRewinding()
    {
        long time = 0;
        var clock = new PlaybackClock(() => time, audioExpected: true);
        clock.AnchorVideo(1_000_000);
        time = 49;
        Assert.Equal(1_000_000, clock.CurrentUs);
        time = 70;
        Assert.Equal(1_020_000, clock.CurrentUs);
        clock.ObserveAudio(1_025_000);
        time = 100;
        Assert.Equal(1_025_000, clock.CurrentUs);
    }

    [Fact]
    public void ProvisionalVideoAnchorCanJumpForwardButNeverRewind()
    {
        long time = 0;
        var clock = new PlaybackClock(() => time, audioExpected: true);
        clock.AnchorVideo(1_000_000);
        time = 100;
        Assert.Equal(1_050_000, clock.CurrentUs);
        clock.AnchorVideo(2_000_000);
        Assert.Equal(2_000_000, clock.CurrentUs);
        clock.AnchorVideo(1_500_000);
        Assert.Equal(2_000_000, clock.CurrentUs);
        time = 175;
        Assert.Equal(2_025_000, clock.CurrentUs);
    }

    [Fact]
    public void VideoRecoveryCannotReanchorHealthyOrPreviouslyEstablishedAudioMaster()
    {
        long time = 0;
        var clock = new PlaybackClock(() => time, audioExpected: true);
        clock.AnchorVideo(1_000_000);
        clock.ObserveAudio(1_010_000);
        clock.AnchorVideo(9_000_000);
        clock.VideoPresented(9_000_000);
        Assert.Equal(1_010_000, clock.CurrentUs);
        clock.BeginAudioRecovery();
        time = 100;
        clock.AnchorVideo(10_000_000);
        clock.VideoPresented(10_000_000);
        Assert.Equal(1_110_000, clock.CurrentUs);
        clock.ObserveAudio(1_100_000);
        Assert.Equal(1_110_000, clock.CurrentUs);
    }

    [Fact]
    public void FreshVideoAdvancesOnlyAudioRecoveryBootstrapBeforeAudioHasPlayed()
    {
        long time = 0;
        var clock = new PlaybackClock(() => time, audioExpected: true);
        clock.AnchorVideo(1_000_000);
        clock.BeginAudioRecovery();
        clock.VideoPresented(2_000_000);
        Assert.Equal(2_000_000, clock.CurrentUs);
        clock.ObserveAudio(2_010_000);
        clock.BeginAudioRecovery();
        time = 100;
        clock.VideoPresented(3_000_000);
        Assert.Equal(2_110_000, clock.CurrentUs);
    }

    [Theory]
    [InlineData(480, 480)]
    [InlineData(480, 490)]
    [InlineData(480, 470)]
    public void PendingStartupPcmRetrimsUsingMappedSourceNotOutputFrameCount(int sourceFrames, int convertedFrames)
    {
        var buffer = new AudioBuffer();
        buffer.Push(new byte[convertedFrames * 4], 1_000_000, sourceFrames);
        buffer.DiscardBefore(1_005_001);
        var block = buffer.Pull(convertedFrames);
        var span = Assert.Single(block.Spans);
        Assert.InRange(span.FirstPtsUs, 1_005_001, 1_005_025);
        Assert.Equal(1_010_000, span.EndPtsUs);
        Assert.Equal(convertedFrames, block.Frames);
        var presentation = new AudioPresentation();
        presentation.Submit(0, block);
        Assert.Equal(span.Frames, presentation.Current(convertedFrames).ContentFrames);
    }

    [Fact]
    public void LongOpusAccessUnitDoesNotDropRetainedSourceDuringCatchup()
    {
        var buffer = new AudioBuffer();
        buffer.PreserveStartupSource(true);
        buffer.Push(new byte[RecoveryAudioFrames * 4], 0, RecoveryAudioFrames);
        Assert.Equal(RecoveryAudioFrames, buffer.Frames);
        Assert.Equal(0, buffer.Snapshot.Dropped);
        Assert.Equal(0, buffer.Pull(960).Spans[0].FirstPtsUs);
    }
    private const int RecoveryAudioFrames = 5760;

    [Fact]
    public void ResamplerLookaheadKeepsOriginalSourceAndReversedIntervalsInOrder()
    {
        var timeline = new AudioConversionTimeline();
        var buffer = new AudioBuffer();
        timeline.Append(1_000_000, 960);
        timeline.Submit(buffer, new byte[944 * 4], 16);
        var first = buffer.Pull(944);
        Assert.Equal(1_000_000, first.Spans[0].FirstPtsUs);
        Assert.Equal(1_019_666, first.Spans[0].EndPtsUs);
        timeline.Append(700_000, 960);
        timeline.Submit(buffer, new byte[950 * 4], 16);
        var second = buffer.Pull(950);
        Assert.Equal(2, second.Spans.Length);
        Assert.Equal(1_019_666, second.Spans[0].FirstPtsUs);
        Assert.Equal(1_020_000, second.Spans[0].EndPtsUs);
        Assert.Equal(700_000, second.Spans[1].FirstPtsUs);
        Assert.Equal(16, timeline.PendingFrames);
    }

    [Theory]
    [InlineData(1_000_020L)]
    [InlineData(2_000_000L)]
    [InlineData(700_000L)]
    public void ShrinkingResamplerConsumesZeroOutputIntervalsWithoutInventingSourceSpans(long secondPts)
    {
        var timeline = new AudioConversionTimeline();
        var buffer = new AudioBuffer();
        timeline.Append(1_000_000, 1);
        timeline.Append(secondPts, 1);
        byte[] converted = [1, 2, 3, 4];
        timeline.Submit(buffer, converted, 0);
        Assert.Equal(0, timeline.PendingFrames);
        var block = buffer.Pull(1);
        Assert.Equal(converted, block.Data);
        var span = Assert.Single(block.Spans);
        Assert.Equal(secondPts, span.FirstPtsUs);
        Assert.Equal(secondPts + 20, span.EndPtsUs);
    }

    [Fact]
    public void FragmentedShrinkingResamplerPreservesOutputOrderAndRetainedSource()
    {
        var timeline = new AudioConversionTimeline();
        var buffer = new AudioBuffer();
        for (var index = 0; index < 5; index++)
            timeline.Append(1_000_000 + index * 1_000_000L / AudioFormat.Rate, 1);
        byte[] converted = [1, 2, 3, 4, 5, 6, 7, 8];
        timeline.Submit(buffer, converted, 1);
        Assert.Equal(1, timeline.PendingFrames);
        var block = buffer.Pull(2);
        Assert.Equal(converted, block.Data);
        Assert.Equal(2, block.Spans.Length);
        Assert.Equal(1_000_020, block.Spans[0].FirstPtsUs);
        Assert.Equal(1_000_062, block.Spans[1].FirstPtsUs);
        timeline.Submit(buffer, [9, 10, 11, 12], 0);
        Assert.Equal(0, timeline.PendingFrames);
        var remaining = buffer.Pull(1);
        Assert.Equal(new byte[] { 9, 10, 11, 12 }, remaining.Data);
        Assert.Equal(1_000_083, Assert.Single(remaining.Spans).FirstPtsUs);
    }

    [Fact]
    public async Task HeldCompressedPrerollKeepsItsByteAndEntryChargeUntilDecoded()
    {
        using var queue = new ReceiveQueue(1024, 256, true);
        var retained = QueueAndTrackTests.Media(0, 2, size: 256);
        queue.Add(retained);
        await queue.TakeAsync(2, default);
        queue.HoldMedia(retained);
        queue.Complete(retained);
        Assert.Equal((1, 256L), (queue.Snapshot(2).Entries, queue.Snapshot(2).Bytes));
        Assert.Throws<ProtocolException>(() => queue.Add(QueueAndTrackTests.Media(10, 2)));
        var video = QueueAndTrackTests.Media(0, 1, size: 768);
        queue.Add(video);
        await queue.TakeAsync(1, default);
        queue.Complete(video);
        queue.ReleaseHeld(retained);
        Assert.Equal(0, queue.Snapshot(2).Entries);
        queue.Add(QueueAndTrackTests.Media(10, 2, size: 256));
    }

    [Fact]
    public void NegotiatedTrackPartitionsMatchAndroidEightMiBAndHalfMiBAudio()
    {
        using var queue = new ReceiveQueue(ReceiveQueue.MaximumIndependentBytes, ReceiveQueue.AudioTrackBytes, true);
        queue.Add(QueueAndTrackTests.Media(0, 1, size: 8 * 1024 * 1024));
        queue.Add(QueueAndTrackTests.Media(0, 2, size: 512 * 1024));
        Assert.Throws<ProtocolException>(() => queue.Add(QueueAndTrackTests.Media(0, 1)));
        Assert.Throws<ProtocolException>(() => queue.Add(QueueAndTrackTests.Media(0, 2)));
    }
}
