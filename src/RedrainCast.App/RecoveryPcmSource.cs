using System.Diagnostics;
using System.Runtime.CompilerServices;
using ScrcpySend.Core;

namespace RedrainCast.App;

internal sealed class RecoveryPcmSource(TimeProvider? timeProvider = null) : IAudioSource
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    public object Configuration => new { codec = "raw", sampleRate = 48000, channels = 2, csd = Array.Empty<string>(), sampleFormat = "s16le" };
    public void BeginTransmission() { }

    public async IAsyncEnumerable<AudioPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken token)
    {
        var first = (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);
        var started = time.GetTimestamp();
        long index = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            yield return new(new byte[480 * 4], first + index * 10_000);
            index++;
            var remaining = TimeSpan.FromMilliseconds(index * 10) - time.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, time, token);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
