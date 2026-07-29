namespace Coda.Agent.Hooks;

/// <summary>
/// A single user-configured hook that fires on an agent lifecycle event.
/// Supports four handler types: <c>command</c> (shell subprocess), <c>http</c>
/// (POST to a URL), <c>prompt</c> (LLM rule evaluation), and <c>agent</c>
/// (subagent evaluation).
/// </summary>
/// <param name="Event">The lifecycle event name: e.g. <c>"PreToolUse"</c>, <c>"UserPromptSubmit"</c>.</param>
/// <param name="Command">
/// The shell command to execute. Required when <paramref name="HandlerType"/> is <c>"command"</c>
/// (or <see langword="null"/>). <see langword="null"/> for non-command handler types.
/// </param>
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
/// </param>
/// <param name="AllowSystemPromptReplace">
/// Opt-in flag that permits a <c>UserPromptSubmit</c> hook to return <c>systemPrompt</c> (full
/// replacement). Default <see langword="false"/>: without this flag a <c>systemPrompt</c> field
/// in the hook output is ignored and a warning is logged.
/// </param>
/// <param name="Mutates">
/// The list of output fields this hook may return that mutate data seen by the user or stored in
/// history. Used statically at session start by the TUI to decide whether to buffer assistant text.
/// <see langword="null"/> or empty when the hook does not declare any mutations.
/// </param>
/// <param name="HandlerType">
/// The handler type: <c>"command"</c> (default when <paramref name="Command"/> is set),
/// <c>"http"</c>, <c>"prompt"</c>, or <c>"agent"</c>.
/// <see langword="null"/> defaults to <c>"command"</c>.
/// </param>
/// <param name="Url">The URL for an <c>http</c> handler. <see langword="null"/> for other types.</param>
/// <param name="HookPrompt">
/// The natural-language rule for a <c>prompt</c> or <c>agent</c> handler.
/// <see langword="null"/> for other types.
/// </param>
/// <param name="AgentType">
/// The subagent type for an <c>agent</c> handler (e.g. <c>"code-review"</c>).
/// <see langword="null"/> defaults to <c>"general-purpose"</c>.
/// </param>
public sealed record UserHook(
    string Event,
    string? Command,
    string? Matcher = null,
    int? TimeoutSeconds = null,
    bool? FailOpen = null,
    string? UnattendedDecision = null,
    bool AllowSystemPromptReplace = false,
    IReadOnlyList<string>? Mutates = null,
    string? HandlerType = null,
    string? Url = null,
    string? HookPrompt = null,
    string? AgentType = null,
    bool Enabled = true,
    HookScope Scope = HookScope.User);
