using FFmpeg.AutoGen;
using ScrcpySend.Core;

namespace ScrcpySend.Media;

internal sealed unsafe class FragmentedMp4Muxer : IDisposable
{
    private AVFormatContext* context;
    private AVIOContext* io;
    private AVStream* video;
    private AVStream* audio;
    private readonly avio_alloc_context_write_packet writeCallback;
    private Exception? writeFailure;
    private readonly Action<byte[]> write;
    private readonly long originUs;
    private long pendingBytes;
    private int videoCount, audioCount;
    private long lastVideoUs = -1, lastAudioUs = -1;
    private bool disposed;

    public FragmentedMp4Muxer(CaptureSettings settings, int width, int height, byte[] videoConfiguration,
        byte[]? audioConfiguration, long originUs, Action<byte[]> write)
    {
        FfmpegRuntime.Initialize();
        this.write = write;
        this.originUs = originUs;
        writeCallback = Write;
        try
        {
            AVFormatContext* created = null;
            FfmpegRuntime.Check(ffmpeg.avformat_alloc_output_context2(&created, null, "mp4", null), "Create fragmented MP4");
            context = created;
            // Inputs already share a nonnegative origin; automatic shifting can make a later-arriving audio track negative.
            context->avoid_negative_ts = ffmpeg.AVFMT_AVOID_NEG_TS_DISABLED;
            video = ffmpeg.avformat_new_stream(context, null);
            if (video == null) throw new OutOfMemoryException();
            video->time_base = new() { num = 1, den = 1_000_000 };
            var parameters = video->codecpar;
            parameters->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            parameters->codec_id = settings.VideoCodec switch { "h264" => AVCodecID.AV_CODEC_ID_H264, "h265" => AVCodecID.AV_CODEC_ID_HEVC, _ => AVCodecID.AV_CODEC_ID_AV1 };
            parameters->codec_tag = settings.VideoCodec switch { "h264" => Tag("avc3"), "h265" => Tag("hev1"), _ => Tag("av01") };
            parameters->width = width;
            parameters->height = height;
            parameters->extradata = FfmpegRuntime.ExtraData(videoConfiguration);
            parameters->extradata_size = videoConfiguration.Length;
            if (settings.VideoCodec == "av1" &&
                !Av1.SequenceHeader(videoConfiguration).AsSpan().SequenceEqual(videoConfiguration))
                throw new IOException("AV1 SRT requires the actual sequence header, not an invented configuration.");
            if (audioConfiguration != null)
            {
                audio = ffmpeg.avformat_new_stream(context, null);
                if (audio == null) throw new OutOfMemoryException();
                audio->time_base = new() { num = 1, den = 48000 };
                audio->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
                audio->codecpar->codec_id = AVCodecID.AV_CODEC_ID_AAC;
                audio->codecpar->sample_rate = 48000;
                audio->codecpar->frame_size = 1024;
                ffmpeg.av_channel_layout_default(&audio->codecpar->ch_layout, 2);
                audio->codecpar->extradata = FfmpegRuntime.ExtraData(audioConfiguration);
                audio->codecpar->extradata_size = audioConfiguration.Length;
            }
            var buffer = (byte*)ffmpeg.av_malloc(32768);
            if (buffer == null) throw new OutOfMemoryException();
            io = ffmpeg.avio_alloc_context(buffer, 32768, 1, null, null, writeCallback, null);
            if (io == null) { ffmpeg.av_free(buffer); throw new OutOfMemoryException(); }
            context->pb = io;
            context->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            AVDictionary* options = null;
            try
            {
                ffmpeg.av_dict_set(&options, "use_editlist", "0", 0);
                ffmpeg.av_dict_set(&options, "movflags", "empty_moov+frag_custom+default_base_moof+frag_discont", 0);
                var headerResult = ffmpeg.avformat_write_header(context, &options);
                CheckWrite();
                FfmpegRuntime.Check(headerResult, "Write fMP4 init segment");
                ffmpeg.avio_flush(io);
                CheckWrite();
            }
            finally { ffmpeg.av_dict_free(&options); }
        }
        catch { Dispose(); throw; }
    }

    private static uint Tag(string value) => (uint)(value[0] | value[1] << 8 | value[2] << 16 | value[3] << 24);

    private int Write(void* opaque, byte* bytes, int size)
    {
        try { write(new ReadOnlySpan<byte>(bytes, size).ToArray()); return size; }
        catch (Exception error) { writeFailure ??= error; return -5; }
    }

    private void CheckWrite()
    {
        if (writeFailure is OperationCanceledException cancelled) throw cancelled;
        if (writeFailure != null) throw new IOException("SRT mux output failed.", writeFailure);
    }

    // The next capture PTS is unknown at timed flush. A minimal tail duration avoids extrapolating past it.
    public void Video(EncodedVideo frame, int fps) => Packet(video, frame.Data, frame.PtsUs,
        1, VideoBitstream.IsKeyFrame(
            video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_H264 ? "h264" :
            video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC ? "h265" : "av1", frame.Data, frame.Configuration));
    public void Audio(AudioPacket packet) => Packet(audio, packet.Data, packet.PtsUs, 1024L * 1_000_000 / 48000, true);

    private void Packet(AVStream* stream, byte[] bytes, long ptsUs, long durationUs, bool key)
    {
        if (stream == null || ptsUs < originUs) throw new IOException("Invalid fMP4 track/timestamp.");
        if (ptsUs < (stream == video ? lastVideoUs : lastAudioUs))
            throw new IOException("Encoded fMP4 timestamps moved backwards.");
        if (stream == video) lastVideoUs = ptsUs; else lastAudioUs = ptsUs;
        pendingBytes += bytes.Length;
        if (stream == video) videoCount++; else audioCount++;
        if (pendingBytes > 4 * 1024 * 1024 || videoCount > 64 || audioCount > 100)
            throw new IOException("CONGESTION: fMP4 pending samples exceeded bounded admission.");
        var packet = ffmpeg.av_packet_alloc();
        if (packet == null) throw new OutOfMemoryException();
        try
        {
            FfmpegRuntime.Check(ffmpeg.av_new_packet(packet, bytes.Length), "Allocate mux packet");
            bytes.CopyTo(new Span<byte>(packet->data, bytes.Length));
            packet->stream_index = stream->index;
            var source = new AVRational { num = 1, den = 1_000_000 };
            packet->pts = packet->dts = ffmpeg.av_rescale_q(ptsUs - originUs, source, stream->time_base);
            packet->duration = stream == audio ? 1024 : ffmpeg.av_rescale_q(durationUs, source, stream->time_base);
            packet->flags = key ? ffmpeg.AV_PKT_FLAG_KEY : 0;
            var result = ffmpeg.av_write_frame(context, packet);
            CheckWrite();
            FfmpegRuntime.Check(result, "Mux media sample");
        }
        finally { ffmpeg.av_packet_free(&packet); }
    }

    public void Flush()
    {
        var result = ffmpeg.av_write_frame(context, null);
        CheckWrite();
        FfmpegRuntime.Check(result, "Flush fMP4 fragment");
        ffmpeg.avio_flush(io);
        CheckWrite();
        pendingBytes = 0;
        videoCount = audioCount = 0;
        // Independent tfdt anchors preserve source DTS across timed fragments, including static-video gaps.
        FfmpegRuntime.Check(ffmpeg.av_opt_set(context->priv_data, "movflags", "+frag_discont", 0), "Preserve fragment source timestamps");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (context != null) { ffmpeg.avformat_free_context(context); context = null; }
        if (io != null)
        {
            ffmpeg.av_free(io->buffer);
            var owned = io;
            ffmpeg.avio_context_free(&owned);
            io = null;
        }
        GC.KeepAlive(writeCallback);
    }
}
