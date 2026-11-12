using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace RedrainCast.Media;

internal static class MediaLaneThread
{
    internal static bool IsCurrent => SynchronizationContext.Current is SerialContext;
    internal static ValueTask ReadExactlyAsync(Stream input, Memory<byte> bytes, CancellationToken token) =>
        IsCurrent ? ReadAffineAsync(input, bytes, token) : input.ReadExactlyAsync(bytes, token);

    private static async ValueTask ReadAffineAsync(Stream input, Memory<byte> bytes, CancellationToken token)
    {
        // The BCL exact-read loop otherwise re-enters application streams without our context.
        while (!bytes.IsEmpty)
        {
            var count = await input.ReadAsync(bytes, token);
            if (count == 0) throw new EndOfStreamException("Unable to read beyond the end of the stream.");
            bytes = bytes[count..];
        }
    }
    public static Task Run(Func<Task> action, Func<IDisposable?>? register = null) => Task.Factory.StartNew(() =>
    {
        using var context = new SerialContext();
        SynchronizationContext.SetSynchronizationContext(context);
        IDisposable? scheduling = null;
        Exception? failure = null;
        try
        {
            scheduling = register?.Invoke();
            var task = action();
            var completed = task.ContinueWith(_ => context.Wake(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            while (!task.IsCompleted) context.Step();
            completed.GetAwaiter().GetResult();
            task.GetAwaiter().GetResult();
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { scheduling?.Dispose(); }
            catch (Exception cleanup)
            {
                failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
            }
            SynchronizationContext.SetSynchronizationContext(null);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private sealed class SerialContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Add(() => callback(state));
        public void Step()
        {
            callbacks.Take()();
        }
        public void Wake() => callbacks.Add(() => { });
        public void Dispose() => callbacks.Dispose();
    }
}
