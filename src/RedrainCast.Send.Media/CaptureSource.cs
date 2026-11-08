using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RedrainCast.Send.Core;
using RedrainCast.Protocol;
using Windows.Graphics.Capture;
using Windows.Graphics.Display;
using WinRT;

namespace RedrainCast.Send.Media;

public sealed record CaptureTarget(string Name, nint Handle, bool IsWindow)
{
    public uint ProcessId { get; init; }
    public uint ThreadId { get; init; }
    public int DisplayNumber { get; init; }
    public int DisplayWidth { get; init; }
    public int DisplayHeight { get; init; }
    public CaptureSource Open()
    {
        if (!CaptureSource.IsAvailable(this))
            throw new IOException("This source closed or is no longer available. Choose another tile or reopen the picker to refresh.");
        var source = new CaptureSource(CaptureInterop.CreateItem(Handle, IsWindow ? 3 : 4), Handle, IsWindow);
        try
        {
            if (!CaptureSource.IsAvailable(this))
                throw new IOException("This source closed or changed while it was opening. Choose a source again.");
            source.EnsureAvailable();
            return source;
        }
        catch { source.Dispose(); throw; }
    }
    public override string ToString() => Name;
}

public sealed partial class CaptureSource : IDisposable
{
    public GraphicsCaptureItem Item { get; }
    public nint Handle { get; }
    public bool IsWindow { get; }
    public uint WindowProcessId { get; }
    public uint WindowThreadId { get; }
    private readonly CaptureSelectionLifetime selection = new();
    private readonly Windows.Foundation.TypedEventHandler<GraphicsCaptureItem, object> sourceClosed;
    private int disposed;
    public CancellationToken Closed => selection.Closed;
    private readonly object colorGate = new();
    private DisplayInformation? display;
    private nint displayMonitor;
    private string? metadataWarning;

    internal CaptureSource(GraphicsCaptureItem item, nint handle, bool isWindow)
    {
        Item = item;
        Handle = handle;
        IsWindow = isWindow;
        if (isWindow)
        {
            WindowThreadId = GetWindowThreadProcessId(handle, out var process);
            WindowProcessId = process;
        }
        var weak = new WeakReference<CaptureSelectionLifetime>(selection);
        sourceClosed = (_, _) => { if (weak.TryGetTarget(out var state)) state.Invalidate(); };
        item.Closed += sourceClosed;
    }

    public static bool SupportsNativePicker(nint owner) => CaptureInterop.SupportsItemHandles(owner);

    public static CaptureSource FromPickedItem(GraphicsCaptureItem item)
    {
        var target = CaptureInterop.ItemTarget(item);
        var source = new CaptureSource(item, target.Handle, target.IsWindow);
        try { source.EnsureAvailable(); return source; }
        catch { source.Dispose(); throw; }
    }

    public void EnsureAvailable() => selection.EnsureAvailable(IsAvailable(new("", Handle, IsWindow)
        { ProcessId = WindowProcessId, ThreadId = WindowThreadId }));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Item.Closed -= sourceClosed;
        selection.Dispose();
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
    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WindowExists(nint window);
    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);
    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();
    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);
    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, uint attribute, out uint value, uint size);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int capacity);
    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string value, uint length, out nint result);
    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint value);
    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint name, in Guid iid, out nint result);

    private static bool EligibleWindow(nint window) =>
        WindowExists(window) && IsWindowVisible(window) && !IsIconic(window) &&
        GetAncestor(window, 2) == window && window != GetShellWindow() &&
        (GetWindowLongPtrW(window, -20).ToInt64() & 0x80) == 0 &&
        DwmGetWindowAttribute(window, 14, out var cloaked, 4) >= 0 && cloaked == 0 &&
        GetWindowRect(window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;

    public static bool IsAvailable(CaptureTarget target)
    {
        if (target.IsWindow)
        {
            var thread = GetWindowThreadProcessId(target.Handle, out var process);
            return EligibleWindow(target.Handle) && (target.ProcessId == 0 || process == target.ProcessId) &&
                (target.ThreadId == 0 || thread == target.ThreadId);
        }
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
        return GetMonitorInfoW(target.Handle, ref info);
    }

    public static IReadOnlyList<CaptureTarget> Enumerate(nint excludedWindow = 0)
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
            targets.Add(new($"Display {targets.Count + 1} ({info.Bounds.Right - info.Bounds.Left} × {info.Bounds.Bottom - info.Bounds.Top})", monitor, false)
            {
                DisplayNumber = targets.Count + 1,
                DisplayWidth = info.Bounds.Right - info.Bounds.Left,
                DisplayHeight = info.Bounds.Bottom - info.Bounds.Top
            });
            return true;
        }, 0)) throw failure ?? new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate capture displays.");
        if (!EnumWindows((window, _) =>
        {
            if (window == excludedWindow || !EligibleWindow(window)) return true;
            var title = new StringBuilder(512);
            if (GetWindowTextW(window, title, title.Capacity) > 0)
            {
                var thread = GetWindowThreadProcessId(window, out var process);
                targets.Add(new(title.ToString(), window, true) { ProcessId = process, ThreadId = thread });
            }
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate capture windows.");
        return targets;
    }

    public CaptureColor ReadColor(Action<string>? diagnostic = null)
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
            if (color.CurrentAdvancedColorKind != AdvancedColorKind.HighDynamicRange)
            {
                metadataWarning = null;
                return CaptureColor.Sdr;
            }
            var profile = CaptureColor.FromHdrDisplayMetadata(color.SdrWhiteLevelInNits, color.MaxLuminanceInNits);
            var warning = profile.SdrWhiteNits is null || profile.PeakNits is null
                ? $"Display HDR luminance metadata is unavailable: rawWhite={color.SdrWhiteLevelInNits}, rawPeak={color.MaxLuminanceInNits}. " +
                  "HDR remains enabled; BT.2390 uses the GPU-measured content peak, not these display values."
                : null;
            if (warning is not null && warning != metadataWarning) diagnostic?.Invoke(warning);
            metadataWarning = warning;
            profile.Validate();
            return profile;
        }
    }

    public HdrStaticMetadata? ReadHdrMetadata(Action<string>? diagnostic = null)
    {
        if (IsWindow) throw new NotSupportedException("HDR desktop color-volume metadata requires whole-display capture.");
        lock (colorGate)
        {
            if (display is null || displayMonitor != Handle)
            {
                display = OpenDisplay(Handle);
                displayMonitor = Handle;
            }
            var color = display.GetAdvancedColorInfo();
            if (color.CurrentAdvancedColorKind != AdvancedColorKind.HighDynamicRange)
                throw new NotSupportedException("The selected display is no longer HDR-active. Restart sharing to renegotiate color.");
            var red = color.RedPrimary;
            var green = color.GreenPrimary;
            var blue = color.BluePrimary;
            var white = color.WhitePoint;
            return HdrDisplayMetadata.Create(color.MaxLuminanceInNits, color.MinLuminanceInNits,
                (red.X, red.Y), (green.X, green.Y), (blue.X, blue.Y), (white.X, white.Y), diagnostic);
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
