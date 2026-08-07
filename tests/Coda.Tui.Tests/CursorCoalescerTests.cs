using Coda.Tui.Ui.Host;

namespace Coda.Tui.Tests;

/// <summary>
/// The coalescer owns cursor emission during a frame because Terminal.Gui cannot: its
/// <c>SetCursorPositionImpl</c> compares against the APPLICATION CARET, which is set once per
/// iteration and never updated while a frame is being written, so any move onto that stale cell was
/// silently swallowed and the following text landed in the wrong place.
/// </summary>
public sealed class CursorCoalescerTests
{
    private static CursorCoalescer Recording(out List<(int Col, int Row)> emits)
    {
        var recorded = new List<(int Col, int Row)>();
        emits = recorded;
        return new CursorCoalescer((c, r) => recorded.Add((c, r)));
    }

    [Fact]
    public void Consecutive_position_requests_collapse_to_single_emit()
    {
        var coalescer = Recording(out var emits);
        coalescer.BeginFrame();

        coalescer.RequestPosition(0, 0);
        coalescer.RequestPosition(1, 0);
        coalescer.RequestPosition(5, 3);
        coalescer.FlushBeforeText();

        Assert.Equal([(5, 3)], emits);
    }

    [Fact]
    public void FlushBeforeText_does_not_emit_when_nothing_is_pending()
    {
        var coalescer = Recording(out var emits);
        coalescer.BeginFrame();

        coalescer.FlushBeforeText();

        Assert.Empty(emits);
    }

    /// <summary>
    /// THE BUG. The old coalescer exempted the first flush of a frame, assuming the terminal cursor
    /// was still wherever the previous iteration's SetCursor left it. When the requested position
    /// happened to match that stale caret the move was dropped, the run was written at the wrong
    /// place, and the frame was still reported trusted — so the corrupt frame became the diff
    /// baseline and those cells were never repainted again.
    /// </summary>
    [Fact]
    public void The_first_move_of_a_frame_is_always_emitted()
    {
        var coalescer = Recording(out var emits);
        coalescer.BeginFrame();

        coalescer.RequestPosition(0, 1);
        coalescer.FlushBeforeText();

        Assert.Equal([(0, 1)], emits);
    }

    [Fact]
    public void Text_write_between_two_requests_forces_two_emits()
    {
        var coalescer = Recording(out var emits);
        coalescer.BeginFrame();

        coalescer.RequestPosition(2, 0);
        coalescer.FlushBeforeText();
        coalescer.NoteTextWritten();

        coalescer.RequestPosition(7, 3);
        coalescer.FlushBeforeText();

        Assert.Equal([(2, 0), (7, 3)], emits);
    }

    /// <summary>
    /// A text run advances the write head by a width this class does not measure, so the tracked
    /// position is discarded and the next run repositions even to the same cell.
    /// </summary>
    [Fact]
    public void The_same_position_is_re_emitted_after_a_text_run()
    {
        var coalescer = Recording(out var emits);
        coalescer.BeginFrame();

        coalescer.RequestPosition(4, 2);
        coalescer.FlushBeforeText();
        coalescer.NoteTextWritten();

        coalescer.RequestPosition(4, 2);
        coalescer.FlushBeforeText();

        Assert.Equal([(4, 2), (4, 2)], emits);
    }

    /// <summary>...but a redundant request with no text in between costs nothing.</summary>
    [Fact]
    public void The_same_position_is_not_re_emitted_without_an_intervening_text_run()
    {
        var coalescer = Recording(out var emits);
        coalescer.BeginFrame();

        coalescer.RequestPosition(4, 2);
        coalescer.FlushBeforeText();
        coalescer.RequestPosition(4, 2);
        coalescer.FlushBeforeText();

        Assert.Equal([(4, 2)], emits);
    }

    [Fact]
    public void EndFrame_flushes_trailing_pending_move()
    {
        var coalescer = Recording(out var emits);
        coalescer.BeginFrame();
        coalescer.RequestPosition(10, 5);

        coalescer.EndFrame();

        Assert.Equal([(10, 5)], emits);
    }

    /// <summary>
    /// Position tracking must not survive a frame boundary: between frames Terminal.Gui repositions
    /// the application caret, so the write head is no longer where this class left it.
    /// </summary>
    [Fact]
    public void BeginFrame_forgets_the_previous_frames_position()
    {
        var coalescer = Recording(out var emits);

        coalescer.BeginFrame();
        coalescer.RequestPosition(3, 3);
        coalescer.FlushBeforeText();

        coalescer.BeginFrame();
        coalescer.RequestPosition(3, 3);
        coalescer.FlushBeforeText();

        Assert.Equal([(3, 3), (3, 3)], emits);
    }

    /// <summary>
    /// With emission owned here, a frame can no longer be untrusted. The flag is retained purely as
    /// an assertion so a future regression has something to trip.
    /// </summary>
    [Fact]
    public void A_frame_is_always_trusted_now_that_emission_cannot_be_declined()
    {
        var coalescer = Recording(out _);
        coalescer.BeginFrame();

        coalescer.NoteTextWritten();
        coalescer.RequestPosition(1, 1);

        Assert.True(coalescer.EndFrame());
        Assert.False(coalescer.FrameUntrusted);
    }
}
