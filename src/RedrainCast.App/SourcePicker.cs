using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RedrainCast.Send.Media;
using RedrainCast.Core;
using Windows.Foundation;

namespace RedrainCast.App;

internal sealed class SourcePicker : IAsyncDisposable
{
    private sealed record Tile(CaptureTarget Target, Button Button, Image Image, TextBlock Status);
    private readonly List<Tile> tiles = [];
    private readonly CancellationTokenSource stop = new();
    private readonly ScrollViewer scroll;
    private readonly Func<bool> canSelect;
    private readonly Action<string> diagnostic;
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly AppStrings strings = AppStrings.Current;
    private readonly List<Action> labels = [];
    private readonly Dictionary<TextBlock, string> statusKeys = [];
    private string? errorKey;
    private string? errorDetail;
    private Task previews = Task.CompletedTask;
    private bool started;
    private CancellationTokenSource? reading;
    public ContentDialog Dialog { get; }
    public CaptureSource? Selected { get; private set; }

    public SourcePicker(IReadOnlyList<CaptureTarget> targets, CaptureSource? current,
        Func<bool> canSelect, Action<string> diagnostic, double availableHeight, double availableWidth)
    {
        this.canSelect = canSelect;
        this.diagnostic = diagnostic;
        var contentWidth = Math.Max(120, Math.Min(560, availableWidth - 100));
        var columns = contentWidth < 400 ? 1 : 2;
        var sections = new StackPanel { Spacing = 12 };
        sections.Children.Add(Label("PickerInstructions"));
        sections.Children.Add(error);
        foreach (var windows in new[] { false, true })
        {
            var heading = Label(windows ? "Windows" : "Displays");
            heading.FontSize = 20;
            heading.Style = Application.Current.Resources["SubtitleTextBlockStyle"] as Style;
            sections.Children.Add(heading);
            var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
            for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new());
            var index = 0;
            foreach (var target in SourcePickerPolicy.Group(targets, target => target.IsWindow, windows))
            {
                var image = new Image { Height = 108, Stretch = Stretch.Uniform, FlowDirection = FlowDirection.LeftToRight };
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                statusKeys[status] = "PreviewPending";
                var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
                content.Children.Add(image);
                var name = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
                labels.Add(() => name.Text = TargetName(target));
                content.Children.Add(name);
                content.Children.Add(status);
                var selected = current is not null &&
                    new SourceIdentity(current.Handle, current.IsWindow) == new SourceIdentity(target.Handle, target.IsWindow);
                if (selected)
                {
                    var selectedLabel = Label("CurrentlySelected");
                    selectedLabel.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    content.Children.Add(selectedLabel);
                }
                var button = new Button
                {
                    Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
                };
                labels.Add(() => AutomationProperties.SetName(button,
                    selected ? strings.Get("SelectedSourceHelp", TargetName(target)) : TargetName(target)));
                labels.Add(() => ToolTipService.SetToolTip(button, TargetName(target)));
                button.Click += (_, _) => Select(target);
                Grid.SetColumn(button, index % columns);
                Grid.SetRow(button, index / columns);
                if (index % columns == 0) grid.RowDefinitions.Add(new());
                grid.Children.Add(button);
                tiles.Add(new(target, button, image, status));
                index++;
            }
            if (index == 0) sections.Children.Add(Label("NoSources"));
            sections.Children.Add(grid);
        }
        scroll = new ScrollViewer
        {
            Content = sections, MaxHeight = Math.Max(120, availableHeight - 240),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Width = contentWidth, MaxWidth = contentWidth,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Dialog = new ContentDialog
        {
            Content = scroll,
            FullSizeDesired = true
        };
        Dialog.Resources["ContentDialogMaxWidth"] = 720d;
        strings.Changed += RefreshLanguage;
        RefreshLanguage();
        scroll.ViewChanged += (_, _) => reading?.Cancel();
        Dialog.Opened += (_, _) =>
        {
            if (started) return;
            started = true;
            previews = PreviewAsync();
        };
        Dialog.Closing += (_, _) => stop.Cancel();
    }

    private TextBlock Label(string key)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        labels.Add(() => text.Text = strings.Get(key));
        return text;
    }

    private string TargetName(CaptureTarget target) => target.IsWindow || target.DisplayNumber == 0 ? target.Name :
        strings.Get("DisplaySource", target.DisplayNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            target.DisplayWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            target.DisplayHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void RefreshLanguage()
    {
        Dialog.Title = strings.Get("ChooseSource");
        Dialog.CloseButtonText = strings.Get("Cancel");
        Dialog.FlowDirection = strings.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (var update in labels) update();
        foreach (var (text, key) in statusKeys) text.Text = strings.Get(key);
        error.Text = errorKey is null ? "" : strings.Get(errorKey) +
            (errorDetail is null ? "" : "\n" + strings.Get("TechnicalDetails", strings.LeftToRight(errorDetail)));
    }

    private void Select(CaptureTarget target)
    {
        if (stop.IsCancellationRequested || !canSelect()) return;
        try
        {
            var selected = target.Open();
            if (stop.IsCancellationRequested || !canSelect()) return;
            Selected = selected;
            stop.Cancel();
            Dialog.Hide();
        }
        catch (Exception failure)
        {
            errorKey = "CannotSelectSource";
            errorDetail = failure.Message;
            RefreshLanguage();
            diagnostic($"Source picker selection unavailable: {failure.Message}");
        }
    }

    private bool Visible(Tile tile)
    {
        if (tile.Button.ActualHeight <= 0 || scroll.ViewportHeight <= 0) return false;
        var bounds = tile.Button.TransformToVisual(scroll).TransformBounds(
            new Rect(0, 0, tile.Button.ActualWidth, tile.Button.ActualHeight));
        return bounds.Bottom > 0 && bounds.Top < scroll.ViewportHeight;
    }

    private async Task PreviewAsync()
    {
        var unavailable = new HashSet<Tile>();
        try
        {
            using var capture = new CaptureThumbnail();
            var index = 0;
            while (!stop.IsCancellationRequested)
            {
                var visible = SourcePickerPolicy.VisiblePreviews(tiles, Visible, unavailable.Contains);
                foreach (var hidden in tiles.Where(tile => !visible.Contains(tile)))
                    hidden.Image.Source = null;
                if (visible.Length > 0)
                {
                    var tile = visible[index++ % visible.Length];
                    using var read = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    reading = read;
                    try
                    {
                        using var bitmap = await capture.ReadAsync(tile.Target, read.Token);
                        if (stop.IsCancellationRequested) break;
                        if (Visible(tile))
                        {
                            var image = new SoftwareBitmapSource();
                            await image.SetBitmapAsync(bitmap);
                            if (!stop.IsCancellationRequested && Visible(tile))
                            {
                                tile.Image.Source = image;
                                statusKeys[tile.Status] = "LivePreview";
                                tile.Status.Text = strings.Get("LivePreview");
                            }
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                    catch (OperationCanceledException) when (read.IsCancellationRequested) { }
                    catch (Exception failure)
                    {
                        unavailable.Add(tile);
                        statusKeys[tile.Status] = "PreviewUnavailable";
                        tile.Status.Text = strings.Get("PreviewUnavailable");
                        diagnostic($"Source picker preview unavailable: {failure.Message}");
                    }
                    finally { reading = null; }
                }
                await Task.Delay(350, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception failure)
        {
            errorKey = "PreviewsUnavailable";
            errorDetail = failure.Message;
            RefreshLanguage();
            diagnostic($"Source picker previews unavailable: {failure.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        strings.Changed -= RefreshLanguage;
        stop.Cancel();
        await previews;
        foreach (var tile in tiles) tile.Image.Source = null;
        stop.Dispose();
    }
}
