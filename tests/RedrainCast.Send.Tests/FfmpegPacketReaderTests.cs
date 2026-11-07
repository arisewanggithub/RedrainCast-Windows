using System.Buffers.Binary;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class FfmpegPacketReaderTests
{
    private static MemoryStream Header(uint width = 640, uint height = 360, uint configurationLength = 0)
    {
        var stream = new MemoryStream();
        stream.Write("RCF1"u8);
        Write32(stream, width);
        Write32(stream, height);
        Write32(stream, configurationLength);
        return stream;
    }

    private static void Write32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void Packet(Stream stream, uint length, long pts, uint flags = 0, byte[]? data = null)
    {
        Write32(stream, length);
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, pts);
        stream.Write(bytes);
        Write32(stream, flags);
        if (data is not null) stream.Write(data);
    }

    [Fact]
    public async Task PreservesBoundariesConfigurationAndMicrosecondSourceTimestamps()
    {
        using var stream = Header(configurationLength: 3);
        stream.Write([1, 2, 3]);
        Packet(stream, 4, 1234567890123, 1, [4, 5, 6, 7]);
        Packet(stream, 2, 1234567890124, data: [8, 9]);
        stream.Position = 0;
        var reader = new FfmpegPacketReader(stream, 640, 360);
        await reader.InitializeAsync(default);
        var first = await reader.ReadAsync(default);
        var second = await reader.ReadAsync(default);
        Assert.Equal(1234567890123, first.PtsUs);
        Assert.Equal(1234567890124, second.PtsUs);
        Assert.Equal(new byte[] { 1, 2, 3 }, first.Configuration);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, first.Data);
        Assert.Equal(new byte[] { 8, 9 }, second.Data);
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync(default));
    }

    [Theory]
    [InlineData(0u, 1L, 0u)]
    [InlineData(4194305u, 1L, 0u)]
    [InlineData(1u, -1L, 0u)]
    [InlineData(1u, 1L, 2u)]
    public async Task RejectsInvalidPacketHeadersBeforeAllocatingPayload(uint length, long pts, uint flags)
    {
        using var stream = Header();
        Packet(stream, length, pts, flags);
        stream.Position = 0;
        var reader = new FfmpegPacketReader(stream, 640, 360);
        await reader.InitializeAsync(default);
        await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadAsync(default));
    }

    [Theory]
    [InlineData(641u, 360u, 0u)]
    [InlineData(640u, 361u, 0u)]
    [InlineData(640u, 360u, 65537u)]
    public async Task RejectsDimensionChangesAndOversizedConfiguration(uint width, uint height, uint configurationLength)
    {
        using var stream = Header(width, height, configurationLength);
        stream.Position = 0;
        await Assert.ThrowsAsync<ProtocolException>(() => new FfmpegPacketReader(stream, 640, 360).InitializeAsync(default));
    }

    [Fact]
    public async Task RejectsBackwardsTimestampsAndTruncatedPackets()
    {
        using var stream = Header();
        Packet(stream, 1, 10, data: [1]);
        Packet(stream, 1, 9, data: [1]);
        stream.Position = 0;
        var reader = new FfmpegPacketReader(stream, 640, 360);
        await reader.InitializeAsync(default);
        _ = await reader.ReadAsync(default);
        await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadAsync(default));
        using var truncated = Header();
        Packet(truncated, 4, 10, data: [1]);
        truncated.Position = 0;
        var incomplete = new FfmpegPacketReader(truncated, 640, 360);
        await incomplete.InitializeAsync(default);
        await Assert.ThrowsAsync<EndOfStreamException>(() => incomplete.ReadAsync(default));
    }

    [Fact]
    public async Task CancellationAndInvalidMagicRemainExplicit()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var stream = Header();
        stream.Position = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FfmpegPacketReader(stream, 640, 360).InitializeAsync(canceled.Token));
        stream.Position = 0;
        stream.Write("BAD1"u8);
        stream.Position = 0;
        await Assert.ThrowsAsync<ProtocolException>(() =>
            new FfmpegPacketReader(stream, 640, 360).InitializeAsync(default));
    }
}
