param(
    [ValidateSet('arm64', 'x64')][string]$Architecture = 'arm64',
    [string]$CacheDirectory = "$env:LOCALAPPDATA\RedrainCast-build-cache"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null

function Get-VerifiedArchive($name, $url, $hash) {
    $archive = Join-Path $CacheDirectory ($name + '.zip')
    if ((Test-Path $archive) -and (Get-FileHash $archive -Algorithm SHA256).Hash -ne $hash) {
        Write-Warning "Discarding incomplete or invalid cached archive: $archive"
        Remove-Item -LiteralPath $archive -Force
    }
    if (-not (Test-Path $archive)) {
        $staging = Join-Path $CacheDirectory ($name + '.' + [Guid]::NewGuid().ToString('N') + '.partial')
        try {
            curl.exe -fL $url -o $staging
            if ($LASTEXITCODE -ne 0) { throw "Download failed: $name" }
            if ((Get-FileHash $staging -Algorithm SHA256).Hash -ne $hash) { throw "Checksum mismatch: $name" }
            Move-Item -LiteralPath $staging -Destination $archive
        } finally {
            if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Force }
        }
    }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $hash) { throw "Checksum mismatch: $name" }
    $destination = Join-Path $CacheDirectory $name
    if (-not (Test-Path (Join-Path $destination '.complete'))) {
        if ((Test-Path $destination) -and @(Get-ChildItem $destination -Force).Count -gt 0) {
            throw "Incomplete cached extraction: $destination. Remove only this failed extraction before retrying."
        }
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $destination)
        New-Item -ItemType File -Path (Join-Path $destination '.complete') -Force | Out-Null
    }
    return $destination
}

$ffmpegHash = if ($Architecture -eq 'arm64') { 'FDA0F9C4BB02839B95CBFA6F85B8120DAF0C3C2B17146C5C38024261973E6F58' } else { 'B58D10B69B0AD578585C43FEE48833D199DDAD4A7DEBC8CF9CE839F0A61C569B' }
$ffmpeg = Get-VerifiedArchive "ffmpeg-7.1.1-$Architecture-lgpl" `
    "https://github.com/System233/ffmpeg-msvc-prebuilt/releases/download/ffmpeg-7.1.1-r2/ffmpeg-7.1.1-r2_$Architecture-windows-shared-lgpl.zip" `
    $ffmpegHash
$srt = Get-VerifiedArchive 'srt-1.5.7' 'https://github.com/Haivision/srt/archive/refs/tags/v1.5.7.zip' `
    '2F87560102B95137E851B4E64909087BCBCD3CEE13C7F354809804A3E29A0D85'
$hostArchitecture = $env:PROCESSOR_ARCHITEW6432
if ([string]::IsNullOrWhiteSpace($hostArchitecture)) { $hostArchitecture = $env:PROCESSOR_ARCHITECTURE }
if ($hostArchitecture -eq 'ARM64') {
    $hostName = 'arm64'; $zigHost = 'aarch64'; $cmakeHost = 'arm64'; $ninjaHost = 'winarm64'
    $zigHash = 'B5AAC0CCC40DD91E8311B1F257717D8E3903B5FEFB8F659DE6D65A840AD1D0E7'
    $cmakeHash = 'FA648FD417F44E6CB08928964A480ADE0D18DF421F9B623639DBA22F9B301E4E'
    $ninjaHash = 'FB959B674970E36A7C9A23191524B80FB5298FC71FC98BFA42456BCC0A8DFB2F'
} elseif ($hostArchitecture -eq 'AMD64') {
    $hostName = 'x64'; $zigHost = 'x86_64'; $cmakeHost = 'x86_64'; $ninjaHost = 'win'
    $zigHash = '554F5378228923FFD558EAC35E21AF020C73789D87AFEABF4BFD16F2E6FEED2C'
    $cmakeHash = 'D163CD3AB4959B0A53FA8988F2DDBD2E6C501658201E6A154386BAD9DBE4F836'
    $ninjaHash = '26A40FA8595694DEC2FAD4911E62D29E10525D2133C9A4230B66397774AE25BF'
} else { throw "Unsupported build-host architecture: $hostArchitecture" }
$zig = Get-VerifiedArchive "zig-0.14.1-$hostName" "https://ziglang.org/download/0.14.1/zig-$zigHost-windows-0.14.1.zip" $zigHash
$cmake = Get-VerifiedArchive "cmake-3.31.6-$hostName" `
    "https://github.com/Kitware/CMake/releases/download/v3.31.6/cmake-3.31.6-windows-$cmakeHost.zip" $cmakeHash
$ninja = Get-VerifiedArchive "ninja-1.13.1-$hostName" `
    "https://github.com/ninja-build/ninja/releases/download/v1.13.1/ninja-$ninjaHost.zip" $ninjaHash
$cmakeExe = Join-Path $cmake "cmake-3.31.6-windows-$cmakeHost\bin\cmake.exe"
$env:SCRCPYSEND_ZIG = Join-Path $zig "zig-$zigHost-windows-0.14.1\zig.exe"
$env:SCRCPYSEND_TARGET_ARCH = $Architecture
$build = Join-Path $CacheDirectory "srt-$Architecture-build"
& $cmakeExe -S (Join-Path $srt 'srt-1.5.7') -B $build -G Ninja `
    "-DCMAKE_TOOLCHAIN_FILE=$(Join-Path $PSScriptRoot 'srt-toolchain.cmake')" `
    "-DCMAKE_MAKE_PROGRAM=$(Join-Path $ninja 'ninja.exe')" -DCMAKE_BUILD_TYPE=Release `
    -DENABLE_ENCRYPTION=OFF -DENABLE_APPS=OFF -DENABLE_SHARED=ON -DENABLE_STATIC=OFF `
    -DENABLE_STDCXX_SYNC=ON -DENABLE_LOGGING=OFF
if ($LASTEXITCODE -ne 0) { throw 'SRT configuration failed.' }
& $cmakeExe --build $build --parallel 4
if ($LASTEXITCODE -ne 0) { throw "SRT $Architecture build failed." }
$native = Join-Path $root "native\win-$Architecture"
New-Item -ItemType Directory -Path $native -Force | Out-Null
Copy-Item (Join-Path $build 'libsrt.dll') (Join-Path $native 'srt.dll') -Force
foreach ($dll in @('avcodec-61.dll', 'avformat-61.dll', 'avutil-59.dll', 'swresample-5.dll')) {
    Copy-Item (Join-Path $ffmpeg "bin\$dll") $native -Force
}
Copy-Item (Join-Path $ffmpeg 'LICENSE.txt') (Join-Path $native 'FFmpeg-LICENSE.txt') -Force
Copy-Item (Join-Path $ffmpeg 'share\ffmpeg\copyright') (Join-Path $native 'FFmpeg-copyright.txt') -Force
Copy-Item (Join-Path $ffmpeg 'CONTROL') (Join-Path $native 'FFmpeg-build-dependencies.txt') -Force
Copy-Item (Join-Path $ffmpeg 'share\ffmpeg\vcpkg.spdx.json') (Join-Path $native 'FFmpeg-dependencies.spdx.json') -Force
Copy-Item (Join-Path $srt 'srt-1.5.7\LICENSE') (Join-Path $native 'SRT-LICENSE.txt') -Force
Copy-Item (Join-Path $CacheDirectory 'srt-1.5.7.zip') (Join-Path $native 'SRT-1.5.7-source.zip') -Force
Write-Output "Prepared native $Architecture SRT/FFmpeg dependencies: $native"
