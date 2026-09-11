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

        // Authorization is checked BEFORE any target lookup/status check so an
        // unauthorized caller cannot distinguish "unknown" from "exists but not
        // mine, running, or without an inbox" — all collapse to the same
        // "not found" wording (mirrors task_get/task_stop).
        if !mgr.is_authorized_caller(task_id, ctx.caller_task_id.as_deref()) {
            return ToolResult::error(format!("Task '{task_id}' not found."));
        }

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

    /// Marker string a denied caller must never see reflected in a response
    /// (and must never manage to enqueue into a sibling's inbox).
    const SIBLING_CANARY: &str = "SIBLING_CANARY_STEERING_MESSAGE";

    fn ctx(mgr: Arc<TaskManager>) -> ToolContext {
        ToolContext::new(".").with_task_manager(mgr)
    }

    fn ctx_with_caller(mgr: Arc<TaskManager>, caller_id: &str) -> ToolContext {
        ToolContext::new(".")
            .with_task_manager(mgr)
            .with_caller_task_id(caller_id)
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

    // ── SECURITY: authorization gate ─────────────────────────────────────────

    /// A sibling must not be able to send a steering message to another
    /// sibling's running task. The denial must look identical to "not found",
    /// gating before the tool ever reaches the running/inbox checks that
    /// would otherwise confirm the target's existence.
    #[tokio::test]
    async fn send_denied_for_sibling_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let a = m
            .register(TaskKind::Subagent, "a", None, TaskExecutionMode::Background)
            .unwrap();
        let b = m
            .register(TaskKind::Subagent, "b", None, TaskExecutionMode::Background)
            .unwrap();

        let result = TaskSendTool
            .execute(
                &serde_json::json!({"taskId": b.id, "message": SIBLING_CANARY}),
                &ctx_with_caller(m, &a.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(
            result.content,
            format!("Task '{}' not found.", b.id),
            "denied send must use not-found wording, not leak running/no-inbox state"
        );
    }

    /// A child must not be able to send a steering message to its own ancestor.
    #[tokio::test]
    async fn send_denied_for_ancestor_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let parent = m
            .register(TaskKind::Subagent, "parent", None, TaskExecutionMode::Background)
            .unwrap();
        let child = m
            .register(TaskKind::Subagent, "child", Some(&parent.id), TaskExecutionMode::Background)
            .unwrap();

        let result = TaskSendTool
            .execute(
                &serde_json::json!({"taskId": parent.id, "message": SIBLING_CANARY}),
                &ctx_with_caller(m, &child.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", parent.id));
    }

    /// A task must not be authorized to send a steering message to itself.
    #[tokio::test]
    async fn send_denied_for_self_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();

        let result = TaskSendTool
            .execute(
                &serde_json::json!({"taskId": t.id, "message": SIBLING_CANARY}),
                &ctx_with_caller(m, &t.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", t.id));
    }

    /// An unregistered caller id must fail closed.
    #[tokio::test]
    async fn send_denied_for_unknown_caller_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();

        let result = TaskSendTool
            .execute(
                &serde_json::json!({"taskId": t.id, "message": SIBLING_CANARY}),
                &ctx_with_caller(m, "task-9999"),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", t.id));
    }

    /// A parent must still reach the real (inert) send logic for its own
    /// descendant — proving the authorization gate does not regress
    /// legitimate access, even though there is no wired inbox yet.
    #[tokio::test]
    async fn send_allowed_for_own_descendant_reaches_inert_inbox_check() {
        let m = TaskManager::with_defaults("session");
        let parent = m
            .register(TaskKind::Subagent, "parent", None, TaskExecutionMode::Background)
            .unwrap();
        let child = m
            .register(TaskKind::Subagent, "child", Some(&parent.id), TaskExecutionMode::Background)
            .unwrap();

        let result = TaskSendTool
            .execute(
                &serde_json::json!({"taskId": child.id, "message": "hello"}),
                &ctx_with_caller(m, &parent.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_ne!(
            result.content,
            format!("Task '{}' not found.", child.id),
            "authorized access must not be denied as not-found"
        );
        assert!(
            result.content.contains("steering inbox"),
            "must reach the real (still-inert) inbox check: {}",
            result.content
        );
    }
}
