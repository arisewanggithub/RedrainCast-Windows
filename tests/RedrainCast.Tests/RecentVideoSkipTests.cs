using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class RecentVideoSkipTests
{
    [Fact]
    public void ProductionOverlayRecordsTheSameSampleTimeCountersAndResetUsedForDisplay()
    {
        var overlay = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "PlaybackStatisticsOverlay.cs.txt"));
        Assert.Contains("var sampledAtMs = Environment.TickCount64;", overlay);
        Assert.Contains("statisticsRates.Sample(sampledAtMs, playback, receive)", overlay);
        Assert.Contains("if (isolatedStatisticsProvider is null)", overlay);
        Assert.Contains("video.RecordStatisticsSample(receive.Session, playback, sampledAtMs, statisticsRates.BaselineReset)", overlay);
        var sample = overlay.IndexOf("statisticsRates.Sample(sampledAtMs", StringComparison.Ordinal);
        var record = overlay.IndexOf("video.RecordStatisticsSample(", StringComparison.Ordinal);
        var display = overlay.IndexOf("ApplyStatisticsDisplay(", sample, StringComparison.Ordinal);
        Assert.True(sample < record && record < display);
    }

    private static PlaybackStatistics Playback(long presented, long skipped, long decoded = 0) =>
        new(1, 1, decoded, decoded, presented, skipped, 640, 360, 0, false, null, null, 0);

    private static ReceiveStatistics Receive(long session = 1) =>
        new(session, new(0, 0, null), new(0, 0, null), new(0, 0, 0), new(0, 0, 0), null, null, null);

    [Theory]
    [InlineData(1000)]
    [InlineData(1600)]
    public void FortyPercentBurstUsesTerminalDeltasNotLifetimeOrDecodedTotals(long interval)
    {
        var sampler = new PlaybackRateSampler();
        sampler.Sample(1000, Playback(1000, 20, 2000), Receive());
        var sample = sampler.Sample(1000 + interval, Playback(1030, 40, 2100), Receive());
        Assert.False(sampler.BaselineReset);
        Assert.Equal(40, sample.SkippedPercent);
        Assert.Equal(30 * 1000.0 / interval, sample.PresentedFps);
        Assert.NotEqual(PlaybackRateSampler.SkippedPercent(1030, 40), sample.SkippedPercent);
    }

    [Fact]
    public void IdleThenSkippedOnlyIntervalIsUnknownThenOneHundredPercent()
    {
        var sampler = new PlaybackRateSampler();
        sampler.Sample(1000, Playback(10, 0), Receive());
        Assert.Null(sampler.Sample(2000, Playback(10, 0), Receive()).SkippedPercent);
        Assert.Equal(100, sampler.Sample(3000, Playback(10, 1), Receive()).SkippedPercent);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("generation")]
    [InlineData("session")]
    [InlineData("skipped_regression")]
    [InlineData("presented_regression")]
    [InlineData("clock_regression")]
    [InlineData("explicit_reset")]
    public void BoundarySampleCannotMixOldCountersIntoRecentPercent(string boundary)
    {
        var sampler = new PlaybackRateSampler();
        sampler.Sample(1000, Playback(1000, 100), Receive());
        var next = Playback(1030, 120);
        var receive = Receive();
        var at = 2000L;
        switch (boundary)
        {
            case "epoch": next = next with { Epoch = 2 }; break;
            case "generation": next = next with { Generation = 2 }; break;
            case "session": receive = Receive(2); break;
            case "skipped_regression": next = next with { Skipped = 0 }; break;
            case "presented_regression": next = next with { Presented = 0 }; break;
            case "clock_regression": at = 999; break;
            case "explicit_reset": sampler.Reset(); break;
        }
        Assert.Null(sampler.Sample(at, next, receive).SkippedPercent);
        Assert.True(sampler.BaselineReset);
        var after = next with { Presented = next.Presented + 30, Skipped = next.Skipped + 20 };
        Assert.Equal(40, sampler.Sample(at + 1000, after, receive).SkippedPercent);
        Assert.False(sampler.BaselineReset);
    }
}
