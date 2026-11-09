using RedrainCast.Send.Media;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--capture-worker")
        {
            CaptureWorker.RunAsync(args[1..]).GetAwaiter().GetResult();
            return Environment.ExitCode;
        }

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new RedrainCast.App.App();
        });
        return Environment.ExitCode;
    }
}
