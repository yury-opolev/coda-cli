using System.Collections.Generic;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Rendering;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace Coda.Tui.Ui.Shells;

/// <summary>
/// A focus-free, host-neutral popup that renders the composer's current slash-command suggestions as a
/// compact, borderless list directly above the composer. It is driven entirely from the outside via
/// <see cref="SetSuggestions"/> (the suggestion list plus the selected index owned by
/// <see cref="Input.ComposerController"/>); it never handles keys itself, so all menu navigation stays in
/// the controller. Rows are plain text — a selection marker, the command name, and its summary — with no
/// ANSI or other control sequences; only a role attribute colors the selected row. The visible window is
/// bounded to <see cref="MaxVisibleOptions"/> rows and scrolls to keep the selection in view, and each row
/// is truncated to the view width so a narrow terminal never overflows.
/// </summary>
internal sealed class CommandCompletionView : View
{
    /// <summary>Maximum number of option rows shown at once; the list scrolls beyond this.</summary>
    internal const int MaxVisibleOptions = 8;

    private TuiTheme theme;

    private IReadOnlyList<ISlashCommand> suggestions = [];
    private int selectedIndex = -1;
    private int scrollOffset;

    public CommandCompletionView(TuiTheme? theme = null)
    {
        this.theme = theme ?? CodaThemes.Current.Tui;
        this.CanFocus = false;
        this.Visible = false;
    }

    internal void ApplyTheme(TuiTheme theme)
    {
        this.theme = theme ?? throw new ArgumentNullException(nameof(theme));
        this.SetNeedsDraw();
    }

    /// <summary>The suggestions currently displayed (empty when hidden).</summary>
    internal IReadOnlyList<ISlashCommand> Suggestions => this.suggestions;

    /// <summary>The selected suggestion index, or -1 when there is no selection.</summary>
    internal int SelectedIndex => this.selectedIndex;

    /// <summary>The first suggestion index rendered at the top of the scrolled window.</summary>
    internal int FirstVisibleIndex => this.scrollOffset;

    /// <summary>Whether any suggestions are currently offered.</summary>
    internal bool HasSuggestions => this.suggestions.Count > 0;

    /// <summary>Rows the menu wants to occupy (bounded by <see cref="MaxVisibleOptions"/>); 0 when hidden.</summary>
    internal int DesiredHeight => Math.Min(this.suggestions.Count, MaxVisibleOptions);

    internal void SetSuggestions(IReadOnlyList<ISlashCommand> suggestions, int selectedIndex)
    {
        this.suggestions = suggestions ?? [];
        this.selectedIndex = this.suggestions.Count == 0
            ? -1
            : Math.Clamp(selectedIndex, 0, this.suggestions.Count - 1);
        this.ScrollSelectionIntoView();
        this.SetNeedsDraw();
    }

    internal IReadOnlyList<string> RenderVisibleRows(int width)
    {
        if (this.suggestions.Count == 0)
        {
            return Array.Empty<string>();
        }

        var window = Math.Min(this.suggestions.Count, MaxVisibleOptions);
        var rows = new List<string>(window);
        for (var i = 0; i < window; i++)
        {
            var index = this.scrollOffset + i;
            if (index >= this.suggestions.Count)
            {
                break;
            }

            rows.Add(Fit(this.RowText(index), width));
        }

        return rows;
    }

    internal TgAttribute AttributeFor(bool selected, bool? trueColor = null)
    {
        var useTrueColor = trueColor ?? TuiTheme.SupportsTrueColor(this.App?.Driver);
        return selected
            ? new TgAttribute(
                TuiTheme.Resolve(this.theme.CompletionSelectedText, useTrueColor),
                TuiTheme.Resolve(this.theme.CompletionSelectedBackground, useTrueColor))
            : new TgAttribute(
                TuiTheme.Resolve(this.theme.CompletionNormal, useTrueColor),
                TuiTheme.Resolve(this.theme.Background, useTrueColor));
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (context is not null)
        {
            this.ClearViewport(context);
        }

        if (this.suggestions.Count == 0)
        {
            return true;
        }

        var width = Math.Max(1, this.Viewport.Width);
        var height = Math.Max(0, this.Viewport.Height);

        for (var row = 0; row < height; row++)
        {
            var index = this.scrollOffset + row;
            if (index >= this.suggestions.Count)
            {
                break;
            }

            this.SetAttribute(this.AttributeFor(index == this.selectedIndex));
            this.Move(0, row);
            this.AddStr(Fit(this.RowText(index), width));
        }

        return true;
    }

    private void ScrollSelectionIntoView()
    {
        if (this.suggestions.Count <= MaxVisibleOptions)
        {
            this.scrollOffset = 0;
            return;
        }

        if (this.selectedIndex < this.scrollOffset)
        {
            this.scrollOffset = this.selectedIndex;
        }
        else if (this.selectedIndex >= this.scrollOffset + MaxVisibleOptions)
        {
            this.scrollOffset = this.selectedIndex - MaxVisibleOptions + 1;
        }

        this.scrollOffset = Math.Clamp(this.scrollOffset, 0, this.suggestions.Count - MaxVisibleOptions);
    }

    private string RowText(int index)
    {
        var command = this.suggestions[index];
        var marker = index == this.selectedIndex ? "> " : "  ";
        // Prepend the skill glyph for skill-derived entries so they are visually
        // distinguishable from built-ins in the live menu (M3: marker at render time only).
        var summaryPrefix = command is SkillSlashCommand ? SkillSlashCommand.SkillMarker : string.Empty;
        var summary = string.IsNullOrWhiteSpace(command.Summary)
            ? (string.IsNullOrEmpty(summaryPrefix) ? string.Empty : "  " + summaryPrefix.TrimEnd())
            : "  " + summaryPrefix + command.Summary;
        return $"{marker}/{command.Name}{summary}";
    }

    private static string Fit(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var flattened = text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        return TerminalCellText.SliceByCells(flattened, 0, width);
    }
}
