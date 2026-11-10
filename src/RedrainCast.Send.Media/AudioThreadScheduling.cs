using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RedrainCast.Send.Media;

internal sealed partial class AudioThreadScheduling : IDisposable
{
    private readonly uint threadId = GetCurrentThreadId();
    private readonly Action<string>? diagnostic;
    private nint handle;

    internal AudioThreadScheduling(Action<string>? diagnostic)
    {
        this.diagnostic = diagnostic;
        Report("thread_start");
        uint taskIndex = 0;
        handle = AvSetMmThreadCharacteristics("Audio", ref taskIndex);
        if (handle == 0) throw NativeFailure("AvSetMmThreadCharacteristicsW(Audio)");
        Report("mmcss_registered");
        // Audio's normal relative priority is a conservative starting point, not realtime process priority.
        if (!AvSetMmThreadPriority(handle, 0))
        {
            var error = NativeFailure("AvSetMmThreadPriority(AVRT_PRIORITY_NORMAL)");
            try { Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
            throw error;
        }
        Report("mmcss_priority_set");
    }

    public void Dispose()
    {
        if (handle == 0) return;
        if (GetCurrentThreadId() != threadId)
            throw new InvalidOperationException("MMCSS registration must be reverted on its capture thread.");
        var owned = handle;
        handle = 0;
        if (!AvRevertMmThreadCharacteristics(owned))
            throw NativeFailure("AvRevertMmThreadCharacteristics");
        Report("mmcss_reverted");
    }

    private Win32Exception NativeFailure(string operation)
    {
        var code = Marshal.GetLastPInvokeError();
        Report($"failure operation={operation} win32={code}");
        return new Win32Exception(code, $"AUDIO_FAILED: {operation} failed on capture thread {threadId} (Win32 {code}).");
    }

    private void Report(string stage)
    {
        try { diagnostic?.Invoke($"event=SENDER_AUDIO_THREAD stage={stage} native_tid={threadId} mmcss_task=Audio mmcss_priority=AVRT_PRIORITY_NORMAL"); }
        catch { }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvSetMmThreadPriority(nint handle, int priority);

    [LibraryImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(nint handle);
}
