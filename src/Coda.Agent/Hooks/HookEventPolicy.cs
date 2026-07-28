namespace Coda.Agent.Hooks;

/// <summary>
/// Per-event timeout and fail-open defaults for user hooks.
/// </summary>
/// <remarks>
/// <c>FailOpen = false</c> for <c>UserPromptSubmit</c>, <c>PreToolUse</c>, and
/// <c>PermissionRequest</c> is deliberate:
/// a policy gate that silently permits on error is no gate at all. For the observation-only
/// events (<c>PostToolUse</c>, <c>Stop</c>) fail-open preserves the existing behaviour where
/// exceptions and timeouts are swallowed rather than interrupting the turn.
/// </remarks>
public static class HookEventPolicy
{
    private static readonly Dictionary<string, HookEventDefaults> Policies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Fail-closed: a policy gate that silently permits on error is no gate at all.
            // 30 s budget because a classifier hook is a plausible UserPromptSubmit implementation.
            ["UserPromptSubmit"] = new HookEventDefaults(TimeoutSeconds: 30, FailOpen: false),
            ["PreToolUse"]  = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: false),
            // Fail-closed: a broken permission gate must never grant access.
            ["PermissionRequest"] = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: false),
            ["PostToolUse"]   = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: true),
            ["Stop"]          = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: true),
            // Session lifecycle events: fail-open so a broken hook never blocks the user.
            ["SessionStart"]  = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: true),
            ["SessionEnd"]    = new HookEventDefaults(TimeoutSeconds:  2, FailOpen: true),
            ["Notification"]  = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: true),
                // Response-side events: fail-open — a broken hook must not discard the response.
                ["AgentResponse"] = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: true),
                // Subagent lifecycle: SubagentStart is fail-closed (a broken hook must not let an
                // unshaped subagent run); SubagentStop is fail-open (must not lose the completed work).
                ["SubagentStart"] = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: false),
                ["SubagentStop"]  = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: true),
                // Compaction hooks: fail-open — a broken hook must not block or lose a compaction.
                ["PreCompact"]    = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: true),
                ["PostCompact"]   = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: true),
        };

    private static readonly HookEventDefaults UnknownDefault = new(TimeoutSeconds: 10, FailOpen: true);

    /// <summary>
    /// Returns the effective defaults for <paramref name="eventName"/>.
    /// Unknown events use <c>(10s, failOpen=true)</c>.
    /// </summary>
    public static HookEventDefaults Get(string eventName) =>
        Policies.TryGetValue(eventName, out var defaults) ? defaults : UnknownDefault;
}

/// <summary>The default timeout and fail-open policy for a specific hook event.</summary>
/// <param name="TimeoutSeconds">
/// How long a hook subprocess may run before it is killed (seconds).
/// </param>
/// <param name="FailOpen">
/// When <see langword="true"/>, a failing or timed-out hook is treated as allow.
/// When <see langword="false"/>, it blocks.
/// </param>
public sealed record HookEventDefaults(int TimeoutSeconds, bool FailOpen);
