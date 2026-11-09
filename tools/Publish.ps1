param(
    [ValidateSet('arm64', 'x64')][string]$Architecture = 'arm64',
    [string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $Version = $buildProperties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Directory.Build.props must specify a three-component release version.' }
}
$rid = "win-$Architecture"
$platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts\$rid" }
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $root $OutputDirectory }
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
    throw 'Publish requires a new or empty output directory; existing work is preserved.'
}
$nativeFiles = @('avcodec-61.dll', 'avutil-59.dll', 'swresample-5.dll',
    'FFmpeg-LICENSE.txt', 'FFmpeg-CREDITS.txt', 'config.h', 'config_components.h', 'FFmpeg-build-manifest.json')
foreach ($file in $nativeFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $root "native\$rid\$file"))) {
        throw "Missing pinned media dependency: $file. Run tools\PrepareNative.ps1 -Architecture $Architecture first."
    }
}
$nativeEntries = @(Get-ChildItem -LiteralPath (Join-Path $root "native\$rid") -Force)
if ($nativeEntries.Count -ne $nativeFiles.Count -or
    @($nativeEntries | Where-Object { $_.PSIsContainer -or $nativeFiles -notcontains $_.Name }).Count -ne 0) {
    throw 'Native inputs must contain only the pinned decoder/audio runtime and its notices.'
}
$restoreArguments = @()
& powershell.exe -NoProfile -STA -File (Join-Path $PSScriptRoot 'Generate-BrandAssets.ps1') -Verify
if ($LASTEXITCODE -ne 0) { throw 'Brand assets failed validation before publication.' }
if ($NoRestore) { $restoreArguments = @('--no-restore') }
& dotnet publish (Join-Path $root 'src\RedrainCast.App\RedrainCast.App.csproj') @restoreArguments -c Release `
    -r $rid "-p:Platform=$platform" --self-contained true "-p:Version=$Version" `
    -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained RedrainCast publish failed.' }
& dotnet publish (Join-Path $root 'src\RedrainCast.CaptureWorker\RedrainCast.CaptureWorker.csproj') @restoreArguments -c Release `
    -r $rid "-p:Platform=$platform" --self-contained true "-p:Version=$Version" `
    -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $OutputDirectory 'capture-worker')
if ($LASTEXITCODE -ne 0) { throw 'Self-contained capture worker publish failed.' }
foreach ($file in @('RedrainCast.App.exe', 'RedrainCast.App.pri', 'coreclr.dll', 'Microsoft.UI.Xaml.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $file))) { throw "Missing runtime/resource: $file" }
}
if (@(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -Filter '*.pdb').Count -ne 0) {
    throw 'Debug symbols must not be distributed.'
}
$otherArchitecture = if ($Architecture -eq 'arm64') { 'x64' } else { 'arm64' }
if (Test-Path -LiteralPath (Join-Path $OutputDirectory "native\win-$otherArchitecture")) {
    throw 'Publication contains native dependencies for the wrong architecture.'
}
foreach ($name in @('README.md', 'PRIVACY.md', 'THIRD_PARTY_NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $OutputDirectory
}
$licenseDirectory = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'licenses') -Force | Copy-Item -Destination $licenseDirectory -Recurse -Force
$assets = Get-Content -LiteralPath (Join-Path $root 'src\RedrainCast.App\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$packagePaths = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } |
    ForEach-Object { $_.Value.path.Replace('/', '\') })
$runtime = Get-Content -LiteralPath (Join-Path $OutputDirectory 'RedrainCast.App.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    if ($framework.name -eq 'Microsoft.NETCore.App') {
        $packagePaths += "microsoft.netcore.app.runtime.$rid\$($framework.version)"
    }
}
foreach ($packagePath in ($packagePaths | Sort-Object -Unique)) {
    $package = $null
    foreach ($packageRoot in $packageRoots) {
        $candidate = Join-Path $packageRoot $packagePath
        if (Test-Path -LiteralPath $candidate) { $package = $candidate; break }
    }
    if (-not $package) { throw "Restored dependency not found: $packagePath" }
    $destination = Join-Path $OutputDirectory "licenses\$packagePath"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $package -File |
        Where-Object { $_.Name -match '(?i)license|notice|copyright|\.nuspec$' } |
        Copy-Item -Destination $destination
}
& (Join-Path $PSScriptRoot 'ValidatePublication.ps1') -Architecture $Architecture -PublishedDirectory $OutputDirectory -ExpectedVersion "$Version.0"
Write-Output "Self-contained RedrainCast build: $OutputDirectory"
