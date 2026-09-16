using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Coda.Client.Protocol;

/// <summary>The kind of a server-initiated request, as reported in the pending
/// list. Kept alongside the string methods (<c>request/permission</c> and
/// friends) the reverse-request stream carries.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PendingRequestKind
{
    Permission,
    Question,
    PlanApproval,
}

/// <summary>
/// One outstanding server-initiated request as seen by a client that did not
/// issue (or has reconnected after) the original round-trip. <see
/// cref="Display"/> carries only what a UI needs to render the prompt; it never
/// carries a credential, header or environment value.
/// </summary>
public sealed record PendingRequestDto
{
    /// <summary>An opaque handle bound to the engine instance that minted it. It
    /// is never parsed; a handle from a previous engine process is rejected, not
    /// silently reapplied to whatever happens to be pending now.</summary>
    public string RequestId { get; init; } = string.Empty;

    public PendingRequestKind Kind { get; init; }

    public string IssuedAt { get; init; } = string.Empty;

    public string? TurnId { get; init; }

    /// <summary>Reserved and always omitted by the Rust engine; correlate on
    /// <see cref="TurnId"/> together with the <c>event/toolCall</c> stream.</summary>
    public string? CallId { get; init; }

    public JsonNode? Display { get; init; }

    /// <summary><c>"deny" | "noAnswer" | "reject"</c>: the outcome the engine
    /// applies if the request is cancelled or abandoned.</summary>
    public string FailClosedDefault { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>The <c>session/getPendingRequests</c> result.</summary>
public sealed record GetPendingRequestsResult
{
    public IReadOnlyList<PendingRequestDto> Requests { get; init; } = Array.Empty<PendingRequestDto>();

    public string EngineInstanceId { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>
/// Parameters for <c>session/resolveRequest</c>. <see cref="Outcome"/> is
/// validated against the pending request's kind: <c>{ allow }</c>,
/// <c>{ answer }</c> or <c>{ approve }</c>. A stale or consumed handle is an
/// error, never a second grant.
/// </summary>
public sealed record ResolveRequestParams
{
    public string RequestId { get; init; } = string.Empty;

    public JsonNode? Outcome { get; init; }
}

/// <summary>The <c>session/resolveRequest</c> result.</summary>
public sealed record ResolveRequestResult
{
    public bool Ok { get; init; }

    public string State { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public string Outcome { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>Parameters for <c>session/cancelRequest</c>: apply a request's
/// fail-closed default and stop waiting. Does not shut down the connection.</summary>
public sealed record CancelRequestParams
{
    public string RequestId { get; init; } = string.Empty;

    public string? Reason { get; init; }
}

/// <summary>The <c>session/cancelRequest</c> result.</summary>
public sealed record CancelRequestResult
{
    public bool Ok { get; init; }

    public string RequestId { get; init; } = string.Empty;

    public string AppliedDefault { get; init; } = string.Empty;

    public string Outcome { get; init; } = string.Empty;

    public string? Reason { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}
