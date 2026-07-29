using Coda.Tui.Skills;

namespace Coda.Tui.Ui.Skills;

/// <summary>The two panes of the <c>/skills</c> browser overlay.</summary>
internal enum SkillBrowserView
{
    /// <summary>The scrollable list of discovered skills.</summary>
    List,

    /// <summary>The single-skill detail pane (frontmatter, source path, argument hint).</summary>
    Detail,
}

/// <summary>A resolved key action within the <c>/skills</c> browser.</summary>
internal enum SkillBrowserCommand
{
    /// <summary>No action.</summary>
    None,

    /// <summary>Close the overlay.</summary>
    Close,

    /// <summary>Move the selection up one row.</summary>
    MoveUp,

    /// <summary>Move the selection down one row.</summary>
    MoveDown,

    /// <summary>Move the selection up one page.</summary>
    PageUp,

    /// <summary>Move the selection down one page.</summary>
    PageDown,

    /// <summary>Move the selection to the first row.</summary>
    MoveToStart,

    /// <summary>Move the selection to the last row.</summary>
    MoveToEnd,

    /// <summary>Open the detail pane for the selected skill.</summary>
    OpenDetail,

    /// <summary>Return from the detail pane to the list.</summary>
    ReturnToList,

    /// <summary>Toggle the selected skill's enabled state (reserved; skills are frontmatter-driven).</summary>
    ToggleEnabled,

    /// <summary>Reload the skill set from disk.</summary>
    Reload,
}

/// <summary>
/// The immutable state snapshot for the skill browser. Mutated only inside
/// <see cref="SkillBrowserController"/>'s lock; the overlay always renders a reference-copied
/// snapshot so a concurrent reload cannot corrupt a render in progress.
/// </summary>
internal sealed record SkillBrowserState(
    IReadOnlyList<SkillDefinition> Skills,
    string? SelectedName,
    SkillBrowserView View,
    SkillDefinition? Detail,
    string? StatusMessage,
    bool ActionBusy)
{
    /// <summary>Empty initial state (no skills, no selection, list view).</summary>
    public static readonly SkillBrowserState Empty =
        new([], null, SkillBrowserView.List, null, null, false);

    /// <summary>Returns a copy with the skills replaced, preserving selection where possible.</summary>
    public SkillBrowserState WithSkills(IReadOnlyList<SkillDefinition> skills)
    {
        var newSel = this.SelectedName is not null && skills.Any(s => s.Name == this.SelectedName)
            ? this.SelectedName
            : skills.Count > 0 ? skills[0].Name : null;
        return this with { Skills = skills, SelectedName = newSel };
    }

    /// <summary>Returns a copy with the selection moved by <paramref name="delta"/> (clamped to bounds).</summary>
    public SkillBrowserState MoveSelection(int delta)
    {
        if (this.Skills.Count == 0)
        {
            return this;
        }

        var idx = IndexOf(this.Skills, this.SelectedName);
        if (idx < 0)
        {
            idx = 0;
        }

        var next = Math.Clamp(idx + delta, 0, this.Skills.Count - 1);
        return this with { SelectedName = this.Skills[next].Name };
    }

    /// <summary>Returns a copy switched to the detail pane for the current selection.</summary>
    public SkillBrowserState OpenDetail()
    {
        if (this.SelectedName is null)
        {
            return this;
        }

        var detail = this.Skills.FirstOrDefault(s => s.Name == this.SelectedName);
        return detail is null
            ? this
            : this with { View = SkillBrowserView.Detail, Detail = detail };
    }

    /// <summary>Returns a copy switched back to the list pane.</summary>
    public SkillBrowserState ReturnToList() =>
        this with { View = SkillBrowserView.List, Detail = null };

    private static int IndexOf(IReadOnlyList<SkillDefinition> skills, string? name)
    {
        for (var i = 0; i < skills.Count; i++)
        {
            if (skills[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }
}
