namespace ScrcpySend.Core;

public static class AacConfiguration
{
    public static byte[] CanonicalCore(byte[] actual)
    {
        var bits = new CodecBits(actual);
        if (bits.Read(5) != 2 || bits.Read(4) != 3 || bits.Read(4) != 2 ||
            bits.Read(1) != 0 || bits.Read(1) != 0 || bits.Read(1) != 0)
            throw new ProtocolException("SRT requires actual AAC-LC stereo 48 kHz, 1024-sample access units.");
        if (actual.Length > 2)
        {
            // FFmpeg may append a sync extension declaring SBR absent. Strip only that inert declaration.
            if (bits.Read(11) != 0x2b7 || bits.Read(5) != 5 || bits.Read(1) != 0)
                throw new ProtocolException("Active or unsupported AAC extension.");
            var remaining = actual.Length * 8 - 33;
            for (var index = 0; index < remaining; index++)
                if (bits.Read(1) != 0) throw new ProtocolException("Unsupported AAC extension data.");
        }
        return actual[..2];
    }
}
