using System.ComponentModel;
using RedrainCast.Media;
using RedrainCast.Receive.Media;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class VideoRenderWorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionWorkerRegistersPlaybackAndRevertsOnItsLongRunningThread(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var stop = new CancellationTokenSource();
        var caller = Environment.CurrentManagedThreadId;
        var messages = new List<string>();
        Exception? reported = null;
        var renderError = new IOException("render");
        var stopped = false;
        var task = Task.Factory.StartNew(() => VideoRenderWorker.Run(() =>
        {
            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
            Assert.NotEqual(caller, Environment.CurrentManagedThreadId);
            Assert.Contains("stage=mmcss_priority_set", messages[^1]);
            if (!cancel) throw renderError;
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
        }, () => stopped = true, stop.Token, error => reported = error, messages.Add),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        if (cancel) await task.WaitAsync(TimeSpan.FromSeconds(15));
        else Assert.Same(renderError, await Assert.ThrowsAsync<IOException>(
            () => task.WaitAsync(TimeSpan.FromSeconds(15))));
        Assert.True(stopped);
        Assert.Equal(cancel ? null : renderError, reported);
        Assert.Equal(4, messages.Count);
        Assert.All(messages, message =>
            Assert.Contains("event=RECEIVER_VIDEO_THREAD", message));
        Assert.All(messages, message =>
            Assert.Contains("mmcss_task=Playback mmcss_priority=AVRT_PRIORITY_NORMAL", message));
        Assert.Single(messages.Select(message => message.Split("native_tid=")[1].Split(' ')[0]).Distinct());
        Assert.Contains("stage=mmcss_reverted", messages[^1]);
    }

    [Fact]
    public void SuccessfulReturnStopsPlaybackBeforeReverting()
    {
        var native = new Native();
        Run(native, () => native.Calls.Add("render"), () => native.Calls.Add("stop"));
        Assert.Equal(["register:Playback", "priority:0", "render", "stop", "revert"], native.Calls);
        Assert.Single(native.Threads.Distinct());
    }

    [Fact]
    public void PreCanceledWorkerDoesNotRegisterOrRender()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var native = new Native();
        Run(native, () => Assert.Fail("rendered"), () => native.Calls.Add("stop"), stop.Token);
        Assert.Equal(["stop"], native.Calls);
    }

    [Fact]
    public void CancellationDuringRegistrationStillRevertsWithoutRendering()
    {
        using var stop = new CancellationTokenSource();
        var native = new Native();
        VideoRenderWorker.Run(() => Assert.Fail("rendered"), () => native.Calls.Add("stop"),
            stop.Token, _ => Assert.Fail("cancellation became failure"), _ => { },
            () => new MediaThreadScheduling(MediaThreadTask.Playback, message =>
            {
                if (message.Contains("stage=mmcss_priority_set")) stop.Cancel();
            }, native));
        Assert.Equal(["register:Playback", "priority:0", "stop", "revert"], native.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeSetupFailureStopsWithoutRenderingAndReportsVideoFailure(bool registration)
    {
        var native = new Native { RegisterFails = registration, PriorityFails = !registration };
        Exception? reported = null;
        var messages = new List<string>();
        var error = Assert.Throws<Win32Exception>(() => Run(native,
            () => Assert.Fail("rendered"), () => native.Calls.Add("stop"),
            failed: failure => reported = failure, diagnostic: messages.Add));
        Assert.Same(error, reported);
        Assert.Equal(5, error.NativeErrorCode);
        Assert.Contains("VIDEO_FAILED:", error.Message);
        Assert.Contains("render thread", error.Message);
        Assert.DoesNotContain("AUDIO_FAILED", error.Message);
        Assert.Contains(messages, message => message.Contains("stage=failure") && message.Contains("win32=5"));
        Assert.Equal(!registration, native.Calls.Contains("revert"));
        Assert.Equal("stop", native.Calls[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupFailureIsFatalEvenAfterCancellation(bool cancel)
    {
        using var stop = new CancellationTokenSource();
        var native = new Native { RevertFails = true };
        Exception? reported = null;
        var error = Assert.Throws<Win32Exception>(() => Run(native, () =>
        {
            if (!cancel) return;
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
        }, () => native.Calls.Add("stop"), stop.Token, failure => reported = failure));
        Assert.Same(error, reported);
        Assert.Contains("VIDEO_FAILED: AvRevertMmThreadCharacteristics", error.Message);
        Assert.Equal(1, native.Calls.Count(call => call == "revert"));
    }

    [Fact]
    public void OriginalRenderAndBothCleanupFailuresReachHealthAndTask()
    {
        var native = new Native { RevertFails = true };
        var render = new IOException("original render");
        var cleanup = new IOException("awake cleanup");
        Exception? reported = null;
        var error = Assert.Throws<AggregateException>(() => Run(native, () => throw render,
            () => throw cleanup, failed: failure => reported = failure));
        Assert.Same(error, reported);
        var failures = error.Flatten().InnerExceptions;
        Assert.Equal(3, failures.Count);
        Assert.Contains(render, failures);
        Assert.Contains(cleanup, failures);
        Assert.Contains(failures, failure => failure is Win32Exception);
        Assert.Equal(1, native.Calls.Count(call => call == "revert"));
    }

    [Theory]
    [InlineData("mmcss_registered")]
    [InlineData("mmcss_priority_set")]
    [InlineData("mmcss_reverted")]
    [InlineData("failure")]
    public void DiagnosticObserverCannotLeakLeaseOrMaskNativeFailure(string stage)
    {
        var native = new Native { PriorityFails = stage == "failure" };
        var observer = new IOException("diagnostic");
        Exception? reported = null;
        var error = Assert.ThrowsAny<Exception>(() => Run(native, () => { }, () => { },
            failed: failure => reported = failure, diagnostic: message =>
            {
                if (message.Contains("stage=" + stage)) throw observer;
            }));
        Assert.Same(error, reported);
        if (stage == "failure")
        {
            var failures = Assert.IsType<AggregateException>(error).Flatten().InnerExceptions;
            Assert.Contains(failures, failure => failure is Win32Exception);
            Assert.Contains(observer, failures);
        }
        else Assert.Same(observer, error);
        Assert.Equal(1, native.Calls.Count(call => call == "revert"));
    }

    [Fact]
    public void ProductionRenderTaskUsesWorkerAndHealthAndAwaitsBeforeResourceDisposal()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "VideoPlayback.cs.txt"));
        Assert.Contains("Task.Factory.StartNew(RenderLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)", source);
        var loop = source[source.IndexOf("private void RenderLoop()", StringComparison.Ordinal)..
            source.IndexOf("public async ValueTask DisposeAsync()", StringComparison.Ordinal)];
        Assert.Contains("VideoRenderWorker.Run(RenderFrames, () => awake.SetPlaying(false), renderCancellation.Token,", loop);
        Assert.Contains("error => Interlocked.CompareExchange(ref renderFailure, error, null)", loop);
        Assert.Contains("message => status(message).AsTask().GetAwaiter().GetResult()", loop);
        Assert.Contains("while (true)", loop);
        Assert.DoesNotContain("await ", loop);
        Assert.Contains("Volatile.Read(ref renderFailure)", source);
        var dispose = source[source.IndexOf("public async ValueTask DisposeAsync()", StringComparison.Ordinal)..];
        Assert.True(dispose.IndexOf("await renderTask", StringComparison.Ordinal) <
            dispose.IndexOf("decoder.Dispose()", StringComparison.Ordinal));
    }

    private static void Run(Native native, Action render, Action stop, CancellationToken token = default,
        Action<Exception>? failed = null, Action<string>? diagnostic = null) =>
        VideoRenderWorker.Run(render, stop, token, failed ?? (_ => Assert.Fail("unexpected failure")),
            diagnostic ?? (_ => { }), () => new MediaThreadScheduling(MediaThreadTask.Playback, diagnostic, native));

    private sealed class Native : MediaThreadScheduling.INative
    {
        internal readonly List<string> Calls = [];
        internal readonly List<int> Threads = [];
        internal bool RegisterFails, PriorityFails, RevertFails;
        public uint ThreadId => (uint)Environment.CurrentManagedThreadId;
        public int LastError => 5;
        public nint Register(string name, ref uint index)
        {
            Record("register:" + name);
            return RegisterFails ? 0 : 42;
        }
        public bool Priority(nint handle, int priority) { Record("priority:" + priority); return !PriorityFails; }
        public bool Revert(nint handle) { Record("revert"); return !RevertFails; }
        private void Record(string call) { Calls.Add(call); Threads.Add(Environment.CurrentManagedThreadId); }
    }
}
