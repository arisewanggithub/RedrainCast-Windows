namespace RedrainCast.App;

internal static class PickerOperationLifetime
{
    public static async Task<T> AwaitAsync<T>(Task<T> completion, Action cancel, Action close,
        CancellationToken cancellation)
    {
        Exception? cancelFailure = null;
        var registration = cancellation.Register(() =>
        {
            try { cancel(); }
            catch (Exception error) { Interlocked.CompareExchange(ref cancelFailure, error, null); }
        });
        try
        {
            T result;
            try { result = await completion; }
            catch (Exception error) when (cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException("Source selection was cancelled.", error, cancellation);
            }
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException("Source selection was cancelled.", cancelFailure, cancellation);
            return result;
        }
        finally
        {
            registration.Dispose();
            close();
        }
    }
}
