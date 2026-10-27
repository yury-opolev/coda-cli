//! `/btw <question>` — side questions (non-blocking runtime, Phase 3).
//!
//! The engine answers with one separate, tool-less model request that sees
//! the conversation and the live task table (`session/askAside`). The
//! request runs on its own task, so neither the event loop nor a running
//! turn ever waits for it; the answer comes back through a channel the loop
//! selects on and fills the side panel opened when the question was asked.
//! Closed meanwhile, the answer is printed as a clearly marked side note
//! instead, so it is never lost. Nothing here enters the conversation.

use std::time::Duration;

use coda_proto::messages::method;
use coda_proto::responses::AskAsideResult;
use tokio::sync::mpsc;

use super::App;
use crate::surface::aside::AsideSurface;
use crate::transcript::NoticeLevel;

/// How long the client waits for a side answer. Longer than the engine's own
/// bound, so the engine's typed refusal normally arrives first.
const ASIDE_CLIENT_TIMEOUT: Duration = Duration::from_secs(150);

/// One answered (or failed) side question.
pub(crate) struct AsideReply {
    id: u64,
    question: String,
    outcome: Result<AskAsideResult, String>,
}

/// The channel side answers come back on.
pub(crate) struct Asides {
    tx: mpsc::UnboundedSender<AsideReply>,
    rx: Option<mpsc::UnboundedReceiver<AsideReply>>,
    next_id: u64,
}

impl Default for Asides {
    fn default() -> Self {
        let (tx, rx) = mpsc::unbounded_channel();
        Self { tx, rx: Some(rx), next_id: 0 }
    }
}

impl Asides {
    /// The receiving end, for the event loop to select on. Taken once.
    pub(crate) fn take_replies(&mut self) -> mpsc::UnboundedReceiver<AsideReply> {
        self.rx.take().unwrap_or_else(|| mpsc::unbounded_channel().1)
    }
}

/// What the user is shown for a reply, and whether it is a failure.
fn reply_text(outcome: &Result<AskAsideResult, String>) -> (String, bool) {
    match outcome {
        Ok(AskAsideResult { ok: true, answer, .. }) => {
            (answer.clone().unwrap_or_default().trim().to_owned(), false)
        }
        Ok(AskAsideResult { reason, error, .. }) => {
            let text = match reason.as_deref() {
                Some("noProvider") => {
                    "No model is connected yet, so the question was not asked. Run /provider or /login."
                        .to_owned()
                }
                Some("emptyQuestion") => "Type a question after /btw.".to_owned(),
                _ => format!(
                    "The side question could not be answered: {}.",
                    error.as_deref().unwrap_or("the engine refused it")
                ),
            };
            (text, true)
        }
        Err(error) => (format!("The side question could not be answered: {error}."), true),
    }
}

impl App {
    /// `/btw <question>`: ask without interrupting the agent.
    pub(super) fn ask_aside(&mut self, args: &str) {
        let question = args.trim().to_owned();
        if question.is_empty() {
            self.notice("Usage: /btw <question> — asks without interrupting the agent.", NoticeLevel::Warning);
            return;
        }
        if !self.require_engine("/btw") {
            return;
        }
        let params = serde_json::json!({ "question": question });
        let receiver = match self.connection.send_request(method::ASK_ASIDE, Some(params)) {
            Ok(receiver) => receiver,
            Err(error) => {
                self.notice(format!("Could not ask the side question: {error}"), NoticeLevel::Error);
                return;
            }
        };
        self.asides.next_id += 1;
        let id = self.asides.next_id;
        let tx = self.asides.tx.clone();
        let asked = question.clone();
        tokio::spawn(async move {
            let outcome = match tokio::time::timeout(ASIDE_CLIENT_TIMEOUT, receiver).await {
                Ok(Ok(Ok(value))) => {
                    serde_json::from_value::<AskAsideResult>(value).map_err(|e| e.to_string())
                }
                Ok(Ok(Err(error))) => Err(error.message),
                Ok(Err(_)) => Err("the engine connection closed".to_owned()),
                Err(_) => Err("the engine did not answer in time".to_owned()),
            };
            let _ = tx.send(AsideReply { id, question: asked, outcome });
        });
        self.surfaces.push(Box::new(AsideSurface::pending(id, question)));
        self.dirty = true;
    }

    /// A side answer arrived: fill the panel still waiting for it, or print
    /// it as a marked side note when that panel was closed.
    pub(super) fn on_aside_reply(&mut self, reply: AsideReply) {
        let (text, failed) = reply_text(&reply.outcome);
        let waiting = self
            .surfaces
            .top()
            .and_then(|top| top.as_any().downcast_ref::<AsideSurface>())
            .is_some_and(|panel| panel.id() == reply.id);
        if waiting {
            self.surfaces.replace_top(Box::new(AsideSurface::answered(reply.id, reply.question, text, failed)));
        } else if failed {
            self.notice(format!("btw: {}: {text}", reply.question), NoticeLevel::Warning);
        } else {
            self.output(format!(
                "btw: {}\n{text}\n(side answer — not part of the conversation)",
                reply.question
            ));
        }
        self.dirty = true;
    }

    /// `p` in the side panel: the question becomes an ordinary message for
    /// the agent — steering while a turn runs, a new turn otherwise.
    pub(super) async fn promote_aside(&mut self, question: String) {
        self.surfaces.pop();
        if self.state.is_busy() {
            self.steer(question).await;
        } else {
            self.submit_programmatic(question).await;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::app::serve::tests::{app_with, notices, outputs};
    use crate::surface::Surface;
    use serde_json::{json, Value};

    #[test]
    fn replies_read_as_answers_or_as_plain_reasons() {
        let ok = Ok(AskAsideResult { ok: true, answer: Some(" yes \n".into()), ..Default::default() });
        assert_eq!(reply_text(&ok), ("yes".to_owned(), false));
        let none = Ok(AskAsideResult { ok: false, reason: Some("noProvider".into()), ..Default::default() });
        assert!(reply_text(&none).0.contains("No model is connected") && reply_text(&none).1);
        let failed = Ok(AskAsideResult {
            ok: false,
            reason: Some("modelError".into()),
            error: Some("the model request failed (timeout)".into()),
            ..Default::default()
        });
        assert!(reply_text(&failed).0.contains("the model request failed (timeout)"));
        assert!(reply_text(&Err("closed".into())).1);
    }

    fn panel(app: &App) -> Option<(u64, Option<String>)> {
        app.surfaces
            .top()
            .and_then(|s| s.as_any().downcast_ref::<AsideSurface>())
            .map(|p| (p.id(), p.answer().map(str::to_owned)))
    }

    #[tokio::test]
    async fn btw_asks_the_engine_and_fills_the_panel_with_the_answer() {
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |method, params: &Value| {
            match method {
                "session/askAside" => json!({ "ok": true, "answer": format!("re: {}", params["question"].as_str().unwrap()) }),
                _ => json!({}),
            }
        });
        let mut replies = harness.app.asides.take_replies();
        harness.app.ask_aside("  are the reviews running?  ");
        assert_eq!(panel(&harness.app), Some((1, None)), "the panel opens at once, waiting");

        let reply = tokio::time::timeout(Duration::from_secs(5), replies.recv()).await.unwrap().unwrap();
        harness.app.on_aside_reply(reply);
        assert_eq!(panel(&harness.app), Some((1, Some("re: are the reviews running?".into()))));
        assert!(harness.calls.lock().unwrap().contains(&"session/askAside".to_owned()));
        assert!(
            harness.app.state.transcript.blocks().is_empty() || outputs(&harness.app).is_empty(),
            "an answered panel adds nothing to the transcript"
        );
    }

    #[tokio::test]
    async fn an_answer_for_a_closed_panel_is_printed_as_a_marked_side_note() {
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| {
            json!({ "ok": true, "answer": "task-0007 is running" })
        });
        let mut replies = harness.app.asides.take_replies();
        harness.app.ask_aside("status?");
        harness.app.surfaces.pop();
        let reply = tokio::time::timeout(Duration::from_secs(5), replies.recv()).await.unwrap().unwrap();
        harness.app.on_aside_reply(reply);
        let printed = outputs(&harness.app);
        assert_eq!(printed.len(), 1, "{printed:?}");
        assert!(printed[0].starts_with("btw: status?") && printed[0].contains("not part of the conversation"));
    }

    #[tokio::test]
    async fn btw_without_a_question_explains_itself() {
        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        harness.app.ask_aside("   ");
        assert!(notices(&harness.app).iter().any(|n| n.contains("Usage: /btw")));
        assert!(harness.app.surfaces.top().is_none());
        assert!(AsideSurface::pending(1, "q").title().contains("Side"));
    }
}
