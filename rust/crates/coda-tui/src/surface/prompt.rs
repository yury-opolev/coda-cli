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
use crate::widgets::{Control, TextInput};
use coda_render::text;
use coda_render::theme::{Role, Theme};
use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
use ratatui::layout::Rect;
use ratatui::text::{Line, Span};

pub struct PromptSurface {
    prompt: PendingPrompt,
    /// Which option the arrows have moved to, for a question.
    highlighted: usize,
    /// Which options are ticked, for a multi-select question. Parallel to the
    /// option list.
    chosen: Vec<bool>,
    /// A typed answer, when the question allows one.
    free_text: Option<TextInput>,
    /// Whether the typed answer has the keyboard rather than the list.
    editing: bool,
}

impl PromptSurface {
    pub fn new(prompt: PendingPrompt) -> Self {
        let (count, free_text) = match &prompt {
            PendingPrompt::Question {
                options,
                allow_free_text,
                ..
            } => (
                options.len(),
                allow_free_text.then(|| TextInput::new("Or type an answer")),
            ),
            _ => (0, None),
        };
        Self {
            prompt,
            highlighted: 0,
            chosen: vec![false; count],
            free_text,
            editing: false,
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

    fn is_multi_select(&self) -> bool {
        matches!(
            &self.prompt,
            PendingPrompt::Question {
                multi_select: true,
                ..
            }
        )
    }

    /// The answer a question would send right now.
    ///
    /// A typed answer wins outright. Otherwise a multi-select joins its ticked
    /// options in their original order, and a single-select takes the
    /// highlighted one. This mirrors the C# `TuiUserQuestionPrompt`, whose
    /// contract is that multi-select returns the labels joined by ", ".
    fn answer(&self) -> Option<String> {
        if let Some(typed) = self.free_text.as_ref().filter(|f| !f.is_empty()) {
            return Some(typed.value());
        }
        if self.is_multi_select() {
            let picked: Vec<&str> = self
                .options()
                .iter()
                .enumerate()
                .filter(|(i, _)| self.chosen.get(*i).copied().unwrap_or(false))
                .map(|(_, o)| o.as_str())
                .collect();
            // No tick and no typing is not an answer; refuse rather than
            // sending an empty string that reads as a considered "none".
            return (!picked.is_empty()).then(|| picked.join(", "));
        }
        self.options().get(self.highlighted).cloned()
    }

    /// The answer for the option at `index`, if there is one.
    fn answer_at(&self, index: usize) -> Option<SurfaceAction> {
        self.options()
            .get(index)
            .map(|option| SurfaceAction::AnswerPrompt {
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
        // A chord is never an answer. Only an unmodified keystroke may approve
        // or refuse.
        //
        // Ctrl+Y is the application's copy shortcut, and the most natural
        // moment to press it is while reading a permission prompt in order to
        // scrutinise the command. Matching on the key code alone made that
        // chord grant approval — the exact opposite of the user's intent, with
        // nothing in the footer to warn them. Shift is allowed through because
        // it is how `Y` is typed.
        //
        // `TextInput` already applies this guard; the prompt, which is the one
        // control where the cost of getting it wrong is a command running
        // unasked, did not.
        let modified = key
            .modifiers
            .intersects(KeyModifiers::CONTROL | KeyModifiers::ALT | KeyModifiers::SUPER);
        if modified {
            return SurfaceOutcome::Handled;
        }

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
            PendingPrompt::Question { options, .. } => {
                // Tab moves between the option list and the typed answer, the
                // same split the form controls use.
                if key.code == KeyCode::Tab && self.free_text.is_some() {
                    self.editing = !self.editing;
                    return SurfaceOutcome::Handled;
                }

                if self.editing {
                    return match key.code {
                        KeyCode::Enter => match self.answer() {
                            Some(answer) => SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt {
                                allowed: true,
                                answer: Some(answer),
                            }),
                            None => SurfaceOutcome::Handled,
                        },
                        KeyCode::Esc => deny,
                        _ => {
                            if let Some(field) = self.free_text.as_mut() {
                                field.handle_key(key);
                            }
                            SurfaceOutcome::Handled
                        }
                    };
                }

                match key.code {
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
                    // Space ticks an option when several may be chosen. On a
                    // single-select question there is nothing to tick, so it
                    // does nothing rather than answering by accident.
                    KeyCode::Char(' ') if self.is_multi_select() => {
                        if let Some(slot) = self.chosen.get_mut(self.highlighted) {
                            *slot = !*slot;
                        }
                        SurfaceOutcome::Handled
                    }
                    KeyCode::Char(c) if c.is_ascii_digit() => {
                        // 1-9 pick directly. A digit past the end does nothing
                        // rather than answering something the user cannot see.
                        let Some(index) = c.to_digit(10).and_then(|d| (d as usize).checked_sub(1))
                        else {
                            return SurfaceOutcome::Handled;
                        };
                        if self.is_multi_select() {
                            if let Some(slot) = self.chosen.get_mut(index) {
                                *slot = !*slot;
                            }
                            return SurfaceOutcome::Handled;
                        }
                        match self.answer_at(index) {
                            Some(action) => SurfaceOutcome::Emit(action),
                            None => SurfaceOutcome::Handled,
                        }
                    }
                    // Answers the highlighted option, not the first. The footer
                    // has always advertised arrow selection; before this surface
                    // the arrows did nothing and Enter always took option one.
                    KeyCode::Enter => match self.answer() {
                        Some(answer) => SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt {
                            allowed: true,
                            answer: Some(answer),
                        }),
                        None => SurfaceOutcome::Handled,
                    },
                    KeyCode::Esc => deny,
                    _ => SurfaceOutcome::Handled,
                }
            }
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
            let multi = self.is_multi_select();
            for (index, option) in options.iter().enumerate() {
                let focused = index == self.highlighted && !self.editing;
                if focused {
                    highlight_row = Some(lines.len());
                }
                let marker = if focused {
                    glyphs::OPTION_MARKER
                } else {
                    glyphs::OPTION_BLANK
                };
                let style = if focused {
                    theme
                        .style(Role::CompletionSelectedText)
                        .bg(theme.fg(Role::CompletionSelectedBackground))
                } else {
                    theme.style(Role::CompletionNormal)
                };
                // A tick box only where several may be chosen, so a
                // single-select question does not look as though it takes more
                // than one answer.
                let label = if multi {
                    let ticked = self.chosen.get(index).copied().unwrap_or(false);
                    let box_ = if ticked {
                        glyphs::RADIO_ON
                    } else {
                        glyphs::RADIO_OFF
                    };
                    format!("{marker}{box_} {}. {option}", index + 1)
                } else {
                    format!("{marker}{}. {option}", index + 1)
                };
                lines.push(Line::from(Span::styled(
                    text::truncate(&text::sanitize(&label), width),
                    style,
                )));
            }
        }

        if let Some(field) = &self.free_text {
            lines.push(Line::default());
            let rendered = field.render(width as u16, self.editing, theme);
            if self.editing {
                highlight_row = Some(lines.len() + rendered.len().saturating_sub(1));
            }
            lines.extend(rendered);
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
    fn no_chord_can_approve_a_permission_gate() {
        // Ctrl+Y is the copy shortcut, and reading a prompt is exactly when a
        // user reaches for it. Matching on the key code alone made it approve.
        for modifier in [
            KeyModifiers::CONTROL,
            KeyModifiers::ALT,
            KeyModifiers::SUPER,
            KeyModifiers::CONTROL | KeyModifiers::SHIFT,
        ] {
            for code in [
                KeyCode::Char('y'),
                KeyCode::Char('Y'),
                KeyCode::Enter,
                KeyCode::Char('1'),
            ] {
                let outcome = permission().handle_key(KeyEvent::new(code, modifier));
                assert!(
                    matches!(outcome, SurfaceOutcome::Handled),
                    "{code:?} with {modifier:?} was treated as an answer"
                );
                let outcome = question().handle_key(KeyEvent::new(code, modifier));
                assert!(
                    matches!(outcome, SurfaceOutcome::Handled),
                    "{code:?} with {modifier:?} answered a question"
                );
            }
        }
    }

    #[test]
    fn shift_still_types_an_uppercase_answer() {
        // Y is typed with Shift, so the guard must not reject it.
        assert!(matches!(
            permission().handle_key(KeyEvent::new(KeyCode::Char('Y'), KeyModifiers::SHIFT)),
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { allowed: true, .. })
        ));
        assert!(matches!(
            permission().handle_key(KeyEvent::new(KeyCode::Char('N'), KeyModifiers::SHIFT)),
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

    fn multi() -> PromptSurface {
        PromptSurface::new(PendingPrompt::Question {
            question: "Which files?".into(),
            options: vec!["one".into(), "two".into(), "three".into()],
            multi_select: true,
            allow_free_text: false,
        })
    }

    fn free_text() -> PromptSurface {
        PromptSurface::new(PendingPrompt::Question {
            question: "Which approach?".into(),
            options: vec!["first".into(), "second".into()],
            multi_select: false,
            allow_free_text: true,
        })
    }

    #[test]
    fn multi_select_joins_its_ticked_options_in_order() {
        // Matches the C# contract: the labels joined by ", " in their original
        // order, not selection order.
        let mut q = multi();
        q.handle_key(key(KeyCode::Char('3')));
        q.handle_key(key(KeyCode::Char('1')));
        match q.handle_key(key(KeyCode::Enter)) {
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { answer, .. }) => {
                assert_eq!(answer.as_deref(), Some("one, three"));
            }
            _ => panic!("Enter did not answer with the ticked options"),
        }
    }

    #[test]
    fn space_ticks_and_unticks_in_a_multi_select() {
        let mut q = multi();
        q.handle_key(key(KeyCode::Char(' ')));
        assert!(q.chosen[0], "Space did not tick");
        q.handle_key(key(KeyCode::Char(' ')));
        assert!(!q.chosen[0], "Space did not untick");
    }

    #[test]
    fn space_does_nothing_on_a_single_select() {
        // Nothing to tick, so it must not answer by accident.
        let mut q = question();
        assert!(matches!(
            q.handle_key(key(KeyCode::Char(' '))),
            SurfaceOutcome::Handled
        ));
    }

    #[test]
    fn a_multi_select_with_nothing_ticked_refuses_to_answer() {
        // An empty join would reach the agent as a considered "none".
        let mut q = multi();
        assert!(matches!(
            q.handle_key(key(KeyCode::Enter)),
            SurfaceOutcome::Handled
        ));
    }

    #[test]
    fn a_digit_ticks_rather_than_answering_in_a_multi_select() {
        let mut q = multi();
        assert!(matches!(
            q.handle_key(key(KeyCode::Char('2'))),
            SurfaceOutcome::Handled
        ));
        assert!(q.chosen[1]);
    }

    #[test]
    fn a_typed_answer_wins_over_the_selection() {
        let mut q = free_text();
        q.handle_key(key(KeyCode::Tab));
        for c in "custom".chars() {
            q.handle_key(key(KeyCode::Char(c)));
        }
        match q.handle_key(key(KeyCode::Enter)) {
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { answer, .. }) => {
                assert_eq!(answer.as_deref(), Some("custom"));
            }
            _ => panic!("the typed answer was not used"),
        }
    }

    #[test]
    fn tab_only_offers_typing_where_the_question_allows_it() {
        let mut plain = question();
        plain.handle_key(key(KeyCode::Tab));
        assert!(!plain.editing, "typing was offered on a fixed-choice question");

        let mut typed = free_text();
        typed.handle_key(key(KeyCode::Tab));
        assert!(typed.editing);
    }

    #[test]
    fn escape_still_denies_while_typing() {
        let mut q = free_text();
        q.handle_key(key(KeyCode::Tab));
        assert!(matches!(
            q.handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { allowed: false, .. })
        ));
    }

    #[test]
    fn a_multi_select_shows_tick_boxes_and_a_single_select_does_not() {
        let text = |s: &PromptSurface| -> String {
            s.render(Rect::new(0, 0, 60, 20), &Theme::default())
                .iter()
                .flat_map(|l| l.spans.iter().map(|sp| sp.content.to_string()))
                .collect()
        };
        assert!(text(&multi()).contains(glyphs::RADIO_OFF));
        assert!(!text(&question()).contains(glyphs::RADIO_OFF));
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
