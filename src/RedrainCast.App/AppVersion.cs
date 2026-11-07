using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using RedrainCast.Receive.Core;

namespace RedrainCast.App;

internal static class AppVersion
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);

    internal static (string Label, string Source) Current()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        if (result == 15700) // APPMODEL_ERROR_NO_PACKAGE: unpackaged application.
        {
            var build = typeof(App).Assembly.GetName().Version
                ?? throw new InvalidOperationException("Application build version is unavailable.");
            return (ReleaseVersion.Label(build), "build");
        }
        if (result != 122) throw new Win32Exception(result, "Cannot determine application package identity.");
        var package = Windows.ApplicationModel.Package.Current.Id.Version;
        return (ReleaseVersion.Label(new Version(package.Major, package.Minor, package.Build, package.Revision)), "package");
    }
}
