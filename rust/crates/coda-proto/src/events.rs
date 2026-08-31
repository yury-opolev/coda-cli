//! Server-to-client `event/*` notifications.
//!
//! [`Event::parse`] turns a raw `(method, params)` pair into a typed value.
//! Unknown methods become [`Event::Unknown`] rather than an error: the engine
//! may add events, and a front-end that refuses to run against a newer engine
//! would be needlessly brittle.

use serde::Deserialize;
use serde_json::Value;

use crate::messages::Correlation;

/// Event method names.
pub mod event_method {
    pub const ASSISTANT_TEXT: &str = "event/assistantText";
    pub const ASSISTANT_TEXT_COMPLETE: &str = "event/assistantTextComplete";
    pub const THINKING: &str = "event/thinking";
    pub const THINKING_COMPLETE: &str = "event/thinkingComplete";
    pub const TOOL_CALL: &str = "event/toolCall";
    pub const TOOL_PROGRESS: &str = "event/toolProgress";
    pub const TOOL_RESULT: &str = "event/toolResult";
    pub const TURN_COMPLETE: &str = "event/turnComplete";
    pub const STOP: &str = "event/stop";
    pub const USAGE: &str = "event/usage";
    pub const ERROR: &str = "event/error";
    pub const LIMIT_REACHED: &str = "event/limitReached";
    pub const STREAM_PROGRESS: &str = "event/streamProgress";
    pub const STEERING_DELIVERED: &str = "event/steeringDelivered";
    pub const TASK_COMPLETED: &str = "event/taskCompleted";
    pub const SCHEDULE_LIFECYCLE: &str = "event/scheduleLifecycle";
    pub const PROMPT_REWRITTEN: &str = "event/promptRewritten";
    pub const RESPONSE_REWRITTEN: &str = "event/responseRewritten";
    pub const TOOL_INPUT_MODIFIED: &str = "event/toolInputModified";
    pub const TOOL_RESULT_MODIFIED: &str = "event/toolResultModified";
    pub const PERMISSION_DECIDED: &str = "event/permissionDecided";
    pub const PERMISSIONS_UPDATED: &str = "event/permissionsUpdated";
    pub const SUBAGENT_BLOCKED: &str = "event/subagentBlocked";
    pub const SUBAGENT_RESULT_MODIFIED: &str = "event/subagentResultModified";
    pub const COMPACTION_CANCELLED: &str = "event/compactionCancelled";
    pub const POST_COMPACT_CONTEXT_INJECTED: &str = "event/postCompactContextInjected";
}

/// Status reported alongside a tool result.
///
/// Serialised by the C# host as `ToString()` on its enum, hence PascalCase.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
pub enum ToolCallStatus {
    Pending,
    AwaitingApproval,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Skipped,
}

impl ToolCallStatus {
    pub fn is_terminal(self) -> bool {
        matches!(
            self,
            ToolCallStatus::Succeeded
                | ToolCallStatus::Failed
                | ToolCallStatus::Cancelled
                | ToolCallStatus::Skipped
        )
    }
}

/// A typed `event/*` notification.
#[derive(Debug, Clone)]
pub enum Event {
    /// A chunk of assistant text. The host coalesces bursts, so a single event
    /// may carry many characters; deltas must be concatenated in arrival order.
    AssistantText { delta: String },
    AssistantTextComplete,
    Thinking { delta: String },
    ThinkingComplete {
        elapsed_ms: i64,
        thinking_tokens: Option<i32>,
    },
    ToolCall {
        tool_name: String,
        /// A *string* containing JSON, not a JSON object.
        input_json: String,
        correlation: Correlation,
    },
    ToolProgress {
        tool_name: String,
        elapsed_ms: i64,
        correlation: Correlation,
    },
    ToolResult {
        tool_name: String,
        content: String,
        is_error: bool,
        status: Option<ToolCallStatus>,
        correlation: Correlation,
    },
    TurnComplete {
        stop_reason: Option<String>,
        interrupted: bool,
        root_turn_id: Option<String>,
        activity_id: Option<String>,
    },
    Stop { stop_reason: Option<String> },
    Usage {
        input_tokens: i64,
        output_tokens: i64,
    },
    Error { message: String },
    LimitReached { kind: String, message: String },
    StreamProgress {
        /// `"first-token"`, `"progress"` or `"complete"`.
        phase: String,
        chunks: i64,
        chars: i64,
        elapsed_ms: i64,
    },
    SteeringDelivered { message_ids: Vec<String> },
    TaskCompleted {
        task_id: String,
        /// `"completed"`, `"failed"` or `"stopped"`.
        status: String,
        description: String,
        report: Option<String>,
    },
    ScheduleLifecycle {
        definition_id: String,
        definition_name: Option<String>,
        task_id: Option<String>,
        /// `"started"`, `"completed"`, `"failed"` or `"stopped"`.
        state: String,
        timestamp: Option<String>,
        summary: Option<String>,
    },
    PromptRewritten {
        hook_command: String,
        original_prompt: String,
        modified_prompt: String,
    },
    ResponseRewritten {
        hook_command: String,
        original_response: String,
        display_content: String,
        modified_response: Option<String>,
    },
    ToolInputModified {
        hook_command: String,
        tool_name: String,
        original_input: String,
        modified_input: String,
    },
    ToolResultModified {
        hook_command: String,
        tool_name: String,
        original_result: String,
        modified_result: String,
    },
    PermissionDecided {
        hook_command: String,
        tool_name: String,
        /// `"allow"` or `"deny"`.
        decision: String,
    },
    PermissionsUpdated {
        hook_command: String,
        mode_applied: Option<String>,
        added_allow: Vec<String>,
        added_deny: Vec<String>,
    },
    SubagentBlocked {
        hook_command: String,
        task_id: String,
        reason: String,
    },
    /// The C# engine ran a hook that modified the result of a finished subagent.
    SubagentResultModified {
        hook_command: String,
        task_id: String,
        original_result: String,
        modified_result: String,
    },
    /// A hook cancelled a compaction pass.
    CompactionCancelled { hook_command: String, trigger: String },
    /// A hook injected extra context immediately after compaction.
    PostCompactContextInjected { additional_context: String },
    /// An event this build does not model. Kept so newer engines still work.
    Unknown { method: String, params: Option<Value> },
}

/// Helper: parse params, falling back to the type's default when absent.
fn parse<T: for<'de> Deserialize<'de> + Default>(params: Option<&Value>) -> T {
    match params {
        Some(value) => serde_json::from_value(value.clone()).unwrap_or_default(),
        None => T::default(),
    }
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DeltaPayload {
    #[serde(default)]
    delta: String,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ThinkingCompletePayload {
    #[serde(default)]
    elapsed_ms: i64,
    #[serde(default)]
    thinking_tokens: Option<i32>,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ToolCallPayload {
    #[serde(default)]
    tool_name: String,
    #[serde(default)]
    input_json: String,
    #[serde(flatten)]
    correlation: Correlation,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ToolProgressPayload {
    #[serde(default)]
    tool_name: String,
    #[serde(default)]
    elapsed_ms: i64,
    #[serde(flatten)]
    correlation: Correlation,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ToolResultPayload {
    #[serde(default)]
    tool_name: String,
    #[serde(default)]
    content: String,
    #[serde(default)]
    is_error: bool,
    #[serde(default)]
    status: Option<ToolCallStatus>,
    #[serde(flatten)]
    correlation: Correlation,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct TurnCompletePayload {
    #[serde(default)]
    stop_reason: Option<String>,
    #[serde(default)]
    interrupted: bool,
    #[serde(default)]
    root_turn_id: Option<String>,
    #[serde(default)]
    activity_id: Option<String>,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct StopPayload {
    #[serde(default)]
    stop_reason: Option<String>,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct UsagePayload {
    #[serde(default)]
    input_tokens: i64,
    #[serde(default)]
    output_tokens: i64,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct MessagePayload {
    #[serde(default)]
    message: String,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LimitReachedPayload {
    #[serde(default)]
    kind: String,
    #[serde(default)]
    message: String,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct StreamProgressPayload {
    #[serde(default)]
    phase: String,
    #[serde(default)]
    chunks: i64,
    #[serde(default)]
    chars: i64,
    #[serde(default)]
    elapsed_ms: i64,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SteeringDeliveredPayload {
    #[serde(default)]
    message_ids: Vec<String>,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct TaskCompletedPayload {
    #[serde(default)]
    task_id: String,
    #[serde(default)]
    status: String,
    #[serde(default)]
    description: String,
    #[serde(default)]
    report: Option<String>,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ScheduleLifecyclePayload {
    #[serde(default)]
    definition_id: String,
    #[serde(default)]
    definition_name: Option<String>,
    #[serde(default)]
    task_id: Option<String>,
    #[serde(default)]
    state: String,
    #[serde(default)]
    timestamp: Option<String>,
    #[serde(default)]
    summary: Option<String>,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PromptRewrittenPayload {
    #[serde(default)]
    hook_command: String,
    #[serde(default)]
    original_prompt: String,
    #[serde(default)]
    modified_prompt: String,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ResponseRewrittenPayload {
    #[serde(default)]
    hook_command: String,
    #[serde(default)]
    original_response: String,
    #[serde(default)]
    display_content: String,
    #[serde(default)]
    modified_response: Option<String>,
}

/// `event/toolInputModified` carries the input before and after the hook ran.
#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ToolInputModifiedPayload {
    #[serde(default)]
    hook_command: String,
    #[serde(default)]
    tool_name: String,
    #[serde(default)]
    original_input: String,
    #[serde(default)]
    modified_input: String,
}

/// `event/toolResultModified` carries the result before and after the hook ran.
#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ToolResultModifiedPayload {
    #[serde(default)]
    hook_command: String,
    #[serde(default)]
    tool_name: String,
    #[serde(default)]
    original_result: String,
    #[serde(default)]
    modified_result: String,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CompactionCancelledPayload {
    #[serde(default)]
    hook_command: String,
    #[serde(default)]
    trigger: String,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PostCompactContextInjectedPayload {
    #[serde(default)]
    additional_context: String,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PermissionDecidedPayload {
    #[serde(default)]
    hook_command: String,
    #[serde(default)]
    tool_name: String,
    #[serde(default)]
    decision: String,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PermissionsUpdatedPayload {
    #[serde(default)]
    hook_command: String,
    #[serde(default)]
    mode_applied: Option<String>,
    #[serde(default)]
    added_allow: Vec<String>,
    #[serde(default)]
    added_deny: Vec<String>,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SubagentBlockedPayload {
    #[serde(default)]
    hook_command: String,
    #[serde(default)]
    task_id: String,
    #[serde(default)]
    reason: String,
}

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SubagentResultModifiedPayload {
    #[serde(default)]
    hook_command: String,
    #[serde(default)]
    task_id: String,
    #[serde(default)]
    original_result: String,
    #[serde(default)]
    modified_result: String,
}

impl Event {
    /// Converts a raw notification into a typed event.
    pub fn parse(method: &str, params: Option<&Value>) -> Event {
        use event_method as m;

        match method {
            m::ASSISTANT_TEXT => Event::AssistantText {
                delta: parse::<DeltaPayload>(params).delta,
            },
            m::ASSISTANT_TEXT_COMPLETE => Event::AssistantTextComplete,
            m::THINKING => Event::Thinking {
                delta: parse::<DeltaPayload>(params).delta,
            },
            m::THINKING_COMPLETE => {
                let p: ThinkingCompletePayload = parse(params);
                Event::ThinkingComplete {
                    elapsed_ms: p.elapsed_ms,
                    thinking_tokens: p.thinking_tokens,
                }
            }
            m::TOOL_CALL => {
                let p: ToolCallPayload = parse(params);
                Event::ToolCall {
                    tool_name: p.tool_name,
                    input_json: p.input_json,
                    correlation: p.correlation,
                }
            }
            m::TOOL_PROGRESS => {
                let p: ToolProgressPayload = parse(params);
                Event::ToolProgress {
                    tool_name: p.tool_name,
                    elapsed_ms: p.elapsed_ms,
                    correlation: p.correlation,
                }
            }
            m::TOOL_RESULT => {
                let p: ToolResultPayload = parse(params);
                Event::ToolResult {
                    tool_name: p.tool_name,
                    content: p.content,
                    is_error: p.is_error,
                    status: p.status,
                    correlation: p.correlation,
                }
            }
            m::TURN_COMPLETE => {
                let p: TurnCompletePayload = parse(params);
                Event::TurnComplete {
                    stop_reason: p.stop_reason,
                    interrupted: p.interrupted,
                    root_turn_id: p.root_turn_id,
                    activity_id: p.activity_id,
                }
            }
            m::STOP => Event::Stop {
                stop_reason: parse::<StopPayload>(params).stop_reason,
            },
            m::USAGE => {
                let p: UsagePayload = parse(params);
                Event::Usage {
                    input_tokens: p.input_tokens,
                    output_tokens: p.output_tokens,
                }
            }
            m::ERROR => Event::Error {
                message: parse::<MessagePayload>(params).message,
            },
            m::LIMIT_REACHED => {
                let p: LimitReachedPayload = parse(params);
                Event::LimitReached {
                    kind: p.kind,
                    message: p.message,
                }
            }
            m::STREAM_PROGRESS => {
                let p: StreamProgressPayload = parse(params);
                Event::StreamProgress {
                    phase: p.phase,
                    chunks: p.chunks,
                    chars: p.chars,
                    elapsed_ms: p.elapsed_ms,
                }
            }
            m::STEERING_DELIVERED => Event::SteeringDelivered {
                message_ids: parse::<SteeringDeliveredPayload>(params).message_ids,
            },
            m::TASK_COMPLETED => {
                let p: TaskCompletedPayload = parse(params);
                Event::TaskCompleted {
                    task_id: p.task_id,
                    status: p.status,
                    description: p.description,
                    report: p.report,
                }
            }
            m::SCHEDULE_LIFECYCLE => {
                let p: ScheduleLifecyclePayload = parse(params);
                Event::ScheduleLifecycle {
                    definition_id: p.definition_id,
                    definition_name: p.definition_name,
                    task_id: p.task_id,
                    state: p.state,
                    timestamp: p.timestamp,
                    summary: p.summary,
                }
            }
            m::PROMPT_REWRITTEN => {
                let p: PromptRewrittenPayload = parse(params);
                Event::PromptRewritten {
                    hook_command: p.hook_command,
                    original_prompt: p.original_prompt,
                    modified_prompt: p.modified_prompt,
                }
            }
            m::RESPONSE_REWRITTEN => {
                let p: ResponseRewrittenPayload = parse(params);
                Event::ResponseRewritten {
                    hook_command: p.hook_command,
                    original_response: p.original_response,
                    display_content: p.display_content,
                    modified_response: p.modified_response,
                }
            }
            m::TOOL_INPUT_MODIFIED => {
                let p: ToolInputModifiedPayload = parse(params);
                Event::ToolInputModified {
                    hook_command: p.hook_command,
                    tool_name: p.tool_name,
                    original_input: p.original_input,
                    modified_input: p.modified_input,
                }
            }
            m::TOOL_RESULT_MODIFIED => {
                let p: ToolResultModifiedPayload = parse(params);
                Event::ToolResultModified {
                    hook_command: p.hook_command,
                    tool_name: p.tool_name,
                    original_result: p.original_result,
                    modified_result: p.modified_result,
                }
            }
            m::PERMISSION_DECIDED => {
                let p: PermissionDecidedPayload = parse(params);
                Event::PermissionDecided {
                    hook_command: p.hook_command,
                    tool_name: p.tool_name,
                    decision: p.decision,
                }
            }
            m::PERMISSIONS_UPDATED => {
                let p: PermissionsUpdatedPayload = parse(params);
                Event::PermissionsUpdated {
                    hook_command: p.hook_command,
                    mode_applied: p.mode_applied,
                    added_allow: p.added_allow,
                    added_deny: p.added_deny,
                }
            }
            m::SUBAGENT_BLOCKED => {
                let p: SubagentBlockedPayload = parse(params);
                Event::SubagentBlocked {
                    hook_command: p.hook_command,
                    task_id: p.task_id,
                    reason: p.reason,
                }
            }
            m::SUBAGENT_RESULT_MODIFIED => {
                let p: SubagentResultModifiedPayload = parse(params);
                Event::SubagentResultModified {
                    hook_command: p.hook_command,
                    task_id: p.task_id,
                    original_result: p.original_result,
                    modified_result: p.modified_result,
                }
            }
            m::COMPACTION_CANCELLED => {
                let p: CompactionCancelledPayload = parse(params);
                Event::CompactionCancelled {
                    hook_command: p.hook_command,
                    trigger: p.trigger,
                }
            }
            m::POST_COMPACT_CONTEXT_INJECTED => {
                let p: PostCompactContextInjectedPayload = parse(params);
                Event::PostCompactContextInjected {
                    additional_context: p.additional_context,
                }
            }
            other => Event::Unknown {
                method: other.to_string(),
                params: params.cloned(),
            },
        }
    }

    /// Whether this event ends the active turn.
    pub fn ends_turn(&self) -> bool {
        matches!(self, Event::TurnComplete { .. })
    }

    /// Encodes this event as a `(method, params)` pair for a notification.
    ///
    /// This is the inverse of [`Event::parse`] and exists for the server half
    /// of the protocol.  The C# host omits null-valued properties rather than
    /// writing `null`, so optional fields are inserted only when `Some`; a
    /// round trip through [`Event::parse`] must therefore reproduce the
    /// original event, which is what the round-trip tests assert.
    ///
    /// Returns `None` for [`Event::Unknown`] with no captured method, which
    /// cannot be re-encoded.
    pub fn to_notification(&self) -> Option<(String, Value)> {
        use event_method as m;
        use serde_json::Map;

        // Inserts only when present, mirroring the C# null-omitting policy.
        fn put_opt(map: &mut Map<String, Value>, key: &str, value: &Option<String>) {
            if let Some(v) = value {
                map.insert(key.to_string(), Value::String(v.clone()));
            }
        }

        fn correlation_fields(map: &mut Map<String, Value>, c: &Correlation) {
            put_opt(map, "rootTurnId", &c.root_turn_id);
            put_opt(map, "activityId", &c.activity_id);
            put_opt(map, "callId", &c.call_id);
            put_opt(map, "sourceId", &c.source_id);
        }

        let (method, params): (&str, Value) = match self {
            Event::AssistantText { delta } => {
                (m::ASSISTANT_TEXT, serde_json::json!({ "delta": delta }))
            }
            Event::AssistantTextComplete => {
                (m::ASSISTANT_TEXT_COMPLETE, Value::Object(Map::new()))
            }
            Event::Thinking { delta } => (m::THINKING, serde_json::json!({ "delta": delta })),
            Event::ThinkingComplete { elapsed_ms, thinking_tokens } => {
                let mut map = Map::new();
                map.insert("elapsedMs".into(), (*elapsed_ms).into());
                if let Some(t) = thinking_tokens {
                    map.insert("thinkingTokens".into(), (*t).into());
                }
                (m::THINKING_COMPLETE, Value::Object(map))
            }
            Event::ToolCall { tool_name, input_json, correlation } => {
                let mut map = Map::new();
                map.insert("toolName".into(), tool_name.clone().into());
                map.insert("inputJson".into(), input_json.clone().into());
                correlation_fields(&mut map, correlation);
                (m::TOOL_CALL, Value::Object(map))
            }
            Event::ToolProgress { tool_name, elapsed_ms, correlation } => {
                let mut map = Map::new();
                map.insert("toolName".into(), tool_name.clone().into());
                map.insert("elapsedMs".into(), (*elapsed_ms).into());
                correlation_fields(&mut map, correlation);
                (m::TOOL_PROGRESS, Value::Object(map))
            }
            Event::ToolResult { tool_name, content, is_error, status, correlation } => {
                let mut map = Map::new();
                map.insert("toolName".into(), tool_name.clone().into());
                map.insert("content".into(), content.clone().into());
                map.insert("isError".into(), (*is_error).into());
                correlation_fields(&mut map, correlation);
                if let Some(s) = status {
                    map.insert("status".into(), format!("{s:?}").into());
                }
                (m::TOOL_RESULT, Value::Object(map))
            }
            Event::TurnComplete { stop_reason, interrupted, root_turn_id, activity_id } => {
                let mut map = Map::new();
                put_opt(&mut map, "stopReason", stop_reason);
                map.insert("interrupted".into(), (*interrupted).into());
                put_opt(&mut map, "rootTurnId", root_turn_id);
                put_opt(&mut map, "activityId", activity_id);
                (m::TURN_COMPLETE, Value::Object(map))
            }
            Event::Stop { stop_reason } => {
                let mut map = Map::new();
                put_opt(&mut map, "stopReason", stop_reason);
                (m::STOP, Value::Object(map))
            }
            Event::Usage { input_tokens, output_tokens } => (
                m::USAGE,
                serde_json::json!({ "inputTokens": input_tokens, "outputTokens": output_tokens }),
            ),
            Event::Error { message } => (m::ERROR, serde_json::json!({ "message": message })),
            Event::LimitReached { kind, message } => (
                m::LIMIT_REACHED,
                serde_json::json!({ "kind": kind, "message": message }),
            ),
            Event::StreamProgress { phase, chunks, chars, elapsed_ms } => (
                m::STREAM_PROGRESS,
                serde_json::json!({
                    "phase": phase, "chunks": chunks, "chars": chars, "elapsedMs": elapsed_ms
                }),
            ),
            Event::SteeringDelivered { message_ids } => (
                m::STEERING_DELIVERED,
                serde_json::json!({ "messageIds": message_ids }),
            ),
            Event::TaskCompleted { task_id, status, description, report } => {
                let mut map = Map::new();
                map.insert("taskId".into(), task_id.clone().into());
                map.insert("status".into(), status.clone().into());
                map.insert("description".into(), description.clone().into());
                put_opt(&mut map, "report", report);
                (m::TASK_COMPLETED, Value::Object(map))
            }
            Event::ScheduleLifecycle {
                definition_id,
                definition_name,
                task_id,
                state,
                timestamp,
                summary,
            } => {
                let mut map = Map::new();
                map.insert("definitionId".into(), definition_id.clone().into());
                put_opt(&mut map, "definitionName", definition_name);
                put_opt(&mut map, "taskId", task_id);
                map.insert("state".into(), state.clone().into());
                put_opt(&mut map, "timestamp", timestamp);
                put_opt(&mut map, "summary", summary);
                (m::SCHEDULE_LIFECYCLE, Value::Object(map))
            }
            Event::PromptRewritten { hook_command, original_prompt, modified_prompt } => (
                m::PROMPT_REWRITTEN,
                serde_json::json!({
                    "hookCommand": hook_command,
                    "originalPrompt": original_prompt,
                    "modifiedPrompt": modified_prompt
                }),
            ),
            Event::ResponseRewritten {
                hook_command,
                original_response,
                display_content,
                modified_response,
            } => {
                let mut map = Map::new();
                map.insert("hookCommand".into(), hook_command.clone().into());
                map.insert("originalResponse".into(), original_response.clone().into());
                map.insert("displayContent".into(), display_content.clone().into());
                put_opt(&mut map, "modifiedResponse", modified_response);
                (m::RESPONSE_REWRITTEN, Value::Object(map))
            }
            Event::ToolInputModified { hook_command, tool_name, original_input, modified_input } => (
                m::TOOL_INPUT_MODIFIED,
                serde_json::json!({
                    "hookCommand": hook_command,
                    "toolName": tool_name,
                    "originalInput": original_input,
                    "modifiedInput": modified_input
                }),
            ),
            Event::ToolResultModified {
                hook_command,
                tool_name,
                original_result,
                modified_result,
            } => (
                m::TOOL_RESULT_MODIFIED,
                serde_json::json!({
                    "hookCommand": hook_command,
                    "toolName": tool_name,
                    "originalResult": original_result,
                    "modifiedResult": modified_result
                }),
            ),
            Event::PermissionDecided { hook_command, tool_name, decision } => (
                m::PERMISSION_DECIDED,
                serde_json::json!({
                    "hookCommand": hook_command, "toolName": tool_name, "decision": decision
                }),
            ),
            Event::PermissionsUpdated { hook_command, mode_applied, added_allow, added_deny } => {
                let mut map = Map::new();
                map.insert("hookCommand".into(), hook_command.clone().into());
                put_opt(&mut map, "modeApplied", mode_applied);
                map.insert("addedAllow".into(), added_allow.clone().into());
                map.insert("addedDeny".into(), added_deny.clone().into());
                (m::PERMISSIONS_UPDATED, Value::Object(map))
            }
            Event::SubagentBlocked { hook_command, task_id, reason } => (
                m::SUBAGENT_BLOCKED,
                serde_json::json!({
                    "hookCommand": hook_command, "taskId": task_id, "reason": reason
                }),
            ),
            Event::SubagentResultModified {
                hook_command,
                task_id,
                original_result,
                modified_result,
            } => (
                m::SUBAGENT_RESULT_MODIFIED,
                serde_json::json!({
                    "hookCommand": hook_command,
                    "taskId": task_id,
                    "originalResult": original_result,
                    "modifiedResult": modified_result
                }),
            ),
            Event::CompactionCancelled { hook_command, trigger } => (
                m::COMPACTION_CANCELLED,
                serde_json::json!({ "hookCommand": hook_command, "trigger": trigger }),
            ),
            Event::PostCompactContextInjected { additional_context } => (
                m::POST_COMPACT_CONTEXT_INJECTED,
                serde_json::json!({ "additionalContext": additional_context }),
            ),
            Event::Unknown { method, params } => {
                return Some((
                    method.clone(),
                    params.clone().unwrap_or(Value::Object(Map::new())),
                ));
            }
        };

        Some((method.to_string(), params))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Encoding an event and parsing it back must reproduce it exactly.
    ///
    /// This is the parity net for the server half: every field the C# host
    /// puts on the wire has to survive the round trip, so a field dropped
    /// from either direction fails here rather than silently degrading a
    /// client that expects it.
    mod round_trip {
        use super::*;

        fn assert_round_trips(event: Event) {
            let (method, params) = event
                .to_notification()
                .unwrap_or_else(|| panic!("{event:?} did not encode"));
            let parsed = Event::parse(&method, Some(&params));
            assert_eq!(
                format!("{event:?}"),
                format!("{parsed:?}"),
                "round trip changed the event via {method}"
            );
        }

        fn correlation() -> Correlation {
            Correlation {
                root_turn_id: Some("root".into()),
                activity_id: Some("act".into()),
                call_id: Some("call".into()),
                source_id: Some("src".into()),
            }
        }

        #[test]
        fn every_hook_event_keeps_its_before_and_after_payloads() {
            assert_round_trips(Event::ResponseRewritten {
                hook_command: "cmd".into(),
                original_response: "before".into(),
                display_content: "shown".into(),
                modified_response: Some("after".into()),
            });
            assert_round_trips(Event::ToolInputModified {
                hook_command: "cmd".into(),
                tool_name: "read".into(),
                original_input: "before".into(),
                modified_input: "after".into(),
            });
            assert_round_trips(Event::ToolResultModified {
                hook_command: "cmd".into(),
                tool_name: "read".into(),
                original_result: "before".into(),
                modified_result: "after".into(),
            });
        }

        #[test]
        fn compaction_events_round_trip() {
            assert_round_trips(Event::CompactionCancelled {
                hook_command: "cmd".into(),
                trigger: "auto".into(),
            });
            assert_round_trips(Event::PostCompactContextInjected {
                additional_context: "extra".into(),
            });
        }

        #[test]
        fn core_stream_events_round_trip() {
            assert_round_trips(Event::AssistantText { delta: "hi".into() });
            assert_round_trips(Event::AssistantTextComplete);
            assert_round_trips(Event::Thinking { delta: "hmm".into() });
            assert_round_trips(Event::ThinkingComplete {
                elapsed_ms: 12,
                thinking_tokens: Some(34),
            });
            assert_round_trips(Event::ThinkingComplete {
                elapsed_ms: 12,
                thinking_tokens: None,
            });
            assert_round_trips(Event::Usage { input_tokens: 1, output_tokens: 2 });
            assert_round_trips(Event::Error { message: "boom".into() });
            assert_round_trips(Event::LimitReached {
                kind: "context".into(),
                message: "full".into(),
            });
            assert_round_trips(Event::StreamProgress {
                phase: "progress".into(),
                chunks: 3,
                chars: 40,
                elapsed_ms: 7,
            });
        }

        #[test]
        fn tool_events_round_trip_with_correlation() {
            assert_round_trips(Event::ToolCall {
                tool_name: "read".into(),
                input_json: "{}".into(),
                correlation: correlation(),
            });
            assert_round_trips(Event::ToolProgress {
                tool_name: "read".into(),
                elapsed_ms: 5,
                correlation: correlation(),
            });
            for status in [
                ToolCallStatus::Pending,
                ToolCallStatus::AwaitingApproval,
                ToolCallStatus::Running,
                ToolCallStatus::Succeeded,
                ToolCallStatus::Failed,
                ToolCallStatus::Cancelled,
                ToolCallStatus::Skipped,
            ] {
                assert_round_trips(Event::ToolResult {
                    tool_name: "read".into(),
                    content: "ok".into(),
                    is_error: false,
                    status: Some(status),
                    correlation: correlation(),
                });
            }
        }

        #[test]
        fn turn_and_lifecycle_events_round_trip() {
            assert_round_trips(Event::TurnComplete {
                stop_reason: Some("end_turn".into()),
                interrupted: false,
                root_turn_id: Some("root".into()),
                activity_id: Some("act".into()),
            });
            assert_round_trips(Event::Stop { stop_reason: None });
            assert_round_trips(Event::SteeringDelivered {
                message_ids: vec!["a".into(), "b".into()],
            });
            assert_round_trips(Event::TaskCompleted {
                task_id: "t1".into(),
                status: "completed".into(),
                description: "did a thing".into(),
                report: None,
            });
            assert_round_trips(Event::ScheduleLifecycle {
                definition_id: "d1".into(),
                definition_name: Some("nightly".into()),
                task_id: None,
                state: "started".into(),
                timestamp: Some("2026-01-01T00:00:00Z".into()),
                summary: None,
            });
        }

        #[test]
        fn permission_and_subagent_events_round_trip() {
            assert_round_trips(Event::PermissionDecided {
                hook_command: "cmd".into(),
                tool_name: "bash".into(),
                decision: "allow".into(),
            });
            assert_round_trips(Event::PermissionsUpdated {
                hook_command: "cmd".into(),
                mode_applied: Some("acceptEdits".into()),
                added_allow: vec!["read".into()],
                added_deny: vec!["bash".into()],
            });
            assert_round_trips(Event::SubagentBlocked {
                hook_command: "cmd".into(),
                task_id: "t1".into(),
                reason: "denied".into(),
            });
            assert_round_trips(Event::SubagentResultModified {
                hook_command: "cmd".into(),
                task_id: "t1".into(),
                original_result: "before".into(),
                modified_result: "after".into(),
            });
            assert_round_trips(Event::PromptRewritten {
                hook_command: "cmd".into(),
                original_prompt: "before".into(),
                modified_prompt: "after".into(),
            });
        }

        /// The C# host omits null-valued properties rather than emitting
        /// `null`, so absent optionals must not appear as keys at all.
        #[test]
        fn absent_optionals_are_omitted_not_null() {
            let (_, params) = Event::ThinkingComplete { elapsed_ms: 1, thinking_tokens: None }
                .to_notification()
                .expect("encodes");
            assert!(
                params.get("thinkingTokens").is_none(),
                "absent optional must be omitted, got {params}"
            );

            let (_, params) = Event::ToolCall {
                tool_name: "read".into(),
                input_json: "{}".into(),
                correlation: Correlation::default(),
            }
            .to_notification()
            .expect("encodes");
            for key in ["rootTurnId", "activityId", "callId", "sourceId"] {
                assert!(params.get(key).is_none(), "{key} must be omitted, got {params}");
            }
        }

        /// The method names must match the C# constants verbatim.
        #[test]
        fn encodes_to_the_documented_method_names() {
            let cases = [
                (Event::AssistantTextComplete, "event/assistantTextComplete"),
                (
                    Event::CompactionCancelled {
                        hook_command: String::new(),
                        trigger: String::new(),
                    },
                    "event/compactionCancelled",
                ),
                (
                    Event::PostCompactContextInjected { additional_context: String::new() },
                    "event/postCompactContextInjected",
                ),
            ];
            for (event, expected) in cases {
                let (method, _) = event.to_notification().expect("encodes");
                assert_eq!(method, expected);
            }
        }
    }

    use serde_json::json;

    fn parse_event(method: &str, params: serde_json::Value) -> Event {
        Event::parse(method, Some(&params))
    }

    #[test]
    fn parses_an_assistant_text_delta() {
        let event = parse_event(event_method::ASSISTANT_TEXT, json!({ "delta": "Hello" }));
        assert!(matches!(event, Event::AssistantText { delta } if delta == "Hello"));
    }

    #[test]
    fn parses_a_text_complete_event_with_empty_params() {
        let event = parse_event(event_method::ASSISTANT_TEXT_COMPLETE, json!({}));
        assert!(matches!(event, Event::AssistantTextComplete));
    }

    #[test]
    fn parses_an_event_with_no_params_at_all() {
        let event = Event::parse(event_method::ASSISTANT_TEXT_COMPLETE, None);
        assert!(matches!(event, Event::AssistantTextComplete));
    }

    #[test]
    fn parses_a_tool_call_with_flattened_correlation_ids() {
        let event = parse_event(
            event_method::TOOL_CALL,
            json!({
                "toolName": "read_file",
                "inputJson": "{\"path\":\"a.txt\"}",
                "rootTurnId": "t1",
                "activityId": "a1",
                "callId": "c1",
                "sourceId": "root:t1"
            }),
        );
        let Event::ToolCall {
            tool_name,
            input_json,
            correlation,
        } = event
        else {
            panic!("expected a tool call");
        };
        assert_eq!(tool_name, "read_file");
        assert_eq!(input_json, r#"{"path":"a.txt"}"#);
        assert!(correlation.is_complete());
        assert_eq!(correlation.call_id.as_deref(), Some("c1"));
    }

    #[test]
    fn parses_a_tool_call_that_omits_correlation_ids() {
        let event = parse_event(
            event_method::TOOL_CALL,
            json!({ "toolName": "glob", "inputJson": "{}" }),
        );
        let Event::ToolCall { correlation, .. } = event else {
            panic!("expected a tool call");
        };
        assert!(!correlation.is_complete());
        assert!(correlation.call_id.is_none());
    }

    #[test]
    fn parses_a_tool_result_status() {
        let event = parse_event(
            event_method::TOOL_RESULT,
            json!({
                "toolName": "run_command",
                "content": "done",
                "isError": false,
                "status": "Succeeded"
            }),
        );
        let Event::ToolResult { status, .. } = event else {
            panic!("expected a tool result");
        };
        assert_eq!(status, Some(ToolCallStatus::Succeeded));
        assert!(status.unwrap().is_terminal());
    }

    #[test]
    fn parses_a_failed_tool_result() {
        let event = parse_event(
            event_method::TOOL_RESULT,
            json!({ "toolName": "edit", "content": "boom", "isError": true }),
        );
        let Event::ToolResult {
            is_error, status, ..
        } = event
        else {
            panic!("expected a tool result");
        };
        assert!(is_error);
        assert!(status.is_none());
    }

    #[test]
    fn parses_an_interrupted_turn_complete() {
        let event = parse_event(
            event_method::TURN_COMPLETE,
            json!({ "interrupted": true, "rootTurnId": "t9" }),
        );
        let Event::TurnComplete {
            interrupted,
            stop_reason,
            root_turn_id,
            ..
        } = event
        else {
            panic!("expected turn complete");
        };
        assert!(interrupted);
        assert!(stop_reason.is_none());
        assert_eq!(root_turn_id.as_deref(), Some("t9"));
        assert!(Event::TurnComplete {
            stop_reason: None,
            interrupted: true,
            root_turn_id: None,
            activity_id: None
        }
        .ends_turn());
    }

    #[test]
    fn parses_usage_counters() {
        let event = parse_event(
            event_method::USAGE,
            json!({ "inputTokens": 1500, "outputTokens": 320 }),
        );
        assert!(matches!(
            event,
            Event::Usage {
                input_tokens: 1500,
                output_tokens: 320
            }
        ));
    }

    #[test]
    fn parses_a_limit_reached_event() {
        let event = parse_event(
            event_method::LIMIT_REACHED,
            json!({ "kind": "max_tool_iterations", "message": "too many steps" }),
        );
        assert!(matches!(event, Event::LimitReached { kind, .. } if kind == "max_tool_iterations"));
    }

    #[test]
    fn parses_steering_delivery_ids() {
        let event = parse_event(
            event_method::STEERING_DELIVERED,
            json!({ "messageIds": ["s1", "s2"] }),
        );
        assert!(matches!(event, Event::SteeringDelivered { message_ids } if message_ids.len() == 2));
    }

    #[test]
    fn parses_a_completed_background_task() {
        let event = parse_event(
            event_method::TASK_COMPLETED,
            json!({ "taskId": "t1", "status": "failed", "description": "build" }),
        );
        let Event::TaskCompleted { status, report, .. } = event else {
            panic!("expected task completed");
        };
        assert_eq!(status, "failed");
        assert!(report.is_none());
    }

    #[test]
    fn keeps_unknown_events_instead_of_failing() {
        let event = parse_event("event/somethingNew", json!({ "a": 1 }));
        let Event::Unknown { method, params } = event else {
            panic!("expected an unknown event");
        };
        assert_eq!(method, "event/somethingNew");
        assert_eq!(params.unwrap()["a"], 1);
    }

    #[test]
    fn tolerates_a_payload_whose_fields_have_the_wrong_type() {
        // A malformed payload degrades to defaults rather than crashing the UI.
        let event = parse_event(event_method::ASSISTANT_TEXT, json!({ "delta": 42 }));
        assert!(matches!(event, Event::AssistantText { delta } if delta.is_empty()));
    }

    #[test]
    fn every_terminal_status_is_reported_as_terminal() {
        for status in [
            ToolCallStatus::Succeeded,
            ToolCallStatus::Failed,
            ToolCallStatus::Cancelled,
            ToolCallStatus::Skipped,
        ] {
            assert!(status.is_terminal(), "{status:?} should be terminal");
        }
        for status in [
            ToolCallStatus::Pending,
            ToolCallStatus::AwaitingApproval,
            ToolCallStatus::Running,
        ] {
            assert!(!status.is_terminal(), "{status:?} should not be terminal");
        }
    }

    #[test]
    fn parses_subagent_result_modified() {
        // MINOR 8: was previously unhandled; the C# engine emits this event.
        let event = parse_event(
            event_method::SUBAGENT_RESULT_MODIFIED,
            json!({
                "hookCommand": "hook.sh",
                "taskId": "task-0001",
                "originalResult": "orig",
                "modifiedResult": "new"
            }),
        );
        let Event::SubagentResultModified {
            hook_command,
            task_id,
            original_result,
            modified_result,
        } = event
        else {
            panic!("expected SubagentResultModified, got {event:?}");
        };
        assert_eq!(hook_command, "hook.sh");
        assert_eq!(task_id, "task-0001");
        assert_eq!(original_result, "orig");
        assert_eq!(modified_result, "new");
    }
}
