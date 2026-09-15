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

        // Authorization is checked BEFORE any mutation so an unauthorized
        // caller cannot distinguish "unknown" from "exists but not mine" — both
        // report identical "not found" wording (mirrors task_get/task_stop).
        if !mgr.is_authorized_caller(task_id, ctx.caller_task_id.as_deref()) {
            return ToolResult::error(format!("Task '{task_id}' not found."));
        }

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

    /// Marker string a denied caller's action must never leave a trace of.
    const SIBLING_CANARY: &str = "SIBLING_CANARY_REMOVE_RESULT";

    fn ctx(mgr: Arc<TaskManager>) -> ToolContext {
        ToolContext::new(".").with_task_manager(mgr)
    }

    fn ctx_with_caller(mgr: Arc<TaskManager>, caller_id: &str) -> ToolContext {
        ToolContext::new(".")
            .with_task_manager(mgr)
            .with_caller_task_id(caller_id)
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

    // ── SECURITY: authorization gate ─────────────────────────────────────────

    /// A sibling must not be able to remove another sibling's terminal task.
    /// The denial must look identical to "not found" and the target must
    /// remain registered, unchanged.
    #[tokio::test]
    async fn remove_denied_for_sibling_looks_like_not_found_and_state_unchanged() {
        let m = TaskManager::with_defaults("session");
        let a = m
            .register(TaskKind::Subagent, "a", None, TaskExecutionMode::Background)
            .unwrap();
        let b = m
            .register(TaskKind::Subagent, "b", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&b.id, Some(SIBLING_CANARY.into()));

        let result = TaskRemoveTool
            .execute(
                &serde_json::json!({"taskId": b.id}),
                &ctx_with_caller(m.clone(), &a.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", b.id));
        assert!(!result.content.contains(SIBLING_CANARY), "{}", result.content);
        assert!(
            m.get(&b.id).is_some(),
            "denied remove must leave the sibling task registered"
        );
    }

    /// A child must not be able to remove its own ancestor.
    #[tokio::test]
    async fn remove_denied_for_ancestor_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let parent = m
            .register(TaskKind::Subagent, "parent", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&parent.id, None);
        let child = m
            .register(TaskKind::Subagent, "child", Some(&parent.id), TaskExecutionMode::Background)
            .unwrap();

        let result = TaskRemoveTool
            .execute(
                &serde_json::json!({"taskId": parent.id}),
                &ctx_with_caller(m.clone(), &child.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", parent.id));
        assert!(m.get(&parent.id).is_some(), "ancestor must not be removed");
    }

    /// A task must not be authorized to remove itself.
    #[tokio::test]
    async fn remove_denied_for_self_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, None);

        let result = TaskRemoveTool
            .execute(
                &serde_json::json!({"taskId": t.id}),
                &ctx_with_caller(m.clone(), &t.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", t.id));
        assert!(m.get(&t.id).is_some(), "self must not be removed");
    }

    /// An unregistered caller id must fail closed.
    #[tokio::test]
    async fn remove_denied_for_unknown_caller_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, None);

        let result = TaskRemoveTool
            .execute(
                &serde_json::json!({"taskId": t.id}),
                &ctx_with_caller(m.clone(), "task-9999"),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", t.id));
        assert!(m.get(&t.id).is_some(), "must not be removed by an unknown caller");
    }

    /// A parent must still be able to remove its own terminal descendant.
    #[tokio::test]
    async fn remove_allowed_for_own_descendant() {
        let m = TaskManager::with_defaults("session");
        let parent = m
            .register(TaskKind::Subagent, "parent", None, TaskExecutionMode::Background)
            .unwrap();
        let child = m
            .register(TaskKind::Subagent, "child", Some(&parent.id), TaskExecutionMode::Background)
            .unwrap();
        m.complete(&child.id, None);

        let result = TaskRemoveTool
            .execute(
                &serde_json::json!({"taskId": child.id}),
                &ctx_with_caller(m.clone(), &parent.id),
                CancellationToken::new(),
            )
            .await;

        assert!(!result.is_error, "{}", result.content);
        assert!(m.get(&child.id).is_none(), "descendant must be removed");
    }
}
