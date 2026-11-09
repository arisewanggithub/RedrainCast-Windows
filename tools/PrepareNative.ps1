param(
    [ValidateSet('arm64', 'x64')][string]$Architecture = 'arm64',
    [string]$CacheDirectory = "$env:LOCALAPPDATA\RedrainCast-build-cache"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifact = 'redraincast-ffmpeg-windows-decoders'
$version = '7.1.3-r2'
$tag = "redraincast-ffmpeg-$version"
$hash = if ($Architecture -eq 'arm64') { '817530BB40B5C8E7DDDE830FDF70279BA3B9E1093853741422597194BAAAFF6C' } else { '408D878CCA7A9C839CB4F588D406F69D4FA5C51DEECDA295EEAE21246E62272B' }
New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
$archive = Join-Path $CacheDirectory "$artifact-$Architecture-$version.zip"
if (-not (Test-Path -LiteralPath $archive)) {
    $partial = Join-Path $CacheDirectory ([guid]::NewGuid().ToString('N') + '.partial')
    try {
        & curl.exe -fL "https://github.com/arisewanggithub/RedrainCast-Native/releases/download/$tag/$artifact-$Architecture-$version.zip" -o $partial
        if ($LASTEXITCODE -ne 0) { throw 'Native dependency download failed.' }
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $hash) { throw 'Native archive SHA256 mismatch.' }
        Move-Item -LiteralPath $partial -Destination $archive
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $hash) {
    throw 'Cached native archive SHA256 mismatch; cache was left untouched.'
}
$native = Join-Path $root "native\win-$Architecture"
if ((Test-Path -LiteralPath $native) -and @(Get-ChildItem -LiteralPath $native -Force).Count -gt 0) {
    throw 'Native output already exists. Preserve it; select a clean checkout for regeneration.'
}
$extract = Join-Path $CacheDirectory ("decoder-extract-" + [guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $extract | Out-Null
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $extract)
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    foreach ($file in @('avcodec-61.dll', 'avutil-59.dll', 'swresample-5.dll',
        'FFmpeg-LICENSE.txt', 'FFmpeg-CREDITS.txt', 'config.h', 'config_components.h')) {
        Copy-Item -LiteralPath (Join-Path $extract $file) -Destination $native
    }
    Copy-Item -LiteralPath (Join-Path $extract 'manifest.json') -Destination (Join-Path $native 'FFmpeg-build-manifest.json')
} finally {
    $owned = (Resolve-Path -LiteralPath $extract).Path
    if ((Split-Path -Parent $owned) -ne (Resolve-Path -LiteralPath $CacheDirectory).Path -or
        (Split-Path -Leaf $owned) -notmatch '^decoder-extract-[a-f0-9]{32}$') { throw 'Refusing unsafe extraction cleanup.' }
    Remove-Item -LiteralPath $owned -Recurse -Force
}
Write-Output "Prepared checksum-pinned FFmpeg decoder/audio runtime for $Architecture."
