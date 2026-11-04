param(
    [Parameter(Mandatory = $true)][ValidateSet('arm64', 'x64')][string]$Architecture,
    [Parameter(Mandatory = $true)][string]$PublishedDirectory
)
$ErrorActionPreference = 'Stop'
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$nativeDirectories = @(Get-ChildItem -LiteralPath (Join-Path $published 'native') -Force)
if ($nativeDirectories.Count -ne 1 -or $nativeDirectories[0].Name -ne "win-$Architecture") {
    throw 'Publication must contain only the selected native architecture.'
}
foreach ($file in @('RedrainCast.App.exe', 'coreclr.dll', 'Microsoft.UI.Xaml.dll',
    "native\win-$Architecture\avcodec-61.dll", "native\win-$Architecture\avformat-61.dll",
    "native\win-$Architecture\avutil-59.dll", "native\win-$Architecture\swresample-5.dll",
    "native\win-$Architecture\srt.dll")) {
    $path = Join-Path $published $file
    $reader = New-Object IO.BinaryReader([IO.File]::OpenRead($path))
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid PE file: $file" }
        $reader.BaseStream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 0 -or $offset -gt $reader.BaseStream.Length - 6) { throw "Invalid PE offset: $file" }
        $reader.BaseStream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE signature: $file" }
        $expected = if ($Architecture -eq 'arm64') { 0xaa64 } else { 0x8664 }
        if ($reader.ReadUInt16() -ne $expected) { throw "Wrong native architecture: $file" }
    } finally { $reader.Dispose() }
}
foreach ($file in @('RedrainCast.App.pri', 'PRIVACY.md', 'THIRD_PARTY_NOTICES.txt',
    'licenses\ScrcpyReceive-Windows-LICENSE.txt', "native\win-$Architecture\FFmpeg-LICENSE.txt",
    "native\win-$Architecture\SRT-LICENSE.txt", "native\win-$Architecture\SRT-1.5.7-source.zip")) {
    if (-not (Test-Path -LiteralPath (Join-Path $published $file))) { throw "Missing resource or notice: $file" }
}
if (@(Get-ChildItem -LiteralPath $published -Recurse -File -Filter '*.pdb').Count -ne 0) {
    throw 'Publication must not contain PDB files.'
}
Write-Output "Validated $Architecture publication."
