//! Per-session state.
//!
//! Holds the real `coda_llm::Message` conversation history and the
//! `SteeringInbox` for mid-turn operator injections. The inbox is
//! constructed with an optional [`SteeringObserver`] (Slice 0 / Stage C) so
//! `EngineState` mirrors every enqueue/delivery/recall/drop transition as a
//! derived projection; the inbox itself remains the sole execution queue and
//! its own authoritative source of truth (§3 of the serve API implementation
//! plan). `steering_log` (a second, undocumented queue authority that leaked
//! full message text and was never cleared at turn end — F1) has been
//! removed: outcomes and timestamps are now owned end-to-end by the steering
//! projection in `crate::state`.

use std::sync::{Arc, Mutex};

use coda_agent::steering::SteeringObserver;
use coda_agent::SteeringInbox;
use coda_llm::Message;

/// Per-connection session state.
pub struct Session {
    pub session_id: String,
    /// Real agent conversation history.
    pub history: Mutex<Vec<Message>>,
    /// Delivery inbox shared with the agent loop.
    pub steering: Arc<SteeringInbox>,
}

impl Session {
    pub fn new(session_id: impl Into<String>) -> Arc<Self> {
        Self::with_steering_observer(session_id, None)
    }

    pub fn with_steering_observer(
        session_id: impl Into<String>,
        observer: Option<Arc<dyn SteeringObserver>>,
    ) -> Arc<Self> {
        Arc::new(Self {
            session_id: session_id.into(),
            history: Mutex::new(Vec::new()),
            steering: Arc::new(SteeringInbox::with_observer(observer)),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn session_history_starts_empty() {
        let s = Session::new("test");
        assert!(s.history.lock().unwrap().is_empty());
    }

    #[test]
    fn session_id_is_preserved() {
        let s = Session::new("abc-123");
        assert_eq!(s.session_id, "abc-123");
    }
}

