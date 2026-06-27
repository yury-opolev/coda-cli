using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>Params for <c>session/models</c>. <c>refresh</c> re-fetches the catalog from models.dev.</summary>
public sealed record ModelsParams(
    [property: JsonPropertyName("refresh")] bool Refresh = false);
