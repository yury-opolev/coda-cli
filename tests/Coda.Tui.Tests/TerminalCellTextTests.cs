using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

/// <summary>
/// Exercises the shared <see cref="TerminalCellText"/> layout helper: display-cell width, grapheme
/// enumeration with UTF-16 source indices and terminal cell offsets, cell-range slicing that keeps whole
/// intersecting graphemes, and word wrapping that prefers whitespace, hard-breaks long runs, preserves
/// newlines, and never splits a grapheme cluster.
/// </summary>
public sealed class TerminalCellTextTests
{
    // "a" (1 cell) + "界" (2 cells) + "é" precomposed (1 cell) + "z" (1 cell).
    private const string Sample = "a\u754c\u00e9z";

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("\u754c", 2)]        // 界 — wide CJK
    [InlineData("\U0001F600", 2)]    // 😀 — wide emoji
    [InlineData("e\u0301", 1)]       // e + combining acute — combining mark is zero width
    public void Width_counts_display_cells(string text, int expected)
    {
        Assert.Equal(expected, TerminalCellText.Width(text));
    }

    [Fact]
    public void Enumerate_reports_source_indices_and_cell_offsets()
    {
        var elements = TerminalCellText.Enumerate(Sample);

        Assert.Collection(
            elements,
            e => Assert.Equal(("a", 0, 1, 0, 1), (e.Text, e.SourceIndex, e.SourceLength, e.CellStart, e.Width)),
            e => Assert.Equal(("\u754c", 1, 1, 1, 2), (e.Text, e.SourceIndex, e.SourceLength, e.CellStart, e.Width)),
            e => Assert.Equal(("\u00e9", 2, 1, 3, 1), (e.Text, e.SourceIndex, e.SourceLength, e.CellStart, e.Width)),
            e => Assert.Equal(("z", 3, 1, 4, 1), (e.Text, e.SourceIndex, e.SourceLength, e.CellStart, e.Width)));
    }

    [Theory]
    [InlineData(1, 3, "\u754c")]         // whole wide grapheme
    [InlineData(3, 4, "\u00e9")]         // single narrow grapheme
    [InlineData(2, 4, "\u754c\u00e9")]   // partial overlap pulls in both whole graphemes
    public void SliceByCells_returns_whole_intersecting_graphemes(int startCell, int endCell, string expected)
    {
        Assert.Equal(expected, TerminalCellText.SliceByCells(Sample, startCell, endCell));
    }

    [Fact]
    public void Wrap_prefers_whitespace_preserves_newlines_and_hard_breaks_wide_runs()
    {
        var rows = TerminalCellText.Wrap("alpha beta\n\u754c\u754c\u754c", width: 6);

        Assert.Collection(
            rows,
            row => Assert.Equal(("alpha", 0, 5, 5), (row.Text, row.SourceStart, row.SourceEnd, row.Width)),
            row => Assert.Equal(("beta", 6, 10, 4), (row.Text, row.SourceStart, row.SourceEnd, row.Width)),
            row => Assert.Equal(("\u754c\u754c\u754c", 11, 14, 6), (row.Text, row.SourceStart, row.SourceEnd, row.Width)));
    }
}
