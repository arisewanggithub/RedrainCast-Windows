param(
    [Parameter(Mandatory = $true)][ValidateSet('arm64', 'x64')][string]$Architecture,
    [Parameter(Mandatory = $true)][string]$PublishedDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+\.0$')][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$sourceRoot = Split-Path -Parent $PSScriptRoot
$sourceIcon = Join-Path $sourceRoot 'assets\package\RedrainCast.ico'
$publishedIcon = Join-Path $published 'Assets\RedrainCast.ico'
if ((Get-FileHash -LiteralPath $publishedIcon -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $sourceIcon -Algorithm SHA256).Hash) {
    throw 'Published window icon differs from the canonical brand asset.'
}
& (Join-Path $PSScriptRoot 'Validate-AppIcon.ps1') -IconPath $publishedIcon -ExecutablePath (Join-Path $published 'RedrainCast.App.exe')
& (Join-Path $PSScriptRoot 'Assert-ShippingBoundary.ps1') -PublishedDirectory $published
$appVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $published 'RedrainCast.App.dll')).Version
if ($ExpectedVersion -and $appVersion -ne [version]$ExpectedVersion) {
    throw "Published application version $appVersion differs from requested $ExpectedVersion."
}
foreach ($file in @('RedrainCast.App.dll', 'RedrainCast.Send.Core.dll', 'RedrainCast.Receive.Core.dll',
    'RedrainCast.Core.dll', 'RedrainCast.Protocol.dll', 'RedrainCast.Send.Media.dll', 'RedrainCast.Receive.Media.dll')) {
    $path = Join-Path $published $file
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($path).Version
    $fileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    if ($assemblyVersion -ne $appVersion -or [version]$fileInfo.FileVersion -ne $appVersion) {
        throw "Mixed release versions in $file (assembly=$assemblyVersion, file=$($fileInfo.FileVersion), app=$appVersion)."
    }
}
if (-not ('RedrainCastPublicationManifest' -as [type])) {
    Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class RedrainCastPublicationManifest {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr LoadResource(IntPtr module, IntPtr info);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern uint SizeofResource(IntPtr module, IntPtr info);
    [DllImport("kernel32.dll")]
    static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);
    public static string Read(string path) {
        var module = LoadLibraryExW(path, IntPtr.Zero, 0x22);
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var info = FindResourceW(module, new IntPtr(1), new IntPtr(24));
            if (info == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Executable manifest is missing.");
            var size = SizeofResource(module, info);
            var data = LockResource(LoadResource(module, info));
            // Self-contained WinUI adds its runtime activation declarations to this resource.
            if (size == 0 || size > 1024 * 1024 || data == IntPtr.Zero)
                throw new InvalidOperationException("Invalid executable manifest resource: bytes=" + size +
                    ", mapped=" + (data != IntPtr.Zero) + ", error=" + Marshal.GetLastWin32Error() + ".");
            var bytes = new byte[(int)size];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return System.Text.Encoding.UTF8.GetString(bytes).Trim('\uFEFF', '\0');
        } finally { FreeLibrary(module); }
    }
}
'@
}
foreach ($file in @('RedrainCast.App.exe')) {
    $path = Join-Path $published $file
    [xml]$manifest = [RedrainCastPublicationManifest]::Read($path)
    $identity = $manifest.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='assemblyIdentity']")
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion
    if ($null -eq $identity -or [version]$identity.version -ne $appVersion -or [version]$fileVersion -ne $appVersion) {
        throw "Executable $file manifest version '$($identity.version)' / file version '$fileVersion' does not match app version $appVersion."
    }
}
$nativeDirectories = @(Get-ChildItem -LiteralPath (Join-Path $published 'native') -Force)
if ($nativeDirectories.Count -ne 1 -or $nativeDirectories[0].Name -ne "win-$Architecture") {
    throw 'Publication must contain only the selected native architecture.'
}
$nativeFiles = @('avcodec-61.dll', 'avutil-59.dll', 'swresample-5.dll',
    'FFmpeg-LICENSE.txt', 'FFmpeg-CREDITS.txt', 'config.h', 'config_components.h', 'FFmpeg-build-manifest.json')
$entries = @(Get-ChildItem -LiteralPath $nativeDirectories[0].FullName -Force)
if ($entries.Count -ne $nativeFiles.Count -or
    @($entries | Where-Object { $_.PSIsContainer -or $nativeFiles -notcontains $_.Name }).Count -ne 0) {
    throw 'Publication contains unexpected native media dependencies; only the decoder/audio runtime is allowed.'
}
foreach ($file in $nativeFiles) {
    if ((Get-FileHash -LiteralPath (Join-Path $nativeDirectories[0].FullName $file) -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $sourceRoot "native\win-$Architecture\$file") -Algorithm SHA256).Hash) {
        throw "Published native dependency differs from the pinned input: $file"
    }
}
foreach ($file in @('RedrainCast.App.exe', 'coreclr.dll', 'Microsoft.UI.Xaml.dll',
    "native\win-$Architecture\avcodec-61.dll",
    "native\win-$Architecture\avutil-59.dll", "native\win-$Architecture\swresample-5.dll")) {
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
    'licenses\ScrcpyReceive-Windows-LICENSE.txt', 'licenses\tone-map-provenance.json',
    "native\win-$Architecture\FFmpeg-LICENSE.txt")) {
    if (-not (Test-Path -LiteralPath (Join-Path $published $file))) { throw "Missing resource or notice: $file" }
}
foreach ($file in @('RedrainCast.CaptureWorker.exe', 'RedrainCast.CaptureWorker.dll',
    'RedrainCast.CaptureWorker.deps.json', 'RedrainCast.CaptureWorker.runtimeconfig.json')) {
    if (Test-Path -LiteralPath (Join-Path $published $file)) {
        throw "Publication must use the unified application executable, not a separate worker: $file"
    }
}
if (Test-Path -LiteralPath (Join-Path $published 'capture-worker')) {
    throw 'The capture worker must share the application runtime from the package root.'
}
if (@(Get-ChildItem -LiteralPath $published -Recurse -File -Filter '*.pdb').Count -ne 0) {
    throw 'Publication must not contain PDB files.'
}
Write-Output "Validated $Architecture publication."
