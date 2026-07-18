using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

public sealed class ComposerVisualLayoutTests
{
    [Fact]
    public void Explicit_and_visual_wraps_produce_stable_rows()
    {
        var layout = ComposerVisualLayout.Create("alpha beta\n界界界", width: 6);

        Assert.Equal(3, layout.VisualLineCount);
        Assert.Equal(new ComposerVisualPosition(0, 5), layout.PositionForIndex(5));
        Assert.Equal(new ComposerVisualPosition(1, 4), layout.PositionForIndex(10));
        Assert.Equal(new ComposerVisualPosition(2, 6), layout.PositionForIndex(14));
    }

    [Fact]
    public void Mapping_round_trips_utf16_indices_at_grapheme_boundaries()
    {
        const string text = "a\U0001F600e\u0301界z";
        var layout = ComposerVisualLayout.Create(text, width: 4);

        foreach (var index in new[] { 0, 1, 3, 5, 6, 7 })
        {
            var position = layout.PositionForIndex(index);
            Assert.Equal(index, layout.IndexForPosition(position.Row, position.Column));
        }
    }

    [Fact]
    public void Vertical_movement_preserves_preferred_display_column()
    {
        var layout = ComposerVisualLayout.Create("12345\n12\n123456", width: 20);

        var down = layout.MoveVertical(4, delta: 1, preferredColumn: null);
        Assert.Equal(8, down.CursorIndex);
        Assert.Equal(4, down.PreferredColumn);

        var downAgain = layout.MoveVertical(down.CursorIndex, delta: 1, down.PreferredColumn);
        Assert.Equal(13, downAgain.CursorIndex);
        Assert.Equal(4, downAgain.PreferredColumn);
    }

    [Fact]
    public void Vertical_movement_uses_visual_wrapped_rows_not_only_newlines()
    {
        var layout = ComposerVisualLayout.Create("abcdefghij", width: 4);

        var down = layout.MoveVertical(2, delta: 1, preferredColumn: null);
        var downAgain = layout.MoveVertical(down.CursorIndex, delta: 1, down.PreferredColumn);

        Assert.Equal(6, down.CursorIndex);
        Assert.Equal(10, downAgain.CursorIndex);
    }

    [Fact]
    public void Hard_wrap_shared_boundary_maps_to_continuation_row_start()
    {
        // "abcdefgh" hard-wraps to width 4 as "abcd" / "efgh" with no whitespace dropped, so index 4 is
        // both row 0's exclusive EndIndex and row 1's inclusive StartIndex. The shared seam must bind to
        // the start of the continuation row rather than to the far end of the preceding row.
        var layout = ComposerVisualLayout.Create("abcdefgh", width: 4);

        Assert.Equal(2, layout.VisualLineCount);
        Assert.Equal(new ComposerVisualPosition(1, 0), layout.PositionForIndex(4));
        Assert.Equal(new ComposerVisualPosition(1, 4), layout.PositionForIndex(8));
    }

    [Fact]
    public void Cjk_hard_wrap_shared_boundary_maps_to_continuation_row_start()
    {
        // Each ideograph is two cells wide, so "一二三四五六" hard-wraps to width 4 as "一二" / "三四" / "五六".
        // The shared seams at UTF-16 indices 2 and 4 must bind to the start of their continuation rows.
        var layout = ComposerVisualLayout.Create("一二三四五六", width: 4);

        Assert.Equal(3, layout.VisualLineCount);
        Assert.Equal(new ComposerVisualPosition(1, 0), layout.PositionForIndex(2));
        Assert.Equal(new ComposerVisualPosition(2, 0), layout.PositionForIndex(4));
    }

    [Fact]
    public void Repeated_down_movement_from_left_edge_progresses_hard_wrapped_rows()
    {
        // "abcdefghijkl" hard-wraps to width 4 as "abcd" / "efgh" / "ijkl". Starting from the left edge of
        // the first row, repeated downward moves must advance one visual row per step (0 -> 4 -> 8) instead
        // of stalling on the seam, then settle on the last row once the bottom is reached.
        var layout = ComposerVisualLayout.Create("abcdefghijkl", width: 4);

        var indices = new List<int>();
        var rows = new List<int> { layout.PositionForIndex(0).Row };
        var index = 0;
        int? preferred = null;
        for (var step = 0; step < 4; step++)
        {
            var moved = layout.MoveVertical(index, delta: 1, preferred);
            index = moved.CursorIndex;
            preferred = moved.PreferredColumn;
            indices.Add(index);
            rows.Add(layout.PositionForIndex(index).Row);
        }

        Assert.Equal(new[] { 4, 8, 8, 8 }, indices);
        Assert.Equal(new[] { 0, 1, 2, 2, 2 }, rows);
    }

    [Fact]
    public void Repeated_down_movement_from_left_edge_progresses_cjk_hard_wrapped_rows()
    {
        // "一二三四五六" hard-wraps to width 4 as "一二" / "三四" / "五六". Repeated downward moves from the
        // left edge must advance one visual row per step (0 -> 2 -> 4) rather than stalling on a seam.
        var layout = ComposerVisualLayout.Create("一二三四五六", width: 4);

        var indices = new List<int>();
        var rows = new List<int> { layout.PositionForIndex(0).Row };
        var index = 0;
        int? preferred = null;
        for (var step = 0; step < 3; step++)
        {
            var moved = layout.MoveVertical(index, delta: 1, preferred);
            index = moved.CursorIndex;
            preferred = moved.PreferredColumn;
            indices.Add(index);
            rows.Add(layout.PositionForIndex(index).Row);
        }

        Assert.Equal(new[] { 2, 4, 4 }, indices);
        Assert.Equal(new[] { 0, 1, 2, 2 }, rows);
    }

    [Fact]
    public void Zero_delta_vertical_movement_keeps_index_and_preferred_column()
    {
        // A zero delta is a no-op: it must not snap horizontally to the preferred column. The caret sits at
        // the end of the last row (index 15, column 6) while carrying a preferred column of 0; MoveVertical
        // with delta 0 must return that same index and preferred column, not jump to column 0's index (9).
        var layout = ComposerVisualLayout.Create("12345\n12\n123456", width: 20);

        var result = layout.MoveVertical(15, delta: 0, preferredColumn: 0);

        Assert.Equal(15, result.CursorIndex);
        Assert.Equal(0, result.PreferredColumn);
    }

    [Fact]
    public void Dropped_wrap_boundary_whitespace_snaps_to_preceding_row_end()
    {
        // Two spaces after "alpha" wrap to width 6: the first space is consumed at the
        // wrap point and the second is skipped as leading whitespace on the next row, so
        // both live in the gap between the visible rows. A trailing explicit line keeps the
        // gap strictly in the middle of the layout so any fallthrough would land on the last
        // row rather than snapping to the preceding row's end.
        var layout = ComposerVisualLayout.Create("alpha  beta\ngamma", width: 6);

        Assert.Equal(3, layout.VisualLineCount);

        var expected = new ComposerVisualPosition(0, 5);
        Assert.Equal(expected, layout.PositionForIndex(5));
        Assert.Equal(expected, layout.PositionForIndex(6));
    }
}
