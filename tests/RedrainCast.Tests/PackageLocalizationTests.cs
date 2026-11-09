using Xunit;

namespace RedrainCast.Tests;

public sealed class PackageLocalizationTests
{
    [Fact]
    public void PackageUsesOfficialSdkWithFullRootResourceValidation()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "Package.ps1"));
        Assert.Contains("& dotnet build", source);
        Assert.Contains("-p:BuildStorePackage=true", source);
        Assert.Contains("'Validate-Localization.ps1'", source);
        Assert.Contains("-PackagePri (Join-Path $stage 'resources.pri')", source);
        Assert.Contains("SDK package changed or omitted validated publication content", source);
        Assert.Contains("$env:MSIX_SIGNING_PFX_BASE64", source);
        Assert.Contains("$certificate.Subject -cne $identity.Publisher", source);
        Assert.DoesNotContain("Import-PfxCertificate", source);
        Assert.DoesNotContain("TrustedPeople", source);
        Assert.DoesNotContain("makeappx", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("package-resource-inputs", source);
        var project = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "App.csproj"));
        Assert.Contains("Microsoft.Windows.SDK.BuildTools.MSIX", project);
        Assert.Contains("<GenerateAppxPackageOnBuild>true</GenerateAppxPackageOnBuild>", project);
        Assert.Contains("<TargetPlatformMinVersion>10.0.22621.0</TargetPlatformMinVersion>", project);
        Assert.Contains("Condition=\"'$(BuildStorePackage)' != 'true'\"", project);
        Assert.Contains("<_ValidatedPackageFile Update=\"@(_ValidatedPackageFile)\">", project);
        var validation = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "Validate-Localization.ps1"));
        Assert.Contains("@('App.xbf', 'MainWindow.xbf')", validation);
        Assert.Contains("foreach ($key in $keys)", validation);
        Assert.DoesNotContain("$resources.Count -ne 1", validation);
    }

    [Fact]
    public void PackageStartupSmokeIsIsolatedAndDoesNotRequireKeyboardInput()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var app = File.ReadAllText(Path.Combine(fixture, "App.xaml.cs.txt"));
        Assert.Contains("\"--package-startup-self-test\"", app);
        Assert.Contains("packageStartupIndex >= 0 ? packageStartupIndex", app);
        Assert.Contains("window.RunPackageStartupSelfTestAsync", app);
        var source = File.ReadAllText(Path.Combine(fixture, "LayoutSelfTest.cs.txt"));
        var start = source.IndexOf("internal async Task RunPackageStartupSelfTestAsync", StringComparison.Ordinal);
        var end = source.IndexOf("internal async Task RunLayoutSelfTestAsync", start, StringComparison.Ordinal);
        var smoke = source[start..end];
        Assert.Contains("!isolated", smoke);
        Assert.Contains("WindowRoot.XamlRoot is null", smoke);
        Assert.Contains("double.IsFinite", smoke);
        Assert.Contains("(\"inputInjected\", false)", smoke);
        Assert.Contains("Environment.ExitCode = failure is null ? 0 : 1", smoke);
        Assert.DoesNotContain("SendLayoutKey", smoke);
        Assert.DoesNotContain("Focus(", smoke);
        var helper = File.ReadAllText(Path.Combine(fixture, "Test-PackageLaunch.ps1"));
        Assert.Contains("--package-startup-self-test", helper);
        Assert.DoesNotContain("--layout-self-test", helper);
        Assert.Contains("WaitForExit(90000)", helper);
        Assert.Contains("Stop-Process -Id $process.Id", helper);
    }
}
