//! Clipboard and pointer gestures.
//!
//! Grouped because they are one subject from the user's side: selecting,
//! copying, pasting and placing the caret are a single continuous interaction,
//! and the rules that keep them coherent — a click clears a selection, a paste
//! is refused while a surface is open — only make sense together.

use arboard;
use crossterm::event::{MouseButton, MouseEvent, MouseEventKind};

use coda_boot::browser::LinkOpenMode;

use super::{App, PointerAction, WHEEL_ROWS};
use crate::transcript::NoticeLevel;

/// The scrollbar's on-screen geometry and the thumb drag over it, if any.
///
/// Kept here rather than on the event loop because it is one thing: the
/// scrollbar is only ever touched through the pointer, and the drag offset is
/// meaningless without the rect it is measured against.
#[derive(Debug, Clone, Copy, Default)]
pub(super) struct ScrollbarPointer {
    /// Where the scrollbar column was drawn, or `None` when the transcript
    /// fits and no scrollbar is shown.
    ///
    /// Captured from the same layout that is about to be drawn, so a press is
    /// hit-tested against exactly the geometry on screen rather than against a
    /// recomputation that could drift from it.
    pub(super) area: Option<ratatui::layout::Rect>,
    /// Rows between the top of the thumb and the pointer while the thumb is
    /// being dragged; `None` when no drag is in progress.
    ///
    /// Held so the thumb keeps the grip it was taken hold of: without it every
    /// drag event would re-centre the thumb on the pointer, and the transcript
    /// would lurch on the first cell of movement.
    pub(super) grab: Option<u16>,
}

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
                // The scrollbar is tested first and claims the gesture outright.
                // It overlaps the transcript's row range, so anything checked
                // ahead of it — a fold header, a selection anchor — would be
                // started by a press meant for the thumb.
                if self.begin_scrollbar_drag(mouse.column, mouse.row) {
                    return None;
                }
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
                // A thumb drag owns the pointer until it is released, wherever
                // it wanders: the column is one cell wide, so insisting the
                // pointer stay inside it would abandon the gesture constantly.
                if self.drag_scrollbar(mouse.row) {
                    return None;
                }
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
                // Releasing the thumb ends the gesture and nothing else: it
                // selected nothing, so falling through would let the release
                // open a link sitting under the scrollbar column.
                if self.scrollbar.grab.take().is_some() {
                    self.dirty = true;
                    return None;
                }
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
                    // A click that selected nothing is a click, not a drag: if
                    // it landed on a link, activate it. Guarded on no surface
                    // being open, so a click never reaches through a modal to
                    // the transcript beneath it.
                    if self.surfaces.is_empty() {
                        if let Some(url) = self.link_at_pointer(mouse.column, mouse.row) {
                            self.open_link(&url, LinkOpenMode::Default);
                        }
                    }
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
                // A right-click on a link opens its context menu instead. Taken
                // before the copy/paste gesture so a link never falls through to
                // it, and guarded on no surface being open.
                if self.surfaces.is_empty() {
                    if let Some(url) = self.link_at_pointer(mouse.column, mouse.row) {
                        self.open_link_menu(url, (mouse.column, mouse.row));
                        return None;
                    }
                }
                return pointer_action(self.selection.has_selection() || self.header_id_selected);
            }
            // A bare pointer move — motion with no button held — refreshes the
            // link-hover underline. Redrawing is gated inside the helper so a
            // move that stays on the same link (or on no link) costs nothing.
            MouseEventKind::Moved => self.update_hovered_link(mouse.column, mouse.row),
            _ => {}
        }
        None
    }

    /// Routes a pointer event: to the open surface first, then the transcript.
    ///
    /// A surface owns the pointer exactly as it owns the keyboard — a click
    /// must never reach through a menu to the transcript beneath it. Only when
    /// no surface consumes the event does it fall through to the transcript
    /// gestures (scroll, select, link, paste), which are themselves guarded on
    /// the stack being empty where that matters. The top surface's content rect
    /// is captured at draw time, so a click is hit-tested against exactly the
    /// geometry that was drawn.
    pub(super) async fn on_mouse(&mut self, mouse: MouseEvent) {
        if !self.surfaces.is_empty() {
            use crate::surface::stack::StackOutcome;
            match self.surfaces.handle_mouse(mouse, self.surface_content) {
                StackOutcome::Handled => {
                    self.dirty = true;
                    return;
                }
                StackOutcome::Action(action) => {
                    self.dirty = true;
                    self.apply_surface_action(action).await;
                    return;
                }
                StackOutcome::Ignored => {}
            }
        }
        if let Some(action) = self.decide_pointer_action(mouse) {
            match action {
                PointerAction::Copy => self.copy_selection_via_pointer(),
                PointerAction::Paste => self.paste_from_pointer(),
            }
        }
    }
    /// Whether `(column, row)` falls within the header's session-id rect.
    ///
    /// The one shared hit region: computed once per frame in `redraw` from
    /// the exact rect drawing used, so a click can never target a position
    /// the header has already moved on from.
    pub(super) fn hit_header_id(&self, column: u16, row: u16) -> bool {
        header_id_hit(self.header_id_rect, column, row)
    }

    /// Starts a scrollbar-thumb drag, if the press landed on the scrollbar.
    ///
    /// A press on the thumb grabs it exactly where it was taken hold of, so
    /// the transcript does not lurch under the pointer. A press on the bare
    /// track jumps the thumb's centre to the pointer and then grabs it there,
    /// so a click-to-jump continues into a drag without a second gesture.
    ///
    /// Returns whether the press was consumed.
    pub(super) fn begin_scrollbar_drag(&mut self, column: u16, row: u16) -> bool {
        let Some(area) = self.scrollbar.area else {
            return false;
        };
        if !scrollbar_hit(area, column, row) {
            return false;
        }
        let track = area.height as usize;
        let Some((position, size)) = self.viewport.thumb(track) else {
            return false;
        };

        let (grab, jump_to) = scrollbar_grab(position, size, (row - area.y) as usize);
        if let Some(thumb_top) = jump_to {
            self.viewport.scroll_to_thumb(track, thumb_top);
            self.remember_position();
        }

        self.scrollbar.grab = Some(grab as u16);
        // The scrollbar owns the gesture outright: no caret, no fold, no
        // selection and no stale header highlight left behind it.
        self.selection.clear();
        self.header_id_selected = false;
        self.dragging = false;
        self.dirty = true;
        true
    }

    /// Continues a thumb drag, if one is in progress.
    ///
    /// Only the row matters: the thumb travels one axis, so a pointer that
    /// strays out of the one-cell column keeps scrolling rather than dropping
    /// the gesture. Returns whether a drag was in progress.
    pub(super) fn drag_scrollbar(&mut self, row: u16) -> bool {
        let (Some(area), Some(grab)) = (self.scrollbar.area, self.scrollbar.grab) else {
            return false;
        };
        let thumb_top = row.saturating_sub(area.y).saturating_sub(grab) as usize;
        self.viewport.scroll_to_thumb(area.height as usize, thumb_top);
        self.remember_position();
        self.dirty = true;
        true
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
        // A screen row here is a *visual* row: once a long line soft-wraps it
        // no longer lines up with a logical line, so this has to go through
        // the same wrapped geometry the composer was drawn with rather than
        // `move_cursor_to`, which assumes row and logical line are the same
        // thing.
        let visual_row = (row - origin_y) as usize;
        if visual_row >= self.composer.visual_line_count(self.composer_text_width) {
            return false;
        }
        // Left of the prompt marker counts as column zero rather than missing,
        // so clicking the gutter puts the caret at the start of the line.
        let cell = column.saturating_sub(origin_x) as usize;
        self.composer.move_cursor_to_visual(self.composer_text_width, visual_row, cell);
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

/// Whether `(column, row)` falls within the scrollbar column drawn this frame.
///
/// Unlike the header id this spans many rows, so both axes are bounded.
fn scrollbar_hit(rect: ratatui::layout::Rect, column: u16, row: u16) -> bool {
    column >= rect.x
        && column < rect.x + rect.width
        && row >= rect.y
        && row < rect.y + rect.height
}

/// Where a press `offset_in_track` rows down a track takes hold of a thumb of
/// `size` rows currently at `position`: the grip to keep for the rest of the
/// drag, and the thumb top to jump to first, if any.
///
/// A press *on* the thumb grabs it exactly where it was taken hold of, so the
/// transcript does not lurch under the pointer. A press on the bare track has
/// no such grip to keep, so the thumb is centred on the pointer and then held
/// there — which is what lets a click-to-jump continue straight into a drag
/// instead of needing a second gesture.
///
/// A free function so the arithmetic is testable without a live `App` (building
/// one needs a real engine).
fn scrollbar_grab(position: usize, size: usize, offset_in_track: usize) -> (usize, Option<usize>) {
    if (position..position + size).contains(&offset_in_track) {
        (offset_in_track - position, None)
    } else {
        let centre = size / 2;
        (centre, Some(offset_in_track.saturating_sub(centre)))
    }
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
    fn scrollbar_hit_bounds_both_axes() {
        let rect = ratatui::layout::Rect::new(79, 1, 1, 20);
        assert!(scrollbar_hit(rect, 79, 1), "top of the track");
        assert!(scrollbar_hit(rect, 79, 20), "bottom of the track");
        assert!(!scrollbar_hit(rect, 79, 21), "past the foot");
        assert!(!scrollbar_hit(rect, 79, 0), "above the head");
        assert!(!scrollbar_hit(rect, 78, 10), "left of the column");
        assert!(!scrollbar_hit(rect, 80, 10), "right of the column");
    }

    #[test]
    fn pressing_the_thumb_keeps_the_grip_it_was_taken_hold_of() {
        // A five-row thumb sitting three rows down, pressed on its middle row.
        let (grab, jump) = scrollbar_grab(3, 5, 5);
        assert_eq!(grab, 2, "two rows below the thumb's top");
        assert_eq!(jump, None, "pressing the thumb must not move it");
    }

    #[test]
    fn pressing_the_thumb_at_either_end_grabs_that_end() {
        assert_eq!(scrollbar_grab(3, 5, 3), (0, None), "its first row");
        assert_eq!(scrollbar_grab(3, 5, 7), (4, None), "its last row");
        assert!(scrollbar_grab(3, 5, 8).1.is_some(), "one past it is the track");
    }

    #[test]
    fn pressing_the_bare_track_centres_the_thumb_on_the_pointer_and_holds_it() {
        let (grab, jump) = scrollbar_grab(0, 4, 10);
        assert_eq!(jump, Some(8), "the thumb's centre lands on the pointer");
        assert_eq!(grab, 2, "and is then held at that centre");
        // The grip must be self-consistent: the thumb top plus the grip is
        // where the pointer actually is, so the very next drag event does not
        // shift the content.
        assert_eq!(jump.unwrap() + grab, 10);
    }

    #[test]
    fn pressing_the_track_above_a_thumb_bigger_than_the_press_parks_at_the_top() {
        let (grab, jump) = scrollbar_grab(8, 6, 1);
        assert_eq!(jump, Some(0), "no room above: park at the head of the track");
        assert_eq!(grab, 3);
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