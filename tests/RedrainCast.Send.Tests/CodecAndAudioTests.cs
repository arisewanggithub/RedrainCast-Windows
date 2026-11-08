using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using RedrainCast.Send.Core;
using Xunit;

namespace RedrainCast.Send.Tests;

public sealed class CodecAndAudioTests
{
    private sealed class Bits
    {
        private readonly List<int> bits = [];
        public void Add(uint value, int count) { for (var index = count - 1; index >= 0; index--) bits.Add((int)(value >> index & 1)); }
        public void Unsigned(uint value)
        {
            var length = 32 - System.Numerics.BitOperations.LeadingZeroCount(value + 1);
            Add(0, length - 1); Add(value + 1, length);
        }
        public byte[] Bytes()
        {
            var data = new byte[(bits.Count + 7) / 8];
            for (var index = 0; index < bits.Count; index++) data[index / 8] |= (byte)(bits[index] << (7 - index % 8));
            return data;
        }
    }

    private static byte[] HevcNal(int type, byte[] payload)
    {
        var result = new List<byte> { (byte)(type << 1), 1 };
        var zeros = 0;
        foreach (var value in payload)
        {
            if (zeros >= 2 && value <= 3) { result.Add(3); zeros = 0; }
            result.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return result.ToArray();
    }

    private static byte[] HevcConfiguration()
    {
        var sps = new Bits();
        sps.Add(0, 4); sps.Add(0, 3); sps.Add(1, 1);
        sps.Add(0, 3); sps.Add(1, 5);
        sps.Add(0, 32); sps.Add(0, 32); sps.Add(0, 16); sps.Add(120, 8);
        sps.Unsigned(0); sps.Unsigned(1); sps.Unsigned(640); sps.Unsigned(360);
        sps.Add(0, 1); sps.Unsigned(0); sps.Unsigned(0);
        var pps = new Bits();
        pps.Unsigned(0); pps.Unsigned(0); pps.Add(0, 2); pps.Add(0, 3);
        return AnnexB.Join([HevcNal(32, [0x80]), HevcNal(33, sps.Bytes()), HevcNal(34, pps.Bytes())]);
    }

    private static byte[] HevcPicture(int type, uint sliceType)
    {
        var header = new Bits();
        header.Add(1, 1);
        if (type is >= 16 and <= 23) header.Add(0, 1);
        header.Unsigned(0); header.Unsigned(sliceType);
        return AnnexB.Join([HevcNal(type, header.Bytes())]);
    }

    private static byte[] Av1Configuration(bool reduced)
    {
        var bits = new Bits();
        bits.Add(0, 3); bits.Add(reduced ? 1u : 0u, 1); bits.Add(reduced ? 1u : 0u, 1);
        if (reduced) bits.Add(0, 5);
        else { bits.Add(0, 1); bits.Add(0, 1); bits.Add(0, 5); bits.Add(0, 12); bits.Add(0, 5); }
        bits.Add(3, 4); bits.Add(3, 4); bits.Add(15, 4); bits.Add(15, 4);
        var payload = bits.Bytes();
        return [0x0a, (byte)payload.Length, .. payload];
    }

    [Theory]
    [InlineData("h264", false)]
    [InlineData("h265", false)]
    [InlineData("av1", false)]
    [InlineData("h264", true)]
    [InlineData("h265", true)]
    [InlineData("av1", true)]
    public void SettingsPersistAndRequestTheExactCodecAndAudioChoice(string codec, bool audio)
    {
        var settings = CaptureSettings.ParseInput("1920", "8", 60, "auto", CultureInfo.InvariantCulture, codec, audio).Settings!;
        Assert.Equal(settings, JsonSerializer.Deserialize<CaptureSettings>(JsonSerializer.Serialize(settings)));
        var request = JsonSerializer.SerializeToElement(settings.Request("Hardware"));
        Assert.Equal(codec, request.GetProperty("video").GetProperty("codec").GetString());
        Assert.Equal(audio, request.GetProperty("audio").GetProperty("enabled").GetBoolean());
        Assert.Equal("raw", request.GetProperty("audio").GetProperty("codec").GetString());
        Assert.Throws<ArgumentException>(() => new CaptureSettings(VideoCodec: "vp9").Validate());
    }

    [Fact]
    public void HevcUsesRealParameterSetsAndRejectsBFramesOrDimensionSubstitution()
    {
        var configuration = HevcConfiguration();
        Hevc.ValidateConfiguration(configuration, 640, 360);
        Assert.Throws<ProtocolException>(() => Hevc.ValidateConfiguration(configuration, 1280, 720));
        Assert.True(Hevc.IsKeyFrame(HevcPicture(19, 2)));
        Hevc.ValidateAccessUnit(HevcPicture(19, 2), configuration);
        Hevc.ValidateAccessUnit(HevcPicture(1, 1), configuration);
        Assert.Throws<ProtocolException>(() => Hevc.ValidateAccessUnit(HevcPicture(1, 0), configuration));
        Assert.Throws<ProtocolException>(() => Hevc.ParameterSets(AnnexB.Join([HevcNal(33, [0x80])])));
    }

    [Fact]
    public void HvccIsConvertedToAnnexBWithoutInventingParameters()
    {
        var expected = HevcConfiguration();
        var units = AnnexB.Units(expected);
        using var stream = new MemoryStream();
        var header = new byte[23]; header[0] = 1; header[22] = (byte)units.Count;
        stream.Write(header);
        foreach (var unit in units)
        {
            stream.WriteByte((byte)(0x80 | (unit[0] >> 1 & 63)));
            stream.Write([0, 1]);
            var length = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)unit.Length);
            stream.Write(length); stream.Write(unit);
        }
        Assert.Equal(expected, Hevc.ParameterSets(stream.ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    public void HevcAdmitsOnlyRecoverableTrailTsaStsaOrIrapPictures(int type)
    {
        var picture = HevcPicture(type, type >= 16 ? 2u : 1u);
        Hevc.ValidateAccessUnit(picture, HevcConfiguration());
        Assert.Equal(type >= 16, Hevc.IsKeyFrame(picture));
        VideoBitstream.ValidateFrame("h265", picture, HevcConfiguration());
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    public void HevcRejectsLeadingAndReservedPicturesBeforeRecoveryAdmission(int type)
    {
        var picture = HevcPicture(type, 2);
        Assert.False(Hevc.IsKeyFrame(picture));
        Assert.Throws<ProtocolException>(() => Hevc.ValidateAccessUnit(picture, HevcConfiguration()));
        Assert.Throws<ProtocolException>(() => VideoBitstream.ValidateFrame("h265", picture, HevcConfiguration()));
    }

    [Fact]
    public void Av1DistinguishesSequenceLimitsFromActualFrameSizeAndValidatesDisplayedKeyframes()
    {
        var configuration = Av1Configuration(false);
        Av1.ValidateConfiguration(configuration, 16, 16);
        Av1.ValidateConfiguration(configuration, 8, 8);
        Assert.Throws<ProtocolException>(() => Av1.ValidateConfiguration(configuration, 32, 16));
        byte[] key = [0x32, 1, 0x10];
        byte[] inter = [0x32, 1, 0x30];
        Assert.True(Av1.IsKeyFrame(key, configuration));
        Assert.False(Av1.IsKeyFrame(inter, configuration));
        Av1.ValidateAccessUnit(key, configuration);
        Av1.ValidateAccessUnit(inter, configuration);
        Assert.Throws<ProtocolException>(() => Av1.ValidateAccessUnit([0x32, 1, 0], configuration));
        Assert.Throws<ProtocolException>(() => Av1.ValidateAccessUnit([0x32, 1, 0x80], configuration));
        Assert.Equal(configuration, Av1.SequenceHeader([0x81, 0, 0, 0, .. configuration]));
        var reduced = Av1Configuration(true);
        Av1.ValidateConfiguration(reduced, 16, 16);
        Assert.True(Av1.IsKeyFrame([0x32, 1, 0], reduced));
    }

    [Theory]
    [InlineData("0A")]
    [InlineData("0A8000")]
    [InlineData("0A05FF")]
    [InlineData("0801FF")]
    public void MalformedAv1ConfigurationFailsExplicitly(string hex) =>
        Assert.Throws<ProtocolException>(() => Av1.SequenceHeader(Convert.FromHexString(hex)));

    [Fact]
    public void AudioQueuePreservesFifoAndFailsInsteadOfDroppingOrGrowing()
    {
        var queue = new AudioQueue();
        queue.Enqueue(new([1, 2, 3, 4], 1000));
        Assert.Throws<ProtocolException>(() => queue.Enqueue(new([1, 2, 3], 2000)));
        Assert.Throws<ProtocolException>(() => queue.Enqueue(new([0, 0, 0, 0], 999)));
        Assert.Throws<IOException>(() => queue.Enqueue(new([0, 0, 0, 0], 501001)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, queue.Dequeue().Data);
    }

    [Fact]
    public void MuteRestorationOnlyChangesStateStillOwnedByTheApp()
    {
        var own = new MuteOwnership(false);
        Assert.False(own.ShouldRestore(true));
        own.MarkAppMute();
        own.Observe(new(true, 1, [1]), own.Context);
        Assert.True(own.ShouldRestore(true));
        Assert.False(own.ShouldRestore(false));
        Assert.True(own.Observe(new(false, 1, [1]), Guid.NewGuid()));
        Assert.False(own.ShouldRestore(false));
        own.MarkAppMute();
        Assert.True(own.ShouldRestore(true));
        var alreadyMuted = new MuteOwnership(true);
        alreadyMuted.MarkAppMute();
        Assert.False(alreadyMuted.ShouldRestore(true));
    }
}
