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
    /// Tools that are pre-approved for the current turn — they skip the user permission prompt
    /// without restricting the advertised tool set. Pre-approval cannot widen a deny imposed by
    /// <see cref="DeniedTools"/> or override an <see cref="AllowedTools"/> allowlist: deny wins,
    /// and a tool absent from an active allowlist is still blocked even if pre-approved.
    /// Null means no pre-approval was declared. Composed by union across layers.
    /// </summary>
    public IReadOnlyList<string>? PreApprovedTools { get; init; }

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
        && this.PreApprovedTools is null
        && this.ToolChoice is null
        && this.Model is null
        && this.Effort is null;

    /// <summary>
    /// Composes a <paramref name="delta"/> shape onto an <paramref name="existing"/> shape,
    /// returning the merged result. Used by <see cref="AgentLoop"/> to apply skill-contributed
    /// shape changes without requiring access to the internal <c>TurnShapeResolver</c>.
    /// </summary>
    /// <remarks>
    /// Composition rules:
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="AllowedTools"/> — intersects when both sides have a list; uses delta's list
    ///     when only delta has one; keeps existing's list otherwise. Prevents a skill from widening
    ///     an allowlist a hook already imposed.
    ///   </item>
    ///   <item>
    ///     <see cref="DeniedTools"/> — union of both sides (denial is monotonic; set only grows).
    ///   </item>
    ///   <item>
    ///     <see cref="Model"/>/<see cref="Effort"/> — delta wins when non-null (skill overrides hook
    ///     model; spec: "the skill wins, because it is the more specific and later decision").
    ///   </item>
    ///   <item>
    ///     All other fields (<see cref="SystemPrompt"/> etc.) — existing wins; skills do not touch
    ///     the system prompt or tool-choice.
    ///   </item>
    /// </list>
    /// Returns <see langword="null"/> when both inputs are null or empty.
    /// </remarks>
    public static TurnShape? Layer(TurnShape? existing, TurnShape? delta)
    {
        if (delta is null || delta.IsEmpty)
        {
            return existing is { IsEmpty: false } ? existing : null;
        }

        if (existing is null || existing.IsEmpty)
        {
            return delta.IsEmpty ? null : delta;
        }

        // AllowedTools: intersect when both have lists; delta's list when only delta has one.
        IReadOnlyList<string>? mergedAllowed;
        if (existing.AllowedTools is not null && delta.AllowedTools is not null)
        {
            var deltaSet = new HashSet<string>(delta.AllowedTools, StringComparer.OrdinalIgnoreCase);
            mergedAllowed = [.. existing.AllowedTools.Where(t => deltaSet.Contains(t))];
        }
        else if (delta.AllowedTools is not null)
        {
            mergedAllowed = delta.AllowedTools;
        }
        else
        {
            mergedAllowed = existing.AllowedTools;
        }

        // DeniedTools: union (denial is monotonic — set only grows).
        IReadOnlyList<string>? mergedDenied;
        if (existing.DeniedTools is { Count: > 0 } && delta.DeniedTools is { Count: > 0 })
        {
            var union = new HashSet<string>(existing.DeniedTools, StringComparer.OrdinalIgnoreCase);
            union.UnionWith(delta.DeniedTools);
            mergedDenied = [.. union];
        }
        else
        {
            mergedDenied = delta.DeniedTools ?? existing.DeniedTools;
        }

        // PreApprovedTools: union (pre-approval can only expand across layers).
        IReadOnlyList<string>? mergedPreApproved;
        if (existing.PreApprovedTools is { Count: > 0 } && delta.PreApprovedTools is { Count: > 0 })
        {
            var union = new HashSet<string>(existing.PreApprovedTools, StringComparer.OrdinalIgnoreCase);
            union.UnionWith(delta.PreApprovedTools);
            mergedPreApproved = [.. union];
        }
        else
        {
            mergedPreApproved = delta.PreApprovedTools ?? existing.PreApprovedTools;
        }

        // Model/Effort: delta wins (skill is more specific — last-write semantics).
        var mergedModel = delta.Model ?? existing.Model;
        var mergedEffort = delta.Effort ?? existing.Effort;

        var result = new TurnShape
        {
            // Non-skill fields: existing wins; skills do not touch system prompt or tool-choice.
            SystemPrompt = existing.SystemPrompt,
            AppendSystemPrompt = existing.AppendSystemPrompt,
            ToolChoice = existing.ToolChoice,

            AllowedTools = mergedAllowed,
            DeniedTools = mergedDenied,
            PreApprovedTools = mergedPreApproved,
            Model = mergedModel,
            Effort = mergedEffort,
        };

        return result.IsEmpty ? null : result;
    }
}
