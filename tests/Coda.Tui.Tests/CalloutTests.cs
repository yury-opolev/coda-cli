using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using TgColor = Terminal.Gui.Drawing.Color;
using TgName = Terminal.Gui.Drawing.ColorName16;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for GitHub-style admonition callout detection, rendering, theme roles, and streaming
/// deferral. Callouts are detected from <c>&gt; [!TYPE]</c> blockquote syntax in assistant markdown
/// and rendered with a glyph title row and a <c>│ </c>-prefixed body, keyed to the five new callout
/// <see cref="TranscriptRole"/> entries and their matching <see cref="TuiTheme"/> role colors.
/// </summary>
public sealed class CalloutTests
{
    // ---------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------

    private static IReadOnlyList<TranscriptRenderLine> Format(string markdown, int width = 80) =>
        TranscriptBlockFormatter.Format(
            new AssistantTranscriptBlock(Guid.NewGuid(), markdown, Complete: true),
            width);

    // ---------------------------------------------------------------------------
    // Detection — recognized types
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("> [!NOTE]", TranscriptRole.CalloutNote, "ℹ", "NOTE")]
    [InlineData("> [!TIP]", TranscriptRole.CalloutTip, "✦", "TIP")]
    [InlineData("> [!IMPORTANT]", TranscriptRole.CalloutImportant, "‼", "IMPORTANT")]
    [InlineData("> [!WARNING]", TranscriptRole.CalloutWarning, "⚠", "WARNING")]
    [InlineData("> [!CAUTION]", TranscriptRole.CalloutCaution, "⊗", "CAUTION")]
    public void Each_callout_type_produces_a_title_row_with_glyph_and_label(
        string markdown,
        TranscriptRole expectedRole,
        string expectedGlyph,
        string expectedLabel)
    {
        var lines = Format(markdown);

        var title = Assert.Single(lines);
        Assert.Equal(expectedRole, title.Role);
        Assert.Equal($" \u25cf {expectedGlyph} {expectedLabel}", title.Text);
    }

    [Theory]
    [InlineData("> [!note]", TranscriptRole.CalloutNote)]
    [InlineData("> [!tip]", TranscriptRole.CalloutTip)]
    [InlineData("> [!important]", TranscriptRole.CalloutImportant)]
    [InlineData("> [!warning]", TranscriptRole.CalloutWarning)]
    [InlineData("> [!caution]", TranscriptRole.CalloutCaution)]
    [InlineData("> [!Note]", TranscriptRole.CalloutNote)]
    [InlineData("> [!Warning]", TranscriptRole.CalloutWarning)]
    public void Callout_detection_is_case_insensitive(string markdown, TranscriptRole expectedRole)
    {
        var lines = Format(markdown);

        var title = Assert.Single(lines);
        Assert.Equal(expectedRole, title.Role);
    }

    // ---------------------------------------------------------------------------
    // Detection — non-callout cases (fall through to plain blockquote)
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("> [!FOO]")]
    [InlineData("> [!UNKNOWN]")]
    [InlineData("> [!NOTES]")]
    public void Unknown_type_renders_as_plain_blockquote_not_a_callout(string markdown)
    {
        var lines = Format(markdown);

        Assert.Single(lines);
        // Plain blockquote renders the content in Assistant role — no callout role.
        Assert.Equal(TranscriptRole.Assistant, lines[0].Role);
        // The text should contain the marker text, not a glyph title.
        Assert.False(
            lines[0].Role is
                TranscriptRole.CalloutNote or TranscriptRole.CalloutTip or
                TranscriptRole.CalloutImportant or TranscriptRole.CalloutWarning or
                TranscriptRole.CalloutCaution,
            "Unknown [!FOO] must not render as any callout type");
    }

    [Fact]
    public void Marker_with_trailing_text_on_same_line_is_not_a_callout()
    {
        // Trailing text after [!NOTE] on the same blockquote line → plain quote.
        var lines = Format("> [!NOTE] trailing text");

        Assert.Equal(TranscriptRole.Assistant, lines[0].Role);
        Assert.DoesNotContain(lines, l => l.Role == TranscriptRole.CalloutNote);
    }

    [Fact]
    public void Marker_with_inline_emphasis_on_same_line_is_not_a_callout()
    {
        // "[!NOTE] *em*" — the *em* is an EmphasisInline, not a LineBreak → plain quote.
        var lines = Format("> [!NOTE] *em*");

        Assert.True(lines.Count >= 1);
        Assert.Equal(TranscriptRole.Assistant, lines[0].Role);
        Assert.DoesNotContain(lines, l => l.Role == TranscriptRole.CalloutNote);
    }

    [Fact]
    public void Marker_with_inline_code_on_same_line_is_not_a_callout()
    {
        // "[!WARNING] `code`" — the `code` is a CodeInline → plain quote.
        var lines = Format("> [!WARNING] `code`");

        Assert.True(lines.Count >= 1);
        Assert.Equal(TranscriptRole.Assistant, lines[0].Role);
        Assert.DoesNotContain(lines, l => l.Role == TranscriptRole.CalloutWarning);
    }

    [Fact]
    public void Marker_with_inline_link_on_same_line_is_not_a_callout()
    {
        // "[!TIP] [x](y)" — the [x](y) is a LinkInline → plain quote.
        var lines = Format("> [!TIP] [x](y)");

        Assert.True(lines.Count >= 1);
        Assert.Equal(TranscriptRole.Assistant, lines[0].Role);
        Assert.DoesNotContain(lines, l => l.Role == TranscriptRole.CalloutTip);
    }

    [Fact]
    public void Genuine_callout_with_newline_body_is_still_detected()
    {
        // Regression: "[!NOTE]\n> body" — the marker is alone on the first line → callout.
        var lines = Format("> [!NOTE]\n> body");

        Assert.True(lines.Count >= 1);
        Assert.Equal(TranscriptRole.CalloutNote, lines[0].Role);
    }

    [Fact]
    public void Marker_not_on_first_line_is_not_a_callout()
    {
        // The [!NOTE] marker appears on the second paragraph — the first line is "text".
        var lines = Format("> text\n>\n> [!NOTE]");

        Assert.DoesNotContain(lines, l => l.Role == TranscriptRole.CalloutNote);
    }

    // ---------------------------------------------------------------------------
    // Rendering — body handling
    // ---------------------------------------------------------------------------

    [Fact]
    public void Marker_only_callout_produces_title_row_only()
    {
        var lines = Format("> [!NOTE]");

        var title = Assert.Single(lines);
        Assert.Equal(TranscriptRole.CalloutNote, title.Role);
    }

    [Fact]
    public void Callout_with_body_produces_title_then_bar_prefixed_body_rows()
    {
        var lines = Format("> [!NOTE]\n> body text here");

        Assert.True(lines.Count >= 2, "callout with body must have at least title + body rows");
        // Title row.
        Assert.Equal(TranscriptRole.CalloutNote, lines[0].Role);
        Assert.StartsWith(" \u25cf \u2139 ", lines[0].Text);
        // Body rows: │ prefix + Assistant role.
        for (var i = 1; i < lines.Count; i++)
        {
            Assert.Equal(TranscriptRole.Assistant, lines[i].Role);
            Assert.StartsWith("   \u2502 ", lines[i].Text);
        }
    }

    [Fact]
    public void Body_text_appears_after_the_bar_prefix()
    {
        var lines = Format("> [!TIP]\n> remember this", 80);

        Assert.True(lines.Count >= 2);
        // The body line is "   │ remember this"
        var bodyLine = lines[1];
        Assert.Equal(TranscriptRole.Assistant, bodyLine.Role);
        Assert.Equal("   \u2502 remember this", bodyLine.Text);
    }

    [Fact]
    public void Body_wraps_under_the_bar_within_available_width()
    {
        // Width 12, bar prefix "│ " = 2 cells, so content width = 10 per body line.
        // "longword1234 more" should wrap such that each wrapped line starts with "│ ".
        var lines = Format("> [!NOTE]\n> longword1234 more", width: 12);

        Assert.True(lines.Count >= 3, "text should wrap under the bar");
        // All body rows have the bar prefix.
        foreach (var line in lines.Skip(1))
        {
            Assert.StartsWith("   \u2502 ", line.Text);
            Assert.Equal(TranscriptRole.Assistant, line.Role);
        }
    }

    [Fact]
    public void Callout_with_multiblock_body_renders_bar_on_all_body_lines()
    {
        // Two paragraphs inside the quote: marker paragraph + a second paragraph body.
        var lines = Format("> [!IMPORTANT]\n>\n> Second paragraph body.");

        Assert.Equal(TranscriptRole.CalloutImportant, lines[0].Role);
        // Every non-title row must have the bar prefix.
        foreach (var bodyLine in lines.Skip(1).Where(l => !string.IsNullOrEmpty(l.Text)))
        {
            Assert.StartsWith("   \u2502 ", bodyLine.Text);
        }
    }

    [Fact]
    public void Warning_callout_has_correct_glyph_and_label()
    {
        var lines = Format("> [!WARNING]");

        var title = Assert.Single(lines);
        Assert.Equal(TranscriptRole.CalloutWarning, title.Role);
        Assert.Equal(" \u25cf \u26a0 WARNING", title.Text);
    }

    [Fact]
    public void Caution_callout_has_correct_glyph_and_label()
    {
        var lines = Format("> [!CAUTION]");

        var title = Assert.Single(lines);
        Assert.Equal(TranscriptRole.CalloutCaution, title.Role);
        Assert.Equal(" \u25cf \u2297 CAUTION", title.Text);
    }

    // ---------------------------------------------------------------------------
    // Glyph — ASCII fallback constants
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(TranscriptRole.CalloutNote, "ℹ", "i")]
    [InlineData(TranscriptRole.CalloutTip, "✦", "*")]
    [InlineData(TranscriptRole.CalloutImportant, "‼", "!!")]
    [InlineData(TranscriptRole.CalloutWarning, "⚠", "!")]
    [InlineData(TranscriptRole.CalloutCaution, "⊗", "x")]
    public void Callout_glyph_returns_unicode_and_ascii_forms(
        TranscriptRole role,
        string expectedUnicode,
        string expectedAscii)
    {
        Assert.Equal(expectedUnicode, TranscriptBlockFormatter.CalloutGlyph(role, ascii: false));
        Assert.Equal(expectedAscii, TranscriptBlockFormatter.CalloutGlyph(role, ascii: true));
    }

    // ---------------------------------------------------------------------------
    // Theme roles — attribute resolution (true-color and 16-color paths)
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(TranscriptRole.CalloutNote)]
    [InlineData(TranscriptRole.CalloutTip)]
    [InlineData(TranscriptRole.CalloutImportant)]
    [InlineData(TranscriptRole.CalloutWarning)]
    [InlineData(TranscriptRole.CalloutCaution)]
    public void Callout_roles_resolve_to_distinct_non_default_truecolor_via_theme(TranscriptRole role)
    {
        using IApplication app = Application.Create();
        using var view = new VirtualizedTranscriptView(app, theme: TuiTheme.WarmEmber);

        var color = view.AttributeFor(role, trueColor: true).Foreground;

        // Each callout role has a non-trivially-zero truecolor.
        Assert.NotEqual(default(TgColor), color);
        // And distinct from the plain assistant color.
        Assert.NotEqual(
            TuiTheme.Resolve(TuiTheme.WarmEmber.TranscriptAssistant, trueColor: true),
            color);
    }

    [Theory]
    [InlineData(TranscriptRole.CalloutNote, TgName.BrightBlue)]
    [InlineData(TranscriptRole.CalloutTip, TgName.BrightGreen)]
    [InlineData(TranscriptRole.CalloutImportant, TgName.BrightMagenta)]
    [InlineData(TranscriptRole.CalloutWarning, TgName.Yellow)]
    [InlineData(TranscriptRole.CalloutCaution, TgName.BrightRed)]
    public void Callout_roles_resolve_to_named_16_color_fallbacks(
        TranscriptRole role,
        TgName expectedFallback)
    {
        using IApplication app = Application.Create();
        using var view = new VirtualizedTranscriptView(app, theme: TuiTheme.WarmEmber);

        var color = view.AttributeFor(role, trueColor: false).Foreground;

        Assert.Equal(new TgColor(expectedFallback), color);
    }

    [Fact]
    public void Callout_roles_in_warm_ember_are_all_distinct_from_each_other()
    {
        var theme = TuiTheme.WarmEmber;
        var colors = new[]
        {
            theme.CalloutNote.TrueColor,
            theme.CalloutTip.TrueColor,
            theme.CalloutImportant.TrueColor,
            theme.CalloutWarning.TrueColor,
            theme.CalloutCaution.TrueColor,
        };

        Assert.Equal(colors.Length, colors.Distinct().Count());
    }

    // ---------------------------------------------------------------------------
    // Streaming — blockquote deferred sealing ensures no broken callout title
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("> [!NOTE]\n> body text\n\nafter")]
    [InlineData("> [!WARNING]\n> warning body")]
    [InlineData("> [!TIP]")]
    [InlineData("> [!IMPORTANT]\n> line one\n> line two")]
    [InlineData("> [!CAUTION]\n> danger!\n\nparagraph after")]
    public void Callout_streams_identically_to_full_format_at_every_prefix(string finalText)
    {
        // Re-use the differential assertion pattern from IncrementalMarkdownFormatterTests:
        // at every prefix the incremental formatter must equal a full re-parse.
        var formatter = new IncrementalMarkdownFormatter();
        var id = Guid.NewGuid();

        for (var p = 0; p <= finalText.Length; p++)
        {
            var prefix = finalText[..p];
            var actual = formatter.Update(id, prefix, width: 40);
            var expected = TranscriptBlockFormatter.Format(
                new AssistantTranscriptBlock(Guid.Empty, prefix, Complete: false),
                width: 40);

            Assert.True(
                expected.SequenceEqual(actual),
                $"Mismatch at prefix length {p} (\"{prefix.Replace("\n", "\\n")}\")\n" +
                $"expected: [{string.Join(" | ", expected.Select(l => $"\"{l.Text}\"/{l.Role}"))}]\n" +
                $"actual:   [{string.Join(" | ", actual.Select(l => $"\"{l.Text}\"/{l.Role}"))}]");
        }
    }

    [Fact]
    public void Partial_callout_marker_does_not_render_as_a_recognized_callout()
    {
        // While streaming, "> [!WARN" is not yet a complete callout marker.
        var partialLines = Format("> [!WARN");

        // Must render as a plain blockquote (Assistant role), not a CalloutWarning title.
        Assert.DoesNotContain(partialLines, l => l.Role == TranscriptRole.CalloutWarning);
    }

    // ---------------------------------------------------------------------------
    // Glyph width — all callout glyphs must be 1 display cell (guards against future
    // emoji-default-presentation regressions like the old ⛔ U+26D4 CAUTION glyph).
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(TranscriptRole.CalloutNote)]
    [InlineData(TranscriptRole.CalloutTip)]
    [InlineData(TranscriptRole.CalloutImportant)]
    [InlineData(TranscriptRole.CalloutWarning)]
    [InlineData(TranscriptRole.CalloutCaution)]
    public void Every_callout_glyph_occupies_exactly_one_display_cell(TranscriptRole role)
    {
        var glyph = TranscriptBlockFormatter.CalloutGlyph(role, ascii: false);

        // TerminalCellText.Width(single-grapheme) == ElementWidth(grapheme).
        // A width > 1 would corrupt the renderer's cell math.
        Assert.Equal(1, TerminalCellText.Width(glyph));
    }

    // ---------------------------------------------------------------------------
    // Prefix color — body rows must expose PrefixCells > 0 and the callout PrefixRole
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("> [!NOTE]\n> body text", TranscriptRole.CalloutNote)]
    [InlineData("> [!TIP]\n> tip body", TranscriptRole.CalloutTip)]
    [InlineData("> [!IMPORTANT]\n> important body", TranscriptRole.CalloutImportant)]
    [InlineData("> [!WARNING]\n> warning body", TranscriptRole.CalloutWarning)]
    [InlineData("> [!CAUTION]\n> caution body", TranscriptRole.CalloutCaution)]
    public void Callout_body_rows_have_PrefixCells_set_to_callout_role(
        string markdown, TranscriptRole calloutRole)
    {
        var lines = Format(markdown, width: 80);

        // Line 0 is the title row; body rows follow.
        Assert.True(lines.Count >= 2);
        Assert.Equal(calloutRole, lines[0].Role);

        foreach (var bodyLine in lines.Skip(1))
        {
            Assert.Equal(TranscriptRole.Assistant, bodyLine.Role);
            Assert.StartsWith("   \u2502 ", bodyLine.Text);
            // PrefixCells covers gutter (3 cells "   ") + the callout bar "│ " (2 cells).
            Assert.Equal(TerminalCellText.Width("   \u2502 "), bodyLine.PrefixCells);
            // PrefixRole must be the callout role so the bar is drawn in the callout color.
            Assert.Equal(calloutRole, bodyLine.PrefixRole);
        }
    }

    [Fact]
    public void Callout_body_prefix_color_attribute_differs_from_assistant_color()
    {
        // Verify that the callout PrefixRole resolves to a different attribute than Assistant,
        // so the bar actually draws in a distinct color at paint time.
        using IApplication app = Application.Create();
        using var view = new VirtualizedTranscriptView(app, theme: TuiTheme.WarmEmber);

        var lines = Format("> [!WARNING]\n> important body", width: 80);
        Assert.True(lines.Count >= 2);

        var bodyLine = lines[1];
        Assert.Equal(TranscriptRole.CalloutWarning, bodyLine.PrefixRole);

        var barAttr = view.AttributeFor(bodyLine.PrefixRole, trueColor: true);
        var textAttr = view.AttributeFor(bodyLine.Role, trueColor: true);

        // The bar draws in a distinct foreground color from the body text.
        Assert.NotEqual(barAttr.Foreground, textAttr.Foreground);
    }

    // ---------------------------------------------------------------------------
    // Links in callout first paragraph body
    // ---------------------------------------------------------------------------

    [Fact]
    public void Callout_first_paragraph_bare_URL_produces_LinkSpan_with_bar_shifted_columns()
    {
        // Markdig parses [!NOTE] + body into ONE paragraph; the whole body must go through the
        // link-aware path so that https://… URLs inside it become clickable LinkSpans.
        var lines = Format("> [!NOTE]\n> See https://example.com for details.", width: 80);

        Assert.True(lines.Count >= 2, "must have title + at least one body row");
        Assert.Equal(TranscriptRole.CalloutNote, lines[0].Role);

        var bodyRow = lines[1];
        Assert.NotNull(bodyRow.Links);
        var link = Assert.Single(bodyRow.Links!);
        Assert.Equal("https://example.com", link.Url);
        Assert.True(link.TextMatchesUrl);
        // The body row text is "   │ See https://..." — gutter "   " (3 cells) + bar "│ " (2 cells) + "See " (4 cells).
        var gutterWidth = TranscriptGlyphs.MarkerCells;
        var barWidth = TerminalCellText.Width("\u2502 ");
        var prefixWidth = TerminalCellText.Width("See ");
        Assert.Equal(gutterWidth + barWidth + prefixWidth, link.StartColumn);
        Assert.Equal(gutterWidth + barWidth + prefixWidth + TerminalCellText.Width("https://example.com"), link.EndColumn);
    }

    [Fact]
    public void Callout_first_paragraph_deceptive_link_has_TextMatchesUrl_false_and_warning_marker()
    {
        // A deceptive markdown link inside a callout body must set TextMatchesUrl=false
        // and inject the ⚠ marker exactly like it does in normal paragraphs.
        var lines = Format("> [!WARNING]\n> [Click here](https://example.com)", width: 80);

        Assert.True(lines.Count >= 2);
        Assert.Equal(TranscriptRole.CalloutWarning, lines[0].Role);

        var bodyRow = lines[1];
        Assert.NotNull(bodyRow.Links);
        var link = Assert.Single(bodyRow.Links!);
        Assert.Equal("https://example.com", link.Url);
        Assert.False(link.TextMatchesUrl, "deceptive link: display text differs from URL");
        Assert.Contains("⚠", bodyRow.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Callout_first_paragraph_honest_link_is_clickable_with_bar_shifted_start()
    {
        // An honest explicit link in the first paragraph is clickable and its columns
        // are shifted right by the bar prefix (so StartColumn ≥ bar width).
        var lines = Format("> [!TIP]\n> Visit https://tip.example.com now", width: 80);

        Assert.True(lines.Count >= 2);
        var bodyRow = lines[1];
        Assert.NotNull(bodyRow.Links);
        var link = Assert.Single(bodyRow.Links!);
        Assert.Equal("https://tip.example.com", link.Url);
        Assert.True(link.TextMatchesUrl);
        // The bar "│ " is at least 2 cells; the link must appear after it.
        Assert.True(link.StartColumn >= TerminalCellText.Width("│ "),
            $"link StartColumn {link.StartColumn} must be ≥ bar width 2 (bar-shifted)");
    }
}
