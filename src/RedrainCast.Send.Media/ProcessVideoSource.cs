using System.Diagnostics;
using System.Runtime.CompilerServices;
using RedrainCast.Send.Core;

namespace RedrainCast.Send.Media;

public abstract class ProcessVideoSource : IRecoverableVideoSource
{
    private readonly string executable;
    private readonly string codec;
    private readonly long privateMemoryLimit;
    private readonly Action<string> diagnostic;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly Queue<string> errors = new();
    private readonly TaskCompletionSource<long> firstTimestamp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? process;
    private OwnedProcessJob? job;
    private Task? preparation;
    private Task? readinessRead;
    private Task? stderrTask, monitorTask, shutdownTask;
    private Exception? resourceFailure;
    private int started, processId;
    private bool childExited = true;
    private long packets, lastPts, maximumPrivateBytes;
    private long captureStartedAt, lastPacketAt, lastCaptureReportAt;
    private string? captureStatistics;
    private int captureIdle;
    private string encoderName;
    public string EncoderName => Volatile.Read(ref encoderName);
    public int Width { get; }
    public int Height { get; }
    protected abstract string Kind { get; }
    protected virtual bool ForwardDiagnostics => false;
    protected virtual bool RequiresStartCommand => false;
    protected virtual CancellationToken SourceClosed => CancellationToken.None;
    protected virtual void ValidateSource() { }
    public virtual bool SupportsStreamRecovery => false;
    private readonly SemaphoreSlim inputGate = new(1, 1);
    public async ValueTask RequestKeyFrameAsync(CancellationToken token)
    {
        if (!SupportsStreamRecovery) throw new NotSupportedException("This capture backend cannot request hardware sync frames.");
        await inputGate.WaitAsync(token);
        try
        {
            var child = process ?? throw new InvalidOperationException("Capture worker is not started.");
            await child.StandardInput.WriteLineAsync("key".AsMemory(), token);
            await child.StandardInput.FlushAsync(token);
        }
        finally { inputGate.Release(); }
    }
    protected abstract IEnumerable<string> Arguments();
    public int ProcessId => Volatile.Read(ref processId);
    public bool ChildExited => Volatile.Read(ref childExited);
    public bool StartCommandSent => RequiresStartCommand && Interlocked.Read(ref captureStartedAt) != 0;
    public long PacketCount => Interlocked.Read(ref packets);
    public Task<long> FirstTimestamp => firstTimestamp.Task;
    public string Statistics => $"childPid={ProcessId}, exited={ChildExited}, packets={PacketCount}, " +
        $"lastPtsUs={Interlocked.Read(ref lastPts)}, maximumPrivateBytes={Interlocked.Read(ref maximumPrivateBytes)}; {Volatile.Read(ref captureStatistics)}";

    protected ProcessVideoSource(string executable, int width, int height, string codec, string encoderName,
        Action<string> diagnostic, long privateMemoryLimit = 512L * 1024 * 1024)
    {
        this.executable = Path.GetFullPath(executable);
        if (!File.Exists(this.executable)) throw new FileNotFoundException("Capture executable was not found.", this.executable);
        Width = width;
        Height = height;
        this.codec = codec;
        this.encoderName = encoderName;
        this.privateMemoryLimit = privateMemoryLimit;
        this.diagnostic = diagnostic;
    }

    public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Process capture cannot be reused.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, SourceClosed);
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            if (RequiresStartCommand)
            {
                await PrepareAsync(linked.Token);
                await inputGate.WaitAsync(linked.Token);
                try
                {
                    ValidateSource();
                    await process!.StandardInput.WriteLineAsync("start".AsMemory(), linked.Token);
                    await process.StandardInput.FlushAsync(linked.Token);
                    Interlocked.Exchange(ref captureStartedAt, Environment.TickCount64);
                    diagnostic($"{Kind} START sent after receiver acceptance.");
                }
                finally { inputGate.Release(); }
            }
            else
            {
                StartProcess();
                Interlocked.Exchange(ref captureStartedAt, Environment.TickCount64);
            }
            var reader = new FfmpegPacketReader(process!.StandardOutput.BaseStream, Width, Height);
            await InitializeAsync(reader, linked.Token);
            byte[] configuration = [];
            while (true)
            {
                var frame = await ReadPacketAsync(reader, linked.Token);
                if (configuration.Length == 0)
                {
                    configuration = VideoBitstream.Configuration(codec, frame.Configuration.Length > 0 ? frame.Configuration : frame.Data);
                    VideoBitstream.ValidateConfiguration(codec, configuration, Width, Height);
                    if (!VideoBitstream.IsKeyFrame(codec, frame.Data, configuration))
                        throw new ProtocolException($"{Kind} did not start with a real random-access picture.");
                }
                VideoBitstream.ValidateFrame(codec, frame.Data, configuration);
                if (VideoBitstream.HasConfiguration(codec, frame.Data) &&
                    !VideoBitstream.Configuration(codec, frame.Data).AsSpan().SequenceEqual(configuration))
                    throw new ProtocolException($"{Kind} changed its fixed-format video configuration.");
                firstTimestamp.TrySetResult(frame.PtsUs);
                if (PacketCount < 8)
                {
                    var now = (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);
                    diagnostic($"{Kind} packet timing: ptsUs={frame.PtsUs}, readQpcUs={now}, ageUs={now - frame.PtsUs}");
                }
                Interlocked.Increment(ref packets);
                Interlocked.Exchange(ref lastPacketAt, Environment.TickCount64);
                Interlocked.Exchange(ref lastPts, frame.PtsUs);
                yield return frame with { Configuration = configuration };
            }
        }
        finally { await DisposeAsync(); }
    }

    public ValueTask PrepareAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!RequiresStartCommand) return ValueTask.CompletedTask;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(shutdownTask is not null, this);
            return new(preparation ??= PrepareWorkerAsync(token));
        }
    }

    private async Task PrepareWorkerAsync(CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, SourceClosed);
        limit.CancelAfter(CaptureWorkerHandshake.PreparationTimeout);
        try
        {
            StartProcess();
            readinessRead = CaptureWorkerHandshake.ReadReadyAsync(process!.StandardOutput.BaseStream, Width, Height, codec, limit.Token);
            await readinessRead.WaitAsync(limit.Token);
            ValidateSource();
            diagnostic($"{Kind} READY received; metadata and encoder initialized, capture has not started.");
        }
        catch (OperationCanceledException) when (SourceClosed.IsCancellationRequested)
        {
            throw Failure(new IOException("The selected source closed while the capture worker was preparing."));
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw Failure(resourceFailure ?? new TimeoutException("Capture worker preparation exceeded its separate 30-second bound.", error));
        }
        catch (Exception error) when (error is IOException)
        {
            throw Failure(error);
        }
    }

    private void StartProcess()
    {
        ValidateSource();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var argument in Arguments()) start.ArgumentList.Add(argument);
        process = new Process { StartInfo = start };
        job = new OwnedProcessJob();
        if (!process.Start()) throw new IOException($"{Kind} process could not start.");
        processId = process.Id;
        childExited = false;
        job.Attach(process);
        _ = process.Handle;
        stderrTask = ReadStderrAsync(process.StandardError);
        monitorTask = MonitorAsync(process);
        diagnostic($"{Kind} started: {Statistics}; {codec} {Width}x{Height}; GPU conversion, compressed-packet pipe only.");
    }

    private async Task InitializeAsync(FfmpegPacketReader reader, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await reader.InitializeAsync(deadline.Token); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw Failure(new TimeoutException($"{Kind} supplied no codec header for 10 seconds.", error));
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            if (token.IsCancellationRequested && resourceFailure is null) throw;
            throw Failure(error);
        }
    }

    private async Task<EncodedVideo> ReadPacketAsync(FfmpegPacketReader reader, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!ForwardDiagnostics) deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { return await reader.ReadAsync(deadline.Token); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw Failure(new TimeoutException($"{Kind} supplied no complete encoded packet for 10 seconds.", error));
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            if (token.IsCancellationRequested && resourceFailure is null) throw;
            throw Failure(error);
        }
    }

    private IOException Failure(Exception error)
    {
        lock (gate) return new IOException($"{Kind} failed: {resourceFailure?.Message ?? error.Message}\n" +
            $"{Statistics}\n{string.Join("\n", errors)}", resourceFailure ?? error);
    }

    private async Task ReadStderrAsync(StreamReader stderr)
    {
        while (await stderr.ReadLineAsync() is { } line)
        {
            if (line.Length > 2048) line = line[..2048] + " [truncated]";
            lock (gate)
            {
                errors.Enqueue(line);
                while (errors.Count > 16) errors.Dequeue();
            }
            const string encoderPrefix = "Capture worker encoder: ";
            if (line.StartsWith(encoderPrefix, StringComparison.Ordinal))
                Volatile.Write(ref encoderName, line[encoderPrefix.Length..]);
            if (line.StartsWith("Sender video ", StringComparison.Ordinal))
            {
                Volatile.Write(ref captureStatistics, line);
                Volatile.Write(ref captureIdle,
                    line.Contains("stage=waiting for capture notification,", StringComparison.Ordinal) ? 1 : 0);
                Interlocked.Exchange(ref lastCaptureReportAt, Environment.TickCount64);
            }
            if (ForwardDiagnostics) diagnostic(line);
            else if (line.Contains("MFT name:", StringComparison.Ordinal) ||
                line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("non monoton", StringComparison.OrdinalIgnoreCase))
                diagnostic($"{Kind}: {line}");
            if (line.Contains("non monoton", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.CompareExchange(ref resourceFailure, new IOException($"{Kind} reported a timestamp error: {line}"), null);
                lifetime.Cancel();
            }
        }
    }

    private async Task MonitorAsync(Process child)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (child.HasExited) return;
                child.Refresh();
                if (ForwardDiagnostics)
                {
                    var now = Environment.TickCount64;
                    var idle = Volatile.Read(ref captureIdle) != 0 && now - Interlocked.Read(ref lastCaptureReportAt) < 7500;
                    var captureStart = Interlocked.Read(ref captureStartedAt);
                    var last = PacketCount == 0 ? captureStart : Interlocked.Read(ref lastPacketAt);
                    if (captureStart != 0 && !idle && now - last > 10_000)
                    {
                        Interlocked.CompareExchange(ref resourceFailure,
                            new TimeoutException($"Capture worker stopped producing video without a recent idle capture report. {Statistics}"), null);
                        lifetime.Cancel();
                        return;
                    }
                }
                var memory = child.PrivateMemorySize64;
                Interlocked.Exchange(ref maximumPrivateBytes, Math.Max(Interlocked.Read(ref maximumPrivateBytes), memory));
                if (memory <= privateMemoryLimit) continue;
                Interlocked.CompareExchange(ref resourceFailure, new IOException($"{Kind} exceeded its {privateMemoryLimit / 1024 / 1024} MiB private-memory guard."), null);
                lifetime.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Interlocked.CompareExchange(ref resourceFailure, new IOException($"{Kind} process monitoring failed: {error.Message}", error), null);
            lifetime.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(shutdownTask ??= ShutdownAsync());
    }

    private async Task ShutdownAsync()
    {
        lifetime.Cancel();
        if (process is null) { job?.Dispose(); lifetime.Dispose(); return; }
        try
        {
            var forcedTermination = false;
            if (ProcessId != 0 && !process.HasExited)
            {
                try
                {
                    using var inputTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await inputGate.WaitAsync(inputTimeout.Token);
                    try
                    {
                        await process.StandardInput.WriteLineAsync("q".AsMemory(), inputTimeout.Token);
                        await process.StandardInput.FlushAsync(inputTimeout.Token);
                    }
                    finally { inputGate.Release(); }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException)
                { diagnostic($"{Kind} stop input failed: {error.Message}"); }
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            forcedTermination = true;
                        }
                    }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await process.WaitForExitAsync(killed.Token);
                    if (forcedTermination)
                        diagnostic($"{Kind} required termination after its bounded Stop grace period.");
                }
            }
            if (monitorTask is not null) await monitorTask;
            if (stderrTask is not null) await stderrTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (readinessRead is not null)
            {
                try { await readinessRead.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) when (lifetime.IsCancellationRequested &&
                    error is IOException or OperationCanceledException)
                { diagnostic($"{Kind} preparation pipe retired: {error.Message}"); }
            }
            if (ProcessId != 0 && !forcedTermination && process.HasExited && process.ExitCode != 0)
                throw Failure(new IOException($"{Kind} exited with exit code {process.ExitCode} during shutdown."));
        }
        finally
        {
            try
            {
                childExited = ProcessId == 0 || process.HasExited;
                process.Dispose();
            }
            finally
            {
                job?.Dispose();
                lifetime.Dispose();
                diagnostic($"{Kind} stopped: {Statistics}");
            }
        }
    }
}
