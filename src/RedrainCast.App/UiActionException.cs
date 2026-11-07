namespace RedrainCast.App;

internal sealed class UiActionException(string key) : IOException(RedrainCast.Core.EnglishCatalog.Get(key))
{
    public string Key { get; } = key;
}
