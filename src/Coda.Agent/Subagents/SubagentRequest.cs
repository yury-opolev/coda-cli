namespace Coda.Agent;

/// <summary>
/// The caller's requested influence over a subagent's system prompt.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Append"/> is the additive form and is always safe: the subagent definition's own body
/// stays in front of it, so a caller can add context but cannot talk the subagent out of its own
/// guardrails. <see cref="Replacement"/> discards that body, which is why it is honoured only when
/// <see cref="Settings.SubagentSettings.AllowSystemPromptReplacement"/> is enabled; otherwise it is
/// demoted to an append rather than dropped, so the caller's intent still reaches the subagent
/// without the guardrails going with it.
/// </para>
/// <para>
/// Both fields carry model-supplied text. They are appended to the prompt and nothing else — never
/// interpolated into tool policy, tool selection, or the depth and fan-out limits, which come from
/// the registry and settings alone.
/// </para>
/// </remarks>
public sealed record SubagentSystemPrompt(string? Append = null, string? Replacement = null)
{
    /// <summary>True when the caller asked for nothing, so the prompt is built exactly as before.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(this.Append) && string.IsNullOrWhiteSpace(this.Replacement);
}

/// <summary>
/// Everything a host needs to run one nested subagent. Bundled into a record rather than added to
/// the positional overloads of <see cref="ISubagentHost"/>, which had already grown to eight
/// parameters and could not absorb another optional one readably.
/// </summary>
/// <param name="SubagentType">The agent definition to resolve (e.g. <c>general-purpose</c>, <c>explore</c>).</param>
/// <param name="Prompt">The task for the subagent.</param>
/// <param name="TaskId">The registered task id this run belongs to.</param>
/// <param name="Depth">Nesting depth of the child: 1 for a subagent of the main agent.</param>
public sealed record SubagentRequest(string SubagentType, string Prompt, string TaskId, int Depth)
{
    /// <summary>The parent turn's tool-activity context, so child tool calls stay correlated.</summary>
    public ToolActivityContext? ParentActivity { get; init; }

    /// <summary>
    /// The parent turn's tool restriction, applied monotonically: the child can only be at least as
    /// restricted as the parent.
    /// </summary>
    public TurnShape? ParentToolRestriction { get; init; }

    /// <summary>The caller's requested system-prompt influence, or null for none.</summary>
    public SubagentSystemPrompt? SystemPrompt { get; init; }

    /// <summary>
    /// Explicit model id for this subagent run, or null to inherit from settings or the session.
    /// Same-provider only by design; an unknown id surfaces as the provider's own error.
    /// </summary>
    public string? Model { get; init; }
}
