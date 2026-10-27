//! `task_wait` — wait for a task to reach a terminal state.

use std::time::Duration;

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tasks::TaskRunStatus;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

/// Longest a single `task_wait` may wait (the yield contract's cap, D1).
pub const MAX_WAIT_SECS: u64 = 30;

pub struct TaskWaitTool;

#[async_trait]
impl Tool for TaskWaitTool {
    fn name(&self) -> &str {
        "task_wait"
    }

    fn description(&self) -> &str {
        "Wait briefly (at most 30 seconds) for a task to finish and return its final status \
         and result. Rarely needed: completions are delivered to you automatically, so the \
         preferred way to wait is to end your turn. Messages that arrive meanwhile are \
         delivered when the wait ends; it never stops the task."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "taskId":{"type":"string","description":"The task id to wait for"},
            "timeoutSeconds":{
              "type":"integer",
              "minimum":0,
              "maximum":30,
              "description":"Seconds to wait (default and maximum 30)"
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

        let wait_secs = input
            .get("timeoutSeconds")
            .and_then(|v| v.as_i64().or_else(|| v.as_f64().map(|f| f.floor() as i64)))
            .map_or(MAX_WAIT_SECS, |n| n.clamp(0, MAX_WAIT_SECS as i64) as u64);

        let mgr = match ctx.get_task_manager() {
            Some(m) => m,
            None => return ToolResult::error("Task manager is not available."),
        };

        // Authorization is checked BEFORE any target lookup so an unauthorized
        // caller cannot distinguish "unknown" from "exists but not mine" — both
        // report identical "not found" wording (mirrors task_get/task_stop).
        if !mgr.is_authorized_caller(task_id, ctx.caller_task_id.as_deref()) {
            return ToolResult::error(format!("Task '{task_id}' not found."));
        }

        let task = match mgr.find_task(task_id) {
            Some(t) => t,
            None => return ToolResult::error(format!("Task '{task_id}' not found.")),
        };

        // Already terminal: return immediately.
        if task.is_terminal() {
            return format_terminal_result(&task.to_snapshot());
        }

        // Still running: the batch executor attaches to it with the yield
        // contract (bounded, not cut short by messages, never kills).
        ToolResult::attach_task(
            task_id,
            Duration::from_secs(wait_secs),
            format!(
                "Task '{task_id}' is still running after waiting {wait_secs}s; it keeps running. \
                 Its completion will be delivered to you automatically: end your turn to wait \
                 for it instead of calling task_wait again."
            ),
        )
    }
}

pub(crate) fn format_terminal_result(snap: &crate::tasks::TaskSnapshot) -> ToolOutcome {
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

    /// Marker string a denied caller must never see in a tool's response.
    const SIBLING_CANARY: &str = "SIBLING_CANARY_WAIT_RESULT";

    fn ctx(mgr: Arc<TaskManager>) -> ToolContext {
        ToolContext::new(".").with_task_manager(mgr)
    }

    fn ctx_with_caller(mgr: Arc<TaskManager>, caller_id: &str) -> ToolContext {
        ToolContext::new(".")
            .with_task_manager(mgr)
            .with_caller_task_id(caller_id)
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
    async fn a_running_task_is_handed_to_the_executor_with_a_wait_capped_at_30s() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        for (requested, expected) in [(None, 30), (Some(300), 30), (Some(5), 5), (Some(-1), 0)] {
            let mut input = serde_json::json!({"taskId": t.id});
            if let Some(n) = requested {
                input["timeoutSeconds"] = serde_json::json!(n);
            }
            let result = TaskWaitTool.execute(&input, &ctx(m.clone()), CancellationToken::new()).await;
            let (id, wait) = result.attached_task().expect("a running task is attached");
            assert_eq!(id, t.id);
            assert_eq!(wait, Duration::from_secs(expected), "requested {requested:?}");
            assert!(result.content.contains("end your turn"), "{}", result.content);
        }
        assert_eq!(t.status(), TaskRunStatus::Running, "waiting never touches the task");
    }

    // ── SECURITY: authorization gate ─────────────────────────────────────────

    /// A sibling must not be able to wait on another sibling's terminal
    /// status/result. The denial must look identical to "not found".
    #[tokio::test]
    async fn wait_denied_for_sibling_looks_like_not_found_and_leaks_no_result() {
        let m = TaskManager::with_defaults("session");
        let a = m
            .register(TaskKind::Subagent, "a", None, TaskExecutionMode::Background)
            .unwrap();
        let b = m
            .register(TaskKind::Subagent, "b", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&b.id, Some(SIBLING_CANARY.into()));

        let result = TaskWaitTool
            .execute(
                &serde_json::json!({"taskId": b.id, "timeoutSeconds": 1}),
                &ctx_with_caller(m, &a.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", b.id));
        assert!(
            !result.content.contains(SIBLING_CANARY),
            "denied wait must not leak the sibling's result: {}",
            result.content
        );
        assert!(
            !result.content.contains("completed"),
            "denied wait must not leak the sibling's status: {}",
            result.content
        );
    }

    /// A child must not be able to wait on its own ancestor.
    #[tokio::test]
    async fn wait_denied_for_ancestor_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let parent = m
            .register(TaskKind::Subagent, "parent", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&parent.id, Some(SIBLING_CANARY.into()));
        let child = m
            .register(TaskKind::Subagent, "child", Some(&parent.id), TaskExecutionMode::Background)
            .unwrap();

        let result = TaskWaitTool
            .execute(
                &serde_json::json!({"taskId": parent.id, "timeoutSeconds": 1}),
                &ctx_with_caller(m, &child.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", parent.id));
        assert!(!result.content.contains(SIBLING_CANARY), "{}", result.content);
    }

    /// A task must not be authorized to wait on itself.
    #[tokio::test]
    async fn wait_denied_for_self_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, Some(SIBLING_CANARY.into()));

        let result = TaskWaitTool
            .execute(
                &serde_json::json!({"taskId": t.id, "timeoutSeconds": 1}),
                &ctx_with_caller(m, &t.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", t.id));
    }

    /// An unregistered caller id must fail closed.
    #[tokio::test]
    async fn wait_denied_for_unknown_caller_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.complete(&t.id, Some(SIBLING_CANARY.into()));

        let result = TaskWaitTool
            .execute(
                &serde_json::json!({"taskId": t.id, "timeoutSeconds": 1}),
                &ctx_with_caller(m, "task-9999"),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", t.id));
        assert!(!result.content.contains(SIBLING_CANARY), "{}", result.content);
    }

    /// A parent must still be able to wait on its own descendant.
    #[tokio::test]
    async fn wait_allowed_for_own_descendant() {
        let m = TaskManager::with_defaults("session");
        let parent = m
            .register(TaskKind::Subagent, "parent", None, TaskExecutionMode::Background)
            .unwrap();
        let child = m
            .register(TaskKind::Subagent, "child", Some(&parent.id), TaskExecutionMode::Background)
            .unwrap();
        m.complete(&child.id, Some("child result".into()));

        let result = TaskWaitTool
            .execute(
                &serde_json::json!({"taskId": child.id, "timeoutSeconds": 1}),
                &ctx_with_caller(m, &parent.id),
                CancellationToken::new(),
            )
            .await;

        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains("child result"), "{}", result.content);
    }
}
