namespace Coda.Tui.Skills;

/// <summary>The source layer from which a skill was loaded, in ascending precedence order.</summary>
public enum SkillOrigin
{
    /// <summary>Loaded from a foreign ecosystem path (.agents/skills/, .claude/agents/, etc.), read-only.</summary>
    Foreign,

    /// <summary>Loaded from the Claude CLI's skill directory (<c>~/.claude/skills</c>), read-only.</summary>
    Claude,

    /// <summary>Loaded from the user-level Coda skill directory (<c>~/.coda/skills</c>).</summary>
    User,

    /// <summary>Loaded from a plugin's bundled skill directory.</summary>
    Plugin,

    /// <summary>Loaded from the project-level skill directory (<c>.coda/skills</c>).</summary>
    Project,
}

/// <summary>A discovered skill with its name, description, body prompt, and metadata.</summary>
public sealed record SkillDefinition(string Name, string Description, string Body)
{
    /// <summary>
    /// Extra description for model routing — appended to <see cref="Description"/> when advertising
    /// the skill to the model. Null when the <c>when-to-use</c> field was absent from the frontmatter.
    /// </summary>
    public string? WhenToUse { get; init; }

    /// <summary>
    /// Argument completion hint shown in <c>/skills</c> listings (e.g. <c>&lt;filename&gt; [options]</c>).
    /// Null when the <c>argument-hint</c> field was absent from the frontmatter.
    /// </summary>
    public string? ArgumentHint { get; init; }

    /// <summary>Declared positional argument names used for <c>$name</c> substitution in the body.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Full path to the <c>SKILL.md</c> file from which this skill was loaded, or <see langword="null"/> if not loaded from disk.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The source layer that provided this skill.</summary>
    public SkillOrigin Origin { get; init; }

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
    /// Tools pre-approved for the invoking turn (skipping the permission prompt). Does not widen
    /// any hook-imposed restriction — denial lists always beat allowlists.
    /// Empty means no pre-approval declared.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Tools removed from the pool for the invoking turn. Unioned with hook-imposed denial lists.
    /// Empty means no additional tools are denied.
    /// </summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>
    /// Model override for the invoking turn. <see langword="null"/> means use the session default
    /// (<c>"inherit"</c> in frontmatter is normalised to null by the parser).
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Reasoning effort override for the invoking turn. <see langword="null"/> means use the
    /// session default (<c>"inherit"</c> in frontmatter is normalised to null by the parser).
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>How the skill body is executed — inline (default) or in a forked subagent.</summary>
    public SkillContextMode ContextMode { get; init; } = SkillContextMode.Inline;

    /// <summary>
    /// The subagent type to use when <see cref="ContextMode"/> is <see cref="SkillContextMode.Fork"/>.
    /// <see langword="null"/> means the general-purpose subagent.
    /// </summary>
    public string? AgentType { get; init; }

    /// <summary>
    /// Glob patterns that restrict which workspaces this skill is advertised to the model in.
    /// User invocation via <c>/skill</c> is never filtered.
    /// Empty means the skill is always advertised.
    /// </summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>
    /// Raw string values for frontmatter keys not modelled by this record. Preserved so a skill
    /// authored for a future Coda version or another harness still loads cleanly.
    /// </summary>
    public IReadOnlyDictionary<string, string> UnknownFields { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}
