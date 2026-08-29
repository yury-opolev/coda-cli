//! `task_send` — deliver a steering message to a running subagent.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tasks::TaskRunStatus;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

pub struct TaskSendTool;

#[async_trait]
impl Tool for TaskSendTool {
    fn name(&self) -> &str {
        "task_send"
    }

    fn description(&self) -> &str {
        "Deliver a steering message to a running subagent task. The message is enqueued in the \
         task's steering inbox and delivered before the agent's next model call. No-op when the \
         task has no steering inbox (shell tasks) or is already terminal."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "taskId":{"type":"string","description":"The subagent task id"},
            "message":{"type":"string","description":"The steering message to deliver"}
          },
          "required":["taskId","message"]
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
        let message = match input.get("message").and_then(Value::as_str) {
            Some(m) if !m.trim().is_empty() => m,
            _ => return ToolResult::error("Missing required 'message'."),
        };

        let mgr = match ctx.get_task_manager() {
            Some(m) => m,
            None => return ToolResult::error("Task manager is not available."),
        };

        let task = match mgr.find_task(task_id) {
            Some(t) => t,
            None => return ToolResult::error(format!("Task '{task_id}' not found.")),
        };

        if task.status() != TaskRunStatus::Running {
            return ToolResult::error(format!(
                "Task '{task_id}' is not running; cannot send a message."
            ));
        }

        // Deliver via the steering inbox if one is attached (subagent tasks only).
        match &task.steering {
            Some(inbox) => {
                inbox.enqueue(message.to_owned());
                ToolResult::ok(format!("Message delivered to task '{task_id}'."))
            }
            None => ToolResult::error(format!(
                "Task '{task_id}' does not have a steering inbox (not a subagent task)."
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
    async fn send_to_task_without_inbox_returns_error() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        let result = TaskSendTool
            .execute(
                &serde_json::json!({"taskId": t.id, "message": "hello"}),
                &ctx(m),
                CancellationToken::new(),
            )
            .await;
        // No steering inbox attached, so this should fail.
        assert!(result.is_error, "{}", result.content);
    }
}
