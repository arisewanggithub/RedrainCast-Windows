param(
    [Parameter(Mandatory = $true)][string]$FfmpegPath,
    [Parameter(Mandatory = $true)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'ToneMapGenerator\ToneMapGenerator.csproj'
$destination = Join-Path $root 'src\ScrcpySend.Core\ToneMaps'
New-Item -ItemType Directory -Force -Path $WorkDirectory, $destination | Out-Null
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tone-map generator build failed.' }
$generator = Join-Path $PSScriptRoot 'ToneMapGenerator\bin\Release\net10.0\ToneMapGenerator.dll'
$cube = Join-Path $WorkDirectory 'cube.rgba64le'
& dotnet $generator cube $cube
if ($LASTEXITCODE -ne 0) { throw 'PQ cube generation failed.' }
$peaks = @(100, 200, 400, 800, 1600, 3200, 6400, 10000)
$hashes = @{}
function Convert-Reference([string]$SourceFile, [string]$Dimensions, [int]$Peak, [string]$Output) {
    $clip = $Output + '.mkv'
    $master = $Peak * 10000
    & $FfmpegPath -hide_banner -loglevel error -f rawvideo -pixel_format rgba64le `
        -video_size $Dimensions -framerate 1 -color_primaries bt2020 -color_trc smpte2084 -colorspace 0 -color_range pc `
        -i $SourceFile -vf 'scale=out_color_matrix=bt2020:out_range=limited,format=yuv444p12le' `
        -c:v libx265 -preset ultrafast -x265-params "lossless=1:pools=2:frame-threads=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:hdr10=1:max-cll=$Peak,0:master-display=G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)L($master,1):log-level=error" `
        -frames:v 1 -y $clip
    if ($LASTEXITCODE -ne 0) { throw "HDR reference encoding failed for $Peak nits." }
    & $FfmpegPath -hide_banner -loglevel error -init_hw_device vulkan=reference -filter_hw_device reference `
        -i $clip -vf 'format=rgba64le,hwupload,libplacebo=format=rgba64le:colorspace=gbr:color_primaries=bt709:color_trc=bt709:range=pc:tonemapping=bt.2390:tonemapping_knee_offset=1:gamut_mode=perceptual:peak_detect=false:contrast_recovery=0:dithering=-1,hwdownload,format=rgba64le' `
        -frames:v 1 -f rawvideo -y $Output
    if ($LASTEXITCODE -ne 0) { throw "BT.2390 reference rendering failed for $Peak nits." }
}
foreach ($peak in $peaks) {
    $output = Join-Path $WorkDirectory "bt2390-$peak.rgba64le"
    Convert-Reference $cube '4225x65' $peak $output
    & dotnet $generator pack $output
    if ($LASTEXITCODE -ne 0) { throw "Reference cube packing failed for $peak nits." }
    $asset = Join-Path $destination "bt2390-$peak.rgba16f.gz"
    Copy-Item -LiteralPath ($output + '.gz') -Destination $asset -Force
    $hashes["$peak"] = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
}
$samples = Join-Path $WorkDirectory 'samples.rgba64le'
$sampleOutput = Join-Path $WorkDirectory 'samples-output.rgba64le'
& dotnet $generator samples $samples
if ($LASTEXITCODE -ne 0) { throw 'Reference swatch generation failed.' }
Convert-Reference $samples '64x64' 800 $sampleOutput
& dotnet $generator fixture $sampleOutput (Join-Path $root 'tests\ScrcpySend.Tests\fixtures\bt2390-reference.json')
if ($LASTEXITCODE -ne 0) { throw 'Independent swatch fixture generation failed.' }
$version = (& $FfmpegPath -version 2>&1 | Select-Object -First 1).ToString()
$manifest = @{
    size = 65; peaks = $peaks; input = 'PQ BT.2020 RGB, absolute luminance'; output = 'BT.709 SDR display-referred RGB'
    method = 'libplacebo BT.2390, knee offset 1.0, perceptual gamut mapping'
    layout = 'R varies fastest, then G, then B; little-endian RGBA float16'
    generation = 'Lossless 4:4:4 12-bit HDR reference transport; GPU peak measurement disabled; explicit HDR10 peak per bank.'
    reference = $version; referenceExecutableSha256 = (Get-FileHash -LiteralPath $FfmpegPath -Algorithm SHA256).Hash.ToLowerInvariant(); sha256 = $hashes
}
[IO.File]::WriteAllText((Join-Path $destination 'manifest.json'), ($manifest | ConvertTo-Json -Depth 4))
