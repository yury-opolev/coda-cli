use coda_proto::messages::{method, RecallSteeringResult, RecalledSteeringMessage, SteerResult, WireImage};

use super::image::image_count_label;
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
/// `emptyText`, and for images `invalidImage`, `imagesUnsupported`,
/// `attachmentCapacity`); dropping it left "could not queue the message" as
/// the only explanation for quite different situations, one of which is
/// simply "there is no turn to steer — press Enter to send it as a new
/// message". `detail` is the engine's own wording, shown where it adds
/// something the reason alone does not say.
fn refusal_text(reason: Option<&str>, detail: Option<&str>) -> String {
    let explanation = match reason {
        Some("noActiveTurn") => {
            "there is no turn running to steer. Press Enter to send it as a new message.".to_string()
        }
        Some("turnEnding") => {
            "the turn was already finishing, so the model would never have seen it. Send it once \
             the next turn starts."
                .to_string()
        }
        Some("emptyText") => "it had no text in it.".to_string(),
        Some("invalidImage") => format!(
            "an image could not be used{}, so nothing was queued.",
            detail.map(|d| format!(" ({d})")).unwrap_or_default()
        ),
        Some("imagesUnsupported") => {
            "the current model cannot receive images, so nothing was queued — the text was not \
             sent without them."
                .to_string()
        }
        Some("attachmentCapacity") => {
            "the queue already holds as many images as it can. Send it once the queued messages \
             have reached the model, or reclaim them first."
                .to_string()
        }
        Some(other) => {
            return format!(
                "The engine refused to queue the message ({other}); it was not sent. Your text \
                 is back in the message box."
            )
        }
        None => "the engine gave no reason.".to_string(),
    };
    format!("The message was not queued: {explanation} Your text is back in the message box.")
}

/// The notice for an image-bearing draft an engine without image steering
/// cannot take. Sending the text alone would silently drop the images.
pub(super) const IMAGES_NEED_NEWER_ENGINE: &str =
    "This engine cannot queue images while it works (it predates image steering), so nothing \
     was sent. Your draft and its images are still here — send it when the turn finishes, or \
     update Coda.";

impl App {
    pub(super) async fn steer(&mut self, text: String, images: Vec<WireImage>) {
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
        let mut params = serde_json::json!({ "text": &text });
        if !images.is_empty() {
            params["images"] = serde_json::json!(&images);
        }
        let response = self.ask::<serde_json::Value>(method::STEER, Some(params)).await;
        // Both outcomes below put the draft back and say what happened where
        // it stays said. A transient hint was the wrong shape for either: the
        // one thing that must not happen is the operator believing a message
        // is on its way to the model when it is sitting in the composer.
        //
        // Staged images are released only once the engine has accepted the
        // message, which by then holds its own copy; on every other outcome
        // they stay staged, so the restored draft still resolves to them.
        let (message, level) = match response {
            Ok(value) => match serde_json::from_value::<SteerResult>(value) {
                Ok(result) if result.ok => {
                    let count = images.len();
                    if count > 0 {
                        self.staged_images.clear();
                    }
                    self.apply(UiEvent::Queued { text, id: result.message_id, images });
                    if count > 0 {
                        self.hint(format!(
                            "Queued · {} · will reach the model at the next safe boundary",
                            image_count_label(count)
                        ));
                    }
                    return;
                }
                Ok(result) => (
                    refusal_text(result.rejected_reason.as_deref(), result.rejected_detail.as_deref()),
                    NoticeLevel::Warning,
                ),
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
        let (text, message_ids, images) = recalled_draft(result.messages);
        self.apply(UiEvent::SteeringRecalled { message_ids });
        // Text and images come back together, each image under the marker
        // that referenced it, so sending the draft again sends the same thing.
        self.restore_draft(text, images);
        self.hint(format!("Reclaimed {count} pending message(s). Edit and press Enter to send."));
        true
    }
}

fn recalled_draft(messages: Vec<RecalledSteeringMessage>) -> (String, Vec<String>, Vec<WireImage>) {
    let text = messages.iter().map(|message| message.text.as_str())
        .collect::<Vec<_>>().join("\n\n");
    let images = messages.iter().flat_map(|message| message.images.iter().cloned()).collect();
    let ids = messages.into_iter().map(|message| message.id).collect();
    (text, ids, images)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn each_refusal_says_what_actually_happened_and_keeps_the_draft() {
        let no_turn = refusal_text(Some("noActiveTurn"), None);
        assert!(no_turn.contains("no turn running"), "{no_turn}");
        assert!(no_turn.contains("new message"), "{no_turn}");

        let ending = refusal_text(Some("turnEnding"), None);
        assert!(ending.contains("already finishing"), "{ending}");

        let empty = refusal_text(Some("emptyText"), None);
        assert!(empty.contains("no text"), "{empty}");

        // A reason this client has never heard of is reported verbatim rather
        // than flattened into a guess.
        let unknown = refusal_text(Some("someFutureReason"), None);
        assert!(unknown.contains("someFutureReason"), "{unknown}");

        let silent = refusal_text(None, None);
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
    fn image_refusals_say_nothing_was_queued_and_keep_the_draft() {
        let invalid = refusal_text(Some("invalidImage"), Some("image 2: unsupported image media type: image/bmp"));
        assert!(invalid.contains("image 2: unsupported"), "{invalid}");
        let unsupported = refusal_text(Some("imagesUnsupported"), None);
        assert!(unsupported.contains("cannot receive images"), "{unsupported}");
        let full = refusal_text(Some("attachmentCapacity"), Some("at most 32 MiB"));
        assert!(full.contains("as many images as it can"), "{full}");
        for text in [invalid, unsupported, full] {
            assert!(text.contains("nothing was queued") || text.contains("not queued"), "{text}");
            assert!(text.contains("back in the message box"), "{text}");
        }
    }

    #[test]
    fn recalled_draft_keeps_every_messages_images_in_order() {
        let image = |b: &str| WireImage { media_type: "image/png".into(), base64: b.into() };
        let messages = vec![
            RecalledSteeringMessage { id: "a".into(), text: "one".into(), enqueued_at: None, images: vec![image("1"), image("2")] },
            RecalledSteeringMessage { id: "b".into(), text: "two".into(), enqueued_at: None, images: vec![image("3")] },
        ];
        let (_, _, images) = recalled_draft(messages);
        let payloads: Vec<_> = images.iter().map(|i| i.base64.as_str()).collect();
        assert_eq!(payloads, ["1", "2", "3"]);
    }

    #[test]
    fn recalled_draft_keeps_fifo_order_whitespace_and_newlines() {
        let messages = vec![
            RecalledSteeringMessage { id: "a".into(), text: "  first\nline  ".into(), enqueued_at: None, images: Vec::new() },
            RecalledSteeringMessage { id: "b".into(), text: "second".into(), enqueued_at: None, images: Vec::new() },
        ];
        let (text, ids, _) = recalled_draft(messages);
        assert_eq!(text, "  first\nline  \n\nsecond");
        assert_eq!(ids, ["a", "b"]);
    }

    #[test]
    fn empty_recall_does_not_invent_a_draft_or_queue_ids() {
        let (text, ids, images) = recalled_draft(Vec::new());
        assert!(images.is_empty());
        assert!(text.is_empty());
        assert!(ids.is_empty());
    }
}
