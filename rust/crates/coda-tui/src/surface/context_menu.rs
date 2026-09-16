//! The right-click context menu for a hyperlink in the transcript.
//!
//! A link is model-generated content, so acting on it is an action with real
//! consequences — a browser opens, a URL reaches the OS. Rather than a single
//! blind gesture, a right-click offers a small, explicit menu: copy the
//! destination, open it, or open it in a private window. The menu is an
//! ordinary [`Surface`], so it obeys the one rule that makes surfaces testable
//! — it *cannot* touch the clipboard or spawn a browser itself. It states the
//! chosen action as a [`SurfaceAction`]; only the application performs it.

use super::{Placement, Surface, SurfaceAction, SurfaceOutcome};
use crate::render::glyphs;
use coda_render::theme::{Role, Theme};
use crossterm::event::{
    KeyCode, KeyEvent, KeyModifiers, MouseButton, MouseEvent, MouseEventKind,
};
use ratatui::{layout::Rect, text::Line};

/// The width the menu prefers before degrading to full-screen on a tiny
/// terminal. Wide enough for the entry labels and a short disabled-reason note.
pub const PREFERRED_WIDTH: u16 = 48;
const SIDE_PADDING: usize = 2;

/// Rows drawn before the first entry: a blank, the destination, a blank.
///
/// Named so the pointer hit-test and the renderer agree on where an entry
/// lands — entry `i` is drawn at content row `HEADER_ROWS + i`. A
/// `debug_assert!` in [`LinkMenuSurface::render`] pins the two together, so a
/// change to the header that forgot to update this fails a test rather than
/// silently making clicks land a row off.
const HEADER_ROWS: usize = 3;

/// Which action a menu entry stands for.
///
/// Deliberately tiny and `Copy`: it travels inside a [`SurfaceAction`], which
/// must be `Clone`/`PartialEq`, and it names the *intent* — the application
/// maps each to the real clipboard write or browser launch, so the surface
/// never has to know how any of them are carried out.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LinkAction {
    /// Copy the destination URL to the clipboard.
    Copy,
    /// Open the destination in the default browser.
    Open,
    /// Open the destination in a private/incognito window.
    OpenPrivate,
}

/// One row of the menu.
struct MenuEntry {
    action: LinkAction,
    label: &'static str,
    /// Whether selecting this entry does anything.
    ///
    /// A disabled entry is shown, not hidden: hiding "open in private window"
    /// when no private-capable browser exists would leave the user wondering
    /// where it went, so it stays visible with a reason and simply swallows
    /// Enter. Better a dimmed row that explains itself than a silent lie that
    /// opened a normal window.
    enabled: bool,
    /// Shown after the label when disabled, so the row explains why.
    note: Option<&'static str>,
}

/// The link context menu.
pub struct LinkMenuSurface {
    url: String,
    entries: Vec<MenuEntry>,
    selected: usize,
    /// The pointer cell the menu was opened on, so it can appear beside the
    /// click. `None` centres it — the right default for a menu raised any other
    /// way than by a pointer.
    anchor: Option<(u16, u16)>,
}

impl LinkMenuSurface {
    /// Builds the menu for `url`.
    ///
    /// `can_open` gates the two "open" entries on the scheme being one the OS
    /// will accept (http/https/mailto); `can_open_private` additionally
    /// requires a private-capable browser to have been found. Both are computed
    /// by the application before the menu is built, because resolving them
    /// touches the filesystem — something a surface must never do.
    pub fn new(url: String, can_open: bool, can_open_private: bool) -> Self {
        let entries = vec![
            MenuEntry {
                action: LinkAction::Copy,
                label: "Copy to clipboard",
                enabled: true,
                note: None,
            },
            MenuEntry {
                action: LinkAction::Open,
                label: "Open in browser",
                enabled: can_open,
                note: (!can_open).then_some("only http, https and mailto links open"),
            },
            MenuEntry {
                action: LinkAction::OpenPrivate,
                label: "Open in private window",
                enabled: can_open_private,
                note: (!can_open_private).then_some("no private-capable browser found"),
            },
        ];
        // Start on the first enabled row so the default Enter never lands on a
        // dead entry. Copy is always enabled and first, so this is normally 0.
        let selected = entries.iter().position(|entry| entry.enabled).unwrap_or(0);
        Self { url, entries, selected, anchor: None }
    }

    /// Anchors the menu to a pointer cell, so it opens beside the click like a
    /// native context menu instead of centred.
    ///
    /// Consumed and returned by value so a call site reads as
    /// `LinkMenuSurface::new(..).at(col, row)`; the anchor only affects where
    /// the box is drawn, never how big it is.
    pub fn at(mut self, column: u16, row: u16) -> Self {
        self.anchor = Some((column, row));
        self
    }

    /// The entry index at a pointer cell, given the menu's drawn `content` rect.
    ///
    /// Returns `None` for a cell outside the content or on a non-entry row (the
    /// blank lines or the destination), so a click on the menu's own body never
    /// activates an entry. Entry `i` sits at content row `HEADER_ROWS + i`,
    /// matching what [`render`](Self::render) draws.
    fn entry_at(&self, column: u16, row: u16, content: Rect) -> Option<usize> {
        if !within(column, row, content) {
            return None;
        }
        let index = ((row - content.y) as usize).checked_sub(HEADER_ROWS)?;
        (index < self.entries.len()).then_some(index)
    }

    /// Moves the highlight, wrapping top-to-bottom, over *every* row.
    ///
    /// Disabled rows are still visitable: the user may want to read the reason
    /// a row is off. Enter is what respects `enabled`, not navigation.
    fn move_selection(&mut self, delta: i32) {
        let count = self.entries.len() as i32;
        if count == 0 {
            return;
        }
        self.selected = (((self.selected as i32 + delta) % count + count) % count) as usize;
    }

    /// Emits the highlighted entry's action, or swallows Enter on a disabled row.
    fn activate(&self) -> SurfaceOutcome {
        let entry = &self.entries[self.selected];
        if !entry.enabled {
            return SurfaceOutcome::Handled;
        }
        SurfaceOutcome::Emit(SurfaceAction::LinkAction {
            url: self.url.clone(),
            action: entry.action,
        })
    }
}

impl Surface for LinkMenuSurface {
    fn title(&self) -> String {
        "Link".to_string()
    }

    fn hints(&self) -> String {
        // Arrows and the middot are prose punctuation, written raw as the
        // hint style does elsewhere; the glyph conventions exempt them.
        "↑/↓ move · Enter select · Esc close".to_string()
    }

    fn placement(&self) -> Placement {
        Placement::FitContent { preferred_width: PREFERRED_WIDTH }
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        // A chord belongs to the global keymap (Ctrl+C must keep working), so
        // only unmodified keys drive the menu.
        if key.modifiers.intersects(KeyModifiers::CONTROL | KeyModifiers::ALT) {
            return SurfaceOutcome::Ignored;
        }
        match key.code {
            KeyCode::Esc => SurfaceOutcome::Close,
            KeyCode::Up | KeyCode::Char('k') => {
                self.move_selection(-1);
                SurfaceOutcome::Handled
            }
            KeyCode::Down | KeyCode::Char('j') => {
                self.move_selection(1);
                SurfaceOutcome::Handled
            }
            KeyCode::Enter => self.activate(),
            _ => SurfaceOutcome::Ignored,
        }
    }

    fn handle_mouse(&mut self, event: MouseEvent, content: Rect) -> SurfaceOutcome {
        match event.kind {
            // Hover moves the highlight onto the entry under the pointer, the
            // same feel as link hover in the transcript. Only a real change is
            // worth a redraw, so an unchanged hover is Ignored and costs
            // nothing — a move fires for every cell the pointer crosses.
            MouseEventKind::Moved => match self.entry_at(event.column, event.row, content) {
                Some(index) if index != self.selected => {
                    self.selected = index;
                    SurfaceOutcome::Handled
                }
                _ => SurfaceOutcome::Ignored,
            },
            // The press is swallowed and the choice is made on release, so the
            // click that closes the menu cannot also fall through and act on
            // whatever the menu was covering. A release on an entry activates
            // it; on the menu's own body it is swallowed so the menu stays
            // open; anywhere outside it dismisses, the way a context menu does.
            MouseEventKind::Down(MouseButton::Left) => SurfaceOutcome::Handled,
            MouseEventKind::Up(MouseButton::Left) => {
                if let Some(index) = self.entry_at(event.column, event.row, content) {
                    self.selected = index;
                    self.activate()
                } else if within(event.column, event.row, content) {
                    SurfaceOutcome::Handled
                } else {
                    SurfaceOutcome::Close
                }
            }
            _ => SurfaceOutcome::Ignored,
        }
    }

    fn anchor(&self) -> Option<(u16, u16)> {
        self.anchor
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        let padding = SIDE_PADDING.min((area.width as usize).saturating_sub(1) / 2);
        let width = (area.width as usize).saturating_sub(padding * 2).max(1);
        let pad = " ".repeat(padding);

        let dim = theme.style(Role::Notification);
        let selected_style = theme.style(Role::FocusText);

        // The destination, shown so the user can verify where a click will go
        // before it goes there — the whole point of a menu over a blind open.
        let mut lines: Vec<Line<'static>> = vec![
            Line::raw(""),
            Line::styled(
                format!("{pad}{}", coda_render::text::truncate_with_ellipsis(&self.url, width)),
                dim,
            ),
            Line::raw(""),
        ];
        // The hit-test assumes entry `i` begins at content row HEADER_ROWS, so
        // the header the loop draws over must be exactly that tall. Pinned here
        // rather than trusted, because a click landing a row off is invisible
        // until someone tries it.
        debug_assert_eq!(lines.len(), HEADER_ROWS);

        for (index, entry) in self.entries.iter().enumerate() {
            let marker = if index == self.selected {
                glyphs::OPTION_MARKER
            } else {
                glyphs::OPTION_BLANK
            };
            let mut label = entry.label.to_string();
            if let Some(note) = entry.note {
                label.push_str(&format!(" ({note})"));
            }
            let style = if index == self.selected && entry.enabled {
                selected_style
            } else {
                dim
            };
            lines.push(Line::styled(
                format!("{pad}{marker}{}", coda_render::text::truncate_with_ellipsis(&label, width.saturating_sub(2))),
                style,
            ));
        }

        lines.push(Line::raw(""));
        lines
    }

    fn as_any(&self) -> &dyn std::any::Any {
        self
    }
}

/// Whether a cell falls inside a rect, half-open on the right and bottom so a
/// rect's own width and height bound it exactly.
///
/// A free function because both the entry hit-test and the outside-click
/// dismissal ask the same question, and answering it in one place keeps the
/// two from disagreeing about where the menu ends.
fn within(column: u16, row: u16, rect: Rect) -> bool {
    column >= rect.x && column < rect.right() && row >= rect.y && row < rect.bottom()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    #[test]
    fn the_menu_offers_copy_open_and_open_in_a_private_window() {
        let menu = LinkMenuSurface::new("https://example.com".into(), true, true);
        let labels: Vec<_> = menu.entries.iter().map(|entry| entry.label).collect();
        assert_eq!(
            labels,
            vec!["Copy to clipboard", "Open in browser", "Open in private window"]
        );
    }

    #[test]
    fn enter_routes_each_entry_to_its_own_action() {
        // The handlers are faked by reading the emitted action rather than
        // performing it — the surface cannot open a browser, by construction.
        let cases = [
            (0usize, LinkAction::Copy),
            (1, LinkAction::Open),
            (2, LinkAction::OpenPrivate),
        ];
        for (index, expected) in cases {
            let mut menu = LinkMenuSurface::new("https://example.com/x".into(), true, true);
            for _ in 0..index {
                menu.handle_key(key(KeyCode::Down));
            }
            match menu.handle_key(key(KeyCode::Enter)) {
                SurfaceOutcome::Emit(SurfaceAction::LinkAction { url, action }) => {
                    assert_eq!(url, "https://example.com/x");
                    assert_eq!(action, expected, "entry {index} routed to the wrong action");
                }
                _ => panic!("entry {index} did not emit a link action"),
            }
        }
    }

    #[test]
    fn a_disabled_entry_swallows_enter_without_emitting() {
        // No private browser: the third entry is disabled and must do nothing.
        let mut menu = LinkMenuSurface::new("https://example.com".into(), true, false);
        menu.handle_key(key(KeyCode::Down)); // -> Open in browser
        menu.handle_key(key(KeyCode::Down)); // -> Open in private window (disabled)
        assert!(matches!(menu.handle_key(key(KeyCode::Enter)), SurfaceOutcome::Handled));
    }

    #[test]
    fn clicking_open_on_a_non_web_link_is_impossible_because_the_entry_is_disabled() {
        // A relative link cannot open; the entry is off and swallows Enter.
        let mut menu = LinkMenuSurface::new("./docs/readme.md".into(), false, false);
        menu.handle_key(key(KeyCode::Down)); // -> Open in browser (disabled)
        assert!(matches!(menu.handle_key(key(KeyCode::Enter)), SurfaceOutcome::Handled));
    }

    #[test]
    fn escape_closes_the_menu() {
        let mut menu = LinkMenuSurface::new("https://example.com".into(), true, true);
        assert!(matches!(menu.handle_key(key(KeyCode::Esc)), SurfaceOutcome::Close));
    }

    #[test]
    fn a_control_chord_falls_through_to_the_global_keymap() {
        let mut menu = LinkMenuSurface::new("https://example.com".into(), true, true);
        let chord = KeyEvent::new(KeyCode::Char('c'), KeyModifiers::CONTROL);
        assert!(matches!(menu.handle_key(chord), SurfaceOutcome::Ignored));
    }

    #[test]
    fn the_menu_shows_the_destination_so_the_user_can_verify_it() {
        let menu = LinkMenuSurface::new("https://example.com/page".into(), true, false);
        let lines = menu.render(Rect::new(0, 0, PREFERRED_WIDTH, 12), &Theme::default());
        let text: String = lines
            .iter()
            .map(ToString::to_string)
            .collect::<Vec<_>>()
            .join("\n");
        assert!(text.contains("https://example.com/page"), "the URL must be visible");
        assert!(text.contains("Open in private window"));
    }

    // ── Pointer ──────────────────────────────────────────────────────────────

    /// A content rect whose entries land at known rows.
    ///
    /// Entry `i` sits at `content.y + HEADER_ROWS + i`, so with `y == 5` the
    /// three entries are at rows 8, 9 and 10. The height covers them and the
    /// trailing blank, matching a real drawn menu.
    fn content() -> Rect {
        Rect::new(10, 5, PREFERRED_WIDTH, 8)
    }

    fn mouse(kind: MouseEventKind, column: u16, row: u16) -> MouseEvent {
        MouseEvent { kind, column, row, modifiers: KeyModifiers::NONE }
    }

    fn click(column: u16, row: u16) -> MouseEvent {
        // A click is a release: the menu makes its choice on button-up so the
        // closing click cannot fall through to what it was covering.
        mouse(MouseEventKind::Up(MouseButton::Left), column, row)
    }

    #[test]
    fn a_left_click_on_an_entry_routes_to_its_own_action() {
        // The handlers are faked by reading the emitted action rather than
        // performing it — the surface cannot open a browser, by construction.
        let cases = [
            (8u16, LinkAction::Copy),
            (9, LinkAction::Open),
            (10, LinkAction::OpenPrivate),
        ];
        for (row, expected) in cases {
            let mut menu = LinkMenuSurface::new("https://example.com/x".into(), true, true);
            match menu.handle_mouse(click(15, row), content()) {
                SurfaceOutcome::Emit(SurfaceAction::LinkAction { url, action }) => {
                    assert_eq!(url, "https://example.com/x");
                    assert_eq!(action, expected, "the entry at row {row} routed wrongly");
                }
                _ => panic!("the entry at row {row} did not emit a link action"),
            }
        }
    }

    #[test]
    fn a_left_click_on_a_disabled_entry_is_swallowed_without_emitting() {
        // No private browser: the third entry (row 10) is disabled and a click
        // on it must do nothing, exactly like Enter on it.
        let mut menu = LinkMenuSurface::new("https://example.com".into(), true, false);
        assert!(matches!(menu.handle_mouse(click(15, 10), content()), SurfaceOutcome::Handled));
    }

    #[test]
    fn a_left_click_outside_the_menu_dismisses_it() {
        let mut menu = LinkMenuSurface::new("https://example.com".into(), true, true);
        // Far above-left of the content rect.
        assert!(matches!(menu.handle_mouse(click(0, 0), content()), SurfaceOutcome::Close));
    }

    #[test]
    fn a_left_click_on_the_menu_body_keeps_it_open() {
        // The destination line sits at content.y + 1; clicking it is neither an
        // entry nor outside, so the menu stays put rather than dismissing.
        let mut menu = LinkMenuSurface::new("https://example.com".into(), true, true);
        assert!(matches!(menu.handle_mouse(click(15, 6), content()), SurfaceOutcome::Handled));
    }

    #[test]
    fn the_press_is_swallowed_so_the_closing_click_cannot_fall_through() {
        // Button-down never acts; only the release does. Without this the click
        // that dismisses the menu would also reach the transcript underneath.
        let mut menu = LinkMenuSurface::new("https://example.com".into(), true, true);
        let press = mouse(MouseEventKind::Down(MouseButton::Left), 0, 0);
        assert!(matches!(menu.handle_mouse(press, content()), SurfaceOutcome::Handled));
    }

    #[test]
    fn hovering_an_entry_highlights_it_and_an_unchanged_hover_costs_no_redraw() {
        let mut menu = LinkMenuSurface::new("https://example.com".into(), true, true);
        assert_eq!(menu.selected, 0);
        // Moving onto the third entry highlights it and asks for a redraw.
        let onto = mouse(MouseEventKind::Moved, 15, 10);
        assert!(matches!(menu.handle_mouse(onto, content()), SurfaceOutcome::Handled));
        assert_eq!(menu.selected, 2, "hover did not follow the pointer");
        // Moving within the same entry changes nothing, so it is Ignored and
        // the frame is never marked dirty for it.
        let within_same = mouse(MouseEventKind::Moved, 16, 10);
        assert!(matches!(menu.handle_mouse(within_same, content()), SurfaceOutcome::Ignored));
        assert_eq!(menu.selected, 2);
    }

    #[test]
    fn a_click_activates_the_entry_under_the_pointer_even_without_a_prior_hover() {
        // Clicking entry 1 directly must select and emit it, not the default 0.
        let mut menu = LinkMenuSurface::new("https://example.com".into(), true, true);
        match menu.handle_mouse(click(15, 9), content()) {
            SurfaceOutcome::Emit(SurfaceAction::LinkAction { action, .. }) => {
                assert_eq!(action, LinkAction::Open);
            }
            _ => panic!("a direct click did not emit a link action"),
        }
    }

    #[test]
    fn the_menu_centres_by_default_and_anchors_to_a_click_when_asked() {
        // Raised any way but by a pointer, the menu centres (no anchor); raised
        // by a right-click it remembers the cell so it can open beside it.
        assert_eq!(
            LinkMenuSurface::new("https://example.com".into(), true, true).anchor(),
            None
        );
        assert_eq!(
            LinkMenuSurface::new("https://example.com".into(), true, true)
                .at(7, 3)
                .anchor(),
            Some((7, 3))
        );
    }
}
