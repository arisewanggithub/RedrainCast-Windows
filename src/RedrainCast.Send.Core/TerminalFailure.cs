namespace RedrainCast.Send.Core;

internal sealed class MediaEofException(string message) : IOException(message);
internal sealed class PeerFatalException(string message) : IOException(message);

// Only an orderly media EOF is provisional; transport/protocol/capture errors retain precedence.
internal sealed class TerminalFailure
{
    private readonly object gate = new();
    private Exception? error;
    private string code = "CAPTURE_FAILED";
    private bool peerStopped;
    private long mediaEofAt;

    public Exception? Error { get { lock (gate) return error; } }
    public string Code { get { lock (gate) return code; } }
    public bool IsProvisional { get { lock (gate) return error is MediaEofException; } }
    public TimeSpan DrainRemaining
    {
        get { lock (gate) return TimeSpan.FromMilliseconds(Math.Max(0, 2000 - (Environment.TickCount64 - mediaEofAt))); }
    }

    public void Record(Exception value, string failureCode = "CAPTURE_FAILED")
    {
        lock (gate)
        {
            if (value is MediaEofException && peerStopped) return;
            if (error is null || error is MediaEofException && value is not MediaEofException &&
                (value is not PeerFatalException || Environment.TickCount64 - mediaEofAt < 2000))
            {
                error = value;
                code = failureCode;
                if (value is MediaEofException) mediaEofAt = Environment.TickCount64;
            }
        }
    }

    public void RemoteStop()
    {
        lock (gate)
        {
            peerStopped = true;
            if (error is MediaEofException && Environment.TickCount64 - mediaEofAt < 2000) error = null;
        }
    }

    public static Exception PeerError(System.Text.Json.JsonElement json)
    {
        var code = StrictJson.String(json, "code", 80);
        if (code is not ("VERSION" or "BUSY" or "UNSUPPORTED_CONFIG" or "PROTOCOL" or
            "CAPTURE_FAILED" or "AUDIO_FAILED" or "DECODER_FAILED" or "TIMEOUT" or "CONGESTION" or "INTERNAL"))
            throw new ProtocolException("Unknown receiver error code.");
        var detail = StrictJson.String(json, "message", 2048);
        if (detail.Length == 0)
            throw new ProtocolException("Invalid receiver error message.");
        return StrictJson.Boolean(json, "fatal")
            ? new PeerFatalException($"{code}: {detail}")
            : new IOException($"{code}: {detail}");
    }
}
