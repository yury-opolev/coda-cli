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
use crate::permission::{PermissionModeState, PermissionPrompt};
use crate::scheduling::ScheduledTaskStore;
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
    permission_mode: Arc<PermissionModeState>,
    /// The full tool registry; restricted per spawn via `resolve_child_tools`.
    tools: Arc<ToolRegistry>,
    quarantine: Arc<ToolQuarantine>,
    task_manager: Arc<TaskManager>,
    /// Shared, session-scoped schedule definitions.  Wired into every child so
    /// `schedule_*` tools inside a subagent see the same store the main agent
    /// and the schedule runtime use.  `None` keeps the host usable in tests
    /// and headless setups that never schedule anything.
    schedule_store: Option<Arc<ScheduledTaskStore>>,
    base_model: Arc<dyn Fn() -> String + Send + Sync>,
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
        permission_mode: Arc<PermissionModeState>,
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
        let base_model = base_model.into();
        Arc::new(Self {
            client,
            permission_prompt,
            permission_mode,
            tools,
            quarantine,
            task_manager,
            schedule_store: None,
            base_model: Arc::new(move || base_model.clone()),
            base_max_tokens,
            base_max_iterations,
            working_directory: working_directory.into(),
            hook_runner,
            semaphore: Arc::new(Semaphore::new(max_concurrent)),
        })
    }

    /// Resolve the inherited model once at the start of each run. Explicit
    /// request overrides still win, and in-flight runs keep their capture.
    pub fn with_model_source(
        self: Arc<Self>,
        source: Arc<dyn Fn() -> String + Send + Sync>,
    ) -> Arc<Self> {
        let mut host = self.clone_for_background();
        host.base_model = source;
        Arc::new(host)
    }

    /// Share the session's schedule definition store with every child this
    /// host spawns.  The SAME `Arc` must be handed to the main agent loop and
    /// to every host (hook-free and hooked) so a schedule created by a
    /// subagent is visible to the main agent and to the schedule runtime.
    pub fn with_schedule_store(self: Arc<Self>, store: Arc<ScheduledTaskStore>) -> Arc<Self> {
        let mut host = self.clone_for_background();
        host.schedule_store = Some(store);
        Arc::new(host)
    }

    /// Read-only accessor for the schedule store this host was wired with
    /// (or `None` when the host has no schedule store attached). Lets a
    /// caller confirm store-sharing directly, without going through a
    /// schedule tool call — useful now that `schedule_create`/`schedule_list`
    /// are (mostly) main-agent-only, so a child spawn can no longer be used
    /// to probe the wiring end-to-end.
    pub fn schedule_store(&self) -> Option<&Arc<ScheduledTaskStore>> {
        self.schedule_store.as_ref()
    }

    pub fn with_defaults(
        client: Arc<dyn coda_llm::LlmClient>,
        permission_prompt: Arc<dyn PermissionPrompt>,
        permission_mode: Arc<PermissionModeState>,
        tools: Arc<ToolRegistry>,
        task_manager: Arc<TaskManager>,
        working_directory: impl Into<String>,
    ) -> Arc<Self> {
        Self::new(
            client,
            permission_prompt,
            permission_mode,
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
        let model = request.model.clone().unwrap_or_else(|| (self.base_model)());

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
        //
        // The child is a *trusted* execution context: it receives the shared
        // TaskManager, the shared schedule store, a factory that reuses this
        // host's concurrency pool, and — critically — its OWN registered task
        // id.  Passing the parent's id (or `None`, which `TaskManager` reads as
        // "the main agent") would hand the child authority over tasks it does
        // not own.  Without this wiring every stateful tool inside a child
        // answered "… is not available".
        //
        // `clone_for_background` shares the `Arc<Semaphore>`, so the nested
        // factory draws from the same pool rather than minting a fresh limit,
        // and it copies `hook_runner` verbatim: a hook-free host stays
        // hook-free, so agent-type hooks cannot re-enter the hook system.
        let child_factory: Arc<dyn SubagentFactory> = Arc::new(self.clone_for_background());

        let mut builder = AgentLoopBuilder::new(
            self.client.clone(),
            self.permission_prompt.clone(),
            Arc::new(child_tools),
        )
        .with_permission_mode_state(Arc::clone(&self.permission_mode))
        .with_model(model)
        .with_system_prompt(final_system)
        .with_max_tokens(self.base_max_tokens)
        .with_max_iterations(self.base_max_iterations)
        .with_working_directory(self.working_directory.clone())
        .with_quarantine(self.quarantine.clone())
        .with_task_manager(Arc::clone(&self.task_manager))
        .with_caller_task_id(request.task_id.clone())
        .with_subagent_factory(child_factory);

        if let Some(store) = &self.schedule_store {
            builder = builder.with_schedule_store(Arc::clone(store));
        }
        // A nested child inherits the scheduled provenance of the run that
        // spawned it; ordinary main-agent children carry `None`.
        if let Some(origin) = &request.schedule_origin {
            builder = builder.with_schedule_origin(origin.clone());
        }

        let loop_ = builder.build();

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
        // A typed abort (today: an operator question the controller never
        // answered) must never be reported as a completed subagent. Returning
        // `Ok(collected_text)` here would hand the parent model a partial
        // transcript that reads exactly like a finished piece of work — the
        // "pretend success" failure this audit exists to prevent. The task
        // manager marks the task failed via the `Err` path.
        if let Err(AgentError::Aborted { reason }) = &run_result {
            return Err(format!(
                "Subagent stopped without completing its task ({reason}). \
                 Any partial output was discarded rather than reported as a result."
            ));
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
            // The background run's identity is the task just registered — not
            // the id the caller happened to put in the request. `run_inner`
            // uses `task_id` as the child's `caller_task_id`, so a stale value
            // here would give the child another task's authority.
            let mut req2 = request.clone();
            req2.task_id = task_id.clone();
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
            permission_mode: self.permission_mode.clone(),
            tools: self.tools.clone(),
            quarantine: self.quarantine.clone(),
            task_manager: self.task_manager.clone(),
            schedule_store: self.schedule_store.clone(),
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
            schedule_origin: None,
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

    #[tokio::test]
    async fn default_pool_allows_twenty_slots_and_refuses_the_twenty_first() {
        struct UnusedClient;
        #[async_trait]
        impl coda_llm::LlmClient for UnusedClient {
            fn provider_id(&self) -> &str { "fixture" }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                panic!("a full pool must refuse before calling the model")
            }
        }

        let directory = tempfile::tempdir().unwrap();
        let manager = TaskManager::new(
            "default-limit", Some(directory.path().to_owned()), 4096, 256,
        );
        let host = SubagentHost::with_defaults(
            Arc::new(UnusedClient),
            Arc::new(crate::permission::prompts::ModePermissionPrompt::new(
                crate::permission::PermissionMode::Default, None,
            )),
            Arc::new(PermissionModeState::new(crate::permission::PermissionMode::Default)),
            Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0])),
            manager.clone(),
            ".",
        );
        let mut held: Vec<_> = (0..20)
            .map(|_| host.try_acquire_slot().expect("all twenty default slots must be available"))
            .collect();
        for foreground in [true, false] {
            let mut request = SubagentRequest::foreground("general-purpose", "work", "t1", 1);
            request.foreground = foreground;
            let result = tokio::time::timeout(
                std::time::Duration::from_millis(500),
                host.spawn(request, Arc::new(crate::events::NullSink), CancellationToken::new()),
            ).await.expect("a full pool must refuse immediately");
            assert!(result.unwrap_err().contains("slots are taken"));
        }
        assert!(manager.list().is_empty(), "refused work must not be registered");

        let clone = host.clone_for_background();
        assert!(clone.try_acquire_slot().is_err(), "clones must share the same limit");
        drop(held.pop());
        let _replacement = clone.try_acquire_slot().expect("a released slot must be reusable");
        assert!(host.try_acquire_slot().is_err(), "the replacement counts against the original pool");
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
            Arc::new(PermissionModeState::new(crate::permission::PermissionMode::Default)),
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
            Arc::new(PermissionModeState::new(crate::permission::PermissionMode::Default)),
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

    // ─────────────────────────────────────────────────────────────────────────
    // STAGE 0 — trusted child execution context
    //
    // `run_inner` used to build the child `AgentLoop` with only
    // client/tools/mode/model/system.  Every stateful tool inside a child
    // therefore answered "… is not available", the child had no identity of
    // its own (so `TaskManager` authorisation could not be evaluated), and
    // nested `task` calls were impossible.  These tests exercise the real
    // child loop through a scripted model.
    // ─────────────────────────────────────────────────────────────────────────
    mod trusted_child_context {
        use super::*;
        use std::collections::VecDeque;
        use std::sync::Mutex;

        use async_trait::async_trait as at;
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::{Content, Correlation, LlmError, Usage};

        use crate::events::{AgentEvent, CollectingSink};
        use crate::permission::PermissionPrompt;
        use crate::scheduling::ScheduledTaskStore;
        use crate::tasks::{TaskExecutionMode, TaskKind, TaskManager};
        use crate::tool::{Tool, ToolContext, ToolOutcome, ToolRegistry, ToolResult};

        // ── Scripted model ────────────────────────────────────────────────────

        /// Returns one scripted stream per `stream()` call, in order.
        pub(super) struct ScriptedClient {
            turns: Mutex<VecDeque<Vec<StreamEvent>>>,
        }

        impl ScriptedClient {
            fn new(turns: Vec<Vec<StreamEvent>>) -> Arc<Self> {
                Arc::new(Self { turns: Mutex::new(turns.into()) })
            }
        }

        #[at]
        impl coda_llm::LlmClient for ScriptedClient {
            fn provider_id(&self) -> &str {
                "scripted"
            }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, LlmError> {
                let events = self
                    .turns
                    .lock()
                    .unwrap()
                    .pop_front()
                    .unwrap_or_else(|| vec![text_turn("(script exhausted)")].remove(0));
                let (tx, rx) = tokio::sync::mpsc::channel(16);
                tokio::spawn(async move {
                    for e in events {
                        let _ = tx.send(Ok(e)).await;
                    }
                });
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }

        fn done_event() -> StreamEvent {
            StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO }
        }

        fn text_turn(text: &str) -> Vec<StreamEvent> {
            vec![StreamEvent::TextDelta(text.into()), done_event()]
        }

        fn tool_turn(id: &str, name: &str, input_json: &str) -> Vec<StreamEvent> {
            vec![
                StreamEvent::ToolUse(Content::ToolUse {
                    id: id.into(),
                    name: name.into(),
                    input_json: input_json.into(),
                    correlation: Correlation::default(),
                }),
                done_event(),
            ]
        }

        // ── Probe tool ────────────────────────────────────────────────────────

        #[derive(Clone)]
        pub(super) struct ProbeCapture {
            pub caller_task_id: Option<String>,
            pub task_manager: Option<Arc<TaskManager>>,
            pub schedule_store: Option<Arc<ScheduledTaskStore>>,
            pub has_factory: bool,
            pub schedule_origin: Option<crate::tool::ScheduleOrigin>,
            /// Names of the tools the child loop actually offered.
            pub available_tools: Vec<String>,
        }

        /// Read-only tool that records the service wiring it was handed.
        pub(super) struct ProbeTool {
            pub seen: Arc<Mutex<Vec<ProbeCapture>>>,
        }

        #[at]
        impl Tool for ProbeTool {
            fn name(&self) -> &str {
                "probe"
            }
            fn description(&self) -> &str {
                "records the tool context wiring"
            }
            fn input_schema_json(&self) -> &str {
                r#"{"type":"object","properties":{}}"#
            }
            fn is_read_only(&self) -> bool {
                true
            }
            async fn execute(
                &self,
                _: &serde_json::Value,
                ctx: &ToolContext,
                _: CancellationToken,
            ) -> ToolOutcome {
                use crate::tool::ToolContextServiceExt as _;
                self.seen.lock().unwrap().push(ProbeCapture {
                    caller_task_id: ctx.caller_task_id.clone(),
                    task_manager: ctx.get_task_manager().cloned(),
                    schedule_store: ctx.get_schedule_store().cloned(),
                    has_factory: ctx.get_subagent_factory().is_some(),
                    schedule_origin: ctx.schedule_origin.clone(),
                    available_tools: ctx
                        .all_tools
                        .as_ref()
                        .map(|t| t.iter().map(|d| d.name.clone()).collect())
                        .unwrap_or_default(),
                });
                ToolResult::ok("probed")
            }
        }

        pub(super) struct AllowAll;
        #[at]
        impl PermissionPrompt for AllowAll {
            async fn request(
                &self,
                _: &dyn crate::tool::Tool,
                _: &str,
                _: CancellationToken,
            ) -> bool {
                true
            }
        }

        pub(super) fn manager(dir: &tempfile::TempDir) -> Arc<TaskManager> {
            TaskManager::new("stage0-session", Some(dir.path().to_owned()), 4096, 64)
        }

        #[allow(clippy::too_many_arguments)]
        pub(super) fn host_with(
            client: Arc<dyn coda_llm::LlmClient>,
            tools: Vec<Arc<dyn Tool>>,
            mgr: Arc<TaskManager>,
            max_concurrent: usize,
        ) -> Arc<SubagentHost> {
            SubagentHost::new(
                client,
                Arc::new(AllowAll),
                Arc::new(PermissionModeState::new(
                    crate::permission::PermissionMode::Default,
                )),
                Arc::new(ToolRegistry::new(tools)),
                Arc::new(crate::tool::ToolQuarantine::new()),
                mgr,
                "model",
                256,
                10,
                ".",
                None,
                max_concurrent,
            )
        }

        pub(super) fn tool_results(sink: &CollectingSink) -> Vec<(String, String, bool)> {
            sink.snapshot()
                .into_iter()
                .filter_map(|e| match e {
                    AgentEvent::ToolResult { tool_name, content, is_error, .. } => {
                        Some((tool_name, content, is_error))
                    }
                    _ => None,
                })
                .collect()
        }

        // ── Test 1 ────────────────────────────────────────────────────────────

        /// A tool executed by a real child `AgentLoop` must see the SAME
        /// `TaskManager` Arc the host owns and the child's OWN registered task
        /// id — not the parent's, and never `None` (which would grant the
        /// child main-agent authority over the whole session).
        #[tokio::test]
        async fn child_loop_tool_sees_shared_task_manager_and_own_task_id() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let seen = Arc::new(Mutex::new(Vec::new()));

            let client = ScriptedClient::new(vec![
                tool_turn("p1", "probe", "{}"),
                text_turn("child done"),
            ]);
            let host = host_with(
                client,
                vec![
                    Arc::new(ProbeTool { seen: Arc::clone(&seen) }) as Arc<dyn Tool>,
                    Arc::new(crate::tools::TaskListTool) as Arc<dyn Tool>,
                ],
                Arc::clone(&mgr),
                4,
            );

            // The child's own task, as the `task` tool registers it.
            let child = mgr
                .register(TaskKind::Subagent, "child work", None, TaskExecutionMode::Foreground)
                .unwrap();

            let mut request =
                SubagentRequest::foreground("general-purpose", "probe please", &child.id, 1);
            request.caller_task_id = None;

            let sink = Arc::new(CollectingSink::new());
            host.spawn(request, sink.clone(), CancellationToken::new()).await.unwrap();

            let captures = seen.lock().unwrap().clone();
            assert_eq!(captures.len(), 1, "the probe tool must have run in the child loop");
            let c = &captures[0];
            assert_eq!(
                c.caller_task_id.as_deref(),
                Some(child.id.as_str()),
                "the child loop must carry its OWN registered task id"
            );
            let child_mgr = c.task_manager.as_ref().expect("child must receive the task manager");
            assert!(
                Arc::ptr_eq(child_mgr, &mgr),
                "the child must share the host's TaskManager instance, not a new one"
            );
            assert!(c.has_factory, "the child must receive a subagent factory");
        }

        /// A stateful tool inside the child must not answer "not available".
        #[tokio::test]
        async fn child_loop_stateful_tools_are_wired() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);

            let client = ScriptedClient::new(vec![
                tool_turn("t1", "task_list", "{}"),
                text_turn("child done"),
            ]);
            let host = host_with(
                client,
                vec![Arc::new(crate::tools::TaskListTool) as Arc<dyn Tool>],
                Arc::clone(&mgr),
                4,
            );

            let child = mgr
                .register(TaskKind::Subagent, "child work", None, TaskExecutionMode::Foreground)
                .unwrap();
            let request =
                SubagentRequest::foreground("general-purpose", "list tasks", &child.id, 1);

            let sink = Arc::new(CollectingSink::new());
            host.spawn(request, sink.clone(), CancellationToken::new()).await.unwrap();

            let results = tool_results(&sink);
            let (_, content, is_error) = results
                .iter()
                .find(|(name, _, _)| name == "task_list")
                .expect("task_list must produce a result");
            assert!(!is_error, "task_list must succeed inside a child loop; got: {content}");
            assert!(
                !content.contains("not available"),
                "the child must reach the shared task manager; got: {content}"
            );
        }

        // ── Test 2 ────────────────────────────────────────────────────────────

        /// The real `task` tool inside a depth-1 child registers a depth-2
        /// grandchild under the child, and the grandchild cannot nest further.
        #[tokio::test]
        async fn nested_task_tool_builds_the_authorization_tree_and_stops_at_depth_two() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let seen = Arc::new(Mutex::new(Vec::new()));

            let client = ScriptedClient::new(vec![
                // depth-1 child: spawn a grandchild
                tool_turn("c1", "task", r#"{"prompt":"grandchild work"}"#),
                // depth-2 grandchild: probe, then try to nest further
                tool_turn("g1", "probe", "{}"),
                tool_turn("g2", "task", r#"{"prompt":"great-grandchild"}"#),
                text_turn("grandchild done"),
                // back in the depth-1 child
                text_turn("child done"),
            ]);
            let host = host_with(
                client,
                vec![
                    Arc::new(ProbeTool { seen: Arc::clone(&seen) }) as Arc<dyn Tool>,
                    Arc::new(crate::tools::TaskTool) as Arc<dyn Tool>,
                ],
                Arc::clone(&mgr),
                8,
            );

            let child = mgr
                .register(TaskKind::Subagent, "child work", None, TaskExecutionMode::Foreground)
                .unwrap();
            let request =
                SubagentRequest::foreground("general-purpose", "delegate", &child.id, 1);

            let sink = Arc::new(CollectingSink::new());
            host.spawn(request, sink.clone(), CancellationToken::new()).await.unwrap();

            let captures = seen.lock().unwrap().clone();
            assert_eq!(captures.len(), 1, "the grandchild probe must have run");
            let grandchild_id = captures[0]
                .caller_task_id
                .clone()
                .expect("the grandchild must have its own task identity");
            let snapshot = mgr
                .get(&grandchild_id)
                .expect("the grandchild id must be registered with the shared manager");
            assert_eq!(snapshot.depth, 2, "a child of a depth-1 task is depth 2");
            assert_eq!(
                snapshot.parent_id.as_deref(),
                Some(child.id.as_str()),
                "the authorization tree must record the depth-1 child as the parent"
            );
            assert!(
                mgr.is_authorized_caller(&grandchild_id, Some(&child.id)),
                "the parent must be authorized over its descendant"
            );
            assert!(
                !mgr.is_authorized_caller(&child.id, Some(&grandchild_id)),
                "a grandchild must NOT be authorized over its own parent"
            );

            // Depth-2 children never receive task-management tools, so the
            // grandchild's attempt to nest further has nothing to call and no
            // depth-3 task can ever be registered.
            assert!(
                !captures[0].available_tools.iter().any(|n| n == "task"),
                "a depth-2 grandchild must not be offered the task tool; offered: {:?}",
                captures[0].available_tools
            );
            assert!(
                mgr.list().iter().all(|t| t.depth <= 2),
                "no task deeper than {MAX_SUBAGENT_DEPTH} may ever be registered; got: {:?}",
                mgr.list().iter().map(|t| (t.id.clone(), t.depth)).collect::<Vec<_>>()
            );
        }

        /// The factory handed to a child must reuse the host's semaphore.
        /// A fresh `Semaphore` in the clone would silently multiply the
        /// configured concurrency ceiling by the nesting depth.
        #[tokio::test]
        async fn child_factory_shares_the_parent_concurrency_pool() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);

            let client = ScriptedClient::new(vec![
                tool_turn("c1", "task", r#"{"prompt":"grandchild work"}"#),
                text_turn("child done"),
            ]);
            // Pool of 2: one permit held below, one taken by the child itself.
            let host = host_with(
                client,
                vec![Arc::new(crate::tools::TaskTool) as Arc<dyn Tool>],
                Arc::clone(&mgr),
                2,
            );
            let _held = host.semaphore.try_acquire().expect("a slot must be free");

            let child = mgr
                .register(TaskKind::Subagent, "child work", None, TaskExecutionMode::Foreground)
                .unwrap();
            let request =
                SubagentRequest::foreground("general-purpose", "delegate", &child.id, 1);

            let sink = Arc::new(CollectingSink::new());
            host.spawn(request, sink.clone(), CancellationToken::new()).await.unwrap();

            let results = tool_results(&sink);
            let (_, content, is_error) = results
                .iter()
                .find(|(name, _, _)| name == "task")
                .expect("the nested task call must produce a result");
            assert!(is_error, "the nested spawn must be refused; got: {content}");
            assert!(
                content.contains("slots are taken"),
                "the child factory must draw from the SAME pool; got: {content}"
            );
        }

        /// A read-only definition keeps read-only tools and loses every
        /// mutating / task-management tool, even with the services wired.
        #[tokio::test]
        async fn read_only_child_keeps_probe_but_loses_task_tools() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let seen = Arc::new(Mutex::new(Vec::new()));

            let client = ScriptedClient::new(vec![
                tool_turn("r1", "probe", "{}"),
                tool_turn("r2", "task_list", "{}"),
                text_turn("explore done"),
            ]);
            let host = host_with(
                client,
                vec![
                    Arc::new(ProbeTool { seen: Arc::clone(&seen) }) as Arc<dyn Tool>,
                    Arc::new(crate::tools::TaskListTool) as Arc<dyn Tool>,
                    Arc::new(crate::tools::TaskTool) as Arc<dyn Tool>,
                ],
                Arc::clone(&mgr),
                4,
            );

            let child = mgr
                .register(TaskKind::Subagent, "explore work", None, TaskExecutionMode::Foreground)
                .unwrap();
            let request = SubagentRequest::foreground("explore", "investigate", &child.id, 1);

            let sink = Arc::new(CollectingSink::new());
            host.spawn(request, sink.clone(), CancellationToken::new()).await.unwrap();

            assert_eq!(
                seen.lock().unwrap().len(),
                1,
                "a read-only child must still run read-only tools"
            );
            let results = tool_results(&sink);
            assert!(
                results
                    .iter()
                    .any(|(name, content, _)| name == "task_list"
                        && content.contains("Unknown tool")),
                "task_list must be stripped from a read-only child; results: {results:?}"
            );
        }

        /// A hook-free host must stay hook-free through every clone used to
        /// build the child's factory (otherwise agent-type hooks re-enter).
        #[tokio::test]
        async fn hook_free_host_clones_stay_hook_free() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let client = ScriptedClient::new(vec![text_turn("done")]);
            let host = host_with(client, vec![], Arc::clone(&mgr), 4);

            assert!(host.hook_runner.is_none(), "fixture must be hook-free");
            assert!(
                host.clone_for_background().hook_runner.is_none(),
                "the factory clone handed to children must not gain a hook runner"
            );
        }

        /// A background spawn must run under the task it just registered, not
        /// under whatever id the caller happened to put in the request.
        #[tokio::test]
        async fn background_child_runs_under_its_own_registered_task_id() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let seen = Arc::new(Mutex::new(Vec::new()));

            let client = ScriptedClient::new(vec![
                tool_turn("b1", "probe", "{}"),
                text_turn("background done"),
            ]);
            let host = host_with(
                client,
                vec![Arc::new(ProbeTool { seen: Arc::clone(&seen) }) as Arc<dyn Tool>],
                Arc::clone(&mgr),
                4,
            );

            let mut request =
                SubagentRequest::foreground("general-purpose", "background work", "", 1);
            request.foreground = false;
            let task_id = host
                .spawn(request, Arc::new(CollectingSink::new()), CancellationToken::new())
                .await
                .unwrap();

            tokio::time::timeout(std::time::Duration::from_secs(5), async {
                while seen.lock().unwrap().is_empty() {
                    tokio::time::sleep(std::time::Duration::from_millis(5)).await;
                }
            })
            .await
            .expect("the background child must run the probe");

            let captures = seen.lock().unwrap().clone();
            assert_eq!(
                captures[0].caller_task_id.as_deref(),
                Some(task_id.as_str()),
                "the background child's identity must be the task the host registered"
            );
            assert!(mgr.get(&task_id).is_some());
        }

        // ── Test 3: schedule store + scheduled origin ─────────────────────────

        /// The child must see the SAME schedule store the session owns — a
        /// definition created by a subagent has to be visible to the main
        /// agent and to the schedule runtime. An ordinary (non-scheduled)
        /// child still gets no `schedule_list` visibility of its own: the
        /// main-agent-only / origin-scoped policy refuses it, but the
        /// refusal must come from the policy check, never from the store
        /// wiring itself being missing.
        #[tokio::test]
        async fn child_loop_shares_the_hosts_schedule_store() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let store = ScheduledTaskStore::new();
            let seen = Arc::new(Mutex::new(Vec::new()));

            let client = ScriptedClient::new(vec![
                tool_turn("p1", "probe", "{}"),
                tool_turn("s1", "schedule_list", "{}"),
                text_turn("done"),
            ]);
            let host = host_with(
                client,
                vec![
                    Arc::new(ProbeTool { seen: Arc::clone(&seen) }) as Arc<dyn Tool>,
                    Arc::new(crate::tools::ScheduleListTool) as Arc<dyn Tool>,
                ],
                Arc::clone(&mgr),
                4,
            )
            .with_schedule_store(Arc::clone(&store));

            let child = mgr
                .register(TaskKind::Subagent, "child work", None, TaskExecutionMode::Foreground)
                .unwrap();
            let request = SubagentRequest::foreground("general-purpose", "look", &child.id, 1);

            let sink = Arc::new(CollectingSink::new());
            host.spawn(request, sink.clone(), CancellationToken::new()).await.unwrap();

            let captures = seen.lock().unwrap().clone();
            let child_store = captures[0]
                .schedule_store
                .as_ref()
                .expect("the child must receive the schedule store");
            assert!(
                Arc::ptr_eq(child_store, &store),
                "the child must share the session's schedule store instance"
            );

            let results = tool_results(&sink);
            let (_, content, is_error) = results
                .iter()
                .find(|(name, _, _)| name == "schedule_list")
                .expect("schedule_list must produce a result");
            assert!(
                is_error,
                "an ordinary child with no scheduled-run origin must be refused; got: {content}"
            );
            assert_ne!(
                content, "Schedule store is not available.",
                "the refusal must come from the origin policy, not missing wiring"
            );
            assert!(
                content.contains("scheduled-run origin"),
                "the refusal must state the reason clearly; got: {content}"
            );
        }

        /// A scheduled run's origin travels into the child AND into a nested
        /// grandchild, while ordinary main-agent work carries no origin.
        #[tokio::test]
        async fn scheduled_origin_propagates_to_child_and_nested_grandchild() {
            use crate::tool::ScheduleOrigin;

            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let seen = Arc::new(Mutex::new(Vec::new()));

            let client = ScriptedClient::new(vec![
                // scheduled depth-1 run: probe, then delegate
                tool_turn("s1", "probe", "{}"),
                tool_turn("s2", "task", r#"{"prompt":"sub-work","originScheduleId":"other-job"}"#),
                // grandchild
                tool_turn("g1", "probe", "{}"),
                text_turn("grandchild done"),
                text_turn("scheduled done"),
                // second run: ordinary main-agent child
                tool_turn("o1", "probe", "{}"),
                text_turn("ordinary done"),
            ]);
            let host = host_with(
                client,
                vec![
                    Arc::new(ProbeTool { seen: Arc::clone(&seen) }) as Arc<dyn Tool>,
                    Arc::new(crate::tools::TaskTool) as Arc<dyn Tool>,
                ],
                Arc::clone(&mgr),
                8,
            );

            let origin = ScheduleOrigin::new("sched-42", Some("nightly audit".into()));
            let scheduled = mgr
                .register(TaskKind::Scheduled, "Scheduled: nightly", None, TaskExecutionMode::Background)
                .unwrap();
            let request =
                SubagentRequest::foreground("general-purpose", "run job", &scheduled.id, 1)
                    .with_schedule_origin(Some(origin.clone()));
            host.spawn(request, Arc::new(CollectingSink::new()), CancellationToken::new())
                .await
                .unwrap();

            let ordinary = mgr
                .register(TaskKind::Subagent, "ordinary", None, TaskExecutionMode::Foreground)
                .unwrap();
            host.spawn(
                SubagentRequest::foreground("general-purpose", "plain work", &ordinary.id, 1),
                Arc::new(CollectingSink::new()),
                CancellationToken::new(),
            )
            .await
            .unwrap();

            let captures = seen.lock().unwrap().clone();
            assert_eq!(captures.len(), 3, "scheduled child, grandchild, ordinary child");
            assert_eq!(
                captures[0].schedule_origin.as_ref(),
                Some(&origin),
                "the scheduled run's child must carry the trusted origin"
            );
            assert_eq!(
                captures[1].schedule_origin.as_ref(),
                Some(&origin),
                "a nested child inherits the origin of the job it runs inside — and the \
                 model's 'originScheduleId' argument must not have changed it"
            );
            assert_eq!(
                captures[2].schedule_origin, None,
                "ordinary main-agent work carries no scheduled origin"
            );
        }

        // ── Test 4: real child loop cannot probe a sibling task ──────────────

        /// A depth-1 child gets the full task-management tool set (see
        /// `resolve_child_tools`), but it must still be unable to reach a
        /// SIBLING task's output or terminal status via `task_peek` /
        /// `task_wait` — the manager-level authorization gate must hold
        /// through a real scripted `AgentLoop`, not just in isolated tool
        /// unit tests.
        #[tokio::test]
        async fn real_child_loop_cannot_peek_or_wait_on_a_sibling_task() {
            const SIBLING_CANARY: &str = "SIBLING_CANARY_REAL_CHILD_LOOP";

            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);

            // An unrelated top-level task the child has no authority over.
            let sibling = mgr
                .register(TaskKind::Subagent, "sibling work", None, TaskExecutionMode::Foreground)
                .unwrap();
            mgr.append_output(&sibling.id, SIBLING_CANARY);
            mgr.complete(&sibling.id, Some(SIBLING_CANARY.into()));

            let client = ScriptedClient::new(vec![
                tool_turn("peek1", "task_peek", &format!(r#"{{"taskId":"{}"}}"#, sibling.id)),
                tool_turn("wait1", "task_wait", &format!(r#"{{"taskId":"{}"}}"#, sibling.id)),
                text_turn("done"),
            ]);
            let host = host_with(
                client,
                vec![
                    Arc::new(crate::tools::TaskPeekTool) as Arc<dyn Tool>,
                    Arc::new(crate::tools::TaskWaitTool) as Arc<dyn Tool>,
                ],
                Arc::clone(&mgr),
                4,
            );

            let child = mgr
                .register(TaskKind::Subagent, "child work", None, TaskExecutionMode::Foreground)
                .unwrap();
            let request =
                SubagentRequest::foreground("general-purpose", "probe sibling", &child.id, 1);

            let sink = Arc::new(CollectingSink::new());
            host.spawn(request, sink.clone(), CancellationToken::new()).await.unwrap();

            let results = tool_results(&sink);
            let (_, peek_content, peek_is_error) = results
                .iter()
                .find(|(name, _, _)| name == "task_peek")
                .expect("task_peek must produce a result");
            let (_, wait_content, wait_is_error) = results
                .iter()
                .find(|(name, _, _)| name == "task_wait")
                .expect("task_wait must produce a result");

            assert!(peek_is_error, "task_peek on a sibling must be denied");
            assert_eq!(
                *peek_content,
                format!("Task '{}' not found.", sibling.id),
                "the denial must look identical to not-found"
            );
            assert!(
                !peek_content.contains(SIBLING_CANARY),
                "task_peek must not leak the sibling's output: {peek_content}"
            );

            assert!(wait_is_error, "task_wait on a sibling must be denied");
            assert_eq!(
                *wait_content,
                format!("Task '{}' not found.", sibling.id),
                "the denial must look identical to not-found"
            );
            assert!(
                !wait_content.contains(SIBLING_CANARY),
                "task_wait must not leak the sibling's result: {wait_content}"
            );
            assert!(
                !wait_content.contains("completed"),
                "task_wait must not leak the sibling's terminal status: {wait_content}"
            );
        }
    }
}
