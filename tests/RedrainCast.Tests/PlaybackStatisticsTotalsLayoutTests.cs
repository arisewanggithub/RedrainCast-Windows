using RedrainCast.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class PlaybackStatisticsTotalsLayoutTests
{
    private static string Source(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", file + ".cs.txt"));

    [Fact]
    public void TotalsUseThreeEqualColumnsWithLabelsAboveLeftAlignedCounts()
    {
        var source = Source("PlaybackStatisticsLayout");
        var begin = source.IndexOf("var totals = StatisticsGrid(star, star, star);", StringComparison.Ordinal);
        var end = source.IndexOf("PlaybackStatsPanel.Children.Add(totals);", begin, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin);
        var totals = source[begin..end];
        Assert.DoesNotContain("var pair", totals);
        Assert.Contains("StatisticsPlace(totals, label, 0, i);", totals);
        Assert.Contains("StatisticsPlace(totals, value, 1, i);", totals);
        Assert.Contains("value.TextAlignment = TextAlignment.Left;", totals);
        Assert.Contains("StatisticsText(\"\", total.Field.Id)", totals);
    }

    [Fact]
    public void CompactMergedClockNoteRetainsSemanticsWithoutReducingFontOrDroppingMetrics()
    {
        var layout = Source("PlaybackStatisticsLayout");
        Assert.Contains("CompactStatisticsClockNote => L(\"StatisticsCompactClockNote\")", layout);
        var compactNote = EnglishCatalog.Get("StatisticsCompactClockNote");
        foreach (var meaning in new[] { "Source PTS", "+ = video ahead", "1 Hz peak", "not end-to-end latency" })
            Assert.Contains(meaning, compactNote);
        var overlay = Source("PlaybackStatisticsOverlay");
        Assert.Contains("statisticsLatencyNote.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;", overlay);
        Assert.Contains("text.FontSize = compact ? 14 : 16;", overlay);
        Assert.Contains("L(\"StatisticsClockNote\")", overlay);
        Assert.Equal("Positive skew = video ahead · Peak observed skew uses 1-Hz samples.", EnglishCatalog.Get("StatisticsClockNote"));
        Assert.Contains("key: \"StatisticsQueueNote\"", layout);
        Assert.StartsWith("Encoded queues: waiting packets", EnglishCatalog.Get("StatisticsQueueNote"));
        Assert.Contains("display.Groups", layout);
        Assert.Contains("display.Footer", layout);
    }
}
