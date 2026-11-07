using Xunit;

namespace ScrcpySend.Tests;

public sealed class ProcessVideoSourceShutdownWiringTests
{
    private static string Source => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ProcessVideoSource.cs.txt"));

    [Fact]
    public void GracefulExitFailureIsThrownAfterPipesDrainAndBeforeOwnedResourcesAreDisposed()
    {
        var shutdown = Source[Source.IndexOf("private async Task ShutdownAsync()", StringComparison.Ordinal)..];
        var check = shutdown.IndexOf("process.ExitCode != 0", StringComparison.Ordinal);
        Assert.True(check > shutdown.IndexOf("await monitorTask;", StringComparison.Ordinal));
        Assert.True(check > shutdown.IndexOf("await stderrTask.WaitAsync(", StringComparison.Ordinal));
        Assert.True(check > shutdown.IndexOf("await readinessRead.WaitAsync(", StringComparison.Ordinal));
        Assert.Contains("throw Failure(new IOException($\"{Kind} exited with exit code {process.ExitCode} during shutdown.\"));", shutdown);
        Assert.True(check < shutdown.IndexOf("process.Dispose();", StringComparison.Ordinal));
        Assert.Contains("finally\n", shutdown.Replace("\r\n", "\n"));
        Assert.Contains("job?.Dispose();", shutdown);
        Assert.Contains("lifetime.Dispose();", shutdown);
    }

    [Fact]
    public void ExitCheckExcludesUnstartedProcessesAndOnlySuccessfulDeliberateKills()
    {
        var source = Source;
        Assert.Contains("var forcedTermination = false;", source);
        Assert.Contains("ProcessId != 0 && !forcedTermination && process.HasExited && process.ExitCode != 0", source);
        var kill = source.IndexOf("process.Kill(entireProcessTree: true);", StringComparison.Ordinal);
        var mark = source.IndexOf("forcedTermination = true;", StringComparison.Ordinal);
        var raceCatch = source.IndexOf("catch (InvalidOperationException) when (process.HasExited)", StringComparison.Ordinal);
        Assert.True(kill < mark && mark < raceCatch);
        Assert.Contains("if (!process.HasExited)", source[..kill]);
        Assert.Contains("if (forcedTermination)\n", source.Replace("\r\n", "\n"));
    }

    [Fact]
    public void DisposalCachesTheFaultedTaskAndPreservesOriginalFailureAndStderr()
    {
        var source = Source;
        Assert.Contains("shutdownTask ??= ShutdownAsync()", source);
        Assert.Contains("resourceFailure ?? error", source);
        Assert.Contains("string.Join(\"\\n\", errors)", source);
        Assert.DoesNotContain("resourceFailure = null", source);
    }
}
