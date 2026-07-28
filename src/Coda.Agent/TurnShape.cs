namespace Coda.Agent;

/// <summary>
/// Per-turn overrides applied to a single <see cref="AgentLoop.RunAsync"/> call. All
/// properties null means "no override" — a null shape and an all-null shape behave
/// identically to a run with no shape supplied. Use <see cref="TurnShapeResolver"/> to
/// compute the effective values for a turn.
/// </summary>
public sealed record TurnShape
{
    /// <summary>
    /// Fully replaces the session system prompt for this turn when non-null. Because the system
    /// prompt is where the tool-use contract lives, wholesale replacement while tools remain
    /// enabled tends to produce a model that ignores its tools. Prefer
    /// <see cref="AppendSystemPrompt"/> for additive changes that leave the tool contract intact.
    /// When both this property and <see cref="AppendSystemPrompt"/> are set, replacement happens
    /// first and then the append is applied to the replaced prompt.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Appended to the effective system prompt after two newlines when non-null. This is the
    /// safe default: the existing system prompt (and therefore the tool-use contract) is
    /// preserved, and extra context is injected at the end. A caller replacing the system prompt
    /// wholesale via <see cref="SystemPrompt"/> while leaving tools enabled risks producing a
    /// model that ignores its tools. When both properties are set, replacement happens first,
    /// then this append is applied.
    /// </summary>
    public string? AppendSystemPrompt { get; init; }

    /// <summary>
    /// Restricts the advertised tool set to only the tools whose names appear in this list
    /// (case-insensitive). A name that matches no registered tool is silently ignored — lists
    /// are written by humans against a tool set that varies by configuration. Null means no
    /// restriction; an empty list means no tools are available this turn. Note that empty and
    /// null differ: null leaves the full set intact, empty removes all tools.
    /// </summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>
    /// Removes the named tools from the advertised set (case-insensitive). A name that matches
    /// no registered tool is silently ignored. Denial wins over allowance: a name present in both
    /// <see cref="AllowedTools"/> and <see cref="DeniedTools"/> is removed.
    /// </summary>
    public IReadOnlyList<string>? DeniedTools { get; init; }

    /// <summary>
    /// Sets the <c>tool_choice</c> value for this turn. Accepted values: <c>auto</c>, <c>any</c>,
    /// <c>none</c> (case-insensitive); any other value is treated as null (no override). Null
    /// leaves the provider's default in effect.
    /// </summary>
    public string? ToolChoice { get; init; }

    /// <summary>
    /// Overrides the model for this turn. Null uses the session model.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Overrides the reasoning effort level for this turn. Null uses the session effort.
    /// Accepted values are provider-specific (e.g. <c>low</c>, <c>medium</c>, <c>high</c>,
    /// <c>max</c>). The model-capability clamp in the wire client still applies against the
    /// resolved model, not the session model.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>A shape with all properties null — equivalent to no override.</summary>
    public static TurnShape None { get; } = new();

    /// <summary>
    /// Whether this shape carries no overrides; <see langword="true"/> when every property is
    /// null. A null shape and an all-null shape behave identically.
    /// </summary>
    public bool IsEmpty =>
        this.SystemPrompt is null
        && this.AppendSystemPrompt is null
        && this.AllowedTools is null
        && this.DeniedTools is null
        && this.ToolChoice is null
        && this.Model is null
        && this.Effort is null;
}
