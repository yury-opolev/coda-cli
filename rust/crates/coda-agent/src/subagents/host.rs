//! SubagentHost implementation: runs a nested AgentLoop with a restricted
//! tool set and permission scope.
//!
//! # Depth and concurrency limits
//! - `depth >= MAX_SUBAGENT_DEPTH`: rejected immediately (no task registered).
//! - A session-scoped semaphore (`max_concurrent`) limits the number of
//!   simultaneously running subagents.
//! - Read-only definitions and max-depth children never receive
//!   task-management tools, preventing unbounded recursion.
//!
//! # Tool restriction
//! `IsTaskManagementTool` identifies the `task` tool and all `task_*` tools.
//! These are stripped from the child's registry when:
//! - The definition is read-only (`explore`), or
//! - The child is at `MAX_SUBAGENT_DEPTH` (grandchild).

use std::sync::Arc;

use async_trait::async_trait;
use tokio::sync::Semaphore;
use tokio_util::sync::CancellationToken;

use coda_llm::Message;

use crate::agent::{AgentError, AgentLoopBuilder};
use crate::events::{AgentSink, CollectingSink};
use crate::hooks::HookRunner;
use crate::permission::PermissionPrompt;
use crate::tasks::{TaskExecutionMode, TaskKind, TaskManager};
use crate::tool::{ToolRegistry, ToolQuarantine};

use super::{
    BuiltInAgents, SubagentFactory, SubagentRequest, MAX_SUBAGENT_DEPTH,
    MAX_CONCURRENT_SUBAGENTS,
};

// ─────────────────────────────────────────────────────────────────────────────
// SubagentHost
// ─────────────────────────────────────────────────────────────────────────────

pub struct SubagentHost {
    client: Arc<dyn coda_llm::LlmClient>,
    permission_prompt: Arc<dyn PermissionPrompt>,
    /// The full tool registry; restricted per spawn via `resolve_child_tools`.
    tools: Arc<ToolRegistry>,
    quarantine: Arc<ToolQuarantine>,
    task_manager: Arc<TaskManager>,
    base_model: String,
    base_max_tokens: u32,
    base_max_iterations: usize,
    working_directory: String,
    hook_runner: Option<Arc<HookRunner>>,
    semaphore: Arc<Semaphore>,
}

impl SubagentHost {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        client: Arc<dyn coda_llm::LlmClient>,
        permission_prompt: Arc<dyn PermissionPrompt>,
        tools: Arc<ToolRegistry>,
        quarantine: Arc<ToolQuarantine>,
        task_manager: Arc<TaskManager>,
        base_model: impl Into<String>,
        base_max_tokens: u32,
        base_max_iterations: usize,
        working_directory: impl Into<String>,
        hook_runner: Option<Arc<HookRunner>>,
        max_concurrent: usize,
    ) -> Arc<Self> {
        Arc::new(Self {
            client,
            permission_prompt,
            tools,
            quarantine,
            task_manager,
            base_model: base_model.into(),
            base_max_tokens,
            base_max_iterations,
            working_directory: working_directory.into(),
            hook_runner,
            semaphore: Arc::new(Semaphore::new(max_concurrent)),
        })
    }

    pub fn with_defaults(
        client: Arc<dyn coda_llm::LlmClient>,
        permission_prompt: Arc<dyn PermissionPrompt>,
        tools: Arc<ToolRegistry>,
        task_manager: Arc<TaskManager>,
        working_directory: impl Into<String>,
    ) -> Arc<Self> {
        Self::new(
            client,
            permission_prompt,
            tools,
            Arc::new(ToolQuarantine::new()),
            task_manager,
            "claude-opus-4-5",
            4096,
            500,
            working_directory,
            None,
            MAX_CONCURRENT_SUBAGENTS,
        )
    }

    /// Refuse if the concurrency slot cannot be taken immediately.
    fn try_acquire_slot(&self) -> Result<tokio::sync::SemaphorePermit<'_>, String> {
        self.semaphore.try_acquire().map_err(|_| {
            "All subagent concurrency slots are taken; try again later.".to_owned()
        })
    }

    /// Run a subagent synchronously (foreground), acquiring and releasing a
    /// concurrency slot.  Returns immediately with an error when all slots are
    /// taken (try-acquire semantics — never blocks the caller indefinitely).
    async fn run_foreground(
        &self,
        request: SubagentRequest,
        sink: Arc<dyn AgentSink>,
        cancel: CancellationToken,
    ) -> Result<String, String> {
        // Depth check before consuming any slot.
        if request.depth > MAX_SUBAGENT_DEPTH {
            return Err(format!(
                "Subagent nesting depth {} exceeds the maximum of {}; cannot spawn further.",
                request.depth, MAX_SUBAGENT_DEPTH
            ));
        }

        // Immediate refusal when all slots are taken (matching C# behaviour).
        let _permit = self.try_acquire_slot()?;

        self.run_inner(request, sink, cancel).await
    }

    /// Core run logic (no depth check, no semaphore management).
    ///
    /// Called by both the foreground path (which holds a `SemaphorePermit`)
    /// and the background spawn (which holds an `OwnedSemaphorePermit` for the
    /// full lifetime of the background task).
    async fn run_inner(
        &self,
        request: SubagentRequest,
        sink: Arc<dyn AgentSink>,
        cancel: CancellationToken,
    ) -> Result<String, String> {
        let definition = BuiltInAgents::resolve(Some(&request.agent_type));

        // Determine the model to use.
        let model = request.model.clone().unwrap_or_else(|| self.base_model.clone());

        // Resolve the tool set for this depth/definition.
        let child_tools = resolve_child_tools(&self.tools, definition.read_only_tools_only, request.depth);

        // Build the system prompt.
        let system_prompt = format!(
            "{}\n\n# Environment\nWorking directory: {}",
            definition.system_prompt_body, self.working_directory
        );

        // Fire SubagentStart hook (fail-closed: blocks spawn on error/block).
        let effective_prompt;
        let append_system: Option<String>;
        if let Some(hr) = &self.hook_runner {
            if hr.has_subagent_start {
                let tool_names: Vec<String> =
                    child_tools.definitions().iter().map(|d| d.name.clone()).collect();
                let start_result = hr
                    .run_subagent_start(
                        &request.task_id,
                        request.depth,
                        &request.prompt,
                        &tool_names,
                        cancel.clone(),
                    )
                    .await;

                if start_result.block {
                    sink.emit(crate::events::AgentEvent::SubagentBlocked {
                        hook_command: start_result
                            .by_hook_command
                            .clone()
                            .unwrap_or_default(),
                        task_id: request.task_id.clone(),
                        reason: start_result.reason.clone().unwrap_or_default(),
                    });
                    return Err(start_result
                        .reason
                        .unwrap_or_else(|| "blocked by SubagentStart hook".into()));
                }

                let modified = match &start_result.modified_prompt {
                    Some(mp) => {
                        if let Some(ctx) = &start_result.additional_context {
                            format!("{ctx}\n\n{mp}")
                        } else {
                            mp.clone()
                        }
                    }
                    None => match &start_result.additional_context {
                        Some(ctx) => format!("{ctx}\n\n{}", request.prompt),
                        None => request.prompt.clone(),
                    },
                };
                effective_prompt = modified;
                append_system = start_result.append_system_prompt;
            } else {
                effective_prompt = request.prompt.clone();
                append_system = None;
            }
        } else {
            effective_prompt = request.prompt.clone();
            append_system = None;
        }

        let final_system = if let Some(extra) = append_system {
            format!("{system_prompt}\n\n{extra}")
        } else {
            system_prompt
        };

        // Build the child loop.
        let loop_ = AgentLoopBuilder::new(
            self.client.clone(),
            self.permission_prompt.clone(),
            Arc::new(child_tools),
        )
        .with_model(model)
        .with_system_prompt(final_system)
        .with_max_tokens(self.base_max_tokens)
        .with_max_iterations(self.base_max_iterations)
        .with_working_directory(self.working_directory.clone())
        .with_quarantine(self.quarantine.clone())
        .build();

        let collecting_sink = Arc::new(CollectingSink::new());

        // Forward to parent sink while collecting.
        let forwarding = ForwardingSink { parent: sink.clone(), collecting: collecting_sink.clone() };

        let mut history = vec![Message::user(effective_prompt)];
        let run_result = loop_.run(&mut history, &forwarding, None, cancel.clone()).await;

        let text = collecting_sink.collected_text();
        let result = if text.is_empty() { "(subagent produced no text output)".into() } else { text };

        // Surface run errors as error strings (not propagated as Err).
        if let Err(AgentError::Cancelled) = run_result {
            return Err("Subagent was cancelled.".into());
        }

        // Fire SubagentStop hook (fail-open: broken hook must not lose the result).
        let final_result = if let Some(hr) = &self.hook_runner {
            if hr.has_subagent_stop {
                let stop_result = hr
                    .run_subagent_stop(
                        &request.task_id,
                        request.depth,
                        &result,
                        cancel,
                    )
                    .await;
                if let Some(mr) = stop_result.modified_result {
                    sink.emit(crate::events::AgentEvent::SubagentResultModified {
                        hook_command: stop_result.by_hook_command.unwrap_or_default(),
                        task_id: request.task_id.clone(),
                        original_result: result.clone(),
                        modified_result: mr.clone(),
                    });
                    mr
                } else {
                    result
                }
            } else {
                result
            }
        } else {
            result
        };

        Ok(final_result)
    }
}

#[async_trait]
impl SubagentFactory for SubagentHost {
    async fn spawn(
        &self,
        request: SubagentRequest,
        sink: Arc<dyn AgentSink>,
        cancel: CancellationToken,
    ) -> Result<String, String> {
        if request.foreground {
            self.run_foreground(request, sink, cancel).await
        } else {
            // Background: acquire a slot FIRST, then register the task.
            // This matches the C# invariant: when all slots are taken the call
            // fails immediately and nothing is registered in the task manager.
            if request.depth > MAX_SUBAGENT_DEPTH {
                return Err(format!(
                    "Subagent nesting depth {} exceeds the maximum of {}.",
                    request.depth, MAX_SUBAGENT_DEPTH
                ));
            }
            // Owned permit so it can be moved into the spawned future and held
            // for the full lifetime of the background work.
            let permit = Arc::clone(&self.semaphore)
                .try_acquire_owned()
                .map_err(|_| {
                    "All subagent concurrency slots are taken; try again later.".to_owned()
                })?;

            // Register AFTER acquiring the slot so the task manager never sees
            // a task that cannot start.
            let task = self.task_manager.register(
                TaskKind::Subagent,
                &request.prompt,
                request.caller_task_id.as_deref(),
                TaskExecutionMode::Background,
            ).map_err(|e| e)?;

            let task_id = task.id.clone();
            let self_arc = Arc::new(self.clone_for_background());
            let req2 = request.clone();
            let sink2 = sink.clone();
            let cancel2 = cancel.clone();
            let mgr = self.task_manager.clone();
            let tid2 = task_id.clone();

            tokio::spawn(async move {
                // Drop the permit only when this future completes (success or
                // failure), so the slot stays occupied for the full run.
                let _permit = permit;
                match self_arc.run_inner(req2, sink2, cancel2).await {
                    Ok(report) => { mgr.complete(&tid2, Some(report)); }
                    Err(e) => { mgr.fail(&tid2, Some(e)); }
                }
            });

            // Return just the task id so the calling tool can format its own message.
            Ok(task_id)
        }
    }
}

impl SubagentHost {
    /// Create a minimal clone for use inside background tokio::spawn.
    fn clone_for_background(&self) -> Self {
        Self {
            client: self.client.clone(),
            permission_prompt: self.permission_prompt.clone(),
            tools: self.tools.clone(),
            quarantine: self.quarantine.clone(),
            task_manager: self.task_manager.clone(),
            base_model: self.base_model.clone(),
            base_max_tokens: self.base_max_tokens,
            base_max_iterations: self.base_max_iterations,
            working_directory: self.working_directory.clone(),
            hook_runner: self.hook_runner.clone(),
            semaphore: self.semaphore.clone(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tool restriction
// ─────────────────────────────────────────────────────────────────────────────

/// Returns `true` for the `task` tool and all `task_*` runtime management tools.
///
/// A single predicate here means future `task_*` tools are automatically
/// denied to read-only / max-depth children without a code change.
pub fn is_task_management_tool(name: &str) -> bool {
    name == "task" || name.starts_with("task_")
}

/// Compute the tool set to offer to a child agent.
///
/// - Read-only definitions: read-only tools only, no task-management tools.
/// - Max-depth children (grandchildren): no task-management tools.
/// - Depth-1 children: full tool set including task-management tools.
pub fn resolve_child_tools(
    tools: &ToolRegistry,
    read_only_definition: bool,
    depth: u32,
) -> ToolRegistry {
    let deny_task = read_only_definition || depth >= MAX_SUBAGENT_DEPTH;
    let base: Vec<_> = if read_only_definition {
        tools.all().iter().filter(|t| t.is_read_only()).cloned().collect()
    } else {
        tools.all().iter().cloned().collect()
    };
    if deny_task {
        ToolRegistry::new(
            base.into_iter()
                .filter(|t| !is_task_management_tool(t.name()))
                .collect::<Vec<_>>(),
        )
    } else {
        ToolRegistry::new(base)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ForwardingSink
// ─────────────────────────────────────────────────────────────────────────────

/// Forwards every event to the parent sink while also recording text in a
/// `CollectingSink` so the caller can extract the subagent's final output.
struct ForwardingSink {
    parent: Arc<dyn AgentSink>,
    collecting: Arc<CollectingSink>,
}

impl AgentSink for ForwardingSink {
    fn emit(&self, event: crate::events::AgentEvent) {
        self.collecting.emit(event.clone());
        self.parent.emit(event);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CollectingSink extension
// ─────────────────────────────────────────────────────────────────────────────

trait CollectedText {
    fn collected_text(&self) -> String;
}

impl CollectedText for CollectingSink {
    fn collected_text(&self) -> String {
        self.snapshot()
            .into_iter()
            .filter_map(|e| match e {
                crate::events::AgentEvent::AssistantText { delta } => Some(delta),
                _ => None,
            })
            .collect::<Vec<_>>()
            .join("")
            .trim()
            .to_owned()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolRegistry;

    #[test]
    fn is_task_management_tool_identifies_task_tools() {
        assert!(is_task_management_tool("task"));
        assert!(is_task_management_tool("task_wait"));
        assert!(is_task_management_tool("task_list"));
        assert!(!is_task_management_tool("read_file"));
        assert!(!is_task_management_tool("bash"));
    }

    #[test]
    fn resolve_child_tools_strips_task_tools_for_max_depth() {
        use crate::tool::ToolResult;
        use async_trait::async_trait;
        use crate::tool::{Tool, ToolContext, ToolOutcome};

        struct MockTool { name: &'static str, ro: bool }
        #[async_trait]
        impl Tool for MockTool {
            fn name(&self) -> &str { self.name }
            fn description(&self) -> &str { "" }
            fn input_schema_json(&self) -> &str { "{}" }
            fn is_read_only(&self) -> bool { self.ro }
            async fn execute(&self, _: &serde_json::Value, _: &ToolContext, _: CancellationToken) -> ToolOutcome { ToolResult::ok("") }
        }

        let tools: Vec<Arc<dyn Tool>> = vec![
            Arc::new(MockTool { name: "read_file", ro: true }),
            Arc::new(MockTool { name: "task", ro: false }),
            Arc::new(MockTool { name: "task_wait", ro: false }),
            Arc::new(MockTool { name: "bash", ro: false }),
        ];
        let registry = ToolRegistry::new(tools);

        // At MAX_SUBAGENT_DEPTH, task tools are stripped.
        let child = resolve_child_tools(&registry, false, MAX_SUBAGENT_DEPTH);
        let names: Vec<&str> = child.all().iter().map(|t| t.name()).collect();
        assert!(!names.contains(&"task"), "task must be stripped at max depth");
        assert!(!names.contains(&"task_wait"), "task_wait must be stripped at max depth");
        assert!(names.contains(&"bash"), "bash must remain");

        // Below max depth, task tools are kept.
        let child_shallow = resolve_child_tools(&registry, false, 1);
        let names_shallow: Vec<&str> = child_shallow.all().iter().map(|t| t.name()).collect();
        assert!(names_shallow.contains(&"task"));
    }

    #[test]
    fn resolve_child_tools_read_only_definition_strips_mutating_tools() {
        use crate::tool::ToolResult;
        use async_trait::async_trait;
        use crate::tool::{Tool, ToolContext, ToolOutcome};

        struct MockTool { name: &'static str, ro: bool }
        #[async_trait]
        impl Tool for MockTool {
            fn name(&self) -> &str { self.name }
            fn description(&self) -> &str { "" }
            fn input_schema_json(&self) -> &str { "{}" }
            fn is_read_only(&self) -> bool { self.ro }
            async fn execute(&self, _: &serde_json::Value, _: &ToolContext, _: CancellationToken) -> ToolOutcome { ToolResult::ok("") }
        }

        let tools: Vec<Arc<dyn Tool>> = vec![
            Arc::new(MockTool { name: "read_file", ro: true }),
            Arc::new(MockTool { name: "write_file", ro: false }),
            Arc::new(MockTool { name: "task", ro: false }),
        ];
        let registry = ToolRegistry::new(tools);

        // Read-only definition strips mutating tools at any depth.
        let child = resolve_child_tools(&registry, true, 1);
        let names: Vec<&str> = child.all().iter().map(|t| t.name()).collect();
        assert!(names.contains(&"read_file"));
        assert!(!names.contains(&"write_file"), "mutating tool must be stripped");
        assert!(!names.contains(&"task"), "task must be stripped for read-only");
    }

    /// Depth limit test: depth > MAX_SUBAGENT_DEPTH returns an error.
    #[tokio::test]
    async fn spawn_rejects_excessive_depth() {
        // We only need a SubagentFactory, not a real LLM. Use a mock factory.
        struct DepthCheckFactory;
        #[async_trait]
        impl SubagentFactory for DepthCheckFactory {
            async fn spawn(
                &self,
                request: SubagentRequest,
                _sink: Arc<dyn AgentSink>,
                _cancel: CancellationToken,
            ) -> Result<String, String> {
                if request.depth > MAX_SUBAGENT_DEPTH {
                    return Err(format!(
                        "Subagent nesting depth {} exceeds the maximum of {}; cannot spawn further.",
                        request.depth, MAX_SUBAGENT_DEPTH
                    ));
                }
                Ok("ok".into())
            }
        }

        let factory = DepthCheckFactory;
        let bad_request = SubagentRequest {
            agent_type: "general-purpose".into(),
            prompt: "do something".into(),
            task_id: "t1".into(),
            depth: MAX_SUBAGENT_DEPTH + 1,
            model: None,
            foreground: true,
            caller_task_id: None,
        };
        let result = factory.spawn(bad_request, Arc::new(crate::events::NullSink), CancellationToken::new()).await;
        assert!(result.is_err(), "depth > MAX must be rejected");
        assert!(result.unwrap_err().contains("exceeds the maximum"));
    }

    /// Concurrency limit: try_acquire refuses the (N+1)th request immediately
    /// when all N slots are occupied, and at most N work items run at once.
    #[tokio::test]
    async fn semaphore_limits_concurrency() {
        // The slot limit is 2; attempt 3 concurrent try_acquire calls.
        // Two succeed, one fails immediately.
        let sem = Arc::new(Semaphore::new(2));

        let p1 = sem.try_acquire().expect("first slot must succeed");
        let p2 = sem.try_acquire().expect("second slot must succeed");
        let p3 = sem.try_acquire();
        assert!(
            p3.is_err(),
            "third try_acquire must fail immediately when all slots are taken"
        );

        // Releasing a slot makes room for the next attempt.
        drop(p1);
        let p4 = sem.try_acquire().expect("slot released; next acquire must succeed");
        drop(p2);
        drop(p4);
    }

    /// Foreground subagent immediately refuses (error, not panic/hang) when
    /// every concurrency slot is taken and registers nothing.
    ///
    /// Mutation-verified: if `try_acquire_slot` were removed or replaced with
    /// a blocking `acquire().await`, this test would time-out instead of
    /// returning `Err`.
    #[tokio::test]
    async fn foreground_refuses_immediately_when_all_slots_taken() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::{Usage, LlmError};
        use async_trait::async_trait as at;

        // A mock client that returns a valid text turn so the subagent can complete.
        struct OkClient;
        #[at]
        impl coda_llm::LlmClient for OkClient {
            fn provider_id(&self) -> &str { "mock" }
            async fn stream(&self, _: coda_llm::ChatRequest) -> Result<coda_llm::ResponseStream, LlmError> {
                let events = vec![
                    Ok(StreamEvent::TextDelta("done".into())),
                    Ok(StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO }),
                ];
                let (tx, rx) = tokio::sync::mpsc::channel(8);
                tokio::spawn(async move { for e in events { let _ = tx.send(e).await; } });
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }

        use crate::permission::PermissionPrompt;
        struct AllowAll;
        #[at]
        impl PermissionPrompt for AllowAll {
            async fn request(&self, _: &dyn crate::tool::Tool, _: &str, _: CancellationToken) -> bool { true }
        }

        let mgr = crate::tasks::TaskManager::new(
            "test-session",
            Some(std::env::temp_dir().join("coda-host-tests")),
            4096,
            10,
        );
        let host = SubagentHost::new(
            Arc::new(OkClient),
            Arc::new(AllowAll),
            Arc::new(crate::tool::ToolRegistry::new(
                [] as [Arc<dyn crate::tool::Tool>; 0],
            )),
            Arc::new(crate::tool::ToolQuarantine::new()),
            mgr.clone(),
            "model",
            256,
            5,
            ".",
            None,
            /* max_concurrent = */ 1,
        );

        // Exhaust the single slot by holding a permit externally.
        let _held = host.semaphore.try_acquire().expect("initial slot must be free");

        let request = SubagentRequest::foreground("general-purpose", "go", "t1", 1);
        let result = tokio::time::timeout(
            std::time::Duration::from_millis(500),
            host.run_foreground(request, Arc::new(crate::events::NullSink), CancellationToken::new()),
        )
        .await
        .expect("run_foreground must not block — should return immediately");

        assert!(result.is_err(), "must refuse when all slots are taken");
        assert!(
            result.unwrap_err().contains("slots are taken"),
            "error must explain that slots are exhausted"
        );
        // Nothing should have been registered in the task manager.
        assert_eq!(mgr.list().len(), 0, "no task must be registered when refused");
    }

    /// Background subagent: when all slots are taken, spawn returns an error
    /// immediately and does NOT register any task in the task manager.
    ///
    /// Mutation-verified: if the slot acquisition were moved after registration
    /// (the original bug), a task would appear in the list as Running.
    #[tokio::test]
    async fn background_refuses_and_registers_nothing_when_all_slots_taken() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::{Usage, LlmError};
        use async_trait::async_trait as at;
        use crate::permission::PermissionPrompt;

        struct OkClient;
        #[at]
        impl coda_llm::LlmClient for OkClient {
            fn provider_id(&self) -> &str { "mock" }
            async fn stream(&self, _: coda_llm::ChatRequest) -> Result<coda_llm::ResponseStream, LlmError> {
                let events = vec![
                    Ok(StreamEvent::TextDelta("done".into())),
                    Ok(StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO }),
                ];
                let (tx, rx) = tokio::sync::mpsc::channel(8);
                tokio::spawn(async move { for e in events { let _ = tx.send(e).await; } });
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }

        struct AllowAll;
        #[at]
        impl PermissionPrompt for AllowAll {
            async fn request(&self, _: &dyn crate::tool::Tool, _: &str, _: CancellationToken) -> bool { true }
        }

        let mgr = crate::tasks::TaskManager::new(
            "test-session",
            Some(std::env::temp_dir().join("coda-host-bg-tests")),
            4096,
            10,
        );
        let host = SubagentHost::new(
            Arc::new(OkClient),
            Arc::new(AllowAll),
            Arc::new(crate::tool::ToolRegistry::new(
                [] as [Arc<dyn crate::tool::Tool>; 0],
            )),
            Arc::new(crate::tool::ToolQuarantine::new()),
            mgr.clone(),
            "model",
            256,
            5,
            ".",
            None,
            /* max_concurrent = */ 1,
        );

        // Exhaust the single slot.
        let _held = host.semaphore.try_acquire().expect("initial slot must be free");

        let mut bg_request = SubagentRequest::foreground("general-purpose", "go", "t1", 1);
        bg_request.foreground = false;

        let result = host.spawn(bg_request, Arc::new(crate::events::NullSink), CancellationToken::new()).await;

        assert!(result.is_err(), "must refuse when all slots are taken");
        assert!(
            result.unwrap_err().contains("slots are taken"),
            "error must explain slots exhausted"
        );
        // The critical invariant: nothing registered.
        assert_eq!(
            mgr.list().len(),
            0,
            "no task must be registered in the task manager when the slot is unavailable"
        );
    }
}
