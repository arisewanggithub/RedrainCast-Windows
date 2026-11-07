using RedrainCast.Send.Media;

Console.Error.WriteLine($"Capture worker bootstrap: pid={Environment.ProcessId}; dedicated worker entry (no WinUI).");
if (args.Length == 0 || args[0] != "--capture-worker")
{
    Console.Error.WriteLine("Capture worker requires --capture-worker --prepare-source-v2 and source identity, settings and expected dimensions.");
    return 1;
}
await CaptureWorker.RunAsync(args[1..]);
return Environment.ExitCode;
