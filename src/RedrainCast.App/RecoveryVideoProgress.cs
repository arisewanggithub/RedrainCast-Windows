namespace RedrainCast.App;

internal sealed record RecoveryVideoProgress(uint Generation, long DecodedBaseline, long PresentedBaseline,
    long? KeyPtsUs = null)
{
    public bool HasOutput(long decodedFrames, long presentedFrames, long lastPresentedPtsUs,
        int minimumDecoded, int minimumPresented) =>
        Generation >= 2 && KeyPtsUs is { } key && lastPresentedPtsUs >= key &&
        decodedFrames - DecodedBaseline >= minimumDecoded &&
        presentedFrames - PresentedBaseline >= minimumPresented;
}
