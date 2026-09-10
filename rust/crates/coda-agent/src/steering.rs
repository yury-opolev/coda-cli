//! Thread-safe steering inbox for mid-turn operator messages.
//!
//! The inbox is open while a turn is running and is atomically sealed at its
//! natural completion, preventing a racing message from sneaking in after the
//! last safe delivery boundary.
//!
//! # Observer (Slice 0 / Stage C)
//!
//! [`SteeringInbox`] optionally carries a [`SteeringObserver`], invoked
//! synchronously **while still holding the inbox's own gate lock** for every
//! mutation (enqueue, delivery, recall, turn-end drop, rejection). This keeps
//! "the mutation happened" and "the observer saw it" one atomic step from the
//! perspective of any other thread touching this inbox — the global lock
//! order for callers that use this hook is `INBOX -> STATE -> BUS`
//! (`coda-serve` implements the trait and takes its own state lock, then its
//! event-bus lock, from inside this callback). The observer must never call
//! back into this inbox (no re-entrant locking) and must never `.await` (the
//! gate is a `std::sync::Mutex`).
//!
//! `SteeringInbox` remains the sole execution queue with its existing proven
//! atomicity; the observer only mirrors transitions into a read-only
//! projection elsewhere. Nothing about the queue's own semantics changes when
//! no observer is installed (the default, used by every existing caller).

use std::sync::{Arc, Mutex};

/// A single operator message queued for delivery.
#[derive(Debug, Clone)]
pub struct SteeringEntry {
    pub id: String,
    pub text: String,
}

/// Why an `enqueue` was refused. Distinguishes "no turn is running" (sealed)
/// from "nothing to send" (empty text) so `session/steer` can report an exact
/// `rejectedReason` instead of a mute `ok:false`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SteerRejectReason {
    /// The queue is sealed — no turn is currently accepting steering.
    Sealed,
    /// The text was empty (or all whitespace).
    EmptyText,
}

/// Synchronous observer for steering-queue transitions.
///
/// Implemented by `coda-serve` (never by `coda-agent` itself — this keeps the
/// dependency direction `coda-serve -> coda-agent` and not the reverse).
pub trait SteeringObserver: Send + Sync {
    /// A message was accepted into the queue.
    fn on_enqueued(&self, _entry: &SteeringEntry) {}
    /// Entries were claimed for delivery (never called with an empty slice).
    fn on_delivered(&self, _entries: &[SteeringEntry]) {}
    /// Entries were withdrawn via recall (never called with an empty slice).
    fn on_recalled(&self, _entries: &[SteeringEntry]) {}
    /// A turn ended while entries were still undelivered; they are being
    /// dropped (never called with an empty slice). Called from both the
    /// explicit seal and `TurnGuard::drop` — natural idempotency: the second
    /// `close_for_turn()` call always sees an already-empty queue, so this
    /// fires at most once per batch of dropped entries.
    fn on_turn_ended_dropped(&self, _entries: &[SteeringEntry]) {}
    /// An `enqueue` was refused.
    fn on_rejected(&self, _reason: SteerRejectReason) {}
}

/// Thread-safe FIFO queue for operator steering injections.
///
/// The loop drains it via [`SteeringInbox::take_all_for_delivery`] at the top
/// of every iteration and before each tool in a batch.  At a natural stop,
/// [`SteeringInbox::try_seal_empty`] atomically seals the queue; a failed seal
/// (some message raced in) forces one more iteration to deliver it.
pub struct SteeringInbox {
    gate: Mutex<SteeringInboxInner>,
    observer: Option<Arc<dyn SteeringObserver>>,
}

struct SteeringInboxInner {
    pending: Vec<SteeringEntry>,
    sealed_empty: bool,
}

impl SteeringInbox {
    pub fn new() -> Self {
        Self::with_observer(None)
    }

    /// Constructs an inbox with an optional synchronous observer (see module
    /// docs for the lock-order contract).
    pub fn with_observer(observer: Option<Arc<dyn SteeringObserver>>) -> Self {
        Self {
            gate: Mutex::new(SteeringInboxInner { pending: Vec::new(), sealed_empty: false }),
            observer,
        }
    }

    /// Returns `true` when undelivered messages are waiting.
    pub fn has_pending(&self) -> bool {
        !self.gate.lock().unwrap().pending.is_empty()
    }

    /// Enqueue a message.  Returns the accepted entry, or `None` when the
    /// queue has been sealed (the owning turn already completed) or the text
    /// was empty. Kept for existing callers that don't need the exact reason;
    /// prefer [`SteeringInbox::enqueue_with_reason`] for a new caller such as
    /// `session/steer`, which reports `rejectedReason`.
    pub fn enqueue(&self, text: impl Into<String>) -> Option<SteeringEntry> {
        self.enqueue_with_reason(text).ok()
    }

    /// Enqueue a message, reporting the exact rejection reason on failure.
    pub fn enqueue_with_reason(
        &self,
        text: impl Into<String>,
    ) -> Result<SteeringEntry, SteerRejectReason> {
        let text = text.into();
        if text.trim().is_empty() {
            if let Some(obs) = &self.observer {
                obs.on_rejected(SteerRejectReason::EmptyText);
            }
            return Err(SteerRejectReason::EmptyText);
        }
        let mut inner = self.gate.lock().unwrap();
        if inner.sealed_empty {
            if let Some(obs) = &self.observer {
                obs.on_rejected(SteerRejectReason::Sealed);
            }
            return Err(SteerRejectReason::Sealed);
        }
        let entry = SteeringEntry {
            id: uuid::Uuid::new_v4().to_string().replace('-', ""),
            text,
        };
        inner.pending.push(entry.clone());
        // Observer runs while `inner` is still held (INBOX -> STATE -> BUS).
        if let Some(obs) = &self.observer {
            obs.on_enqueued(&entry);
        }
        Ok(entry)
    }

    /// Atomically drain all pending entries for delivery.
    pub fn take_all_for_delivery(&self) -> Vec<SteeringEntry> {
        let mut inner = self.gate.lock().unwrap();
        if inner.pending.is_empty() {
            return Vec::new();
        }
        let entries = std::mem::take(&mut inner.pending);
        if let Some(obs) = &self.observer {
            obs.on_delivered(&entries);
        }
        entries
    }

    /// Withdraws only entries not already claimed for delivery, atomically
    /// against the delivery path. Recalling never reopens a sealed inbox.
    pub fn recall_all(&self) -> Vec<SteeringEntry> {
        let mut inner = self.gate.lock().unwrap();
        if inner.pending.is_empty() {
            return Vec::new();
        }
        let entries = std::mem::take(&mut inner.pending);
        if let Some(obs) = &self.observer {
            obs.on_recalled(&entries);
        }
        entries
    }

    /// Ends a turn without allowing undelivered entries to leak into the next
    /// one. The UI retains their text for explicit recovery, not auto-delivery.
    ///
    /// Idempotent: called from both the explicit turn seal and
    /// `TurnGuard::drop` on every turn; whichever runs first reports the
    /// dropped entries to the observer, the second call always finds an
    /// already-empty queue and reports nothing.
    pub fn close_for_turn(&self) {
        let mut inner = self.gate.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        let dropped = std::mem::take(&mut inner.pending);
        inner.sealed_empty = true;
        if !dropped.is_empty() {
            if let Some(obs) = &self.observer {
                obs.on_turn_ended_dropped(&dropped);
            }
        }
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
    fn close_for_turn_discards_old_entries_and_requires_explicit_reopen() {
        let inbox = SteeringInbox::new();
        inbox.enqueue("old").unwrap();
        inbox.close_for_turn();
        assert!(!inbox.has_pending());
        assert!(inbox.enqueue("late").is_none());
        assert!(inbox.recall_all().is_empty());
        inbox.open_for_turn();
        inbox.enqueue("new").unwrap();
        let delivered = inbox.take_all_for_delivery();
        assert_eq!(delivered.len(), 1);
        assert_eq!(delivered[0].text, "new");
    }

    #[test]
    fn recall_and_delivery_have_exactly_one_winner() {
        for _ in 0..32 {
            let inbox = Arc::new(SteeringInbox::new());
            let expected = inbox.enqueue("edit or deliver once").unwrap().id;
            let barrier = Arc::new(std::sync::Barrier::new(2));
            let delivery = {
                let inbox = Arc::clone(&inbox);
                let barrier = Arc::clone(&barrier);
                std::thread::spawn(move || {
                    barrier.wait();
                    inbox.take_all_for_delivery()
                })
            };
            barrier.wait();
            let mut entries = inbox.recall_all();
            entries.extend(delivery.join().unwrap());
            assert_eq!(entries.len(), 1);
            assert_eq!(entries[0].id, expected);
            assert!(!inbox.has_pending());
            assert!(inbox.recall_all().is_empty());
        }
    }

    #[test]
    fn recall_does_not_reopen_a_sealed_inbox() {
        let inbox = SteeringInbox::new();
        assert!(inbox.try_seal_empty());
        assert!(inbox.recall_all().is_empty());
        assert!(inbox.enqueue("must not enqueue").is_none());
    }

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

    // ── Observer tests (Slice 0 / Stage C) ───────────────────────────────────

    #[derive(Default)]
    struct RecordingObserver {
        enqueued: Mutex<Vec<SteeringEntry>>,
        delivered: Mutex<Vec<Vec<SteeringEntry>>>,
        recalled: Mutex<Vec<Vec<SteeringEntry>>>,
        dropped: Mutex<Vec<Vec<SteeringEntry>>>,
        rejected: Mutex<Vec<SteerRejectReason>>,
    }

    impl SteeringObserver for RecordingObserver {
        fn on_enqueued(&self, entry: &SteeringEntry) {
            self.enqueued.lock().unwrap().push(entry.clone());
        }
        fn on_delivered(&self, entries: &[SteeringEntry]) {
            self.delivered.lock().unwrap().push(entries.to_vec());
        }
        fn on_recalled(&self, entries: &[SteeringEntry]) {
            self.recalled.lock().unwrap().push(entries.to_vec());
        }
        fn on_turn_ended_dropped(&self, entries: &[SteeringEntry]) {
            self.dropped.lock().unwrap().push(entries.to_vec());
        }
        fn on_rejected(&self, reason: SteerRejectReason) {
            self.rejected.lock().unwrap().push(reason);
        }
    }

    #[test]
    fn observer_sees_enqueue_delivery_recall_and_rejection() {
        let obs = Arc::new(RecordingObserver::default());
        let inbox = SteeringInbox::with_observer(Some(obs.clone() as Arc<dyn SteeringObserver>));

        let e1 = inbox.enqueue("first").unwrap();
        assert_eq!(obs.enqueued.lock().unwrap().len(), 1);
        assert_eq!(obs.enqueued.lock().unwrap()[0].id, e1.id);

        let e2 = inbox.enqueue("second").unwrap();
        let delivered = inbox.take_all_for_delivery();
        assert_eq!(delivered.len(), 2);
        assert_eq!(obs.delivered.lock().unwrap().len(), 1);
        assert_eq!(obs.delivered.lock().unwrap()[0].len(), 2);

        // Delivering again with nothing pending must not call the observer.
        assert!(inbox.take_all_for_delivery().is_empty());
        assert_eq!(obs.delivered.lock().unwrap().len(), 1, "empty delivery must not notify");

        let e3 = inbox.enqueue("third").unwrap();
        let recalled = inbox.recall_all();
        assert_eq!(recalled.len(), 1);
        assert_eq!(obs.recalled.lock().unwrap().len(), 1);
        assert_eq!(obs.recalled.lock().unwrap()[0][0].id, e3.id);

        // Empty text and a sealed inbox each report their own reason.
        assert!(inbox.enqueue("").is_none());
        assert_eq!(*obs.rejected.lock().unwrap(), vec![SteerRejectReason::EmptyText]);
        inbox.close_for_turn();
        assert!(inbox.enqueue("late").is_none());
        assert_eq!(
            obs.rejected.lock().unwrap().last().copied(),
            Some(SteerRejectReason::Sealed)
        );

        let _ = (e2,); // silence unused-var lint without weakening the assertions above
    }

    #[test]
    fn observer_reports_dropped_entries_exactly_once_across_seal_and_drop() {
        // Simulates the real dual-publish path: run_prompt_inner seals
        // explicitly, then TurnGuard::drop calls close_for_turn() again.
        let obs = Arc::new(RecordingObserver::default());
        let inbox = SteeringInbox::with_observer(Some(obs.clone() as Arc<dyn SteeringObserver>));
        inbox.open_for_turn();
        inbox.enqueue("never delivered").unwrap();

        inbox.close_for_turn(); // explicit seal — reports the drop
        inbox.close_for_turn(); // TurnGuard::drop — queue is already empty

        assert_eq!(obs.dropped.lock().unwrap().len(), 1, "must report exactly once");
        assert_eq!(obs.dropped.lock().unwrap()[0].len(), 1);
    }

    #[test]
    fn enqueue_with_reason_distinguishes_sealed_from_empty_text() {
        let inbox = SteeringInbox::new();
        assert_eq!(inbox.enqueue_with_reason("").err(), Some(SteerRejectReason::EmptyText));
        assert!(inbox.try_seal_empty());
        assert_eq!(inbox.enqueue_with_reason("hi").err(), Some(SteerRejectReason::Sealed));
    }
}
