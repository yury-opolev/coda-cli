//! `task_wait` — wait for a task to reach a terminal state.

use std::time::Duration;

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tasks::TaskRunStatus;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

const DEFAULT_TIMEOUT_SECS: u64 = 300;

pub struct TaskWaitTool;

#[async_trait]
impl Tool for TaskWaitTool {
    fn name(&self) -> &str {
        "task_wait"
    }

    fn description(&self) -> &str {
        "Wait for a task to reach a terminal state (completed/failed/stopped) and return its \
         final status and result. Times out after `timeoutSeconds` (default 300)."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "taskId":{"type":"string","description":"The task id to wait for"},
            "timeoutSeconds":{
              "type":"integer",
              "description":"Maximum seconds to wait (default 300)"
            }
          },
          "required":["taskId"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, cancel: CancellationToken) -> ToolOutcome {
        let task_id = match input.get("taskId").and_then(Value::as_str) {
            Some(id) => id,
            None => return ToolResult::error("Missing required 'taskId'."),
        };

        let timeout_secs = input
            .get("timeoutSeconds")
            .and_then(Value::as_u64)
            .unwrap_or(DEFAULT_TIMEOUT_SECS);

        let mgr = match &ctx.task_manager {
            Some(m) => m,
            None => return ToolResult::error("Task manager is not available."),
        };

        let task = match mgr.find_task(task_id) {
            Some(t) => t,
            None => return ToolResult::error(format!("Task '{task_id}' not found.")),
        };

        // Already terminal: return immediately.
        if task.is_terminal() {
            return format_terminal_result(&task.to_snapshot());
        }

        let timeout = Duration::from_secs(timeout_secs);
        let wait_fut = task.wait_for_completion();

        tokio::select! {
            _ = wait_fut => {
                format_terminal_result(&task.to_snapshot())
            }
            _ = tokio::time::sleep(timeout) => {
                ToolResult::error(format!(
                    "Task '{task_id}' did not complete within {timeout_secs}s."
                ))
            }
            _ = cancel.cancelled() => {
                ToolResult::error("Cancelled.")
            }
        }
    }
}

fn format_terminal_result(snap: &crate::tasks::TaskSnapshot) -> ToolOutcome {
    let status = match snap.status {
        TaskRunStatus::Completed => "completed",
        TaskRunStatus::Failed => "failed",
        TaskRunStatus::Stopped => "stopped",
        TaskRunStatus::Running => "running",
    };
    let mut parts = vec![format!("Task '{}' {status}.", snap.id)];
    if let Some(r) = &snap.result {
        if !r.is_empty() {
            parts.push(format!("Result: {r}"));
        }
    }
    if let Some(e) = &snap.error {
        if !e.is_empty() {
            parts.push(format!("Error: {e}"));
        }
    }
    let is_error = snap.status == TaskRunStatus::Failed;
    if is_error {
        ToolResult::error(parts.join("\n"))
    } else {
        ToolResult::ok(parts.join("\n"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tasks::{TaskExecutionMode, TaskKind, TaskManager};
    use std::sync::Arc;

    fn ctx(mgr: Arc<TaskManager>) -> ToolContext {
        let mut c = ToolContext::new(".");
        c.task_manager = Some(mgr);
        c
    }

    #[tokio::test]
    async fn wait_for_already_terminal_task_returns_immediately() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, Some("done!".into()));
        let result = TaskWaitTool
            .execute(
                &serde_json::json!({"taskId": t.id, "timeoutSeconds": 1}),
                &ctx(m),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains("completed"), "{}", result.content);
        assert!(result.content.contains("done!"), "{}", result.content);
    }

    #[tokio::test]
    async fn wait_for_task_that_completes_async() {
        use tokio::time::Duration;

        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        let m2 = m.clone();
        let id = t.id.clone();
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(50)).await;
            m2.complete(&id, Some("async done".into()));
        });
        let result = TaskWaitTool
            .execute(
                &serde_json::json!({"taskId": t.id, "timeoutSeconds": 5}),
                &ctx(m),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains("async done"), "{}", result.content);
    }

    #[tokio::test]
    async fn wait_times_out_for_non_completing_task() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        let result = TaskWaitTool
            .execute(
                &serde_json::json!({"taskId": t.id, "timeoutSeconds": 0}),
                &ctx(m),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error, "expected timeout error: {}", result.content);
    }
}
