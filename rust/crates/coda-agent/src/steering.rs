//! Thread-safe steering inbox for mid-turn operator messages.
//!
//! The inbox is open while a turn is running and is atomically sealed at its
//! natural completion, preventing a racing message from sneaking in after the
//! last safe delivery boundary.

use std::sync::Mutex;

/// A single operator message queued for delivery.
#[derive(Debug, Clone)]
pub struct SteeringEntry {
    pub id: String,
    pub text: String,
}

/// Thread-safe FIFO queue for operator steering injections.
///
/// The loop drains it via [`SteeringInbox::take_all_for_delivery`] at the top
/// of every iteration and before each tool in a batch.  At a natural stop,
/// [`SteeringInbox::try_seal_empty`] atomically seals the queue; a failed seal
/// (some message raced in) forces one more iteration to deliver it.
pub struct SteeringInbox {
    gate: Mutex<SteeringInboxInner>,
}

struct SteeringInboxInner {
    pending: Vec<SteeringEntry>,
    sealed_empty: bool,
}

impl SteeringInbox {
    pub fn new() -> Self {
        Self {
            gate: Mutex::new(SteeringInboxInner { pending: Vec::new(), sealed_empty: false }),
        }
    }

    /// Returns `true` when undelivered messages are waiting.
    pub fn has_pending(&self) -> bool {
        !self.gate.lock().unwrap().pending.is_empty()
    }

    /// Enqueue a message.  Returns the accepted entry, or `None` when the
    /// queue has been sealed (the owning turn already completed).
    pub fn enqueue(&self, text: impl Into<String>) -> Option<SteeringEntry> {
        let text = text.into();
        if text.trim().is_empty() {
            return None;
        }
        let mut inner = self.gate.lock().unwrap();
        if inner.sealed_empty {
            return None;
        }
        let entry = SteeringEntry {
            id: uuid::Uuid::new_v4().to_string().replace('-', ""),
            text,
        };
        inner.pending.push(entry.clone());
        Some(entry)
    }

    /// Atomically drain all pending entries for delivery.
    pub fn take_all_for_delivery(&self) -> Vec<SteeringEntry> {
        self.take_all()
    }

    /// Reopen the queue for a newly-started turn without discarding any
    /// already-queued entries.
    pub fn open_for_turn(&self) {
        self.gate.lock().unwrap().sealed_empty = false;
    }

    /// Clear pending entries and reopen the queue.
    pub fn clear(&self) {
        let mut inner = self.gate.lock().unwrap();
        inner.pending.clear();
        inner.sealed_empty = false;
    }

    /// Atomically seal the queue **only if** it is empty.
    ///
    /// Returns `true` on success (turn can complete naturally).  Returns
    /// `false` when a message raced in — the caller must loop once more to
    /// deliver it before asking the model again.
    pub fn try_seal_empty(&self) -> bool {
        let mut inner = self.gate.lock().unwrap();
        if !inner.pending.is_empty() {
            return false;
        }
        inner.sealed_empty = true;
        true
    }

    fn take_all(&self) -> Vec<SteeringEntry> {
        let mut inner = self.gate.lock().unwrap();
        if inner.pending.is_empty() {
            return Vec::new();
        }
        std::mem::take(&mut inner.pending)
    }
}

impl Default for SteeringInbox {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    #[test]
    fn new_inbox_has_no_pending_entries() {
        assert!(!SteeringInbox::new().has_pending());
    }

    #[test]
    fn enqueue_returns_entry() {
        let inbox = SteeringInbox::new();
        let entry = inbox.enqueue("hello").expect("should accept");
        assert_eq!(entry.text, "hello");
        assert!(!entry.id.is_empty());
    }

    #[test]
    fn enqueue_empty_text_is_rejected() {
        let inbox = SteeringInbox::new();
        assert!(inbox.enqueue("").is_none());
        assert!(inbox.enqueue("   ").is_none());
    }

    #[test]
    fn take_all_drains_fifo() {
        let inbox = SteeringInbox::new();
        inbox.enqueue("first").unwrap();
        inbox.enqueue("second").unwrap();
        let entries = inbox.take_all_for_delivery();
        assert_eq!(entries.len(), 2);
        assert_eq!(entries[0].text, "first");
        assert_eq!(entries[1].text, "second");
        assert!(!inbox.has_pending());
    }

    #[test]
    fn try_seal_empty_succeeds_when_empty() {
        let inbox = SteeringInbox::new();
        assert!(inbox.try_seal_empty());
        // After sealing, enqueue is rejected.
        assert!(inbox.enqueue("message").is_none());
    }

    #[test]
    fn try_seal_empty_fails_when_messages_pending() {
        let inbox = SteeringInbox::new();
        inbox.enqueue("pending").unwrap();
        assert!(!inbox.try_seal_empty());
        // Queue remains open.
        assert!(inbox.has_pending());
    }

    #[test]
    fn open_for_turn_unseals_the_queue() {
        let inbox = SteeringInbox::new();
        assert!(inbox.try_seal_empty()); // seal it
        assert!(inbox.enqueue("after seal").is_none()); // rejected
        inbox.open_for_turn(); // reopen
        assert!(inbox.enqueue("after reopen").is_some()); // accepted
    }

    // §8 item 23: a racing message forces a continuation.
    #[test]
    fn racing_message_prevents_seal() {
        let inbox = Arc::new(SteeringInbox::new());
        let inbox2 = Arc::clone(&inbox);

        // Simulate: loop is about to seal, but a message arrives first.
        inbox2.enqueue("race!").unwrap();
        let sealed = inbox.try_seal_empty();
        assert!(!sealed, "seal must fail when a message raced in");
    }
}
