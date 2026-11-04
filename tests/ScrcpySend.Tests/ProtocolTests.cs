using System.Net;
using System.Text;
using System.Text.Json;
using ScrcpySend.Core;
using Xunit;

namespace ScrcpySend.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task SharedAndroidFramingFixturesRoundTripExactly()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "v3.json")));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var bytes = Convert.FromHexString(item.GetProperty("hex").GetString()!);
            using var source = new MemoryStream(bytes);
            if (!item.GetProperty("valid").GetBoolean())
            {
                await Assert.ThrowsAnyAsync<IOException>(async () => await Wire.ReadAsync(source, default));
                continue;
            }
            var message = await Wire.ReadAsync(source, default);
            using var target = new MemoryStream();
            await Wire.WriteAsync(target, message, default);
            Assert.Equal(bytes, target.ToArray());
        }
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":1.5}")]
    [InlineData("{\"a\":2147483648}")]
    [InlineData("[]")]
    [InlineData("{\"a\":NaN}")]
    [InlineData("{\"a\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"a\":\"\\uD800\"}")]
    public void StrictJsonRejectsInvalidObjects(string text) =>
        Assert.Throws<ProtocolException>(() => StrictJson.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void StrictJsonRejectsMalformedUtf8() =>
        Assert.Throws<ProtocolException>(() => StrictJson.Parse([123, 34, 97, 34, 58, 34, 255, 34, 125]));

    [Fact]
    public void DiscoveryUsesPacketSourceNotAdvertisedHostOrName()
    {
        var table = new DiscoveryTable();
        var now = DateTimeOffset.UtcNow;
        var packet = Encoding.UTF8.GetBytes(
            """{"protocol":"screencopy","version":3,"name":"TV","port":27184,"state":"available","host":"1.2.3.4"}""");
        Assert.True(table.Observe(packet, IPAddress.Parse("192.168.1.10"), now));
        Assert.True(table.Observe(packet, IPAddress.Parse("192.168.1.11"), now));
        Assert.Equal(2, table.Snapshot(now).Count);
        Assert.Equal("192.168.1.10", table.Snapshot(now)[0].Address.ToString());
        Assert.Empty(table.Snapshot(now.AddSeconds(6)));
    }

    [Fact]
    public void DiscoveryCapsEntriesAndDoesNotAcceptAnotherWireVersion()
    {
        var table = new DiscoveryTable();
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 65; i++)
            table.Observe(Encoding.UTF8.GetBytes(
                """{"protocol":"screencopy","version":3,"name":"TV","port":27184,"state":"busy"}"""),
                IPAddress.Parse($"192.168.1.{i}"), now);
        Assert.Equal(64, table.Snapshot(now).Count);
        Assert.All(table.Snapshot(now), item => Assert.True(item.Busy));
        Assert.False(table.Observe(Encoding.UTF8.GetBytes(
            """{"protocol":"screencopy","version":2,"name":"TV","port":27184,"state":"available"}"""),
            IPAddress.Loopback, now));
    }

    [Fact]
    public void AudioCannotSilentlyBecomeVideoOnly()
    {
        var enabled = JsonSerializer.SerializeToElement(new CaptureSettings(EnableAudio: true).Request("Hardware"));
        Assert.True(enabled.GetProperty("audio").GetProperty("enabled").GetBoolean());
        Assert.Equal("raw", enabled.GetProperty("audio").GetProperty("codec").GetString());
        var request = JsonSerializer.SerializeToElement(new CaptureSettings().Request("Actual hardware encoder"));
        Assert.False(request.GetProperty("audio").GetProperty("enabled").GetBoolean());
        Assert.Equal("Actual hardware encoder", request.GetProperty("video").GetProperty("encoder").GetString());
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(1080, 1920, 1080, 1920)]
    [InlineData(1365, 767, 1364, 766)]
    public void OutputSizePreservesAspectAndEncoderAlignment(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), new CaptureSettings().OutputSize(width, height));
    }

    [Fact]
    public void QueueFailsInsteadOfDroppingOrGrowing()
    {
        var queue = new VideoQueue();
        for (var i = 0; i < 64; i++) queue.Enqueue(new([1], i * 1000, []));
        Assert.Throws<IOException>(() => queue.Enqueue(new([1], 64000, [])));
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(0, first!.PtsUs);
        queue.Enqueue(new([1], 64000, []));
        var span = new VideoQueue();
        span.Enqueue(new([1], 1000, []));
        Assert.Throws<IOException>(() => span.Enqueue(new([1], 501001, [])));
        var bytes = new VideoQueue();
        Assert.Throws<IOException>(() => bytes.Enqueue(new(new byte[8 * 1024 * 1024 + 1], 0, [])));
    }

    [Fact]
    public void AnnexBRequiresRealParameterSetsAndIdr()
    {
        byte[] accessUnit = [0, 0, 0, 1, 0x67, 1, 0, 0, 1, 0x68, 2, 0, 0, 0, 1, 0x65, 3];
        Assert.True(AnnexB.IsIdr(accessUnit));
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 1, 0, 0, 0, 1, 0x68, 2 }, AnnexB.ParameterSets(accessUnit));
        Assert.Throws<ProtocolException>(() => AnnexB.ParameterSets([0, 0, 0, 1, 0x65, 1]));
        Assert.Throws<ProtocolException>(() => AnnexB.Units([0, 0, 0, 2, 0x65, 1]));
    }

    [Theory]
    [InlineData("TV", 27184)]
    [InlineData("192.168.1.2", 0)]
    [InlineData("192.168.1.2", 65536)]
    public void InvalidManualEndpointsAreReported(string host, int port) =>
        Assert.Throws<ArgumentException>(() => ReceiverEndpoint.Manual(host, port));

    [Theory]
    [InlineData("""{"nonce":"1"}""")]
    [InlineData("""{"nonce":null}""")]
    [InlineData("""{"nonce":true}""")]
    public void IntegerFieldsDoNotCoerceOtherJsonTypes(string json) =>
        Assert.Throws<ProtocolException>(() => StrictJson.Integer(StrictJson.Parse(Encoding.UTF8.GetBytes(json)), "nonce"));

    [Theory]
    [InlineData(0xb8, false)]
    [InlineData(0xe0, false)]
    [InlineData(0xa0, true)]
    [InlineData(0x90, true)]
    public void ActualSliceTypesRejectBAndSpWithoutDependingOnOptionalDriverProperties(byte slice, bool invalid)
    {
        byte[] frame = [0, 0, 0, 1, 0x61, slice];
        if (invalid) Assert.Throws<ProtocolException>(() => H264.ValidateAccessUnit(frame));
        else H264.ValidateAccessUnit(frame);
    }

    [Fact]
    public void BaselineSpsDimensionsAreParsedRatherThanAssumed()
    {
        var bits = new List<int>();
        void Write(uint value, int count) { for (var i = count - 1; i >= 0; i--) bits.Add((int)(value >> i & 1)); }
        void Unsigned(uint value)
        {
            var code = value + 1;
            var length = 32 - System.Numerics.BitOperations.LeadingZeroCount(code);
            Write(0, length - 1); Write(code, length);
        }
        Write(66, 8); Write(0, 8); Write(30, 8);
        Unsigned(0); Unsigned(0); Unsigned(0); Unsigned(0);
        Unsigned(1); Write(0, 1); Unsigned(39); Unsigned(22);
        Write(1, 1); Write(1, 1); Write(1, 1);
        Unsigned(0); Unsigned(0); Unsigned(0); Unsigned(4);
        Write(1, 1);
        while (bits.Count % 8 != 0) bits.Add(0);
        var sps = new byte[1 + bits.Count / 8];
        sps[0] = 0x67;
        for (var i = 0; i < bits.Count; i++) sps[1 + i / 8] |= (byte)(bits[i] << (7 - i % 8));
        var csd = AnnexB.Join([sps, [0x68, 0x80]]);
        H264.ValidateConfiguration(csd, 640, 360);
        Assert.Throws<ProtocolException>(() => H264.ValidateConfiguration(csd, 640, 368));
        sps[1] = 100;
        Assert.Throws<ProtocolException>(() => H264.ValidateConfiguration(AnnexB.Join([sps, [0x68, 0x80]]), 640, 360));
    }
}
