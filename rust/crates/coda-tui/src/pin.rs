//! The transcript pin: a one-line summary of the active user prompt shown when
//! it has scrolled entirely out of the viewport.
//!
//! Ported from `TranscriptPin.cs`.  The pin is shown whenever a turn is
//! running and the user prompt block is not visible — both the "above" and
//! "below" cases, though in practice the prompt is always above the streaming
//! response.
//!
//! Line selection happens AFTER sanitization, not before: a non-blank line can
//! still sanitize to empty (a bare escape sequence, a bidi mark), and choosing
//! it would blank the pin for the whole turn.

use unicode_segmentation::UnicodeSegmentation;

use coda_render::text;

/// The gutter prefix width used for the pin row (matches `Gutter::UserMarker`).
const PREFIX: &str = " ❯ "; // ❯
const PREFIX_CELLS: usize = 3;

/// Builds the pin row text for `prompt_text` at `width` cells.
///
/// Returns `None` when there is nothing worth pinning (empty prompt, insufficient
/// width, or every line sanitizes to nothing).
pub fn compose(prompt_text: &str, width: usize) -> Option<String> {
    if prompt_text.is_empty() || width < PREFIX_CELLS + 1 {
        return None;
    }

    let (content, has_more) = first_surviving_line(prompt_text)?;

    let composed = format!("{PREFIX}{content}");
    if !has_more && text::width(&composed) <= width {
        return Some(composed);
    }

    // Elide: keep whole graphemes so a wide (CJK) character is never split,
    // and reserve one cell for the ellipsis.
    let content_budget = width.saturating_sub(PREFIX_CELLS + 1);
    let mut truncated = PREFIX.to_string();
    let mut used = 0usize;

    for grapheme in content.graphemes(true) {
        let w = text::grapheme_width(grapheme).max(1);
        if used + w > content_budget {
            break;
        }
        truncated.push_str(grapheme);
        used += w;
    }
    truncated.push('…');
    Some(truncated)
}

/// Whether the pin should be drawn.
///
/// True when output is being produced (`has_active_work`), a prompt exists
/// (`block_start` is `Some`), and none of that prompt's rows are currently
/// visible in the viewport.
pub fn should_show(
    has_active_work: bool,
    block_start: Option<usize>,
    block_end_exclusive: usize,
    top_row: usize,
    viewport_height: usize,
) -> bool {
    if !has_active_work || block_start.is_none() || viewport_height == 0 {
        return false;
    }
    let start = block_start.unwrap();
    let view_end = top_row + viewport_height;
    let block_above = block_end_exclusive <= top_row;
    let block_below = start >= view_end;
    block_above || block_below
}

/// Returns the first non-empty, sanitized line of `text` and whether a second
/// such line follows, without allocating the entire text.
fn first_surviving_line(text: &str) -> Option<(String, bool)> {
    let mut first: Option<String> = None;
    for raw_line in text.split(['\n', '\r']) {
        let trimmed = raw_line.trim();
        if trimmed.is_empty() {
            continue;
        }
        let sanitized = coda_render::text::sanitize(trimmed);
        if sanitized.is_empty() {
            continue;
        }
        if first.is_none() {
            first = Some(sanitized);
        } else {
            return Some((first.unwrap(), true));
        }
    }
    first.map(|f| (f, false))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn returns_none_for_empty_prompt() {
        assert!(compose("", 80).is_none());
    }

    #[test]
    fn returns_none_when_width_too_narrow() {
        assert!(compose("hello", 3).is_none());
        // prefix is 3 cells, we need at least 4
        assert!(compose("hello", 4).is_some());
    }

    #[test]
    fn short_single_line_is_returned_as_is() {
        let result = compose("hello", 80).unwrap();
        assert_eq!(result, " \u{276F} hello");
    }

    #[test]
    fn single_line_that_fits_exactly_is_not_truncated() {
        // " ❯ " (3 cells) + "hi" (2 cells) = 5 cells; width=5
        let result = compose("hi", 5).unwrap();
        assert_eq!(result, " \u{276F} hi");
    }

    #[test]
    fn multi_line_prompt_is_elided_with_ellipsis() {
        let result = compose("first\nsecond", 80).unwrap();
        assert!(result.ends_with('…'), "expected ellipsis: {result:?}");
        assert!(result.contains("first"), "should contain first line: {result:?}");
    }

    #[test]
    fn long_single_line_is_truncated_with_ellipsis() {
        let long = "a".repeat(100);
        let result = compose(&long, 20).unwrap();
        assert!(result.ends_with('…'));
        assert!(text::width(&result) <= 20, "width {} exceeds 20", text::width(&result));
    }

    #[test]
    fn blank_first_lines_are_skipped_to_find_content() {
        let result = compose("\n\nsecond line", 80).unwrap();
        assert!(result.contains("second line"), "expected second line: {result:?}");
    }

    #[test]
    fn should_show_when_block_is_above_viewport() {
        // block rows 0..3, viewport starts at 5
        assert!(should_show(true, Some(0), 3, 5, 10));
    }

    #[test]
    fn should_show_when_block_is_below_viewport() {
        // block starts at row 20, viewport shows rows 0..10
        assert!(should_show(true, Some(20), 23, 0, 10));
    }

    #[test]
    fn does_not_show_when_block_is_visible() {
        // block rows 3..6, viewport shows rows 0..10
        assert!(!should_show(true, Some(3), 6, 0, 10));
    }

    #[test]
    fn does_not_show_when_no_active_work() {
        assert!(!should_show(false, Some(0), 3, 5, 10));
    }

    #[test]
    fn does_not_show_without_block() {
        assert!(!should_show(true, None, 0, 5, 10));
    }

    #[test]
    fn does_not_show_with_zero_height_viewport() {
        assert!(!should_show(true, Some(0), 3, 0, 0));
    }
}
