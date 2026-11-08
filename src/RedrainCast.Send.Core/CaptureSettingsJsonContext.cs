using System.Text.Json.Serialization;

namespace RedrainCast.Send.Core;

[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(CaptureSettings))]
[JsonSerializable(typeof(RedrainCast.Protocol.HdrVideoFormat))]
[JsonSerializable(typeof(RedrainCast.Protocol.HdrStaticMetadata))]
public partial class CaptureSettingsJsonContext : JsonSerializerContext;
