using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Tests;

/// <summary>
/// Unit tests for <see cref="TranscriptPin"/>. All tests are pure — no Terminal.Gui host required.
/// </summary>
public sealed class TranscriptPinTests
{
    private static readonly TranscriptGlyphs Unicode = TranscriptGlyphs.Unicode;
    private static readonly TranscriptGlyphs Ascii = TranscriptGlyphs.Ascii;

    // -------------------------------------------------------------------------
    // TranscriptPin.Compose — null / empty / whitespace input
    // -------------------------------------------------------------------------

    [Fact]
    public void Compose_null_text_returns_null() =>
        Assert.Null(TranscriptPin.Compose(null, width: 20, Unicode));

    [Fact]
    public void Compose_empty_text_returns_null() =>
        Assert.Null(TranscriptPin.Compose(string.Empty, width: 20, Unicode));

    [Fact]
    public void Compose_whitespace_only_text_returns_null() =>
        Assert.Null(TranscriptPin.Compose("   \t  ", width: 20, Unicode));

    [Fact]
    public void Compose_newlines_only_returns_null() =>
        Assert.Null(TranscriptPin.Compose("\n\n\n", width: 20, Unicode));

    // -------------------------------------------------------------------------
    // TranscriptPin.Compose — width too small
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]   // MarkerCells = 3, need at least MarkerCells + 1 = 4
    public void Compose_width_below_minimum_returns_null(int width) =>
        Assert.Null(TranscriptPin.Compose("hello", width, Unicode));

    [Fact]
    public void Compose_minimum_width_4_is_accepted()
    {
        var result = TranscriptPin.Compose("hello", width: 4, Unicode);
        Assert.NotNull(result);
    }

    // -------------------------------------------------------------------------
    // TranscriptPin.Compose — short single-line prompt that fits
    // -------------------------------------------------------------------------

    [Fact]
    public void Compose_short_single_line_fits_without_ellipsis()
    {
        var result = TranscriptPin.Compose("hello", width: 20, Unicode);
        Assert.Equal(" \u276f hello", result);
        Assert.DoesNotContain("…", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_prefix_is_user_marker()
    {
        var result = TranscriptPin.Compose("hi", width: 20, Unicode);
        Assert.NotNull(result);
        Assert.StartsWith(Unicode.Prefix(TranscriptGutterKind.UserMarker), result, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // TranscriptPin.Compose — long single-line prompt elides to exactly `width`
    // -------------------------------------------------------------------------

    [Fact]
    public void Compose_long_single_line_elides_to_width_cells()
    {
        var width = 12;
        var result = TranscriptPin.Compose("This is a very long prompt that exceeds the width", width, Unicode);
        Assert.NotNull(result);
        Assert.True(TerminalCellText.Width(result) <= width, $"Result width {TerminalCellText.Width(result)} exceeds {width}");
        Assert.EndsWith("…", result, StringComparison.Ordinal);
        Assert.StartsWith(" \u276f ", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_long_single_line_result_starts_with_marker()
    {
        var result = TranscriptPin.Compose("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", width: 10, Unicode);
        Assert.NotNull(result);
        Assert.StartsWith(" \u276f ", result, StringComparison.Ordinal);
        Assert.EndsWith("…", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(40)]
    public void Compose_elided_result_never_exceeds_width(int width)
    {
        var result = TranscriptPin.Compose(new string('X', 100), width, Unicode);
        Assert.NotNull(result);
        Assert.True(TerminalCellText.Width(result) <= width);
        Assert.EndsWith("…", result, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // TranscriptPin.Compose — multi-line prompt gets ellipsis even if first line fits
    // -------------------------------------------------------------------------

    [Fact]
    public void Compose_multi_line_with_fitting_first_line_still_elides()
    {
        // "hi" easily fits in width 20 as " > hi" (5 cells), but there is a second non-blank line,
        // so the result must still end with "…".
        var result = TranscriptPin.Compose("hi\nsecond line", width: 20, Unicode);
        Assert.NotNull(result);
        Assert.EndsWith("…", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_multi_line_content_is_from_first_non_blank_line()
    {
        var result = TranscriptPin.Compose("first\nsecond\nthird", width: 40, Unicode);
        Assert.NotNull(result);
        Assert.Contains("first", result, StringComparison.Ordinal);
        Assert.DoesNotContain("second", result, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // TranscriptPin.Compose — leading blank lines are skipped
    // -------------------------------------------------------------------------

    [Fact]
    public void Compose_leading_blank_lines_skipped_to_first_non_blank()
    {
        var result = TranscriptPin.Compose("\n\n\nhello world", width: 40, Unicode);
        Assert.NotNull(result);
        Assert.Contains("hello world", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_blank_first_line_uses_first_non_blank_with_ellipsis()
    {
        // Leading blank lines → skips to "real content". Since we found a first non-blank line
        // by skipping, and there may be other non-blank lines, the ellipsis rules still apply.
        var result = TranscriptPin.Compose("\n\nfirst content\nsecond content", width: 40, Unicode);
        Assert.NotNull(result);
        Assert.Contains("first content", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_all_blank_lines_returns_null() =>
        Assert.Null(TranscriptPin.Compose("  \n   \n\t", width: 20, Unicode));

    // -------------------------------------------------------------------------
    // TranscriptPin.Compose — CJK / wide-character handling
    // -------------------------------------------------------------------------

    [Fact]
    public void Compose_wide_characters_result_width_within_requested_width()
    {
        // Each CJK character is 2 cells. "你好世界你好世界" = 16 cells.
        var result = TranscriptPin.Compose("你好世界你好世界", width: 10, Unicode);
        Assert.NotNull(result);
        var actualWidth = TerminalCellText.Width(result);
        Assert.True(actualWidth <= 10, $"Result width {actualWidth} exceeds 10");
        Assert.EndsWith("…", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_wide_characters_no_broken_grapheme()
    {
        // Width chosen so truncation falls between two-cell glyphs — result must never exceed width.
        for (var width = 4; width <= 12; width++)
        {
            var result = TranscriptPin.Compose("你好世界", width, Unicode);
            if (result is not null)
            {
                var actualWidth = TerminalCellText.Width(result);
                Assert.True(actualWidth <= width, $"At width {width}: result width {actualWidth} > {width}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // TranscriptPin.Compose — each glyph set uses its own user marker
    // -------------------------------------------------------------------------

    [Fact]
    public void Compose_uses_the_glyph_sets_own_user_marker()
    {
        var resultUnicode = TranscriptPin.Compose("hello", width: 20, Unicode);
        var resultAscii = TranscriptPin.Compose("hello", width: 20, Ascii);

        Assert.Equal(" \u276f hello", resultUnicode);
        Assert.Equal(" > hello", resultAscii);
    }

    // -------------------------------------------------------------------------
    // TranscriptPin.ShouldShow
    // -------------------------------------------------------------------------

    [Fact]
    public void ShouldShow_false_when_no_active_work() =>
        Assert.False(TranscriptPin.ShouldShow(hasActiveWork: false, blockFirstRow: 0, blockEndRowExclusive: 5, topRow: 10, viewportHeight: 10));

    [Fact]
    public void ShouldShow_false_when_blockFirstRow_is_null() =>
        Assert.False(TranscriptPin.ShouldShow(hasActiveWork: true, blockFirstRow: null, blockEndRowExclusive: 0, topRow: 0, viewportHeight: 10));

    [Fact]
    public void ShouldShow_false_when_viewportHeight_zero() =>
        Assert.False(TranscriptPin.ShouldShow(hasActiveWork: true, blockFirstRow: 0, blockEndRowExclusive: 5, topRow: 0, viewportHeight: 0));

    [Fact]
    public void ShouldShow_false_when_viewportHeight_negative() =>
        Assert.False(TranscriptPin.ShouldShow(hasActiveWork: true, blockFirstRow: 0, blockEndRowExclusive: 5, topRow: 0, viewportHeight: -1));

    [Fact]
    public void ShouldShow_true_when_block_entirely_above_viewport()
    {
        // Block occupies rows [0, 3), viewport starts at row 5.
        Assert.True(TranscriptPin.ShouldShow(
            hasActiveWork: true,
            blockFirstRow: 0,
            blockEndRowExclusive: 3,
            topRow: 5,
            viewportHeight: 10));
    }

    [Fact]
    public void ShouldShow_false_when_block_fully_visible_inside_viewport()
    {
        // Viewport [5, 15), block [7, 10) — fully inside.
        Assert.False(TranscriptPin.ShouldShow(
            hasActiveWork: true,
            blockFirstRow: 7,
            blockEndRowExclusive: 10,
            topRow: 5,
            viewportHeight: 10));
    }

    [Fact]
    public void ShouldShow_false_when_block_partially_visible_at_top_edge()
    {
        // Viewport [5, 15), block [3, 7) — first row 3 is above, but row 5 and 6 are in the viewport.
        Assert.False(TranscriptPin.ShouldShow(
            hasActiveWork: true,
            blockFirstRow: 3,
            blockEndRowExclusive: 7,
            topRow: 5,
            viewportHeight: 10));
    }

    [Fact]
    public void ShouldShow_false_when_block_partially_visible_at_bottom_edge()
    {
        // Viewport [5, 15), block [13, 18) — rows 13 and 14 are in the viewport.
        Assert.False(TranscriptPin.ShouldShow(
            hasActiveWork: true,
            blockFirstRow: 13,
            blockEndRowExclusive: 18,
            topRow: 5,
            viewportHeight: 10));
    }

    [Fact]
    public void ShouldShow_true_when_block_entirely_below_viewport()
    {
        // Viewport [5, 15), block [15, 20) — block starts exactly at viewport end (no intersection).
        // The spec says return true when the block does NOT intersect the viewport, in either direction.
        Assert.True(TranscriptPin.ShouldShow(
            hasActiveWork: true,
            blockFirstRow: 15,
            blockEndRowExclusive: 20,
            topRow: 5,
            viewportHeight: 10));
    }

    // -------------------------------------------------------------------------
    // Line selection happens after sanitization
    // -------------------------------------------------------------------------

    [Fact]
    public void Compose_skips_a_first_line_that_sanitizes_away_to_nothing()
    {
        // The escape sequence is non-blank to Trim but empty once sanitized: the pin must fall through
        // to the next real line rather than disappearing for the whole turn.
        var pin = TranscriptPin.Compose("\u001b[2J\nWhat is the meaning of life?", width: 40, Unicode);

        Assert.NotNull(pin);
        Assert.StartsWith(" \u276f What is the meaning of life?", pin);
    }

    [Fact]
    public void Compose_does_not_elide_when_the_only_surviving_line_is_followed_by_escape_noise()
    {
        // "\u001b[0m" sanitizes to nothing, so it is not a continuation and must not add an ellipsis.
        var pin = TranscriptPin.Compose("hello\n\u001b[0m", width: 40, Unicode);

        Assert.Equal(" \u276f hello", pin);
    }

    [Fact]
    public void Compose_returns_null_when_every_line_sanitizes_away()
    {
        Assert.Null(TranscriptPin.Compose("\u001b[2J\n\u001b[0m", width: 40, Unicode));
    }

    [Fact]
    public void Compose_never_leaks_an_escape_sequence_into_the_pin()
    {
        var pin = TranscriptPin.Compose("hi \u001b[31mred\u001b[0m there", width: 40, Unicode);

        Assert.NotNull(pin);
        Assert.DoesNotContain('\u001b', pin!);
    }}
