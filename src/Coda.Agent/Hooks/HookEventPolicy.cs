namespace Coda.Agent.Hooks;

/// <summary>
/// Per-event timeout and fail-open defaults for user hooks.
/// </summary>
/// <remarks>
/// <c>FailOpen = false</c> for <c>PreToolUse</c> is deliberate: a policy gate that silently
/// permits on error is no gate at all. For the observation-only events (<c>PostToolUse</c>,
/// <c>Stop</c>) fail-open preserves the existing behaviour where exceptions and timeouts are
/// swallowed rather than interrupting the turn.
/// </remarks>
public static class HookEventPolicy
{
    private static readonly Dictionary<string, HookEventDefaults> Policies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["PreToolUse"]  = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: false),
            ["PostToolUse"] = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: true),
            ["Stop"]        = new HookEventDefaults(TimeoutSeconds: 10, FailOpen: true),
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
