param(
    [Parameter(Mandatory = $true)][ValidateSet('arm64', 'x64')][string]$Architecture,
    [Parameter(Mandatory = $true)][string]$PublishedDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+\.0$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$identity = Get-Content -LiteralPath (Join-Path $root 'StoreIdentity.json') -Raw | ConvertFrom-Json
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path
if (-not $Version) { $Version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $published 'RedrainCast.App.dll')).Version.ToString() }
$number = [version]::Parse($Version)
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$' -or
    $number.Major -gt 65535 -or $number.Minor -gt 65535 -or $number.Build -gt 65535) {
    throw 'MSIX version must have three components from 0 to 65535 and a zero revision.'
}
& (Join-Path $PSScriptRoot 'ValidatePublication.ps1') -Architecture $Architecture -PublishedDirectory $published -ExpectedVersion $Version
& powershell.exe -NoProfile -STA -File (Join-Path $PSScriptRoot 'Generate-BrandAssets.ps1') -Verify
if ($LASTEXITCODE -ne 0) { throw 'Brand assets failed validation.' }
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
    throw 'Package output must be new or empty; existing work is preserved.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$manifest = New-Object Xml.XmlDocument
$manifest.Load((Join-Path $root 'src\RedrainCast.App\Package.appxmanifest'))
$packageIdentity = $manifest.Package.Identity
if ($packageIdentity.Name -cne $identity.PackageName -or $packageIdentity.Publisher -cne $identity.Publisher -or
    $manifest.Package.Properties.DisplayName -cne $identity.DisplayName -or
    $manifest.Package.Properties.PublisherDisplayName -cne $identity.PublisherDisplayName) {
    throw 'The package project manifest must match the exact Partner Center identity.'
}
$packageIdentity.SetAttribute('Version', $Version)
$manifestPath = Join-Path $output 'Package.appxmanifest'
$manifest.Save($manifestPath)
$platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
$project = Join-Path $root 'src\RedrainCast.App\RedrainCast.App.csproj'
$certificatePath = $null
$oldPassword = $env:PackageCertificatePassword
try {
    $signing = -not [string]::IsNullOrWhiteSpace($env:MSIX_SIGNING_PFX_BASE64)
    if (-not $signing -and -not [string]::IsNullOrEmpty($env:MSIX_SIGNING_PASSWORD)) {
        throw 'A signing password was supplied without a signing certificate.'
    }
    $signingArguments = @('-p:AppxPackageSigningEnabled=false')
    if ($signing) {
        $certificatePath = Join-Path ([IO.Path]::GetTempPath()) ("redraincast-signing-" + [Guid]::NewGuid().ToString('N') + '.pfx')
        [IO.File]::WriteAllBytes($certificatePath, [Convert]::FromBase64String($env:MSIX_SIGNING_PFX_BASE64))
        $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2(
            $certificatePath, $env:MSIX_SIGNING_PASSWORD,
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        try {
            if (-not $certificate.HasPrivateKey -or $certificate.Subject -cne $identity.Publisher -or
                $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
                throw 'Signing requires a valid private-key certificate whose subject exactly matches the Store publisher.'
            }
        } finally { $certificate.Dispose() }
        $env:PackageCertificatePassword = $env:MSIX_SIGNING_PASSWORD
        $signingArguments = @('-p:AppxPackageSigningEnabled=true', "-p:PackageCertificateKeyFile=$certificatePath",
            '-p:AppxPackageSigningTimestampDigestAlgorithm=SHA256')
    }
    & dotnet build $project -c Release "-p:Platform=$platform" "-r" "win-$Architecture" --self-contained true `
        "-p:Version=$($number.Major).$($number.Minor).$($number.Build)" -p:BuildStorePackage=true `
        "-p:PackageManifestPath=$manifestPath" "-p:PublishedPackageDirectory=$published" `
        "-p:AppxPackageDir=$output\packages\" @signingArguments
    if ($LASTEXITCODE -ne 0) { throw 'Official Windows SDK MSIX build failed.' }
} finally {
    $env:PackageCertificatePassword = $oldPassword
    if ($certificatePath -and (Test-Path -LiteralPath $certificatePath)) { Remove-Item -LiteralPath $certificatePath -Force }
}
$packages = @(Get-ChildItem -LiteralPath (Join-Path $output 'packages') -Recurse -File -Filter '*.msix')
if ($packages.Count -ne 1) { throw 'The Windows SDK must produce exactly one architecture-specific MSIX.' }
$package = Join-Path $output "RedrainCast-$Architecture.msix"
Copy-Item -LiteralPath $packages[0].FullName -Destination $package

Add-Type -AssemblyName System.IO.Compression.FileSystem
$stage = Join-Path $output 'staging'
[IO.Compression.ZipFile]::ExtractToDirectory($package, $stage)
if ($signing -and -not (Test-Path -LiteralPath (Join-Path $stage 'AppxSignature.p7x') -PathType Leaf)) {
    throw 'Signing was requested but the SDK package has no signature.'
}
[xml]$actual = Get-Content -LiteralPath (Join-Path $stage 'AppxManifest.xml') -Raw
if ($actual.Package.Identity.Name -cne $identity.PackageName -or $actual.Package.Identity.Publisher -cne $identity.Publisher -or
    $actual.Package.Identity.ProcessorArchitecture -cne $Architecture -or $actual.Package.Identity.Version -cne $Version) {
    throw 'The SDK package identity, architecture or version differs from the requested publication.'
}
$deviceFamily = $actual.Package.Dependencies.TargetDeviceFamily
if ($deviceFamily.Name -cne 'Windows.Desktop' -or $deviceFamily.MinVersion -cne '10.0.22621.0' -or
    $deviceFamily.MaxVersionTested -cne '10.0.26100.0') {
    throw 'The SDK package must preserve the supported Windows 11 device-family versions.'
}
if (@(Get-ChildItem -LiteralPath $stage -Recurse -File -Filter '*.pdb').Count -ne 0) {
    throw 'MSIX contains debug symbols.'
}
foreach ($required in @('RedrainCast.App.exe', 'RedrainCast.App.dll', 'RedrainCast.App.pri', 'resources.pri',
    'Assets\Cast44.png', 'Assets\Cast50.png', 'Assets\Cast150.png', 'Assets\RedrainCast.ico',
    'THIRD_PARTY_NOTICES.txt',
    'licenses\ScrcpyReceive-Windows-LICENSE.txt', 'licenses\tone-map-provenance.json',
    "native\win-$Architecture\avcodec-61.dll", "native\win-$Architecture\avutil-59.dll",
    "native\win-$Architecture\swresample-5.dll", "native\win-$Architecture\FFmpeg-LICENSE.txt")) {
    $path = Join-Path $stage $required
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "SDK package is missing required content: $required"
    }
}
foreach ($file in Get-ChildItem -LiteralPath $published -Recurse -File) {
    $relative = $file.FullName.Substring($published.TrimEnd('\').Length + 1)
    if ($relative -eq 'resources.pri') { continue }
    $packaged = Join-Path $stage $relative
    if (-not (Test-Path -LiteralPath $packaged -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file.FullName).Hash -cne (Get-FileHash -LiteralPath $packaged).Hash) {
        throw "SDK package changed or omitted validated publication content: $relative"
    }
}
& (Join-Path $PSScriptRoot 'Validate-Localization.ps1') -Pri (Join-Path $stage 'RedrainCast.App.pri') `
    -PackagePri (Join-Path $stage 'resources.pri') -ManifestPath (Join-Path $stage 'AppxManifest.xml') `
    -ReportPath (Join-Path $output 'localization-validation.json')
& (Join-Path $PSScriptRoot 'Assert-ShippingBoundary.ps1') -PublishedDirectory $stage
$signatureState = if ($signing) { 'signed; installation requires a trusted signer' } else { 'unsigned; Store submission input, not directly installable' }
Write-Output "Windows SDK MSIX ($signatureState): $package"
