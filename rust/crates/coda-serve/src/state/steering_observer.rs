//! Bridges `coda_agent::steering::SteeringObserver` to `EngineState`.
//!
//! This is the only place `coda-serve` reaches *into* `coda-agent`'s steering
//! module — the dependency direction stays `coda-serve -> coda-agent`, never
//! the reverse (`coda-agent` has no knowledge of `EngineState` or the bus).
//! `SteeringInbox` calls these methods synchronously, from inside its own
//! gate lock, so the global lock order is `INBOX -> STATE -> BUS`: this
//! struct takes STATE (via `EngineState::update`) which itself takes BUS.
//! No method here may `.await` or call back into the inbox.

use std::sync::Arc;

use coda_agent::steering::{SteerRejectReason, SteeringEntry, SteeringObserver};

use super::EngineState;

/// Tracks the current turn id so steering-delivered outcomes can be tagged
/// with it (best-effort; `None` when no turn is active, e.g. a late recall).
pub struct SteeringStateObserver {
    state: Arc<EngineState>,
    current_turn_id: std::sync::Mutex<Option<String>>,
}

impl SteeringStateObserver {
    pub fn new(state: Arc<EngineState>) -> Arc<Self> {
        Arc::new(Self { state, current_turn_id: std::sync::Mutex::new(None) })
    }

    /// Called by the host at turn start/end (not by the inbox) so delivered
    /// outcomes can be tagged with the owning turn.
    pub fn set_current_turn(&self, turn_id: Option<String>) {
        *self.current_turn_id.lock().unwrap() = turn_id;
    }

    pub(crate) fn turn_id(&self) -> Option<String> {
        self.current_turn_id.lock().unwrap().clone()
    }
}

impl SteeringObserver for SteeringStateObserver {
    fn on_enqueued(&self, entry: &SteeringEntry) {
        self.state.steering_enqueued(&entry.id, &entry.text);
    }

    fn on_delivered(&self, entries: &[SteeringEntry]) {
        let ids: Vec<String> = entries.iter().map(|e| e.id.clone()).collect();
        self.state.steering_delivered(&ids, self.turn_id().as_deref());
    }

    fn on_recalled(&self, entries: &[SteeringEntry]) {
        let ids: Vec<String> = entries.iter().map(|e| e.id.clone()).collect();
        self.state.steering_recalled(&ids);
    }

    fn on_turn_ended_dropped(&self, entries: &[SteeringEntry]) {
        let ids: Vec<String> = entries.iter().map(|e| e.id.clone()).collect();
        self.state.steering_turn_ended_dropped(&ids, self.turn_id().as_deref());
    }

    fn on_rejected(&self, _reason: SteerRejectReason) {
        // Not part of `StateSnapshot` (no queue mutation happened); the exact
        // reason is returned synchronously to the `session/steer` caller
        // instead (see `crate::host`'s serialized preflight path).
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::bus::EventBus;
    use crate::state::tests::active_config;
    use std::collections::HashMap;

    fn state() -> Arc<EngineState> {
        let (tx, _rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        Arc::new(EngineState::new(bus, "s1", "/work", HashMap::new(), active_config()))
    }

    #[test]
    fn enqueue_delivery_and_recall_reach_engine_state() {
        let state = state();
        let observer = SteeringStateObserver::new(Arc::clone(&state));
        let inbox = coda_agent::SteeringInbox::with_observer(Some(
            observer.clone() as Arc<dyn SteeringObserver>
        ));

        inbox.enqueue("steer me").unwrap();
        let snap = state.project("v1",
            coda_proto::state::Limits {
                ring_envelopes: 1,
                ring_bytes: 1,
                live_bytes_cap: 1,
                outcomes_retained: 1,
                history_block_bytes_cap: 1,
                max_history_page: 500,
                max_session_page: 200,
            },
        );
        assert_eq!(snap.steering.pending_count, 1);

        observer.set_current_turn(Some("t1".into()));
        let delivered = inbox.take_all_for_delivery();
        assert_eq!(delivered.len(), 1);

        let snap = state.project("v1",
            coda_proto::state::Limits {
                ring_envelopes: 1,
                ring_bytes: 1,
                live_bytes_cap: 1,
                outcomes_retained: 1,
                history_block_bytes_cap: 1,
                max_history_page: 500,
                max_session_page: 200,
            },
        );
        assert_eq!(snap.steering.pending_count, 0);
        assert_eq!(snap.steering.outcomes.len(), 1);
        assert_eq!(
            snap.steering.outcomes[0].outcome,
            coda_proto::state::SteeringOutcomeKind::Delivered
        );
        assert_eq!(snap.steering.outcomes[0].turn_id.as_deref(), Some("t1"));
    }
}
