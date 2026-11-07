using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RedrainCast.Protocol;

/// <summary>Explicit JSON trees, without a reflection-based serializer fallback.</summary>
public static class JsonData
{
    public static JsonObject Object(params (string Name, JsonNode? Value)[] properties)
    {
        var result = new JsonObject();
        foreach (var (name, value) in properties) result.Add(name, value?.DeepClone());
        return result;
    }

    public static JsonNode? Value(JsonNode? value) => value;
    public static JsonNode? Value(string? value) => JsonValue.Create(value);
    public static JsonNode Value(bool value) => JsonValue.Create(value)!;
    public static JsonNode? Value(bool? value) => value.HasValue ? Value(value.Value) : null;
    public static JsonNode Value(int value) => JsonValue.Create(value)!;
    public static JsonNode Value(uint value) => JsonValue.Create(value)!;
    public static JsonNode Value(long value) => JsonValue.Create(value)!;
    public static JsonNode? Value(long? value) => value.HasValue ? Value(value.Value) : null;
    public static JsonNode Value(double value) => JsonValue.Create(value)!;
    public static JsonNode? Value(double? value) => value.HasValue ? Value(value.Value) : null;
    public static JsonNode Value(DateTime value) => JsonValue.Create(value)!;
    public static JsonNode Value(IEnumerable<string> values) => Array(values, Value);
    public static JsonNode? Value(string[]? values) => values is null ? null : Value((IEnumerable<string>)values);
    public static JsonNode Value(IEnumerable<int> values) => Array(values, Value);
    public static JsonNode Value(IEnumerable<uint> values) => Array(values, Value);
    public static JsonNode Value(IEnumerable<long> values) => Array(values, Value);
    public static JsonNode Value(IEnumerable<JsonNode> values) => Array(values, Value);

    public static JsonArray Array<T>(IEnumerable<T> values, Func<T, JsonNode?> convert)
    {
        var result = new JsonArray();
        foreach (var value in values) result.Add(convert(value)?.DeepClone());
        return result;
    }

    public static byte[] Bytes(JsonNode value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, JsonDataContext.Default.JsonNode);

    public static string Text(JsonNode value, bool indented = false) =>
        indented ? value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) :
            Encoding.UTF8.GetString(Bytes(value));
}

[JsonSerializable(typeof(JsonNode))]
internal partial class JsonDataContext : JsonSerializerContext;
