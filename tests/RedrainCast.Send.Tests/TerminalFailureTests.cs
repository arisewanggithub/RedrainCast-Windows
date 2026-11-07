using System.Reflection;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class TerminalFailureTests
{
    private static readonly Assembly Core = typeof(SenderSession).Assembly;
    private readonly Type type = Core.GetType("RedrainCast.Send.Core.TerminalFailure", true)!;
    private readonly object terminal = Activator.CreateInstance(Core.GetType("RedrainCast.Send.Core.TerminalFailure", true)!, true)!;

    private Exception? Error => (Exception?)type.GetProperty("Error")!.GetValue(terminal);
    private string Code => (string)type.GetProperty("Code")!.GetValue(terminal)!;
    private void Record(Exception error, string code = "CAPTURE_FAILED") =>
        type.GetMethod("Record")!.Invoke(terminal, [error, code]);
    private void Stop() => type.GetMethod("RemoteStop")!.Invoke(terminal, null);
    private void AgeEof() => type.GetField("mediaEofAt", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(terminal, Environment.TickCount64 - 3000);
    private static Exception InternalError(string name) =>
        (Exception)Activator.CreateInstance(Core.GetType($"RedrainCast.Send.Core.{name}", true)!, ["fixture"])!;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IndependentFailureReplacesEofAndSurvivesStop(bool stale, bool protocol)
    {
        Record(InternalError("MediaEofException"));
        if (stale) AgeEof();
        Exception failure = protocol ? new ProtocolException("invalid proof") : new IOException("capture/write failed");
        Record(failure, "PROTOCOL");
        Stop();
        Assert.Same(failure, Error);
        Assert.Equal("PROTOCOL", Code);
    }

    [Fact]
    public void FirstRealFailureRetainsItsCodeAcrossEofStopAndPeerFatal()
    {
        var failure = new IOException("audio write failed");
        Record(failure, "AUDIO_FAILED");
        Record(InternalError("MediaEofException"), "PROTOCOL");
        Record(InternalError("PeerFatalException"), "PROTOCOL");
        Stop();
        Assert.Same(failure, Error);
        Assert.Equal("AUDIO_FAILED", Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PeerFatalReplacesOnlyAnEofInsideItsGrace(bool stale)
    {
        var eof = InternalError("MediaEofException");
        var fatal = InternalError("PeerFatalException");
        Record(eof);
        if (stale) AgeEof();
        Record(fatal, "PROTOCOL");
        Assert.Same(stale ? eof : fatal, Error);
        Assert.Equal(stale ? "CAPTURE_FAILED" : "PROTOCOL", Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopClearsOnlyAnUnaccompaniedEofInsideItsGrace(bool stale)
    {
        var eof = InternalError("MediaEofException");
        Record(eof);
        if (stale) AgeEof();
        Stop();
        Assert.Same(stale ? eof : null, Error);
        Record(InternalError("MediaEofException"));
        Assert.Same(stale ? eof : null, Error);
        var failure = new IOException("independent control failure");
        Record(failure, "PROTOCOL");
        Assert.Same(failure, Error);
        Assert.Equal("PROTOCOL", Code);
    }

    [Fact]
    public void RepeatedEofDoesNotExtendGrace()
    {
        var eof = InternalError("MediaEofException");
        Record(eof);
        AgeEof();
        Record(InternalError("MediaEofException"), "PROTOCOL");
        Assert.Equal(TimeSpan.Zero, (TimeSpan)type.GetProperty("DrainRemaining")!.GetValue(terminal)!);
        Stop();
        Assert.Same(eof, Error);
        Assert.Equal("CAPTURE_FAILED", Code);
    }
}
