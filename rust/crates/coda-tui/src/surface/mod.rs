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

pub mod browser;
pub mod effort;
pub mod form;
pub mod mcp_editor;
pub mod prompt;
pub mod settings;
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
    /// A bordered modal whose outer size is computed from the surface's own
    /// content rather than from a percentage of the terminal.
    ///
    /// The stack calls [`Surface::desired_size`] at `preferred_width` (capped
    /// to the available content width) and builds the modal around the result.
    /// This ensures the width used to measure is always the width rendered at,
    /// which is how oversized modals and mismatched-width double-renders are
    /// both avoided.
    ///
    /// Degrades to [`Placement::Full`] when the terminal is too small to hold
    /// the minimum modal chrome, matching the behaviour of `Modal`.
    FitContent { preferred_width: u16 },
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
            // FitContent degrades on the same chrome thresholds as Modal.
            // Better to go full-screen than to clip the actionable footer or
            // hide a focused control off the bottom.
            Placement::FitContent { .. }
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
///
/// Variants are added when a surface emits them, not in advance: an arm with
/// no caller is dead code that reads as a working feature.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SurfaceAction {
    /// Persist the settings held by the surface that emitted this.
    SaveSettings,
    /// Reply to the open engine prompt.
    ///
    /// Carries the decision, not the transport: the surface works out what
    /// the answer is and the application sends it, because the responder is
    /// engine state a surface must not hold.
    AnswerPrompt {
        allowed: bool,
        answer: Option<String>,
    },
    /// Persist the MCP server held by the editor that emitted this.
    SaveMcpServer,

    // ── Browser row actions ────────────────────────────────────────────────
    //
    // Named after the work rather than the browser, so dispatching them needs
    // no `BrowserKind` lookup. A browser declares these when it is built, next
    // to its columns and its rows, which is what stops one being forgotten.
    /// Switch the active model and restart the engine against this session.
    SwitchModel(String),
    /// Restart the engine against a stored session.
    ResumeSession(String),
    /// Enable or disable an installed plugin.
    TogglePlugin(String),
    /// Pull the latest for a git-installed plugin.
    UpdatePlugin(String),
    /// Enable or disable a configured MCP server.
    ToggleMcp(String),
    /// Open the editor on a new MCP server.
    NewMcpServer,
    /// Open the editor on an existing MCP server.
    EditMcpServer(String),
    /// Remove an MCP server from its `.mcp.json`.
    DeleteMcpServer(String),
    /// Remove a scheduled task.
    DeleteSchedule(String),
    /// Explain that creating a schedule needs arguments.
    ExplainScheduleCreation,
    /// Explain that a skill cannot be toggled from the browser.
    ExplainSkillToggle,

    /// Set the reasoning-effort level for the session.
    ///
    /// `persist` distinguishes "Enter" (save to settings) from "s" (session
    /// only, no disk write). `for_model` is the `(provider, model)` identity
    /// at the moment the picker was opened, so the apply handler can write
    /// the per-model preference without a race if the model changed while the
    /// picker was visible.
    SetEffort {
        effort: String,
        persist: bool,
        for_model: (String, String),
    },

    /// Open the effort picker for the current session model.
    ///
    /// Raised by the bare `/effort` slash command and row actions where full
    /// async is not available; the application opens the picker against the
    /// model currently in effect.
    OpenEffortPicker,

    /// Switch to the given model, then open the effort picker for it.
    ///
    /// Raised by the model browser's `e` key: it identifies the row so the
    /// picker edits *that* model's effort, not whatever happened to be current.
    /// The switch is intentional and visible (the picker title names the
    /// model), never a silent edit of a different model.
    OpenEffortPickerForModel(String),

    /// Adjust the highlighted model without activating it.
    AdjustModelEffort { model: String, direction: i32 },

    /// A browser row action that needs the engine or the filesystem.
    ///
    /// Carries the browser's kind, so the host knows what the row refers to
    /// without keeping a parallel field in step with the stack.
    Browser {
        kind: browser::BrowserKind,
        intent: crate::overlay::Intent,
    },
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

    fn cursor(&self, _area: Rect, _theme: &Theme) -> Option<(u16, u16)> {
        None
    }

    /// The preferred content size (width, height) at most `max_width` wide,
    /// **excluding chrome** (border + hints footer).
    ///
    /// Used by [`Placement::FitContent`] so the stack can size the modal to
    /// exactly what the surface needs rather than to a percentage of the
    /// terminal.
    ///
    /// The default implementation probes [`render`] at `max_width` and returns
    /// the line count as the height. Both width and height are then clamped and
    /// the border is added back by the stack, so surfaces that know their own
    /// dimensions can override for efficiency.
    ///
    /// **Important**: the width returned must equal the width that will be
    /// passed to [`render`], because the stack uses this value to construct the
    /// content `Rect`. Returning a different width would cause the surface to
    /// render into an area it did not size itself for.
    fn desired_size(&self, max_width: u16, theme: &Theme) -> (u16, u16) {
        // Probe with a tall area so the surface does not clip itself. The
        // half-max avoids overflow in callers that multiply this.
        let probe = Rect::new(0, 0, max_width.max(1), u16::MAX / 2);
        let lines = self.render(probe, theme);
        (max_width, lines.len() as u16)
    }

    /// Recovers the concrete type, so the action interpreter can read typed
    /// values back out of the surface that emitted an action.
    fn as_any(&self) -> &dyn std::any::Any;
}

/// Chrome geometry, shared by the stack and the renderer.
///
/// Both need the same answer to "how much room does the content actually get".
/// Computing it in two places is how a surface ends up scrolling against one
/// height while being drawn into another — content that silently disappears
/// with nothing in either file looking wrong.
pub mod chrome {
    use super::{Placement, Rect};

    /// Rows the border costs: one at the top, one at the bottom.
    pub const BORDER_ROWS: u16 = 2;
    /// Columns the border and its one-column padding cost, per side.
    pub const BORDER_COLS: u16 = 2;
    /// Most rows the hint footer may take before it is truncated.
    ///
    /// Two, because a hint list long enough to need three is too long to read
    /// at a glance and should be shortened instead.
    pub const MAX_HINT_ROWS: u16 = 2;

    /// Whether a placement is drawn inside a bordered box.
    ///
    /// Only a modal floats and therefore needs edges to read as bounded. A
    /// full-screen surface owns the screen, and an inline or split one is
    /// contiguous with the shell — bordering those would draw a box around the
    /// whole terminal, or a rail down the middle of a pane that has no edge.
    pub fn is_bordered(placement: Placement) -> bool {
        matches!(
            placement,
            Placement::Modal { .. } | Placement::FitContent { .. }
        )
    }

    /// The area inside the border and padding.
    ///
    /// A region too small to hold its own chrome yields an empty rect at the
    /// region's own origin, rather than an origin pushed outside it. Nothing
    /// draws either way, but a caller doing arithmetic on the origin would be
    /// working from a point that is not in the region at all.
    pub fn inner(region: Rect, placement: Placement) -> Rect {
        if !is_bordered(placement) {
            return region;
        }
        let width = region.width.saturating_sub(BORDER_COLS * 2);
        let height = region.height.saturating_sub(BORDER_ROWS);
        if width == 0 || height == 0 {
            return Rect::new(region.x, region.y, 0, 0);
        }
        Rect::new(region.x + BORDER_COLS, region.y + 1, width, height)
    }

    /// How many rows `hints` needs at `width`.
    pub fn hint_rows(hints: &str, width: u16) -> u16 {
        if hints.is_empty() || width == 0 {
            return 0;
        }
        (coda_render::text::wrap(hints, width as usize).len() as u16).min(MAX_HINT_ROWS)
    }

    /// The area a surface's own content gets: inside the chrome, above hints.
    pub fn content(region: Rect, hints: &str, placement: Placement) -> Rect {
        let inner = inner(region, placement);
        let footer = hint_rows(hints, inner.width);
        Rect::new(
            inner.x,
            inner.y,
            inner.width,
            inner.height.saturating_sub(footer),
        )
    }

    /// The area the hint footer occupies.
    pub fn footer(region: Rect, hints: &str, placement: Placement) -> Rect {
        let inner = inner(region, placement);
        let rows = hint_rows(hints, inner.width).min(inner.height);
        Rect::new(
            inner.x,
            inner.bottom().saturating_sub(rows),
            inner.width,
            rows,
        )
    }

    /// Computes the outer region for a [`Placement::FitContent`] modal given
    /// the surface's preferred content size (excluding chrome) and available
    /// terminal area.
    ///
    /// The modal is centered, capped to `area`, and always at least 1×1. The
    /// same `hints` text used to measure hint rows here must be the one passed
    /// to [`content`] and [`footer`] so the three regions stay consistent.
    pub fn fit_content_region(content_size: (u16, u16), hints: &str, area: Rect) -> Rect {
        let (cw, ch) = content_size;
        let h_rows = hint_rows(hints, cw);
        let want_w = cw.saturating_add(BORDER_COLS * 2);
        let want_h = ch.saturating_add(h_rows).saturating_add(BORDER_ROWS);
        let w = want_w.min(area.width).max(1);
        let h = want_h.min(area.height).max(1);
        let x = area.x + area.width.saturating_sub(w) / 2;
        let y = area.y + area.height.saturating_sub(h) / 2;
        Rect::new(x, y, w, h)
    }
}

/// Turns a resolved placement into a concrete region of `area`.
///
/// For [`Placement::FitContent`], the stack calls [`chrome::fit_content_region`]
/// with the surface's own [`Surface::desired_size`] instead of this function,
/// because only the stack has access to the surface. This fallback is provided
/// for callers outside the stack (tests, future uses) and returns a centered
/// region at the preferred width spanning the full terminal height.
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
        Placement::FitContent { preferred_width } => {
            // The stack uses desired_size instead. This fallback centers the
            // preferred width and spans the full terminal height.
            let w = preferred_width.saturating_add(chrome::BORDER_COLS * 2)
                .min(area.width)
                .max(1);
            let x = area.x + area.width.saturating_sub(w) / 2;
            Rect::new(x, area.y, w, area.height)
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
        assert_eq!(stub.cursor(Rect::new(0, 0, 10, 10), &Theme::default()), None);
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
    fn content_and_footer_partition_the_inner_area_without_overlapping() {
        // The stack renders into `content` and the drawer puts hints in
        // `footer`. If they overlap, content is drawn and then covered; if
        // they leave a gap, rows are wasted. Both are silent.
        let region = Rect::new(0, 0, 60, 20);
        let hints = "Tab: next    Enter: save    Esc: cancel";
        let content = chrome::content(region, hints, Placement::Modal { width_pct: 70, height_pct: 70 });
        let footer = chrome::footer(region, hints, Placement::Modal { width_pct: 70, height_pct: 70 });
        let inner = chrome::inner(region, Placement::Modal { width_pct: 70, height_pct: 70 });

        assert_eq!(content.y, inner.y);
        assert_eq!(content.bottom(), footer.y, "content and footer disagree");
        assert_eq!(footer.bottom(), inner.bottom());
        assert_eq!(content.height + footer.height, inner.height);
    }

    #[test]
    fn long_hints_are_given_a_second_row_rather_than_being_cut() {
        // A hint line that runs past the border loses whatever is on the
        // right, which is where "Esc: cancel" sits — the one hint a stuck
        // user most needs.
        let narrow = Rect::new(0, 0, 30, 20);
        let long = "Tab: next    Enter: save    Esc: cancel    F1: help";
        assert!(
            chrome::hint_rows(long, chrome::inner(narrow, Placement::Modal { width_pct: 70, height_pct: 70 }).width) > 1,
            "long hints were squeezed onto one row"
        );
    }

    #[test]
    fn hints_never_take_more_than_their_cap() {
        let region = Rect::new(0, 0, 24, 20);
        let absurd = "a ".repeat(200);
        assert!(chrome::hint_rows(&absurd, chrome::inner(region, Placement::Modal { width_pct: 70, height_pct: 70 }).width) <= chrome::MAX_HINT_ROWS);
    }

    #[test]
    fn chrome_survives_a_region_too_small_to_hold_it() {
        for (w, h) in [(0u16, 0u16), (1, 1), (3, 2), (4, 3)] {
            let region = Rect::new(0, 0, w, h);
            let content = chrome::content(region, "Esc", Placement::Modal { width_pct: 70, height_pct: 70 });
            assert!(
                content.right() <= region.right().max(region.x)
                    && content.bottom() <= region.bottom().max(region.y),
                "content escaped a {w}x{h} region: {content:?}"
            );
        }
    }

    #[test]
    fn only_a_modal_is_boxed() {
        // A full-screen surface owns the screen and an inline or split one is
        // contiguous with the shell. Bordering those would draw a box around
        // the whole terminal, or a rail down the middle of a pane with no edge.
        assert!(chrome::is_bordered(Placement::Modal {
            width_pct: 70,
            height_pct: 70
        }));
        assert!(chrome::is_bordered(Placement::FitContent { preferred_width: 60 }));
        assert!(!chrome::is_bordered(Placement::Full));
        assert!(!chrome::is_bordered(Placement::Inline { max_rows: 4 }));
        assert!(!chrome::is_bordered(Placement::Split {
            side: Side::Right,
            width_pct: 50
        }));
    }

    #[test]
    fn an_unboxed_placement_gives_its_content_the_whole_region() {
        // The rows a border would have cost are content instead, so a wizard
        // is not silently four columns and two rows smaller than the screen.
        let region = Rect::new(0, 0, 80, 24);
        let content = chrome::content(region, "", Placement::Full);
        assert_eq!(content.width, region.width);
        assert_eq!(content.height, region.height);
        assert_eq!((content.x, content.y), (region.x, region.y));
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

    // ── FitContent geometry ──────────────────────────────────────────────────

    #[test]
    fn fit_content_region_sizes_to_content_not_to_terminal_percentage() {
        // The whole point of FitContent: 3 lines of content in a 24-row terminal
        // should produce a 3+border row modal, not a 16-row (70%) one.
        let area = Rect::new(0, 0, 80, 24);
        let region = chrome::fit_content_region((60, 3), "Esc: close", area);
        // inner height = content_h + hint_rows; outer = inner + BORDER_ROWS
        // hint_rows("Esc: close", 60) = 1
        let expected_h = 3 + 1 + chrome::BORDER_ROWS; // 6
        assert_eq!(
            region.height, expected_h,
            "short content should produce a compact modal, not {}", region.height
        );
        // width: content 60 + 2*BORDER_COLS = 64
        assert_eq!(region.width, 60 + chrome::BORDER_COLS * 2);
    }

    #[test]
    fn fit_content_region_caps_to_terminal_when_content_is_tall() {
        // A 200-line form must not escape the screen.
        let area = Rect::new(0, 0, 80, 24);
        let region = chrome::fit_content_region((60, 200), "Esc: close", area);
        assert_eq!(region.height, area.height);
        assert!(region.bottom() <= area.bottom());
    }

    #[test]
    fn fit_content_region_caps_to_terminal_when_content_is_wide() {
        // A very wide preferred_width must not escape the screen.
        let area = Rect::new(0, 0, 80, 24);
        let region = chrome::fit_content_region((200, 5), "Esc: close", area);
        assert!(region.right() <= area.right());
        assert_eq!(region.width, area.width);
    }

    #[test]
    fn fit_content_region_is_centered() {
        let area = Rect::new(0, 0, 80, 24);
        let region = chrome::fit_content_region((20, 4), "", area);
        let outer_w = 20 + chrome::BORDER_COLS * 2; // 24
        let expected_x = (80 - outer_w) / 2; // 28
        assert_eq!(region.x, expected_x, "region is not horizontally centered");
        let outer_h = 4 + chrome::BORDER_ROWS; // 6 (no hints)
        let expected_y = (24 - outer_h) / 2; // 9
        assert_eq!(region.y, expected_y, "region is not vertically centered");
    }

    #[test]
    fn fit_content_region_survives_zero_content() {
        // Zero-content surface: should still produce a drawable (1x1) region.
        let area = Rect::new(0, 0, 80, 24);
        let region = chrome::fit_content_region((0, 0), "", area);
        assert!(region.width >= 1 && region.height >= 1);
    }

    #[test]
    fn fit_content_degrades_to_full_on_tiny_terminal() {
        // Below MIN_MODAL_WIDTH/MIN_MODAL_HEIGHT the surface cannot afford its
        // chrome; degrading to Full beats clipping the actionable footer.
        let tiny = Rect::new(0, 0, 10, 4);
        assert_eq!(
            Placement::FitContent { preferred_width: 60 }.resolve(tiny),
            Placement::Full
        );
    }

    #[test]
    fn fit_content_survives_normal_terminal() {
        let area = Rect::new(0, 0, 80, 24);
        assert!(matches!(
            Placement::FitContent { preferred_width: 60 }.resolve(area),
            Placement::FitContent { .. }
        ));
    }

    #[test]
    fn fit_content_content_and_footer_partition_inner_exactly() {
        // Same invariant as Modal: content + footer = inner, no gap, no overlap.
        let area = Rect::new(0, 0, 80, 24);
        let region = chrome::fit_content_region((60, 5), "Tab: next    Esc: cancel", area);
        let p = Placement::FitContent { preferred_width: 60 };
        let inner = chrome::inner(region, p);
        let hints = "Tab: next    Esc: cancel";
        let content = chrome::content(region, hints, p);
        let footer = chrome::footer(region, hints, p);

        assert_eq!(content.y, inner.y);
        assert_eq!(content.bottom(), footer.y, "content and footer must be adjacent");
        assert_eq!(footer.bottom(), inner.bottom());
        assert_eq!(content.height + footer.height, inner.height);
    }

    #[test]
    fn desired_size_default_counts_rendered_lines() {
        // The default desired_size calls render, so a surface that produces N
        // lines at a given width must report N as its preferred height.
        struct Lines(u16);
        impl Surface for Lines {
            fn as_any(&self) -> &dyn std::any::Any { self }
            fn title(&self) -> String { "Lines".into() }
            fn hints(&self) -> String { String::new() }
            fn handle_key(&mut self, _: KeyEvent) -> SurfaceOutcome { SurfaceOutcome::Handled }
            fn render(&self, area: Rect, _: &Theme) -> Vec<Line<'static>> {
                (0..self.0.min(area.height)).map(|i| Line::from(format!("line {i}"))).collect()
            }
        }

        let surface = Lines(5);
        let (w, h) = surface.desired_size(60, &Theme::default());
        assert_eq!(w, 60);
        assert_eq!(h, 5, "desired_size must count what render produces");
    }

    #[test]
    fn stack_render_width_is_consistent_for_fit_content() {
        // The content area passed to render must have the same width as the
        // one used to compute desired_size. A mismatch causes the surface to
        // lay itself out for one width and be drawn at another.
        use crate::surface::stack::SurfaceStack;

        struct WidthRecorder {
            last_width: std::cell::Cell<u16>,
        }
        impl Surface for WidthRecorder {
            fn as_any(&self) -> &dyn std::any::Any { self }
            fn title(&self) -> String { "Recorder".into() }
            fn hints(&self) -> String { "Esc: close".into() }
            fn placement(&self) -> Placement { Placement::FitContent { preferred_width: 60 } }
            fn handle_key(&mut self, _: KeyEvent) -> SurfaceOutcome { SurfaceOutcome::Handled }
            fn desired_size(&self, max_width: u16, _: &Theme) -> (u16, u16) {
                (max_width, 3)
            }
            fn render(&self, area: Rect, _: &Theme) -> Vec<Line<'static>> {
                self.last_width.set(area.width);
                vec![Line::from("content")]
            }
        }

        let mut stack = SurfaceStack::default();
        let recorder = Box::new(WidthRecorder {
            last_width: std::cell::Cell::new(0),
        });
        // We need to inspect the recorder after rendering. Use `as_any` from
        // the rendered surface's perspective — but since we can't after push,
        // verify via the rendered content rect width.
        stack.push(recorder);

        let area = Rect::new(0, 0, 80, 24);
        let rendered = stack.render(area, &Theme::default());
        assert_eq!(rendered.len(), 1);

        // content.width must equal the width desired_size received (60).
        // BORDER_COLS * 2 = 4, so available content width = 80 - 4 = 76.
        // preferred_width = 60 < 76, so content width = 60.
        let content_w = rendered[0].content.width;
        assert_eq!(
            content_w, 60,
            "content.width ({content_w}) did not match preferred_width (60)"
        );
    }

    #[test]
    fn fit_content_region_stays_inside_area() {
        // Matches the invariant tested for other placements.
        let area = Rect::new(0, 0, 80, 24);
        for (cw, ch) in [(0u16, 0u16), (60, 5), (200, 200), (80, 24)] {
            let region = chrome::fit_content_region((cw, ch), "Esc: close", area);
            assert!(
                region.right() <= area.right() && region.bottom() <= area.bottom(),
                "region escaped area for content ({cw}x{ch}): {region:?}"
            );
            assert!(
                region.width >= 1 && region.height >= 1,
                "region vanished for content ({cw}x{ch})"
            );
        }
    }
}
