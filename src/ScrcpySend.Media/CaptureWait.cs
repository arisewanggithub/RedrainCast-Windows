using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScrcpySend.Media;

internal sealed partial class CaptureWait : WaitHandle
{
    private readonly CancellationToken token;
    private readonly WaitHandle[] waits;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    private static partial nint CreateTimer(nint attributes, nint name, uint flags, uint access);

    [LibraryImport("kernel32.dll", EntryPoint = "SetWaitableTimerEx", SetLastError = true)]
    private static partial int SetTimer(nint timer, in long dueTime, int period, nint completion, nint argument, nint wakeContext, uint tolerance);

    public CaptureWait(CancellationToken token)
    {
        this.token = token;
        var timer = CreateTimer(0, 0, 2, 0x1f0003);
        if (timer == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "High-resolution capture timer creation failed.");
        SafeWaitHandle = new SafeWaitHandle(timer, true);
        waits = [this, token.WaitHandle];
    }

    public void Pause()
    {
        token.ThrowIfCancellationRequested();
        long due = -20_000;
        if (SetTimer(SafeWaitHandle.DangerousGetHandle(), in due, 0, 0, 0, 0, 0) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "High-resolution capture timer scheduling failed.");
        WaitAny(waits);
        token.ThrowIfCancellationRequested();
    }
}
