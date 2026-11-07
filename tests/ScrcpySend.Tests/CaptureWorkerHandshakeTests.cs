using System.Net;
using System.Net.Sockets;
using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class CaptureWorkerHandshakeTests
{
    [Fact]
    public void PreparationRequiresExplicitOptInBeforeAnySourceInitialization()
    {
        string[] metadata = ["1", "window", "{}", "640", "360", "123", "456"];
        Assert.Throws<ProtocolException>(() => CaptureWorkerHandshake.MetadataArguments(metadata));
        Assert.Throws<ProtocolException>(() => CaptureWorkerHandshake.MetadataArguments(["wrong", .. metadata]));
        Assert.Throws<ProtocolException>(() => CaptureWorkerHandshake.MetadataArguments(["--prepare", .. metadata]));
        Assert.Equal(metadata, CaptureWorkerHandshake.MetadataArguments([CaptureWorkerHandshake.PreparationArgument, .. metadata]));
    }

    [Fact]
    public async Task ReadyContainsOnlyMetadataAndLeavesInitialCsdForAfterStart()
    {
        using var stream = new MemoryStream();
        await CaptureWorkerHandshake.WriteReadyAsync(stream, 640, 360, "h264", default);
        Assert.Equal(16, stream.Length);
        await new PacketPipeWriter(stream, 640, 360).WriteAsync(new([4, 5], 12345, [1, 2, 3]), default);
        stream.Position = 0;
        await CaptureWorkerHandshake.ReadReadyAsync(stream, 640, 360, "h264", default);
        Assert.Equal(16, stream.Position);
        var reader = new FfmpegPacketReader(stream, 640, 360);
        await reader.InitializeAsync(default);
        var frame = await reader.ReadAsync(default);
        Assert.Equal(12345, frame.PtsUs);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Configuration);
        Assert.Equal(new byte[] { 4, 5 }, frame.Data);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongRevisionOrRasterFailsBeforeStart(bool revision)
    {
        using var stream = new MemoryStream();
        await CaptureWorkerHandshake.WriteReadyAsync(stream, 640, 360, "h264", default);
        if (revision) stream.GetBuffer()[3] = (byte)'1';
        stream.Position = 0;
        await Assert.ThrowsAsync<ProtocolException>(() =>
            CaptureWorkerHandshake.ReadReadyAsync(stream, revision ? 640 : 638, 360, "h264", default));
    }

    [Fact]
    public async Task TruncatedReadyCannotBeMistakenForSuccessfulPreparation()
    {
        using var stream = new MemoryStream("RCW2"u8.ToArray());
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            CaptureWorkerHandshake.ReadReadyAsync(stream, 640, 360, "h264", default));
    }

    [Fact]
    public async Task CancellationPreventsReady()
    {
        using var stream = new MemoryStream();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CaptureWorkerHandshake.ReadReadyAsync(stream, 640, 360, "h264", stop.Token));
    }

    [Theory]
    [InlineData(639, 360)]
    [InlineData(640, 0)]
    public async Task InvalidMetadataCannotAdvertiseReadiness(int width, int height)
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ProtocolException>(() =>
            CaptureWorkerHandshake.WriteReadyAsync(stream, width, height, "h264", default));
        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData("h265")]
    [InlineData("av1")]
    public async Task ChangedCodecIsRejectedBeforeStart(string codec)
    {
        using var stream = new MemoryStream();
        await CaptureWorkerHandshake.WriteReadyAsync(stream, 640, 360, codec, default);
        stream.Position = 0;
        await Assert.ThrowsAsync<ProtocolException>(() =>
            CaptureWorkerHandshake.ReadReadyAsync(stream, 640, 360, "h264", default));
    }

    [Theory]
    [InlineData("RCW1")]
    [InlineData("RCF1")]
    public async Task OldHelperIsRejectedFromItsPrefixWithoutWaitingForMoreBytes(string prefix)
    {
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(prefix));
        await Assert.ThrowsAsync<ProtocolException>(() =>
            CaptureWorkerHandshake.ReadReadyAsync(stream, 640, 360, "h264", default));
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task UnknownCodecProducesNoReadyHeader()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ProtocolException>(() =>
            CaptureWorkerHandshake.WriteReadyAsync(stream, 640, 360, "unknown", default));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task MissingReadyTimesOutWhileTheWorkerPipeRemainsOpen()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var stream = await listener.AcceptTcpClientAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CaptureWorkerHandshake.ReadReadyAsync(stream.GetStream(), 640, 360, "h264", deadline.Token));
        Assert.True(peer.Connected);
    }
}
