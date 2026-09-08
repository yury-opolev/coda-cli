use coda_proto::messages::{method, RecallSteeringResult, RecalledSteeringMessage, SteerResult};

use super::App;
use crate::state::UiEvent;

impl App {
    pub(super) async fn steer(&mut self, text: String) {
        let response = self.connection.request(method::STEER, Some(serde_json::json!({ "text": &text }))).await;
        let hint = match response {
            Ok(value) => match serde_json::from_value::<SteerResult>(value) {
                Ok(result) if result.ok => {
                    self.apply(UiEvent::Queued { text, id: result.message_id });
                    return;
                }
                Ok(_) => "The engine could not queue the message; your draft was restored.",
                Err(_) => "Could not confirm queuing; draft restored. The engine may already have received it.",
            },
            Err(_) => "Could not confirm queuing; draft restored. The engine may already have received it.",
        };
        self.composer.set_text(text);
        self.hint(hint);
    }

    /// Recall is engine-owned: an acknowledged message must never be edited
    /// locally while it remains deliverable in the engine's steering inbox.
    pub(super) async fn recall_pending_into_composer(&mut self) -> bool {
        if !self.composer.is_empty() || self.state.queued.is_empty() {
            return false;
        }
        let value = match self.connection.request(method::RECALL_STEERING, None).await {
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
