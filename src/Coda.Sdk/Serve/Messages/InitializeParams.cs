using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record InitializeParams(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("clientInfo")] string? ClientInfo,
    [property: JsonPropertyName("apiKey")] string? ApiKey = null,
    [property: JsonPropertyName("sessionId")] string? SessionId = null);
