using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using Windows.Foundation;
using Windows.UI.Text;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    internal async Task RunPlaybackStatisticsSelfTestAsync(string reportFile)
    {
        var checks = new List<JsonNode>();
        string? failure = null;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            if (!isolated || source is not null || video.IsAttached)
                throw new InvalidOperationException("Statistics UI proof requires a fresh isolated window without media resources.");
            await CheckProductionPreferredLanguagesAsync(checks, limit.Token);
            strings.Stop();
            strings.Start(DispatcherQueue, error => throw new IOException("English statistics resource lookup failed.", error),
                () => new[] { "en-US" });
            while (WindowRoot.XamlRoot is null || WindowRoot.ActualHeight <= 0) await Task.Delay(50, limit.Token);
            if (PlaybackStatsOverlay.Visibility != Visibility.Collapsed || statisticsTimer.IsEnabled)
                throw new InvalidOperationException("Statistics are not default-off.");
            checks.Add(JsonData.Object(("phase", "default-off"), ("visible", false), ("timer", false)));
            HomePage.Visibility = Visibility.Collapsed;
            var request = new CastRequest("h265", "raw", 240, "auto", new ReceiverOptions(true), 8_000_000);
            var playback = new PlaybackStatistics(long.MaxValue, uint.MaxValue, long.MaxValue, long.MaxValue - 1, long.MaxValue - 2,
                long.MaxValue - 3, 3840, 2160, 270, false,
                new(int.MaxValue, int.MaxValue, uint.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue),
                new(uint.MaxValue, 1_000_000, 900_000, 48000, 100_000, 1), 123456789,
                new(new(true), VideoTransfer.Pq, "Main 10", 10),
                VideoPresentationStatistics.ForStream(new(true), false));
            var receive = new ReceiveStatistics(7, new(123456789, 1234567890123, 12345), new(123456789, 123456789012, 12345),
                new(512, 8388608, 500000), new(512, 8388608, 500000), 1234.5, 12345, new(7, uint.MaxValue, uint.MaxValue, false),
                VideoWaitingQueue: new(511, 8388508, 500000), AudioWaitingQueue: new(511, 8388508, 500000));
            var samples = 0;
            isolatedStatisticsProvider = () => { samples++; return new(request, playback, receive); };
            string Value(string id) => statisticsValues[id].Text;
            void CheckLayout(string phase)
            {
                PlaybackStatsOverlay.UpdateLayout();
                var cellChecks = new List<JsonNode>();
                foreach (var text in statisticsLabels.Concat(statisticsValues.Values))
                {
                    if (text.Visibility == Visibility.Collapsed)
                    {
                        cellChecks.Add(JsonData.Object(("text", text.Text), ("visible", false), ("clipped", false),
                            ("reason", "Compact clock note merged without dropping its semantics.")));
                        continue;
                    }
                    var end = text.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                    var slot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(text);
                    var natural = new TextBlock
                    {
                        Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
                        FontWeight = text.FontWeight, LineHeight = text.LineHeight,
                        LineStackingStrategy = text.LineStackingStrategy, TextWrapping = text.TextWrapping
                    };
                    Typography.SetNumeralAlignment(natural, FontNumeralAlignment.Tabular);
                    natural.Measure(new(text.TextWrapping == TextWrapping.Wrap ? slot.Width : double.PositiveInfinity,
                        double.PositiveInfinity));
                    var origin = text.TransformToVisual(PlaybackStatsOverlay).TransformPoint(new Point());
                    var parent = (FrameworkElement)text.Parent;
                    var parentOrigin = parent.TransformToVisual(PlaybackStatsOverlay).TransformPoint(new Point());
                    var id = statisticsValues.FirstOrDefault(p => ReferenceEquals(p.Value, text)).Key;
                    var clipped = text.IsTextTrimmed || natural.DesiredSize.Width > slot.Width + 1 ||
                        natural.DesiredSize.Height > slot.Height + 1 || end.Bottom > slot.Height + 1 ||
                        end.Right > slot.Width + 1 ||
                        origin.X < -1 || origin.Y < -1 ||
                        origin.X + end.Right > PlaybackStatsOverlay.ActualWidth + 1 ||
                        origin.Y + end.Bottom > PlaybackStatsOverlay.ActualHeight + 1 ||
                        origin.X + end.Right > parentOrigin.X + parent.ActualWidth + 1;
                    cellChecks.Add(JsonData.Object(
                        ("id", id), ("text", text.Text), ("visible", true), ("x", origin.X), ("y", origin.Y), ("width", text.ActualWidth),
                        ("height", text.ActualHeight), ("desiredWidth", natural.DesiredSize.Width),
                        ("assignedWidth", slot.Width), ("assignedHeight", slot.Height),
                        ("desiredHeight", natural.DesiredSize.Height), ("endRight", end.Right), ("endBottom", end.Bottom),
                        ("fontSizeDip", text.FontSize), ("textTrimmed", text.IsTextTrimmed), ("clipped", clipped),
                        ("alignment", text.TextAlignment.ToString())));
                    if (clipped || text.FontSize < 14 || text.IsHitTestVisible || text.IsTextSelectionEnabled)
                    {
                        checks.Add(JsonData.Object(("phase", phase), ("cells", JsonData.Value(cellChecks))));
                        throw new InvalidOperationException($"Statistics cell clipped or intercepted input: {id ?? text.Text}; " +
                            $"desired {natural.DesiredSize}, actual {text.ActualWidth}×{text.ActualHeight}, at {origin}.");
                    }
                }
                var totalChecks = new List<JsonNode>();
                double? firstColumnWidth = null;
                for (var i = 0; i < statisticsTotalLabels.Count; i++)
                {
                    var label = statisticsTotalLabels[i];
                    var id = statisticsDisplay!.Totals[i].Field.Id;
                    var value = statisticsValues[id];
                    var labelOrigin = label.TransformToVisual(PlaybackStatsOverlay).TransformPoint(new Point());
                    var valueOrigin = value.TransformToVisual(PlaybackStatsOverlay).TransformPoint(new Point());
                    var labelSlot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(label);
                    var valueSlot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(value);
                    var valueEnd = value.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                    var nextLabelX = i + 1 < statisticsTotalLabels.Count
                        ? statisticsTotalLabels[i + 1].TransformToVisual(PlaybackStatsOverlay).TransformPoint(new Point()).X
                        : (double?)null;
                    var gapToNextLabel = nextLabelX - (valueOrigin.X + valueEnd.Right);
                    firstColumnWidth ??= labelSlot.Width;
                    var valid = labelOrigin.Y < valueOrigin.Y && labelOrigin.Y + labelSlot.Height <= valueOrigin.Y + 1 &&
                        Math.Abs(labelOrigin.X - valueOrigin.X) <= 1 &&
                        Math.Abs(labelSlot.Width - valueSlot.Width) <= 1 &&
                        Math.Abs(firstColumnWidth.Value - labelSlot.Width) <= 1 &&
                        value.TextAlignment == TextAlignment.Left &&
                        (gapToNextLabel is null || gapToNextLabel >= (CompactStatistics ? 4 : 12) - 1);
                    totalChecks.Add(JsonData.Object(("id", id), ("label", label.Text), ("value", value.Text), ("labelX", labelOrigin.X),
                        ("labelY", labelOrigin.Y), ("labelHeight", labelSlot.Height), ("valueX", valueOrigin.X),
                        ("valueY", valueOrigin.Y), ("columnWidth", labelSlot.Width), ("valueColumnWidth", valueSlot.Width),
                        ("nextLabelX", nextLabelX), ("gapToNextLabel", gapToNextLabel), ("fontSizeDip", value.FontSize), ("valid", valid)));
                    if (!valid)
                    {
                        checks.Add(JsonData.Object(("phase", phase), ("cells", JsonData.Value(cellChecks)), ("totals", JsonData.Value(totalChecks))));
                        throw new InvalidOperationException($"Total label/value arrangement or equal-column separation failed: {id}.");
                    }
                }
                checks.Add(JsonData.Object(
                    ("phase", phase), ("scale", WindowRoot.XamlRoot.RasterizationScale),
                    ("actualClientWidthDip", WindowRoot.ActualWidth), ("actualClientHeightDip", WindowRoot.ActualHeight),
                    ("overlayWidth", PlaybackStatsOverlay.ActualWidth), ("overlayHeight", PlaybackStatsOverlay.ActualHeight),
                    ("normalFontSizeDip", CompactStatistics ? 14 : 16), ("timer", statisticsTimer.IsEnabled),
                    ("intervalMs", statisticsTimer.Interval.TotalMilliseconds), ("snapshotsCollected", samples),
                    ("interceptsInput", PlaybackStatsOverlay.IsHitTestVisible), ("cells", JsonData.Value(cellChecks)), ("totals", JsonData.Value(totalChecks)),
                    ("compactClockNoteMerged", CompactStatistics)));
                if (PlaybackStatsOverlay.ActualWidth > WindowRoot.ActualWidth - 24 + 1 ||
                    PlaybackStatsOverlay.ActualHeight > WindowRoot.ActualHeight - 24 + 1 ||
                    !statisticsValues.ContainsKey("occluded") || Value("occluded") == "" ||
                    statisticsDisplay?.Groups.Count != 2 ||
                    statisticsDisplay.Groups[0].Cells.Count != PlaybackStatisticsDisplay.VideoFields.Length ||
                    statisticsDisplay.Groups[1].Cells.Count != PlaybackStatisticsDisplay.AudioFields.Length)
                    throw new InvalidOperationException("Statistics lost readable viewport bounds, a shared category or the footer.");
            }
            foreach (var size in new[] { (Width: 520, Height: 420), (Width: 560, Height: 420), (Width: 800, Height: 600), (Width: 1100, Height: 760) })
            {
                var scale = WindowRoot.XamlRoot.RasterizationScale;
                AppWindow.ResizeClient(new((int)(size.Width * scale), (int)(size.Height * scale)));
                await Task.Delay(200, limit.Token);
                UpdateStatisticsVisibility();
                CheckLayout("first-sample-large-counters");
                var previousSamples = samples;
                while (samples <= previousSamples) await Task.Delay(100, limit.Token);
                if (!statisticsTimer.IsEnabled || PlaybackStatsOverlay.Visibility != Visibility.Visible ||
                    PlaybackStatsOverlay.IsHitTestVisible || Value("encodedSize") != "3840 × 2160 px")
                    throw new InvalidOperationException("Statistics lost visibility, typed metrics or non-interception.");
                CheckLayout("one-second-static-zero-not-unhealthy");
                var rates = new PlaybackRates(239.9, 239.8, 228.7, 123.4, 1.5, 25.0, 1234567, 9876543);
                ApplyStatisticsDisplay(PlaybackStatisticsDisplay.Create(request, playback, receive, rates, 3840, 2160, 123456789, CompactStatistics));
                CheckLayout("large-recent-deltas");
                StatisticsTick(null, null!);
            }
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            await Task.Delay(200, limit.Token);
            StatisticsTick(null, null!);
            CheckLayout("own-fullscreen");
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            await Task.Delay(200, limit.Token);
            var compactScale = WindowRoot.XamlRoot.RasterizationScale;
            AppWindow.ResizeClient(new((int)(560 * compactScale), (int)(420 * compactScale)));
            await Task.Delay(200, limit.Token);
            foreach (var output in new[] { VideoPresentationOutput.SdrToneMapped, VideoPresentationOutput.Hdr })
            {
                playback = playback with { Presentation = new(output) };
                StatisticsTick(null, null!);
                var expected = output == VideoPresentationOutput.Hdr ? "HDR output" : "SDR (tone-mapped)";
                if (Value("videoDynamicRange") != "HDR10 · PQ" || Value("videoDecodeFormat") != "Main 10 · 10-bit" ||
                    Value("presentationOutput") != expected)
                    throw new InvalidOperationException("HDR stream details were confused with presentation output.");
                checks.Add(JsonData.Object(("phase", "cached-monitor-output-change"), ("snapshot", ReportJson.Value(playback))));
                CheckLayout("hdr-stream-" + output);
            }
            playback = playback with
            {
                Video = new(HdrVideoFormat.Sdr, VideoTransfer.Bt709, "Main", 8),
                Presentation = VideoPresentationStatistics.ForStream(HdrVideoFormat.Sdr, true)
            };
            StatisticsTick(null, null!);
            if (Value("videoDynamicRange") != "SDR · BT.709" || Value("videoDecodeFormat") != "Main · 8-bit" ||
                Value("presentationOutput") != "SDR output")
                throw new InvalidOperationException("SDR content was relabeled HDR on an HDR-capable display.");
            CheckLayout("sdr-stream-on-hdr-display");
            playback = playback with { Video = null, Presentation = null };
            StatisticsTick(null, null!);
            if (PlaybackStatisticsDisplay.VideoPresentationFields.Any(f => Value(f.Id) != "N/A"))
                throw new InvalidOperationException("Missing decoded or presented metadata claimed success.");
            CheckLayout("unknown-video-presentation");
            request = request with { AudioCodec = null };
            playback = playback with { Audio = null, Sync = null, EncodedWidth = 888, EncodedHeight = 1920 };
            ClearStatistics();
            UpdateStatisticsVisibility();
            if (Value("audioCodec") != "Off" || Value("pcmBufferTarget") != "N/A" || Value("receivedFps") != "N/A")
                throw new InvalidOperationException("Audio-off or first-sample unknowns were misrepresented.");
            CheckLayout("audio-off-unknowns-portrait-raster");
            playback = playback with { Epoch = 2, Generation = 2, Received = 1, Decoded = 1, Presented = 1, Skipped = 0 };
            receive = receive with { Session = 8, Recovery = new(8, 2, 2, true) };
            StatisticsTick(null, null!);
            if (Value("receivedFps") != "N/A" || Value("state") != "Recovering" || Value("peakObservedSkew") != "N/A")
                throw new InvalidOperationException("Recovery did not reset rate and peak baselines.");
            CheckLayout("session-generation-epoch-recovery-reset");
            isolatedStatisticsProvider = null;
            UpdateStatisticsVisibility();
            var stoppedSamples = samples;
            await Task.Delay(1100, limit.Token);
            if (statisticsTimer.IsEnabled || PlaybackStatsOverlay.Visibility != Visibility.Collapsed ||
                statisticsValues.Values.Any(c => c.Text != "") || statisticsDisplay is not null || samples != stoppedSamples)
                throw new InvalidOperationException("Stop did not clear all cells, timer and sampling.");
            checks.Add(JsonData.Object(("phase", "simulated-stop"), ("visible", false), ("timer", false), ("cleared", true)));
            var localizationScale = WindowRoot.XamlRoot.RasterizationScale;
            AppWindow.ResizeClient(new((int)(1100 * localizationScale), (int)(760 * localizationScale)));
            await Task.Delay(150, limit.Token);
            await CheckLocalizationAsync(checks, limit.Token);
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            isolatedStatisticsProvider = null;
            ClearStatistics();
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        }
        File.WriteAllText(reportFile, JsonData.Text(JsonData.Object(
            ("passed", failure is null), ("failure", failure), ("pid", Environment.ProcessId), ("version", AppVersion.Current().Label),
            ("typedSyntheticSnapshots", true), ("nativeXaml", true), ("loopbackListenerOnly", true),
            ("captureStarted", false), ("audioStarted", false), ("encoderStarted", false), ("nativeDecoderStarted", false),
            ("userSettingsWritten", false), ("inputInjected", false), ("userAppsTouched", false), ("checks", JsonData.Value(checks))),
            indented: true));
    }
}
