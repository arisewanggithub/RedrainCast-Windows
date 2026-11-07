namespace RedrainCast.Receive.Core;

public static class ReleaseVersion
{
    public static string Label(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Build < 0) throw new ArgumentException("Release version requires major, minor and build components.", nameof(version));
        return $"v{version.ToString(version.Revision > 0 ? 4 : 3)}";
    }
}
