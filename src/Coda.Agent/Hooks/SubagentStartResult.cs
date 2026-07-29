namespace Coda.Agent.Hooks;

/// <summary>
/// The merged outcome of all <c>SubagentStart</c> hook invocations, representing
/// what hooks want to do before the nested agent makes its first model call.
/// </summary>
public sealed record SubagentStartResult
{
    /// <summary>
    /// When <see langword="true"/>, the subagent must not run; the <c>task</c> tool should
    /// return <see cref="Reason"/> as an error result. A fail-closed hook that times out or
    /// throws also produces a block.
    /// </summary>
    public bool Block { get; init; }

    /// <summary>Human-readable reason surfaced as the error when <see cref="Block"/> is <see langword="true"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Replacement task text. When non-null, replaces the <c>prompt</c> parameter the subagent
    /// receives; the original text is not preserved in the subagent's context.
    /// </summary>
    public string? ModifiedPrompt { get; init; }

    /// <summary>
    /// Additional context prepended to the effective prompt. Because a subagent's history starts
    /// empty, this is prepended to the prompt text rather than injected as a separate message.
    /// </summary>
    public string? AdditionalContext { get; init; }

    /// <summary>
    /// Text appended to the subagent's system prompt only. Does not affect the parent's system prompt.
    /// </summary>
    public string? AppendSystemPrompt { get; init; }

    /// <summary>
    /// Tool shape produced by merging the hook's <c>allowedTools</c> / <c>deniedTools</c> with the
    /// inherited parent restriction. <see langword="null"/> means no additional restriction beyond the parent's.
    /// A hook's restriction is composed monotonically: it can only tighten, never widen, the parent.
    /// </summary>
    public TurnShape? Shape { get; init; }

    /// <summary>The shell command of the hook that produced the last mutation, or <see langword="null"/>.</summary>
    public string? ByHookCommand { get; init; }

    /// <summary>An allow result with no mutations — every property is null or default.</summary>
    public static SubagentStartResult Allow { get; } = new();
}
