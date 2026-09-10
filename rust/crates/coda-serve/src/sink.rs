//! `AgentSink` → outbound event notifications.
//!
//! `ServeSink` implements `AgentSink` by translating each `AgentEvent` to a
//! `coda_proto::Event` via `coda_agent::events::to_proto_event`, then calling
//! `Event::to_notification()` to get the `(method, params)` pair, and finally
//! publishing it through the shared [`EventBus`] — which assigns the
//! envelope `seq`/`engineInstanceId`, stores the whole frame in the bounded
//! ring, and writes it to the outbound channel. Legacy events are never
//! gated: `gated=false` on every `publish` call from this sink (§2.1 — only
//! the *new* state/queue/lifecycle methods, published directly by
//! `EngineState`, are gated behind `stateEvents`).
//!
//! # Scope after Stage C correction C1
//!
//! `ServeSink` owns the bus and is the publication path for events that carry
//! **no** state transition — host-level `Error` frames and deferred MCP
//! notices. Everything the agent loop emits during a turn goes through
//! `crate::state_sink::StateSink` instead, which publishes the very same
//! frame through the very same bus but does so *inside* the `EngineState`
//! transaction that applies the transition, so a snapshot's cursor can never
//! disagree with its own content. Both paths share one bus, so seq
//! assignment and wire order stay globally consistent either way.
//!
//! The sink is `Send + Sync` because [`EventBus`] is.

use std::sync::Arc;

use coda_agent::events::{AgentEvent, AgentSink, to_proto_event};
use tokio::sync::mpsc;
use uuid::Uuid;

use crate::bus::EventBus;

/// Bridges the agent event stream to the shared event bus.
pub struct ServeSink {
    bus: Arc<EventBus>,
}

impl ServeSink {
    pub fn new(outgoing: mpsc::UnboundedSender<Vec<u8>>) -> Self {
        Self { bus: Arc::new(EventBus::new(outgoing, Uuid::new_v4().to_string())) }
    }

    /// The shared bus this sink publishes through — `ServeHost` uses this to
    /// build `EngineState` so both share one ordering/publication point and
    /// one `engineInstanceId`.
    pub fn bus(&self) -> Arc<EventBus> {
        Arc::clone(&self.bus)
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
        self.bus.publish(&method, params, false);
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
        // Additive envelope metadata (§2.1): every event burns a seq.
        assert_eq!(msg["params"]["seq"], 1);
        assert!(msg["params"]["engineInstanceId"].is_string());
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

    #[test]
    fn sequential_events_get_increasing_seq_numbers() {
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = ServeSink::new(tx);
        sink.emit(AgentEvent::AssistantText { delta: "a".into() });
        sink.emit(AgentEvent::AssistantText { delta: "b".into() });
        let first = decode_notification(rx.try_recv().unwrap());
        let second = decode_notification(rx.try_recv().unwrap());
        assert_eq!(first["params"]["seq"], 1);
        assert_eq!(second["params"]["seq"], 2);
    }
}
