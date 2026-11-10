using System.ComponentModel;
using RedrainCast.Media;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class CaptureThreadSchedulingTests
{
    [Theory]
    [InlineData("Audio", "AUDIO")]
    [InlineData("Capture", "VIDEO")]
    public void TasksSelectNormalPriorityAndDistinctDiagnostics(string taskName, string kind)
    {
        var task = Enum.Parse<MediaThreadTask>(taskName);
        var native = new Native();
        var messages = new List<string>();
        using (new MediaThreadScheduling(task, messages.Add, native))
            Assert.Equal(["register:" + task, "priority:0"], native.Calls);
        Assert.Equal(["register:" + task, "priority:0", "revert"], native.Calls);
        Assert.Equal(4, messages.Count);
        Assert.All(messages, message =>
        {
            Assert.Contains($"event=SENDER_{kind}_THREAD", message);
            Assert.Contains($"native_tid=17 mmcss_task={task} mmcss_priority=AVRT_PRIORITY_NORMAL", message);
        });
        Assert.Contains("stage=mmcss_reverted", messages[^1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeSetupFailuresPropagateAndPriorityFailureReverts(bool registration)
    {
        var native = new Native { RegisterFails = registration, PriorityFails = !registration };
        var messages = new List<string>();
        var error = Assert.Throws<Win32Exception>(() =>
            new MediaThreadScheduling(MediaThreadTask.Capture, messages.Add, native));
        Assert.Equal(5, error.NativeErrorCode);
        Assert.Contains("VIDEO_FAILED:", error.Message);
        Assert.Contains(messages, message => message.Contains("stage=failure operation=") && message.Contains("win32=5"));
        Assert.Equal(!registration, native.Calls.Contains("revert"));
    }

    [Fact]
    public void PriorityAndRevertFailuresAreBothRetained()
    {
        var native = new Native { PriorityFails = true, RevertFails = true };
        var error = Assert.Throws<AggregateException>(() =>
            new MediaThreadScheduling(MediaThreadTask.Capture, null, native));
        Assert.Equal(2, error.Flatten().InnerExceptions.Count);
        Assert.Contains("AvSetMmThreadPriority", error.InnerExceptions[0].Message);
        Assert.Contains("AvRevertMmThreadCharacteristics", error.InnerExceptions[1].Message);
        Assert.Equal(1, native.Calls.Count(call => call == "revert"));
    }

    [Fact]
    public void WrongThreadCannotConsumeRegistrationAndDoubleDisposeDoesNotRevertTwice()
    {
        var native = new Native();
        var lease = new MediaThreadScheduling(MediaThreadTask.Capture, null, native);
        native.ThreadId = 18;
        Assert.Throws<InvalidOperationException>(lease.Dispose);
        Assert.DoesNotContain("revert", native.Calls);
        native.ThreadId = 17;
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, native.Calls.Count(call => call == "revert"));
    }

    [Theory]
    [InlineData("mmcss_registered")]
    [InlineData("mmcss_priority_set")]
    public void VideoObserverSetupFailureDoesNotLeakRegistration(string stage)
    {
        var native = new Native();
        var expected = new IOException("observer");
        var error = Assert.Throws<IOException>(() => new MediaThreadScheduling(MediaThreadTask.Capture,
            message => { if (message.Contains("stage=" + stage)) throw expected; }, native));
        Assert.Same(expected, error);
        Assert.Equal(1, native.Calls.Count(call => call == "revert"));
    }

    [Fact]
    public void NativeFailureIsNotMaskedByVideoObserver()
    {
        var native = new Native { PriorityFails = true };
        var error = Assert.Throws<AggregateException>(() => new MediaThreadScheduling(MediaThreadTask.Capture,
            message => { if (message.Contains("stage=failure")) throw new IOException("observer"); }, native));
        Assert.IsType<Win32Exception>(error.InnerExceptions[0]);
        Assert.IsType<IOException>(error.InnerExceptions[1]);
        Assert.Contains("revert", native.Calls);
    }

    [Fact]
    public void AudioRetainsBestEffortObserverContract()
    {
        var native = new Native();
        using (new MediaThreadScheduling(MediaThreadTask.Audio, _ => throw new IOException("observer"), native)) { }
        Assert.Equal(["register:Audio", "priority:0", "revert"], native.Calls);
    }

    [Fact]
    public async Task ActualWindowsCaptureRegistrationAndRevertUseExistingLongRunningThread()
    {
        if (!OperatingSystem.IsWindows()) return;
        var caller = Environment.CurrentManagedThreadId;
        var messages = new List<string>();
        await Task.Factory.StartNew(() =>
        {
            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
            Assert.NotEqual(caller, Environment.CurrentManagedThreadId);
            using var lease = new MediaThreadScheduling(MediaThreadTask.Capture, messages.Add);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.Equal(4, messages.Count);
        Assert.All(messages, message => Assert.Contains("event=SENDER_VIDEO_THREAD", message));
        Assert.All(messages, message => Assert.Contains("mmcss_task=Capture mmcss_priority=AVRT_PRIORITY_NORMAL", message));
        Assert.Single(messages.Select(message => message.Split("native_tid=")[1].Split(' ')[0]).Distinct());
        Assert.Contains("stage=mmcss_reverted", messages[^1]);
    }

    [Fact]
    public async Task CaptureCancellationDuringRegistrationRevertsOnWorker()
    {
        using var stop = new CancellationTokenSource();
        var native = new Native();
        var messages = new List<string>();
        await Task.Factory.StartNew(() =>
        {
            using var lease = new MediaThreadScheduling(MediaThreadTask.Capture, message =>
            {
                messages.Add(message);
                if (message.Contains("stage=mmcss_priority_set")) stop.Cancel();
            }, native);
            Assert.Throws<OperationCanceledException>(() => stop.Token.ThrowIfCancellationRequested());
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.Equal(["register:Capture", "priority:0", "revert"], native.Calls);
        Assert.Contains("stage=mmcss_reverted", messages[^1]);
    }

    [Fact]
    public void RevertNativeFailureIsReportedAndNotRetriedWithConsumedHandle()
    {
        var native = new Native { RevertFails = true };
        var messages = new List<string>();
        var lease = new MediaThreadScheduling(MediaThreadTask.Capture, messages.Add, native);
        var error = Assert.Throws<Win32Exception>(lease.Dispose);
        Assert.Contains("VIDEO_FAILED: AvRevertMmThreadCharacteristics", error.Message);
        Assert.Contains(messages, message => message.Contains("stage=failure operation=AvRevertMmThreadCharacteristics win32=5"));
        lease.Dispose();
        Assert.Equal(1, native.Calls.Count(call => call == "revert"));
    }

    [Fact]
    public void ProductionVideoLeaseEnclosesStartupLoopAndCleanupBeforeChannelCompletion()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "WindowsVideoSource.cs.txt"));
        Assert.Contains("Task.Factory.StartNew(() => Capture(queue, signals.Writer, linked.Token),", source);
        Assert.Contains("CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)", source);
        source = source[source.IndexOf("private void Capture(", StringComparison.Ordinal)..];
        var acquire = source.IndexOf("scheduling = new MediaThreadScheduling(MediaThreadTask.Capture, diagnostic);", StringComparison.Ordinal);
        var release = source.IndexOf("scheduling?.Dispose();", StringComparison.Ordinal);
        Assert.True(acquire > source.IndexOf("token.ThrowIfCancellationRequested();", StringComparison.Ordinal));
        Assert.True(acquire < source.IndexOf("using var dispatcher", StringComparison.Ordinal));
        Assert.Contains("diagnostic);\n            token.ThrowIfCancellationRequested();", source.Replace("\r\n", "\n"));
        Assert.True(release > source.IndexOf("processor?.Dispose();", StringComparison.Ordinal));
        Assert.True(release > source.IndexOf("catch (OperationCanceledException)", StringComparison.Ordinal));
        Assert.True(release > source.IndexOf("catch (Exception error)", StringComparison.Ordinal));
        Assert.True(release < source.IndexOf("signals.TryComplete(failure)", StringComparison.Ordinal));
        Assert.Contains("new AggregateException(failure, cleanupError)", source);
        Assert.Contains("System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();", source);
        Assert.Contains("finally { progress.Complete(); signals.TryComplete(failure); }", source);
        Assert.DoesNotContain("await ", source[..source.IndexOf("public async ValueTask DisposeAsync", StringComparison.Ordinal)]);
    }

    private sealed class Native : MediaThreadScheduling.INative
    {
        public uint ThreadId { get; set; } = 17;
        public int LastError => 5;
        public bool RegisterFails, PriorityFails, RevertFails;
        public List<string> Calls { get; } = [];
        public nint Register(string taskName, ref uint taskIndex)
        {
            Calls.Add("register:" + taskName);
            return RegisterFails ? 0 : 42;
        }
        public bool Priority(nint handle, int priority) { Calls.Add("priority:" + priority); return !PriorityFails; }
        public bool Revert(nint handle) { Calls.Add("revert"); return !RevertFails; }
    }
}
