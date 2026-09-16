using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coda.Client.Protocol;

/// <summary>
/// Parameters for <c>session/prompt</c>. <see cref="Text"/> is optional and
/// <see cref="Images"/> is always present (possibly empty), matching the wire
/// shape where the images array is required even when there are none.
/// </summary>
public sealed record PromptParams
{
    public string? Text { get; init; }

    public IReadOnlyList<WireImage> Images { get; init; } = Array.Empty<WireImage>();
}

/// <summary>A base64-encoded image attached to a prompt.</summary>
public sealed record WireImage
{
    /// <summary><c>image/png</c>, <c>image/jpeg</c>, <c>image/gif</c> or
    /// <c>image/webp</c>.</summary>
    public string MediaType { get; init; } = string.Empty;

    public string Base64 { get; init; } = string.Empty;
}

/// <summary>
/// The <c>session/prompt</c> result. A successful JSON-RPC round-trip can still
/// report failure: check <see cref="Ok"/> and <see cref="Error"/> before treating
/// a turn as having done what was asked. <see cref="Interrupted"/> being true is
/// the authoritative signal that an interrupt actually landed — an interrupt
/// acknowledgement on its own is not this.
/// </summary>
public sealed record PromptResult
{
    public bool Ok { get; init; }

    public string? StopReason { get; init; }

    public bool Interrupted { get; init; }

    /// <summary>Present only when a goal was active and produced a non-<c>None</c>
    /// outcome; omitted otherwise.</summary>
    public WireGoalStatus? GoalStatus { get; init; }

    public string? Error { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>
/// The goal outcome carried on a <see cref="PromptResult"/>. <see
/// cref="Outcome"/> is a string rather than an enum so an outcome this build has
/// never heard of survives instead of throwing; the known values are
/// <c>Met</c>, <c>Unmet</c>, <c>GenuinelyBlocked</c> and <c>Stalled</c>.
/// </summary>
public sealed record WireGoalStatus
{
    public string Outcome { get; init; } = string.Empty;

    /// <summary>The judge's last "what still remains" text, or null when the goal
    /// was met.</summary>
    public string? Remaining { get; init; }

    public int Continuations { get; init; }

    public double ElapsedSeconds { get; init; }

    public bool Escalated { get; init; }

    public bool ExtensionUsed { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>
/// Parameters for <c>session/setGoal</c>. Every field is nullable and omitted
/// when null; because the engine treats an absent budget field as "restore the
/// default", each call replaces the <b>entire</b> configuration rather than
/// patching it. Prefer the typed <c>CodaClient.SetGoalAsync</c> overload, which
/// speaks <see cref="Coda.Client.ContinuationBudget"/> and
/// <see cref="Coda.Client.DurationBudget"/> so these sentinels never have to be
/// hand-written.
/// </summary>
public sealed record SetGoalParams
{
    /// <summary>The objective text, or null to clear the active goal. Clearing a
    /// goal is not an interrupt.</summary>
    public string? Goal { get; init; }

    /// <summary>Wall-clock budget as a suffixed integer (<c>30m</c>), the token
    /// <c>none</c> for no limit, or null to restore the default (240h).</summary>
    public string? MaxDuration { get; init; }

    /// <summary>Turn budget: a non-negative count, <c>-1</c> for no limit, or null
    /// to restore the default (60000). <c>0</c> is zero continuations, not
    /// unlimited.</summary>
    public int? MaxContinuations { get; init; }
}

/// <summary>
/// The <c>session/setGoal</c> result, echoing the stored configuration. Fields
/// are omitted when the goal is cleared or a budget is unset.
/// </summary>
public sealed record SetGoalResult
{
    public bool Ok { get; init; }

    public string? Goal { get; init; }

    public string? MaxDuration { get; init; }

    public int? MaxContinuations { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}
