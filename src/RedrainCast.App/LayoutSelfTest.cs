using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RedrainCast.Core;
using Windows.Foundation;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct LayoutKeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(12)] public uint Flags;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static partial nint LayoutForegroundWindow();
    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint LayoutWindowProcess(nint window, out uint process);
    [LibraryImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static unsafe partial uint LayoutSendInput(uint count, LayoutKeyboardInput* inputs, int size);

    private unsafe void SendLayoutKey(ushort virtualKey = 0x09)
    {
        var foreground = LayoutForegroundWindow();
        _ = LayoutWindowProcess(foreground, out var owner);
        if (foreground != WinRT.Interop.WindowNative.GetWindowHandle(this) || owner != Environment.ProcessId)
            throw new InvalidOperationException("Keyboard layout proof blocked: the isolated test window is not foreground. No input was injected.");
        var inputs = stackalloc LayoutKeyboardInput[2];
        inputs[0] = new() { Type = 1, VirtualKey = virtualKey };
        inputs[1] = new() { Type = 1, VirtualKey = virtualKey, Flags = 2 };
        if (LayoutSendInput(2, inputs, sizeof(LayoutKeyboardInput)) != 2)
            throw new InvalidOperationException($"Owned-window keyboard input failed: {Marshal.GetLastPInvokeError()}.");
    }

    internal async Task RunLayoutSelfTestAsync(string reportFile, DiagnosticLog? journal)
    {
        var measurements = new List<object>();
        string? failure = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var pickerStarted = false;
        try
        {
            await CheckHomeLayoutAsync(measurements, journal, timeout.Token);
            pickerStarted = true;
            await CheckNativeSelectionAsync(measurements, journal, timeout.Token);
        }
        catch (Exception error) { failure = error.ToString(); }
        journal?.Write($"Layout-only self-test completed: passed={failure is null}; cases={measurements.Count}; failure={failure}");
        File.WriteAllText(reportFile, JsonSerializer.Serialize(new
        {
            passed = failure is null, failure, pid = Environment.ProcessId, version = AppVersion.Current().Label,
            layoutOnly = true, syntheticError = true, loopbackOnly = true,
            captureStarted = false, nativeItemProofStarted = pickerStarted,
            captureScope = "none; owned synthetic window capture-item metadata only; no real picker or compatibility chooser",
            encoderStarted = false, audioStarted = false, settingsWritten = false,
            inputCoverage = "Native ChangeView and Tab key down/up, gated before each key to this exact foreground HWND/PID; no picker UI, wheel, touch or physical keyboard.",
            measurements
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task CheckHomeLayoutAsync(List<object> measurements, DiagnosticLog? journal, CancellationToken token)
    {
        if (!isolated || source is not null || receiver is null || roles.Role != CastRole.Idle || video.IsAttached)
            throw new InvalidOperationException("Layout checks require an isolated, idle loopback receiver without capture or playback.");
        journal?.Write("Layout-only phase: waiting for the native XAML root and finite client layout");
        for (var attempt = 0; attempt < 30 && (WindowRoot.XamlRoot is null || WindowRoot.ActualHeight <= 0); attempt++)
            await Task.Delay(100, token);
        if (WindowRoot.XamlRoot is null || WindowRoot.ActualHeight <= 0)
            throw new InvalidOperationException("The native XAML root did not load within three seconds.");
        var oldTitle = Problem.Title;
        var oldMessage = Problem.Message;
        var oldOpen = Problem.IsOpen;
        var oldSize = AppWindow.ClientSize;
        try
        {
            foreach (var (width, height) in new[] { (760, 360), (1100, 400) })
            foreach (var errorOpen in new[] { false, true })
            {
                journal?.Write($"Layout-only phase: width={width}, height={height}, errorOpen={errorOpen}");
                var scale = WindowRoot.XamlRoot.RasterizationScale;
                AppWindow.ResizeClient(new((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale)));
                Problem.Title = "Synthetic AV1 send failure — layout regression only";
                Problem.Message = string.Join("\n", Enumerable.Range(1, 32).Select(line =>
                    $"Synthetic detail {line:00}: AV1 send failed. Choose a supported codec in Settings; the receiver remains ready."));
                Problem.IsOpen = errorOpen;
                await SettleAsync();
                var actual = AppWindow.ClientSize;
                if (WindowRoot.ActualHeight > 460 || WindowRoot.ActualHeight < 250 ||
                    (WindowRoot.ActualWidth < 900) != (width < 900))
                    throw new InvalidOperationException($"Requested short {(width < 900 ? "stacked" : "two-column")} viewport not achieved: client={actual.Width}x{actual.Height}, XAML={WindowRoot.ActualWidth}x{WindowRoot.ActualHeight}.");
                if (Grid.GetRow(ReceiveCard) != (width < 900 ? 1 : 0) ||
                    HomeScroll.ViewportHeight <= 0 || HomeScroll.ScrollableHeight <= 0)
                    throw new InvalidOperationException("Home has no usable scrolling viewport or lost its responsive cards.");

                await ScrollAsync(0);
                journal?.Write("Layout-only phase: checking header and banner bounds");
                var topOffset = HomeScroll.VerticalOffset;
                RequireVisible(SettingsButton);
                var bannerAtTop = !errorOpen || IntersectsViewport(Problem);
                if (!bannerAtTop || errorOpen && Problem.ActualHeight <= HomeScroll.ViewportHeight)
                    throw new InvalidOperationException("The long synthetic error was not laid out, or is not reachable at the top.");
                if (errorOpen)
                {
                    var banner = Bounds(Problem);
                    await ScrollAsync(Math.Min(HomeScroll.ScrollableHeight, banner.Bottom - HomeScroll.ViewportHeight));
                    var bottom = Bounds(Problem).Bottom;
                    if (bottom < 0 || bottom > HomeScroll.ViewportHeight + 2)
                        throw new InvalidOperationException("The end of the error details cannot be scrolled into the viewport.");
                }
                await ScrollAsync(HomeScroll.ScrollableHeight);
                journal?.Write("Layout-only phase: checking footer bounds");
                var bottomOffset = HomeScroll.VerticalOffset;
                RequireVisible(ConnectionHelp);
                RequireVisible(VersionText);
                if (bottomOffset <= topOffset)
                    throw new InvalidOperationException("Native scrolling did not move the home content.");
                var footerAtBottom = Bounds(ConnectionHelp);
                var versionAtBottom = Bounds(VersionText);

                await ScrollAsync(0);
                journal?.Write("Layout-only phase: checking foreground-owned Tab input and focus bring-into-view");
                if (!SettingsButton.Focus(FocusState.Keyboard))
                    throw new InvalidOperationException("Could not start keyboard navigation on the home screen.");
                var focusMoves = 0;
                while (!ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), ConnectionHelp) && focusMoves++ < 24)
                {
                    SendLayoutKey();
                    await SettleAsync();
                }
                if (!ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), ConnectionHelp))
                    throw new InvalidOperationException("Tab navigation did not reach Connection help.");
                RequireVisible(ConnectionHelp);
                var tabOffset = HomeScroll.VerticalOffset;
                await ScrollAsync(0);
                RequireVisible(SettingsButton);
                measurements.Add(new
                {
                    requestedWidth = width, requestedHeight = height, clientWidth = actual.Width, clientHeight = actual.Height,
                    xamlWidth = WindowRoot.ActualWidth, xamlHeight = WindowRoot.ActualHeight, scale,
                    errorOpen, detailLines = errorOpen ? 32 : 0, stacked = Grid.GetRow(ReceiveCard) == 1,
                    viewportHeight = HomeScroll.ViewportHeight, extentHeight = HomeScroll.ExtentHeight,
                    scrollableHeight = HomeScroll.ScrollableHeight, topOffset, bottomOffset, tabOffset, focusMoves,
                    bannerHeight = errorOpen ? Problem.ActualHeight : 0, bannerAtTop, bannerEndReachable = errorOpen,
                    footerReachable = true, versionReachable = true,
                    footerAtBottom = new { footerAtBottom.Top, footerAtBottom.Bottom },
                    versionAtBottom = new { versionAtBottom.Top, versionAtBottom.Bottom },
                    tabBringIntoView = true, topReturnReachable = true
                });
            }
        }
        finally
        {
            Problem.Title = oldTitle;
            Problem.Message = oldMessage;
            Problem.IsOpen = oldOpen;
            AppWindow.ResizeClient(oldSize);
            HomeScroll.ChangeView(null, 0, null, true);
        }

        Rect Bounds(FrameworkElement element) => element.TransformToVisual(HomeScroll)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bool IntersectsViewport(FrameworkElement element)
        {
            var bounds = Bounds(element);
            return bounds.Bottom > 0 && bounds.Top < HomeScroll.ViewportHeight;
        }
        void RequireVisible(FrameworkElement element)
        {
            var bounds = Bounds(element);
            if (bounds.Top < -2 || bounds.Bottom > HomeScroll.ViewportHeight + 2 || bounds.Height <= 0)
                throw new InvalidOperationException($"{element.Name} is clipped: top={bounds.Top}, bottom={bounds.Bottom}, viewport={HomeScroll.ViewportHeight}.");
        }
        async Task SettleAsync()
        {
            await Task.Delay(100, token);
            WindowRoot.UpdateLayout();
        }
        async Task ScrollAsync(double offset)
        {
            HomeScroll.ChangeView(null, Math.Max(0, offset), null, true);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await SettleAsync();
                if (Math.Abs(HomeScroll.VerticalOffset - Math.Max(0, offset)) <= 2) return;
            }
            throw new InvalidOperationException($"Native scroll failed: target={offset}, actual={HomeScroll.VerticalOffset}.");
        }
    }
}
