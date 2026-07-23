namespace Coda.Tui.Ui.Shells;

internal enum TranscriptFollowMode
{
    Following,
    Detached,
}

/// <summary>
/// Shell-local scroll bookkeeping for the virtualized transcript: the top visible row, whether the view
/// auto-follows new output, and how many appended rows have not yet been seen while scrolled away. It is
/// deliberately free of Terminal.Gui types so it can be unit-tested without a running application loop
/// and never enters <see cref="Coda.Tui.Ui.State.UiSessionSnapshot"/>.
/// </summary>
internal sealed class TranscriptViewportState
{
    /// <summary>The first content row rendered at the top of the viewport.</summary>
    public int TopRow { get; private set; }

    /// <summary>Whether the viewport is following the newest output or detached from it.</summary>
    public TranscriptFollowMode Mode { get; private set; } = TranscriptFollowMode.Following;

    /// <summary>Whether the viewport pins to the newest output as rows are appended.</summary>
    public bool AutoFollow => this.Mode == TranscriptFollowMode.Following;

    /// <summary>The stable content anchor captured when the viewport detached.</summary>
    public TranscriptViewportAnchor? DetachedAnchor { get; private set; }

    /// <summary>Rows appended while scrolled away that the user has not seen yet.</summary>
    public int UnseenRows { get; private set; }

    /// <summary>Visible blocks appended while scrolled away, excluding streaming growth of existing blocks.</summary>
    public int UnseenBlocks { get; private set; }

    /// <summary>Total content rows currently laid out.</summary>
    public int ContentRows { get; private set; }

    /// <summary>Height of the viewport in rows.</summary>
    public int ViewportHeight { get; private set; }

    /// <summary>The largest valid <see cref="TopRow"/> that still fills the viewport.</summary>
    public int MaxTopRow => Math.Max(0, this.ContentRows - this.ViewportHeight);

    /// <summary>Updates the viewport height and re-clamps (following the bottom while auto-following).</summary>
    public void SetViewportHeight(int height)
    {
        this.ViewportHeight = Math.Max(0, height);
        this.Reclamp();
    }

    /// <summary>Updates the content row count and re-clamps (following the bottom while auto-following).</summary>
    public void SetContentRows(int rows)
    {
        this.ApplyContentLayout(
            rows,
            this.DetachedAnchor,
            this.Mode == TranscriptFollowMode.Detached ? this.TopRow : null);
    }

    /// <summary>
    /// Applies a completed content-layout mutation without exposing an intermediate clamped position. A detached
    /// anchor that still resolves wins over its previous global row; when it no longer resolves, the current row
    /// is clamped and the caller-provided replacement anchor is retained when possible.
    /// </summary>
    public void ApplyContentLayout(
        int contentRows,
        TranscriptViewportAnchor? detachedAnchor,
        int? resolvedAnchorRow)
    {
        this.ContentRows = Math.Max(0, contentRows);
        if (this.AutoFollow)
        {
            this.FollowNewest();
            return;
        }

        if (resolvedAnchorRow is { } anchorRow)
        {
            this.TopRow = Math.Clamp(anchorRow, 0, this.MaxTopRow);
        }
        else
        {
            this.TopRow = Math.Clamp(this.TopRow, 0, this.MaxTopRow);
        }

        if (this.TopRow >= this.MaxTopRow)
        {
            this.FollowNewest();
            return;
        }

        this.DetachedAnchor = detachedAnchor;
    }

    /// <summary>
    /// Scrolls by <paramref name="deltaRows"/> rows. Scrolling up (negative) stops auto-following;
    /// scrolling down to the bottom resumes it and clears the unseen count.
    /// </summary>
    public void ScrollBy(int deltaRows)
    {
        this.ScrollBy(deltaRows, null);
    }

    /// <summary>Scrolls by rows while capturing the supplied stable anchor when detaching.</summary>
    public void ScrollBy(int deltaRows, TranscriptViewportAnchor? anchor)
    {
        if (deltaRows == 0)
        {
            return;
        }

        var nextTopRow = Math.Clamp(this.TopRow + deltaRows, 0, this.MaxTopRow);
        if (nextTopRow >= this.MaxTopRow)
        {
            this.FollowNewest();
            return;
        }

        this.TopRow = nextTopRow;
        this.Mode = TranscriptFollowMode.Detached;
        this.DetachedAnchor = anchor;
    }

    /// <summary>Jumps to the top of the transcript and stops auto-following.</summary>
    public void ScrollToTop()
    {
        this.ScrollToTop(null);
    }

    /// <summary>Jumps to the top while capturing the supplied stable anchor.</summary>
    public void ScrollToTop(TranscriptViewportAnchor? anchor)
    {
        this.ScrollToRow(0, anchor);
    }

    /// <summary>
    /// Records that <paramref name="rows"/> rows were appended. While auto-following the viewport stays
    /// pinned to the bottom and nothing is unseen; otherwise the appended rows accumulate as unseen.
    /// </summary>
    public void OnRowsAppended(int rows)
    {
        var wasDetached = this.Mode == TranscriptFollowMode.Detached;
        this.ApplyContentLayout(
            this.ContentRows + rows,
            this.DetachedAnchor,
            wasDetached ? this.TopRow : null);
        this.RecordAppendedRows(rows);
    }

    /// <summary>Records newly appended rows after their content layout has already been applied atomically.</summary>
    public void RecordAppendedRows(int rows)
    {
        if (rows > 0 && this.Mode == TranscriptFollowMode.Detached)
        {
            this.UnseenRows += rows;
        }
    }

    /// <summary>Records one complete visible block append without treating streaming row growth as a message.</summary>
    public void OnBlockAppended()
    {
        if (!this.AutoFollow)
        {
            this.UnseenBlocks++;
        }
    }

    /// <summary>Moves to an absolute content row, resuming follow when that reaches the bottom.</summary>
    public void ScrollToRow(int row)
    {
        this.ScrollToRow(row, null);
    }

    /// <summary>Moves to an absolute row while capturing the supplied stable anchor when detaching.</summary>
    public void ScrollToRow(int row, TranscriptViewportAnchor? anchor)
    {
        var nextTopRow = Math.Clamp(row, 0, this.MaxTopRow);
        if (nextTopRow >= this.MaxTopRow)
        {
            this.FollowNewest();
            return;
        }

        this.TopRow = nextTopRow;
        this.Mode = TranscriptFollowMode.Detached;
        this.DetachedAnchor = anchor;
    }

    /// <summary>
    /// Restores a detached viewport row and anchor without changing its mode or unseen counters.
    /// </summary>
    public void RestoreDetachedPosition(int row, TranscriptViewportAnchor anchor)
    {
        if (this.Mode != TranscriptFollowMode.Detached)
        {
            return;
        }

        this.TopRow = Math.Clamp(row, 0, this.MaxTopRow);
        this.DetachedAnchor = anchor;
    }

    /// <summary>Jumps to the newest row, resumes auto-following, and clears the unseen count.</summary>
    public void JumpToNewest()
    {
        this.FollowNewest();
    }

    private void Reclamp()
    {
        if (this.AutoFollow)
        {
            this.TopRow = this.MaxTopRow;
        }
        else
        {
            this.TopRow = Math.Clamp(this.TopRow, 0, this.MaxTopRow);
            if (this.TopRow >= this.MaxTopRow)
            {
                this.FollowNewest();
            }
        }
    }

    private void FollowNewest()
    {
        this.Mode = TranscriptFollowMode.Following;
        this.DetachedAnchor = null;
        this.UnseenRows = 0;
        this.UnseenBlocks = 0;
        this.TopRow = this.MaxTopRow;
    }
}
