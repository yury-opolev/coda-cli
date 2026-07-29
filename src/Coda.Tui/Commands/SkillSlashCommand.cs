using Coda.Tui.Repl;
using Coda.Tui.Skills;

namespace Coda.Tui.Commands;

/// <summary>
/// A first-class slash command backed by a user-invocable <see cref="SkillDefinition"/>.
/// Invoking <c>/&lt;name&gt; [args…]</c> behaves identically to <c>/skill &lt;name&gt; [args…]</c>,
/// including the Phase 0 opt-in argument-substitution rule: substitution runs only when at
/// least one argument is supplied at invocation time, or the skill itself declares named
/// <c>arguments</c> in its frontmatter.
/// </summary>
public sealed class SkillSlashCommand : ISlashCommand
{
    /// <summary>
    /// Unicode bullet prepended to the summary so skill-derived entries are visually
    /// distinguishable from built-ins in the completion menu and <c>/help</c> listing.
    /// </summary>
    public const string SkillMarker = "◆ ";

    private readonly SkillDefinition skill;

    /// <summary>Initializes a new instance backed by <paramref name="skill"/>.</summary>
    public SkillSlashCommand(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        this.skill = skill;
    }

    /// <inheritdoc/>
    public string Name => this.skill.Name;

    /// <inheritdoc/>
    public IReadOnlyList<string> Aliases => [];

    /// <inheritdoc/>
    /// <remarks>
    /// Returns the skill's description and <c>argument-hint</c> as a dim suffix so the
    /// completion menu shows expected arguments alongside the description. The
    /// <see cref="SkillMarker"/> glyph is prepended by UI renderers at render time, not here,
    /// so the ranking heuristic and machine-readable JSON output are unaffected.
    /// </remarks>
    public string Summary
    {
        get
        {
            var description = string.IsNullOrWhiteSpace(this.skill.Description)
                ? string.Empty
                : this.skill.Description;
            var hint = this.skill.ArgumentHint is { Length: > 0 }
                ? "  " + this.skill.ArgumentHint
                : string.Empty;
            return description + hint;
        }
    }

    /// <inheritdoc/>
    public CommandHelp Help => new(
        Usage: this.skill.ArgumentHint is { Length: > 0 }
            ? $"/{this.skill.Name} {this.skill.ArgumentHint}"
            : $"/{this.skill.Name}",
        Description: string.IsNullOrWhiteSpace(this.skill.Description)
            ? null
            : this.skill.Description,
        Examples: [$"/{this.skill.Name}"]);

    /// <inheritdoc/>
    public Task<CommandResult> ExecuteAsync(
        CommandContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var body = SkillArgumentBinder.BindOptIn(this.skill, args);
        return Task.FromResult(CommandResult.RunPrompt(body));
    }
}
