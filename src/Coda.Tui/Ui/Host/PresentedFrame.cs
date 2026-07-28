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

    // Per-row flag set during Adopt: true when any cell in that row had a non-null URL in the
    // baseline. Lets SuppressUnchangedCells skip the lock-bearing GetCellUrl for rows where
    // neither the baseline nor the current frame can contain a URL.
    private bool[]? baselineRowHasUrl;

    private int cols;
    private int rows;

    /// <summary>
    /// Clears dirty flags on cells unchanged from the previous frame and recomputes dirty lines.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when there is no compatible retained frame and suppression is
    /// disabled; no flags are modified, and <see cref="SyncDirtyLines"/> is not called either.
    /// Terminal.Gui's write loop still skips rows whose <c>DirtyLines</c> entry is false, so a
    /// disabled-suppression frame is not guaranteed to be a full write.
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

        // Read DirtyLines once before the loops; it drives both the URL-skip optimisation and
        // the dirty-line recomputation pass at the end.
        var dirtyLines = buffer.DirtyLines;

        // Equality covers everything that determines a cell's appearance: grapheme, attribute and URL.
        // GetCellUrl takes (col, row) — the inverse of Contents[row, col].
        for (var row = 0; row < rows; row++)
        {
            // Only call GetCellUrl (which acquires the buffer's lock and does dictionary lookups)
            // for rows that might carry a URL: rows written this frame (DirtyLines[row] == true)
            // or rows whose retained baseline already held a URL. For all other rows, the URL is
            // null on both sides and the comparison is trivially equal without the lookup.
            var rowNeedsUrlCheck = dirtyLines is null
                || (row < dirtyLines.Length && dirtyLines[row])
                || (baselineRowHasUrl is not null && row < baselineRowHasUrl.Length && baselineRowHasUrl[row]);

            for (var col = 0; col < cols; col++)
            {
                var cell = contents[row, col];
                var presented = cells[row, col];
                string? url;
                if (rowNeedsUrlCheck)
                {
                    url = NormalizeUrl(buffer.GetCellUrl(col, row));
                }
                else
                {
                    url = null;
                }
                urlCache![row, col] = url;  // reused by Adopt so each cell's URL is fetched once per frame
                // A cell whose baseline is Unknown (Known == false) was never confirmed as
                // presented; it must not be suppressed regardless of apparent content equality.
                equalScratch![row, col] = presented.Known
                    && StringComparer.Ordinal.Equals(cell.Grapheme, presented.Grapheme)
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

        SyncDirtyLines(buffer);
        return true;
    }

    /// <summary>
    /// Re-derives every <c>DirtyLines</c> entry from the per-cell dirty flags of that row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Terminal.Gui raises <c>DirtyLines[row]</c> only from <c>AddGrapheme</c> — an actual text
    /// write — and never lowers it again. <c>FillRect</c>, the path behind
    /// <c>View.ClearViewport</c>, marks the individual cells dirty but leaves the line flag
    /// alone. Lowering a line flag is therefore a one-way door in stock Terminal.Gui: a row that
    /// is subsequently only blanked, never written with text, would be skipped by
    /// <c>OutputBase.Write</c> for the rest of the session, leaving whatever was last drawn
    /// there — the body of a dialog that has since closed, for instance — permanently on screen.
    /// </para>
    /// <para>
    /// Recomputing in both directions on every frame removes that hazard: the flag becomes a
    /// plain summary of the row rather than accumulated history, so lowering it is always
    /// reversible and no dirty cell can hide inside a row the write loop skips.
    /// </para>
    /// </remarks>
    public static void SyncDirtyLines(IOutputBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        var contents = buffer.Contents;
        var dirtyLines = buffer.DirtyLines;
        if (contents is null || dirtyLines is null)
        {
            return;
        }

        var rows = Math.Min(Math.Min(buffer.Rows, contents.GetLength(0)), dirtyLines.Length);
        var cols = Math.Min(buffer.Cols, contents.GetLength(1));

        for (var row = 0; row < rows; row++)
        {
            var hasDirtyCell = false;
            for (var col = 0; col < cols; col++)
            {
                if (contents[row, col].IsDirty)
                {
                    hasDirtyCell = true;
                    break;
                }
            }

            dirtyLines[row] = hasDirtyCell;
        }
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

        if (cells is null || rows != newRows || cols != newCols)
        {
            cells = new FrameCell[newRows, newCols];
        }

        // The preceding suppression pass, when it ran, already resolved every cell's URL.
        var useUrlCache = urlCacheValid
            && urlCache is not null
            && urlCache.GetLength(0) >= newRows
            && urlCache.GetLength(1) >= newCols;
        urlCacheValid = false;

        // Reset per-row URL tracking; it will be rebuilt from the cells adopted below.
        if (baselineRowHasUrl is null || baselineRowHasUrl.Length < newRows)
        {
            baselineRowHasUrl = new bool[newRows];
        }
        else
        {
            Array.Clear(baselineRowHasUrl, 0, newRows);
        }

        var dirtyLines = buffer.DirtyLines;

        for (var row = 0; row < newRows; row++)
        {
            for (var col = 0; col < newCols; col++)
            {
                var cell = contents[row, col];

                if (cell.IsDirty)
                {
                    if (cells![row, col].Known)
                    {
                        // A confirmed previous value exists. Retain it so the next frame can
                        // detect whether the content changed relative to what the terminal shows.
                        if (cells[row, col].Url is not null)
                        {
                            baselineRowHasUrl![row] = true;
                        }

                        continue;
                    }

                    // No confirmed previous value. Adopt only if the row is known to have been
                    // emitted: when DirtyLines tracking says the row was not dirty, the write
                    // loop skipped it entirely (e.g. a FillRect-only draw), and the cell never
                    // reached the terminal — leave it Unknown so future frames keep it dirty.
                    var rowWasEmitted = dirtyLines is null
                        || (row < dirtyLines.Length && dirtyLines[row]);
                    if (!rowWasEmitted)
                    {
                        continue;  // cell is Unknown; default(FrameCell) already in place
                    }

                    // Row was in the dirty set; fall through to adopt with Known = true.
                }

                var url = useUrlCache ? urlCache![row, col] : NormalizeUrl(buffer.GetCellUrl(col, row));
                cells![row, col] = new FrameCell(cell.Grapheme, cell.Attribute, url, IsWide(cell.Grapheme), Known: true);
                if (url is not null)
                {
                    baselineRowHasUrl![row] = true;
                }
            }
        }

        retainedContentsReference = contents;
        cols = newCols;
        rows = newRows;
    }

    /// <summary>
    /// Drops the retained frame so the next <see cref="SuppressUnchangedCells"/> call returns
    /// <see langword="false"/>, disabling cell-level suppression for that frame. Terminal.Gui's
    /// write loop still skips rows whose <c>DirtyLines</c> entry is false, so an invalidated
    /// frame does not guarantee a complete write to the terminal.
    /// </summary>
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
        bool IsWide,
        bool Known);
}
