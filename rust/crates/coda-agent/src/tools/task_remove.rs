//! `task_remove` — remove a terminal task from the registry.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tasks::TaskActionResult;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

pub struct TaskRemoveTool;

#[async_trait]
impl Tool for TaskRemoveTool {
    fn name(&self) -> &str {
        "task_remove"
    }

    fn description(&self) -> &str {
        "Remove a terminal (completed/failed/stopped) task from the registry. \
         Returns an error when the task is still running — stop it first."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "taskId":{"type":"string","description":"The task id to remove"}
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

        match mgr.remove(task_id) {
            TaskActionResult::Ok => ToolResult::ok(format!("Task '{task_id}' removed.")),
            TaskActionResult::NotFound | TaskActionResult::Denied => {
                ToolResult::error(format!("Task '{task_id}' not found."))
            }
            TaskActionResult::Rejected => ToolResult::error(format!(
                "Task '{task_id}' is still running; stop it before removing."
            )),
            TaskActionResult::InvalidState => ToolResult::error(format!(
                "Task '{task_id}' cannot be removed in its current state."
            )),
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

    #[tokio::test]
    async fn remove_terminal_task() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, None);
        let result = TaskRemoveTool
            .execute(&serde_json::json!({"taskId": t.id}), &ctx(m.clone()), CancellationToken::new())
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert!(m.get(&t.id).is_none(), "task still in registry");
    }

    #[tokio::test]
    async fn remove_running_task_returns_error() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        let result = TaskRemoveTool
            .execute(&serde_json::json!({"taskId": t.id}), &ctx(m), CancellationToken::new())
            .await;
        assert!(result.is_error);
        assert!(result.content.contains("still running"), "{}", result.content);
    }
}
