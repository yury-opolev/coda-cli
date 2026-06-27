using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>Result of <c>session/steer</c>: <see cref="Ok"/> is true when the comment was accepted and queued.</summary>
public sealed record SteerResult(
    [property: JsonPropertyName("ok")] bool Ok);
