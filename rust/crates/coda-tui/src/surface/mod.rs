//! The `Surface` abstraction: one contract for every interactive overlay.
//!
//! A surface is a state machine that turns keys into outcomes and renders to
//! lines. It **cannot reach the engine** — no `App`, no async, no RPC, no I/O.
//! That constraint is the load-bearing one: it is what makes every surface
//! testable with a key event and an assertion, and it is why [`SurfaceAction`]
//! exists. A surface states its intent; the application performs the work.
//!
//! Surfaces render to [`Line`]s rather than into a `Frame`, matching the
//! `widgets` module. Keeping them pure means a test needs no terminal, and it
//! sidesteps the borrowed-frame lifetimes that make trait objects awkward. The
//! one thing lines cannot express — where the caret sits — comes back through
//! [`Surface::cursor`].

use coda_render::theme::Theme;
use crossterm::event::KeyEvent;
use ratatui::layout::Rect;
use ratatui::text::Line;

pub mod stack;

/// Below this width a split pane leaves too little for either side.
const MIN_SPLIT_WIDTH: u16 = 40;
/// Below these a modal's border and padding cost more than they give.
const MIN_MODAL_WIDTH: u16 = 24;
const MIN_MODAL_HEIGHT: u16 = 8;

/// Which side a split pane docks to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Side {
    Left,
    Right,
}

/// Where a surface wants to be drawn.
///
/// Declared by the surface rather than chosen by the caller, so a split-pane
/// diff and a full-screen wizard need no special-casing at each call site.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Placement {
    Modal { width_pct: u16, height_pct: u16 },
    Full,
    Split { side: Side, width_pct: u16 },
    Inline { max_rows: u16 },
}

impl Placement {
    /// Degrades this placement until it fits `area`.
    ///
    /// Degrading rather than clipping means a small terminal shows a usable
    /// surface instead of a truncated one. A split that cannot afford two
    /// columns becomes a modal; a modal that cannot afford its chrome becomes
    /// full screen.
    pub fn resolve(self, area: Rect) -> Placement {
        match self {
            Placement::Split { .. } if area.width < MIN_SPLIT_WIDTH => Placement::Modal {
                width_pct: 90,
                height_pct: 80,
            }
            .resolve(area),
            Placement::Modal { .. }
                if area.width < MIN_MODAL_WIDTH || area.height < MIN_MODAL_HEIGHT =>
            {
                Placement::Full
            }
            other => other,
        }
    }
}

/// Whether a surface can be superseded.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Modality {
    /// Another surface may open above this one.
    Normal,
    /// Nothing may open above, and `Esc` alone cannot dismiss it.
    ///
    /// Used by engine prompts, which block the turn until answered. Making
    /// this a property rather than an ordering rule in the key handler is the
    /// difference between a guarantee and a convention.
    Exclusive,
}

/// Work only the application can do, requested by a surface.
///
/// The single channel from a surface to the engine and the filesystem. A
/// surface never awaits anything.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SurfaceAction {
    /// Persist the settings held by the surface that emitted this.
    SaveSettings,
    /// Restart the engine against a stored session.
    ResumeSession(String),
    /// Run a slash command, without its leading slash.
    RunCommand(String),
    /// Answer the open engine prompt with the option at `index`.
    AnswerPrompt { index: usize },
    /// Refuse the open engine prompt.
    DenyPrompt,
}

/// What a key did.
pub enum SurfaceOutcome {
    /// Consumed; stay open.
    Handled,
    /// Not consumed. The global keymap may act, so `Ctrl+C` keeps working
    /// while a surface is open.
    Ignored,
    /// Pop this surface.
    Close,
    /// Ask the application to act.
    Emit(SurfaceAction),
    /// Open another surface above this one: a detail view, a wizard step.
    Push(Box<dyn Surface>),
    /// Replace this surface, for a wizard advancing a step.
    Replace(Box<dyn Surface>),
}

/// One interactive overlay.
pub trait Surface {
    fn title(&self) -> String;

    /// Key hints for the footer.
    fn hints(&self) -> String;

    fn placement(&self) -> Placement {
        Placement::Modal {
            width_pct: 70,
            height_pct: 70,
        }
    }

    fn modality(&self) -> Modality {
        Modality::Normal
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome;

    /// Renders at most `area.height` lines, scrolled so the focused element is
    /// visible.
    ///
    /// The surface scrolls itself because only it knows which of its rows
    /// matters. Scrolling must key off the focused element's row range rather
    /// than the caret: a switch and a radio group have no caret and would
    /// otherwise be scrolled out of view exactly when focused.
    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>>;

    fn cursor(&self, _area: Rect) -> Option<(u16, u16)> {
        None
    }

    /// Recovers the concrete type, so the action interpreter can read typed
    /// values back out of the surface that emitted an action.
    fn as_any(&self) -> &dyn std::any::Any;
}

/// Turns a resolved placement into a concrete region of `area`.
pub fn region_for(placement: Placement, area: Rect) -> Rect {
    match placement {
        Placement::Full => area,
        Placement::Modal {
            width_pct,
            height_pct,
        } => {
            let w = ((area.width as u32 * width_pct as u32 / 100) as u16)
                .max(1)
                .min(area.width.max(1));
            let h = ((area.height as u32 * height_pct as u32 / 100) as u16)
                .max(1)
                .min(area.height.max(1));
            Rect::new(
                area.x + area.width.saturating_sub(w) / 2,
                area.y + area.height.saturating_sub(h) / 2,
                w,
                h,
            )
        }
        Placement::Split { side, width_pct } => {
            let w = ((area.width as u32 * width_pct as u32 / 100) as u16)
                .max(1)
                .min(area.width.max(1));
            match side {
                Side::Right => Rect::new(area.right().saturating_sub(w), area.y, w, area.height),
                Side::Left => Rect::new(area.x, area.y, w, area.height),
            }
        }
        Placement::Inline { max_rows } => {
            let h = max_rows.min(area.height).max(1);
            Rect::new(area.x, area.bottom().saturating_sub(h), area.width, h)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::{KeyCode, KeyModifiers};

    struct Stub;

    impl Surface for Stub {
        fn as_any(&self) -> &dyn std::any::Any {
            self
        }
        fn title(&self) -> String {
            "Stub".into()
        }
        fn hints(&self) -> String {
            "Esc: close".into()
        }
        fn handle_key(&mut self, _key: KeyEvent) -> SurfaceOutcome {
            SurfaceOutcome::Handled
        }
        fn render(&self, _area: Rect, _theme: &Theme) -> Vec<Line<'static>> {
            Vec::new()
        }
    }

    #[test]
    fn a_surface_defaults_to_a_normal_modal() {
        let mut stub = Stub;
        assert_eq!(stub.modality(), Modality::Normal);
        assert!(matches!(stub.placement(), Placement::Modal { .. }));
        assert_eq!(stub.cursor(Rect::new(0, 0, 10, 10)), None);
        assert!(matches!(
            stub.handle_key(KeyEvent::new(KeyCode::Esc, KeyModifiers::NONE)),
            SurfaceOutcome::Handled
        ));
    }

    #[test]
    fn a_split_too_narrow_to_be_useful_becomes_a_modal() {
        let split = Placement::Split {
            side: Side::Right,
            width_pct: 50,
        };
        assert!(matches!(
            split.resolve(Rect::new(0, 0, 30, 20)),
            Placement::Modal { .. }
        ));
    }

    #[test]
    fn a_modal_too_small_for_its_chrome_becomes_full_screen() {
        assert_eq!(
            Placement::Modal {
                width_pct: 70,
                height_pct: 70
            }
            .resolve(Rect::new(0, 0, 20, 6)),
            Placement::Full
        );
    }

    #[test]
    fn a_split_degrades_all_the_way_when_the_terminal_is_tiny() {
        // Two steps in one: too narrow to split, then too small to be a modal.
        let split = Placement::Split {
            side: Side::Right,
            width_pct: 50,
        };
        assert_eq!(split.resolve(Rect::new(0, 0, 18, 5)), Placement::Full);
    }

    #[test]
    fn placements_stay_inside_the_area_they_are_given() {
        let area = Rect::new(0, 0, 80, 24);
        for placement in [
            Placement::Full,
            Placement::Modal {
                width_pct: 70,
                height_pct: 70,
            },
            Placement::Split {
                side: Side::Right,
                width_pct: 40,
            },
            Placement::Split {
                side: Side::Left,
                width_pct: 40,
            },
            Placement::Inline { max_rows: 6 },
        ] {
            let region = region_for(placement, area);
            assert!(
                region.right() <= area.right() && region.bottom() <= area.bottom(),
                "{placement:?} escaped its area: {region:?}"
            );
            assert!(
                region.width > 0 && region.height > 0,
                "{placement:?} vanished"
            );
        }
    }

    #[test]
    fn an_inline_placement_sits_at_the_bottom() {
        let area = Rect::new(0, 0, 80, 24);
        let region = region_for(Placement::Inline { max_rows: 5 }, area);
        assert_eq!(region.bottom(), area.bottom());
        assert_eq!(region.height, 5);
    }

    #[test]
    fn a_placement_never_vanishes_in_a_one_cell_terminal() {
        // Percentages round to zero here; the region must still be drawable.
        let area = Rect::new(0, 0, 1, 1);
        let region = region_for(
            Placement::Modal {
                width_pct: 70,
                height_pct: 70,
            },
            area,
        );
        assert_eq!((region.width, region.height), (1, 1));
    }
}
