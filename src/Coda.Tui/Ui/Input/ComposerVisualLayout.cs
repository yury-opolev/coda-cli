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

    public (int CursorIndex, int PreferredColumn) MoveVertical(
        int utf16Index,
        int delta,
        int? preferredColumn)
    {
        var current = this.PositionForIndex(utf16Index);
        var preferred = preferredColumn ?? current.Column;
        var targetRow = Math.Clamp(current.Row + Math.Sign(delta), 0, this.rows.Length - 1);
        return (this.IndexForPosition(targetRow, preferred), preferred);
    }
}
