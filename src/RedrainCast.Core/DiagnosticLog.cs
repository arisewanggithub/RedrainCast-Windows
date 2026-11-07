using System.Text;

namespace RedrainCast.Core;

public sealed class DiagnosticLog : IDisposable
{
    private readonly object gate = new();
    private readonly long maximumBytes;
    private readonly int maximumEntryCharacters;
    private StreamWriter writer;
    private bool disposed;
    public string FilePath { get; }

    public DiagnosticLog(string directory, long maximumBytes = 2 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1024);
        this.maximumBytes = maximumBytes;
        maximumEntryCharacters = (int)Math.Min(16_384, maximumBytes / 4 - 64);
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"RedrainCast-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        writer = Open();
        try { Prune(directory); }
        catch { writer.Dispose(); throw; }
    }

    private StreamWriter Open() => new(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
        new UTF8Encoding(false)) { AutoFlush = true };

    public void Write(string entry)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (entry.Length > maximumEntryCharacters)
                entry = entry[..maximumEntryCharacters] + "\n[diagnostic entry truncated]";
            if (writer.BaseStream.Length + Encoding.UTF8.GetByteCount(entry) + Environment.NewLine.Length > maximumBytes)
            {
                writer.Dispose();
                File.Move(FilePath, Path.ChangeExtension(FilePath, ".previous.log"), true);
                writer = Open();
            }
            writer.WriteLine(entry);
        }
    }

    private static void Prune(string directory)
    {
        var retained = 0;
        foreach (var file in new DirectoryInfo(directory).GetFiles("RedrainCast-*.log")
                     .OrderByDescending(file => file.LastWriteTimeUtc))
        {
            try
            {
                // Active app instances hold their log open and must not lose it.
                using (file.Open(FileMode.Open, FileAccess.Read, FileShare.None)) { }
            }
            catch (FileNotFoundException) { continue; }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { continue; }
            if (++retained > 10) file.Delete();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            writer.Dispose();
        }
    }
}
