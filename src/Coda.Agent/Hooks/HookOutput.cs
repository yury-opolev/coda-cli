using System.Text.Json.Nodes;

namespace Coda.Agent.Hooks;

/// <summary>
/// The structured output returned by a single hook invocation,
/// parsed from the hook's stdout after the process exits.
/// </summary>
public sealed record HookOutput
{
    /// <summary>
    /// When <see langword="false"/>, the agent run is aborted immediately.
    /// Default is <see langword="true"/>.
    /// </summary>
    public bool Continue { get; init; } = true;

    /// <summary>Reason surfaced to the user when <see cref="Continue"/> is <see langword="false"/>.</summary>
    public string? StopReason { get; init; }

    /// <summary>Warning or informational message shown to the user alongside the normal output.</summary>
    public string? SystemMessage { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the hook's stdout is excluded from the session transcript.
    /// </summary>
    public bool SuppressOutput { get; init; }

    /// <summary>
    /// Event-specific control decision: <c>allow</c>, <c>block</c>, <c>deny</c>, or <c>ask</c>.
    /// <see langword="null"/> is equivalent to <c>allow</c>.
    /// </summary>
    /// <remarks>
    /// <c>ask</c> is stored and forwarded now but not yet consumed by any Phase 0 event;
    /// it will be resolved by the unattended-decision logic when hooks can return it.
    /// </remarks>
    public string? Decision { get; init; }

    /// <summary>Human-readable explanation of the decision, surfaced as the block or deny reason.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Arbitrary hook-event-specific output (e.g. <c>additionalContext</c>, <c>modifiedInput</c>).
    /// Unknown properties are preserved for forward compatibility.
    /// </summary>
    public JsonObject? HookSpecificOutput { get; init; }

    /// <summary>A no-op output — all defaults, no decision, no side effects.</summary>
    public static HookOutput NoOp { get; } = new();
}
