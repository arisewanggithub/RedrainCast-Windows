using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Tests;

[CollectionDefinition("Process timing", DisableParallelization = true)]
public sealed class ProcessTimingCollection;

[Collection("Process timing")]
public sealed class MediaTimingTests
{
    [Fact]
    public void ReceiverStageTraceRemainsCompleteWithoutNativeCpuProbes()
    {
        using var capture = MediaTiming.Start(64);
        foreach (var stage in Enum.GetValues<MediaStage>())
            MediaTiming.Record(stage, 12, 2, 1, 123);
        var trace = capture.Stop();
        Assert.True(trace.Complete);
        Assert.Empty(MediaTiming.CheckPeerTrace(trace, sender: false));
    }

    [Fact]
    public void DisabledDoesNotRetainEvents()
    {
        Assert.False(MediaTiming.Enabled);
        MediaTiming.Record(MediaStage.SourcePcm, 12, 2, 1, 123);
        using var capture = MediaTiming.Start(4);
        Assert.Empty(capture.Stop().Events);
        Assert.False(MediaTiming.Enabled);
    }

    [Fact]
    public void BoundedOverflowIsExplicitAndIncomplete()
    {
        using var capture = MediaTiming.Start(2);
        for (var i = 0; i < 5; i++) MediaTiming.Record(MediaStage.SourcePcm, 12, 2, 1, i);
        var result = capture.Stop();
        Assert.Equal(5, result.Attempted);
        Assert.Equal(3, result.Dropped);
        Assert.False(result.Complete);
        Assert.Equal(new long[] { 0, 1 }, result.Events.Select(item => item.Sequence));
        Assert.Equal(new long[] { 0, 1 }, result.Events.Select(item => item.Pts));
    }

    [Fact]
    public void LifecycleRejectsOverlapAndStopsIdempotently()
    {
        using var first = MediaTiming.Start(2);
        Assert.Throws<InvalidOperationException>(() => MediaTiming.Start(2));
        MediaTiming.Record(MediaStage.SourcePcm, 12, 2, 1);
        var stopped = first.Stop();
        MediaTiming.Record(MediaStage.SourcePcm, 12, 2, 1);
        Assert.Same(stopped, first.Stop());
        using var second = MediaTiming.Start(2);
        first.Dispose();
        Assert.True(MediaTiming.Enabled);
        Assert.Empty(second.Stop().Events);
    }

    [Fact]
    public async Task ConcurrentWritersPublishEveryReservedNumericEvent()
    {
        using var capture = MediaTiming.Start(4000);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(lane => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
                MediaTiming.Record(MediaStage.SourcePcm, lane + 1, 2, 3, lane * 1000 + i);
        })));
        var result = capture.Stop();
        Assert.True(result.Complete);
        Assert.Equal(0, result.Dropped);
        Assert.Equal(4000, result.Events.Length);
        Assert.Equal(4000, result.Events.Select(item => item.Pts).Distinct().Count());
        Assert.All(result.Events, item =>
        {
            Assert.InRange(item.Qpc, result.StartedQpc, result.StoppedQpc);
            Assert.True(item.ThreadId > 0);
            Assert.Equal(3u, item.Generation);
        });
        Assert.True(result.Frequency > 0);
        Assert.True(result.RecordingTicks >= result.MaximumRecordingTicks);
    }

    [Fact]
    public async Task QueueStagesCorrelateExactSessionGenerationAndPts()
    {
        using var capture = MediaTiming.Start(4);
        using var queue = new ReceiveQueue();
        var packet = new Packet(PacketType.AudioPacket, 12, 2, 3, 123456, 0, new byte[1920]);
        queue.Add(packet);
        var taken = await queue.TakeAsync(2, CancellationToken.None);
        queue.Complete(taken);
        var result = capture.Stop();
        Assert.True(result.Complete);
        Assert.Equal(new[] { MediaStage.QueueAdmit, MediaStage.QueueTake }, result.Events.Select(item => item.Stage));
        Assert.All(result.Events, item =>
        {
            Assert.Equal(12, item.Session);
            Assert.Equal(2u, item.Track);
            Assert.Equal(3u, item.Generation);
            Assert.Equal(123456, item.Pts);
            Assert.Equal(1920, item.A);
        });
    }

    [Fact]
    public void InvalidCapacityCannotEnableCapture()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaTiming.Start(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaTiming.Start(MediaTiming.DefaultCapacity + 1));
        Assert.False(MediaTiming.Enabled);
    }

    [Fact]
    public void MissingStageOrPartnerIsNotCompletePeerEvidence()
    {
        using var capture = MediaTiming.Start(8);
        MediaTiming.Record(MediaStage.SourcePcm, 12, 2, 1, 123);
        MediaTiming.Record(MediaStage.TcpWriteBegin, 12, 2, 1, 123);
        var trace = capture.Stop();
        Assert.NotEmpty(MediaTiming.CheckPeerTrace(trace, sender: true));
        Assert.NotEmpty(MediaTiming.CheckPeerTrace(trace, sender: false));
    }

    [Fact]
    public void PartnerWithDifferentPtsOrGenerationCannotCompleteTrace()
    {
        using var capture = MediaTiming.Start(8);
        MediaTiming.Record(MediaStage.SourcePcm, 12, 2, 1, 123);
        MediaTiming.Record(MediaStage.TcpWriteBegin, 12, 2, 1, 123);
        MediaTiming.Record(MediaStage.TcpWriteEnd, 12, 2, 2, 124);
        Assert.Contains(MediaTiming.CheckPeerTrace(capture.Stop(), sender: true),
            error => error.Contains("Unpaired"));
    }

    [Fact]
    public void CompleteSenderTraceAndRecordingAllocatesNoPerEventObjects()
    {
        using var capture = MediaTiming.Start(8);
        MediaTiming.Record(MediaStage.SourcePcm, 12, 2, 1, 123);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        MediaTiming.Record(MediaStage.TcpWriteBegin, 12, 2, 1, 123);
        MediaTiming.Record(MediaStage.TcpWriteEnd, 12, 2, 1, 123);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert.Empty(MediaTiming.CheckPeerTrace(capture.Stop(), sender: true));
    }

    [Fact]
    public async Task StopDuringConcurrentRecordingProducesImmutableQuiescentSnapshot()
    {
        using var capture = MediaTiming.Start();
        using var started = new ManualResetEventSlim();
        var worker = Task.Run(() =>
        {
            started.Set();
            for (var i = 0; i < 10000; i++) MediaTiming.Record(MediaStage.SourcePcm, 12, 2, 1, i);
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var result = capture.Stop();
        await worker;
        Assert.Same(result, capture.Stop());
        Assert.Equal(result.Attempted, result.Events.Length);
        Assert.True(result.Complete);
        Assert.All(result.Events, item => Assert.InRange(item.Qpc, result.StartedQpc, result.StoppedQpc));
    }
}
