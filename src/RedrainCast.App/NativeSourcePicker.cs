using ScrcpySend.Media;
using Windows.Graphics.Capture;

namespace RedrainCast.App;

internal static class NativeSourcePicker
{
    public static async Task<CaptureSource?> PickAsync(nint owner, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
        var operation = picker.PickSingleItemAsync();
        var item = await PickerOperationLifetime.AwaitAsync(operation.AsTask(),
            operation.Cancel, operation.Close, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (item is null) return null;
        var source = CaptureSource.FromPickedItem(item);
        if (source.IsWindow && source.Handle == owner)
        {
            source.Dispose();
            throw new UiActionException("ChooseDifferentWindow");
        }
        return source;
    }
}
