using System.Text.Json.Serialization;

namespace Synopsis.Mcp;

[JsonSerializable(typeof(McpRequest))]
[JsonSerializable(typeof(McpResponse))]
[JsonSerializable(typeof(McpError))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
internal partial class McpJsonContext : JsonSerializerContext;
