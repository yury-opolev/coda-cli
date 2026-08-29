//! Per-session state.
//!
//! Holds the session ID and provides a seam for history access.
//! History is currently stubbed (no LLM client is wired); the fields exist so
//! `host.rs` can be extended to a live agent incrementally.

use std::sync::{Arc, Mutex};

/// Per-connection session state.
pub struct Session {
    pub session_id: String,
    /// Conversation history.  Stubbed as raw JSON for now; a full
    /// implementation will use `Vec<coda_llm::Message>`.
    history: Mutex<Vec<HistoryEntry>>,
}

/// A single history entry for the wire format.
#[derive(Clone)]
pub struct HistoryEntry {
    pub role: String,
    pub content: String,
}

impl Session {
    pub fn new(session_id: impl Into<String>) -> Arc<Self> {
        Arc::new(Self { session_id: session_id.into(), history: Mutex::new(Vec::new()) })
    }

    pub fn history(&self) -> Vec<HistoryEntry> {
        self.history.lock().expect("history lock poisoned").clone()
    }

    pub fn push(&self, entry: HistoryEntry) {
        self.history.lock().expect("history lock poisoned").push(entry);
    }

    pub fn len(&self) -> usize {
        self.history.lock().expect("history lock poisoned").len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn session_has_unique_id() {
        let a = Session::new("abc");
        let b = Session::new("xyz");
        assert_ne!(a.session_id, b.session_id);
    }

    #[test]
    fn session_history_starts_empty() {
        let s = Session::new("test");
        assert!(s.history().is_empty());
    }

    #[test]
    fn session_push_appends_to_history() {
        let s = Session::new("test");
        s.push(HistoryEntry { role: "user".into(), content: "hello".into() });
        s.push(HistoryEntry { role: "assistant".into(), content: "hi".into() });
        let h = s.history();
        assert_eq!(h.len(), 2);
        assert_eq!(h[0].role, "user");
        assert_eq!(h[1].role, "assistant");
    }
}
