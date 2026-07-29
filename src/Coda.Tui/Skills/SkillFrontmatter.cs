namespace Coda.Tui.Skills;

/// <summary>The result of parsing the YAML-subset frontmatter block from a SKILL.md file.</summary>
public sealed class SkillFrontmatter
{
    private static readonly IReadOnlyDictionary<string, string> EmptyDict =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether the content contained a valid, closed frontmatter block (<c>---…---</c>).</summary>
    public bool HasFrontmatter { get; init; }

    /// <summary>Value of the <c>name</c> field, or <see langword="null"/> if absent.</summary>
    public string? Name { get; init; }

    /// <summary>Value of the <c>description</c> field, or <see langword="null"/> if absent.</summary>
    public string? Description { get; init; }

    /// <summary>Value of the <c>when-to-use</c> (or <c>when_to_use</c>) field, or <see langword="null"/> if absent.</summary>
    public string? WhenToUse { get; init; }

    /// <summary>Value of the <c>argument-hint</c> (or <c>argument_hint</c>) field, or <see langword="null"/> if absent.</summary>
    public string? ArgumentHint { get; init; }

    /// <summary>Items from the <c>arguments</c> list field, or an empty list if absent.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// When <see langword="true"/>, this skill is excluded from the model-facing <c>skill</c> tool
    /// (its enum and description catalogue) but remains runnable by the user via <c>/skill</c>.
    /// Default <see langword="false"/>.
    /// </summary>
    public bool DisableModelInvocation { get; init; }

    /// <summary>
    /// When <see langword="false"/>, this skill is model-only: absent from <c>/skills</c> listing
    /// and rejected by <c>/skill &lt;name&gt;</c>, but present in the model-facing <c>skill</c> tool.
    /// Default <see langword="true"/>.
    /// </summary>
    public bool UserInvocable { get; init; } = true;

    /// <summary>
    /// Tools that the invoking turn pre-approves (skipping the permission prompt for those tools
    /// while the skill is active). The effective set is intersected with any hook-imposed
    /// allowlist and still respects denial lists — pre-approval cannot widen a hook restriction.
    /// Empty means no pre-approval declared.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Tools removed from the advertised pool for the invoking turn. Unioned with any
    /// hook-imposed denial list so the set of denied tools can only grow, never shrink.
    /// Empty means no additional tools are denied.
    /// </summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>
    /// Model override for the invoking turn. <c>"inherit"</c> (or null) means use the session
    /// default. When set and different from the hook-set model, the skill wins (last-write).
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Reasoning effort override for the invoking turn. <c>"inherit"</c> (or null) means use the
    /// session default. When set and different from the hook-set effort, the skill wins.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>
    /// How the skill body is executed. <see cref="SkillContextMode.Fork"/> runs the body in a
    /// subagent; the parent context receives only the subagent's final report.
    /// Defaults to <see cref="SkillContextMode.Inline"/>.
    /// </summary>
    public SkillContextMode ContextMode { get; init; } = SkillContextMode.Inline;

    /// <summary>
    /// The subagent type to use when <see cref="ContextMode"/> is <see cref="SkillContextMode.Fork"/>.
    /// Null means the general-purpose subagent.
    /// </summary>
    public string? Agent { get; init; }

    /// <summary>
    /// Glob patterns restricting which workspaces this skill is advertised to the model in.
    /// The skill is included in the model-facing <c>skill</c> tool only when the working directory
    /// matches at least one pattern. User invocation via <c>/skill</c> is never filtered.
    /// Empty means the skill is always advertised.
    /// </summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>
    /// Raw string values for any frontmatter key not modelled by this class. List values are
    /// serialized as newline-joined items. Preserves forward-compatibility: a skill authored for
    /// a future Coda version or another harness still loads cleanly.
    /// </summary>
    public IReadOnlyDictionary<string, string> UnknownFields { get; init; } = EmptyDict;

    /// <summary>The body text — content after the closing <c>---</c> delimiter, whitespace-trimmed.</summary>
    public string Body { get; init; } = string.Empty;
}
