//! Machine-readable inventory of RPC directions and their schema contracts.

use serde_json::{json, Value};
use crate::events::event_method as e;
use crate::messages::{method as m, server_method as s, CONTRACT_VERSION, PROTOCOL_VERSION};

#[derive(Clone, Copy)]
enum Shape {
    Client,
    Shared,
}

fn path(name: &str) -> String {
    format!("schemas/{name}.json")
}

fn rpc(method: &str, params: Option<&str>, result: Option<&str>, input: Shape, output: Shape) -> Value {
    let availability = match method {
        m::SKILLS_TRUST => "unsupported",
        m::PLUGINS_LIST | m::HISTORY | m::MESSAGES => "limited",
        _ => "reusable",
    };
    json!({
        "method": method,
        "paramsSchema": params.map(path),
        "resultSchema": result.map(path),
        "paramsContract": params.map(|_| match input {
            Shape::Client => "clientWriter",
            Shape::Shared => "sharedParser",
        }),
        "resultContract": result.map(|_| match output {
            Shape::Client => "clientReader",
            Shape::Shared => "sharedServerDto",
        }),
        "requiresInitialize": crate::requests::INITIALIZATION_GATED_METHODS.contains(&method),
        "availability": availability,
    })
}

fn event(method: &str, gated: bool) -> Value {
    let name = method.strip_prefix("event/").expect("event method constant");
    let mut chars = name.chars();
    let stem = format!("{}{}", chars.next().unwrap().to_ascii_uppercase(), chars.as_str());
    json!({
        "method": method,
        "paramsSchema": path(&format!("{stem}Event")),
        "requiresStateEvents": gated,
        "paramsContract": if gated { "sharedPublisherWithMetadata" } else { "clientReaderWithRustMetadata" },
    })
}

pub(super) fn document() -> Value {
    use Shape::{Client as C, Shared as S};
    let methods = vec![
        rpc(m::INITIALIZE, Some("InitializeParams"), Some("InitializeResponse"), C, S),
        rpc(m::SHUTDOWN, None, Some("OkResult"), C, C),
        rpc(m::PROMPT, Some("PromptParams"), Some("PromptResult"), C, C),
        rpc(m::INTERRUPT, None, Some("OkResult"), C, C),
        rpc(m::STEER, Some("SteerParams"), Some("SteerResult"), C, C),
        rpc(m::RECALL_STEERING, None, Some("RecallSteeringResult"), C, C),
        rpc(m::HISTORY, None, Some("HistoryResult"), C, C),
        rpc(m::MESSAGES, Some("MessagesParams"), Some("MessagesResult"), C, C),
        rpc(m::MODELS, Some("ModelsParams"), Some("ModelsResult"), C, C),
        rpc(m::SET_MODEL, Some("SetModelParams"), Some("SetModelResult"), S, S),
        rpc(m::SET_GOAL, Some("SetGoalParams"), Some("SetGoalResult"), C, C),
        rpc(m::SET_EFFORT, Some("SetEffortParams"), Some("SetEffortResult"), C, C),
        rpc(m::ADJUST_MODEL_EFFORT, Some("ModelAdjustEffortParams"), Some("ModelEffortResult"), C, C),
        rpc(m::REASONING_CAPABILITY, None, Some("ReasoningCapabilityResult"), C, C),
        rpc(m::SET_PERMISSION_MODE, Some("SetPermissionModeParams"), Some("SetPermissionModeResult"), C, C),
        rpc(m::SET_SYSTEM_PROMPT, Some("SetSystemPromptParams"), Some("SetSystemPromptResult"), C, C),
        rpc(m::SCHEDULE_LIST, None, Some("ScheduleListResult"), C, C),
        rpc(m::SCHEDULE_CREATE, Some("ScheduleCreateParams"), Some("ScheduledTask"), C, C),
        rpc(m::SCHEDULE_DELETE, Some("ScheduleDeleteParams"), Some("OkResult"), C, C),
        rpc(m::HOOKS_LIST, None, Some("HooksListResult"), C, C),
        rpc(m::HOOKS_INFO, Some("HooksInfoParams"), Some("HooksInfoResult"), S, C),
        rpc(m::HOOKS_TRUST, Some("HooksTrustParams"), Some("HooksTrustResult"), S, S),
        rpc(m::SKILLS_LIST, None, Some("SkillsListResult"), C, C),
        rpc(m::SKILLS_TRUST, None, None, C, C),
        rpc(m::PLUGINS_LIST, None, Some("PluginsListResult"), C, C),
        rpc(m::COMPACT, Some("CompactParams"), Some("CompactResult"), C, C),
        rpc(m::FORK, Some("ForkParams"), Some("ForkResponse"), S, S),
        rpc(m::REWIND, Some("RewindParams"), Some("RewindResponse"), S, S),
        rpc(m::GET_STATE, Some("GetStateParams"), Some("StateSnapshot"), S, S),
        rpc(m::GET_EVENTS, Some("GetEventsParams"), Some("GetEventsResult"), S, S),
        rpc(m::GET_HISTORY, Some("GetHistoryParams"), Some("GetHistoryResult"), S, S),
        rpc(m::LIST_SESSIONS, Some("ListSessionsParams"), Some("ListSessionsResult"), S, S),
        rpc(m::GET_PENDING_REQUESTS, None, Some("GetPendingRequestsResult"), S, S),
        rpc(m::RESOLVE_REQUEST, Some("ResolveRequestParams"), Some("ResolveRequestResult"), S, S),
        rpc(m::CANCEL_REQUEST, Some("CancelRequestParams"), Some("CancelRequestResult"), S, S),
        rpc(m::CONFIG_DESCRIBE, None, Some("ConfigDescribeResult"), S, S),
        rpc(m::CONFIG_SET, Some("ConfigSetParams"), Some("ConfigSetResult"), S, S),
        rpc(m::MCP_LIST, None, Some("McpListResult"), S, S),
    ];
    let mut events: Vec<_> = [
        e::ASSISTANT_TEXT, e::ASSISTANT_TEXT_COMPLETE, e::THINKING, e::THINKING_COMPLETE,
        e::TOOL_CALL, e::TOOL_PROGRESS, e::TOOL_RESULT, e::TURN_COMPLETE, e::STOP,
        e::USAGE, e::ERROR, e::LIMIT_REACHED, e::STREAM_PROGRESS, e::STEERING_DELIVERED,
        e::TASK_COMPLETED, e::SCHEDULE_LIFECYCLE, e::PROMPT_REWRITTEN, e::RESPONSE_REWRITTEN,
        e::TOOL_INPUT_MODIFIED, e::TOOL_RESULT_MODIFIED, e::PERMISSION_DECIDED,
        e::PERMISSIONS_UPDATED, e::SUBAGENT_BLOCKED, e::SUBAGENT_RESULT_MODIFIED,
        e::COMPACTION_CANCELLED, e::POST_COMPACT_CONTEXT_INJECTED,
    ].into_iter().map(|method| event(method, false)).collect();
    events.extend([
        e::ACTIVITY, e::TURN_ENDED, e::LIFECYCLE, e::CONFIG_CHANGED, e::STEERING_QUEUE,
        e::SESSION_CHANGED, e::EVENTS_DROPPED, e::REQUEST_PENDING, e::REQUEST_RESOLVED,
    ].into_iter().map(|method| event(method, true)));
    let server_requests: Vec<_> = [
        (s::PERMISSION, "PermissionRequest", "PermissionResponse", "deny"),
        (s::QUESTION, "QuestionRequest", "QuestionResponse", "noAnswer"),
        (s::PLAN_APPROVAL, "PlanApprovalRequest", "PlanApprovalResponse", "reject"),
    ].into_iter().map(|(method, params, result, cancel)| json!({
        "method": method,
        "paramsSchema": path(params),
        "resultSchema": path(result),
        "cancelDefault": cancel,
    })).collect();
    json!({
        "format": "coda-serve-catalog/1",
        "protocolVersion": PROTOCOL_VERSION,
        "contractVersion": CONTRACT_VERSION,
        "transport": "contentLengthFramedStdio",
        "methods": methods,
        "serverRequests": server_requests,
        "events": events,
        "supportSchemas": [
            { "schema": path("EventEnvelope"), "role": "replayEnvelope" },
            { "schema": path("InitializeResult"), "role": "legacyCompatibleInitializeReader" }
        ],
        "notes": [
            "Schema links describe typed payloads, not JSON-RPC envelopes or every semantic validation.",
            "Legacy client writer/reader shapes retain compatibility defaults; shared DTOs are used by the current engine.",
            "Inspect ok as well as JSON-RPC errors: some legacy setters refuse through ok:false.",
            "Availability describes the implementation, not current credentials or provider connectivity.",
            "plugins/list is a compatibility stub; skills/trust is always refused.",
            "Question cancellation is a JSON-RPC error or cancelRequest, never an invented answer."
        ]
    })
}
