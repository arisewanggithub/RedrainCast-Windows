using System.Xml.Linq;
using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class PlaybackStatisticsTests
{
    private static PlaybackStatistics Playback(long frames = 100) => new(1, 1, frames, frames, frames, 0,
        640, 360, 90, false, new(4800, 2400, 480, 9600, 480, 960), new(1, 1_000_000, 900_000, 48000, 100_000, 1), 100_000);
    private static ReceiveStatistics Receive(long bytes = 1000, long packets = 100) => new(1, new(packets, bytes, 10), new(100, bytes, 20),
        new(1, 100, 25_000), new(2, 200, 30_000), null, null, null);

    [Fact]
    public void SuccessfulPresentationCacheTracksMonitorChangesAndResetsWithoutGuessing()
    {
        var cache = new VideoPresentationStatisticsCache();
        Assert.Null(cache.Snapshot);
        cache.Presented(new(true), false);
        Assert.Equal(VideoPresentationOutput.SdrToneMapped, cache.Snapshot!.Output);
        var previous = cache.Snapshot;
        Assert.Same(previous, cache.Snapshot);
        cache.Presented(new(true), true);
        Assert.Equal(VideoPresentationOutput.Hdr, cache.Snapshot!.Output);
        cache.Presented(new(true), false);
        Assert.Equal(VideoPresentationOutput.SdrToneMapped, cache.Snapshot!.Output);
        cache.Presented(HdrVideoFormat.Sdr, true);
        Assert.Equal(VideoPresentationOutput.Sdr, cache.Snapshot!.Output);
        cache.Reset();
        Assert.Null(cache.Snapshot);
    }

    [Fact]
    public void MonitorOutputChangesReprojectOnNextSampleWithoutResettingRates()
    {
        var sampler = new PlaybackRateSampler();
        var format = new HdrVideoFormat(true);
        var cache = new VideoPresentationStatisticsCache();
        cache.Presented(format, false);
        var p = Playback() with { Video = new(format, VideoTransfer.Pq, "Main 10", 10), Presentation = cache.Snapshot };
        sampler.Sample(1000, p, Receive());
        cache.Presented(format, true);
        p = p with { Presentation = cache.Snapshot };
        var rates = sampler.Sample(2000, p, Receive());
        Assert.False(sampler.BaselineReset);
        Assert.Equal(0, rates.PresentedFps);
        var display = PlaybackStatisticsDisplay.Create(new("h265", null, 60, "auto"), p, Receive(), rates, 560, 420, null);
        Assert.Equal("HDR output", display.Groups[0].Cells.Single(c => c.Field.Id == "presentationOutput").Value);
        Assert.Equal("HDR10 · PQ", display.Groups[0].Cells.Single(c => c.Field.Id == "videoDynamicRange").Value);
    }
    [Fact]
    public void CompactTextPreservesCategoriesUnitsAndClockCaveat()
    {
        var text = PlaybackStatisticsText.FormatCompact(new("h264", "raw", 120, "auto"), Playback(), Receive(),
            new(null, null, null, null, null, null, null, null), 1920, 1080, 560, 420);
        foreach (var label in new[] { "Encoded size", "Capture limit: ≤120 FPS", "Received FPS", "Receiver canvas",
            "Rotation", "Video bitrate", "Decoded", "Skipped", "Network loss: N/A",
            "PCM buffer / target", "Device padding", "Inserted silence", "Discarded PCM", "(+N/A) ms",
            "A/V skew", "Peak observed skew", "Positive skew = video ahead", "not end-to-end latency",
            "Encoded queues: waiting packets", "1-Hz samples", "Control", "Generation", "Occluded: No" })
            Assert.Contains(label, text);
        Assert.EndsWith("Occluded: No", text);
    }

    [Fact]
    public void FirstSampleIsUnknownThenRatesUseActualDeltasAndIdleIsZero()
    {
        var sampler = new PlaybackRateSampler();
        Assert.Null(sampler.Sample(1000, Playback(), Receive()).PresentedFps);
        Assert.True(sampler.BaselineReset);
        var sample = sampler.Sample(2000, Playback(120) with { Skipped = 5 }, Receive(1_001_000, 120));
        Assert.False(sampler.BaselineReset);
        Assert.Equal(20, sample.ReceivedFps);
        Assert.Equal(20, sample.DecodedFps);
        Assert.Equal(20, sample.PresentedFps);
        Assert.Equal(8, sample.VideoMbps);
        Assert.Equal(20, sample.SkippedPercent);
        Assert.Equal(0, sampler.Sample(3000, Playback(120) with { Skipped = 5 }, Receive(1_001_000, 120)).PresentedFps);
    }

    [Fact]
    public void SessionEpochGenerationResetAndCounterRegressionResetAllBaselines()
    {
        foreach (var mode in Enumerable.Range(0, 7))
        {
            var sampler = new PlaybackRateSampler();
            sampler.Sample(1000, Playback(), Receive());
            var playback = mode switch
            {
                0 => Playback() with { Epoch = 2 },
                1 => Playback() with { Generation = 2 },
                2 => Playback(0),
                3 => Playback() with { Presented = 0 },
                _ => Playback()
            };
            var receive = mode switch { 4 => Receive() with { Session = 2 }, 5 => Receive(0), _ => Receive() };
            Assert.Null(sampler.Sample(mode == 6 ? 1000 : 2000, playback, receive).PresentedFps);
            Assert.True(sampler.BaselineReset);
        }
    }

    [Fact]
    public void FormulaHandlesBoundsZeroAndLargeCountersWithoutOverflow()
    {
        Assert.Null(PlaybackRateSampler.Rate(1, 0, 0));
        Assert.Null(PlaybackRateSampler.Rate(0, 1, 1000));
        Assert.Null(PlaybackRateSampler.Percent(0, 0));
        Assert.Null(PlaybackRateSampler.Percent(2, 1));
        Assert.Equal(25, PlaybackRateSampler.Percent(1, 4));
        Assert.True(double.IsFinite(PlaybackRateSampler.Rate(long.MaxValue, 0, 1)!.Value));
        Assert.Equal(8, PlaybackRateSampler.Rate(1_000_000, 0, 1000, .008));
        Assert.Equal(1, PlaybackRateSampler.Rate(long.MaxValue, long.MaxValue - 1, 1000));
        Assert.Equal(50, PlaybackRateSampler.SkippedPercent(long.MaxValue, long.MaxValue));
    }

    [Fact]
    public void AudioRecentCountersUseSampleFramesNotPacketsAndResetOnRegression()
    {
        var sampler = new PlaybackRateSampler();
        sampler.Sample(1000, Playback(), Receive());
        var next = Playback() with { Audio = Playback().Audio! with { SilenceFrames = 960, DiscardedFrames = 1440 } };
        var delta = sampler.Sample(2000, next, Receive());
        Assert.Equal(10, delta.SilenceMs);
        Assert.Equal(10, delta.DiscardedMs);
        var reset = sampler.Sample(3000, Playback(), Receive());
        Assert.Null(reset.SilenceMs);
        Assert.Null(reset.PresentedFps);
    }

    [Fact]
    public async Task QueueCountsOnlyMediaIncludingInFlightAndCalculatesPtsSpan()
    {
        using var queue = new ReceiveQueue();
        queue.Add(new(PacketType.VideoFrame, 1, 1, 1, 2000, 1, new byte[100]));
        queue.Add(new(PacketType.VideoFrame, 1, 1, 1, 52_000, 0, new byte[200]));
        var snapshot = queue.Snapshot(1);
        Assert.Equal(new EncodedQueueStatistics(2, 300, 50_000), snapshot);
        var packet = await queue.TakeAsync(1, default);
        Assert.Equal(snapshot, queue.Snapshot(1));
        Assert.Equal(new EncodedQueueStatistics(1, 200, 0), queue.WaitingSnapshot(1));
        queue.Complete(packet);
        Assert.Equal(new EncodedQueueStatistics(1, 200, 0), queue.Snapshot(1));
        Assert.Equal(new EncodedQueueStatistics(0, 0, 0), queue.Snapshot(2));
    }

    [Fact]
    public void ReceivePayloadCountersAndAgesDoNotInventUnreceivedAudio()
    {
        long clock = 100;
        var diagnostics = new ReceiveDiagnostics(() => clock);
        Assert.Null(diagnostics.Snapshot(2).LastPacketAgeMs);
        diagnostics.Received(new(PacketType.VideoFrame, 1, 1, 1, 10_000, 1, new byte[100]), 0);
        clock += 50;
        Assert.Equal(new TrackStatistics(1, 100, 50), diagnostics.Snapshot(1));
        Assert.Equal(new TrackStatistics(0, 0, null), diagnostics.Snapshot(2));
    }

    [Fact]
    public void ControlLatencyRequiresOurOwnMatchingNonceAndMonotonicTimestamp()
    {
        var latency = new ControlLatency();
        Assert.Null(latency.Snapshot(0).RttMs);
        var nonce = latency.Begin(100)!.Value;
        Assert.Null(latency.Begin(200));
        latency.Valid(250, nonce + 1);
        Assert.Null(latency.Snapshot(300).RttMs);
        latency.Valid(350, nonce);
        Assert.Equal(250, latency.Snapshot(400).RttMs);
        Assert.Equal(50, latency.Snapshot(400).AgeMs);
        var second = latency.Begin(500)!.Value;
        latency.Valid(450, second);
        Assert.Equal(250, latency.Snapshot(500).RttMs);
        Assert.NotNull(latency.Begin(10_500));
    }

    [Fact]
    public void TextLabelsUnitsSourceClockAndUnknownsHonestly()
    {
        var sampler = new PlaybackRateSampler();
        var p = Playback();
        var text = PlaybackStatisticsText.Format(new("h264", "raw", 120, "auto"), p, Receive(),
            sampler.Sample(1000, p, Receive()), 1920, 1080, 960, 540);
        Assert.Contains("Encoded size: 640 × 360 px", text);
        Assert.Contains("Capture limit: ≤120 FPS", text);
        Assert.Contains("Received FPS: N/A", text);
        Assert.Contains("100.0 / 50.0 ms", text);
        Assert.Contains("Device padding: 10.0 ms", text);
        Assert.Contains("Inserted silence: 10.0 (+N/A) ms", text);
        Assert.Contains("Discarded PCM: 20.0 (+N/A) ms", text);
        Assert.Contains("Network loss: N/A", text);
        Assert.Contains("not end-to-end latency", text);
        Assert.Contains("Control: — / — / N/A / N/A", text);
    }

    [Fact]
    public void OverlayIsSeparateVideoLayerDefaultHiddenAndNoninteractive()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var overlay = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "PlaybackStatsOverlay");
        var home = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "HomePage");
        Assert.Same(home.Parent, overlay.Parent);
        Assert.Equal("Collapsed", (string?)overlay.Attribute("Visibility"));
        Assert.Equal("False", (string?)overlay.Attribute("IsHitTestVisible"));
        Assert.DoesNotContain(overlay.Descendants(), e => e.Name.LocalName is "Button" or "CheckBox" or "TextBox");
        Assert.Equal("StackPanel", overlay.Elements().Single().Name.LocalName);
        Assert.DoesNotContain(overlay.Descendants(), e => e.Name.LocalName == "ScrollViewer");
    }
}
