using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using RedrainCast.Receive.Core;
using Windows.UI;
using Windows.UI.Text;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, TextBlock> statisticsValues = new();
    private readonly List<TextBlock> statisticsLabels = [];
    private readonly List<TextBlock> statisticsHeadings = [];
    private readonly List<Grid> statisticsGrids = [];
    private readonly List<Border> statisticsRules = [];
    private readonly List<TextBlock> statisticsKpiValues = [];
    private readonly List<TextBlock> statisticsTotalLabels = [];
    private TextBlock? statisticsClockNote;
    private TextBlock? statisticsLatencyNote;
    private string CompactStatisticsClockNote => L("StatisticsCompactClockNote");
    private readonly Dictionary<TextBlock, string> statisticsLabelKeys = [];
    private readonly StatisticsObservedPeak statisticsPeak = new();
    private StatisticsDisplay? statisticsDisplay;
    private Grid? statisticsFooter;
    private bool? statisticsLayoutCompact;
    private readonly List<Grid> statisticsFooterPairs = [];
    private static readonly Brush StatisticsWhite = new SolidColorBrush(Color.FromArgb(255, 239, 244, 252));
    private static readonly Brush StatisticsSecondary = new SolidColorBrush(Color.FromArgb(255, 184, 203, 229));
    private static readonly Brush StatisticsBlue = new SolidColorBrush(Color.FromArgb(255, 112, 199, 255));
    private static readonly Brush StatisticsAmber = new SolidColorBrush(Color.FromArgb(255, 255, 190, 69));

    private TextBlock StatisticsText(string text, string? id = null, bool heading = false, string? key = null)
    {
        var cell = new TextBlock
        {
            Text = key is null ? text : L(key), FontFamily = new("Segoe UI"), Foreground = id is null ? StatisticsSecondary : StatisticsWhite,
            IsHitTestVisible = false, IsTextSelectionEnabled = false, TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None, VerticalAlignment = VerticalAlignment.Center,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        Typography.SetNumeralAlignment(cell, FontNumeralAlignment.Tabular);
        if (id is not null)
        {
            statisticsValues.Add(id, cell);
            cell.FlowDirection = FlowDirection.LeftToRight;
            cell.TextAlignment = TextAlignment.Right;
            cell.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else statisticsLabels.Add(cell);
        if (key is not null) statisticsLabelKeys[cell] = key;
        if (heading) { cell.FontWeight = FontWeights.SemiBold; statisticsHeadings.Add(cell); }
        return cell;
    }

    private Grid StatisticsGrid(params GridLength[] columns)
    {
        var grid = new Grid();
        foreach (var column in columns) grid.ColumnDefinitions.Add(new() { Width = column });
        statisticsGrids.Add(grid);
        return grid;
    }

    private static void StatisticsPlace(Grid grid, FrameworkElement child, int row, int column)
    {
        while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(child, row); Grid.SetColumn(child, column); grid.Children.Add(child);
    }

    private void StatisticsRule()
    {
        var rule = new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(180, 111, 137, 163)) };
        statisticsRules.Add(rule);
        PlaybackStatsPanel.Children.Add(rule);
    }

    private void EnsureStatisticsLayout(StatisticsDisplay display)
    {
        if (statisticsValues.Count != 0) return;
        var star = new GridLength(1, GridUnitType.Star);
        PlaybackStatsPanel.Children.Add(StatisticsText(PlaybackStatisticsDisplay.Title, heading: true, key: "StatisticsTitle"));
        StatisticsRule();
        var kpis = StatisticsGrid(star, star, star, star);
        for (var i = 0; i < display.Kpis.Count; i++)
        {
            var kpi = display.Kpis[i];
            StatisticsPlace(kpis, StatisticsText(kpi.Field.Label, key: "Statistics_" + kpi.Field.Id), 0, i);
            var value = StatisticsText("", kpi.Field.Id);
            value.TextAlignment = TextAlignment.Left;
            statisticsKpiValues.Add(value);
            StatisticsPlace(kpis, value, 1, i);
        }
        PlaybackStatsPanel.Children.Add(kpis);
        var totals = StatisticsGrid(star, star, star);
        for (var i = 0; i < display.Totals.Count; i++)
        {
            var total = display.Totals[i];
            var label = StatisticsText(total.Field.Label, key: "Statistics_" + total.Field.Id);
            statisticsTotalLabels.Add(label);
            StatisticsPlace(totals, label, 0, i);
            var value = StatisticsText("", total.Field.Id);
            value.TextAlignment = TextAlignment.Left;
            StatisticsPlace(totals, value, 1, i);
        }
        PlaybackStatsPanel.Children.Add(totals);
        var presentation = StatisticsGrid(star, star, star);
        var presentationCells = display.Groups.SelectMany(g => g.Cells)
            .Where(c => PlaybackStatisticsDisplay.VideoPresentationFields.Contains(c.Field)).ToArray();
        for (var i = 0; i < presentationCells.Length; i++)
        {
            var cell = presentationCells[i];
            var label = StatisticsText(cell.Field.Label, key: "Statistics_" + cell.Field.Id);
            var value = StatisticsText("", cell.Field.Id);
            label.TextWrapping = value.TextWrapping = TextWrapping.Wrap;
            label.HorizontalAlignment = value.HorizontalAlignment = HorizontalAlignment.Stretch;
            value.TextAlignment = TextAlignment.Left;
            StatisticsPlace(presentation, label, 0, i);
            StatisticsPlace(presentation, value, 1, i);
        }
        PlaybackStatsPanel.Children.Add(presentation);
        StatisticsRule();
        var body = StatisticsGrid(star, star);
        for (var i = 0; i < display.Groups.Count; i++)
        {
            var group = display.Groups[i];
            var fields = StatisticsGrid(star, GridLength.Auto);
            var title = StatisticsText(group.Title, heading: true, key: group.Id == "video" ? "StatisticsVideo" : "StatisticsAudioSync");
            StatisticsPlace(fields, title, 0, 0); Grid.SetColumnSpan(title, 2);
            var bodyCells = group.Cells.Where(c => !PlaybackStatisticsDisplay.VideoPresentationFields.Contains(c.Field)).ToArray();
            for (var row = 0; row < bodyCells.Length; row++)
            {
                var cell = bodyCells[row];
                StatisticsPlace(fields, StatisticsText(cell.Field.Label, key: "Statistics_" + cell.Field.Id), row + 1, 0);
                StatisticsPlace(fields, StatisticsText("", cell.Field.Id), row + 1, 1);
            }
            StatisticsPlace(body, fields, 0, i);
        }
        PlaybackStatsPanel.Children.Add(body);
        statisticsClockNote = StatisticsText("", key: "StatisticsClockNote");
        statisticsLatencyNote = StatisticsText("", key: "StatisticsLatencyNote");
        PlaybackStatsPanel.Children.Add(statisticsClockNote);
        PlaybackStatsPanel.Children.Add(statisticsLatencyNote);
        StatisticsRule();
        PlaybackStatsPanel.Children.Add(StatisticsText(PlaybackStatisticsDisplay.ConnectionsTitle, heading: true, key: "StatisticsConnections"));
        var table = StatisticsGrid(new(0.8, GridUnitType.Star), new(1.8, GridUnitType.Star),
            new(1, GridUnitType.Star), new(1.2, GridUnitType.Star), new(0.8, GridUnitType.Star));
        for (var column = 0; column < PlaybackStatisticsDisplay.ConnectionColumns.Length; column++)
        {
            var header = StatisticsText(PlaybackStatisticsDisplay.ConnectionColumns[column],
                key: new[] { "StatisticsColumnChannel", "StatisticsColumnQueued", "StatisticsColumnSpan", "StatisticsColumnLastActivity", "StatisticsColumnRTT" }[column]);
            header.TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Right;
            StatisticsPlace(table, header, 0, column);
        }
        for (var row = 0; row < display.Connections.Count; row++)
        {
            StatisticsPlace(table, StatisticsText(display.Connections[row].Channel,
                key: new[] { "ChannelVideo", "ChannelAudio", "ChannelControl" }[row]), row + 1, 0);
            for (var column = 1; column < 5; column++)
                StatisticsPlace(table, StatisticsText("", $"connection.{row}.{column}"), row + 1, column);
        }
        PlaybackStatsPanel.Children.Add(table);
        PlaybackStatsPanel.Children.Add(StatisticsText("", key: "StatisticsQueueNote"));
        StatisticsRule();
        var footer = StatisticsGrid(GridLength.Auto, star, star, star);
        statisticsFooter = footer;
        for (var i = 0; i < display.Footer.Count; i++)
        {
            var cell = display.Footer[i];
            var pair = StatisticsGrid(GridLength.Auto, GridLength.Auto);
            statisticsFooterPairs.Add(pair);
            if (i != 0) StatisticsPlace(pair, StatisticsText(cell.Field.Label, key: "Statistics_" + cell.Field.Id), 0, 0);
            StatisticsPlace(pair, StatisticsText("", cell.Field.Id), 0, 1);
            StatisticsPlace(footer, pair, 0, i);
        }

        PlaybackStatsPanel.Children.Add(footer);
    }

    private void RefreshStatisticsLanguage()
    {
        foreach (var (label, key) in statisticsLabelKeys) label.Text = L(key);
        if (statisticsPresentation is { } cached)
            ApplyStatisticsDisplay(PlaybackStatisticsDisplay.Create(cached.Frame.Request, cached.Frame.Playback,
                cached.Frame.Receive, cached.Rates, cached.CanvasWidth, cached.CanvasHeight, cached.Peak,
                CompactStatistics, key => L(key), strings.Culture));
    }

    private void ApplyStatisticsDisplay(StatisticsDisplay display)
    {
        EnsureStatisticsLayout(display);
        statisticsDisplay = display;
        statisticsCompact = CompactStatistics;
        foreach (var cell in display.Kpis.Concat(display.Totals).Concat(display.Groups.SelectMany(g => g.Cells)).Concat(display.Footer))
        {
            var control = statisticsValues[cell.Field.Id];
            control.Text = cell.Value;
            control.Foreground = cell.Field.Id == "recentSkippedPercent" ?
                cell.Highlight ? StatisticsAmber : StatisticsWhite :
                display.Kpis.Contains(cell) ? StatisticsBlue : StatisticsWhite;
        }
        for (var row = 0; row < display.Connections.Count; row++)
        {
            var c = display.Connections[row];
            var values = new[] { c.Queued, c.Span, c.LastActivity, c.Rtt };
            for (var column = 1; column < 5; column++) statisticsValues[$"connection.{row}.{column}"].Text = values[column - 1];
        }
        FitStatisticsPanel();
        PlaybackStatsOverlay.Visibility = Visibility.Visible;
    }
}
