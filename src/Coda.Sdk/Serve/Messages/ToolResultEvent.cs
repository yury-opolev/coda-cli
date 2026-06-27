using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record ToolResultEvent(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("isError")] bool IsError);
