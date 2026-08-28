//! `task_peek` — read the recent output tail of a task without advancing a cursor.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

/// Default number of chars returned when `maxChars` is not specified.
const DEFAULT_PEEK_CHARS: usize = 4000;
const MAX_PEEK_CHARS: usize = 20_000;

pub struct TaskPeekTool;

#[async_trait]
impl Tool for TaskPeekTool {
    fn name(&self) -> &str {
        "task_peek"
    }

    fn description(&self) -> &str {
        "Return the most recent output of a task without advancing the incremental read cursor. \
         Useful for a quick look at what a task is currently producing."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "taskId":{"type":"string","description":"The task id"},
            "maxChars":{
              "type":"integer",
              "description":"Maximum characters to return (default 4000, max 20000)"
            }
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

        let max_chars = input
            .get("maxChars")
            .and_then(Value::as_u64)
            .map(|n| (n as usize).min(MAX_PEEK_CHARS))
            .unwrap_or(DEFAULT_PEEK_CHARS);

        let mgr = match &ctx.task_manager {
            Some(m) => m,
            None => return ToolResult::error("Task manager is not available."),
        };

        match mgr.try_peek(task_id, max_chars) {
            Some(text) if text.is_empty() => ToolResult::ok("(no output yet)"),
            Some(text) => ToolResult::ok(text),
            None => ToolResult::error(format!("Task '{task_id}' not found.")),
        }
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
    async fn peek_returns_output_tail() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&t.id, "hello world output");
        let result = TaskPeekTool
            .execute(&serde_json::json!({"taskId": t.id}), &ctx(m), CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("hello world"), "{}", result.content);
    }

    #[tokio::test]
    async fn peek_empty_returns_placeholder() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        let result = TaskPeekTool
            .execute(&serde_json::json!({"taskId": t.id}), &ctx(m), CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("no output"), "{}", result.content);
    }
}
