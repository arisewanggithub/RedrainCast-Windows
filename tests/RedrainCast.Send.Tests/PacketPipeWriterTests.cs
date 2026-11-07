using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class PacketPipeWriterTests
{
    [Theory]
    [InlineData(640, 360)]
    [InlineData(2496, 1664)]
    public async Task RoundTripPreservesDimensionsConfigurationPacketsAndAbsoluteTimestamps(int width, int height)
    {
        using var stream = new MemoryStream();
        var writer = new PacketPipeWriter(stream, width, height);
        byte[] configuration = [1, 2, 3];
        foreach (var pts in new[] { 123456789012L, 123456789012L, 123456790000L })
            await writer.WriteAsync(new([4, 5, 6], pts, configuration), default);
        stream.Position = 0;
        var reader = new FfmpegPacketReader(stream, width, height);
        await reader.InitializeAsync(default);
        foreach (var pts in new[] { 123456789012L, 123456789012L, 123456790000L })
        {
            var frame = await reader.ReadAsync(default);
            Assert.Equal(pts, frame.PtsUs);
            Assert.Equal(configuration, frame.Configuration);
            Assert.Equal(new byte[] { 4, 5, 6 }, frame.Data);
        }
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(639, 360)]
    [InlineData(640, 0)]
    [InlineData(3842, 2160)]
    public async Task InvalidDimensionsProduceNoOutput(int width, int height)
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ProtocolException>(() =>
            new PacketPipeWriter(stream, width, height).WriteAsync(new([1], 0, [2]), default));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task RejectsMissingConfigurationOversizedPayloadAndReversedTime()
    {
        using var stream = new MemoryStream();
        var writer = new PacketPipeWriter(stream, 640, 360);
        await Assert.ThrowsAsync<ProtocolException>(() => writer.WriteAsync(new([1], 0, []), default));
        await Assert.ThrowsAsync<ProtocolException>(() => writer.WriteAsync(new(new byte[4 * 1024 * 1024 + 1], 0, [2]), default));
        await writer.WriteAsync(new([1], 10, [2]), default);
        var before = stream.Length;
        await Assert.ThrowsAsync<ProtocolException>(() => writer.WriteAsync(new([1], 9, [2]), default));
        await Assert.ThrowsAsync<ProtocolException>(() => writer.WriteAsync(new([1], 11, [3]), default));
        Assert.Equal(before, stream.Length);
    }
}
