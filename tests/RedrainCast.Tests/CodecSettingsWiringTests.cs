using Xunit;

namespace RedrainCast.Tests;

public sealed class CodecSettingsWiringTests
{
    [Fact]
    public void OutgoingSessionUsesTheSameHevcLabelAsThePicker() =>
        Assert.Contains("\"h265\" => \"H.265 (HEVC)\"", Source);

    private static string Source => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "MainWindow.xaml.cs.txt"));

    [Fact]
    public void OpeningAndCancelDoNotPersistOrFallbackAndSaveUsesTypedChoice()
    {
        var source = Source;
        var start = source.IndexOf("private async Task ShowSharingSettingsAsync()", StringComparison.Ordinal);
        var save = source.IndexOf("dialog.PrimaryButtonClick +=", start, StringComparison.Ordinal);
        var dialog = source[start..save];
        Assert.Contains("ItemsSource = codecSelection.Choices", dialog);
        Assert.Contains("SelectedItem = codecSelection.Find(settings.VideoCodec)", dialog);
        Assert.Contains("codecSelection.SavedSelectionMessage(settings.VideoCodec)", dialog);
        Assert.Contains("dialog.CloseButtonClick += (_, _) => cancelled = true", dialog);
        Assert.DoesNotContain("File.Write", dialog);
        Assert.DoesNotContain("settings =", dialog);
        Assert.DoesNotContain("codec.SelectedIndex", source[start..]);
        Assert.Contains("ShowSharingSettingsDialogAsync(codecSelection, ReadCodecSelectionAsync)", dialog);
        var refresh = source.IndexOf("codecSelection = await refreshSelection();", save, StringComparison.Ordinal);
        var cancel = source.IndexOf("if (cancelled)", refresh, StringComparison.Ordinal);
        var validate = source.IndexOf("codecSelection.ValidateSelection(requested)", cancel, StringComparison.Ordinal);
        var write = source.IndexOf("File.WriteAllText(SettingsPath", validate, StringComparison.Ordinal);
        var move = source.IndexOf("File.Move(SettingsPath", write, StringComparison.Ordinal);
        var update = source.IndexOf("settings = next;", move, StringComparison.Ordinal);
        Assert.True(refresh > save && cancel > refresh && validate > cancel && write > validate && move > write && update > move);
        Assert.Contains("codec.SelectedItem as CodecChoice", source[save..validate]);
        Assert.Contains("click.Cancel = true;", source[cancel..validate]);
        Assert.Contains("settingsErrorKey = error is ArgumentException ? \"SettingsValidation\" : \"RetryAction\";", source[update..]);
        Assert.Contains("RefreshSettingsLabels();", source[update..]);
        Assert.Contains("ReportAction(error, settingsErrorKey!, 0.1m.ToString(editCulture));", source[update..]);
    }

    [Fact]
    public void MetadataUsesTheCaptureDeviceAndSelectedSourceWithoutActivationOrFallback()
    {
        var source = Source;
        var start = source.IndexOf("private async Task<CodecSelection> ReadCodecSelectionAsync()", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task ShowSharingSettingsAsync()", start, StringComparison.Ordinal);
        var metadata = source[start..end];
        Assert.Contains("var selectedSource = source;", metadata);
        Assert.Contains("WindowsVideoSource.ReadCaptureAdapterVendorId()", metadata);
        Assert.Contains("selectedSource?.ReadColor(Diagnostic).Hdr", metadata);
        Assert.Contains("throw new IOException(", metadata);
        Assert.Contains("ReferenceEquals(selectedSource, source)", metadata);
        Assert.DoesNotContain("?? false", metadata);
        Assert.DoesNotContain("?? 0", metadata);
        var media = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "WindowsVideoSource.cs.txt"));
        start = media.IndexOf("public static uint ReadCaptureAdapterVendorId()", StringComparison.Ordinal);
        end = media.IndexOf("private readonly CaptureSource source;", start, StringComparison.Ordinal);
        var adapter = media[start..end];
        Assert.Contains("using var device = CreateHardwareDevice();", adapter);
        Assert.Contains("using var dxgi = device.QueryInterface<IDXGIDevice>();", adapter);
        Assert.Contains("using var adapter = dxgi.GetAdapter();", adapter);
        Assert.Contains("return adapter.Description.VendorId;", adapter);
        Assert.DoesNotContain("HardwareEncoder", adapter);
        Assert.DoesNotContain("StartCapture", adapter);
        Assert.DoesNotContain("catch", adapter);
    }

    [Fact]
    public void SettingsAsyncVoidReportsFailuresIncludingInitialMetadataRead()
    {
        var source = Source;
        var start = source.IndexOf("private async void Settings_Click(", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<CodecSelection>", start, StringComparison.Ordinal);
        Assert.Contains("try { await ShowSharingSettingsAsync(); }", source[start..end]);
        Assert.Contains("catch (Exception error) { ReportAction(error, \"SettingsUnavailable\"); }", source[start..end]);
    }
}
