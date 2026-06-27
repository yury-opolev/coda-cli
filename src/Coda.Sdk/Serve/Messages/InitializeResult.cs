using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

public sealed record InitializeResult(
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("serverInfo")] string ServerInfo,
    [property: JsonPropertyName("telemetryLogPath")] string? TelemetryLogPath = null);
