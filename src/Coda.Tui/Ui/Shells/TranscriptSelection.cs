using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Shells;

/// <summary>A shell-local cell position: a global transcript row and a terminal cell column within it.</summary>
internal readonly record struct TranscriptCellPosition(int GlobalRow, int CellColumn);

/// <summary>
/// Models an inclusive text selection over global transcript rows. A selection only becomes active once
/// the active cell moves at least one cell away from the anchor, so a click that presses and releases in
/// place selects nothing. Endpoints are stored inclusive and normalized; <see cref="RangeForRow"/> and
/// <see cref="CopyText"/> convert them to half-open, ordered cell slices for highlighting and copying.
/// </summary>
internal sealed class TranscriptSelection
{
    private bool moved;

    internal TranscriptCellPosition Anchor { get; private set; }

    internal TranscriptCellPosition Active { get; private set; }

    internal bool HasSelection => this.moved;

    internal void Begin(TranscriptCellPosition anchor)
    {
        this.Anchor = Normalize(anchor);
        this.Active = this.Anchor;
        this.moved = false;
    }

    internal bool Update(TranscriptCellPosition active)
    {
        this.Active = Normalize(active);
        this.moved = this.Active != this.Anchor;
        return this.moved;
    }

    internal void Clear()
    {
        this.moved = false;
        this.Anchor = default;
        this.Active = default;
    }

    internal (int StartCell, int EndCellExclusive)? RangeForRow(int globalRow, int rowWidth)
    {
        if (!this.HasSelection)
        {
            return null;
        }

        var (start, end) = this.Ordered();
        if (globalRow < start.GlobalRow || globalRow > end.GlobalRow)
        {
            return null;
        }

        var width = Math.Max(0, rowWidth);
        if (start.GlobalRow == end.GlobalRow)
        {
            return (
                Math.Clamp(start.CellColumn, 0, width),
                Math.Clamp(end.CellColumn + 1, 0, width));
        }

        if (globalRow == start.GlobalRow)
        {
            return (Math.Clamp(start.CellColumn, 0, width), width);
        }

        if (globalRow == end.GlobalRow)
        {
            return (0, Math.Clamp(end.CellColumn + 1, 0, width));
        }

        return (0, width);
    }

    internal string CopyText(IReadOnlyList<TranscriptRow> rows)
    {
        var selected = new List<string>();
        foreach (var row in rows.OrderBy(row => row.GlobalRow))
        {
            var width = TerminalCellText.Width(row.Text);
            if (this.RangeForRow(row.GlobalRow, width) is not { } range)
            {
                continue;
            }

            selected.Add(TerminalCellText.SliceByCells(
                row.Text,
                range.StartCell,
                range.EndCellExclusive));
        }

        return string.Join('\n', selected);
    }

    internal (TranscriptCellPosition Start, TranscriptCellPosition End) Ordered()
    {
        var anchorBeforeActive =
            this.Anchor.GlobalRow < this.Active.GlobalRow ||
            (this.Anchor.GlobalRow == this.Active.GlobalRow &&
             this.Anchor.CellColumn <= this.Active.CellColumn);
        return anchorBeforeActive
            ? (this.Anchor, this.Active)
            : (this.Active, this.Anchor);
    }

    private static TranscriptCellPosition Normalize(TranscriptCellPosition value) =>
        new(Math.Max(0, value.GlobalRow), Math.Max(0, value.CellColumn));
}
