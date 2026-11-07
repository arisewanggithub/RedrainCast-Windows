namespace ScrcpySend.Core;

public static class EncoderCompatibility
{
    public readonly record struct UInt32Setting(Guid Property, uint Value);

    private static readonly IReadOnlyList<UInt32Setting> QualcommSettings = Array.AsReadOnly<UInt32Setting>(
    [
        new(new("b28a6e64-3ff9-446a-8a4b-0d7a53413236"), 5), // AVScenarioInfo: CameraRecord.
        new(new("8d390aac-dc5c-4200-b57f-814d04babab2"), 0)  // AVEncMPVDefaultBPictureCount.
    ]);

    public static bool IsQualcomm(uint vendorId) => vendorId is 0x5143 or 0x4D4F4351;

    public static void ValidateCapture(uint vendorId, bool hdr, string codec)
    {
        if (UnsupportedCaptureReason(vendorId, hdr, codec) is { } reason)
            throw new NotSupportedException(reason);
    }

    public static string? UnsupportedCaptureReason(uint vendorId, bool? hdr, string codec)
    {
        _ = OutputProfile(codec);
        if (codec == "av1" && IsQualcomm(vendorId))
            return "Qualcomm AV1 emits hidden/reordered frames that this receiver cannot currently accept. Choose H.264 or HEVC.";
        if (codec == "av1" && hdr == true)
            return "HDR-to-SDR AV1 sending is temporarily disabled pending stability validation. Choose H.264 or HEVC. The codec will not be changed automatically.";
        return null;
    }

    public static uint? OutputProfile(string codec) => codec switch
    {
        "h264" => 66,
        "h265" => 1,
        "av1" => null,
        _ => throw new ArgumentException("Unsupported video codec.", nameof(codec))
    };

    public static IReadOnlyList<UInt32Setting> ForVendor(uint vendorId) =>
        IsQualcomm(vendorId) ? QualcommSettings : Array.Empty<UInt32Setting>();
}
