namespace Coda.Agent.Hooks;

/// <summary>
/// A single user-configured shell hook that fires on an agent lifecycle event.
/// </summary>
/// <param name="Event">The lifecycle event name: <c>"PreToolUse"</c>, <c>"PostToolUse"</c>, or <c>"Stop"</c>.</param>
/// <param name="Command">The shell command to execute.</param>
/// <param name="Matcher">
/// Optional tool-name filter expressed as a regex pattern (anchored: <c>^(?:</c><paramref name="Matcher"/><c>)$</c>,
/// case-insensitive). A null or empty value matches every tool. For <c>Stop</c> hooks this is ignored.
/// If the pattern fails to compile the hook falls back to case-insensitive exact string equality.
/// </param>
/// <param name="TimeoutSeconds">
/// Per-hook timeout override in seconds. When <see langword="null"/> the event's default is used
/// (see <see cref="HookEventPolicy"/>).
/// </param>
/// <param name="FailOpen">
/// Per-hook fail-open override. When <see langword="null"/> the event's default is used.
/// <c>false</c> means a broken or timed-out hook blocks the action; <c>true</c> means it is silently allowed.
/// </param>
/// <param name="UnattendedDecision">
/// Resolution when a hook returns <c>decision:"ask"</c> and no interactive answerer is available.
/// Accepted values: <c>"allow"</c> or <c>"deny"</c> (case-insensitive); anything else is treated as <c>"deny"</c>.
/// Stored and forwarded in Phase 0 but not yet consumed, because no Phase 0 event can return <c>ask</c>.
/// </param>
public sealed record UserHook(
    string Event,
    string Command,
    string? Matcher = null,
    int? TimeoutSeconds = null,
    bool? FailOpen = null,
    string? UnattendedDecision = null);
