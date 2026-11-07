using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RedrainCast.Protocol;
using RedrainCast.Receive.Core;
using RedrainCast.Receive.Media;
using RedrainCast.Send.Media;

namespace RedrainCast.App;

internal static class ReportJson
{
    public static JsonNode? Value(ColorProbeReport? value) =>
        JsonSerializer.SerializeToNode(value, ReportJsonContext.Default.ColorProbeReport);
    public static JsonNode Value(PresentedVideoSample[] value) =>
        JsonSerializer.SerializeToNode(value, ReportJsonContext.Default.PresentedVideoSampleArray)!;
    public static JsonNode? Value(MediaTimingSnapshot? value) =>
        JsonSerializer.SerializeToNode(value, ReportJsonContext.Default.MediaTimingSnapshot);
    public static JsonNode? Value(ReceiverRecoveryState? value) =>
        JsonSerializer.SerializeToNode(value, ReportJsonContext.Default.ReceiverRecoveryState);
    public static JsonNode? Value(PeerPlaybackSnapshot? value) =>
        JsonSerializer.SerializeToNode(value, ReportJsonContext.Default.PeerPlaybackSnapshot);
}

[JsonSerializable(typeof(ColorProbeReport))]
[JsonSerializable(typeof(PresentedVideoSample[]))]
[JsonSerializable(typeof(MediaTimingSnapshot))]
[JsonSerializable(typeof(ReceiverRecoveryState))]
[JsonSerializable(typeof(PeerPlaybackSnapshot))]
internal partial class ReportJsonContext : JsonSerializerContext;
