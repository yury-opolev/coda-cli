//! Clipboard and pointer gestures.
//!
//! Grouped because they are one subject from the user's side: selecting,
//! copying, pasting and placing the caret are a single continuous interaction,
//! and the rules that keep them coherent — a click clears a selection, a paste
//! is refused while a surface is open — only make sense together.

use arboard;
use crossterm::event::{MouseButton, MouseEvent, MouseEventKind};

use super::{App, PointerAction, WHEEL_ROWS};
use crate::transcript::NoticeLevel;

impl App {
    /// Maps a pointer event onto its effect, returning the clipboard action the
    /// gesture asks for (if any).
    ///
    /// Split out from the event loop so tests can drive real pointer events and
    /// observe the decision. Handlers buried in the loop are exactly the shape
    /// that has silently gone unwired here before.
    pub(super) fn decide_pointer_action(&mut self, mouse: MouseEvent) -> Option<PointerAction> {
        match mouse.kind {
            MouseEventKind::ScrollUp => {
                self.capture_anchor_if_following();
                self.viewport.scroll_up(WHEEL_ROWS);
                self.dirty = true;
            }
            MouseEventKind::ScrollDown => {
                self.viewport.scroll_down(WHEEL_ROWS);
                self.dirty = true;
            }
            // Drag-select. Mouse capture disables the terminal's own selection,
            // so without this there is no way to select anything at all — the
            // capture takes the native behaviour away and gives nothing back.
            MouseEventKind::Down(MouseButton::Left) => {
                // A click in the composer moves the caret rather than starting
                // a transcript selection: the composer is the one region where
                // a click already has an editing meaning.
                if self.move_caret_to_click(mouse.column, mouse.row) {
                    self.selection.clear();
                    self.dirty = true;
                } else if self.toggle_fold_at_click(mouse.column, mouse.row) {
                    // No anchor is set here, so a drag that starts on a header
                    // must not be treated as an in-progress selection: with the
                    // anchor left at its default the drag would select from the
                    // top of the transcript, and Ctrl+Y would copy all of it.
                    self.selection.clear();
                    self.dragging = false;
                } else if let Some(pos) = self.mouse_to_selection(mouse.column, mouse.row) {
                    self.selection.begin(pos);
                    self.dragging = true;
                    self.dirty = true;
                }
            }
            MouseEventKind::Drag(MouseButton::Left) => {
                if !self.dragging {
                    return None;
                }
                if let Some(pos) = self.mouse_to_selection(mouse.column, mouse.row) {
                    self.selection.update(pos);
                    self.dirty = true;
                }
            }
            MouseEventKind::Up(MouseButton::Left) => {
                if self.dragging {
                    if let Some(pos) = self.mouse_to_selection(mouse.column, mouse.row) {
                        self.selection.update(pos);
                    }
                    self.dragging = false;
                }
                // A click with no drag clears rather than leaving a stale
                // one-cell selection that Ctrl+Y would then copy.
                if !self.selection.has_selection() {
                    self.selection.clear();
                }
                self.dirty = true;
            }
            // Right-click is copy-or-paste, matching the C# build and the
            // Windows console convention: with a selection it copies, and with
            // nothing selected it pastes into the draft. The paste target is
            // always the composer regardless of where the pointer was, so the
            // gesture does not depend on aim.
            MouseEventKind::Down(MouseButton::Right) => {
                self.dirty = true;
                return pointer_action(self.selection.has_selection());
            }
            _ => {}
        }
        None
    }
    /// Translates a screen cell into a position in the flat `rows` array.
    ///
    /// Returns `None` for a click outside the transcript area, so clicking the
    /// composer or status bar does not start a selection in the transcript.
    /// Places the caret where the pointer was clicked, if it landed in the
    /// composer. Returns whether it did.
    pub(super) fn move_caret_to_click(&mut self, column: u16, row: u16) -> bool {
        let (origin_x, origin_y) = self.composer_origin;
        if origin_y == 0 || row < origin_y {
            return false;
        }
        let line = (row - origin_y) as usize;
        if line >= self.composer.line_count() {
            return false;
        }
        // Left of the prompt marker counts as column zero rather than missing,
        // so clicking the gutter puts the caret at the start of the line.
        let cell = column.saturating_sub(origin_x) as usize;
        self.composer.move_cursor_to(line, cell);
        true
    }
    /// Folds or unfolds the block whose header was clicked.
    ///
    /// Only the header row counts. Clicking anywhere in the body would make
    /// selecting the reasoning text impossible, and the body is exactly what
    /// someone expands the block in order to read.
    pub(super) fn toggle_fold_at_click(&mut self, _column: u16, row: u16) -> bool {
        let Some(pos) = self.mouse_to_selection(0, row) else {
            return false;
        };
        let Some(index) = header_block_at(&self.block_starts, pos.row) else {
            return false;
        };
        if !self.state.transcript.is_foldable(index) {
            return false;
        }
        // Through `apply`, never straight at the transcript: `apply` is what
        // invalidates the cached rows. Toggling directly flipped the fold and
        // left the screen exactly as it was.
        self.apply(crate::state::UiEvent::ThinkingFoldToggled { block: index });
        true
    }

    pub(super) fn mouse_to_selection(
        &self,
        column: u16,
        row: u16,
    ) -> Option<crate::selection::SelectionPos> {
        let (origin_row, height) = self.transcript_origin;
        if height == 0 || row < origin_row || row >= origin_row.saturating_add(height) {
            return None;
        }
        let offset_in_view = (row - origin_row) as usize;
        let visible = self.viewport.visible_range();
        let index = visible.start.checked_add(offset_in_view)?;
        if index >= self.rows.len() {
            return None;
        }
        Some(crate::selection::SelectionPos { row: index, col: column as usize })
    }
    /// Copies the active selection for a right-click gesture.
    ///
    /// The selection is cleared only on a successful write. Keeping it after a
    /// failure means the user can retry rather than having to reselect, which
    /// is what the C# does for the same reason.
    pub(super) fn copy_selection_via_pointer(&mut self) {
        let text = self.selection.copy_text(&self.rows);
        if text.is_empty() {
            return;
        }
        match arboard::Clipboard::new().and_then(|mut c| c.set_text(&text)) {
            Ok(()) => {
                self.selection.clear();
                self.notice("Copied selection to clipboard.", NoticeLevel::Info);
            }
            Err(err) => {
                self.notice(
                    format!("Could not access the clipboard: {err}"),
                    NoticeLevel::Warning,
                );
            }
        }
    }
    /// Pastes the clipboard into the composer for a right-click with nothing
    /// selected.
    ///
    /// Refused while a prompt is up, so a pointer can never type into a
    /// composer the user cannot see — the same guard the C# applies.
    pub(super) fn paste_from_pointer(&mut self) {
        // Any open surface blocks it, not just a prompt or a browser: a
        // pointer must never type into a composer the user cannot see.
        if !self.surfaces.is_empty() {
            return;
        }
        match arboard::Clipboard::new().and_then(|mut c| c.get_text()) {
            Ok(text) if !text.is_empty() => {
                self.composer.insert(&text);
            }
            Ok(_) => {}
            Err(err) => {
                self.notice(
                    format!("Could not read the clipboard: {err}"),
                    NoticeLevel::Warning,
                );
            }
        }
    }
    pub(super) fn copy_to_clipboard(&mut self) {
        // A selection wins over the visible screen: if the user has selected
        // something, copying everything on screen instead is silently the
        // wrong answer.
        let (text, what) = if self.selection.has_selection() {
            (self.selection.copy_text(&self.rows), "selection")
        } else {
            (
                crate::selection::copy_visible_text(&self.rows, self.viewport.visible_range()),
                "transcript",
            )
        };
        if text.is_empty() {
            self.dirty = false;
            return;
        }
        match arboard::Clipboard::new().and_then(|mut c| c.set_text(&text)) {
            Ok(()) => {
                self.notice(format!("Copied {what} to clipboard."), NoticeLevel::Info);
            }
            Err(err) => {
                self.notice(
                    format!("Could not access the clipboard: {err}"),
                    NoticeLevel::Warning,
                );
            }
        }
    }

}

/// Decides what a right-click means.
///
/// One button carries both operations, chosen by whether anything is selected:
/// this is the Windows console convention and matches the C# build, so muscle
/// memory carries over. A selection is consumed by the copy, which is what
/// makes the alternation feel natural — select, right-click to copy, then
/// right-click again to paste.
fn pointer_action(has_selection: bool) -> Option<PointerAction> {
    Some(if has_selection {
        PointerAction::Copy
    } else {
        PointerAction::Paste
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn right_click_with_a_selection_copies() {
        assert_eq!(pointer_action(true), Some(PointerAction::Copy));
    }

    #[test]
    fn right_click_without_a_selection_pastes() {
        assert_eq!(pointer_action(false), Some(PointerAction::Paste));
    }
}

/// The block whose *header* row this is, if any.
///
/// A free function because `App` needs a live engine to construct, so anything
/// living on it cannot be unit tested -- and this is the part with the edge
/// cases. `block_starts` ends with a sentinel equal to the row count, and
/// blocks that render to nothing share the following block's start.
pub(super) fn header_block_at(block_starts: &[usize], row: usize) -> Option<usize> {
    // Drop the sentinel: it is a row count, not a block, and treating it as
    // one folds the last block when the click lands past the end.
    let blocks = block_starts.len().checked_sub(1)?;
    if blocks == 0 {
        return None;
    }
    // The last block starting at or before the row. Taking the *last* matters
    // when a block renders no rows: it shares the next block's start, and the
    // row belongs to the one that actually drew it.
    let index = block_starts
        .partition_point(|&start| start <= row)
        .checked_sub(1)?
        .min(blocks - 1);
    (block_starts[index] == row).then_some(index)
}

#[cfg(test)]
mod fold_tests {
    use super::header_block_at;

    #[test]
    fn a_click_on_a_block_header_finds_that_block() {
        // Three blocks of 3, 2 and 4 rows, then the sentinel.
        let starts = vec![0, 3, 5, 9];
        assert_eq!(header_block_at(&starts, 0), Some(0));
        assert_eq!(header_block_at(&starts, 3), Some(1));
        assert_eq!(header_block_at(&starts, 5), Some(2));
    }

    #[test]
    fn a_click_on_a_body_row_is_not_a_header() {
        let starts = vec![0, 3, 5, 9];
        for row in [1, 2, 4, 6, 7, 8] {
            assert_eq!(header_block_at(&starts, row), None, "row {row}");
        }
    }

    #[test]
    fn a_click_past_the_end_folds_nothing() {
        // The sentinel is a row count. Counted as a block, row 9 would fold
        // the last block from a click on empty space below the transcript.
        let starts = vec![0, 3, 5, 9];
        assert_eq!(header_block_at(&starts, 9), None);
        assert_eq!(header_block_at(&starts, 40), None);
    }

    #[test]
    fn an_empty_block_does_not_steal_its_neighbours_header() {
        // Block 1 renders nothing, so it shares block 2's start. The row was
        // drawn by block 2, and folding block 1 would leave the user clicking
        // a header and watching a different block move.
        let starts = vec![0, 3, 3, 7];
        assert_eq!(header_block_at(&starts, 3), Some(2));
    }

    #[test]
    fn an_empty_table_folds_nothing() {
        assert_eq!(header_block_at(&[], 0), None);
        assert_eq!(header_block_at(&[0], 0), None);
    }
}