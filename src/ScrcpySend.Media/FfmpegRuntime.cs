using FFmpeg.AutoGen;
using ScrcpySend.Core;

namespace ScrcpySend.Media;

internal static unsafe class FfmpegRuntime
{
    public static event Action<string>? Diagnostic;
    private static readonly av_log_set_callback_callback LogCallback = NativeLog;
    private static readonly object Gate = new();
    private static bool initialized;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (initialized) return;
            ffmpeg.RootPath = NativeArchitecture.LibraryDirectory;
            if (!Directory.Exists(ffmpeg.RootPath))
                throw new NotSupportedException("SRT native media dependencies are missing. Run tools\\PrepareSrt.ps1 and rebuild.");
            if (ffmpeg.avformat_version() >> 16 != 61 || ffmpeg.avcodec_version() >> 16 != 61)
                throw new NotSupportedException("SRT requires the pinned FFmpeg 7.1 native ABI.");
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            ffmpeg.av_log_set_callback(LogCallback);
            initialized = true;
        }
    }

    private static void NativeLog(void* context, int level, string format, byte* arguments)
    {
        if (level > ffmpeg.AV_LOG_WARNING) return;
        try
        {
            var bytes = stackalloc byte[4096];
            var prefix = 1;
            ffmpeg.av_log_format_line2(context, level, format, arguments, bytes, 4096, &prefix);
            var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)bytes) ?? "Empty FFmpeg diagnostic.";
            System.Diagnostics.Trace.TraceError(text);
            Diagnostic?.Invoke($"FFmpeg: {text.TrimEnd()}");
        }
        catch (Exception error) { System.Diagnostics.Trace.TraceError($"FFmpeg diagnostic delivery failed: {error}"); }
    }

    public static void Check(int result, string operation)
    {
        if (result >= 0) return;
        var buffer = stackalloc byte[256];
        ffmpeg.av_strerror(result, buffer, 256);
        throw new IOException($"{operation}: {System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buffer)}");
    }

    public static byte* ExtraData(byte[] bytes)
    {
        var pointer = (byte*)ffmpeg.av_mallocz((ulong)(bytes.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        if (pointer == null) throw new OutOfMemoryException();
        bytes.CopyTo(new Span<byte>(pointer, bytes.Length));
        return pointer;
    }
}
