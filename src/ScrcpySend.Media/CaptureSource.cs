using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ScrcpySend.Core;
using Windows.Graphics.Capture;
using Windows.Graphics.Display;
using WinRT;

namespace ScrcpySend.Media;

public sealed record CaptureTarget(string Name, nint Handle, bool IsWindow)
{
    public CaptureSource Open() => new(CaptureInterop.CreateItem(Handle, IsWindow ? 3 : 4), Handle, IsWindow);
    public override string ToString() => Name;
}

public sealed partial class CaptureSource
{
    public GraphicsCaptureItem Item { get; }
    public nint Handle { get; }
    public bool IsWindow { get; }
    private readonly object colorGate = new();
    private DisplayInformation? display;
    private nint displayMonitor;

    internal CaptureSource(GraphicsCaptureItem item, nint handle, bool isWindow)
    {
        Item = item;
        Handle = handle;
        IsWindow = isWindow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint data);
    private delegate bool WindowCallback(nint window, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int capacity);
    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string value, uint length, out nint result);
    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint value);
    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint name, in Guid iid, out nint result);

    public static IReadOnlyList<CaptureTarget> Enumerate()
    {
        var targets = new List<CaptureTarget>();
        Exception? failure = null;
        if (!EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (!GetMonitorInfoW(monitor, ref info))
            {
                failure = new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read capture display information.");
                return false;
            }
            targets.Add(new($"{info.Device.Replace(@"\\.\", "")} ({info.Bounds.Right - info.Bounds.Left} x {info.Bounds.Bottom - info.Bounds.Top})", monitor, false));
            return true;
        }, 0)) throw failure ?? new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate capture displays.");
        if (!EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            var title = new StringBuilder(512);
            if (GetWindowTextW(window, title, title.Capacity) > 0) targets.Add(new(title.ToString(), window, true));
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate capture windows.");
        return targets;
    }

    public CaptureColor ReadColor()
    {
        var monitor = IsWindow ? MonitorFromWindow(Handle, 2) : Handle;
        if (monitor == 0) throw new IOException("The capture source no longer has a display.");
        lock (colorGate)
        {
            if (display is null || displayMonitor != monitor)
            {
                display = OpenDisplay(monitor);
                displayMonitor = monitor;
            }
            var color = display.GetAdvancedColorInfo();
            if (color.CurrentAdvancedColorKind != AdvancedColorKind.HighDynamicRange) return CaptureColor.Sdr;
            var profile = new CaptureColor(true, color.SdrWhiteLevelInNits, color.MaxLuminanceInNits);
            profile.Validate();
            return profile;
        }
    }

    private static unsafe DisplayInformation OpenDisplay(nint monitor)
    {
        const string className = "Windows.Graphics.Display.DisplayInformation";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out var text));
        nint factory = 0, pointer = 0;
        try
        {
            var interop = new Guid("7449121c-382b-4705-8da7-a795ba482013");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, in interop, out factory));
            var iid = new Guid("bed112ae-adc3-4dc9-ae65-851f4d7d4799");
            var get = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[7];
            Marshal.ThrowExceptionForHR(get(factory, monitor, &iid, &pointer));
            return MarshalInterface<DisplayInformation>.FromAbi(pointer);
        }
        finally
        {
            if (pointer != 0) Marshal.Release(pointer);
            if (factory != 0) Marshal.Release(factory);
            WindowsDeleteString(text);
        }
    }
}
