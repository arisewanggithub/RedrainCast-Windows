using System.Text.Json.Nodes;
using RedrainCast.Protocol;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RedrainCast.Send.Core;

namespace RedrainCast.Send.Media;

public sealed class WindowsAudioSource : IAudioSource
{
    private readonly CancellationTokenSource lifetime = new();
    private RenderMuteLease? mute;
    private Exception? failure;
    private Exception? shutdownFailure;
    private Task? worker;
    private bool started, disposed;
    public JsonNode Configuration => JsonData.Object(("codec", "raw"), ("sampleRate", 48000), ("channels", 2),
        ("csd", JsonData.Value(Array.Empty<string>())), ("sampleFormat", "s16le"));

    public void BeginTransmission()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Volatile.Read(ref mute) != null) throw new InvalidOperationException("Audio transmission already started.");
        var lease = new RenderMuteLease(error => Interlocked.CompareExchange(ref failure, error, null));
        Volatile.Write(ref mute, lease);
        if (Volatile.Read(ref failure) is { } setupFailure) throw setupFailure;
    }

    public async IAsyncEnumerable<AudioPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (started) throw new InvalidOperationException("Audio capture cannot be reused.");
        started = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        var queue = new AudioQueue(1024 * 1024);
        var signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(100) { SingleReader = true, SingleWriter = true });
        worker = Task.Run(() => CaptureAsync(queue, signals.Writer, linked.Token), CancellationToken.None);
        try
        {
            await foreach (var _ in signals.Reader.ReadAllAsync(linked.Token)) yield return queue.Dequeue();
            await worker;
        }
        finally { linked.Cancel(); await worker; }
    }

    private async Task CaptureAsync(AudioQueue queue, ChannelWriter<bool> signals, CancellationToken token)
    {
        Exception? error = null;
        try
        {
            using var activation = CancellationTokenSource.CreateLinkedTokenSource(token);
            activation.CancelAfter(TimeSpan.FromSeconds(10));
            using var client = await AudioInterop.ActivateLoopbackAsync(activation.Token);
            using var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
            using var capture = AudioInterop.InitializeCapture(client, ready);
            AudioInterop.Start(client);
            try
            {
                WaitHandle[] waits = [ready, token.WaitHandle];
                RenderMuteLease? observedMute = null;
                var first = true;
                var progress = System.Diagnostics.Stopwatch.GetTimestamp();
                long previousPts = -1;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref failure) is { } captureFailure) throw captureFailure;
                    var currentMute = Volatile.Read(ref mute);
                    if (currentMute != null && !ReferenceEquals(currentMute, observedMute))
                    {
                        observedMute = currentMute;
                        waits = [ready, token.WaitHandle, currentMute.Changes];
                    }
                    WaitHandle.WaitAny(waits, 100);
                    token.ThrowIfCancellationRequested();
                    observedMute?.ApplyPendingMutes();
                    while (AudioInterop.PacketSize(capture) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        var packet = AudioInterop.ReadPacket(capture, first);
                        if (packet.PtsUs < previousPts) throw new IOException("AUDIO_FAILED: capture clock moved backwards.");
                        previousPts = packet.PtsUs;
                        first = false;
                        queue.Enqueue(packet);
                        if (!signals.TryWrite(true)) throw new IOException("CONGESTION: audio consumer is not keeping up.");
                        progress = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(progress) > TimeSpan.FromSeconds(10))
                        throw new TimeoutException("AUDIO_FAILED: WASAPI supplied no audio buffers, including captured silence, for 10 seconds.");
                }
            }
            finally
            {
                try { AudioInterop.Stop(client); }
                catch (Exception stopError)
                {
                    Interlocked.CompareExchange(ref shutdownFailure, stopError, null);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception caught) { error = caught; }
        finally { signals.TryComplete(error); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        var failures = new List<Exception>();
        try { if (worker != null) await worker; }
        catch (Exception error) { failures.Add(error); }
        if (Volatile.Read(ref shutdownFailure) is { } stopFailure) failures.Add(stopFailure);
        try { mute?.Dispose(); }
        catch (Exception error) { failures.Add(error); }
        finally { lifetime.Dispose(); }
        if (failures.Count > 0) throw new AggregateException("Audio shutdown/restoration failed; check Windows mute settings.", failures);
    }

    public static async Task<string> ProbeAsync(CancellationToken token)
    {
        var outputs = RenderMuteLease.ProbeBindings();
        await using var source = new WindowsAudioSource();
        var count = 0;
        await foreach (var packet in source.ReadPacketsAsync(token))
        {
            if (packet.Data.Length % 4 != 0 || packet.PtsUs < 0) throw new IOException("Invalid captured PCM.");
            if (++count == 10) break;
        }
        return $"WASAPI process loopback: {count} real 48 kHz stereo s16le packets, including captured silence; {outputs} output mute controls/callback bindings checked read-only; simulated volume/auto-unmute notifications pass. No output muting, recording or network transmission.";
    }
}
