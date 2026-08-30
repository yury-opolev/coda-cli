//! The agent control loop and its builder.
//!
//! `AgentLoop::run` is the single entry point.  It mutates `history` in place
//! (appending assistant turns, tool-result user turns, and synthetic
//! injections), emits events to `sink`, and returns when the turn completes
//! naturally or due to a limit / cancellation.
//!
//! # Builder
//! `AgentLoopBuilder` constructs the loop from mandatory fields (client,
//! permissions, tools) plus a set of optional extras.  This replaces the C#
//! 20-parameter constructor.
//!
//! # Optional seams
//! Where the loop needs hooks, compaction, subagents, LSP, or task
//! management (later phases), the builder accepts `Option<Arc<…>>` fields and
//! the loop skips those paths when they are absent.

use std::sync::Arc;
use std::time::Duration;

use coda_llm::{
    ChatRequest, Content, Correlation as LlmCorrelation, Effort, Message, Role,
};
use tokio_util::sync::CancellationToken;

use crate::compaction::{CompactionPolicy, CompactionService, TokenEstimator};
use crate::events::{AgentEvent, AgentSink};
use crate::goal::{GoalStatus, GoalSupervisor, last_assistant_text};
use crate::lsp::LspServerManager;
use crate::permission::{PermissionMode, PermissionModeState, PermissionPrompt};
use crate::scheduling::ScheduledTaskStore;
use crate::steering::SteeringInbox;
use crate::subagents::SubagentFactory;
use crate::tasks::TaskManager;
use crate::todos::TodoStore;
use crate::tool::{PlanApprover, ToolQuarantine, ToolRegistry, UserQuestion};

pub mod stop;
pub mod stream;
pub mod tools;

use stop::{StopAction, UserQuestionPrompt, decide_stop};
use stream::{RetryConfig, is_context_overflow_error, stream_with_retries};
use tools::{BatchContext, ToolBatchResult, run_tools};

// ─────────────────────────────────────────────────────────────────────────────
// Error type
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, thiserror::Error)]
pub enum AgentError {
    #[error("operation cancelled")]
    Cancelled,
    #[error("LLM error: {0}")]
    Llm(#[from] coda_llm::LlmError),
    #[error("{0}")]
    Other(#[from] anyhow::Error),
}

// ─────────────────────────────────────────────────────────────────────────────
// Correlation / activity tracking
// ─────────────────────────────────────────────────────────────────────────────

/// Generates correlation IDs for a tool-call batch, stamped on each
/// `Content::ToolUse` before it is appended to history.
pub(crate) struct ToolActivity {
    root_turn_id: String,
    activity_id: String,
}

impl ToolActivity {
    pub fn new() -> Self {
        Self {
            root_turn_id: uuid::Uuid::new_v4().to_string(),
            activity_id: uuid::Uuid::new_v4().to_string(),
        }
    }

    pub fn for_call(&self, call_id: &str) -> LlmCorrelation {
        LlmCorrelation {
            root_turn_id: Some(self.root_turn_id.clone()),
            activity_id: Some(self.activity_id.clone()),
            source_id: Some(call_id.to_owned()),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AgentLoop
// ─────────────────────────────────────────────────────────────────────────────

/// The immutable configuration of an agent run loop.
///
/// Build with [`AgentLoopBuilder`]; call [`AgentLoop::run`] to start a turn.
pub struct AgentLoop {
    // Core services (always present).
    client: Arc<dyn coda_llm::LlmClient>,
    permission_prompt: Arc<dyn PermissionPrompt>,
    tools: Arc<ToolRegistry>,
    quarantine: Arc<ToolQuarantine>,

    // Permissions.
    permission_mode: PermissionMode,
    permission_mode_state: Option<Arc<PermissionModeState>>,

    // Optional services.
    steering: Option<Arc<SteeringInbox>>,
    // Seam: user-question prompt for goal escalation (stop-decision ladder).
    user_question: Option<Arc<dyn UserQuestionPrompt>>,
    /// Hook runner: fires lifecycle hooks (PreToolUse, PostToolUse, AgentResponse, Stop, …).
    /// `None` means no hooks are configured for this run.
    hook_runner: Option<Arc<crate::hooks::HookRunner>>,
    /// Compaction service: summarises history to free context space.
    compaction_service: Option<Arc<CompactionService>>,
    /// Policy controlling when proactive compaction fires.
    compaction_policy: CompactionPolicy,

    // ── Stateful service handles forwarded into ToolContext on every batch ────
    // Without these the built-in tools that need a store or factory all silently
    // no-op or return "not available" errors (CRITICAL 1).
    /// Session todo list; wired via `with_todos`.
    todos: Option<Arc<TodoStore>>,
    /// Multiple-choice question seam for the `ask_user_question` tool.
    tool_user_question: Option<Arc<dyn UserQuestion>>,
    /// Plan-approval seam for `exit_plan_mode`.
    plan_approver: Option<Arc<dyn PlanApprover>>,
    /// LSP server manager for `lsp_diagnostics`.
    lsp_manager: Option<Arc<LspServerManager>>,
    /// Task manager for all `task_*` and `background_task_*` tools.
    task_manager: Option<Arc<TaskManager>>,
    /// Scheduled task store for `schedule_*` tools.
    schedule_store: Option<Arc<ScheduledTaskStore>>,
    /// Id of the owning task (set when this loop runs inside a subagent task).
    caller_task_id: Option<String>,
    /// Subagent factory for the `task` tool.
    subagent_factory: Option<Arc<dyn SubagentFactory>>,

    // Configuration.
    model: String,
    system_prompt: Option<String>,
    max_tokens: u32,
    max_iterations: usize,
    effort: Option<Effort>,
    working_directory: String,
    granted_directories: Option<std::collections::HashSet<String>>,

    // Tuning knobs.
    tool_max_duration: Option<Duration>,
    tool_progress_interval: Duration,
    max_transport_retries: u32,
    max_schema_evictions: u32,
    /// Seam for stop hooks (later phase).
    #[allow(dead_code)]
    max_stop_continuations: u32,
}

impl AgentLoop {
    /// Begin an agent turn.
    ///
    /// - `history` is mutated in place (assistant turns, tool results, and
    ///   synthetic user messages are appended).
    /// - `goal` is owned by this call; the returned [`GoalStatus`] reflects the
    ///   final state when a goal was active.
    /// - Returns `Err(AgentError::Cancelled)` only for the **caller** cancel;
    ///   tool-ceiling timeouts are contained as error results.
    pub async fn run(
        &self,
        history: &mut Vec<Message>,
        sink: &dyn AgentSink,
        goal: Option<GoalSupervisor>,
        cancel: CancellationToken,
    ) -> Result<GoalStatus, AgentError> {
        // Reopen the steering inbox so messages enqueued between runs (or
        // concurrently during this run) are accepted.  A prior natural stop
        // will have sealed it via try_seal_empty; without this call a second
        // run on the same Arc<SteeringInbox> starts sealed and all steering
        // is silently dropped (§MINOR 4).
        if let Some(steering) = &self.steering {
            steering.open_for_turn();
        }
        // Pending post-sampling hook tasks — drained on EVERY exit path
        // (§8 item 27).  The inner function returns via the single exit point
        // below, which always drains before returning.
        let mut pending_hook_tasks: Vec<tokio::task::JoinHandle<()>> = Vec::new();
        let result =
            self.execute_loop(history, sink, goal, cancel, &mut pending_hook_tasks).await;
        // §8 item 27: always drain, regardless of Ok/Err.
        for handle in pending_hook_tasks {
            let _ = handle.await;
        }
        result
    }

    async fn execute_loop(
        &self,
        history: &mut Vec<Message>,
        sink: &dyn AgentSink,
        mut goal: Option<GoalSupervisor>,
        cancel: CancellationToken,
        _pending_hook_tasks: &mut Vec<tokio::task::JoinHandle<()>>,
    ) -> Result<GoalStatus, AgentError> {
        let mut stop_continuations: u32 = 0;
        let mut activity = ToolActivity::new();
        let mut blocked_compaction_at: Option<usize> = None;

        let retry_cfg = RetryConfig {
            max_transport_retries: self.max_transport_retries,
            max_schema_evictions: self.max_schema_evictions,
        };

        for iteration in 0_usize.. {
            // Honour caller cancel before any work this iteration.  This
            // catches cancels that arrived between iterations (e.g. after a
            // tool batch but before the next model call) and ensures the loop
            // never starts new work after a cancel.
            if cancel.is_cancelled() {
                return Err(AgentError::Cancelled);
            }

            // 1. Cooperative pause gate (no-op seam; later phase adds the gate).

            // 2. Iteration bound check (non-goal only, §1.2).
            if goal.is_none() && iteration >= self.max_iterations {
                break;
            }

            // 3. Proactive compaction (goal runs only, C# §AutoCompact).
            // Fires when: a goal is active, compaction is configured, threshold > 0,
            // and the estimated token count exceeds the threshold (with suppression
            // to prevent re-firing until history grows by a full additional threshold).
            if goal.is_some() {
                if let Some(svc) = &self.compaction_service {
                    let threshold = self.compaction_policy.token_threshold;
                    if threshold > 0 {
                        let current = TokenEstimator::estimate(history);
                        let suppressed = blocked_compaction_at
                            .map(|b| current <= b + threshold)
                            .unwrap_or(false);
                        if !suppressed && current > threshold {
                            match compact_history(
                                svc,
                                self.hook_runner.as_deref(),
                                history,
                                sink,
                                "auto",
                                cancel.clone(),
                            )
                            .await
                            {
                                Ok(true) => {} // compacted; blocked_compaction_at stays None
                                Ok(false) => {
                                    blocked_compaction_at = Some(current);
                                }
                                Err(_) => {} // swallow; compaction is best-effort
                            }
                        }
                    }
                }
            }

            // 4a. LSP diagnostics injection (no-op seam; later phase).
            // Only after the first tool cycle (iteration > 0).

            // 4b. Steering injection (§1.2 step 4).
            if let Some(steering) = &self.steering {
                let steers = steering.take_all_for_delivery();
                if !steers.is_empty() {
                    let text = steers
                        .iter()
                        .map(|e| e.text.as_str())
                        .collect::<Vec<_>>()
                        .join("\n\n");
                    history.push(Message::user(text));
                    sink.emit(AgentEvent::SteeringDelivered {
                        message_ids: steers.iter().map(|e| e.id.clone()).collect(),
                    });
                }
            }

            // 4c. Task-completion injection (no-op seam; later phase).
            // 4d. Deferred-tools reminder (no-op seam; later phase).

            // 5. Wire tool definitions.
            let tool_defs = self.quarantine.filter(self.tools.definitions());

            // 6. Build ChatRequest.
            let mut request = ChatRequest::new(self.model.clone(), history.clone())
                .with_max_tokens(self.max_tokens)
                .with_effort(self.effort)
                .with_tools(tool_defs.clone());
            if let Some(sys) = &self.system_prompt {
                request = request.with_system(sys.clone());
            }

            // 7. Stream with retry arms.
            // For the overflow arm: if stream returns a context-overflow error and
            // compaction is configured, compact history and retry once.
            let mut overflow_retried = false;
            let stream_result = loop {
                let sr = stream_with_retries(
                    &*self.client,
                    &mut request,
                    &self.quarantine,
                    sink,
                    cancel.clone(),
                    &retry_cfg,
                    None,
                    &mut blocked_compaction_at,
                )
                .await;

                // Overflow compaction retry (matches C# one-shot compact-retry).
                if let Err(ref err) = sr {
                    if !overflow_retried
                        && !cancel.is_cancelled()
                        && is_context_overflow_error(err)
                    {
                        if let Some(svc) = &self.compaction_service {
                            let threshold = self.compaction_policy.token_threshold;
                            let current = TokenEstimator::estimate(history);
                            let suppressed = if threshold > 0 {
                                blocked_compaction_at
                                    .map(|b| current <= b + threshold)
                                    .unwrap_or(false)
                            } else {
                                false
                            };

                            if !suppressed {
                                overflow_retried = true;
                                match compact_history(
                                    svc,
                                    self.hook_runner.as_deref(),
                                    history,
                                    sink,
                                    "overflow",
                                    cancel.clone(),
                                )
                                .await
                                {
                                    Ok(true) => {
                                        // Rebuild request with compacted history and retry.
                                        request = ChatRequest::new(
                                            self.model.clone(),
                                            history.clone(),
                                        )
                                        .with_max_tokens(self.max_tokens)
                                        .with_effort(self.effort)
                                        .with_tools(tool_defs.clone());
                                        if let Some(sys) = &self.system_prompt {
                                            request = request.with_system(sys.clone());
                                        }
                                        continue; // retry with compacted history
                                    }
                                    Ok(false) => {
                                        blocked_compaction_at = Some(current);
                                    }
                                    Err(_) => {} // swallow; let the overflow error surface
                                }
                            }
                        }
                    }
                }
                break sr;
            };

            // §PRIVACY withhold-on-interrupt (streaming cancel path):
            // When stream_with_retries returns LlmError::Cancelled, partial text
            // has already been emitted to the sink but the accumulator is not
            // returned.  Emit the warning NOW — before propagating — so the sink
            // always sees the notice when a turn is interrupted mid-stream.
            if matches!(&stream_result, Err(coda_llm::LlmError::Cancelled)) {
                sink.emit(AgentEvent::Warning {
                    message: "[response withheld — turn was interrupted before it completed]"
                        .into(),
                });
                return Err(AgentError::Cancelled);
            }

            let mut acc = stream_result.map_err(|e| {
                // Normalise LlmError::Cancelled → AgentError::Cancelled so every
                // cancel-observing path (retry-loop top, select arm, or this spot)
                // returns the same variant.  A caller matching AgentError::Cancelled
                // would otherwise miss the streaming case where the token was seen
                // by stream_with_retries rather than the tool path (§CRITICAL 1c).
                match e {
                    coda_llm::LlmError::Cancelled => AgentError::Cancelled,
                    other => AgentError::Llm(other),
                }
            })?;

            // 8. Stamp correlation on tool_use blocks, then assemble assistant message.
            if !acc.tool_uses.is_empty() {
                activity = ToolActivity::new(); // fresh activity per tool batch
            }
            for block in &mut acc.tool_uses {
                if let Content::ToolUse { id, correlation, .. } = block {
                    *correlation = activity.for_call(id);
                }
            }

            // §3 strict block order: redacted-thinking → signed-thinking → text → tool_use.
            // (Unsigned thinking blocks are collected but filtered out here.)
            let mut assistant_content: Vec<Content> = Vec::new();
            for block in acc.redacted_thinking_blocks.drain(..) {
                assistant_content.push(block);
            }
            for block in acc.thinking_blocks.drain(..) {
                // Skip unsigned thinking blocks (§1.4: "skip unsigned, `:829`").
                if matches!(&block, Content::Thinking { signature: Some(_), .. }) {
                    assistant_content.push(block);
                }
            }

            // §PRIVACY withhold-on-interrupt: if the turn was interrupted before
            // the AgentResponse hook ran, the raw buffered text must NOT be
            // surfaced.  Replace it with a notice so history stays consistent.
            let text_to_store = if cancel.is_cancelled() && !acc.text.is_empty() {
                // Emit a warning so callers can observe the withholding.
                sink.emit(AgentEvent::Warning {
                    message: "[response withheld — turn was interrupted before it completed]"
                        .into(),
                });
                String::new()
            } else {
                acc.text.clone()
            };

            if !text_to_store.is_empty() {
                assistant_content.push(Content::Text(text_to_store.clone()));
            }
            for block in acc.tool_uses.drain(..) {
                assistant_content.push(block);
            }

            history.push(Message::new(Role::Assistant, assistant_content));

            // §8 item 28: persist after assistant turn commit (seam; no-op here).

            // 9. AgentResponse hook (fires after the text is settled, before the
            //    stop-decision ladder).  Only fires on non-interrupted, tool-free
            //    turns — the response is only final when the model stopped naturally
            //    and there are no pending tool calls.
            //    Fail-open: a broken or timed-out hook leaves the response unchanged.
            let _ = _pending_hook_tasks; // reserved for future hook background tasks

            // 10. Stop decision vs. tool execution.
            // Drive off PRESENCE of tool calls, not stop_reason (§8 item 2).
            let tool_uses_in_history: Vec<Content> = history
                .last()
                .iter()
                .flat_map(|m| m.content.iter())
                .filter(|b| matches!(b, Content::ToolUse { .. }))
                .cloned()
                .collect();

            if tool_uses_in_history.is_empty() {
                // AgentResponse hook: fires for completed text turns only.
                if !cancel.is_cancelled() {
                    if let Some(hr) = &self.hook_runner {
                        if hr.has_agent_response {
                            let original_text = last_assistant_text(history);
                            let ar = hr
                                .run_agent_response(
                                    &original_text,
                                    acc.stop_reason.as_deref(),
                                    cancel.clone(),
                                )
                                .await;
                            // Apply mutation: modifiedResponse enters history; displayContent is
                            // what the user sees. C# priority: displayContent ?? modifiedResponse.
                            let has_change = ar.modified_response.is_some()
                                || ar.display_content.is_some();
                            if has_change {
                                if let Some(ref mr) = ar.modified_response {
                                    replace_last_assistant_text(history, mr);
                                }
                                let display = ar
                                    .display_content
                                    .clone()
                                    .or_else(|| ar.modified_response.clone())
                                    .unwrap_or_default();
                                sink.emit(AgentEvent::ResponseRewritten {
                                    hook_command: ar
                                        .by_hook_command
                                        .clone()
                                        .unwrap_or_default(),
                                    original_response: original_text,
                                    display_content: display,
                                    modified_response: ar.modified_response.clone(),
                                });
                            }
                        }
                    }
                }

                // No tool calls → stop-decision ladder (§1.5).
                let recent_text = last_assistant_text(history);

                let action = decide_stop(
                    acc.stop_reason.as_deref(),
                    &recent_text,
                    &mut goal,
                    &mut stop_continuations,
                    self.steering.as_deref(),
                    sink,
                    cancel.clone(),
                    self.user_question.as_deref(),
                )
                .await
                .map_err(|_| AgentError::Cancelled)?;

                match action {
                    StopAction::Continue { nudge } => {
                        if !nudge.is_empty() {
                            history.push(Message::user(nudge));
                        }
                        continue;
                    }
                    StopAction::Stop => {
                        // Emit Stop + optional LimitReached.
                        sink.emit(AgentEvent::Stop {
                            stop_reason: acc.stop_reason.clone(),
                        });
                        if acc.stop_reason.as_deref() == Some("max_tokens") {
                            sink.emit(AgentEvent::LimitReached {
                                kind: "max_tokens".into(),
                                message: "The response was truncated (max_tokens reached).".into(),
                            });
                        }
                        return Ok(goal_status(&goal));
                    }
                }
            } else {
                // Tool calls present → run them serially (§1.6).
                let batch_ctx = BatchContext {
                    tools: &*self.tools,
                    permission_prompt: &*self.permission_prompt,
                    permission_mode: self.permission_mode,
                    permission_mode_state: self.permission_mode_state.as_deref(),
                    steering: self.steering.as_deref(),
                    working_directory: &self.working_directory,
                    granted_directories: self.granted_directories.as_ref(),
                    tool_max_duration: self.tool_max_duration,
                    tool_progress_interval: self.tool_progress_interval,
                    // Service handles forwarded into every ToolContext (CRITICAL 1).
                    todos: self.todos.clone(),
                    tool_user_question: self.tool_user_question.clone(),
                    plan_approver: self.plan_approver.clone(),
                    lsp_manager: self.lsp_manager.clone(),
                    task_manager: self.task_manager.clone(),
                    schedule_store: self.schedule_store.clone(),
                    caller_task_id: self.caller_task_id.clone(),
                    subagent_factory: self.subagent_factory.clone(),
                };

                let ToolBatchResult { result_blocks, abort_reason } =
                    run_tools(&tool_uses_in_history, &activity, sink, &batch_ctx, cancel.clone())
                        .await?;

                history.push(Message::new(Role::User, result_blocks));

                // §8 item 28: persist after tool results (seam; no-op here).

                if let Some(reason) = abort_reason {
                    // PreToolUse hook returned continue:false (§1.5).
                    sink.emit(AgentEvent::Stop {
                        stop_reason: Some("hook_abort".into()),
                    });
                    sink.emit(AgentEvent::LimitReached {
                        kind: "hook_abort".into(),
                        message: format!("A hook stopped the run: {reason}"),
                    });
                    return Ok(goal_status(&goal));
                }
                // Loop continues for the next sampling iteration.
            }
        }

        // Non-goal path only: max_iterations hit (§8 item 25).
        history.push(Message::assistant(
            "(stopped: reached the maximum tool iterations)",
        ));
        sink.emit(AgentEvent::LimitReached {
            kind: "max_tool_iterations".into(),
            message: format!(
                "Reached the maximum of {} tool iterations.",
                self.max_iterations
            ),
        });
        Ok(goal_status(&goal))
    }
}

fn goal_status(goal: &Option<GoalSupervisor>) -> GoalStatus {
    goal.as_ref().map(|g| g.status()).unwrap_or_else(GoalStatus::none)
}

/// Replace the `Text` block in the last assistant message with `new_text`.
/// Matches C# `ReplaceLastAssistantText`.
fn replace_last_assistant_text(history: &mut Vec<coda_llm::Message>, new_text: &str) {
    if let Some(last) = history.last_mut() {
        if last.role == coda_llm::Role::Assistant {
            let mut found = false;
            for block in &mut last.content {
                if let coda_llm::Content::Text(t) = block {
                    *t = new_text.to_owned();
                    found = true;
                    break;
                }
            }
            // If no Text block yet, add one.
            if !found {
                last.content.push(coda_llm::Content::Text(new_text.to_owned()));
            }
        }
    }
}

/// Runs compaction on `history` in-place: fires PreCompact hook (if configured),
/// calls the compaction service, and fires PostCompact hook (if configured).
///
/// Returns `Ok(true)` when history was successfully compacted, `Ok(false)` when
/// a hook blocked compaction, and `Err` when compaction failed.  The original
/// history is preserved intact on failure — no silent data loss.
async fn compact_history(
    svc: &CompactionService,
    hook_runner: Option<&crate::hooks::HookRunner>,
    history: &mut Vec<Message>,
    sink: &dyn AgentSink,
    trigger: &str,
    cancel: CancellationToken,
) -> anyhow::Result<bool> {
    // Pre-compact hook.
    let instructions_override = if let Some(hr) = hook_runner {
        if hr.has_pre_compact {
            let pre = hr
                .run_pre_compact(
                    trigger,
                    TokenEstimator::estimate(history),
                    history.len(),
                    None,
                    cancel.clone(),
                )
                .await;
            if pre.cancel {
                sink.emit(AgentEvent::CompactionCancelled {
                    hook_command: pre.by_hook_command.unwrap_or_default(),
                    trigger: trigger.to_owned(),
                });
                return Ok(false);
            }
            pre.instructions_override
        } else {
            None
        }
    } else {
        None
    };

    let tokens_before = TokenEstimator::estimate(history);
    let (new_history, summary) = svc
        .compact(history, instructions_override.as_deref(), cancel.clone())
        .await;

    // If the summariser returned nothing, history is unchanged — fail gracefully.
    if summary.is_none() {
        return Err(anyhow::anyhow!("compaction returned no summary"));
    }

    *history = new_history;

    // Post-compact hook.
    if let Some(hr) = hook_runner {
        if hr.has_post_compact {
            let post = hr
                .run_post_compact(
                    tokens_before,
                    TokenEstimator::estimate(history),
                    history.len(),
                    summary.as_deref().unwrap_or(""),
                    cancel.clone(),
                )
                .await;
            if let Some(ctx) = post.additional_context {
                history.push(Message::user(ctx.clone()));
                sink.emit(AgentEvent::PostCompactContextInjected { context: ctx });
            }
        }
    }

    Ok(true)
}

/// Builder for [`AgentLoop`].
///
/// Only `client`, `permission_prompt`, and `tools` are mandatory; everything
/// else has a sensible default.  The builder compiles to nothing at runtime —
/// it is a pure constructor pattern.
pub struct AgentLoopBuilder {
    client: Arc<dyn coda_llm::LlmClient>,
    permission_prompt: Arc<dyn PermissionPrompt>,
    tools: Arc<ToolRegistry>,
    quarantine: Arc<ToolQuarantine>,
    permission_mode: PermissionMode,
    permission_mode_state: Option<Arc<PermissionModeState>>,
    steering: Option<Arc<SteeringInbox>>,
    user_question: Option<Arc<dyn UserQuestionPrompt>>,
    hook_runner: Option<Arc<crate::hooks::HookRunner>>,
    compaction_service: Option<Arc<CompactionService>>,
    compaction_policy: CompactionPolicy,
    // Service handles for ToolContext (see AgentLoop fields for WHY).
    todos: Option<Arc<TodoStore>>,
    tool_user_question: Option<Arc<dyn UserQuestion>>,
    plan_approver: Option<Arc<dyn PlanApprover>>,
    lsp_manager: Option<Arc<LspServerManager>>,
    task_manager: Option<Arc<TaskManager>>,
    schedule_store: Option<Arc<ScheduledTaskStore>>,
    caller_task_id: Option<String>,
    subagent_factory: Option<Arc<dyn SubagentFactory>>,
    model: String,
    system_prompt: Option<String>,
    max_tokens: u32,
    max_iterations: usize,
    effort: Option<Effort>,
    working_directory: String,
    granted_directories: Option<std::collections::HashSet<String>>,
    tool_max_duration: Option<Duration>,
    tool_progress_interval: Duration,
    max_transport_retries: u32,
    max_schema_evictions: u32,
    max_stop_continuations: u32,
}

impl AgentLoopBuilder {
    pub fn new(
        client: Arc<dyn coda_llm::LlmClient>,
        permission_prompt: Arc<dyn PermissionPrompt>,
        tools: Arc<ToolRegistry>,
    ) -> Self {
        Self {
            client,
            permission_prompt,
            tools,
            quarantine: Arc::new(ToolQuarantine::new()),
            permission_mode: PermissionMode::Default,
            permission_mode_state: None,
            steering: None,
            user_question: None,
            hook_runner: None,
            compaction_service: None,
            compaction_policy: CompactionPolicy::default(),
            todos: None,
            tool_user_question: None,
            plan_approver: None,
            lsp_manager: None,
            task_manager: None,
            schedule_store: None,
            caller_task_id: None,
            subagent_factory: None,
            model: "claude-opus-4-5".into(),
            system_prompt: None,
            max_tokens: 4096,
            max_iterations: 20,
            effort: None,
            working_directory: String::new(),
            granted_directories: None,
            tool_max_duration: Some(Duration::from_secs(30 * 60)),
            tool_progress_interval: Duration::from_secs(15),
            max_transport_retries: 2,
            max_schema_evictions: 3,
            max_stop_continuations: 3,
        }
    }

    pub fn with_model(mut self, model: impl Into<String>) -> Self {
        self.model = model.into();
        self
    }

    pub fn with_compaction_service(mut self, svc: Arc<CompactionService>) -> Self {
        self.compaction_service = Some(svc);
        self
    }

    pub fn with_compaction_policy(mut self, policy: CompactionPolicy) -> Self {
        self.compaction_policy = policy;
        self
    }

    pub fn with_system_prompt(mut self, prompt: impl Into<String>) -> Self {
        self.system_prompt = Some(prompt.into());
        self
    }

    pub fn with_max_tokens(mut self, n: u32) -> Self {
        self.max_tokens = n;
        self
    }

    pub fn with_max_iterations(mut self, n: usize) -> Self {
        self.max_iterations = n;
        self
    }

    pub fn with_effort(mut self, effort: Option<Effort>) -> Self {
        self.effort = effort;
        self
    }

    pub fn with_working_directory(mut self, dir: impl Into<String>) -> Self {
        self.working_directory = dir.into();
        self
    }

    pub fn with_permission_mode(mut self, mode: PermissionMode) -> Self {
        self.permission_mode = mode;
        self
    }

    pub fn with_permission_mode_state(mut self, state: Arc<PermissionModeState>) -> Self {
        self.permission_mode_state = Some(state);
        self
    }

    pub fn with_quarantine(mut self, q: Arc<ToolQuarantine>) -> Self {
        self.quarantine = q;
        self
    }

    pub fn with_steering(mut self, s: Arc<SteeringInbox>) -> Self {
        self.steering = Some(s);
        self
    }

    pub fn with_user_question(mut self, uq: Arc<dyn UserQuestionPrompt>) -> Self {
        self.user_question = Some(uq);
        self
    }

    pub fn with_tool_max_duration(mut self, d: Option<Duration>) -> Self {
        self.tool_max_duration = d;
        self
    }

    pub fn with_max_transport_retries(mut self, n: u32) -> Self {
        self.max_transport_retries = n;
        self
    }

    pub fn with_max_schema_evictions(mut self, n: u32) -> Self {
        self.max_schema_evictions = n;
        self
    }

    pub fn with_hook_runner(mut self, hr: Arc<crate::hooks::HookRunner>) -> Self {
        self.hook_runner = Some(hr);
        self
    }

    // ── Service handle injectors (wired into ToolContext via BatchContext) ────

    pub fn with_todos(mut self, todos: Arc<TodoStore>) -> Self {
        self.todos = Some(todos);
        self
    }

    /// Multiple-choice question seam for the `ask_user_question` tool.
    /// Distinct from `with_user_question`, which is for goal-escalation prompts.
    pub fn with_tool_user_question(mut self, uq: Arc<dyn UserQuestion>) -> Self {
        self.tool_user_question = Some(uq);
        self
    }

    pub fn with_plan_approver(mut self, pa: Arc<dyn PlanApprover>) -> Self {
        self.plan_approver = Some(pa);
        self
    }

    pub fn with_lsp_manager(mut self, mgr: Arc<LspServerManager>) -> Self {
        self.lsp_manager = Some(mgr);
        self
    }

    pub fn with_task_manager(mut self, mgr: Arc<TaskManager>) -> Self {
        self.task_manager = Some(mgr);
        self
    }

    pub fn with_schedule_store(mut self, store: Arc<ScheduledTaskStore>) -> Self {
        self.schedule_store = Some(store);
        self
    }

    pub fn with_caller_task_id(mut self, task_id: impl Into<String>) -> Self {
        self.caller_task_id = Some(task_id.into());
        self
    }

    pub fn with_subagent_factory(mut self, factory: Arc<dyn SubagentFactory>) -> Self {
        self.subagent_factory = Some(factory);
        self
    }

    pub fn build(self) -> AgentLoop {
        AgentLoop {
            client: self.client,
            permission_prompt: self.permission_prompt,
            tools: self.tools,
            quarantine: self.quarantine,
            permission_mode: self.permission_mode,
            permission_mode_state: self.permission_mode_state,
            steering: self.steering,
            user_question: self.user_question,
            hook_runner: self.hook_runner,
            compaction_service: self.compaction_service,
            compaction_policy: self.compaction_policy,
            todos: self.todos,
            tool_user_question: self.tool_user_question,
            plan_approver: self.plan_approver,
            lsp_manager: self.lsp_manager,
            task_manager: self.task_manager,
            schedule_store: self.schedule_store,
            caller_task_id: self.caller_task_id,
            subagent_factory: self.subagent_factory,
            model: self.model,
            system_prompt: self.system_prompt,
            max_tokens: self.max_tokens,
            max_iterations: self.max_iterations,
            effort: self.effort,
            working_directory: self.working_directory,
            granted_directories: self.granted_directories,
            tool_max_duration: self.tool_max_duration,
            tool_progress_interval: self.tool_progress_interval,
            max_transport_retries: self.max_transport_retries,
            max_schema_evictions: self.max_schema_evictions,
            max_stop_continuations: self.max_stop_continuations,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;
    use std::sync::{Arc, Mutex};
    use std::time::Duration;

    use async_trait::async_trait;
    use coda_llm::anthropic::StreamEvent;
    use coda_llm::{Content, Correlation, LlmError, Message, Role, Usage};
    use coda_proto::events::ToolCallStatus;
    use tokio_util::sync::CancellationToken;

    use crate::events::{AgentEvent, CollectingSink, NullSink};
    use crate::permission::PermissionPrompt;
    use crate::tool::{ToolContext, ToolOutcome, ToolRegistry, ToolResult};

    // ── Mock LLM client ─────────────────────────────────────────────────────

    struct MockLlmClient {
        /// Each call to `stream` pops the next scripted sequence.
        sequences: Mutex<VecDeque<Vec<Result<StreamEvent, LlmError>>>>,
    }

    impl MockLlmClient {
        fn new(sequences: Vec<Vec<Result<StreamEvent, LlmError>>>) -> Arc<Self> {
            Arc::new(Self {
                sequences: Mutex::new(sequences.into_iter().collect()),
            })
        }
    }

    #[async_trait]
    impl coda_llm::LlmClient for MockLlmClient {
        fn provider_id(&self) -> &str {
            "mock"
        }
        async fn stream(&self, _: ChatRequest) -> Result<coda_llm::ResponseStream, LlmError> {
            let events = self
                .sequences
                .lock()
                .unwrap()
                .pop_front()
                .expect("MockLlmClient ran out of scripted sequences");
            let (tx, rx) = tokio::sync::mpsc::channel(64);
            tokio::spawn(async move {
                for ev in events {
                    let _ = tx.send(ev).await;
                }
            });
            Ok(coda_llm::ResponseStream::new(rx))
        }
    }

    // ── Mock Tool ───────────────────────────────────────────────────────────

    struct MockTool {
        tool_name: &'static str,
        read_only: bool,
        results: Mutex<VecDeque<ToolResult>>,
        /// Records (call_index, name) when a tool is executed.
        call_log: Arc<Mutex<Vec<String>>>,
        /// Optional delay to simulate slow tools.
        delay: Option<Duration>,
    }

    impl MockTool {
        fn new(name: &'static str, read_only: bool, log: Arc<Mutex<Vec<String>>>) -> Arc<Self> {
            Arc::new(Self {
                tool_name: name,
                read_only,
                results: Mutex::new(VecDeque::new()),
                call_log: log,
                delay: None,
            })
        }

        #[allow(dead_code)]
        fn with_result(self: Arc<Self>, result: ToolResult) -> Arc<Self> {
            self.results.lock().unwrap().push_back(result);
            self
        }

        fn with_delay(mut self: Arc<Self>, d: Duration) -> Arc<Self> {
            Arc::get_mut(&mut self).unwrap().delay = Some(d);
            self
        }
    }

    #[async_trait]
    impl crate::tool::Tool for MockTool {
        fn name(&self) -> &str {
            self.tool_name
        }
        fn description(&self) -> &str {
            self.tool_name
        }
        fn input_schema_json(&self) -> &str {
            "{}"
        }
        fn is_read_only(&self) -> bool {
            self.read_only
        }
        async fn execute(
            &self,
            _input: &serde_json::Value,
            _ctx: &ToolContext,
            cancel: CancellationToken,
        ) -> ToolOutcome {
            self.call_log.lock().unwrap().push(self.tool_name.to_owned());
            if let Some(d) = self.delay {
                tokio::select! {
                    _ = tokio::time::sleep(d) => {}
                    _ = cancel.cancelled() => {
                        return ToolResult::error("cancelled by token");
                    }
                }
            }
            self.results
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or_else(|| ToolResult::ok("ok"))
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// Coerce `Arc<T: Tool>` to `Arc<dyn Tool>` to satisfy `ToolRegistry::new`.
    fn dyn_tool(t: Arc<impl crate::tool::Tool + 'static>) -> Arc<dyn crate::tool::Tool> { t }

    /// A permission prompt that always allows.
    struct AllowAll;
    #[async_trait]
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

    /// A permission prompt that denies the named tool(s), allows the rest.
    struct DenyNamed(Vec<&'static str>);
    #[async_trait]
    impl PermissionPrompt for DenyNamed {
        async fn request(
            &self,
            tool: &dyn crate::tool::Tool,
            _: &str,
            _: CancellationToken,
        ) -> bool {
            !self.0.contains(&tool.name())
        }
    }

    fn done() -> StreamEvent {
        StreamEvent::Done {
            stop_reason: Some("end_turn".into()),
            usage: Usage { input_tokens: 10, output_tokens: 5, ..Usage::ZERO },
        }
    }

    fn done_max_tokens() -> StreamEvent {
        StreamEvent::Done {
            stop_reason: Some("max_tokens".into()),
            usage: Usage::ZERO,
        }
    }

    fn tool_use_event(id: &str, name: &str) -> StreamEvent {
        StreamEvent::ToolUse(Content::ToolUse {
            id: id.into(),
            name: name.into(),
            input_json: "{}".into(),
            correlation: Correlation::default(),
        })
    }

    fn make_loop(
        client: Arc<dyn coda_llm::LlmClient>,
        permission_prompt: Arc<dyn PermissionPrompt>,
        tools: Arc<ToolRegistry>,
    ) -> AgentLoop {
        AgentLoopBuilder::new(client, permission_prompt, tools)
            .with_max_iterations(20)
            .with_tool_max_duration(None)
            .build()
    }

    // ── §8 item 1: tools execute serially ───────────────────────────────────

    #[tokio::test]
    async fn tools_execute_serially_in_requested_order() {
        let log = Arc::new(Mutex::new(Vec::new()));

        // Script: one turn with two tool calls, then a final text-only turn.
        let client = MockLlmClient::new(vec![
            vec![
                Ok(tool_use_event("t1", "tool_a")),
                Ok(tool_use_event("t2", "tool_b")),
                Ok(done()),
            ],
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([
            dyn_tool(MockTool::new("tool_a", true, log.clone())),
            dyn_tool(MockTool::new("tool_b", true, log.clone())),
        ]));

        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();

        let order = log.lock().unwrap().clone();
        assert_eq!(order, vec!["tool_a", "tool_b"], "tools must run in requested order");
    }

    // ── §8 item 2: loop drives off tool-call presence ───────────────────────

    #[tokio::test]
    async fn loop_continues_on_tool_calls_regardless_of_stop_reason() {
        let log = Arc::new(Mutex::new(Vec::new()));

        // Iteration 0: has tool_use + stop_reason="end_turn" (not "tool_use").
        // Iteration 1: no tool calls → natural stop.
        let client = MockLlmClient::new(vec![
            vec![
                Ok(tool_use_event("t1", "tool_a")),
                // stop_reason = "end_turn" (NOT "tool_use") — loop must still execute tools.
                Ok(StreamEvent::Done {
                    stop_reason: Some("end_turn".into()),
                    usage: Usage::ZERO,
                }),
            ],
            vec![Ok(StreamEvent::TextDelta("all done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([dyn_tool(MockTool::new("tool_a", true, log.clone()))]));
        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();

        assert_eq!(log.lock().unwrap().clone(), vec!["tool_a"], "tool must execute despite end_turn stop_reason");
    }

    // ── §8 item 3: assistant block order ────────────────────────────────────

    #[tokio::test]
    async fn assistant_block_order_redacted_signed_text_tool() {
        let signed = Content::Thinking {
            text: "thinking".into(),
            signature: Some("sig".into()),
        };
        let unsigned = Content::Thinking { text: "unsigned".into(), signature: None };
        let redacted = Content::RedactedThinking { data: "opaque".into() };

        let log = Arc::new(Mutex::new(Vec::new()));

        let client = MockLlmClient::new(vec![
            vec![
                // Redacted thinking
                Ok(StreamEvent::ThinkingDone(redacted.clone())),
                // Signed thinking
                Ok(StreamEvent::ThinkingDelta("thinking".into())),
                Ok(StreamEvent::ThinkingDone(signed.clone())),
                // Unsigned thinking — must be skipped in history
                Ok(StreamEvent::ThinkingDelta("unsigned".into())),
                Ok(StreamEvent::ThinkingDone(unsigned)),
                // Text
                Ok(StreamEvent::TextDelta("answer".into())),
                // Tool use
                Ok(tool_use_event("t1", "tool_a")),
                Ok(done()),
            ],
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([dyn_tool(MockTool::new("tool_a", true, log.clone()))]));
        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();

        // Find the first assistant message (after the initial user message).
        let assistant_msg = history.iter().find(|m| m.role == Role::Assistant).unwrap();
        let content = &assistant_msg.content;

        assert!(
            matches!(&content[0], Content::RedactedThinking { .. }),
            "block 0 must be RedactedThinking"
        );
        assert!(
            matches!(&content[1], Content::Thinking { signature: Some(_), .. }),
            "block 1 must be signed Thinking"
        );
        assert!(
            matches!(&content[2], Content::Text(_)),
            "block 2 must be Text"
        );
        assert!(
            matches!(&content[3], Content::ToolUse { .. }),
            "block 3 must be ToolUse"
        );
        assert_eq!(content.len(), 4, "unsigned thinking must be filtered out");
    }

    // ── §8 item 4: correlation stamping ─────────────────────────────────────

    #[tokio::test]
    async fn tool_use_blocks_are_correlation_stamped() {
        let log = Arc::new(Mutex::new(Vec::new()));

        let client = MockLlmClient::new(vec![
            vec![Ok(tool_use_event("call-1", "tool_a")), Ok(done())],
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([dyn_tool(MockTool::new("tool_a", true, log))]));
        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();

        let assistant_msg = history.iter().find(|m| m.role == Role::Assistant).unwrap();
        let has_stamped = assistant_msg.content.iter().any(|b| {
            matches!(b, Content::ToolUse { correlation, .. } if !correlation.is_empty())
        });
        assert!(has_stamped, "tool_use blocks must have correlation IDs stamped");
    }

    // ── §8 item 5: transport retry only when nothing emitted ─────────────────

    #[tokio::test]
    async fn transport_retry_fires_when_nothing_emitted() {
        let sink = CollectingSink::new();

        // First call: transport error before anything emitted → retry.
        // Second call: succeeds with text.
        let client = MockLlmClient::new(vec![
            vec![Err(LlmError::Transport("reset".into()))],
            vec![
                Ok(StreamEvent::TextDelta("hello".into())),
                Ok(done()),
            ],
        ]);

        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_max_transport_retries(2)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("hi")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        let events = sink.take();
        let text_events: Vec<&str> = events
            .iter()
            .filter_map(|e| {
                if let AgentEvent::AssistantText { delta } = e { Some(delta.as_str()) } else { None }
            })
            .collect();
        // Exactly one "hello" — no duplicate from retry.
        assert_eq!(text_events, vec!["hello"], "text must appear exactly once after transport retry");
    }

    #[tokio::test]
    async fn no_transport_retry_after_text_emitted() {
        let sink = CollectingSink::new();

        // First call: text then transport error → mid-stream failure, no retry.
        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("partial".into())),
            Err(LlmError::Transport("mid-stream reset".into())),
        ]]);

        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_max_transport_retries(2)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("hi")];
        let result = agent.run(&mut history, &sink, None, CancellationToken::new()).await;

        assert!(result.is_err(), "mid-stream error after text must propagate");
        let text_events: Vec<_> = sink
            .take()
            .into_iter()
            .filter(|e| matches!(e, AgentEvent::AssistantText { .. }))
            .collect();
        assert_eq!(text_events.len(), 1, "text must appear exactly once — no retry after text emitted");
    }

    // ── §8 item 7: schema eviction ───────────────────────────────────────────

    #[tokio::test]
    async fn schema_eviction_quarantines_offending_tool_only() {
        let quarantine = Arc::new(ToolQuarantine::new());

        // First call: 400 with body naming 'bad_tool'.
        // Second call (after eviction): succeeds.
        let client = MockLlmClient::new(vec![
            vec![Err(LlmError::Api {
                status: 400,
                message: "bad schema".into(),
                kind: coda_llm::FailureKind::Permanent,
                retry_after: None,
                body: Some("Invalid schema for tool 'bad_tool': x".into()),
            })],
            vec![Ok(StreamEvent::TextDelta("ok".into())), Ok(done())],
        ]);

        let log = Arc::new(Mutex::new(Vec::<String>::new()));
        let tools = Arc::new(ToolRegistry::new([
            dyn_tool(MockTool::new("good_tool", true, log.clone())),
            dyn_tool(MockTool::new("bad_tool", true, log.clone())),
        ]));

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_quarantine(quarantine.clone())
            .with_max_schema_evictions(3)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();

        assert!(quarantine.is_quarantined("bad_tool"), "bad_tool must be quarantined");
        assert!(!quarantine.is_quarantined("good_tool"), "good_tool must survive");
    }

    // ── §8 item 8: cancellation and ceiling ──────────────────────────────────

    #[tokio::test]
    async fn caller_cancel_propagates_and_unwinds_turn() {
        let cancel = CancellationToken::new();
        let cancel_clone = cancel.clone();

        // Tool that cancels the caller token when it starts.
        struct CancellingTool(CancellationToken);
        #[async_trait]
        impl crate::tool::Tool for CancellingTool {
            fn name(&self) -> &str { "cancel_tool" }
            fn description(&self) -> &str { "cancels" }
            fn input_schema_json(&self) -> &str { "{}" }
            fn is_read_only(&self) -> bool { true }
            async fn execute(
                &self,
                _: &serde_json::Value,
                _: &ToolContext,
                _cancel: CancellationToken,
            ) -> ToolOutcome {
                self.0.cancel(); // cancel the OUTER caller token
                ToolResult::ok("done")
            }
        }

        let client = MockLlmClient::new(vec![vec![
            Ok(tool_use_event("t1", "cancel_tool")),
            Ok(done()),
        ]]);

        let tools = Arc::new(ToolRegistry::new([Arc::new(CancellingTool(cancel_clone))
            as Arc<dyn crate::tool::Tool>]));

        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        let result = agent.run(&mut history, &NullSink, None, cancel).await;

        assert!(
            matches!(result, Err(AgentError::Cancelled)),
            "caller cancel must propagate, got: {result:?}"
        );
    }

    #[tokio::test]
    async fn tool_ceiling_produces_error_result_session_survives() {
        let log = Arc::new(Mutex::new(Vec::new()));

        // Tool that sleeps longer than the ceiling.
        let slow_tool = MockTool::new("slow_tool", true, log.clone())
            .with_delay(Duration::from_millis(500));

        let client = MockLlmClient::new(vec![
            vec![Ok(tool_use_event("t1", "slow_tool")), Ok(done())],
            vec![Ok(StreamEvent::TextDelta("session survived".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([slow_tool as Arc<dyn crate::tool::Tool>]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            // Very short ceiling: 10ms < 500ms tool sleep.
            .with_tool_max_duration(Some(Duration::from_millis(10)))
            .build();

        let mut history = vec![Message::user("go")];
        let result = agent.run(&mut history, &NullSink, None, CancellationToken::new()).await;

        assert!(result.is_ok(), "session must survive a ceiling timeout");
        // The tool result in history should be an error.
        let tool_result_msg = history.iter().find(|m| {
            m.role == Role::User
                && m.content
                    .iter()
                    .any(|b| matches!(b, Content::ToolResult { is_error: true, .. }))
        });
        assert!(tool_result_msg.is_some(), "history must contain the ceiling error result");
    }

    // ── §8 item 15: denied tool yields error, batch continues ───────────────

    #[tokio::test]
    async fn denied_tool_yields_error_result_and_batch_continues() {
        let log = Arc::new(Mutex::new(Vec::new()));
        let sink = CollectingSink::new();

        // Three tools: A (read-only, allowed), B (writable, denied), C (read-only, allowed).
        let client = MockLlmClient::new(vec![
            vec![
                Ok(tool_use_event("t1", "tool_a")),
                Ok(tool_use_event("t2", "tool_b")),
                Ok(tool_use_event("t3", "tool_c")),
                Ok(done()),
            ],
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([
            dyn_tool(MockTool::new("tool_a", true, log.clone())),
            dyn_tool(MockTool::new("tool_b", false, log.clone())), // writable → goes through permission
            dyn_tool(MockTool::new("tool_c", true, log.clone())),
        ]));

        // Deny only tool_b.
        let agent = AgentLoopBuilder::new(
            client,
            Arc::new(DenyNamed(vec!["tool_b"])),
            tools,
        )
        .with_tool_max_duration(None)
        .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        // tool_a and tool_c must have executed.
        let executed = log.lock().unwrap().clone();
        assert!(executed.contains(&"tool_a".to_owned()), "tool_a must execute");
        assert!(executed.contains(&"tool_c".to_owned()), "tool_c must execute");

        // tool_b must have a denied result.
        let events = sink.take();
        let denied = events.iter().any(|e| {
            matches!(e, AgentEvent::ToolResult { tool_name, is_error: true, .. } if tool_name == "tool_b")
        });
        assert!(denied, "tool_b must have a denial error result");
    }

    // ── §8 item 25: max_iterations soft stop ─────────────────────────────────

    #[tokio::test]
    async fn max_iterations_emits_limit_reached_and_closing_message() {
        let log = Arc::new(Mutex::new(Vec::new()));
        let sink = CollectingSink::new();

        // LLM always returns a tool call; loop should stop after max_iterations.
        let client = MockLlmClient::new(vec![
            vec![Ok(tool_use_event("t1", "tool_a")), Ok(done())],
            vec![Ok(tool_use_event("t1", "tool_a")), Ok(done())],
            vec![Ok(tool_use_event("t1", "tool_a")), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([dyn_tool(MockTool::new("tool_a", true, log))]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_max_iterations(2)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        let events = sink.take();
        let limit = events.iter().any(|e| {
            matches!(e, AgentEvent::LimitReached { kind, .. } if kind == "max_tool_iterations")
        });
        assert!(limit, "max_tool_iterations LimitReached must be emitted");

        let closing_msg = history.iter().rev().find(|m| m.role == Role::Assistant);
        assert!(
            closing_msg
                .map(|m| m.text().contains("maximum tool iterations"))
                .unwrap_or(false),
            "closing assistant message must mention maximum tool iterations"
        );
    }

    // ── §8 item 26: max_tokens emits LimitReached ────────────────────────────

    #[tokio::test]
    async fn max_tokens_stop_emits_limit_reached() {
        let sink = CollectingSink::new();

        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("truncated".into())),
            Ok(done_max_tokens()),
        ]]);

        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        let events = sink.take();
        let limit = events.iter().any(|e| {
            matches!(e, AgentEvent::LimitReached { kind, .. } if kind == "max_tokens")
        });
        assert!(limit, "max_tokens LimitReached must be emitted");
    }

    // ── §8 item 27: pending hook tasks drained on every exit path ───────────
    //
    // With no hooks wired, pending_hook_tasks is always empty.  The previous
    // test only verified the loop returned Ok without panicking — that asserts
    // nothing about the drain logic (deleting the drain loop would not break
    // it).  Deleted; the drain path is covered by the cancellation tests below,
    // which exercise all exit paths.

    // ── §8 item 22: steering injection in iteration ──────────────────────────

    #[tokio::test]
    async fn steering_is_injected_into_history() {
        let steering = Arc::new(SteeringInbox::new());
        let sink = CollectingSink::new();

        // Pre-queue a steering message before the loop starts.
        steering.enqueue("pivot your approach").unwrap();

        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("done".into())),
            Ok(done()),
        ]]);

        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_steering(steering)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        // Steering text must appear as a User message before the assistant response.
        let steering_msg = history
            .iter()
            .find(|m| m.role == Role::User && m.text().contains("pivot your approach"));
        assert!(steering_msg.is_some(), "steering message must be injected into history");

        let events = sink.take();
        let delivered = events.iter().any(|e| matches!(e, AgentEvent::SteeringDelivered { .. }));
        assert!(delivered, "SteeringDelivered event must be emitted");
    }

    // ── §8 item 23: steering mid-batch skips remaining tools ─────────────────
    //
    // When a steering message arrives BETWEEN tools in a batch, the tool
    // that triggered the steering check and all subsequent tools are marked
    // Skipped.  We test this by having tool_a enqueue a message during its
    // execution, which causes tool_b to be skipped before it starts.

    #[tokio::test]
    async fn steering_mid_batch_skips_remaining_tools() {
        let steering = Arc::new(SteeringInbox::new());
        let log = Arc::new(Mutex::new(Vec::<String>::new()));
        let sink = CollectingSink::new();

        // A tool that enqueues a steering message when it executes.
        struct SteeringTool {
            name: &'static str,
            inbox: Arc<SteeringInbox>,
            log: Arc<Mutex<Vec<String>>>,
        }
        #[async_trait]
        impl crate::tool::Tool for SteeringTool {
            fn name(&self) -> &str { self.name }
            fn description(&self) -> &str { self.name }
            fn input_schema_json(&self) -> &str { "{}" }
            fn is_read_only(&self) -> bool { true }
            async fn execute(&self, _: &serde_json::Value, _: &ToolContext, _: CancellationToken) -> ToolOutcome {
                self.log.lock().unwrap().push(self.name.to_owned());
                self.inbox.enqueue("operator steering arrived mid-batch").unwrap();
                ToolResult::ok("done")
            }
        }

        let client = MockLlmClient::new(vec![
            vec![
                Ok(tool_use_event("t1", "tool_a")),
                Ok(tool_use_event("t2", "tool_b")),
                Ok(done()),
            ],
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([
            Arc::new(SteeringTool { name: "tool_a", inbox: steering.clone(), log: log.clone() })
                as Arc<dyn crate::tool::Tool>,
            dyn_tool(MockTool::new("tool_b", true, log.clone())),
        ]));

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_steering(steering)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        let executed = log.lock().unwrap().clone();
        // tool_a ran (and enqueued steering); tool_b must be skipped.
        assert!(executed.contains(&"tool_a".to_owned()), "tool_a must have executed");
        assert!(!executed.contains(&"tool_b".to_owned()), "tool_b must be skipped by steering preempt");

        let events = sink.take();
        let skipped = events.iter().filter(|e| {
            matches!(e, AgentEvent::ToolResult { status: ToolCallStatus::Skipped, .. })
        }).count();
        assert_eq!(skipped, 1, "tool_b should have exactly one Skipped result");
    }

    // ── CRITICAL 1: cancellation tests ──────────────────────────────────────
    //
    // Three defects, one root cause:
    //  (a) no cancel check at the top of each execute_loop iteration,
    //  (b) drive_stream has no cancel arm — cancel during streaming is ignored,
    //  (c) LlmError::Cancelled is mapped to AgentError::Llm(Cancelled) not
    //      AgentError::Cancelled, so callers matching the latter miss it.

    // (b) cancel DURING a text-only turn (stream never completes).
    // Before fix: drive_stream blocks on stream.next() forever — the tokio::time::timeout
    // in the test fires and panics rather than returning Cancelled.
    // After fix: the select!() cancel arm drops the stream and returns Cancelled.
    #[tokio::test]
    async fn cancel_during_text_turn_yields_cancelled() {
        let cancel = CancellationToken::new();
        let cancel_for_spawn = cancel.clone();

        // A client whose stream sends one delta and then hangs indefinitely
        // (no Done event).  Without a cancel arm in drive_stream the loop
        // blocks on stream.next() until the test timeout fires.
        struct HangingClient;
        #[async_trait]
        impl coda_llm::LlmClient for HangingClient {
            fn provider_id(&self) -> &str { "mock" }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                let (tx, rx) = tokio::sync::mpsc::channel(64);
                tokio::spawn(async move {
                    let _ = tx.send(Ok(StreamEvent::TextDelta("partial".into()))).await;
                    // Block forever so the stream never completes.
                    std::future::pending::<()>().await
                });
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }

        // Cancel after a brief pause to let the stream start.
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(50)).await;
            cancel_for_spawn.cancel();
        });

        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = AgentLoopBuilder::new(Arc::new(HangingClient), Arc::new(AllowAll), tools)
            .with_tool_max_duration(None)
            .build();
        let mut history = vec![Message::user("go")];

        // If drive_stream has no cancel arm the agent hangs; the 2 s wall-clock
        // ceiling turns the hang into a panic so the test fails clearly.
        let result = tokio::time::timeout(
            Duration::from_secs(2),
            agent.run(&mut history, &NullSink, None, cancel),
        )
        .await
        .expect("agent hung instead of observing the cancel token — drive_stream needs a cancel select arm");

        assert!(
            matches!(result, Err(AgentError::Cancelled)),
            "cancel during a text-only turn must yield Cancelled, got: {result:?}"
        );
    }

    // (c) pre-cancelled token returns the wrong variant.
    // Before fix: stream_with_retries returns LlmError::Cancelled, mapped by
    //   map_err(AgentError::Llm) to AgentError::Llm(LlmError::Cancelled).
    //   The test checks AgentError::Cancelled → fails.
    // After fix (c) or (a): AgentError::Cancelled → passes.
    #[tokio::test]
    async fn pre_cancelled_token_yields_cancelled() {
        let cancel = CancellationToken::new();
        cancel.cancel(); // cancel before the run starts

        // The model must never be called; MockLlmClient with empty sequences
        // panics if stream() is called.
        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = AgentLoopBuilder::new(
            MockLlmClient::new(vec![]),
            Arc::new(AllowAll),
            tools,
        )
        .with_tool_max_duration(None)
        .build();

        let mut history = vec![Message::user("go")];
        let result = agent.run(&mut history, &NullSink, None, cancel).await;

        assert!(
            matches!(result, Err(AgentError::Cancelled)),
            "pre-cancelled token must yield AgentError::Cancelled, got: {result:?}"
        );
    }

    // (a) cancel between iterations (after a tool batch, before the next model call).
    // Before fix: the cancel is caught by stream_with_retries (LlmError::Cancelled)
    //   then mapped to AgentError::Llm(Cancelled) — wrong variant — test fails.
    // After fix (a) or (c): correctly yields AgentError::Cancelled.
    #[tokio::test]
    async fn cancel_between_iterations_yields_cancelled() {
        let cancel = CancellationToken::new();
        let cancel_for_tool = cancel.clone();

        // A tool that fires the outer cancel token when it executes, so the
        // cancel lands AFTER the first tool batch but BEFORE the second model call.
        struct CancelTool(CancellationToken);
        #[async_trait]
        impl crate::tool::Tool for CancelTool {
            fn name(&self) -> &str { "cancel_tool" }
            fn description(&self) -> &str { "fires outer cancel" }
            fn input_schema_json(&self) -> &str { "{}" }
            fn is_read_only(&self) -> bool { true }
            async fn execute(
                &self,
                _: &serde_json::Value,
                _: &ToolContext,
                _: CancellationToken,
            ) -> ToolOutcome {
                self.0.cancel();
                ToolResult::ok("done")
            }
        }

        let client = MockLlmClient::new(vec![
            // Iteration 0: tool call; tool fires cancel.
            vec![Ok(tool_use_event("t1", "cancel_tool")), Ok(done())],
            // Iteration 1: must never be reached.
            vec![Ok(StreamEvent::TextDelta("unreachable".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([
            Arc::new(CancelTool(cancel_for_tool)) as Arc<dyn crate::tool::Tool>,
        ]));
        let agent = make_loop(client, Arc::new(AllowAll), tools);
        let mut history = vec![Message::user("go")];
        let result = agent.run(&mut history, &NullSink, None, cancel).await;

        assert!(
            matches!(result, Err(AgentError::Cancelled)),
            "cancel between iterations must yield Cancelled, got: {result:?}"
        );
    }

    // ── IMPORTANT 2: goal-stop falls through to steering seal ───────────────
    //
    // When a goal is active, the original code `return`ed from the goal path
    // before running try_seal_empty().  A steering message that raced the goal
    // completion was accepted by enqueue (inbox unsealed) but never delivered.
    //
    // Fix: fall through to the seal check for BOTH goal and no-goal stop paths.
    #[tokio::test]
    async fn goal_stop_with_racing_message_delivers_steering() {
        use crate::goal::{ForkedAgent, GoalBudget, GoalRetryPolicy, GoalSupervisor};
        use coda_llm::Message as LlmMessage;

        let steering = Arc::new(SteeringInbox::new());
        let sink = CollectingSink::new();

        // A judge that also enqueues a steering message on every call, simulating
        // a concurrent operator message that races the goal completing.
        struct EnqueueOnDoneJudge {
            inbox: Arc<SteeringInbox>,
            enqueued: std::sync::atomic::AtomicBool,
        }
        #[async_trait]
        impl ForkedAgent for EnqueueOnDoneJudge {
            async fn run(
                &self,
                _: &str,
                _: Vec<LlmMessage>,
                _: CancellationToken,
            ) -> anyhow::Result<String> {
                // Enqueue once, simulating the race: an operator message arrives
                // while the goal judge is deciding to stop.
                if !self.enqueued.swap(true, std::sync::atomic::Ordering::SeqCst) {
                    let _ = self.inbox.enqueue("racing steering after goal");
                }
                Ok("DONE".to_owned())
            }
        }

        let client = MockLlmClient::new(vec![
            // Iteration 0: text-only turn (no tools) → triggers goal stop decision.
            vec![Ok(StreamEvent::TextDelta("goal achieved".into())), Ok(done())],
            // Iteration 1: delivered after steering is picked up.
            vec![Ok(StreamEvent::TextDelta("continuing after steering".into())), Ok(done())],
        ]);

        let goal = GoalSupervisor::new(
            Box::new(EnqueueOnDoneJudge {
                inbox: steering.clone(),
                enqueued: std::sync::atomic::AtomicBool::new(false),
            }),
            "test goal",
            GoalBudget::new(Duration::MAX, 5, 0.5, || Duration::ZERO),
            Some(GoalRetryPolicy::for_tests()),
        );

        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_steering(steering.clone())
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, Some(goal), CancellationToken::new()).await.unwrap();

        // Without fix: goal path returns early before try_seal_empty; the racing
        // message is never delivered.
        // With fix: seal check runs, inbox not empty → StopAction::Continue →
        //   message delivered in the next iteration.
        let steering_in_history = history.iter().any(|m| {
            m.role == Role::User && m.text().contains("racing steering after goal")
        });
        assert!(
            steering_in_history,
            "a steering message that races goal completion must be delivered before the loop exits"
        );

        let events = sink.take();
        assert!(
            events.iter().any(|e| matches!(e, AgentEvent::SteeringDelivered { .. })),
            "SteeringDelivered event must be emitted"
        );
    }

    // ── MINOR 3: tool ceiling cancels the tool's own token ───────────────────
    //
    // execute_with_ceiling drops the tool future on timeout but never calls
    // tool_cancel.cancel(), so background tasks the tool spawned (holding
    // a clone of tool_cancel) never learn they were killed.
    #[tokio::test]
    async fn tool_ceiling_cancels_tool_token() {
        use std::sync::atomic::{AtomicBool, Ordering};

        let cancel_observed = Arc::new(AtomicBool::new(false));
        let co_clone = cancel_observed.clone();

        // A tool that spawns a background task holding a clone of its token.
        // The task blocks until the token fires, then sets the flag.
        struct BackgroundWorkTool {
            cancel_observed: Arc<AtomicBool>,
        }
        #[async_trait]
        impl crate::tool::Tool for BackgroundWorkTool {
            fn name(&self) -> &str { "bg_tool" }
            fn description(&self) -> &str { "spawns background work" }
            fn input_schema_json(&self) -> &str { "{}" }
            fn is_read_only(&self) -> bool { true }
            async fn execute(
                &self,
                _: &serde_json::Value,
                _: &ToolContext,
                cancel: CancellationToken,
            ) -> ToolOutcome {
                let observed = self.cancel_observed.clone();
                // Spawn background work that holds a copy of the tool token.
                tokio::spawn(async move {
                    cancel.cancelled().await;
                    observed.store(true, Ordering::SeqCst);
                });
                // Block forever — the ceiling will kill this future.
                std::future::pending::<ToolOutcome>().await
            }
        }

        let client = MockLlmClient::new(vec![
            vec![Ok(tool_use_event("t1", "bg_tool")), Ok(done())],
            vec![Ok(StreamEvent::TextDelta("session survived".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([
            Arc::new(BackgroundWorkTool { cancel_observed: co_clone }) as Arc<dyn crate::tool::Tool>,
        ]));
        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            // Short ceiling: fires before the tool's infinite sleep.
            .with_tool_max_duration(Some(Duration::from_millis(50)))
            .build();

        let mut history = vec![Message::user("go")];
        let result = agent.run(&mut history, &NullSink, None, CancellationToken::new()).await;
        assert!(result.is_ok(), "session must survive a ceiling timeout");

        // Give the background task a moment to react to the cancellation signal.
        tokio::time::sleep(Duration::from_millis(150)).await;

        // Without fix: tool_cancel is only dropped (not cancelled), so the
        //   background task keeps waiting → flag stays false → test fails.
        // With fix: tool_cancel_handle.cancel() is called on ceiling →
        //   background task fires → flag set → test passes.
        assert!(
            cancel_observed.load(Ordering::SeqCst),
            "the tool's child cancellation token must be cancelled when the ceiling fires"
        );
    }

    // ── MINOR 4: steering inbox is reopened at the start of each run ─────────
    //
    // A natural stop seals the inbox via try_seal_empty().  Without
    // open_for_turn() at the start of run(), a second run starts sealed and all
    // operator steering is silently dropped forever after.
    #[tokio::test]
    async fn second_run_on_same_inbox_delivers_steering() {
        let steering = Arc::new(SteeringInbox::new());
        let sink = CollectingSink::new();

        // ── Run 1 ────────────────────────────────────────────────────────────
        let client1 = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("run1".into())),
            Ok(done()),
        ]]);
        let tools1 = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent1 = AgentLoopBuilder::new(client1, Arc::new(AllowAll), tools1)
            .with_steering(steering.clone())
            .with_tool_max_duration(None)
            .build();

        let mut history1 = vec![Message::user("go")];
        agent1.run(&mut history1, &sink, None, CancellationToken::new()).await.unwrap();
        sink.take(); // discard run-1 events

        // After run 1 the inbox is sealed (try_seal_empty succeeded).
        assert!(
            steering.enqueue("just-after-run1").is_none(),
            "inbox must be sealed after a completed run"
        );

        // ── Run 2 ────────────────────────────────────────────────────────────
        // A tool that enqueues a steering message; simulates an operator message
        // that arrives AFTER open_for_turn has unsealed the inbox.
        struct EnqueueTool(Arc<SteeringInbox>);
        #[async_trait]
        impl crate::tool::Tool for EnqueueTool {
            fn name(&self) -> &str { "enqueue_tool" }
            fn description(&self) -> &str { "enqueues steering" }
            fn input_schema_json(&self) -> &str { "{}" }
            fn is_read_only(&self) -> bool { true }
            async fn execute(
                &self,
                _: &serde_json::Value,
                _: &ToolContext,
                _: CancellationToken,
            ) -> ToolOutcome {
                // Without fix: inbox is still sealed → enqueue returns None → nothing injected.
                // With fix: open_for_turn at run start reopens the inbox → enqueue succeeds.
                let _ = self.0.enqueue("steer during run2");
                ToolResult::ok("done")
            }
        }

        let client2 = MockLlmClient::new(vec![
            // Iteration 0: tool call (enqueues steering).
            vec![Ok(tool_use_event("t1", "enqueue_tool")), Ok(done())],
            // Iteration 1: steering injected at 4b, then final text.
            vec![Ok(StreamEvent::TextDelta("run2 done".into())), Ok(done())],
        ]);
        let tools2 = Arc::new(ToolRegistry::new([
            Arc::new(EnqueueTool(steering.clone())) as Arc<dyn crate::tool::Tool>,
        ]));
        let agent2 = AgentLoopBuilder::new(client2, Arc::new(AllowAll), tools2)
            .with_steering(steering.clone())
            .with_tool_max_duration(None)
            .build();

        let mut history2 = vec![Message::user("go again")];
        agent2.run(&mut history2, &sink, None, CancellationToken::new()).await.unwrap();

        let events = sink.take();
        // Without fix: enqueue in the tool returned None (inbox sealed) → no delivery.
        // With fix: inbox was reopened → steering is delivered in iteration 1.
        assert!(
            events.iter().any(|e| matches!(e, AgentEvent::SteeringDelivered { .. })),
            "steering must be delivered in the second run; inbox must be reopened by open_for_turn"
        );
        let steering_in_history = history2
            .iter()
            .any(|m| m.role == Role::User && m.text().contains("steer during run2"));
        assert!(
            steering_in_history,
            "steering message must appear in run-2 history"
        );
    }

    // ── §PRIVACY withhold-on-interrupt ─────────────────────────────────────
    //
    // If a turn is interrupted (cancel fires) AFTER text has been streamed
    // but before the AgentResponse hook ran, the raw buffered text must NOT
    // be stored in history or emitted to the sink as the canonical response.
    // It is replaced by a notice so no partial LLM output leaks.
    #[tokio::test]
    async fn withheld_text_is_replaced_with_notice_on_interrupt() {
        let cancel = CancellationToken::new();
        let cancel_stream = cancel.clone();
        let sink = CollectingSink::new();

        // A client whose stream sends text, then cancels the outer token,
        // and then blocks forever (never sends Done).
        struct InterruptClient(CancellationToken);
        #[async_trait]
        impl coda_llm::LlmClient for InterruptClient {
            fn provider_id(&self) -> &str { "mock" }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                let cancel = self.0.clone();
                let (tx, rx) = tokio::sync::mpsc::channel(16);
                tokio::spawn(async move {
                    let _ = tx.send(Ok(StreamEvent::TextDelta("SENSITIVE_DATA".into()))).await;
                    tokio::time::sleep(Duration::from_millis(20)).await;
                    cancel.cancel(); // fire outer cancel mid-stream
                    // Never send Done — the stream just hangs.
                    std::future::pending::<()>().await
                });
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }

        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let agent = AgentLoopBuilder::new(Arc::new(InterruptClient(cancel_stream)), Arc::new(AllowAll), tools)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("tell me a secret")];
        let result = tokio::time::timeout(
            Duration::from_secs(2),
            agent.run(&mut history, &sink, None, cancel),
        )
        .await
        .expect("agent hung");

        // The run should return Cancelled.
        assert!(matches!(result, Err(AgentError::Cancelled)));

        // §PRIVACY: SENSITIVE_DATA must NOT appear in history.
        let sensitive_in_history = history.iter().any(|m| m.text().contains("SENSITIVE_DATA"));
        assert!(!sensitive_in_history, "§PRIVACY: withheld text must not appear in history");

        // A Warning event must be emitted to signal the withholding.
        let events = sink.take();
        let has_warning = events.iter().any(|e| {
            matches!(e, AgentEvent::Warning { message } if message.contains("withheld"))
        });
        assert!(has_warning, "a Warning event must signal the withholding to the sink");
    }

    // ── IMPORTANT 3: integration tests — loop + real built-in tools ──────────
    //
    // Before CRITICAL 1 was fixed, these tests failed with the symptom described:
    //   - todo_write: ctx.todos was None → store was never written → items() == 0
    //   - task_list:  ctx.task_manager was None → returned "Task manager is not available."
    //   - tool_search: ctx.all_tools was None → search over [] → "No matching tools found."
    //
    // After the fix, BatchContext carries the real service handles and
    // make_tool_context() populates ToolContext from them.

    /// `todo_write` run through AgentLoop persists to the injected TodoStore.
    #[tokio::test]
    async fn loop_todo_write_persists_to_store() {
        use crate::todos::TodoStore;
        use crate::tools::TodoWriteTool;

        let store = Arc::new(TodoStore::new());

        // The model requests one todo_write call, then stops.
        let client = MockLlmClient::new(vec![
            vec![
                Ok(StreamEvent::ToolUse(Content::ToolUse {
                    id: "tw1".into(),
                    name: "todo_write".into(),
                    input_json: r#"{"todos":[{"content":"Write tests","activeForm":"Writing tests","status":"in_progress"}]}"#.into(),
                    correlation: coda_llm::Correlation::default(),
                })),
                Ok(done()),
            ],
            // Second iteration: no tools → natural stop.
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([
            Arc::new(TodoWriteTool) as Arc<dyn crate::tool::Tool>,
        ]));

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_todos(Arc::clone(&store))
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &NullSink, None, CancellationToken::new()).await.unwrap();

        // Before CRITICAL 1 fix: store.items().is_empty() → test fails.
        // After fix: todo_write received a non-None ctx.todos and called store.set().
        let items = store.items();
        assert_eq!(items.len(), 1, "todo_write must persist to the injected store; got: {items:?}");
        assert_eq!(items[0].content, "Write tests");
    }

    /// `task_list` run through AgentLoop reaches the injected TaskManager (no "not available" error).
    #[tokio::test]
    async fn loop_task_list_reaches_task_manager() {
        use crate::tasks::TaskManager;
        use crate::tools::TaskListTool;

        let task_mgr = TaskManager::with_defaults("integration-test-session");

        let client = MockLlmClient::new(vec![
            vec![
                Ok(StreamEvent::ToolUse(Content::ToolUse {
                    id: "tl1".into(),
                    name: "task_list".into(),
                    input_json: "{}".into(),
                    correlation: coda_llm::Correlation::default(),
                })),
                Ok(done()),
            ],
            vec![Ok(StreamEvent::TextDelta("done".into())), Ok(done())],
        ]);

        let tools = Arc::new(ToolRegistry::new([
            Arc::new(TaskListTool) as Arc<dyn crate::tool::Tool>,
        ]));

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_task_manager(Arc::clone(&task_mgr))
            .with_tool_max_duration(None)
            .build();

        let sink = CollectingSink::new();
        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        // Before CRITICAL 1 fix: ctx.task_manager was None → "Task manager is not available."
        // After fix: task_list sees the real manager and returns a success result.
        let events = sink.take();
        let task_list_result = events.iter().find(|e| {
            matches!(e, AgentEvent::ToolResult { tool_name, .. } if tool_name == "task_list")
        });
        assert!(task_list_result.is_some(), "task_list must produce a result event");
        if let Some(AgentEvent::ToolResult { content, is_error, .. }) = task_list_result {
            assert!(
                !is_error,
                "task_list must not error when manager is injected; got: {content}"
            );
            assert!(
                !content.contains("not available"),
                "task_list must not return 'not available' when manager is injected; got: {content}"
            );
        }
    }

    // ── Feature 2: Compaction wiring ────────────────────────────────────────
    //
    // The compaction service is wired into the agent loop for:
    //   - Proactive compaction: when token count exceeds threshold (goal-only).
    //   - Overflow compaction: when the LLM rejects the request due to context.

    /// Mock ForkedAgent that returns a canned summary.
    struct MockFork(String);

    #[async_trait]
    impl crate::goal::ForkedAgent for MockFork {
        async fn run(
            &self,
            _system: &str,
            _messages: Vec<Message>,
            _cancel: CancellationToken,
        ) -> anyhow::Result<String> {
            Ok(self.0.clone())
        }
    }

    /// Compaction fires proactively for goal runs when token count exceeds threshold.
    ///
    /// Mutation-verified: comment out step 3 compaction block and this test
    /// fails (history length doesn't decrease).
    #[tokio::test]
    async fn compaction_fires_proactively_when_over_threshold_in_goal_run() {
        use crate::compaction::CompactionService;
        use crate::goal::{ForkedAgent, GoalBudget, GoalRetryPolicy, GoalSupervisor};

        let svc = Arc::new(CompactionService::new(Arc::new(MockFork("summary text".into()))));

        // Use a very small threshold so it's exceeded by normal messages.
        let policy = crate::compaction::CompactionPolicy { token_threshold: 1, ..Default::default() };

        // Goal judge always returns DONE.
        struct DoneJudge;
        #[async_trait]
        impl ForkedAgent for DoneJudge {
            async fn run(&self, _: &str, _: Vec<Message>, _: CancellationToken) -> anyhow::Result<String> {
                Ok("DONE".to_owned())
            }
        }

        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("ok".into())),
            Ok(done()),
        ]]);
        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let sink = CollectingSink::new();

        let goal = GoalSupervisor::new(
            Box::new(DoneJudge),
            "test goal",
            GoalBudget::new(Duration::MAX, 3, 0.5, || Duration::ZERO),
            Some(GoalRetryPolicy::for_tests()),
        );

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_compaction_service(svc)
            .with_compaction_policy(policy)
            .with_tool_max_duration(None)
            .build();

        // Build a history that's large enough to exceed the tiny threshold.
        let mut history = vec![
            Message::user("prompt"),
            Message::assistant("previous response that is long enough to trigger compaction"),
        ];
        agent.run(&mut history, &sink, Some(goal), CancellationToken::new()).await.unwrap();

        // After compaction: history must contain the summary pair (2 messages: user summary + assistant ack).
        // The summary message contains our mock summary text.
        let has_summary = history.iter().any(|m| m.text().contains("summary text"));
        assert!(has_summary, "history must contain the compaction summary after proactive compaction");
    }

    /// When threshold is 0, compaction is disabled.
    ///
    /// Mirrors C# `CodaSession_does_not_auto_compact_when_threshold_is_zero`.
    #[tokio::test]
    async fn compaction_disabled_when_threshold_is_zero() {
        use crate::compaction::{CompactionService, CompactionPolicy};
        use std::sync::atomic::{AtomicUsize, Ordering};

        let fork_call_count = Arc::new(AtomicUsize::new(0));
        let cnt = fork_call_count.clone();

        struct CountingFork(Arc<AtomicUsize>);
        #[async_trait]
        impl crate::goal::ForkedAgent for CountingFork {
            async fn run(&self, _: &str, _: Vec<Message>, _: CancellationToken) -> anyhow::Result<String> {
                self.0.fetch_add(1, Ordering::SeqCst);
                Ok("summary".to_owned())
            }
        }

        let svc = Arc::new(CompactionService::new(Arc::new(CountingFork(cnt))));
        let policy = CompactionPolicy { token_threshold: 0, ..Default::default() }; // disabled

        use crate::goal::{ForkedAgent, GoalBudget, GoalRetryPolicy, GoalSupervisor};
        struct DoneJudge;
        #[async_trait]
        impl ForkedAgent for DoneJudge {
            async fn run(&self, _: &str, _: Vec<Message>, _: CancellationToken) -> anyhow::Result<String> {
                Ok("DONE".to_owned())
            }
        }

        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("done".into())),
            Ok(done()),
        ]]);
        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let goal = GoalSupervisor::new(
            Box::new(DoneJudge),
            "test",
            GoalBudget::new(Duration::MAX, 3, 0.5, || Duration::ZERO),
            Some(GoalRetryPolicy::for_tests()),
        );

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_compaction_service(svc)
            .with_compaction_policy(policy) // threshold=0 disables
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![
            Message::user("big context".repeat(1000)),
        ];
        agent.run(&mut history, &NullSink, Some(goal), CancellationToken::new()).await.unwrap();

        assert_eq!(
            fork_call_count.load(Ordering::SeqCst),
            0,
            "with threshold=0, compaction must not fire"
        );
    }

    /// If compaction fails (empty summarizer reply), history is preserved intact.
    ///
    /// Mirrors C# invariant: a failed compaction must not corrupt history.
    #[tokio::test]
    async fn compaction_failure_preserves_original_history() {
        use crate::compaction::CompactionService;
        use crate::events::NullSink;

        struct EmptyFork;
        #[async_trait]
        impl crate::goal::ForkedAgent for EmptyFork {
            async fn run(&self, _: &str, _: Vec<Message>, _: CancellationToken) -> anyhow::Result<String> {
                Ok(String::new()) // empty → fail
            }
        }

        let svc = Arc::new(CompactionService::new(Arc::new(EmptyFork)));
        let history = vec![
            Message::user("original prompt"),
            Message::assistant("original response"),
        ];
        let mut h = history.clone();
        // compact_history should return Err (empty summary) and leave h unchanged.
        let result = compact_history(
            &svc,
            None,
            &mut h,
            &NullSink,
            "test",
            CancellationToken::new(),
        ).await;

        assert!(result.is_err(), "empty summarizer must return an error");
        assert_eq!(h.len(), 2, "history must be preserved on compaction failure");
        assert_eq!(h[0].text(), "original prompt");
    }

    // ── Feature 3: AgentResponse hook mutation ──────────────────────────────

    /// Simple fixed-response executor for hook tests.
    struct FixedHookExecutor(String);

    #[async_trait]
    impl crate::hooks::runner::HookExecutor for FixedHookExecutor {
        async fn exec(
            &self,
            _command: &str,
            _payload: &str,
            _cancel: CancellationToken,
        ) -> anyhow::Result<(i32, String, String)> {
            Ok((0, self.0.clone(), String::new()))
        }
    }

    fn make_agent_response_hook_runner(response_json: &str) -> Arc<crate::hooks::HookRunner> {
        use crate::hooks::{HookScope, UserHook};
        let h = UserHook {
            event: "AgentResponse".into(),
            command: Some("check.sh".into()),
            matcher: None,
            timeout_seconds: None,
            fail_open: None,
            unattended_decision: None,
            allow_system_prompt_replace: false,
            mutates: Some(vec!["modifiedResponse".into(), "displayContent".into()]),
            handler_type: None,
            url: None,
            hook_prompt: None,
            agent_type: None,
            enabled: true,
            scope: HookScope::User,
            plugin_origin: None,
        };
        Arc::new(crate::hooks::HookRunner::with_executor(
            vec![h],
            Arc::new(FixedHookExecutor(response_json.to_owned())),
        ))
    }

    /// AgentResponse hook: modifiedResponse mutates history and emits ResponseRewritten.
    ///
    /// Mutation-verified: remove `replace_last_assistant_text` call and this test
    /// fails (history still has original text).
    #[tokio::test]
    async fn agent_response_hook_modified_response_mutates_history() {
        let hook_runner = make_agent_response_hook_runner(
            r#"{"hookSpecificOutput":{"modifiedResponse":"HOOKED RESPONSE"}}"#,
        );

        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("original response".into())),
            Ok(done()),
        ]]);
        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let sink = CollectingSink::new();

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_hook_runner(hook_runner)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        // History must contain the modified response, not the original.
        let last_assistant_text = history
            .iter()
            .rev()
            .find(|m| m.role == Role::Assistant)
            .map(|m| m.text())
            .unwrap_or_default();
        assert_eq!(
            last_assistant_text, "HOOKED RESPONSE",
            "modifiedResponse must replace the assistant text in history"
        );

        // ResponseRewritten event must be emitted.
        let events = sink.take();
        let rr = events.iter().find(|e| matches!(e, AgentEvent::ResponseRewritten { .. }));
        assert!(rr.is_some(), "ResponseRewritten event must be emitted");
        if let Some(AgentEvent::ResponseRewritten {
            original_response,
            display_content,
            modified_response,
            ..
        }) = rr
        {
            assert_eq!(original_response, "original response");
            assert_eq!(display_content, "HOOKED RESPONSE"); // fallback to modifiedResponse
            assert_eq!(modified_response.as_deref(), Some("HOOKED RESPONSE"));
        }
    }

    /// AgentResponse hook: displayContent only does not mutate history.
    #[tokio::test]
    async fn agent_response_hook_display_content_only_does_not_mutate_history() {
        let hook_runner = make_agent_response_hook_runner(
            r#"{"hookSpecificOutput":{"displayContent":"DISPLAY ONLY"}}"#,
        );

        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("original".into())),
            Ok(done()),
        ]]);
        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let sink = CollectingSink::new();

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_hook_runner(hook_runner)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        // History must still have the original text (only displayContent was set).
        let last = history
            .iter()
            .rev()
            .find(|m| m.role == Role::Assistant)
            .map(|m| m.text())
            .unwrap_or_default();
        assert_eq!(last, "original", "displayContent-only must not mutate history");

        let events = sink.take();
        let rr = events.iter().find(|e| matches!(e, AgentEvent::ResponseRewritten { .. }));
        assert!(rr.is_some(), "ResponseRewritten must be emitted when displayContent is set");
        if let Some(AgentEvent::ResponseRewritten { display_content, modified_response, .. }) = rr {
            assert_eq!(display_content, "DISPLAY ONLY");
            assert!(modified_response.is_none(), "modifiedResponse must be None for display-only hook");
        }
    }

    /// AgentResponse hook: both displayContent and modifiedResponse → displayContent shown.
    #[tokio::test]
    async fn agent_response_hook_display_content_takes_priority_over_modified_response() {
        let hook_runner = make_agent_response_hook_runner(
            r#"{"hookSpecificOutput":{"modifiedResponse":"FOR_HISTORY","displayContent":"FOR_USER"}}"#,
        );

        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("original".into())),
            Ok(done()),
        ]]);
        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let sink = CollectingSink::new();

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_hook_runner(hook_runner)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        let events = sink.take();
        let rr = events.iter().find(|e| matches!(e, AgentEvent::ResponseRewritten { .. }));
        if let Some(AgentEvent::ResponseRewritten { display_content, modified_response, .. }) = rr {
            assert_eq!(display_content, "FOR_USER", "displayContent must take priority for display");
            assert_eq!(modified_response.as_deref(), Some("FOR_HISTORY"), "modifiedResponse stored");
        } else {
            panic!("ResponseRewritten event not found");
        }
    }

    /// AgentResponse hook: no change means no ResponseRewritten event.
    #[tokio::test]
    async fn agent_response_hook_no_change_emits_no_event() {
        // Hook returns empty JSON (no modifications).
        let hook_runner = make_agent_response_hook_runner("{}");

        let client = MockLlmClient::new(vec![vec![
            Ok(StreamEvent::TextDelta("original".into())),
            Ok(done()),
        ]]);
        let tools = Arc::new(ToolRegistry::new([] as [Arc<dyn crate::tool::Tool>; 0]));
        let sink = CollectingSink::new();

        let agent = AgentLoopBuilder::new(client, Arc::new(AllowAll), tools)
            .with_hook_runner(hook_runner)
            .with_tool_max_duration(None)
            .build();

        let mut history = vec![Message::user("go")];
        agent.run(&mut history, &sink, None, CancellationToken::new()).await.unwrap();

        let events = sink.take();
        let has_rr = events.iter().any(|e| matches!(e, AgentEvent::ResponseRewritten { .. }));
        assert!(!has_rr, "no ResponseRewritten event must be emitted when hook returns no change");
    }
}




