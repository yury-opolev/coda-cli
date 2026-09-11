//! Bridges `coda_agent::message::MessageBusObserver` to the authoritative
//! `EventBus`.
//!
//! Unlike `SteeringStateObserver` (which mirrors into `EngineState`'s own
//! projection), agent-message notifications have no derived `StateSnapshot`
//! section of their own in this stage — recovery goes straight through
//! `MessageBus::user_since` (see `dispatch::pending_messages`), which is why
//! this observer's only job is to announce each new publication on the wire
//! as `event/agentMessage`. The bus's own cursor (carried in the event
//! payload as `cursor`) is NOT the `EventBus` seq — see `coda_agent::message`
//! module docs. Never called re-entrantly into `MessageBus`, never `.await`s.

use std::sync::Arc;

use coda_agent::message::{MessageBusObserver, UserMessage};
use serde_json::json;

use crate::bus::EventBus;

pub struct MessageBusEventBridge {
    bus: Arc<EventBus>,
}

impl MessageBusEventBridge {
    pub fn new(bus: Arc<EventBus>) -> Arc<Self> {
        Arc::new(Self { bus })
    }
}

impl MessageBusObserver for MessageBusEventBridge {
    fn on_published(&self, msg: &UserMessage) {
        self.bus.publish(
            coda_proto::events::event_method::AGENT_MESSAGE,
            json!({
                "id": msg.id,
                "cursor": msg.cursor,
                "label": msg.label,
                "text": msg.body,
                "context": msg.context,
                "source": msg.source_kind,
                "taskId": msg.task_id,
                "scheduleDefinitionId": msg.schedule_definition_id,
            }),
            false,
        );
    }

    fn on_overflow(&self, _from_cursor: u64, _to_cursor: u64) {
        // Deliberately silent on every routine overflow eviction: the
        // approved design calls for explicit *messages*, not a wire event on
        // every ring rotation. A client discovers an overflow honestly via
        // `session/pendingMessages`' `gap: true` on its next recovery read.
    }

    fn on_closed(&self) {
        // No wire signal: the bus closes only at engine/session teardown,
        // by which point there is no connection left to notify.
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_agent::message::{MessageBus, MessageSource};

    #[test]
    fn publish_emits_agent_message_event_on_the_bus() {
        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel();
        let event_bus = Arc::new(EventBus::new(tx, "engine-1"));
        let bridge = MessageBusEventBridge::new(Arc::clone(&event_bus));
        let msg_bus = MessageBus::with_observer(Some(bridge as Arc<dyn MessageBusObserver>));

        msg_bus.publish_user(&MessageSource::Main, "hello", None, None).unwrap();

        let frame = rx.try_recv().expect("expected one frame on the wire");
        // Frames are length-prefixed JSON-RPC notifications; skip the header
        // and parse the JSON body directly (mirrors `bus::tests::decode_notification`).
        let s = std::str::from_utf8(&frame).unwrap();
        let body_start = s.find("\r\n\r\n").map(|i| i + 4).unwrap_or(0);
        let value: serde_json::Value = serde_json::from_str(&s[body_start..]).unwrap();
        assert_eq!(value["method"], "event/agentMessage");
        assert_eq!(value["params"]["text"], "hello");
        assert_eq!(value["params"]["source"], "main");
    }
}
