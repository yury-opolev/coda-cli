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
        /// Retained for call-site compatibility. The coalescer now owns emission and never declines,
        /// so this no longer suppresses a move — a position it names is still counted.
        /// </param>
        public CountingCoalescingOutput((int Col, int Row)? alreadyAt = null)
        {
            _ = alreadyAt;
            coalescer = new CursorCoalescer((c, r) => CharsWritten += CursorSequenceLength(c, r));
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

    /// <summary>
    /// The corruption this layer exists to prevent: a cursor move that lands on the stale
    /// application caret used to be silently dropped, the following run landed at the wrong place,
    /// and the frame was still reported trusted — so it became the diff baseline and those cells
    /// were suppressed forever. The coalescer now owns emission, so no move can be declined.
    /// </summary>
    [Fact]
    public void A_move_onto_the_stale_caret_is_still_emitted_and_the_frame_stays_trusted()
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 100, rows: 30);
        DrawFrame(buffer, changedCell: false);

        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        DrawFrame(buffer, changedCell: true);
        Assert.True(frame.SuppressUnchangedCells(buffer));

        // (99, 15) is the trailing position of the dirty row — exactly the kind of cell that used
        // to collide with AnsiOutput's stale caret and be swallowed.
        var output = new CountingCoalescingOutput(alreadyAt: (Col: 99, Row: 15));
        output.Write(buffer);

        Assert.True(output.LastFrameTrusted);
        Assert.True(output.CharsWritten > 0, "The frame must have emitted cursor positioning.");

        // A trusted frame is adopted, and the next identical frame is then fully suppressed —
        // which is only safe because nothing was misplaced.
        frame.Adopt(buffer);
        DrawFrame(buffer, changedCell: true);
        Assert.True(frame.SuppressUnchangedCells(buffer));
    }

    /// <summary>
    /// Recovery must actually repaint. <c>Invalidate</c> alone only stops suppression — it
    /// re-dirties nothing, so Terminal.Gui's write loop still skips every row whose DirtyLines
    /// entry is false, and in an idle TUI the stale cells are never rewritten.
    /// </summary>
    [Fact]
    public void ForceFullRepaintNextFrame_redirties_every_row()
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 100, rows: 30);
        DrawFrame(buffer, changedCell: false);

        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        // An unchanged frame: nothing is dirty and every row flag is down.
        DrawFrame(buffer, changedCell: false);
        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.DoesNotContain(buffer.DirtyLines, line => line);

        frame.ForceFullRepaintNextFrame();

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.All(buffer.DirtyLines, Assert.True);
    }

    /// <summary>A bare Invalidate is NOT enough on its own — this is why Fix B exists.</summary>
    [Fact]
    public void Invalidate_alone_does_not_redirty_anything()
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 100, rows: 30);
        DrawFrame(buffer, changedCell: false);

        var frame = new PresentedFrame();
        frame.Adopt(buffer);
        DrawFrame(buffer, changedCell: false);
        Assert.True(frame.SuppressUnchangedCells(buffer));

        frame.Invalidate();

        Assert.False(frame.SuppressUnchangedCells(buffer));
        Assert.DoesNotContain(buffer.DirtyLines, line => line);
    }
}
