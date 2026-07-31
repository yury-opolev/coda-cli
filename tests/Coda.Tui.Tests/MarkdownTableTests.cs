using System;
using System.Linq;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Markdown tables were not parsed at all — the pipeline never enabled pipe tables — so a table arrived
/// as one long paragraph and was word-wrapped into mush, separator row and all. These pin the rendering
/// that replaces it: real columns, aligned, fitted to the viewport.
/// </summary>
public sealed class MarkdownTableTests
{
    private const string Simple = """
        | Key | Type | Description |
        |-----|------|-------------|
        | alpha | string | the first one |
        | b | int | second |
        """;

    private static TranscriptRenderLine[] Format(string markdown, int width = 60) =>
        TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), markdown, true), width).ToArray();

    private static string[] Texts(string markdown, int width = 60) =>
        Format(markdown, width).Select(l => l.Text).ToArray();

    // -----------------------------------------------------------------------
    // Structure
    // -----------------------------------------------------------------------

    [Fact]
    public void Every_cell_appears_on_its_own_row_rather_than_one_run_on_line()
    {
        var texts = Texts(Simple);

        Assert.Contains(texts, t => t.Contains("alpha", StringComparison.Ordinal));
        var alphaRow = texts.Single(t => t.Contains("alpha", StringComparison.Ordinal));

        // The old behaviour folded every row into one line; "b | int" must not share alpha's row.
        Assert.DoesNotContain("second", alphaRow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_separator_row_never_reaches_the_transcript_as_dashes()
    {
        var texts = Texts(Simple);

        Assert.DoesNotContain(texts, t => t.Contains("|-----|", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rule_separates_the_header_from_the_body()
    {
        var lines = Format(Simple);

        Assert.Contains(lines, l => l.Text.Contains('\u2500') && l.Role == TranscriptRole.DiffContext);
    }

    [Fact]
    public void The_header_row_is_styled_as_a_heading()
    {
        var lines = Format(Simple);

        var header = Assert.Single(lines, l => l.Text.Contains("Description", StringComparison.Ordinal));
        Assert.Equal(TranscriptRole.Heading, header.Role);
    }

    [Fact]
    public void Columns_line_up_across_every_row()
    {
        var texts = Texts(Simple).Where(t => t.Contains('\u2502')).ToArray();

        Assert.True(texts.Length >= 3, "header plus both body rows should carry column separators");

        var first = texts[0].IndexOf('\u2502');
        Assert.All(texts, t => Assert.Equal(first, t.IndexOf('\u2502')));
    }

    // -----------------------------------------------------------------------
    // Fitting the viewport
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(100)]
    public void No_row_is_wider_than_the_viewport(int width)
    {
        foreach (var line in Format(Simple, width))
        {
            Assert.True(
                TerminalCellText.Width(line.Text) <= width,
                $"row '{line.Text}' is {TerminalCellText.Width(line.Text)} cells at width {width}");
        }
    }

    [Fact]
    public void A_cell_too_long_for_its_column_wraps_rather_than_vanishing()
    {
        var markdown = """
            | Name | Notes |
            |------|-------|
            | x | a considerably longer note that cannot fit in one line at all |
            """;

        var texts = Texts(markdown, 40);
        var joined = string.Concat(texts);

        Assert.Contains("considerably", joined, StringComparison.Ordinal);
        Assert.Contains("one line", joined, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Alignment
    // -----------------------------------------------------------------------

    [Fact]
    public void A_right_aligned_column_is_padded_on_the_left()
    {
        var markdown = """
            | Item | Count |
            |------|------:|
            | a | 5 |
            | b | 1000 |
            """;

        var texts = Texts(markdown, 40);
        var five = texts.Single(t => t.Contains(" a ", StringComparison.Ordinal));
        var thousand = texts.Single(t => t.Contains(" b ", StringComparison.Ordinal));

        // Both numbers end at the same column when right aligned.
        Assert.Equal(five.TrimEnd().Length, thousand.TrimEnd().Length);
        Assert.EndsWith("5", five.TrimEnd(), StringComparison.Ordinal);
        Assert.EndsWith("1000", thousand.TrimEnd(), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Content
    // -----------------------------------------------------------------------

    [Fact]
    public void Inline_markup_inside_a_cell_is_rendered_as_its_text()
    {
        var markdown = """
            | Key | Value |
            |-----|-------|
            | **bold** | `code` |
            """;

        var joined = string.Concat(Texts(markdown));

        Assert.Contains("bold", joined, StringComparison.Ordinal);
        Assert.Contains("code", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("**", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_with_no_body_still_renders_its_header()
    {
        var markdown = """
            | Only | Header |
            |------|--------|
            """;

        var joined = string.Concat(Texts(markdown));

        Assert.Contains("Only", joined, StringComparison.Ordinal);
        Assert.Contains("Header", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ragged_row_with_fewer_cells_is_padded_rather_than_throwing()
    {
        var markdown = """
            | A | B | C |
            |---|---|---|
            | 1 |
            | 1 | 2 | 3 |
            """;

        var texts = Texts(markdown, 40);

        Assert.Contains(texts, t => t.Contains('1'));
        Assert.Contains(texts, t => t.Contains('3'));
    }

    [Fact]
    public void No_table_row_carries_trailing_whitespace()
    {
        // Padding the last column would end up in copied text for no visible benefit.
        foreach (var text in Texts(Simple))
        {
            Assert.Equal(text.TrimEnd(), text);
        }
    }

    [Fact]
    public void Prose_around_a_table_is_untouched()
    {
        var joined = string.Concat(Texts($"Before.\n\n{Simple}\n\nAfter.\n"));

        Assert.Contains("Before.", joined, StringComparison.Ordinal);
        Assert.Contains("After.", joined, StringComparison.Ordinal);
    }
}
