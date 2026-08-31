//! The engine prompt surface: permission, question and plan approval.
//!
//! `Exclusive`, because the engine is blocked until this is answered. Nothing
//! may open above it, and `Esc` denies rather than dismissing — closing
//! without answering would leave the turn waiting forever on a responder that
//! never receives a reply.
//!
//! The surface decides *what the answer is*; the application sends it. That
//! split is what lets every branch below be tested without an engine.

use super::{Modality, Surface, SurfaceAction, SurfaceOutcome};
use crate::render::glyphs;
use crate::state::PendingPrompt;
use coda_render::text;
use coda_render::theme::{Role, Theme};
use crossterm::event::{KeyCode, KeyEvent};
use ratatui::layout::Rect;
use ratatui::text::{Line, Span};

pub struct PromptSurface {
    prompt: PendingPrompt,
    /// Which option the arrows have moved to, for a question.
    highlighted: usize,
}

impl PromptSurface {
    pub fn new(prompt: PendingPrompt) -> Self {
        Self {
            prompt,
            highlighted: 0,
        }
    }

    pub fn prompt(&self) -> &PendingPrompt {
        &self.prompt
    }

    fn options(&self) -> &[String] {
        match &self.prompt {
            PendingPrompt::Question { options, .. } => options,
            _ => &[],
        }
    }

    /// The answer for the option at `index`, if there is one.
    fn answer_at(&self, index: usize) -> Option<SurfaceAction> {
        self.options().get(index).map(|option| SurfaceAction::AnswerPrompt {
            allowed: true,
            answer: Some(option.clone()),
        })
    }
}

impl Surface for PromptSurface {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn title(&self) -> String {
        match &self.prompt {
            PendingPrompt::Permission { .. } => "Permission required".into(),
            PendingPrompt::Question { .. } => "Question".into(),
            PendingPrompt::PlanApproval { .. } => "Approve plan?".into(),
        }
    }

    fn hints(&self) -> String {
        match &self.prompt {
            PendingPrompt::Question { options, .. } if !options.is_empty() => format!(
                "{}: choose    1-9: pick    Enter: answer    Esc: cancel",
                glyphs::ARROWS_VERTICAL
            ),
            PendingPrompt::Question { .. } => "Enter: answer    Esc: cancel".into(),
            PendingPrompt::PlanApproval { .. } => {
                "y: approve    n: reject    Esc: reject".into()
            }
            PendingPrompt::Permission { .. } => "y: allow    n: deny    Esc: deny".into(),
        }
    }

    fn modality(&self) -> Modality {
        Modality::Exclusive
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        let deny = SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt {
            allowed: false,
            answer: None,
        });

        match &self.prompt {
            PendingPrompt::Permission { .. } | PendingPrompt::PlanApproval { .. } => {
                match key.code {
                    KeyCode::Char('y') | KeyCode::Char('Y') | KeyCode::Enter => {
                        SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt {
                            allowed: true,
                            answer: None,
                        })
                    }
                    KeyCode::Char('n') | KeyCode::Char('N') | KeyCode::Esc => deny,
                    // Everything else is swallowed rather than ignored.
                    // Ignored would let the stack's Esc handling pop a prompt
                    // the engine is still waiting on.
                    _ => SurfaceOutcome::Handled,
                }
            }
            PendingPrompt::Question { options, .. } => match key.code {
                KeyCode::Up => {
                    self.highlighted = self.highlighted.saturating_sub(1);
                    SurfaceOutcome::Handled
                }
                KeyCode::Down => {
                    if self.highlighted + 1 < options.len() {
                        self.highlighted += 1;
                    }
                    SurfaceOutcome::Handled
                }
                KeyCode::Char(c) if c.is_ascii_digit() => {
                    // 1-9 pick directly. A digit past the end does nothing
                    // rather than answering something the user cannot see.
                    match c.to_digit(10).and_then(|d| (d as usize).checked_sub(1)) {
                        Some(index) => match self.answer_at(index) {
                            Some(action) => SurfaceOutcome::Emit(action),
                            None => SurfaceOutcome::Handled,
                        },
                        None => SurfaceOutcome::Handled,
                    }
                }
                // Answers the highlighted option, not the first. The footer
                // has always advertised arrow selection; before this surface
                // the arrows did nothing and Enter always took option one.
                KeyCode::Enter => match self.answer_at(self.highlighted) {
                    Some(action) => SurfaceOutcome::Emit(action),
                    None => SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt {
                        allowed: true,
                        answer: None,
                    }),
                },
                KeyCode::Esc => deny,
                _ => SurfaceOutcome::Handled,
            },
        }
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        let width = area.width.max(1) as usize;
        let mut lines: Vec<Line<'static>> = Vec::new();

        let body = match &self.prompt {
            PendingPrompt::Permission { tool, preview } => format!("{tool}\n\n{preview}"),
            PendingPrompt::PlanApproval { plan } => plan.clone(),
            PendingPrompt::Question { question, .. } => question.clone(),
        };

        let prose = theme.style(Role::PromptText);
        for line in body.lines() {
            for row in text::wrap(&text::sanitize(line), width) {
                lines.push(Line::from(Span::styled(row, prose)));
            }
        }

        let options = self.options();
        let mut highlight_row = None;
        if !options.is_empty() {
            lines.push(Line::default());
            for (index, option) in options.iter().enumerate() {
                let chosen = index == self.highlighted;
                if chosen {
                    highlight_row = Some(lines.len());
                }
                let marker = if chosen {
                    glyphs::OPTION_MARKER
                } else {
                    glyphs::OPTION_BLANK
                };
                let style = if chosen {
                    theme
                        .style(Role::CompletionSelectedText)
                        .bg(theme.fg(Role::CompletionSelectedBackground))
                } else {
                    theme.style(Role::CompletionNormal)
                };
                let label = format!("{marker}{}. {option}", index + 1);
                lines.push(Line::from(Span::styled(
                    text::truncate(&text::sanitize(&label), width),
                    style,
                )));
            }
        }

        // Scrolled to keep the highlighted option visible: a long plan or a
        // long option list must not hide the thing being answered. The row is
        // recorded while building rather than derived afterwards, so it cannot
        // drift from the layout it describes.
        let height = area.height as usize;
        if lines.len() > height && height > 0 {
            let anchor = highlight_row.unwrap_or(0);
            let scroll = (anchor + 1).saturating_sub(height).min(lines.len() - height);
            lines.drain(..scroll);
            lines.truncate(height);
        } else {
            lines.truncate(height);
        }
        lines
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::KeyModifiers;

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn permission() -> PromptSurface {
        PromptSurface::new(PendingPrompt::Permission {
            tool: "write_file".into(),
            preview: "src/main.rs".into(),
        })
    }

    fn question() -> PromptSurface {
        PromptSurface::new(PendingPrompt::Question {
            question: "Which approach?".into(),
            options: vec!["first".into(), "second".into(), "third".into()],
            multi_select: false,
            allow_free_text: false,
        })
    }

    #[test]
    fn every_prompt_is_exclusive() {
        // The single property that stops a browser opening over a permission
        // gate and stops Esc answering it.
        assert_eq!(permission().modality(), Modality::Exclusive);
        assert_eq!(question().modality(), Modality::Exclusive);
        assert_eq!(
            PromptSurface::new(PendingPrompt::PlanApproval { plan: "p".into() }).modality(),
            Modality::Exclusive
        );
    }

    #[test]
    fn y_allows_and_n_denies() {
        assert!(matches!(
            permission().handle_key(key(KeyCode::Char('y'))),
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { allowed: true, .. })
        ));
        assert!(matches!(
            permission().handle_key(key(KeyCode::Char('n'))),
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { allowed: false, .. })
        ));
    }

    #[test]
    fn uppercase_answers_too() {
        assert!(matches!(
            permission().handle_key(key(KeyCode::Char('Y'))),
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { allowed: true, .. })
        ));
        assert!(matches!(
            permission().handle_key(key(KeyCode::Char('N'))),
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { allowed: false, .. })
        ));
    }

    #[test]
    fn escape_denies_rather_than_closing() {
        // Closing without answering would leave the engine waiting forever.
        assert!(matches!(
            permission().handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { allowed: false, .. })
        ));
    }

    #[test]
    fn an_unrelated_key_is_swallowed_not_ignored() {
        // Ignored would let the stack's Esc handling pop the prompt.
        assert!(matches!(
            permission().handle_key(key(KeyCode::Char('z'))),
            SurfaceOutcome::Handled
        ));
        assert!(matches!(
            question().handle_key(key(KeyCode::Char('z'))),
            SurfaceOutcome::Handled
        ));
    }

    #[test]
    fn arrows_move_the_highlight_and_enter_answers_it() {
        // The footer has always advertised arrow selection. Before this
        // surface the arrows did nothing and Enter always took option one.
        let mut q = question();
        q.handle_key(key(KeyCode::Down));
        match q.handle_key(key(KeyCode::Enter)) {
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { answer, .. }) => {
                assert_eq!(answer.as_deref(), Some("second"));
            }
            _ => panic!("Enter did not answer the highlighted option"),
        }
    }

    #[test]
    fn the_highlight_stops_at_both_ends() {
        let mut q = question();
        q.handle_key(key(KeyCode::Up));
        assert_eq!(q.highlighted, 0);
        for _ in 0..9 {
            q.handle_key(key(KeyCode::Down));
        }
        assert_eq!(q.highlighted, 2);
    }

    #[test]
    fn digits_pick_an_option_directly() {
        match question().handle_key(key(KeyCode::Char('3'))) {
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { answer, .. }) => {
                assert_eq!(answer.as_deref(), Some("third"));
            }
            _ => panic!("a digit did not pick its option"),
        }
    }

    #[test]
    fn a_digit_past_the_end_does_nothing() {
        // Answering something the user cannot see is worse than ignoring.
        assert!(matches!(
            question().handle_key(key(KeyCode::Char('9'))),
            SurfaceOutcome::Handled
        ));
        assert!(matches!(
            question().handle_key(key(KeyCode::Char('0'))),
            SurfaceOutcome::Handled
        ));
    }

    #[test]
    fn a_question_renders_its_options_and_marks_the_highlight() {
        let mut q = question();
        q.handle_key(key(KeyCode::Down));
        let text: String = q
            .render(Rect::new(0, 0, 60, 20), &Theme::default())
            .iter()
            .flat_map(|l| l.spans.iter().map(|s| s.content.to_string()))
            .collect();
        assert!(text.contains("Which approach?"));
        assert!(text.contains("second"));
        assert!(text.contains(glyphs::OPTION_MARKER.trim_end()));
    }

    #[test]
    fn it_never_renders_more_lines_than_the_area_allows() {
        let long = PromptSurface::new(PendingPrompt::PlanApproval {
            plan: (0..80).map(|i| format!("step {i}")).collect::<Vec<_>>().join("\n"),
        });
        for height in [1, 4, 10, 40] {
            let area = Rect::new(0, 0, 50, height);
            assert!(
                long.render(area, &Theme::default()).len() <= height as usize,
                "overflowed a {height}-row area"
            );
        }
    }

    #[test]
    fn a_long_option_list_keeps_the_highlight_visible() {
        let mut q = PromptSurface::new(PendingPrompt::Question {
            question: "Pick".into(),
            options: (0..40).map(|i| format!("option{i}")).collect(),
            multi_select: false,
            allow_free_text: false,
        });
        for _ in 0..39 {
            q.handle_key(key(KeyCode::Down));
        }
        let text: String = q
            .render(Rect::new(0, 0, 40, 8), &Theme::default())
            .iter()
            .flat_map(|l| l.spans.iter().map(|s| s.content.to_string()))
            .collect();
        assert!(
            text.contains("option39"),
            "the highlighted option scrolled out of sight: {text:?}"
        );
    }
}
