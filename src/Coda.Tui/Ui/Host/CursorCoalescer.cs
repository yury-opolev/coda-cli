namespace Coda.Tui.Ui.Host;

/// <summary>
/// Manages deferred cursor positioning during a single frame write, collapsing runs of
/// intermediate moves into a single escape sequence per text emission and tracking whether
/// a silently-dropped move has compromised the frame's integrity.
/// </summary>
/// <remarks>
/// <para>
/// Terminal.Gui's <c>AnsiOutput.SetCursorPositionImpl</c> returns <see langword="false"/>
/// without emitting when the requested position matches the internally-tracked previous
/// caret (<c>_currentCursor</c>). At frame start that caret reflects the terminal's actual
/// cursor position, so a false return is accurate and harmless. Once this frame has emitted
/// text or a prior cursor move, the terminal cursor is somewhere else; a false return at
/// that point means the move was silently skipped and subsequent content will appear at the
/// wrong location.
/// </para>
/// <para>
/// This class tracks both facts — whether anything has been emitted this frame, and whether
/// a dropped move occurred after emission began — so callers can decide whether to adopt
/// or invalidate the frame baseline.
/// </para>
/// </remarks>
internal sealed class CursorCoalescer
{
    private readonly Func<int, int, bool> emitCursor;
    private readonly DeferredCursor deferred = new();
    private bool frameEmittedAnything;
    private bool frameUntrusted;

    /// <param name="emitCursor">
    /// Moves the terminal cursor, returning whether a move was actually emitted. Terminal.Gui
    /// reports <see langword="false"/> when it believes the cursor is already at the requested
    /// position.
    /// </param>
    internal CursorCoalescer(Func<int, int, bool> emitCursor)
    {
        this.emitCursor = emitCursor;
    }

    /// <summary>
    /// Resets per-frame tracking. Must be called once at the start of each frame write.
    /// Clears any pending position left over from an aborted previous frame.
    /// </summary>
    public void BeginFrame()
    {
        deferred.Clear();
        frameEmittedAnything = false;
        frameUntrusted = false;
    }

    /// <summary>
    /// Records the latest requested cursor position, replacing any previously pending one.
    /// Returns <see langword="true"/> unconditionally because
    /// <c>OutputBase.Write</c> abandons the whole frame when <c>SetCursorPositionImpl</c>
    /// returns <see langword="false"/>.
    /// </summary>
    public bool RequestPosition(int col, int row)
    {
        deferred.Request(col, row);
        return true;
    }

    /// <summary>
    /// Emits the pending cursor move, if any, immediately before text is written.
    /// </summary>
    /// <remarks>
    /// A false return from <see cref="emitCursor"/> is harmless only when nothing has
    /// been emitted yet this frame — the terminal cursor is still at the position set by
    /// the previous frame's <c>SetCursor</c>. Once text or a prior cursor move has been
    /// emitted the terminal cursor has shifted; a subsequent dropped move would place the
    /// following text at the wrong location, so the frame is marked untrusted.
    /// </remarks>
    public void FlushBeforeText()
    {
        if (!deferred.TryTake(out var col, out var row))
        {
            return;
        }

        var emitted = emitCursor(col, row);
        if (emitted)
        {
            frameEmittedAnything = true;
        }
        else if (frameEmittedAnything)
        {
            // The terminal cursor is not at the expected position: this frame already shifted
            // it via text or a prior move, yet the requested move was silently skipped.
            frameUntrusted = true;
        }
    }

    /// <summary>Records that text has been transmitted to the terminal this frame.</summary>
    public void NoteTextWritten() => frameEmittedAnything = true;

    /// <summary>
    /// Emits any trailing pending cursor move and returns whether the frame is trusted.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when no dropped move occurred after emission began;
    /// <see langword="false"/> when a move was silently skipped after text or a prior move
    /// had already shifted the terminal cursor, meaning some content may have appeared at
    /// the wrong position.
    /// </returns>
    public bool EndFrame()
    {
        FlushBeforeText();
        return !frameUntrusted;
    }
}

