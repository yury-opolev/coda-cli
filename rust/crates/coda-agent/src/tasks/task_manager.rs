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

/// Observes every task's single transition to a terminal state through
/// [`TaskManager::complete`], [`TaskManager::fail`] or [`TaskManager::stop`].
///
/// Invoked exactly once per task, after the task's log is closed (so its
/// redacted tail is final) and without any registry lock held. Must not block.
/// Shutdown's force-stop of stragglers deliberately does not notify: the
/// session is ending and nobody is left to tell.
pub trait TaskCompletionListener: Send + Sync {
    fn on_terminal(&self, task: &Arc<ManagedTask>, status: TaskRunStatus);
}

/// Observes every task's lifecycle for client-facing events (non-blocking
/// runtime, Phase 3: `event/taskStarted`, `event/taskOutput`,
/// `event/taskCompleted`).
///
/// Called synchronously, without any registry lock held, from the thread
/// doing the work: after a task is registered, after output was appended
/// (and redacted into the task's tail), and once at the terminal transition
/// (before the completion is pushed anywhere). Must never block — an
/// implementation only records, it does not format or send output inline.
pub trait TaskEventObserver: Send + Sync {
    fn on_started(&self, task: &Arc<ManagedTask>);
    fn on_output(&self, task: &Arc<ManagedTask>);
    fn on_terminal(&self, task: &Arc<ManagedTask>, status: TaskRunStatus);
}

/// How many shell commands may be running at once (§6.9 resource bounds).
/// A command started beyond it is refused, never queued and never evicting
/// a running one; finished commands do not count.
pub const DEFAULT_MAX_LIVE_SHELL_TASKS: usize = 32;

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
    completion_listener: Mutex<Option<Arc<dyn TaskCompletionListener>>>,
    event_observer: Mutex<Option<Arc<dyn TaskEventObserver>>>,
    max_live_shell_tasks: std::sync::atomic::AtomicUsize,
    /// Signalled after every task settles (its terminal transition has been
    /// fully reported). A parked agent waits on it to re-check its live tasks.
    settle_signal: tokio::sync::Notify,
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
            completion_listener: Mutex::new(None),
            event_observer: Mutex::new(None),
            max_live_shell_tasks: std::sync::atomic::AtomicUsize::new(DEFAULT_MAX_LIVE_SHELL_TASKS),
            settle_signal: tokio::sync::Notify::new(),
            session_id,
        })
    }

    /// Installs the listener told about every task's terminal transition
    /// (see [`TaskCompletionListener`]). Replaces any previous listener.
    pub fn set_completion_listener(&self, listener: Arc<dyn TaskCompletionListener>) {
        *self.completion_listener.lock().unwrap() = Some(listener);
    }

    /// Installs the observer told about every task's start, output and end
    /// (see [`TaskEventObserver`]). Replaces any previous observer.
    pub fn set_event_observer(&self, observer: Arc<dyn TaskEventObserver>) {
        *self.event_observer.lock().unwrap() = Some(observer);
    }

    fn event_observer(&self) -> Option<Arc<dyn TaskEventObserver>> {
        self.event_observer.lock().unwrap().clone()
    }

    /// Overrides the cap on concurrently running shell commands.
    pub fn set_max_live_shell_tasks(&self, cap: usize) {
        self.max_live_shell_tasks.store(cap.max(1), std::sync::atomic::Ordering::Relaxed);
    }

    fn notify_terminal(&self, task: &Arc<ManagedTask>, status: TaskRunStatus) {
        if let Some(observer) = self.event_observer() {
            observer.on_terminal(task, status);
        }
        // An owner waiting on this task inline (`task_wait`) receives the
        // completion there; it is then pushed nowhere.
        task.claim_delivery_for_attached_owner();
        self.push_to_owner_inbox(task, status);
        let listener = self.completion_listener.lock().unwrap().clone();
        if let Some(listener) = listener {
            listener.on_terminal(task, status);
        }
        self.settle(task);
        // Pruning only removes settled tasks, so run it again now that this
        // one is (see `prune_terminal_tasks`).
        self.prune_terminal_tasks();
    }

    /// Marks a terminal task as fully reported and wakes parked owners.
    fn settle(&self, task: &Arc<ManagedTask>) {
        task.mark_settled();
        self.settle_signal.notify_waiters();
    }

    /// Delivers a finished task's completion to the inbox of the agent that
    /// started it, when that agent is a subagent (or scheduled run) with an
    /// inbox (non-blocking runtime, Phase 2). The main agent's completions go
    /// through the installed [`TaskCompletionListener`] instead.
    ///
    /// Exactly once: called once per terminal transition, and a task still in
    /// the foreground was observed inline by the call that awaited it.
    /// Unlike the main inbox, operator stops are delivered too: the owner is
    /// still running (or parked waiting on this very task) and must learn
    /// that the work it is waiting for will never report.
    fn push_to_owner_inbox(&self, task: &Arc<ManagedTask>, status: TaskRunStatus) {
        if task.mode() != TaskExecutionMode::Background || task.delivered_inline_to_owner() {
            return;
        }
        let Some(parent_id) = task.parent_id.as_deref() else { return };
        let Some(parent) = self.find_task(parent_id) else { return };
        let Some(inbox) = parent.steering() else { return };
        if parent.status() != TaskRunStatus::Running {
            return;
        }
        inbox.push_item(
            crate::steering::InboxItemKind::TaskCompleted,
            super::completion_push::owner_completion_text(task, status),
        );
        task.mark_completion_pushed();
    }

    /// Signalled after any task settles; see [`TaskManager::live_children`].
    pub fn settle_signal(&self) -> &tokio::sync::Notify {
        &self.settle_signal
    }

    /// The tasks `owner` started that have not settled yet — the work a
    /// parked agent is waiting for. `None` is the main agent; scheduled runs
    /// are independent of it and never count as its children.
    pub fn live_children(&self, owner: Option<&str>) -> Vec<Arc<ManagedTask>> {
        self.state
            .lock()
            .unwrap()
            .order
            .iter()
            .filter(|t| t.parent_id.as_deref() == owner)
            .filter(|t| !(owner.is_none() && t.kind == TaskKind::Scheduled))
            .filter(|t| !t.is_settled())
            .cloned()
            .collect()
    }

    /// Stops every running strict descendant of `id` (and their process
    /// trees). Used when a subagent ends: nothing it started may outlive it.
    pub fn stop_descendants(&self, id: &str) -> usize {
        let running = self.state.lock().unwrap().running_tasks();
        let mut stopped = 0;
        for task in &running {
            if task.id != id && self.is_authorized_caller(&task.id, Some(id)) {
                task.cancel_task();
                task.kill_attached_shell();
                stopped += 1;
            }
        }
        stopped
    }

    // ── User controls (non-blocking runtime, Phase 1) ─────────────────────────

    /// Stops awaiting one task in the foreground; the work keeps running.
    ///
    /// Only shell commands and wrapped tool calls are ever awaited in the
    /// foreground: subagents always start in the background (Phase 2), so for
    /// them this reports `InvalidState` (nothing is waiting on them).
    pub fn background(&self, id: &str) -> TaskActionResult {
        let Some(task) = self.find_task(id) else { return TaskActionResult::NotFound };
        if !task.kind.is_backgroundable() {
            return if task.mode() == TaskExecutionMode::Background {
                TaskActionResult::InvalidState
            } else {
                TaskActionResult::Rejected
            };
        }
        if task.request_background() {
            TaskActionResult::Ok
        } else {
            TaskActionResult::InvalidState
        }
    }

    /// Stops awaiting every running foreground shell task. Returns how many
    /// were asked to move to the background.
    pub fn background_all(&self) -> usize {
        let running = self.state.lock().unwrap().running_tasks();
        running
            .iter()
            .filter(|task| task.kind.is_backgroundable() && task.request_background())
            .count()
    }

    /// Stops one running task (and its process tree, for a shell). Operator
    /// authority: there is no calling agent to authorize. Marked as an
    /// operator stop, so its completion does not wake the agent.
    pub fn stop_task(&self, id: &str) -> TaskActionResult {
        if let Some(task) = self.find_task(id) {
            if task.status() == TaskRunStatus::Running {
                task.mark_operator_stop();
            }
        }
        let result = self.request_stop(id, None);
        if result == TaskActionResult::Ok {
            if let Some(task) = self.find_task(id) {
                task.kill_attached_shell();
            }
        }
        result
    }

    /// Delivers an operator message to a running agent task's inbox (Phase 2,
    /// `task/send`): it is delivered at the agent's next boundary (a running
    /// wait is not cut short), or wakes it when parked. `Rejected` for a task with no inbox (a shell command),
    /// `InvalidState` when it is not running or no longer accepts messages.
    pub fn send_message(&self, id: &str, text: &str) -> TaskActionResult {
        let Some(task) = self.find_task(id) else { return TaskActionResult::NotFound };
        if task.status() != TaskRunStatus::Running {
            return TaskActionResult::InvalidState;
        }
        let Some(inbox) = task.steering() else { return TaskActionResult::Rejected };
        match inbox.enqueue(text) {
            Some(_) => TaskActionResult::Ok,
            None => TaskActionResult::InvalidState,
        }
    }

    /// Stops every running task. Returns how many were asked to stop.
    pub fn stop_all(&self) -> usize {
        let running = self.state.lock().unwrap().running_tasks();
        for task in &running {
            task.mark_operator_stop();
            task.cancel_task();
            task.kill_attached_shell();
        }
        running.len()
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
        if kind == TaskKind::Shell {
            let cap = self.max_live_shell_tasks.load(std::sync::atomic::Ordering::Relaxed);
            let live = s
                .order
                .iter()
                .filter(|t| t.kind == TaskKind::Shell && t.status() == TaskRunStatus::Running)
                .count();
            if live >= cap {
                return Err(format!(
                    "{live} shell commands are already running, the most allowed at once. \
                     Stop one you no longer need (task_stop) or wait for one to finish, \
                     then try again."
                ));
            }
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
        let log = Arc::new(TaskLogWriter::with_tail(
            log_path,
            super::log_writer::DEFAULT_MAX_BYTES,
            task.redacted_output.clone(),
        ));

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
        if let Some(observer) = self.event_observer() {
            observer.on_started(&task);
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

    /// Registration-ordered snapshots and redacted output tails. Cloning the
    /// task handles makes concurrent removal safe without consuming cursors.
    pub fn list_with_output(&self, max_chars: usize) -> Vec<(TaskSnapshot, String)> {
        let tasks = self.state.lock().unwrap().order.clone();
        tasks.into_iter()
            .map(|task| (task.to_snapshot(), task.redacted_output.peek(max_chars)))
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
        if let Some(observer) = self.event_observer() {
            observer.on_output(&task);
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
        self.notify_terminal(&task, TaskRunStatus::Completed);
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
        self.notify_terminal(&task, TaskRunStatus::Failed);
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
        self.notify_terminal(&task, TaskRunStatus::Stopped);
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

    // ── Authorization helpers ──────────────────────────────────────────────────

    /// Returns `true` when `caller_task_id` is authorized to read or stop `target_id`.
    ///
    /// The main agent (`None` caller) has full authority over the whole session.
    /// A subagent has authority only over its strict descendants.
    ///
    /// Mirrors C# `TaskManager.IsAuthorizedCaller`.
    pub fn is_authorized_caller(&self, target_id: &str, caller_task_id: Option<&str>) -> bool {
        let Some(caller_id) = caller_task_id else { return true; };

        let target = match self.find_task(target_id) {
            Some(t) => t,
            None => return false,
        };

        // Walk strict ancestors of the target. Defensive cap at 64.
        let mut parent_id = target.parent_id.clone();
        let mut guard: u32 = 0;
        while let Some(pid) = parent_id {
            if pid == caller_id {
                return true;
            }
            if guard > 64 {
                return false;
            }
            guard += 1;
            parent_id = self.find_task(&pid).and_then(|p| p.parent_id.clone());
        }
        false
    }

    /// Reads incremental output with authorization check.
    ///
    /// Returns `(found=false, ...)` when the id is unknown OR the caller is unauthorized
    /// so a subagent cannot probe the existence of tasks it does not own.
    ///
    /// Mirrors C# `TaskManager.ReadOutput`.
    pub fn read_output(
        &self,
        id: &str,
        caller_task_id: Option<&str>,
    ) -> (bool, String, bool, TaskRunStatus) {
        let task = match self.find_task(id) {
            Some(t) => t,
            None => return (false, String::new(), false, TaskRunStatus::Running),
        };
        if !self.is_authorized_caller(id, caller_task_id) {
            return (false, String::new(), false, TaskRunStatus::Running);
        }
        let consumer_id = caller_task_id.unwrap_or(MAIN_CONSUMER_ID);
        let (text, truncated, status) = task.read_from_cursor(consumer_id);
        (true, text, truncated, status)
    }

    /// Requests cancellation of a running task with authorization check.
    ///
    /// Authorization is checked BEFORE any state inspection so an unauthorized caller
    /// cannot probe whether a task exists or is running.
    ///
    /// Returns:
    /// - `NotFound` when the id is unknown,
    /// - `Denied` when the caller is unauthorized (tools must use the same "not found"
    ///   wording as `NotFound` to prevent existence probing),
    /// - `InvalidState` when the task is already terminal,
    /// - `Ok` on success.
    ///
    /// Mirrors C# `TaskManager.RequestStop`.
    pub fn request_stop(&self, id: &str, caller_task_id: Option<&str>) -> TaskActionResult {
        let task = match self.find_task(id) {
            Some(t) => t,
            None => return TaskActionResult::NotFound,
        };
        if !self.is_authorized_caller(id, caller_task_id) {
            return TaskActionResult::Denied;
        }
        if task.status() != TaskRunStatus::Running {
            return TaskActionResult::InvalidState;
        }
        task.cancel_task();
        TaskActionResult::Ok
    }

    /// Narrow capability: a task cancelling **itself**.
    ///
    /// [`TaskManager::is_authorized_caller`] deliberately grants authority only
    /// over *strict descendants*, so `request_stop(self, Some(self))` is denied
    /// by design — a subagent must not be able to name its own id and have the
    /// generic path treat that as ownership. This is the one exception, and it
    /// is kept explicit and narrow rather than solved by handing a scheduled
    /// run main-agent privileges: the caller passes a single id, that id is the
    /// only thing it can affect, and nothing about an ancestor, a sibling, or
    /// any other task is reachable through it.
    ///
    /// Callers must have already proved that `id` is the caller's own task.
    pub fn request_self_stop(&self, id: &str) -> TaskActionResult {
        let task = match self.find_task(id) {
            Some(t) => t,
            None => return TaskActionResult::NotFound,
        };
        if task.status() != TaskRunStatus::Running {
            return TaskActionResult::InvalidState;
        }
        task.cancel_task();
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
            self.settle(t);
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
            // Only settled tasks: a task that just ended but has not reported yet
            // must stay visible to its parked owner (`live_children`).
            let terminal_count =
                s.order.iter().filter(|t| t.is_terminal() && t.is_settled()).count();
            if terminal_count <= self.max_retained_terminal_tasks {
                return;
            }

            let mut pruned = Vec::new();
            let excess = terminal_count - self.max_retained_terminal_tasks;
            let mut removed = 0usize;
            let mut idx = 0;
            while removed < excess && idx < s.order.len() {
                if s.order[idx].is_terminal() && s.order[idx].is_settled() {
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
    fn browser_output_is_redacted_bounded_and_survives_log_failure_and_completion() {
        let dir = tempfile::tempdir().unwrap();
        let blocked_root = dir.path().join("not-a-directory");
        std::fs::write(&blocked_root, "file").unwrap();
        let m = TaskManager::new("s", Some(blocked_root), 4096, 10);
        let task = m.register(
            TaskKind::Shell, "output", None, TaskExecutionMode::Background,
        ).unwrap();
        m.append_output(&task.id, "sk-ab");
        assert_eq!(m.list_with_output(4000)[0].1, "");
        m.append_output(&task.id, "cdefghijk ");
        m.append_output(&task.id, "visible ");
        let (_, output) = m.list_with_output(4000).pop().unwrap();
        assert_eq!(output, "sk-*** visible ");
        assert!(!task.log_path.exists(), "disk failure must not hide in-memory output");
        m.append_output(&task.id, &"界".repeat(5000));
        assert_eq!(m.list_with_output(4000)[0].1, "界".repeat(4000));
        m.append_output(&task.id, " sk-ab");
        m.complete(&task.id, None);
        assert!(m.list_with_output(4000)[0].1.ends_with(" sk-ab"),
            "completion must flush an unconfirmed candidate into the retained tail");
    }

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
    fn a_task_that_ended_but_has_not_reported_is_never_pruned() {
        let m = TaskManager::new("test-session", Some(std::env::temp_dir().join("coda-mgr-prune3")), 512, 1);
        // Ended, but its terminal notification (push, then settle) has not run.
        let unreported = m
            .register(TaskKind::Shell, "unreported", None, TaskExecutionMode::Background)
            .unwrap();
        unreported.try_complete(Some("exit code: 0".into()));
        for _ in 0..3 {
            let t = m
                .register(TaskKind::Subagent, "done", None, TaskExecutionMode::Background)
                .unwrap();
            m.complete(&t.id, None);
        }
        assert!(
            m.find_task(&unreported.id).is_some(),
            "an unsettled task must stay visible to its parked owner"
        );
        assert_eq!(m.list().len(), 2, "settled tasks are still pruned to the limit");
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

    // ── is_authorized_caller ──────────────────────────────────────────────────

    #[test]
    fn main_agent_caller_has_full_authority() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        assert!(m.is_authorized_caller(&t.id, None), "main agent must have full authority");
    }

    #[test]
    fn parent_is_authorized_over_child() {
        let m = mgr();
        let parent = m
            .register(TaskKind::Subagent, "parent", None, TaskExecutionMode::Background)
            .unwrap();
        let child = m
            .register(TaskKind::Subagent, "child", Some(&parent.id), TaskExecutionMode::Background)
            .unwrap();
        assert!(
            m.is_authorized_caller(&child.id, Some(&parent.id)),
            "parent must be authorized over child"
        );
    }

    #[test]
    fn sibling_is_not_authorized() {
        let m = mgr();
        let a = m
            .register(TaskKind::Subagent, "a", None, TaskExecutionMode::Background)
            .unwrap();
        let b = m
            .register(TaskKind::Subagent, "b", None, TaskExecutionMode::Background)
            .unwrap();
        assert!(
            !m.is_authorized_caller(&b.id, Some(&a.id)),
            "sibling must not be authorized"
        );
    }

    #[test]
    fn task_is_not_authorized_over_itself() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        assert!(
            !m.is_authorized_caller(&t.id, Some(&t.id)),
            "task must not be authorized over itself"
        );
    }

    #[test]
    fn unknown_target_is_not_authorized() {
        let m = mgr();
        // Caller = main agent always succeeds — test with a subagent caller instead.
        let caller = m
            .register(TaskKind::Subagent, "caller", None, TaskExecutionMode::Background)
            .unwrap();
        assert!(
            !m.is_authorized_caller("task-9999", Some(&caller.id)),
            "unknown target must be unauthorized for subagent caller"
        );
    }

    // ── request_stop ─────────────────────────────────────────────────────────

    #[test]
    fn request_stop_running_task_ok() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        let result = m.request_stop(&t.id, None);
        assert_eq!(result, TaskActionResult::Ok);
        assert!(t.cancel.is_cancelled(), "cancel token must be fired");
    }

    #[test]
    fn request_stop_terminal_task_invalid_state() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, None);
        let result = m.request_stop(&t.id, None);
        assert_eq!(result, TaskActionResult::InvalidState);
    }

    #[test]
    fn request_stop_unknown_id_not_found() {
        let m = mgr();
        assert_eq!(m.request_stop("task-9999", None), TaskActionResult::NotFound);
    }

    #[test]
    fn request_stop_denied_for_unauthorized_caller() {
        let m = mgr();
        let a = m
            .register(TaskKind::Subagent, "a", None, TaskExecutionMode::Background)
            .unwrap();
        let b = m
            .register(TaskKind::Subagent, "b", None, TaskExecutionMode::Background)
            .unwrap();
        // "a" tries to stop unrelated "b"
        let result = m.request_stop(&b.id, Some(&a.id));
        assert_eq!(result, TaskActionResult::Denied, "sibling stop must be denied");
        assert!(!b.cancel.is_cancelled(), "b token must not be cancelled");
    }

    // ── read_output ───────────────────────────────────────────────────────────

    #[test]
    fn read_output_main_agent_reads_any_task() {
        let m = mgr();
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&t.id, "hello");
        let (found, text, _truncated, _status) = m.read_output(&t.id, None);
        assert!(found);
        assert_eq!(text, "hello");
    }

    #[test]
    fn read_output_unauthorized_returns_not_found() {
        let m = mgr();
        let a = m
            .register(TaskKind::Subagent, "a", None, TaskExecutionMode::Background)
            .unwrap();
        let b = m
            .register(TaskKind::Subagent, "b", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&b.id, "secret");
        let (found, text, _, _) = m.read_output(&b.id, Some(&a.id));
        assert!(!found, "sibling must be unauthorized");
        assert!(!text.contains("secret"), "secret output must not be leaked");
    }

    #[test]
    fn read_output_parent_can_read_child() {
        let m = mgr();
        let parent = m
            .register(TaskKind::Subagent, "p", None, TaskExecutionMode::Background)
            .unwrap();
        let child = m
            .register(TaskKind::Subagent, "c", Some(&parent.id), TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&child.id, "child output");
        let (found, text, _, _) = m.read_output(&child.id, Some(&parent.id));
        assert!(found);
        assert_eq!(text, "child output");
    }

    // ── Phase 3: live shell cap and lifecycle observer ──────────────────

    #[test]
    fn shell_commands_beyond_the_cap_are_refused_and_finished_ones_free_a_slot() {
        let m = mgr();
        m.set_max_live_shell_tasks(2);
        let first = m.register(TaskKind::Shell, "a", None, TaskExecutionMode::Foreground).unwrap();
        m.register(TaskKind::Shell, "b", None, TaskExecutionMode::Background).unwrap();
        // Other kinds are bounded elsewhere and never count against the cap.
        m.register(TaskKind::Subagent, "agent", None, TaskExecutionMode::Background).unwrap();

        let Err(message) = m.register(TaskKind::Shell, "c", None, TaskExecutionMode::Foreground) else {
            panic!("a third live shell is refused");
        };
        assert!(message.contains("2 shell commands are already running"), "{message}");
        assert!(message.contains("task_stop"), "the refusal says how to free a slot: {message}");
        assert!(m.list().iter().all(|t| t.description != "c"), "nothing was registered");

        m.complete(&first.id, Some("exit code: 0".into()));
        assert!(
            m.register(TaskKind::Shell, "c", None, TaskExecutionMode::Foreground).is_ok(),
            "a finished command no longer counts"
        );
    }

    #[test]
    fn the_default_cap_is_thirty_two_live_shells() {
        let m = mgr();
        for i in 0..DEFAULT_MAX_LIVE_SHELL_TASKS {
            m.register(TaskKind::Shell, format!("s{i}"), None, TaskExecutionMode::Background).unwrap();
        }
        assert!(m.register(TaskKind::Shell, "one more", None, TaskExecutionMode::Background).is_err());
    }

    #[derive(Default)]
    struct RecordingObserver(Mutex<Vec<String>>);

    impl TaskEventObserver for RecordingObserver {
        fn on_started(&self, task: &Arc<ManagedTask>) {
            self.0.lock().unwrap().push(format!("started {}", task.id));
        }
        fn on_output(&self, task: &Arc<ManagedTask>) {
            self.0.lock().unwrap().push(format!("output {}", task.id));
        }
        fn on_terminal(&self, task: &Arc<ManagedTask>, status: TaskRunStatus) {
            self.0.lock().unwrap().push(format!("{} {}", status.as_str(), task.id));
        }
    }

    #[test]
    fn the_event_observer_sees_start_output_and_one_terminal_transition() {
        let m = mgr();
        let observer = Arc::new(RecordingObserver::default());
        m.set_event_observer(observer.clone());
        let t = m.register(TaskKind::Shell, "x", None, TaskExecutionMode::Foreground).unwrap();
        m.append_output(&t.id, "hello\n");
        m.fail(&t.id, Some("exit code: 1".into()));
        m.complete(&t.id, None);
        m.append_output(&t.id, "late");
        let seen = observer.0.lock().unwrap().clone();
        assert_eq!(seen, vec![
            format!("started {}", t.id),
            format!("output {}", t.id),
            format!("failed {}", t.id),
        ]);
    }
}
