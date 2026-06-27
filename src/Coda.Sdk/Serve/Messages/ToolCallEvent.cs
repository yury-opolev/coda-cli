using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record ToolCallEvent(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("inputJson")] string InputJson);
