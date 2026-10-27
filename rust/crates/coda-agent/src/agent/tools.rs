//! Serial tool batch executor.
//!
//! Tools execute SERIALLY in the model's requested order — never concurrently.
//! Ordering and the mid-batch steering/abort semantics depend on sequential
//! execution; do NOT "optimize" to parallel.

use std::sync::Arc;
use std::time::Duration;

use coda_llm::{Content, Correlation as LlmCorrelation};
use coda_proto::events::ToolCallStatus;
use tokio_util::sync::CancellationToken;

use crate::events::{AgentEvent, AgentSink};
use crate::lsp::LspServerManager;
use crate::permission::{PermissionMode, PermissionModeState, PermissionPrompt};
use crate::scheduling::ScheduledTaskStore;
use crate::steering::SteeringInbox;
use crate::subagents::SubagentFactory;
use crate::tasks::TaskManager;
use crate::todos::TodoStore;
use crate::tool::{PlanApprover, ToolContext, ToolDescriptor, ToolRegistry, ToolResult, UserQuestion};

use super::AgentError;
use super::ToolActivity;

/// The outcome of executing a batch of tool calls.
pub(crate) struct ToolBatchResult {
    pub result_blocks: Vec<Content>,
    pub abort_reason: Option<String>,
    /// Set when a tool raised [`coda_tool::ToolControl::AbortRun`]: the run
    /// must stop with a typed failure and **no** follow-up model request.
    ///
    /// Distinct from `abort_reason` (the hook `continue:false` path), which
    /// ends the run as an ordinary, successful completion.
    pub control_abort: Option<String>,
}

/// Context that stays constant for the entire batch.
pub(crate) struct BatchContext<'a> {
    pub tools: &'a ToolRegistry,
    pub permission_prompt: &'a dyn PermissionPrompt,
    pub permission_mode: PermissionMode,
    pub permission_mode_state: Option<&'a PermissionModeState>,
    pub steering: Option<&'a SteeringInbox>,
    pub working_directory: &'a str,
    pub granted_directories: Option<&'a std::collections::HashSet<String>>,
    pub tool_max_duration: Option<Duration>,
    /// Seam for the tool heartbeat (later phase).
    #[allow(dead_code)]
    pub tool_progress_interval: Duration,

    // ── Stateful service handles forwarded into every ToolContext ─────────────
    // These were all None before CRITICAL 1 was fixed; every stateful built-in
    // tool (todo_write, task_*, schedule_*, lsp_diagnostics, ask_user_question,
    // exit_plan_mode, tool_search, task) silently no-opped or returned an
    // "is not available" error because the loop never wired them.
    pub todos: Option<Arc<TodoStore>>,
    pub tool_user_question: Option<Arc<dyn UserQuestion>>,
    pub plan_approver: Option<Arc<dyn PlanApprover>>,
    pub lsp_manager: Option<Arc<LspServerManager>>,
    pub task_manager: Option<Arc<TaskManager>>,
    pub schedule_store: Option<Arc<ScheduledTaskStore>>,
    pub caller_task_id: Option<String>,
    /// Scheduled provenance for this run (trusted; never model-supplied).
    pub schedule_origin: Option<coda_tool::ScheduleOrigin>,
    pub subagent_factory: Option<Arc<dyn SubagentFactory>>,
    pub message_bus: Option<Arc<crate::message::MessageBus>>,
    pub is_main_context: bool,
}

impl<'a> BatchContext<'a> {
    fn effective_mode(&self) -> PermissionMode {
        self.permission_mode_state.map(|s| s.get()).unwrap_or(self.permission_mode)
    }

    fn allow_outside_working_directory(&self) -> bool {
        self.effective_mode() == PermissionMode::BypassPermissions
    }

    /// Build the per-call ToolContext from batch-constant data.
    ///
    /// `all_tools` is derived from the registry so `tool_search` can list
    /// every registered tool without holding a separate snapshot.
    /// Engine-specific handles (lsp, tasks, schedule, subagent) are wrapped via
    /// `ToolContextServiceExt` so `ToolContext` can live in `coda-tool`.
    fn make_tool_context(&self) -> ToolContext {
        use crate::tool::ToolContextServiceExt;

        let all_tools: Vec<ToolDescriptor> = self.tools.all().iter()
            .map(|t| ToolDescriptor {
                name: t.name().to_owned(),
                description: t.description().to_owned(),
                input_schema_json: t.input_schema_json().to_owned(),
                is_deferred: t.should_defer(),
                search_hint: t.search_hint().map(str::to_owned),
            })
            .collect();

        let mut ctx = ToolContext {
            working_directory: self.working_directory.to_owned(),
            allow_outside_working_directory: self.allow_outside_working_directory(),
            granted_directories: self.granted_directories.cloned(),
            todos: self.todos.clone(),
            user_question: self.tool_user_question.clone(),
            plan_approver: self.plan_approver.clone(),
            all_tools: Some(all_tools),
            caller_task_id: self.caller_task_id.clone(),
            schedule_origin: self.schedule_origin.clone(),
            is_main_context: self.is_main_context,
            lsp_manager: None,
            task_manager: None,
            schedule_store: None,
            subagent_factory: None,
            message_bus: None,
        };

        if let Some(mgr) = &self.lsp_manager {
            ctx = ctx.with_lsp_manager(Arc::clone(mgr));
        }
        if let Some(mgr) = &self.task_manager {
            ctx = ctx.with_task_manager(Arc::clone(mgr));
        }
        if let Some(store) = &self.schedule_store {
            ctx = ctx.with_schedule_store(Arc::clone(store));
        }
        if let Some(factory) = &self.subagent_factory {
            ctx = ctx.with_subagent_factory(Arc::clone(factory));
        }
        if let Some(bus) = &self.message_bus {
            ctx = ctx.with_message_bus(Arc::clone(bus));
        }
        ctx
    }
}

/// Execute a batch of tool-use blocks serially, returning all result blocks.
///
/// §1.6: "Tools run strictly SERIALLY, in the model's requested order — a
/// single `for i in 0..tool_uses.len()`.  Nothing parallelizes."
///
/// Cancellation returns completed/cancelled/skipped results, not an early
/// error: the loop must commit them before propagating `AgentError::Cancelled`.
///
/// Emits `ToolBatchStarted`/`ToolBatchEnded` around the whole batch — the
/// `coda-serve` state sink uses this pair to enter/leave `runningTools`.
/// `ToolBatchEnded` fires exactly once, on every exit path (success, error,
/// cancellation), because it wraps the call to [`run_tools_body`] rather than
/// living inline in a function with early returns.
pub(crate) async fn run_tools(
    tool_uses: &[Content],
    activity: &ToolActivity,
    sink: &dyn AgentSink,
    ctx: &BatchContext<'_>,
    cancel: CancellationToken,
) -> Result<ToolBatchResult, AgentError> {
    let batch_id = activity.batch_id().to_owned();
    let call_ids: Vec<String> = tool_uses
        .iter()
        .filter_map(|b| match b {
            Content::ToolUse { id, .. } => Some(id.clone()),
            _ => None,
        })
        .collect();
    sink.emit(AgentEvent::ToolBatchStarted { batch_id: batch_id.clone(), call_ids });
    let result = run_tools_body(tool_uses, activity, sink, ctx, cancel).await;
    sink.emit(AgentEvent::ToolBatchEnded { batch_id });
    result
}

async fn run_tools_body(
    tool_uses: &[Content],
    _activity: &ToolActivity,
    sink: &dyn AgentSink,
    ctx: &BatchContext<'_>,
    cancel: CancellationToken,
) -> Result<ToolBatchResult, AgentError> {
    let mut results: Vec<Content> = Vec::with_capacity(tool_uses.len());
    let abort_reason: Option<String> = None;
    let mut control_abort: Option<String> = None;

    // Pre-pass: queue every call before executing any.
    for block in tool_uses.iter() {
        if let Content::ToolUse { name, input_json, correlation, .. } = block {
            sink.emit(AgentEvent::ToolQueued {
                tool_name: name.clone(),
                input_json: input_json.clone(),
                correlation: correlation.clone(),
            });
        }
    }

    for (i, block) in tool_uses.iter().enumerate() {
        if cancel.is_cancelled() {
            cancel_remaining(&tool_uses[i..], false, sink, &mut results);
            break;
        }
        let (id, name, input_json, correlation) = match block {
            Content::ToolUse { id, name, input_json, correlation } => {
                (id, name, input_json, correlation)
            }
            _ => continue,
        };

        // --- Steering pre-empt (§1.6 step 2) ---
        if let Some(steering) = ctx.steering {
            let delivered = steering.take_all_for_delivery();
            if !delivered.is_empty() {
                for j in i..tool_uses.len() {
                    if let Content::ToolUse { id: sid, name: sname, correlation: scorr, .. } = &tool_uses[j] {
                        let msg = "Skipped: not executed because new operator steering arrived before this tool started.";
                        sink.emit(AgentEvent::ToolResult {
                            tool_name: sname.clone(),
                            content: msg.into(),
                            is_error: true,
                            status: ToolCallStatus::Skipped,
                            correlation: scorr.clone(),
                        });
                        results.push(make_result_block(sid, msg, true, ToolCallStatus::Skipped, scorr.clone()));
                    }
                }
                let steering_text = delivered.iter().map(|e| e.text.as_str()).collect::<Vec<_>>().join("\n\n");
                results.push(Content::Text(steering_text));
                sink.emit(AgentEvent::SteeringDelivered {
                    message_ids: delivered.iter().map(|e| e.id.clone()).collect(),
                });
                break;
            }
        }

        let effective_input = input_json.clone();

        // --- Resolve tool ---
        let tool = match ctx.tools.resolve(name) {
            Some(t) => t,
            None => {
                sink.emit(AgentEvent::ToolCall {
                    tool_name: name.clone(),
                    input_json: effective_input.clone(),
                    correlation: correlation.clone(),
                });
                let err_msg = format!("Unknown tool '{name}'.");
                sink.emit(AgentEvent::ToolResult {
                    tool_name: name.clone(),
                    content: err_msg.clone(),
                    is_error: true,
                    status: ToolCallStatus::Failed,
                    correlation: correlation.clone(),
                });
                results.push(make_result_block(id, &err_msg, true, ToolCallStatus::Failed, correlation.clone()));
                continue;
            }
        };

        sink.emit(AgentEvent::ToolCall {
            tool_name: name.clone(),
            input_json: effective_input.clone(),
            correlation: correlation.clone(),
        });

        // --- Permission gate (non-read-only tools only) ---
        if !tool.is_read_only() {
            sink.emit(AgentEvent::ToolStatus {
                tool_name: name.clone(),
                status: ToolCallStatus::AwaitingApproval,
                correlation: correlation.clone(),
            });

            let allowed = ctx.permission_prompt.request(&*tool, &effective_input, cancel.clone()).await;

            if cancel.is_cancelled() {
                cancel_remaining(&tool_uses[i..], true, sink, &mut results);
                break;
            }

            if !allowed {
                let denied_msg = "Permission denied by the user.";
                sink.emit(AgentEvent::ToolResult {
                    tool_name: name.clone(),
                    content: denied_msg.into(),
                    is_error: true,
                    status: ToolCallStatus::Failed,
                    correlation: correlation.clone(),
                });
                results.push(make_result_block(id, denied_msg, true, ToolCallStatus::Failed, correlation.clone()));
                continue; // batch CONTINUES after a denial
            }
        }

        // --- Execute ---
        let input_value: serde_json::Value = if effective_input.trim().is_empty() {
            serde_json::json!({})
        } else {
            serde_json::from_str(&effective_input).unwrap_or(serde_json::json!({}))
        };

        // Build the per-call ToolContext from BatchContext.  This is the only
        // place ToolContext is created for real tool executions; all service
        // handles were formerly hardcoded to None (CRITICAL 1).
        let tool_ctx = ctx.make_tool_context();

        sink.emit(AgentEvent::ToolStatus {
            tool_name: name.clone(),
            status: ToolCallStatus::Running,
            correlation: correlation.clone(),
        });

        let tool_result = match execute_with_ceiling(
            &*tool,
            &input_value,
            &tool_ctx,
            cancel.clone(),
            ctx.tool_max_duration,
            name,
        )
        .await
        {
            Ok(result) => result,
            Err(AgentError::Cancelled) => {
                cancel_remaining(&tool_uses[i..], true, sink, &mut results);
                break;
            }
            Err(error) => return Err(error),
        };

        // A tool that handed back a running managed task: wait for it under
        // the yield contract (non-blocking runtime, §6.2).
        let mut yielded_task: Option<String> = None;
        let tool_result = match tool_result.running_task_handle() {
            None => tool_result,
            Some((task_id, wait)) => {
                let task_id = task_id.to_owned();
                match await_running_task(ctx, &task_id, wait, &cancel).await {
                    TaskWaitOutcome::Finished(result) => result,
                    TaskWaitOutcome::Yielded(result) => {
                        yielded_task = Some(task_id);
                        result
                    }
                    TaskWaitOutcome::Interrupted => {
                        cancel_remaining(&tool_uses[i..], true, sink, &mut results);
                        break;
                    }
                }
            }
        };

        let terminal_status = if tool_result.is_error { ToolCallStatus::Failed } else { ToolCallStatus::Succeeded };

        sink.emit(AgentEvent::ToolResult {
            tool_name: name.clone(),
            content: tool_result.content.clone(),
            is_error: tool_result.is_error,
            status: terminal_status,
            correlation: correlation.clone(),
        });

        results.push(make_result_block(id, &tool_result.content, tool_result.is_error, terminal_status, correlation.clone()));

        // Batch rule (§6.2, P6): once a tool is still running in the
        // background, nothing planned after it may run against its unfinished
        // effects. The model re-plans with the handle in hand.
        if let Some(task_id) = &yielded_task {
            let msg = format!(
                "Skipped: an earlier tool is still running as task {task_id}; re-plan."
            );
            for later in tool_uses.iter().skip(i + 1) {
                if let Content::ToolUse { id: sid, name: sname, correlation: scorr, .. } = later {
                    sink.emit(AgentEvent::ToolResult {
                        tool_name: sname.clone(),
                        content: msg.clone(),
                        is_error: true,
                        status: ToolCallStatus::Skipped,
                        correlation: scorr.clone(),
                    });
                    results.push(make_result_block(sid, &msg, true, ToolCallStatus::Skipped, scorr.clone()));
                }
            }
            break;
        }

        // A typed terminal control signal (today: an unanswered operator
        // question). The result block above is still recorded — a `tool_use`
        // without a matching `tool_result` is not a valid conversation — but
        // nothing after it runs, and the caller turns this into a typed
        // failure rather than a completion. Every remaining call in the batch
        // is explicitly skipped rather than silently dropped.
        if let Some(reason) = tool_result.abort_reason() {
            control_abort = Some(reason.to_owned());
            for later in tool_uses.iter().skip(i + 1) {
                if let Content::ToolUse { id: sid, name: sname, correlation: scorr, .. } = later {
                    let msg = "Skipped: the run stopped before this tool started.";
                    sink.emit(AgentEvent::ToolResult {
                        tool_name: sname.clone(),
                        content: msg.into(),
                        is_error: true,
                        status: ToolCallStatus::Skipped,
                        correlation: scorr.clone(),
                    });
                    results.push(make_result_block(sid, msg, true, ToolCallStatus::Skipped, scorr.clone()));
                }
            }
            break;
        }
    }

    Ok(ToolBatchResult { result_blocks: results, abort_reason, control_abort })
}

fn cancel_remaining(
    tool_uses: &[Content],
    current_started: bool,
    sink: &dyn AgentSink,
    results: &mut Vec<Content>,
) {
    for (index, block) in tool_uses.iter().enumerate() {
        if let Content::ToolUse {
            id,
            name,
            correlation,
            ..
        } = block
        {
            let (status, message) = if index == 0 && current_started {
                (
                    ToolCallStatus::Cancelled,
                    "Cancelled: no final result was recorded for this tool call.",
                )
            } else {
                (
                    ToolCallStatus::Skipped,
                    "Skipped: not executed because the run was cancelled.",
                )
            };
            sink.emit(AgentEvent::ToolResult {
                tool_name: name.clone(),
                content: message.into(),
                is_error: true,
                status,
                correlation: correlation.clone(),
            });
            results.push(make_result_block(
                id,
                message,
                true,
                status,
                correlation.clone(),
            ));
        }
    }
}

/// Restore only missing results, without replaying tools or guessing their outcomes.
pub(super) fn recover_interrupted_history(history: &mut Vec<coda_llm::Message>) -> usize {
    use coda_llm::{Message, Role};

    let mut recovered = 0;
    let mut index = 0;
    while index < history.len() {
        if history[index].role != Role::Assistant {
            index += 1;
            continue;
        }
        let next = history
            .get(index + 1)
            .filter(|message| message.role == Role::User);
        let recorded: std::collections::HashSet<&str> = next
            .into_iter()
            .flat_map(|message| message.content.iter())
            .filter_map(|block| match block {
                Content::ToolResult { tool_use_id, .. } => Some(tool_use_id.as_str()),
                _ => None,
            })
            .collect();
        let missing: Vec<Content> = history[index].tool_uses().filter_map(|block| match block {
            Content::ToolUse { id, correlation, .. } if !recorded.contains(id.as_str()) => {
                Some(Content::ToolResult {
                    tool_use_id: id.clone(),
                    content: "Tool result unavailable: the outcome of this previous call is unknown. \
                              Check its effects before deciding whether to retry; it was not re-executed \
                              during history recovery.".into(),
                    is_error: true,
                    correlation: correlation.clone(),
                    status: None,
                })
            }
            _ => None,
        }).collect();
        if !missing.is_empty() {
            recovered += missing.len();
            if next.is_some() {
                history[index + 1].content.splice(0..0, missing);
            } else {
                history.insert(index + 1, Message::new(Role::User, missing));
            }
        }
        index += 1;
    }
    recovered
}

/// How a wait on a running managed task ended.
enum TaskWaitOutcome {
    /// The task reached a terminal state while awaited: its own result, shaped
    /// exactly as the tool always returned it.
    Finished(ToolResult),
    /// The wait ended first; the task keeps running in the background and the
    /// result is a handle the model can act on.
    Yielded(ToolResult),
    /// The turn was cancelled. The awaited task was stopped (D4).
    Interrupted,
}

/// Why a wait ended before the task did.
#[derive(Clone, Copy, PartialEq, Eq)]
enum YieldReason {
    Budget,
    Steering,
    UserBackground,
}

/// Longest output tail included in a yield handle.
const YIELD_TAIL_CHARS: usize = 4_000;

/// The yield contract (§6.2): wait for a running task until it finishes, its
/// yield budget expires, operator steering arrives, the user asks for
/// "background now", or the turn is cancelled — whichever is first.
///
/// Ending the wait never stops the work, with one exception: an interrupted
/// turn stops the task it was awaiting in the foreground (D4). Tasks already
/// in the background are never touched here.
async fn await_running_task(
    ctx: &BatchContext<'_>,
    task_id: &str,
    wait: Option<Duration>,
    cancel: &CancellationToken,
) -> TaskWaitOutcome {
    let Some(task) = ctx.task_manager.as_ref().and_then(|m| m.find_task(task_id)) else {
        return TaskWaitOutcome::Finished(ToolResult::error(format!(
            "Task {task_id} is no longer registered; its outcome is unknown."
        )));
    };
    // Without a budget (a context that cannot receive a pushed completion),
    // the per-tool ceiling still bounds the wait — by yielding, never killing.
    let budget = wait.or(ctx.tool_max_duration);
    let budget_expired = async {
        match budget {
            Some(d) => tokio::time::sleep(d).await,
            None => std::future::pending().await,
        }
    };
    let steering_arrived = async {
        match ctx.steering {
            Some(inbox) => inbox.wait_for_pending().await,
            None => std::future::pending().await,
        }
    };

    let reason = tokio::select! {
        biased;
        _ = cancel.cancelled() => None,
        _ = task.wait_for_completion() => Some(None),
        _ = task.background_requested() => Some(Some(YieldReason::UserBackground)),
        _ = steering_arrived => Some(Some(YieldReason::Steering)),
        _ = budget_expired => Some(Some(YieldReason::Budget)),
    };

    match reason {
        None => {
            if !task.is_terminal() {
                task.cancel_task();
                task.kill_attached_shell();
            }
            TaskWaitOutcome::Interrupted
        }
        // Decided under the task's own lock: promoted means it is still
        // running and now in the background; not promoted and not running
        // means it finished first and its result is reported inline.
        Some(Some(reason))
            if task.try_promote_to_background().is_some()
                || task.status() == crate::tasks::TaskRunStatus::Running =>
        {
            TaskWaitOutcome::Yielded(yield_handle(&task, reason, ctx.caller_task_id.is_none()))
        }
        // Finished, or finished in the instant before it could be promoted:
        // either way the loop observed it, so it is reported inline.
        Some(_) => TaskWaitOutcome::Finished(finished_result(&task)),
    }
}

fn finished_result(task: &crate::tasks::ManagedTask) -> ToolResult {
    use crate::tasks::TaskRunStatus;
    let snapshot = task.to_snapshot();
    let output = task.take_tool_output();
    match snapshot.status {
        TaskRunStatus::Completed => {
            ToolResult::ok(output.or(snapshot.result).unwrap_or_default())
        }
        TaskRunStatus::Stopped => {
            let tail = task.redacted_peek(YIELD_TAIL_CHARS);
            let mut text = format!("Stopped: task {} was stopped before it finished.", task.id);
            if !tail.trim().is_empty() {
                text.push_str(&format!("\nOutput before it stopped:\n{}", tail.trim_end()));
            }
            ToolResult::error(text)
        }
        TaskRunStatus::Failed | TaskRunStatus::Running => ToolResult::error(
            output.or(snapshot.error).unwrap_or_else(|| format!("Task {} failed.", task.id)),
        ),
    }
}

/// The handle the model receives for work that keeps running.
fn yield_handle(task: &crate::tasks::ManagedTask, reason: YieldReason, pushed: bool) -> ToolResult {
    let age = task.started_at.elapsed().map(|d| d.as_secs()).unwrap_or(0);
    let why = match reason {
        YieldReason::Budget => "",
        YieldReason::Steering => "; the wait ended early because a new user message arrived",
        YieldReason::UserBackground => "; the user moved it to the background",
    };
    let tail = task.redacted_peek(YIELD_TAIL_CHARS);
    let output = if tail.trim().is_empty() { "(none yet)".to_owned() } else { tail.trim_end().to_owned() };
    let follow_up = if pushed {
        "You will be notified when it finishes; do not poll or sleep. \
         Use task_stop to stop it, task_peek to read more output."
    } else {
        "Use task_wait to wait for it, task_peek to read its output, or task_stop to stop it."
    };
    ToolResult::ok(format!(
        "Still running as task {} (started {age}s ago){why}. Output so far:\n{output}\n\n{follow_up}",
        task.id
    ))
}

/// Execute a tool with an optional wall-clock ceiling.
///
/// Ceiling fires → error result, session survives (§8 item 8b).
/// Caller cancel → `AgentError::Cancelled` propagates (§8 item 8a).
async fn execute_with_ceiling(
    tool: &dyn crate::tool::Tool,
    input: &serde_json::Value,
    ctx: &ToolContext,
    cancel: CancellationToken,
    max_duration: Option<Duration>,
    tool_name: &str,
) -> Result<ToolResult, AgentError> {
    let tool_cancel = cancel.child_token();
    // Retain a handle so we can explicitly cancel the child token when the
    // ceiling fires.  Dropping the future only drops the token clone that was
    // moved into execute(); background tasks the tool spawned (holding their
    // own clone) would keep running indefinitely without this explicit cancel
    // (C# uses CancelAfter on the tool's own token — §MINOR 3).
    let tool_cancel_handle = tool_cancel.clone();

    let outcome = if let Some(max_dur) = max_duration {
        tokio::select! {
            r = tokio::time::timeout(max_dur, tool.execute(input, ctx, tool_cancel)) => {
                match r {
                    Ok(o) => {
                        if cancel.is_cancelled() { return Err(AgentError::Cancelled); }
                        o
                    }
                    Err(_) => {
                        // Cancel the tool's own token so background work it
                        // spawned (e.g. subprocesses, spawned tasks) learns
                        // it was killed rather than waiting indefinitely.
                        tool_cancel_handle.cancel();
                        if cancel.is_cancelled() { return Err(AgentError::Cancelled); }
                        ToolResult::error(format!(
                            "Tool '{}' exceeded the {}s maximum run time and was terminated.",
                            tool_name, max_dur.as_secs()
                        ))
                    }
                }
            }
            _ = cancel.cancelled() => return Err(AgentError::Cancelled),
        }
    } else {
        tokio::select! {
            o = tool.execute(input, ctx, tool_cancel) => {
                if cancel.is_cancelled() { return Err(AgentError::Cancelled); }
                o
            }
            _ = cancel.cancelled() => return Err(AgentError::Cancelled),
        }
    };

    Ok(outcome)
}

fn make_result_block(
    tool_use_id: &str,
    content: &str,
    is_error: bool,
    status: ToolCallStatus,
    correlation: LlmCorrelation,
) -> Content {
    Content::ToolResult {
        tool_use_id: tool_use_id.to_owned(),
        content: content.to_owned(),
        is_error,
        correlation,
        status: Some(status_str(status).to_owned()),
    }
}

fn status_str(s: ToolCallStatus) -> &'static str {
    match s {
        ToolCallStatus::Pending => "Pending",
        ToolCallStatus::AwaitingApproval => "AwaitingApproval",
        ToolCallStatus::Running => "Running",
        ToolCallStatus::Succeeded => "Succeeded",
        ToolCallStatus::Failed => "Failed",
        ToolCallStatus::Cancelled => "Cancelled",
        ToolCallStatus::Skipped => "Skipped",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn status_strings_are_stable() {
        // All 7 variants must be covered so removing any branch breaks this test.
        assert_eq!(status_str(ToolCallStatus::Pending), "Pending");
        assert_eq!(status_str(ToolCallStatus::AwaitingApproval), "AwaitingApproval");
        assert_eq!(status_str(ToolCallStatus::Running), "Running");
        assert_eq!(status_str(ToolCallStatus::Succeeded), "Succeeded");
        assert_eq!(status_str(ToolCallStatus::Failed), "Failed");
        assert_eq!(status_str(ToolCallStatus::Cancelled), "Cancelled");
        assert_eq!(status_str(ToolCallStatus::Skipped), "Skipped");
    }
}
