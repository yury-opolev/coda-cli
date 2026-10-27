//! Task lifecycle events for clients (non-blocking runtime, Phase 3):
//! `event/taskStarted`, throttled `event/taskOutput`, and `event/taskCompleted`
//! with the owning agent.
//!
//! [`TaskEventPublisher`] is the `TaskManager`'s [`TaskEventObserver`]. Start
//! and completion frames are published as they happen. Output is never
//! copied here: an output notification only marks the task dirty, and a
//! flusher reads the task's own bounded, secret-redacted tail from a private
//! cursor when the task is due — at most [`OUTPUT_FRAMES_PER_SECOND`] frames
//! per task, each at most [`OUTPUT_FRAME_MAX_CHARS`] characters (newest
//! kept, `truncated` set when anything was skipped). While the connection is
//! congested (see [`OutboundHealth`]) nothing is sent and nothing is
//! buffered beyond that cursor, so a client that never reads cannot make the
//! engine's memory grow with task output.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock, Weak};
use std::time::Duration;

use coda_agent::tasks::{ManagedTask, TaskEventObserver, TaskRunStatus};
use coda_proto::events::Event;
use tokio::sync::Notify;
use tokio::time::Instant;

use crate::bus::{EventBus, OutboundHealth};

/// Most `event/taskOutput` frames per task per second.
pub const OUTPUT_FRAMES_PER_SECOND: u32 = 4;
/// Longest `text` one `event/taskOutput` frame carries.
pub const OUTPUT_FRAME_MAX_CHARS: usize = 2_000;
/// Longest `report` carried by `event/taskCompleted`.
pub const COMPLETED_REPORT_MAX_CHARS: usize = 4_000;

const OUTPUT_INTERVAL: Duration =
    Duration::from_millis(1_000 / OUTPUT_FRAMES_PER_SECOND as u64);

/// Where one running task's output stream stands. No text is ever held.
struct OutputState {
    task: Arc<ManagedTask>,
    /// Absolute character cursor into the task's redacted tail.
    cursor: u64,
    last_sent: Option<Instant>,
    dirty: bool,
}

/// Publishes task lifecycle events on the shared [`EventBus`].
pub struct TaskEventPublisher {
    bus: Arc<EventBus>,
    health: Arc<OutboundHealth>,
    outputs: Mutex<HashMap<String, OutputState>>,
    wake: Arc<Notify>,
    flusher: OnceLock<()>,
    self_ref: OnceLock<Weak<TaskEventPublisher>>,
}

impl TaskEventPublisher {
    pub fn new(bus: Arc<EventBus>) -> Arc<Self> {
        let health = bus.outbound_health();
        let publisher = Arc::new(Self {
            bus,
            health,
            outputs: Mutex::new(HashMap::new()),
            wake: Arc::new(Notify::new()),
            flusher: OnceLock::new(),
            self_ref: OnceLock::new(),
        });
        let _ = publisher.self_ref.set(Arc::downgrade(&publisher));
        publisher
    }

    fn publish(&self, event: Event) {
        if let Some((method, params)) = event.to_notification() {
            self.bus.publish(&method, params, false);
        }
    }

    /// Starts the flusher on first use. Lazy because the host is built
    /// synchronously, sometimes with no Tokio runtime at all.
    fn ensure_flusher(&self) {
        if self.flusher.get().is_some() {
            return;
        }
        let Ok(runtime) = tokio::runtime::Handle::try_current() else { return };
        let Some(me) = self.self_ref.get().cloned() else { return };
        if self.flusher.set(()).is_err() {
            return;
        }
        let wake = Arc::clone(&self.wake);
        runtime.spawn(async move {
            loop {
                let notified = wake.notified();
                tokio::pin!(notified);
                notified.as_mut().enable();
                // Holds the publisher only while flushing, never while
                // parked: dropping the host must be able to drop it.
                let next = match me.upgrade() {
                    Some(publisher) => publisher.flush_due(Instant::now()),
                    None => return,
                };
                match next {
                    Some(due) => {
                        tokio::select! {
                            _ = tokio::time::sleep_until(due) => {}
                            _ = &mut notified => {}
                        }
                    }
                    None => notified.await,
                }
            }
        });
    }

    /// Sends every dirty task's new output that is due at `now`. Returns
    /// when the next dirty task becomes due, or `None` when none is dirty.
    fn flush_due(&self, now: Instant) -> Option<Instant> {
        let mut outputs = self.outputs.lock().unwrap_or_else(|p| p.into_inner());
        let mut next: Option<Instant> = None;
        let congested = self.health.congested();
        for state in outputs.values_mut().filter(|s| s.dirty) {
            let due = state.last_sent.map(|t| t + OUTPUT_INTERVAL).unwrap_or(now);
            if due > now || congested {
                let retry = if congested { now + OUTPUT_INTERVAL } else { due };
                next = Some(next.map_or(retry, |n| n.min(retry)));
                continue;
            }
            self.send_output(state, now);
        }
        next
    }

    /// Publishes one bounded frame of `state`'s unsent output, if any.
    /// Called with the outputs lock held so a task's output frames and its
    /// completion are published in order.
    fn send_output(&self, state: &mut OutputState, now: Instant) {
        state.dirty = false;
        let (text, next, evicted) = state.task.redacted_read_from(state.cursor);
        state.cursor = next;
        if text.is_empty() {
            return;
        }
        let total = text.chars().count();
        let (text, truncated) = if total > OUTPUT_FRAME_MAX_CHARS {
            (text.chars().skip(total - OUTPUT_FRAME_MAX_CHARS).collect(), true)
        } else {
            (text, evicted)
        };
        state.last_sent = Some(now);
        self.publish(Event::TaskOutput { task_id: state.task.id.clone(), text, truncated });
    }

    #[cfg(test)]
    pub(crate) fn tracked_tasks(&self) -> usize {
        self.outputs.lock().unwrap().len()
    }
}

impl Drop for TaskEventPublisher {
    fn drop(&mut self) {
        self.wake.notify_waiters();
    }
}

impl TaskEventObserver for TaskEventPublisher {
    fn on_started(&self, task: &Arc<ManagedTask>) {
        let mut outputs = self.outputs.lock().unwrap_or_else(|p| p.into_inner());
        outputs.insert(
            task.id.clone(),
            OutputState { task: Arc::clone(task), cursor: 0, last_sent: None, dirty: false },
        );
        self.publish(Event::TaskStarted {
            task_id: task.id.clone(),
            kind: task.kind.as_str().to_owned(),
            description: task.description.clone(),
            owner_task_id: task.parent_id.clone(),
            mode: task.mode().as_str().to_owned(),
        });
    }

    fn on_output(&self, task: &Arc<ManagedTask>) {
        {
            let mut outputs = self.outputs.lock().unwrap_or_else(|p| p.into_inner());
            match outputs.get_mut(&task.id) {
                Some(state) if !state.dirty => state.dirty = true,
                _ => return,
            }
        }
        self.ensure_flusher();
        self.wake.notify_one();
    }

    fn on_terminal(&self, task: &Arc<ManagedTask>, status: TaskRunStatus) {
        let mut outputs = self.outputs.lock().unwrap_or_else(|p| p.into_inner());
        // The last of the output goes out before the completion, unless the
        // client is not keeping up — then the completion alone is sent.
        if let Some(mut state) = outputs.remove(&task.id) {
            if !self.health.congested() {
                self.send_output(&mut state, Instant::now());
            }
        }
        let snapshot = task.to_snapshot();
        let report = snapshot.result.or(snapshot.error).map(|text| {
            let mut bounded: String = text.chars().take(COMPLETED_REPORT_MAX_CHARS).collect();
            if text.chars().count() > COMPLETED_REPORT_MAX_CHARS {
                bounded.push_str("\n[report truncated]");
            }
            bounded
        });
        self.publish(Event::TaskCompleted {
            task_id: task.id.clone(),
            status: status.as_str().to_owned(),
            description: task.description.clone(),
            report,
            owner_task_id: task.parent_id.clone(),
            kind: Some(task.kind.as_str().to_owned()),
            mode: Some(snapshot.mode.as_str().to_owned()),
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_agent::tasks::{TaskExecutionMode, TaskKind, TaskManager};
    use serde_json::Value;
    use tokio::sync::mpsc;

    struct Fixture {
        manager: Arc<TaskManager>,
        publisher: Arc<TaskEventPublisher>,
        bus: Arc<EventBus>,
        rx: mpsc::UnboundedReceiver<Vec<u8>>,
    }

    fn fixture() -> Fixture {
        let (tx, rx) = mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, "engine-1"));
        let manager = TaskManager::new(
            "task-events",
            Some(std::env::temp_dir().join("coda-task-events-tests")),
            64 * 1024,
            16,
        );
        let publisher = TaskEventPublisher::new(Arc::clone(&bus));
        manager.set_event_observer(publisher.clone());
        Fixture { manager, publisher, bus, rx }
    }

    fn frames(rx: &mut mpsc::UnboundedReceiver<Vec<u8>>) -> Vec<(String, Value)> {
        let mut out = Vec::new();
        while let Ok(frame) = rx.try_recv() {
            let text = String::from_utf8(frame).unwrap();
            let body = &text[text.find("\r\n\r\n").unwrap() + 4..];
            let value: Value = serde_json::from_str(body).unwrap();
            out.push((value["method"].as_str().unwrap().to_owned(), value["params"].clone()));
        }
        out
    }

    #[tokio::test]
    async fn started_and_completed_carry_kind_owner_and_mode() {
        let mut f = fixture();
        let agent = f
            .manager
            .register(TaskKind::Subagent, "deep review", None, TaskExecutionMode::Background)
            .unwrap();
        let shell = f
            .manager
            .register(TaskKind::Shell, "cargo test", Some(&agent.id), TaskExecutionMode::Foreground)
            .unwrap();
        f.manager.fail(&shell.id, Some("exit code: 101".into()));
        f.manager.complete(&agent.id, Some("all good".into()));

        let seen = frames(&mut f.rx);
        let methods: Vec<&str> = seen.iter().map(|(m, _)| m.as_str()).collect();
        assert_eq!(methods, [
            "event/taskStarted", "event/taskStarted", "event/taskCompleted", "event/taskCompleted",
        ]);
        let started = &seen[1].1;
        assert_eq!(started["taskId"], shell.id.as_str());
        assert_eq!(started["kind"], "shell");
        assert_eq!(started["ownerTaskId"], agent.id.as_str());
        assert_eq!(started["mode"], "foreground");
        assert!(seen[0].1.get("ownerTaskId").is_none(), "the main agent's task has no owner id");

        let failed = &seen[2].1;
        assert_eq!(failed["status"], "failed");
        assert_eq!(failed["ownerTaskId"], agent.id.as_str());
        assert_eq!(failed["kind"], "shell");
        assert_eq!(failed["report"], "exit code: 101");
        let done = &seen[3].1;
        assert_eq!((done["status"].as_str(), done["mode"].as_str()), (Some("completed"), Some("background")));
        assert!(done.get("ownerTaskId").is_none());
        assert_eq!(f.publisher.tracked_tasks(), 0, "nothing is retained for finished tasks");
    }

    #[tokio::test(start_paused = true)]
    async fn output_is_throttled_per_task_and_bounded_per_frame() {
        let mut f = fixture();
        let task = f.manager.register(TaskKind::Shell, "noisy", None, TaskExecutionMode::Background).unwrap();
        frames(&mut f.rx);

        // 2 s of output, one line every 10 ms.
        for i in 0..200 {
            f.manager.append_output(&task.id, &format!("{i:04} {}\n", "x".repeat(40)));
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        tokio::time::sleep(Duration::from_millis(500)).await;

        let outputs: Vec<Value> = frames(&mut f.rx)
            .into_iter()
            .filter(|(m, _)| m == "event/taskOutput")
            .map(|(_, p)| p)
            .collect();
        assert!(!outputs.is_empty());
        assert!(
            outputs.len() <= 2 * OUTPUT_FRAMES_PER_SECOND as usize + 2,
            "at most {OUTPUT_FRAMES_PER_SECOND}/s per task, got {}",
            outputs.len()
        );
        for frame in &outputs {
            assert!(frame["text"].as_str().unwrap().chars().count() <= OUTPUT_FRAME_MAX_CHARS);
        }
        let all: String = outputs.iter().map(|p| p["text"].as_str().unwrap()).collect();
        assert!(all.contains("0199 "), "the newest output is delivered: {}", all.len());
        f.manager.stop(&task.id);
    }

    #[tokio::test(start_paused = true)]
    async fn a_client_that_never_reads_gets_no_output_and_costs_no_memory() {
        let mut f = fixture();
        f.bus.outbound_health().force_congested(true);
        let task = f.manager.register(TaskKind::Shell, "flood", None, TaskExecutionMode::Background).unwrap();
        for _ in 0..500 {
            f.manager.append_output(&task.id, &"y".repeat(1_000));
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        tokio::time::sleep(Duration::from_secs(2)).await;
        let held = frames(&mut f.rx);
        assert!(
            held.iter().all(|(m, _)| m != "event/taskOutput"),
            "nothing optional is queued for a stalled client"
        );
        assert_eq!(f.publisher.tracked_tasks(), 1, "one cursor per live task, never the text");

        // The client catches up: one bounded frame, marked as skipping.
        f.bus.outbound_health().force_congested(false);
        f.manager.append_output(&task.id, "tail\n");
        tokio::time::sleep(Duration::from_secs(1)).await;
        let outputs: Vec<Value> = frames(&mut f.rx)
            .into_iter()
            .filter(|(m, _)| m == "event/taskOutput")
            .map(|(_, p)| p)
            .collect();
        assert_eq!(outputs.len(), 1, "{outputs:?}");
        assert_eq!(outputs[0]["truncated"], true);
        let text = outputs[0]["text"].as_str().unwrap();
        assert!(text.ends_with("tail\n") && text.chars().count() <= OUTPUT_FRAME_MAX_CHARS);
        f.manager.stop(&task.id);
    }

    #[tokio::test]
    async fn the_last_output_precedes_the_completion() {
        let mut f = fixture();
        let task = f.manager.register(TaskKind::Shell, "build", None, TaskExecutionMode::Foreground).unwrap();
        f.manager.append_output(&task.id, "Finished\n");
        f.manager.complete(&task.id, Some("exit code: 0".into()));
        let methods: Vec<String> = frames(&mut f.rx).into_iter().map(|(m, _)| m).collect();
        let output = methods.iter().position(|m| m == "event/taskOutput").expect("output sent");
        let completed = methods.iter().position(|m| m == "event/taskCompleted").unwrap();
        assert!(output < completed, "{methods:?}");
    }
}
