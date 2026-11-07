using System.Globalization;
using System.Text.Json;
using ScrcpyReceive.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class PlaybackStatisticsDisplayTests
{
    private static PlaybackStatistics Playback() => new(1, 2, 100, 90, 80, 10, 888, 1920, 270, false,
        new(4800, 2400, 480, 9600, 480, 960), new(2, 1_000_000, 900_000, 48000, 100_000, 1), 999_999);
    private static ReceiveStatistics Receive() => new(3, new(100, 1000, 125), new(10, 100, 250),
        new(2, 300, 50_000), new(1, 50, 0), 78, 266, null, VideoWaitingQueue: new(1, 200, 0),
        AudioWaitingQueue: new(0, 0, 0));
    private static PlaybackRates Rates() => new(11.8, 11.8, 8.9, 1.2, 1.4, 25, 3, 5);
    private static StatisticsDisplay Display(bool compact = false) => PlaybackStatisticsDisplay.Create(
        new("h264", "raw", 60, "auto", ConfiguredVideoBitrate: 8_000_000), Playback(), Receive(), Rates(), 3840, 2160, 234.4, compact);
    private static StatisticsCell Cell(StatisticsDisplay display, string id) =>
        display.Groups.SelectMany(g => g.Cells).Concat(display.Kpis).Concat(display.Totals).Concat(display.Footer).Single(c => c.Field.Id == id);

    [Fact]
    public void GoldenContractHasExactStableIdsLabelsUnitsAndOrder()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures",
            "playback-statistics-display-contract.json")));
        var root = document.RootElement;
        var display = Display();
        void Match(JsonElement fields, IEnumerable<StatisticsCell> actual) =>
            Assert.Equal(fields.EnumerateArray().Select(f => (f.GetProperty("id").GetString(), f.GetProperty("label").GetString(),
                f.TryGetProperty("unit", out var unit) ? unit.GetString() : null)),
                actual.Select(c => ((string?)c.Field.Id, (string?)c.Field.Label, c.Field.Unit)));
        Assert.False(root.GetProperty("wireContractChanged").GetBoolean());
        Assert.Equal(PlaybackStatisticsDisplay.Title, root.GetProperty("title").GetString());
        Match(root.GetProperty("kpis"), display.Kpis);
        var groups = root.GetProperty("groups").EnumerateArray().ToArray();
        for (var i = 0; i < groups.Length; i++)
        {
            Assert.Equal(groups[i].GetProperty("id").GetString(), display.Groups[i].Id);
            Assert.Equal(groups[i].GetProperty("title").GetString(), display.Groups[i].Title);
            Match(groups[i].GetProperty("fields"), display.Groups[i].Cells);
        }
        Assert.Equal(root.GetProperty("videoTotals").EnumerateArray().Select(x => x.GetString()), display.Totals.Select(c => c.Field.Label));
        Assert.Equal(root.GetProperty("footer").EnumerateArray().Select(x => x.GetString()), display.Footer.Select(c => c.Field.Label));
        var connections = root.GetProperty("connections");
        Assert.Equal(PlaybackStatisticsDisplay.ConnectionsTitle, connections.GetProperty("title").GetString());
        Assert.Equal(connections.GetProperty("columns").EnumerateArray().Select(x => x.GetString()), PlaybackStatisticsDisplay.ConnectionColumns);
        Assert.Equal(connections.GetProperty("rows").EnumerateArray().Select(x => x.GetString()), display.Connections.Select(c => c.Channel));
        Assert.Equal(PlaybackStatisticsDisplay.QueueNote, connections.GetProperty("footnote").GetString());
        Assert.Equal(root.GetProperty("notes").EnumerateArray().Select(x => x.GetString()), PlaybackStatisticsDisplay.Notes);
    }

    [Fact]
    public void ValuesDistinguishActualConfiguredTotalsRecentAndSourceClocks()
    {
        var display = Display();
        Assert.Equal("H.264", Cell(display, "videoCodec").Value);
        Assert.Equal("888 × 1920 px", Cell(display, "encodedSize").Value);
        Assert.Equal("3840 × 2160 px", Cell(display, "canvasSize").Value);
        Assert.Equal("≤60 FPS", Cell(display, "captureLimit").Value);
        Assert.Equal("270° · Auto", Cell(display, "rotation").Value);
        Assert.Equal("1.2 Mbps", Cell(display, "videoBitrate").Value);
        Assert.Equal("8.0 Mbps", Cell(display, "configuredVideoBitrate").Value);
        Assert.Equal("PCM", Cell(display, "audioCodec").Value);
        Assert.Equal("10.0 (+3.0) ms", Cell(display, "insertedSilence").Value);
        Assert.Equal("20.0 (+5.0) ms", Cell(display, "discardedPcm").Value);
        Assert.Equal("+100.0 ms", Cell(display, "avSkew").Value);
        Assert.Equal("234.4 ms", Cell(display, "peakObservedSkew").Value);
        Assert.Equal("90", Cell(display, "decodedTotal").Value);
        Assert.Equal(90, Cell(display, "decodedTotal").Counter);
        Assert.Equal("25.0%", Cell(display, "recentSkippedPercent").Value);
        Assert.True(Cell(display, "recentSkippedPercent").Highlight);
        Assert.Equal(new StatisticsConnection("Video", "1 · 200 B", "0.0 ms", "125.0 ms", "—"), display.Connections[0]);
        Assert.Equal(new StatisticsConnection("Control", "—", "—", "266.0 ms", "78.0 ms"), display.Connections[2]);
    }

    [Fact]
    public void MissingWaitingSnapshotIsUnknownNeverInclusiveQueueFallback()
    {
        var r = Receive() with { VideoWaitingQueue = null };
        var display = PlaybackStatisticsDisplay.Create(new("h265", null, 240, "auto"), Playback() with { Audio = null, Sync = null },
            r, new(0, 0, 0, null, null, 0, null, null), 0, 0, null);
        Assert.Equal("N/A", display.Connections[0].Queued);
        Assert.Equal("N/A", display.Connections[0].Span);
        Assert.Equal("H.265 (HEVC)", Cell(display, "videoCodec").Value);
        Assert.Equal("Off", Cell(display, "audioCodec").Value);
        foreach (var id in new[] { "canvasSize", "audioBitrate", "pcmBufferTarget", "devicePadding", "insertedSilence",
                     "discardedPcm", "avSkew", "peakObservedSkew", "networkLoss" })
            Assert.Equal("N/A", Cell(display, id).Value);
        Assert.False(Cell(display, "recentSkippedPercent").Highlight);
        Assert.All(display.Kpis.Take(3), c => Assert.Equal(0, c.Number));
    }

    [Fact]
    public void ObservedPeakUsesOnlyOverlaySamplesAndResetsWithoutChangingDiagnosticPeak()
    {
        var peak = new StatisticsObservedPeak();
        var p = Playback();
        Assert.Equal(100, peak.Sample(p, Receive(), true));
        Assert.Equal(100, peak.Sample(p with { Sync = p.Sync! with { SignedSkewUs = -50_000 } }, Receive(), false));
        Assert.Equal(200, peak.Sample(p with { Sync = p.Sync! with { SignedSkewUs = -200_000 } }, Receive(), false));
        Assert.Equal(100, peak.Sample(p with { Epoch = 2 }, Receive(), false));
        Assert.Equal(100, peak.Sample(p with { Generation = 3, Sync = p.Sync! with { Generation = 3 } }, Receive(), false));
        Assert.Equal(100, peak.Sample(p, Receive() with { Session = 4 }, false));
        Assert.Null(peak.Sample(p with { Sync = null }, Receive(), true));
        peak.Reset();
        Assert.Null(peak.Sample(p with { Sync = null }, Receive(), false));
        Assert.Equal(999_999, p.PeakAbsoluteSkewUs);
    }

    [Fact]
    public void StaleGenerationTimingIsNotAValidSkewOrPeakSample()
    {
        var p = Playback() with { Sync = Playback().Sync! with { Generation = 1 } };
        var peak = new StatisticsObservedPeak();
        Assert.Null(peak.Sample(p, Receive(), true));
        var display = PlaybackStatisticsDisplay.Create(new("h264", "raw", 60, "auto"), p, Receive(), Rates(), 100, 100, null);
        Assert.Equal("N/A", Cell(display, "avSkew").Value);
    }

    [Theory]
    [InlineData("av1", "AV1", "opus", "Opus")]
    [InlineData("h265", "H.265 (HEVC)", "aac", "AAC")]
    [InlineData("h264", "H.264", "raw", "PCM")]
    public void CodecNamesFollowSharedContract(string codec, string label, string audio, string audioLabel)
    {
        var display = PlaybackStatisticsDisplay.Create(new(codec, audio, 120, "auto"), Playback(), Receive(), Rates(), 100, 200, null);
        Assert.Equal(label, Cell(display, "videoCodec").Value);
        Assert.Equal(audioLabel, Cell(display, "audioCodec").Value);
    }

    [Fact]
    public void NumericFormattingIsInvariantFiniteAndCompactWithoutChangingSampleMath()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("11.8", PlaybackStatisticsDisplay.Number(11.8));
            Assert.Equal("N/A", PlaybackStatisticsDisplay.Number(double.NaN));
            Assert.Equal("N/A", PlaybackStatisticsDisplay.Number(double.PositiveInfinity));
            Assert.Equal("1.2E6", PlaybackStatisticsDisplay.Number(1_234_567, true));
            Assert.Equal(Rates().ReceivedFps, Display(true).Kpis[0].Number);
            Assert.Equal(Display().Groups.SelectMany(g => g.Cells).Select(c => c.Field),
                Display(true).Groups.SelectMany(g => g.Cells).Select(c => c.Field));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
}
