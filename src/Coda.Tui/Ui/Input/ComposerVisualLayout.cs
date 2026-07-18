using System.Collections.Immutable;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Input;

/// <summary>A caret position in the wrapped composer grid: its visual row and display column.</summary>
internal readonly record struct ComposerVisualPosition(int Row, int Column);

/// <summary>
/// The grapheme- and cell-aware visual layout of the composer text. It projects the UTF-16 source onto
/// the wrapped rows produced by <see cref="TerminalCellText.Wrap"/> and provides the single seam that maps
/// UTF-16 indices to visual positions and back, including vertical movement that follows visual rows
/// (explicit newlines and soft wraps alike) while preserving a preferred display column.
/// </summary>
internal sealed class ComposerVisualLayout
{
    private readonly string text;
    private readonly ImmutableArray<WrappedCellRow> rows;

    private ComposerVisualLayout(string text, ImmutableArray<WrappedCellRow> rows)
    {
        this.text = text;
        this.rows = rows;
    }

    public int VisualLineCount => this.rows.Length;

    public static ComposerVisualLayout Create(string text, int width)
    {
        text ??= string.Empty;
        return new ComposerVisualLayout(text, TerminalCellText.Wrap(text, Math.Max(1, width)));
    }

    public ComposerVisualPosition PositionForIndex(int utf16Index)
    {
        var index = Math.Clamp(utf16Index, 0, this.text.Length);
        for (var row = 0; row < this.rows.Length; row++)
        {
            var current = this.rows[row];

            // The index falls before this row's first source index. That only happens when it lands on
            // whitespace dropped at a wrap boundary (a run consumed at the break plus leading whitespace
            // skipped on the next row). Snap it deterministically to the end of the preceding visual row so
            // it never falls through to the final row; if there is no preceding row snap to this row start.
            if (index < current.StartIndex)
            {
                return row == 0
                    ? new ComposerVisualPosition(0, 0)
                    : new ComposerVisualPosition(row - 1, this.rows[row - 1].CellWidth);
            }

            if (index > current.EndIndex)
            {
                continue;
            }

            // Disambiguate a hard-wrap seam where no boundary whitespace was dropped: the shared index is
            // both this row's exclusive EndIndex and the next row's inclusive StartIndex. Bind it to the
            // continuation row's first column so the caret sits at the start of the wrapped text instead of
            // past the end of the preceding row, which otherwise stalls vertical navigation on that seam.
            // Explicit newline and whitespace boundaries drop or consume a character, so the next row's
            // StartIndex advances past EndIndex and this disambiguation does not apply to them.
            if (index == current.EndIndex
                && row + 1 < this.rows.Length
                && this.rows[row + 1].StartIndex == index)
            {
                continue;
            }

            var column = 0;
            foreach (var element in current.Elements)
            {
                if (element.Utf16Start >= index)
                {
                    break;
                }

                column += element.CellWidth;
            }

            return new ComposerVisualPosition(row, column);
        }

        var last = this.rows[^1];
        return new ComposerVisualPosition(this.rows.Length - 1, last.CellWidth);
    }

    public int IndexForPosition(int row, int displayColumn)
    {
        var current = this.rows[Math.Clamp(row, 0, this.rows.Length - 1)];
        var column = Math.Max(0, displayColumn);
        foreach (var element in current.Elements)
        {
            var end = element.CellStart + Math.Max(1, element.CellWidth);
            if (column < end)
            {
                return element.Utf16Start;
            }
        }

        return current.EndIndex;
    }

    /// <summary>
    /// Moves the caret vertically by <paramref name="delta"/>. Only the sign of <paramref name="delta"/>
    /// matters: the caret moves exactly one visual row in that direction (magnitudes beyond one are treated
    /// as one), clamped to the first and last rows. A zero delta is a no-op that returns the caret's current
    /// index and preferred column unchanged rather than snapping horizontally to the preferred column on the
    /// same row. The preferred display column is carried across calls so a run of vertical moves tracks the
    /// same column even over shorter rows.
    /// </summary>
    public (int CursorIndex, int PreferredColumn) MoveVertical(
        int utf16Index,
        int delta,
        int? preferredColumn)
    {
        var current = this.PositionForIndex(utf16Index);
        var preferred = preferredColumn ?? current.Column;
        if (delta == 0)
        {
            return (Math.Clamp(utf16Index, 0, this.text.Length), preferred);
        }

        var targetRow = Math.Clamp(current.Row + Math.Sign(delta), 0, this.rows.Length - 1);
        return (this.IndexForPosition(targetRow, preferred), preferred);
    }
}
