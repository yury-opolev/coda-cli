namespace Coda.Agent.Hooks;

/// <summary>
/// The merged output of all <c>SessionStart</c> hooks applied once per session start.
/// </summary>
/// <remarks>
/// Unlike per-turn <c>UserPromptSubmit</c> outputs, these values are session-scoped:
/// <see cref="AppendSystemPrompt"/> applies to every turn, <see cref="AdditionalContext"/>
/// is injected once before the first user turn, and <see cref="InitialUserMessage"/> creates
/// a synthetic first turn that still passes through <c>UserPromptSubmit</c> hooks.
/// </remarks>
public sealed record SessionStartResult
{
    /// <summary>
    /// Context text injected as a synthetic user message exactly once, immediately before the
    /// first real user turn. Null when no hook returned <c>additionalContext</c>.
    /// Multiple hooks' values are concatenated with double newlines.
    /// </summary>
    public string? AdditionalContext { get; init; }

    /// <summary>
    /// Text appended to the session system prompt on every turn for the life of the session.
    /// Null when no hook returned <c>appendSystemPrompt</c>.
    /// Multiple hooks' values are concatenated with double newlines.
    /// </summary>
    public string? AppendSystemPrompt { get; init; }

    /// <summary>
    /// A synthetic first turn submitted as if the user had typed it. Passes through
    /// <c>UserPromptSubmit</c> hooks; cannot re-trigger <c>SessionStart</c>. Last-writer-wins
    /// when multiple hooks return this field. Null when no hook returned
    /// <c>initialUserMessage</c>.
    /// </summary>
    public string? InitialUserMessage { get; init; }

    /// <summary>An empty result with all fields null (no outputs from any hook).</summary>
    public static SessionStartResult Empty { get; } = new();
}
