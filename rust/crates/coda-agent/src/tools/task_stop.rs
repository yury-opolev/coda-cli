//! `task_stop` — cancel a running task.
//!
//! Mirrors C# `BackgroundTaskStopTool` (name: `task_stop`).
//! Authorization is enforced: a subagent may only stop tasks in its own subtree;
//! unauthorized attempts and unknown ids both report "not found" to prevent
//! existence probing.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tasks::TaskActionResult;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

pub struct TaskStopTool;

#[async_trait]
impl Tool for TaskStopTool {
    fn name(&self) -> &str {
        "task_stop"
    }

    fn description(&self) -> &str {
        "Cancel a background task started with task_start. \
         The task's status will transition to stopped once it acknowledges the cancellation."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "task_id":{"type":"string","description":"The task id returned by task_start"}
          },
          "required":["task_id"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        let mgr = match ctx.get_task_manager() {
            Some(m) => m,
            None => return ToolResult::ok("Background tasks are not available in this context."),
        };

        let task_id = match input.get("task_id").and_then(Value::as_str) {
            Some(id) if !id.trim().is_empty() => id,
            _ => return ToolResult::error("Missing required 'task_id'."),
        };

        let caller_id = ctx.caller_task_id.as_deref();
        match mgr.request_stop(task_id, caller_id) {
            TaskActionResult::Ok => ToolResult::ok(format!("Task '{task_id}' has been stopped.")),
            // NotFound and Denied share identical wording so a subagent cannot distinguish
            // a task it is not allowed to stop from one that does not exist.
            TaskActionResult::NotFound | TaskActionResult::Denied => {
                ToolResult::ok(format!("Task '{task_id}' not found."))
            }
            TaskActionResult::InvalidState => ToolResult::ok(format!(
                "Task '{task_id}' is already finished and cannot be stopped."
            )),
            _ => ToolResult::ok(format!("Task '{task_id}' cannot be stopped.")),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tasks::{TaskExecutionMode, TaskKind, TaskManager};
    use std::sync::Arc;

    fn ctx(mgr: Arc<TaskManager>) -> ToolContext {
        ToolContext::new(".").with_task_manager(mgr)
    }

    fn ctx_with_caller(mgr: Arc<TaskManager>, caller_id: &str) -> ToolContext {
        ToolContext::new(".")
            .with_task_manager(mgr)
            .with_caller_task_id(caller_id)
    }

    #[tokio::test]
    async fn stop_missing_task_manager_reports_not_available() {
        let result = TaskStopTool
            .execute(&serde_json::json!({"task_id": "task-0001"}), &ToolContext::new("."), CancellationToken::new())
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert_eq!(result.content, "Background tasks are not available in this context.");
    }

    #[tokio::test]
    async fn stop_missing_task_id_returns_error() {
        let m = TaskManager::with_defaults("s");
        let result = TaskStopTool
            .execute(&serde_json::json!({}), &ctx(m), CancellationToken::new())
            .await;
        assert!(result.is_error, "{}", result.content);
        assert_eq!(result.content, "Missing required 'task_id'.");
    }

    #[tokio::test]
    async fn stop_unknown_id_reports_not_found() {
        let m = TaskManager::with_defaults("s");
        let result = TaskStopTool
            .execute(&serde_json::json!({"task_id": "ghost"}), &ctx(m), CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert_eq!(result.content, "Task 'ghost' not found.");
    }

    #[tokio::test]
    async fn stop_running_task_reports_stopped_and_cancels_token() {
        let m = TaskManager::with_defaults("s");
        let t = m
            .register(TaskKind::Subagent, "d", None, TaskExecutionMode::Background)
            .unwrap();
        let cancel_token = t.cancel.clone();

        let result = TaskStopTool
            .execute(&serde_json::json!({"task_id": t.id}), &ctx(m), CancellationToken::new())
            .await;

        assert!(!result.is_error);
        assert_eq!(result.content, format!("Task '{}' has been stopped.", t.id));
        assert!(cancel_token.is_cancelled(), "cancellation token must be fired");
    }

    #[tokio::test]
    async fn stop_finished_task_reports_already_finished() {
        let m = TaskManager::with_defaults("s");
        let t = m
            .register(TaskKind::Subagent, "d", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, Some("done".into()));

        let result = TaskStopTool
            .execute(&serde_json::json!({"task_id": t.id}), &ctx(m), CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert_eq!(
            result.content,
            format!("Task '{}' is already finished and cannot be stopped.", t.id)
        );
    }

    /// SECURITY: a subagent must not be able to stop an unrelated task.
    /// The denial must look identical to "not found" to prevent existence probing.
    #[tokio::test]
    async fn stop_denied_for_sibling_looks_like_not_found() {
        let m = TaskManager::with_defaults("s");
        let a = m
            .register(TaskKind::Subagent, "a", None, TaskExecutionMode::Background)
            .unwrap();
        let b = m
            .register(TaskKind::Subagent, "b", None, TaskExecutionMode::Background)
            .unwrap();

        let result = TaskStopTool
            .execute(
                &serde_json::json!({"task_id": b.id}),
                &ctx_with_caller(m.clone(), &a.id),
                CancellationToken::new(),
            )
            .await;

        assert!(!result.is_error);
        assert_eq!(
            result.content,
            format!("Task '{}' not found.", b.id),
            "denied stop must use not-found wording"
        );
        // b must still be Running — the stop was denied.
        use crate::tasks::TaskRunStatus;
        assert_eq!(m.get(&b.id).unwrap().status, TaskRunStatus::Running);
    }
}
