//! The engine prompt surface: permission, question and plan approval.
//!
//! `Exclusive`, because the engine is blocked until this is answered. Nothing
//! may open above it, and `Esc` denies rather than dismissing — closing
//! without answering would leave the turn waiting forever on a responder that
//! never receives a reply.
//!
//! The surface decides *what the answer is*; the application sends it. That
//! split is what lets every branch below be tested without an engine. A
//! question's body lives in [`QuestionView`], which this surface owns so the
//! application keeps retiring every prompt the same way.

use super::question::{QuestionOutcome, QuestionView};
use super::{Modality, Surface, SurfaceAction, SurfaceOutcome};
use crate::state::PendingPrompt;
use coda_proto::messages::WireImage;
use coda_render::text;
use coda_render::theme::{Role, Theme};
use crossterm::event::{KeyCode, KeyEvent, KeyModifiers, MouseEvent};
use ratatui::layout::Rect;
use ratatui::text::{Line, Span};

pub struct PromptSurface {
    prompt: PendingPrompt,
    /// The question's state, for a question prompt.
    question: Option<QuestionView>,
}

impl PromptSurface {
    pub fn new(prompt: PendingPrompt) -> Self {
        let question = match &prompt {
            PendingPrompt::Question {
                question,
                options,
                multi_select,
                allow_free_text,
                accepts_images,
            } => Some(QuestionView::new(
                question.clone(),
                options.clone(),
                *multi_select,
                *allow_free_text,
                *accepts_images,
            )),
            _ => None,
        };
        Self { prompt, question }
    }

    pub fn prompt(&self) -> &PendingPrompt {
        &self.prompt
    }

    /// The open question, so the application can attach a pasted image or
    /// report a clipboard error.
    pub fn question_view_mut(&mut self) -> Option<&mut QuestionView> {
        self.question.as_mut()
    }

    /// The images to send with the answer; empty for anything but a question.
    pub fn answer_images(&self) -> Vec<WireImage> {
        self.question
            .as_ref()
            .map(QuestionView::images)
            .unwrap_or_default()
    }

    fn deny() -> SurfaceOutcome {
        SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt {
            allowed: false,
            answer: None,
        })
    }

    fn question_outcome(outcome: QuestionOutcome) -> SurfaceOutcome {
        match outcome {
            QuestionOutcome::Handled => SurfaceOutcome::Handled,
            QuestionOutcome::Submit { answer } => {
                SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt {
                    allowed: true,
                    answer: Some(answer),
                })
            }
            QuestionOutcome::Cancel => Self::deny(),
            QuestionOutcome::RequestPaste => SurfaceOutcome::Emit(SurfaceAction::PasteIntoPrompt),
        }
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
        if let Some(question) = &self.question {
            return question.hints();
        }
        match &self.prompt {
            PendingPrompt::PlanApproval { .. } => {
                "y: approve    n: reject    Esc: reject".into()
            }
            _ => "y: allow    n: deny    Esc: deny".into(),
        }
    }

    fn placement(&self) -> super::Placement {
        // A question can carry long options and a typed explanation, so it
        // gets a wide box. A permission prompt has short content: a tool name
        // and a path. A plan-approval prompt has a plan that can be long but
        // scrolls. FitContent keeps the modal compact for short prompts and
        // scrolls longer ones without ever allocating 70% of the terminal for
        // three lines.
        let preferred_width = if self.question.is_some() { 110 } else { 60 };
        super::Placement::FitContent { preferred_width }
    }

    fn modality(&self) -> Modality {
        Modality::Exclusive
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        // The question applies its own chord rule, because its answer field
        // takes the composer's newline chords.
        if let Some(question) = self.question.as_mut() {
            return Self::question_outcome(question.handle_key(key));
        }

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

        match key.code {
            KeyCode::Char('y') | KeyCode::Char('Y') | KeyCode::Enter => {
                SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt {
                    allowed: true,
                    answer: None,
                })
            }
            KeyCode::Char('n') | KeyCode::Char('N') | KeyCode::Esc => Self::deny(),
            // Everything else is swallowed rather than ignored. Ignored would
            // let the stack's Esc handling pop a prompt the engine is still
            // waiting on.
            _ => SurfaceOutcome::Handled,
        }
    }

    fn handle_mouse(&mut self, event: MouseEvent, content: Rect) -> SurfaceOutcome {
        match self.question.as_mut() {
            Some(question) => Self::question_outcome(question.handle_mouse(event, content)),
            None => SurfaceOutcome::Ignored,
        }
    }

    fn handle_paste(&mut self, pasted: &str) -> bool {
        match self.question.as_mut() {
            Some(question) => {
                question.paste_text(pasted);
                true
            }
            None => false,
        }
    }

    fn question_mut(&mut self) -> Option<&mut QuestionView> {
        self.question.as_mut()
    }

    fn desired_size(&self, max_width: u16, theme: &Theme) -> (u16, u16) {
        // Measured without rendering: rendering a question records its scroll
        // position, and a probe at an unlimited height would reset it.
        match &self.question {
            Some(question) => {
                let height = question.content_height(max_width, theme);
                (max_width, height.min(usize::from(u16::MAX / 2)) as u16)
            }
            None => {
                let probe = Rect::new(0, 0, max_width.max(1), u16::MAX / 2);
                (max_width, self.render(probe, theme).len() as u16)
            }
        }
    }

    fn cursor(&self, area: Rect, theme: &Theme) -> Option<(u16, u16)> {
        self.question.as_ref()?.cursor(area, theme)
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        if let Some(question) = &self.question {
            return question.render(area, theme);
        }

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

        lines.truncate(area.height as usize);
        lines
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::render::glyphs;

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
            accepts_images: false,
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

    fn answer_of(outcome: SurfaceOutcome) -> Option<String> {
        match outcome {
            SurfaceOutcome::Emit(SurfaceAction::AnswerPrompt { answer, .. }) => answer,
            _ => None,
        }
    }

    #[test]
    fn the_highlight_stops_at_both_ends() {
        let mut q = question();
        q.handle_key(key(KeyCode::Up));
        assert_eq!(answer_of(q.handle_key(key(KeyCode::Enter))).as_deref(), Some("first"));
        let mut q = question();
        for _ in 0..9 {
            q.handle_key(key(KeyCode::Down));
        }
        assert_eq!(answer_of(q.handle_key(key(KeyCode::Enter))).as_deref(), Some("third"));
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
            accepts_images: false,
        })
    }

    fn free_text() -> PromptSurface {
        PromptSurface::new(PendingPrompt::Question {
            question: "Which approach?".into(),
            options: vec!["first".into(), "second".into()],
            multi_select: false,
            allow_free_text: true,
            accepts_images: false,
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
        q.handle_key(key(KeyCode::Char(' ')));
        assert!(
            matches!(q.handle_key(key(KeyCode::Enter)), SurfaceOutcome::Handled),
            "Space did not untick"
        );
        q.handle_key(key(KeyCode::Char(' ')));
        assert_eq!(answer_of(q.handle_key(key(KeyCode::Enter))).as_deref(), Some("one"));
    }

    #[test]
    fn space_selects_without_answering_on_a_single_select() {
        // Space marks the radio; only Enter or a digit sends it.
        let mut q = question();
        assert!(matches!(
            q.handle_key(key(KeyCode::Char(' '))),
            SurfaceOutcome::Handled
        ));
        let text: String = q
            .render(Rect::new(0, 0, 60, 20), &Theme::default())
            .iter()
            .flat_map(|l| l.spans.iter().map(|s| s.content.to_string()))
            .collect();
        assert!(text.contains(glyphs::RADIO_ON));
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
        assert_eq!(answer_of(q.handle_key(key(KeyCode::Enter))).as_deref(), Some("two"));
    }

    #[test]
    fn a_typed_answer_with_no_option_chosen_is_sent_as_typed() {
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
        assert!(
            plain.hints().starts_with(glyphs::ARROWS_VERTICAL),
            "typing was offered on a fixed-choice question: {}",
            plain.hints()
        );

        let mut typed = free_text();
        typed.handle_key(key(KeyCode::Tab));
        assert!(typed.hints().starts_with("Enter submit"), "{}", typed.hints());
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
        assert!(text(&multi()).contains(glyphs::CHECK_OFF));
        assert!(!text(&multi()).contains(glyphs::RADIO_OFF));
        assert!(text(&question()).contains(glyphs::RADIO_OFF));
        assert!(!text(&question()).contains(glyphs::CHECK_OFF));
    }

    #[test]
    fn a_question_gets_a_wide_modal_and_other_prompts_stay_compact() {
        assert_eq!(
            question().placement(),
            super::super::Placement::FitContent { preferred_width: 110 }
        );
        assert_eq!(
            permission().placement(),
            super::super::Placement::FitContent { preferred_width: 60 }
        );
    }

    #[test]
    fn a_paste_chord_asks_the_application_for_the_clipboard() {
        let mut q = free_text();
        assert!(matches!(
            q.handle_key(KeyEvent::new(KeyCode::Char('v'), KeyModifiers::CONTROL)),
            SurfaceOutcome::Emit(SurfaceAction::PasteIntoPrompt)
        ));
        // A permission gate has nothing to paste into.
        assert!(matches!(
            permission().handle_key(KeyEvent::new(KeyCode::Char('v'), KeyModifiers::CONTROL)),
            SurfaceOutcome::Handled
        ));
    }

    #[test]
    fn a_bracketed_paste_lands_in_the_question_and_nowhere_else() {
        let mut q = free_text();
        assert!(q.handle_paste("pasted"));
        assert_eq!(answer_of(q.handle_key(key(KeyCode::Enter))).as_deref(), Some("pasted"));
        assert!(!permission().handle_paste("pasted"));
    }

    #[test]
    fn attached_images_travel_with_the_answer() {
        let mut q = PromptSurface::new(PendingPrompt::Question {
            question: "Which?".into(),
            options: vec!["a".into()],
            multi_select: false,
            allow_free_text: true,
            accepts_images: true,
        });
        q.question_view_mut()
            .expect("a question view")
            .attach_image("image/png", "AAAA".into())
            .expect("images are accepted");
        assert_eq!(
            q.answer_images(),
            vec![WireImage {
                media_type: "image/png".into(),
                base64: "AAAA".into()
            }]
        );
        assert!(permission().answer_images().is_empty());
    }

    #[test]
    fn measuring_a_question_does_not_move_its_scroll_position() {
        let mut q = PromptSurface::new(PendingPrompt::Question {
            question: "Pick".into(),
            options: (0..40).map(|i| format!("option{i}")).collect(),
            multi_select: false,
            allow_free_text: false,
            accepts_images: false,
        });
        for _ in 0..39 {
            q.handle_key(key(KeyCode::Down));
        }
        let area = Rect::new(0, 0, 40, 8);
        let theme = Theme::default();
        let before = q.render(area, &theme);
        let (_, height) = q.desired_size(40, &theme);
        assert!(height as usize > 40);
        assert_eq!(q.render(area, &theme), before);
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
            accepts_images: false,
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
