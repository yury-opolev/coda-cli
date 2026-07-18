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
