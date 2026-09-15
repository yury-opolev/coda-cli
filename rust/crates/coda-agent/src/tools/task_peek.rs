//! `task_peek` — read the recent output tail of a task without advancing a cursor.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

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

    /// Marker string a denied caller must never see in a tool's response.
    const SIBLING_CANARY: &str = "SIBLING_CANARY_PEEK_OUTPUT";

    fn ctx(mgr: Arc<TaskManager>) -> ToolContext {
        ToolContext::new(".").with_task_manager(mgr)
    }

    fn ctx_with_caller(mgr: Arc<TaskManager>, caller_id: &str) -> ToolContext {
        ToolContext::new(".")
            .with_task_manager(mgr)
            .with_caller_task_id(caller_id)
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

    // ── SECURITY: authorization gate ─────────────────────────────────────────

    /// A sibling task must not be able to peek another sibling's output. The
    /// denial must look identical to "not found" and leak none of the target's
    /// buffered output.
    #[tokio::test]
    async fn peek_denied_for_sibling_looks_like_not_found_and_leaks_no_output() {
        let m = TaskManager::with_defaults("session");
        let a = m
            .register(TaskKind::Subagent, "a", None, TaskExecutionMode::Background)
            .unwrap();
        let b = m
            .register(TaskKind::Subagent, "b", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&b.id, SIBLING_CANARY);

        let result = TaskPeekTool
            .execute(
                &serde_json::json!({"taskId": b.id}),
                &ctx_with_caller(m, &a.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", b.id));
        assert!(
            !result.content.contains(SIBLING_CANARY),
            "denied peek must not leak the sibling's output: {}",
            result.content
        );
    }

    /// A child must not be able to peek its own ancestor's output.
    #[tokio::test]
    async fn peek_denied_for_ancestor_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let parent = m
            .register(TaskKind::Subagent, "parent", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&parent.id, SIBLING_CANARY);
        let child = m
            .register(TaskKind::Subagent, "child", Some(&parent.id), TaskExecutionMode::Background)
            .unwrap();

        let result = TaskPeekTool
            .execute(
                &serde_json::json!({"taskId": parent.id}),
                &ctx_with_caller(m, &child.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", parent.id));
        assert!(!result.content.contains(SIBLING_CANARY), "{}", result.content);
    }

    /// A task must not be authorized to peek itself.
    #[tokio::test]
    async fn peek_denied_for_self_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&t.id, SIBLING_CANARY);

        let result = TaskPeekTool
            .execute(
                &serde_json::json!({"taskId": t.id}),
                &ctx_with_caller(m, &t.id),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", t.id));
    }

    /// An unregistered caller id must fail closed rather than being treated
    /// as an authorized identity.
    #[tokio::test]
    async fn peek_denied_for_unknown_caller_looks_like_not_found() {
        let m = TaskManager::with_defaults("session");
        let t = m
            .register(TaskKind::Subagent, "t", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&t.id, SIBLING_CANARY);

        let result = TaskPeekTool
            .execute(
                &serde_json::json!({"taskId": t.id}),
                &ctx_with_caller(m, "task-9999"),
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", t.id));
        assert!(!result.content.contains(SIBLING_CANARY), "{}", result.content);
    }

    /// A parent MUST still be able to peek its own descendant's output —
    /// the authorization gate must not regress legitimate access.
    #[tokio::test]
    async fn peek_allowed_for_own_descendant() {
        let m = TaskManager::with_defaults("session");
        let parent = m
            .register(TaskKind::Subagent, "parent", None, TaskExecutionMode::Background)
            .unwrap();
        let child = m
            .register(TaskKind::Subagent, "child", Some(&parent.id), TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&child.id, "child output");

        let result = TaskPeekTool
            .execute(
                &serde_json::json!({"taskId": child.id}),
                &ctx_with_caller(m, &parent.id),
                CancellationToken::new(),
            )
            .await;

        assert!(!result.is_error, "{}", result.content);
        assert!(result.content.contains("child output"), "{}", result.content);
    }
}
