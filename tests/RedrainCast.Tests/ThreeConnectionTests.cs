using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using RedrainCast.Receive.Core;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Tests;

public sealed class ThreeConnectionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActualSenderAndReceiverExchangeTracksAndAcknowledgeStop(bool audioEnabled, bool receiverStops)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var stop = new CancellationTokenSource();
        var playback = new Playback(audioEnabled);
        var notices = new ConcurrentQueue<ReceiverNotice>();
        await using var receiver = new ReceiverServer("Integration receiver", playback, notices.Enqueue, 0, IPAddress.Loopback);
        receiver.Start(false);
        var video = new Video();
        var audio = audioEnabled ? new Audio() : null;
        var run = new SenderSession().RunAsync(new(IPAddress.Loopback, receiver.Port, "Integration receiver", false),
            new(EnableAudio: audioEnabled), video, _ => { }, stop.Token, audio);
        await playback.Presented.Task.WaitAsync(timeout.Token);
        Assert.True(receiver.IsBusy);
        Assert.Equal(audioEnabled ? new[] { 1u, 2u } : [1u], playback.Tracks.Order().ToArray());
        if (receiverStops) await receiver.DisconnectAsync().WaitAsync(timeout.Token);
        else stop.Cancel();
        await run.WaitAsync(timeout.Token);
        while (receiver.IsBusy) await Task.Delay(10, timeout.Token);
        Assert.True(playback.Disposed);
        Assert.True(video.Disposed);
        if (audio is not null) { Assert.True(audio.Disposed); Assert.False(audio.Muted); }
        Assert.DoesNotContain(notices, notice => notice.State is "PROTOCOL" or "INTERNAL" or "Cleanup failed");
    }

    private sealed class Playback(bool audio) : IPlaybackFactory, IPlayback
    {
        public string Description => "Synthetic three-connection integration test";
        public ConcurrentBag<uint> Tracks { get; } = [];
        public TaskCompletionSource Presented { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public ValueTask<IPlayback> OpenAsync(CastRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IPlayback>(this);
        public ValueTask ConfigureAsync(Packet configuration, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask PresentAsync(Packet media, CancellationToken cancellationToken)
        {
            Tracks.Add(media.Track);
            if (Tracks.Contains(1u) && (!audio || Tracks.Contains(2u))) Presented.TrySetResult();
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Video : IVideoSource
    {
        public string EncoderName => "Synthetic test";
        public int Width => 640;
        public int Height => 480;
        public bool Disposed { get; private set; }
        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken token)
        {
            yield return new([0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1], 1000, []);
            await Task.Delay(Timeout.Infinite, token);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Audio : IAudioSource
    {
        public bool Muted { get; private set; }
        public bool Disposed { get; private set; }
        public System.Text.Json.Nodes.JsonNode Configuration => RedrainCast.Protocol.JsonData.Object(
            ("codec", "raw"), ("sampleRate", 48000), ("channels", 2),
            ("csd", RedrainCast.Protocol.JsonData.Value(Array.Empty<string>())), ("sampleFormat", "s16le"));
        public void BeginTransmission() => Muted = true;
        public async IAsyncEnumerable<AudioPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken token)
        {
            yield return new([0, 0, 0, 0], 1000);
            await Task.Delay(Timeout.Infinite, token);
        }
        public ValueTask DisposeAsync() { Disposed = true; Muted = false; return ValueTask.CompletedTask; }
    }
}
