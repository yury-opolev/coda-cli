using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Text;

namespace Coda.Tui.Ui.Host;

/// <summary>
/// Retains the cell grid most recently presented to the terminal so that redundant repaints can be
/// suppressed.
/// </summary>
/// <remarks>
/// Terminal.Gui marks a cell dirty whenever a view writes to it, even when the value written is identical
/// to what the cell already held. Because every view redraws on every frame, the entire screen is marked
/// dirty and retransmitted for a single keystroke. Comparing against the previously presented grid restores
/// the distinction between "written" and "actually changed", leaving Terminal.Gui's own encoder to emit the
/// result. Only dirty flags are ever cleared, never set, so an unrecognised situation degrades to a full
/// repaint rather than to a stale screen.
/// </remarks>
internal sealed class PresentedFrame
{
    // Terminal.Gui allocates a new Contents array whenever the buffer is cleared, including on forced
    // redraws that also physically erase the terminal, and does so without changing the dimensions.
    // Comparing array identity detects that reallocation; a dimensions-only check would not, and the
    // resulting diff against a stale baseline would conclude nothing changed and leave the screen blank.
    private Cell[,]? retainedContentsReference;

    // Scratch buffers are retained between frames because this runs per keystroke; reallocating them
    // each time would put a multi-hundred-kilobyte allocation on the hot path.
    private FrameCell[,]? cells;
    private bool[,]? equalScratch;
    private bool[,]? keepDirtyScratch;
    private string?[,]? urlCache;
    private bool urlCacheValid;

    private int cols;
    private int rows;

    /// <summary>
    /// Clears dirty flags on cells unchanged from the previous frame and recomputes dirty lines.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when there is no compatible retained frame and the buffer must be written in
    /// full; in that case, no flags are modified.
    /// </returns>
    public bool SuppressUnchangedCells(IOutputBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var contents = buffer.Contents;
        if (cells is null
            || contents is null
            || buffer.Cols != cols
            || buffer.Rows != rows
            || contents.GetLength(0) < rows
            || contents.GetLength(1) < cols
            // Rejects a buffer whose backing array was replaced at identical dimensions.
            // Only identity is inspected; cell data is always read from the deep copy.
            || !ReferenceEquals(contents, retainedContentsReference))
        {
            return false;
        }

        EnsureScratchArrays(rows, cols);
        Array.Clear(keepDirtyScratch!);  // the wide-glyph pass only ever sets true, so it must start clear
        urlCacheValid = false;

        // Equality covers everything that determines a cell's appearance: grapheme, attribute and URL.
        // GetCellUrl takes (col, row) — the inverse of Contents[row, col].
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var cell = contents[row, col];
                var presented = cells[row, col];
                var url = NormalizeUrl(buffer.GetCellUrl(col, row));
                urlCache![row, col] = url;  // reused by Adopt so each cell's URL is fetched once per frame
                equalScratch![row, col] = StringComparer.Ordinal.Equals(cell.Grapheme, presented.Grapheme)
                    && Nullable.Equals(cell.Attribute, presented.Attribute)
                    && StringComparer.Ordinal.Equals(url, presented.Url);  // both sides already normalised
            }
        }

        urlCacheValid = true;

        // A two-column grapheme occupies a lead cell plus a continuation cell, and the pair must be
        // repainted as a unit. Equality is tested before wideness because measuring a grapheme's width
        // allocates, so it is worth doing only next to a cell that actually changed.
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols - 1; col++)
            {
                if (!equalScratch![row, col] || !equalScratch[row, col + 1])
                {
                    if (IsWide(contents[row, col].Grapheme) || cells[row, col].IsWide)
                    {
                        keepDirtyScratch![row, col] = true;
                        keepDirtyScratch[row, col + 1] = true;
                    }
                }
            }
        }

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                if (contents[row, col].IsDirty && equalScratch![row, col] && !keepDirtyScratch![row, col])
                {
                    contents[row, col].IsDirty = false;
                }
            }
        }

        var dirtyLines = buffer.DirtyLines;
        if (dirtyLines is null)
        {
            return true;
        }

        for (var row = 0; row < rows && row < dirtyLines.Length; row++)
        {
            if (!dirtyLines[row])
            {
                continue;
            }

            var hasDirtyCell = false;
            for (var col = 0; col < cols; col++)
            {
                if (contents[row, col].IsDirty)
                {
                    hasDirtyCell = true;
                    break;
                }
            }

            if (!hasDirtyCell)
            {
                dirtyLines[row] = false;
            }
        }

        return true;
    }

    /// <summary>Records the buffer grid as the newly presented frame baseline.</summary>
    public void Adopt(IOutputBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var contents = buffer.Contents;
        if (contents is null
            || buffer.Cols < 0
            || buffer.Rows < 0
            || contents.GetLength(0) < buffer.Rows
            || contents.GetLength(1) < buffer.Cols)
        {
            Invalidate();
            return;
        }

        var newRows = buffer.Rows;
        var newCols = buffer.Cols;

        // A cell still dirty after the write was never transmitted, because Terminal.Gui clears the flag
        // on each cell it emits. Such cells keep their previous baseline so the next frame repaints them
        // rather than silently accepting content the terminal never received. This is only possible when
        // a compatible previous frame exists to fall back to.
        var hasPrevFrame = cells is not null && rows == newRows && cols == newCols;

        // The preceding suppression pass, when it ran, already resolved every cell's URL.
        var useUrlCache = urlCacheValid
            && urlCache is not null
            && urlCache.GetLength(0) >= newRows
            && urlCache.GetLength(1) >= newCols;
        urlCacheValid = false;

        if (!hasPrevFrame)
        {
            cells = new FrameCell[newRows, newCols];
        }

        for (var row = 0; row < newRows; row++)
        {
            for (var col = 0; col < newCols; col++)
            {
                var cell = contents[row, col];

                if (cell.IsDirty && hasPrevFrame)
                {
                    continue;
                }

                var url = useUrlCache ? urlCache![row, col] : NormalizeUrl(buffer.GetCellUrl(col, row));
                cells![row, col] = new FrameCell(cell.Grapheme, cell.Attribute, url, IsWide(cell.Grapheme));
            }
        }

        retainedContentsReference = contents;
        cols = newCols;
        rows = newRows;
    }

    /// <summary>Drops the retained frame so the next frame is written in full.</summary>
    public void Invalidate()
    {
        cells = null;
        retainedContentsReference = null;
        cols = 0;
        rows = 0;
        urlCacheValid = false;
    }

    private void EnsureScratchArrays(int neededRows, int neededCols)
    {
        if (equalScratch is null || equalScratch.GetLength(0) < neededRows || equalScratch.GetLength(1) < neededCols)
        {
            equalScratch = new bool[neededRows, neededCols];
        }

        if (keepDirtyScratch is null || keepDirtyScratch.GetLength(0) < neededRows || keepDirtyScratch.GetLength(1) < neededCols)
        {
            keepDirtyScratch = new bool[neededRows, neededCols];
        }

        if (urlCache is null || urlCache.GetLength(0) < neededRows || urlCache.GetLength(1) < neededCols)
        {
            urlCache = new string?[neededRows, neededCols];
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="grapheme"/> occupies more than one terminal column.
    /// </summary>
    private static bool IsWide(string? grapheme)
    {
        if (grapheme is null || grapheme.Length == 0)
        {
            return false;
        }

        // Measuring width performs grapheme segmentation and allocates, so the single-byte ASCII case
        // that dominates ordinary text is answered directly.
        if (grapheme.Length == 1 && grapheme[0] < 0x80)
        {
            return false;
        }

        return grapheme.GetColumns() > 1;
    }

    private static string? NormalizeUrl(string? url)
        => string.IsNullOrEmpty(url) ? null : url;

    private readonly record struct FrameCell(
        string? Grapheme,
        Terminal.Gui.Drawing.Attribute? Attribute,
        string? Url,
        bool IsWide);
}
