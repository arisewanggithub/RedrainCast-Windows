using System.Diagnostics;
using System.Text.Json;
using RedrainCast.Receive.Media;
using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class VideoFrameTraceTests
{
    [Fact]
    public void ReadDiagnosticsRequireMasterAndDetailedOptInAndHaveBoundedCapacity()
    {
        Assert.False(VideoTraceSettings.Parse(null, null, "1", "1", "1").Reads);
        Assert.False(VideoTraceSettings.Parse("1", null, null, "1", "1").Reads);
        var settings = VideoTraceSettings.Parse("1", "180", "1", "1", "1");
        Assert.True(settings.Reads);
        Assert.Equal(2211840, settings.Capacity);
        Assert.Equal(7372800, VideoTraceSettings.Parse("1", "600", "1", null, "1").Capacity);
        var buffer = new VideoTraceBuffer(true, capacity: 2, durationTicks: 10);
        Assert.False(buffer.Add("read_begin", 1, 0, -1, 99));
        buffer.Add("receive", 1, 1, 1, 100);
        buffer.Add("read_begin", 1, 0, -1, 101);
        buffer.Add("header_begin", 1, 0, -1, 102);
        buffer.Add("read_end", 1, 1, 1, 110);
        var snapshot = Assert.IsType<VideoTraceSnapshot>(buffer.Stop("duration", 110));
        Assert.Equal(2, snapshot.Events.Length);
        Assert.Equal(1, snapshot.Dropped);
        Assert.False(snapshot.Complete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlaySampleMetadataKeepsExactLongCountersAndResetWithoutAudio(bool reset)
    {
        var playback = new PlaybackStatistics(42, 7, long.MaxValue, long.MaxValue - 1, long.MaxValue - 20,
            20, 640, 360, 0, false, null, null, 0);
        using var sample = JsonDocument.Parse(VideoTraceJson.StatisticsSample(playback, 123456, reset));
        var root = sample.RootElement;
        Assert.Equal(123456, root.GetProperty("sampledAtMs").GetInt64());
        Assert.Equal(42, root.GetProperty("epoch").GetInt64());
        Assert.Equal(reset, root.GetProperty("baselineReset").GetBoolean());
        Assert.Equal(long.MaxValue, root.GetProperty("received").GetInt64());
        Assert.Equal(long.MaxValue - 1, root.GetProperty("decoded").GetInt64());
        Assert.Equal(long.MaxValue - 20, root.GetProperty("presented").GetInt64());
        Assert.Equal(20, root.GetProperty("skipped").GetInt64());
        Assert.Equal(7, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("audio", out _));
    }

    [Theory]
    [InlineData(null, "1", "1", false)]
    [InlineData("1", null, "1", false)]
    [InlineData("1", "1", null, false)]
    [InlineData("1", "1", "true", false)]
    [InlineData("1", "1", "1", true)]
    public void ReadinessRequiresAllThreeExplicitOptIns(string? enabled, string? detailed,
        string? readiness, bool expected)
    {
        Assert.Equal(expected, VideoTraceSettings.Parse(enabled, "180", detailed, readiness).Readiness);
    }

    [Fact]
    public void ReadinessHasSeparateBoundAndIsActiveOnlyInsideReceivedCaptureWindow()
    {
        var settings = VideoTraceSettings.Parse("1", "600", "1", "1");
        Assert.Equal(4915200, settings.Capacity);
        // Packet stages plus readiness, mailbox, context and dispatch events at 240fps/200 retries.
        Assert.True(settings.Capacity >= 600 * (240 * 18 + 200 * 12));
        var buffer = new VideoTraceBuffer(true, capacity: 2, durationTicks: 10);
        Assert.False(buffer.IsActive(100));
        buffer.Add("receive", 1, 1, 10, 100);
        Assert.True(buffer.IsActive(109));
        Assert.False(buffer.IsActive(110));
        buffer.Stop("dispose", 109);
        Assert.False(buffer.IsActive(109));
    }

    [Theory]
    [InlineData(null, 30, 122880)]
    [InlineData("1", 1, 65536)]
    [InlineData("30", 30, 122880)]
    [InlineData("180", 180, 737280)]
    [InlineData("600", 600, 2457600)]
    public void DetailedStagesRequireExplicitMasterOptInAndHaveSeparateBound(string? seconds, int duration, int capacity)
    {
        Assert.False(VideoTraceSettings.Parse(null, seconds, "1").Detailed);
        Assert.False(VideoTraceSettings.Parse("1", seconds, "true").Detailed);
        var settings = VideoTraceSettings.Parse("1", seconds, "1");
        Assert.True(settings.Detailed);
        Assert.Equal(duration, settings.DurationSeconds);
        Assert.Equal(capacity, settings.Capacity);
    }

    [Fact]
    public void DetailedTenMinuteBudgetRetains240FpsAndRenderRetries()
    {
        var settings = VideoTraceSettings.Parse("1", "600", "1");
        // Nine packet/frame stages at 240fps plus four stages at 200 render retries/s.
        Assert.True(settings.Capacity >= 600 * (240 * 9 + 200 * 4));
        Assert.Equal(614400, VideoTraceSettings.Parse("1", "600").Capacity);
    }

    [Theory]
    [InlineData("queue_take", "QUEUE_TAKE", null)]
    [InlineData("decode_wait", "DECODE_GATE_WAIT", null)]
    [InlineData("decode_enter", "DECODE_GATE_ACQUIRED", null)]
    [InlineData("decode_end", "DECODE_END", null)]
    [InlineData("pending_wait", "PENDING_GATE_WAIT", null)]
    [InlineData("pending", "PENDING", null)]
    [InlineData("render_wake", "RENDER_WAKE", "signaled")]
    [InlineData("render_wake", "RENDER_WAKE", "timeout")]
    [InlineData("render_wake", "RENDER_WAKE", "readiness")]
    [InlineData("read_begin", "VIDEO_READ_BEGIN", null)]
    [InlineData("header_begin", "VIDEO_HEADER_BEGIN", null)]
    [InlineData("header_end", "VIDEO_HEADER_END", null)]
    [InlineData("payload_begin", "VIDEO_PAYLOAD_BEGIN", null)]
    [InlineData("payload_end", "VIDEO_PAYLOAD_END", null)]
    [InlineData("read_end", "VIDEO_READ_END", "header:eof")]
    [InlineData("admission_begin", "VIDEO_ADMISSION_BEGIN", null)]
    [InlineData("admission_end", "VIDEO_ADMISSION_END", "accepted")]
    [InlineData("render_gate_miss", "RENDER_GATE_MISS", null)]
    [InlineData("render_begin", "RENDER_BEGIN", null)]
    [InlineData("render_end", "RENDER_END", "not_ready")]
    [InlineData("render_end", "RENDER_END", "still_drawing")]
    [InlineData("render_end", "RENDER_END", "occluded")]
    [InlineData("render_end", "RENDER_END", "request_rejected")]
    [InlineData("render_end", "RENDER_END", "presented")]
    [InlineData("render_end", "RENDER_END", "redrawn")]
    [InlineData("render_wait", "RENDER_WAIT", "timeoutMs=5;pending=True")]
    [InlineData("readiness_signal", "READINESS_SIGNAL", "pending")]
    [InlineData("readiness_check", "READINESS_CHECK", "held")]
    [InlineData("readiness_check", "READINESS_CHECK", "signal")]
    [InlineData("readiness_check", "READINESS_CHECK", "not_ready")]
    [InlineData("render_gate_wait", "RENDER_GATE_WAIT", null)]
    [InlineData("render_gate_enter", "RENDER_GATE_ACQUIRED", null)]
    [InlineData("mailbox_take", "MAILBOX_TAKE", null)]
    [InlineData("mailbox_return", "MAILBOX_RETURN", "begin")]
    [InlineData("mailbox_return", "MAILBOX_RETURN", "end")]
    [InlineData("context_wait", "GPU_CONTEXT_WAIT", null)]
    [InlineData("context_enter", "GPU_CONTEXT_ACQUIRED", null)]
    [InlineData("context_leave", "GPU_CONTEXT_RELEASED", null)]
    [InlineData("statistics_sample", "STATISTICS_SAMPLE", "{\"sampledAtMs\":1000,\"epoch\":1,\"baselineReset\":false,\"presented\":30,\"skipped\":20}")]
    public void DetailedStageJsonPreservesTimestampAndOutcome(string stage, string expected, string? reason)
    {
        var trace = new VideoTraceBuffer(true, capacity: 4, durationTicks: 100);
        trace.Add("receive", 123, 7, 42, 1000);
        trace.Add(stage, 123, 7, 42, 1001, reason);
        var snapshot = Assert.IsType<VideoTraceSnapshot>(trace.Stop("duration", 1100));
        using var json = JsonDocument.Parse(VideoTraceJson.Event(snapshot, snapshot.Events[1]));
        Assert.Equal(expected, json.RootElement.GetProperty("stage").GetString());
        Assert.Equal(reason, json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(1001, json.RootElement.GetProperty("qpc").GetInt64());
        Assert.Equal(42, json.RootElement.GetProperty("ptsUs").GetInt64());
        Assert.True(snapshot.Complete);
    }

    [Fact]
    public void DefaultSettingsPreserveThirtySecondWindowAndCapacity()
    {
        var settings = VideoTraceSettings.Parse("1", null);
        Assert.True(settings.Enabled);
        Assert.Equal(30, settings.DurationSeconds);
        Assert.Equal(16384, settings.Capacity);
        var trace = new VideoTraceBuffer(true);
        trace.Add("receive", 1, 1, 0, 1000);
        trace.Add("decode", 1, 1, 0, 1000 + 30 * Stopwatch.Frequency - 1);
        trace.Add("present", 1, 1, 0, 1000 + 30 * Stopwatch.Frequency);
        var snapshot = Assert.IsType<VideoTraceSnapshot>(trace.Stop("duration", 1000 + 30 * Stopwatch.Frequency));
        Assert.True(snapshot.Complete);
        Assert.Equal(2, snapshot.Events.Length);
        using var header = JsonDocument.Parse(VideoTraceJson.Header(snapshot));
        Assert.Equal(30, header.RootElement.GetProperty("durationSeconds").GetInt32());
        Assert.Equal(16384, header.RootElement.GetProperty("capacity").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("601")]
    [InlineData("600.0")]
    [InlineData(" 600")]
    [InlineData("600 ")]
    [InlineData("invalid")]
    [InlineData("2147483648")]
    public void InvalidOptInDurationIsExplicitlyRejected(string seconds)
    {
        var error = Assert.Throws<ArgumentException>(() => VideoTraceSettings.Parse("1", seconds));
        Assert.Contains("REDRAIN_VIDEO_TRACE_SECONDS", error.Message);
        Assert.Contains("1 through 600", error.Message);
        Assert.False(VideoTraceSettings.Parse(null, seconds).Enabled);
    }

    [Theory]
    [InlineData("1", 1, 16384)]
    [InlineData("30", 30, 16384)]
    [InlineData("31", 31, 31744)]
    [InlineData("600", 600, 614400)]
    public void ValidOptInDurationHasBoundedCapacity(string seconds, int duration, int capacity)
    {
        var settings = VideoTraceSettings.Parse("1", seconds);
        Assert.True(settings.Enabled);
        Assert.Equal(duration, settings.DurationSeconds);
        Assert.Equal(capacity, settings.Capacity);
    }

    [Fact]
    public void TenMinuteWindowRetainsHighRateStreamAndExcludesBoundary()
    {
        var settings = VideoTraceSettings.Parse("1", "600");
        var trace = new VideoTraceBuffer(true, settings.Capacity, durationSeconds: settings.DurationSeconds);
        const int eventCount = 600 * 240 * 4;
        const long start = 1000;
        var duration = 600 * Stopwatch.Frequency;
        for (var i = 0; i < eventCount; i++)
            trace.Add((i % 4) switch { 0 => "receive", 1 => "decode", 2 => "skip", _ => "present" },
                1, 1, i / 4, start + i * duration / eventCount);
        trace.Add("receive", 1, 1, eventCount, start + duration - 1);
        trace.Add("receive", 1, 1, eventCount + 1, start + duration);
        var snapshot = Assert.IsType<VideoTraceSnapshot>(trace.Stop("duration", start + duration + 1));
        Assert.True(snapshot.Complete);
        Assert.False(snapshot.Overflow);
        Assert.Equal(0, snapshot.Dropped);
        Assert.Equal(eventCount + 1, snapshot.Events.Length);
        Assert.Equal(start + duration - 1, snapshot.Events[^1].Qpc);
        Assert.Equal(start + duration, snapshot.EndQpc);
        using var header = JsonDocument.Parse(VideoTraceJson.Header(snapshot));
        Assert.Equal(600, header.RootElement.GetProperty("durationSeconds").GetInt32());
        Assert.Equal(614400, header.RootElement.GetProperty("capacity").GetInt32());
    }

    [Fact]
    public void TenMinuteCapacityRemainsFixedAndReportsOverflow()
    {
        var settings = VideoTraceSettings.Parse("1", "600");
        var trace = new VideoTraceBuffer(true, settings.Capacity, durationSeconds: settings.DurationSeconds);
        for (var i = 0; i <= settings.Capacity; i++)
            trace.Add("receive", 1, 1, i, 1000);
        var snapshot = Assert.IsType<VideoTraceSnapshot>(trace.Stop("duration", 1000 + 600 * Stopwatch.Frequency));
        Assert.Equal(settings.Capacity, snapshot.Events.Length);
        Assert.Equal(1, snapshot.Dropped);
        Assert.True(snapshot.Overflow);
        Assert.False(snapshot.Complete);
    }

    [Fact]
    public void DisabledAndPreReceiveEventsDoNotStartCapture()
    {
        var disabled = new VideoTraceBuffer(false);
        Assert.False(disabled.Add("receive", 1, 2, 3, 10));
        Assert.Null(disabled.Stop("dispose", 20));
        var enabled = new VideoTraceBuffer(true);
        Assert.False(enabled.Add("decode", 1, 2, 3, 10));
        Assert.Null(enabled.Stop("dispose", 20));
    }

    [Fact]
    public void EventsKeepExactPtsSessionGenerationAndSkipReason()
    {
        var trace = new VideoTraceBuffer(true, durationTicks: 100);
        Assert.True(trace.Add("receive", 123, 7, 90001, 1000));
        trace.Add("decode", 123, 7, 90001, 1002);
        trace.Add("skip", 123, 7, 90001, 1004, "pending_replaced");
        trace.Add("receive", 123, 8, 90002, 1005);
        trace.Add("decode", 123, 8, 90002, 1006);
        trace.Add("present", 123, 8, 90002, 1007);
        var snapshot = Assert.IsType<VideoTraceSnapshot>(trace.Stop("duration", 1100));
        Assert.True(snapshot.Complete);
        Assert.False(snapshot.Overflow);
        Assert.Equal(Stopwatch.Frequency, snapshot.QpcFrequency);
        Assert.Equal(new long[] { 90001, 90001, 90001, 90002, 90002, 90002 },
            snapshot.Events.Select(e => e.PtsUs));
        Assert.All(snapshot.Events, e => Assert.Equal(123, e.Session));
        Assert.Equal(7u, snapshot.Events[2].Generation);
        Assert.Equal("pending_replaced", snapshot.Events[2].Reason);
        Assert.Equal(8u, snapshot.Events[5].Generation);
        Assert.Equal(1007, snapshot.Events[5].Qpc);
    }

    [Fact]
    public void CapacityAndDurationAreBoundedWithExplicitOverflowCounts()
    {
        var trace = new VideoTraceBuffer(true, capacity: 2, durationTicks: 100);
        trace.Add("receive", 1, 1, 10, 1000);
        trace.Add("decode", 1, 1, 10, 1001);
        trace.Add("present", 1, 1, 10, 1002);
        trace.Add("receive", 1, 1, 11, 1100);
        var snapshot = Assert.IsType<VideoTraceSnapshot>(trace.Stop("duration", 1110));
        Assert.False(snapshot.Complete);
        Assert.True(snapshot.Overflow);
        Assert.Equal(1, snapshot.Dropped);
        Assert.Equal(2, snapshot.Events.Length);
        Assert.Equal(1, snapshot.Counts["receive"]);
        Assert.Equal(1, snapshot.Counts["present"]);
        Assert.Equal(1100, snapshot.EndQpc);
        Assert.Null(trace.Stop("dispose", 1200));
    }

    [Fact]
    public void EarlyDisposeIsIncompleteAndConcurrentWritersStayBounded()
    {
        var trace = new VideoTraceBuffer(true, capacity: 100, durationTicks: 1000);
        trace.Add("receive", 1, 1, 0, 0);
        Parallel.For(0, 1000, i => trace.Add("decode", 1, 1, i, 1));
        var snapshot = Assert.IsType<VideoTraceSnapshot>(trace.Stop("dispose", 2));
        Assert.False(snapshot.Complete);
        Assert.True(snapshot.Overflow);
        Assert.Equal(100, snapshot.Events.Length);
        Assert.Equal(901, snapshot.Dropped);
        Assert.Equal(1000, snapshot.Counts["decode"]);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), snapshot.Events.Select(e => e.Sequence));
    }

    [Fact]
    public void JsonLinesUseSharedSchemaAndOverflowSafeLocalNanoseconds()
    {
        var samples = new[]
        {
            new VideoTraceEvent(1, "receive", 123, 7, 42, 9_000_000_000, null),
            new VideoTraceEvent(2, "decode", 123, 7, 42, 10_000_000_000, null),
            new VideoTraceEvent(3, "skip", 123, 7, 42, 10_000_000_001, "pending_replaced"),
            new VideoTraceEvent(4, "present", 123, 7, 43, 10_000_000_002, null)
        };
        var snapshot = new VideoTraceSnapshot(123, 10_000_000, 9_000_000_000, 10_000_000_002,
            "duration", true, false, 0, new() { ["receive"] = 1 }, samples);
        using var header = JsonDocument.Parse(VideoTraceJson.Header(snapshot));
        Assert.Equal("header", header.RootElement.GetProperty("kind").GetString());
        Assert.Equal(123, header.RootElement.GetProperty("session").GetInt64());
        Assert.Equal(1_000_000_000, header.RootElement.GetProperty("frequency").GetInt64());
        Assert.Equal(10_000_000, header.RootElement.GetProperty("qpcFrequency").GetInt64());
        var stages = new[] { "RECEIVED", "DECODED", "SKIPPED", "PRESENTED" };
        for (var i = 0; i < samples.Length; i++)
        {
            using var json = JsonDocument.Parse(VideoTraceJson.Event(snapshot, samples[i]));
            var root = json.RootElement;
            Assert.Equal("event", root.GetProperty("kind").GetString());
            Assert.Equal(stages[i], root.GetProperty("stage").GetString());
            Assert.Equal(samples[i].Sequence, root.GetProperty("sequence").GetInt64());
            Assert.Equal(123, root.GetProperty("session").GetInt64());
            Assert.Equal(7u, root.GetProperty("generation").GetUInt32());
            Assert.Equal(samples[i].PtsUs, root.GetProperty("ptsUs").GetInt64());
            Assert.Equal((samples[i].Qpc - snapshot.StartQpc) * 100, root.GetProperty("atNs").GetInt64());
            Assert.Equal(samples[i].Qpc, root.GetProperty("qpc").GetInt64());
            if (i == 2) Assert.Equal("pending_replaced", root.GetProperty("reason").GetString());
        }
        using var summary = JsonDocument.Parse(VideoTraceJson.Summary(snapshot));
        Assert.Equal("summary", summary.RootElement.GetProperty("kind").GetString());
        Assert.Equal(4, summary.RootElement.GetProperty("events").GetInt32());
        Assert.Equal(0, summary.RootElement.GetProperty("dropped").GetInt64());
        Assert.True(summary.RootElement.GetProperty("windowComplete").GetBoolean());
        using var incomplete = JsonDocument.Parse(VideoTraceJson.Summary(snapshot with
        {
            Complete = false, Overflow = true, Dropped = 1
        }));
        Assert.False(incomplete.RootElement.GetProperty("windowComplete").GetBoolean());
        Assert.Equal(1, incomplete.RootElement.GetProperty("dropped").GetInt64());
    }
}
