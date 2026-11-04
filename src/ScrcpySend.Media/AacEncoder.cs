using FFmpeg.AutoGen;
using ScrcpySend.Core;

namespace ScrcpySend.Media;

internal sealed unsafe class AacEncoder : IDisposable
{
    private AVCodecContext* context;
    private AVFrame* frame;
    private AVPacket* packet;
    private readonly Queue<byte> pcm = new();
    private long originUs = -1;
    private long sampleCount;
    public byte[] Configuration { get; }

    public AacEncoder()
    {
        FfmpegRuntime.Initialize();
        try
        {
            var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            if (codec == null) throw new NotSupportedException("AAC-LC encoder unavailable.");
            context = ffmpeg.avcodec_alloc_context3(codec);
            if (context == null) throw new OutOfMemoryException();
            context->sample_rate = 48000;
            context->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            context->bit_rate = 192000;
            context->profile = ffmpeg.FF_PROFILE_AAC_LOW;
            context->time_base = new AVRational { num = 1, den = 48000 };
            ffmpeg.av_channel_layout_default(&context->ch_layout, 2);
            FfmpegRuntime.Check(ffmpeg.avcodec_open2(context, codec, null), "Initialize AAC-LC");
            if (context->frame_size != 1024 || context->extradata_size <= 0)
                throw new NotSupportedException("AAC encoder configuration is not supported.");
            Configuration = AacConfiguration.CanonicalCore(new ReadOnlySpan<byte>(context->extradata, context->extradata_size).ToArray());
            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame == null || packet == null) throw new OutOfMemoryException();
            frame->format = (int)context->sample_fmt;
            frame->sample_rate = 48000;
            frame->nb_samples = 1024;
            ffmpeg.av_channel_layout_copy(&frame->ch_layout, &context->ch_layout);
            FfmpegRuntime.Check(ffmpeg.av_frame_get_buffer(frame, 0), "Allocate AAC input");
        }
        catch { Dispose(); throw; }
    }

    public IReadOnlyList<AudioPacket> Encode(AudioPacket input)
    {
        if (input.Data.Length % 4 != 0) throw new IOException("AAC input must be stereo s16le.");
        if (originUs < 0) originUs = input.PtsUs;
        if (pcm.Count + input.Data.Length > 512 * 1024) throw new IOException("CONGESTION: AAC input buffer exceeded 512 KiB.");
        foreach (var value in input.Data) pcm.Enqueue(value);
        var result = new List<AudioPacket>();
        while (pcm.Count >= 4096)
        {
            FfmpegRuntime.Check(ffmpeg.av_frame_make_writable(frame), "Acquire AAC input");
            for (var index = 0; index < 1024; index++)
            {
                var left = (short)(pcm.Dequeue() | pcm.Dequeue() << 8);
                var right = (short)(pcm.Dequeue() | pcm.Dequeue() << 8);
                ((float*)frame->data[0])[index] = left / 32768f;
                ((float*)frame->data[1])[index] = right / 32768f;
            }
            frame->pts = sampleCount;
            sampleCount += 1024;
            FfmpegRuntime.Check(ffmpeg.avcodec_send_frame(context, frame), "Encode AAC");
            while (true)
            {
                var read = ffmpeg.avcodec_receive_packet(context, packet);
                if (read == -11) break;
                FfmpegRuntime.Check(read, "Read AAC access unit");
                try
                {
                    var pts = originUs + packet->pts * 1_000_000 / 48000;
                    if (pts < 0 || packet->size <= 0 || packet->size > 262144)
                        throw new IOException("AAC produced an invalid access unit or timestamp.");
                    result.Add(new(new ReadOnlySpan<byte>(packet->data, packet->size).ToArray(), pts));
                }
                finally { ffmpeg.av_packet_unref(packet); }
            }
        }
        return result;
    }

    public void Dispose()
    {
        if (frame != null) { var owned = frame; ffmpeg.av_frame_free(&owned); frame = null; }
        if (packet != null) { var owned = packet; ffmpeg.av_packet_free(&owned); packet = null; }
        if (context != null) { var owned = context; ffmpeg.avcodec_free_context(&owned); context = null; }
    }
}
