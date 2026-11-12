using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RedrainCast.Media;
using RedrainCast.Receive.Core;
using RedrainCast.Receive.Media;
using Xunit;
using Xunit.Abstractions;

namespace RedrainCast.Receive.Tests;

public sealed partial class MediaLaneAffinityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void ReceiverUsesProductionLanesUnlessExplicitlyDisabled(string? setting, bool expected) =>
        Assert.Equal(expected, MediaLaneScheduling.IsEnabled(setting));

    private sealed class Lease(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(40, false)]
    [InlineData(40, true)]
    public async Task FramingAdmissionAndDecodeResumeOnRegisteredDedicatedLane(int pauseAt, bool diagnostics)
    {
        using var encoded = new MemoryStream();
        await Wire.WriteAsync(encoded, new(PacketType.VideoFrame, 1, 1, 1, 10, 0, [1, 2, 3, 4]));
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var threads = new ConcurrentBag<int>();
        var phases = new List<string>();
        var entered = 0;
        var retired = 0;
        var laneThread = 0;
        void Check()
        {
            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
            Assert.Equal(1, Volatile.Read(ref entered));
            Assert.Equal(laneThread, Environment.CurrentManagedThreadId);
            threads.Add(Environment.CurrentManagedThreadId);
        }
        using var stream = new PausedStream(encoded.ToArray(), pauseAt, paused, release, Check);
        using var boundary = new BoundaryReadStream(stream);
        var worker = TrackThread.Run(async () =>
        {
            Check();
            Action<VideoReadObservation>? observe = diagnostics ? observation =>
            {
                Check();
                phases.Add(observation.Stage);
            } : null;
            var packet = await Wire.ReadAsync(boundary, diagnostic: observe);
            Check();
            Assert.Equal(4, packet.Payload.Length);
            admitted.SetResult();
            await admission.Task;
            Check();
            await Task.Yield();
            Check();
        }, () =>
        {
            laneThread = Environment.CurrentManagedThreadId;
            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
            Volatile.Write(ref entered, 1);
            return new Lease(() =>
            {
                Check();
                Volatile.Write(ref entered, 0);
                Interlocked.Increment(ref retired);
            });
        });
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(worker.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref retired));
        release.SetResult();
        if (await Task.WhenAny(admitted.Task, worker).WaitAsync(TimeSpan.FromSeconds(10)) == worker)
            await worker;
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(worker.IsCompleted);
        admission.SetResult();
        await worker.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, retired);
        Assert.Single(threads.Distinct());
        if (diagnostics)
            Assert.Equal(["read_begin", "header_begin", "header_end", "payload_begin", "payload_end", "read_end"], phases);
        else Assert.Empty(phases);
    }

    [Fact]
    public async Task CongestedVideoAdmissionDoesNotBlockAudioLaneAndRetiresOnOwner()
    {
        using var queue = new ReceiveQueue(8);
        var first = new Packet(PacketType.VideoFrame, 1, 1, 1, 10, 0, new byte[8]);
        var next = first with { PtsUs = 20 };
        queue.Add(first);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = 0;
        var retired = 0;
        var video = TrackThread.Run(async () =>
        {
            waiting.SetResult();
            await queue.AddAsync(next, CancellationToken.None);
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
        }, () =>
        {
            thread = Environment.CurrentManagedThreadId;
            return new Lease(() =>
            {
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
                Interlocked.Increment(ref retired);
            });
        });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await TrackThread.Run(async () =>
        {
            Assert.NotEqual(thread, Environment.CurrentManagedThreadId);
            await Task.Yield();
            Assert.NotEqual(thread, Environment.CurrentManagedThreadId);
            var packet = await queue.TakeAsync(1, CancellationToken.None);
            queue.Complete(packet);
        }).WaitAsync(TimeSpan.FromSeconds(10));
        await video.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, retired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndFaultRetireWithoutLeakingRegistration(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var owner = 0;
        var retired = 0;
        var worker = TrackThread.Run(async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellation.Token);
            throw new IOException("decode failed");
        }, () =>
        {
            owner = Environment.CurrentManagedThreadId;
            return new Lease(() =>
            {
                Assert.Equal(owner, Environment.CurrentManagedThreadId);
                Assert.False(Thread.CurrentThread.IsThreadPoolThread);
                retired++;
            });
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (cancel) cancellation.Cancel(); else release.SetResult();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
        else await Assert.ThrowsAsync<IOException>(() => worker.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, retired);
        await Task.Run(() => Assert.NotEqual(owner, Environment.CurrentManagedThreadId));
    }

    [Fact]
    public async Task RegistrationFailureDoesNotRunMediaOrSilentlyFallback()
    {
        var failure = new Win32Exception(5);
        var worker = TrackThread.Run(() => throw new InvalidOperationException("ran media"),
            () => throw failure);
        Assert.Same(failure, await Assert.ThrowsAsync<Win32Exception>(() => worker));
    }

    [Fact]
    public async Task OriginalMediaAndScopeCleanupFailuresAreBothReported()
    {
        var original = new IOException("media");
        var cleanup = new Win32Exception(5);
        var worker = TrackThread.Run(() => Task.FromException(original),
            () => new Lease(() => throw cleanup));
        var error = await Assert.ThrowsAsync<AggregateException>(() => worker);
        Assert.Contains(original, error.InnerExceptions);
        Assert.Contains(cleanup, error.InnerExceptions);
    }

    [Fact]
    public async Task TaskCompletingOffLaneStillWakesAndRetiresAffineLoop()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = 0;
        var worker = TrackThread.Run(() =>
        {
            entered.SetResult();
            return release.Task;
        }, () => new Lease(() => retired++));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        await worker.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, retired);
    }

    [Fact]
    public void LifecycleObserverFailureStillRevertsAndWrongThreadCannotRetire()
    {
        uint thread = 10;
        var retired = 0;
        var messages = new List<string>();
        Assert.Throws<IOException>(() => new MediaLaneScheduling("video_read", "Playback",
            () => new Lease(() => retired++), message =>
            {
                messages.Add(message);
                if (message.Contains("stage=mmcss_registered")) throw new IOException("observer");
            }, () => thread));
        Assert.Equal(1, retired);
        Assert.Contains(messages, x => x.Contains("stage=failure"));
        var lease = new MediaLaneScheduling("audio_read", "Audio",
            () => new Lease(() => retired++), messages.Add, () => thread);
        thread = 11;
        Assert.Throws<InvalidOperationException>(lease.Dispose);
        Assert.Equal(1, retired);
        thread = 10;
        lease.Dispose();
        Assert.Equal(2, retired);
    }

    [Theory]
    [InlineData(2, "video_read")]
    [InlineData(2, "video_decode")]
    [InlineData(0, "audio_read")]
    public async Task NativeWindowsRegistrationSpansSuspendedContinuations(int taskValue, string lane)
    {
        if (!OperatingSystem.IsWindows()) return;
        var task = (MediaThreadTask)taskValue;
        var messages = new ConcurrentQueue<string>();
        uint tid = 0;
        await TrackThread.Run(async () =>
        {
            tid = GetCurrentThreadId();
            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
            await Task.Yield();
            Assert.Equal(tid, GetCurrentThreadId());
        }, () => new MediaLaneScheduling(lane, task.ToString(),
            () => new MediaThreadScheduling(task, messages.Enqueue), messages.Enqueue))
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains(messages, x => x.Contains("stage=mmcss_reverted"));
        Assert.All(messages, x => Assert.Contains($"native_tid={tid}", x));
        foreach (var message in messages) output.WriteLine(message);
    }

    [Fact]
    public void ProductionReaderGateAndVideoDecodeRegistrationAreWired()
    {
        var server = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ReceiverServer.cs.txt"));
        var playback = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "VideoPlayback.cs.txt"));
        Assert.Contains("AffineMediaReaders: true", server);
        Assert.Contains("mediaPlayback.EnterMediaReader(mediaTrack)", server);
        Assert.Contains("REDRAIN_MEDIA_MMCSS_LANES", playback);
        Assert.Contains("PlaybackLane(\"video_decode\")", playback);
        Assert.Contains("PlaybackLane(\"video_read\")", playback);
        Assert.Contains("\"audio_read\", \"Audio\"", playback);
    }

    private sealed class PausedStream(byte[] bytes, int pauseAt, TaskCompletionSource paused,
        TaskCompletionSource release, Action check) : Stream
    {
        private int position;
        private bool didPause;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            check();
            if (position == pauseAt && !didPause)
            {
                didPause = true;
                paused.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                check();
            }
            var count = Math.Min(2, Math.Min(buffer.Length, bytes.Length - position));
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
