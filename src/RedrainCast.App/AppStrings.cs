using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.ApplicationModel.Resources;
using RedrainCast.Core;

namespace RedrainCast.App;

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class AppStrings : INotifyPropertyChanged
{
    public static AppStrings Current => (AppStrings)Microsoft.UI.Xaml.Application.Current.Resources["Strings"];
    private ResourceManager? manager;
    private ResourceContext? context;
    private LanguageObserver? observer;
    private Action<Exception>? report;
    private readonly HashSet<string> reportedMissing = [];
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    public bool IsRightToLeft => Culture.TextInfo.IsRightToLeft;
    public string HomeTagline => Get("HomeTagline");
    public string Settings => Get("Settings");
    public string SendScreen => Get("SendScreen");
    public string SendHint => Get("SendHint");
    public string ChooseSourceHelp => Get("ChooseSourceHelp");
    public string ShareSource => Get("ShareSource");
    public string ChooseSource => Get("ChooseSource");
    public string NearbyReceivers => Get("NearbyReceivers");
    public string AutoUpdating => Get("AutoUpdating");
    public string LookingForReceivers => Get("LookingForReceivers");
    public string ManualConnect => Get("ManualConnect");
    public string ReceiveScreen => Get("ReceiveScreen");
    public string Starting => Get("Starting");
    public string OpeningListener => Get("OpeningListener");
    public string ConnectThisPC => Get("ConnectThisPC");
    public string AddressHint => Get("AddressHint");
    public string CopyAddress => Get("CopyAddress");
    public string CopyAddressHelp => Get("CopyAddressHelp");
    public string IncomingHint => Get("IncomingHint");
    public string TrustedNetworkHint => Get("TrustedNetworkHint");
    public string ConnectionHelp => Get("ConnectionHelp");
    public string LeftToRight(string value) => !IsRightToLeft ? value :
        string.Join("\n", value.Split('\n').Select(line => "\u2066" + line + "\u2069"));

    public void Start(DispatcherQueue dispatcher, Action<Exception> report,
        Func<IReadOnlyList<string>>? preferredLanguages = null)
    {
        this.report = report;
        try { manager = new ResourceManager(Path.Combine(AppContext.BaseDirectory, "RedrainCast.App.pri")); }
        catch (Exception error) { report(new IOException("MRT resources unavailable; using canonical English.", error)); }
        observer = new(preferredLanguages ?? ReadPreferredLanguages, action =>
        {
            if (!dispatcher.TryEnqueue(() => action()))
                throw new InvalidOperationException("Could not dispatch a system language reconciliation.");
        }, report);
        observer.Changed += ApplyLanguage;
        ApplyLanguage();
        observer.Reconcile();
    }

    private void ApplyLanguage()
    {
        var language = observer?.Language ?? "en-US";
        Culture = CultureInfo.GetCultureInfo(language);
        context = manager?.CreateResourceContext();
        if (context is not null) context.QualifierValues["Language"] = language;
        reportedMissing.Clear();
        cache.Clear();
        // Keep culture explicit at presentation boundaries. Never change process/thread defaults used by wire code.
        PropertyChanged?.Invoke(this, new(string.Empty));
        Changed?.Invoke();
    }

    public void Reconcile() => observer?.Reconcile();
    public void Stop()
    {
        observer?.Dispose();
        observer = null;
        context = null;
        manager = null;
        report = null;
    }

    public string Get(string key, params object?[] arguments)
    {
        if (!cache.TryGetValue(key, out var value))
        {
            value = manager is null ? EnglishCatalog.Get(key) :
                CatalogText.Resolve(key, name => manager.MainResourceMap.GetValue("Resources/" + name, context!).ValueAsString,
                    error => { if (reportedMissing.Add(key)) report?.Invoke(error); });
            cache[key] = value;
        }
        return arguments.Length == 0 ? value : string.Format(Culture, value, arguments);
    }

    internal (string Value, string Language) ResolveCandidate(string key)
    {
        if (manager is null || context is null) throw new InvalidOperationException("MRT is not initialized.");
        var candidate = manager.MainResourceMap.GetValue("Resources/" + key, context);
        return (candidate.ValueAsString, candidate.QualifierValues["Language"]);
    }

    public IDisposable Bind(Action update)
    {
        update();
        Changed += update;
        return new Subscription(() => Changed -= update);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    internal static IReadOnlyList<string> ReadPreferredLanguages() =>
        Windows.System.UserProfile.GlobalizationPreferences.Languages.ToArray();
}
