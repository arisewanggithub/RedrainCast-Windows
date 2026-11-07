using System.Text.Json.Serialization;

namespace RedrainCast.Send.Core;

[JsonSerializable(typeof(CaptureSettings))]
public partial class CaptureSettingsJsonContext : JsonSerializerContext;
