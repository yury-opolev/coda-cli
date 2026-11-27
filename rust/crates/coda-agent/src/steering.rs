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

/// What an inbox item is (non-blocking runtime, Phase 2). Every agent's inbox
/// carries more than steering: a subagent's inbox also receives the
/// completions of the tasks it started and its own deadline notice.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum InboxItemKind {
    /// A user (main) or parent (`task_send`) message. Delivered at the next
    /// boundary; it never cuts a running wait short.
    #[default]
    Message,
    /// A task this agent started has finished; the text is a framed data block.
    TaskCompleted,
    /// The agent's lifetime or parked-time limit was reached.
    Deadline,
}

impl InboxItemKind {
    /// Stable name used when an inbox is serialized (planned restart).
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Message => "message",
            Self::TaskCompleted => "taskCompleted",
            Self::Deadline => "deadline",
        }
    }

    /// Inverse of [`InboxItemKind::as_str`].
    pub fn parse(name: &str) -> Option<Self> {
        match name {
            "message" => Some(Self::Message),
            "taskCompleted" => Some(Self::TaskCompleted),
            "deadline" => Some(Self::Deadline),
            _ => None,
        }
    }
}

/// One image carried by an operator message.
///
/// `Debug` reports the media type and size only: image bytes never belong in
/// diagnostics.
#[derive(Clone, PartialEq, Eq)]
pub struct SteeringImage {
    pub media_type: String,
    pub base64: String,
}

impl SteeringImage {
    /// Size of the base64 payload; what the queue budget charges.
    pub fn byte_len(&self) -> usize {
        self.base64.len()
    }
}

impl std::fmt::Debug for SteeringImage {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SteeringImage")
            .field("media_type", &self.media_type)
            .field("base64_len", &self.base64.len())
            .finish()
    }
}

/// Aggregate base64 bytes of images that may wait in one inbox at a time.
///
/// Every pending image counts, including the one being offered; a message
/// that would exceed it is refused with [`SteerRejectReason::AttachmentCapacity`]
/// rather than queued partially.
pub const MAX_QUEUED_IMAGE_BYTES: usize = 32 * 1024 * 1024;

/// A single item queued for delivery.
///
/// `images` is an immutable snapshot taken at acceptance; runtime items
/// (`TaskCompleted`, `Deadline`) never carry any.
#[derive(Debug, Clone)]
pub struct SteeringEntry {
    pub id: String,
    pub text: String,
    pub kind: InboxItemKind,
    pub images: Arc<[SteeringImage]>,
}

impl SteeringEntry {
    fn image_bytes(&self) -> usize {
        self.images.iter().map(SteeringImage::byte_len).sum()
    }
}

/// Why an `enqueue` was refused. Distinguishes "no turn is running" (sealed)
/// from "nothing to send" (empty text) so `session/steer` can report an exact
/// `rejectedReason` instead of a mute `ok:false`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SteerRejectReason {
    /// The queue is sealed — no turn is currently accepting steering.
    Sealed,
    /// Neither text (beyond whitespace) nor images.
    EmptyText,
    /// Accepting the message's images would exceed [`MAX_QUEUED_IMAGE_BYTES`].
    AttachmentCapacity,
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
    /// Image-bearing messages were withdrawn instead of delivered because the
    /// active model cannot receive images (never called with an empty slice).
    /// They are kept for explicit recovery; their text is never delivered
    /// alone.
    fn on_undeliverable(&self, _entries: &[SteeringEntry]) {}
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
    /// Signalled whenever an item is accepted, so a parked agent wakes up.
    arrived: tokio::sync::Notify,
    /// Whether the model this inbox delivers to receives image blocks. Set by
    /// the agent at the start of each run; `true` until told otherwise.
    accepts_images: std::sync::atomic::AtomicBool,
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
            arrived: tokio::sync::Notify::new(),
            accepts_images: std::sync::atomic::AtomicBool::new(true),
        }
    }

    /// Records whether delivery may hand images to the model. When it may
    /// not, image-bearing messages are withdrawn at delivery (see
    /// [`SteeringObserver::on_undeliverable`]) rather than sent as text alone.
    pub fn set_accepts_images(&self, accepts: bool) {
        self.accepts_images.store(accepts, std::sync::atomic::Ordering::SeqCst);
    }

    /// Resolves once any undelivered item (message, completion, deadline) is
    /// waiting. Used by a parked agent. A running tool's wait never listens
    /// here: only an explicit "background now" ends it early.
    pub async fn wait_for_any(&self) {
        loop {
            let notified = self.arrived.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();
            if self.has_any() {
                return;
            }
            notified.await;
        }
    }

    /// Returns `true` when undelivered user/parent messages are waiting.
    pub fn has_pending(&self) -> bool {
        self.gate
            .lock()
            .unwrap()
            .pending
            .iter()
            .any(|e| e.kind == InboxItemKind::Message)
    }

    /// Returns `true` when any undelivered item is waiting.
    pub fn has_any(&self) -> bool {
        !self.gate.lock().unwrap().pending.is_empty()
    }

    /// Queues a runtime item (a task completion or a deadline notice).
    ///
    /// Unlike a message, it is accepted even when the inbox is sealed: the
    /// seal protects a user's words from being dropped after the last
    /// delivery boundary, while a runtime item is re-checked by the owner
    /// after sealing (see the parked state in `agent::park`). The observer
    /// is not told: it mirrors operator steering only.
    pub fn push_item(&self, kind: InboxItemKind, text: impl Into<String>) -> SteeringEntry {
        self.push_item_with_images(kind, text, Vec::new())
    }

    /// [`Self::push_item`] for an item restored with its images (a planned
    /// restart re-queues a suspended agent's inbox exactly as it was).
    pub fn push_item_with_images(
        &self,
        kind: InboxItemKind,
        text: impl Into<String>,
        images: Vec<SteeringImage>,
    ) -> SteeringEntry {
        let entry = SteeringEntry {
            id: uuid::Uuid::new_v4().to_string().replace('-', ""),
            text: text.into(),
            kind,
            images: images.into(),
        };
        self.gate.lock().unwrap().pending.push(entry.clone());
        self.arrived.notify_waiters();
        entry
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
        self.enqueue_message(text, Vec::new())
    }

    /// Enqueue an operator message with its images as one indivisible item.
    ///
    /// Refused only when there is neither text nor an image, when the inbox
    /// is sealed, or when the images would exceed the aggregate budget
    /// ([`MAX_QUEUED_IMAGE_BYTES`]); a refusal queues nothing at all.
    pub fn enqueue_message(
        &self,
        text: impl Into<String>,
        images: Vec<SteeringImage>,
    ) -> Result<SteeringEntry, SteerRejectReason> {
        let text = text.into();
        if text.trim().is_empty() && images.is_empty() {
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
        let offered: usize = images.iter().map(SteeringImage::byte_len).sum();
        if offered > 0 {
            let queued: usize = inner.pending.iter().map(SteeringEntry::image_bytes).sum();
            if queued.saturating_add(offered) > MAX_QUEUED_IMAGE_BYTES {
                if let Some(obs) = &self.observer {
                    obs.on_rejected(SteerRejectReason::AttachmentCapacity);
                }
                return Err(SteerRejectReason::AttachmentCapacity);
            }
        }
        let entry = SteeringEntry {
            id: uuid::Uuid::new_v4().to_string().replace('-', ""),
            text,
            kind: InboxItemKind::Message,
            images: images.into(),
        };
        inner.pending.push(entry.clone());
        // Observer runs while `inner` is still held (INBOX -> STATE -> BUS).
        if let Some(obs) = &self.observer {
            obs.on_enqueued(&entry);
        }
        drop(inner);
        self.arrived.notify_waiters();
        Ok(entry)
    }

    /// Every undelivered item, oldest first, without removing anything
    /// (a planned restart copies the inbox of a suspended agent).
    pub fn snapshot_items(&self) -> Vec<SteeringEntry> {
        self.gate.lock().unwrap().pending.clone()
    }

    /// Atomically drain all pending entries for delivery.
    ///
    /// When the model cannot receive images, image-bearing messages are
    /// withdrawn in the same step and reported via
    /// [`SteeringObserver::on_undeliverable`] instead of being delivered.
    pub fn take_all_for_delivery(&self) -> Vec<SteeringEntry> {
        let mut inner = self.gate.lock().unwrap();
        if inner.pending.is_empty() {
            return Vec::new();
        }
        let mut entries = std::mem::take(&mut inner.pending);
        if !self.accepts_images.load(std::sync::atomic::Ordering::SeqCst) {
            let (withheld, deliverable): (Vec<_>, Vec<_>) = entries
                .into_iter()
                .partition(|e| e.kind == InboxItemKind::Message && !e.images.is_empty());
            entries = deliverable;
            if !withheld.is_empty() {
                if let Some(obs) = &self.observer {
                    obs.on_undeliverable(&withheld);
                }
            }
            if entries.is_empty() {
                return entries;
            }
        }
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

/// Content blocks for a run of delivered entries that carries images: per
/// entry in arrival order, its text and then its own images, so each image
/// stays beside the words that explain it. `prefix` (the live-task brief)
/// opens the first entry's text.
pub(crate) fn steering_blocks(entries: &[SteeringEntry], prefix: Option<String>) -> Vec<coda_llm::Content> {
    let mut prefix = prefix;
    let mut blocks = Vec::new();
    for entry in entries {
        let text = match prefix.take() {
            Some(prefix) if entry.text.is_empty() => prefix,
            Some(prefix) => format!("{prefix}\n\n{}", entry.text),
            None => entry.text.clone(),
        };
        if !text.is_empty() {
            blocks.push(coda_llm::Content::Text(text));
        }
        blocks.extend(entry.images.iter().map(|image| coda_llm::Content::Image {
            media_type: image.media_type.clone(),
            base64: image.base64.clone(),
        }));
    }
    blocks
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
        undeliverable: Mutex<Vec<Vec<SteeringEntry>>>,
    }

    impl SteeringObserver for RecordingObserver {
        fn on_undeliverable(&self, entries: &[SteeringEntry]) {
            self.undeliverable.lock().unwrap().push(entries.to_vec());
        }
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

    // ── Typed runtime items (non-blocking runtime, Phase 2) ──────────────────

    #[test]
    fn a_completion_is_accepted_when_sealed_but_is_not_a_pending_message() {
        let inbox = SteeringInbox::new();
        assert!(inbox.try_seal_empty());
        inbox.push_item(InboxItemKind::TaskCompleted, "[task-completed] task-0002");
        assert!(inbox.has_any());
        assert!(!inbox.has_pending(), "a completion is not a pending message");
        assert!(!inbox.try_seal_empty(), "an undelivered completion keeps the agent going");
        let items = inbox.take_all_for_delivery();
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].kind, InboxItemKind::TaskCompleted);
    }

    #[test]
    fn snapshot_items_copies_every_pending_item_without_draining() {
        let inbox = SteeringInbox::new();
        inbox.enqueue("check the logs").unwrap();
        inbox.push_item(InboxItemKind::TaskCompleted, "[task-completed] task-00000002");
        let items: Vec<_> = inbox.snapshot_items().into_iter().map(|e| (e.kind, e.text)).collect();
        assert_eq!(
            items,
            vec![
                (InboxItemKind::Message, "check the logs".to_owned()),
                (InboxItemKind::TaskCompleted, "[task-completed] task-00000002".to_owned()),
            ]
        );
        assert_eq!(inbox.take_all_for_delivery().len(), 2, "nothing was removed");
        for kind in [InboxItemKind::Message, InboxItemKind::TaskCompleted, InboxItemKind::Deadline] {
            assert_eq!(InboxItemKind::parse(kind.as_str()), Some(kind));
        }
    }

    #[tokio::test]
    async fn wait_for_any_wakes_on_a_completion() {
        let inbox = Arc::new(SteeringInbox::new());
        let waiter = {
            let inbox = Arc::clone(&inbox);
            tokio::spawn(async move { inbox.wait_for_any().await })
        };
        tokio::task::yield_now().await;
        inbox.push_item(InboxItemKind::TaskCompleted, "done");
        tokio::time::timeout(std::time::Duration::from_secs(1), waiter)
            .await
            .expect("wait_for_any must wake")
            .unwrap();
    }

    #[test]
    fn enqueue_with_reason_distinguishes_sealed_from_empty_text() {
        let inbox = SteeringInbox::new();
        assert_eq!(inbox.enqueue_with_reason("").err(), Some(SteerRejectReason::EmptyText));
        assert!(inbox.try_seal_empty());
        assert_eq!(inbox.enqueue_with_reason("hi").err(), Some(SteerRejectReason::Sealed));
    }

    // ── Image steering ───────────────────────────────────────────────────────

    fn image(media_type: &str, base64: &str) -> SteeringImage {
        SteeringImage { media_type: media_type.into(), base64: base64.into() }
    }

    #[test]
    fn an_image_only_message_is_accepted_but_nothing_at_all_is_not() {
        let inbox = SteeringInbox::new();
        let entry = inbox.enqueue_message("", vec![image("image/png", "AAAA")]).unwrap();
        assert_eq!(entry.text, "");
        assert_eq!(entry.images.len(), 1);
        assert_eq!(inbox.enqueue_message("  ", Vec::new()).err(), Some(SteerRejectReason::EmptyText));
    }

    #[test]
    fn fifo_order_and_each_messages_image_order_survive_delivery() {
        let inbox = SteeringInbox::new();
        inbox.enqueue_message("one", vec![image("image/png", "AAA1"), image("image/gif", "AAA2")]).unwrap();
        inbox.enqueue("two").unwrap();
        inbox.enqueue_message("three", vec![image("image/jpeg", "AAA3")]).unwrap();
        let delivered = inbox.take_all_for_delivery();
        let texts: Vec<_> = delivered.iter().map(|e| e.text.as_str()).collect();
        assert_eq!(texts, ["one", "two", "three"]);
        let payloads: Vec<Vec<&str>> = delivered
            .iter()
            .map(|e| e.images.iter().map(|i| i.base64.as_str()).collect())
            .collect();
        assert_eq!(payloads, vec![vec!["AAA1", "AAA2"], vec![], vec!["AAA3"]]);
    }

    #[test]
    fn the_aggregate_image_budget_refuses_explicitly_and_frees_on_removal() {
        let obs = Arc::new(RecordingObserver::default());
        let inbox = SteeringInbox::with_observer(Some(obs.clone() as Arc<dyn SteeringObserver>));
        let half = "A".repeat(MAX_QUEUED_IMAGE_BYTES / 2);
        inbox.enqueue_message("a", vec![image("image/png", &half)]).unwrap();
        inbox.enqueue_message("b", vec![image("image/png", &half)]).unwrap();
        assert_eq!(
            inbox.enqueue_message("c", vec![image("image/png", "AAAA")]).err(),
            Some(SteerRejectReason::AttachmentCapacity)
        );
        assert_eq!(obs.rejected.lock().unwrap().last().copied(), Some(SteerRejectReason::AttachmentCapacity));
        assert!(inbox.enqueue("text still fits").is_some(), "text is not charged against the image budget");
        assert_eq!(inbox.recall_all().len(), 3);
        assert!(inbox.enqueue_message("c", vec![image("image/png", "AAAA")]).is_ok(), "recall frees capacity");
    }

    #[test]
    fn a_model_that_cannot_take_images_never_gets_their_text_alone() {
        let obs = Arc::new(RecordingObserver::default());
        let inbox = SteeringInbox::with_observer(Some(obs.clone() as Arc<dyn SteeringObserver>));
        inbox.enqueue_message("see screenshot", vec![image("image/png", "AAAA")]).unwrap();
        inbox.enqueue("plain").unwrap();
        inbox.set_accepts_images(false);
        let delivered = inbox.take_all_for_delivery();
        assert_eq!(delivered.len(), 1);
        assert_eq!(delivered[0].text, "plain");
        let withheld = obs.undeliverable.lock().unwrap().clone();
        assert_eq!(withheld.len(), 1);
        assert_eq!(withheld[0][0].text, "see screenshot");
        assert!(!inbox.has_any(), "the withheld message left the queue for explicit recovery");
    }

    #[test]
    fn recall_and_delivery_with_images_are_mutually_exclusive() {
        for _ in 0..50 {
            let inbox = Arc::new(SteeringInbox::new());
            let id = inbox.enqueue_message("x", vec![image("image/png", "AAAA")]).unwrap().id;
            let other = Arc::clone(&inbox);
            let deliver = std::thread::spawn(move || other.take_all_for_delivery());
            let recalled = inbox.recall_all();
            let delivered = deliver.join().unwrap();
            let seen: Vec<_> = recalled.iter().chain(delivered.iter()).collect();
            assert_eq!(seen.len(), 1);
            assert_eq!(seen[0].id, id);
            assert_eq!(seen[0].images.len(), 1, "the images travel with whichever side won");
        }
    }

    #[test]
    fn debug_output_never_contains_image_bytes() {
        let inbox = SteeringInbox::new();
        let entry = inbox.enqueue_message("t", vec![image("image/png", "SECRETPAYLOAD")]).unwrap();
        let printed = format!("{entry:?}");
        assert!(!printed.contains("SECRETPAYLOAD"), "{printed}");
        assert!(printed.contains("image/png"), "{printed}");
    }

    #[test]
    fn runtime_items_never_carry_images_and_snapshots_keep_them() {
        let inbox = SteeringInbox::new();
        inbox.enqueue_message("m", vec![image("image/png", "AAAA")]).unwrap();
        let item = inbox.push_item(InboxItemKind::TaskCompleted, "done");
        assert!(item.images.is_empty());
        let snapshot = inbox.snapshot_items();
        assert_eq!(snapshot[0].images.len(), 1);
        assert!(snapshot[1].images.is_empty());
    }
}
