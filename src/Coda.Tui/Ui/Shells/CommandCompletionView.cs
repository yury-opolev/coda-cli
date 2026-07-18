using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Coda.Tui.Repl;
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

    private IReadOnlyList<ISlashCommand> suggestions = [];
    private int selectedIndex = -1;
    private int scrollOffset;

    public CommandCompletionView()
    {
        this.CanFocus = false;
        this.Visible = false;
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

    /// <summary>
    /// Replaces the displayed suggestions and selected index. The selection is clamped into range and the
    /// scroll window is adjusted so the selection stays visible.
    /// </summary>
    internal void SetSuggestions(IReadOnlyList<ISlashCommand> suggestions, int selectedIndex)
    {
        this.suggestions = suggestions ?? [];
        this.selectedIndex = this.suggestions.Count == 0
            ? -1
            : Math.Clamp(selectedIndex, 0, this.suggestions.Count - 1);
        this.ScrollSelectionIntoView();
        this.SetNeedsDraw();
    }

    /// <summary>
    /// The plain-text rows for the current scrolled window, each truncated to <paramref name="width"/>
    /// display cells. Exposed so the shell (and tests) can render/inspect the menu without a live draw.
    /// </summary>
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

    /// <inheritdoc />
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

        var normal = this.GetAttributeForRole(Terminal.Gui.Drawing.VisualRole.Normal);
        var selected = new TgAttribute(normal.Background, normal.Foreground);
        var width = Math.Max(1, this.Viewport.Width);
        var height = Math.Max(0, this.Viewport.Height);

        for (var row = 0; row < height; row++)
        {
            var index = this.scrollOffset + row;
            if (index >= this.suggestions.Count)
            {
                break;
            }

            this.SetAttribute(index == this.selectedIndex ? selected : normal);
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
        var summary = string.IsNullOrWhiteSpace(command.Summary) ? string.Empty : "  " + command.Summary;
        return $"{marker}/{command.Name}{summary}";
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="width"/> display cells without
    /// splitting a grapheme cluster, collapsing embedded control whitespace to spaces first. Grapheme
    /// count approximates cell width, which is exact for the ASCII command names/summaries in practice and
    /// otherwise clipped by the driver, so a row can never overflow the view.
    /// </summary>
    private static string Fit(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var flattened = text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        var elements = StringInfo.GetTextElementEnumerator(flattened);
        var builder = new StringBuilder();
        var used = 0;
        while (elements.MoveNext())
        {
            if (used >= width)
            {
                break;
            }

            builder.Append((string)elements.Current);
            used++;
        }

        return builder.ToString();
    }
}
