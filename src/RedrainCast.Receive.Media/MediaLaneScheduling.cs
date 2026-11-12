using System.Runtime.InteropServices;

namespace RedrainCast.Receive.Media;

internal sealed partial class MediaLaneScheduling : IDisposable
{
    internal static bool IsEnabled(string? setting) => setting != "0";
    private readonly string lane;
    private readonly Action<string> diagnostic;
    private readonly Func<uint> currentThread;
    private readonly uint threadId;
    private IDisposable? registration;

    internal MediaLaneScheduling(string lane, string task, Func<IDisposable> register,
        Action<string> diagnostic, Func<uint>? currentThread = null)
    {
        this.lane = lane;
        this.diagnostic = diagnostic;
        this.currentThread = currentThread ?? GetCurrentThreadId;
        threadId = this.currentThread();
        try
        {
            Report("thread_start", task);
            registration = register();
            Report("mmcss_registered", task);
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            try { diagnostic($"event=RECEIVER_MEDIA_THREAD lane={lane} stage=failure native_tid={threadId} error={error.GetType().Name}: {error.Message}"); }
            catch (Exception observer) { failures.Add(observer); }
            try { Dispose(); }
            catch (Exception cleanup) { failures.Add(cleanup); }
            if (failures.Count > 1) throw new AggregateException(failures);
            throw;
        }
    }

    private void Report(string stage, string task) =>
        diagnostic($"event=RECEIVER_MEDIA_THREAD lane={lane} stage={stage} native_tid={threadId} mmcss_task={task} mmcss_priority={(task == "Playback" ? "AVRT_PRIORITY_NORMAL" : "existing_Audio_policy")}");

    public void Dispose()
    {
        if (registration is null) return;
        if (currentThread() != threadId)
            throw new InvalidOperationException("Media lane MMCSS scope must retire on its owning native thread.");
        var owned = registration;
        registration = null;
        try { owned.Dispose(); }
        catch (Exception error)
        {
            try { diagnostic($"event=RECEIVER_MEDIA_THREAD lane={lane} stage=mmcss_revert_failed native_tid={threadId}"); }
            catch (Exception observer) { throw new AggregateException(error, observer); }
            throw;
        }
        diagnostic($"event=RECEIVER_MEDIA_THREAD lane={lane} stage=mmcss_reverted native_tid={threadId}");
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
