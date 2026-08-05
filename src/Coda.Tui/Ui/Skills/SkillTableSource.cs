using System.Collections.Immutable;
using Coda.Tui.Skills;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Skills;

/// <summary>
/// An <see cref="ITableSource"/> projection over a snapshot of <see cref="SkillDefinition"/>s that
/// feeds the <see cref="TableView"/> in the skills browser's list pane.
///
/// <para>This type holds no I/O and no Terminal.Gui driver dependency — it can be created and asserted
/// in unit tests without initializing an application. Column order: Status, Name, Origin,
/// Description.</para>
/// </summary>
internal sealed class SkillTableSource : ITableSource
{
    private static readonly string[] ColumnNamesArray = ["Status", "Name", "Origin", "Description"];

    private readonly IReadOnlyList<SkillDefinition> skills;
    private readonly StatusGlyphs glyphs;

    public SkillTableSource(IReadOnlyList<SkillDefinition> skills, StatusGlyphs glyphs)
    {
        this.skills = skills ?? [];
        this.glyphs = glyphs ?? StatusGlyphs.Unicode;
    }

    /// <inheritdoc/>
    public int Columns => ColumnNamesArray.Length;

    /// <inheritdoc/>
    public int Rows => this.skills.Count;

    /// <inheritdoc/>
    public string[] ColumnNames => ColumnNamesArray;

    /// <inheritdoc/>
    public object this[int row, int col]
    {
        get
        {
            var skill = this.skills[row];
            return col switch
            {
                0 => this.glyphs[GetState(skill)],
                1 => TerminalTextSanitizer.SanitizeSingleLine(skill.Name),
                2 => skill.Origin.ToString().ToLowerInvariant(),
                3 => TerminalTextSanitizer.SanitizeSingleLine(skill.Description),
                _ => string.Empty,
            };
        }
    }

    /// <summary>
    /// Maps a <see cref="SkillDefinition"/> to a <see cref="BrowserItemState"/>.
    ///
    /// <para>Mapping: <c>DisableModelInvocation == true</c> → <see cref="BrowserItemState.Idle"/>
    /// (the skill is discovered but its model-invocation path is switched off); otherwise →
    /// <see cref="BrowserItemState.Healthy"/>.</para>
    /// </summary>
    public static BrowserItemState GetState(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return skill.DisableModelInvocation ? BrowserItemState.Idle : BrowserItemState.Healthy;
    }

    /// <summary>Returns the <see cref="SkillDefinition"/> at <paramref name="rowIndex"/>.</summary>
    public SkillDefinition SkillAt(int rowIndex) => this.skills[rowIndex];
}
