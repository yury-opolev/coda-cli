//! `AgentSink` → outbound event notifications.
//!
//! `ServeSink` implements `AgentSink` by translating each `AgentEvent` to a
//! `coda_proto::Event` via `coda_agent::events::to_proto_event`, then calling
//! `Event::to_notification()` to get the `(method, params)` pair, and finally
//! enqueuing a framed notification onto the shared write channel.
//!
//! The sink is `Send + Sync` because `tokio::sync::mpsc::UnboundedSender` is.

use coda_agent::events::{AgentEvent, AgentSink, to_proto_event};
use coda_proto::{Notification, encode_frame};
use tokio::sync::mpsc;

/// Bridges the agent event stream to the JSON-RPC writer channel.
pub struct ServeSink {
    outgoing: mpsc::UnboundedSender<Vec<u8>>,
}

impl ServeSink {
    pub fn new(outgoing: mpsc::UnboundedSender<Vec<u8>>) -> Self {
        Self { outgoing }
    }
}

impl AgentSink for ServeSink {
    fn emit(&self, event: AgentEvent) {
        let Some(proto) = to_proto_event(&event) else {
            return;
        };
        let Some((method, params)) = proto.to_notification() else {
            return;
        };
        let notification = Notification::new(method, Some(params));
        match serde_json::to_vec(&notification) {
            Ok(bytes) => {
                let _ = self.outgoing.send(encode_frame(&bytes));
            }
            Err(e) => tracing::error!(%e, "failed to serialise event notification"),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::Value;

    fn decode_notification(bytes: Vec<u8>) -> Value {
        // Strip the Content-Length framing header.
        let s = String::from_utf8(bytes).expect("utf8");
        let body_start = s.find("\r\n\r\n").map(|i| i + 4).unwrap_or(0);
        serde_json::from_str(&s[body_start..]).expect("json")
    }

    #[test]
    fn assistant_text_event_is_forwarded_as_notification() {
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = ServeSink::new(tx);

        sink.emit(AgentEvent::AssistantText { delta: "hello".into() });

        let frame = rx.try_recv().expect("frame");
        let msg = decode_notification(frame);
        assert_eq!(msg["method"], "event/assistantText");
        assert_eq!(msg["params"]["delta"], "hello");
        // Notifications must not have an id field.
        assert!(msg.get("id").is_none(), "notifications must not have an id");
    }

    #[test]
    fn turn_complete_event_is_forwarded() {
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = ServeSink::new(tx);

        sink.emit(AgentEvent::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        });

        let frame = rx.try_recv().expect("frame");
        let msg = decode_notification(frame);
        assert_eq!(msg["method"], "event/turnComplete");
        assert_eq!(msg["params"]["stopReason"], "end_turn");
        // Absent optionals must be omitted, not null.
        assert!(
            msg["params"].get("rootTurnId").is_none(),
            "absent rootTurnId must be omitted from JSON"
        );
    }

    #[test]
    fn gap_events_produce_no_frame() {
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = ServeSink::new(tx);

        // ToolQueued and Warning have no proto event.
        sink.emit(AgentEvent::ToolQueued {
            tool_name: "t".into(),
            input_json: "{}".into(),
            correlation: Default::default(),
        });
        sink.emit(AgentEvent::Warning { message: "heads up".into() });

        assert!(
            rx.try_recv().is_err(),
            "gap events must not produce any outbound frame"
        );
    }
}
