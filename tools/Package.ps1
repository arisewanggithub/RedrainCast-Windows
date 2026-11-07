param(
    [Parameter(Mandatory = $true)][ValidateSet('arm64', 'x64')][string]$Architecture,
    [Parameter(Mandatory = $true)][string]$PublishedDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+\.0$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$identity = Get-Content -LiteralPath (Join-Path $root 'StoreIdentity.json') -Raw | ConvertFrom-Json
if ($identity.PackageName -notmatch '^[A-Za-z0-9.-]{3,50}$' -or -not $identity.Publisher.StartsWith('CN=') -or
    [string]::IsNullOrWhiteSpace($identity.DisplayName) -or [string]::IsNullOrWhiteSpace($identity.PublisherDisplayName)) {
    throw 'StoreIdentity.json must contain exact Partner Center identity values.'
}
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path
if (-not $Version) { $Version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $published 'RedrainCast.App.dll')).Version.ToString() }
$number = [version]::Parse($Version)
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$' -or
    $number.Major -gt 65535 -or $number.Minor -gt 65535 -or $number.Build -gt 65535) {
    throw 'MSIX version must have three components from 0 to 65535 and a zero revision.'
}
& (Join-Path $PSScriptRoot 'ValidatePublication.ps1') -Architecture $Architecture -PublishedDirectory $published
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $published 'RedrainCast.App.dll')).Version -ne $number) {
    throw 'MSIX version must match the published application assembly version.'
}
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
    throw 'Package output must be new or empty; existing work is preserved.'
}
& powershell.exe -NoProfile -STA -File (Join-Path $PSScriptRoot 'Generate-BrandAssets.ps1') -Verify
if ($LASTEXITCODE -ne 0) { throw 'Brand assets failed validation. Regenerate them with tools\Generate-BrandAssets.ps1.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$stage = Join-Path $output 'staging'
New-Item -ItemType Directory -Path $stage | Out-Null
Get-ChildItem -LiteralPath $published -Force | Copy-Item -Destination $stage -Recurse
$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
foreach ($size in @(44, 50, 150)) {
    $source = Join-Path $root "assets\package\Cast$size.png"
    $destination = Join-Path $assets "Cast$size.png"
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
            throw "Published asset conflicts with the verified package brand: Cast$size.png."
        }
    } else { Copy-Item -LiteralPath $source -Destination $destination }
}
$name = [Security.SecurityElement]::Escape($identity.PackageName)
$publisher = [Security.SecurityElement]::Escape($identity.Publisher)
$display = [Security.SecurityElement]::Escape($identity.DisplayName)
$publisherDisplay = [Security.SecurityElement]::Escape($identity.PublisherDisplayName)
$descriptionResource = "ms-resource://$name/Resources/AppDescription"
$languageResources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\RedrainCast.App\Strings') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Resources.resw') } |
    Sort-Object Name | ForEach-Object { '<Resource Language="' + [Security.SecurityElement]::Escape($_.Name) + '"/>' }) -join ''
if (-not $languageResources.Contains('Language="en-US"')) { throw 'The canonical en-US resource catalog is missing.' }
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
  <Description>$descriptionResource</Description>
 </Properties>
 <Resources>$languageResources</Resources>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22621.0" MaxVersionTested="10.0.26100.0"/></Dependencies>
 <Applications>
  <Application Id="App" Executable="RedrainCast.App.exe" EntryPoint="Windows.FullTrustApplication">
   <uap:VisualElements DisplayName="$display" Description="$descriptionResource"
    BackgroundColor="#9F2036" Square44x44Logo="Assets\Cast44.png" Square150x150Logo="Assets\Cast150.png"/>
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
$hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$sdkBin = Join-Path $cache "microsoft.windows.sdk.buildtools\10.0.26100.4654\bin\10.0.26100.0\$hostArchitecture"
$makepri = Join-Path $sdkBin 'makepri.exe'
if (-not (Test-Path -LiteralPath $makepri)) { throw 'Pinned MakePri missing. Restore/build the application first.' }
# Shell reads package-root resources.pri; MRT Core still reads the unchanged application index.
$resourceInputs = Join-Path $output 'package-resource-inputs'
New-Item -ItemType Directory -Path $resourceInputs | Out-Null
foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $root 'src\RedrainCast.App\Strings') -Directory) {
    $source = New-Object Xml.XmlDocument
    $source.Load((Join-Path $directory.FullName 'Resources.resw'))
    $description = $source.SelectSingleNode('/root/data[@name="AppDescription"]')
    if ($null -eq $description) { throw "Missing package description: $($directory.Name)" }
    $document = New-Object Xml.XmlDocument
    $document.LoadXml('<root/>')
    [void]$document.DocumentElement.AppendChild($document.ImportNode($description, $true))
    $languageDirectory = Join-Path $resourceInputs $directory.Name
    New-Item -ItemType Directory -Path $languageDirectory | Out-Null
    $document.Save((Join-Path $languageDirectory 'Resources.resw'))
}
$priConfig = Join-Path $output 'package-priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 *> (Join-Path $output 'makepri-config.log')
if ($LASTEXITCODE -ne 0) { throw 'Package resource configuration failed.' }
$configuration = New-Object Xml.XmlDocument
$configuration.Load($priConfig)
# Keep all language candidates in the main package, not separate resource packages.
[void]$configuration.resources.RemoveChild($configuration.resources.packaging)
$configuration.Save($priConfig)
$packagePri = Join-Path $stage 'resources.pri'
if (Test-Path -LiteralPath $packagePri) { throw 'Publication unexpectedly contains a package resource index.' }
& $makepri new /pr $resourceInputs /cf $priConfig /in $identity.PackageName /of $packagePri *> (Join-Path $output 'makepri.log')
if ($LASTEXITCODE -ne 0) { throw 'Package resource indexing failed.' }
& (Join-Path $PSScriptRoot 'Validate-Localization.ps1') -Pri (Join-Path $stage 'RedrainCast.App.pri') `
    -PackagePri $packagePri -ManifestPath (Join-Path $stage 'AppxManifest.xml') `
    -ReportPath (Join-Path $output 'localization-validation.json')
& (Join-Path $PSScriptRoot 'Assert-ShippingBoundary.ps1') -PublishedDirectory $stage
$makeappx = Join-Path $sdkBin 'makeappx.exe'
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
    if ($actual.Package.Applications.Application.VisualElements.BackgroundColor -ne '#9F2036') {
        throw 'Packaged background color does not match the shared brand.'
    }
    $packagedLanguages = @($actual.Package.Resources.Resource | ForEach-Object { $_.Language } | Sort-Object)
    $expectedLanguages = @(Get-ChildItem -LiteralPath (Join-Path $root 'src\RedrainCast.App\Strings') -Directory |
        Select-Object -ExpandProperty Name | Sort-Object)
    if (($packagedLanguages -join '|') -cne ($expectedLanguages -join '|')) {
        throw 'Packaged manifest does not declare every validated language exactly once.'
    }
    foreach ($priName in @('RedrainCast.App.pri', 'resources.pri')) {
        $priEntry = $archive.GetEntry($priName)
        if ($null -eq $priEntry) { throw "Packaged resource index is missing: $priName" }
        $priStream = $priEntry.Open()
        $priSha = [Security.Cryptography.SHA256]::Create()
        try { $packagedPriHash = ([BitConverter]::ToString($priSha.ComputeHash($priStream))).Replace('-', '') }
        finally { $priStream.Dispose(); $priSha.Dispose() }
        $expectedPri = if ($priName -eq 'RedrainCast.App.pri') { Join-Path $published $priName } else { $packagePri }
        if ($packagedPriHash -ne (Get-FileHash -LiteralPath $expectedPri -Algorithm SHA256).Hash) {
            throw "Packaged PRI differs from its validated source: $priName"
        }
    }
    foreach ($asset in @('Cast44.png', 'Cast50.png', 'Cast150.png', 'RedrainCast.ico')) {
        $entry = $archive.GetEntry("Assets/$asset")
        if ($null -eq $entry) { throw "MSIX is missing brand icon $asset." }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $packagedHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($packagedHash -ne (Get-FileHash -LiteralPath (Join-Path $root "assets\package\$asset") -Algorithm SHA256).Hash) {
            throw "MSIX brand icon differs from the verified vector asset: $asset."
        }
    }
    foreach ($required in @('Assets/Cast44.png', 'Assets/Cast50.png', 'Assets/Cast150.png', 'Assets/RedrainCast.ico',
        'RedrainCast.App.exe', 'RedrainCast.App.dll', 'RedrainCast.App.pri', 'resources.pri',
        'capture-worker/RedrainCast.CaptureWorker.exe', 'THIRD_PARTY_NOTICES.txt',
        'licenses/ScrcpyReceive-Windows-LICENSE.txt', 'licenses/tone-map-provenance.json',
        "native/win-$Architecture/avcodec-61.dll", "native/win-$Architecture/avutil-59.dll",
        "native/win-$Architecture/swresample-5.dll", "native/win-$Architecture/FFmpeg-LICENSE.txt")) {
        $entry = $archive.GetEntry($required)
        if ($null -eq $entry -or $entry.Length -eq 0) { throw "MSIX is missing required content: $required" }
    }
    $stream = $archive.GetEntry('RedrainCast.App.exe').Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $executableHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($executableHash -ne (Get-FileHash -LiteralPath (Join-Path $published 'RedrainCast.App.exe') -Algorithm SHA256).Hash) {
        throw 'MSIX executable differs from the publication with verified native icon resources.'
    }
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -notin @('[Content_Types].xml', 'AppxBlockMap.xml') -and
            -not (Test-Path -LiteralPath (Join-Path $stage $entry.FullName.Replace('/', '\')) -PathType Leaf)) {
            throw "Unexpected MSIX content not present in the validated staging directory: $($entry.FullName)"
        }
    }
} finally { $archive.Dispose() }
Write-Output "Unsigned Store submission package (not signed or submitted): $package"
