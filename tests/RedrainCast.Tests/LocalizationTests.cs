using System.Globalization;
using System.Text;
using System.Xml.Linq;
using RedrainCast.Core;
using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-Hans-TW", "zh-Hans")]
    [InlineData("zh-Hant-CN", "zh-Hant")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("es-MX", "es")]
    [InlineData("fr-CA", "fr")]
    [InlineData("en-GB", "en-US")]
    [InlineData("AR-sa", "ar")]
    [InlineData("xx-Unsupported", "en-US")]
    [InlineData("", "en-US")]
    public void ResolvesPreferredLanguageWithoutDependingOnCachedThreadCulture(string input, string expected) =>
        Assert.Equal(expected, AppLanguages.Resolve([input]));

    [Fact]
    public void PreferredListOrderAndSupportedSetAreExplicit()
    {
        Assert.Equal(22, AppLanguages.Supported.Count);
        Assert.Equal("ja", AppLanguages.Resolve(["xx", "ja-JP", "de-DE"]));
        Assert.Equal("en-US", AppLanguages.Resolve(["en-GB", "ja-JP"]));
        Assert.Equal("en-US", AppLanguages.Resolve([]));
        Assert.Equal("zh-Hant", AppLanguages.Resolve(["zh-Hant-HK", "zh-CN"]));
        Assert.Equal("zh-Hans", AppLanguages.Resolve(["zh-Hans-CN", "en-US"]));
        Assert.Equal("en-US", AppLanguages.Resolve(["en-US", "zh-Hans-CN"]));
    }

    [Fact]
    public void NotificationDispatchIsBoundedDeduplicatedAndReprojectsPresentationOnly()
    {
        IReadOnlyList<string> preferred = ["en-US"];
        var queue = new Queue<Action>();
        var reads = 0;
        var updates = 0;
        var errors = new List<Exception>();
        var initialCulture = CultureInfo.CurrentCulture;
        var initialUiCulture = CultureInfo.CurrentUICulture;
        var initialDefault = CultureInfo.DefaultThreadCurrentCulture;
        using var observer = new LanguageObserver(() => { reads++; return preferred; }, queue.Enqueue, errors.Add);
        observer.Changed += () => updates++;
        observer.Reconcile();
        observer.Reconcile();
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(1, reads);
        Assert.Equal(0, updates);
        preferred = ["ar-SA", "en-US"];
        observer.Reconcile();
        Assert.Equal("en-US", observer.Language);
        queue.Dequeue()();
        Assert.Equal("ar", observer.Language);
        Assert.Equal(1, updates);
        preferred = ["ar-EG"];
        observer.Reconcile();
        queue.Dequeue()();
        Assert.Equal(1, updates);
        Assert.Empty(errors);
        Assert.Same(initialCulture, CultureInfo.CurrentCulture);
        Assert.Same(initialUiCulture, CultureInfo.CurrentUICulture);
        Assert.Same(initialDefault, CultureInfo.DefaultThreadCurrentCulture);
    }

    [Fact]
    public void PendingNotificationsAndSubscriptionsAreRemovedOnDisposal()
    {
        var queue = new Queue<Action>();
        var reads = 0;
        var updates = 0;
        var observer = new LanguageObserver(() => { reads++; return ["fr"]; }, queue.Enqueue, _ => Assert.Fail());
        observer.Changed += () => updates++;
        observer.Reconcile();
        observer.Dispose();
        queue.Dequeue()();
        observer.Reconcile();
        Assert.Empty(queue);
        Assert.Equal(0, reads);
        Assert.Equal(0, updates);
    }

    [Fact]
    public void LanguageReadErrorsAreReportedWithoutClaimingAnUpdateAndCanRetry()
    {
        var fail = true;
        var errors = new List<Exception>();
        using var observer = new LanguageObserver(
            () => fail ? throw new IOException("OS query failed") : ["fr"], action => action(), errors.Add);
        observer.Reconcile();
        Assert.Single(errors);
        Assert.Equal("en-US", observer.Language);
        fail = false;
        observer.Reconcile();
        Assert.Equal("fr", observer.Language);
    }

    [Fact]
    public void MissingTranslationFallsBackExplicitlyButUnknownCanonicalKeysFail()
    {
        var errors = new List<Exception>();
        Assert.Equal("Save", CatalogText.Resolve("Save", _ => null, errors.Add));
        Assert.Equal("Save", CatalogText.Resolve("Save", _ => throw new IOException("PRI failure"), errors.Add));
        Assert.Equal(2, errors.Count);
        Assert.Equal("Enregistrer", CatalogText.Resolve("Save", _ => "Enregistrer", errors.Add));
        Assert.Throws<KeyNotFoundException>(() => CatalogText.Resolve("NotARealKey", _ => null, errors.Add));
    }

    [Fact]
    public void EveryPresentCatalogHasExactlyTheCanonicalKeysAndPlaceholders()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "Strings");
        var files = Directory.GetFiles(directory, "Resources.resw", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            _ = new UTF8Encoding(false, true).GetString(bytes);
            var document = XDocument.Load(file);
            var strings = CatalogText.Validate(document);
            Assert.All(strings, pair => Assert.Matches("^[A-Za-z][A-Za-z0-9_]*$", pair.Key));
            Assert.All(document.Root!.Elements("data").Where(e => e.Element("value")!.Value.Contains('{')),
                e => Assert.False(string.IsNullOrWhiteSpace(e.Element("comment")?.Value)));
        }
    }

    [Fact]
    public void MissingDuplicateBlankAndMismatchedPlaceholdersAreRejected()
    {
        XDocument Canonical() => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "Strings", "en-US", "Resources.resw"));
        var missing = Canonical();
        missing.Root!.Elements("data").First().Remove();
        Assert.Throws<InvalidDataException>(() => CatalogText.Validate(missing));
        var duplicate = Canonical();
        duplicate.Root!.Add(new XElement(duplicate.Root.Elements("data").First()));
        Assert.Throws<ArgumentException>(() => CatalogText.Validate(duplicate));
        var blank = Canonical();
        blank.Root!.Elements("data").First().Element("value")!.Value = "";
        Assert.Throws<InvalidDataException>(() => CatalogText.Validate(blank));
        var placeholders = Canonical();
        placeholders.Root!.Elements("data").Single(e => (string?)e.Attribute("name") == "Version").Element("value")!.Value = "{1}";
        Assert.Throws<InvalidDataException>(() => CatalogText.Validate(placeholders));
    }

    [Fact]
    public void RowTranslationNeverChangesWireStateOrPeerIdentity()
    {
        var endpoint = ReceiverEndpoint.Manual("192.0.2.1", 5000) with { Name = "Verbatim peer" };
        var active = new OutgoingCast(endpoint).ApplyStatus("Preparing capture");
        var rows = ReceiverRows.Create([], active, true, key => "translated-" + key,
            (action, name) => action + "|" + name);
        Assert.Equal("Preparing capture", active.Notice);
        Assert.Equal("PreparingCapture", active.NoticeKey);
        Assert.Equal("translated-Connecting", rows[0].Status);
        Assert.Equal("translated-Cancel|Verbatim peer", rows[0].ActionHelp);
        Assert.Same(endpoint, rows[0].Endpoint);
        Assert.Equal("192.0.2.1 : 5000", rows[0].DisplayAddress);
        Assert.Equal(OutgoingPhase.Casting, active.ApplyStatus("Streaming").Phase);
    }

    [Fact]
    public void StatisticsEnglishPresentationMatchesCanonicalAndOtherCulturesDoNotChangeContract()
    {
        var request = new CastRequest("h265", null, 60, "landscape", ConfiguredVideoBitrate: 8_000_000);
        var playback = new PlaybackStatistics(1, 2, 100, 90, 80, 10, 888, 1920, 270, false, null, null, 0);
        var receive = new ReceiveStatistics(3, new(100, 1000, 125), new(10, 100, 250),
            new(2, 300, 50_000), new(1, 50, 0), 78, 266, null);
        var rates = new PlaybackRates(11.8, 11.8, 8.9, 1.2, 1.4, 25, 3, 5);
        var canonical = PlaybackStatisticsDisplay.Create(request, playback, receive, rates, 1920, 1080, 234.4);
        var english = PlaybackStatisticsDisplay.Create(request, playback, receive, rates, 1920, 1080, 234.4,
            localize: EnglishCatalog.Get, culture: CultureInfo.GetCultureInfo("en-US"));
        static StatisticsCell[] Cells(StatisticsDisplay display) => display.Kpis.Concat(display.Totals)
            .Concat(display.Groups.SelectMany(g => g.Cells)).Concat(display.Footer).ToArray();
        Assert.Equal(Cells(canonical), Cells(english));
        Assert.Equal(canonical.Connections, english.Connections);
        var french = PlaybackStatisticsDisplay.Create(request, playback, receive, rates, 1920, 1080, 234.4,
            localize: key => key == "UnitMegabits" ? "Mbit/s" : EnglishCatalog.Get(key),
            culture: CultureInfo.GetCultureInfo("fr-FR"));
        Assert.Equal("1,2 Mbit/s", Cells(french).Single(c => c.Field.Id == "videoBitrate").Value);
        Assert.Equal("H.265 (HEVC)", Cells(french).Single(c => c.Field.Id == "videoCodec").Value);
        Assert.Equal(Cells(canonical).Select(c => c.Field), Cells(french).Select(c => c.Field));
    }

    [Fact]
    public void HdrStatisticsUseEveryCatalogWithoutTranslatingProtocolOrInferringOutput()
    {
        var request = new CastRequest("h265", null, 60, "auto");
        var p = new PlaybackStatistics(1, 1, 100, 90, 80, 10, 1920, 1080, 0, false, null, null, 0,
            new(new(true), VideoTransfer.Pq, "Main 10", 10));
        var r = new ReceiveStatistics(1, new(100, 1000, 1), new(0, 0, 1), new(0, 0, 0), new(0, 0, 0), null, null, null);
        var rates = new PlaybackRates(null, null, null, null, null, null, null, null);
        foreach (var language in AppLanguages.Supported)
        {
            var catalog = CatalogText.Validate(XDocument.Load(Path.Combine(AppContext.BaseDirectory,
                "fixtures", "Strings", language, "Resources.resw")));
            foreach (var (output, key) in new[]
            {
                (VideoPresentationOutput.Hdr, "StatisticsHdrOutput"),
                (VideoPresentationOutput.SdrToneMapped, "StatisticsSdrToneMapped"),
                (VideoPresentationOutput.Sdr, "StatisticsSdrOutput")
            })
            {
                var snapshot = p with { Presentation = new(output) };
                foreach (var compact in new[] { false, true })
                {
                    var display = PlaybackStatisticsDisplay.Create(request, snapshot, r, rates, 520, 420, null,
                        compact, key => catalog[key], CultureInfo.GetCultureInfo(language));
                    var cells = display.Groups[0].Cells.ToDictionary(c => c.Field.Id);
                    Assert.Equal("HDR10 · PQ", cells["videoDynamicRange"].Value);
                    Assert.Equal("Main 10 · " + string.Format(CultureInfo.GetCultureInfo(language), catalog["StatisticsBitDepth"], 10),
                        cells["videoDecodeFormat"].Value);
                    Assert.Equal(catalog[key], cells["presentationOutput"].Value);
                    Assert.Equal(11, cells.Count);
                    Assert.All(PlaybackStatisticsDisplay.VideoPresentationFields,
                        field => Assert.False(string.IsNullOrWhiteSpace(catalog["Statistics_" + field.Id])));
                }
            }
        }
    }

    [Fact]
    public void LanguageReprojectionPreservesCounterDeltasAndObservedPeak()
    {
        var playback = new PlaybackStatistics(1, 1, 100, 90, 80, 10, 1920, 1080, 0, false, null,
            new(1, 1_000_000, 900_000, 48000, 200_000, 1), 0);
        var receive = new ReceiveStatistics(1, new(100, 1000, 1), new(0, 0, 1), new(0, 0, 0), new(0, 0, 0), null, null, null);
        var sampler = new PlaybackRateSampler();
        var peak = new StatisticsObservedPeak();
        sampler.Sample(1000, playback, receive);
        Assert.Equal(200, peak.Sample(playback, receive, true));
        playback = playback with { Decoded = 108, Presented = 97, Skipped = 11,
            Sync = playback.Sync! with { SignedSkewUs = 100_000 } };
        receive = receive with { Video = receive.Video with { Packets = 120, PayloadBytes = 2000 } };
        var rates = sampler.Sample(2000, playback, receive);
        foreach (var language in new[] { "en-US", "ar", "zh-Hans" })
        {
            var display = PlaybackStatisticsDisplay.Create(new("h265", null, 60, "auto"), playback, receive, rates,
                1920, 1080, peak.Sample(playback, receive, false), localize: EnglishCatalog.Get,
                culture: CultureInfo.GetCultureInfo(language));
            Assert.Equal(18, display.Kpis[1].Number);
            Assert.Equal(108, display.Totals[0].Counter);
            Assert.False(sampler.BaselineReset);
            Assert.Equal(200, peak.Sample(playback, receive, false));
        }
        var next = sampler.Sample(3000, playback with { Decoded = 126, Presented = 114, Skipped = 12 },
            receive with { Video = receive.Video with { Packets = 140, PayloadBytes = 3000 } });
        Assert.Equal(20, next.ReceivedFps);
        Assert.Equal(18, next.DecodedFps);
        Assert.Equal(17, next.PresentedFps);
        Assert.Equal(200, peak.Sample(playback, receive, false));
    }
}
