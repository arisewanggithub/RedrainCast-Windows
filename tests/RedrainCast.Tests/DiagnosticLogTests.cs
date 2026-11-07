using RedrainCast.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("RedrainCast-log-test-");

    [Fact]
    public void EntriesAreFlushedAndReadableWhileTheAppRuns()
    {
        using var log = new DiagnosticLog(directory.FullName);
        log.Write("stage=waiting for encoder input request; captured=0; encoded=0");
        using var input = new FileStream(log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(input);
        Assert.Contains("captured=0; encoded=0", reader.ReadToEnd());
    }

    [Fact]
    public void RotationAndOversizedEntriesStayWithinTheByteBudget()
    {
        string path;
        using (var log = new DiagnosticLog(directory.FullName, maximumBytes: 1024))
        {
            path = log.FilePath;
            for (var index = 0; index < 40; index++) log.Write($"entry={index}: {new string('x', 100)}");
            log.Write(new string('\u4e2d', 4000));
            log.Write("final failure: captured=4; submitted=4; encoded=0");
        }
        var files = directory.GetFiles("RedrainCast-*.log");
        Assert.Equal(2, files.Length);
        Assert.All(files, file => Assert.InRange(file.Length, 1, 1024));
        Assert.Contains("final failure: captured=4", File.ReadAllText(path));
        Assert.Contains("[diagnostic entry truncated]", File.ReadAllText(path));
    }

    [Fact]
    public void MultipleInstancesUseSeparateLogsAndRetentionPreservesActiveAndUnrelatedFiles()
    {
        var unrelated = Path.Combine(directory.FullName, "unrelated.txt");
        File.WriteAllText(unrelated, "preserve");
        using var active = new DiagnosticLog(directory.FullName);
        active.Write("still active");
        for (var index = 0; index < 15; index++)
        {
            using var log = new DiagnosticLog(directory.FullName);
            Assert.NotEqual(active.FilePath, log.FilePath);
            log.Write($"closed instance {index}");
        }
        Assert.True(File.Exists(active.FilePath));
        Assert.True(File.Exists(unrelated));
        Assert.InRange(directory.GetFiles("RedrainCast-*.log").Length, 2, 12);
        active.Write("still writable");
    }

    [Fact]
    public void LogCreationErrorsAreNotSilentlyIgnored()
    {
        var blocked = Path.Combine(directory.FullName, "file-not-directory");
        File.WriteAllText(blocked, "occupied");
        Assert.Throws<IOException>(() => new DiagnosticLog(blocked));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticLog(directory.FullName, 1023));
    }

    public void Dispose() => directory.Delete(true);
}
