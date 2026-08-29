//! In-process registry and coordinator for all long-running work in a session
//! (subagents and shells).
//!
//! # Guarantees
//! - **No task outlives shutdown.** [`TaskManager::shutdown`] cancels every
//!   running task, waits up to `budget` for them to reach a terminal state, then
//!   force-marks any straggler Stopped. On return every task is terminal.
//! - **Output ring is bounded.** Each task's ring is capped at `output_ring_bytes`.
//! - **Persistent logs.** Append-only, secret-redacted per-task log files under
//!   `log_root/<session>/<task>.log`, with log retention run at startup.
//! - **Auto-pruning.** Terminal tasks are pruned once the count exceeds
//!   `max_retained_terminal_tasks`; running tasks are never pruned.
//! - **Change subscriptions.** Consumers receive an initial snapshot and bounded
//!   drop-oldest versioned change stream; overflow triggers a resync flag.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex, Weak};
use std::time::Duration;

use super::log_retention;
use super::log_writer::TaskLogWriter;
use super::managed_task::{
    ManagedTask, TaskActionResult, TaskExecutionMode, TaskKind, TaskRunStatus, TaskSnapshot,
    MAIN_CONSUMER_ID,
};
use super::task_subscription::{TaskChange, TaskChangeKind, TaskSubscription, DEFAULT_CAPACITY};

pub use super::log_writer::TaskOutputChannel;

// ── Completion entry ──────────────────────────────────────────────────────────

/// A terminal-state notification delivered via the per-owner completion outbox.
#[derive(Clone, Debug)]
pub struct TaskCompletionEntry {
    pub task_id: String,
    pub description: String,
    pub status: TaskRunStatus,
    pub report: Option<String>,
}

// ── Manager constants ─────────────────────────────────────────────────────────

pub const DEFAULT_MAX_RETAINED_TERMINAL_TASKS: usize = 256;
pub const COMPLETION_OUTBOX_CAPACITY: usize = 64;
pub static DEFAULT_SHUTDOWN_BUDGET: Duration = Duration::from_secs(5);

// ── Internal registry state ───────────────────────────────────────────────────

struct ManagerState {
    order: Vec<Arc<ManagedTask>>,
    tasks: HashMap<String, Arc<ManagedTask>>,
    /// Arc so we can clone the writer out of the lock before doing any file I/O.
    logs: HashMap<String, Arc<TaskLogWriter>>,
    /// Weak refs so that dropping a subscriber automatically removes it from
    /// the list without needing an on_close callback. Dead entries are pruned
    /// lazily during publish so no live work is skipped.
    subscriptions: Vec<Weak<TaskSubscription>>,
    outbox: HashMap<String, std::collections::VecDeque<TaskCompletionEntry>>,
    next_id: u32,
    shutting_down: bool,
    disposed: bool,
}

impl ManagerState {
    fn find(&self, id: &str) -> Option<&Arc<ManagedTask>> {
        self.tasks.get(id)
    }

    #[allow(dead_code)]
    fn is_idle(&self) -> bool {
        !self.shutting_down
            && !self.disposed
            && self.order.iter().all(|t| t.status() != TaskRunStatus::Running)
    }

    fn running_tasks(&self) -> Vec<Arc<ManagedTask>> {
        self.order
            .iter()
            .filter(|t| t.status() == TaskRunStatus::Running)
            .cloned()
            .collect()
    }
}

// ── TaskManager ───────────────────────────────────────────────────────────────

pub struct TaskManager {
    pub session_id: String,
    pub log_root: PathBuf,
    output_ring_bytes: u64,
    max_retained_terminal_tasks: usize,
    state: Mutex<ManagerState>,
}

impl TaskManager {
    pub fn new(
        session_id: impl Into<String>,
        log_root: Option<PathBuf>,
        output_ring_bytes: u64,
        max_retained_terminal_tasks: usize,
    ) -> Arc<Self> {
        assert!(output_ring_bytes > 0);
        let root = log_root.unwrap_or_else(default_log_root);
        let session_id = session_id.into();

        // Best-effort startup housekeeping.
        log_retention::cleanup(&root);

        Arc::new(Self {
            log_root: root,
            output_ring_bytes,
            max_retained_terminal_tasks,
            state: Mutex::new(ManagerState {
                order: Vec::new(),
                tasks: HashMap::new(),
                logs: HashMap::new(),
                subscriptions: Vec::new(),
                outbox: HashMap::new(),
                next_id: 0,
                shutting_down: false,
                disposed: false,
            }),
            session_id,
        })
    }

    pub fn with_defaults(session_id: impl Into<String>) -> Arc<Self> {
        Self::new(
            session_id,
            None,
            super::output_ring::DEFAULT_MAX_BYTES,
            DEFAULT_MAX_RETAINED_TERMINAL_TASKS,
        )
    }

    /// Register a new task and return it in the Running state.
    ///
    /// Depth is derived from the parent: no parent = depth 1.
    /// Rejects when the manager is shutting down or disposed.
    pub fn register(
        &self,
        kind: TaskKind,
        description: impl Into<String>,
        parent_task_id: Option<&str>,
        mode: TaskExecutionMode,
    ) -> Result<Arc<ManagedTask>, String> {
        let depth = if let Some(pid) = parent_task_id {
            let s = self.state.lock().unwrap();
            if let Some(parent) = s.find(pid) {
                parent.depth + 1
            } else {
                return Err(format!("Unknown parent task '{pid}'."));
            }
        } else {
            1
        };

        let mut s = self.state.lock().unwrap();
        if s.shutting_down || s.disposed {
            return Err("Task manager is shutting down; no new tasks may be registered.".into());
        }

        s.next_id += 1;
        let id = format!("task-{:04}", s.next_id);
        let log_path = self.log_root.join(&self.session_id).join(format!("{id}.log"));

        let task = ManagedTask::new(
            id.clone(),
            parent_task_id.map(str::to_owned),
            depth,
            kind,
            description.into(),
            log_path.clone(),
            self.output_ring_bytes,
            mode,
        );

        // Log writer stored as Arc so it can be cloned out before file I/O.
        let log = Arc::new(TaskLogWriter::new(log_path));

        let created_version = task.version();
        let change = TaskChange {
            task_id: id.clone(),
            version: created_version,
            kind: TaskChangeKind::Created,
        };

        let subs: Vec<_> = s.subscriptions.iter().filter_map(Weak::upgrade).collect();

        s.order.push(task.clone());
        s.tasks.insert(id.clone(), task.clone());
        s.logs.insert(id, log);

        drop(s);

        for sub in &subs {
            sub.post(change.clone());
        }

        Ok(task)
    }

    /// Returns a snapshot for the task, or `None` if the id is unknown.
    pub fn get(&self, id: &str) -> Option<TaskSnapshot> {
        self.state.lock().unwrap().find(id).map(|t| t.to_snapshot())
    }

    /// Returns snapshots for all tasks in registration order.
    pub fn list(&self) -> Vec<TaskSnapshot> {
        self.state
            .lock()
            .unwrap()
            .order
            .iter()
            .map(|t| t.to_snapshot())
            .collect()
    }

    /// Returns the live task for an id. For use by tools and hosts.
    pub fn find_task(&self, id: &str) -> Option<Arc<ManagedTask>> {
        self.state.lock().unwrap().find(id).cloned()
    }

    /// Append output to a task's ring and persistent log.
    pub fn append_output(&self, id: &str, text: &str) {
        self.append_output_channel(id, text, TaskOutputChannel::General);
    }

    pub fn append_output_channel(&self, id: &str, text: &str, channel: TaskOutputChannel) {
        if text.is_empty() {
            return;
        }
        // Clone the task Arc and the log writer Arc while holding the lock, then
        // release the lock before doing any ring writes or file I/O. This keeps
        // disk I/O completely outside the registry lock so concurrent output from
        // multiple tasks does not serialize through a single mutex.
        let (task, log_writer, subs) = {
            let s = self.state.lock().unwrap();
            let task = match s.find(id) {
                Some(t) => t.clone(),
                None => return,
            };
            let log_writer = s.logs.get(id).cloned();
            let subs: Vec<_> = s.subscriptions.iter().filter_map(Weak::upgrade).collect();
            (task, log_writer, subs)
        };

        let version = match task.try_append(text) {
            Some(v) => v,
            None => return, // terminal or empty
        };

        // File I/O happens without any registry lock held.
        if let Some(w) = &log_writer {
            w.append(text, channel);
        }

        let change = TaskChange {
            task_id: task.id.clone(),
            version,
            kind: TaskChangeKind::Output,
        };
        for sub in &subs {
            sub.post(change.clone());
        }
    }

    /// Transition a task to Completed and publish a status change.
    pub fn complete(&self, id: &str, result: Option<String>) -> bool {
        let task = match self.find_task(id) {
            Some(t) => t,
            None => return false,
        };
        let version = match task.try_complete(result.clone()) {
            Some(v) => v,
            None => return false,
        };
        self.on_task_terminal(&task);
        self.publish(&task.id, version, TaskChangeKind::Status);
        self.enqueue_completion_if_background(&task, TaskRunStatus::Completed, result);
        true
    }

    /// Transition a task to Failed and publish a status change.
    pub fn fail(&self, id: &str, error: Option<String>) -> bool {
        let task = match self.find_task(id) {
            Some(t) => t,
            None => return false,
        };
        let version = match task.try_fail(error.clone()) {
            Some(v) => v,
            None => return false,
        };
        self.on_task_terminal(&task);
        self.publish(&task.id, version, TaskChangeKind::Status);
        self.enqueue_completion_if_background(&task, TaskRunStatus::Failed, error);
        true
    }

    /// Transition a task to Stopped and publish a status change.
    pub fn stop(&self, id: &str) -> bool {
        let task = match self.find_task(id) {
            Some(t) => t,
            None => return false,
        };
        let version = match task.try_stop() {
            Some(v) => v,
            None => return false,
        };
        self.on_task_terminal(&task);
        self.publish(&task.id, version, TaskChangeKind::Status);
        self.enqueue_completion_if_background(&task, TaskRunStatus::Stopped, None);
        true
    }

    /// Request cancellation of a task's token (does not change status).
    pub fn cancel(&self, id: &str) {
        if let Some(t) = self.find_task(id) {
            t.cancel_task();
        }
    }

    /// Remove a terminal task from the registry. Rejected while running.
    pub fn remove(&self, id: &str) -> TaskActionResult {
        let mut s = self.state.lock().unwrap();

        let idx = match s.order.iter().position(|t| t.id == id) {
            Some(i) => i,
            None => return TaskActionResult::NotFound,
        };

        let task = &s.order[idx];
        if task.status() == TaskRunStatus::Running {
            return TaskActionResult::Rejected;
        }

        let removed_version = task.bump_version_for_removal();
        let task = s.order.remove(idx);
        s.tasks.remove(id);
        if let Some(log) = s.logs.remove(id) {
            drop(log); // closes and flushes
        }

        let subs: Vec<_> = s.subscriptions.iter().filter_map(Weak::upgrade).collect();
        drop(s);

        drop(task);
        self.publish_to_subs(&subs, id, removed_version, TaskChangeKind::Removed);
        TaskActionResult::Ok
    }

    // ── Subscriptions ─────────────────────────────────────────────────────────

    /// Create a subscription seeded with the current task list.
    ///
    /// The subscription is stored as a `Weak` reference so that dropping the
    /// returned `Arc` automatically removes it from future publishes — no
    /// `on_close` callback needed. Dead entries are pruned lazily on each publish.
    pub fn subscribe(&self) -> Arc<TaskSubscription> {
        let mut s = self.state.lock().unwrap();
        let snapshot = s.order.iter().map(|t| t.to_snapshot()).collect();
        let sub = Arc::new(TaskSubscription::new(snapshot, DEFAULT_CAPACITY, None));
        s.subscriptions.push(Arc::downgrade(&sub));
        sub
    }

    // ── Completion outbox ──────────────────────────────────────────────────────

    pub fn drain_completions(&self, owner_task_id: Option<&str>) -> Vec<TaskCompletionEntry> {
        let key = outbox_key(owner_task_id);
        let mut s = self.state.lock().unwrap();
        if let Some(q) = s.outbox.get_mut(&key) {
            q.drain(..).collect()
        } else {
            Vec::new()
        }
    }

    // ── Incremental reads ──────────────────────────────────────────────────────

    pub fn try_read_incremental(&self, id: &str, cursor: u64) -> Option<(String, u64, bool)> {
        self.find_task(id).map(|t| t.read_incremental(cursor))
    }

    pub fn try_peek(&self, id: &str, max_chars: usize) -> Option<String> {
        self.find_task(id).map(|t| t.peek(max_chars))
    }

    /// Number of live subscriptions (Weak refs that can still be upgraded).
    pub fn subscription_count(&self) -> usize {
        self.state
            .lock()
            .unwrap()
            .subscriptions
            .iter()
            .filter(|w| w.strong_count() > 0)
            .count()
    }

    // ── Shutdown ───────────────────────────────────────────────────────────────

    /// Gracefully shut down: cancel all running tasks, wait up to `budget` for
    /// them to reach terminal, force-stop any stragglers.
    ///
    /// On return every task is terminal. Idempotent.
    pub async fn shutdown(&self, budget: Duration) {
        let running: Vec<Arc<ManagedTask>> = {
            let mut s = self.state.lock().unwrap();
            if s.disposed {
                return;
            }
            s.shutting_down = true;
            s.running_tasks()
        };

        // Cancel all running tasks AND kill attached shell processes before waiting.
        for t in &running {
            t.cancel_task();
            t.kill_attached_shell();
        }

        // Wait up to budget for tasks to reach terminal via their own completion path.
        if !running.is_empty() {
            let all_done = async {
                for t in &running {
                    t.wait_for_completion().await;
                }
            };
            let _ = tokio::time::timeout(budget, all_done).await;
        }

        // Force-stop any stragglers so the snapshot never shows phantom running tasks.
        for t in &running {
            if !t.is_terminal() {
                t.try_stop();
                self.on_task_terminal(t);
            }
        }

        // Hard teardown: close subscriptions, flush logs, release resources.
        self.dispose();
    }

    fn dispose(&self) {
        let (tasks, subs) = {
            let mut s = self.state.lock().unwrap();
            if s.disposed {
                return;
            }
            s.disposed = true;
            let tasks: Vec<_> = s.order.drain(..).collect();
            let subs: Vec<_> = s.subscriptions.drain(..).filter_map(|w| w.upgrade()).collect();
            s.tasks.clear();
            s.logs.clear(); // last Arc drops, TaskLogWriters flush+close
            (tasks, subs)
        };

        for sub in subs {
            sub.close();
        }
        drop(tasks);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// Terminal hook: close the task's log writer and prune terminal tasks.
    fn on_task_terminal(&self, task: &Arc<ManagedTask>) {
        {
            let mut s = self.state.lock().unwrap();
            if let Some(log) = s.logs.remove(&task.id) {
                drop(log); // last Arc drops → flush + close
            }
        }
        self.prune_terminal_tasks();
    }

    fn prune_terminal_tasks(&self) {
        let pruned = {
            let mut s = self.state.lock().unwrap();
            let terminal_count = s.order.iter().filter(|t| t.is_terminal()).count();
            if terminal_count <= self.max_retained_terminal_tasks {
                return;
            }

            let mut pruned = Vec::new();
            let excess = terminal_count - self.max_retained_terminal_tasks;
            let mut removed = 0usize;
            let mut idx = 0;
            while removed < excess && idx < s.order.len() {
                if s.order[idx].is_terminal() {
                    let version = s.order[idx].bump_version_for_removal();
                    let task = s.order.remove(idx);
                    s.tasks.remove(&task.id);
                    if let Some(log) = s.logs.remove(&task.id) {
                        drop(log);
                    }
                    pruned.push((task.id.clone(), version, task));
                    removed += 1;
                } else {
                    idx += 1;
                }
            }
            pruned
        };

        for (id, version, task) in pruned {
            drop(task);
            self.publish(&id, version, TaskChangeKind::Removed);
        }
    }

    fn publish(&self, task_id: &str, version: u64, kind: TaskChangeKind) {
        let subs: Vec<_> = {
            let mut s = self.state.lock().unwrap();
            // Prune dead weak refs lazily while we snapshot the live set.
            s.subscriptions.retain(|w| w.strong_count() > 0);
            s.subscriptions.iter().filter_map(Weak::upgrade).collect()
        };
        self.publish_to_subs(&subs, task_id, version, kind);
    }

    fn publish_to_subs(
        &self,
        subs: &[Arc<TaskSubscription>],
        task_id: &str,
        version: u64,
        kind: TaskChangeKind,
    ) {
        let change = TaskChange { task_id: task_id.to_owned(), version, kind };
        for sub in subs {
            sub.post(change.clone());
        }
    }

    fn enqueue_completion_if_background(
        &self,
        task: &Arc<ManagedTask>,
        status: TaskRunStatus,
        report: Option<String>,
    ) {
        if task.mode() != TaskExecutionMode::Background {
            return;
        }
        let entry = TaskCompletionEntry {
            task_id: task.id.clone(),
            description: task.description.clone(),
            status,
            report,
        };
        let key = outbox_key(task.parent_id.as_deref());
        let mut s = self.state.lock().unwrap();
        let q = s.outbox.entry(key).or_default();
        if q.len() >= COMPLETION_OUTBOX_CAPACITY {
            q.pop_front();
        }
        q.push_back(entry);
    }
}

fn outbox_key(owner_task_id: Option<&str>) -> String {
    // The leading NUL is the stable main-agent sentinel, matching C#.
    owner_task_id
        .map(str::to_owned)
        .unwrap_or_else(|| MAIN_CONSUMER_ID.to_owned())
}

fn default_log_root() -> PathBuf {
    // Use USERPROFILE on Windows, HOME on Unix. Fall back to "." if neither is set.
    let home = std::env::var("USERPROFILE")
        .or_else(|_| std::env::var("HOME"))
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from("."));
    home.join(".coda").join("task-logs")
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::time::{timeout, Duration as TDuration};

    fn mgr() -> Arc<TaskManager> {
        TaskManager::new("test-session", Some(std::env::temp_dir().join("coda-mgr-tests")), 4096, 10)
    }

    // ── register ──────────────────────────────────────────────────────────────

    #[test]
    fn register_creates_task_in_running_state() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "hello", None, TaskExecutionMode::Background)
            .unwrap();
        assert_eq!(t.status(), TaskRunStatus::Running);
        assert_eq!(m.list().len(), 1);
    }

    #[test]
    fn register_assigns_sequential_ids() {
        let m = mgr();
        let t1 = m
            .register(TaskKind::Subagent, "a", None, TaskExecutionMode::Background)
            .unwrap();
        let t2 = m
            .register(TaskKind::Subagent, "b", None, TaskExecutionMode::Background)
            .unwrap();
        assert_eq!(t1.id, "task-0001");
        assert_eq!(t2.id, "task-0002");
    }

    #[test]
    fn register_rejects_unknown_parent() {
        let m = mgr();
        let result = m.register(
            TaskKind::Subagent,
            "child",
            Some("task-9999"),
            TaskExecutionMode::Background,
        );
        assert!(result.is_err());
    }

    #[test]
    fn register_sets_depth_from_parent() {
        let m = mgr();
        let parent = m
            .register(TaskKind::Subagent, "parent", None, TaskExecutionMode::Background)
            .unwrap();
        let child = m
            .register(
                TaskKind::Subagent,
                "child",
                Some(&parent.id),
                TaskExecutionMode::Background,
            )
            .unwrap();
        assert_eq!(parent.depth, 1);
        assert_eq!(child.depth, 2);
    }

    // ── lifecycle transitions ──────────────────────────────────────────────────

    #[test]
    fn complete_transitions_task() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        assert!(m.complete(&t.id, Some("done".into())));
        assert_eq!(m.get(&t.id).unwrap().status, TaskRunStatus::Completed);
    }

    #[test]
    fn fail_transitions_task() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        assert!(m.fail(&t.id, Some("oops".into())));
        assert_eq!(m.get(&t.id).unwrap().status, TaskRunStatus::Failed);
    }

    #[test]
    fn stop_transitions_task() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        assert!(m.stop(&t.id));
        assert_eq!(m.get(&t.id).unwrap().status, TaskRunStatus::Stopped);
    }

    #[test]
    fn complete_unknown_id_returns_false() {
        let m = mgr();
        assert!(!m.complete("task-9999", None));
    }

    // ── output ────────────────────────────────────────────────────────────────

    #[test]
    fn append_output_is_readable() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&t.id, "hello world");
        let (text, _, _) = m.try_read_incremental(&t.id, 0).unwrap();
        assert_eq!(text, "hello world");
    }

    #[test]
    fn append_output_after_terminal_is_no_op() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, None);
        m.append_output(&t.id, "late");
        let (text, _, _) = m.try_read_incremental(&t.id, 0).unwrap();
        assert!(text.is_empty(), "should have no output after terminal: {text}");
    }

    // ── pruning ───────────────────────────────────────────────────────────────

    #[test]
    fn terminal_tasks_pruned_to_max() {
        let m = TaskManager::new("test-session", Some(std::env::temp_dir().join("coda-mgr-prune")), 512, 3);
        // Create and complete 5 tasks.
        for _ in 0..5 {
            let t = m
                .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
                .unwrap();
            m.complete(&t.id, None);
        }
        // Only 3 terminal tasks should remain.
        assert!(m.list().len() <= 3, "too many tasks: {}", m.list().len());
    }

    #[test]
    fn running_tasks_are_never_pruned() {
        let m = TaskManager::new("test-session", Some(std::env::temp_dir().join("coda-mgr-prune2")), 512, 1);
        let _running = m
            .register(TaskKind::Subagent, "running", None, TaskExecutionMode::Background)
            .unwrap();
        // Complete enough tasks to trigger pruning.
        for _ in 0..3 {
            let t = m
                .register(TaskKind::Subagent, "done", None, TaskExecutionMode::Background)
                .unwrap();
            m.complete(&t.id, None);
        }
        // The running task must survive pruning.
        assert!(
            m.list().iter().any(|s| s.status == TaskRunStatus::Running),
            "running task was pruned"
        );
    }

    // ── remove ────────────────────────────────────────────────────────────────

    #[test]
    fn remove_terminal_task_ok() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, None);
        assert_eq!(m.remove(&t.id), TaskActionResult::Ok);
        assert!(m.get(&t.id).is_none());
    }

    #[test]
    fn remove_running_task_rejected() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        assert_eq!(m.remove(&t.id), TaskActionResult::Rejected);
    }

    #[test]
    fn remove_unknown_id_is_not_found() {
        let m = mgr();
        assert_eq!(m.remove("task-9999"), TaskActionResult::NotFound);
    }

    // ── subscriptions ─────────────────────────────────────────────────────────

    #[test]
    fn subscription_receives_initial_snapshot() {
        let m = mgr();
        m.register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        let sub = m.subscribe();
        assert_eq!(sub.initial_snapshot.len(), 1);
    }

    #[test]
    fn subscription_receives_created_change() {
        let m = mgr();
        let sub = m.subscribe();
        m.register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        let (changes, _) = sub.drain();
        assert!(
            changes.iter().any(|c| c.kind == TaskChangeKind::Created),
            "no Created change: {changes:?}"
        );
    }

    // ── shutdown ──────────────────────────────────────────────────────────────

    /// Shutdown must terminate all running tasks within its budget and leave
    /// nothing in the Running state afterwards.
    #[tokio::test]
    async fn shutdown_terminates_running_tasks_within_budget() {
        let m = mgr();
        let t1 = m
            .register(TaskKind::Subagent, "t1", None, TaskExecutionMode::Background)
            .unwrap();
        let t2 = m
            .register(TaskKind::Subagent, "t2", None, TaskExecutionMode::Background)
            .unwrap();

        // Simulate tasks that run until their cancel token fires.
        let t1c = t1.clone();
        let t2c = t2.clone();
        tokio::spawn(async move {
            t1c.cancel.cancelled().await;
            t1c.try_stop();
        });
        tokio::spawn(async move {
            t2c.cancel.cancelled().await;
            t2c.try_stop();
        });

        timeout(TDuration::from_secs(5), m.shutdown(DEFAULT_SHUTDOWN_BUDGET))
            .await
            .expect("shutdown timed out");

        // No task may remain in the Running state.
        let running = m
            .list()
            .iter()
            .filter(|s| s.status == TaskRunStatus::Running)
            .count();
        assert_eq!(running, 0, "tasks still running after shutdown");
    }

    #[tokio::test]
    async fn shutdown_force_stops_uncooperative_tasks() {
        let m = mgr();
        let _t = m
            .register(TaskKind::Subagent, "stubborn", None, TaskExecutionMode::Background)
            .unwrap();
        // No cooperation: the task ignores cancellation.
        // Shutdown with a very short budget to force the stop path.
        timeout(
            TDuration::from_secs(3),
            m.shutdown(Duration::from_millis(50)),
        )
        .await
        .expect("shutdown timed out");

        let running = m
            .list()
            .iter()
            .filter(|s| s.status == TaskRunStatus::Running)
            .count();
        assert_eq!(running, 0, "task still running after force-stop");
    }

    #[tokio::test]
    async fn register_after_shutdown_is_rejected() {
        let m = mgr();
        m.shutdown(Duration::from_millis(10)).await;
        let result = m.register(TaskKind::Subagent, "late", None, TaskExecutionMode::Background);
        assert!(result.is_err(), "should reject registration after shutdown");
    }

    // ── completion outbox ──────────────────────────────────────────────────────

    #[test]
    fn background_completion_is_enqueued() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, Some("result".into()));
        let entries = m.drain_completions(None); // main agent outbox
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].task_id, t.id);
        assert_eq!(entries[0].status, TaskRunStatus::Completed);
    }

    #[test]
    fn foreground_completion_is_not_enqueued() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Foreground)
            .unwrap();
        m.complete(&t.id, None);
        let entries = m.drain_completions(None);
        assert!(entries.is_empty(), "foreground should not enqueue");
    }

    #[test]
    fn drain_completions_is_exactly_once() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, None);
        let first = m.drain_completions(None);
        let second = m.drain_completions(None);
        assert_eq!(first.len(), 1);
        assert!(second.is_empty());
    }

    // ── MINOR 7: cancel() ─────────────────────────────────────────────────────

    #[test]
    fn cancel_fires_the_cancellation_token_without_changing_status() {
        // cancel() is the risk-critical path: it triggers orderly shutdown of the
        // task's async work but leaves status transitions to the task itself.
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "work", None, TaskExecutionMode::Background)
            .unwrap();

        assert_eq!(t.status(), TaskRunStatus::Running);
        // The cancel token must not be fired before cancel() is called.
        assert!(!t.cancel.is_cancelled(), "token should not be cancelled before cancel()");

        m.cancel(&t.id);

        // Status is still Running — the task itself transitions to a terminal state
        // when it observes the token.
        assert_eq!(t.status(), TaskRunStatus::Running, "cancel() must not change status");
        // The underlying token MUST be fired so the task's work can stop.
        assert!(t.cancel.is_cancelled(), "cancel() must fire the cancellation token");
    }

    #[test]
    fn cancel_unknown_id_is_a_no_op() {
        // cancel() for an unknown id must not panic — the task may have already
        // been removed or was never registered.
        let m = mgr();
        m.cancel("task-9999"); // must not panic
    }
}
