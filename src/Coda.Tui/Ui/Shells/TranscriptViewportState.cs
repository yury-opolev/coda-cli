namespace Coda.Tui.Ui.Shells;

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

    /// <summary>Whether the viewport pins to the newest output as rows are appended.</summary>
    public bool AutoFollow { get; private set; } = true;

    /// <summary>Rows appended while scrolled away that the user has not seen yet.</summary>
    public int UnseenRows { get; private set; }

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
        this.ContentRows = Math.Max(0, rows);
        this.Reclamp();
    }

    /// <summary>
    /// Scrolls by <paramref name="deltaRows"/> rows. Scrolling up (negative) stops auto-following;
    /// scrolling down to the bottom resumes it and clears the unseen count.
    /// </summary>
    public void ScrollBy(int deltaRows)
    {
        if (deltaRows == 0)
        {
            return;
        }

        if (deltaRows < 0)
        {
            this.AutoFollow = false;
            this.TopRow = Math.Clamp(this.TopRow + deltaRows, 0, this.MaxTopRow);
            return;
        }

        this.TopRow = Math.Clamp(this.TopRow + deltaRows, 0, this.MaxTopRow);
        if (this.TopRow >= this.MaxTopRow)
        {
            this.AutoFollow = true;
            this.UnseenRows = 0;
        }
    }

    /// <summary>Jumps to the top of the transcript and stops auto-following.</summary>
    public void ScrollToTop()
    {
        this.AutoFollow = false;
        this.TopRow = 0;
    }

    /// <summary>
    /// Records that <paramref name="rows"/> rows were appended. While auto-following the viewport stays
    /// pinned to the bottom and nothing is unseen; otherwise the appended rows accumulate as unseen.
    /// </summary>
    public void OnRowsAppended(int rows)
    {
        if (rows <= 0)
        {
            this.ContentRows = Math.Max(0, this.ContentRows + rows);
            this.Reclamp();
            return;
        }

        this.ContentRows += rows;
        if (this.AutoFollow)
        {
            this.TopRow = this.MaxTopRow;
            this.UnseenRows = 0;
        }
        else
        {
            this.UnseenRows += rows;
        }
    }

    /// <summary>Jumps to the newest row, resumes auto-following, and clears the unseen count.</summary>
    public void JumpToNewest()
    {
        this.AutoFollow = true;
        this.UnseenRows = 0;
        this.TopRow = this.MaxTopRow;
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
        }
    }
}
