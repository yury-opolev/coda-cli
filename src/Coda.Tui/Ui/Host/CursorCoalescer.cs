namespace Coda.Tui.Ui.Host;

/// <summary>
/// Owns cursor positioning during a single frame write, collapsing runs of intermediate moves into
/// a single escape sequence per text emission.
/// </summary>
/// <remarks>
/// <para>
/// This class must decide for itself whether a move is needed, because Terminal.Gui cannot.
/// <c>AnsiOutput.SetCursorPositionImpl</c> compares the requested position against
/// <c>_currentCursor</c> and emits nothing when they match — but <c>_currentCursor</c> is the
/// APPLICATION CARET, assigned once per iteration by <c>SetCursor</c> and never updated by
/// <c>SetCursorPositionImpl</c> itself. For the whole duration of a frame it is therefore a single
/// stale point, and any move onto that cell is swallowed no matter how far the write head has
/// already advanced. The following text run then lands at the wrong place, and the diffing layer
/// adopts the intended frame as "what the terminal shows" — making the corruption permanent.
/// </para>
/// <para>
/// So the coalescer tracks the WRITE HEAD instead, and emits unconditionally through a raw writer
/// that bypasses the diffing output's own <c>Write(StringBuilder)</c> override (which would
/// otherwise re-enter this class). After every text run the tracked position is discarded, so the
/// next run always repositions: a few bytes per run in exchange for removing an entire class of
/// drift.
/// </para>
/// </remarks>
internal sealed class CursorCoalescer
{
    private readonly Action<int, int> emitCursor;
    private readonly DeferredCursor deferred = new();
    private int lastCol;
    private int lastRow;
    private bool hasEmittedPosition;
    private bool frameUntrusted;

    /// <param name="emitCursor">
    /// Moves the terminal cursor. Must emit unconditionally — this class owns the decision about
    /// whether a move is required.
    /// </param>
    internal CursorCoalescer(Action<int, int> emitCursor)
    {
        this.emitCursor = emitCursor;
    }

    /// <summary>
    /// Whether a requested move could not be satisfied. Retained as an assertion: now that this
    /// class owns emission it must never become true, so a test (or a future regression) has
    /// something to catch.
    /// </summary>
    internal bool FrameUntrusted => this.frameUntrusted;

    /// <summary>
    /// Resets per-frame tracking. Must be called once at the start of each frame write.
    /// Clears any pending position left over from an aborted previous frame.
    /// </summary>
    public void BeginFrame()
    {
        this.deferred.Clear();
        this.hasEmittedPosition = false;
        this.frameUntrusted = false;
    }

    /// <summary>
    /// Records the latest requested cursor position, replacing any previously pending one.
    /// Returns <see langword="true"/> unconditionally because <c>OutputBase.Write</c> abandons the
    /// whole frame when <c>SetCursorPositionImpl</c> returns <see langword="false"/>.
    /// </summary>
    public bool RequestPosition(int col, int row)
    {
        this.deferred.Request(col, row);
        return true;
    }

    /// <summary>
    /// Emits the pending cursor move, if any, immediately before text is written. A move is emitted
    /// whenever the write head is not already known to be at the requested cell.
    /// </summary>
    public void FlushBeforeText()
    {
        if (!this.deferred.TryTake(out var col, out var row))
        {
            return;
        }

        if (this.hasEmittedPosition && this.lastCol == col && this.lastRow == row)
        {
            return;
        }

        this.emitCursor(col, row);
        this.lastCol = col;
        this.lastRow = row;
        this.hasEmittedPosition = true;
    }

    /// <summary>
    /// Records that text has been transmitted this frame. The write head has advanced by the run's
    /// width, which this class does not measure, so the tracked position is discarded and the next
    /// run repositions unconditionally.
    /// </summary>
    public void NoteTextWritten() => this.hasEmittedPosition = false;

    /// <summary>Emits any trailing pending cursor move and returns whether the frame is trusted.</summary>
    public bool EndFrame()
    {
        this.FlushBeforeText();
        return !this.frameUntrusted;
    }
}
