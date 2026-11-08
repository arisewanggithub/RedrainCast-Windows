namespace RedrainCast.Send.Core;

public static class VideoBitstream
{
    public static bool HasConfiguration(string codec, byte[] bytes) => codec switch
    {
        "h264" => AnnexB.Units(bytes).Any(unit => (unit[0] & 31) is 7 or 8),
        "h265" => AnnexB.Units(bytes).Any(unit => (unit[0] >> 1 & 63) is 32 or 33 or 34),
        "av1" => Av1.HasSequenceHeader(bytes),
        _ => throw new ProtocolException("Unsupported video codec.")
    };

    public static byte[] Configuration(string codec, byte[] bytes) => codec switch
    {
        "h264" => AnnexB.ParameterSets(bytes),
        "h265" => Hevc.ParameterSets(bytes),
        "av1" => Av1.SequenceHeader(bytes),
        _ => throw new ProtocolException("Unsupported video codec.")
    };

    public static bool IsKeyFrame(string codec, byte[] data, byte[] configuration) => codec switch
    {
        "h264" => AnnexB.IsIdr(data),
        "h265" => Hevc.IsKeyFrame(data),
        "av1" => Av1.IsKeyFrame(data, configuration),
        _ => throw new ProtocolException("Unsupported video codec.")
    };

    public static void ValidateConfiguration(string codec, byte[] configuration, int width, int height,
        RedrainCast.Protocol.HdrVideoFormat? color = null)
    {
        color?.Validate(codec);
        switch (codec)
        {
            case "h264": H264.ValidateConfiguration(configuration, width, height); break;
            case "h265": Hevc.ValidateConfiguration(configuration, width, height, color); break;
            case "av1": Av1.ValidateConfiguration(configuration, width, height); break;
            default: throw new ProtocolException("Unsupported video codec.");
        }
    }

    public static void ValidateFrame(string codec, byte[] data, byte[] configuration)
    {
        switch (codec)
        {
            case "h264": H264.ValidateAccessUnit(data); break;
            case "h265": Hevc.ValidateAccessUnit(data, configuration); break;
            case "av1": Av1.ValidateAccessUnit(data, configuration); break;
            default: throw new ProtocolException("Unsupported video codec.");
        }
    }
}
