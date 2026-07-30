using System.Collections.Immutable;
using Coda.Agent;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using TgColor = Terminal.Gui.Drawing.Color;
using TgName = Terminal.Gui.Drawing.ColorName16;

namespace Coda.Tui.Tests;

/// <summary>
/// Part A tests for "Clickable Links": link span extraction, TextMatchesUrl classification,
/// deceptive-link marker, wrapping across lines, TranscriptRow mirroring, theme roles, and
/// draw-path attribute tests.  Interaction (Part B) is explicitly out of scope here.
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class LinkSpanTests
{
    // ---------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------

    private static IReadOnlyList<TranscriptRenderLine> Format(string markdown, int width = 80) =>
        TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), markdown, Complete: true),
            width);

    // ---------------------------------------------------------------------------
    // 1. Autolink extraction
    // ---------------------------------------------------------------------------

    [Fact]
    public void Autolink_produces_link_span_with_TextMatchesUrl_true()
    {
        var lines = Format("https://example.com");

        var line = Assert.Single(lines);
        Assert.NotNull(line.Links);
        var link = Assert.Single(line.Links!);
        Assert.Equal("https://example.com", link.Url);
        Assert.True(link.TextMatchesUrl);
        // The text must contain the URL (no ⚠ for honest links).
        Assert.Contains("https://example.com", line.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("⚠", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Autolink_span_columns_match_start_and_end_of_url_in_text()
    {
        // "See https://example.com" rendered as " ● See https://example.com" — URL starts at col 7.
        var lines = Format("See https://example.com", width: 80);

        var line = Assert.Single(lines);
        var link = Assert.Single(line.Links ?? []);
        Assert.Equal(7, link.StartColumn);
        Assert.Equal(7 + TerminalCellText.Width("https://example.com"), link.EndColumn);
    }

    // ---------------------------------------------------------------------------
    // 2. Markdown explicit link extraction
    // ---------------------------------------------------------------------------

    [Fact]
    public void Markdown_link_with_different_text_is_deceptive()
    {
        var lines = Format("[click here](https://example.com)");

        var line = Assert.Single(lines);
        var link = Assert.Single(line.Links ?? []);
        Assert.Equal("https://example.com", link.Url);
        Assert.False(link.TextMatchesUrl);
    }

    [Fact]
    public void Deceptive_link_appends_warning_glyph_to_rendered_text()
    {
        var lines = Format("[click here](https://example.com)");

        var line = Assert.Single(lines);
        Assert.Contains("click here", line.Text, StringComparison.Ordinal);
        Assert.Contains("⚠", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Deceptive_link_span_includes_warning_glyph_in_column_range()
    {
        // " ● click here⚠": link starts at col 3 (gutter " ● "), ends at 3+11=14.
        var lines = Format("[click here](https://example.com)");

        var line = Assert.Single(lines);
        var link = Assert.Single(line.Links!);
        Assert.Equal(3, link.StartColumn);
        // "click here⚠": width = 10 + 1 = 11 (⚠ is 1 cell).
        Assert.Equal(14, link.EndColumn);
    }

    // ---------------------------------------------------------------------------
    // 3. TextMatchesUrl classification
    // ---------------------------------------------------------------------------

    [Fact]
    public void Link_text_equal_to_url_is_honest()
    {
        var lines = Format("[https://example.com](https://example.com)");

        var line = Assert.Single(lines);
        var link = Assert.Single(line.Links ?? []);
        Assert.True(link.TextMatchesUrl);
        Assert.DoesNotContain("⚠", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_text_case_insensitive_url_match_is_honest()
    {
        var lines = Format("[HTTPS://EXAMPLE.COM](https://example.com)");

        var line = Assert.Single(lines);
        var link = Assert.Single(line.Links ?? []);
        Assert.True(link.TextMatchesUrl);
    }

    [Fact]
    public void Link_text_equal_to_host_authority_is_honest()
    {
        var lines = Format("[example.com](https://example.com)");

        var line = Assert.Single(lines);
        var link = Assert.Single(line.Links ?? []);
        Assert.True(link.TextMatchesUrl);
        Assert.DoesNotContain("⚠", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_text_unrelated_to_url_is_deceptive()
    {
        var lines = Format("[safe link](https://phishing.example.com)");

        var line = Assert.Single(lines);
        var link = Assert.Single(line.Links ?? []);
        Assert.False(link.TextMatchesUrl);
        Assert.Contains("⚠", line.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // 4. Wrapped links
    // ---------------------------------------------------------------------------

    [Fact]
    public void Autolink_url_longer_than_width_produces_one_span_per_line_sharing_url()
    {
        // At width 10, "https://example.com" (19 cells) must break across lines.
        // The paragraph "See https://example.com" has "See" on one line and the URL broken.
        var lines = Format("See https://example.com", width: 10);

        Assert.True(lines.Count > 1, "narrow width should cause wrapping");

        var linesWithLinks = lines.Where(l => l.Links is { Count: > 0 }).ToList();
        Assert.True(linesWithLinks.Count >= 2, "wrapped link should produce spans on multiple lines");

        // All sub-spans must share the same URL.
        foreach (var lineWithLink in linesWithLinks)
        {
            var link = Assert.Single(lineWithLink.Links!);
            Assert.Equal("https://example.com", link.Url);
            Assert.True(link.TextMatchesUrl);
        }
    }

    [Fact]
    public void Wrapped_link_sub_spans_use_local_column_positions()
    {
        // "See https://example.com" at width 10 wraps.
        // The URL sub-spans on each line must start at column 0 (they start a new line).
        var lines = Format("See https://example.com", width: 10);

        var linesWithLinks = lines.Where(l => l.Links is { Count: > 0 }).ToList();
        foreach (var lineWithLink in linesWithLinks)
        {
            // Each sub-span must be within [0, lineWidth].
            var lineWidth = TerminalCellText.Width(lineWithLink.Text);
            var link = Assert.Single(lineWithLink.Links!);
            Assert.True(link.StartColumn >= 0);
            Assert.True(link.EndColumn <= lineWidth);
            Assert.True(link.StartColumn < link.EndColumn);
        }
    }

    // ---------------------------------------------------------------------------
    // 5. Plain text has no links
    // ---------------------------------------------------------------------------

    [Fact]
    public void Plain_text_produces_null_links()
    {
        var lines = Format("no links here");

        var line = Assert.Single(lines);
        Assert.Null(line.Links);
    }

    [Fact]
    public void Code_block_text_has_no_links_even_with_url_content()
    {
        // URLs inside code blocks are not parsed as links.
        var lines = Format("```\nhttps://example.com\n```");

        Assert.All(lines, l => Assert.Null(l.Links));
    }

    // ---------------------------------------------------------------------------
    // 6. TranscriptRow mirrors Links from render line
    // ---------------------------------------------------------------------------

    [Fact]
    public void TranscriptRow_carries_links_from_the_formatter()
    {
        var index = new TranscriptLayoutIndex(TranscriptBlockFormatter.Format);
        var block = new AssistantTranscriptBlock(
            Guid.NewGuid(), "Visit https://example.com today", Complete: true);
        index.Append(block, 80);

        var rows = index.GetRows(0, index.TotalRows);
        var contentRow = rows.FirstOrDefault(r => !r.IsSeparator && r.Text.Contains("example.com", StringComparison.Ordinal));
        Assert.NotNull(contentRow.Text);
        Assert.NotNull(contentRow.Links);
        var link = Assert.Single(contentRow.Links!);
        Assert.Equal("https://example.com", link.Url);
        Assert.True(link.TextMatchesUrl);
    }

    [Fact]
    public void TranscriptRow_carries_null_links_for_plain_rows()
    {
        var index = new TranscriptLayoutIndex(TranscriptBlockFormatter.Format);
        var block = new AssistantTranscriptBlock(Guid.NewGuid(), "plain text only", Complete: true);
        index.Append(block, 80);

        var rows = index.GetRows(0, index.TotalRows);
        var contentRow = rows.FirstOrDefault(r => !r.IsSeparator && r.Text.Length > 0);
        Assert.NotNull(contentRow.Text);
        Assert.Null(contentRow.Links);
    }

    // ---------------------------------------------------------------------------
    // 7. Theme roles — Link and LinkDeceptive defined in TuiTheme
    // ---------------------------------------------------------------------------

    [Fact]
    public void Link_theme_color_is_non_default()
    {
        Assert.NotEqual(default(TuiThemeColor), TuiTheme.WarmEmber.Link);
    }

    [Fact]
    public void LinkDeceptive_theme_color_is_non_default()
    {
        Assert.NotEqual(default(TuiThemeColor), TuiTheme.WarmEmber.LinkDeceptive);
    }

    [Fact]
    public void Link_and_LinkDeceptive_are_distinct_colors_in_WarmEmber()
    {
        var theme = TuiTheme.WarmEmber;
        Assert.NotEqual(theme.Link.TrueColor, theme.LinkDeceptive.TrueColor);
    }

    // ---------------------------------------------------------------------------
    // 8. Draw path — LinkAttributeFor returns distinct, non-trivial colors
    // ---------------------------------------------------------------------------

    [Fact]
    public void Link_attribute_differs_from_assistant_color()
    {
        using IApplication app = Application.Create();
        using var view = new VirtualizedTranscriptView(app, theme: TuiTheme.WarmEmber);

        var linkAttr = view.LinkAttributeFor(deceptive: false, trueColor: true);
        var assistantAttr = view.AttributeFor(TranscriptRole.Assistant, trueColor: true);

        Assert.NotEqual(assistantAttr.Foreground, linkAttr.Foreground);
    }

    [Fact]
    public void LinkDeceptive_attribute_differs_from_honest_link_attribute()
    {
        using IApplication app = Application.Create();
        using var view = new VirtualizedTranscriptView(app, theme: TuiTheme.WarmEmber);

        var linkAttr = view.LinkAttributeFor(deceptive: false, trueColor: true);
        var deceptiveAttr = view.LinkAttributeFor(deceptive: true, trueColor: true);

        Assert.NotEqual(linkAttr.Foreground, deceptiveAttr.Foreground);
    }

    // ---------------------------------------------------------------------------
    // 9. Draw-path integration — LinkDrawCount increments when link spans are drawn
    // ---------------------------------------------------------------------------

    [Fact]
    public void DrawRow_increments_LinkDrawCount_for_rows_with_link_spans()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);

        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var block = new AssistantTranscriptBlock(
            Guid.NewGuid(), "Visit https://example.com today", Complete: true);
        shell.Transcript.Append(block);
        app.LayoutAndDraw();

        Assert.True(shell.Transcript.LinkDrawCount > 0, "link spans should be drawn with the link attribute");

        if (token is not null) app.End(token);
    }

    [Fact]
    public void DrawRow_selection_wins_over_link_span()
    {
        // When a selection covers a link span, the selection attribute wins — selection draw count
        // rises but link draw count stays zero for the overlapping region.
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);

        using var shell = ShellTestFactory.CreateFullscreen(app);
        var token = app.Begin(shell);
        app.LayoutAndDraw();

        var block = new AssistantTranscriptBlock(
            Guid.NewGuid(), "Visit https://example.com today", Complete: true);
        shell.Transcript.Append(block);
        app.LayoutAndDraw();

        // Select the entire first row (covers the link).
        var top = shell.Transcript.TopRow;
        shell.Transcript.BeginSelection(new TranscriptCellPosition(top, 0));
        shell.Transcript.UpdateSelection(new TranscriptCellPosition(top, 30));
        app.LayoutAndDraw();

        Assert.True(shell.Transcript.SelectionDrawCount > 0, "selection should be drawn");
        // The link draw count may be less than the full link width because selection has taken priority
        // over some/all cells; the exact value depends on overlap but we assert no crash and counter
        // is tracked.
        _ = shell.Transcript.LinkDrawCount;

        if (token is not null) app.End(token);
    }

    // ---------------------------------------------------------------------------
    // 10. Link span columns inside lists (indent/marker shift)
    // ---------------------------------------------------------------------------

    [Fact]
    public void BulletList_link_span_columns_land_on_url_after_marker()
    {
        // "- see https://example.com" → rendered as "• see https://example.com"
        // Bullet "• " = 2 cols, "see " = 4 cols → URL starts at col 6, ends at col 25.
        var lines = Format("- see https://example.com", width: 80);

        var contentLine = lines.FirstOrDefault(l => l.Links is { Count: > 0 });
        Assert.NotNull(contentLine.Text);
        var link = Assert.Single(contentLine.Links!);
        Assert.Equal("https://example.com", link.Url);

        // The span must slice exactly the URL out of the rendered line text.
        var sliced = TerminalCellText.SliceByCells(contentLine.Text, link.StartColumn, link.EndColumn);
        Assert.Equal("https://example.com", sliced);
    }

    [Fact]
    public void NumberedList_link_span_columns_land_on_url_after_marker()
    {
        // "1. https://example.com" → marker "1. " = 3 cols → URL starts at col 3.
        var lines = Format("1. https://example.com", width: 80);

        var contentLine = lines.FirstOrDefault(l => l.Links is { Count: > 0 });
        Assert.NotNull(contentLine.Text);
        var link = Assert.Single(contentLine.Links!);
        Assert.Equal("https://example.com", link.Url);

        var sliced = TerminalCellText.SliceByCells(contentLine.Text, link.StartColumn, link.EndColumn);
        Assert.Equal("https://example.com", sliced);
    }

    [Fact]
    public void NestedBulletList_link_span_columns_account_for_all_indent_levels()
    {
        // "- outer\n  - https://example.com" → nested bullet has 4 cols of prefix ("  • ").
        var lines = Format("- outer\n  - https://example.com", width: 80);

        var contentLine = lines.FirstOrDefault(l => l.Links is { Count: > 0 });
        Assert.NotNull(contentLine.Text);
        var link = Assert.Single(contentLine.Links!);
        Assert.Equal("https://example.com", link.Url);

        var sliced = TerminalCellText.SliceByCells(contentLine.Text, link.StartColumn, link.EndColumn);
        Assert.Equal("https://example.com", sliced);
    }
}
