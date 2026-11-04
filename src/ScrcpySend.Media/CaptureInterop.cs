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
        GetWindowThreadProcessId(window, out var process);
        if (process != Environment.ProcessId)
            throw new ArgumentException("Capture diagnostics may only capture a window owned by this process.");
        return new(CreateItem(window, 3), window, true);
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
