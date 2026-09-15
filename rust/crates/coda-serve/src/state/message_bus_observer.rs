//! Bridges `coda_agent::message::MessageBusObserver` to `EngineState`/the
//! authoritative `EventBus` (Stage 2 passive notifications, Stage 3 chunk B
//! `ask_main` delivery).
//!
//! Stage 2's passive `notify_user` publications have no derived
//! `StateSnapshot` section of their own — recovery goes straight through
//! `MessageBus::user_since` (see `dispatch::pending_messages`), so
//! `on_published` only announces each new publication on the wire as
//! `event/agentMessage`, going straight to the bus rather than through an
//! `EngineState` transaction (unchanged from before this bridge also held
//! `EngineState`).
//!
//! The main-inbox side (`ask_main`, Stage 3) is different:
//! - `on_main_accepted` is deliberately a WAKE SIGNAL ONLY
//!   (`main_wake.notify_one()`): no wire event, no user-ring publication.
//!   Accepting a background request is not itself user-facing (see
//!   `coda_agent::message` module docs — it is neither a passive
//!   notification nor a new user instruction), so nothing here surfaces it
//!   automatically. The single trusted consumer of this signal is the
//!   engine's idle main-inbox pump (`ServeHost::main_pump_step`), which
//!   claims the ordinary single-flight turn slot so the main `AgentLoop`'s
//!   own step 4c can drain the queue. This observer never drains it itself.
//! - `on_main_delivered` goes through `EngineState::main_messages_delivered`,
//!   which projects the delivered items' `injected_text()` into the running
//!   turn's own live history and announces `event/agentMessageDelivered` in
//!   ONE transaction — unlike `on_published`, a second, direct wire publish
//!   here would race the projection it is supposed to describe.
//!
//! Never called re-entrantly into `MessageBus`, never `.await`s — mirrors
//! `SteeringStateObserver`'s discipline (see that module's docs for the lock
//! order this preserves: `BUS -> STATE -> (outer) BUS`).

use std::sync::Arc;

use coda_agent::message::{MainMessage, MessageBusObserver, UserMessage};
use serde_json::json;

use super::{EngineState, MainMessagesDeliveredOutcome};

pub struct MessageBusEventBridge {
    state: Arc<EngineState>,
    /// Signalled exactly once per newly-accepted `ask_main` item
    /// (`on_main_accepted`), never for anything else. Stored (rather than
    /// only held by a spawned task) so the engine's idle main-inbox pump can
    /// `.notified().await` on the SAME `Arc` `ServeHost` retains.
    main_wake: Arc<tokio::sync::Notify>,
}

impl MessageBusEventBridge {
    pub fn new(state: Arc<EngineState>, main_wake: Arc<tokio::sync::Notify>) -> Arc<Self> {
        Arc::new(Self { state, main_wake })
    }
}

impl MessageBusObserver for MessageBusEventBridge {
    fn on_published(&self, msg: &UserMessage) {
        self.state.bus_ref().publish(
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

    /// Wake-only — see module docs. Never touches the wire or the user ring.
    fn on_main_accepted(&self, _msg: &MainMessage) {
        self.main_wake.notify_one();
    }

    /// Projects the drained batch into the running turn's live history and
    /// announces delivery, atomically, inside `EngineState`. See
    /// `EngineState::main_messages_delivered` for the dedup / no-active-turn
    /// contract this relies on.
    fn on_main_delivered(&self, msgs: &[MainMessage]) {
        if msgs.is_empty() {
            return;
        }
        match self.state.main_messages_delivered(msgs) {
            MainMessagesDeliveredOutcome::Projected { .. } => {}
            MainMessagesDeliveredOutcome::NoActiveTurn => {
                // Metadata-only invariant warning — never the message body.
                // The only correct caller of `take_main_for_delivery` is the
                // trusted main `AgentLoop`'s own iteration boundary while its
                // own turn is live (see `crate::agent::AgentLoop` step 4c in
                // `coda-agent`); seeing this with no active turn means some
                // caller reached it outside that contract. Never fabricated
                // as history — just surfaced so the misuse is not silent.
                tracing::warn!(
                    count = msgs.len(),
                    "ask_main delivery observed with no active running turn; nothing was \
                     projected (see EngineState::main_messages_delivered)"
                );
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::bus::EventBus;
    use crate::state::tests::active_config;
    use coda_agent::message::{MessageBus, MessageSource};
    use std::collections::HashMap;
    use std::time::Duration;

    fn state_and_rx() -> (Arc<EngineState>, tokio::sync::mpsc::UnboundedReceiver<Vec<u8>>) {
        let (tx, rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        let state =
            Arc::new(EngineState::new(bus, "s1", "/work", HashMap::new(), active_config()));
        (state, rx)
    }

    fn bridge(state: &Arc<EngineState>) -> (Arc<MessageBusEventBridge>, Arc<tokio::sync::Notify>) {
        let wake = Arc::new(tokio::sync::Notify::new());
        (MessageBusEventBridge::new(Arc::clone(state), Arc::clone(&wake)), wake)
    }

    /// Frames are length-prefixed JSON-RPC notifications; skip the header and
    /// parse the JSON body directly (mirrors `bus::tests::decode_notification`).
    fn decode(frame: &[u8]) -> serde_json::Value {
        let s = std::str::from_utf8(frame).unwrap();
        let body_start = s.find("\r\n\r\n").map(|i| i + 4).unwrap_or(0);
        serde_json::from_str(&s[body_start..]).unwrap()
    }

    #[test]
    fn publish_emits_agent_message_event_on_the_bus() {
        let (state, mut rx) = state_and_rx();
        let (bridge, _wake) = bridge(&state);
        let msg_bus = MessageBus::with_observer(Some(bridge as Arc<dyn MessageBusObserver>));

        msg_bus.publish_user(&MessageSource::Main, "hello", None, None).unwrap();

        let frame = rx.try_recv().expect("expected one frame on the wire");
        let value = decode(&frame);
        assert_eq!(value["method"], "event/agentMessage");
        assert_eq!(value["params"]["text"], "hello");
        assert_eq!(value["params"]["source"], "main");
    }

    #[tokio::test]
    async fn on_main_accepted_only_wakes_never_touches_wire_or_user_ring() {
        let (state, mut rx) = state_and_rx();
        let (bridge, wake) = bridge(&state);
        let msg_bus = MessageBus::with_observer(Some(bridge as Arc<dyn MessageBusObserver>));

        let source = MessageSource::Subagent { task_id: "t1".into(), label: "worker".into() };
        msg_bus.publish_main(&source, "please look at X", None, None).unwrap();

        tokio::time::timeout(Duration::from_millis(200), wake.notified())
            .await
            .expect("on_main_accepted must notify the wake signal exactly once");

        assert!(
            rx.try_recv().is_err(),
            "accepting a main-inbox item must never itself publish a wire event"
        );
        assert_eq!(
            msg_bus.user_since(0, None).messages.len(),
            0,
            "accepting a main-inbox item must never publish into the passive user ring either"
        );
    }

    #[test]
    fn on_main_delivered_projects_atomically_and_announces_metadata_only() {
        let (state, mut rx) = state_and_rx();
        let (bridge, _wake) = bridge(&state);
        let msg_bus = MessageBus::with_observer(Some(bridge as Arc<dyn MessageBusObserver>));

        assert!(state.begin_turn(
            "turn-1",
            "hi",
            active_config(),
            coda_proto::state::ActivityPhase::Preparing,
        ));
        // Drain whatever `begin_turn` itself published so it cannot be
        // mistaken for the delivery announcement below.
        let _ = rx.try_recv();

        let source = MessageSource::Subagent { task_id: "t1".into(), label: "worker".into() };
        let receipt = msg_bus.publish_main(&source, "please look at X", None, None).unwrap();
        // `publish_main`'s own `on_main_accepted` never publishes a frame
        // (asserted separately above) — nothing to drain here.

        let delivered = msg_bus.take_main_for_delivery();
        assert_eq!(delivered.len(), 1);
        let expected_text = delivered[0].injected_text();

        let view = state.history_view();
        let live = view.live.expect("a running turn always has a live view");
        let found = live.entries.iter().any(|e| {
            e.blocks.iter().any(|b| {
                matches!(
                    b,
                    coda_proto::history::HistoryBlock::Text { text, .. } if text == &expected_text
                )
            })
        });
        assert!(found, "the exact injected_text() must be projected live, verbatim");

        let frame = rx.try_recv().expect("expected event/agentMessageDelivered on the wire");
        let value = decode(&frame);
        assert_eq!(value["method"], "event/agentMessageDelivered");
        assert_eq!(value["params"]["turnId"], "turn-1");
        assert_eq!(value["params"]["items"][0]["id"], receipt.id);
        assert_eq!(value["params"]["items"][0]["seq"], receipt.seq as i64);
        assert_eq!(value["params"]["items"][0]["source"], "subagent");
        assert_eq!(value["params"]["items"][0]["taskId"], "t1");
        // Metadata only: the injected text itself must never ride the wire
        // here — a client recovers it via `session/getHistory`.
        assert!(value["params"]["items"][0].get("text").is_none());
        assert!(value["params"].get("text").is_none());
    }

    #[test]
    fn on_main_delivered_with_no_active_turn_fabricates_no_history_and_emits_no_event() {
        let (state, mut rx) = state_and_rx();
        let (bridge, _wake) = bridge(&state);
        let msg_bus = MessageBus::with_observer(Some(bridge as Arc<dyn MessageBusObserver>));

        // No `begin_turn` call: nothing is running when this drains — the
        // bus itself invokes `on_main_delivered` through the observer below.
        let source = MessageSource::Subagent { task_id: "t1".into(), label: "worker".into() };
        msg_bus.publish_main(&source, "please look at X", None, None).unwrap();

        let delivered = msg_bus.take_main_for_delivery();
        assert_eq!(delivered.len(), 1);

        assert!(rx.try_recv().is_err(), "no active turn must never announce a delivery event");
        assert!(
            state.history_view().live.is_none(),
            "no active turn must never fabricate a live view"
        );
    }
}
