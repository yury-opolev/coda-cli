using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coda.Client.Protocol;

/// <summary>
/// The <c>request/permission</c> payload: the engine is asking whether a tool
/// call may proceed. <see cref="RequestId"/> is the same opaque handle the
/// pending list carries, present so a client that also discovers the request
/// through <c>session/getPendingRequests</c> can tell the two views apart.
/// </summary>
public sealed record PermissionRequest
{
    public string ToolName { get; init; } = string.Empty;

    public string InputPreview { get; init; } = string.Empty;

    public string? RequestId { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>The reply to <c>request/permission</c>. There is no default: a
/// missing reply is a decline, never an implicit <c>allow: true</c>.</summary>
public sealed record PermissionResponse
{
    public bool Allow { get; init; }
}

/// <summary>The <c>request/question</c> payload.</summary>
public sealed record QuestionRequest
{
    public string Question { get; init; } = string.Empty;

    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    public bool MultiSelect { get; init; }

    public bool AllowFreeText { get; init; }

    public string? RequestId { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>The reply to <c>request/question</c>. There is no default answer: an
/// unanswered question must abort, never select the first option or manufacture
/// an empty string.</summary>
public sealed record QuestionResponse
{
    public string Answer { get; init; } = string.Empty;
}

/// <summary>The <c>request/planApproval</c> payload.</summary>
public sealed record PlanApprovalRequest
{
    public string Plan { get; init; } = string.Empty;

    public string? RequestId { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>The reply to <c>request/planApproval</c>. A missing reply rejects the
/// plan; it is never an implicit approval.</summary>
public sealed record PlanApprovalResponse
{
    public bool Approve { get; init; }
}
