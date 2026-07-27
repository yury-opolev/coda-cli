using Coda.Tui.Ui.Host;
using Terminal.Gui;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;

namespace Coda.Tui.Tests;

public sealed class PresentedFrameTests
{
    [Fact]
    public void Identical_rewrite_is_suppressed()
    {
        var buffer = CreateBuffer(4, 1);
        buffer.AddStr("hi");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(0, 0);
        buffer.AddStr("hi");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.False(IsDirty(buffer, 0, 0));
        Assert.False(IsDirty(buffer, 1, 0));
    }

    [Fact]
    public void Changed_cell_stays_dirty()
    {
        var buffer = CreateBuffer(4, 1);
        buffer.AddStr("hi");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(0, 0);
        buffer.AddStr("ho");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.True(IsDirty(buffer, 1, 0));
    }

    [Fact]
    public void Attribute_only_change_stays_dirty()
    {
        var buffer = CreateBuffer(2, 1);
        buffer.CurrentAttribute = new Terminal.Gui.Drawing.Attribute(ColorName16.Red, ColorName16.Black);
        buffer.AddStr("x");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(0, 0);
        buffer.CurrentAttribute = new Terminal.Gui.Drawing.Attribute(ColorName16.Blue, ColorName16.Black);
        buffer.AddStr("x");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.True(IsDirty(buffer, 0, 0));
    }

    [Fact]
    public void Url_only_change_stays_dirty()
    {
        var buffer = CreateBuffer(2, 1);
        buffer.CurrentUrl = "https://first.example";
        buffer.AddStr("x");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(0, 0);
        buffer.CurrentUrl = "https://second.example";
        buffer.AddStr("x");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.True(IsDirty(buffer, 0, 0));
    }

    [Fact]
    public void Dirty_lines_are_recomputed()
    {
        var buffer = CreateBuffer(3, 2);
        buffer.AddStr("aa");
        buffer.Move(0, 1);
        buffer.AddStr("bb");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(0, 0);
        buffer.AddStr("aa");
        buffer.Move(0, 1);
        buffer.AddStr("bc");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.False(buffer.DirtyLines[0]);
        Assert.True(buffer.DirtyLines[1]);
    }

    [Fact]
    public void First_frame_requires_full_write()
    {
        var buffer = CreateBuffer(2, 1);
        buffer.AddStr("x");
        var frame = new PresentedFrame();

        var wasDirty = IsDirty(buffer, 0, 0);

        Assert.False(frame.SuppressUnchangedCells(buffer));
        Assert.Equal(wasDirty, IsDirty(buffer, 0, 0));
    }

    [Fact]
    public void Size_change_requires_full_write()
    {
        var buffer = CreateBuffer(2, 1);
        buffer.AddStr("x");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.SetSize(3, 1);
        buffer.Move(0, 0);
        buffer.AddStr("x");
        var wasDirty = IsDirty(buffer, 0, 0);

        Assert.False(frame.SuppressUnchangedCells(buffer));
        Assert.Equal(wasDirty, IsDirty(buffer, 0, 0));
    }

    [Fact]
    public void Invalidate_forces_full_write()
    {
        var buffer = CreateBuffer(2, 1);
        buffer.AddStr("x");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);
        frame.Invalidate();

        buffer.Move(0, 0);
        buffer.AddStr("x");

        Assert.False(frame.SuppressUnchangedCells(buffer));
    }

    [Fact]
    public void Wide_glyph_change_keeps_both_cells_dirty()
    {
        var buffer = CreateBuffer(3, 1);
        buffer.AddStr("\u4E2D");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(0, 0);
        buffer.AddStr("\u6587");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.True(IsDirty(buffer, 0, 0));
        Assert.True(IsDirty(buffer, 1, 0));
    }
    // ── URL / non-square buffer ────────────────────────────────────────────────

    [Fact]
    public void Url_change_on_non_square_buffer_stays_dirty()
    {
        // Use a non-square buffer (cols != rows) and place the URL at (col=3, row=0).
        // GetCellUrl(col, row) takes arguments in (col, row) order. A transposed call
        // GetCellUrl(row=0, col=3) would hit an out-of-range row on this 2-row buffer
        // and return null for both frames, falsely concluding the URL is unchanged.
        var buffer = CreateBuffer(4, 2);
        buffer.Move(3, 0);
        buffer.CurrentUrl = "https://first.example";
        buffer.AddStr("x");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(3, 0);
        buffer.CurrentUrl = "https://second.example";
        buffer.AddStr("x");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.True(IsDirty(buffer, 3, 0));
    }

    [Fact]
    public void Url_unchanged_on_non_square_buffer_is_suppressed()
    {
        var buffer = CreateBuffer(4, 2);
        buffer.Move(3, 0);
        buffer.CurrentUrl = "https://constant.example";
        buffer.AddStr("x");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(3, 0);
        buffer.CurrentUrl = "https://constant.example";
        buffer.AddStr("x");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.False(IsDirty(buffer, 3, 0));
    }

    // ── Wide-glyph width transitions ──────────────────────────────────────────

    [Fact]
    public void Wide_to_narrow_width_change_keeps_both_cells_dirty()
    {
        // Frame 1: wide CJK at col 0 (occupies cols 0 and 1).
        // Frame 2: narrow ASCII at col 0. Col 1 appears unchanged, but the wide-glyph
        // rule must keep it dirty so the terminal re-draws the now-vacated second column.
        var buffer = CreateBuffer(3, 1);
        buffer.AddStr("\u4E2D");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(0, 0);
        buffer.AddStr("A");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.True(IsDirty(buffer, 0, 0));
        Assert.True(IsDirty(buffer, 1, 0));
    }

    [Fact]
    public void Narrow_to_wide_width_change_keeps_both_cells_dirty()
    {
        // Frame 1: narrow ASCII at col 0. Frame 2: wide CJK replaces it.
        // Col 1 transitions from a clean cell to the second column of the wide glyph.
        var buffer = CreateBuffer(3, 1);
        buffer.AddStr("A");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(0, 0);
        buffer.AddStr("\u4E2D");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.True(IsDirty(buffer, 0, 0));
        Assert.True(IsDirty(buffer, 1, 0));
    }

    [Fact]
    public void Wide_glyph_shifted_by_one_column_keeps_cells_dirty()
    {
        // Frame 1: wide glyph at col 0 (occupies 0–1).
        // Frame 2: same wide glyph at col 1 (occupies 1–2).
        // Col 0 looks equal to frame 1 but the glyph vacated it; col 1 and col 2 moved.
        var buffer = CreateBuffer(4, 1);
        buffer.Move(0, 0);
        buffer.AddStr("\u4E2D");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(1, 0);
        buffer.AddStr("\u4E2D");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.True(IsDirty(buffer, 0, 0));
        Assert.True(IsDirty(buffer, 1, 0));
    }

    [Fact]
    public void Wide_glyph_at_last_column_pair_is_handled()
    {
        // Wide glyph whose lead cell is at cols-2 (last valid wide-glyph position).
        // Verifies that the loop processes col+1 = cols-1 without an index out-of-range.
        var buffer = CreateBuffer(4, 1);
        buffer.Move(2, 0);
        buffer.AddStr("\u4E2D");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        buffer.Move(2, 0);
        buffer.AddStr("\u6587");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.True(IsDirty(buffer, 2, 0));
        Assert.True(IsDirty(buffer, 3, 0));
    }

    // ── FIX 1: ClearContents invalidates the retained frame ───────────────────

    [Fact]
    public void Buffer_cleared_at_same_size_forces_full_write()
    {
        // Prime the frame.
        var buffer = CreateBuffer(4, 2);
        buffer.AddStr("abcd");
        buffer.Move(0, 1);
        buffer.AddStr("efgh");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        // ClearContents() reallocates Contents at the same dimensions and physically erases the
        // terminal. Without FIX 1, the dimension check passes and SuppressUnchangedCells would
        // diff against the stale frame, conclude nothing changed, and leave the screen blank.
        buffer.ClearContents();
        buffer.Move(0, 0);
        buffer.AddStr("abcd");
        buffer.Move(0, 1);
        buffer.AddStr("efgh");

        Assert.False(frame.SuppressUnchangedCells(buffer));
        Assert.True(IsDirty(buffer, 0, 0));  // flags must not have been modified
    }

    // ── FIX 2: Un-presented cells are not baselined ───────────────────────────

    [Fact]
    public void Cells_left_dirty_are_not_adopted()
    {
        // Prime the frame with "aa".
        var buffer = CreateBuffer(4, 1);
        buffer.AddStr("aa");
        var frame = new PresentedFrame();
        frame.Adopt(buffer);

        // Second draw: change col 1 to 'b'.
        buffer.Move(0, 0);
        buffer.AddStr("ab");

        // Simulate a partial write: col 0 was presented (clear its dirty flag), but col 1
        // was not (leave it dirty). Without FIX 2, Adopt would baseline col 1 as 'b', and
        // the next frame's identical 'b' would be suppressed — the unpainted cell stays blank.
        buffer.Contents![0, 0].IsDirty = false;
        // col 1 remains dirty (IsDirty = true from AddStr)

        frame.Adopt(buffer);  // should retain "a" as the baseline for col 1

        // Third draw: the same "ab" content.
        buffer.Move(0, 0);
        buffer.AddStr("ab");

        Assert.True(frame.SuppressUnchangedCells(buffer));
        Assert.True(IsDirty(buffer, 1, 0));  // col 1 must remain dirty — it was never presented
    }

    private static bool IsDirty(OutputBufferImpl buffer, int col, int row)
        => (buffer.Contents ?? throw new InvalidOperationException("Buffer contents were not initialized."))[row, col].IsDirty;
    private static OutputBufferImpl CreateBuffer(int cols, int rows)
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(cols, rows);
        buffer.Move(0, 0);
        return buffer;
    }
}

