using RedrainCast.Protocol;
using RedrainCast.Send.Core;

namespace RedrainCast.Send.Media;

internal static class HdrCaptureFormat
{
    internal static bool Eligible(CaptureSource source, CaptureSettings settings, CaptureColor color, Action<string>? diagnostic)
    {
        if (settings.VideoCodec != "h265" || source.IsWindow || !color.Hdr) return false;
        if (HardwareEncoder.HasNativeHdrCandidate) return true;
        diagnostic?.Invoke("Native HDR10 unavailable: no registered hardware HEVC encoder accepts P010 input. " +
            "Keeping the selected HEVC codec and using the existing HDR-to-SDR GPU path.");
        return false;
    }

    internal static HdrVideoFormat Configure(CaptureSource source, CaptureSettings settings, bool enabled,
        Action<string>? diagnostic)
    {
        source.EnsureAvailable();
        if (!enabled) return HdrVideoFormat.Sdr;
        if (source.IsWindow || settings.VideoCodec != "h265")
            throw new NotSupportedException("Native HDR10 requires HEVC and whole-display capture.");
        var color = source.ReadColor(diagnostic);
        if (!color.Hdr) throw new NotSupportedException("The selected display is no longer HDR-active. Restart sharing to renegotiate color.");
        color.Validate();
        if (settings.VideoColor?.Hdr10 == true) return settings.VideoColor;
        return new(true, source.ReadHdrMetadata(diagnostic));
    }

    internal static void ValidateActive(CaptureSource source, HdrVideoFormat format, CaptureColor color)
    {
        if (format.Hdr10 && (source.IsWindow || !color.Hdr))
            throw new IOException("Source HDR state changed during native HDR10 sharing. Stop and restart sharing to renegotiate color.");
    }
}
