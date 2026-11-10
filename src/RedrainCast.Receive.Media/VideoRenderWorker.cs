using System.Runtime.ExceptionServices;
using RedrainCast.Media;

namespace RedrainCast.Receive.Media;

internal static class VideoRenderWorker
{
    // Called synchronously by the existing long-running render task, never by its caller.
    internal static void Run(Action render, Action stopPlayback, CancellationToken token,
        Action<Exception> failed, Action<string> diagnostic, Func<IDisposable>? register = null)
    {
        IDisposable? scheduling = null;
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            scheduling = register is null
                ? new MediaThreadScheduling(MediaThreadTask.Playback, diagnostic)
                : register();
            token.ThrowIfCancellationRequested();
            render();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally
        {
            try { stopPlayback(); }
            catch (Exception error) { Record(error); }
            try { scheduling?.Dispose(); }
            catch (Exception error) { Record(error); }
        }
        if (failure is not null)
        {
            failed(failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        void Record(Exception error) =>
            failure = failure is null ? error : new AggregateException(failure, error);
    }
}
