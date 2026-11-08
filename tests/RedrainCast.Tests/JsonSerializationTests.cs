using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedrainCast.Protocol;
using RedrainCast.Send.Core;
using Xunit;
using Receiver = RedrainCast.Receive.Core;

namespace RedrainCast.Tests;

public sealed class JsonSerializationTests
{
    private static string SettingsJson(CaptureSettings settings) =>
        JsonSerializer.Serialize(settings, CaptureSettingsJsonContext.Default.CaptureSettings);

    [Fact]
    public void DefaultSettingsRetainExactNamesOrderAndDefaults()
    {
        const string expected = """{"MaxSize":1920,"MaxFps":0,"VideoBitrate":8000000,"EnableAudio":false,"ReceiverOrientation":"auto","VideoCodec":"h264","ShowPlaybackStatistics":false}""";
        Assert.Equal(expected, SettingsJson(new()));
        Assert.Equal(new CaptureSettings(), CaptureSettings.ReadSaved("{}"));
        Assert.Equal(new CaptureSettings(), CaptureSettings.ReadSaved(expected));
    }

    [Fact]
    public void NegotiatedHdrSettingsTravelToWorkerButCannotBeRestoredAsUserPreferences()
    {
        var color = new HdrVideoFormat(true, new(1000, 50, 1000, 400));
        var settings = new CaptureSettings(VideoCodec: "h265", VideoColor: color);
        var json = SettingsJson(settings);
        Assert.Equal(settings, JsonSerializer.Deserialize(json, CaptureSettingsJsonContext.Default.CaptureSettings));
        Assert.Throws<NotSupportedException>(() => CaptureSettings.ReadSaved(json));
        Assert.Equal(color, HdrVideoProtocol.Format(StrictJson.Parse(JsonData.Bytes(settings.Request("Hardware", color: color)))));
        Assert.DoesNotContain("VideoColor", SettingsJson(new()));
    }

    [Theory]
    [InlineData("h264", false, "auto", 0)]
    [InlineData("h264", false, "auto", 27)]
    [InlineData("h265", true, "portrait", 90)]
    [InlineData("av1", true, "landscape", 120)]
    public void SourceGeneratedSettingsRoundTripAndWorkerContract(string codec, bool audio, string orientation, int fps)
    {
        var settings = new CaptureSettings(640, fps, 100_000, audio, orientation, codec, true);
        var json = SettingsJson(settings);
        Assert.Equal(settings, CaptureSettings.ReadSaved(json));
        Assert.Equal(settings, JsonSerializer.Deserialize(json, CaptureSettingsJsonContext.Default.CaptureSettings));
        Assert.Equal(settings, CaptureSettings.ReadSaved(json[..^1] + ""","UnknownFutureField":{"nested":[1,2]},"MediaTransport":"tcp"}"""));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("{")]
    [InlineData("""{"MaxFps":"60"}""")]
    [InlineData("""{"EnableAudio":1}""")]
    [InlineData("""{"MaxFps":2147483648}""")]
    public void MalformedSavedSettingsStillFail(string json) =>
        Assert.ThrowsAny<JsonException>(() => CaptureSettings.ReadSaved(json));

    [Theory]
    [InlineData("""{"MaxFps":-1}""")]
    [InlineData("""{"MaxSize":63}""")]
    [InlineData("""{"VideoBitrate":99999}""")]
    [InlineData("""{"VideoCodec":null}""")]
    [InlineData("""{"ReceiverOrientation":"sideways"}""")]
    public void InvalidSavedSettingsStillFailValidation(string json) =>
        Assert.Throws<ArgumentException>(() => CaptureSettings.ReadSaved(json));

    [Theory]
    [InlineData("""{"MediaTransport":"udp"}""")]
    [InlineData("""{"MediaTransport":null}""")]
    [InlineData("""{"MediaTransport":1}""")]
    public void UnsupportedPersistedTransportIsNotSilentlyMigrated(string json) =>
        Assert.Throws<NotSupportedException>(() => CaptureSettings.ReadSaved(json));

    [Fact]
    public void SessionRequestRetainsNullCropDefaultAudioAndConditionalReceiverOptions()
    {
        const string expected = """{"video":{"codec":"h264","encoder":"Hardware","bitrate":8000000,"maxSize":1920,"maxFps":0,"displayId":0,"orientation":"0","crop":null,"codecOptions":""},"receiverOrientation":"auto","audio":{"enabled":false,"codec":"raw","encoder":"","bitrate":1536000,"source":"output","codecOptions":""}}""";
        var settings = new CaptureSettings();
        Assert.Equal(expected, JsonData.Text(settings.Request("Hardware")));
        Assert.Equal(expected, JsonData.Text(settings.Request("Hardware", ReceiverOptions.None)));
        var requested = settings.Request("Hardware", new(true));
        Assert.Equal(expected[..^1] + ""","receiverOptions":{"version":1,"playbackStats":true}}""", JsonData.Text(requested));
        Assert.Equal(new ReceiverOptions(true), ReceiverOptionProtocol.Parse(
            StrictJson.Parse(JsonData.Bytes(requested)), ReceiverOptionCapabilities.Implemented));
        Assert.Equal("""{"version":1,"supported":["playbackStats"]}""",
            JsonData.Text(ReceiverOptionCapabilities.Implemented.WireValue));
        Assert.Equal("""{"version":1,"supported":[]}""",
            JsonData.Text(ReceiverOptionCapabilities.None.WireValue));
    }

    [Fact]
    public void DiscoveryRetainsExactShapeAndOmitsOnlyAbsentInstanceId()
    {
        const string expected = """{"protocol":"screencopy","version":3,"name":"TV","port":27184,"state":"available"}""";
        Assert.Equal(expected, Encoding.UTF8.GetString(Receiver.Discovery.Advertisement("TV", 27184, false)));
        Assert.Equal(expected.Replace("available", "busy")[..^1] + ""","instanceId":""}""",
            Encoding.UTF8.GetString(Receiver.Discovery.Advertisement("TV", 27184, true, "")));
    }

    [Fact]
    public async Task ControlAndStreamTreesKeepWireEnvelopeAndBytes()
    {
        var hello = JsonData.Object(("role", "sender"), ("versions", JsonData.Value(new[] { 3 })), ("name", "Windows"));
        var sender = Message.Control(MessageType.Hello, 0, hello);
        var receiver = Receiver.Packet.Control(Receiver.PacketType.Hello, 0, hello);
        Assert.Equal("""{"role":"sender","versions":[3],"name":"Windows"}""", Encoding.UTF8.GetString(sender.Payload));
        Assert.Equal(sender.Payload, receiver.Payload);
        Assert.Equal(-1, sender.PtsUs);
        Assert.Equal(0u, sender.Track);
        using var wire = new MemoryStream();
        await Wire.WriteAsync(wire, sender, CancellationToken.None);
        wire.Position = 0;
        Assert.Equal(sender.Payload, (await Receiver.Wire.ReadAsync(wire, CancellationToken.None)).Payload);

        var video = JsonData.Object(("codec", "h264"), ("width", 640), ("height", 360), ("csd", JsonData.Value(new[] { "AA==" })));
        Assert.Equal("""{"codec":"h264","width":640,"height":360,"csd":["AA=="]}""", JsonData.Text(video));
        var audio = JsonData.Object(("codec", "raw"), ("sampleRate", 48000), ("channels", 2),
            ("csd", JsonData.Value(Array.Empty<string>())), ("sampleFormat", "s16le"));
        Assert.Equal("""{"codec":"raw","sampleRate":48000,"channels":2,"csd":[],"sampleFormat":"s16le"}""", JsonData.Text(audio));
        var format = Receiver.AudioFormat.Parse(Receiver.JsonObject.Parse(JsonData.Bytes(audio)), "raw");
        Assert.Equal("raw", format.Codec);
        Assert.Equal("""{"ok":true}""", JsonData.Text(JsonData.Object(("ok", true))));
        Assert.Equal("""{"nonce":-2147483648}""", JsonData.Text(JsonData.Object(("nonce", int.MinValue))));
        Assert.Equal("""{"reason":null}""", JsonData.Text(JsonData.Object(("reason", null))));
    }

    [Theory]
    [InlineData("""{"nonce":1,"nonce":2}""")]
    [InlineData("""{"uncappedFps":true,"uncappedFps":false}""")]
    [InlineData("""{"nonce":1.5}""")]
    [InlineData("""{"nonce":2147483648}""")]
    [InlineData("""{"nested":{"a":1,"a":2}}""")]
    [InlineData("[]")]
    [InlineData("{")]
    public void StrictProtocolValidationStillRejectsMalformedObjects(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.Throws<ProtocolException>(() => StrictJson.Parse(bytes));
        Assert.Throws<Receiver.ProtocolException>(() => Receiver.JsonObject.Parse(bytes));
    }

    [Fact]
    public void ReportsRetainIndentationEscapingNumbersNullsAndReusableTrees()
    {
        var item = JsonData.Object(("PtsUs", 1234567890123L), ("rate", 0.5), ("failure", null), ("name", "<é>"));
        var report = JsonData.Object(("passed", false), ("checks", JsonData.Value(new[] { item, item })));
        const string expected = "{\n  \"passed\": false,\n  \"checks\": [\n    {\n      \"PtsUs\": 1234567890123,\n      \"rate\": 0.5,\n      \"failure\": null,\n      \"name\": \"\\u003C\\u00E9\\u003E\"\n    },\n    {\n      \"PtsUs\": 1234567890123,\n      \"rate\": 0.5,\n      \"failure\": null,\n      \"name\": \"\\u003C\\u00E9\\u003E\"\n    }\n  ]\n}";
        Assert.Equal(expected, JsonData.Text(report, indented: true).Replace("\r\n", "\n"));
        Assert.Equal(JsonData.Bytes(report), Encoding.UTF8.GetBytes(JsonData.Text(report)));
        Assert.Null(item.Parent);
        Assert.Throws<ArgumentException>(() => JsonData.Object(("duplicate", 1), ("duplicate", 2)));
    }

    [Fact]
    public void ReflectionDisabledRunIsActuallyReflectionDisabled()
    {
        if (Environment.GetEnvironmentVariable("REDRAINCAST_TEST_REFLECTION_DISABLED") == "1")
        {
            Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
            Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(new { forbidden = true }));
        }
        Assert.Equal(new CaptureSettings(), CaptureSettings.ReadSaved(SettingsJson(new())));
        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(
            Message.Control(MessageType.StreamReady, 1, JsonData.Object(("ok", true))).Payload));
    }
}
