//! User controls for running tool work (non-blocking runtime, Phases 1–2):
//! background-now (Ctrl+B), `/stop`,
//! `/send <task> <message>`, and the `/tasks` browser's `b` (background),
//! `x` (stop) and `m` (message) keys.
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

/// Splits `/send` arguments into the task id and the message.
fn parse_send_args(args: &str) -> Option<(String, String)> {
    let (task_id, text) = args.trim().split_once(char::is_whitespace)?;
    let text = text.trim();
    (!text.is_empty()).then(|| (task_id.to_owned(), text.to_owned()))
}

fn refusal(task_id: &str, reason: Option<&str>) -> String {
    match reason {
        Some("notFound") => format!("Task {task_id} no longer exists."),
        Some("notRunning") => format!("Task {task_id} has already finished."),
        Some("notForeground") => {
            format!("Task {task_id} is already running in the background.")
        }
        Some("notBackgroundable") => format!(
            "Task {task_id} cannot be moved to the background (only shell commands can)."
        ),
        Some("notAgent") => {
            format!("Task {task_id} is a command, not an agent; it cannot receive messages.")
        }
        Some("notAccepting") => {
            format!("Task {task_id} is finishing and no longer accepts messages.")
        }
        Some("emptyText") => "Type a message after the task id.".to_owned(),
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

    /// A `/tasks` row action (`b`, `x`, `m`).
    pub(super) async fn task_row_action(&mut self, action: crate::surface::SurfaceAction) {
        use crate::surface::SurfaceAction as A;
        match action {
            A::BackgroundTask(id) => self.background_task(&id).await,
            A::StopTask(id) => self.stop_task(&id).await,
            A::MessageTask(id) => self.compose_task_message(&id),
            _ => {}
        }
    }

    /// `/tasks` `m`: close the browser and start a `/send` to the task.
    pub(super) fn compose_task_message(&mut self, row_id: &str) {
        let task_id = task_id_of(row_id).to_owned();
        self.surfaces.pop();
        self.composer.set_text(format!("/send {task_id} "));
        self.hint(format!("Type your message for task {task_id} and press Enter."));
        self.dirty = true;
    }

    /// `/tasks` `h`: switch between the active and history views, keeping
    /// the selected task where it is still listed.
    pub(super) async fn toggle_task_history(&mut self) {
        self.task_view = self.task_view.toggled();
        self.reload_browser_with_status(String::new()).await;
    }

    /// `/tasks [all]`: opens the active view, or the history view with `all`.
    pub(super) async fn open_tasks(&mut self, arg: Option<&str>) {
        self.task_view = match arg {
            None => crate::browsers::TaskView::Active,
            Some("all" | "history") => crate::browsers::TaskView::History,
            Some(other) => {
                self.notice(
                    format!("Unknown /tasks view '{other}'. Usage: /tasks [all]"),
                    NoticeLevel::Warning,
                );
                return;
            }
        };
        self.open_browser(crate::surface::browser::BrowserKind::Tasks).await;
    }

    /// Re-reads an open `/tasks` browser after a task started or ended (or a
    /// resync). One read per owed refresh, keeping the selection by task id
    /// and whatever status the browser was showing.
    pub(super) async fn refresh_task_browser(&mut self) {
        self.needs_task_refresh = false;
        self.reload_browser_of(Some(crate::surface::browser::BrowserKind::Tasks), None).await;
    }

    /// `/send <task-id> <message>`: deliver a message to a running subagent.
    /// It reaches the subagent's inbox, cutting its current wait short or
    /// waking it if it is parked.
    pub(super) async fn send_task_message(&mut self, args: &str) {
        let Some((task_id, text)) = parse_send_args(args) else {
            self.notice("Usage: /send <task-id> <message>", NoticeLevel::Warning);
            return;
        };
        if !self.require_engine("Messaging a task") {
            return;
        }
        let params = serde_json::json!({ "taskId": task_id, "text": text });
        match self.ask::<TaskControlResult>(method::TASK_SEND, Some(params)).await {
            Ok(result) if result.ok => {
                self.notice(format!("Message sent to task {task_id}."), NoticeLevel::Info)
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
    fn send_arguments_split_into_the_task_and_the_whole_message() {
        assert_eq!(
            parse_send_args("  task-0004   use the dry run, please "),
            Some(("task-0004".into(), "use the dry run, please".into()))
        );
        assert_eq!(parse_send_args("task-0004"), None);
        assert_eq!(parse_send_args("task-0004   "), None);
        assert_eq!(parse_send_args(""), None);
        assert!(refusal("task-1", Some("notAgent")).contains("cannot receive messages"));
    }

    #[tokio::test]
    async fn task_events_drive_the_running_task_count() {
        use crate::app::serve::tests::app_with;
        use serde_json::json;

        let mut harness = app_with(crate::local::AccessMode::TrustedLocal, |_, _| json!({}));
        let app = &mut harness.app;
        app.needs_rehydrate = false;
        app.needs_resync = false;
        let frames = [
            ("event/taskStarted", json!({ "taskId": "task-0001", "kind": "shell", "description": "build", "mode": "foreground" })),
            ("event/taskStarted", json!({ "taskId": "task-0002", "kind": "subagent", "description": "review", "mode": "background" })),
            ("event/taskOutput", json!({ "taskId": "task-0001", "text": "Compiling\n", "truncated": false })),
            ("event/taskCompleted", json!({ "taskId": "task-0001", "status": "completed", "description": "build", "mode": "foreground" })),
        ];
        let mut counts = Vec::new();
        for (seq, (method, mut params)) in frames.into_iter().enumerate() {
            params["seq"] = json!(seq as i64 + 1);
            params["engineInstanceId"] = json!("e1");
            for frame in app.accept(method.to_owned(), Some(params)) {
                app.dispatch_frame(frame);
            }
            counts.push(app.state.running_tasks.len());
        }
        assert_eq!(counts, [1, 2, 2, 1]);
        assert!(
            crate::app::serve::tests::notices(app).iter().all(|n| !n.contains("build")),
            "a command the agent waited for is not announced twice"
        );
    }

    /// A fake engine registry whose `session/listTasks` honours `statusFilter`
    /// and records every filter it was asked for.
    fn registry_engine(
        tasks: std::sync::Arc<std::sync::Mutex<Vec<(&'static str, &'static str)>>>,
        filters: std::sync::Arc<std::sync::Mutex<Vec<String>>>,
        cursor: std::sync::Arc<std::sync::atomic::AtomicI64>,
    ) -> crate::app::serve::tests::Harness {
        use serde_json::json;
        crate::app::serve::tests::app_with(crate::local::AccessMode::TrustedLocal, move |method, params| {
            if method == "session/getState" {
                return crate::app::serve::tests::snapshot_value(cursor.load(std::sync::atomic::Ordering::SeqCst), 0, 500);
            }
            if method != "session/listTasks" {
                return json!({});
            }
            let filter = params["statusFilter"].as_str().unwrap_or("all").to_owned();
            filters.lock().unwrap().push(filter.clone());
            let rows: Vec<_> = tasks.lock().unwrap().iter()
                .filter(|(_, status)| match filter.as_str() {
                    "active" => *status == "running",
                    "terminal" => *status != "running",
                    _ => true,
                })
                .map(|(id, status)| json!({
                    "id": id, "parentId": null, "kind": "shell", "description": format!("{id} work"),
                    "status": status, "mode": "background", "outputTail": "", "report": null,
                }))
                .collect();
            json!({ "sessionId": "s1", "tasks": rows })
        })
    }

    fn press(c: char) -> crossterm::event::KeyEvent {
        crossterm::event::KeyEvent::new(crossterm::event::KeyCode::Char(c), crossterm::event::KeyModifiers::NONE)
    }

    fn deliver(app: &mut App, cursor: &std::sync::atomic::AtomicI64, seq: i64, method: &str, mut params: serde_json::Value) {
        cursor.store(seq, std::sync::atomic::Ordering::SeqCst);
        params["seq"] = serde_json::json!(seq);
        params["engineInstanceId"] = serde_json::json!("e1");
        for frame in app.accept(method.to_owned(), Some(params)) {
            app.dispatch_frame(frame);
        }
    }

    #[tokio::test]
    async fn the_tasks_browser_follows_task_lifecycle_events_and_keeps_the_selection() {
        use serde_json::json;
        use std::sync::{Arc, Mutex};

        let tasks = Arc::new(Mutex::new(vec![
            ("task-00000001", "running"),
            ("task-00000002", "completed"),
            ("task-00000003", "running"),
            ("task-00000004", "running"),
        ]));
        let filters = Arc::new(Mutex::new(Vec::new()));
        let cursor = Arc::new(std::sync::atomic::AtomicI64::new(0));
        let mut harness = registry_engine(tasks.clone(), filters.clone(), cursor.clone());
        let app = &mut harness.app;
        app.needs_rehydrate = false;
        app.needs_resync = false;
        app.needs_pending_messages = false;

        app.run_command(crate::commands::parse("/tasks").unwrap()).await;
        assert_eq!(app.browser().unwrap().title(), "Tasks — active (3)");
        assert_eq!(filters.lock().unwrap().as_slice(), ["active"]);
        app.on_key(press('j')).await;
        assert_eq!(app.browser().unwrap().selected_id(), Some("s1/task-00000003"));

        // An unrelated task ends: the selection stays on its task.
        tasks.lock().unwrap()[0].1 = "completed";
        deliver(app, &cursor, 1, "event/taskCompleted", json!({ "taskId": "task-00000001", "status": "completed", "description": "x" }));
        app.settle_with_engine().await;
        let browser = app.browser().unwrap();
        assert_eq!(browser.title(), "Tasks — active (2)");
        assert_eq!(browser.selected_id(), Some("s1/task-00000003"));

        // The selected task ends: the selection moves to a remaining one.
        tasks.lock().unwrap()[2].1 = "failed";
        deliver(app, &cursor, 2, "event/taskCompleted", json!({ "taskId": "task-00000003", "status": "failed", "description": "x" }));
        app.settle_with_engine().await;
        assert_eq!(app.browser().unwrap().selected_id(), Some("s1/task-00000004"));

        // A new task appears without any polling.
        tasks.lock().unwrap().push(("task-00000005", "running"));
        deliver(app, &cursor, 3, "event/taskStarted", json!({ "taskId": "task-00000005", "kind": "shell", "description": "x", "mode": "background" }));
        app.settle_with_engine().await;
        assert_eq!(app.browser().unwrap().title(), "Tasks — active (2)");
        assert_eq!(app.browser().unwrap().selected_id(), Some("s1/task-00000004"));

        // The last active tasks end: the empty state, not a stale row.
        for row in tasks.lock().unwrap().iter_mut() {
            row.1 = "stopped";
        }
        deliver(app, &cursor, 4, "event/taskCompleted", json!({ "taskId": "task-00000004", "status": "stopped", "description": "x" }));
        deliver(app, &cursor, 5, "event/taskCompleted", json!({ "taskId": "task-00000005", "status": "stopped", "description": "x" }));
        app.settle_with_engine().await;
        let browser = app.browser().unwrap();
        assert_eq!(browser.title(), "Tasks — active (0)");
        assert!(browser.is_empty() && browser.selected_id().is_none());
        assert_eq!(browser.empty_message(), Some("No active tasks"));

        let asked = filters.lock().unwrap().len();
        app.settle_with_engine().await;
        assert_eq!(filters.lock().unwrap().len(), asked, "no event, no re-read");
    }

    #[tokio::test]
    async fn h_toggles_between_the_active_and_history_views() {
        use std::sync::{Arc, Mutex};

        let tasks = Arc::new(Mutex::new(vec![("task-00000001", "completed"), ("task-00000002", "running")]));
        let filters = Arc::new(Mutex::new(Vec::new()));
        let mut harness = registry_engine(tasks, filters.clone(), Default::default());
        let app = &mut harness.app;

        app.run_command(crate::commands::parse("/tasks").unwrap()).await;
        assert_eq!(app.browser().unwrap().title(), "Tasks — active (1)");
        app.on_key(press('h')).await;
        assert_eq!(app.browser().unwrap().title(), "Tasks — history (2)");
        assert_eq!(app.browser().unwrap().selected_id(), Some("s1/task-00000002"),
            "the selected task stays selected across views");
        app.on_key(press('h')).await;
        assert_eq!(app.browser().unwrap().title(), "Tasks — active (1)");
        app.close_browser();

        app.run_command(crate::commands::parse("/tasks all").unwrap()).await;
        assert_eq!(app.browser().unwrap().title(), "Tasks — history (2)");
        app.close_browser();
        app.run_command(crate::commands::parse("/tasks").unwrap()).await;
        assert_eq!(app.browser().unwrap().title(), "Tasks — active (1)", "bare /tasks is the active view");
        assert_eq!(filters.lock().unwrap().as_slice(), ["active", "all", "active", "all", "active"]);
    }

    #[tokio::test]
    async fn a_resync_rereads_an_open_tasks_browser() {
        use std::sync::{Arc, Mutex};

        let tasks = Arc::new(Mutex::new(vec![("task-00000001", "running")]));
        let filters = Arc::new(Mutex::new(Vec::new()));
        let cursor = Arc::new(std::sync::atomic::AtomicI64::new(0));
        let mut harness = registry_engine(tasks.clone(), filters.clone(), cursor.clone());
        let app = &mut harness.app;
        app.needs_rehydrate = false;
        app.needs_pending_messages = false;
        app.needs_resync = false;
        app.run_command(crate::commands::parse("/tasks").unwrap()).await;
        assert_eq!(app.browser().unwrap().title(), "Tasks — active (1)");

        // The task ended while the client was not listening (a reconnect).
        tasks.lock().unwrap()[0].1 = "completed";
        app.needs_resync = true;
        app.settle_with_engine().await;
        assert_eq!(app.browser().unwrap().title(), "Tasks — active (0)");
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
        let mut surface = App::browser_surface(BrowserKind::Tasks, crate::browsers::tasks(&listing, crate::browsers::TaskView::Active));
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
        match surface.handle_key(press('m')) {
            SurfaceOutcome::Emit(SurfaceAction::MessageTask(id)) => {
                assert_eq!(task_id_of(&id), "task-0003")
            }
            _ => panic!("m must start a message to the selected task"),
        }
    }
}
