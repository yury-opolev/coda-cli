//! `task_recall` — deprecated (non-blocking runtime, Phase 2; decision D3).
//!
//! Completions are now pushed into the owner's inbox automatically. The tool
//! stays for one release so older prompts keep working: it returns only
//! completions that could *not* be pushed (for example a full main inbox),
//! never one the agent has already been told about.

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
        "Deprecated: task completions are delivered to you automatically, so there is no need \
         to call this. Returns any completion that could not be delivered automatically."
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

        let entries: Vec<_> = mgr
            .drain_completions(ctx.caller_task_id.as_deref())
            .into_iter()
            .filter(|e| !mgr.find_task(&e.task_id).is_some_and(|t| t.completion_pushed()))
            .collect();
        if entries.is_empty() {
            return ToolResult::ok(
                "No pending completions. Task completions are delivered to you automatically \
                 when tasks finish; you do not need to call task_recall.",
            );
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

    #[tokio::test]
    async fn a_completion_already_pushed_is_not_returned_again() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, Some("pushed".into()));
        t.mark_completion_pushed();
        let result = TaskRecallTool
            .execute(&Value::Object(Default::default()), &ctx(m), CancellationToken::new())
            .await;
        assert!(!result.content.contains("pushed"), "{}", result.content);
        assert!(result.content.contains("delivered to you automatically"), "{}", result.content);
    }
}
