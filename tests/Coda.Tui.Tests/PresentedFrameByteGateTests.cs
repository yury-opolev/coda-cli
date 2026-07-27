using System.Text;
using Coda.Tui.Ui.Host;
using Terminal.Gui.Drivers;
using Xunit.Abstractions;

namespace Coda.Tui.Tests;

public sealed class PresentedFrameByteGateTests(ITestOutputHelper output)
{
    [Fact]
    public void One_cell_redraw_measures_suppress_and_coalesce_savings()
    {
        var unfiltered = CountRedrawCharacters(suppressUnchangedCells: false, coalesce: false);
        var suppressedOnly = CountRedrawCharacters(suppressUnchangedCells: true, coalesce: false);
        var suppressedCoalesced = CountRedrawCharacters(suppressUnchangedCells: true, coalesce: true);

        output.WriteLine(
            $"Unfiltered: {unfiltered}  Suppressed-only: {suppressedOnly}  Suppressed+coalesced: {suppressedCoalesced}");

        Assert.True(
            unfiltered > 1000,
            $"Expected unfiltered repaint to emit > 1000 characters, but emitted {unfiltered}.");

        // Cursor moves dominate even after cell suppression — this is why coalescing exists.
        Assert.True(
            suppressedOnly > 300,
            $"Expected suppressed-only repaint to emit > 300 characters (cursor moves dominate), but emitted {suppressedOnly}.");

        Assert.True(
            suppressedCoalesced < 100,
            $"Expected suppressed+coalesced repaint to emit < 100 characters, but emitted {suppressedCoalesced}.");
    }

    private static int CountRedrawCharacters(bool suppressUnchangedCells, bool coalesce)
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 100, rows: 30);
        DrawFrame(buffer, changedCell: false);

        var frame = new PresentedFrame();
        // First frame: adopt the initial content as the baseline. No write needed — on the first
        // Adopt there is no previous frame, so all cells are unconditionally recorded.
        frame.Adopt(buffer);

        DrawFrame(buffer, changedCell: true);

        if (suppressUnchangedCells)
        {
            Assert.True(frame.SuppressUnchangedCells(buffer));
        }

        if (coalesce)
        {
            var coalescing = new CountingCoalescingOutput();
            coalescing.Write(buffer);
            return coalescing.CharsWritten;
        }
        else
        {
            var counting = new CountingOutput();
            counting.Write(buffer);
            return counting.CharsWritten;
        }
    }

    private static void DrawFrame(OutputBufferImpl buffer, bool changedCell)
    {
        for (var row = 0; row < buffer.Rows; row++)
        {
            var line = $"row {row:D2}  Coda terminal frame content for redraw measurement."
                .PadRight(buffer.Cols)
                [..buffer.Cols];
            if (changedCell && row == 15)
            {
                line = line[..50] + "#" + line[51..];
            }

            buffer.Move(0, row);
            buffer.AddStr(line);
        }
    }

    /// <summary>
    /// Counts characters without coalescing, modeling the real length of a CSI cursor-position
    /// sequence instead of a flat constant.
    /// </summary>
    private sealed class CountingOutput : OutputBase
    {
        public int CharsWritten { get; private set; }

        protected override void Write(StringBuilder sb) => CharsWritten += sb.Length;

        protected override bool SetCursorPositionImpl(int col, int row)
        {
            CharsWritten += CursorSequenceLength(col, row);
            return true;
        }
    }

    /// <summary>
    /// Counts characters WITH coalescing, delegating to <see cref="CursorCoalescer"/> so the
    /// same production algorithm that <see cref="DiffingAnsiOutput"/> uses is exercised here
    /// rather than a hand-rolled reimplementation.
    /// </summary>
    private sealed class CountingCoalescingOutput : OutputBase
    {
        private readonly CursorCoalescer coalescer;
        private bool coalescing;

        public int CharsWritten { get; private set; }

        /// <summary>Whether the most recent frame was trusted by the coalescer.</summary>
        public bool LastFrameTrusted { get; private set; }

        /// <param name="alreadyAt">
        /// Optional position for which the emitter returns <see langword="false"/> without
        /// counting characters, simulating the Terminal.Gui behaviour where
        /// <c>AnsiOutput.SetCursorPositionImpl</c> skips a move when it believes the
        /// cursor is already at the requested position.
        /// </param>
        public CountingCoalescingOutput((int Col, int Row)? alreadyAt = null)
        {
            coalescer = new CursorCoalescer((c, r) =>
            {
                if (alreadyAt.HasValue && c == alreadyAt.Value.Col && r == alreadyAt.Value.Row)
                {
                    return false;
                }
                CharsWritten += CursorSequenceLength(c, r);
                return true;
            });
        }

        public override void Write(IOutputBuffer buffer)
        {
            coalescer.BeginFrame();
            coalescing = true;
            try
            {
                base.Write(buffer);
                LastFrameTrusted = coalescer.EndFrame();
            }
            finally
            {
                coalescing = false;
            }
        }

        protected override bool SetCursorPositionImpl(int col, int row)
        {
            if (coalescing)
            {
                return coalescer.RequestPosition(col, row);
            }

            CharsWritten += CursorSequenceLength(col, row);
            return true;
        }

        protected override void Write(StringBuilder sb)
        {
            coalescer.FlushBeforeText();
            CharsWritten += sb.Length;
            coalescer.NoteTextWritten();
        }
    }

    // ESC [ row+1 ; col+1 H
    private static int CursorSequenceLength(int col, int row)
        => $"\x1b[{row + 1};{col + 1}H".Length;

    [Fact]
    public void Dropped_cursor_move_after_text_marks_frame_untrusted_and_forces_full_repaint()
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 100, rows: 30);
        DrawFrame(buffer, changedCell: false);

        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        // Draw a second frame with one changed cell; after suppression only row 15 is dirty.
        DrawFrame(buffer, changedCell: true);
        Assert.True(frame.SuppressUnchangedCells(buffer));

        // The base write loop moves the cursor to every cell in dirty rows. After writing
        // the one dirty cell at col 50 it continues positioning through cols 51–99, so the
        // last pending position at EndFrame time is (99, 15).
        //
        // Configure the emitter to return false (without counting) for that trailing position,
        // simulating AnsiOutput._currentCursor being there from a previous frame. At the time
        // EndFrame flushes this move the frame has already written text at col 50, so
        // frameEmittedAnything is true and the dropped move must mark the frame untrusted.
        var output = new CountingCoalescingOutput(alreadyAt: (Col: 99, Row: 15));
        output.Write(buffer);

        Assert.False(output.LastFrameTrusted,
            "A dropped cursor move after text was written must mark the frame untrusted.");

        // DiffingAnsiOutput calls frame.Invalidate() instead of frame.Adopt(buffer) when
        // untrusted. Replicate that decision here to verify the downstream effect.
        if (!output.LastFrameTrusted)
        {
            frame.Invalidate();
        }

        // Third frame: same content. Because the baseline was invalidated, the next
        // SuppressUnchangedCells call must report that no compatible frame exists, forcing
        // a full repaint and preventing the corrupted position from becoming permanent.
        DrawFrame(buffer, changedCell: true);
        Assert.False(frame.SuppressUnchangedCells(buffer),
            "An invalidated baseline must require a full repaint on the next frame.");
    }
}
