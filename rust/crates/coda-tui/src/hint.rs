//! Priority queue of transient hint-line messages.
//!
//! The hint line sits above the composer and shows passing messages — things
//! worth saying once but not keeping in the transcript.  Two kinds compete:
//!
//! - **Transient** (e.g. "Copied 412 characters") — informational, 2.5 s.
//! - **Chord** (e.g. "Press Ctrl+C again to exit.") — actionable, tied to
//!   the chord window so it disappears exactly when the armed state expires.
//!
//! The chord hint overrides the transient one while both are active.  When all
//! entries age out the hint line falls back to scroll guidance (handled by the
//! draw layer, not here).
//!
//! All mutating and querying methods take an explicit `now: Instant` so the
//! unit tests can exercise expiry without sleeping.

use std::time::{Duration, Instant};

/// How long a transient copy / info hint stays visible.
pub const TRANSIENT_TTL: Duration = Duration::from_millis(2500);

/// Priority of a hint entry.  Higher value wins when both are active.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum HintPriority {
    /// A passing informational message, e.g. "Copied N characters".
    Transient = 0,
    /// An actionable chord affordance — the user is one keystroke away from a
    /// destructive action and needs to know it.
    Chord = 1,
}

struct HintEntry {
    text: String,
    expires_at: Instant,
    priority: HintPriority,
}

/// A priority queue of transient hint-line messages with per-entry expiry.
#[derive(Default)]
pub struct HintQueue {
    entries: Vec<HintEntry>,
}

impl std::fmt::Debug for HintQueue {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HintQueue")
            .field("entry_count", &self.entries.len())
            .finish()
    }
}

impl HintQueue {
    pub fn new() -> Self {
        Self { entries: Vec::new() }
    }

    /// Push a transient informational hint (e.g. "Copied N characters").
    ///
    /// Replaces any existing transient entry so only the newest matters.
    /// Takes explicit `now` for deterministic tests.
    pub fn push_transient(&mut self, text: impl Into<String>, now: Instant) {
        self.entries.retain(|e| e.priority != HintPriority::Transient);
        self.entries.push(HintEntry {
            text: text.into(),
            expires_at: now + TRANSIENT_TTL,
            priority: HintPriority::Transient,
        });
    }

    /// Push or refresh the chord affordance hint (e.g. "Press Ctrl+C again to exit.").
    ///
    /// At most one chord hint exists at a time; calling this again replaces the
    /// previous one.  `ttl` should equal `CHORD_WINDOW` so the hint disappears
    /// at the same moment the armed state expires.
    pub fn push_chord(&mut self, text: impl Into<String>, ttl: Duration, now: Instant) {
        self.entries.retain(|e| e.priority != HintPriority::Chord);
        self.entries.push(HintEntry {
            text: text.into(),
            expires_at: now + ttl,
            priority: HintPriority::Chord,
        });
    }

    /// Discard the chord hint immediately (chord fired or was abandoned by a
    /// different keystroke).
    pub fn clear_chord(&mut self) {
        self.entries.retain(|e| e.priority != HintPriority::Chord);
    }

    /// Returns the text of the highest-priority non-expired entry, or `None`
    /// when all entries have aged out or the queue is empty.
    pub fn current(&self, now: Instant) -> Option<&str> {
        self.entries
            .iter()
            .filter(|e| e.expires_at > now)
            .max_by_key(|e| e.priority)
            .map(|e| e.text.as_str())
    }

    pub fn current_chord(&self, now: Instant) -> Option<&str> {
        self.entries.iter()
            .find(|e| e.priority == HintPriority::Chord && e.expires_at > now)
            .map(|e| e.text.as_str())
    }

    /// Returns the earliest expiry instant among active entries, so callers
    /// can schedule a wakeup to redraw when the hint disappears.
    pub fn next_expiry(&self, now: Instant) -> Option<Instant> {
        self.entries
            .iter()
            .filter(|e| e.expires_at > now)
            .map(|e| e.expires_at)
            .min()
    }

    /// Drop entries that have already expired.  Keeps allocations bounded when
    /// the queue receives many pushes over a long session.
    pub fn prune(&mut self, now: Instant) -> bool {
        let before = self.entries.len();
        self.entries.retain(|e| e.expires_at > now);
        self.entries.len() != before
    }

    /// Whether any entry is currently active.
    pub fn is_active(&self, now: Instant) -> bool {
        self.entries.iter().any(|e| e.expires_at > now)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    fn t0() -> Instant {
        // A fixed base Instant — derived from now() once so tests are
        // deterministic relative to each other without sleeping.
        Instant::now()
    }

    #[test]
    fn empty_queue_returns_none() {
        let q = HintQueue::new();
        assert_eq!(q.current(t0()), None);
    }

    #[test]
    fn fresh_transient_is_returned() {
        let now = t0();
        let mut q = HintQueue::new();
        q.push_transient("Copied 42 characters to the clipboard.", now);
        assert_eq!(q.current(now), Some("Copied 42 characters to the clipboard."));
    }

    #[test]
    fn transient_expires_after_its_ttl() {
        let now = t0();
        let mut q = HintQueue::new();
        q.push_transient("copied", now);
        // Just before expiry: still visible.
        assert!(q.current(now + TRANSIENT_TTL - Duration::from_millis(1)).is_some());
        // At and after expiry: gone.
        assert_eq!(q.current(now + TRANSIENT_TTL), None);
        assert_eq!(q.current(now + TRANSIENT_TTL + Duration::from_secs(10)), None);
    }

    #[test]
    fn chord_hint_overrides_transient_hint() {
        let now = t0();
        let mut q = HintQueue::new();
        q.push_transient("Copied 99 characters to the clipboard.", now);
        q.push_chord("Press Ctrl+C again to exit.", Duration::from_millis(1500), now);
        assert_eq!(q.current(now), Some("Press Ctrl+C again to exit."));
    }

    #[test]
    fn transient_reappears_after_chord_expires() {
        let now = t0();
        let chord_ttl = Duration::from_millis(1500);
        let mut q = HintQueue::new();
        q.push_transient("Copied 7 characters to the clipboard.", now);
        q.push_chord("Press Esc again to stop the turn.", chord_ttl, now);

        // Chord is visible while active.
        assert_eq!(q.current(now), Some("Press Esc again to stop the turn."));

        // After chord expires the transient is still within its own TTL.
        let after_chord = now + chord_ttl + Duration::from_millis(1);
        assert!(
            after_chord < now + TRANSIENT_TTL,
            "transient should still be alive at this point"
        );
        assert_eq!(q.current(after_chord), Some("Copied 7 characters to the clipboard."));
    }

    #[test]
    fn clear_chord_removes_chord_immediately() {
        let now = t0();
        let mut q = HintQueue::new();
        q.push_transient("Copied 5 characters to the clipboard.", now);
        q.push_chord("Press Ctrl+C again to exit.", Duration::from_millis(1500), now);
        assert_eq!(q.current(now), Some("Press Ctrl+C again to exit."));

        q.clear_chord();
        assert_eq!(q.current(now), Some("Copied 5 characters to the clipboard."));
    }

    #[test]
    fn clear_chord_with_no_chord_is_safe() {
        let now = t0();
        let mut q = HintQueue::new();
        q.push_transient("hi", now);
        q.clear_chord(); // no chord to remove — must not panic
        assert_eq!(q.current(now), Some("hi"));
    }

    #[test]
    fn pushing_transient_again_replaces_old_one() {
        let now = t0();
        let mut q = HintQueue::new();
        q.push_transient("first copy", now);
        q.push_transient("second copy", now);
        assert_eq!(q.current(now), Some("second copy"));
        assert_eq!(q.entries.iter().filter(|e| e.priority == HintPriority::Transient).count(), 1);
    }

    #[test]
    fn pushing_chord_again_replaces_old_chord() {
        let now = t0();
        let mut q = HintQueue::new();
        q.push_chord("first chord", Duration::from_millis(1500), now);
        q.push_chord("second chord", Duration::from_millis(1500), now);
        assert_eq!(q.current(now), Some("second chord"));
        assert_eq!(q.entries.iter().filter(|e| e.priority == HintPriority::Chord).count(), 1);
    }

    #[test]
    fn next_expiry_returns_earliest_active_entry() {
        let now = t0();
        let chord_ttl = Duration::from_millis(1500);
        let mut q = HintQueue::new();
        q.push_transient("transient", now);
        q.push_chord("chord", chord_ttl, now);

        // Chord expires sooner (1500ms < 2500ms).
        let expiry = q.next_expiry(now).expect("should have an expiry");
        let expected = now + chord_ttl;
        assert_eq!(expiry, expected);
    }

    #[test]
    fn pruning_expired_hint_requests_an_idle_redraw_once() {
        let now = t0();
        let mut q = HintQueue::new();
        q.push_transient("copied", now);
        assert!(!q.prune(now));
        assert!(q.prune(now + TRANSIENT_TTL));
        assert!(!q.prune(now + TRANSIENT_TTL));
    }

    #[test]
    fn next_expiry_is_none_when_all_expired() {
        let now = t0();
        let mut q = HintQueue::new();
        q.push_transient("old", now);
        let later = now + TRANSIENT_TTL + Duration::from_secs(1);
        assert_eq!(q.next_expiry(later), None);
    }

    #[test]
    fn prune_drops_expired_entries() {
        let now = t0();
        let mut q = HintQueue::new();
        q.push_transient("goes away", now);
        assert_eq!(q.entries.len(), 1);
        q.prune(now + TRANSIENT_TTL + Duration::from_secs(1));
        assert_eq!(q.entries.len(), 0);
    }

    #[test]
    fn is_active_reflects_live_entries() {
        let now = t0();
        let mut q = HintQueue::new();
        assert!(!q.is_active(now));
        q.push_transient("hi", now);
        assert!(q.is_active(now));
        assert!(!q.is_active(now + TRANSIENT_TTL + Duration::from_secs(1)));
    }
}
