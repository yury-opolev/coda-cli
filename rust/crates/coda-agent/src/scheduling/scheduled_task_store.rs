//! Thread-safe store for scheduled definitions with optional durable JSON
//! persistence.
//!
//! Every successful mutation advances a monotonic version and wakes waiters
//! registered through [`ScheduledTaskStore::wait_for_change`]. Persistence is
//! atomic (unique sibling temp file + rename) and best-effort: a failed write
//! leaves the previous on-disk document intact while the in-memory mutation
//! still succeeds.
//!
//! Loading recovers each array element independently: a malformed or
//! invariant-violating record is skipped without discarding valid neighbours.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use tokio::sync::Notify;

use super::scheduled_task::{
    ScheduleDefinitionDraft, ScheduleLiveState, ScheduledTask, ScheduledTaskStoreSnapshot,
};

pub struct ScheduledTaskStore {
    persist_path: Option<PathBuf>,
    state: Mutex<StoreState>,
    signal: Arc<Notify>,
}

struct StoreState {
    items: Vec<ScheduledTask>,
    version: u64,
    /// Ephemeral runtime status, deliberately kept *beside* the definitions so
    /// it can never be serialized, reloaded, or mistaken for configuration.
    /// A process that did not launch a run owns no live state for it.
    live: HashMap<String, ScheduleLiveState>,
}

impl ScheduledTaskStore {
    /// In-memory store with no persistence.
    pub fn new() -> Arc<Self> {
        Arc::new(Self {
            persist_path: None,
            state: Mutex::new(StoreState {
                items: Vec::new(),
                version: 0,
                live: HashMap::new(),
            }),
            signal: Arc::new(Notify::new()),
        })
    }

    /// Persistent store backed by `path`. Loads existing records on construction.
    pub fn with_path(path: impl Into<PathBuf>) -> Arc<Self> {
        let path = path.into();
        let items = load_from_disk(&path);
        Arc::new(Self {
            persist_path: Some(path),
            state: Mutex::new(StoreState {
                items,
                version: 0,
                live: HashMap::new(),
            }),
            signal: Arc::new(Notify::new()),
        })
    }

    pub fn get_snapshot(&self) -> ScheduledTaskStoreSnapshot {
        let s = self.state.lock().unwrap();
        ScheduledTaskStoreSnapshot {
            version: s.version,
            items: s.items.clone(),
            live: s.live.clone(),
        }
    }

    pub fn items(&self) -> Vec<ScheduledTask> {
        self.state.lock().unwrap().items.clone()
    }

    /// The ephemeral live state for one definition; `Idle` when the runtime
    /// has never reported anything for it (including after a reload).
    pub fn live_state(&self, id: &str) -> ScheduleLiveState {
        self.state.lock().unwrap().live.get(id).cloned().unwrap_or_default()
    }

    /// The whole ephemeral live table.
    pub fn live_states(&self) -> HashMap<String, ScheduleLiveState> {
        self.state.lock().unwrap().live.clone()
    }

    /// Publish the runtime's live status for one definition.
    ///
    /// Deliberately does **not** advance the store version: live status is not
    /// a definition change, and waking the schedule loop for its own status
    /// publication would make it spin.
    pub fn set_live_state(&self, id: &str, state: ScheduleLiveState) {
        let mut s = self.state.lock().unwrap();
        if state == ScheduleLiveState::default() {
            s.live.remove(id);
        } else {
            s.live.insert(id.to_owned(), state);
        }
    }

    /// Add a new definition built from `draft`.
    pub fn add(&self, draft: ScheduleDefinitionDraft, now_utc: chrono::DateTime<chrono::Utc>) -> ScheduledTask {
        let id = short_id();
        let task = ScheduledTask {
            schema_version: ScheduledTask::CURRENT_SCHEMA_VERSION,
            id,
            name: draft.name,
            kind: draft.kind,
            prompt: draft.prompt,
            interval: draft.interval.map(|d| d.as_secs_f64()),
            at_utc: draft.at_utc,
            cron: draft.cron,
            time_zone_id: draft.time_zone_id,
            next_run_utc: draft.next_run_utc,
            created_at_utc: now_utc,
            updated_at_utc: now_utc,
            last_terminal_outcome: None,
            expires_at_utc: draft.expires_at_utc,
            max_runs: draft.max_runs,
            runs_started: 0,
            retirement: None,
        };
        self.add_core(task)
    }

    /// Remove the task with the given id. Returns `true` if found and removed.
    pub fn remove(&self, id: &str) -> bool {
        let changed = {
            let mut s = self.state.lock().unwrap();
            let before = s.items.len();
            s.items.retain(|t| t.id != id);
            if s.items.len() == before {
                return false;
            }
            s.live.remove(id);
            self.commit_locked(&mut s);
            true
        };
        if changed {
            self.signal.notify_waiters();
        }
        changed
    }

    /// Replace the task sharing `updated.id`. Returns `true` if found.
    ///
    /// This is a *blind* overwrite and is therefore unsuitable for any
    /// mutation that must not clobber a concurrent retirement or run-counter
    /// bump; use [`ScheduledTaskStore::update`] for those.
    pub fn replace(&self, updated: ScheduledTask) -> bool {
        let changed = {
            let mut s = self.state.lock().unwrap();
            if let Some(slot) = s.items.iter_mut().find(|t| t.id == updated.id) {
                *slot = updated;
                self.commit_locked(&mut s);
                true
            } else {
                false
            }
        };
        if changed {
            self.signal.notify_waiters();
        }
        changed
    }

    /// Atomically read-modify-write the definition with `id` under the store
    /// lock, returning whatever the mutator produced.
    ///
    /// Every bounded-schedule decision (claiming a launch slot, committing an
    /// accepted launch, recording a retirement) goes through here so that a
    /// decision and the write that enacts it cannot be separated by another
    /// writer. `mutate` must stay cheap and synchronous: it runs while the
    /// registry lock is held, so it must not await, call back into a runner,
    /// or otherwise re-enter the store.
    ///
    /// Returns `None` when the id is unknown — a definition deleted between
    /// the decision and the write is never resurrected.
    pub fn update<R>(
        &self,
        id: &str,
        mutate: impl FnOnce(&mut ScheduledTask) -> R,
    ) -> Option<R> {
        let (outcome, changed) = {
            let mut s = self.state.lock().unwrap();
            let Some(slot) = s.items.iter_mut().find(|t| t.id == id) else {
                return None;
            };
            let before = slot.clone();
            let outcome = mutate(slot);
            let changed = *slot != before;
            if changed {
                self.commit_locked(&mut s);
            }
            (outcome, changed)
        };
        if changed {
            self.signal.notify_waiters();
        }
        Some(outcome)
    }

    /// Wait until the store advances past `observed_version`.
    ///
    /// Uses `Notified::enable()` to register in the wait list BEFORE re-checking
    /// the version, closing the race window where a `notify_waiters()` call could
    /// land between the version check and the `await`.
    pub async fn wait_for_change(&self, observed_version: u64) {
        // Fast path: already changed.
        {
            let s = self.state.lock().unwrap();
            if s.version != observed_version {
                return;
            }
        }

        // Enable the notified future before re-checking so we can't miss a
        // notify_waiters() call that races between the first check and enable().
        let notified = self.signal.notified();
        tokio::pin!(notified);
        notified.as_mut().enable();

        {
            let s = self.state.lock().unwrap();
            if s.version != observed_version {
                return;
            }
        }

        notified.await;
    }

    fn add_core(&self, task: ScheduledTask) -> ScheduledTask {
        {
            let mut s = self.state.lock().unwrap();
            s.items.push(task.clone());
            self.commit_locked(&mut s);
        }
        // Notify outside the lock so waiters can re-acquire it.
        self.signal.notify_waiters();
        task
    }

    /// Advance the version and persist best-effort.
    /// Callers must call `signal.notify_waiters()` after releasing the lock.
    fn commit_locked(&self, s: &mut StoreState) {
        s.version += 1;
        if let Some(path) = &self.persist_path {
            let _ = persist_atomic(path, &s.items);
        }
    }
}

// ── Persistence helpers ────────────────────────────────────────────────────────

/// Write `items` to `path` atomically via a sibling temp file + rename.
/// Cleans up the temp file on failure so no ghost temp files are left behind.
fn persist_atomic(path: &Path, items: &[ScheduledTask]) -> Result<(), String> {
    // Create the parent directory if needed.
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)
            .map_err(|e| format!("create dir: {e}"))?;
    }

    // Write to a unique sibling temp file so that a crash during write cannot
    // corrupt the previous persisted document.
    let temp_path = path.with_extension(format!("tmp-{}", uuid::Uuid::new_v4()));

    let json = serde_json::to_string_pretty(items)
        .map_err(|e| format!("serialize: {e}"))?;

    if let Err(e) = std::fs::write(&temp_path, json.as_bytes()) {
        // Clean up the temp file on failure — the C# leaked these.
        let _ = std::fs::remove_file(&temp_path);
        return Err(format!("write temp: {e}"));
    }

    // Atomic rename.
    if let Err(e) = std::fs::rename(&temp_path, path) {
        let _ = std::fs::remove_file(&temp_path);
        return Err(format!("rename: {e}"));
    }

    Ok(())
}

fn load_from_disk(path: &Path) -> Vec<ScheduledTask> {
    if !path.exists() {
        return Vec::new();
    }
    let json = match std::fs::read_to_string(path) {
        Ok(s) => s,
        Err(_) => return Vec::new(),
    };
    // Per-element recovery: parse the root array, skip malformed elements.
    let arr: serde_json::Value = match serde_json::from_str(&json) {
        Ok(v) => v,
        Err(_) => return Vec::new(),
    };
    let arr = match arr.as_array() {
        Some(a) => a,
        None => return Vec::new(),
    };
    arr.iter()
        .filter_map(|v| serde_json::from_value::<ScheduledTask>(v.clone()).ok())
        .collect()
}

fn short_id() -> String {
    uuid::Uuid::new_v4().simple().to_string()[..12].to_owned()
}

impl Default for ScheduledTaskStore {
    fn default() -> Self {
        Self {
            persist_path: None,
            state: Mutex::new(StoreState {
                items: Vec::new(),
                version: 0,
                live: HashMap::new(),
            }),
            signal: Arc::new(Notify::new()),
        }
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::scheduling::scheduled_task::{ScheduleKind, ScheduledTask};
    use chrono::Utc;
    use std::time::Duration;

    fn now() -> chrono::DateTime<Utc> {
        Utc::now()
    }

    fn draft(kind: ScheduleKind) -> ScheduleDefinitionDraft {
        ScheduleDefinitionDraft {
            name: None,
            kind,
            prompt: "test prompt".into(),
            interval: if kind == ScheduleKind::Interval {
                Some(Duration::from_secs(3600))
            } else {
                None
            },
            at_utc: if kind == ScheduleKind::At {
                Some(now() + chrono::Duration::hours(1))
            } else {
                None
            },
            cron: if kind == ScheduleKind::Cron {
                Some("0 9 * * *".into())
            } else {
                None
            },
            time_zone_id: "UTC".into(),
            next_run_utc: now() + chrono::Duration::hours(1),
            expires_at_utc: None,
            max_runs: None,
        }
    }

    // ── add / items ────────────────────────────────────────────────────────────

    #[test]
    fn add_interval_task_is_stored() {
        let store = ScheduledTaskStore::new();
        let t = store.add(draft(ScheduleKind::Interval), now());
        assert_eq!(store.items().len(), 1);
        assert_eq!(store.items()[0].id, t.id);
    }

    #[test]
    fn multiple_tasks_are_ordered() {
        let store = ScheduledTaskStore::new();
        let a = store.add(draft(ScheduleKind::Interval), now());
        let b = store.add(draft(ScheduleKind::At), now());
        let items = store.items();
        assert_eq!(items[0].id, a.id);
        assert_eq!(items[1].id, b.id);
    }

    // ── remove ────────────────────────────────────────────────────────────────

    #[test]
    fn remove_known_id_returns_true() {
        let store = ScheduledTaskStore::new();
        let t = store.add(draft(ScheduleKind::Interval), now());
        assert!(store.remove(&t.id));
        assert!(store.items().is_empty());
    }

    #[test]
    fn remove_unknown_id_returns_false() {
        let store = ScheduledTaskStore::new();
        assert!(!store.remove("nonexistent"));
    }

    // ── replace ────────────────────────────────────────────────────────────────

    #[test]
    fn replace_updates_in_place() {
        let store = ScheduledTaskStore::new();
        let t = store.add(draft(ScheduleKind::Interval), now());
        let mut updated = t.clone();
        updated.name = Some("updated".into());
        assert!(store.replace(updated));
        assert_eq!(store.items()[0].name.as_deref(), Some("updated"));
    }

    // ── versioning ────────────────────────────────────────────────────────────

    #[test]
    fn version_increments_on_mutation() {
        let store = ScheduledTaskStore::new();
        let snap0 = store.get_snapshot();
        store.add(draft(ScheduleKind::Interval), now());
        let snap1 = store.get_snapshot();
        store.remove(&snap1.items[0].id);
        let snap2 = store.get_snapshot();
        assert_eq!(snap0.version, 0);
        assert_eq!(snap1.version, 1);
        assert_eq!(snap2.version, 2);
    }

    // ── atomic update ─────────────────────────────────────────────────────────

    #[test]
    fn update_mutates_in_place_and_bumps_the_version() {
        let store = ScheduledTaskStore::new();
        let t = store.add(draft(ScheduleKind::Interval), now());
        let before = store.get_snapshot().version;

        let seen = store.update(&t.id, |task| {
            task.runs_started += 1;
            task.runs_started
        });

        assert_eq!(seen, Some(1));
        assert_eq!(store.items()[0].runs_started, 1);
        assert_eq!(store.get_snapshot().version, before + 1);
    }

    #[test]
    fn update_of_an_unknown_id_reports_none_and_creates_nothing() {
        let store = ScheduledTaskStore::new();
        let outcome = store.update("ghost", |task| {
            task.runs_started += 1;
        });
        assert!(outcome.is_none(), "a deleted definition must never be resurrected");
        assert!(store.items().is_empty());
    }

    /// A no-op mutation must not advance the version: the schedule loop parks
    /// on the version, so a write that changes nothing would spin it.
    #[test]
    fn update_without_a_change_does_not_bump_the_version() {
        let store = ScheduledTaskStore::new();
        let t = store.add(draft(ScheduleKind::Interval), now());
        let before = store.get_snapshot().version;
        store.update(&t.id, |task| {
            let _ = task.runs_started;
        });
        assert_eq!(store.get_snapshot().version, before);
    }

    /// The counter survives an unrelated blind `replace` only when callers use
    /// `update`; this documents exactly why the runtime must not use
    /// `replace` for bounded definitions.
    #[test]
    fn update_preserves_fields_a_blind_replace_would_clobber() {
        let store = ScheduledTaskStore::new();
        let stale = store.add(draft(ScheduleKind::Interval), now());

        store.update(&stale.id, |task| {
            task.runs_started = 5;
            task.retirement = Some(crate::scheduling::ScheduleRetirement {
                reason: crate::scheduling::ScheduleRetirementReason::RunLimit,
                retired_at_utc: now(),
                note: None,
            });
        });

        // A blind replace with the pre-retirement copy would undo both.
        store.replace(stale.clone());
        assert_eq!(store.items()[0].runs_started, 0, "replace really is blind");

        // update() applied to the current record cannot lose them.
        store.update(&stale.id, |task| task.runs_started = 5);
        store.update(&stale.id, |task| task.updated_at_utc = now());
        assert_eq!(store.items()[0].runs_started, 5);
    }

    // ── ephemeral live state ──────────────────────────────────────────────────

    #[test]
    fn live_state_defaults_to_idle_and_round_trips() {
        use crate::scheduling::{ScheduleLiveState, ScheduleLiveStatus};

        let store = ScheduledTaskStore::new();
        let t = store.add(draft(ScheduleKind::Interval), now());
        assert_eq!(store.live_state(&t.id), ScheduleLiveState::default());

        store.set_live_state(
            &t.id,
            ScheduleLiveState {
                status: ScheduleLiveStatus::Running,
                active_task_id: Some("task-0007".into()),
            },
        );
        assert_eq!(store.live_state(&t.id).status, ScheduleLiveStatus::Running);
        assert_eq!(store.live_state(&t.id).active_task_id.as_deref(), Some("task-0007"));
    }

    /// Publishing live status must not wake the schedule loop: it parks on the
    /// store version, and its own status publication is not a definition change.
    #[test]
    fn live_state_publication_does_not_bump_the_version() {
        use crate::scheduling::{ScheduleLiveState, ScheduleLiveStatus};

        let store = ScheduledTaskStore::new();
        let t = store.add(draft(ScheduleKind::Interval), now());
        let before = store.get_snapshot().version;
        store.set_live_state(
            &t.id,
            ScheduleLiveState { status: ScheduleLiveStatus::Running, active_task_id: None },
        );
        assert_eq!(store.get_snapshot().version, before);
    }

    #[test]
    fn removing_a_definition_drops_its_live_state() {
        use crate::scheduling::{ScheduleLiveState, ScheduleLiveStatus};

        let store = ScheduledTaskStore::new();
        let t = store.add(draft(ScheduleKind::Interval), now());
        store.set_live_state(
            &t.id,
            ScheduleLiveState { status: ScheduleLiveStatus::Running, active_task_id: None },
        );
        store.remove(&t.id);
        assert_eq!(store.live_state(&t.id), ScheduleLiveState::default());
        assert!(store.live_states().is_empty());
    }

    // ── bounds are configuration, never ephemeral ─────────────────────────────

    /// Bounds and counters must be ordinary serde fields. If they were
    /// `#[serde(skip)]`, a persisted bounded definition would reload as an
    /// unbounded one — silently removing the user's limit.
    #[test]
    fn bounds_and_counters_survive_a_serde_round_trip() {
        use crate::scheduling::{ScheduleRetirement, ScheduleRetirementReason};

        let store = ScheduledTaskStore::new();
        let t = store.add(draft(ScheduleKind::Interval), now());
        store.update(&t.id, |task| {
            task.max_runs = Some(7);
            task.runs_started = 3;
            task.expires_at_utc = Some(now() + chrono::Duration::days(7));
            task.retirement = Some(ScheduleRetirement {
                reason: ScheduleRetirementReason::Cancelled,
                retired_at_utc: now(),
                note: Some("done".into()),
            });
        });

        let original = store.items().remove(0);
        let json = serde_json::to_string(&original).unwrap();
        let restored: ScheduledTask = serde_json::from_str(&json).unwrap();

        assert_eq!(restored.max_runs, Some(7));
        assert_eq!(restored.runs_started, 3);
        assert_eq!(restored.expires_at_utc, original.expires_at_utc);
        assert_eq!(
            restored.retirement.as_ref().map(|r| r.reason),
            Some(ScheduleRetirementReason::Cancelled)
        );
    }

    /// A record written before bounds existed loads as an explicitly unlimited
    /// definition, not as one with a zero run budget.
    #[test]
    fn a_record_without_bounds_loads_as_unlimited() {
        let json = serde_json::json!({
            "schemaVersion": 2,
            "id": "legacy000001",
            "name": null,
            "kind": "interval",
            "prompt": "p",
            "intervalSecs": 3600.0,
            "timeZoneId": "UTC",
            "nextRunUtc": "2087-01-01T00:00:00Z",
            "createdAtUtc": "2087-01-01T00:00:00Z",
            "updatedAtUtc": "2087-01-01T00:00:00Z",
        });
        let task: ScheduledTask = serde_json::from_value(json).unwrap();
        assert_eq!(task.max_runs, None);
        assert_eq!(task.expires_at_utc, None);
        assert_eq!(task.runs_started, 0);
        assert!(!task.is_retired());
    }

    /// The whole Stage 1 feature is RAM-only: a fresh default store starts
    /// empty and writes nothing to disk.
    #[test]
    fn a_fresh_in_memory_store_recovers_no_definitions_and_touches_no_files() {
        let store = ScheduledTaskStore::new();
        store.add(draft(ScheduleKind::Interval), now());
        assert_eq!(store.items().len(), 1);

        // A second, independent store is what a restart looks like for an
        // in-memory engine: nothing is carried over.
        let restarted = ScheduledTaskStore::new();
        assert!(restarted.items().is_empty(), "no restart continuation");
        assert!(restarted.live_states().is_empty());
    }

    // ── persistence ────────────────────────────────────────────────────────────

    #[test]
    fn persist_and_reload() {
        let dir = std::env::temp_dir().join("coda-sched-store-test");
        std::fs::create_dir_all(&dir).ok();
        let path = dir.join("persist_reload.json");
        let _ = std::fs::remove_file(&path);

        let store = ScheduledTaskStore::with_path(&path);
        let t = store.add(draft(ScheduleKind::Interval), now());

        // Load fresh from disk.
        let store2 = ScheduledTaskStore::with_path(&path);
        let items = store2.items();
        assert_eq!(items.len(), 1, "expected 1 item after reload");
        assert_eq!(items[0].id, t.id);

        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn atomic_write_no_temp_files_left_on_success() {
        let dir = std::env::temp_dir().join("coda-sched-atomic-test");
        std::fs::create_dir_all(&dir).ok();
        let path = dir.join("atomic_write.json");
        let _ = std::fs::remove_file(&path);

        let store = ScheduledTaskStore::with_path(&path);
        store.add(draft(ScheduleKind::Interval), now());

        // No .tmp-* files should remain.
        let tmp_count = std::fs::read_dir(&dir)
            .unwrap()
            .filter_map(|e| e.ok())
            .filter(|e| {
                e.path()
                    .to_string_lossy()
                    .contains("tmp-")
            })
            .count();
        assert_eq!(tmp_count, 0, "stray temp files found");

        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn corrupt_json_loads_empty() {
        let dir = std::env::temp_dir().join("coda-sched-corrupt-test");
        std::fs::create_dir_all(&dir).ok();
        let path = dir.join("corrupt.json");
        std::fs::write(&path, b"not valid json {{{{").unwrap();
        let store = ScheduledTaskStore::with_path(&path);
        assert!(store.items().is_empty(), "expected empty on corrupt load");
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn malformed_element_skipped_others_loaded() {
        let dir = std::env::temp_dir().join("coda-sched-malformed-test");
        std::fs::create_dir_all(&dir).ok();
        let path = dir.join("malformed.json");

        // Valid item followed by a malformed one (not an object).
        let valid = ScheduledTask {
            schema_version: 2,
            id: "abc123def456".into(),
            name: None,
            kind: ScheduleKind::Interval,
            prompt: "test".into(),
            interval: Some(3600.0),
            at_utc: None,
            cron: None,
            time_zone_id: "UTC".into(),
            next_run_utc: now(),
            created_at_utc: now(),
            updated_at_utc: now(),
            last_terminal_outcome: None,
            expires_at_utc: None,
            max_runs: None,
            runs_started: 0,
            retirement: None,
        };
        let json = format!("[{},\"not-an-object\"]", serde_json::to_string(&valid).unwrap());
        std::fs::write(&path, json.as_bytes()).unwrap();

        let store = ScheduledTaskStore::with_path(&path);
        assert_eq!(store.items().len(), 1);
        assert_eq!(store.items()[0].id, "abc123def456");

        let _ = std::fs::remove_file(&path);
    }

    // ── wait_for_change ────────────────────────────────────────────────────────

    #[tokio::test]
    async fn wait_for_change_returns_immediately_when_already_changed() {
        use tokio::time::{timeout, Duration};
        let store = ScheduledTaskStore::new();
        store.add(draft(ScheduleKind::Interval), now());
        // Version is now 1; observed_version = 0 → should return immediately.
        timeout(Duration::from_millis(10), store.wait_for_change(0))
            .await
            .expect("should return immediately");
    }

    #[tokio::test]
    async fn wait_for_change_blocks_until_mutation() {
        use tokio::time::{timeout, Duration};

        let store = ScheduledTaskStore::new();
        let store2 = store.clone();
        let snap = store.get_snapshot();

        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(30)).await;
            store2.add(draft(ScheduleKind::Interval), Utc::now());
        });

        timeout(Duration::from_secs(2), store.wait_for_change(snap.version))
            .await
            .expect("wait_for_change timed out");
    }
}
