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
    public static JsonNode Value(PlaybackStatistics value) =>
        JsonSerializer.SerializeToNode(value, ReportJsonContext.Default.PlaybackStatistics)!;
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
    public static JsonNode? Value(HdrRenderDiagnosticReport? value) =>
        JsonSerializer.SerializeToNode(value, ReportJsonContext.Default.HdrRenderDiagnosticReport);
    public static JsonNode? Value(HdrDecodedColorProbeReport? value) =>
        JsonSerializer.SerializeToNode(value, ReportJsonContext.Default.HdrDecodedColorProbeReport);
    public static JsonNode Value(HdrLoopbackReport[] value) =>
        JsonSerializer.SerializeToNode(value, ReportJsonContext.Default.HdrLoopbackReportArray)!;
}

[JsonSerializable(typeof(ColorProbeReport))]
[JsonSerializable(typeof(PlaybackStatistics))]
[JsonSerializable(typeof(PresentedVideoSample[]))]
[JsonSerializable(typeof(MediaTimingSnapshot))]
[JsonSerializable(typeof(ReceiverRecoveryState))]
[JsonSerializable(typeof(PeerPlaybackSnapshot))]
[JsonSerializable(typeof(HdrRenderDiagnosticReport))]
[JsonSerializable(typeof(HdrDecodedColorProbeReport))]
[JsonSerializable(typeof(HdrLoopbackReport[]))]
internal partial class ReportJsonContext : JsonSerializerContext;
