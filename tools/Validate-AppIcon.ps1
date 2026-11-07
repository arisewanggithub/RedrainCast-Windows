param(
    [Parameter(Mandatory = $true)][string]$IconPath,
    [string]$ExecutablePath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('RedrainCast.NativeBrandIcon' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
namespace RedrainCast {
    public static class NativeBrandIcon {
        [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
        static extern uint ExtractIconExW(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", SetLastError=true)]
        static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr info);
        [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr info);
        [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        delegate bool ResourceName(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, ResourceName callback, IntPtr param);
        static void Require(bool condition, string message) {
            if (!condition) throw new InvalidDataException(message);
        }
        static bool Equal(byte[] a, byte[] b) {
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(a)) == Convert.ToBase64String(sha.ComputeHash(b));
        }
        static byte[] Resource(IntPtr module, IntPtr name, int type) {
            var info = FindResourceW(module, name, new IntPtr(type));
            if (info == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Missing icon resource.");
            var length = SizeofResource(module, info);
            var data = LockResource(LoadResource(module, info));
            Require(length > 0 && length < 1024 * 1024 && data != IntPtr.Zero, "Invalid icon resource.");
            var bytes = new byte[(int)length]; Marshal.Copy(data, bytes, 0, bytes.Length); return bytes;
        }
        static void CompareIcon(IntPtr handle, Dictionary<int, byte[]> frames) {
            Require(handle != IntPtr.Zero, "Win32 could not load brand icon.");
            using (var icon = Icon.FromHandle(handle))
            using (var actual = icon.ToBitmap()) {
                byte[] png;
                Require(actual.Width == actual.Height && frames.TryGetValue(actual.Width, out png),
                    "Win32 selected an unexpected icon size.");
                png = frames[actual.Width];
                using (var stream = new MemoryStream(png))
                using (var expected = new Bitmap(stream)) {
                    for (int y = 0; y < actual.Height; y++)
                        for (int x = 0; x < actual.Width; x++)
                            Require(actual.GetPixel(x, y).ToArgb() == expected.GetPixel(x, y).ToArgb(),
                                "Win32 icon pixels differ from the native vector PNG.");
                }
            }
        }
        public static void Validate(string path, string executable) {
            var frames = new Dictionary<int, byte[]>();
            int[] sizes = {16,20,24,32,40,48,64,128,256};
            using (var reader = new BinaryReader(File.OpenRead(path))) {
                Require(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1 &&
                    reader.ReadUInt16() == sizes.Length, "Invalid ICO header.");
                long offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++) {
                    reader.BaseStream.Position = 6 + 16 * i;
                    int dimension = sizes[i] == 256 ? 0 : sizes[i];
                    Require(reader.ReadByte() == dimension && reader.ReadByte() == dimension &&
                        reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 32,
                        "Invalid ICO directory metadata.");
                    uint length = reader.ReadUInt32(), start = reader.ReadUInt32();
                    Require(start == offset && length > 32 && length <= reader.BaseStream.Length - offset,
                        "Invalid ICO frame offset/length.");
                    reader.BaseStream.Position = offset;
                    var png = reader.ReadBytes((int)length);
                    Require(png[0] == 137 && png[1] == 80 && png[2] == 78 && png[3] == 71 &&
                        png[4] == 13 && png[5] == 10 && png[6] == 26 && png[7] == 10, "ICO frame must be PNG.");
                    using (var stream = new MemoryStream(png))
                    using (var bitmap = new Bitmap(stream)) {
                        Require(bitmap.Width == sizes[i] && bitmap.Height == sizes[i], "Invalid PNG frame dimensions.");
                        int white = 0, edge = 0;
                        for (int y = 0; y < bitmap.Height; y++)
                            for (int x = 0; x < bitmap.Width; x++) {
                                var c = bitmap.GetPixel(x, y);
                                Require(c.A == 255, "Brand icon must be opaque.");
                                if (c.R == 159 && c.G == 32 && c.B == 54) continue;
                                double t = (c.G - 32) / 223.0;
                                Require(t > 0 && t <= 1 && Math.Abs(c.R - (159 + 96*t)) <= 2 &&
                                    Math.Abs(c.B - (54 + 201*t)) <= 2, "Invalid brand palette.");
                                if (c.R == 255 && c.G == 255 && c.B == 255) white++; else edge++;
                            }
                        Require(white > 0 && edge > 0, "Brand glyph or native antialiasing missing.");
                    }
                    frames.Add(sizes[i], png); offset += length;
                }
                Require(offset == reader.BaseStream.Length, "Unexpected ICO trailing bytes.");
            }
            foreach (int size in sizes) {
                var handle = LoadImageW(IntPtr.Zero, path, 1, size, size, 0x10);
                try { CompareIcon(handle, frames); } finally { if (handle != IntPtr.Zero) DestroyIcon(handle); }
            }
            if (String.IsNullOrEmpty(executable)) return;
            var module = LoadLibraryExW(executable, IntPtr.Zero, 0x22);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                var groups = new List<byte[]>();
                ResourceName callback = (m, t, n, p) => { groups.Add(Resource(m, n, 14)); return true; };
                Require(EnumResourceNamesW(module, new IntPtr(14), callback, IntPtr.Zero) && groups.Count == 1,
                    "Executable must contain exactly one brand icon group.");
                using (var reader = new BinaryReader(new MemoryStream(groups[0]))) {
                    Require(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1 &&
                        reader.ReadUInt16() == sizes.Length, "Executable icon group is incomplete.");
                    foreach (int size in sizes) {
                        int dimension = size == 256 ? 0 : size;
                        Require(reader.ReadByte() == dimension && reader.ReadByte() == dimension &&
                            reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 32,
                            "Executable icon group metadata differs from ICO.");
                        uint length = reader.ReadUInt32(); ushort id = reader.ReadUInt16();
                        var bytes = Resource(module, new IntPtr(id), 3);
                        Require(bytes.Length == length && Equal(bytes, frames[size]),
                            "Embedded executable icon differs from verified ICO PNG payload.");
                    }
                    Require(reader.BaseStream.Position == reader.BaseStream.Length, "Invalid executable icon group length.");
                }
            } finally { FreeLibrary(module); }
            var large = new IntPtr[1]; var small = new IntPtr[1];
            try {
                // Both requested sizes contribute to the extracted handle count.
                Require(ExtractIconExW(executable, 0, large, small, 1) == 2, "ExtractIconEx failed.");
                CompareIcon(large[0], frames); CompareIcon(small[0], frames);
            } finally {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
        }
    }
}
'@
}
$icon = (Resolve-Path -LiteralPath $IconPath).Path
$executable = if ($ExecutablePath) { (Resolve-Path -LiteralPath $ExecutablePath).Path } else { $null }
[RedrainCast.NativeBrandIcon]::Validate($icon, $executable)
Write-Output 'Validated nine native PNG ICO frames with Win32 LoadImage; executable resources/ExtractIconEx checked when supplied.'
