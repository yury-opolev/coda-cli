using LlmClient;

namespace Coda.Agent;

/// <summary>
/// The effective turn values after applying a <see cref="TurnShape"/> to the session defaults.
/// Produced by <see cref="TurnShapeResolver.Resolve"/>; consumed by <see cref="AgentLoop"/>
/// to build the per-iteration <see cref="ChatRequest"/> and enforce tool restrictions at
/// execution time.
/// </summary>
internal sealed record TurnShapeResolution
{
    /// <summary>The effective system prompt for this turn.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>The effective model identifier for this turn.</summary>
    public required string Model { get; init; }

    /// <summary>The effective reasoning effort level for this turn; null for the model default.</summary>
    public string? Effort { get; init; }

    /// <summary>
    /// The tool definitions to advertise in the <see cref="ChatRequest"/> for this turn, after
    /// applying any <see cref="TurnShape.AllowedTools"/> and <see cref="TurnShape.DeniedTools"/>
    /// restrictions from the session registry. For the tool-search case (where the advertised
    /// set changes per iteration), call <see cref="FilterDefinitions"/> on the tool-search output
    /// after each iteration instead of using this pre-computed list.
    /// </summary>
    public required IReadOnlyList<ToolDefinition> ToolDefinitions { get; init; }

    /// <summary>
    /// Case-insensitive set of tool names that are permitted this turn. <see langword="null"/>
    /// means no filter was applied and all registered tools are allowed. Used at execution time
    /// to enforce restrictions: the model may be advertised a filtered set but could still call
    /// a denied tool by name, so the check must happen at invocation too.
    /// </summary>
    private HashSet<string>? AllowedNames { get; init; }

    /// <summary>
    /// When the restriction was driven by <see cref="TurnShape.DeniedTools"/> alone (no
    /// AllowedTools filter was set), records the original denied list so that
    /// <see cref="ToToolRestrictionShape"/> can propagate a <c>DeniedTools</c>-only shape to
    /// child subagents rather than the fully-resolved <see cref="AllowedNames"/> set. A
    /// DeniedTools-only propagation preserves the child's unrestricted access to its own
    /// registry while still blocking the explicitly denied tools, achieving the monotonic
    /// "union denied" rule from the proposal. Null when AllowedTools was also active (the
    /// tighter AllowedNames intersection is then used instead).
    /// </summary>
    private IReadOnlyList<string>? DeniedOnlyInput { get; init; }

    /// <summary>The validated <c>tool_choice</c> value for this turn, or null for none.</summary>
    public string? ToolChoice { get; init; }

    /// <summary>
    /// Returns a <see cref="TurnShape"/> carrying only the tool restriction from this resolution,
    /// or <see langword="null"/> when no filter is active. Used by <c>SubagentHost</c> to
    /// propagate the parent turn's restriction to child subagents, keeping children monotonically
    /// at least as restricted as their parent.
    /// <para>
    /// Only the tool restriction crosses the parent–child boundary. System prompt, model, and
    /// effort overrides are per-parent-turn decisions specific to that turn's context (the
    /// parent's own role prompt, model budget, etc.) and must not bleed into a subagent that
    /// has its own system prompt (<see cref="Subagents.AgentSystemPrompt"/>) and context.
    /// </para>
    /// <para>
    /// Monotonicity is maintained in two ways:
    /// <list type="bullet">
    ///   <item>Deny-only parent: the same <see cref="TurnShape.DeniedTools"/> is forwarded,
    ///     so the child inherits the deny and retains unrestricted access to its own other tools
    ///     (union-denied rule).</item>
    ///   <item>AllowedTools parent: the resolved <see cref="AllowedNames"/> is forwarded as
    ///     <see cref="TurnShape.AllowedTools"/>, intersecting with the child's registry
    ///     (intersect-allowed rule).</item>
    /// </list>
    /// </para>
    /// </summary>
    internal TurnShape? ToToolRestrictionShape()
    {
        if (this.AllowedNames is null)
        {
            return null;
        }

        // Deny-only case: forward the original denied list so the child loses only those tools
        // and keeps everything else in its own registry.
        if (this.DeniedOnlyInput is not null)
        {
            return new TurnShape { DeniedTools = this.DeniedOnlyInput };
        }

        // AllowedTools case: forward the resolved allowed set; the child resolver will intersect
        // this with the child's registry, ensuring it cannot exceed the parent's permitted scope.
        return new TurnShape { AllowedTools = [.. this.AllowedNames] };
    }


    /// <summary>
    /// Returns <see langword="true"/> when the named tool is permitted to execute this turn.
    /// When no filter is active (null shape or no tool lists set) every tool is allowed.
    /// Matching is case-insensitive.
    /// </summary>
    public bool IsToolAllowed(string name) =>
        this.AllowedNames is null || this.AllowedNames.Contains(name);

    /// <summary>
    /// Filters <paramref name="definitions"/> to the subset allowed this turn. Use when
    /// tool-search is active and the advertised set is recomputed per iteration; pass the
    /// tool-search output here to get the shape-filtered result. When no filter is active,
    /// returns <paramref name="definitions"/> unchanged.
    /// </summary>
    public IReadOnlyList<ToolDefinition> FilterDefinitions(IReadOnlyList<ToolDefinition> definitions)
    {
        if (this.AllowedNames is null)
        {
            return definitions;
        }

        return [.. definitions.Where(d => this.AllowedNames.Contains(d.Name))];
    }

    internal static TurnShapeResolution FromDefaults(
        string systemPrompt,
        string model,
        string? effort,
        IReadOnlyList<ToolDefinition> toolDefinitions) =>
        new()
        {
            SystemPrompt = systemPrompt,
            Model = model,
            Effort = effort,
            ToolDefinitions = toolDefinitions,
            AllowedNames = null,
            ToolChoice = null,
        };

    internal static TurnShapeResolution Create(
        string systemPrompt,
        string model,
        string? effort,
        IReadOnlyList<ToolDefinition> toolDefinitions,
        HashSet<string>? allowedNames,
        string? toolChoice,
        IReadOnlyList<string>? deniedOnlyInput = null) =>
        new()
        {
            SystemPrompt = systemPrompt,
            Model = model,
            Effort = effort,
            ToolDefinitions = toolDefinitions,
            AllowedNames = allowedNames,
            ToolChoice = toolChoice,
            DeniedOnlyInput = deniedOnlyInput,
        };
}

/// <summary>
/// Pure resolver that computes the effective <see cref="TurnShapeResolution"/> for a single
/// agent turn by merging a <see cref="TurnShape"/> override onto session defaults. Has no I/O
/// and no dependency on <see cref="AgentLoop"/>, so it is fully unit-testable in isolation.
/// </summary>
internal static class TurnShapeResolver
{
    private static readonly HashSet<string> ValidToolChoices =
        new(["auto", "any", "none"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Merges <paramref name="shape"/> onto the session defaults and returns the effective
    /// values for one turn.
    /// </summary>
    /// <param name="sessionSystemPrompt">The session-wide system prompt.</param>
    /// <param name="sessionModel">The session-wide model identifier.</param>
    /// <param name="sessionEffort">The session-wide reasoning effort level, or null.</param>
    /// <param name="tools">The full session tool registry.</param>
    /// <param name="shape">
    /// The per-turn override, or null. A null shape and a shape with all properties null are
    /// treated identically: session defaults pass through unchanged.
    /// </param>
    /// <returns>The effective values to use when building a <see cref="ChatRequest"/>.</returns>
    public static TurnShapeResolution Resolve(
        string sessionSystemPrompt,
        string sessionModel,
        string? sessionEffort,
        ToolRegistry tools,
        TurnShape? shape)
    {
        ArgumentNullException.ThrowIfNull(tools);

        if (shape is null || shape.IsEmpty)
        {
            return TurnShapeResolution.FromDefaults(sessionSystemPrompt, sessionModel, sessionEffort, tools.Definitions);
        }

        // System prompt: replace, then append.
        var systemPrompt = shape.SystemPrompt ?? sessionSystemPrompt;
        if (shape.AppendSystemPrompt is not null)
        {
            systemPrompt = systemPrompt + "\n\n" + shape.AppendSystemPrompt;
        }

        // Model / Effort: override when non-null.
        var model = shape.Model ?? sessionModel;
        var effort = shape.Effort ?? sessionEffort;

        // ToolChoice: pass through validated value or null.
        string? toolChoice = null;
        if (shape.ToolChoice is { Length: > 0 } tc && ValidToolChoices.Contains(tc))
        {
            toolChoice = tc.ToLowerInvariant();
        }

        // Tool filtering: apply AllowedTools restriction then DeniedTools removal.
        // - AllowedTools null  → no restriction
        // - AllowedTools empty → no tools at all (empty ≠ null)
        // - DeniedTools        → always remove, denial wins when in both
        // Matching is case-insensitive; unrecognized names are silently ignored.
        if (shape.AllowedTools is not null || shape.DeniedTools is { Count: > 0 })
        {
            var allNames = tools.All.Select(t => t.Name);

            IEnumerable<string> effective;
            if (shape.AllowedTools is not null)
            {
                var allowedLookup = new HashSet<string>(shape.AllowedTools, StringComparer.OrdinalIgnoreCase);
                effective = allNames.Where(n => allowedLookup.Contains(n));
            }
            else
            {
                effective = allNames;
            }

            if (shape.DeniedTools is { Count: > 0 } denied)
            {
                var deniedLookup = new HashSet<string>(denied, StringComparer.OrdinalIgnoreCase);
                effective = effective.Where(n => !deniedLookup.Contains(n));
            }

            var allowedNames = new HashSet<string>(effective, StringComparer.OrdinalIgnoreCase);
            var filteredDefs = tools.Definitions
                .Where(d => allowedNames.Contains(d.Name))
                .ToList();

            // When only DeniedTools drove the filter (no AllowedTools), record the original
            // denied list so ToToolRestrictionShape() can propagate a deny-only shape to child
            // subagents. An AllowedTools-based filter does not set DeniedOnlyInput: the tighter
            // AllowedNames intersection is forwarded directly in that case.
            var deniedOnlyInput = shape.AllowedTools is null ? shape.DeniedTools : null;

            return TurnShapeResolution.Create(systemPrompt, model, effort, filteredDefs, allowedNames, toolChoice, deniedOnlyInput);
        }

        return TurnShapeResolution.Create(systemPrompt, model, effort, tools.Definitions, allowedNames: null, toolChoice);
    }
}
