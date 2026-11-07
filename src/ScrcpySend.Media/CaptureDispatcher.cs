using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT;

namespace ScrcpySend.Media;

internal sealed partial class CaptureDispatcher : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Options { public uint Size; public int ThreadType, ApartmentType; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(Options options, out nint controller);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Message message, nint window, uint minimum, uint maximum, uint remove);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Message message);
    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in Message message);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint timeout, uint wakeMask, uint flags);

    private readonly DispatcherQueueController controller;

    public CaptureDispatcher()
    {
        PeekMessageW(out _, 0, 0, 0, 0);
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(
            new() { Size = (uint)Marshal.SizeOf<Options>(), ThreadType = 2, ApartmentType = 0 }, out var pointer));
        try { controller = MarshalInterface<DispatcherQueueController>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    public void Pump()
    {
        while (PeekMessageW(out var message, 0, 0, 0, 1))
        {
            TranslateMessage(in message);
            DispatchMessageW(in message);
        }
    }

    public unsafe void Wait(ValueTask pending)
    {
        if (pending.IsCompleted) { pending.GetAwaiter().GetResult(); return; }
        var task = pending.AsTask();
        using var completed = ((IAsyncResult)task).AsyncWaitHandle;
        var handle = completed.SafeWaitHandle.DangerousGetHandle();
        while (!task.IsCompleted)
        {
            Pump();
            if (task.IsCompleted) break;
            if (MsgWaitForMultipleObjectsEx(1, &handle, uint.MaxValue, 0x04ff, 4) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Capture dispatcher wait failed.");
        }
        task.GetAwaiter().GetResult();
    }

    public void Dispose() => Wait(new(controller.ShutdownQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))));
}
