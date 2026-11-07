using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace RedrainCast.Tests;

public sealed class BrandIconTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void ExecutableAndWindowUseTheSameRequiredPublishedIcon()
    {
        var project = File.ReadAllText(Fixture("App.csproj"));
        Assert.Contains(@"<ApplicationIcon>..\..\assets\package\RedrainCast.ico</ApplicationIcon>", project);
        Assert.Contains("Link=\"Assets\\RedrainCast.ico\"", project);
        Assert.Contains("CopyToOutputDirectory=\"PreserveNewest\" CopyToPublishDirectory=\"PreserveNewest\"", project);
        var source = File.ReadAllText(Fixture("MainWindow.xaml.cs.txt"));
        Assert.Contains("Path.Combine(AppContext.BaseDirectory, \"Assets\", \"RedrainCast.ico\")", source);
        Assert.Contains("throw new FileNotFoundException", source);
        Assert.True(source.IndexOf("InitializeComponent();", StringComparison.Ordinal) <
            source.IndexOf("AppWindow.SetIcon(iconPath);", StringComparison.Ordinal));
        Assert.Contains("RedrainCast.ico", File.ReadAllText(Fixture("Package.ps1")));
    }

    [Fact]
    public void EveryNativePngFrameMatchesTheRecordedBinaryPayload()
    {
        var bytes = File.ReadAllBytes(Fixture("RedrainCast.ico"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Fixture("asset-manifest.json")));
        var icon = manifest.RootElement.GetProperty("assets").EnumerateArray()
            .Single(asset => asset.GetProperty("path").GetString() == "package/RedrainCast.ico");
        Assert.Equal(icon.GetProperty("sha256").GetString(), Hash(bytes));
        using var reader = new BinaryReader(new MemoryStream(bytes));
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        Assert.Equal(9, reader.ReadUInt16());
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var frames = icon.GetProperty("frames").EnumerateArray().ToArray();
        Assert.Equal(sizes.Length, frames.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            reader.BaseStream.Position = 6 + 16 * i;
            Assert.Equal(sizes[i] == 256 ? 0 : sizes[i], reader.ReadByte());
            Assert.Equal(sizes[i] == 256 ? 0 : sizes[i], reader.ReadByte());
            Assert.Equal(0, reader.ReadUInt16());
            Assert.Equal(1, reader.ReadUInt16());
            Assert.Equal(32, reader.ReadUInt16());
            var length = reader.ReadInt32();
            Assert.Equal(offset, reader.ReadInt32());
            Assert.Equal(offset, frames[i].GetProperty("offset").GetInt32());
            Assert.Equal(length, frames[i].GetProperty("length").GetInt32());
            var png = bytes.AsSpan(offset, length);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8].ToArray());
            Assert.Equal(sizes[i], BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)));
            Assert.Equal(sizes[i], BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)));
            Assert.Equal(sizes[i], frames[i].GetProperty("width").GetInt32());
            Assert.Equal(sizes[i], frames[i].GetProperty("height").GetInt32());
            Assert.Equal(frames[i].GetProperty("sha256").GetString(), Hash(png.ToArray()));
            offset += length;
        }
        Assert.Equal(bytes.Length, offset);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(18)]
    [InlineData(int.MaxValue)]
    public async Task Win32LoadsNativeFramesAndRejectsMalformedIcon(int corruptOffset)
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(AppContext.BaseDirectory, $"brand-icon-{Guid.NewGuid():N}.ico");
        try
        {
            var bytes = File.ReadAllBytes(Fixture("RedrainCast.ico"));
            if (corruptOffset == int.MaxValue) bytes = [.. bytes, 0];
            else if (corruptOffset >= 0) bytes[corruptOffset] ^= 0xff;
            File.WriteAllBytes(path, bytes);
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File",
                Fixture("Validate-AppIcon.ps1"), "-IconPath", path })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True((process.ExitCode == 0) == (corruptOffset == -1), await output + await errors);
        }
        finally { File.Delete(path); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
