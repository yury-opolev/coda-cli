//! Serial tool batch executor.
//!
//! Tools execute SERIALLY in the model's requested order — never concurrently.
//! Ordering and the mid-batch steering/abort semantics depend on sequential
//! execution; do NOT "optimize" to parallel.

use std::time::Duration;

use coda_llm::{Content, Correlation as LlmCorrelation};
use coda_proto::events::ToolCallStatus;
use tokio_util::sync::CancellationToken;

use crate::events::{AgentEvent, AgentSink};
use crate::permission::{PermissionMode, PermissionModeState, PermissionPrompt};
use crate::steering::SteeringInbox;
use crate::tool::{ToolContext, ToolRegistry, ToolResult};

use super::AgentError;
use super::ToolActivity;

/// The outcome of executing a batch of tool calls.
pub(crate) struct ToolBatchResult {
    pub result_blocks: Vec<Content>,
    pub abort_reason: Option<String>,
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
}

impl<'a> BatchContext<'a> {
    fn effective_mode(&self) -> PermissionMode {
        self.permission_mode_state.map(|s| s.get()).unwrap_or(self.permission_mode)
    }

    fn allow_outside_working_directory(&self) -> bool {
        self.effective_mode() == PermissionMode::BypassPermissions
    }
}

/// Execute a batch of tool-use blocks serially, returning all result blocks.
///
/// §1.6: "Tools run strictly SERIALLY, in the model's requested order — a
/// single `for i in 0..tool_uses.len()`.  Nothing parallelizes."
pub(crate) async fn run_tools(
    tool_uses: &[Content],
    _activity: &ToolActivity,
    sink: &dyn AgentSink,
    ctx: &BatchContext<'_>,
    cancel: CancellationToken,
) -> Result<ToolBatchResult, AgentError> {
    let mut results: Vec<Content> = Vec::with_capacity(tool_uses.len());
    let abort_reason: Option<String> = None;

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
                return Err(AgentError::Cancelled);
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

        let tool_ctx = ToolContext {
            working_directory: ctx.working_directory.to_owned(),
            allow_outside_working_directory: ctx.allow_outside_working_directory(),
            granted_directories: ctx.granted_directories.cloned(),
            todos: None,
            user_question: None,
            plan_approver: None,
            all_tools: None,
            lsp_manager: None,
            task_manager: None,
            schedule_store: None,
            caller_task_id: None,
            subagent_factory: None,
        };

        sink.emit(AgentEvent::ToolStatus {
            tool_name: name.clone(),
            status: ToolCallStatus::Running,
            correlation: correlation.clone(),
        });

        let tool_result = execute_with_ceiling(&*tool, &input_value, &tool_ctx, cancel.clone(), ctx.tool_max_duration, name).await?;

        let terminal_status = if tool_result.is_error { ToolCallStatus::Failed } else { ToolCallStatus::Succeeded };

        sink.emit(AgentEvent::ToolResult {
            tool_name: name.clone(),
            content: tool_result.content.clone(),
            is_error: tool_result.is_error,
            status: terminal_status,
            correlation: correlation.clone(),
        });

        results.push(make_result_block(id, &tool_result.content, tool_result.is_error, terminal_status, correlation.clone()));
    }

    Ok(ToolBatchResult { result_blocks: results, abort_reason })
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
