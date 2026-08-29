//! `task_get` — get a snapshot of a specific task.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tasks::TaskRunStatus;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

pub struct TaskGetTool;

#[async_trait]
impl Tool for TaskGetTool {
    fn name(&self) -> &str {
        "task_get"
    }

    fn description(&self) -> &str {
        "Get the current snapshot of a task: id, description, status, kind, execution mode, \
         start/end times, result, and error."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "taskId":{"type":"string","description":"The task id (e.g. task-0001)"}
          },
          "required":["taskId"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        true
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

        // Unknown and unauthorized tasks return the same "not found" message to
        // prevent probing by subagents.
        let snap = match mgr.get(task_id) {
            Some(s) => s,
            None => return ToolResult::error(format!("Task '{task_id}' not found.")),
        };

        // Subagents may only access tasks in their own descendant subtree.
        if let Some(caller) = &ctx.caller_task_id {
            if !is_accessible(task_id, caller, mgr) {
                return ToolResult::error(format!("Task '{task_id}' not found."));
            }
        }

        let status = match snap.status {
            TaskRunStatus::Running => "running",
            TaskRunStatus::Completed => "completed",
            TaskRunStatus::Failed => "failed",
            TaskRunStatus::Stopped => "stopped",
        };

        let mut lines = vec![
            format!("id: {}", snap.id),
            format!("description: {}", snap.description),
            format!("status: {status}"),
            format!("kind: {:?}", snap.kind),
            format!("mode: {:?}", snap.mode),
        ];

        if let Some(model) = &snap.resolved_model {
            lines.push(format!("model: {model}"));
        }

        if let Some(result) = &snap.result {
            lines.push(format!("result: {result}"));
        }
        if let Some(error) = &snap.error {
            lines.push(format!("error: {error}"));
        }

        ToolResult::ok(lines.join("\n"))
    }
}

/// Returns true when the caller (a subagent) is permitted to access `task_id`.
/// The caller may access any task it is a strict ancestor of.
fn is_accessible(task_id: &str, caller_id: &str, mgr: &crate::tasks::TaskManager) -> bool {
    // Walk the task's ancestor chain to see if caller_id appears.
    let snap = match mgr.get(task_id) {
        Some(s) => s,
        None => return false,
    };

    let mut parent_id = snap.parent_id;
    while let Some(pid) = parent_id {
        if pid == caller_id {
            return true;
        }
        parent_id = mgr.get(&pid).and_then(|s| s.parent_id);
    }
    false
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
    async fn get_existing_task() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "hello task", None, TaskExecutionMode::Background)
            .unwrap();
        let result = TaskGetTool
            .execute(
                &serde_json::json!({"taskId": t.id}),
                &ctx(m),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains("task-0001"), "{}", result.content);
        assert!(result.content.contains("running"), "{}", result.content);
        assert!(result.content.contains("hello task"), "{}", result.content);
    }

    #[tokio::test]
    async fn get_unknown_task_returns_error() {
        let m = TaskManager::with_defaults("session");
        let result = TaskGetTool
            .execute(
                &serde_json::json!({"taskId": "task-9999"}),
                &ctx(m),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }
}
