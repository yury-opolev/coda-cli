namespace Coda.Tui.Ui.Host;

/// <summary>
/// Defers cursor-positioning requests so that a run of consecutive moves collapses into a single
/// terminal escape sequence. Only the last requested position is retained; all intermediate ones
/// are silently discarded.
/// </summary>
/// <remarks>
/// When the terminal emits text for a dirty cell, only the cursor position immediately before that
/// text matters. Every intermediate cursor move in a run of clean cells wastes bandwidth. This
/// class captures the latest pending position so the caller can flush it precisely when text is
/// about to be written, collapsing an arbitrarily long run of moves into one.
/// </remarks>
internal sealed class DeferredCursor
{
    private int pendingCol;
    private int pendingRow;
    private bool hasPending;

    /// <summary>
    /// Records the latest requested cursor position, replacing any previously pending one.
    /// Consecutive calls collapse so that only the last position is ever emitted.
    /// </summary>
    public void Request(int col, int row)
    {
        pendingCol = col;
        pendingRow = row;
        hasPending = true;
    }

    /// <summary>
    /// Returns <see langword="true"/> and yields the pending position, clearing it so subsequent
    /// calls return <see langword="false"/> until a new <see cref="Request"/> is made.
    /// Returns <see langword="false"/> when no position is pending; <paramref name="col"/> and
    /// <paramref name="row"/> are set to zero in that case.
    /// </summary>
    public bool TryTake(out int col, out int row)
    {
        if (!hasPending)
        {
            col = 0;
            row = 0;
            return false;
        }

        col = pendingCol;
        row = pendingRow;
        hasPending = false;
        return true;
    }

    /// <summary>Discards any pending position without emitting it.</summary>
    public void Clear() => hasPending = false;
}
