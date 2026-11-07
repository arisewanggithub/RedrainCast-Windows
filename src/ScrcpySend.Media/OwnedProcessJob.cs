using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScrcpySend.Media;

internal sealed partial class OwnedProcessJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle CreateJobObjectW(nint attributes, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int informationClass, in ExtendedLimits limits, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    private readonly SafeFileHandle handle;

    public OwnedProcessJob()
    {
        handle = CreateJobObjectW(0, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create capture process job."); }
        var limits = new ExtendedLimits { Basic = new() { Flags = 0x2000 } };
        if (SetInformationJobObject(handle, 9, in limits, (uint)Marshal.SizeOf<ExtendedLimits>())) return;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, "Cannot enforce capture termination when the parent exits.");
    }

    public void Attach(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.SafeHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot isolate the capture process.");
    }

    public void Dispose() => handle.Dispose();
}
