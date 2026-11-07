using System.Text.RegularExpressions;
using System.Xml.Linq;
using RedrainCast.Core;
using RedrainCast.Receive.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class LocalizationWiringTests
{
    private static string Source(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", file + ".cs.txt"));

    [Fact]
    public void HomeTextAutomationAndTooltipsUseRealCanonicalKeys()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        var document = XDocument.Parse(xaml);
        var textAttributes = document.Descendants().Attributes().Where(a => a.Name.LocalName is
            "Text" or "Content" or "ToolTipService.ToolTip" or "AutomationProperties.Name");
        foreach (var attribute in textAttributes)
            Assert.True(attribute.Value.StartsWith('{') || attribute.Value == "RedrainCast", attribute.ToString());
        var keys = Regex.Matches(xaml, @"Binding ([A-Za-z]+), Source=\{StaticResource Strings\}").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(22, keys.Length);
        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(EnglishCatalog.Get(key))));
        Assert.All(keys, key => Assert.Contains($"public string {key} => Get(\"{key}\")", Source("AppStrings")));
        Assert.Contains("FlowDirection=\"LeftToRight\"", xaml);
    }

    [Fact]
    public void LiteralPresentationKeysArePresentInTheEnglishCatalog()
    {
        foreach (var file in new[] { "MainWindow.xaml", "SourcePicker", "AppStrings",
            "PlaybackStatisticsLayout", "PlaybackStatisticsOverlay" })
        {
            var matches = Regex.Matches(Source(file), "(?:\\bL|strings\\.Get|\\bGet)\\(\"([A-Za-z][A-Za-z0-9_]*)\"");
            foreach (Match match in matches)
            {
                var key = match.Groups[1].Value;
                if (!key.EndsWith('_')) Assert.False(string.IsNullOrWhiteSpace(EnglishCatalog.Get(key)), key);
            }
        }
    }

    [Fact]
    public void OpenDialogsRefreshLabelsWithoutReplacingInputsOrTheirParsingCulture()
    {
        var main = Source("MainWindow.xaml");
        var begin = main.IndexOf("private async Task ShowSharingSettingsAsync()", StringComparison.Ordinal);
        var end = main.IndexOf("private void LoadSettings()", begin, StringComparison.Ordinal);
        var settings = main[begin..end];
        Assert.Contains("var editCulture = strings.Culture;", settings);
        Assert.Contains("receiverOrientation, editCulture, videoCodec", settings);
        Assert.Contains("using var labels = strings.Bind(RefreshSettingsLabels);", settings);
        var refresh = settings[settings.IndexOf("void RefreshSettingsLabels()", StringComparison.Ordinal)..
            settings.IndexOf("using var labels", StringComparison.Ordinal)];
        Assert.DoesNotContain("size.Text =", refresh);
        Assert.DoesNotContain("bitrate.Text =", refresh);
        Assert.Contains("var selectedIndex = orientation.SelectedIndex;", refresh);
        Assert.Contains("orientation.SelectedIndex = selectedIndex;", refresh);
        Assert.DoesNotContain(".Hide()", refresh);
        Assert.Contains("validationKey is null ? \"\" : L(validationKey)", main);
        Assert.Contains("problemMessage = () =>", main);
        Assert.Contains("strings.Changed -= RefreshLanguage;", main);
    }

    [Fact]
    public void SystemEventsUseActualPreferredLanguagesExplicitMrtContextAndBoundedLifetime()
    {
        var source = Source("AppStrings");
        Assert.Contains("Windows.System.UserProfile.GlobalizationPreferences.Languages.ToArray()", source);
        Assert.Contains("new ResourceManager(Path.Combine(AppContext.BaseDirectory, \"RedrainCast.App.pri\"))", source);
        Assert.DoesNotContain("new ResourceManager()", source);
        Assert.DoesNotContain("GetUserPreferredUILanguages", source);
        Assert.Contains("observer = new(preferredLanguages ?? ReadPreferredLanguages", source);
        Assert.Contains("dispatcher.TryEnqueue", source);
        Assert.Contains("context.QualifierValues[\"Language\"] = language", source);
        Assert.Contains("cache.Clear()", source);
        Assert.Contains("observer?.Dispose()", source);
        Assert.Contains("PropertyChanged?.Invoke(this, new(string.Empty))", source);
        Assert.DoesNotContain("PrimaryLanguageOverride", source);
        Assert.DoesNotContain("DefaultThreadCurrentCulture =", source);
        Assert.DoesNotContain("CurrentCulture =", source);
        var notifications = Source("SystemLanguageNotifications");
        Assert.Contains("message == 0x001A", notifications);
        Assert.DoesNotContain("Marshal.PtrToStringUni", notifications);
        Assert.DoesNotContain("lParam != 0", notifications);
        Assert.Contains("window.Activated += Activated", notifications);
        Assert.Contains("window.Activated -= Activated", notifications);
        Assert.Contains("RemoveWindowSubclass(handle, callback, SubclassId)", notifications);
        Assert.Contains("message == 0x0218", notifications);
        Assert.Contains("observedRoot.Changed -= RootPresentationChanged", Source("MainWindow.xaml"));
        Assert.DoesNotContain("Timer", notifications);
    }

    [Fact]
    public void StatisticsResourcesKeepCanonicalEnglishLabelsAndAllPhaseKeysExist()
    {
        foreach (var field in PlaybackStatisticsDisplay.KpiFields.Concat(PlaybackStatisticsDisplay.VideoFields)
            .Concat(PlaybackStatisticsDisplay.AudioFields))
            Assert.Equal(field.Label, EnglishCatalog.Get("Statistics_" + field.Id));
        foreach (var phase in Enum.GetValues<ReceiverUiPhase>())
            Assert.False(string.IsNullOrEmpty(EnglishCatalog.Get("ReceiverPhase" + phase)));
        var layout = Source("PlaybackStatisticsLayout");
        Assert.Contains("key: \"Statistics_\" + cell.Field.Id", layout);
        Assert.Contains("cell.FlowDirection = FlowDirection.LeftToRight", layout);
        Assert.Contains("foreach (var (label, key) in statisticsLabelKeys)", layout);
        Assert.Contains("CompactStatistics, key => L(key), strings.Culture", layout);
    }

    [Fact]
    public void NativeHarnessChecksProductionPreferencesBeforeInstallingSyntheticLanguages()
    {
        var harness = Source("PlaybackStatisticsSelfTest");
        Assert.True(harness.IndexOf("await CheckProductionPreferredLanguagesAsync(checks, limit.Token)", StringComparison.Ordinal) <
            harness.IndexOf("strings.Stop()", StringComparison.Ordinal));
        var proof = Source("LocalizationSelfTest");
        Assert.Contains("var actualLanguages = AppStrings.ReadPreferredLanguages();", proof);
        Assert.Contains("var current = AppStrings.ReadPreferredLanguages();", proof);
        Assert.Contains("VisibleProductionState(actualLanguages)", proof);
        Assert.Contains("LocalizationSendMessage(handle, 0x001A, 0, 0)", proof);
        Assert.Contains("production-preferences-activation-visibility", proof);
        Assert.Contains("System.Globalization.CultureInfo.CurrentUICulture.Name", proof);
    }
}
