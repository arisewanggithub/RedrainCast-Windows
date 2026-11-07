using System.Globalization;
using System.Text.Json;
using ScrcpySend.Core;

namespace ScrcpySend.Media;

public static class CaptureWorker
{
    public static async Task RunAsync(string[] arguments)
    {
        using var stop = new CancellationTokenSource();
        using var input = Console.OpenStandardInput();
        Task? control = null;
        WindowsVideoSource? active = null;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            arguments = CaptureWorkerHandshake.MetadataArguments(arguments);
            if (arguments.Length != 7 || !long.TryParse(arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var handle) ||
                handle == 0 || arguments[1] is not ("window" or "display") ||
                !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
                !uint.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out var process) ||
                !uint.TryParse(arguments[6], NumberStyles.None, CultureInfo.InvariantCulture, out var thread) ||
                (arguments[1] == "window" && (process == 0 || thread == 0)) ||
                (arguments[1] == "display" && (process != 0 || thread != 0)))
                throw new ArgumentException("Capture worker requires source identity, settings and expected dimensions.");
            var settings = JsonSerializer.Deserialize<CaptureSettings>(arguments[2]) ??
                throw new ArgumentException("Capture settings are missing.");
            settings.Validate();
            using var apartment = new AudioInterop.MtaLease();
            control = ReadStopAsync();
            Console.Error.WriteLine("Capture worker bootstrap: arguments validated; scheduling source initialization.");
            await Task.Run(async () =>
            {
                Console.Error.WriteLine("Capture worker bootstrap: opening selected capture source.");
                using var source = new CaptureTarget("Selected capture source", new nint(handle), arguments[1] == "window")
                    { ProcessId = process, ThreadId = thread }.Open();
                Console.Error.WriteLine("Capture worker bootstrap: source opened; constructing hardware video source.");
                await using var video = new WindowsVideoSource(source, settings, Console.Error.WriteLine);
                active = video;
                if (video.Width != width || video.Height != height)
                    throw new IOException("Capture source dimensions changed before the worker started. Select the source again.");
                Console.Error.WriteLine($"Capture worker encoder: {video.EncoderName}");
                using var output = Console.OpenStandardOutput();
                stop.Token.ThrowIfCancellationRequested();
                await CaptureWorkerHandshake.WriteReadyAsync(output, width, height, settings.VideoCodec, stop.Token);
                Console.Error.WriteLine("Capture worker bootstrap: READY; capture is disabled until START.");
                await start.Task.WaitAsync(stop.Token);
                Console.Error.WriteLine("Capture worker bootstrap: START received; beginning capture.");
                var writer = new PacketPipeWriter(output, width, height);
                await foreach (var frame in video.ReadFramesAsync(stop.Token))
                    await writer.WriteAsync(frame, stop.Token);
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (control?.IsFaulted == true)
            {
                Environment.ExitCode = 1;
                Console.Error.WriteLine($"Capture worker control failure: {control.Exception}");
            }
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            Console.Error.WriteLine($"Capture worker failed: {error}");
        }
        finally
        {
            stop.Cancel();
            if (control is not null)
            {
                try { await control.WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException)
                { Console.Error.WriteLine($"Capture worker control cleanup: {error.Message}"); }
            }
        }

        async Task ReadStopAsync()
        {
            try
            {
                using var reader = new StreamReader(input, leaveOpen: true);
                while (true)
                {
                    var command = await reader.ReadLineAsync(stop.Token);
                    if (command is null or "q") return;
                    if (command == "start")
                    {
                        if (active is null || !start.TrySetResult()) throw new IOException("Unexpected capture worker START.");
                        continue;
                    }
                    if (command != "key") throw new IOException("Unknown capture worker command.");
                    if (!start.Task.IsCompletedSuccessfully) throw new IOException("Capture worker key request preceded START.");
                    Console.Error.WriteLine("Capture worker control: key received after START.");
                    await (active ?? throw new IOException("Capture worker is not ready.")).RequestKeyFrameAsync(stop.Token);
                }
            }
            finally { stop.Cancel(); }
        }
    }
}
