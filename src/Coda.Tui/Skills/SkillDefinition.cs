namespace Coda.Tui.Skills;

/// <summary>The source layer from which a skill was loaded, in ascending precedence order.</summary>
public enum SkillOrigin
{
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
    /// Raw string values for frontmatter keys not modelled by this record. Preserved so a skill
    /// authored for a future Coda version or another harness still loads cleanly.
    /// </summary>
    public IReadOnlyDictionary<string, string> UnknownFields { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}
