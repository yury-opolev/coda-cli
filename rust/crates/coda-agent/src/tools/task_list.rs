//! `task_list` — list all managed tasks with their current status.

use async_trait::async_trait;
use coda_proto::requests::TaskStatusFilter;
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
        "List background tasks for the current session with their id, description, status, \
         and kind. Lists all tasks by default; statusFilter \"active\" lists only running work \
         and \"terminal\" only completed, failed or stopped tasks. Returns an empty list when \
         no tasks match."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "statusFilter":{
              "type":"string",
              "enum":["active","terminal","all"],
              "description":"Which tasks to list: active (running), terminal (completed, failed or stopped) or all (default)"
            }
          }
        }"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        let filter = match input.get("statusFilter") {
            None | Some(Value::Null) => TaskStatusFilter::All,
            Some(value) => match value.as_str().and_then(TaskStatusFilter::parse) {
                Some(filter) => filter,
                None => {
                    return ToolResult::error(format!(
                        "Invalid statusFilter {value}; expected one of: active, terminal, all."
                    ))
                }
            },
        };
        let empty = match filter {
            TaskStatusFilter::Active => "No active tasks.",
            TaskStatusFilter::Terminal => "No finished tasks.",
            TaskStatusFilter::All => "No tasks.",
        };

        let mgr = match ctx.get_task_manager() {
            Some(m) => m,
            None => return ToolResult::error("Task manager is not available."),
        };

        let tasks = mgr.list();
        if tasks.is_empty() {
            return ToolResult::ok(empty);
        }

        // Only show tasks visible to the caller.
        let visible: Vec<_> = tasks
            .iter()
            .filter(|t| filter.admits(t.status.is_terminal()))
            .filter(|t| is_visible(ctx.caller_task_id.as_deref(), t, mgr))
            .collect();

        if visible.is_empty() {
            return ToolResult::ok(empty);
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
            let kind = t.kind.as_str();
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
        assert!(result.content.contains("task-00000001"), "{}", result.content);
        assert!(result.content.contains("my-task"), "{}", result.content);
        assert!(result.content.contains("running"), "{}", result.content);
    }

    async fn list(m: &Arc<TaskManager>, input: Value) -> ToolResult {
        TaskListTool.execute(&input, &ctx(m.clone()), CancellationToken::new()).await
    }

    #[tokio::test]
    async fn the_status_filter_selects_active_or_finished_tasks_and_defaults_to_all() {
        let m = TaskManager::with_defaults("session");
        let live = m.register(TaskKind::Shell, "live-one", None, TaskExecutionMode::Background).unwrap();
        let done = m.register(TaskKind::Shell, "done-one", None, TaskExecutionMode::Background).unwrap();
        let stopped = m.register(TaskKind::Shell, "stopped-one", None, TaskExecutionMode::Background).unwrap();
        m.complete(&done.id, None);
        m.stop(&stopped.id);

        let all = list(&m, serde_json::json!({})).await.content;
        assert!(all.contains("live-one") && all.contains("done-one") && all.contains("stopped-one"), "{all}");
        let active = list(&m, serde_json::json!({ "statusFilter": "active" })).await.content;
        assert!(active.contains(&live.id) && !active.contains("done-one") && !active.contains("stopped-one"), "{active}");
        let terminal = list(&m, serde_json::json!({ "statusFilter": "terminal" })).await.content;
        assert!(!terminal.contains("live-one") && terminal.contains("done-one") && terminal.contains("stopped-one"), "{terminal}");
        assert_eq!(list(&m, serde_json::json!({ "statusFilter": "all" })).await.content, all);
        assert_eq!(m.list().len(), 3);

        m.complete(&live.id, None);
        let none = list(&m, serde_json::json!({ "statusFilter": "active" })).await;
        assert!(!none.is_error && none.content.contains("No active tasks"), "{}", none.content);
    }

    #[tokio::test]
    async fn an_unknown_status_filter_is_refused() {
        let m = TaskManager::with_defaults("session");
        for bad in [serde_json::json!("finished"), serde_json::json!(3)] {
            let result = list(&m, serde_json::json!({ "statusFilter": bad })).await;
            assert!(result.is_error, "{}", result.content);
            assert!(result.content.contains("active, terminal, all"), "{}", result.content);
        }
    }

    #[tokio::test]
    async fn a_subagent_still_sees_only_its_own_tasks_under_a_filter() {
        let m = TaskManager::with_defaults("session");
        let agent = m.register(TaskKind::Subagent, "agent", None, TaskExecutionMode::Background).unwrap();
        let mine = m.register(TaskKind::Shell, "mine", Some(&agent.id), TaskExecutionMode::Background).unwrap();
        m.register(TaskKind::Shell, "other", None, TaskExecutionMode::Background).unwrap();
        let ctx = ToolContext::new(".").with_task_manager(m.clone()).with_caller_task_id(agent.id.clone());
        let result = TaskListTool
            .execute(&serde_json::json!({ "statusFilter": "active" }), &ctx, CancellationToken::new())
            .await;
        assert!(result.content.contains(&mine.id) && !result.content.contains("other"), "{}", result.content);
    }
}
