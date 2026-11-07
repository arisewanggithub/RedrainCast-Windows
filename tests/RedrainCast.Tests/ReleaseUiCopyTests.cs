using Xunit;
using RedrainCast.Core;

namespace RedrainCast.Tests;

public sealed class ReleaseUiCopyTests
{
    private static string WindowSource => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "MainWindow.xaml.cs.txt"));

    [Fact]
    public void ConnectionHelpCoversCompatiblePlatformsAndPublicSupport()
    {
        var source = WindowSource;
        var start = source.IndexOf("private async void Help_Click", StringComparison.Ordinal);
        var end = source.IndexOf("private void Root_KeyDown", start, StringComparison.Ordinal);
        var help = source[start..end];

        Assert.Contains("L(\"HelpBody\")", help);
        Assert.Contains("compatible Android devices", EnglishCatalog.Get("HelpBody"));
        Assert.DoesNotContain("both PCs", help);
        Assert.DoesNotContain("UDP 27183", help);
        Assert.Contains("https://redraincast.redrain.ccwu.cc/support/", help);
        Assert.Contains("https://redraincast.redrain.ccwu.cc/privacy/", help);
        Assert.Contains("new ScrollViewer", help);
        Assert.DoesNotContain("Diagnostic logs", help);
        Assert.DoesNotContain("DiagnosticDirectory", help);
        Assert.DoesNotContain("new Expander", help);
        Assert.Contains("not authenticated or encrypted", EnglishCatalog.Get("HelpBody"));
    }

    [Fact]
    public void FooterAndSelfTestUseTheSameDynamicReleaseLabel()
    {
        var source = WindowSource;
        Assert.Contains("VersionText.Text = L(\"Version\", AppVersion.Current().Label)", source);
        Assert.Equal("RedrainCast · {0}", EnglishCatalog.Get("Version"));
        Assert.Contains("var displayedVersion = L(\"Version\", AppVersion.Current().Label)", source);
        Assert.DoesNotContain("RedrainCast Preview", source);
    }

    [Fact]
    public void SendingSelfTestUsesLocalizedRowStatuses()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "SendingSelfTest.cs.txt"));
        Assert.Contains("rows[1].Status != L(\"Busy\")", source);
        Assert.Contains("stopping[0].Status != L(\"Stopping\")", source);
        Assert.DoesNotContain("Status != \"Busy\"", source);
        Assert.DoesNotContain("Status != \"Stopping...\"", source);
    }
}
