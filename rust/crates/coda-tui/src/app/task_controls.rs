//! User controls for running tool work (non-blocking runtime, Phase 1):
//! background-now (Ctrl+B), send-now (Ctrl+Enter / Ctrl+G), `/stop`, and the
//! `/tasks` browser's `b` (background) and `x` (stop) keys.
//!
//! Every action is the engine's: this module only asks and reports what the
//! engine answered, never what it hoped would happen.

use coda_proto::messages::method;
use coda_proto::responses::{TaskBulkControlResult, TaskControlResult};

use super::App;
use crate::transcript::NoticeLevel;

/// The task id inside a `/tasks` row id (`<session>/<task>`).
fn task_id_of(row_id: &str) -> &str {
    row_id.rsplit_once('/').map(|(_, task)| task).unwrap_or(row_id)
}

fn refusal(task_id: &str, reason: Option<&str>) -> String {
    match reason {
        Some("notFound") => format!("Task {task_id} no longer exists."),
        Some("notRunning") => format!("Task {task_id} has already finished."),
        Some("notForeground") => {
            format!("Task {task_id} is already running in the background.")
        }
        Some(other) => format!("The engine refused the request for task {task_id} ({other})."),
        None => format!("The engine refused the request for task {task_id}."),
    }
}

impl App {
    /// Ctrl+B: stop waiting on whatever the agent is waiting for. The work
    /// keeps running; the agent is told and continues.
    pub(super) async fn background_now(&mut self) {
        if !self.require_engine("Moving work to the background") {
            return;
        }
        match self.ask::<TaskBulkControlResult>(method::BACKGROUND_ALL, None).await {
            Ok(result) if result.count > 0 => self.hint(format!(
                "Moved {} running task(s) to the background; they keep running and report \
                 back when they finish.",
                result.count
            )),
            Ok(_) => self.hint("Nothing is waiting on a running task right now."),
            Err(error) => self.notice(
                format!("Could not move work to the background: {error}"),
                NoticeLevel::Error,
            ),
        }
    }

    /// Ctrl+Enter / Ctrl+G: while a turn runs, stop waiting on tool work and
    /// deliver the composed message at once (as steering). When idle it is
    /// an ordinary submit.
    pub(super) async fn send_now(&mut self) {
        if !self.state.is_busy() {
            self.submit().await;
            return;
        }
        if self.require_engine("Send now") {
            // Quietly: the message itself is the feedback the user expects.
            let _ = self.ask::<TaskBulkControlResult>(method::BACKGROUND_ALL, None).await;
        }
        if self.composer.is_empty() {
            self.hint("Moved waiting work to the background; queued messages are delivered now.");
            return;
        }
        self.submit().await;
    }

    /// `/stop`: interrupt the turn (if one is running) and stop every running
    /// task, killing their process trees.
    pub(super) async fn stop_everything(&mut self) {
        if self.state.is_busy() {
            self.interrupt();
        }
        if !self.require_engine("/stop") {
            return;
        }
        match self.ask::<TaskBulkControlResult>(method::STOP_ALL_TASKS, None).await {
            Ok(result) if result.count > 0 => {
                self.notice(format!("Stopping {} running task(s).", result.count), NoticeLevel::Info)
            }
            Ok(_) => self.notice("No tasks were running.", NoticeLevel::Info),
            Err(error) => {
                self.notice(format!("Could not stop the running tasks: {error}"), NoticeLevel::Error)
            }
        }
    }

    /// `/tasks` `b`: stop waiting on one task.
    pub(super) async fn background_task(&mut self, row_id: &str) {
        self.task_control(method::TASK_BACKGROUND, row_id, "Moved task {} to the background.")
            .await;
    }

    /// `/tasks` `x`: stop one task.
    pub(super) async fn stop_task(&mut self, row_id: &str) {
        self.task_control(method::TASK_STOP, row_id, "Stopping task {}.").await;
    }

    async fn task_control(&mut self, rpc_method: &str, row_id: &str, done: &str) {
        let task_id = task_id_of(row_id).to_owned();
        if !self.require_engine("Controlling a task") {
            return;
        }
        let params = serde_json::json!({ "taskId": task_id });
        match self.ask::<TaskControlResult>(rpc_method, Some(params)).await {
            Ok(result) if result.ok => {
                let status = done.replace("{}", &task_id);
                self.reload_browser_with_status(status).await;
            }
            Ok(result) => {
                self.notice(refusal(&task_id, result.reason.as_deref()), NoticeLevel::Warning)
            }
            Err(error) => self.notice(
                format!("Could not reach the engine for task {task_id}: {error}"),
                NoticeLevel::Error,
            ),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_browser_row_id_names_its_task() {
        assert_eq!(task_id_of("4c1e-session/task-0007"), "task-0007");
        assert_eq!(task_id_of("task-0007"), "task-0007");
    }

    #[test]
    fn each_refusal_says_what_actually_happened() {
        assert!(refusal("task-1", Some("notFound")).contains("no longer exists"));
        assert!(refusal("task-1", Some("notRunning")).contains("already finished"));
        assert!(refusal("task-1", Some("notForeground")).contains("already running in the background"));
        assert!(refusal("task-1", Some("later")).contains("(later)"));
    }

    #[test]
    fn the_tasks_browser_backgrounds_and_stops_the_selected_task() {
        use crate::surface::browser::BrowserKind;
        use crate::surface::{Surface, SurfaceAction, SurfaceOutcome};
        use coda_proto::responses::{TaskListResult, TaskSummaryDto};
        use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

        let listing = TaskListResult {
            session_id: "s1".into(),
            tasks: vec![TaskSummaryDto {
                id: "task-0003".into(),
                parent_id: None,
                kind: "shell".into(),
                description: "cargo build".into(),
                status: "running".into(),
                mode: "foreground".into(),
                output_tail: String::new(),
                report: None,
            }],
        };
        let mut surface = App::browser_surface(BrowserKind::Tasks, crate::browsers::tasks(&listing));
        let press = |c| KeyEvent::new(KeyCode::Char(c), KeyModifiers::NONE);
        match surface.handle_key(press('b')) {
            SurfaceOutcome::Emit(SurfaceAction::BackgroundTask(id)) => {
                assert_eq!(task_id_of(&id), "task-0003")
            }
            _ => panic!("b must background the selected task"),
        }
        match surface.handle_key(press('x')) {
            SurfaceOutcome::Emit(SurfaceAction::StopTask(id)) => assert_eq!(task_id_of(&id), "task-0003"),
            _ => panic!("x must stop the selected task"),
        }
    }
}
