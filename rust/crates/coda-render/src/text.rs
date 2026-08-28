//! Terminal text measurement and wrapping.
//!
//! Terminal layout is measured in cells, not characters. A single user-perceived
//! character (grapheme cluster) can occupy zero, one, or two cells, so all width
//! arithmetic goes through here rather than using `str::len` or `chars().count()`.

use unicode_segmentation::UnicodeSegmentation;
use unicode_width::UnicodeWidthStr;

/// Columns a tab advances to the next multiple of.
pub const TAB_WIDTH: usize = 4;

/// Display width of a string in terminal cells.
///
/// Measured per grapheme cluster and clamped to `[1, 2]`, matching how the
/// terminal driver advances the cursor. Summing per-character widths would be
/// wrong: a ZWJ emoji sequence such as `👨‍👩‍👧` is several wide characters but
/// occupies only two cells.
pub fn width(text: &str) -> usize {
    text.graphemes(true).map(grapheme_width).sum()
}

/// Display width of a single grapheme cluster, clamped to `[1, 2]`.
pub fn grapheme_width(grapheme: &str) -> usize {
    // Fast path: printable ASCII is always one cell.
    let mut chars = grapheme.chars();
    if let (Some(c), None) = (chars.next(), chars.next()) {
        if (' '..'\u{7F}').contains(&c) {
            return 1;
        }
    }
    if grapheme.width() > 1 {
        2
    } else {
        1
    }
}

/// Expands tabs to spaces so that widths are stable and wrapping is predictable.
pub fn expand_tabs(text: &str) -> String {
    if !text.contains('\t') {
        return text.to_string();
    }

    let mut out = String::with_capacity(text.len());
    let mut column = 0usize;
    for grapheme in text.graphemes(true) {
        if grapheme == "\t" {
            let advance = TAB_WIDTH - (column % TAB_WIDTH);
            out.extend(std::iter::repeat(' ').take(advance));
            column += advance;
        } else {
            out.push_str(grapheme);
            column += grapheme_width(grapheme);
        }
    }
    out
}

/// Truncates to `max_width` cells, appending an ellipsis when text was removed.
///
/// The result never exceeds `max_width` cells, including the ellipsis.
pub fn truncate_with_ellipsis(text: &str, max_width: usize) -> String {
    if width(text) <= max_width {
        return text.to_string();
    }
    if max_width == 0 {
        return String::new();
    }
    if max_width == 1 {
        return "…".to_string();
    }

    let budget = max_width - 1;
    let mut out = String::new();
    let mut used = 0usize;
    for grapheme in text.graphemes(true) {
        let w = grapheme_width(grapheme);
        if used + w > budget {
            break;
        }
        out.push_str(grapheme);
        used += w;
    }
    out.push('…');
    out
}

/// Truncates to `max_width` cells without adding any marker.
pub fn truncate(text: &str, max_width: usize) -> String {
    if width(text) <= max_width {
        return text.to_string();
    }
    let mut out = String::new();
    let mut used = 0usize;
    for grapheme in text.graphemes(true) {
        let w = grapheme_width(grapheme);
        if used + w > max_width {
            break;
        }
        out.push_str(grapheme);
        used += w;
    }
    out
}

/// Wraps text to `max_width` cells, breaking at ASCII spaces.
///
/// Mirrors the C# formatter: only `' '` is a break opportunity, words are
/// rejoined with a single space, and a word wider than the budget is hard-split
/// at grapheme boundaries rather than allowed to overflow. Returns at least one
/// line.
pub fn wrap(text: &str, max_width: usize) -> Vec<String> {
    if max_width == 0 {
        return vec![String::new()];
    }

    let mut lines = Vec::new();
    let mut current = String::new();
    let mut current_width = 0usize;

    for word in text.split(' ') {
        if word.is_empty() {
            // A run of spaces: only meaningful mid-line, where it separates
            // words. Leading spaces on a wrapped line are dropped.
            if current_width > 0 && current_width < max_width {
                current.push(' ');
                current_width += 1;
            }
            continue;
        }

        let word_width = width(word);
        let separator = usize::from(current_width > 0 && !current.ends_with(' '));

        if current_width + separator + word_width <= max_width {
            if separator == 1 {
                current.push(' ');
                current_width += 1;
            }
            current.push_str(word);
            current_width += word_width;
            continue;
        }

        if current_width > 0 {
            lines.push(std::mem::take(&mut current));
            current_width = 0;
        }

        if word_width <= max_width {
            current.push_str(word);
            current_width = word_width;
            continue;
        }

        // A single word wider than the budget: hard-split at grapheme
        // boundaries so a cluster is never torn in half.
        for grapheme in word.graphemes(true) {
            let w = grapheme_width(grapheme);
            if current_width + w > max_width {
                lines.push(std::mem::take(&mut current));
                current_width = 0;
            }
            current.push_str(grapheme);
            current_width += w;
        }
    }

    lines.push(current);
    lines
}

/// Hard-wraps without looking for word boundaries.
///
/// Used for preformatted content (code blocks, command output) where collapsing
/// runs of spaces or breaking at spaces would corrupt the layout.
pub fn wrap_preformatted(text: &str, max_width: usize) -> Vec<String> {
    if max_width == 0 {
        return vec![String::new()];
    }

    let mut lines = Vec::new();
    let mut current = String::new();
    let mut current_width = 0usize;

    for grapheme in text.graphemes(true) {
        let w = grapheme_width(grapheme);
        if current_width + w > max_width {
            lines.push(std::mem::take(&mut current));
            current_width = 0;
        }
        current.push_str(grapheme);
        current_width += w;
    }

    lines.push(current);
    lines
}

/// Strips control characters that would corrupt terminal layout.
///
/// Tool output is arbitrary process output, so it can contain escape sequences,
/// carriage returns and tabs. Anything that moves the cursor is removed rather
/// than escaped: the transcript owns the screen, and a stray `ESC [2J` from a
/// subprocess must not be able to clear it.
pub fn sanitize(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut chars = text.chars().peekable();

    while let Some(c) = chars.next() {
        match c {
            '\u{1b}' => {
                // Drop an ANSI escape sequence up to its final byte.
                if chars.peek() == Some(&'[') {
                    chars.next();
                    for next in chars.by_ref() {
                        if ('\u{40}'..='\u{7E}').contains(&next) {
                            break;
                        }
                    }
                } else {
                    chars.next();
                }
            }
            '\t' => out.push_str(&" ".repeat(TAB_WIDTH)),
            '\r' => {}
            '\n' => out.push('\n'),
            c if (c.is_control() && c != '\n') || c == '\u{7f}' => {}
            c => out.push(c),
        }
    }

    out
}

/// Wraps text and prefixes continuation lines with a hanging indent.
pub fn wrap_with_hanging_indent(text: &str, max_width: usize, indent: usize) -> Vec<String> {
    if indent >= max_width {
        return wrap(text, max_width);
    }

    let mut lines = wrap(text, max_width);
    if lines.len() <= 1 {
        return lines;
    }

    // Re-wrap the tail at the narrower budget so the indent does not push
    // content past the right edge.
    let head = lines.remove(0);
    let consumed = head.chars().count();
    let rest: String = text.chars().skip(consumed).collect();
    let rest = rest.trim_start();

    let pad = " ".repeat(indent);
    let mut out = vec![head];
    for line in wrap(rest, max_width - indent) {
        out.push(format!("{pad}{line}"));
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn measures_ascii_by_character_count() {
        assert_eq!(width("hello"), 5);
    }

    #[test]
    fn measures_wide_cjk_as_two_cells() {
        assert_eq!(width("日本語"), 6);
    }

    #[test]
    fn measures_emoji_as_two_cells() {
        assert_eq!(width("🚀"), 2);
    }

    #[test]
    fn measures_combining_marks_as_a_single_cell() {
        // "e" followed by a combining acute accent.
        assert_eq!(width("e\u{0301}"), 1);
    }

    #[test]
    fn expands_tabs_to_the_next_stop() {
        assert_eq!(expand_tabs("a\tb"), "a   b");
        assert_eq!(expand_tabs("ab\tc"), "ab  c");
        assert_eq!(expand_tabs("abc\td"), "abc d");
        assert_eq!(expand_tabs("abcd\te"), "abcd    e");
    }

    #[test]
    fn leaves_tab_free_text_untouched() {
        assert_eq!(expand_tabs("plain"), "plain");
    }

    #[test]
    fn wraps_at_word_boundaries() {
        assert_eq!(
            wrap("the quick brown fox", 10),
            vec!["the quick", "brown fox"]
        );
    }

    #[test]
    fn measures_a_zwj_emoji_sequence_as_one_cluster() {
        // Summing per-character widths would give 6; the terminal advances 2.
        assert_eq!(width("👨‍👩‍👧"), 2);
    }

    #[test]
    fn sanitize_strips_ansi_escape_sequences() {
        assert_eq!(sanitize("\u{1b}[31mred\u{1b}[0m"), "red");
    }

    #[test]
    fn sanitize_strips_a_screen_clearing_sequence() {
        assert_eq!(sanitize("before\u{1b}[2Jafter"), "beforeafter");
    }

    #[test]
    fn sanitize_expands_tabs_and_drops_carriage_returns() {
        assert_eq!(sanitize("a\tb\r\nc"), "a    b\nc");
    }

    #[test]
    fn sanitize_keeps_newlines_and_normal_text() {
        assert_eq!(sanitize("line one\nline two"), "line one\nline two");
    }

    #[test]
    fn sanitize_strips_bare_control_characters() {
        assert_eq!(sanitize("a\u{7}b\u{0}c"), "abc");
    }

    #[test]
    fn preformatted_wrap_preserves_runs_of_spaces() {
        assert_eq!(wrap_preformatted("a    b", 10), vec!["a    b"]);
    }

    #[test]
    fn preformatted_wrap_hard_breaks_at_the_budget() {
        assert_eq!(wrap_preformatted("abcdefgh", 3), vec!["abc", "def", "gh"]);
    }

    #[test]
    fn preformatted_wrap_never_splits_a_grapheme() {
        for line in wrap_preformatted("日本語日本語", 3) {
            assert!(width(&line) <= 3);
        }
    }

    #[test]
    fn wrap_never_exceeds_the_budget() {
        let text = "alpha beta gamma delta epsilon zeta eta theta";
        for budget in 1..40 {
            for line in wrap(text, budget) {
                assert!(
                    width(&line) <= budget,
                    "line {line:?} exceeds budget {budget}"
                );
            }
        }
    }

    #[test]
    fn hard_splits_a_word_longer_than_the_budget() {
        let lines = wrap("supercalifragilistic", 7);
        assert!(lines.iter().all(|l| width(l) <= 7));
        assert_eq!(lines.concat(), "supercalifragilistic");
    }

    #[test]
    fn hard_splits_wide_characters_without_straddling_a_cell() {
        let lines = wrap("日本語日本語日本語", 5);
        for line in &lines {
            assert!(width(line) <= 5, "{line:?} too wide");
        }
        assert_eq!(lines.concat(), "日本語日本語日本語");
    }

    #[test]
    fn wrapping_empty_text_yields_one_empty_line() {
        assert_eq!(wrap("", 10), vec![String::new()]);
    }

    #[test]
    fn wrapping_to_zero_width_yields_one_empty_line() {
        assert_eq!(wrap("anything", 0), vec![String::new()]);
    }

    #[test]
    fn does_not_start_a_wrapped_line_with_a_space() {
        for line in wrap("aaaa bbbb cccc dddd", 6) {
            assert!(!line.starts_with(' '), "line {line:?} starts with a space");
        }
    }

    #[test]
    fn truncates_with_an_ellipsis_within_budget() {
        assert_eq!(truncate_with_ellipsis("hello world", 8), "hello w…");
        assert_eq!(width(&truncate_with_ellipsis("hello world", 8)), 8);
    }

    #[test]
    fn does_not_truncate_text_that_already_fits() {
        assert_eq!(truncate_with_ellipsis("short", 10), "short");
    }

    #[test]
    fn truncation_respects_wide_character_cells() {
        let result = truncate_with_ellipsis("日本語日本語", 5);
        assert!(width(&result) <= 5);
        assert!(result.ends_with('…'));
    }

    #[test]
    fn truncates_to_a_single_ellipsis_at_width_one() {
        assert_eq!(truncate_with_ellipsis("hello", 1), "…");
    }

    #[test]
    fn truncates_to_nothing_at_width_zero() {
        assert_eq!(truncate_with_ellipsis("hello", 0), "");
    }

    #[test]
    fn plain_truncate_adds_no_marker() {
        assert_eq!(truncate("hello world", 5), "hello");
    }

    #[test]
    fn hanging_indent_pads_continuation_lines() {
        let lines = wrap_with_hanging_indent("alpha beta gamma delta", 12, 2);
        assert!(!lines[0].starts_with(' '));
        for line in &lines[1..] {
            assert!(line.starts_with("  "), "line {line:?} lacks the indent");
            assert!(width(line) <= 12);
        }
    }
}
