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
/// <param name="AllowSystemPromptReplace">
/// Opt-in flag that permits a <c>UserPromptSubmit</c> hook to return <c>systemPrompt</c> (full
/// replacement). Default <see langword="false"/>: without this flag a <c>systemPrompt</c> field
/// in the hook output is ignored and a warning is logged. Full replacement while tools remain
/// enabled tends to produce a model that ignores its tools; this deliberate opt-in ensures the
/// hook author understands the hazard.
/// </param>
/// <param name="Mutates">
/// The list of output fields this hook may return that mutate data seen by the user or stored in
/// history. Accepted values for an <c>AgentResponse</c> hook: <c>"displayContent"</c> (changes
/// what the user sees; history keeps the original) and <c>"modifiedResponse"</c> (changes both
/// display and history). Unknown entries are preserved and silently ignored at runtime. Used
/// statically at session start by the TUI to decide whether to buffer assistant text.
/// <see langword="null"/> or empty when the hook does not declare any mutations.
/// </param>
public sealed record UserHook(
    string Event,
    string Command,
    string? Matcher = null,
    int? TimeoutSeconds = null,
    bool? FailOpen = null,
    string? UnattendedDecision = null,
    bool AllowSystemPromptReplace = false,
    IReadOnlyList<string>? Mutates = null);
