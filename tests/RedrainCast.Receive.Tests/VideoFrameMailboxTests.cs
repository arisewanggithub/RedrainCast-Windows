using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using RedrainCast.Receive.Media;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class VideoFrameMailboxTests
{
    private static readonly TimeSpan timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RendererConsumesWhileProducerIsBusyDecodingNextPacket()
    {
        using var wake = new AutoResetEvent(false);
        using var decoding = new ManualResetEventSlim();
        using var finishDecode = new ManualResetEventSlim();
        var decoderGate = new object();
        var mailbox = Create(() => wake.Set());
        var frame = new Frame();
        var producer = Task.Run(() =>
        {
            lock (decoderGate) mailbox.Publish(frame, mailbox.Revision);
            lock (decoderGate)
            {
                decoding.Set();
                Assert.True(finishDecode.Wait(timeout));
            }
        });
        try
        {
            Assert.True(decoding.Wait(timeout));
            var renderer = Task.Run(() =>
            {
                Assert.True(wake.WaitOne(timeout));
                Assert.Same(frame, mailbox.Take());
                Assert.False(finishDecode.IsSet);
                frame.Release("presented");
            });
            await renderer.WaitAsync(timeout);
        }
        finally
        {
            finishDecode.Set();
            await producer.WaitAsync(timeout);
        }
        Assert.Equal(1, frame.Releases);
    }

    [Fact]
    public void BurstKeepsOnlyLatestAndCoalescesWakeWithoutGrowingQueue()
    {
        var wakes = 0;
        var mailbox = Create(() => wakes++);
        var frames = Enumerable.Range(0, 100).Select(_ => new Frame()).ToArray();
        foreach (var frame in frames) Assert.True(mailbox.Publish(frame, mailbox.Revision));
        Assert.Equal(1, wakes);
        Assert.All(frames[..^1], frame => Assert.Equal("pending_replaced", frame.Reason));
        Assert.Same(frames[^1], mailbox.Take());
        Assert.Null(mailbox.Take());
        frames[^1].Release("presented");
        Assert.All(frames, frame => Assert.Equal(1, frame.Releases));
        var next = new Frame();
        mailbox.Publish(next, mailbox.Revision);
        Assert.Equal(2, wakes);
        mailbox.Reset("disposed", close: true);
        Assert.Equal(1, next.Releases);
    }

    [Fact]
    public async Task NotificationAndReplacementReleaseRunOutsideMailboxLock()
    {
        VideoFrameMailbox<Frame>? mailbox = null;
        var notifications = 0;
        mailbox = new VideoFrameMailbox<Frame>(() =>
        {
            Assert.True(Task.Run(() => mailbox!.HasPending).WaitAsync(timeout).GetAwaiter().GetResult());
            notifications++;
        }, (frame, reason) =>
        {
            Assert.True(Task.Run(() => mailbox!.HasPending).WaitAsync(timeout).GetAwaiter().GetResult());
            frame.Release(reason);
        });
        var first = new Frame();
        var second = new Frame();
        await Task.Run(() =>
        {
            mailbox.Publish(first, mailbox.Revision);
            mailbox.Publish(second, mailbox.Revision);
        }).WaitAsync(timeout);
        Assert.Equal(1, notifications);
        Assert.Equal(1, first.Releases);
        Assert.Same(second, mailbox.Take());
        second.Release("presented");
    }

    [Fact]
    public void BusyPresentationReturnsOwnershipWithoutReplacingNewerFrame()
    {
        var mailbox = Create();
        var busy = new Frame();
        mailbox.Publish(busy, mailbox.Revision);
        Assert.Same(busy, mailbox.Take());
        mailbox.Return(busy, mailbox.Revision);
        Assert.Equal(0, busy.Releases);
        Assert.Same(busy, mailbox.Take());
        var latest = new Frame();
        mailbox.Publish(latest, mailbox.Revision);
        mailbox.Return(busy, mailbox.Revision);
        Assert.Equal("pending_replaced", busy.Reason);
        Assert.Same(latest, mailbox.Take());
        latest.Release("presented");
        Assert.Equal(1, busy.Releases);
        Assert.Equal(1, latest.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetOrCancellationRetiresPendingAndRejectsLateDecodeAndReturn(bool cancel)
    {
        using var decoded = new ManualResetEventSlim();
        using var publish = new ManualResetEventSlim();
        var mailbox = Create();
        var revision = mailbox.Revision;
        var inflight = new Frame();
        mailbox.Publish(inflight, revision);
        Assert.Same(inflight, mailbox.Take());
        var pending = new Frame();
        mailbox.Publish(pending, revision);
        var late = new Frame();
        var producer = Task.Run(() =>
        {
            decoded.Set();
            Assert.True(publish.Wait(timeout));
            Assert.False(mailbox.Publish(late, revision));
        });
        try
        {
            Assert.True(decoded.Wait(timeout));
            mailbox.Reset(cancel ? "disposed" : "configuration_reset", close: cancel);
            Assert.Equal(0, inflight.Releases); // The renderer, not reset, owns this reference.
            mailbox.Return(inflight, revision);
        }
        finally
        {
            publish.Set();
            await producer.WaitAsync(timeout);
        }
        Assert.False(mailbox.HasPending);
        Assert.All(new[] { pending, inflight, late }, frame => Assert.Equal(1, frame.Releases));
        var next = new Frame();
        Assert.Equal(!cancel, mailbox.Publish(next, mailbox.Revision));
        mailbox.Reset("disposed", close: true);
        Assert.Equal(1, next.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRedrawRetainsDisplayedOwnershipAndRequestWhileNewFrameCanPublish(bool throws)
    {
        var mailbox = Create();
        var requests = new PresentationRequestState();
        var displayed = new Frame();
        mailbox.Publish(displayed, mailbox.Revision);
        Assert.Same(displayed, mailbox.Take());
        Assert.True(requests.TryRender(HdrVideoFormat.Sdr, _ => true));
        requests.SetViewport(1280, 720, 1, 1);
        var newer = new Frame();
        bool Redraw() => requests.TryRender(HdrVideoFormat.Sdr, _ =>
        {
            mailbox.Publish(newer, mailbox.Revision);
            if (throws) throw new IOException("redraw failure");
            return false;
        });
        if (throws) Assert.Throws<IOException>(() => Redraw());
        else Assert.False(Redraw());
        Assert.True(requests.HasPending);
        Assert.Equal(0, displayed.Releases);
        Assert.Same(newer, mailbox.Take());
        Assert.True(requests.TryRender(HdrVideoFormat.Sdr, _ => true));
        displayed.Release("replaced_displayed");
        newer.Release("disposed");
        Assert.All(new[] { displayed, newer }, frame => Assert.Equal(1, frame.Releases));
    }

    [Fact]
    public void ProductionRendererDoesNotAcquireDecoderGateOrUseTryEnterFallback()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "VideoPlayback.cs.txt"));
        var render = source[source.IndexOf("private void RenderFrames()", StringComparison.Ordinal)..
            source.IndexOf("public async ValueTask DisposeAsync()", StringComparison.Ordinal)];
        Assert.DoesNotContain("decoderGate", render);
        Assert.DoesNotContain("TryEnter", render);
        Assert.Contains("lock (renderGate)", render);
        Assert.Contains("mailbox.Take(out var revision)", render);
        Assert.Contains("mailbox.Return(frame, revision)", render);
        Assert.Contains("presenter.Present(frame.Surface)", render);
        Assert.Contains("presenter.Redraw(lastFrame.Surface)", render);
        Assert.Contains("WaitForRender(token, delay, hasWork && !occluded && !videoSuspended && !sessionStopped)", render);
        Assert.Contains("presenter.WaitForRender(renderWake, token.WaitHandle, delay, readinessEligible,", render);
        Assert.Contains("wake == 2 ? \"readiness\"", render);
        Assert.DoesNotContain("trace.ReadinessActive\n                ? Wait", render);
        Assert.Contains("trace.ReadinessStage(\"render_gate_wait\"", render);
        Assert.Contains("trace.ReadinessStage(\"mailbox_take\"", render);
        var lockedRender = Block(render, "lock (renderGate)");
        Assert.DoesNotContain("audio?.Position", lockedRender);
        Assert.DoesNotContain("status(", lockedRender);
        Assert.Contains("sample.Revision == mailbox.Revision", render);
        var present = source[source.IndexOf("public async ValueTask PresentAsync", StringComparison.Ordinal)..
            source.IndexOf("private void RenderLoop()", StringComparison.Ordinal)];
        Assert.DoesNotContain("presenter.Present", present);
        Assert.Contains("mailbox.Publish(", present);
    }

    private static string Block(string source, string marker)
    {
        var begin = source.IndexOf('{', source.IndexOf(marker, StringComparison.Ordinal));
        var depth = 1;
        for (var index = begin + 1; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            if (source[index] == '}' && --depth == 0) return source[begin..(index + 1)];
        }
        throw new InvalidOperationException("Unclosed source block.");
    }

    private static VideoFrameMailbox<Frame> Create(Action? notify = null) =>
        new(notify ?? (() => { }), (frame, reason) => frame.Release(reason));

    private sealed class Frame
    {
        public int Releases { get; private set; }
        public string? Reason { get; private set; }
        public void Release(string reason)
        {
            Assert.Equal(0, Releases);
            Releases++;
            Reason = reason;
        }
    }
}
