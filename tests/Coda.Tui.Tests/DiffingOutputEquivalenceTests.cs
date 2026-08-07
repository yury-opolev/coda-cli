using System.Text;
using Coda.Tui.Ui.Host;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace Coda.Tui.Tests;

/// <summary>
/// The invariant the whole diffing output layer rests on, and which nothing previously asserted:
/// after every frame, what the emitted ANSI would put on a terminal must equal what Terminal.Gui
/// believes is on screen.
/// </summary>
/// <remarks>
/// This is what the stale-cell corruption violated. A cursor move that landed on Terminal.Gui's
/// stale application caret was silently dropped, the following text run went to the wrong place,
/// and the frame was still adopted as the diff baseline — so every later frame compared against a
/// lie and suppressed those cells forever. Reconstructing the terminal from the bytes we actually
/// emit is the only way to catch that class of bug.
/// </remarks>
public sealed class DiffingOutputEquivalenceTests
{
    [Fact]
    public void A_full_first_frame_reproduces_the_buffer_exactly()
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 40, rows: 8);
        var terminal = new TerminalModel(buffer.Cols, buffer.Rows);

        Fill(buffer, row => $"row {row} original content");

        var output = new CapturingCoalescingOutput();
        output.Write(buffer);
        terminal.Apply(output.Emitted);

        Assert.Equal(Snapshot(buffer), terminal.Snapshot());
    }

    /// <summary>
    /// The regression: a sparse repaint of many disjoint runs — exactly what an overlay transition
    /// (list to detail and back) produces, and what made the corruption reproducible.
    /// </summary>
    [Fact]
    public void A_sparse_second_frame_reproduces_the_buffer_exactly()
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 40, rows: 8);
        var terminal = new TerminalModel(buffer.Cols, buffer.Rows);
        var frame = new PresentedFrame();

        Fill(buffer, row => $"row {row} original content");
        var first = new CapturingCoalescingOutput();
        first.Write(buffer);
        terminal.Apply(first.Emitted);
        frame.Adopt(buffer);

        // Scatter changes across rows and columns so the frame is a sequence of disjoint runs.
        for (var row = 0; row < buffer.Rows; row++)
        {
            buffer.Move(row * 3 % 20, row);
            buffer.AddStr("XY");
        }

        Assert.True(frame.SuppressUnchangedCells(buffer));
        var second = new CapturingCoalescingOutput();
        second.Write(buffer);
        terminal.Apply(second.Emitted);

        Assert.Equal(Snapshot(buffer), terminal.Snapshot());
    }

    /// <summary>
    /// The precise shape of the original defect: the very first run of a frame targets the cell the
    /// application caret already occupies. Terminal.Gui would emit nothing for that move; the
    /// coalescer must emit it anyway, or the run lands wherever the previous frame left off.
    /// </summary>
    [Fact]
    public void A_frame_whose_first_run_targets_the_stale_caret_still_lands_correctly()
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 40, rows: 8);
        var terminal = new TerminalModel(buffer.Cols, buffer.Rows);
        var frame = new PresentedFrame();

        Fill(buffer, row => $"row {row} original content");
        var first = new CapturingCoalescingOutput();
        first.Write(buffer);
        terminal.Apply(first.Emitted);
        frame.Adopt(buffer);

        // Leave the terminal's write head at the end of the last row, then change a cell at the
        // START of the buffer — the new frame's first move goes backwards to (0, 0).
        buffer.Move(0, 0);
        buffer.AddStr("Z");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        var second = new CapturingCoalescingOutput();
        second.Write(buffer);
        terminal.Apply(second.Emitted);

        Assert.Equal(Snapshot(buffer), terminal.Snapshot());
        Assert.StartsWith("Z", terminal.Snapshot()[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A forced repaint must actually rewrite the screen. If the terminal has drifted for any
    /// reason, re-dirtying everything is what brings it back — dropping the baseline alone would
    /// leave every row skipped by the write loop.
    /// </summary>
    [Fact]
    public void A_forced_repaint_restores_a_terminal_that_has_drifted()
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 40, rows: 8);
        var frame = new PresentedFrame();

        Fill(buffer, row => $"row {row} original content");
        var first = new CapturingCoalescingOutput();
        first.Write(buffer);
        frame.Adopt(buffer);

        // A terminal that lost content the differ believes is still there.
        var terminal = new TerminalModel(buffer.Cols, buffer.Rows);

        // Nothing changed, so an ordinary frame emits nothing and the drift persists.
        Assert.True(frame.SuppressUnchangedCells(buffer));
        var quiet = new CapturingCoalescingOutput();
        quiet.Write(buffer);
        terminal.Apply(quiet.Emitted);
        Assert.NotEqual(Snapshot(buffer), terminal.Snapshot());

        // A forced repaint re-dirties everything, so the next frame rewrites the screen in full.
        frame.ForceFullRepaintNextFrame();
        Assert.True(frame.SuppressUnchangedCells(buffer));
        var repaint = new CapturingCoalescingOutput();
        repaint.Write(buffer);
        terminal.Apply(repaint.Emitted);

        Assert.Equal(Snapshot(buffer), terminal.Snapshot());
    }

    /// <summary>
    /// Drives the REAL <see cref="DiffingAnsiOutput"/> through a frame. Every other test here
    /// supplies its own emitter, so nothing exercised the production cursor-emission path — which
    /// is how a version that threw on every frame (reading the legacy static
    /// <c>Application.Screen</c>, unusable once the instance-based application model is in play)
    /// passed a fully green suite while making the TUI unable to draw at all.
    /// </summary>
    [Fact]
    public void The_real_diffing_output_emits_a_frame_without_throwing()
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 20, rows: 4);
        Fill(buffer, row => $"row {row}");

        using var output = new DiffingAnsiOutput(AppModel.FullScreen);

        var exception = Record.Exception(() => output.Write(buffer));

        Assert.Null(exception);
    }

    /// <summary>
    /// The same, in inline mode — where the row offset is non-zero and therefore actually matters.
    /// </summary>
    [Fact]
    public void The_real_diffing_output_emits_an_inline_frame_without_throwing()
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 20, rows: 4);
        Fill(buffer, row => $"row {row}");

        using var output = new DiffingAnsiOutput(AppModel.Inline);

        var exception = Record.Exception(() => output.Write(buffer));

        Assert.Null(exception);
    }

    /// <summary>
    /// Pins the ACTUAL defect. This double reproduces Terminal.Gui's real contract: a caret that is
    /// updated only ONCE per frame (as <c>SetCursor</c> does) and a positioning call that emits
    /// NOTHING when the requested cell matches it. The old design asked that call whether a move was
    /// needed, so a move onto the stale caret was dropped and the following run landed wherever the
    /// write head happened to be. The coalescer now owns the decision, so the same terminal is
    /// reproduced exactly.
    /// </summary>
    [Fact]
    public void A_terminal_that_declines_redundant_moves_is_still_reproduced_exactly()
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols: 40, rows: 8);
        var frame = new PresentedFrame();

        Fill(buffer, row => $"row {row} original content");
        var first = new StaleCaretOutput(caretCol: 0, caretRow: 0);
        first.Write(buffer);

        var terminal = new TerminalModel(buffer.Cols, buffer.Rows);
        terminal.Apply(first.Emitted);
        Assert.Equal(Snapshot(buffer), terminal.Snapshot());
        frame.Adopt(buffer);

        // Change a cell on the row the stale caret sits on, so the frame's move targets it.
        buffer.Move(0, 3);
        buffer.AddStr("CHANGED");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        var second = new StaleCaretOutput(caretCol: 0, caretRow: 3);
        second.Write(buffer);
        terminal.Apply(second.Emitted);

        // The frame HAD to move onto the stale caret, and a declining implementation would have
        // swallowed it — leaving "CHANGED" wherever the previous frame's write head stopped.
        Assert.NotEmpty(second.MovesTerminalGuiWouldHaveDropped);
        Assert.Equal(Snapshot(buffer), terminal.Snapshot());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static void Fill(OutputBufferImpl buffer, Func<int, string> lineFor)
    {
        for (var row = 0; row < buffer.Rows; row++)
        {
            buffer.Move(0, row);
            buffer.AddStr(lineFor(row).PadRight(buffer.Cols)[..buffer.Cols]);
        }
    }

    private static string[] Snapshot(IOutputBuffer buffer)
    {
        var lines = new string[buffer.Rows];
        for (var row = 0; row < buffer.Rows; row++)
        {
            var sb = new StringBuilder(buffer.Cols);
            for (var col = 0; col < buffer.Cols; col++)
            {
                var grapheme = buffer.Contents![row, col].Grapheme;
                sb.Append(string.IsNullOrEmpty(grapheme) ? " " : grapheme);
            }

            lines[row] = sb.ToString();
        }

        return lines;
    }

    /// <summary>
    /// A deliberately minimal terminal: a character grid plus a cursor, understanding only
    /// <c>CSI row;col H</c> and literal text. Every other escape sequence is skipped, which is
    /// enough because this asserts POSITIONING, not attributes.
    /// </summary>
    private sealed class TerminalModel(int cols, int rows)
    {
        private readonly char[,] grid = Create(cols, rows);
        private int col;
        private int row;

        private static char[,] Create(int cols, int rows)
        {
            var grid = new char[rows, cols];
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    grid[r, c] = ' ';
                }
            }

            return grid;
        }

        public void Apply(string ansi)
        {
            var i = 0;
            while (i < ansi.Length)
            {
                if (ansi[i] != '\x1b')
                {
                    this.Put(ansi[i]);
                    i++;
                    continue;
                }

                // CSI ... <final byte in @..~>
                var j = i + 2;
                while (j < ansi.Length && !(ansi[j] >= '@' && ansi[j] <= '~'))
                {
                    j++;
                }

                if (j < ansi.Length && ansi[j] == 'H')
                {
                    var parts = ansi[(i + 2)..j].Split(';');
                    this.row = int.Parse(parts[0]) - 1;
                    this.col = parts.Length > 1 ? int.Parse(parts[1]) - 1 : 0;
                }

                i = j + 1;
            }
        }

        private void Put(char ch)
        {
            if (ch is '\n' or '\r')
            {
                return;
            }

            if (this.row >= 0 && this.row < this.grid.GetLength(0)
                && this.col >= 0 && this.col < this.grid.GetLength(1))
            {
                this.grid[this.row, this.col] = ch;
            }

            this.col++;
        }

        public string[] Snapshot()
        {
            var lines = new string[this.grid.GetLength(0)];
            for (var r = 0; r < lines.Length; r++)
            {
                var sb = new StringBuilder(this.grid.GetLength(1));
                for (var c = 0; c < this.grid.GetLength(1); c++)
                {
                    sb.Append(this.grid[r, c]);
                }

                lines[r] = sb.ToString();
            }

            return lines;
        }
    }

    /// <summary>
    /// Models Terminal.Gui's real <c>AnsiOutput</c>: a caret fixed for the whole frame, and a
    /// positioning call that would emit NOTHING when asked to move onto it. The production coalescer
    /// must not route its decision through such a call — this double records what a declining
    /// implementation would have dropped, so a test can assert those moves were emitted anyway.
    /// </summary>
    private sealed class StaleCaretOutput : OutputBase
    {
        private readonly StringBuilder emitted = new();
        private readonly CursorCoalescer coalescer;
        private readonly int caretCol;
        private readonly int caretRow;
        private bool coalescing;

        public StaleCaretOutput(int caretCol, int caretRow)
        {
            this.caretCol = caretCol;
            this.caretRow = caretRow;
            this.coalescer = new CursorCoalescer(this.Emit);
        }

        public string Emitted => this.emitted.ToString();

        /// <summary>Moves a declining implementation would have swallowed, but which we emitted.</summary>
        public List<(int Col, int Row)> MovesTerminalGuiWouldHaveDropped { get; } = [];

        public override void Write(IOutputBuffer buffer)
        {
            this.coalescer.BeginFrame();
            this.coalescing = true;
            try
            {
                base.Write(buffer);
                this.coalescer.EndFrame();
            }
            finally
            {
                this.coalescing = false;
            }
        }

        private void Emit(int col, int row)
        {
            if (col == this.caretCol && row == this.caretRow)
            {
                this.MovesTerminalGuiWouldHaveDropped.Add((col, row));
            }

            this.emitted.Append('\x1b').Append('[').Append(row + 1).Append(';').Append(col + 1).Append('H');
        }

        protected override bool SetCursorPositionImpl(int col, int row)
        {
            if (this.coalescing)
            {
                return this.coalescer.RequestPosition(col, row);
            }

            this.Emit(col, row);
            return true;
        }

        protected override void Write(StringBuilder sb)
        {
            this.coalescer.FlushBeforeText();
            this.emitted.Append(sb);
            this.coalescer.NoteTextWritten();
        }
    }

    /// <summary>
    /// Captures the bytes a frame would send, driving the PRODUCTION
    /// <see cref="CursorCoalescer"/> so the emission decisions under test are the real ones.
    /// </summary>
    private sealed class CapturingCoalescingOutput : OutputBase
    {
        private readonly StringBuilder emitted = new();
        private readonly CursorCoalescer coalescer;
        private bool coalescing;

        public CapturingCoalescingOutput()
        {
            this.coalescer = new CursorCoalescer((c, r) =>
                this.emitted.Append('\x1b').Append('[').Append(r + 1).Append(';').Append(c + 1).Append('H'));
        }

        public string Emitted => this.emitted.ToString();

        public override void Write(IOutputBuffer buffer)
        {
            this.coalescer.BeginFrame();
            this.coalescing = true;
            try
            {
                base.Write(buffer);
                this.coalescer.EndFrame();
            }
            finally
            {
                this.coalescing = false;
            }
        }

        protected override bool SetCursorPositionImpl(int col, int row)
        {
            if (this.coalescing)
            {
                return this.coalescer.RequestPosition(col, row);
            }

            this.emitted.Append('\x1b').Append('[').Append(row + 1).Append(';').Append(col + 1).Append('H');
            return true;
        }

        protected override void Write(StringBuilder sb)
        {
            this.coalescer.FlushBeforeText();
            this.emitted.Append(sb);
            this.coalescer.NoteTextWritten();
        }
    }
}
