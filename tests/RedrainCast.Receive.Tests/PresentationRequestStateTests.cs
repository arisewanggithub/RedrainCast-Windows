using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using RedrainCast.Receive.Media;
using Xunit;

namespace RedrainCast.Receive.Tests;

public sealed class PresentationRequestStateTests
{
    private static readonly HdrVideoFormat source = new(true);
    private static readonly TimeSpan timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InflightPresentationOrStaticRedrawCannotConsumeLaterFallbackOrPeakChange(bool redraw, bool peakChange)
    {
        var requests = new PresentationRequestState();
        var original = HdrDisplayPolicy.FromMonitor(true, 250);
        requests.SetDisplayPolicy(original);
        if (redraw)
        {
            Assert.True(requests.TryRender(source, _ => true));
            requests.SetViewport(800, 600, 1, 1);
        }
        var old = requests.Desired;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var inflight = Task.Run(() => requests.TryRender(source, request =>
        {
            Assert.Same(old, request);
            entered.Set();
            Assert.True(release.Wait(timeout));
            return true;
        }));
        try
        {
            Assert.True(entered.Wait(timeout));
            var latestPolicy = peakChange ? HdrDisplayPolicy.FromMonitor(true, 400) :
                HdrDisplayPolicy.FromMonitor(true, float.NaN);
            await Task.Run(() =>
            {
                Assert.True(requests.SetDisplayPolicy(latestPolicy));
                Assert.True(requests.SetViewport(1920, 1080, 1.5f, 1.25f));
            }).WaitAsync(timeout);
            var latest = requests.Desired;
            release.Set();
            Assert.True(await inflight.WaitAsync(timeout));
            Assert.Same(old, requests.Successful);
            Assert.True(requests.HasPending);
            Assert.Equal(VideoPresentationOutput.Hdr, requests.Statistics!.Output);
            Assert.False(requests.SetDisplayPolicy(latestPolicy));
            Assert.True(requests.HasPending); // Deduplicated metadata queries cannot remove the retry.
            Assert.True(requests.TryRender(source, request =>
            {
                Assert.Same(latest, request);
                Assert.Equal(latestPolicy, request.Display);
                Assert.Equal((1920, 1080, 1.5f, 1.25f), (request.Width, request.Height, request.ScaleX, request.ScaleY));
                return true;
            }));
            Assert.False(requests.HasPending);
            Assert.Same(latest, requests.Successful);
            Assert.Equal(peakChange ? VideoPresentationOutput.Hdr : VideoPresentationOutput.SdrToneMapped,
                requests.Statistics!.Output);
            Assert.True(source.Hdr10);
        }
        finally
        {
            release.Set();
            await inflight.WaitAsync(timeout);
        }
    }

    [Fact]
    public void FailedBusyOrOccludedSubmissionRetainsRequestAndLastSuccessfulStatistics()
    {
        var requests = new PresentationRequestState();
        requests.SetDisplayPolicy(HdrDisplayPolicy.FromMonitor(true, 250));
        Assert.True(requests.TryRender(source, _ => true));
        var successful = requests.Successful;
        requests.SetDisplayPolicy(HdrDisplayPolicy.MetadataUnavailable("monitor disconnected"));
        var fallback = requests.Desired;
        for (var retry = 0; retry < 3; retry++)
        {
            Assert.False(requests.TryRender(source, request =>
            {
                Assert.Same(fallback, request);
                return false;
            }));
            Assert.True(requests.HasPending);
            Assert.Same(successful, requests.Successful);
            Assert.Equal(VideoPresentationOutput.Hdr, requests.Statistics!.Output);
        }
        Assert.True(requests.TryRender(source, request => ReferenceEquals(fallback, request)));
        Assert.False(requests.HasPending);
        Assert.Equal(VideoPresentationOutput.SdrToneMapped, requests.Statistics!.Output);
    }

    [Fact]
    public void UpdateImmediatelyBeforeAcknowledgementCannotBeCapturedAsTheRenderedPolicy()
    {
        var requests = new PresentationRequestState();
        requests.SetDisplayPolicy(HdrDisplayPolicy.FromMonitor(true, 250));
        var old = requests.Desired;
        Assert.True(requests.TryRender(source, request =>
        {
            Assert.Same(old, request);
            requests.SetDisplayPolicy(HdrDisplayPolicy.MetadataUnavailable("late failure"));
            return true;
        }));
        Assert.Same(old, requests.Successful);
        Assert.True(requests.HasPending);
        Assert.Equal(VideoPresentationOutput.Hdr, requests.Statistics!.Output);
        Assert.True(requests.TryRender(source, _ => true));
        Assert.False(requests.HasPending);
        Assert.Equal(VideoPresentationOutput.SdrToneMapped, requests.Statistics!.Output);
    }

    [Fact]
    public void PolicyAbaAndViewportChangesCannotReuseAnOldAcknowledgement()
    {
        var requests = new PresentationRequestState();
        var original = HdrDisplayPolicy.FromMonitor(true, 250);
        requests.SetDisplayPolicy(original);
        var old = requests.Desired;
        Assert.True(requests.TryRender(source, _ =>
        {
            requests.SetDisplayPolicy(HdrDisplayPolicy.MetadataUnavailable("transient failure"));
            requests.SetViewport(1280, 720, 2, 2);
            requests.SetDisplayPolicy(original);
            requests.SetViewport(640, 360, 1, 1);
            return true;
        }));
        Assert.Equal(old, requests.Desired);
        Assert.NotSame(old, requests.Desired);
        Assert.True(requests.HasPending);
        Assert.True(requests.TryRender(source, request => ReferenceEquals(requests.Desired, request)));
        Assert.False(requests.HasPending);
    }

    [Fact]
    public async Task CancellationDoesNotBlockUiUpdatesOrAcknowledgeAnInterruptedRender()
    {
        var requests = new PresentationRequestState();
        Assert.True(requests.TryRender(source, _ => true));
        var successful = requests.Successful;
        requests.SetDisplayPolicy(HdrDisplayPolicy.FromMonitor(true, 250));
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var inflight = Task.Run(() => requests.TryRender(source, _ =>
        {
            entered.Set();
            Assert.True(release.Wait(timeout));
            cancellation.Token.ThrowIfCancellationRequested();
            return true;
        }));
        try
        {
            Assert.True(entered.Wait(timeout));
            await Task.Run(() =>
            {
                requests.SetDisplayPolicy(HdrDisplayPolicy.MetadataUnavailable("closing monitor"));
                requests.SetViewport(1000, 750, 1, 1);
                cancellation.Cancel();
            }).WaitAsync(timeout);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inflight.WaitAsync(timeout));
            Assert.Same(successful, requests.Successful);
            Assert.True(requests.HasPending);
            Assert.Equal(VideoPresentationOutput.SdrToneMapped, requests.Statistics!.Output);
        }
        finally
        {
            release.Set();
            try { await inflight.WaitAsync(timeout); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    [Fact]
    public void FatalRendererFailurePropagatesWithoutAcknowledgingOrInventingOutput()
    {
        var requests = new PresentationRequestState();
        requests.SetDisplayPolicy(HdrDisplayPolicy.FromMonitor(true, 250));
        var error = new IOException("device removed");
        Assert.Same(error, Assert.Throws<IOException>(() => requests.TryRender(source, _ => throw error)));
        Assert.Null(requests.Successful);
        Assert.Null(requests.Statistics);
        Assert.True(requests.HasPending);
    }

    [Fact]
    public void ConfigurationResetDoesNotAcknowledgePendingOutputOrInventStatistics()
    {
        var requests = new PresentationRequestState();
        Assert.True(requests.TryRender(source, _ => true));
        requests.SetDisplayPolicy(HdrDisplayPolicy.FromMonitor(true, 400));
        var desired = requests.Desired;
        requests.ResetStatistics();
        Assert.Null(requests.Statistics);
        Assert.Same(desired, requests.Desired);
        Assert.True(requests.HasPending);
        Assert.True(requests.TryRender(HdrVideoFormat.Sdr, _ => true));
        Assert.False(requests.HasPending);
        Assert.Equal(VideoPresentationOutput.Sdr, requests.Statistics!.Output);
    }
}
