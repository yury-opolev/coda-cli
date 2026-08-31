//! Per-session state.
//!
//! Holds the real `coda_llm::Message` conversation history, the
//! `SteeringInbox` for mid-turn operator injections, and a log of all
//! steering messages for `session/recallSteering`.

use std::sync::{Arc, Mutex};

use coda_agent::SteeringInbox;
use coda_llm::Message;

/// A steering entry that was accepted and logged.
#[derive(Clone)]
pub struct SteeringLogEntry {
    pub id: String,
    pub text: String,
    pub enqueued_at: String,
}

/// Per-connection session state.
pub struct Session {
    pub session_id: String,
    /// Real agent conversation history.
    pub history: Mutex<Vec<Message>>,
    /// Delivery inbox shared with the agent loop.
    pub steering: Arc<SteeringInbox>,
    /// Full log of every steering message (for `recallSteering`).
    pub steering_log: Mutex<Vec<SteeringLogEntry>>,
}

impl Session {
    pub fn new(session_id: impl Into<String>) -> Arc<Self> {
        Arc::new(Self {
            session_id: session_id.into(),
            history: Mutex::new(Vec::new()),
            steering: Arc::new(SteeringInbox::new()),
            steering_log: Mutex::new(Vec::new()),
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

