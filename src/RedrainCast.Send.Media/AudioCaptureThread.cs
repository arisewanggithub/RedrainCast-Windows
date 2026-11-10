namespace RedrainCast.Send.Media;

internal static class AudioCaptureThread
{
    internal static Task RunAsync(Action<CancellationToken> capture, CancellationToken token,
        Func<IDisposable> apartment, Func<IDisposable> scheduling, Action<Exception> cleanupFailure)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? error = null;
            IDisposable? apartmentLease = null, schedulingLease = null;
            try
            {
                token.ThrowIfCancellationRequested();
                apartmentLease = apartment();
                schedulingLease = scheduling();
                token.ThrowIfCancellationRequested();
                capture(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception caught) { error = caught; }
            finally
            {
                Release(schedulingLease);
                Release(apartmentLease);
                if (error is null) completion.TrySetResult();
                else completion.TrySetException(error);
            }

            void Release(IDisposable? resource)
            {
                try { resource?.Dispose(); }
                catch (Exception caught)
                {
                    error = error is null ? caught : new AggregateException(error, caught);
                    try { cleanupFailure(caught); }
                    catch (Exception callbackError) { error = new AggregateException(error, callbackError); }
                }
            }
        }) { IsBackground = true, Name = "RedrainCast WASAPI capture" };
        try { thread.Start(); }
        catch (Exception error) { completion.TrySetException(error); }
        return completion.Task;
    }
}
