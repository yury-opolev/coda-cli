//! Task change subscription: initial snapshot + bounded drop-oldest change stream.
//!
//! Consumers receive the complete task list at creation time, then bounded
//! versioned change notifications. Each change carries the exact task version
//! assigned by the manager at publish time. The subscription detects version
//! gaps/duplicates/out-of-order and reports `resync_required = true` from
//! the next [`drain`] call, instructing the consumer to re-read manager snapshots
//! which are always authoritative.

use std::collections::{HashMap, VecDeque};
use std::sync::Mutex;

use tokio::sync::Notify;

use super::managed_task::TaskSnapshot;

// ── Change types ──────────────────────────────────────────────────────────────

/// What a change notification is about.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TaskChangeKind {
    Created,
    Status,
    Output,
    /// The task's execution mode changed (e.g. foreground promoted to background).
    Mode,
    Removed,
}

/// A bounded change notification: which task, its version at publish time, the kind.
#[derive(Clone, Debug)]
pub struct TaskChange {
    pub task_id: String,
    pub version: u64,
    pub kind: TaskChangeKind,
}

// ── TaskSubscription ──────────────────────────────────────────────────────────

pub const DEFAULT_CAPACITY: usize = 1024;

struct SubState {
    queue: VecDeque<TaskChange>,
    /// Last version observed per task id; seeded from the initial snapshot.
    last_version: HashMap<String, u64>,
    gap: bool,
    closed: bool,
}

pub struct TaskSubscription {
    /// Complete task list captured at creation time.
    pub initial_snapshot: Vec<TaskSnapshot>,
    capacity: usize,
    state: Mutex<SubState>,
    signal: Notify,
    on_close: Option<Box<dyn Fn() + Send + Sync>>,
}

impl TaskSubscription {
    pub fn new(
        initial_snapshot: Vec<TaskSnapshot>,
        capacity: usize,
        on_close: Option<Box<dyn Fn() + Send + Sync>>,
    ) -> Self {
        let last_version = initial_snapshot
            .iter()
            .map(|s| (s.id.clone(), s.version))
            .collect();
        Self {
            initial_snapshot,
            capacity,
            state: Mutex::new(SubState {
                queue: VecDeque::new(),
                last_version,
                gap: false,
                closed: false,
            }),
            signal: Notify::new(),
            on_close,
        }
    }

    pub fn is_closed(&self) -> bool {
        self.state.lock().unwrap().closed
    }

    /// Enqueues a change (drop-oldest on overflow) and wakes any waiter.
    /// Never blocks. Called only by the manager.
    pub fn post(&self, change: TaskChange) {
        let mut s = self.state.lock().unwrap();
        if s.closed {
            return;
        }
        track_version(&mut s, &change);
        if s.queue.len() >= self.capacity {
            s.queue.pop_front();
            s.gap = true;
        }
        s.queue.push_back(change);
        // Drop the lock before notifying to avoid holding it while waking tasks.
        drop(s);
        self.signal.notify_waiters();
    }

    /// Remove and return all pending changes plus whether a resync is required.
    pub fn drain(&self) -> (Vec<TaskChange>, bool) {
        let mut s = self.state.lock().unwrap();
        let items: Vec<_> = s.queue.drain(..).collect();
        let had_gap = s.gap;
        s.gap = false;
        (items, had_gap)
    }

    /// Completes when at least one change is pending or the subscription is closed.
    pub async fn wait(&self) {
        {
            let s = self.state.lock().unwrap();
            if s.closed || !s.queue.is_empty() || s.gap {
                return;
            }
        }
        // Register the waiter before re-checking so we don't miss a concurrent post.
        let notified = self.signal.notified();
        {
            let s = self.state.lock().unwrap();
            if s.closed || !s.queue.is_empty() || s.gap {
                return;
            }
        }
        notified.await;
    }

    /// Close the subscription, wake any waiter, and fire the on_close callback.
    pub fn close(&self) {
        let closed_now = {
            let mut s = self.state.lock().unwrap();
            if s.closed {
                return;
            }
            s.closed = true;
            s.queue.clear();
            s.gap = false;
            true
        };
        if closed_now {
            self.signal.notify_waiters();
            if let Some(cb) = &self.on_close {
                cb();
            }
        }
    }
}

impl Drop for TaskSubscription {
    fn drop(&mut self) {
        self.close();
    }
}

// ── Version tracking ──────────────────────────────────────────────────────────

fn track_version(s: &mut SubState, change: &TaskChange) {
    let known = s.last_version.get(&change.task_id).copied();

    match change.kind {
        TaskChangeKind::Created => {
            if known.is_some() {
                // Duplicate/reorder: we already have this task.
                s.gap = true;
            }
        }
        _ => match known {
            None => {
                // First change for a task we never saw created — missed its birth.
                s.gap = true;
            }
            Some(last) if change.version != last + 1 => {
                // Skipped, duplicated, or out-of-order.
                s.gap = true;
            }
            _ => {}
        },
    }

    s.last_version.insert(change.task_id.clone(), change.version);
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::SystemTime;
    use super::super::managed_task::{TaskExecutionMode, TaskKind, TaskRunStatus};

    fn snap(id: &str, version: u64) -> TaskSnapshot {
        TaskSnapshot {
            id: id.to_owned(),
            parent_id: None,
            depth: 1,
            kind: TaskKind::Subagent,
            description: "test".to_owned(),
            status: TaskRunStatus::Running,
            mode: TaskExecutionMode::Background,
            version,
            started_at: SystemTime::now(),
            ended_at: None,
            log_path: String::new(),
            result: None,
            error: None,
            resolved_model: None,
        }
    }

    fn change(id: &str, version: u64, kind: TaskChangeKind) -> TaskChange {
        TaskChange { task_id: id.to_owned(), version, kind }
    }

    #[test]
    fn initial_snapshot_is_captured() {
        let snaps = vec![snap("task-0001", 0)];
        let sub = TaskSubscription::new(snaps.clone(), DEFAULT_CAPACITY, None);
        assert_eq!(sub.initial_snapshot.len(), 1);
        assert_eq!(sub.initial_snapshot[0].id, "task-0001");
    }

    #[test]
    fn posted_change_is_drainable() {
        let sub = TaskSubscription::new(vec![], DEFAULT_CAPACITY, None);
        sub.post(change("task-0001", 1, TaskChangeKind::Created));
        let (items, gap) = sub.drain();
        assert_eq!(items.len(), 1);
        assert!(!gap);
    }

    #[test]
    fn drain_clears_the_queue() {
        let sub = TaskSubscription::new(vec![], DEFAULT_CAPACITY, None);
        sub.post(change("task-0001", 1, TaskChangeKind::Created));
        sub.drain();
        let (items, _) = sub.drain();
        assert!(items.is_empty());
    }

    #[test]
    fn overflow_sets_gap_and_drops_oldest() {
        let sub = TaskSubscription::new(vec![], 2, None);
        sub.post(change("task-0001", 1, TaskChangeKind::Created));
        sub.post(change("task-0001", 2, TaskChangeKind::Output));
        sub.post(change("task-0001", 3, TaskChangeKind::Output)); // overflow
        let (items, gap) = sub.drain();
        assert!(gap, "expected resync on overflow");
        assert_eq!(items.len(), 2);
    }

    #[test]
    fn version_gap_sets_resync_flag() {
        let sub = TaskSubscription::new(vec![snap("task-0001", 0)], DEFAULT_CAPACITY, None);
        // Version 2 instead of expected 1 — gap detected.
        sub.post(change("task-0001", 2, TaskChangeKind::Status));
        let (_, gap) = sub.drain();
        assert!(gap, "expected resync on version gap");
    }

    #[test]
    fn contiguous_versions_do_not_set_gap() {
        let sub = TaskSubscription::new(vec![snap("task-0001", 0)], DEFAULT_CAPACITY, None);
        sub.post(change("task-0001", 1, TaskChangeKind::Status));
        let (_, gap) = sub.drain();
        assert!(!gap);
    }

    #[test]
    fn close_marks_subscription_closed() {
        let sub = TaskSubscription::new(vec![], DEFAULT_CAPACITY, None);
        assert!(!sub.is_closed());
        sub.close();
        assert!(sub.is_closed());
    }

    #[test]
    fn close_fires_callback() {
        use std::sync::{Arc, atomic::{AtomicBool, Ordering}};
        let fired = Arc::new(AtomicBool::new(false));
        let fired2 = fired.clone();
        let sub = TaskSubscription::new(
            vec![],
            DEFAULT_CAPACITY,
            Some(Box::new(move || fired2.store(true, Ordering::SeqCst))),
        );
        sub.close();
        assert!(fired.load(Ordering::SeqCst));
    }

    #[tokio::test]
    async fn wait_returns_immediately_when_queue_not_empty() {
        use tokio::time::{timeout, Duration};
        let sub = TaskSubscription::new(vec![], DEFAULT_CAPACITY, None);
        sub.post(change("task-0001", 1, TaskChangeKind::Created));
        timeout(Duration::from_millis(10), sub.wait())
            .await
            .expect("wait should return immediately");
    }

    #[tokio::test]
    async fn wait_returns_after_post() {
        use std::sync::Arc as StdArc;
        use tokio::time::{timeout, Duration};

        let sub = StdArc::new(TaskSubscription::new(vec![], DEFAULT_CAPACITY, None));
        let sub2 = sub.clone();

        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(30)).await;
            sub2.post(change("task-0001", 1, TaskChangeKind::Created));
        });

        timeout(Duration::from_secs(2), sub.wait())
            .await
            .expect("wait timed out");
    }
}
