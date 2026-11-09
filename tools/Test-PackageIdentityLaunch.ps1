param(
    [Parameter(Mandatory = $true)][string]$StagingDirectory,
    [Parameter(Mandatory = $true)][string]$ReportPath
)
$ErrorActionPreference = 'Stop'
# Requires development registration policy; never changes policy or installs certificates/frameworks.
$staging = (Resolve-Path -LiteralPath $StagingDirectory).Path
$report = [IO.Path]::GetFullPath($ReportPath)
if ($report.Contains('"')) { throw 'Report path cannot contain a quote.' }
if (Test-Path -LiteralPath $report) { throw 'Package identity validation requires a new report path.' }
foreach ($file in @('RedrainCast.App.exe', 'AppxManifest.xml', 'resources.pri')) {
    if (-not (Test-Path -LiteralPath (Join-Path $staging $file) -PathType Leaf)) { throw "Missing staged input: $file" }
}
[xml]$source = Get-Content -LiteralPath (Join-Path $staging 'AppxManifest.xml') -Raw
$framework = @($source.Package.Dependencies.PackageDependency | Where-Object { $_.Name -ceq 'Microsoft.WindowsAppRuntime.2' })
if ($framework.Count -ne 1) { throw 'Exactly one Windows App Runtime 2 dependency is required.' }
$architecture = [string]$source.Package.Identity.ProcessorArchitecture
$installed = @(Get-AppxPackage -Name $framework[0].Name | Where-Object {
    [string]$_.Architecture -ieq $architecture -and [version]$_.Version -ge [version]$framework[0].MinVersion -and
    $_.Publisher -ceq $framework[0].Publisher
})
if ($installed.Count -eq 0) { throw 'Install the manifest-declared Windows App Runtime before package identity testing.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $report) -Force | Out-Null
$name = 'RedrainCast.Test.' + [Guid]::NewGuid().ToString('N')
$directory = Join-Path (Split-Path -Parent $report) $name
$manifestPath = Join-Path $directory 'AppxManifest.xml'
[xml]$manifest = @'
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap uap10 rescap">
 <Identity Name="Placeholder" Publisher="CN=RedrainCast Startup Test" Version="1.0.0.0" ProcessorArchitecture="arm64"/>
 <Properties><DisplayName>RedrainCast isolated startup test</DisplayName><PublisherDisplayName>Startup test</PublisherDisplayName>
  <Logo>Assets\Cast50.png</Logo><uap10:AllowExternalContent>true</uap10:AllowExternalContent></Properties>
 <Resources><Resource Language="en-us"/></Resources>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22621.0" MaxVersionTested="10.0.26100.0"/></Dependencies>
 <Applications><Application Id="App" Executable="RedrainCast.App.exe" EntryPoint="Windows.FullTrustApplication"
  uap10:RuntimeBehavior="packagedClassicApp" uap10:TrustLevel="mediumIL">
  <uap:VisualElements AppListEntry="none" DisplayName="RedrainCast isolated startup test" Description="Isolated startup test"
   BackgroundColor="transparent" Square44x44Logo="Assets\Cast44.png" Square150x150Logo="Assets\Cast150.png"/>
 </Application></Applications>
 <Capabilities><rescap:Capability Name="runFullTrust"/></Capabilities>
</Package>
'@
$manifest.Package.Identity.SetAttribute('Name', $name)
$manifest.Package.Identity.SetAttribute('ProcessorArchitecture', $architecture)
[void]$manifest.Package.Dependencies.AppendChild($manifest.ImportNode($framework[0], $true))
if (-not ('RedrainCastStartupIdentity.Activation' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace RedrainCastStartupIdentity {
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string app,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }
    public static class Activation {
        public static uint Launch(string app, string arguments) {
            object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
            try {
                uint processId;
                Marshal.ThrowExceptionForHR(((IApplicationActivationManager)instance).ActivateApplication(app, arguments, 0, out processId));
                return processId;
            } finally { Marshal.ReleaseComObject(instance); }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder name);
        public static string PackageName(IntPtr process) {
            uint length = 0;
            int error = GetPackageFullName(process, ref length, null);
            if (error != 122) throw new System.ComponentModel.Win32Exception(error, "Process has no readable package identity.");
            var name = new StringBuilder((int)length);
            error = GetPackageFullName(process, ref length, name);
            if (error != 0) throw new System.ComponentModel.Win32Exception(error);
            return name.ToString();
        }
    }
}
'@
}
$package = $null
$process = $null
$registrationAttempted = $false
$directoryCreated = $false
try {
    if (@(Get-AppxPackage -Name $name).Count -ne 0) { throw 'Random test package identity already exists.' }
    New-Item -ItemType Directory -Path $directory | Out-Null
    $directoryCreated = $true
    $manifest.Save($manifestPath)
    $registrationAttempted = $true
    Add-AppxPackage -Register $manifestPath -ExternalLocation $staging -ErrorAction Stop
    $package = @(Get-AppxPackage -Name $name)
    if ($package.Count -ne 1 -or $package[0].Name -cne $name) { throw 'Temporary test registration did not resolve exactly.' }
    $package = $package[0]
    $processId = [RedrainCastStartupIdentity.Activation]::Launch(
        "$($package.PackageFamilyName)!App", "--package-startup-self-test `"$report`"")
    $process = Get-Process -Id $processId -ErrorAction Stop
    $observedIdentity = [RedrainCastStartupIdentity.Activation]::PackageName($process.Handle)
    if ($observedIdentity -cne $package.PackageFullName) { throw "Unexpected process package identity: $observedIdentity" }
    if (-not $process.WaitForExit(90000)) { throw 'Package identity startup timed out after 90 seconds.' }
    if ($process.ExitCode -ne 0) { throw "Package identity startup failed with exit code $($process.ExitCode)." }
    if (-not (Test-Path -LiteralPath $report -PathType Leaf)) { throw 'Package identity process produced no report.' }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if ($result.passed -isnot [bool] -or -not $result.passed -or $result.packageStartupOnly -ne $true -or
        $result.pid -ne $processId -or $result.inputInjected -ne $false -or $result.captureStarted -ne $false -or
        $result.encoderStarted -ne $false -or $result.audioStarted -ne $false -or $result.settingsWritten -ne $false) {
        throw 'Package identity report did not pass the isolated, no-input startup check.'
    }
    $result | Add-Member -NotePropertyName observedPackageFullName -NotePropertyValue $observedIdentity
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $report -Encoding UTF8
} finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        $process.Dispose()
    }
    if ($registrationAttempted) {
        $remaining = @(Get-AppxPackage -Name $name | Where-Object { $_.Name -ceq $name })
        foreach ($item in $remaining) { Remove-AppxPackage -Package $item.PackageFullName -ErrorAction Stop }
        if (@(Get-AppxPackage -Name $name).Count -ne 0) { throw "Temporary test package cleanup failed: $name" }
    }
    if ($directoryCreated) { Remove-Item -LiteralPath $directory -Recurse -Force }
}
Write-Output "Package identity startup passed: $observedIdentity; temporary registration removed."
