namespace Coda.Agent.Hooks;

/// <summary>
/// The result of running all <c>UserPromptSubmit</c> hooks for a turn, combining the
/// allow/block decision with any per-turn mutations (prompt, context, shape overrides).
/// </summary>
public sealed record UserPromptSubmitResult
{
    /// <summary>
    /// When <see langword="true"/>, the turn must not proceed: the user message must not be
    /// appended to history and the agent loop must not run. <see cref="Reason"/> is the
    /// human-readable explanation surfaced to the caller.
    /// </summary>
    public bool Block { get; init; }

    /// <summary>Human-readable reason for the block, or <see langword="null"/> when not blocked.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The merged prompt text to send to the model. When non-null this replaces the text content
    /// of the user's message in the history entry. The modified text is what the model sees and
    /// what is persisted in the transcript (history holds the honest record of what the model
    /// actually received). The original prompt reaches the user through the
    /// <see cref="IAgentSink.OnPromptRewritten"/> notification emitted by the session after
    /// storing the modified content — it is not stored in history.
    /// </summary>
    public string? ModifiedPrompt { get; init; }

    /// <summary>
    /// Concatenated additional context from all hooks that supplied it. When non-null this is
    /// appended as a separate synthetic user message immediately after the (possibly modified)
    /// user message — not merged into it.
    /// </summary>
    public string? AdditionalContext { get; init; }

    /// <summary>
    /// Per-turn shape overrides built from the merged hook outputs (system prompt, tool lists,
    /// tool choice, model, effort). <see langword="null"/> or an all-null shape means no override.
    /// </summary>
    public TurnShape? Shape { get; init; }

    /// <summary>
    /// The command of the hook that produced the last <c>modifiedPrompt</c> value (last-writer
    /// wins). Non-null only when <see cref="ModifiedPrompt"/> is set; used for the transcript
    /// notice that tells the user their prompt was rewritten.
    /// </summary>
    public string? ModifiedByHookCommand { get; init; }

    /// <summary>An allow result with no mutations — every property is null or default.</summary>
    public static UserPromptSubmitResult Allow { get; } = new();
}
