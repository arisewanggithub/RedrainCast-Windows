using System.Text.Json.Nodes;
using RedrainCast.Protocol;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RedrainCast.Core;
using RedrainCast.Receive.Core;
using RedrainCast.Send.Core;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint LocalizationSendMessage(nint window, uint message, nuint wParam, nint lParam);

    private async Task CheckProductionPreferredLanguagesAsync(List<JsonNode> checks, CancellationToken token)
    {
        var actualLanguages = AppStrings.ReadPreferredLanguages();
        var resolved = AppLanguages.Resolve(actualLanguages);
        while (WindowRoot.XamlRoot is null || WindowRoot.ActualHeight <= 0 ||
            strings.Culture.Name != resolved) await Task.Delay(20, token);
        WindowRoot.UpdateLayout();

        JsonNode VisibleProductionState(IReadOnlyList<string> languages)
        {
            var expected = AppLanguages.Resolve(languages);
            var manual = strings.ResolveCandidate("ManualConnect");
            var ready = strings.ResolveCandidate("Ready");
            var settingsName = strings.ResolveCandidate("Settings");
            if (strings.Culture.Name != expected || ManualConnect.Content?.ToString() != manual.Value ||
                ReadinessTitle.Text != ready.Value || AutomationProperties.GetName(SettingsButton) != settingsName.Value ||
                AppLanguages.Resolve([manual.Language]) != expected || AppLanguages.Resolve([ready.Language]) != expected)
                throw new InvalidOperationException("Production preferred-language reader did not drive the visible Home UI and MRT context.");
            return JsonData.Object(
                ("preferredLanguages", JsonData.Value(languages.ToArray())), ("resolvedLanguage", expected),
                ("appCulture", strings.Culture.Name),
                ("threadUiCulture", System.Globalization.CultureInfo.CurrentUICulture.Name),
                ("threadCulture", System.Globalization.CultureInfo.CurrentCulture.Name),
                ("manualConnect", ManualConnect.Content?.ToString()), ("readiness", ReadinessTitle.Text),
                ("settingsAutomationName", AutomationProperties.GetName(SettingsButton)),
                ("mrtLanguageQualifier", manual.Language));
        }

        checks.Add(JsonData.Object(
            ("phase", "production-preferred-languages-startup"), ("defaultProductionReader", true),
            ("syntheticLanguages", false), ("observed", VisibleProductionState(actualLanguages))));

        // Instrument the real reader, without replacing its result, to prove fresh same-process reads on notifications.
        var reads = new List<IReadOnlyList<string>>();
        var failures = new List<Exception>();
        strings.Stop();
        strings.Start(DispatcherQueue, failures.Add, () =>
        {
            var current = AppStrings.ReadPreferredLanguages();
            reads.Add(current);
            return current;
        });
        while (reads.Count == 0) await Task.Delay(20, token);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        async Task RecordFreshReadAsync(string phase, int before)
        {
            while (reads.Count <= before) await Task.Delay(20, token);
            if (failures.Count != 0) throw new AggregateException("Production preference reconciliation failed.", failures);
            WindowRoot.UpdateLayout();
            checks.Add(JsonData.Object(
                ("phase", phase), ("productionApi", "Windows.System.UserProfile.GlobalizationPreferences.Languages"),
                ("syntheticLanguages", false), ("readsBefore", before), ("readsAfter", reads.Count),
                ("observed", VisibleProductionState(reads[^1])), ("osProfileChanged", false)));
        }
        var before = reads.Count;
        LocalizationSendMessage(handle, 0x001A, 0, 0);
        await RecordFreshReadAsync("production-preferences-null-setting-notification", before);
        var section = Marshal.StringToHGlobalUni("Environment");
        try
        {
            before = reads.Count;
            LocalizationSendMessage(handle, 0x001A, 0, section);
            await RecordFreshReadAsync("production-preferences-unrelated-setting-notification", before);
        }
        finally { Marshal.FreeHGlobal(section); }
        before = reads.Count;
        AppWindow.Hide();
        await Task.Delay(50, token);
        AppWindow.Show();
        Activate();
        await RecordFreshReadAsync("production-preferences-activation-visibility", before);
    }

    private async Task CheckLocalizationAsync(List<JsonNode> checks, CancellationToken token)
    {
        if (!isolated || source is not null || video.IsAttached || roles.Role != CastRole.Idle)
            throw new InvalidOperationException("Localization proof requires an isolated, idle synthetic window without capture.");
        IReadOnlyList<string> preferred = ["en-US"];
        var errors = new List<Exception>();
        strings.Stop();
        strings.Start(DispatcherQueue, errors.Add, () => preferred);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var section = Marshal.StringToHGlobalUni("intl");
        var originalSettings = settings;
        var endpoint = ReceiverEndpoint.Manual("192.0.2.10", 50432) with { Name = "Owned synthetic peer" };
        var syntheticCast = new OutgoingCast(endpoint, OutgoingPhase.Casting, "H.265 (HEVC) - 1920 x 1080",
            "Owned synthetic encoder");
        var problem = new IOException(@"Owned synthetic failure: C:\RedrainCast\fixture.log; [2001:db8::1]:50432");
        var request = new CastRequest("h265", null, 60, "portrait");
        var playback = new PlaybackStatistics(1, 1, 100, 90, 80, 10, 1920, 1080, 0, false, null,
            new(1, 1_000_000, 900_000, 48000, 100_000, 1), 0,
            new(new(true), VideoTransfer.Pq, "Main 10", 10),
            VideoPresentationStatistics.ForStream(new(true), false));
        var receive = new ReceiveStatistics(1, new(100, 1000, 1), new(0, 0, 1), new(0, 0, 0), new(0, 0, 0), null, null, null);
        isolatedStatisticsProvider = () => new(request, playback, receive);
        outgoing = syntheticCast;
        HomePage.Visibility = Visibility.Visible;
        ReportAction(problem, "CopyFailed");
        StatisticsPresentation? preserved = null;
        var continuityChecks = 0;
        var layoutFailures = new List<string>();
        void CheckTextBounds(DependencyObject root, string language, string context)
        {
            var measured = 0;
            var firstFailure = layoutFailures.Count;
            void Visit(DependencyObject element)
            {
                if (element is TextBlock text && text.Visibility == Visibility.Visible &&
                    text.ActualWidth > 0 && text.ActualHeight > 0 && !string.IsNullOrEmpty(text.Text))
                {
                    measured++;
                    var slot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(text);
                    if (slot.Width <= 0 || slot.Height <= 0) return;
                    var natural = new TextBlock
                    {
                        Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize,
                        FontWeight = text.FontWeight, FlowDirection = text.FlowDirection,
                        TextWrapping = text.TextWrapping, LineHeight = text.LineHeight,
                        LineStackingStrategy = text.LineStackingStrategy, Language = text.Language,
                        IsTextScaleFactorEnabled = text.IsTextScaleFactorEnabled,
                        CharacterSpacing = text.CharacterSpacing, FontStyle = text.FontStyle, FontStretch = text.FontStretch
                    };
                    natural.Measure(new(text.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : slot.Width,
                        double.PositiveInfinity));
                    var end = text.ContentEnd.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Backward);
                    if (text.IsTextTrimmed || end.Bottom > slot.Height + 1 ||
                        text.TextWrapping == TextWrapping.NoWrap && natural.DesiredSize.Width > slot.Width + 1)
                    {
                        var key = statisticsLabelKeys.TryGetValue(text, out var resource) ? resource :
                            statisticsValues.FirstOrDefault(pair => ReferenceEquals(pair.Value, text)).Key ?? "dialog";
                        layoutFailures.Add($"{language}/{context}/{key}: {text.Text}; " +
                            $"desired={natural.DesiredSize.Width:F1}x{natural.DesiredSize.Height:F1}, slot={slot.Width:F1}x{slot.Height:F1}");
                    }
                }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                    Visit(VisualTreeHelper.GetChild(element, i));
            }
            if (root is FrameworkElement frame) frame.UpdateLayout();
            Visit(root);
            checks.Add(JsonData.Object(("phase", "localized-text-bounds"), ("language", language), ("context", context), ("measured", measured),
                ("issues", JsonData.Value(layoutFailures.Skip(firstFailure).ToArray())), ("cumulativeFailures", layoutFailures.Count)));
        }
        void BeforeRefresh(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            var now = Environment.TickCount64;
            statisticsRates.Reset();
            statisticsRates.Sample(now - 1000, playback, receive);
            statisticsRates.Sample(now, playback, receive);
            statisticsPeak.Reset();
            statisticsPeak.Sample(playback with { Sync = playback.Sync! with { SignedSkewUs = 200_000 } }, receive, true);
            if (statisticsPresentation is { } cached) statisticsPresentation = cached with { Peak = 200 };
            preserved = statisticsPresentation;
        }
        void AfterRefresh()
        {
            if (!ReferenceEquals(preserved, statisticsPresentation) || statisticsRates.BaselineReset ||
                statisticsPeak.Sample(playback, receive, false) != 200 || settings != originalSettings)
                throw new InvalidOperationException("Language refresh reset statistics baselines, peak, counters or saved settings.");
            continuityChecks++;
        }

        async Task NotifyAsync(string language, IReadOnlyList<string>? preferences = null)
        {
            preferred = preferences ?? [language];
            // This changes only the owned window's input notification, never Windows' settings.
            LocalizationSendMessage(handle, 0x001A, 0, section);
            while (strings.Culture.Name != language) await Task.Delay(20, token);
            await Task.Delay(40, token);
            if (ManualConnect.Content?.ToString() != L("ManualConnect") ||
                AutomationProperties.GetName(SettingsButton) != L("Settings") ||
                ToolTipService.GetToolTip(SettingsButton)?.ToString() != L("Settings") ||
                !ReferenceEquals(outgoing, syntheticCast) || !ReferenceEquals(outgoing.Target, endpoint) ||
                source is not null || video.IsAttached || roles.Role != CastRole.Idle)
                throw new InvalidOperationException($"Live Home/automation/identity refresh failed for {language}.");
            var rtl = strings.IsRightToLeft;
            if (HomePage.FlowDirection != (rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight) ||
                PlayerInput.FlowDirection != FlowDirection.LeftToRight ||
                statisticsValues.Values.Any(value => value.FlowDirection != FlowDirection.LeftToRight))
                throw new InvalidOperationException($"Label/data/video flow directions failed for {language}.");
            if (Problem.Title != L("ActionFailed") || !Problem.Message.Contains(L("CopyFailed"), StringComparison.Ordinal) ||
                !Problem.Message.Contains(problem.Message, StringComparison.Ordinal))
                throw new InvalidOperationException($"Stored actionable error was not reprojected for {language}.");
            if (!statisticsLabelKeys.All(pair => pair.Key.Text == L(
                    ReferenceEquals(pair.Key, statisticsClockNote) && CompactStatistics ? "StatisticsCompactClockNote" : pair.Value)) ||
                statisticsValues["audioCodec"].Text != L("Off") ||
                statisticsValues["videoCodec"].Text != "H.265 (HEVC)" ||
                statisticsLabels.Concat(statisticsValues.Values).Any(text => text.FontSize < 14))
                throw new InvalidOperationException($"Statistics presentation did not refresh for {language}.");
            var values = EnglishCatalog.Strings.Keys.ToDictionary(key => key, key =>
            {
                var candidate = strings.ResolveCandidate(key);
                if (AppLanguages.Resolve([candidate.Language]) != language)
                    throw new InvalidDataException($"MRT fell back instead of selecting {language}: {key} -> {candidate.Language}");
                return candidate.Value;
            });
            CatalogText.Validate(new XDocument(new XElement("root", values.Select(pair =>
                new XElement("data", new XAttribute("name", pair.Key), new XElement("value", pair.Value))))));
            if (errors.Count != 0) throw new AggregateException("Localization reported a resource or OS-reader failure.", errors);
        }

        strings.PropertyChanged += BeforeRefresh;
        strings.Changed += AfterRefresh;
        try
        {
            await Task.Delay(50, token);
            UpdateStatisticsVisibility();
            await NotifyAsync("en-US");
            ManualConnect_Click(ManualConnect, new RoutedEventArgs());
            while (currentDialog is null) await Task.Delay(20, token);
            var manual = currentDialog;
            var manualContent = (StackPanel)manual.Content;
            var input = (TextBox)manualContent.Children[0];
            const string typed = "  [2001:db8::1] : 50432  ";
            input.Text = typed;
            input.Select(3, 5);
            foreach (var language in AppLanguages.Supported)
            {
                await NotifyAsync(language);
                if (!ReferenceEquals(currentDialog, manual) || input.Text != typed ||
                    input.SelectionStart != 3 || input.SelectionLength != 5 ||
                    input.FlowDirection != FlowDirection.LeftToRight ||
                    manual.Title?.ToString() != L("ConnectReceiver") ||
                    manual.PrimaryButtonText != L("Send") || manual.CloseButtonText != L("Cancel"))
                    throw new InvalidOperationException($"Open manual dialog lost text/selection or labels for {language}.");
                checks.Add(JsonData.Object(("phase", "localization-manual-dialog"), ("language", language), ("compiledKeys", EnglishCatalog.Strings.Count),
                    ("nativeNotification", "owned HWND WM_SETTINGCHANGE/intl"), ("inputRetained", true),
                    ("selectionRetained", true), ("syntheticCastIdentityRetained", true), ("rtl", strings.IsRightToLeft)));
                CheckTextBounds(manual, language, "manual-dialog");
                CheckTextBounds(PlaybackStatsPanel, language, "statistics-wide");
            }
            manual.Hide();
            while (currentDialog is not null) await Task.Delay(20, token);
            await NotifyAsync("fr");
            var codecChoices = new CodecSelection(0x10de, false);
            var sharing = ShowSharingSettingsDialogAsync(codecChoices, () => Task.FromResult(codecChoices));
            while (currentDialog is null) await Task.Delay(20, token);
            var settingsDialog = currentDialog;
            var panel = (StackPanel)((ScrollViewer)settingsDialog.Content).Content;
            var dimension = (TextBox)panel.Children[0];
            var fps = (ComboBox)panel.Children[1];
            var bitrate = (TextBox)panel.Children[2];
            var codec = (ComboBox)panel.Children[3];
            var orientation = (ComboBox)panel.Children[4];
            var audio = (CheckBox)panel.Children[5];
            dimension.Text = "1920";
            bitrate.Text = "1,5";
            fps.SelectedValue = 120;
            orientation.SelectedIndex = 2;
            codec.SelectedItem = codecChoices.Find("h265");
            audio.IsChecked = true;
            foreach (var language in AppLanguages.Supported)
            {
                await NotifyAsync(language);
                if (!ReferenceEquals(currentDialog, settingsDialog) || dimension.Text != "1920" || bitrate.Text != "1,5" ||
                    fps.SelectedValue is not int cap || cap != 120 || orientation.SelectedIndex != 2 ||
                    codec.SelectedItem is not CodecChoice { Codec: "h265" } || audio.IsChecked != true ||
                    settingsDialog.Title?.ToString() != L("SharingSettings") ||
                    settingsDialog.PrimaryButtonText != L("Save") || settingsDialog.CloseButtonText != L("Cancel"))
                    throw new InvalidOperationException($"Open sharing dialog lost unsaved values or labels for {language}.");
                var parsed = CaptureSettings.ParseInput(dimension.Text, bitrate.Text, cap, "portrait",
                    System.Globalization.CultureInfo.GetCultureInfo("fr"), "h265", true);
                if (parsed.Settings?.VideoBitrate != 1_500_000)
                    throw new InvalidOperationException("The retained French edit context did not preserve the fractional bitrate.");
                checks.Add(JsonData.Object(("phase", "localization-settings-dialog"), ("language", language), ("editCulture", "fr"),
                    ("unsavedValuesRetained", true), ("settingsWritten", false)));
                CheckTextBounds(settingsDialog, language, "settings-dialog");
            }
            settingsDialog.Hide();
            await sharing;
            if (settings != originalSettings) throw new InvalidOperationException("Cancelling the fixture changed sharing settings.");
            var scale = WindowRoot.XamlRoot.RasterizationScale;
            AppWindow.ResizeClient(new((int)(560 * scale), (int)(420 * scale)));
            await Task.Delay(150, token);
            foreach (var language in AppLanguages.Supported)
            {
                await NotifyAsync(language);
                CheckTextBounds(PlaybackStatsPanel, language, "statistics-compact");
            }
            await NotifyAsync("en-US", ["qaa-Unsupported"]);
            await NotifyAsync("de", ["qaa-Unsupported", "de-DE", "ar"]);
            await NotifyAsync("zh-Hant", ["zh-HK"]);
            preferred = ["ar"];
            AppWindow.Hide();
            await Task.Delay(50, token);
            AppWindow.Show();
            Activate();
            while (strings.Culture.Name != "ar") await Task.Delay(20, token);
            checks.Add(JsonData.Object(("phase", "localization-continuity-and-fallback"), ("continuityChecks", continuityChecks),
                ("preferredOrder", true), ("unsupportedEnglishFallback", true), ("chineseRegionFallback", true),
                ("activationVisibilityReconciled", true), ("baselineAndPeakPreserved", true)));
            if (layoutFailures.Count != 0)
                throw new InvalidOperationException("Localized text clipping:\n" + string.Join("\n", layoutFailures));
        }
        finally
        {
            strings.PropertyChanged -= BeforeRefresh;
            strings.Changed -= AfterRefresh;
            currentDialog?.Hide();
            Marshal.FreeHGlobal(section);
            outgoing = null;
            isolatedStatisticsProvider = null;
            ClearStatistics();
            strings.Stop();
        }
    }
}
