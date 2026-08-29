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

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use tokio::sync::Notify;

use super::scheduled_task::{
    ScheduleDefinitionDraft, ScheduledTask, ScheduledTaskStoreSnapshot,
};

pub struct ScheduledTaskStore {
    persist_path: Option<PathBuf>,
    state: Mutex<StoreState>,
    signal: Arc<Notify>,
}

struct StoreState {
    items: Vec<ScheduledTask>,
    version: u64,
}

impl ScheduledTaskStore {
    /// In-memory store with no persistence.
    pub fn new() -> Arc<Self> {
        Arc::new(Self {
            persist_path: None,
            state: Mutex::new(StoreState { items: Vec::new(), version: 0 }),
            signal: Arc::new(Notify::new()),
        })
    }

    /// Persistent store backed by `path`. Loads existing records on construction.
    pub fn with_path(path: impl Into<PathBuf>) -> Arc<Self> {
        let path = path.into();
        let items = load_from_disk(&path);
        Arc::new(Self {
            persist_path: Some(path),
            state: Mutex::new(StoreState { items, version: 0 }),
            signal: Arc::new(Notify::new()),
        })
    }

    pub fn get_snapshot(&self) -> ScheduledTaskStoreSnapshot {
        let s = self.state.lock().unwrap();
        ScheduledTaskStoreSnapshot {
            version: s.version,
            items: s.items.clone(),
        }
    }

    pub fn items(&self) -> Vec<ScheduledTask> {
        self.state.lock().unwrap().items.clone()
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
            self.commit_locked(&mut s);
            true
        };
        if changed {
            self.signal.notify_waiters();
        }
        changed
    }

    /// Replace the task sharing `updated.id`. Returns `true` if found.
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
            state: Mutex::new(StoreState { items: Vec::new(), version: 0 }),
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
