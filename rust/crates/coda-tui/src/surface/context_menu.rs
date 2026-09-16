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
use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
use ratatui::{layout::Rect, text::Line};

/// The width the menu prefers before degrading to full-screen on a tiny
/// terminal. Wide enough for the entry labels and a short disabled-reason note.
pub const PREFERRED_WIDTH: u16 = 48;
const SIDE_PADDING: usize = 2;

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
        Self { url, entries, selected }
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
}
