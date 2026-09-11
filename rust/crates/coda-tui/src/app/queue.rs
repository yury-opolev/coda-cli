use coda_proto::messages::{method, RecallSteeringResult, RecalledSteeringMessage, SteerResult};

use super::App;
use crate::state::UiEvent;
use crate::transcript::NoticeLevel;

/// An outcome the engine never reported. The message may or may not have been
/// queued, so it is neither claimed as sent nor sent again.
const UNCONFIRMED: &str = "The engine did not confirm whether the message was queued, so it was \
                           not sent again — it may already have reached the queue. Your text is \
                           back in the message box.";

/// What the engine's refusal actually means, in the operator's terms.
///
/// The classification is the engine's own (`noActiveTurn`, `turnEnding`,
/// `emptyText`); dropping it left "could not queue the message" as the only
/// explanation for three quite different situations, one of which is simply
/// "there is no turn to steer — press Enter to send it as a new message".
fn refusal_text(reason: Option<&str>) -> String {
    let explanation = match reason {
        Some("noActiveTurn") => {
            "there is no turn running to steer. Press Enter to send it as a new message."
        }
        Some("turnEnding") => {
            "the turn was already finishing, so the model would never have seen it. Send it once \
             the next turn starts."
        }
        Some("emptyText") => "it had no text in it.",
        Some(other) => {
            return format!(
                "The engine refused to queue the message ({other}); it was not sent. Your text \
                 is back in the message box."
            )
        }
        None => "the engine gave no reason.",
    };
    format!("The message was not queued: {explanation} Your text is back in the message box.")
}

impl App {
    pub(super) async fn steer(&mut self, text: String) {
        // A disconnected session cannot queue anything at an engine, and the
        // draft is the user's: it goes back in the composer rather than into
        // a request nobody will ever answer.
        if !self.engine_connected() {
            self.composer.set_text(text);
            self.hint(
                "Not connected to an engine, so nothing was queued. Your message is still here.",
            );
            return;
        }
        let response = self
            .ask::<serde_json::Value>(method::STEER, Some(serde_json::json!({ "text": &text })))
            .await;
        // Both outcomes below put the draft back and say what happened where
        // it stays said. A transient hint was the wrong shape for either: the
        // one thing that must not happen is the operator believing a message
        // is on its way to the model when it is sitting in the composer.
        let (message, level) = match response {
            Ok(value) => match serde_json::from_value::<SteerResult>(value) {
                Ok(result) if result.ok => {
                    self.apply(UiEvent::Queued { text, id: result.message_id });
                    return;
                }
                Ok(result) => (refusal_text(result.rejected_reason.as_deref()), NoticeLevel::Warning),
                Err(_) => (UNCONFIRMED.to_string(), NoticeLevel::Warning),
            },
            Err(_) => (UNCONFIRMED.to_string(), NoticeLevel::Warning),
        };
        self.composer.set_text(text);
        self.notice(message, level);
    }

    /// Recall is engine-owned: an acknowledged message must never be edited
    /// locally while it remains deliverable in the engine's steering inbox.
    ///
    /// Which is also why a disconnected session reclaims nothing: the queue on
    /// screen describes messages an engine still holds, and emptying it here
    /// on the strength of a call that never happened would lose them.
    pub(super) async fn recall_pending_into_composer(&mut self) -> bool {
        if !self.composer.is_empty() || self.state.queued.is_empty() {
            return false;
        }
        if !self.engine_connected() {
            self.hint(
                "Not connected to an engine, so nothing was reclaimed; your pending text has \
                 been retained.",
            );
            return true;
        }
        let value = match self.ask::<serde_json::Value>(method::RECALL_STEERING, None).await {
            Ok(value) => value,
            Err(_) => {
                self.hint("Could not confirm the reclaim; your pending text has been retained.");
                return true;
            }
        };
        if !value.get("messages").is_some_and(serde_json::Value::is_array) {
            self.hint("The engine returned an invalid recall response; the queue was left unchanged.");
            return true;
        }
        let result: RecallSteeringResult = match serde_json::from_value(value) {
            Ok(result) => result,
            Err(_) => {
                self.hint("The engine returned invalid recalled messages; the queue was left unchanged.");
                return true;
            }
        };
        if result.messages.is_empty() {
            self.hint("No messages remain pending at the engine; nothing was reclaimed.");
            return true;
        }
        let count = result.messages.len();
        let (text, message_ids) = recalled_draft(result.messages);
        self.apply(UiEvent::SteeringRecalled { message_ids });
        self.composer.set_text(text);
        self.hint(format!("Reclaimed {count} pending message(s). Edit and press Enter to send."));
        true
    }
}

fn recalled_draft(messages: Vec<RecalledSteeringMessage>) -> (String, Vec<String>) {
    let text = messages.iter().map(|message| message.text.as_str())
        .collect::<Vec<_>>().join("\n\n");
    let ids = messages.into_iter().map(|message| message.id).collect();
    (text, ids)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn each_refusal_says_what_actually_happened_and_keeps_the_draft() {
        let no_turn = refusal_text(Some("noActiveTurn"));
        assert!(no_turn.contains("no turn running"), "{no_turn}");
        assert!(no_turn.contains("new message"), "{no_turn}");

        let ending = refusal_text(Some("turnEnding"));
        assert!(ending.contains("already finishing"), "{ending}");

        let empty = refusal_text(Some("emptyText"));
        assert!(empty.contains("no text"), "{empty}");

        // A reason this client has never heard of is reported verbatim rather
        // than flattened into a guess.
        let unknown = refusal_text(Some("someFutureReason"));
        assert!(unknown.contains("someFutureReason"), "{unknown}");

        let silent = refusal_text(None);
        assert!(silent.contains("no reason"), "{silent}");

        for text in [no_turn, ending, empty, unknown, silent] {
            assert!(text.contains("back in the message box"), "{text}");
            assert!(!text.contains("resent") && !text.contains("again"), "{text}");
        }
    }

    #[test]
    fn an_unconfirmed_steer_states_the_doubt_and_never_claims_it_was_sent() {
        assert!(UNCONFIRMED.contains("not sent again"));
        assert!(UNCONFIRMED.contains("may already have reached the queue"));
    }

    #[test]
    fn recalled_draft_keeps_fifo_order_whitespace_and_newlines() {
        let messages = vec![
            RecalledSteeringMessage { id: "a".into(), text: "  first\nline  ".into(), enqueued_at: None },
            RecalledSteeringMessage { id: "b".into(), text: "second".into(), enqueued_at: None },
        ];
        let (text, ids) = recalled_draft(messages);
        assert_eq!(text, "  first\nline  \n\nsecond");
        assert_eq!(ids, ["a", "b"]);
    }

    #[test]
    fn empty_recall_does_not_invent_a_draft_or_queue_ids() {
        let (text, ids) = recalled_draft(Vec::new());
        assert!(text.is_empty());
        assert!(ids.is_empty());
    }
}
