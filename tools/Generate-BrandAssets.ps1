<#
.SYNOPSIS
Renders RedrainCast Store/package PNGs and Windows ICO from the shared Android SVG.
.DESCRIPTION
Run with Windows PowerShell: powershell.exe -NoProfile -STA -File tools\Generate-BrandAssets.ps1
Every size is rendered directly from vector geometry at 96 DPI, never resized
from a bitmap. Square assets retain the complete 48x48 Android composition.
Rectangular artwork centers that square on the same solid burgundy background.
PosterArt1440x2160 is 2:3 (not 9:16); promotional artwork is optional.
The checked-in SVG is authoritative at runtime; no Android checkout is needed.
The stable manifest records source provenance, palette, output sizes and hashes.
.PARAMETER OutputRoot
Destination for store, package and branding manifest directories; defaults to assets.
Use a separate repository-local directory to test repeatability without replacing assets.
.PARAMETER Verify
Validate all existing PNGs, ICO frames and the manifest without writing any files.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = '',
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'WPF rendering requires STA. Run powershell.exe -NoProfile -STA -File tools\Generate-BrandAssets.ps1.'
}
Add-Type -AssemblyName WindowsBase, PresentationCore

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'assets'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$sourcePath = Join-Path $repositoryRoot 'assets\branding\RedrainCast.svg'

# Read only the deliberately small SVG subset understood by this renderer.
$settings = New-Object Xml.XmlReaderSettings
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$reader = [Xml.XmlReader]::Create($sourcePath, $settings)
try {
    $svg = New-Object Xml.XmlDocument
    $svg.XmlResolver = $null
    $svg.Load($reader)
} finally {
    $reader.Dispose()
}
$namespaces = New-Object Xml.XmlNamespaceManager($svg.NameTable)
$namespaces.AddNamespace('svg', 'http://www.w3.org/2000/svg')
$namespaces.AddNamespace('brand', 'urn:redraincast:branding')
$root = $svg.DocumentElement
if ($root.LocalName -ne 'svg' -or $root.NamespaceURI -ne 'http://www.w3.org/2000/svg' -or
    $root.GetAttribute('viewBox') -ne '0 0 48 48' -or
    $root.GetAttribute('width') -ne '48' -or $root.GetAttribute('height') -ne '48' -or
    $root.GetAttribute('fill-rule') -ne 'nonzero') {
    throw 'Canonical SVG must use the Android 48x48 viewport and nonzero fill rule.'
}
foreach ($element in @($svg.SelectNodes('//*'))) {
    foreach ($attribute in @($element.Attributes)) {
        if ($attribute.Name -in @('transform', 'style', 'opacity', 'fill-opacity', 'stroke', 'clip-path', 'mask')) {
            throw "Unsupported SVG attribute: $($attribute.Name)."
        }
    }
}
$unexpected = $svg.SelectNodes('/svg:svg/*[not(self::svg:title or self::svg:metadata or self::svg:path)]', $namespaces)
$paths = $svg.SelectNodes('/svg:svg/svg:path', $namespaces)
if ($unexpected.Count -ne 0 -or $paths.Count -ne 2) {
    throw 'Canonical SVG must contain exactly the two Android paths, plus title/metadata only.'
}
$background = $svg.SelectSingleNode('/svg:svg/svg:path[@id="background"]', $namespaces)
$foreground = $svg.SelectSingleNode('/svg:svg/svg:path[@id="foreground"]', $namespaces)
if ($null -eq $background -or $null -eq $foreground -or
    $paths[0].GetAttribute('id') -ne 'background' -or
    $background.GetAttribute('fill') -cne '#9F2036' -or
    $foreground.GetAttribute('fill') -cne '#FFFFFF' -or
    $background.GetAttribute('d') -cne 'M0,0h48v48h-48z') {
    throw 'Canonical SVG must preserve the Android burgundy square and white foreground.'
}
$provenance = $svg.SelectSingleNode('/svg:svg/svg:metadata/brand:androidSource', $namespaces)
if ($null -eq $provenance -or $provenance.GetAttribute('repository') -ne 'RedrainCast-Android' -or
    $provenance.GetAttribute('path') -ne 'app/src/main/res/drawable/cast_icon.xml' -or
    $provenance.GetAttribute('sha256') -cnotmatch '^[0-9a-f]{64}$') {
    throw 'Canonical SVG must record the Android repository-relative source path and SHA256.'
}
$backgroundBrush = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($background.GetAttribute('fill'))
$foregroundBrush = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($foreground.GetAttribute('fill'))
$geometry = [Windows.Media.Geometry]::Parse('F1 ' + $foreground.GetAttribute('d'))
$geometry.Freeze()
if ($geometry.Bounds.X -ne 8 -or $geometry.Bounds.Y -ne 10 -or
    $geometry.Bounds.Width -ne 32 -or $geometry.Bounds.Height -ne 30) {
    throw 'Foreground geometry no longer has the Android glyph bounds.'
}
# The asymmetric ring/dot samples also catch reversed arc sweeps or mirrored paths.
$samples = @(
    @(10.0, 12.0, $true), @(38.0, 32.0, $true), @(9.0, 29.5, $true),
    @(18.0, 39.0, $true), @(9.0, 38.0, $true),
    @(10.0, 25.0, $false), @(9.0, 34.0, $false), @(14.0, 38.0, $false),
    @(24.0, 24.0, $false), @(42.0, 12.0, $false)
)
foreach ($sample in $samples) {
    if ($geometry.FillContains([Windows.Point]::new($sample[0], $sample[1])) -ne $sample[2]) {
        throw "Foreground does not preserve the Android geometry at $($sample[0]),$($sample[1])."
    }
}

# Scan every pixel in native code, including large promotional artwork.
if (-not ('RedrainCast.BrandPixelValidator' -as [type])) {
    Add-Type -TypeDefinition @'
namespace RedrainCast {
    public static class BrandPixelValidator {
        public static void Validate(byte[] pixels, int width, int height) {
            double side = System.Math.Min(width, height);
            double scale = side / 48.0;
            double left = (width - side) / 2.0 + 8.0 * scale;
            double top = (height - side) / 2.0 + 10.0 * scale;
            double right = (width - side) / 2.0 + 40.0 * scale;
            double bottom = (height - side) / 2.0 + 40.0 * scale;
            int minX = width, minY = height, maxX = -1, maxY = -1;
            int whiteCount = 0, edgeCount = 0;
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    int i = (y * width + x) * 4;
                    int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    if (pixels[i + 3] != 255)
                        throw new System.IO.InvalidDataException("Every pixel must be opaque.");
                    if (r == 159 && g == 32 && b == 54) continue;
                    // Antialiasing may only mix the two source colors.
                    double t = (g - 32) / 223.0;
                    if (t <= 0 || t > 1 || System.Math.Abs(r - (159 + 96 * t)) > 2 ||
                        System.Math.Abs(b - (54 + 201 * t)) > 2)
                        throw new System.IO.InvalidDataException("Pixel outside the source palette.");
                    if (x + 1 <= left || x >= right || y + 1 <= top || y >= bottom)
                        throw new System.IO.InvalidDataException("Glyph escaped its Android bounds.");
                    minX = System.Math.Min(minX, x); minY = System.Math.Min(minY, y);
                    maxX = System.Math.Max(maxX, x); maxY = System.Math.Max(maxY, y);
                    if (r == 255 && g == 255 && b == 255) whiteCount++; else edgeCount++;
                }
            }
            if (whiteCount == 0 || edgeCount == 0 || System.Math.Abs(minX - left) > 1 ||
                System.Math.Abs(minY - top) > 1 || System.Math.Abs(maxX + 1 - right) > 1 ||
                System.Math.Abs(maxY + 1 - bottom) > 1)
                throw new System.IO.InvalidDataException("Missing glyph, antialiasing, or expected margins.");
        }
    }
}
'@
}

$specifications = @(
    @{ Path = 'store\StoreLogo71x71.png'; Width = 71; Height = 71 },
    @{ Path = 'store\StoreLogo150x150.png'; Width = 150; Height = 150 },
    @{ Path = 'store\StoreLogo300x300.png'; Width = 300; Height = 300 },
    @{ Path = 'store\BoxArt2160x2160.png'; Width = 2160; Height = 2160 },
    @{ Path = 'store\PosterArt1440x2160.png'; Width = 1440; Height = 2160 },
    @{ Path = 'store\HeroArt3840x2160.png'; Width = 3840; Height = 2160 },
    @{ Path = 'package\Cast44.png'; Width = 44; Height = 44 },
    @{ Path = 'package\Cast50.png'; Width = 50; Height = 50 },
    @{ Path = 'package\Cast150.png'; Width = 150; Height = 150 }
)
function Render-BrandPng([int]$width, [int]$height) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    try {
        $context.DrawRectangle($backgroundBrush, $null, [Windows.Rect]::new(0, 0, $width, $height))
        $side = [Math]::Min($width, $height)
        $context.PushTransform([Windows.Media.TranslateTransform]::new(($width - $side) / 2.0, ($height - $side) / 2.0))
        $context.PushTransform([Windows.Media.ScaleTransform]::new($side / 48.0, $side / 48.0))
        $context.DrawGeometry($foregroundBrush, $null, $geometry)
        $context.Pop()
        $context.Pop()
    } finally {
        $context.Close()
    }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return ,$stream.ToArray()
    } finally {
        $stream.Dispose()
    }
}
function Assert-BrandPng([byte[]]$bytes, [int]$width, [int]$height) {
    $stream = [IO.MemoryStream]::new($bytes, $false)
    try {
        $decoder = [Windows.Media.Imaging.PngBitmapDecoder]::new($stream,
            [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $frame = $decoder.Frames[0]
        if ($decoder.Frames.Count -ne 1 -or $frame.PixelWidth -ne $width -or $frame.PixelHeight -ne $height -or
            [Math]::Abs($frame.DpiX - 96) -gt 0.1 -or [Math]::Abs($frame.DpiY - 96) -gt 0.1 -or
            $frame.Format -notin @([Windows.Media.PixelFormats]::Bgra32, [Windows.Media.PixelFormats]::Bgr32, [Windows.Media.PixelFormats]::Rgb24)) {
            throw "Unexpected PNG dimensions, DPI, or truecolor format: ${width}x$height"
        }
        $converted = [Windows.Media.Imaging.FormatConvertedBitmap]::new($frame, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
        $pixels = New-Object byte[] ($width * $height * 4)
        $converted.CopyPixels($pixels, $width * 4, 0)
        [RedrainCast.BrandPixelValidator]::Validate($pixels, $width, $height)
        # Tiny frames also receive the full-pixel palette, opacity and glyph-bounds check.
        foreach ($sample in @($samples | Where-Object { $width -ge 44 })) {
            $side = [Math]::Min($width, $height)
            $x = [int][Math]::Floor(($width - $side) / 2.0 + $sample[0] * $side / 48.0)
            $y = [int][Math]::Floor(($height - $side) / 2.0 + $sample[1] * $side / 48.0)
            $green = $pixels[($y * $width + $x) * 4 + 1]
            if (($sample[2] -and $green -lt 180) -or (-not $sample[2] -and $green -gt 100)) {
                throw "PNG does not preserve Android foreground/negative space: ${width}x$height ($x,$y)."
            }
        }
    } finally {
        $stream.Dispose()
    }
}
function Get-BytesHash([byte[]]$bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
$entries = @()
foreach ($specification in $specifications) {
    $path = Join-Path $OutputRoot $specification.Path
    $width = $specification.Width
    $height = $specification.Height
    if (-not $Verify) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllBytes($path, (Render-BrandPng $width $height))
    }
    Assert-BrandPng ([IO.File]::ReadAllBytes($path)) $width $height
    $entries += [ordered]@{
        path = $specification.Path.Replace('\', '/')
        width = $width
        height = $height
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$iconPath = Join-Path $OutputRoot 'package\RedrainCast.ico'
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
if (-not $Verify) {
    $frames = @($iconSizes | ForEach-Object { ,(Render-BrandPng $_ $_) })
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($iconPath))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $iconSizes.Count
        for ($i = 0; $i -lt $iconSizes.Count; $i++) {
            $dimension = if ($iconSizes[$i] -eq 256) { 0 } else { $iconSizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose() }
}
$frameEntries = @()
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($iconPath))
try {
    if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1 -or $reader.ReadUInt16() -ne $iconSizes.Count) {
        throw 'Invalid ICO header.'
    }
    $offset = 6 + 16 * $iconSizes.Count
    for ($i = 0; $i -lt $iconSizes.Count; $i++) {
        $reader.BaseStream.Position = 6 + 16 * $i
        $dimension = if ($iconSizes[$i] -eq 256) { 0 } else { $iconSizes[$i] }
        if ($reader.ReadByte() -ne $dimension -or $reader.ReadByte() -ne $dimension -or
            $reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1 -or $reader.ReadUInt16() -ne 32) {
            throw "Invalid ICO directory entry: $($iconSizes[$i])."
        }
        $length = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne $offset -or $length -lt 32 -or $length -gt 1MB -or
            $length -gt $reader.BaseStream.Length - $offset) { throw 'Invalid ICO frame offset or length.' }
        $reader.BaseStream.Position = $offset
        $bytes = $reader.ReadBytes($length)
        if (-not $Verify -and (Get-BytesHash $bytes) -cne (Get-BytesHash $frames[$i])) {
            throw "ICO frame differs from its native vector render: $($iconSizes[$i])."
        }
        Assert-BrandPng $bytes $iconSizes[$i] $iconSizes[$i]
        $frameEntries += [ordered]@{ width = $iconSizes[$i]; height = $iconSizes[$i]; offset = $offset
            length = $bytes.Length; sha256 = (Get-BytesHash $bytes) }
        $offset += $bytes.Length
    }
    if ($reader.BaseStream.Length -ne $offset) { throw 'Unexpected trailing or missing ICO bytes.' }
} finally { $reader.Dispose() }
& (Join-Path $PSScriptRoot 'Validate-AppIcon.ps1') -IconPath $iconPath
$entries += [ordered]@{ path = 'package/RedrainCast.ico'
    sha256 = (Get-FileHash -LiteralPath $iconPath -Algorithm SHA256).Hash.ToLowerInvariant()
    frames = $frameEntries }

$manifest = [ordered]@{
    schemaVersion = 1
    source = [ordered]@{
        path = 'assets/branding/RedrainCast.svg'
        sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        androidRepository = $provenance.GetAttribute('repository')
        androidPath = $provenance.GetAttribute('path')
        androidSha256 = $provenance.GetAttribute('sha256')
        viewBox = '0 0 48 48'
        fillRule = 'nonzero'
        background = $background.GetAttribute('fill')
        foreground = $foreground.GetAttribute('fill')
    }
    rendering = [ordered]@{
        renderer = 'WPF DrawingVisual / RenderTargetBitmap / PngBitmapEncoder'
        dpi = 96
        composition = 'Full Android square; rectangular artwork centers the same min(width,height) square on the solid background.'
        transparency = 'opaque'
        storeArtworkGuidance = 'https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images'
        posterAspectRatio = '2:3'
    }
    assets = $entries
}
$manifestPath = Join-Path $OutputRoot 'branding\asset-manifest.json'
$manifestText = ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine
if ($Verify) {
    if ([IO.File]::ReadAllText($manifestPath) -cne $manifestText) {
        throw 'Asset manifest does not match the canonical SVG and validated PNGs. Regenerate the assets.'
    }
} else {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifestPath)) | Out-Null
    [IO.File]::WriteAllText($manifestPath, $manifestText, [Text.UTF8Encoding]::new($false))
}
Write-Host "Validated $($specifications.Count) native-vector PNG assets, $($iconSizes.Count)-frame ICO and manifest in $OutputRoot."
