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
    Completed,
    Failed,
    Stopped,
}

impl TaskRunStatus {
    pub fn is_terminal(self) -> bool {
        !matches!(self, TaskRunStatus::Running)
    }
}

/// The kind of work a managed task represents.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum TaskKind {
    Subagent,
    Shell,
    Scheduled,
}

/// Whether a managed task runs in the foreground or the background.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum TaskExecutionMode {
    Foreground,
    Background,
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
    /// Steering inbox for subagent tasks; `None` for shell tasks.
    pub steering: Option<Arc<SteeringInbox>>,
    output: OutputRing,
    state: Mutex<TaskState>,
    /// Signalled (notify_waiters) when the task first transitions to terminal.
    completion: Arc<Notify>,
    /// Atomic flag so `wait_for_completion` can detect terminal without the lock.
    terminal_flag: std::sync::atomic::AtomicBool,
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
        Arc::new(Self {
            id,
            parent_id,
            depth,
            kind,
            description,
            log_path,
            started_at: SystemTime::now(),
            cancel: CancellationToken::new(),
            resolved_model: None,
            steering: None,
            output: OutputRing::new(output_ring_bytes),
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
        })
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

    /// Atomically promotes a foreground Running task to the background. Returns
    /// `Some(version)` on success, `None` when already terminal or already background.
    pub fn try_promote_to_background(&self) -> Option<u64> {
        let mut s = self.state.lock().unwrap();
        if s.status != TaskRunStatus::Running || s.mode != TaskExecutionMode::Foreground {
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
            if s.status != TaskRunStatus::Running {
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
        if s.status != TaskRunStatus::Running {
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
