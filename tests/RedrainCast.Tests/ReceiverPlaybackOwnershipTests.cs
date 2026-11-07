using Xunit;

namespace RedrainCast.Tests;

public sealed class ReceiverPlaybackOwnershipTests
{
    [Fact]
    public void ScheduledTracksCaptureAnIndependentPlaybackReference()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ReceiverServer.cs.txt"));
        Assert.Contains("var workerPlayback = playback;", source);
        Assert.Contains("TrackThread.Run(() => PumpAsync(workerPlayback, trackState, track))", source);
        Assert.Contains("watchdog = WatchdogAsync(workerPlayback)", source);
        Assert.DoesNotContain("TrackThread.Run(() => PumpAsync(playback,", source);
        Assert.Contains("var retiring = playback;\n", source.Replace("\r\n", "\n"));
        Assert.Contains("playback = null;", source);
    }
}
