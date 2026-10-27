//! The `/btw` side panel: a side question and its answer.
//!
//! Shown beside the transcript rather than in it, because the answer is not
//! part of the conversation and is never sent to the agent. `p` promotes the
//! question to an ordinary message for the agent (steering when a turn is
//! running), for when the user decides it should act on it after all.
//!
//! Built from the shared widget toolkit: a [`Form`] of [`StaticText`]s, which
//! already wraps and styles text; this surface adds only scrolling and keys.

use coda_render::theme::{Role, Theme};
use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
use ratatui::layout::Rect;
use ratatui::text::Line;

use super::{Placement, Side, Surface, SurfaceAction, SurfaceOutcome};
use crate::widgets::{Form, StaticText};

/// A side question, waiting for or showing its answer.
pub struct AsideSurface {
    id: u64,
    question: String,
    answer: Option<String>,
    failed: bool,
    scroll: u16,
}

impl AsideSurface {
    /// A question that has been asked and not yet answered.
    pub fn pending(id: u64, question: impl Into<String>) -> Self {
        Self { id, question: question.into(), answer: None, failed: false, scroll: 0 }
    }

    /// The same question with its answer (or why there is none).
    pub fn answered(id: u64, question: impl Into<String>, answer: impl Into<String>, failed: bool) -> Self {
        Self { id, question: question.into(), answer: Some(answer.into()), failed, scroll: 0 }
    }

    pub fn id(&self) -> u64 {
        self.id
    }

    pub fn question(&self) -> &str {
        &self.question
    }

    pub fn answer(&self) -> Option<&str> {
        self.answer.as_deref()
    }

    fn form(&self) -> Form {
        let answer = match &self.answer {
            Some(text) => StaticText::new(text.clone())
                .with_role(if self.failed { Role::Warning } else { Role::PromptText }),
            None => StaticText::new("Thinking… the agent keeps working meanwhile."),
        };
        Form::new(vec![
            Box::new(StaticText::new(format!("btw: {}", self.question)).with_role(Role::PromptAccent)),
            Box::new(answer),
            Box::new(StaticText::new(
                "A side answer: it is not added to the conversation and the agent never sees it.",
            )),
        ])
    }
}

impl Surface for AsideSurface {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn title(&self) -> String {
        "Side question".to_owned()
    }

    fn hints(&self) -> String {
        "↑/↓ scroll · p: send to the agent as a message · Esc: close".to_owned()
    }

    fn placement(&self) -> Placement {
        Placement::Split { side: Side::Right, width_pct: 45 }
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        if key.modifiers.intersects(KeyModifiers::CONTROL | KeyModifiers::ALT) {
            return SurfaceOutcome::Ignored;
        }
        match key.code {
            KeyCode::Esc | KeyCode::Char('q') => SurfaceOutcome::Close,
            KeyCode::Char('p') => SurfaceOutcome::Emit(SurfaceAction::PromoteAside(self.question.clone())),
            KeyCode::Up => {
                self.scroll = self.scroll.saturating_sub(1);
                SurfaceOutcome::Handled
            }
            KeyCode::Down => {
                self.scroll = self.scroll.saturating_add(1);
                SurfaceOutcome::Handled
            }
            KeyCode::PageUp => {
                self.scroll = self.scroll.saturating_sub(10);
                SurfaceOutcome::Handled
            }
            KeyCode::PageDown => {
                self.scroll = self.scroll.saturating_add(10);
                SurfaceOutcome::Handled
            }
            _ => SurfaceOutcome::Handled,
        }
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        let mut lines = self.form().render(area.width, theme);
        let max_scroll = lines.len().saturating_sub(area.height as usize);
        let scroll = (self.scroll as usize).min(max_scroll);
        lines.drain(..scroll);
        lines.truncate(area.height as usize);
        lines
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn text(surface: &AsideSurface, height: u16) -> String {
        surface
            .render(Rect::new(0, 0, 120, height), &Theme::default())
            .iter()
            .map(|l| l.spans.iter().map(|s| s.content.to_string()).collect::<String>())
            .collect::<Vec<_>>()
            .join("\n")
    }

    #[test]
    fn it_shows_the_question_then_the_answer_and_says_it_is_not_in_the_conversation() {
        let pending = AsideSurface::pending(1, "are the reviews running?");
        let shown = text(&pending, 40);
        assert!(shown.contains("btw: are the reviews running?"), "{shown}");
        assert!(shown.contains("Thinking"), "{shown}");

        let answered = AsideSurface::answered(1, "are the reviews running?", "Yes, task-0007.", false);
        let shown = text(&answered, 40);
        assert!(shown.contains("Yes, task-0007."), "{shown}");
        assert!(shown.contains("not added to the conversation"), "{shown}");
    }

    #[test]
    fn p_promotes_the_question_and_esc_closes() {
        let mut surface = AsideSurface::answered(2, "use a dry run", "ok", false);
        match surface.handle_key(key(KeyCode::Char('p'))) {
            SurfaceOutcome::Emit(SurfaceAction::PromoteAside(text)) => assert_eq!(text, "use a dry run"),
            _ => panic!("p must promote the question"),
        }
        assert!(matches!(surface.handle_key(key(KeyCode::Esc)), SurfaceOutcome::Close));
        let ctrl_c = KeyEvent::new(KeyCode::Char('c'), KeyModifiers::CONTROL);
        assert!(matches!(surface.handle_key(ctrl_c), SurfaceOutcome::Ignored), "global chords still work");
    }

    #[test]
    fn a_long_answer_scrolls_within_the_area() {
        let long = (0..60).map(|i| format!("line {i}")).collect::<Vec<_>>().join("\n\n");
        let mut surface = AsideSurface::answered(3, "q", long, false);
        assert!(!text(&surface, 10).contains("line 59"));
        for _ in 0..20 {
            surface.handle_key(key(KeyCode::PageDown));
        }
        let bottom = text(&surface, 10);
        assert!(bottom.contains("not added to the conversation"), "{bottom}");
        assert!(surface.render(Rect::new(0, 0, 50, 10), &Theme::default()).len() <= 10);
    }
}
