//! One live unit of work (subagent or shell). Owns a cancellation source,
//! a monotonic version, and its lifecycle status. Extended with an output ring,
//! a steering inbox handle, and shell-kill delegate.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Mutex, Arc};
use std::time::SystemTime;

use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

use super::output_ring::OutputRing;
use crate::steering::SteeringInbox;

// ── Public enums ──────────────────────────────────────────────────────────────

/// Lifecycle state of a managed task.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum TaskRunStatus {
    Running,
    /// Paused at a safe point (planned restart); not terminal. Only
    /// subagents are ever suspended.
    Suspended,
    Completed,
    Failed,
    Stopped,
}

impl TaskRunStatus {
    pub fn is_terminal(self) -> bool {
        !matches!(self, TaskRunStatus::Running | TaskRunStatus::Suspended)
    }

    /// The wire spelling (`session/listTasks`, task events).
    pub fn as_str(self) -> &'static str {
        match self {
            TaskRunStatus::Running => "running",
            TaskRunStatus::Suspended => "suspended",
            TaskRunStatus::Completed => "completed",
            TaskRunStatus::Failed => "failed",
            TaskRunStatus::Stopped => "stopped",
        }
    }
}

/// The kind of work a managed task represents.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum TaskKind {
    Subagent,
    Shell,
    Scheduled,
    /// A long tool call (e.g. an MCP tool) wrapped as a task by the batch
    /// executor so it can be awaited under the yield contract.
    Tool,
}

impl TaskKind {
    /// The wire spelling (`session/listTasks`, task events).
    pub fn as_str(self) -> &'static str {
        match self {
            TaskKind::Subagent => "subagent",
            TaskKind::Shell => "shell",
            TaskKind::Scheduled => "scheduled",
            TaskKind::Tool => "tool",
        }
    }

    /// Kinds a loop awaits in the foreground and the user can release with
    /// "background now".
    pub fn is_backgroundable(self) -> bool {
        matches!(self, TaskKind::Shell | TaskKind::Tool)
    }
}

/// Whether a managed task runs in the foreground or the background.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum TaskExecutionMode {
    Foreground,
    Background,
}

impl TaskExecutionMode {
    /// The wire spelling (`session/listTasks`, task events).
    pub fn as_str(self) -> &'static str {
        match self {
            TaskExecutionMode::Foreground => "foreground",
            TaskExecutionMode::Background => "background",
        }
    }
}

/// Outcome of a lifecycle request so tools can produce precise messages.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TaskActionResult {
    Ok,
    NotFound,
    InvalidState,
    Rejected,
    /// The caller is not authorized (maps to the same wording as NotFound so
    /// a subagent cannot probe the existence of tasks it does not own).
    Denied,
}

// ── TaskSnapshot ──────────────────────────────────────────────────────────────

/// Immutable point-in-time view of a managed task.
#[derive(Clone, Debug)]
pub struct TaskSnapshot {
    pub id: String,
    pub parent_id: Option<String>,
    pub depth: u32,
    pub kind: TaskKind,
    pub description: String,
    pub status: TaskRunStatus,
    pub mode: TaskExecutionMode,
    pub version: u64,
    pub started_at: SystemTime,
    pub ended_at: Option<SystemTime>,
    pub log_path: String,
    pub result: Option<String>,
    pub error: Option<String>,
    pub resolved_model: Option<String>,
    /// Re-created from a planned-restart file rather than started fresh.
    pub resumed: bool,
    /// Suspension is requested for this task, explicitly or inherited from
    /// an ancestor. Only [`TaskManager`](super::TaskManager) snapshots carry
    /// the inherited part; [`ManagedTask::to_snapshot`] sees only its own flag.
    pub suspend_requested: bool,
}

/// The conversation state a subagent left at the safe point where it
/// suspended; what a planned restart serializes for it.
#[derive(Clone, Debug)]
pub struct TaskCheckpoint {
    pub history: Vec<coda_llm::Message>,
    pub steps_used: usize,
    /// Suspended while parked (waiting for its children), not mid-loop.
    pub parked: bool,
    pub suspended_at: SystemTime,
}

// ── ManagedTask ───────────────────────────────────────────────────────────────

struct TaskState {
    status: TaskRunStatus,
    mode: TaskExecutionMode,
    version: u64,
    ended_at: Option<SystemTime>,
    result: Option<String>,
    error: Option<String>,
    /// Per-consumer output cursors; keyed by consumer id.
    cursors: HashMap<String, u64>,
    /// Best-effort kill delegate for the shell process, if any.
    kill_shell: Option<Box<dyn Fn() + Send + Sync>>,
}

pub struct ManagedTask {
    pub id: String,
    pub parent_id: Option<String>,
    pub depth: u32,
    pub kind: TaskKind,
    pub description: String,
    pub log_path: PathBuf,
    pub started_at: SystemTime,
    pub cancel: CancellationToken,
    /// Resolved model id (subagents only); set once before the loop starts.
    pub resolved_model: Option<String>,
    /// Inbox of the agent running as this task (subagents and scheduled runs;
    /// attached once by the subagent host). `None` for shell tasks.
    steering: std::sync::OnceLock<Arc<SteeringInbox>>,
    output: OutputRing,
    pub(super) redacted_output: Arc<OutputRing>,
    state: Mutex<TaskState>,
    /// Signalled (notify_waiters) when the task first transitions to terminal.
    completion: Arc<Notify>,
    /// Atomic flag so `wait_for_completion` can detect terminal without the lock.
    terminal_flag: std::sync::atomic::AtomicBool,
    /// Set (sticky) when a user asked for this task to stop being awaited in
    /// the foreground ("background now"). Signalled through `background_signal`.
    background_requested: std::sync::atomic::AtomicBool,
    background_signal: Notify,
    /// Set when the operator (not the agent) stopped this task — `/stop`,
    /// `task/stop`. Such a stop is not pushed back to the agent as a
    /// completion: the user asked for quiet, not for a new turn.
    operator_stop: std::sync::atomic::AtomicBool,
    /// One-shot hand-off of the full result for the tool call that started
    /// this task, when that call observes the completion inline. Never part
    /// of a snapshot: it is the tool's own (unredacted) output, which must not
    /// reach `session/listTasks` or any other reader.
    tool_output: Mutex<Option<String>>,
    /// Set once the terminal transition has been fully reported (the owner's
    /// inbox already holds its completion, if it gets one). Until then the
    /// task still counts as live for its owner's parked state, which closes
    /// the race between "the child just finished" and "its report arrived".
    settled: std::sync::atomic::AtomicBool,
    /// Set when the completion was pushed to its owner's inbox, so the
    /// deprecated `task_recall` does not hand it out a second time.
    completion_pushed: std::sync::atomic::AtomicBool,
    /// `(owner waits attached, delivery claimed inline)`: while the owning
    /// agent waits on this task with `task_wait`, a completion is handed to
    /// that wait instead of being pushed, so it is delivered exactly once.
    owner_attach: Mutex<(u32, bool)>,
    /// Suspension explicitly requested for this task itself (its
    /// descendants inherit it; see `TaskManager::is_suspension_requested`).
    suspend_explicit: std::sync::atomic::AtomicBool,
    /// Why the operator stopped this task, when a reason was given
    /// (`TaskManager::stop_with_note`).
    stop_note: Mutex<Option<String>>,
    /// Re-created from a planned-restart file.
    resumed: std::sync::atomic::AtomicBool,
    /// Set while suspended at a safe point; cleared on resume.
    checkpoint: Mutex<Option<TaskCheckpoint>>,
    /// Opaque description of how to re-create this subagent (its spawn
    /// spec), recorded once at spawn.
    spec: std::sync::OnceLock<serde_json::Value>,
}

/// Stable consumer id for the main agent's cursor. The leading NUL cannot collide
/// with a `task-NNNN` id so a subagent can never masquerade as the main consumer.
pub const MAIN_CONSUMER_ID: &str = "\u{0000}main";

impl ManagedTask {
    pub fn new(
        id: String,
        parent_id: Option<String>,
        depth: u32,
        kind: TaskKind,
        description: String,
        log_path: PathBuf,
        output_ring_bytes: u64,
        mode: TaskExecutionMode,
    ) -> Arc<Self> {
        Self::new_started_at(
            id,
            parent_id,
            depth,
            kind,
            description,
            log_path,
            output_ring_bytes,
            mode,
            SystemTime::now(),
        )
    }

    /// [`ManagedTask::new`] with a given start time (a restored task keeps
    /// the one it had before the restart).
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn new_started_at(
        id: String,
        parent_id: Option<String>,
        depth: u32,
        kind: TaskKind,
        description: String,
        log_path: PathBuf,
        output_ring_bytes: u64,
        mode: TaskExecutionMode,
        started_at: SystemTime,
    ) -> Arc<Self> {
        Arc::new(Self {
            id,
            parent_id,
            depth,
            kind,
            description,
            log_path,
            started_at,
            cancel: CancellationToken::new(),
            resolved_model: None,
            steering: std::sync::OnceLock::new(),
            output: OutputRing::new(output_ring_bytes),
            redacted_output: Arc::new(OutputRing::new(16 * 1024)),
            state: Mutex::new(TaskState {
                status: TaskRunStatus::Running,
                mode,
                version: 0,
                ended_at: None,
                result: None,
                error: None,
                cursors: HashMap::new(),
                kill_shell: None,
            }),
            completion: Arc::new(Notify::new()),
            terminal_flag: std::sync::atomic::AtomicBool::new(false),
            background_requested: std::sync::atomic::AtomicBool::new(false),
            background_signal: Notify::new(),
            operator_stop: std::sync::atomic::AtomicBool::new(false),
            tool_output: Mutex::new(None),
            settled: std::sync::atomic::AtomicBool::new(false),
            completion_pushed: std::sync::atomic::AtomicBool::new(false),
            owner_attach: Mutex::new((0, false)),
            suspend_explicit: std::sync::atomic::AtomicBool::new(false),
            stop_note: Mutex::new(None),
            resumed: std::sync::atomic::AtomicBool::new(false),
            checkpoint: Mutex::new(None),
            spec: std::sync::OnceLock::new(),
        })
    }

    /// Whether suspension was explicitly requested for this task itself.
    pub fn suspend_explicit(&self) -> bool {
        self.suspend_explicit.load(std::sync::atomic::Ordering::Acquire)
    }

    /// Sets or clears the explicit suspension request. Returns the previous value.
    pub(crate) fn set_suspend_explicit(&self, on: bool) -> bool {
        self.suspend_explicit.swap(on, std::sync::atomic::Ordering::AcqRel)
    }

    /// Why the operator stopped this task, if a reason was recorded.
    pub fn stop_note(&self) -> Option<String> {
        self.stop_note.lock().unwrap().clone()
    }

    pub(crate) fn set_stop_note(&self, note: &str) {
        *self.stop_note.lock().unwrap() = Some(note.to_owned());
    }

    /// Whether this task was re-created from a planned-restart file.
    pub fn was_resumed(&self) -> bool {
        self.resumed.load(std::sync::atomic::Ordering::Acquire)
    }

    pub(crate) fn mark_resumed_from_restart(&self) {
        self.resumed.store(true, std::sync::atomic::Ordering::Release);
    }

    /// The checkpoint taken when this task suspended, while it is suspended.
    pub fn checkpoint(&self) -> Option<TaskCheckpoint> {
        self.checkpoint.lock().unwrap().clone()
    }

    /// Records the opaque spawn spec. First call wins.
    pub fn set_spec(&self, spec: serde_json::Value) -> bool {
        self.spec.set(spec).is_ok()
    }

    /// The opaque spawn spec, if one was recorded.
    pub fn spec(&self) -> Option<&serde_json::Value> {
        self.spec.get()
    }

    /// Running → Suspended, keeping `checkpoint`. Returns the new version, or
    /// `None` when the task is not running.
    pub(crate) fn try_suspend(&self, checkpoint: TaskCheckpoint) -> Option<u64> {
        let mut s = self.state.lock().unwrap();
        if s.status != TaskRunStatus::Running {
            return None;
        }
        s.status = TaskRunStatus::Suspended;
        s.version += 1;
        *self.checkpoint.lock().unwrap() = Some(checkpoint);
        Some(s.version)
    }

    /// Suspended → Running, dropping the checkpoint. Returns the new version,
    /// or `None` when the task is not suspended.
    pub(crate) fn try_resume(&self) -> Option<u64> {
        let mut s = self.state.lock().unwrap();
        if s.status != TaskRunStatus::Suspended {
            return None;
        }
        s.status = TaskRunStatus::Running;
        s.version += 1;
        *self.checkpoint.lock().unwrap() = None;
        Some(s.version)
    }

    /// The owning agent starts waiting on this task inline (`task_wait`).
    pub fn begin_owner_attach(&self) {
        self.owner_attach.lock().unwrap().0 += 1;
    }

    /// The owning agent stops waiting. Returns `true` when the completion was
    /// claimed by the wait (not pushed), so the waiter must report it.
    pub fn end_owner_attach(&self) -> bool {
        let mut attach = self.owner_attach.lock().unwrap();
        attach.0 = attach.0.saturating_sub(1);
        attach.1
    }

    /// Called once at the terminal notification: claims the completion for
    /// an attached owner wait, if there is one.
    pub(crate) fn claim_delivery_for_attached_owner(&self) -> bool {
        let mut attach = self.owner_attach.lock().unwrap();
        if attach.0 > 0 {
            attach.1 = true;
        }
        attach.1
    }

    /// Whether the completion went to an attached owner wait instead of a push.
    pub fn delivered_inline_to_owner(&self) -> bool {
        self.owner_attach.lock().unwrap().1
    }

    /// Attaches the inbox of the agent running as this task. First call wins.
    pub fn attach_steering(&self, inbox: Arc<SteeringInbox>) -> bool {
        self.steering.set(inbox).is_ok()
    }

    /// The inbox of the agent running as this task, if one is attached.
    pub fn steering(&self) -> Option<&Arc<SteeringInbox>> {
        self.steering.get()
    }

    /// Whether the terminal transition has been fully reported.
    pub fn is_settled(&self) -> bool {
        self.settled.load(std::sync::atomic::Ordering::Acquire)
    }

    pub(crate) fn mark_settled(&self) {
        self.settled.store(true, std::sync::atomic::Ordering::Release);
    }

    /// Whether this task's completion was pushed into its owner's inbox.
    pub fn completion_pushed(&self) -> bool {
        self.completion_pushed.load(std::sync::atomic::Ordering::Acquire)
    }

    pub fn mark_completion_pushed(&self) {
        self.completion_pushed.store(true, std::sync::atomic::Ordering::Release);
    }

    /// Records that the operator asked for this task to stop.
    pub fn mark_operator_stop(&self) {
        self.operator_stop.store(true, std::sync::atomic::Ordering::Release);
    }

    /// Whether the operator (rather than the agent) stopped this task.
    pub fn stopped_by_operator(&self) -> bool {
        self.operator_stop.load(std::sync::atomic::Ordering::Acquire)
    }

    /// Stores the starting tool call's full result. Call before the terminal
    /// transition so a waiter woken by it always finds the result.
    pub fn set_tool_output(&self, output: String) {
        *self.tool_output.lock().unwrap() = Some(output);
    }

    /// Takes the starting tool call's full result, if one was stored.
    pub fn take_tool_output(&self) -> Option<String> {
        self.tool_output.lock().unwrap().take()
    }

    /// Asks whoever is awaiting this task in the foreground to stop waiting
    /// and let it run on in the background. Never stops the work itself.
    /// Returns `false` when the task is terminal or already in the background.
    pub fn request_background(&self) -> bool {
        if self.is_terminal() || self.mode() != TaskExecutionMode::Foreground {
            return false;
        }
        self.background_requested.store(true, std::sync::atomic::Ordering::Release);
        self.background_signal.notify_waiters();
        true
    }

    /// Resolves once [`ManagedTask::request_background`] has been called.
    pub async fn background_requested(&self) {
        loop {
            let notified = self.background_signal.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();
            if self.background_requested.load(std::sync::atomic::Ordering::Acquire) {
                return;
            }
            notified.await;
        }
    }

    pub fn status(&self) -> TaskRunStatus {
        self.state.lock().unwrap().status
    }

    pub fn mode(&self) -> TaskExecutionMode {
        self.state.lock().unwrap().mode
    }

    pub fn version(&self) -> u64 {
        self.state.lock().unwrap().version
    }

    pub fn is_terminal(&self) -> bool {
        self.terminal_flag.load(std::sync::atomic::Ordering::Acquire)
    }

    /// Requests cancellation of the underlying work without changing status.
    pub fn cancel_task(&self) {
        self.cancel.cancel();
    }

    /// Attaches a best-effort tree-kill delegate for the live shell process.
    pub fn attach_shell_kill(&self, kill: impl Fn() + Send + Sync + 'static) {
        self.state.lock().unwrap().kill_shell = Some(Box::new(kill));
    }

    /// Clears the shell-kill delegate (called when the shell process is disposed).
    pub fn detach_shell_kill(&self) {
        self.state.lock().unwrap().kill_shell = None;
    }

    /// Requests a tree-kill of the attached shell process, if any. Best-effort.
    pub fn kill_attached_shell(&self) {
        let s = self.state.lock().unwrap();
        if let Some(ref k) = s.kill_shell {
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| k()));
        }
    }

    /// Atomically bumps version for a terminal removal. Valid only on a terminal task.
    pub fn bump_version_for_removal(&self) -> u64 {
        let mut s = self.state.lock().unwrap();
        s.version += 1;
        s.version
    }

    /// Atomically promotes a foreground live (running or suspended) task to the
    /// background. Returns `Some(version)` on success, `None` when already
    /// terminal or already background.
    pub fn try_promote_to_background(&self) -> Option<u64> {
        let mut s = self.state.lock().unwrap();
        if s.status.is_terminal() || s.mode != TaskExecutionMode::Foreground {
            return None;
        }
        s.mode = TaskExecutionMode::Background;
        s.version += 1;
        Some(s.version)
    }

    pub fn try_complete(&self, result: Option<String>) -> Option<u64> {
        self.transition(TaskRunStatus::Completed, result, None)
    }

    pub fn try_fail(&self, error: Option<String>) -> Option<u64> {
        self.transition(TaskRunStatus::Failed, None, error)
    }

    pub fn try_stop(&self) -> Option<u64> {
        self.transition(TaskRunStatus::Stopped, None, None)
    }

    fn transition(
        &self,
        next: TaskRunStatus,
        result: Option<String>,
        error: Option<String>,
    ) -> Option<u64> {
        let version = {
            let mut s = self.state.lock().unwrap();
            if s.status.is_terminal() {
                return None;
            }
            s.status = next;
            s.result = result;
            s.error = error;
            s.ended_at = Some(SystemTime::now());
            s.version += 1;
            s.version
        };

        // Mark terminal BEFORE notifying waiters so they observe the new status.
        self.terminal_flag
            .store(true, std::sync::atomic::Ordering::Release);
        self.completion.notify_waiters();

        Some(version)
    }

    /// Returns a future that completes once the task reaches a terminal state.
    /// If already terminal, the future resolves immediately.
    pub async fn wait_for_completion(&self) {
        // Fast path: already terminal.
        if self.is_terminal() {
            return;
        }
        // Register a waiter BEFORE re-checking so we don't miss a concurrent notification.
        let notified = self.completion.notified();
        // Re-check after registering (closes the race window between the check and notified()).
        if self.is_terminal() {
            return;
        }
        notified.await;
    }

    /// Append text to the output ring. Returns the new version, or None when
    /// the task is already terminal or the text is empty.
    pub fn try_append(&self, text: &str) -> Option<u64> {
        if text.is_empty() {
            return None;
        }
        let mut s = self.state.lock().unwrap();
        if s.status.is_terminal() {
            return None;
        }
        self.output.append(text);
        s.version += 1;
        Some(s.version)
    }

    /// Reads output at or after the absolute cursor (for task_wait / task_output).
    pub fn read_incremental(&self, cursor: u64) -> (String, u64, bool) {
        self.output.read_from(cursor)
    }

    /// Returns the last `max_chars` characters of buffered output.
    pub fn peek(&self, max_chars: usize) -> String {
        self.output.peek(max_chars)
    }

    /// The last `max_chars` characters of the secret-redacted output tail —
    /// the only form of task output that may be shown back to a model or a
    /// client without passing through the original tool's own path.
    pub fn redacted_peek(&self, max_chars: usize) -> String {
        self.redacted_output.peek(max_chars)
    }

    /// Redacted output at or after the absolute `cursor`: `(text, next
    /// cursor, truncated)`, where `truncated` means part of the span between
    /// `cursor` and the oldest retained character was already evicted.
    pub fn redacted_read_from(&self, cursor: u64) -> (String, u64, bool) {
        self.redacted_output.read_from(cursor)
    }

    /// Reads output since the given consumer's server-side cursor and advances it.
    /// Per-consumer cursors prevent readers stealing each other's spans.
    pub fn read_from_cursor(&self, consumer_id: &str) -> (String, bool, TaskRunStatus) {
        let mut s = self.state.lock().unwrap();
        let cursor = *s.cursors.get(consumer_id).unwrap_or(&0);
        let (text, next, truncated) = self.output.read_from(cursor);
        s.cursors.insert(consumer_id.to_owned(), next);
        (text, truncated, s.status)
    }

    pub fn to_snapshot(&self) -> TaskSnapshot {
        let s = self.state.lock().unwrap();
        TaskSnapshot {
            id: self.id.clone(),
            parent_id: self.parent_id.clone(),
            depth: self.depth,
            kind: self.kind,
            description: self.description.clone(),
            status: s.status,
            mode: s.mode,
            version: s.version,
            started_at: self.started_at,
            ended_at: s.ended_at,
            log_path: self.log_path.to_string_lossy().into_owned(),
            result: s.result.clone(),
            error: s.error.clone(),
            resolved_model: self.resolved_model.clone(),
            resumed: self.was_resumed(),
            suspend_requested: self.suspend_explicit(),
        }
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    fn task(id: &str) -> Arc<ManagedTask> {
        ManagedTask::new(
            id.to_owned(),
            None,
            1,
            TaskKind::Subagent,
            "test task".to_owned(),
            PathBuf::from("/tmp/task.log"),
            1024,
            TaskExecutionMode::Background,
        )
    }

    #[test]
    fn initial_status_is_running() {
        let t = task("task-0001");
        assert_eq!(t.status(), TaskRunStatus::Running);
        assert!(!t.is_terminal());
    }

    #[test]
    fn try_complete_transitions_to_completed() {
        let t = task("task-0001");
        let v = t.try_complete(Some("done".to_owned()));
        assert!(v.is_some());
        assert_eq!(t.status(), TaskRunStatus::Completed);
        assert!(t.is_terminal());
    }

    #[test]
    fn try_fail_transitions_to_failed() {
        let t = task("task-0001");
        t.try_fail(Some("oops".to_owned()));
        assert_eq!(t.status(), TaskRunStatus::Failed);
    }

    #[test]
    fn try_stop_transitions_to_stopped() {
        let t = task("task-0001");
        t.try_stop();
        assert_eq!(t.status(), TaskRunStatus::Stopped);
    }

    #[test]
    fn double_transition_is_no_op() {
        let t = task("task-0001");
        let v1 = t.try_complete(None);
        let v2 = t.try_complete(None); // second attempt
        assert!(v1.is_some());
        assert!(v2.is_none(), "second transition must fail");
    }

    #[test]
    fn try_append_after_terminal_is_no_op() {
        let t = task("task-0001");
        t.try_complete(None);
        let v = t.try_append("more output");
        assert!(v.is_none());
    }

    #[test]
    fn output_ring_captures_appended_text() {
        let t = task("task-0001");
        t.try_append("hello");
        t.try_append(" world");
        let (text, _, _) = t.read_incremental(0);
        assert_eq!(text, "hello world");
    }

    #[test]
    fn per_consumer_cursors_are_independent() {
        let t = task("task-0001");
        t.try_append("aaa");
        let (t1, _, _) = t.read_from_cursor("consumer-a");
        assert_eq!(t1, "aaa");
        t.try_append("bbb");
        // consumer-a gets only "bbb"; consumer-b gets all of "aaabbb"
        let (ta, _, _) = t.read_from_cursor("consumer-a");
        let (tb, _, _) = t.read_from_cursor("consumer-b");
        assert_eq!(ta, "bbb");
        assert_eq!(tb, "aaabbb");
    }

    #[tokio::test]
    async fn wait_for_completion_resolves_after_terminal_transition() {
        use tokio::time::{timeout, Duration};

        let t = task("task-0001");
        let t2 = t.clone();

        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(50)).await;
            t2.try_complete(None);
        });

        timeout(Duration::from_secs(2), t.wait_for_completion())
            .await
            .expect("wait_for_completion timed out");
    }

    #[tokio::test]
    async fn wait_for_completion_returns_immediately_when_already_terminal() {
        use tokio::time::{timeout, Duration};

        let t = task("task-0001");
        t.try_complete(None);
        timeout(Duration::from_millis(10), t.wait_for_completion())
            .await
            .expect("should return immediately");
    }
}
