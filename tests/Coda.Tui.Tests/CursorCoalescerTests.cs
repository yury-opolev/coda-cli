using Coda.Tui.Ui.Host;

namespace Coda.Tui.Tests;

public sealed class CursorCoalescerTests
{
    [Fact]
    public void Consecutive_position_requests_collapse_to_single_emit()
    {
        var emits = new List<(int Col, int Row)>();
        var coalescer = new CursorCoalescer((c, r) => { emits.Add((c, r)); return true; });
        coalescer.BeginFrame();

        coalescer.RequestPosition(0, 0);
        coalescer.RequestPosition(1, 0);
        coalescer.RequestPosition(5, 3);
        coalescer.FlushBeforeText();

        Assert.Single(emits);
        Assert.Equal((5, 3), emits[0]);
    }

    [Fact]
    public void FlushBeforeText_does_not_emit_when_nothing_is_pending()
    {
        var emitCount = 0;
        var coalescer = new CursorCoalescer((c, r) => { emitCount++; return true; });
        coalescer.BeginFrame();

        coalescer.FlushBeforeText();

        Assert.Equal(0, emitCount);
    }

    [Fact]
    public void Text_write_between_two_requests_forces_two_emits()
    {
        var emits = new List<(int Col, int Row)>();
        var coalescer = new CursorCoalescer((c, r) => { emits.Add((c, r)); return true; });
        coalescer.BeginFrame();

        coalescer.RequestPosition(2, 0);
        coalescer.FlushBeforeText();  // first emit: (2, 0)
        coalescer.NoteTextWritten();

        coalescer.RequestPosition(7, 3);
        coalescer.FlushBeforeText();  // second emit: (7, 3)

        Assert.Equal(2, emits.Count);
        Assert.Equal((2, 0), emits[0]);
        Assert.Equal((7, 3), emits[1]);
    }

    [Fact]
    public void False_emit_before_anything_emitted_keeps_frame_trusted()
    {
        // At frame start the terminal cursor is genuinely at the position Terminal.Gui
        // last set, so a false return from the emitter is accurate and harmless.
        var coalescer = new CursorCoalescer((c, r) => false);
        coalescer.BeginFrame();
        coalescer.RequestPosition(3, 5);
        coalescer.FlushBeforeText();

        var trusted = coalescer.EndFrame();
        Assert.True(trusted);
    }

    [Fact]
    public void False_emit_after_successful_cursor_emit_marks_frame_untrusted()
    {
        // Once a move has been emitted the terminal cursor is no longer at its known
        // start position; a subsequent dropped move places the following content at the
        // wrong location.
        var callCount = 0;
        var coalescer = new CursorCoalescer((c, r) =>
        {
            callCount++;
            return callCount == 1;  // only the first call succeeds
        });
        coalescer.BeginFrame();

        coalescer.RequestPosition(1, 0);
        coalescer.FlushBeforeText();  // succeeds → frameEmittedAnything = true
        coalescer.NoteTextWritten();

        coalescer.RequestPosition(2, 5);
        var trusted = coalescer.EndFrame();  // emitter returns false → untrusted

        Assert.False(trusted);
    }

    [Fact]
    public void False_emit_after_text_write_marks_frame_untrusted()
    {
        // Text written by the frame shifts the terminal cursor; a dropped move after
        // that leaves subsequent content at the wrong position.
        var coalescer = new CursorCoalescer((c, r) => false);
        coalescer.BeginFrame();

        coalescer.NoteTextWritten();  // frameEmittedAnything = true

        coalescer.RequestPosition(3, 5);
        var trusted = coalescer.EndFrame();  // emitter returns false → untrusted

        Assert.False(trusted);
    }

    [Fact]
    public void EndFrame_flushes_trailing_pending_move()
    {
        var emits = new List<(int Col, int Row)>();
        var coalescer = new CursorCoalescer((c, r) => { emits.Add((c, r)); return true; });
        coalescer.BeginFrame();
        coalescer.RequestPosition(10, 5);  // pending, not yet flushed

        coalescer.EndFrame();

        Assert.Single(emits);
        Assert.Equal((10, 5), emits[0]);
    }

    [Fact]
    public void BeginFrame_resets_trust_state_from_previous_frame()
    {
        var coalescer = new CursorCoalescer((c, r) => false);

        // First frame: mark untrusted
        coalescer.BeginFrame();
        coalescer.NoteTextWritten();
        coalescer.RequestPosition(1, 1);
        Assert.False(coalescer.EndFrame());

        // Second frame: clean slate
        coalescer.BeginFrame();
        coalescer.RequestPosition(2, 2);
        Assert.True(coalescer.EndFrame());
    }
}
