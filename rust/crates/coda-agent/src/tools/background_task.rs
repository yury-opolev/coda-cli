//! `background_task_start` — start a background subagent task.
//!
//! In Phase 5 first half the task is registered in the Running state and a
//! `JoinHandle` is spawned to run it. The actual agent-loop wiring will be
//! completed when the `SubagentFactory` seam is threaded through in Phase 5b.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tasks::{TaskExecutionMode, TaskKind};
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

pub struct BackgroundTaskStartTool;

#[async_trait]
impl Tool for BackgroundTaskStartTool {
    fn name(&self) -> &str {
        "background_task_start"
    }

    fn description(&self) -> &str {
        "Start a background subagent task with a given prompt. Returns immediately with the task \
         id. Use task_wait, task_peek, or task_recall to monitor progress."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "prompt":{"type":"string","description":"The prompt for the subagent"},
            "description":{"type":"string","description":"Human-readable label for the task"}
          },
          "required":["prompt"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        false
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        let prompt = match input.get("prompt").and_then(Value::as_str) {
            Some(p) if !p.trim().is_empty() => p,
            _ => return ToolResult::error("Missing required 'prompt'."),
        };
        let description = input
            .get("description")
            .and_then(Value::as_str)
            .unwrap_or(prompt)
            .to_owned();

        let mgr = match &ctx.task_manager {
            Some(m) => m,
            None => return ToolResult::error("Task manager is not available."),
        };

        let task = match mgr.register(
            TaskKind::Subagent,
            &description,
            ctx.caller_task_id.as_deref(),
            TaskExecutionMode::Background,
        ) {
            Ok(t) => t,
            Err(e) => return ToolResult::error(e),
        };

        let task_id = task.id.clone();
        let mgr2 = mgr.clone();
        let prompt_owned = prompt.to_owned();

        // Spawn a background task. The actual agent loop wiring is deferred to
        // Phase 5b; for now, register the task and mark it failed immediately
        // so the caller can observe the "not yet wired" state.
        // TODO(phase-5b): wire SubagentFactory and run a real AgentLoop here.
        tokio::spawn(async move {
            // Signal that the agent loop is not yet wired.
            let _ = prompt_owned; // prompt will be passed to the loop in 5b
            mgr2.fail(
                &task_id,
                Some("background subagent execution not yet wired (Phase 5b)".into()),
            );
        });

        ToolResult::ok(format!(
            "Background task started: {} ({})\nUse task_wait '{}' to monitor completion.",
            task.id, description, task.id
        ))
    }
}

/// `background_task_output` — read new output since the last call.
pub struct BackgroundTaskOutputTool;

#[async_trait]
impl Tool for BackgroundTaskOutputTool {
    fn name(&self) -> &str {
        "background_task_output"
    }

    fn description(&self) -> &str {
        "Read new output produced by a background task since the last call. Each call returns \
         only the output that has not been returned by a previous call for the same caller."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "taskId":{"type":"string","description":"The background task id"}
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

        let mgr = match &ctx.task_manager {
            Some(m) => m,
            None => return ToolResult::error("Task manager is not available."),
        };

        let task = match mgr.find_task(task_id) {
            Some(t) => t,
            None => return ToolResult::error(format!("Task '{task_id}' not found.")),
        };

        // Use the caller's task id as the consumer id so different callers
        // (main agent, parent subagent) do not steal each other's output.
        let consumer_id = ctx.caller_task_id.as_deref().unwrap_or(crate::tasks::MAIN_CONSUMER_ID);
        let (text, truncated, status) = task.read_from_cursor(consumer_id);

        let prefix = if truncated {
            "[some output was lost due to ring overflow]\n"
        } else {
            ""
        };
        let status_str = format!("\n[status: {status:?}]");

        let content = if text.is_empty() {
            format!("{prefix}(no new output){status_str}")
        } else {
            format!("{prefix}{text}{status_str}")
        };

        ToolResult::ok(content)
    }
}

/// `background_task_stop` — stop a background task.
pub struct BackgroundTaskStopTool;

#[async_trait]
impl Tool for BackgroundTaskStopTool {
    fn name(&self) -> &str {
        "background_task_stop"
    }

    fn description(&self) -> &str {
        "Request cancellation of a background task. Returns immediately; use task_wait to \
         confirm the task has stopped."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "taskId":{"type":"string","description":"The background task id to stop"}
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

        let mgr = match &ctx.task_manager {
            Some(m) => m,
            None => return ToolResult::error("Task manager is not available."),
        };

        match mgr.find_task(task_id) {
            Some(_) => {
                mgr.cancel(task_id);
                ToolResult::ok(format!("Cancellation requested for '{task_id}'."))
            }
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
    async fn background_task_start_registers_task() {
        let m = TaskManager::with_defaults("session");
        let result = BackgroundTaskStartTool
            .execute(
                &serde_json::json!({"prompt": "do something", "description": "my bg task"}),
                &ctx(m.clone()),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains("task-0001"), "{}", result.content);
    }

    #[tokio::test]
    async fn background_task_output_reads_cursor() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&t.id, "output chunk");
        let result = BackgroundTaskOutputTool
            .execute(
                &serde_json::json!({"taskId": t.id}),
                &ctx(m),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("output chunk"), "{}", result.content);
    }

    #[tokio::test]
    async fn background_task_stop_cancels_task() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        let token = t.cancel.clone();
        let result = BackgroundTaskStopTool
            .execute(
                &serde_json::json!({"taskId": t.id}),
                &ctx(m),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        assert!(token.is_cancelled());
    }
}
