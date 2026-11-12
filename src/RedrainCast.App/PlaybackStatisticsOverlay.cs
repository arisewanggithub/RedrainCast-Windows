using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RedrainCast.Receive.Core;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    private sealed record StatisticsFrame(CastRequest Request, PlaybackStatistics Playback, ReceiveStatistics Receive);
    private sealed record StatisticsPresentation(StatisticsFrame Frame, PlaybackRates Rates, int CanvasWidth,
        int CanvasHeight, double? Peak);
    private StatisticsPresentation? statisticsPresentation;
    private bool? statisticsCompact;
    private Func<StatisticsFrame?>? isolatedStatisticsProvider;
    private bool statisticsFailed;
    private void ClearStatistics()
    {
        statisticsTimer.Stop();
        statisticsRates.Reset();
        statisticsPeak.Reset();
        statisticsDisplay = null;
        statisticsPresentation = null;
        statisticsCompact = null;
        PlaybackStatsOverlay.Visibility = Visibility.Collapsed;
        foreach (var cell in statisticsValues.Values) cell.Text = "";
    }

    private void UpdateStatisticsVisibility()
    {
        var enabled = !closing && (isolatedStatisticsProvider is not null || roles.Role == RedrainCast.Core.CastRole.Receiving &&
            WindowRoot.XamlRoot?.IsHostVisible == true && video.StatisticsRequest is not null &&
            video.HasDecodedVideo) && !statisticsFailed;
        if (!enabled)
        {
            ClearStatistics();
            if (video.StatisticsRequest is null) statisticsFailed = false;
            return;
        }
        if (!statisticsTimer.IsEnabled)
        {
            statisticsRates.Reset();
            statisticsTimer.Start();
            StatisticsTick(null, null!);
        }
    }

    private void FitStatisticsPanel()
    {
        if (statisticsPresentation is { } cached && statisticsCompact != CompactStatistics)
        {
            ApplyStatisticsDisplay(PlaybackStatisticsDisplay.Create(cached.Frame.Request, cached.Frame.Playback,
                cached.Frame.Receive, cached.Rates, cached.CanvasWidth, cached.CanvasHeight, cached.Peak, CompactStatistics,
                key => L(key), strings.Culture));
            return;
        }
        PlaybackStatsOverlay.MaxWidth = Math.Max(1, Math.Min(960, WindowRoot.ActualWidth - 24));
        PlaybackStatsOverlay.Width = PlaybackStatsOverlay.MaxWidth;
        PlaybackStatsOverlay.MaxHeight = Math.Max(1, WindowRoot.ActualHeight - 24);
        var compact = CompactStatistics;
        if (statisticsClockNote is not null)
            statisticsClockNote.Text = compact ? CompactStatisticsClockNote :
                L("StatisticsClockNote");
        if (statisticsLatencyNote is not null)
            statisticsLatencyNote.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PlaybackStatsOverlay.Padding = new(compact ? 6 : 12);
        foreach (var text in statisticsLabels.Concat(statisticsValues.Values))
        {
            text.FontSize = compact ? 14 : 16;
            text.LineHeight = compact ? 16 : 20;
        }
        foreach (var heading in statisticsHeadings) heading.FontSize = compact ? 14 : 16;
        foreach (var kpi in statisticsKpiValues)
        {
            kpi.FontSize = compact ? 20 : 28;
            kpi.LineHeight = compact ? 24 : 34;
        }
        foreach (var grid in statisticsGrids)
        {
            grid.ColumnSpacing = compact ? 4 : 12;
            grid.RowSpacing = 0;
        }
        foreach (var rule in statisticsRules) rule.Margin = new(0, compact ? 0 : 3, 0, compact ? 0 : 3);
        if (statisticsFooter is { } footer && statisticsLayoutCompact != compact)
        {
            statisticsLayoutCompact = compact;
            footer.ColumnDefinitions.Clear();
            foreach (var weight in compact ? new[] { 1.0, 1.0 } : new[] { 0.6, 1.4, 2.2, 1.0 })
                footer.ColumnDefinitions.Add(new() { Width = new(weight, GridUnitType.Star) });
            footer.RowDefinitions.Clear();
            footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
            if (compact) footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (var i = 0; i < statisticsFooterPairs.Count; i++)
            {
                Grid.SetRow(statisticsFooterPairs[i], compact ? i / 2 : 0);
                Grid.SetColumn(statisticsFooterPairs[i], compact ? i % 2 : i);
            }
        }
    }

    private bool CompactStatistics => WindowRoot.ActualWidth < 800 || WindowRoot.ActualHeight < 600;

    private void StatisticsTick(object? sender, object args)
    {
        if (closing || isolatedStatisticsProvider is null &&
            (roles.Role != RedrainCast.Core.CastRole.Receiving || video.StatisticsRequest is null))
        {
            ClearStatistics();
            return;
        }
        try
        {
            var frame = isolatedStatisticsProvider?.Invoke();
            var request = frame?.Request ?? video.StatisticsRequest;
            var playback = frame?.Playback ?? video.Statistics;
            var receive = frame?.Receive ?? receiver?.Statistics;
            if (request is null || playback is null || receive is null ||
                receive.Recovery is { } recovery && recovery.ActiveGeneration != playback.Generation)
            {
                ClearStatistics();
                return;
            }
            var sampledAtMs = Environment.TickCount64;
            var rates = statisticsRates.Sample(sampledAtMs, playback, receive);
            if (isolatedStatisticsProvider is null)
                video.RecordStatisticsSample(receive.Session, playback, sampledAtMs, statisticsRates.BaselineReset);
            var peak = statisticsPeak.Sample(playback, receive, statisticsRates.BaselineReset);
            var canvasWidth = (int)Math.Ceiling(VideoSurface.ActualWidth * VideoSurface.CompositionScaleX);
            var canvasHeight = (int)Math.Ceiling(VideoSurface.ActualHeight * VideoSurface.CompositionScaleY);
            statisticsPresentation = new(new(request, playback, receive), rates, canvasWidth, canvasHeight, peak);
            ApplyStatisticsDisplay(PlaybackStatisticsDisplay.Create(request, playback, receive, rates,
                canvasWidth, canvasHeight, peak, CompactStatistics, key => L(key), strings.Culture));
        }
        catch (Exception error)
        {
            statisticsFailed = true;
            ClearStatistics();
            ReportAction(error, "StatisticsUnavailable");
        }
    }
}
