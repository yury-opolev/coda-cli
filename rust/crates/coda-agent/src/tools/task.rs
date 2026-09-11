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
use crate::tool::ToolContextServiceExt as _;

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

        let factory = match ctx.get_subagent_factory() {
            Some(f) => f,
            None => {
                return ToolResult::error(
                    "Subagent factory is not available in this context. \
                     The `task` tool requires a fully wired agent loop.",
                );
            }
        };

        // Derive the caller's depth so the child depth is caller + 1.
        //
        // A caller id that cannot be verified against the task manager is a
        // hard refusal, never "assume depth 0". Treating an unknown id as the
        // main agent would let any unwired or forged identity mint an endless
        // chain of depth-1 children and would silently grant it the main
        // agent's session-wide authority.
        let caller_depth = match caller_depth(ctx) {
            Ok(d) => d,
            Err(e) => return ToolResult::error(e),
        };
        let child_depth = caller_depth + 1;

        if child_depth > MAX_SUBAGENT_DEPTH {
            return ToolResult::error(format!(
                "Subagent nesting depth {child_depth} exceeds the maximum of {}. \
                 Grandchildren cannot spawn further subagents.",
                MAX_SUBAGENT_DEPTH
            ));
        }

        // Generate a task id for this invocation.
        let task_id = if let Some(mgr) = ctx.get_task_manager() {
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

        // Trusted fields come from the context, never from `input`: the model
        // cannot choose its own caller identity, depth, task id, or claim that
        // its work belongs to somebody else's scheduled job.
        let request = SubagentRequest {
            agent_type,
            prompt,
            task_id: task_id.clone(),
            depth: child_depth,
            model,
            foreground: true,
            caller_task_id: ctx.caller_task_id.clone(),
            schedule_origin: ctx.schedule_origin.clone(),
        };

        let sink = std::sync::Arc::new(NullSink);
        match factory.spawn(request, sink, cancel).await {
            Ok(result) => {
                // Mark the task as completed if we registered it.
                if let Some(mgr) = ctx.get_task_manager() {
                    mgr.complete(&task_id, Some(result.clone()));
                }
                ToolResult::ok(result)
            }
            Err(e) => {
                if let Some(mgr) = ctx.get_task_manager() {
                    mgr.fail(&task_id, Some(e.clone()));
                }
                ToolResult::error(e)
            }
        }
    }
}

/// Derive the current agent's depth from the task manager.
///
/// - No caller id at all → the main agent, depth 0.
/// - A caller id that the task manager can resolve → that task's depth.
/// - A caller id that cannot be resolved (no manager wired, or an id the
///   manager has never registered) → `Err`: fail closed.
///
/// The last case is the security-relevant one. `TaskManager::is_authorized_caller`
/// already treats `None` as the main agent with session-wide authority, so the
/// one thing this function must never do is *collapse* an unverifiable identity
/// into that same `None`-shaped answer.
fn caller_depth(ctx: &ToolContext) -> Result<u32, String> {
    let task_id = match ctx.caller_task_id.as_deref() {
        Some(id) => id,
        None => return Ok(0),
    };
    let mgr = ctx.get_task_manager().ok_or_else(|| {
        "Subagent nesting is unavailable: this run carries a task identity but no task \
         manager is wired, so its depth and authority cannot be verified."
            .to_owned()
    })?;
    mgr.get(task_id).map(|s| s.depth).ok_or_else(|| {
        format!(
            "Subagent nesting is unavailable: the calling task '{task_id}' is not registered, \
             so its depth and authority cannot be verified."
        )
    })
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

    // ── STAGE 0: caller identity must fail closed ────────────────────────────

    /// Records the request the tool built so the trusted fields can be checked.
    struct RecordingFactory {
        seen: Arc<std::sync::Mutex<Vec<SubagentRequest>>>,
    }

    #[async_trait]
    impl SubagentFactory for RecordingFactory {
        async fn spawn(
            &self,
            request: SubagentRequest,
            _sink: Arc<dyn crate::events::AgentSink>,
            _cancel: CancellationToken,
        ) -> Result<String, String> {
            self.seen.lock().unwrap().push(request);
            Ok("ok".into())
        }
    }

    /// A caller id the task manager has never heard of must NOT be treated
    /// like the main agent (depth 0, full session authority). Inferring
    /// "unknown id ⇒ root" lets any unwired or forged identity spawn an
    /// unbounded chain of depth-1 children.
    #[tokio::test]
    async fn unknown_caller_task_id_fails_closed_instead_of_claiming_main_privilege() {
        let seen = Arc::new(std::sync::Mutex::new(Vec::new()));
        let dir = tempfile::tempdir().unwrap();
        let mgr = crate::tasks::TaskManager::new(
            "task-tool-guard",
            Some(dir.path().to_owned()),
            4096,
            16,
        );
        let ctx = ToolContext::new(".")
            .with_subagent_factory(Arc::new(RecordingFactory { seen: Arc::clone(&seen) }))
            .with_task_manager(Arc::clone(&mgr))
            .with_caller_task_id("task-9999");

        let result = TaskTool
            .execute(
                &serde_json::json!({"prompt": "do something"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error, "an unverifiable caller identity must be refused");
        assert!(
            seen.lock().unwrap().is_empty(),
            "nothing may be spawned for an unverifiable caller identity"
        );
        assert!(
            mgr.list().is_empty(),
            "no task may be registered for an unverifiable caller identity"
        );
    }

    /// A caller id with no task manager at all cannot be verified either.
    #[tokio::test]
    async fn caller_task_id_without_a_task_manager_fails_closed() {
        let seen = Arc::new(std::sync::Mutex::new(Vec::new()));
        let ctx = ToolContext::new(".")
            .with_subagent_factory(Arc::new(RecordingFactory { seen: Arc::clone(&seen) }))
            .with_caller_task_id("task-0001");

        let result = TaskTool
            .execute(
                &serde_json::json!({"prompt": "do something"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;

        assert!(result.is_error, "an unverifiable caller identity must be refused");
        assert!(seen.lock().unwrap().is_empty(), "nothing may be spawned");
    }

    /// Model-supplied JSON keys never reach the trusted fields of the request.
    #[tokio::test]
    async fn model_supplied_keys_cannot_forge_caller_identity() {
        let seen = Arc::new(std::sync::Mutex::new(Vec::new()));
        let dir = tempfile::tempdir().unwrap();
        let mgr = crate::tasks::TaskManager::new(
            "task-tool-spoof",
            Some(dir.path().to_owned()),
            4096,
            16,
        );
        let parent = mgr
            .register(
                crate::tasks::TaskKind::Subagent,
                "parent",
                None,
                crate::tasks::TaskExecutionMode::Foreground,
            )
            .unwrap();
        let ctx = ToolContext::new(".")
            .with_subagent_factory(Arc::new(RecordingFactory { seen: Arc::clone(&seen) }))
            .with_task_manager(Arc::clone(&mgr))
            .with_caller_task_id(&parent.id);

        let result = TaskTool
            .execute(
                &serde_json::json!({
                    "prompt": "do something",
                    "callerTaskId": "task-0001",
                    "depth": 0,
                    "taskId": "task-0001",
                }),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "{}", result.content);

        let requests = seen.lock().unwrap().clone();
        assert_eq!(requests.len(), 1);
        assert_eq!(
            requests[0].caller_task_id.as_deref(),
            Some(parent.id.as_str()),
            "the caller identity comes from the trusted context, never from tool arguments"
        );
        assert_eq!(requests[0].depth, 2, "depth is derived from the registered parent");
        assert_ne!(
            requests[0].task_id, "task-0001",
            "the child's own id is assigned by the task manager"
        );
    }
}
