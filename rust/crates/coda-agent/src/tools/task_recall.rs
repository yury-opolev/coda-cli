//! `task_recall` — drain completion entries from the owner's outbox.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tasks::TaskRunStatus;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

pub struct TaskRecallTool;

#[async_trait]
impl Tool for TaskRecallTool {
    fn name(&self) -> &str {
        "task_recall"
    }

    fn description(&self) -> &str {
        "Drain all background-task completion notifications from the outbox. Each entry is \
         delivered exactly once. Returns the list of completed/failed/stopped background tasks \
         since the last call."
    }

    fn input_schema_json(&self) -> &str {
        r#"{"type":"object","properties":{}}"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(&self, _input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        let mgr = match ctx.get_task_manager() {
            Some(m) => m,
            None => return ToolResult::error("Task manager is not available."),
        };

        let entries = mgr.drain_completions(ctx.caller_task_id.as_deref());
        if entries.is_empty() {
            return ToolResult::ok("No pending completions.");
        }

        let lines: Vec<String> = entries
            .iter()
            .map(|e| {
                let status = match e.status {
                    TaskRunStatus::Completed => "completed",
                    TaskRunStatus::Failed => "failed",
                    TaskRunStatus::Stopped => "stopped",
                    TaskRunStatus::Running => "running",
                };
                let report = e.report.as_deref().unwrap_or("(no report)");
                format!("{} [{status}] {} — {report}", e.task_id, e.description)
            })
            .collect();

        ToolResult::ok(lines.join("\n"))
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
    async fn recall_returns_completions() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "my-task", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, Some("done!".into()));
        let result = TaskRecallTool
            .execute(&Value::Object(Default::default()), &ctx(m), CancellationToken::new())
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains("task-0001"), "{}", result.content);
        assert!(result.content.contains("completed"), "{}", result.content);
        assert!(result.content.contains("done!"), "{}", result.content);
    }

    #[tokio::test]
    async fn recall_is_exactly_once() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, None);
        TaskRecallTool
            .execute(&Value::Object(Default::default()), &ctx(m.clone()), CancellationToken::new())
            .await;
        let second = TaskRecallTool
            .execute(&Value::Object(Default::default()), &ctx(m), CancellationToken::new())
            .await;
        assert!(!second.is_error);
        assert!(second.content.contains("No pending"), "{}", second.content);
    }
}
