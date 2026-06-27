using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record PermissionResponse(
    [property: JsonPropertyName("allow")] bool Allow);
