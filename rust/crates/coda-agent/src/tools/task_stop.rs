//! `task_stop` — cancel a running task.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tasks::TaskRunStatus;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

pub struct TaskStopTool;

#[async_trait]
impl Tool for TaskStopTool {
    fn name(&self) -> &str {
        "task_stop"
    }

    fn description(&self) -> &str {
        "Request cancellation of a running task. The task will reach a terminal state \
         asynchronously; poll task_get to confirm. A no-op when the task is already terminal."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "taskId":{"type":"string","description":"The id of the task to stop"}
          },
          "required":["taskId"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        false
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        let task_id = match input.get("taskId").and_then(Value::as_str) {
            Some(id) => id,
            None => return ToolResult::error("Missing required 'taskId'."),
        };

        let mgr = match ctx.get_task_manager() {
            Some(m) => m,
            None => return ToolResult::error("Task manager is not available."),
        };

        let snap = match mgr.get(task_id) {
            Some(s) => s,
            None => return ToolResult::error(format!("Task '{task_id}' not found.")),
        };

        if snap.status != TaskRunStatus::Running {
            return ToolResult::ok(format!(
                "Task '{task_id}' is already terminal ({:?}); nothing to stop.",
                snap.status
            ));
        }

        // Signal cancellation; the task runner observes it and will transition to Stopped.
        mgr.cancel(task_id);
        ToolResult::ok(format!("Cancellation requested for task '{task_id}'."))
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

    #[tokio::test]
    async fn stop_running_task_signals_cancel() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        let cancel_token = t.cancel.clone();
        let result = TaskStopTool
            .execute(&serde_json::json!({"taskId": t.id}), &ctx(m), CancellationToken::new())
            .await;
        assert!(!result.is_error, "{}", result.content);
        // The cancellation token should now be cancelled.
        assert!(cancel_token.is_cancelled(), "token not cancelled");
    }

    #[tokio::test]
    async fn stop_terminal_task_is_no_op() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, None);
        let result = TaskStopTool
            .execute(&serde_json::json!({"taskId": t.id}), &ctx(m), CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("already terminal"), "{}", result.content);
    }
}
