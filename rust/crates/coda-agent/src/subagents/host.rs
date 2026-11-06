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
use std::time::SystemTime;

use async_trait::async_trait;
use tokio::sync::{OwnedSemaphorePermit, Semaphore};
use tokio_util::sync::CancellationToken;

use coda_llm::Message;

use crate::agent::output_budget::ModelBudget;
use crate::agent::park::ParkPolicy;
use crate::agent::{AgentError, AgentLoopBuilder};
use crate::events::{AgentSink, CollectingSink};
use crate::hooks::HookRunner;
use crate::permission::{PermissionModeState, PermissionPrompt};
use crate::scheduling::ScheduledTaskStore;
use crate::steering::SteeringInbox;
use crate::tasks::{ManagedTask, TaskExecutionMode, TaskKind, TaskManager};
use crate::tool::{ToolRegistry, ToolQuarantine};

use super::checkpoint::{SubagentCheckpoint, SubagentSpec, SUBAGENT_CHECKPOINT_VERSION};
use super::{
    SubagentFactory, SubagentRegistry, SubagentRequest, MAX_SUBAGENT_DEPTH,
    MAX_CONCURRENT_SUBAGENTS,
};

/// A run continued from a planned-restart checkpoint instead of started
/// fresh: its recorded spec, conversation and loop position.
struct RestoredRun {
    spec: SubagentSpec,
    history: Vec<Message>,
    steps_used: usize,
    parked: bool,
}

/// A restored task that is registered (with its inbox) but not running yet.
struct PreparedRestore {
    task: Arc<ManagedTask>,
    request: SubagentRequest,
    run: RestoredRun,
}

// ─────────────────────────────────────────────────────────────────────────────
// SubagentHost
// ─────────────────────────────────────────────────────────────────────────────

pub struct SubagentHost {
    client: Arc<dyn coda_llm::LlmClient>,
    permission_prompt: Arc<dyn PermissionPrompt>,
    permission_mode: Arc<PermissionModeState>,
    /// Shared with the run that spawned this host, so a subagent waits out an
    /// outage on the same terms as its parent. A goal run is unattended for
    /// hours; a subagent inside it that gave up after fifteen minutes would
    /// fail its branch and discard its context at exactly the moment waiting
    /// would have worked.
    outage_policy: Option<crate::agent::stream::SharedOutagePolicy>,
    /// The full tool registry; restricted per spawn via `resolve_child_tools`.
    tools: Arc<ToolRegistry>,
    quarantine: Arc<ToolQuarantine>,
    task_manager: Arc<TaskManager>,
    /// Shared, session-scoped schedule definitions.  Wired into every child so
    /// `schedule_*` tools inside a subagent see the same store the main agent
    /// and the schedule runtime use.  `None` keeps the host usable in tests
    /// and headless setups that never schedule anything.
    schedule_store: Option<Arc<ScheduledTaskStore>>,
    /// Shared, engine-owned user-notification bus (Stage 2 `notify_user`).
    /// Same sharing invariant as `schedule_store`: every child must receive
    /// the SAME `Arc` the main agent and the schedule runtime use, never a
    /// per-child bus and never a lazily-created one.
    message_bus: Option<Arc<crate::message::MessageBus>>,
    base_model: Arc<dyn Fn() -> String + Send + Sync>,
    base_max_tokens: u32,
    /// Resolves the output budget for the model a child actually runs on.
    /// `None` uses `base_max_tokens` with no context window.
    budget_source: Option<Arc<dyn Fn(&str) -> ModelBudget + Send + Sync>>,
    base_max_iterations: usize,
    working_directory: String,
    hook_runner: Option<Arc<HookRunner>>,
    semaphore: Arc<Semaphore>,
    /// Plugin-contributed agent definitions; built-ins always win.
    registry: Arc<SubagentRegistry>,
    /// Extra roots file tools may read (plugin directories), handed to every child.
    granted_directories: Option<std::collections::HashSet<String>>,
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
            outage_policy: None,
            tools,
            quarantine,
            task_manager,
            schedule_store: None,
            message_bus: None,
            base_model: Arc::new(move || base_model.clone()),
            base_max_tokens,
            budget_source: None,
            base_max_iterations,
            working_directory: working_directory.into(),
            hook_runner,
            semaphore: Arc::new(Semaphore::new(max_concurrent)),
            registry: Arc::new(SubagentRegistry::empty()),
            granted_directories: None,
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

    /// Resolve the output budget per child model, so a subagent on a model
    /// different from the parent's gets that model's limits.
    pub fn with_budget_source(
        self: Arc<Self>,
        source: Arc<dyn Fn(&str) -> ModelBudget + Send + Sync>,
    ) -> Arc<Self> {
        let mut host = self.clone_for_background();
        host.budget_source = Some(source);
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

    /// Share the session's engine-owned message bus with every child this
    /// host spawns, so `notify_user` inside a subagent (or a scheduled run,
    /// which spawns through this same host) publishes onto the SAME bus the
    /// main agent and `coda-serve` observe.
    pub fn with_message_bus(self: Arc<Self>, bus: Arc<crate::message::MessageBus>) -> Arc<Self> {
        let mut host = self.clone_for_background();
        host.message_bus = Some(bus);
        Arc::new(host)
    }

    /// Read-only accessor for the message bus this host was wired with.
    pub fn message_bus(&self) -> Option<&Arc<crate::message::MessageBus>> {
        self.message_bus.as_ref()
    }

    /// Resolve subagent types through `registry` (plugin agents) as well as the built-ins.
    pub fn with_registry(self: Arc<Self>, registry: Arc<SubagentRegistry>) -> Arc<Self> {
        let mut host = self.clone_for_background();
        host.registry = registry;
        Arc::new(host)
    }

    /// Extra directories children's file tools may access (plugin roots).
    pub fn with_granted_directories(self: Arc<Self>, dirs: std::collections::HashSet<String>) -> Arc<Self> {
        let mut host = self.clone_for_background();
        host.granted_directories = if dirs.is_empty() { None } else { Some(dirs) };
        Arc::new(host)
    }

    /// Share the run's outage-retry policy with every child this host spawns.
    pub fn with_outage_policy(
        self: Arc<Self>,
        policy: crate::agent::stream::SharedOutagePolicy,
    ) -> Arc<Self> {
        let mut host = self.clone_for_background();
        host.outage_policy = Some(policy);
        Arc::new(host)
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
            crate::agent::DEFAULT_MAX_TOKENS,
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

        self.run_inner(request, sink, cancel, None).await
    }

    /// Fires the SubagentStart hook (fail-closed: blocks the spawn on
    /// error/block) and returns the final system prompt and first prompt.
    async fn start_prompts(
        &self,
        request: &SubagentRequest,
        child_tools: &ToolRegistry,
        sink: &Arc<dyn AgentSink>,
        cancel: &CancellationToken,
    ) -> Result<(String, String), String> {
        let definition = self.registry.try_resolve(Some(&request.agent_type))?;

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
        Ok((final_system, effective_prompt))
    }

    /// Core run logic (no depth check, no semaphore management).
    ///
    /// Called by both the foreground path (which holds a `SemaphorePermit`)
    /// and the background spawn (which holds an `OwnedSemaphorePermit` for the
    /// full lifetime of the background task). `restored` continues a run from
    /// a planned-restart checkpoint instead of starting a fresh one.
    async fn run_inner(
        &self,
        request: SubagentRequest,
        sink: Arc<dyn AgentSink>,
        cancel: CancellationToken,
        restored: Option<RestoredRun>,
    ) -> Result<String, String> {
        let definition = self.registry.try_resolve(Some(&request.agent_type))?;

        // Determine the model to use.
        let model = request.model.clone().unwrap_or_else(|| (self.base_model)());
        let mut budget = match &self.budget_source {
            Some(resolve) => resolve(&model),
            None => ModelBudget { max_tokens: self.base_max_tokens, context_window: None },
        };
        let max_iterations = match &restored {
            Some(run) => {
                budget.max_tokens = run.spec.max_tokens;
                run.spec.max_steps
            }
            None => self.base_max_iterations,
        };

        // Resolve the tool set for this depth/definition.
        let child_tools = resolve_child_tools(&self.tools, definition.read_only_tools_only, request.depth);

        // A restored run already has its final system prompt and its
        // conversation: the SubagentStart hook ran when it was first spawned
        // and is not re-run, since it could block or rewrite work that is
        // already under way.
        let (final_system, mut history, steps_used, start_parked) = match restored {
            Some(run) => (run.spec.system_prompt, run.history, run.steps_used, run.parked),
            None => {
                let (system, prompt) =
                    self.start_prompts(&request, &child_tools, &sink, &cancel).await?;
                (system, vec![Message::user(prompt)], 0, false)
            }
        };

        // Record how to re-create this run (planned restart). Only a run
        // registered as a task can ever be suspended and checkpointed.
        let managed = self.task_manager.find_task(&request.task_id);
        if let Some(task) = &managed {
            let spec = SubagentSpec {
                agent_type: request.agent_type.clone(),
                prompt: request.prompt.clone(),
                system_prompt: final_system.clone(),
                model: model.clone(),
                provider: Some(self.client.provider_id().to_owned()),
                effort: None,
                depth: request.depth,
                max_steps: max_iterations,
                max_tokens: budget.max_tokens,
                foreground: request.foreground,
                schedule_origin: request.schedule_origin.as_ref().map(SubagentSpec::origin_to_json),
            };
            if let Ok(value) = serde_json::to_value(&spec) {
                task.set_spec(value);
            }
        }

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
        .with_model_budget(budget)
        .with_max_iterations(max_iterations)
        .with_steps_offset(steps_used)
        .with_start_parked(start_parked)
        .with_working_directory(self.working_directory.clone())
        .with_quarantine(self.quarantine.clone())
        .with_task_manager(Arc::clone(&self.task_manager))
        .with_caller_task_id(request.task_id.clone())
        .with_subagent_factory(child_factory);

        if let Some(dirs) = &self.granted_directories {
            builder = builder.with_granted_directories(dirs.clone());
        }

        if let Some(store) = &self.schedule_store {
            builder = builder.with_schedule_store(Arc::clone(store));
        }
        if let Some(bus) = &self.message_bus {
            builder = builder.with_message_bus(Arc::clone(bus));
        }
        // Inherit the parent's outage policy: a subagent inside an unattended
        // goal run must wait out a provider outage for as long as the parent
        // would, not give up after the interactive default.
        if let Some(policy) = &self.outage_policy {
            builder = builder.with_outage_policy(Arc::clone(policy));
        }
        // A nested child inherits the scheduled provenance of the run that
        // spawned it; ordinary main-agent children carry `None`.
        if let Some(origin) = &request.schedule_origin {
            builder = builder.with_schedule_origin(origin.clone());
        }

        // Non-blocking runtime, Phase 2: a subagent running as a registered
        // task gets its own inbox (parent messages via `task_send`, pushed
        // completions of the tasks it starts) and parks — instead of ending —
        // while any of those tasks are still running. A run without a
        // registered task (e.g. an agent-type hook) keeps the old rule. A
        // restored task already has its inbox (with its undelivered items).
        if let Some(task) = &managed {
            task.attach_steering(Arc::new(SteeringInbox::new()));
            if let Some(inbox) = task.steering() {
                builder = builder
                    .with_steering(Arc::clone(inbox))
                    .with_park_policy(ParkPolicy::subagent(request.task_id.clone()));
            }
        }

        let loop_ = builder.build();

        let collecting_sink = Arc::new(CollectingSink::new());

        // Forward to parent sink while collecting.
        let forwarding = ForwardingSink { parent: sink.clone(), collecting: collecting_sink.clone() };

        let run_result = loop_.run(&mut history, &forwarding, None, cancel.clone()).await;

        // Nothing a subagent started may outlive it: commands and children
        // still running when it ends (deadline, cancel, failure) are stopped.
        if managed.is_some() {
            self.task_manager.stop_descendants(&request.task_id);
        }

        // The report is the subagent's final message (it may have run many
        // turns while parked); earlier narration is not part of it.
        let text = match final_assistant_text(&history) {
            Some(text) => text,
            None => collecting_sink.collected_text(),
        };
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

        // The same rule for a provider failure, which used to fall through to
        // the success path below: a subagent whose model call failed returned
        // its empty transcript as though the work were done, and the parent had
        // no way to tell "the provider was down" from "there was nothing to
        // say". Under a goal that is the worst possible confusion — the parent
        // would mark the branch complete and move on, and the operator would
        // come back to work that never happened.
        //
        // Named explicitly so the parent can tell an outage apart from a task
        // that genuinely failed, and retry rather than give up.
        if let Err(AgentError::Llm(err)) = &run_result {
            return Err(format!(
                "Subagent could not reach the model provider ({err}). \
                 This is a provider failure, not a failed task: nothing was \
                 completed and no output was produced. Retrying later may succeed."
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
            // The background run's identity is the task just registered — not
            // the id the caller happened to put in the request. `run_inner`
            // uses `task_id` as the child's `caller_task_id`, so a stale value
            // here would give the child another task's authority.
            let mut req2 = request.clone();
            req2.task_id = task_id.clone();
            // The run is stopped through its own task (`task_stop`, `/stop`,
            // shutdown) or by the caller's token, whichever fires first.
            {
                let run_cancel = task.cancel.clone();
                tokio::spawn(async move {
                    tokio::select! {
                        _ = cancel.cancelled() => run_cancel.cancel(),
                        _ = run_cancel.cancelled() => {}
                    }
                });
            }
            self.launch_background(&task, req2, sink, Some(permit), None);

            // Return just the task id so the calling tool can format its own message.
            Ok(task_id)
        }
    }
}

impl SubagentHost {
    /// Runs `request` as the background task `task` and settles the task
    /// with the outcome. `permit` is the concurrency slot already taken, or
    /// `None` to wait for one inside the run (restored work is never refused
    /// for lack of a slot).
    fn launch_background(
        &self,
        task: &Arc<ManagedTask>,
        request: SubagentRequest,
        sink: Arc<dyn AgentSink>,
        permit: Option<OwnedSemaphorePermit>,
        restored: Option<RestoredRun>,
    ) {
        let host = Arc::new(self.clone_for_background());
        let semaphore = Arc::clone(&self.semaphore);
        let mgr = self.task_manager.clone();
        let task_id = task.id.clone();
        let run_cancel = task.cancel.clone();

        tokio::spawn(async move {
            // Drop the permit only when this future completes (success or
            // failure), so the slot stays occupied for the full run.
            let _permit = match permit {
                Some(permit) => Some(permit),
                None => tokio::select! {
                    permit = semaphore.acquire_owned() => permit.ok(),
                    _ = run_cancel.cancelled() => {
                        mgr.stop(&task_id);
                        // run_inner never ran, so its descendant cleanup must happen here.
                        mgr.stop_descendants(&task_id);
                        return;
                    }
                },
            };
            match host.run_inner(request, sink, run_cancel.clone(), restored).await {
                Ok(report) => { mgr.complete(&task_id, Some(report)); }
                Err(_) if run_cancel.is_cancelled() => { mgr.stop(&task_id); }
                Err(e) => { mgr.fail(&task_id, Some(e)); }
            }
            // Ends the caller-token watcher; the task is already terminal.
            run_cancel.cancel();
        });
    }

    /// Re-creates a subagent from a planned-restart checkpoint under its old
    /// task id and continues its run in the background, where it left off.
    /// Its owner (if any) must already be restored, and a parked owner's children
    /// must be registered before it runs: restore several related tasks with
    /// [`Self::restore_all`]. With `keep_suspended`
    /// (or when the checkpoint was explicitly suspended) it stays suspended
    /// until resumed. Returns the task id.
    pub async fn restore(
        self: &Arc<Self>,
        cp: SubagentCheckpoint,
        keep_suspended: bool,
        sink: Arc<dyn AgentSink>,
    ) -> Result<String, String> {
        let prepared = self.prepare_restore(cp, keep_suspended)?;
        let task_id = prepared.task.id.clone();
        self.start_restored(prepared, sink);
        Ok(task_id)
    }

    /// Restores several checkpoints: owners before their children, and every
    /// task registered before any of them runs (so a parked owner never sees
    /// its children missing). Returns `(taskId, outcome)` per checkpoint in
    /// restore order.
    pub async fn restore_all(
        self: &Arc<Self>,
        mut cps: Vec<SubagentCheckpoint>,
        keep_suspended: bool,
        sink: Arc<dyn AgentSink>,
    ) -> Vec<(String, Result<String, String>)> {
        cps.sort_by_key(|cp| cp.spec.depth);
        let mut results = Vec::with_capacity(cps.len());
        let mut prepared = Vec::with_capacity(cps.len());
        for cp in cps {
            let task_id = cp.task_id.clone();
            match self.prepare_restore(cp, keep_suspended) {
                Ok(p) => {
                    results.push((task_id.clone(), Ok(task_id)));
                    prepared.push(p);
                }
                Err(e) => results.push((task_id, Err(e))),
            }
        }
        for p in prepared {
            self.start_restored(p, Arc::clone(&sink));
        }
        results
    }

    /// Registers the restored task under its old id and refills its inbox.
    fn prepare_restore(
        &self,
        cp: SubagentCheckpoint,
        keep_suspended: bool,
    ) -> Result<PreparedRestore, String> {
        if cp.version > SUBAGENT_CHECKPOINT_VERSION {
            return Err(format!("Unsupported subagent checkpoint version {}.", cp.version));
        }
        if cp.kind != TaskKind::Subagent.as_str() {
            return Err(format!("Task '{}' is not a subagent.", cp.task_id));
        }
        let history = cp.messages();
        if history.is_empty() {
            return Err(format!("Task '{}' has no conversation to restore.", cp.task_id));
        }
        // Always background: no foreground caller survives a restart, and its
        // report must be pushed to its owner like any background subagent's.
        let task = self.task_manager.register_restored(
            &cp.task_id,
            TaskKind::Subagent,
            cp.description.clone(),
            cp.owner_task_id.as_deref(),
            TaskExecutionMode::Background,
            SystemTime::from(cp.started_at),
            keep_suspended || cp.explicitly_suspended,
        )?;
        let inbox = Arc::new(SteeringInbox::new());
        for item in &cp.steering {
            inbox.push_item(item.inbox_kind(), item.text.clone());
        }
        task.attach_steering(inbox);
        let request = SubagentRequest {
            agent_type: cp.spec.agent_type.clone(),
            prompt: cp.spec.prompt.clone(),
            task_id: task.id.clone(),
            depth: task.depth,
            model: Some(cp.spec.model.clone()),
            foreground: cp.spec.foreground,
            caller_task_id: cp.owner_task_id.clone(),
            schedule_origin: cp.spec.schedule_origin(),
        };
        let run = RestoredRun {
            spec: cp.spec,
            history,
            steps_used: cp.steps_used,
            parked: cp.parked,
        };
        Ok(PreparedRestore { task, request, run })
    }

    fn start_restored(&self, prepared: PreparedRestore, sink: Arc<dyn AgentSink>) {
        let PreparedRestore { task, request, run } = prepared;
        self.launch_background(&task, request, sink, None, Some(run));
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
            message_bus: self.message_bus.clone(),
            base_model: self.base_model.clone(),
            base_max_tokens: self.base_max_tokens,
            budget_source: self.budget_source.clone(),
            base_max_iterations: self.base_max_iterations,
            working_directory: self.working_directory.clone(),
            hook_runner: self.hook_runner.clone(),
            outage_policy: self.outage_policy.clone(),
            semaphore: self.semaphore.clone(),
            registry: self.registry.clone(),
            granted_directories: self.granted_directories.clone(),
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
    tools.filtered(move |t| {
        (!read_only_definition || t.is_read_only())
            && !(deny_task && is_task_management_tool(t.name()))
    })
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

/// The text of the last assistant message that has any, trimmed.
fn final_assistant_text(history: &[Message]) -> Option<String> {
    history
        .iter()
        .rev()
        .filter(|m| m.role == coda_llm::Role::Assistant)
        .map(|m| {
            m.content
                .iter()
                .filter_map(|c| match c {
                    coda_llm::Content::Text(t) => Some(t.as_str()),
                    _ => None,
                })
                .collect::<String>()
                .trim()
                .to_owned()
        })
        .find(|t| !t.is_empty())
}

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
        let all = child.all();
        let names: Vec<&str> = all.iter().map(|t| t.name()).collect();
        assert!(!names.contains(&"task"), "task must be stripped at max depth");
        assert!(!names.contains(&"task_wait"), "task_wait must be stripped at max depth");
        assert!(names.contains(&"bash"), "bash must remain");

        // Below max depth, task tools are kept.
        let child_shallow = resolve_child_tools(&registry, false, 1);
        let all_shallow = child_shallow.all();
        let names_shallow: Vec<&str> = all_shallow.iter().map(|t| t.name()).collect();
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
        let all = child.all();
        let names: Vec<&str> = all.iter().map(|t| t.name()).collect();
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

    /// A plugin agent named by its short name runs with its own body as the
    /// system prompt; an ambiguous short name is refused before any model call.
    #[tokio::test]
    async fn plugin_agents_resolve_through_the_registry() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::{LlmError, Usage};
        use async_trait::async_trait as at;
        use crate::permission::PermissionPrompt;

        struct Capture(std::sync::Mutex<Vec<String>>);
        #[at]
        impl coda_llm::LlmClient for Capture {
            fn provider_id(&self) -> &str { "mock" }
            async fn stream(&self, r: coda_llm::ChatRequest) -> Result<coda_llm::ResponseStream, LlmError> {
                self.0.lock().unwrap().push(r.system.clone().unwrap_or_default());
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

        let def = |t: &str, body: &str| super::super::SubagentDefinition {
            agent_type: t.to_owned(),
            description: String::new(),
            system_prompt_body: body.to_owned(),
            read_only_tools_only: false,
            default_model: None,
        };
        let client = Arc::new(Capture(std::sync::Mutex::new(Vec::new())));
        let mgr = crate::tasks::TaskManager::new(
            "plugin-agent-session",
            Some(std::env::temp_dir().join("coda-host-plugin-agent-tests")),
            4096,
            10,
        );
        let host = SubagentHost::new(
            Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
            Arc::new(AllowAll),
            Arc::new(PermissionModeState::new(crate::permission::PermissionMode::Default)),
            Arc::new(crate::tool::ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0])),
            Arc::new(crate::tool::ToolQuarantine::new()),
            mgr,
            "model",
            4096,
            5,
            ".",
            None,
            2,
        )
        .with_registry(Arc::new(SubagentRegistry::new(vec![
            def("deep-review:skeptic", "You are the skeptic."),
            def("a:advocate", "A"),
            def("b:advocate", "B"),
        ])));

        let request = SubagentRequest::foreground("skeptic", "go", "t1", 1);
        host.run_foreground(request, Arc::new(crate::events::NullSink), CancellationToken::new())
            .await
            .expect("plugin agent run completes");
        let seen = client.0.lock().unwrap().clone();
        assert!(seen[0].starts_with("You are the skeptic."), "{seen:?}");

        let ambiguous = SubagentRequest::foreground("advocate", "go", "t2", 1);
        let err = host
            .run_foreground(ambiguous, Arc::new(crate::events::NullSink), CancellationToken::new())
            .await
            .unwrap_err();
        assert!(err.contains("a:advocate") && err.contains("b:advocate"), "{err}");
        assert_eq!(client.0.lock().unwrap().len(), 1, "no model call for an ambiguous type");
    }

    /// A subagent requests the budget resolved for the model it runs on, not
    /// the host's base value, and the source sees the child's actual model.
    #[tokio::test]
    async fn subagent_sends_resolved_max_tokens() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::{LlmError, Usage};
        use async_trait::async_trait as at;
        use crate::permission::PermissionPrompt;

        struct Capture(std::sync::Mutex<Vec<(String, u32)>>);
        #[at]
        impl coda_llm::LlmClient for Capture {
            fn provider_id(&self) -> &str { "mock" }
            async fn stream(&self, r: coda_llm::ChatRequest) -> Result<coda_llm::ResponseStream, LlmError> {
                self.0.lock().unwrap().push((r.model.clone(), r.max_tokens));
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

        let client = Arc::new(Capture(std::sync::Mutex::new(Vec::new())));
        let mgr = crate::tasks::TaskManager::new(
            "budget-session",
            Some(std::env::temp_dir().join("coda-host-budget-tests")),
            4096,
            10,
        );
        let host = SubagentHost::new(
            Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
            Arc::new(AllowAll),
            Arc::new(PermissionModeState::new(crate::permission::PermissionMode::Default)),
            Arc::new(crate::tool::ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0])),
            Arc::new(crate::tool::ToolQuarantine::new()),
            mgr,
            "parent-model",
            4096,
            5,
            ".",
            None,
            2,
        )
        .with_budget_source(Arc::new(|model: &str| ModelBudget {
            max_tokens: if model == "child-model" { 48_000 } else { 12_000 },
            context_window: Some(200_000),
        }));

        let inherit = SubagentRequest::foreground("general-purpose", "go", "t1", 1);
        host.run_foreground(inherit, Arc::new(crate::events::NullSink), CancellationToken::new())
            .await
            .expect("inherited-model run completes");
        let mut overridden = SubagentRequest::foreground("general-purpose", "go", "t2", 1);
        overridden.model = Some("child-model".into());
        host.run_foreground(overridden, Arc::new(crate::events::NullSink), CancellationToken::new())
            .await
            .expect("overridden-model run completes");

        let seen = client.0.lock().unwrap().clone();
        assert_eq!(
            seen,
            vec![("parent-model".to_owned(), 12_000), ("child-model".to_owned(), 48_000)],
            "each child must request its own model's budget, never the 4096 base"
        );
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

        pub(super) fn done_event() -> StreamEvent {
            StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO }
        }

        pub(super) fn text_turn(text: &str) -> Vec<StreamEvent> {
            vec![StreamEvent::TextDelta(text.into()), done_event()]
        }

        pub(super) fn tool_turn(id: &str, name: &str, input_json: &str) -> Vec<StreamEvent> {
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

        /// A scheduled run's child (spawned through this same host, exactly
        /// as `ScheduleRuntime`'s runner does — see `scheduling/runtime.rs`)
        /// must reach the SAME message bus the host was wired with by an
        /// actual `notify_user` call from inside a real spawned `AgentLoop`
        /// — not merely a `host.message_bus()` getter equality check.
        #[tokio::test]
        async fn scheduled_child_notify_user_publishes_onto_the_shared_message_bus() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let bus = Arc::new(crate::message::MessageBus::new());

            let client = ScriptedClient::new(vec![
                tool_turn("n1", "notify_user", r#"{"text":"nightly audit complete"}"#),
                text_turn("done"),
            ]);
            let host = host_with(
                client,
                vec![Arc::new(crate::tools::NotifyUserTool) as Arc<dyn Tool>],
                Arc::clone(&mgr),
                4,
            )
            .with_message_bus(Arc::clone(&bus));

            // The schedule runtime registers its own task id for this firing
            // (exactly like `ScheduleRuntime`'s runner does) before handing
            // the request to the factory — this is that same registered id,
            // not the parent's and not a fabricated one.
            let child = mgr
                .register(TaskKind::Scheduled, "nightly audit", None, TaskExecutionMode::Background)
                .unwrap();
            let mut request = SubagentRequest::foreground(
                "general-purpose",
                "run the nightly audit",
                &child.id,
                1,
            );
            request.caller_task_id = None;
            request.schedule_origin =
                Some(crate::tool::ScheduleOrigin::new("def-nightly", Some("nightly audit".into())));

            let sink = Arc::new(CollectingSink::new());
            host.spawn(request, sink, CancellationToken::new()).await.unwrap();

            let since = bus.user_since(0, None);
            assert_eq!(
                since.messages.len(),
                1,
                "notify_user called inside the spawned child must reach the SAME bus"
            );
            let msg = &since.messages[0];
            assert_eq!(msg.source_kind, "scheduledTask");
            assert_eq!(msg.task_id.as_deref(), Some(child.id.as_str()));
            assert_eq!(msg.schedule_definition_id.as_deref(), Some("def-nightly"));
            assert_eq!(msg.body, "nightly audit complete");
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
        /// End to end at depth 2 (Phase 2): the child gets the handle at once,
        /// parks, is woken by the grandchild's pushed report, and finishes.
        #[tokio::test(start_paused = true)]
        async fn nested_task_tool_builds_the_authorization_tree_and_stops_at_depth_two() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let seen = Arc::new(Mutex::new(Vec::new()));

            let client = super::nonblocking::RoutedClient::new(vec![
                (
                    "delegate",
                    vec![
                        tool_turn("c1", "task", r#"{"prompt":"grandchild work"}"#),
                        text_turn("waiting for the grandchild"),
                        text_turn("child done"),
                    ],
                ),
                (
                    "grandchild work",
                    vec![
                        tool_turn("g1", "probe", "{}"),
                        tool_turn("g2", "task", r#"{"prompt":"great-grandchild"}"#),
                        text_turn("grandchild done"),
                    ],
                ),
            ]);
            let host = host_with(
                Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
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
            let report =
                host.spawn(request, sink.clone(), CancellationToken::new()).await.unwrap();
            assert_eq!(report, "child done", "the report is the child's final message");
            let child_requests = client.requests_for("delegate");
            assert_eq!(child_requests.len(), 3, "spawn, park, one wake-up turn");
            assert!(
                child_requests[2].contains("[task-completed]")
                    && child_requests[2].contains("grandchild done"),
                "the grandchild's report reaches the parked child: {}",
                child_requests[2]
            );
            assert!(
                child_requests[1].contains("Started subagent task"),
                "the task tool returns a handle immediately: {}",
                child_requests[1]
            );

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

        // ── Test 3b: real child loop self-cancel ─────────────────────────────

        /// Builds a host whose child loops can reach the schedule store, plus a
        /// scheduled root task and two definitions: the run's own, and a
        /// foreign one that must never be affected.
        fn self_cancel_fixture(
            client: Arc<dyn coda_llm::LlmClient>,
            mgr: Arc<TaskManager>,
        ) -> (Arc<SubagentHost>, Arc<ScheduledTaskStore>, String, String) {
            let store = ScheduledTaskStore::new();
            let mut ids = Vec::new();
            for prompt in ["OWN_JOB_PROMPT", "FOREIGN_JOB_PROMPT"] {
                ids.push(
                    store
                        .add(
                            crate::scheduling::ScheduleDefinitionDraft {
                                name: Some("watcher".into()),
                                kind: crate::scheduling::ScheduleKind::Interval,
                                prompt: prompt.into(),
                                interval: Some(std::time::Duration::from_secs(3600)),
                                at_utc: None,
                                cron: None,
                                time_zone_id: "UTC".into(),
                                next_run_utc: chrono::Utc::now() + chrono::Duration::hours(1),
                                expires_at_utc: None,
                                max_runs: None,
                            },
                            chrono::Utc::now(),
                        )
                        .id,
                );
            }
            let host = host_with(
                client,
                vec![
                    Arc::new(crate::tools::ScheduleCancelSelfTool) as Arc<dyn Tool>,
                    Arc::new(crate::tools::TaskTool) as Arc<dyn Tool>,
                ],
                mgr,
                8,
            )
            .with_schedule_store(Arc::clone(&store));
            let own = ids[0].clone();
            let foreign = ids[1].clone();
            (host, store, own, foreign)
        }

        fn retired(store: &Arc<ScheduledTaskStore>, id: &str) -> bool {
            store.items().iter().any(|t| t.id == id && t.is_retired())
        }

        /// The real thing: a scheduled run's own `AgentLoop` calls
        /// `schedule_cancel_self`, its definition stops scheduling future work,
        /// and — by default — the model is left to finish the turn it is in.
        #[tokio::test]
        async fn a_real_scheduled_run_cancels_only_its_own_future_runs() {
            use crate::tool::ScheduleOrigin;

            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let client = ScriptedClient::new(vec![
                tool_turn("c1", "schedule_cancel_self", "{}"),
                text_turn("watcher finished its job"),
            ]);
            let (host, store, own, foreign) =
                self_cancel_fixture(client, Arc::clone(&mgr));

            let run = mgr
                .register(
                    TaskKind::Scheduled,
                    "Scheduled: watcher",
                    None,
                    TaskExecutionMode::Background,
                )
                .unwrap();

            let sink = Arc::new(CollectingSink::new());
            let report = host
                .spawn(
                    SubagentRequest::foreground("general-purpose", "watch", &run.id, 1)
                        .with_schedule_origin(Some(ScheduleOrigin::new(own.clone(), None))),
                    sink.clone(),
                    CancellationToken::new(),
                )
                .await
                .unwrap();

            let results = tool_results(&sink);
            let (_, content, is_error) = results
                .iter()
                .find(|(name, _, _)| name == "schedule_cancel_self")
                .expect("the tool must have run inside the real child loop");
            assert!(!is_error, "{content}");

            assert!(retired(&store, &own), "the run's own definition must be retired");
            assert!(
                !retired(&store, &foreign),
                "no other definition may be touched"
            );
            assert!(
                !run.cancel.is_cancelled(),
                "the default must let the model finish its turn"
            );
            assert!(
                report.contains("watcher finished its job"),
                "the run must have produced its final answer: {report}"
            );
        }

        /// `stopRunning: true` cancels the run's own `ManagedTask` token — the
        /// same token the schedule runner hands to the loop — so the run really
        /// ends instead of merely being reported as ended.
        #[tokio::test]
        async fn stop_running_cancels_the_runs_own_managed_task_token() {
            use crate::tool::ScheduleOrigin;

            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let client = ScriptedClient::new(vec![
                tool_turn("c1", "schedule_cancel_self", r#"{"stopRunning":true}"#),
                text_turn("unreachable"),
            ]);
            let (host, store, own, _foreign) = self_cancel_fixture(client, Arc::clone(&mgr));

            let run = mgr
                .register(
                    TaskKind::Scheduled,
                    "Scheduled: watcher",
                    None,
                    TaskExecutionMode::Background,
                )
                .unwrap();

            // The runner hands the task's own token to the loop.
            let _ = host
                .spawn(
                    SubagentRequest::foreground("general-purpose", "watch", &run.id, 1)
                        .with_schedule_origin(Some(ScheduleOrigin::new(own.clone(), None))),
                    Arc::new(CollectingSink::new()),
                    run.cancel.clone(),
                )
                .await;

            assert!(
                run.cancel.is_cancelled(),
                "stopRunning must cancel the token the runner observes"
            );
            assert!(retired(&store, &own));
        }

        /// A nested subagent inside a scheduled run inherits the origin but is
        /// not the run: it must be refused, must not retire the definition, and
        /// must certainly not stop its parent's run.
        #[tokio::test]
        async fn a_nested_subagent_inside_a_real_scheduled_run_cannot_self_cancel() {
            use crate::tool::ScheduleOrigin;

            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let client = ScriptedClient::new(vec![
                tool_turn("n1", "schedule_cancel_self", r#"{"stopRunning":true}"#),
                text_turn("nested done"),
            ]);
            let (host, store, own, _foreign) = self_cancel_fixture(client, Arc::clone(&mgr));

            // The scheduled root run, and a nested child beneath it — exactly
            // the shape `SubagentHost` produces when a scheduled run delegates.
            let run = mgr
                .register(
                    TaskKind::Scheduled,
                    "Scheduled: watcher",
                    None,
                    TaskExecutionMode::Background,
                )
                .unwrap();
            let nested = mgr
                .register(
                    TaskKind::Subagent,
                    "nested worker",
                    Some(&run.id),
                    TaskExecutionMode::Foreground,
                )
                .unwrap();

            let sink = Arc::new(CollectingSink::new());
            host.spawn(
                // Depth 2, the child's own task id, and the origin it inherited
                // from the run it is executing inside.
                SubagentRequest::foreground("general-purpose", "decide", &nested.id, 2)
                    .with_schedule_origin(Some(ScheduleOrigin::new(own.clone(), None))),
                sink.clone(),
                CancellationToken::new(),
            )
            .await
            .unwrap();

            let results = tool_results(&sink);
            let (_, content, is_error) = results
                .iter()
                .find(|(name, _, _)| name == "schedule_cancel_self")
                .expect("the nested attempt must be visible");
            assert!(is_error, "a nested subagent must be refused: {content}");
            assert!(!retired(&store, &own), "the definition must survive the attempt");
            assert!(
                !run.cancel.is_cancelled(),
                "a nested child must not be able to stop its parent's run"
            );
            assert!(
                !nested.cancel.is_cancelled(),
                "and the refusal must not stop anything at all"
            );
        }

        /// Depth and read-only policy still apply: the tool mutates, so a
        /// read-only definition never receives it, and a grandchild's inherited
        /// origin buys it nothing because the authorization check is about
        /// being the run, not about carrying an origin.
        #[test]
        fn self_cancel_is_stripped_from_read_only_children_like_any_mutating_tool() {
            let registry = ToolRegistry::new(vec![
                Arc::new(crate::tools::ScheduleCancelSelfTool) as Arc<dyn Tool>,
                Arc::new(crate::tools::ScheduleListTool) as Arc<dyn Tool>,
            ]);

            let read_only = resolve_child_tools(&registry, true, 1);
            let names: Vec<_> =
                read_only.all().iter().map(|t| t.name().to_owned()).collect();
            assert!(
                !names.contains(&"schedule_cancel_self".to_owned()),
                "a read-only definition must not receive a mutating tool"
            );
            assert!(names.contains(&"schedule_list".to_owned()));

            // It is not a task-management tool, so depth alone does not strip
            // it: a depth-limited *scheduled root* still needs it.
            let deep = resolve_child_tools(&registry, false, MAX_SUBAGENT_DEPTH);
            let deep_names: Vec<_> = deep.all().iter().map(|t| t.name().to_owned()).collect();
            assert!(deep_names.contains(&"schedule_cancel_self".to_owned()));
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

    // ── Non-blocking runtime, Phase 2: parked agents and pushed completions ──

    pub(super) mod nonblocking {
        use std::collections::VecDeque;
        use std::sync::{Arc, Mutex};
        use std::time::Duration;

        use async_trait::async_trait as at;
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::{Content, Correlation, LlmError, Message};
        use tokio_util::sync::CancellationToken;

        use super::trusted_child_context::{done_event, host_with, manager, text_turn, tool_turn, AllowAll};
        use crate::agent::park::ParkPolicy;
        use crate::agent::AgentLoopBuilder;
        use crate::events::CollectingSink;
        use crate::message::MessageBus;
        use crate::steering::SteeringInbox;
        use crate::subagents::{SubagentFactory, SubagentRequest};
        use crate::tasks::{
            MainInboxCompletionPush, TaskExecutionMode, TaskKind, TaskManager, TaskRunStatus,
        };
        use crate::tool::{Tool, ToolContext, ToolContextServiceExt as _, ToolOutcome, ToolRegistry, ToolResult};

        /// Scripted model that serves each agent its own script, chosen by the
        /// agent's original prompt (its first message), and records every
        /// request — so concurrently running agents cannot steal each other's
        /// turns.
        pub(crate) struct RoutedClient {
            scripts: Mutex<Vec<(String, VecDeque<Vec<StreamEvent>>)>>,
            requests: Mutex<Vec<(String, String)>>,
        }

        impl RoutedClient {
            pub(crate) fn new(routes: Vec<(&str, Vec<Vec<StreamEvent>>)>) -> Arc<Self> {
                Arc::new(Self {
                    scripts: Mutex::new(
                        routes.into_iter().map(|(k, v)| (k.to_owned(), v.into())).collect(),
                    ),
                    requests: Mutex::new(Vec::new()),
                })
            }

            /// The full text of every request one agent made, in order.
            pub(crate) fn requests_for(&self, key: &str) -> Vec<String> {
                self.requests
                    .lock()
                    .unwrap()
                    .iter()
                    .filter(|(k, _)| k == key)
                    .map(|(_, text)| text.clone())
                    .collect()
            }
        }

        fn message_text(message: &Message) -> String {
            message
                .content
                .iter()
                .filter_map(|c| match c {
                    Content::Text(t) => Some(t.clone()),
                    Content::ToolResult { content, .. } => Some(content.clone()),
                    _ => None,
                })
                .collect::<Vec<_>>()
                .join("\n")
        }

        #[at]
        impl coda_llm::LlmClient for RoutedClient {
            fn provider_id(&self) -> &str {
                "routed"
            }
            async fn stream(
                &self,
                request: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, LlmError> {
                let first = request.messages.first().map(message_text).unwrap_or_default();
                let full =
                    request.messages.iter().map(message_text).collect::<Vec<_>>().join("\n");
                let (key, events) = {
                    let mut scripts = self.scripts.lock().unwrap();
                    let route = scripts
                        .iter_mut()
                        .find(|(k, _)| first.contains(k.as_str()))
                        .unwrap_or_else(|| panic!("no script for prompt {first:?}"));
                    let events =
                        route.1.pop_front().unwrap_or_else(|| text_turn("(script exhausted)"));
                    (route.0.clone(), events)
                };
                self.requests.lock().unwrap().push((key, full));
                let (tx, rx) = tokio::sync::mpsc::channel(16);
                tokio::spawn(async move {
                    for e in events {
                        let _ = tx.send(Ok(e)).await;
                    }
                });
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }

        /// One model turn calling several tools at once.
        fn tools_turn(calls: &[(&str, &str, String)]) -> Vec<StreamEvent> {
            let mut events: Vec<StreamEvent> = calls
                .iter()
                .map(|(id, name, input)| {
                    StreamEvent::ToolUse(Content::ToolUse {
                        id: (*id).into(),
                        name: (*name).into(),
                        input_json: input.clone(),
                        correlation: Correlation::default(),
                    })
                })
                .collect();
            events.push(done_event());
            events
        }

        /// Sleeps `ms` milliseconds (paused clock in these tests).
        struct SlowTool;

        #[at]
        impl Tool for SlowTool {
            fn name(&self) -> &str {
                "slow"
            }
            fn description(&self) -> &str {
                "sleeps"
            }
            fn input_schema_json(&self) -> &str {
                r#"{"type":"object","properties":{"ms":{"type":"integer"}}}"#
            }
            fn is_read_only(&self) -> bool {
                true
            }
            async fn execute(
                &self,
                input: &serde_json::Value,
                _: &ToolContext,
                _: CancellationToken,
            ) -> ToolOutcome {
                let ms = input.get("ms").and_then(serde_json::Value::as_u64).unwrap_or(0);
                tokio::time::sleep(Duration::from_millis(ms)).await;
                ToolResult::ok("slept")
            }
        }

        /// Starts a task owned by the caller that never finishes on its own
        /// (e.g. a server); `finish_after_ms` completes it after a delay.
        struct StartTaskTool {
            started: Arc<Mutex<Vec<String>>>,
        }

        #[at]
        impl Tool for StartTaskTool {
            fn name(&self) -> &str {
                "start_task"
            }
            fn description(&self) -> &str {
                "starts a background task"
            }
            fn input_schema_json(&self) -> &str {
                r#"{"type":"object","properties":{"finish_after_ms":{"type":"integer"}}}"#
            }
            fn is_read_only(&self) -> bool {
                true
            }
            async fn execute(
                &self,
                input: &serde_json::Value,
                ctx: &ToolContext,
                _: CancellationToken,
            ) -> ToolOutcome {
                use crate::tool::ToolContextServiceExt as _;
                let mgr = Arc::clone(ctx.get_task_manager().unwrap());
                let task = mgr
                    .register(
                        TaskKind::Shell,
                        "long job",
                        ctx.caller_task_id.as_deref(),
                        TaskExecutionMode::Background,
                    )
                    .unwrap();
                mgr.append_output(&task.id, "job output line");
                self.started.lock().unwrap().push(task.id.clone());
                if let Some(ms) = input.get("finish_after_ms").and_then(serde_json::Value::as_u64)
                {
                    let id = task.id.clone();
                    tokio::spawn(async move {
                        tokio::time::sleep(Duration::from_millis(ms)).await;
                        mgr.complete(&id, Some("exit code: 0".into()));
                    });
                }
                ToolResult::ok(format!("started {}", task.id))
            }
        }

        fn depth_one_child(mgr: &TaskManager, what: &str) -> String {
            mgr.register(TaskKind::Subagent, what, None, TaskExecutionMode::Foreground)
                .unwrap()
                .id
                .clone()
        }

        #[tokio::test(start_paused = true)]
        async fn a_burst_of_five_completions_wakes_a_parked_subagent_once() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let calls: Vec<(String, String)> = (1..=5)
                .map(|i| (format!("t{i}"), format!(r#"{{"prompt":"leaf {i}"}}"#)))
                .collect();
            let batch: Vec<(&str, &str, String)> =
                calls.iter().map(|(id, input)| (id.as_str(), "task", input.clone())).collect();
            let mut routes = vec![(
                "orchestrate",
                vec![tools_turn(&batch), text_turn("waiting"), text_turn("all five done")],
            )];
            let leaf_keys: Vec<String> = (1..=5).map(|i| format!("leaf {i}")).collect();
            for (i, key) in leaf_keys.iter().enumerate() {
                let ms = (i as u64 + 1) * 100;
                routes.push((
                    key.as_str(),
                    vec![
                        tool_turn("s", "slow", &format!(r#"{{"ms":{ms}}}"#)),
                        text_turn(&format!("{key} report")),
                    ],
                ));
            }
            let client = RoutedClient::new(routes);
            let host = host_with(
                Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
                vec![
                    Arc::new(crate::tools::TaskTool) as Arc<dyn Tool>,
                    Arc::new(SlowTool) as Arc<dyn Tool>,
                ],
                Arc::clone(&mgr),
                8,
            );
            let child = depth_one_child(&mgr, "orchestrate");
            let request = SubagentRequest::foreground("general-purpose", "orchestrate", &child, 1);

            let report = host
                .spawn(request, Arc::new(CollectingSink::new()), CancellationToken::new())
                .await
                .unwrap();

            assert_eq!(report, "all five done");
            let requests = client.requests_for("orchestrate");
            assert_eq!(requests.len(), 3, "five completions coalesce into one wake-up turn");
            assert_eq!(requests[2].matches("[task-completed]").count(), 5, "{}", requests[2]);
            for i in 1..=5 {
                assert!(requests[2].contains(&format!("leaf {i} report")), "{}", requests[2]);
            }
        }

        #[tokio::test(start_paused = true)]
        async fn a_parked_subagent_is_woken_by_a_message_then_reports_at_its_park_deadline() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let started = Arc::new(Mutex::new(Vec::new()));
            let client = RoutedClient::new(vec![(
                "watch the job",
                vec![
                    tool_turn("j", "start_task", "{}"),
                    text_turn("parked while the job runs"),
                    text_turn("noted the new instruction"),
                    text_turn("partial report: the job never finished"),
                ],
            )]);
            let host = host_with(
                Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
                vec![Arc::new(StartTaskTool { started: Arc::clone(&started) }) as Arc<dyn Tool>],
                Arc::clone(&mgr),
                4,
            );
            let child = depth_one_child(&mgr, "watch the job");
            let request =
                SubagentRequest::foreground("general-purpose", "watch the job", &child, 1);
            let run = tokio::spawn(async move {
                host.spawn(request, Arc::new(CollectingSink::new()), CancellationToken::new())
                    .await
            });

            // Let it start the job and park.
            tokio::time::sleep(Duration::from_secs(60)).await;
            assert_eq!(client.requests_for("watch the job").len(), 2, "parked: no model calls");
            let sent = crate::tools::TaskSendTool
                .execute(
                    &serde_json::json!({"taskId": child, "message": "also check the logs"}),
                    &ToolContext::new(".").with_task_manager(Arc::clone(&mgr)),
                    CancellationToken::new(),
                )
                .await;
            assert!(!sent.is_error, "{}", sent.content);

            let report = run.await.unwrap().unwrap();
            assert_eq!(report, "partial report: the job never finished");
            let requests = client.requests_for("watch the job");
            assert_eq!(requests.len(), 4);
            assert!(requests[2].contains("also check the logs"), "{}", requests[2]);
            assert!(
                requests[3].contains("[deadline]") && requests[3].contains("maximum time waiting"),
                "{}",
                requests[3]
            );
            let job = mgr.find_task(&started.lock().unwrap()[0]).unwrap();
            assert!(job.cancel.is_cancelled(), "a subagent's tasks do not outlive it");
        }

        #[tokio::test(start_paused = true)]
        async fn a_subagent_past_its_lifetime_is_asked_for_its_report() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let owner = depth_one_child(&mgr, "long work");
            let client = RoutedClient::new(vec![(
                "long work",
                vec![tool_turn("s", "slow", r#"{"ms":61000}"#), text_turn("final partial report")],
            )]);
            let policy = ParkPolicy {
                max_lifetime: Some(Duration::from_secs(60)),
                ..ParkPolicy::subagent(owner.clone())
            };
            let agent = AgentLoopBuilder::new(
                Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
                Arc::new(AllowAll),
                Arc::new(ToolRegistry::new(vec![Arc::new(SlowTool) as Arc<dyn Tool>])),
            )
            .with_task_manager(Arc::clone(&mgr))
            .with_caller_task_id(owner)
            .with_steering(Arc::new(SteeringInbox::new()))
            .with_park_policy(policy)
            .build();

            let mut history = vec![Message::user("long work")];
            agent
                .run(&mut history, &CollectingSink::new(), None, CancellationToken::new())
                .await
                .unwrap();

            let requests = client.requests_for("long work");
            assert_eq!(requests.len(), 2);
            assert!(
                requests[1].contains("[deadline]") && requests[1].contains("maximum lifetime"),
                "{}",
                requests[1]
            );
        }

        #[tokio::test(start_paused = true)]
        async fn a_headless_main_run_does_not_end_while_its_tasks_run() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let bus = Arc::new(MessageBus::new());
            mgr.set_completion_listener(MainInboxCompletionPush::new(&bus));
            let client = RoutedClient::new(vec![(
                "build it",
                vec![
                    tool_turn("b", "start_task", r#"{"finish_after_ms":600000}"#),
                    text_turn("started the build"),
                    text_turn("the build passed"),
                ],
            )]);
            let started = Arc::new(Mutex::new(Vec::new()));
            let agent = AgentLoopBuilder::new(
                Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
                Arc::new(AllowAll),
                Arc::new(ToolRegistry::new(vec![
                    Arc::new(StartTaskTool { started: Arc::clone(&started) }) as Arc<dyn Tool>
                ])),
            )
            .with_task_manager(Arc::clone(&mgr))
            .with_message_bus(Arc::clone(&bus))
            .with_steering(Arc::new(SteeringInbox::new()))
            .with_main_context()
            .with_park_policy(ParkPolicy::headless_main())
            .build();

            let begun = tokio::time::Instant::now();
            let mut history = vec![Message::user("build it")];
            agent
                .run(&mut history, &CollectingSink::new(), None, CancellationToken::new())
                .await
                .unwrap();

            assert!(begun.elapsed() >= Duration::from_secs(600), "the run waited for the build");
            let requests = client.requests_for("build it");
            assert_eq!(requests.len(), 3);
            assert!(
                requests[2].contains("[task-completed]") && requests[2].contains("exit code: 0"),
                "{}",
                requests[2]
            );
            let build = mgr.find_task(&started.lock().unwrap()[0]).unwrap();
            assert_eq!(build.status(), TaskRunStatus::Completed);
        }

        /// `task_wait` attaches with the yield contract: capped at 30 s, not
        /// cut short by a new message, and it never stops the task.
        #[tokio::test(start_paused = true)]
        async fn task_wait_is_capped_and_not_cut_short_by_a_message() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let job = mgr
                .register(TaskKind::Shell, "job", None, TaskExecutionMode::Background)
                .unwrap();
            let wait = format!(r#"{{"taskId":"{}","timeoutSeconds":300}}"#, job.id);
            let client = RoutedClient::new(vec![
                ("capped", vec![tool_turn("w", "task_wait", &wait), text_turn("ok")]),
                ("steered", vec![tool_turn("w", "task_wait", &wait), text_turn("ok")]),
            ]);
            let build = |steering: Arc<SteeringInbox>| {
                AgentLoopBuilder::new(
                    Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
                    Arc::new(AllowAll),
                    Arc::new(ToolRegistry::new(vec![
                        Arc::new(crate::tools::TaskWaitTool) as Arc<dyn Tool>
                    ])),
                )
                .with_task_manager(Arc::clone(&mgr))
                .with_steering(steering)
                .with_main_context()
                .build()
            };

            let begun = tokio::time::Instant::now();
            let mut history = vec![Message::user("capped")];
            build(Arc::new(SteeringInbox::new()))
                .run(&mut history, &CollectingSink::new(), None, CancellationToken::new())
                .await
                .unwrap();
            assert_eq!(begun.elapsed(), Duration::from_secs(30), "300 s is capped at 30 s");
            assert!(client.requests_for("capped")[1].contains("still running"));

            let steering = Arc::new(SteeringInbox::new());
            let agent = build(Arc::clone(&steering));
            let begun = tokio::time::Instant::now();
            let sender = {
                let steering = Arc::clone(&steering);
                tokio::spawn(async move {
                    tokio::time::sleep(Duration::from_secs(5)).await;
                    steering.enqueue("are you done?");
                })
            };
            let mut history = vec![Message::user("steered")];
            agent
                .run(&mut history, &CollectingSink::new(), None, CancellationToken::new())
                .await
                .unwrap();
            sender.await.unwrap();
            assert_eq!(begun.elapsed(), Duration::from_secs(30), "a message does not end the wait");
            let second = &client.requests_for("steered")[1];
            assert!(second.contains("still running"), "{second}");
            assert!(!second.contains("ended early"), "{second}");
            assert!(second.contains("are you done?"), "{second}");
            assert_eq!(job.status(), TaskRunStatus::Running, "waiting never stops the task");
        }

        /// A completion the owner already received inline through `task_wait`
        /// is not pushed to it a second time.
        #[tokio::test(start_paused = true)]
        async fn a_completion_seen_through_task_wait_is_not_pushed_again() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let bus = Arc::new(MessageBus::new());
            mgr.set_completion_listener(MainInboxCompletionPush::new(&bus));
            let job = mgr
                .register(TaskKind::Shell, "job", None, TaskExecutionMode::Background)
                .unwrap();
            {
                let mgr = Arc::clone(&mgr);
                let id = job.id.clone();
                tokio::spawn(async move {
                    tokio::time::sleep(Duration::from_secs(5)).await;
                    mgr.complete(&id, Some("exit code: 0\nall good".into()));
                });
            }
            let wait = format!(r#"{{"taskId":"{}"}}"#, job.id);
            let client = RoutedClient::new(vec![(
                "wait for it",
                vec![tool_turn("w", "task_wait", &wait), text_turn("ok")],
            )]);
            let agent = AgentLoopBuilder::new(
                Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
                Arc::new(AllowAll),
                Arc::new(ToolRegistry::new(vec![
                    Arc::new(crate::tools::TaskWaitTool) as Arc<dyn Tool>
                ])),
            )
            .with_task_manager(Arc::clone(&mgr))
            .with_message_bus(Arc::clone(&bus))
            .with_steering(Arc::new(SteeringInbox::new()))
            .with_main_context()
            .build();
            let mut history = vec![Message::user("wait for it")];
            agent
                .run(&mut history, &CollectingSink::new(), None, CancellationToken::new())
                .await
                .unwrap();

            let second = &client.requests_for("wait for it")[1];
            assert!(second.contains("completed") && second.contains("all good"), "{second}");
            assert!(!second.contains("[task-completed]"), "{second}");
            assert!(!bus.has_pending_main(), "the completion must not be pushed as well");
        }

        /// Failure and stop of a subagent reach the parent as completions.
        #[tokio::test(start_paused = true)]
        async fn a_failed_and_a_stopped_child_subagent_both_wake_the_parent() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let client = RoutedClient::new(vec![
                (
                    "parent",
                    vec![
                        tools_turn(&[
                            ("a", "task", r#"{"prompt":"doomed child"}"#.into()),
                            ("b", "task", r#"{"prompt":"endless child"}"#.into()),
                        ]),
                        text_turn("waiting"),
                        text_turn("first wake"),
                        text_turn("second wake"),
                    ],
                ),
                ("doomed child", vec![tool_turn("x", "abort_now", "{}")]),
                ("endless child", vec![tool_turn("j", "start_task", "{}"), text_turn("parked")]),
            ]);
            let host = host_with(
                Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
                vec![
                    Arc::new(crate::tools::TaskTool) as Arc<dyn Tool>,
                    Arc::new(AbortTool) as Arc<dyn Tool>,
                    Arc::new(StartTaskTool { started: Arc::new(Mutex::new(Vec::new())) })
                        as Arc<dyn Tool>,
                ],
                Arc::clone(&mgr),
                8,
            );
            let parent = depth_one_child(&mgr, "parent");
            let request = SubagentRequest::foreground("general-purpose", "parent", &parent, 1);
            let run = tokio::spawn(async move {
                host.spawn(request, Arc::new(CollectingSink::new()), CancellationToken::new())
                    .await
            });
            tokio::time::sleep(Duration::from_secs(60)).await;
            let endless = mgr
                .list()
                .into_iter()
                .find(|t| t.description == "endless child")
                .unwrap();
            assert_eq!(mgr.stop_task(&endless.id), crate::tasks::TaskActionResult::Ok);

            assert_eq!(run.await.unwrap().unwrap(), "second wake");
            let requests = client.requests_for("parent");
            let all = requests.join("\n");
            assert!(all.contains("status: failed"), "{all}");
            assert!(all.contains("status: stopped") && all.contains("stopped by the user"), "{all}");
        }

        struct AbortTool;

        #[at]
        impl Tool for AbortTool {
            fn name(&self) -> &str {
                "abort_now"
            }
            fn description(&self) -> &str {
                "aborts the run"
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
                _: &ToolContext,
                _: CancellationToken,
            ) -> ToolOutcome {
                ToolResult::abort("test.abort", "aborted")
            }
        }

        /// The `task` tool hands back a handle at once; the subagent runs on.
        #[tokio::test]
        async fn the_task_tool_returns_a_handle_immediately() {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let client = RoutedClient::new(vec![(
                "slow job",
                vec![tool_turn("s", "slow", r#"{"ms":3600000}"#), text_turn("done")],
            )]);
            let host = host_with(
                Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
                vec![Arc::new(SlowTool) as Arc<dyn Tool>],
                Arc::clone(&mgr),
                4,
            );
            let ctx = ToolContext::new(".")
                .with_task_manager(Arc::clone(&mgr))
                .with_subagent_factory(Arc::clone(&host) as Arc<dyn SubagentFactory>);
            let result = tokio::time::timeout(
                Duration::from_secs(5),
                crate::tools::TaskTool.execute(
                    &serde_json::json!({"prompt": "slow job"}),
                    &ctx,
                    CancellationToken::new(),
                ),
            )
            .await
            .expect("task must not wait for the subagent");
            assert!(result.content.starts_with("Started subagent task task-"), "{}", result.content);
            let task = &mgr.list()[0];
            assert_eq!(task.status, TaskRunStatus::Running);
            assert_eq!(task.mode, TaskExecutionMode::Background);
            mgr.stop_all();
        }
    }

    // ── Planned restart: subagent checkpoints and restore ──────────────────

    mod restart {
        use std::sync::atomic::{AtomicBool, Ordering};
        use std::sync::{Arc, Mutex};
        use std::time::{Duration, SystemTime};

        use async_trait::async_trait as at;
        use chrono::{DateTime, Utc};
        use coda_llm::{Content, Correlation, Message, Role};
        use tokio_util::sync::CancellationToken;

        use super::nonblocking::RoutedClient;
        use super::trusted_child_context::{host_with, manager, text_turn, tool_turn};
        use crate::events::CollectingSink;
        use crate::subagents::SubagentFactory as _;
        use crate::subagents::{
            CheckpointInboxItem, SubagentCheckpoint, SubagentRequest, SubagentSpec,
            CHECKPOINT_SIZE_CAP, SUBAGENT_CHECKPOINT_VERSION,
        };
        use crate::tasks::{ManagedTask, TaskEventObserver, TaskManager, TaskRunStatus};
        use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

        /// Blocks until the test opens it.
        struct GateTool {
            entered: AtomicBool,
            open: tokio::sync::Semaphore,
        }

        impl Default for GateTool {
            fn default() -> Self {
                Self { entered: AtomicBool::new(false), open: tokio::sync::Semaphore::new(0) }
            }
        }

        #[at]
        impl Tool for GateTool {
            fn name(&self) -> &str {
                "gate"
            }
            fn description(&self) -> &str {
                "waits for the test"
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
                _: &ToolContext,
                _: CancellationToken,
            ) -> ToolOutcome {
                self.entered.store(true, Ordering::SeqCst);
                self.open.acquire().await.unwrap().forget();
                ToolResult::ok("gate opened")
            }
        }

        #[derive(Default)]
        struct StartedRecorder {
            started: Mutex<Vec<(String, bool)>>,
        }

        impl TaskEventObserver for StartedRecorder {
            fn on_started(&self, task: &Arc<ManagedTask>) {
                let resumed = task.to_snapshot().resumed;
                self.started.lock().unwrap().push((task.id.clone(), resumed));
            }
            fn on_output(&self, _: &Arc<ManagedTask>) {}
            fn on_terminal(&self, _: &Arc<ManagedTask>, _: TaskRunStatus) {}
        }

        async fn wait_until(what: &str, mut done: impl FnMut() -> bool) {
            tokio::time::timeout(Duration::from_secs(10), async {
                while !done() {
                    tokio::time::sleep(Duration::from_millis(5)).await;
                }
            })
            .await
            .unwrap_or_else(|_| panic!("timed out waiting for {what}"));
        }

        fn status(mgr: &TaskManager, id: &str) -> TaskRunStatus {
            mgr.get(id).unwrap().status
        }

        fn sample_spec() -> SubagentSpec {
            SubagentSpec {
                agent_type: "general-purpose".into(),
                prompt: "parent work".into(),
                system_prompt: "You are a subagent.".into(),
                model: "model".into(),
                provider: Some("routed".into()),
                effort: None,
                depth: 1,
                max_steps: 10,
                max_tokens: 256,
                foreground: false,
                schedule_origin: Some(serde_json::json!({
                    "definitionId": "def-1",
                    "definitionName": "nightly"
                })),
            }
        }

        fn tool_exchange(id: &str, output: String) -> Vec<Message> {
            vec![
                Message::new(
                    Role::Assistant,
                    vec![Content::ToolUse {
                        id: id.into(),
                        name: "probe".into(),
                        input_json: "{}".into(),
                        correlation: Correlation::default(),
                    }],
                ),
                Message::new(
                    Role::User,
                    vec![Content::ToolResult {
                        tool_use_id: id.into(),
                        content: output,
                        is_error: false,
                        correlation: Correlation::default(),
                        status: None,
                    }],
                ),
            ]
        }

        fn sample_checkpoint(history: &[Message]) -> SubagentCheckpoint {
            let at: DateTime<Utc> = DateTime::<Utc>::from(SystemTime::UNIX_EPOCH)
                + chrono::Duration::seconds(1_800_000_000);
            SubagentCheckpoint {
                version: SUBAGENT_CHECKPOINT_VERSION,
                task_id: "task-00000003".into(),
                owner_task_id: None,
                reports_to: "main".into(),
                kind: "subagent".into(),
                description: "parent work".into(),
                mode: "background".into(),
                spec: sample_spec(),
                conversation: crate::session::message_json::serialize_messages(history),
                steps_used: 2,
                parked: true,
                steering: vec![
                    CheckpointInboxItem { kind: "message".into(), text: "check logs".into() },
                    CheckpointInboxItem {
                        kind: "taskCompleted".into(),
                        text: "[task-completed] task-00000004".into(),
                    },
                ],
                explicitly_suspended: true,
                started_at: at,
                suspended_at: at + chrono::Duration::seconds(5),
                truncated: false,
            }
        }

        fn tool_outputs(cp: &SubagentCheckpoint) -> Vec<String> {
            cp.messages()
                .into_iter()
                .flat_map(|m| m.content)
                .filter_map(|c| match c {
                    Content::ToolResult { content, .. } => Some(content),
                    _ => None,
                })
                .collect()
        }

        #[tokio::test]
        async fn checkpoint_round_trips_through_json() {
            let mut history = vec![Message::user("parent work")];
            history.extend(tool_exchange("p1", "probed".into()));
            history.push(Message::assistant("waiting"));
            let cp = sample_checkpoint(&history);

            let (bytes, truncated) = cp.to_json_capped(CHECKPOINT_SIZE_CAP);
            assert!(!truncated);
            let json: serde_json::Value = serde_json::from_slice(&bytes).unwrap();
            assert_eq!(json["taskId"], "task-00000003");
            assert_eq!(json["spec"]["systemPrompt"], "You are a subagent.");
            assert_eq!(json["reportsTo"], "main");

            let dir = tempfile::tempdir().unwrap();
            let (path, truncated) = cp.write(dir.path()).await.unwrap();
            assert!(!truncated);
            assert_eq!(path, dir.path().join("task-00000003.json"));
            let back = SubagentCheckpoint::read(&path).unwrap();
            assert_eq!(back, cp);
            assert_eq!(back.messages(), history);
            assert_eq!(back.spec.schedule_origin().unwrap().definition_id, "def-1");

            let mut newer = json.clone();
            newer["version"] = serde_json::json!(SUBAGENT_CHECKPOINT_VERSION + 1);
            let newer_path = dir.path().join("newer.json");
            std::fs::write(&newer_path, serde_json::to_vec(&newer).unwrap()).unwrap();
            let err = SubagentCheckpoint::read(&newer_path).unwrap_err();
            assert!(err.contains("version 2"), "{err}");
        }

        #[test]
        fn oversized_checkpoint_truncates_oldest_tool_outputs_first() {
            let mut history = vec![Message::user("parent work")];
            for (i, fill) in ['a', 'b', 'c'].into_iter().enumerate() {
                history.extend(tool_exchange(&format!("p{i}"), fill.to_string().repeat(2048)));
            }
            let cp = sample_checkpoint(&history);

            let (bytes, truncated) = cp.to_json_capped(4 * 1024);
            assert!(truncated);
            assert!(bytes.len() <= 4 * 1024, "{} bytes", bytes.len());
            let back: SubagentCheckpoint = serde_json::from_slice(&bytes).unwrap();
            assert!(back.truncated);
            let outputs = tool_outputs(&back);
            assert_eq!(
                outputs[0],
                "[truncated for a planned restart: 2048 characters omitted]"
            );
            assert_eq!(outputs[2], "c".repeat(2048), "the newest output is kept");
            assert_eq!(back.steering, cp.steering);
        }

        /// A background parent parked on a child it started with the `task`
        /// tool, and that child, both suspended at safe points; returns
        /// their checkpoints (parent, child).
        async fn suspended_pair() -> (SubagentCheckpoint, SubagentCheckpoint) {
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let gate = Arc::new(GateTool::default());
            let client = RoutedClient::new(vec![
                (
                    "parent work",
                    vec![
                        tool_turn("t1", "task", r#"{"prompt":"child work"}"#),
                        text_turn("waiting for the child"),
                    ],
                ),
                ("child work", vec![tool_turn("g1", "gate", "{}")]),
            ]);
            let host = host_with(
                Arc::clone(&client) as Arc<dyn coda_llm::LlmClient>,
                vec![
                    Arc::new(crate::tools::TaskTool) as Arc<dyn Tool>,
                    Arc::clone(&gate) as Arc<dyn Tool>,
                ],
                Arc::clone(&mgr),
                8,
            );
            let mut request = SubagentRequest::foreground("general-purpose", "parent work", "", 1);
            request.foreground = false;
            let parent = host
                .spawn(request, Arc::new(CollectingSink::new()), CancellationToken::new())
                .await
                .unwrap();

            wait_until("the child to enter the gate", || gate.entered.load(Ordering::SeqCst))
                .await;
            wait_until("the parent to park", || client.requests_for("parent work").len() == 2)
                .await;
            tokio::time::sleep(Duration::from_millis(50)).await;
            let child = mgr
                .list()
                .into_iter()
                .find(|t| t.parent_id.as_deref() == Some(parent.as_str()))
                .unwrap()
                .id;

            assert_eq!(mgr.request_suspend(&parent), crate::tasks::TaskActionResult::Ok);
            wait_until("the parent to suspend", || {
                status(&mgr, &parent) == TaskRunStatus::Suspended
            })
            .await;
            gate.open.add_permits(1);
            wait_until("the child to suspend", || status(&mgr, &child) == TaskRunStatus::Suspended)
                .await;

            let mut parent_cp = SubagentCheckpoint::from_task(&mgr, &parent).unwrap();
            let child_cp = SubagentCheckpoint::from_task(&mgr, &child).unwrap();
            mgr.stop_all();

            assert!(parent_cp.parked, "the parent suspended while parked");
            assert!(parent_cp.explicitly_suspended);
            // Suspending for the restart itself is not an operator's request
            // to keep it paused; the restart writer records only those.
            parent_cp.explicitly_suspended = false;
            assert_eq!(parent_cp.reports_to, "main");
            assert_eq!(parent_cp.spec.depth, 1);
            assert!(!child_cp.parked);
            assert!(!child_cp.explicitly_suspended, "the child inherited the suspension");
            assert_eq!(child_cp.steps_used, 1);
            assert_eq!(child_cp.owner_task_id.as_deref(), Some(parent.as_str()));
            assert_eq!(child_cp.reports_to, parent);
            assert_eq!(child_cp.spec.depth, 2);
            (parent_cp, child_cp)
        }

        fn restored_host(
            client: &Arc<RoutedClient>,
            mgr: &Arc<TaskManager>,
        ) -> Arc<crate::subagents::SubagentHost> {
            host_with(
                Arc::clone(client) as Arc<dyn coda_llm::LlmClient>,
                vec![
                    Arc::new(crate::tools::TaskTool) as Arc<dyn Tool>,
                    Arc::new(GateTool::default()) as Arc<dyn Tool>,
                ],
                Arc::clone(mgr),
                8,
            )
        }

        fn after_restart_client() -> Arc<RoutedClient> {
            RoutedClient::new(vec![
                ("parent work", vec![text_turn("parent final: the child reported")]),
                ("child work", vec![text_turn("child report after the restart")]),
            ])
        }

        fn assert_parent_got_child_report(client: &RoutedClient, mgr: &TaskManager, id: &str) {
            let requests = client.requests_for("parent work");
            assert_eq!(requests.len(), 1, "the parked parent waits for the report");
            assert!(requests[0].contains("waiting for the child"), "{}", requests[0]);
            assert!(
                requests[0].contains("[task-completed]")
                    && requests[0].contains("child report after the restart"),
                "{}",
                requests[0]
            );
            let parent = mgr.get(id).unwrap();
            assert_eq!(parent.status, TaskRunStatus::Completed);
            assert_eq!(parent.result.as_deref(), Some("parent final: the child reported"));
        }

        #[tokio::test]
        async fn nested_subagents_suspend_and_restore_under_same_ids() {
            let (parent_cp, child_cp) = suspended_pair().await;

            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let observer = Arc::new(StartedRecorder::default());
            mgr.set_event_observer(Arc::clone(&observer) as Arc<dyn TaskEventObserver>);
            let client = after_restart_client();
            let host = restored_host(&client, &mgr);
            let sink = Arc::new(CollectingSink::new());

            let restored = host.restore_all(vec![child_cp.clone(), parent_cp.clone()], false, sink).await;
            let parent = restored[0].1.clone().unwrap();
            let child = restored[1].1.clone().unwrap();
            assert_eq!(parent, parent_cp.task_id);
            assert_eq!(child, child_cp.task_id);
            assert_eq!(mgr.get(&child).unwrap().parent_id.as_deref(), Some(parent.as_str()));

            wait_until("the parent to finish", || status(&mgr, &parent).is_terminal()).await;
            let child_requests = client.requests_for("child work");
            assert_eq!(child_requests.len(), 1);
            assert!(child_requests[0].contains("gate opened"), "{}", child_requests[0]);
            assert_eq!(status(&mgr, &child), TaskRunStatus::Completed);
            assert_parent_got_child_report(&client, &mgr, &parent);
            assert_eq!(
                *observer.started.lock().unwrap(),
                vec![(parent.clone(), true), (child.clone(), true)]
            );
        }

        #[tokio::test]
        async fn restore_keep_suspended_waits_for_resume() {
            let (parent_cp, child_cp) = suspended_pair().await;

            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let client = after_restart_client();
            let host = restored_host(&client, &mgr);
            // Child first: `restore_all` orders owners before their children.
            let results = host
                .restore_all(
                    vec![child_cp.clone(), parent_cp.clone()],
                    true,
                    Arc::new(CollectingSink::new()),
                )
                .await;
            assert!(results.iter().all(|(_, r)| r.is_ok()), "{results:?}");
            let (parent, child) = (parent_cp.task_id.clone(), child_cp.task_id.clone());

            wait_until("both to suspend", || {
                status(&mgr, &parent) == TaskRunStatus::Suspended
                    && status(&mgr, &child) == TaskRunStatus::Suspended
            })
            .await;
            tokio::time::sleep(Duration::from_millis(100)).await;
            assert!(client.requests_for("child work").is_empty(), "no model call while suspended");
            assert!(client.requests_for("parent work").is_empty());
            let again = SubagentCheckpoint::from_task(&mgr, &child).unwrap();
            assert_eq!(again.messages(), child_cp.messages(), "a restored run can checkpoint again");
            assert!(SubagentCheckpoint::from_task(&mgr, &parent).unwrap().parked);

            assert_eq!(mgr.resume_all(), 2);
            wait_until("the parent to finish", || status(&mgr, &parent).is_terminal()).await;
            assert_eq!(status(&mgr, &child), TaskRunStatus::Completed);
            assert_parent_got_child_report(&client, &mgr, &parent);
        }

        #[tokio::test]
        async fn restored_parent_receives_child_report_after_resume() {
            let (parent_cp, mut child_cp) = suspended_pair().await;
            // An operator suspended the child before the restart: it stays
            // suspended after the restore until it is resumed.
            child_cp.explicitly_suspended = true;

            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let client = after_restart_client();
            let host = restored_host(&client, &mgr);
            let sink = Arc::new(CollectingSink::new());
            let restored = host.restore_all(vec![child_cp, parent_cp], false, sink).await;
            let parent = restored[0].1.clone().unwrap();
            let child = restored[1].1.clone().unwrap();

            wait_until("the child to suspend", || status(&mgr, &child) == TaskRunStatus::Suspended)
                .await;
            tokio::time::sleep(Duration::from_millis(100)).await;
            assert_eq!(status(&mgr, &parent), TaskRunStatus::Running, "parked on the child");
            assert!(client.requests_for("parent work").is_empty());

            assert_eq!(mgr.request_resume(&child), crate::tasks::TaskActionResult::Ok);
            wait_until("the parent to finish", || status(&mgr, &parent).is_terminal()).await;
            assert_parent_got_child_report(&client, &mgr, &parent);
        }

        #[tokio::test]
        async fn restore_refuses_a_child_whose_owner_is_missing() {
            let (_, child_cp) = suspended_pair().await;
            let dir = tempfile::tempdir().unwrap();
            let mgr = manager(&dir);
            let host = restored_host(&after_restart_client(), &mgr);
            let err = host
                .restore(child_cp, false, Arc::new(CollectingSink::new()))
                .await
                .unwrap_err();
            assert!(err.contains("Unknown parent"), "{err}");
            assert!(mgr.list().is_empty());
        }
    }
}
