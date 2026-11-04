using System.Diagnostics;
using System.Net;
using ScrcpySend.Core;

namespace ScrcpySend.Media;

public sealed class SrtMedia : ISrtMedia
{
    public static event Action<string>? NativeDiagnostic
    {
        add => FfmpegRuntime.Diagnostic += value;
        remove => FfmpegRuntime.Diagnostic -= value;
    }
    private readonly Action<string>? diagnostic;
    public SrtMedia(Action<string>? diagnostic = null) => this.diagnostic = diagnostic;
    private SrtNative? transport;
    public SrtDescriptor Prepare(IPAddress localAddress, IPAddress receiverAddress, long session)
    {
        if (transport != null) throw new InvalidOperationException("SRT listener already prepared.");
        FfmpegRuntime.Initialize();
        transport = new(localAddress, receiverAddress, session);
        return transport.Descriptor;
    }

    public Task RunAsync(IVideoSource video, IAudioSource? audio, CaptureSettings settings, CancellationToken token) =>
        Task.Run(async () =>
        {
            using var run = CancellationTokenSource.CreateLinkedTokenSource(token);
            token = run.Token;
            var owned = transport ?? throw new InvalidOperationException("SRT listener not prepared.");
            await owned.AcceptAsync(token);
            await using var frames = video.ReadFramesAsync(token).GetAsyncEnumerator(token);
            await using var packets = audio?.ReadPacketsAsync(token).GetAsyncEnumerator(token);
            using var encoder = audio == null ? null : new AacEncoder();
            var nextVideo = frames.MoveNextAsync().AsTask();
            var nextAudio = packets?.MoveNextAsync().AsTask();
            try
            {
            await nextVideo;
            if (!nextVideo.Result) throw new IOException("SRT capture ended before its first video frame.");
            var firstVideo = frames.Current;
            if (!VideoBitstream.IsKeyFrame(settings.VideoCodec, firstVideo.Data, firstVideo.Configuration))
                throw new IOException("SRT capture requires an authentic initial keyframe.");
            IReadOnlyList<AudioPacket> firstAudio = [];
            if (packets != null)
            {
                while (firstAudio.Count == 0)
                {
                    if (!await nextAudio!) throw new IOException("SRT audio ended during startup.");
                    firstAudio = encoder!.Encode(packets.Current);
                    nextAudio = packets.MoveNextAsync().AsTask();
                }
            }
            var origin = Math.Min(firstVideo.PtsUs, firstAudio.Count > 0 ? firstAudio[0].PtsUs : firstVideo.PtsUs);
            diagnostic?.Invoke($"SRT mux initializing codec={settings.VideoCodec} size={video.Width}x{video.Height} videoPtsUs={firstVideo.PtsUs} audioPtsUs={(firstAudio.Count > 0 ? firstAudio[0].PtsUs : -1)} originUs={origin} videoBytes={firstVideo.Data.Length} config={Convert.ToHexString(firstVideo.Configuration)}");
            using var mux = new FragmentedMp4Muxer(settings, video.Width, video.Height, firstVideo.Configuration,
                encoder?.Configuration, origin, data => owned.Write(data, token));
            mux.Video(firstVideo, settings.MaxFps);
            foreach (var packet in firstAudio) mux.Audio(packet);
            mux.Flush();
            nextVideo = frames.MoveNextAsync().AsTask();
            var flushAt = Stopwatch.GetTimestamp();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (nextVideo.IsCompleted)
                {
                    if (!await nextVideo) throw new IOException("SRT video capture ended unexpectedly.");
                    mux.Video(frames.Current, settings.MaxFps);
                    nextVideo = frames.MoveNextAsync().AsTask();
                }
                if (nextAudio?.IsCompleted == true)
                {
                    if (!await nextAudio) throw new IOException("SRT audio capture ended unexpectedly.");
                    foreach (var accessUnit in encoder!.Encode(packets!.Current)) mux.Audio(accessUnit);
                    nextAudio = packets!.MoveNextAsync().AsTask();
                }
                if (Stopwatch.GetElapsedTime(flushAt) >= TimeSpan.FromMilliseconds(100))
                {
                    mux.Flush();
                    flushAt = Stopwatch.GetTimestamp();
                }
                await Task.Delay(2, token);
            }
            }
            finally
            {
                run.Cancel();
                if (!nextVideo.IsCompleted)
                {
                    try { await nextVideo; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }
                if (nextAudio != null && !nextAudio.IsCompleted)
                {
                    try { await nextAudio; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }
            }
        }, CancellationToken.None);

    public void Abort() => transport?.Dispose();
    public ValueTask DisposeAsync() { Abort(); return ValueTask.CompletedTask; }
}
