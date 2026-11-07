using Xunit;

namespace RedrainCast.Tests;

public sealed class PackageLocalizationTests
{
    [Fact]
    public void PackageBuildsIndependentShellIndexWithoutReplacingApplicationMrtIndex()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "Package.ps1"));
        Assert.Contains("ms-resource://$name/Resources/AppDescription", source);
        Assert.Contains("<Description>$descriptionResource</Description>", source);
        Assert.Contains("Description=\"$descriptionResource\"", source);
        Assert.Contains("'package-resource-inputs'", source);
        Assert.Contains("/dq en-US", source);
        Assert.Contains("/in $identity.PackageName /of $packagePri", source);
        Assert.Contains("-PackagePri $packagePri -ManifestPath", source);
        Assert.Contains("@('RedrainCast.App.pri', 'resources.pri')", source);
        Assert.Contains("Join-Path $published $priName", source);
        Assert.DoesNotContain("Copy-Item -LiteralPath $source -Destination $packagePri", source);
    }
}
