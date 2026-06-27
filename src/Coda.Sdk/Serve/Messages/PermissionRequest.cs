using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record PermissionRequest(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("inputPreview")] string InputPreview);
