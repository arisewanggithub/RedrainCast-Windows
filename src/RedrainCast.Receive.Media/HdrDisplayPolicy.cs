using System.ComponentModel;
using System.Runtime.InteropServices;
using RedrainCast.Protocol;

namespace RedrainCast.Receive.Media;

public sealed partial record HdrDisplayPolicy(bool NativeHdr, float PeakNits, string Diagnostic)
{
    public static HdrDisplayPolicy Sdr { get; } = new(false, 100, "SDR output: explicit BT.2390/BT.709 mapping.");

    public bool? DesktopHdrActive { get; init; } = false;

    public static HdrDisplayPolicy FromMonitor(bool desktopHdrActive, float peakNits)
    {
        if (!desktopHdrActive) return Sdr;
        return IsValidNativePeak(peakNits)
            ? new(true, peakNits, $"Native HDR scRGB, output peak {peakNits:F0} nits (DXGI).") { DesktopHdrActive = true }
            : new(false, Sdr.PeakNits,
                "Windows HDR is active, but DXGI monitor peak luminance is unavailable or invalid; " +
                "native HDR output disabled. Local HDR-to-SDR BT.2390/BT.709 tone-mapping; Windows HDR setting unchanged.")
                { DesktopHdrActive = true };
    }

    public static HdrDisplayPolicy MetadataUnavailable(string error) =>
        new(false, Sdr.PeakNits, $"DXGI monitor luminance metadata query failed: {error}. " +
            "Native HDR output disabled. Local HDR-to-SDR BT.2390/BT.709 tone-mapping; Windows HDR setting unchanged.")
            { DesktopHdrActive = null };

    internal static HdrDisplayPolicy ReadMetadata(Func<HdrDisplayPolicy> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        try { return query(); }
        catch (HdrDisplayMetadataException error) { return MetadataUnavailable(error.Message); }
        catch (COMException error) when (IsMetadataQueryFailure(error.HResult))
        {
            return MetadataUnavailable(error.Message);
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 5 or 6 or 31 or 50 or 1167)
        {
            return MetadataUnavailable(error.Message);
        }
    }

    internal static bool IsMetadataQueryFailure(int result) => unchecked((uint)result) is
        0x80004001 or 0x80004002 or 0x80004005 or
        0x80070005 or 0x80070006 or 0x8007001F or 0x80070032 or 0x8007048F or
        0x887A0002 or 0x887A0004 or 0x887A0005 or 0x887A0006 or 0x887A0007 or
        0x887A0020 or 0x887A0022 or 0x887A0026 or 0x887A0028;

    private static bool IsValidNativePeak(float peakNits) =>
        float.IsFinite(peakNits) && peakNits >= 80 && peakNits <= 10000;

    public void Validate()
    {
        if (NativeHdr && !IsValidNativePeak(PeakNits))
            throw new NotSupportedException("Native HDR output requires a validated DXGI monitor peak between 80 and 10000 nits.");
    }

    public HdrReceiverCapabilities Capabilities(bool rendererVerified)
    {
        Validate();
        return rendererVerified ? new(true, NativeHdr, true) : HdrReceiverCapabilities.None;
    }
}

internal sealed class HdrDisplayMetadataException(string message) : Exception(message);
