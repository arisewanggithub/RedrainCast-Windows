using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace RedrainCast.App;

internal sealed class SystemLanguageNotifications : IDisposable
{
    private readonly Window window;
    private readonly nint handle;
    private readonly SubclassProc callback;
    private readonly Action reconcile;
    private bool disposed;
    private const nuint SubclassId = 0x52434C;
    public SystemLanguageNotifications(Window window, Action reconcile)
    {
        this.window = window;
        this.reconcile = reconcile;
        handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        callback = WindowMessage;
        if (!SetWindowSubclass(handle, callback, SubclassId, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        window.Activated += Activated;
    }

    private void Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) reconcile();
    }

    private nint WindowMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Preference changes use different setting names; the observer coalesces reads and skips unchanged languages.
        if (message == 0x001A) reconcile();
        // PBT_APMRESUMESUSPEND / PBT_APMRESUMEAUTOMATIC: reconcile after a missed suspended notification.
        if (message == 0x0218 && wParam is 7 or 18) reconcile();
        if (message == 0x0082) RemoveWindowSubclass(hwnd, callback, SubclassId);
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.Activated -= Activated;
        RemoveWindowSubclass(handle, callback, SubclassId);
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
