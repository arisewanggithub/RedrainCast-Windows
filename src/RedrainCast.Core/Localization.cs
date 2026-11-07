using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RedrainCast.Core;

public static class AppLanguages
{
    public static IReadOnlyList<string> Supported { get; } = Array.AsReadOnly<string>(
        ["ar", "de", "en-US", "es", "fr", "hi", "id", "it", "ja", "ko", "ms", "nl", "pl",
         "pt-BR", "ru", "sv", "th", "tr", "uk", "vi", "zh-Hans", "zh-Hant"]);

    public static string Resolve(IEnumerable<string> preferred)
    {
        foreach (var language in preferred)
        {
            var tag = language.Replace('_', '-');
            var exact = Supported.FirstOrDefault(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var parts = tag.Split('-');
            if (parts[0].Equals("zh", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Any(x => x.Equals("Hans", StringComparison.OrdinalIgnoreCase))) return "zh-Hans";
                if (parts.Any(x => x.Equals("Hant", StringComparison.OrdinalIgnoreCase))) return "zh-Hant";
                return parts.Any(x =>
                    x.Equals("TW", StringComparison.OrdinalIgnoreCase) || x.Equals("HK", StringComparison.OrdinalIgnoreCase) ||
                    x.Equals("MO", StringComparison.OrdinalIgnoreCase)) ? "zh-Hant" : "zh-Hans";
            }
            if (parts[0].Equals("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
            if (parts[0].Equals("en", StringComparison.OrdinalIgnoreCase)) return "en-US";
            var neutral = Supported.FirstOrDefault(x => x.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            if (neutral is not null) return neutral;
        }
        return "en-US";
    }
}

/// <summary>Event-driven observer; the dispatcher and OS language reader are supplied by the UI host.</summary>
public sealed class LanguageObserver : IDisposable
{
    private readonly Func<IReadOnlyList<string>> read;
    private readonly Action<Action> dispatch;
    private readonly Action<Exception> report;
    private int queued;
    private volatile bool disposed;
    public string Language { get; private set; } = "en-US";
    public event Action? Changed;

    public LanguageObserver(Func<IReadOnlyList<string>> read, Action<Action> dispatch, Action<Exception> report)
    {
        this.read = read;
        this.dispatch = dispatch;
        this.report = report;
    }

    public void Reconcile()
    {
        if (disposed || Interlocked.Exchange(ref queued, 1) != 0) return;
        try { dispatch(() =>
        {
            Interlocked.Exchange(ref queued, 0);
            if (disposed) return;
            try
            {
                var next = AppLanguages.Resolve(read());
                if (Language == next) return;
                var previous = Language;
                Language = next;
                try { Changed?.Invoke(); }
                catch { Language = previous; throw; }
            }
            catch (Exception error) { report(error); }
        }); }
        catch (Exception error) { Interlocked.Exchange(ref queued, 0); report(error); }
    }

    public void Dispose() { disposed = true; Changed = null; }
}

public static class EnglishCatalog
{
    public static IReadOnlyDictionary<string, string> Strings { get; } = Load();
    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(EnglishCatalog).Assembly.GetManifestResourceStream("RedrainCast.English.resw")
            ?? throw new InvalidOperationException("The canonical English catalog is missing.");
        return XDocument.Load(stream).Root!.Elements("data").ToDictionary(
            e => (string)e.Attribute("name")!, e => (string)e.Element("value")!, StringComparer.Ordinal);
    }

    public static string Get(string key) => Strings.TryGetValue(key, out var value) ? value :
        throw new KeyNotFoundException($"Missing canonical localization key: {key}");
}

public static class CatalogText
{
    public static string Resolve(string key, Func<string, string?> lookup, Action<Exception> report)
    {
        var fallback = EnglishCatalog.Get(key);
        try
        {
            var value = lookup(key);
            if (!string.IsNullOrEmpty(value)) return value;
            report(new IOException($"Missing resource {key}; using canonical English."));
        }
        catch (Exception error) { report(new IOException($"Resource lookup failed: {key}; using canonical English.", error)); }
        return fallback;
    }

    public static IReadOnlyDictionary<string, string> Validate(XDocument document)
    {
        var values = document.Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!,
            e => (string)e.Element("value")!, StringComparer.Ordinal);
        if (!values.Keys.Order().SequenceEqual(EnglishCatalog.Strings.Keys.Order()))
            throw new InvalidDataException("Translation keys differ from the canonical English catalog.");
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"Empty resource: {key}");
            _ = CompositeFormat.Parse(value);
            static string[] Placeholders(string text) => Regex.Matches(text, @"(?<!\{)\{\d+(?:,[^:}]+)?(?::[^}]+)?\}(?!\})")
                .Select(m => m.Value).Order().ToArray();
            if (!Placeholders(value).SequenceEqual(Placeholders(EnglishCatalog.Get(key))))
                throw new InvalidDataException($"Placeholder mismatch: {key}");
        }
        return values;
    }
}
