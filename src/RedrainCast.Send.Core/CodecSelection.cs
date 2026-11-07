namespace RedrainCast.Send.Core;

public sealed record CodecChoice(string Codec, string Label);

public sealed class CodecSelection(uint vendorId, bool? hdr)
{
    private static readonly CodecChoice[] AllChoices =
    [
        new("h264", "H.264"), new("h265", "H.265 (HEVC)"), new("av1", "AV1")
    ];

    public IReadOnlyList<CodecChoice> Choices { get; } = Array.AsReadOnly(AllChoices
        .Where(choice => EncoderCompatibility.UnsupportedCaptureReason(vendorId, hdr, choice.Codec) is null).ToArray());

    public CodecChoice? Find(string codec)
    {
        _ = EncoderCompatibility.OutputProfile(codec);
        return Choices.SingleOrDefault(choice => choice.Codec == codec);
    }

    public string SavedSelectionMessage(string codec) =>
        EncoderCompatibility.UnsupportedCaptureReason(vendorId, hdr, codec) is { } reason
            ? $"Saved {AllChoices.Single(choice => choice.Codec == codec).Label} unavailable in this context. {reason} Select a supported codec explicitly before saving."
            : "";

    public string ValidateSelection(CodecChoice? choice)
    {
        if (choice is null)
            throw new ArgumentException("Choose a supported video codec before saving.");
        if (EncoderCompatibility.UnsupportedCaptureReason(vendorId, hdr, choice.Codec) is { } reason)
            throw new ArgumentException(reason);
        if (!Choices.Contains(choice))
            throw new ArgumentException("Choose a supported video codec before saving.");
        return choice.Codec;
    }
}
