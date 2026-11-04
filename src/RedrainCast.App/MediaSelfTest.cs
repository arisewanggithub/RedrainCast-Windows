using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using RedrainCast.Core;
using ScrcpySend.Core;

namespace RedrainCast.App;

public sealed partial class MainWindow
{
    internal async Task RunMediaSelfTestAsync(string fixtureFile, string reportFile)
    {
        string? failure = null;
        long presented = 0;
        var hardwareSurfaces = 0;
        var videoOnly = false;
        Task? sender = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            if (receiver is null) throw new InvalidOperationException("Listener did not start.");
            await using var frames = new SyntheticSource(fixtureFile);
            var endpoint = new ReceiverEndpoint(IPAddress.Loopback, receiver.Port, "Self-test", false);
            sender = Task.Run(() => new SenderSession().RunAsync(endpoint,
                new CaptureSettings(MaxFps: 30, VideoCodec: frames.Codec), frames, Diagnostic, stop.Token));
            while (video.PresentedFrames < 5)
            {
                if (sender.IsCompleted) await sender;
                await Task.Delay(25, stop.Token);
            }
            presented = video.PresentedFrames;
            hardwareSurfaces = video.HardwareSurfaceCount;
            videoOnly = HomePage.Visibility == Visibility.Collapsed && roles.Role == CastRole.Receiving;
            if (!videoOnly || hardwareSurfaces == 0)
                throw new InvalidOperationException("Incoming cast did not show hardware-decoded video without the home controls.");
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            stop.Cancel();
            if (sender is not null)
            {
                try { await sender; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error) { failure = $"{failure}\nSender: {error}"; }
            }
        }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (roles.Role != CastRole.Idle || video.IsAttached)
                await Task.Delay(25, cleanup.Token);
            Render();
            if (HomePage.Visibility != Visibility.Visible || ReadinessTitle.Text != "READY" || video.KeepsDisplayAwake)
                throw new InvalidOperationException("Ending reception did not restore the ready home and release media resources.");
        }
        catch (Exception error) { failure = $"{failure}\nCleanup: {error}"; }
        await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
        {
            passed = failure is null, failure, presentedFrames = presented, hardwareSurfaces,
            receivingShowsVideoOnly = videoOnly, restoredReadyHome = ReadinessTitle.Text == "READY",
            detached = !video.IsAttached, syntheticOnly = true, physicalDeviceTested = false, diagnostics
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class SyntheticSource : IVideoSource
    {
        private readonly EncodedVideo[] frames;
        public string Codec { get; }
        public string EncoderName => "Synthetic fixture (not captured content)";
        public int Width { get; }
        public int Height { get; }

        public SyntheticSource(string path)
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024)
                throw new ArgumentException("Synthetic fixture exceeds 16 MiB.");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var fixture = document.RootElement;
            if (!fixture.GetProperty("syntheticOnly").GetBoolean())
                throw new ArgumentException("Only a labeled synthetic fixture is allowed.");
            Codec = fixture.TryGetProperty("videoCodec", out var codec) ? codec.GetString()! : "h264";
            var generation = fixture.GetProperty("generations")[0];
            Width = generation.GetProperty("width").GetInt32();
            Height = generation.GetProperty("height").GetInt32();
            frames = generation.GetProperty("frames").EnumerateArray().Select(frame => new EncodedVideo(
                Convert.FromBase64String(frame.GetProperty("data").GetString()!), frame.GetProperty("ptsUs").GetInt64(),
                Convert.FromBase64String(frame.GetProperty("configuration").GetString()!))).ToArray();
            if (frames.Length is < 1 or > 512) throw new ArgumentException("Invalid synthetic GOP.");
        }

        public async IAsyncEnumerable<EncodedVideo> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = frames[index % frames.Length];
                yield return frame with { PtsUs = frames[0].PtsUs + index * 1_000_000L / 30 };
                index++;
                await Task.Delay(34, cancellationToken);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
