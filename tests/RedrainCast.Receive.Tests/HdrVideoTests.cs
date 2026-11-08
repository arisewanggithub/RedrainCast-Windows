using System.Text.Json;
using System.Text.Json.Nodes;
using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using Xunit;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using WireJson = RedrainCast.Receive.Core.JsonObject;

namespace RedrainCast.Receive.Tests;

public sealed class HdrVideoTests
{
    private static JsonElement Parse(JsonNode value) => WireJson.Parse(JsonData.Bytes(value));
    private static readonly HdrVideoFormat Hdr = new(true, new(1000, 50, 1000, 400));

    [Theory]
    [InlineData(7087, 7087, 50000, true)]
    [InlineData(7087, 7086, 50000, true)]
    [InlineData(7087, 7088, 50000, true)]
    [InlineData(7087, 3543, 25000, true)]
    [InlineData(7087, 7085, 50000, false)]
    [InlineData(7087, 7089, 50000, false)]
    [InlineData(7087, 708599, 5000000, false)]
    [InlineData(7087, 708801, 5000000, false)]
    [InlineData(0, 0, 50000, true)]
    [InlineData(50000, 50000, 50000, true)]
    [InlineData(25000, 1000000000, 2000000000, true)]
    [InlineData(7087, 7087, 0, false)]
    [InlineData(7087, -7087, -50000, false)]
    [InlineData(0, -1, 50000, false)]
    [InlineData(50000, 50001, 50000, false)]
    [InlineData(-1, 0, 50000, false)]
    [InlineData(50001, 50000, 50000, false)]
    public void EncodedChromaticitiesAllowOnlyOneQuantumOfHardwareFloatRequantization(
        int expected, int numerator, int denominator, bool matches)
    {
        Assert.Equal(matches, HdrStaticMetadata.MatchesEncodedChromaticity(expected, numerator, denominator));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void CapabilitiesRequireAnActualOutputPath(bool native, bool conversion, bool usable)
    {
        var capabilities = new HdrReceiverCapabilities(true, native, conversion);
        var root = JsonData.Object(("receiveCapabilities", capabilities.WireValue));
        Assert.Equal(capabilities, HdrVideoProtocol.Capabilities(Parse(root)));
        Assert.Equal(usable, capabilities.CanReceiveHdr10);
        Assert.False(new HdrReceiverCapabilities(true).CanReceiveHdr10);
        Assert.True(new HdrReceiverCapabilities(true, null, true).CanReceiveHdr10);
    }

    [Fact]
    public void MissingAndFutureCapabilitiesDoNotEnableHdr()
    {
        Assert.Equal(HdrReceiverCapabilities.None, HdrVideoProtocol.Capabilities(Parse(new JsonObject())));
        var value = new HdrReceiverCapabilities(true, true, true).WireValue;
        value["version"] = 2;
        Assert.Equal(HdrReceiverCapabilities.None,
            HdrVideoProtocol.Capabilities(Parse(JsonData.Object(("receiveCapabilities", value)))));
    }

    [Theory]
    [InlineData("version", "\"1\"")]
    [InlineData("version", "0")]
    [InlineData("videoModes", "{}")]
    [InlineData("nativeHdrAvailable", "\"true\"")]
    [InlineData("hdrToSdr", "1")]
    public void MalformedCapabilitiesFailExplicitly(string member, string json)
    {
        var value = new HdrReceiverCapabilities(true, true, true).WireValue;
        value[member] = JsonNode.Parse(json);
        Assert.Throws<FormatException>(() =>
            HdrVideoProtocol.Capabilities(Parse(JsonData.Object(("receiveCapabilities", value)))));
    }

    [Fact]
    public void DuplicateAndOversizedExtensionsFail()
    {
        using var duplicate = JsonDocument.Parse(
            """{"receiveCapabilities":{"version":1,"version":1,"videoModes":[],"hdrToSdr":false}}""");
        Assert.Throws<FormatException>(() => HdrVideoProtocol.Capabilities(duplicate.RootElement));
        var value = new HdrReceiverCapabilities(true, true, true).WireValue;
        value["future"] = new string('x', HdrVideoProtocol.MaximumBytes);
        Assert.Throws<FormatException>(() =>
            HdrVideoProtocol.Capabilities(Parse(JsonData.Object(("receiveCapabilities", value)))));
    }

    [Fact]
    public void ExactFormatAndMetadataMustBeAcknowledged()
    {
        var root = new JsonObject();
        HdrVideoProtocol.AddFormat(root, Hdr);
        Assert.Equal(Hdr, HdrVideoProtocol.Format(Parse(root)));
        HdrVideoProtocol.RequireAcknowledgement(Parse(root), Hdr);
        Assert.Throws<FormatException>(() => HdrVideoProtocol.RequireAcknowledgement(Parse(new JsonObject()), Hdr));
        Assert.Throws<FormatException>(() =>
            HdrVideoProtocol.RequireAcknowledgement(Parse(root), new(true, new(400))));
        Assert.Throws<FormatException>(() => HdrVideoProtocol.RequireAcknowledgement(Parse(root), HdrVideoFormat.Sdr));
        HdrVideoProtocol.RequireAcknowledgement(Parse(new JsonObject()), HdrVideoFormat.Sdr);
    }

    [Theory]
    [InlineData("bitDepth", "8")]
    [InlineData("codec", "\"h264\"")]
    [InlineData("profile", "\"main\"")]
    [InlineData("transfer", "\"hlg\"")]
    [InlineData("range", "\"full\"")]
    public void OnlyTheNegotiatedHdr10TupleIsSupported(string member, string json)
    {
        var value = Hdr.WireValue;
        value[member] = JsonNode.Parse(json);
        Assert.Throws<FormatException>(() =>
            HdrVideoProtocol.Format(Parse(JsonData.Object(("videoColor", value)))));
    }

    [Fact]
    public void MetadataUnitsAndBoundsAreValidated()
    {
        Assert.Equal(50, HdrVideoProtocol.Format(Parse(JsonData.Object(("videoColor", Hdr.WireValue))))
            .Metadata!.MinMasteringTenThousandths);
        Assert.Throws<FormatException>(() => new HdrStaticMetadata(0).Validate());
        Assert.Throws<FormatException>(() => new HdrStaticMetadata(1000, 10_000_001).Validate());
        Assert.Throws<FormatException>(() => new HdrStaticMetadata(1000, MaxContentLightNits: 400,
            MaxFrameAverageLightNits: 1000).Validate());
        Assert.Throws<FormatException>(() => new HdrStaticMetadata(1000, RedX: 50001).Validate());
        Assert.Throws<FormatException>(() => new HdrVideoFormat(false, new(1000)).Validate("h265"));
    }

    [Fact]
    public void MinimumMasteringLuminanceFitsTheSharedCta861DescriptorWithoutTruncation()
    {
        var color = new HdrVideoFormat(true, new(1000, 65535));
        Assert.Equal(color, HdrVideoProtocol.Format(Parse(JsonData.Object(("videoColor", color.WireValue)))));
        Assert.Throws<FormatException>(() => new HdrStaticMetadata(1000, 65536).Validate());
        Assert.Throws<FormatException>(() => new HdrStaticMetadata(1, 10001).Validate());
        var invalid = color.WireValue;
        invalid["staticMetadata"]!["minMasteringTenThousandths"] = 65536;
        Assert.Throws<FormatException>(() =>
            HdrVideoProtocol.Format(Parse(JsonData.Object(("videoColor", invalid)))));
    }

    private static JsonObject Request(HdrVideoFormat? color = null)
    {
        var root = JsonNode.Parse(JsonSerializer.Serialize(RequestTests.Settings(false)))!.AsObject();
        root["video"]!["codec"] = "h265";
        if (color is not null) HdrVideoProtocol.AddFormat(root, color);
        return root;
    }

    [Fact]
    public void RequestRequiresNegotiatedUsableHdrCapability()
    {
        var root = Parse(Request(Hdr));
        Assert.Equal("UNSUPPORTED_CONFIG", Assert.Throws<ProtocolException>(() => CastRequest.Parse(root)).Code);
        Assert.Throws<ProtocolException>(() => CastRequest.Parse(root, hdrCapabilities: new(true, false, false)));
        var request = CastRequest.Parse(root, hdrCapabilities: new(true, true, false));
        Assert.Equal(Hdr, request.ColorFormat);
        Assert.Equal(60, request.VideoProcessingFramesPerSecond);
        Assert.Equal(HdrVideoFormat.Sdr, CastRequest.Parse(Parse(Request())).ColorFormat);
        var wrongCodec = Request(Hdr);
        wrongCodec["video"]!["codec"] = "h264";
        Assert.Throws<ProtocolException>(() =>
            CastRequest.Parse(Parse(wrongCodec), hdrCapabilities: new(true, true, true)));
    }

    [Fact]
    public void StreamConfigurationCannotChangeNegotiatedColor()
    {
        var request = CastRequest.Parse(Parse(Request(Hdr)), hdrCapabilities: new(true, true, true));
        var value = JsonData.Object(("codec", "h265"), ("width", 16), ("height", 16),
            ("csd", JsonData.Value(new[] { Convert.ToBase64String(Hevc()) })));
        var packet = new Packet(PacketType.StreamConfig, 1, 1, 1, -1, 0, JsonData.Bytes(value));
        Assert.Throws<ProtocolException>(() => new TrackState(request).Configure(packet));
        HdrVideoProtocol.AddFormat(value, Hdr);
        var state = new TrackState(request);
        state.Configure(packet with { Payload = JsonData.Bytes(value) });
        value["videoColor"] = new HdrVideoFormat(true, new(400)).WireValue;
        Assert.Throws<ProtocolException>(() =>
            state.Configure(packet with { Generation = 2, Payload = JsonData.Bytes(value) }));
    }

    [Fact]
    public void DiscoveryRemainsBoundedAndAdditive()
    {
        var capabilities = new HdrReceiverCapabilities(true, true, true);
        var bytes = Discovery.Advertisement(new string('n', 80), 12345, false,
            Guid.NewGuid().ToString("N"), capabilities);
        Assert.True(bytes.Length <= 1024);
        Assert.Equal(capabilities, HdrVideoProtocol.Capabilities(WireJson.Parse(bytes)));
        Assert.Equal("screencopy", WireJson.Parse(bytes).GetProperty("protocol").GetString());
    }

    [Theory]
    [InlineData(9u, 16u, 9u, false, true)]
    [InlineData(1u, 16u, 9u, false, false)]
    [InlineData(9u, 1u, 9u, false, false)]
    [InlineData(9u, 16u, 1u, false, false)]
    [InlineData(9u, 16u, 9u, true, false)]
    public void Main10RequiresExactVui(uint primaries, uint transfer, uint matrix, bool full, bool supported)
    {
        var csd = Hevc(primaries, transfer, matrix, full);
        Assert.Throws<ProtocolException>(() => HevcStream.ValidateConfiguration(csd, 16, 16));
        if (supported)
        {
            HevcStream.ValidateConfiguration(csd, 16, 16, Hdr);
            Assert.Equal((16, 16), HevcStream.Dimensions(csd, Hdr));
        }
        else Assert.Throws<ProtocolException>(() => HevcStream.ValidateConfiguration(csd, 16, 16, Hdr));
    }

    [Fact]
    public void TruncatedHdrVuiDoesNotPassProfileOnlyValidation()
    {
        var data = Hevc();
        var units = H264Stream.Units(data);
        var sps = units.Single(unit => (unit.Span[0] >> 1 & 63) == 33);
        var prefix = new byte[] { 0, 0, 0, 1 };
        var truncated = units.SelectMany(unit => prefix.Concat(
            (unit.Span[0] >> 1 & 63) == 33 ? sps[..^4].ToArray() : unit.ToArray())).ToArray();
        Assert.Throws<ProtocolException>(() => HevcStream.ValidateConfiguration(truncated, 16, 16, Hdr));
    }

    private sealed class Bits
    {
        private readonly List<int> values = [];
        public Bits Put(uint value, int count)
        {
            for (var bit = count - 1; bit >= 0; bit--) values.Add((int)(value >> bit & 1));
            return this;
        }
        public Bits Ue(uint value)
        {
            var code = value + 1;
            var size = 32 - System.Numerics.BitOperations.LeadingZeroCount(code);
            return Put(0, size - 1).Put(code, size);
        }
        public byte[] Finish()
        {
            Put(1, 1);
            while (values.Count % 8 != 0) values.Add(0);
            return Enumerable.Range(0, values.Count / 8).Select(index =>
                (byte)values.Skip(index * 8).Take(8).Aggregate(0, (result, bit) => result * 2 + bit)).ToArray();
        }
    }

    private static byte[] Nal(int type, byte[] payload)
    {
        var result = new List<byte> { 0, 0, 0, 1, (byte)(type << 1), 1 };
        var zeros = 0;
        foreach (var value in payload)
        {
            if (zeros >= 2 && value <= 3) { result.Add(3); zeros = 0; }
            result.Add(value); zeros = value == 0 ? zeros + 1 : 0;
        }
        return result.ToArray();
    }

    private static byte[] Hevc(uint primaries = 9, uint transfer = 16, uint matrix = 9, bool full = false)
    {
        var vps = Nal(32, new Bits().Put(0, 4).Finish());
        var bits = new Bits().Put(0, 4).Put(0, 3).Put(1, 1).Put(0, 3).Put(2, 5)
            .Put(0, 32).Put(0, 32).Put(0, 16).Put(120, 8)
            .Ue(0).Ue(1).Ue(16).Ue(16).Put(0, 1).Ue(2).Ue(2)
            .Ue(4).Put(0, 1).Ue(3).Ue(0).Ue(0);
        for (var index = 0; index < 6; index++) bits.Ue(0);
        bits.Put(0, 1).Put(0, 2).Put(0, 1).Ue(0).Put(0, 1).Put(0, 2)
            .Put(1, 1).Put(0, 1).Put(0, 1).Put(1, 1).Put(5, 3)
            .Put(full ? 1u : 0u, 1).Put(1, 1).Put(primaries, 8).Put(transfer, 8).Put(matrix, 8);
        var pps = Nal(34, new Bits().Ue(0).Ue(0).Put(0, 5).Finish());
        return vps.Concat(Nal(33, bits.Finish())).Concat(pps).ToArray();
    }
}
