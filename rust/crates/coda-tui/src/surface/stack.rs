//! The surface stack: key routing, render order and modality enforcement.
//!
//! Keys go to the top surface; anything it declines falls through to the
//! global keymap, which is what keeps `Ctrl+C` working while a surface is
//! open. Rendering runs bottom-up so a detail view sits over the list that
//! opened it.

use super::{region_for, Modality, Surface, SurfaceAction, SurfaceOutcome};
use coda_render::theme::Theme;
use crossterm::event::{KeyCode, KeyEvent};
use ratatui::layout::Rect;
use ratatui::text::Line;

/// What handling a key did to the stack as a whole.
pub enum StackOutcome {
    /// A surface consumed the key.
    Handled,
    /// No surface consumed it; the caller's global keymap may act.
    Ignored,
    /// A surface asked the application to do something.
    Action(SurfaceAction),
}

/// One surface's rendered output and where it goes.
pub struct RenderedSurface {
    pub region: Rect,
    pub title: String,
    pub hints: String,
    pub lines: Vec<Line<'static>>,
    /// Caret position, absolute. Only ever set for the top surface.
    pub cursor: Option<(u16, u16)>,
}

/// An ordered set of open surfaces. The last is on top.
#[derive(Default)]
pub struct SurfaceStack {
    surfaces: Vec<Box<dyn Surface>>,
}

impl SurfaceStack {
    pub fn len(&self) -> usize {
        self.surfaces.len()
    }

    pub fn is_empty(&self) -> bool {
        self.surfaces.is_empty()
    }

    pub fn top_title(&self) -> Option<String> {
        self.surfaces.last().map(|s| s.title())
    }

    pub fn top(&self) -> Option<&dyn Surface> {
        self.surfaces.last().map(|s| s.as_ref())
    }

    /// Opens a surface. Returns `false` when an exclusive surface refused it.
    ///
    /// The caller is told rather than silently ignored, so a command that
    /// cannot run while a prompt is up can say so instead of appearing to do
    /// nothing.
    pub fn push(&mut self, surface: Box<dyn Surface>) -> bool {
        if self.top_is_exclusive() {
            return false;
        }
        self.surfaces.push(surface);
        true
    }

    pub fn pop(&mut self) -> Option<Box<dyn Surface>> {
        self.surfaces.pop()
    }

    pub fn clear(&mut self) {
        self.surfaces.clear();
    }

    fn top_is_exclusive(&self) -> bool {
        self.surfaces
            .last()
            .map(|s| s.modality() == Modality::Exclusive)
            .unwrap_or(false)
    }

    pub fn handle_key(&mut self, key: KeyEvent) -> StackOutcome {
        let Some(surface) = self.surfaces.last_mut() else {
            return StackOutcome::Ignored;
        };

        match surface.handle_key(key) {
            SurfaceOutcome::Handled => StackOutcome::Handled,
            SurfaceOutcome::Close => {
                self.surfaces.pop();
                StackOutcome::Handled
            }
            SurfaceOutcome::Emit(action) => StackOutcome::Action(action),
            SurfaceOutcome::Push(next) => {
                self.push(next);
                StackOutcome::Handled
            }
            SurfaceOutcome::Replace(next) => {
                self.surfaces.pop();
                self.surfaces.push(next);
                StackOutcome::Handled
            }
            // The surface passed. Esc is the stack's own key, but an exclusive
            // surface blocks the turn and must be answered, not dismissed.
            SurfaceOutcome::Ignored => {
                if key.code == KeyCode::Esc && !self.top_is_exclusive() {
                    self.surfaces.pop();
                    StackOutcome::Handled
                } else {
                    StackOutcome::Ignored
                }
            }
        }
    }

    /// Renders every surface bottom-up.
    pub fn render(&self, area: Rect, theme: &Theme) -> Vec<RenderedSurface> {
        let top = self.surfaces.len().saturating_sub(1);
        self.surfaces
            .iter()
            .enumerate()
            .map(|(index, surface)| {
                let region = region_for(surface.placement().resolve(area), area);
                // Only the top surface gets a caret: two visible carets would
                // be worse than none, and the terminal only has one.
                let cursor = (index == top)
                    .then(|| surface.cursor(region))
                    .flatten()
                    .map(|(x, y)| (region.x + x, region.y + y));
                RenderedSurface {
                    region,
                    title: surface.title(),
                    hints: surface.hints(),
                    lines: surface.render(region, theme),
                    cursor,
                }
            })
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::{KeyCode, KeyModifiers};

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    /// A surface whose behaviour the test dictates.
    struct Probe {
        name: &'static str,
        modality: Modality,
        action_on_enter: Option<SurfaceAction>,
    }

    impl Probe {
        fn normal(name: &'static str) -> Self {
            Self {
                name,
                modality: Modality::Normal,
                action_on_enter: None,
            }
        }
        fn exclusive(name: &'static str) -> Self {
            Self {
                name,
                modality: Modality::Exclusive,
                action_on_enter: None,
            }
        }
        fn emitting(name: &'static str, action: SurfaceAction) -> Self {
            Self {
                name,
                modality: Modality::Normal,
                action_on_enter: Some(action),
            }
        }
    }

    impl Surface for Probe {
        fn as_any(&self) -> &dyn std::any::Any {
            self
        }
        fn title(&self) -> String {
            self.name.into()
        }
        fn hints(&self) -> String {
            String::new()
        }
        fn modality(&self) -> Modality {
            self.modality
        }
        fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
            match key.code {
                KeyCode::Enter => match self.action_on_enter.clone() {
                    Some(action) => SurfaceOutcome::Emit(action),
                    None => SurfaceOutcome::Handled,
                },
                KeyCode::Char('x') => SurfaceOutcome::Close,
                KeyCode::Char('d') => SurfaceOutcome::Push(Box::new(Probe::normal("detail"))),
                KeyCode::Char('r') => SurfaceOutcome::Replace(Box::new(Probe::normal("step2"))),
                // Anything else falls through, so the stack's own rules apply.
                _ => SurfaceOutcome::Ignored,
            }
        }
        fn render(&self, _area: Rect, _theme: &Theme) -> Vec<Line<'static>> {
            vec![Line::from(self.name)]
        }
    }

    #[test]
    fn keys_go_to_the_top_surface() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("under")));
        stack.push(Box::new(Probe::normal("over")));
        assert_eq!(stack.top_title().as_deref(), Some("over"));
        assert!(matches!(
            stack.handle_key(key(KeyCode::Enter)),
            StackOutcome::Handled
        ));
    }

    #[test]
    fn an_unhandled_key_falls_through_so_global_keys_keep_working() {
        // Without this, opening any surface would disable Ctrl+C.
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("only")));
        assert!(matches!(
            stack.handle_key(key(KeyCode::Char('q'))),
            StackOutcome::Ignored
        ));
    }

    #[test]
    fn an_empty_stack_ignores_everything() {
        let mut stack = SurfaceStack::default();
        assert!(matches!(
            stack.handle_key(key(KeyCode::Enter)),
            StackOutcome::Ignored
        ));
    }

    #[test]
    fn escape_pops_one_level() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("under")));
        stack.push(Box::new(Probe::normal("over")));
        stack.handle_key(key(KeyCode::Esc));
        assert_eq!(stack.top_title().as_deref(), Some("under"));
    }

    #[test]
    fn escape_cannot_dismiss_an_exclusive_surface() {
        // An engine prompt blocks the turn; letting Esc close it would leave
        // the engine waiting on an answer that never comes.
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::exclusive("prompt")));
        assert!(matches!(
            stack.handle_key(key(KeyCode::Esc)),
            StackOutcome::Ignored
        ));
        assert_eq!(stack.top_title().as_deref(), Some("prompt"));
    }

    #[test]
    fn nothing_can_be_pushed_above_an_exclusive_surface() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::exclusive("prompt")));
        let accepted = stack.push(Box::new(Probe::normal("browser")));
        assert!(!accepted, "a surface opened above an exclusive prompt");
        assert_eq!(stack.top_title().as_deref(), Some("prompt"));
        assert_eq!(stack.len(), 1);
    }

    #[test]
    fn an_exclusive_surface_cannot_push_one_over_itself_either() {
        // The guard lives in push(), so an outcome-driven push is covered too.
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::exclusive("prompt")));
        stack.handle_key(key(KeyCode::Char('d')));
        assert_eq!(stack.len(), 1);
    }

    #[test]
    fn close_pops_the_surface_that_asked() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("only")));
        stack.handle_key(key(KeyCode::Char('x')));
        assert!(stack.is_empty());
    }

    #[test]
    fn push_opens_a_detail_over_its_list() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("list")));
        stack.handle_key(key(KeyCode::Char('d')));
        assert_eq!(stack.len(), 2);
        assert_eq!(stack.top_title().as_deref(), Some("detail"));
    }

    #[test]
    fn replace_advances_without_growing_the_stack() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("step1")));
        stack.handle_key(key(KeyCode::Char('r')));
        assert_eq!(stack.len(), 1);
        assert_eq!(stack.top_title().as_deref(), Some("step2"));
    }

    #[test]
    fn an_emitted_action_reaches_the_caller() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::emitting(
            "settings",
            SurfaceAction::SaveSettings,
        )));
        match stack.handle_key(key(KeyCode::Enter)) {
            StackOutcome::Action(SurfaceAction::SaveSettings) => {}
            _ => panic!("the action did not reach the caller"),
        }
    }

    #[test]
    fn only_the_top_surface_gets_a_caret() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("under")));
        stack.push(Box::new(Probe::normal("over")));
        let rendered = stack.render(Rect::new(0, 0, 80, 24), &Theme::default());
        assert_eq!(rendered.len(), 2);
        assert!(rendered[0].cursor.is_none());
    }

    #[test]
    fn surfaces_render_bottom_up() {
        let mut stack = SurfaceStack::default();
        stack.push(Box::new(Probe::normal("under")));
        stack.push(Box::new(Probe::normal("over")));
        let rendered = stack.render(Rect::new(0, 0, 80, 24), &Theme::default());
        assert_eq!(rendered[0].title, "under");
        assert_eq!(rendered[1].title, "over");
    }
}
