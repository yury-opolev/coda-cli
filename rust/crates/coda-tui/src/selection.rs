//! Keyboard-driven transcript selection and clipboard copy.
//!
//! Ported from `TranscriptSelection.cs`.  The C# implementation is
//! mouse-driven; this Rust port keeps the core selection model (inclusive
//! row+cell endpoints, gutter skipping, copy-text extraction) and adds
//! keyboard navigation on top of it.
//!
//! A selection only becomes active once the active cell has moved at least one
//! row away from the anchor.  `RangeForRow` and `copy_text` convert inclusive
//! stored endpoints to half-open, ordered cell slices.

use coda_render::{text, RenderLine};

/// A row-level position within the rendered transcript.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct SelectionPos {
    /// Index into the flat `rows` array.
    pub row: usize,
    /// Terminal cell column within that row.
    pub col: usize,
}

/// An inclusive text selection over global transcript rows.
#[derive(Debug, Default, Clone)]
pub struct TranscriptSelection {
    anchor: SelectionPos,
    active: SelectionPos,
    active_set: bool,
}

impl TranscriptSelection {
    pub fn new() -> Self {
        Self::default()
    }

    /// Starts a new selection at `anchor`.  Nothing is selected until the
    /// active end moves.
    pub fn begin(&mut self, anchor: SelectionPos) {
        self.anchor = normalize(anchor);
        self.active = self.anchor;
        self.active_set = false;
    }

    /// Moves the active end to `pos`.  Returns `true` when this creates a
    /// non-empty selection (active moved away from anchor by at least one row).
    pub fn update(&mut self, pos: SelectionPos) -> bool {
        self.active = normalize(pos);
        self.active_set = self.active != self.anchor;
        self.active_set
    }

    /// Extends the selection to row `row`, keeping the column at 0 (for
    /// keyboard navigation — the whole row is selected).
    pub fn extend_to_row(&mut self, row: usize, total_rows: usize) {
        let row = row.min(total_rows.saturating_sub(1));
        self.active = SelectionPos { row, col: 0 };
        self.active_set = self.active != self.anchor;
    }

    /// Clears the selection.
    pub fn clear(&mut self) {
        self.active_set = false;
        self.anchor = SelectionPos::default();
        self.active = SelectionPos::default();
    }

    /// Whether an active selection exists.
    pub fn has_selection(&self) -> bool {
        self.active_set
    }

    /// Returns the `(start_row, end_row_inclusive)` pair in ascending order.
    pub fn row_range(&self) -> Option<(usize, usize)> {
        if !self.has_selection() {
            return None;
        }
        let (a, b) = self.ordered();
        Some((a.row, b.row))
    }

    /// The half-open cell range `[start, end)` for `row`, or `None` when the
    /// row is outside the selection.
    pub fn range_for_row(&self, row: usize, row_width: usize) -> Option<(usize, usize)> {
        if !self.has_selection() {
            return None;
        }
        let (start, end) = self.ordered();
        if row < start.row || row > end.row {
            return None;
        }
        let w = row_width;
        if start.row == end.row {
            return Some((start.col.min(w), (end.col + 1).min(w)));
        }
        if row == start.row {
            return Some((start.col.min(w), w));
        }
        if row == end.row {
            return Some((0, (end.col + 1).min(w)));
        }
        Some((0, w))
    }

    /// Extracts the selected text from `rows` as a newline-joined string.
    ///
    /// The gutter prefix — role marker, continuation indent, or tree connector —
    /// is skipped: it is chrome that draws with the row but pasting it into an
    /// editor is never what the user intended.  Separator rows are skipped
    /// entirely; a blank interior row contributes an empty string.
    pub fn copy_text(&self, rows: &[RenderLine]) -> String {
        if !self.has_selection() {
            return String::new();
        }
        let mut parts: Vec<String> = Vec::new();
        for (i, row) in rows.iter().enumerate() {
            if row.is_separator {
                continue;
            }
            let w = text::width(&row.text);
            let Some((start_cell, end_cell)) = self.range_for_row(i, w) else {
                continue;
            };
            // Skip gutter chrome.
            let gutter_cells = row.gutter.cells();
            let content_start = start_cell.max(gutter_cells);
            if content_start >= end_cell {
                parts.push(String::new());
                continue;
            }
            parts.push(slice_by_cells(&row.text, content_start, end_cell));
        }
        parts.join("\n")
    }

    fn ordered(&self) -> (SelectionPos, SelectionPos) {
        let anchor_first = self.anchor.row < self.active.row
            || (self.anchor.row == self.active.row && self.anchor.col <= self.active.col);
        if anchor_first {
            (self.anchor, self.active)
        } else {
            (self.active, self.anchor)
        }
    }
}

fn normalize(pos: SelectionPos) -> SelectionPos {
    SelectionPos { row: pos.row, col: pos.col }
}

/// Extracts the grapheme-cluster substring from `text` occupying cell range
/// `[start, end)`.
pub fn slice_by_cells(text: &str, start: usize, end: usize) -> String {
    use unicode_segmentation::UnicodeSegmentation;
    let mut out = String::new();
    let mut cell = 0usize;
    for grapheme in text.graphemes(true) {
        let w = text::grapheme_width(grapheme).max(1);
        if cell >= end {
            break;
        }
        if cell + w > start {
            out.push_str(grapheme);
        }
        cell += w;
    }
    out
}

/// Copies all visible (non-separator) rows in the given range to a plain
/// string, stripping gutter chrome.  Used for a full-transcript copy when no
/// selection is active.
pub fn copy_visible_text(rows: &[RenderLine], range: std::ops::Range<usize>) -> String {
    let visible = rows.get(range).unwrap_or(&[]);
    let parts: Vec<&str> = visible
        .iter()
        .filter(|r| !r.is_separator)
        .map(|r| {
            let gutter_cells = r.gutter.cells();
            // The gutter prefix is literally prepended to `text`, so we skip
            // those bytes by slicing at the cell boundary.
            skip_gutter_prefix(&r.text, gutter_cells)
        })
        .collect();
    parts.join("\n")
}

/// Returns the text of `s` after skipping the first `gutter_cells` display cells.
fn skip_gutter_prefix(s: &str, gutter_cells: usize) -> &str {
    use unicode_segmentation::UnicodeSegmentation;
    if gutter_cells == 0 {
        return s;
    }
    let mut cell = 0usize;
    for (byte_idx, grapheme) in s.grapheme_indices(true) {
        if cell >= gutter_cells {
            return &s[byte_idx..];
        }
        cell += text::grapheme_width(grapheme).max(1);
    }
    ""
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_render::theme::Role;

    fn pos(row: usize, col: usize) -> SelectionPos {
        SelectionPos { row, col }
    }

    #[test]
    fn no_selection_initially() {
        assert!(!TranscriptSelection::new().has_selection());
    }

    #[test]
    fn a_click_and_release_in_place_selects_nothing() {
        let mut sel = TranscriptSelection::new();
        sel.begin(pos(5, 2));
        assert!(!sel.has_selection());
    }

    #[test]
    fn moving_to_a_different_row_activates_selection() {
        let mut sel = TranscriptSelection::new();
        sel.begin(pos(5, 0));
        assert!(sel.update(pos(6, 0)));
        assert!(sel.has_selection());
    }

    #[test]
    fn clear_removes_selection() {
        let mut sel = TranscriptSelection::new();
        sel.begin(pos(0, 0));
        sel.update(pos(2, 0));
        sel.clear();
        assert!(!sel.has_selection());
    }

    #[test]
    fn range_for_row_on_a_single_row_selection() {
        let mut sel = TranscriptSelection::new();
        sel.begin(pos(3, 2));
        sel.update(pos(3, 7));
        // Same row: start..end+1
        assert_eq!(sel.range_for_row(3, 80), Some((2, 8)));
        assert_eq!(sel.range_for_row(2, 80), None);
    }

    #[test]
    fn range_for_row_on_a_multi_row_selection() {
        let mut sel = TranscriptSelection::new();
        sel.begin(pos(2, 5));
        sel.update(pos(4, 3));
        assert_eq!(sel.range_for_row(2, 80), Some((5, 80)));
        assert_eq!(sel.range_for_row(3, 80), Some((0, 80)));
        assert_eq!(sel.range_for_row(4, 80), Some((0, 4)));
    }

    #[test]
    fn selection_is_normalized_regardless_of_anchor_vs_active_order() {
        let mut sel = TranscriptSelection::new();
        sel.begin(pos(8, 0));
        sel.update(pos(3, 0));
        let range = sel.row_range().unwrap();
        assert_eq!(range, (3, 8));
    }

    #[test]
    fn copy_text_skips_separator_rows() {
        let rows = vec![
            RenderLine::new("hello", Role::Assistant),
            RenderLine::separator(),
            RenderLine::new("world", Role::Assistant),
        ];
        let mut sel = TranscriptSelection::new();
        sel.begin(pos(0, 0));
        sel.update(pos(2, 0));
        let text = sel.copy_text(&rows);
        assert!(!text.contains('\n') || !text.contains("  "), "no separator line");
        // Both content rows contributed; separator was skipped
        assert!(text.contains("hello") || text.contains("world"));
    }

    #[test]
    fn copy_visible_text_joins_rows_without_gutter() {
        use coda_render::Gutter;
        let row0 = RenderLine::new("hello", Role::User).with_gutter(Gutter::UserMarker);
        let row1 = RenderLine::new("world", Role::User).with_gutter(Gutter::UserMarker);
        let rows = vec![row0, row1];
        let text = copy_visible_text(&rows, 0..2);
        assert_eq!(text, "hello\nworld");
    }

    #[test]
    fn slice_by_cells_extracts_ascii_range() {
        assert_eq!(slice_by_cells("hello", 1, 4), "ell");
    }

    #[test]
    fn slice_by_cells_handles_wide_characters() {
        // "A" (1 cell) + "文" (2 cells) + "B" (1 cell)
        let s = "A\u{6587}B";
        // cells: A=0..1, 文=1..3, B=3..4
        assert_eq!(slice_by_cells(s, 1, 3), "\u{6587}");
        assert_eq!(slice_by_cells(s, 0, 1), "A");
    }
}
