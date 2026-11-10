using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RedrainCast.Media;

internal enum MediaThreadTask { Audio, Capture, Playback }

internal sealed partial class MediaThreadScheduling : IDisposable
{
    private readonly uint threadId;
    private readonly Action<string>? diagnostic;
    private readonly MediaThreadTask task;
    private readonly INative native;
    private nint handle;

    internal MediaThreadScheduling(MediaThreadTask task, Action<string>? diagnostic, INative? native = null)
    {
        this.task = task;
        this.native = native ?? Native.Instance;
        threadId = this.native.ThreadId;
        this.diagnostic = diagnostic;
        Report("thread_start");
        uint taskIndex = 0;
        handle = this.native.Register(task.ToString(), ref taskIndex);
        if (handle == 0) throw NativeFailure($"AvSetMmThreadCharacteristicsW({task})");
        try
        {
            Report("mmcss_registered");
            // Normal relative priority is conservative, not realtime process priority.
            if (!this.native.Priority(handle, 0))
                throw NativeFailure("AvSetMmThreadPriority(AVRT_PRIORITY_NORMAL)");
            Report("mmcss_priority_set");
        }
        catch (Exception error)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
            throw;
        }
    }

    public void Dispose()
    {
        if (handle == 0) return;
        if (native.ThreadId != threadId)
            throw new InvalidOperationException("MMCSS registration must be reverted on its owning media thread.");
        var owned = handle;
        handle = 0;
        if (!native.Revert(owned))
            throw NativeFailure("AvRevertMmThreadCharacteristics");
        Report("mmcss_reverted");
    }

    private Exception NativeFailure(string operation)
    {
        var code = native.LastError;
        var role = task == MediaThreadTask.Playback ? "render" : "capture";
        var error = new Win32Exception(code, $"{(task == MediaThreadTask.Audio ? "AUDIO_FAILED" : "VIDEO_FAILED")}: {operation} failed on {role} thread {threadId} (Win32 {code}).");
        try { Report($"failure operation={operation} win32={code}"); }
        catch (Exception observerError) { return new AggregateException(error, observerError); }
        return error;
    }

    private void Report(string stage)
    {
        var name = task == MediaThreadTask.Playback ? "RECEIVER_VIDEO" :
            task == MediaThreadTask.Audio ? "SENDER_AUDIO" : "SENDER_VIDEO";
        var message = $"event={name}_THREAD stage={stage} native_tid={threadId} mmcss_task={task} mmcss_priority=AVRT_PRIORITY_NORMAL";
        if (task == MediaThreadTask.Audio)
        {
            // Preserve the audio observer's existing best-effort contract.
            try { diagnostic?.Invoke(message); }
            catch { }
        }
        else diagnostic?.Invoke(message);
    }

    internal interface INative
    {
        uint ThreadId { get; }
        int LastError { get; }
        nint Register(string taskName, ref uint taskIndex);
        bool Priority(nint handle, int priority);
        bool Revert(nint handle);
    }

    private sealed class Native : INative
    {
        internal static readonly Native Instance = new();
        public uint ThreadId => GetCurrentThreadId();
        public int LastError => Marshal.GetLastPInvokeError();
        public nint Register(string taskName, ref uint taskIndex) => AvSetMmThreadCharacteristics(taskName, ref taskIndex);
        public bool Priority(nint handle, int priority) => AvSetMmThreadPriority(handle, priority);
        public bool Revert(nint handle) => AvRevertMmThreadCharacteristics(handle);
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
