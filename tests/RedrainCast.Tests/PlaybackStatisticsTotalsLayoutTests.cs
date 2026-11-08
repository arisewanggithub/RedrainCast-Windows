using RedrainCast.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class PlaybackStatisticsTotalsLayoutTests
{
    private static string Source(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", file + ".cs.txt"));

    [Fact]
    public void PresentationStripUsesThreeBoundedWrappingColumnsWithoutGrowingTheEightRowBody()
    {
        var layout = Source("PlaybackStatisticsLayout");
        var begin = layout.IndexOf("var presentation = StatisticsGrid(star, star, star);", StringComparison.Ordinal);
        var end = layout.IndexOf("PlaybackStatsPanel.Children.Add(presentation);", begin, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin);
        var strip = layout[begin..end];
        Assert.Contains("PlaybackStatisticsDisplay.VideoPresentationFields.Contains(c.Field)", strip);
        Assert.Contains("label.TextWrapping = value.TextWrapping = TextWrapping.Wrap;", strip);
        Assert.Contains("label.HorizontalAlignment = value.HorizontalAlignment = HorizontalAlignment.Stretch;", strip);
        Assert.Contains("StatisticsPlace(presentation, label, 0, i);", strip);
        Assert.Contains("StatisticsPlace(presentation, value, 1, i);", strip);
        Assert.Contains("value.TextAlignment = TextAlignment.Left;", strip);
        Assert.DoesNotContain("GridLength.Auto", strip);
        Assert.Contains("group.Cells.Where(c => !PlaybackStatisticsDisplay.VideoPresentationFields.Contains(c.Field))", layout);
        Assert.Equal(3, RedrainCast.Receive.Core.PlaybackStatisticsDisplay.VideoPresentationFields.Length);
        Assert.Equal(8, RedrainCast.Receive.Core.PlaybackStatisticsDisplay.VideoFields.Length - 3);
    }

    [Fact]
    public void PresentationCacheIsWrittenOnlyAfterSuccessfulSubmissionAndResetOnConfiguration()
    {
        var presenter = Source("GpuPresenter");
        var submitted = presenter.IndexOf("presentationSlot.Submitted();", StringComparison.Ordinal);
        var returned = presenter.IndexOf("return true;", submitted, StringComparison.Ordinal);
        var success = presenter.LastIndexOf("result.CheckError();", submitted, StringComparison.Ordinal);
        Assert.True(success >= 0 && success < submitted && submitted < returned);
        var requests = Source("PresentationRequestState");
        var render = requests.IndexOf("if (!render(request)) return false;", StringComparison.Ordinal);
        var observed = requests.IndexOf("statistics.Presented(format, request.Display.NativeHdr);", StringComparison.Ordinal);
        Assert.True(render >= 0 && observed > render);
        Assert.Contains("var request = Desired;", requests);
        Assert.Contains("Volatile.Write(ref successful, request);", requests);
        var policyBegin = presenter.IndexOf("public bool SetDisplayPolicy", StringComparison.Ordinal);
        var policyEnd = presenter.IndexOf("public void Prepare", policyBegin, StringComparison.Ordinal);
        Assert.DoesNotContain("Presented", presenter[policyBegin..policyEnd]);
        var playback = Source("VideoPlayback");
        Assert.Contains("presenter.ResetStatistics();", playback);
        Assert.Contains("Volatile.Write(ref statisticsVideo, null);", playback);
        Assert.Contains("Volatile.Write(ref statisticsVideo, frame.Statistics);", playback);
        Assert.Contains("Volatile.Read(ref statisticsVideo), presenter.Statistics", playback);
        var tick = Source("PlaybackStatisticsOverlay");
        Assert.DoesNotContain("HdrDisplayPolicy.Query", tick);
        Assert.DoesNotContain("avcodec", tick);
        Assert.DoesNotContain("av_pix_fmt", tick);
        Assert.Contains("cached.Frame.Playback", tick);
    }

    [Fact]
    public void DecoderStatisticsUseActualCodecContextTransferAndSurfaceComponentDepth()
    {
        var decoder = Source("HardwareDecoder");
        Assert.Contains("ffmpeg.avcodec_profile_name(context->codec_id, context->profile)", decoder);
        Assert.Contains("ffmpeg.av_pix_fmt_desc_get(pool->sw_format)", decoder);
        Assert.Contains("descriptor->comp[0].depth", decoder);
        Assert.Contains("VideoTransfer? transfer = frame->color_trc switch", decoder);
        Assert.Contains("Statistics = new(ColorFormat, transfer, codecProfile, depth);", decoder);
        Assert.Contains("new HardwareFrame(copy, ColorFormat, Statistics.CodecProfile)", decoder);
        Assert.DoesNotContain("color.Hdr10 ? 10 : 8", decoder);
        var report = Source("ReportJson");
        Assert.Contains("[JsonSerializable(typeof(PlaybackStatistics))]", report);
        Assert.Contains("ReportJsonContext.Default.PlaybackStatistics", report);
        var harness = Source("PlaybackStatisticsSelfTest");
        foreach (var phase in new[] { "cached-monitor-output-change", "sdr-stream-on-hdr-display", "unknown-video-presentation" })
            Assert.Contains(phase, harness);
        Assert.Contains("text.TextWrapping == TextWrapping.Wrap ? slot.Width", harness);
    }
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
