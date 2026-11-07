using RedrainCast.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class SourcePickerTests
{
    private sealed record Source(string Name, SourceIdentity Identity);
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".cs.txt"));

    [Fact]
    public void GroupingPreservesEnumerationOrderAndDuplicateTitles()
    {
        Source[] sources = [
            new("same", new(4, true)), new("Display 1", new(1, false)),
            new("same", new(5, true)), new("Display 2", new(2, false))];
        Assert.Equal(new nint[] { 1, 2 }, SourcePickerPolicy.Group(sources, s => s.Identity.IsWindow, false).Select(s => s.Identity.Handle));
        Assert.Equal(new nint[] { 4, 5 }, SourcePickerPolicy.Group(sources, s => s.Identity.IsWindow, true).Select(s => s.Identity.Handle));
        Assert.NotEqual(sources[0].Identity, sources[2].Identity);
        Assert.NotEqual(new SourceIdentity(1, true), new SourceIdentity(1, false));
    }

    [Theory]
    [InlineData(false, false, CastRole.Idle, true)]
    [InlineData(true, false, CastRole.Idle, false)]
    [InlineData(false, true, CastRole.Idle, false)]
    [InlineData(false, false, CastRole.Receiving, false)]
    [InlineData(false, false, CastRole.Sending, false)]
    [InlineData(true, true, CastRole.Receiving, false)]
    public void SelectionRequiresAnUncancelledIdleLifetime(bool cancelled, bool closing, CastRole role, bool expected) =>
        Assert.Equal(expected, SourcePickerPolicy.CanSelect(cancelled, closing, role));

    [Fact]
    public void PreviewBudgetIncludesOnlyVisibleAvailableSources()
    {
        var previews = SourcePickerPolicy.VisiblePreviews(Enumerable.Range(0, 40), n => n >= 10, n => n == 11);
        Assert.Equal(new[] { 10, 12, 13, 14, 15, 16 }, previews);
        Assert.Equal(SourcePickerPolicy.MaximumVisiblePreviews, previews.Length);
        Assert.Empty(SourcePickerPolicy.VisiblePreviews(Enumerable.Range(0, 40), _ => false, _ => false));
    }

    [Fact]
    public void PickerSelectionHasNoDropdownChooseSendOrPersistence()
    {
        var main = Fixture("MainWindow.xaml");
        var start = main.IndexOf("private async void ChooseSource_Click", StringComparison.Ordinal);
        var selection = main[start..main.IndexOf("private async void DeviceAction_Click", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("ComboBox", selection);
        Assert.DoesNotContain("PrimaryButtonText", selection);
        Assert.DoesNotContain("StartSendingAsync", selection);
        Assert.DoesNotContain("SaveSettings", selection);
        Assert.Contains("await using var picker", selection);
        Assert.Contains("CaptureSource.Enumerate(owner)", selection);
        Assert.Contains("SourcePickerPolicy.CanSelect(cancelled.IsCancellationRequested, closing, roles.Role)", selection);
        var picker = Fixture("SourcePicker");
        Assert.DoesNotContain("PrimaryButtonText", picker);
        Assert.Contains("Dialog.CloseButtonText = strings.Get(\"Cancel\")", picker);
        Assert.Equal("Cancel", EnglishCatalog.Get("Cancel"));
        Assert.Contains("button.Click +=", picker);
        Assert.Contains("AutomationProperties.SetName", picker);
        Assert.Contains("Dialog.Hide()", picker);
    }

    [Fact]
    public void ClosedSourceFailsExplicitlyBeforeOpeningAndPickerRemainsAvailable()
    {
        var source = Fixture("CaptureSource");
        Assert.Contains("if (!CaptureSource.IsAvailable(this))", source);
        Assert.Contains("throw new IOException(\"This source closed", source);
        Assert.Contains("process == target.ProcessId", source);
        var picker = Fixture("SourcePicker");
        var select = picker[picker.IndexOf("private void Select", StringComparison.Ordinal)..picker.IndexOf("private bool Visible", StringComparison.Ordinal)];
        Assert.True(select.IndexOf("target.Open()", StringComparison.Ordinal) < select.IndexOf("Selected = selected", StringComparison.Ordinal));
        Assert.Equal(2, select.Split("!canSelect()", StringSplitOptions.None).Length - 1);
        Assert.Contains("errorKey = \"CannotSelectSource\"", select);
        Assert.StartsWith("Cannot select this source", EnglishCatalog.Get("CannotSelectSource"));
    }

    [Fact]
    public void PreviewStartupCleanupAndResourceBoundsAreWired()
    {
        var picker = Fixture("SourcePicker");
        Assert.Contains("Dialog.Opened +=", picker);
        Assert.Contains("previews = PreviewAsync()", picker);
        Assert.Contains("Dialog.Closing += (_, _) => stop.Cancel()", picker);
        Assert.Contains("scroll.ViewChanged += (_, _) => reading?.Cancel()", picker);
        Assert.Contains("SourcePickerPolicy.VisiblePreviews", picker);
        Assert.Contains("await previews", picker);
        Assert.Contains("tile.Image.Source = null", picker);
        Assert.Contains("using var bitmap = await capture.ReadAsync", picker);
        var thumbnail = Fixture("CaptureThumbnail");
        Assert.Contains("TimeSpan.FromSeconds(1)", thumbnail);
        Assert.Contains("using var pool", thumbnail);
        Assert.Contains("using var session", thumbnail);
        Assert.Contains("using var frame", thumbnail);
        Assert.Contains("Math.Min(320", thumbnail);
        Assert.DoesNotContain("ReadColor", thumbnail);
        Assert.DoesNotContain("Encoder", thumbnail);
        Assert.DoesNotContain("File.", thumbnail);
        Assert.DoesNotContain("RequestAccess", thumbnail);
        var main = Fixture("MainWindow.xaml");
        var startup = main[main.IndexOf("private async Task InitializeCoreAsync", StringComparison.Ordinal)..main.IndexOf("private void Diagnostic", StringComparison.Ordinal)];
        Assert.DoesNotContain("CaptureThumbnail", startup);
        Assert.DoesNotContain("SourcePicker", startup);
    }
}
