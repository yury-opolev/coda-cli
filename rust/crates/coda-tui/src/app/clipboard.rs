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
                self.viewport.scroll_up(WHEEL_ROWS);
                self.remember_position();
                self.dirty = true;
            }
            MouseEventKind::ScrollDown => {
                self.viewport.scroll_down(WHEEL_ROWS);
                self.remember_position();
                self.dirty = true;
            }
            // Drag-select. Mouse capture disables the terminal's own selection,
            // so without this there is no way to select anything at all — the
            // capture takes the native behaviour away and gives nothing back.
            MouseEventKind::Down(MouseButton::Left) => {
                // The header's session id is a distinct selection target: a
                // click there selects the whole id and claims the gesture, so
                // it can never also start a caret move, a fold, or a
                // transcript selection underneath it.
                if self.hit_header_id(mouse.column, mouse.row) {
                    self.header_id_selected = true;
                    self.selection.clear();
                    self.dragging = false;
                    self.dirty = true;
                } else if self.move_caret_to_click(mouse.column, mouse.row) {
                    self.selection.clear();
                    self.header_id_selected = false;
                    self.dirty = true;
                } else if self.toggle_fold_at_click(mouse.column, mouse.row) {
                    // No anchor is set here, so a drag that starts on a header
                    // must not be treated as an in-progress selection: with the
                    // anchor left at its default the drag would select from the
                    // top of the transcript, and Ctrl+Y would copy all of it.
                    self.selection.clear();
                    self.header_id_selected = false;
                    self.dragging = false;
                } else if let Some(pos) = self.mouse_to_selection(mouse.column, mouse.row) {
                    self.selection.begin(pos);
                    self.header_id_selected = false;
                    self.dragging = true;
                    self.dirty = true;
                }
            }
            MouseEventKind::Drag(MouseButton::Left) => {
                // A drag that stays within the id keeps it selected; it never
                // grows into a partial-text range the way transcript drag does.
                if self.hit_header_id(mouse.column, mouse.row) {
                    self.header_id_selected = true;
                    self.dirty = true;
                    return None;
                }
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
                return pointer_action(self.selection.has_selection() || self.header_id_selected);
            }
            _ => {}
        }
        None
    }
    /// Whether `(column, row)` falls within the header's session-id rect.
    ///
    /// The one shared hit region: computed once per frame in `redraw` from
    /// the exact rect drawing used, so a click can never target a position
    /// the header has already moved on from.
    pub(super) fn hit_header_id(&self, column: u16, row: u16) -> bool {
        header_id_hit(self.header_id_rect, column, row)
    }

    /// Restores the most recently unsent message into an empty composer.
    ///
    /// Never overwrites a draft (only acts when the box is empty) and never
    /// submits anything — it just fills the input, exactly like recalling
    /// ordinary history. Returns whether it did, so the caller falls back to
    /// ordinary history recall when there was nothing to restore.
    pub(super) fn recall_unsent_into_composer(&mut self) -> bool {
        if !self.composer.is_empty() {
            return false;
        }
        match self.state.recall_unsent() {
            Some(text) => {
                self.composer.set_text(text);
                true
            }
            None => false,
        }
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
        let Some(index) = header_block_at(&self.block_starts, &self.rows, pos.row) else {
            return false;
        };
        let event = if self.state.transcript.is_foldable(index) {
            crate::state::UiEvent::ThinkingFoldToggled { block: index }
        } else if self.state.display_mode == coda_render::tool::ToolDisplayMode::Summary
            && self.state.transcript.is_tool_group_foldable(index)
        {
            crate::state::UiEvent::ToolGroupFoldToggled { block: index }
        } else {
            return false;
        };
        // Through `apply`, never straight at the transcript: `apply` is what
        // invalidates the cached rows. Toggling directly flipped the fold and
        // left the screen exactly as it was.
        self.apply(event);
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
    /// What a copy gesture should copy right now, and what to call it in the
    /// confirmation hint. Delegates the decision to a free function so it is
    /// testable without a live `App` (constructing one needs a real engine).
    fn pending_copy(&self) -> Option<(String, &'static str)> {
        resolve_copy_payload(
            self.header_id_selected,
            self.state.session_id.as_deref(),
            self.selection.has_selection(),
            || self.selection.copy_text(&self.rows),
        )
    }

    /// Copies the active selection for a right-click gesture.
    ///
    /// The selection is cleared only on a successful write. Keeping it after a
    /// failure means the user can retry rather than having to reselect, which
    /// is what the C# does for the same reason.
    pub(super) fn copy_selection_via_pointer(&mut self) {
        let Some((text, what)) = self.pending_copy() else {
            return;
        };
        if text.is_empty() {
            return;
        }
        match arboard::Clipboard::new().and_then(|mut c| c.set_text(&text)) {
            Ok(()) => {
                let count = text.chars().count();
                self.selection.clear();
                self.header_id_selected = false;
                // A hint, not a transcript entry: copying is worth saying once
                // and worth no permanent record, and putting it in the
                // conversation pushed the conversation up the screen to do it.
                self.hint(format!("Copied {what} — {count} characters."));
            }
            Err(err) => {
                self.notice(
                    format!("Could not access the clipboard: {err}"),
                    NoticeLevel::Warning,
                );
            }
        }
    }
    /// Pastes from the clipboard, preferring an image attachment over plain
    /// text when the clipboard carries image data.
    ///
    /// Image path:
    /// 1. Read raw RGBA pixels from the clipboard.
    /// 2. Validate dimensions and pixel count (cap: 16 M pixels).
    /// 3. Encode to PNG.
    /// 4. Enforce the 5 MB encoded-size limit.
    /// 5. Stage as a `WireImage` and insert `[Image N]` token in the composer.
    ///
    /// Text fallback: taken only when the clipboard has no image
    /// (`ContentNotAvailable`), *not* on other clipboard errors — a partial
    /// read should not silently downgrade to text.
    ///
    /// Refuses while any surface is open: the composer is not visible then,
    /// and pasting into an invisible field is never right.
    pub(super) fn paste_image_from_clipboard(&mut self) {
        // Any open surface blocks the paste: no field to paste into is visible.
        if !self.surfaces.is_empty() {
            return;
        }

        let mut clipboard = match arboard::Clipboard::new() {
            Ok(c) => c,
            Err(err) => {
                self.notice(
                    format!("Could not access the clipboard: {err}"),
                    NoticeLevel::Warning,
                );
                return;
            }
        };

        match clipboard.get_image() {
            Ok(img) => {
                match crate::app::image::rgba_to_png(img.width, img.height, &img.bytes) {
                    Ok(png_bytes) => {
                        if png_bytes.len() > crate::app::image::MAX_IMAGE_BYTES {
                            let size_mb = png_bytes.len() as f64 / (1024.0 * 1024.0);
                            self.notice(
                                format!(
                                    "Clipboard image too large ({size_mb:.1} MB encoded). Maximum is 5 MB."
                                ),
                                NoticeLevel::Warning,
                            );
                        } else {
                            let token = self.stage_image_bytes("image/png", &png_bytes);
                            let size_kb = png_bytes.len() as f64 / 1024.0;
                            self.notice(
                                format!(
                                    "Pasted clipboard image as {token} ({size_kb:.1} KB). \
                                     It will be sent with your next message."
                                ),
                                NoticeLevel::Info,
                            );
                            self.dirty = true;
                        }
                    }
                    Err(err) => {
                        self.notice(
                            format!("Could not encode clipboard image: {err}"),
                            NoticeLevel::Warning,
                        );
                    }
                }
            }
            // No image on the clipboard — fall back to text.
            Err(arboard::Error::ContentNotAvailable) => {
                match clipboard.get_text() {
                    Ok(text) if !text.is_empty() => {
                        self.composer.insert(&text);
                        self.dirty = true;
                    }
                    Ok(_) => {}
                    Err(arboard::Error::ContentNotAvailable) => {}
                    Err(err) => {
                        self.notice(
                            format!("Could not read the clipboard: {err}"),
                            NoticeLevel::Warning,
                        );
                    }
                }
            }
            // A real clipboard error (not just "no image") — report it and
            // stop; do not silently fall back to a text read.
            Err(err) => {
                self.notice(
                    format!("Could not read clipboard image: {err}"),
                    NoticeLevel::Warning,
                );
            }
        }
    }

    /// Right-click paste: delegates to the unified clipboard paste which
    /// prefers image over text, and includes the open-surface guard.
    pub(super) fn paste_from_pointer(&mut self) {
        self.paste_image_from_clipboard();
    }

    pub(super) fn copy_to_clipboard(&mut self) {
        // The header selection wins over a transcript selection, which wins
        // over the visible screen: whatever the user most recently selected
        // is what Ctrl+Y copies, never the whole transcript instead.
        let (text, what) = match self.pending_copy() {
            Some(pair) => pair,
            None => (
                crate::selection::copy_visible_text(&self.rows, self.viewport.visible_range()),
                "transcript",
            ),
        };
        if text.is_empty() {
            self.dirty = false;
            return;
        }
        match arboard::Clipboard::new().and_then(|mut c| c.set_text(&text)) {
            Ok(()) => {
                let count = text.chars().count();
                self.selection.clear();
                self.header_id_selected = false;
                self.hint(format!("Copied {what} — {count} characters."));
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

/// Whether `(column, row)` falls within `rect` — the header's session-id hit
/// region, when one is present this frame.
fn header_id_hit(rect: Option<ratatui::layout::Rect>, column: u16, row: u16) -> bool {
    let Some(rect) = rect else {
        return false;
    };
    column >= rect.x && column < rect.x + rect.width && row == rect.y
}

/// Decides what a copy gesture should copy: the header's full session id —
/// never a clipped or narrowed on-screen substring — when it is selected,
/// otherwise the transcript selection, when there is one.
///
/// A free function, and `selection_text` a closure rather than a plain
/// `String`, so this is cheap and testable without a live `App` (building one
/// needs a real engine) and without extracting a selection nobody asked for.
fn resolve_copy_payload(
    header_id_selected: bool,
    session_id: Option<&str>,
    selection_has_selection: bool,
    selection_text: impl FnOnce() -> String,
) -> Option<(String, &'static str)> {
    if header_id_selected {
        return session_id.map(|id| (id.to_string(), "session id"));
    }
    if selection_has_selection {
        return Some((selection_text(), "selection"));
    }
    None
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

    #[test]
    fn header_id_hit_matches_only_within_its_rect() {
        let rect = Some(ratatui::layout::Rect::new(10, 0, 6, 1));
        assert!(header_id_hit(rect, 10, 0));
        assert!(header_id_hit(rect, 15, 0));
        assert!(!header_id_hit(rect, 16, 0), "past the right edge");
        assert!(!header_id_hit(rect, 10, 1), "wrong row");
        assert!(!header_id_hit(None, 10, 0), "no header this frame");
    }

    #[test]
    fn a_selected_header_id_copies_the_full_id_not_a_clipped_display() {
        // The id would show truncated on a narrow header; the copy payload
        // must still be the whole thing, never the clipped on-screen text.
        let full_id = "a1b2c3d4-full-session-identifier-0000000000";
        let (text, what) = resolve_copy_payload(true, Some(full_id), false, || {
            panic!("must not fall back to the transcript selection")
        })
        .expect("a selected header id must always yield a payload");
        assert_eq!(text, full_id);
        assert_eq!(what, "session id");
    }

    #[test]
    fn header_selection_takes_precedence_over_a_transcript_selection() {
        let (text, what) = resolve_copy_payload(true, Some("sid"), true, || "ignored".into())
            .expect("payload");
        assert_eq!(text, "sid");
        assert_eq!(what, "session id");
    }

    #[test]
    fn without_a_header_selection_the_transcript_selection_is_used() {
        let (text, what) = resolve_copy_payload(false, Some("sid"), true, || "selected text".into())
            .expect("payload");
        assert_eq!(text, "selected text");
        assert_eq!(what, "selection");
    }

    #[test]
    fn hovering_with_neither_selection_copies_nothing() {
        assert_eq!(
            resolve_copy_payload(false, Some("sid"), false, || "unused".into()),
            None,
            "hover alone must never produce a copy payload"
        );
    }

    #[test]
    fn a_header_selection_with_no_known_session_id_copies_nothing() {
        assert_eq!(resolve_copy_payload(true, None, false, || "unused".into()), None);
    }
}

/// The block whose *header* row this is, if any.
///
/// A free function because `App` needs a live engine to construct, so anything
/// living on it cannot be unit tested -- and this is the part with the edge
/// cases. `block_starts` ends with a sentinel equal to the row count, and
/// blocks that render to nothing share the following block's start.
///
/// `rows` is checked first: a wholly decorative row (`is_chrome`, e.g. a
/// card's border) is never a header, regardless of what the block-start
/// arithmetic around it would otherwise resolve to. A border can end up
/// sitting exactly where an empty block's own recorded start also falls, and
/// without this check that coincidence would fold the empty block a click on
/// the border was never meant to touch.
pub(super) fn header_block_at(
    block_starts: &[usize],
    rows: &[coda_render::RenderLine],
    row: usize,
) -> Option<usize> {
    if rows.get(row).is_some_and(|r| r.is_chrome) {
        return None;
    }
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
    use coda_render::{Role, RenderLine};

    #[test]
    fn merged_tool_header_toggles_its_group_but_detail_rows_do_not() {
        use crate::transcript::{ActivityKey, Block, Transcript};
        use coda_render::tool::{CallStatus, ToolActivity, ToolCall, ToolDisplayMode};
        let mut transcript = Transcript::new();
        for name in ["first", "second"] {
            let mut call = ToolCall::new(name, "{}");
            call.status = CallStatus::Succeeded;
            call.result = Some("result".into());
            transcript.push(Block::Tools {
                activity: ToolActivity { calls: vec![call], complete: true },
                key: ActivityKey::default(), calls: Vec::new(),
            });
        }
        let (rows, starts) = transcript.render_with_block_starts(80, ToolDisplayMode::Summary);
        let index = header_block_at(&starts, &rows, 0).unwrap();
        assert!(transcript.is_tool_group_foldable(index));
        assert!(transcript.toggle_tool_group(index));
        let (rows, starts) = transcript.render_with_block_starts(80, ToolDisplayMode::Summary);
        assert!(header_block_at(&starts, &rows, 0).is_some());
        assert!(header_block_at(&starts, &rows, 1).is_none());
        assert!(rows.iter().any(|row| row.text.contains("result")));
    }

    /// `n` plain (non-chrome) rows, enough for any row index a test asks about.
    fn rows(n: usize) -> Vec<RenderLine> {
        (0..n).map(|_| RenderLine::new("x", Role::Assistant)).collect()
    }

    #[test]
    fn a_click_on_a_block_header_finds_that_block() {
        // Three blocks of 3, 2 and 4 rows, then the sentinel.
        let starts = vec![0, 3, 5, 9];
        let rows = rows(9);
        assert_eq!(header_block_at(&starts, &rows, 0), Some(0));
        assert_eq!(header_block_at(&starts, &rows, 3), Some(1));
        assert_eq!(header_block_at(&starts, &rows, 5), Some(2));
    }

    #[test]
    fn a_click_on_a_body_row_is_not_a_header() {
        let starts = vec![0, 3, 5, 9];
        let rows = rows(9);
        for row in [1, 2, 4, 6, 7, 8] {
            assert_eq!(header_block_at(&starts, &rows, row), None, "row {row}");
        }
    }

    #[test]
    fn a_click_past_the_end_folds_nothing() {
        // The sentinel is a row count. Counted as a block, row 9 would fold
        // the last block from a click on empty space below the transcript.
        let starts = vec![0, 3, 5, 9];
        let rows = rows(9);
        assert_eq!(header_block_at(&starts, &rows, 9), None);
        assert_eq!(header_block_at(&starts, &rows, 40), None);
    }

    #[test]
    fn an_empty_block_does_not_steal_its_neighbours_header() {
        // Block 1 renders nothing, so it shares block 2's start. The row was
        // drawn by block 2, and folding block 1 would leave the user clicking
        // a header and watching a different block move.
        let starts = vec![0, 3, 3, 7];
        let rows = rows(7);
        assert_eq!(header_block_at(&starts, &rows, 3), Some(2));
    }

    #[test]
    fn an_empty_table_folds_nothing() {
        assert_eq!(header_block_at(&[], &[], 0), None);
        assert_eq!(header_block_at(&[0], &[], 0), None);
    }

    #[test]
    fn a_chrome_row_is_never_a_header_even_if_the_arithmetic_would_say_so() {
        // A card border can land exactly on an empty block's own recorded
        // start (see `render_pass` in transcript.rs); `is_chrome` must win
        // regardless of what the block_starts table says about that index.
        let starts = vec![0, 3, 3, 7];
        let mut rows = rows(7);
        rows[3] = RenderLine::new("\u{2500}".repeat(10), Role::Notification).as_chrome();
        assert_eq!(
            header_block_at(&starts, &rows, 3),
            None,
            "a chrome row must never resolve to a foldable header"
        );
    }
}