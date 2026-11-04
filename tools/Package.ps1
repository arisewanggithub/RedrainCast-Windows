param(
    [Parameter(Mandatory = $true)][ValidateSet('arm64', 'x64')][string]$Architecture,
    [Parameter(Mandatory = $true)][string]$PublishedDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+\.0$')][string]$Version = '0.1.1.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$identity = Get-Content -LiteralPath (Join-Path $root 'StoreIdentity.json') -Raw | ConvertFrom-Json
if ($identity.PackageName -notmatch '^[A-Za-z0-9.-]{3,50}$' -or -not $identity.Publisher.StartsWith('CN=') -or
    [string]::IsNullOrWhiteSpace($identity.DisplayName) -or [string]::IsNullOrWhiteSpace($identity.PublisherDisplayName)) {
    throw 'StoreIdentity.json must contain exact Partner Center identity values.'
}
$number = [version]::Parse($Version)
if ($number.Major -gt 65535 -or $number.Minor -gt 65535 -or $number.Build -gt 65535) { throw 'MSIX version component exceeds 65535.' }
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path
& (Join-Path $PSScriptRoot 'ValidatePublication.ps1') -Architecture $Architecture -PublishedDirectory $published
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $published 'RedrainCast.App.dll')).Version -ne $number) {
    throw 'MSIX version must match the published application assembly version.'
}
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
    throw 'Package output must be new or empty; existing work is preserved.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$stage = Join-Path $output 'staging'
New-Item -ItemType Directory -Path $stage | Out-Null
Get-ChildItem -LiteralPath $published -Force | Copy-Item -Destination $stage -Recurse
$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Path $assets | Out-Null
Add-Type -AssemblyName System.Drawing
foreach ($size in @(44, 50, 150)) {
    $image = New-Object Drawing.Bitmap($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($image)
    $pen = New-Object Drawing.Pen([Drawing.Color]::White, [single]($size * 0.045))
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(0, 95, 184))
        $graphics.DrawRectangle($pen, [single]($size * 0.14), [single]($size * 0.18), [single]($size * 0.5), [single]($size * 0.4))
        $graphics.DrawRectangle($pen, [single]($size * 0.36), [single]($size * 0.4), [single]($size * 0.5), [single]($size * 0.4))
        $image.Save((Join-Path $assets "Cast$size.png"), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $pen.Dispose(); $graphics.Dispose(); $image.Dispose() }
}
$name = [Security.SecurityElement]::Escape($identity.PackageName)
$publisher = [Security.SecurityElement]::Escape($identity.Publisher)
$display = [Security.SecurityElement]::Escape($identity.DisplayName)
$publisherDisplay = [Security.SecurityElement]::Escape($identity.PublisherDisplayName)
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap rescap">
 <Identity Name="$name" Publisher="$publisher" Version="$Version" ProcessorArchitecture="$Architecture"/>
 <Properties>
  <DisplayName>$display</DisplayName>
  <PublisherDisplayName>$publisherDisplay</PublisherDisplayName>
  <Logo>Assets\Cast50.png</Logo>
  <Description>Send and receive screens on trusted local networks.</Description>
 </Properties>
 <Resources><Resource Language="en-us"/></Resources>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22621.0" MaxVersionTested="10.0.26100.0"/></Dependencies>
 <Applications>
  <Application Id="App" Executable="RedrainCast.App.exe" EntryPoint="Windows.FullTrustApplication">
   <uap:VisualElements DisplayName="$display" Description="Send and receive screens"
    BackgroundColor="#005FB8" Square44x44Logo="Assets\Cast44.png" Square150x150Logo="Assets\Cast150.png"/>
  </Application>
 </Applications>
 <Capabilities>
  <Capability Name="internetClient"/>
  <Capability Name="privateNetworkClientServer"/>
  <rescap:Capability Name="runFullTrust"/>
 </Capabilities>
</Package>
"@
[IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$hostArchitecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x64' }
$makeappx = Join-Path $cache "microsoft.windows.sdk.buildtools\10.0.26100.4654\bin\10.0.26100.0\$hostArchitecture\makeappx.exe"
if (-not (Test-Path -LiteralPath $makeappx)) { throw 'Pinned MakeAppx missing. Restore/build the application first.' }
$package = Join-Path $output "RedrainCast-$Architecture.msix"
& $makeappx pack /d $stage /p $package /h SHA256 *> (Join-Path $output 'makeappx.log')
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath (Join-Path $output 'makeappx.log') -Tail 20
    throw 'MSIX schema validation/packing failed.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $reader = New-Object IO.StreamReader($archive.GetEntry('AppxManifest.xml').Open())
    try { [xml]$actual = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($actual.Package.Identity.Name -ne $identity.PackageName -or $actual.Package.Identity.Publisher -ne $identity.Publisher -or
        $actual.Package.Identity.ProcessorArchitecture -ne $Architecture -or $actual.Package.Identity.Version -ne $Version) {
        throw 'Packaged identity does not match Partner Center configuration.'
    }
    if (@($archive.Entries | Where-Object { $_.FullName -match '\.pdb$' }).Count -ne 0) { throw 'MSIX contains PDB files.' }
} finally { $archive.Dispose() }
Write-Output "Unsigned Store submission package (not signed or submitted): $package"
