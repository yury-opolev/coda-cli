//! `task_list` — list all managed tasks with their current status.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tasks::TaskRunStatus;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

pub struct TaskListTool;

#[async_trait]
impl Tool for TaskListTool {
    fn name(&self) -> &str {
        "task_list"
    }

    fn description(&self) -> &str {
        "List all background tasks for the current session with their id, description, status, \
         and kind. Returns an empty list when no tasks exist."
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

        let tasks = mgr.list();
        if tasks.is_empty() {
            return ToolResult::ok("No tasks.");
        }

        // Only show tasks visible to the caller.
        let visible: Vec<_> = tasks
            .iter()
            .filter(|t| is_visible(ctx.caller_task_id.as_deref(), t, mgr))
            .collect();

        if visible.is_empty() {
            return ToolResult::ok("No tasks.");
        }

        let mut lines = Vec::new();
        for t in &visible {
            let status = match t.status {
                TaskRunStatus::Running => "running",
                TaskRunStatus::Completed => "completed",
                TaskRunStatus::Failed => "failed",
                TaskRunStatus::Stopped => "stopped",
            };
            let mode = match t.mode {
                crate::tasks::TaskExecutionMode::Foreground => "foreground",
                crate::tasks::TaskExecutionMode::Background => "background",
            };
            let kind = match t.kind {
                crate::tasks::TaskKind::Subagent => "subagent",
                crate::tasks::TaskKind::Shell => "shell",
                crate::tasks::TaskKind::Scheduled => "scheduled",
            };
            lines.push(format!(
                "{} [{kind}/{mode}] {status} — {}",
                t.id, t.description
            ));
            if let Some(ref r) = t.result {
                if !r.is_empty() {
                    lines.push(format!("  result: {r}"));
                }
            }
            if let Some(ref e) = t.error {
                if !e.is_empty() {
                    lines.push(format!("  error: {e}"));
                }
            }
        }

        ToolResult::ok(lines.join("\n"))
    }
}

/// Returns `true` when the caller is allowed to see a task with `task_parent_id`.
/// Main agent (caller = None) sees everything.
/// A subagent sees only tasks whose ancestor chain includes the subagent.
fn is_visible(
    caller_id: Option<&str>,
    snap: &crate::tasks::TaskSnapshot,
    mgr: &crate::tasks::TaskManager,
) -> bool {
    let caller = match caller_id {
        Some(id) => id,
        None => return true, // main agent sees everything
    };
    // The task is visible if the caller is somewhere in its parent chain.
    let mut parent_id = snap.parent_id.clone();
    while let Some(pid) = parent_id {
        if pid == caller {
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
    use crate::tool::ToolContext;
    use std::sync::Arc;

    fn ctx(mgr: Arc<TaskManager>) -> ToolContext {
        ToolContext::new(".").with_task_manager(mgr)
    }

    #[tokio::test]
    async fn empty_list_returns_no_tasks_message() {
        let m = TaskManager::with_defaults("session");
        let result = TaskListTool
            .execute(&Value::Object(Default::default()), &ctx(m), CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("No tasks"), "{}", result.content);
    }

    #[tokio::test]
    async fn lists_registered_task() {
        let m = TaskManager::with_defaults("session");
        m.register(TaskKind::Subagent, "my-task", None, TaskExecutionMode::Background)
            .unwrap();
        let result = TaskListTool
            .execute(&Value::Object(Default::default()), &ctx(m), CancellationToken::new())
            .await;
        assert!(!result.is_error);
        assert!(result.content.contains("task-0001"), "{}", result.content);
        assert!(result.content.contains("my-task"), "{}", result.content);
        assert!(result.content.contains("running"), "{}", result.content);
    }
}
