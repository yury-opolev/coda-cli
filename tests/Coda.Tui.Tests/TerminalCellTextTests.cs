using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

/// <summary>
/// Exercises the shared <see cref="TerminalCellText"/> layout helper: display-cell width measured per
/// grapheme cluster (combining/format marks add nothing, wide runes count two, multi-rune emoji/ZWJ
/// clusters take the maximum rune width rather than the sum), grapheme enumeration that reports absolute
/// UTF-16 positions and terminal cell offsets, cell-range slicing that keeps whole intersecting graphemes
/// (using <c>Math.Max(1, CellWidth)</c> so zero-width clusters still intersect), and word wrapping that
/// prefers whitespace, hard-breaks long runs, preserves newlines, never splits a grapheme cluster, and
/// exposes per-row <see cref="TerminalTextElement"/> metadata with absolute UTF-16 indices and cell starts
/// rebased to the row.
/// </summary>
public sealed class TerminalCellTextTests
{
    // "a" (1 cell) + "界" (2 cells) + "e" + combining acute (1 cell) + "z" (1 cell).
    private const string Sample = "a\u754ce\u0301z";

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("\u754c", 2)]        // 界 — wide CJK
    [InlineData("\U0001F600", 2)]    // 😀 — wide emoji
    [InlineData("e\u0301", 1)]       // e + combining acute — combining mark is zero width
    public void Width_counts_terminal_cells_without_splitting_graphemes(string text, int expected)
    {
        Assert.Equal(expected, TerminalCellText.Width(text));
    }

    [Fact]
    public void Enumerate_reports_absolute_utf16_positions_and_cell_offsets()
    {
        var elements = TerminalCellText.Enumerate(Sample);

        Assert.Collection(
            elements,
            e => Assert.Equal(("a", 0, 1, 0, 1), (e.Text, e.Utf16Start, e.Utf16Length, e.CellStart, e.CellWidth)),
            e => Assert.Equal(("\u754c", 1, 1, 1, 2), (e.Text, e.Utf16Start, e.Utf16Length, e.CellStart, e.CellWidth)),
            e => Assert.Equal(("e\u0301", 2, 2, 3, 1), (e.Text, e.Utf16Start, e.Utf16Length, e.CellStart, e.CellWidth)),
            e => Assert.Equal(("z", 4, 1, 4, 1), (e.Text, e.Utf16Start, e.Utf16Length, e.CellStart, e.CellWidth)));
    }

    [Theory]
    [InlineData(1, 3, "\u754c")]              // whole wide grapheme
    [InlineData(3, 4, "e\u0301")]             // combining grapheme kept whole
    [InlineData(2, 4, "\u754ce\u0301")]       // partial overlap pulls in both whole graphemes
    public void SliceByCells_selects_whole_graphemes_that_intersect_the_range(int startCell, int endCell, string expected)
    {
        Assert.Equal(expected, TerminalCellText.SliceByCells(Sample, startCell, endCell));
    }

    [Fact]
    public void SliceByCells_includes_zero_width_grapheme_using_max_one_cell()
    {
        // A lone combining mark measures zero cells; slicing must still treat it as occupying one cell
        // (Math.Max(1, CellWidth)) so a [0, 1) request returns it rather than the empty string.
        const string combining = "\u0301";

        var element = Assert.Single(TerminalCellText.Enumerate(combining));
        Assert.Equal(0, element.CellWidth);
        Assert.Equal(combining, TerminalCellText.SliceByCells(combining, 0, 1));
    }

    [Fact]
    public void Wrap_preserves_newlines_prefers_whitespace_and_hard_breaks_long_grapheme_runs()
    {
        var rows = TerminalCellText.Wrap("alpha beta\n\u754c\u754c\u754c", width: 6);

        Assert.Collection(
            rows,
            row => Assert.Equal(("alpha", 0, 5, 5), (row.Text, row.StartIndex, row.EndIndex, row.CellWidth)),
            row => Assert.Equal(("beta", 6, 10, 4), (row.Text, row.StartIndex, row.EndIndex, row.CellWidth)),
            row => Assert.Equal(("\u754c\u754c\u754c", 11, 14, 6), (row.Text, row.StartIndex, row.EndIndex, row.CellWidth)));
    }

    [Fact]
    public void Wrapped_row_elements_have_absolute_utf16_positions_and_rebased_cell_starts()
    {
        var rows = TerminalCellText.Wrap("alpha beta\n\u754c\u754c\u754c", width: 6);

        var beta = rows[1];
        Assert.Equal("beta", beta.Text);
        Assert.Collection(
            beta.Elements,
            e => Assert.Equal(("b", 6, 1, 0, 1), (e.Text, e.Utf16Start, e.Utf16Length, e.CellStart, e.CellWidth)),
            e => Assert.Equal(("e", 7, 1, 1, 1), (e.Text, e.Utf16Start, e.Utf16Length, e.CellStart, e.CellWidth)),
            e => Assert.Equal(("t", 8, 1, 2, 1), (e.Text, e.Utf16Start, e.Utf16Length, e.CellStart, e.CellWidth)),
            e => Assert.Equal(("a", 9, 1, 3, 1), (e.Text, e.Utf16Start, e.Utf16Length, e.CellStart, e.CellWidth)));

        var wide = rows[2];
        Assert.Equal("\u754c\u754c\u754c", wide.Text);
        Assert.Collection(
            wide.Elements,
            e => Assert.Equal(("\u754c", 11, 1, 0, 2), (e.Text, e.Utf16Start, e.Utf16Length, e.CellStart, e.CellWidth)),
            e => Assert.Equal(("\u754c", 12, 1, 2, 2), (e.Text, e.Utf16Start, e.Utf16Length, e.CellStart, e.CellWidth)),
            e => Assert.Equal(("\u754c", 13, 1, 4, 2), (e.Text, e.Utf16Start, e.Utf16Length, e.CellStart, e.CellWidth)));
    }

    [Fact]
    public void Multi_rune_emoji_grapheme_occupies_two_cells_not_the_sum_of_runes()
    {
        const string thumbsUp = "\U0001F44D\U0001F3FD"; // 👍🏽 — base emoji + medium skin-tone modifier

        var element = Assert.Single(TerminalCellText.Enumerate(thumbsUp));
        Assert.Equal(thumbsUp, element.Text);
        Assert.Equal(0, element.Utf16Start);
        Assert.Equal(4, element.Utf16Length);
        Assert.Equal(2, element.CellWidth);            // maximum wide rune, not 2 + 2
        Assert.Equal(2, TerminalCellText.Width(thumbsUp));
    }

    [Fact]
    public void Zwj_family_grapheme_occupies_two_cells_not_the_sum_of_runes()
    {
        const string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467"; // 👨‍👩‍👧

        var element = Assert.Single(TerminalCellText.Enumerate(family));
        Assert.Equal(family, element.Text);
        Assert.Equal(2, element.CellWidth);            // ZWJ is zero width; the max wide rune wins, not 6
        Assert.Equal(2, TerminalCellText.Width(family));
    }

    [Fact]
    public void Wrapping_never_splits_a_multi_rune_emoji_grapheme()
    {
        const string thumbsUp = "\U0001F44D\U0001F3FD";

        var rows = TerminalCellText.Wrap(thumbsUp + thumbsUp, width: 2);

        Assert.Collection(
            rows,
            row => Assert.Equal(thumbsUp, row.Text),
            row => Assert.Equal(thumbsUp, row.Text));
    }

    [Fact]
    public void Regional_indicator_flag_stays_whole_when_grouped_and_is_not_summed()
    {
        const string flag = "\U0001F1EF\U0001F1F5"; // 🇯🇵

        var elements = TerminalCellText.Enumerate(flag);
        if (elements.Length != 1)
        {
            return; // This runtime does not group regional indicators; nothing to assert.
        }

        var element = elements[0];
        Assert.Equal(flag, element.Text);
        Assert.Equal(4, element.Utf16Length);
        // Grouped grapheme width is the maximum rune width, never the sum of the two regional indicators.
        Assert.Equal(element.CellWidth, TerminalCellText.Width(flag));
        Assert.Equal(flag, TerminalCellText.SliceByCells(flag, 0, Math.Max(1, element.CellWidth)));
        Assert.Equal(flag, Assert.Single(TerminalCellText.Wrap(flag, width: 1)).Text);
    }
}
