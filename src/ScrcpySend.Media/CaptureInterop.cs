using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ScrcpySend.Media;

internal static partial class CaptureInterop
{
    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string value, uint length, out nint result);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint value);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint name, in Guid iid, out nint result);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, uint flags);

    public static CaptureSource DefaultDisplay()
    {
        if (!Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows display capture is unavailable. Choose another capture source.");
        var monitor = MonitorFromPoint(new Point { X = 0, Y = 0 }, 1);
        if (monitor == 0) throw new IOException("The primary display is unavailable. Choose a capture source.");
        return new(CreateItem(monitor, 4), monitor, false);
    }

    public static CaptureSource OwnWindowForDiagnostics(nint window)
    {
        EnsureOwnWindow(window);
        return new(CreateItem(window, 3), window, true);
    }

    internal static void EnsureOwnWindow(nint window)
    {
        GetWindowThreadProcessId(window, out var process);
        if (process != Environment.ProcessId)
            throw new ArgumentException("Capture diagnostics may only capture a window owned by this process.");
    }

    internal static unsafe Windows.Graphics.Capture.GraphicsCaptureItem CreateItem(nint target, int method)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out var text));
        nint factory = 0;
        try
        {
            var factoryId = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, in factoryId, out factory));
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[method];
            var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            nint item = 0;
            Marshal.ThrowExceptionForHR(create(factory, target, &itemId, &item));
            try { return MarshalInterface<Windows.Graphics.Capture.GraphicsCaptureItem>.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally
        {
            if (factory != 0) Marshal.Release(factory);
            WindowsDeleteString(text);
        }
    }

    public static IDirect3DDevice CreateDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var pointer));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    // Public Windows.Graphics.Capture.Interop.h interfaces (SDK 10.0.26100.2161).
    // Availability is queried at runtime; their presence in the SDK is not a minimum-OS guarantee.
    private static readonly Guid WindowItemInterop = new("38E4C48B-94E6-4C44-9CFA-968193316C0C");
    private static readonly Guid MonitorItemInterop = new("33274D14-A076-4048-8416-747E9B04DB7B");

    internal static bool SupportsItemHandles(nint owner)
    {
        var item = CreateItem(owner, 3);
        var result = ReadItemHandle(item, WindowItemInterop);
        if (result.HResult == unchecked((int)0x80004002)) return false;
        Marshal.ThrowExceptionForHR(result.HResult);
        if (result.Handle != owner) throw new IOException("Capture-item capability probe returned an unexpected owner handle.");
        return true;
    }

    internal static (nint Handle, bool IsWindow) ItemTarget(Windows.Graphics.Capture.GraphicsCaptureItem item)
    {
        var result = ReadItemHandle(item, WindowItemInterop);
        if (result.HResult >= 0 && result.Handle != 0) return (result.Handle, true);
        if (result.HResult != unchecked((int)0x80004002))
            throw new IOException($"The selected window is unavailable (0x{result.HResult:X8}).");
        result = ReadItemHandle(item, MonitorItemInterop);
        if (result.HResult >= 0 && result.Handle != 0) return (result.Handle, false);
        if (result.HResult == unchecked((int)0x80004002))
            throw new NotSupportedException("This Windows runtime cannot expose the selected source's capture handle.");
        throw new IOException($"The selected display is unavailable (0x{result.HResult:X8}).");
    }

    private static unsafe (int HResult, nint Handle) ReadItemHandle(
        Windows.Graphics.Capture.GraphicsCaptureItem item, Guid iid)
    {
        var inspectable = MarshalInspectable<Windows.Graphics.Capture.GraphicsCaptureItem>.FromManaged(item);
        nint interop = 0, handle = 0;
        try
        {
            var result = Marshal.QueryInterface(inspectable, in iid, out interop);
            if (result < 0) return (result, 0);
            var get = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)interop)[3];
            result = get(interop, &handle);
            return (result, handle);
        }
        finally
        {
            if (interop != 0) Marshal.Release(interop);
            Marshal.Release(inspectable);
        }
    }

    public static unsafe ID3D11Texture2D Texture(IDirect3DSurface surface)
    {
        var pointer = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        var iid = new Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
        nint access = 0;
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(pointer, in iid, out access));
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
            var textureId = typeof(ID3D11Texture2D).GUID;
            nint texture = 0;
            Marshal.ThrowExceptionForHR(getInterface(access, &textureId, &texture));
            return new ID3D11Texture2D(texture);
        }
        finally
        {
            if (access != 0) Marshal.Release(access);
            Marshal.Release(pointer);
        }
    }
}
