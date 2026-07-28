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
    /// Raw string values for any frontmatter key not modelled by this class. List values are
    /// serialized as newline-joined items. Preserves forward-compatibility: a skill authored for
    /// a future Coda version or another harness still loads cleanly.
    /// </summary>
    public IReadOnlyDictionary<string, string> UnknownFields { get; init; } = EmptyDict;

    /// <summary>The body text — content after the closing <c>---</c> delimiter, whitespace-trimmed.</summary>
    public string Body { get; init; } = string.Empty;
}
