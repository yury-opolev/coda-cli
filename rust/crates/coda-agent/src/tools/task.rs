//! `task` — launch a foreground subagent task.
//!
//! The tool delegates to the `SubagentFactory` seam in `ToolContext`.  When
//! the factory is absent (headless, no subagent wiring) the tool returns an
//! informative error rather than panicking.
//!
//! Nesting depth and concurrency limits are enforced by the factory
//! implementation (`SubagentHost`); this tool only passes the parameters
//! through and formats the result.

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::events::NullSink;
use crate::subagents::{SubagentRequest, MAX_SUBAGENT_DEPTH};
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

// ─────────────────────────────────────────────────────────────────────────────
// Task tool
// ─────────────────────────────────────────────────────────────────────────────

pub struct TaskTool;

#[async_trait]
impl Tool for TaskTool {
    fn name(&self) -> &str {
        "task"
    }

    fn description(&self) -> &str {
        "Launch a subagent to complete a focused, self-contained task. The subagent runs with a \
         restricted tool set and returns its report as a string. For long-running work, prefer \
         background_task_start. Nesting is limited to two levels: a subagent may spawn one \
         generation of children, but grandchildren cannot spawn further."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "prompt": {
              "type": "string",
              "description": "The task description for the subagent."
            },
            "subagentType": {
              "type": "string",
              "description": "The built-in or registered subagent type. Defaults to 'general-purpose'."
            },
            "model": {
              "type": "string",
              "description": "Optional model override."
            }
          },
          "required": ["prompt"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        false // spawning an agent is a side-effecting action
    }

    async fn execute(&self, input: &Value, ctx: &ToolContext, cancel: CancellationToken) -> ToolOutcome {
        let prompt = match input.get("prompt").and_then(Value::as_str) {
            Some(p) if !p.trim().is_empty() => p.to_owned(),
            _ => return ToolResult::error("Missing required 'prompt'."),
        };

        let agent_type = input
            .get("subagentType")
            .and_then(Value::as_str)
            .unwrap_or("general-purpose")
            .to_owned();

        let model = input
            .get("model")
            .and_then(Value::as_str)
            .map(str::to_owned);

        let factory = match &ctx.subagent_factory {
            Some(f) => f.clone(),
            None => {
                return ToolResult::error(
                    "Subagent factory is not available in this context. \
                     The `task` tool requires a fully wired agent loop.",
                );
            }
        };

        // Derive the caller's depth so the child depth is caller + 1.
        let caller_depth = caller_depth(ctx);
        let child_depth = caller_depth + 1;

        if child_depth > MAX_SUBAGENT_DEPTH {
            return ToolResult::error(format!(
                "Subagent nesting depth {child_depth} exceeds the maximum of {}. \
                 Grandchildren cannot spawn further subagents.",
                MAX_SUBAGENT_DEPTH
            ));
        }

        // Generate a task id for this invocation.
        let task_id = if let Some(mgr) = &ctx.task_manager {
            match mgr.register(
                crate::tasks::TaskKind::Subagent,
                &prompt,
                ctx.caller_task_id.as_deref(),
                crate::tasks::TaskExecutionMode::Foreground,
            ) {
                Ok(t) => t.id.clone(),
                Err(e) => return ToolResult::error(e),
            }
        } else {
            uuid::Uuid::new_v4().to_string()
        };

        let request = SubagentRequest {
            agent_type,
            prompt,
            task_id: task_id.clone(),
            depth: child_depth,
            model,
            foreground: true,
        };

        let sink = std::sync::Arc::new(NullSink);
        match factory.spawn(request, sink, cancel).await {
            Ok(result) => {
                // Mark the task as completed if we registered it.
                if let Some(mgr) = &ctx.task_manager {
                    mgr.complete(&task_id, Some(result.clone()));
                }
                ToolResult::ok(result)
            }
            Err(e) => {
                if let Some(mgr) = &ctx.task_manager {
                    mgr.fail(&task_id, Some(e.clone()));
                }
                ToolResult::error(e)
            }
        }
    }
}

/// Derive the current agent's depth from the task manager.
///
/// Returns 0 (main agent) when no task id or manager is available.
fn caller_depth(ctx: &ToolContext) -> u32 {
    let task_id = match ctx.caller_task_id.as_deref() {
        Some(id) => id,
        None => return 0,
    };
    let mgr = match &ctx.task_manager {
        Some(m) => m,
        None => return 0,
    };
    mgr.get(task_id).map(|s| s.depth).unwrap_or(0)
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::subagents::SubagentFactory;
    use async_trait::async_trait;
    use std::sync::Arc;

    struct MockFactory {
        result: Result<String, String>,
    }

    #[async_trait]
    impl SubagentFactory for MockFactory {
        async fn spawn(
            &self,
            _request: SubagentRequest,
            _sink: Arc<dyn crate::events::AgentSink>,
            _cancel: CancellationToken,
        ) -> Result<String, String> {
            self.result.clone()
        }
    }

    fn ctx_with_factory(factory: Arc<dyn SubagentFactory>) -> ToolContext {
        ToolContext::new(".").with_subagent_factory(factory)
    }

    #[tokio::test]
    async fn task_tool_returns_subagent_result() {
        let factory = Arc::new(MockFactory { result: Ok("found 3 files".into()) });
        let ctx = ctx_with_factory(factory);
        let result = TaskTool
            .execute(
                &serde_json::json!({"prompt": "list all .rs files"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);
        assert_eq!(result.content, "found 3 files");
    }

    #[tokio::test]
    async fn task_tool_surfaces_subagent_error() {
        let factory = Arc::new(MockFactory { result: Err("blocked by hook".into()) });
        let ctx = ctx_with_factory(factory);
        let result = TaskTool
            .execute(
                &serde_json::json!({"prompt": "do something"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
        assert!(result.content.contains("blocked by hook"));
    }

    #[tokio::test]
    async fn task_tool_without_factory_returns_error() {
        let ctx = ToolContext::new(".");
        let result = TaskTool
            .execute(
                &serde_json::json!({"prompt": "do something"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
        assert!(result.content.contains("factory is not available"));
    }

    #[tokio::test]
    async fn task_tool_requires_prompt() {
        let factory = Arc::new(MockFactory { result: Ok("ok".into()) });
        let ctx = ctx_with_factory(factory);
        let result = TaskTool
            .execute(&serde_json::json!({}), &ctx, CancellationToken::new())
            .await;
        assert!(result.is_error);
        assert!(result.content.contains("Missing required 'prompt'"));
    }
}
