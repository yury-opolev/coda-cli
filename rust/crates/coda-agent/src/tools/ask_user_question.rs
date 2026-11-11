//! Ask the user a structured multiple-choice question and receive an answer.
//!
//! When no interactive user is available (`ctx.user_question` is `None`),
//! the tool returns a graceful no-op message so the agent can proceed with
//! its best judgment rather than failing hard.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use coda_tool::AnswerOutcome;

pub struct AskUserQuestionTool;

#[async_trait]
impl Tool for AskUserQuestionTool {
    fn name(&self) -> &str {
        "ask_user_question"
    }

    fn description(&self) -> &str {
        "Ask the user a structured multiple-choice question and get their answer. \
         Use when you need clarification or a decision before proceeding. \
         Provide clear options. For multiSelect, the user may choose more than one option. \
         The user can also type their own answer instead and attach screenshots, which \
         follow the tool result."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "question": {
              "type": "string",
              "description": "The question to ask the user."
            },
            "options": {
              "type": "array",
              "items": {"type": "string"},
              "description": "The list of choices to present."
            },
            "multiSelect": {
              "type": "boolean",
              "description": "When true the user may select multiple options (default false)."
            }
          },
          "required": ["question", "options"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(
        &self,
        input: &serde_json::Value,
        ctx: &ToolContext,
        cancel: CancellationToken,
    ) -> ToolOutcome {
        let question = match input.get("question").and_then(|v| v.as_str()) {
            Some(q) if !q.is_empty() => q.to_owned(),
            _ => {
                return ToolResult::error("ask_user_question requires a 'question' string.")
            }
        };

        let options_val = match input.get("options").and_then(|v| v.as_array()) {
            Some(a) => a,
            None => {
                return ToolResult::error("ask_user_question requires an 'options' array.")
            }
        };

        let options: Vec<String> = options_val
            .iter()
            .filter_map(|v| v.as_str())
            .filter(|s| !s.trim().is_empty())
            .map(str::to_owned)
            .collect();

        if options.is_empty() {
            return ToolResult::error(
                "ask_user_question requires at least one non-empty option.",
            );
        }

        let multi_select = input
            .get("multiSelect")
            .and_then(|v| v.as_bool())
            .unwrap_or(false);

        match &ctx.user_question {
            None => ToolResult::ok(
                "No interactive user is available; proceed using your best judgment.",
            ),
            Some(uq) => {
                let reply = uq.ask_with_attachments(&question, &options, multi_select, cancel).await;
                match reply.outcome {
                    AnswerOutcome::Answered(answer) => {
                        let images = reply.images;
                        if images.is_empty() {
                            ToolResult::ok(format!("User answered: {answer}"))
                        } else {
                            let n = images.len();
                            let noun = if n == 1 { "image" } else { "images" };
                            ToolResult::ok(format!(
                                "User answered: {answer}\n\n(The user attached {n} {noun} to this \
                                 answer; they follow this tool result.)"
                            ))
                            .with_images(images)
                        }
                    }
                    // A deliberate set-aside, not a fault: the question was
                    // recorded as a blocker and the run should carry on elsewhere.
                    // Non-fatal, so the agent can route around it, and phrased so
                    // it cannot read as an answer.
                    AnswerOutcome::Parked { reason } => ToolResult::error(format!(
                        "This question was not settled and has been recorded as a blocker ({reason}). \
                         No option was chosen. Do not ask it again — continue with a different part \
                         of the task."
                    )),
                    // SECURITY: a fault is not an answer. Never the first option,
                    // never an empty success, never a retry. The run stops with a
                    // typed abort so no follow-up model request can be issued on
                    // the strength of a fabricated choice.
                    AnswerOutcome::NoAnswer(reason) => ToolResult::abort(
                        format!("question.noAnswer.{}", reason.as_str()),
                        format!(
                            "The question was not answered ({}). No answer was received, \
                             so the run stopped without choosing any option.",
                            reason.as_str()
                        ),
                    ),
                }
            }
        }
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use async_trait::async_trait;
    use crate::tool::{context::UserQuestion, ToolContext};
    use coda_tool::{NoAnswerReason, ToolControl};

    struct AlwaysChooseFirst;

    #[async_trait]
    impl UserQuestion for AlwaysChooseFirst {
        async fn ask(
            &self,
            _question: &str,
            options: &[String],
            _multi: bool,
            _cancel: CancellationToken,
        ) -> AnswerOutcome {
            match options.first() {
                Some(first) => AnswerOutcome::Answered(first.clone()),
                None => AnswerOutcome::NoAnswer(NoAnswerReason::Malformed),
            }
        }
    }

    /// A controller that is present but never produces an answer — the shape
    /// a dropped connection, a timeout or a cancellation all take.
    struct NeverAnswers(NoAnswerReason);

    #[async_trait]
    impl UserQuestion for NeverAnswers {
        async fn ask(
            &self,
            _question: &str,
            _options: &[String],
            _multi: bool,
            _cancel: CancellationToken,
        ) -> AnswerOutcome {
            AnswerOutcome::NoAnswer(self.0)
        }
    }

    fn ctx_headless() -> ToolContext {
        ToolContext::new("/")
    }

    fn ctx_with_question() -> ToolContext {
        ToolContext::new("/").with_user_question(Arc::new(AlwaysChooseFirst))
    }

    #[tokio::test]
    async fn missing_question_returns_error() {
        let result = AskUserQuestionTool
            .execute(
                &serde_json::json!({"options": ["yes"]}),
                &ctx_headless(),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn missing_options_returns_error() {
        let result = AskUserQuestionTool
            .execute(
                &serde_json::json!({"question": "Do it?"}),
                &ctx_headless(),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn empty_options_returns_error() {
        let result = AskUserQuestionTool
            .execute(
                &serde_json::json!({"question": "Do it?", "options": []}),
                &ctx_headless(),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn headless_returns_graceful_no_op() {
        let result = AskUserQuestionTool
            .execute(
                &serde_json::json!({"question": "Do it?", "options": ["yes", "no"]}),
                &ctx_headless(),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "unexpected error: {}", result.content);
        assert!(
            result.content.contains("proceed"),
            "unexpected: {}",
            result.content
        );
    }

    #[tokio::test]
    async fn routes_question_to_handler_and_returns_answer() {
        let ctx = ctx_with_question();
        let result = AskUserQuestionTool
            .execute(
                &serde_json::json!({
                    "question": "Pick one",
                    "options": ["alpha", "beta"]
                }),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "unexpected error: {}", result.content);
        assert!(result.content.contains("alpha"), "{}", result.content);
    }

    // ── Stage D: a fault is never an answer ──────────────────────────────

    /// SECURITY: for every no-answer reason the tool must abort with a typed
    /// control signal — never `"User answered: …"`, never the first option,
    /// never an empty success.
    #[tokio::test]
    async fn every_no_answer_reason_aborts_the_run_instead_of_inventing_an_answer() {
        for reason in [
            NoAnswerReason::Disconnected,
            NoAnswerReason::Cancelled,
            NoAnswerReason::Timeout,
            NoAnswerReason::Malformed,
            NoAnswerReason::Declined,
            NoAnswerReason::NoController,
        ] {
            let ctx = ToolContext::new("/").with_user_question(Arc::new(NeverAnswers(reason)));
            let result = AskUserQuestionTool
                .execute(
                    &serde_json::json!({
                        "question": "Delete the production database?",
                        "options": ["Delete", "Keep"]
                    }),
                    &ctx,
                    CancellationToken::new(),
                )
                .await;

            assert!(result.is_error, "{reason:?}: an unanswered question is a failure");
            assert_eq!(
                result.control,
                Some(ToolControl::AbortRun {
                    reason: format!("question.noAnswer.{}", reason.as_str())
                }),
                "{reason:?}: the abort must be a typed control signal, not a magic string"
            );
            assert!(
                !result.content.contains("User answered"),
                "{reason:?}: SECURITY — a fault must never read as an answer: {}",
                result.content
            );
            assert!(
                !result.content.contains("Delete") || result.content.contains("not answered"),
                "{reason:?}: the first option must never leak into the result: {}",
                result.content
            );
            assert!(
                result.content.contains(reason.as_str()),
                "{reason:?}: the result must name why there was no answer: {}",
                result.content
            );
        }
    }

    /// A genuine answer still succeeds and carries no control signal, so the
    /// abort path cannot swallow ordinary use.
    #[tokio::test]
    async fn a_real_answer_carries_no_control_signal() {
        let ctx = ctx_with_question();
        let result = AskUserQuestionTool
            .execute(
                &serde_json::json!({"question": "Pick one", "options": ["alpha", "beta"]}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.control.is_none());
        assert!(!result.is_error);
    }

    /// The headless no-op is a real (non-abort) success: an agent with no
    /// controller wired at all proceeds on its own judgment, exactly as before.
    #[tokio::test]
    async fn headless_no_op_is_not_an_abort() {
        let result = AskUserQuestionTool
            .execute(
                &serde_json::json!({"question": "Do it?", "options": ["yes", "no"]}),
                &ctx_headless(),
                CancellationToken::new(),
            )
            .await;
        assert!(result.control.is_none());
        assert!(!result.is_error);
    }

    struct AnswersWithImage;

    #[async_trait]
    impl UserQuestion for AnswersWithImage {
        async fn ask(&self, _: &str, _: &[String], _: bool, _: CancellationToken) -> AnswerOutcome {
            AnswerOutcome::Answered("plain".into())
        }

        async fn ask_with_attachments(
            &self,
            _: &str,
            _: &[String],
            _: bool,
            _: CancellationToken,
        ) -> coda_tool::QuestionReply {
            coda_tool::QuestionReply {
                outcome: AnswerOutcome::Answered("alpha".into()),
                images: vec![coda_tool::ImageAttachment {
                    media_type: "image/png".into(),
                    base64: "AAAA".into(),
                }],
            }
        }
    }

    #[tokio::test]
    async fn an_answer_with_images_returns_them_and_says_how_many() {
        let ctx = ToolContext::new("/").with_user_question(Arc::new(AnswersWithImage));
        let result = AskUserQuestionTool
            .execute(
                &serde_json::json!({"question": "Pick", "options": ["alpha"]}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("User answered: alpha"), "{}", result.content);
        assert!(result.content.contains("1 image"), "{}", result.content);
        assert_eq!(result.images.len(), 1);
    }

    #[tokio::test]
    async fn an_answer_without_images_attaches_none_and_does_not_mention_them() {
        let result = AskUserQuestionTool
            .execute(
                &serde_json::json!({"question": "Pick", "options": ["alpha"]}),
                &ctx_with_question(),
                CancellationToken::new(),
            )
            .await;
        assert!(result.images.is_empty());
        assert!(!result.content.contains("image"), "{}", result.content);
    }
}
