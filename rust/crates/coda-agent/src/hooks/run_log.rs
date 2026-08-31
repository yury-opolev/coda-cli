//! Per-hook execution audit log.
//!
//! [`HookRunLog`] records the outcome and duration of each hook run, keyed by
//! the hook's 0-based index in the configured list.  Thread-safe (Mutex-
//! protected).  Caller-cancelled runs are deliberately **not** recorded —
//! the `cancelled` outcome is excluded from the log so that external tooling
//! does not treat an in-flight cancel as a policy decision.
//!
//! Mirrors C# `HookRunLog.cs` / `HookRunEntry.cs`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

// ─────────────────────────────────────────────────────────────────────────────
// HookRunEntry
// ─────────────────────────────────────────────────────────────────────────────

/// A single recorded hook execution.
///
/// `outcome` is one of: `"allow"`, `"blocked"`, `"abort"`, `"timeout"`,
/// `"error"`, `"skipped"`.  `"cancelled"` is never stored — caller-cancelled
/// runs are excluded from the log.
#[derive(Debug, Clone)]
pub struct HookRunEntry {
    pub ran_at: DateTime<Utc>,
    pub outcome: String,
    pub duration_ms: i64,
}

// ─────────────────────────────────────────────────────────────────────────────
// HookRunLog
// ─────────────────────────────────────────────────────────────────────────────

/// Thread-safe audit log for hook executions.
#[derive(Debug, Default)]
pub struct HookRunLog {
    entries: Mutex<HashMap<usize, HookRunEntry>>,
}

impl HookRunLog {
    pub fn new() -> Self {
        Self::default()
    }

    /// Record a hook run.  The hook index is its 0-based position in the
    /// runner's configured hook list (matching C# reference-equality lookup).
    pub fn record(&self, hook_index: usize, entry: HookRunEntry) {
        self.entries.lock().unwrap().insert(hook_index, entry);
    }

    /// Return the most recent run entry for the hook at `hook_index`, or
    /// `None` if no run has been recorded for that index.
    pub fn get(&self, hook_index: usize) -> Option<HookRunEntry> {
        self.entries.lock().unwrap().get(&hook_index).cloned()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn get_returns_none_before_any_run() {
        let log = HookRunLog::new();
        assert!(log.get(0).is_none(), "no entry expected before first run");
        assert!(log.get(99).is_none(), "index 99 also none before any run");
    }

    #[test]
    fn record_then_get_returns_entry() {
        let log = HookRunLog::new();
        log.record(
            0,
            HookRunEntry { ran_at: Utc::now(), outcome: "allow".into(), duration_ms: 42 },
        );
        let entry = log.get(0).expect("entry must be present");
        assert_eq!(entry.outcome, "allow");
        assert!(entry.duration_ms >= 0);
    }

    #[test]
    fn record_overwrites_on_same_index() {
        let log = HookRunLog::new();
        log.record(
            0,
            HookRunEntry { ran_at: Utc::now(), outcome: "timeout".into(), duration_ms: 5000 },
        );
        log.record(
            0,
            HookRunEntry { ran_at: Utc::now(), outcome: "allow".into(), duration_ms: 100 },
        );
        let entry = log.get(0).unwrap();
        assert_eq!(entry.outcome, "allow", "latest record must win");
    }

    #[test]
    fn different_indices_are_independent() {
        let log = HookRunLog::new();
        log.record(
            0,
            HookRunEntry { ran_at: Utc::now(), outcome: "allow".into(), duration_ms: 1 },
        );
        log.record(
            1,
            HookRunEntry { ran_at: Utc::now(), outcome: "blocked".into(), duration_ms: 2 },
        );
        assert_eq!(log.get(0).unwrap().outcome, "allow");
        assert_eq!(log.get(1).unwrap().outcome, "blocked");
        assert!(log.get(2).is_none());
    }
}
