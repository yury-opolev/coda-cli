//! Background task tools: `task_start`, `task_output`.
//!
//! Mirrors C# `BackgroundTaskStartTool`, `BackgroundTaskOutputTool`, `BackgroundTaskStopTool`.
//! `task_stop` is implemented in `task_stop.rs` (which also handles the authorization-based
//! stop semantics).

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::subagents::{SubagentRequest, MAX_SUBAGENT_DEPTH};
use crate::tasks::TaskRunStatus;
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;
use crate::events::NullSink;

// ── helper: derive caller depth from context ──────────────────────────────────

fn caller_depth(ctx: &ToolContext) -> u32 {
    let task_id = match ctx.caller_task_id.as_deref() {
        Some(id) => id,
        None => return 0,
    };
    let mgr = match ctx.get_task_manager() {
        Some(m) => m,
        None => return 0,
    };
    mgr.get(task_id).map(|s| s.depth).unwrap_or(0)
}

// ── BackgroundTaskStartTool ───────────────────────────────────────────────────

/// `task_start` — start a background subagent and return its task id immediately.
pub struct BackgroundTaskStartTool;

#[async_trait]
impl Tool for BackgroundTaskStartTool {
    fn name(&self) -> &str {
        "task_start"
    }

    fn description(&self) -> &str {
        "Start a subagent in the background and return its task id immediately. \
         Use task_output to read incremental progress and task_stop to cancel it."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "prompt":{"type":"string","description":"The detailed task for the subagent"},
            "subagent_type":{"type":"string","description":"Subagent type: \"general-purpose\" (default) or \"explore\" (read-only research)."},
            "model":{"type":"string","description":"Model id for this subagent. Omit to inherit the session model."},
            "system_prompt_append":{"type":"string","description":"Extra standing instructions appended to the subagent system prompt."},
            "system_prompt":{"type":"string","description":"Replaces the subagent type's own instructions. Prefer system_prompt_append."}
          },
          "required":["prompt"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        // Launching is not itself mutating; the subagent's own mutating tools
        // are permission-gated when they execute. Matches C# IsReadOnly = true.
        true
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        // Both task manager and subagent factory must be present.
        if ctx.get_task_manager().is_none() || ctx.get_subagent_factory().is_none() {
            return ToolResult::ok("Background tasks are not available in this context.");
        }

        // Depth check: reject when the calling context is already at max depth.
        let current_depth = caller_depth(ctx);
        if current_depth >= MAX_SUBAGENT_DEPTH {
            return ToolResult::error(
                "Cannot start a background subagent from here: the maximum subagent nesting depth has been reached.",
            );
        }

        let prompt = match input.get("prompt").and_then(Value::as_str) {
            Some(p) if !p.trim().is_empty() => p,
            _ => return ToolResult::error("Missing required 'prompt'."),
        };

        let subagent_type = input
            .get("subagent_type")
            .and_then(Value::as_str)
            .unwrap_or("general-purpose")
            .to_owned();

        let model = input.get("model").and_then(Value::as_str).map(str::to_owned);

        let factory = ctx.get_subagent_factory().unwrap(); // checked above

        // Child depth = current_depth + 1.
        let child_depth = current_depth + 1;

        let request = SubagentRequest {
            agent_type: subagent_type.clone(),
            prompt: prompt.to_owned(),
            task_id: String::new(), // background: host assigns the id
            depth: child_depth,
            model,
            foreground: false,
            caller_task_id: ctx.caller_task_id.clone(),
        };

        let sink = std::sync::Arc::new(NullSink);
        match factory.spawn(request, sink, CancellationToken::new()).await {
            Ok(task_id) => ToolResult::ok(format!(
                "Started background task {task_id}. Use task_output to read its progress."
            )),
            Err(e) => ToolResult::error(e),
        }
    }
}

// ── BackgroundTaskOutputTool ──────────────────────────────────────────────────

/// `task_output` — read new output from a background task (cursor-based).
pub struct BackgroundTaskOutputTool;

#[async_trait]
impl Tool for BackgroundTaskOutputTool {
    fn name(&self) -> &str {
        "task_output"
    }

    fn description(&self) -> &str {
        "Read new output from a background task started with task_start. \
         Each call returns only text produced since the previous read (cursor-based). \
         Check the status line to know when the task has finished."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type":"object",
          "properties":{
            "task_id":{"type":"string","description":"The task id returned by task_start"}
          },
          "required":["task_id"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, _cancel: CancellationToken) -> ToolOutcome {
        let mgr = match ctx.get_task_manager() {
            Some(m) => m,
            None => return ToolResult::ok("Background tasks are not available in this context."),
        };

        let task_id = match input.get("task_id").and_then(Value::as_str) {
            Some(id) if !id.trim().is_empty() => id,
            _ => return ToolResult::error("Missing required 'task_id'."),
        };

        let caller_id = ctx.caller_task_id.as_deref();
        let (found, text, truncated, status) = mgr.read_output(task_id, caller_id);

        if !found {
            // Not found OR unauthorized — unified "not found" response prevents probing.
            return ToolResult::ok(format!("Task '{task_id}' not found."));
        }

        let output_section = if !text.is_empty() {
            text
        } else if status == TaskRunStatus::Running {
            "(no new output yet; still running)".to_owned()
        } else {
            "(no new output since last read)".to_owned()
        };

        let output_section = if truncated {
            format!("[earlier output truncated]\n{output_section}")
        } else {
            output_section
        };

        let status_label = match status {
            TaskRunStatus::Running => "running",
            TaskRunStatus::Completed => "completed",
            TaskRunStatus::Failed => "failed",
            TaskRunStatus::Stopped => "stopped",
        };

        ToolResult::ok(format!("{output_section}\n[status: {status_label}]"))
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tasks::{TaskExecutionMode, TaskKind, TaskManager};
    use std::sync::Arc;

    fn ctx(mgr: Arc<TaskManager>) -> ToolContext {
        ToolContext::new(".").with_task_manager(mgr)
    }

    fn ctx_with_caller(mgr: Arc<TaskManager>, caller_id: &str) -> ToolContext {
        ToolContext::new(".")
            .with_task_manager(mgr)
            .with_caller_task_id(caller_id)
    }

    // ── task_output ──────────────────────────────────────────────────────────

    #[tokio::test]
    async fn task_output_missing_task_manager_reports_not_available() {
        let ctx = ToolContext::new(".");
        let result = BackgroundTaskOutputTool
            .execute(&serde_json::json!({"task_id": "task-0001"}), &ctx, CancellationToken::new())
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert_eq!(result.content, "Background tasks are not available in this context.");
    }

    #[tokio::test]
    async fn task_output_missing_task_id_returns_error() {
        let m = TaskManager::with_defaults("s");
        let result = BackgroundTaskOutputTool
            .execute(&serde_json::json!({}), &ctx(m), CancellationToken::new())
            .await;
        assert!(result.is_error, "{}", result.content);
        assert_eq!(result.content, "Missing required 'task_id'.");
    }

    #[tokio::test]
    async fn task_output_unknown_id_reports_not_found() {
        let m = TaskManager::with_defaults("s");
        let result = BackgroundTaskOutputTool
            .execute(&serde_json::json!({"task_id": "ghost"}), &ctx(m), CancellationToken::new())
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert_eq!(result.content, "Task 'ghost' not found.");
    }

    #[tokio::test]
    async fn task_output_reads_new_text_then_no_new_output_running() {
        let m = TaskManager::with_defaults("s");
        let t = m
            .register(TaskKind::Subagent, "d", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&t.id, "hello world");
        let input = serde_json::json!({"task_id": t.id});

        let first = BackgroundTaskOutputTool
            .execute(&input, &ctx(m.clone()), CancellationToken::new())
            .await;
        assert_eq!(first.content, "hello world\n[status: running]");

        // Cursor advanced; nothing new.
        let second = BackgroundTaskOutputTool
            .execute(&input, &ctx(m.clone()), CancellationToken::new())
            .await;
        assert_eq!(second.content, "(no new output yet; still running)\n[status: running]");
    }

    #[tokio::test]
    async fn task_output_no_new_output_on_finished_task() {
        let m = TaskManager::with_defaults("s");
        let t = m
            .register(TaskKind::Subagent, "d", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&t.id, "partial");
        let input = serde_json::json!({"task_id": t.id});

        // Drain the ring.
        BackgroundTaskOutputTool.execute(&input, &ctx(m.clone()), CancellationToken::new()).await;
        m.complete(&t.id, Some("done".into()));

        let result = BackgroundTaskOutputTool
            .execute(&input, &ctx(m), CancellationToken::new())
            .await;
        assert_eq!(result.content, "(no new output since last read)\n[status: completed]");
    }

    #[tokio::test]
    async fn task_output_prepends_truncation_notice() {
        // Tiny ring so a large append evicts everything before the cursor.
        use crate::tasks::output_ring::DEFAULT_MAX_BYTES;
        let _ = DEFAULT_MAX_BYTES; // satisfy unused warning
        let m = TaskManager::new(
            "s",
            Some(std::env::temp_dir().join("coda-bg-trunc-test")),
            32,
            10,
        );
        let t = m
            .register(TaskKind::Subagent, "d", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&t.id, "AAAA");
        let input = serde_json::json!({"task_id": t.id});
        // Advance cursor past "AAAA".
        BackgroundTaskOutputTool.execute(&input, &ctx(m.clone()), CancellationToken::new()).await;
        // Evict past the cursor.
        m.append_output(&t.id, &"B".repeat(200));

        let result = BackgroundTaskOutputTool
            .execute(&input, &ctx(m), CancellationToken::new())
            .await;
        assert!(
            result.content.starts_with("[earlier output truncated]\n"),
            "expected truncation notice, got: {}",
            result.content
        );
        assert!(result.content.ends_with("\n[status: running]"), "{}", result.content);
    }

    #[tokio::test]
    async fn task_output_status_labels_all_states() {
        use crate::tasks::TaskRunStatus;
        let cases = [
            (TaskRunStatus::Running, "running"),
            (TaskRunStatus::Completed, "completed"),
            (TaskRunStatus::Failed, "failed"),
            (TaskRunStatus::Stopped, "stopped"),
        ];
        for (status, label) in &cases {
            let m = TaskManager::with_defaults("s");
            let t = m
                .register(TaskKind::Subagent, "d", None, TaskExecutionMode::Background)
                .unwrap();
            match status {
                TaskRunStatus::Completed => { m.complete(&t.id, Some("done".into())); }
                TaskRunStatus::Failed => { m.fail(&t.id, Some("boom".into())); }
                TaskRunStatus::Stopped => { m.stop(&t.id); }
                _ => {}
            }
            let result = BackgroundTaskOutputTool
                .execute(&serde_json::json!({"task_id": t.id}), &ctx(m), CancellationToken::new())
                .await;
            assert!(
                result.content.ends_with(&format!("[status: {label}]")),
                "expected [status: {label}] for {status:?}, got: {}",
                result.content
            );
        }
    }

    // ── authorization (caller-scoped isolation) ──────────────────────────────

    #[tokio::test]
    async fn task_output_denied_for_sibling_looks_like_not_found() {
        let m = TaskManager::with_defaults("s");
        let a = m
            .register(TaskKind::Subagent, "a", None, TaskExecutionMode::Background)
            .unwrap();
        let b = m
            .register(TaskKind::Subagent, "b", None, TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&b.id, "secret");

        let ctx = ctx_with_caller(m, &a.id);
        let result = BackgroundTaskOutputTool
            .execute(&serde_json::json!({"task_id": b.id}), &ctx, CancellationToken::new())
            .await;

        assert!(!result.is_error);
        assert_eq!(result.content, format!("Task '{}' not found.", b.id));
        assert!(!result.content.contains("secret"), "secret must not be leaked");
    }

    #[tokio::test]
    async fn task_output_allowed_for_callers_own_descendant() {
        let m = TaskManager::with_defaults("s");
        let parent = m
            .register(TaskKind::Subagent, "p", None, TaskExecutionMode::Background)
            .unwrap();
        let child = m
            .register(TaskKind::Subagent, "c", Some(&parent.id), TaskExecutionMode::Background)
            .unwrap();
        m.append_output(&child.id, "child progress");

        let ctx = ctx_with_caller(m, &parent.id);
        let result = BackgroundTaskOutputTool
            .execute(&serde_json::json!({"task_id": child.id}), &ctx, CancellationToken::new())
            .await;

        assert!(!result.is_error);
        assert_eq!(result.content, "child progress\n[status: running]");
    }
}

