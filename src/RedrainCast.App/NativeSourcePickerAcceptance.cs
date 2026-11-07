using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScrcpySend.Media;
using RedrainCast.Core;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    private async Task CheckNativeSelectionAsync(List<object> measurements, DiagnosticLog? journal, CancellationToken token)
    {
        if (!isolated || roles.Role != CastRole.Idle || source is not null)
            throw new InvalidOperationException("Native item acceptance requires the isolated idle fixture.");
        var fixture = new Window
        {
            Title = "Owned native source identity fixture",
            Content = new Grid { Background = new SolidColorBrush(Colors.Lime) }
        };
        var closed = false;
        try
        {
            fixture.AppWindow.ResizeClient(new(640, 360));
            fixture.Activate();
            await Task.Delay(150, token);
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(fixture);
            journal?.Write("Native item acceptance: own 640x360 window; no picker UI or capture session is opened");
            using var representative = new CaptureTarget("Own native item fixture", handle, true)
                { ProcessId = (uint)Environment.ProcessId }.Open();
            using var selection = CaptureSource.FromPickedItem(representative.Item);
            if (!ReferenceEquals(selection.Item, representative.Item) || selection.Handle != handle ||
                !selection.IsWindow || selection.WindowProcessId != Environment.ProcessId ||
                selection.WindowThreadId == 0 || !CaptureSource.SupportsNativePicker(handle))
                throw new InvalidOperationException("Public native handle identity or original-item retention failed.");
            selection.EnsureAvailable();
            var color = selection.ReadColor();
            measurements.Add(new
            {
                nativeItem = "Own CreateForWindow representative; not a natural OS-picker selection",
                exactPublicGetWindow = true, originalItemRetained = true, ownerProcessThreadChecked = true,
                sourceMonitorColorRead = true, hdr = color.Hdr,
                realPickerOpened = false, compatibilityChooserOpened = false, captureStarted = false
            });
            using var preparing = CancellationTokenSource.CreateLinkedTokenSource(selection.Closed, token);
            fixture.Close();
            closed = true;
            for (var attempt = 0; attempt < 50 && !selection.Closed.IsCancellationRequested; attempt++)
                await Task.Delay(50, token);
            if (!selection.Closed.IsCancellationRequested || !preparing.IsCancellationRequested)
                throw new InvalidOperationException("Original-item closure did not cancel source preparation.");
            try
            {
                selection.EnsureAvailable();
                throw new InvalidOperationException("A closed original selection became available again.");
            }
            catch (IOException) { }
            try
            {
                using var stale = new CaptureTarget("Closed own fixture", handle, true)
                    { ProcessId = selection.WindowProcessId, ThreadId = selection.WindowThreadId }.Open();
                throw new InvalidOperationException("Worker-style opening accepted the closed own fixture.");
            }
            catch (IOException) { }
            measurements.Add(new
            {
                nativeItem = "Closed original item latches invalidation and cancels preparation",
                staleHandleRejected = true, noWorkerOrEncoderStarted = true, ownWindowClosed = true
            });
        }
        finally
        {
            if (!closed) fixture.Close();
            Activate();
        }
    }
}
