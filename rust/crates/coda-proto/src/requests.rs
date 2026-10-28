//! Shared request and interaction-response payloads for the additive serve API.
//!
//! The server applies semantic validation (bounds, fences and pending-request
//! kind) after deserialization. These DTOs define the actual parser shape.

use serde::{Deserialize, Serialize};
use serde_json::Value;

/// Current Rust contract methods that require a completed handshake.
pub const INITIALIZATION_GATED_METHODS: &[&str] = &[
    crate::messages::method::GET_HISTORY,
    crate::messages::method::GET_PENDING_REQUESTS,
    crate::messages::method::RESOLVE_REQUEST,
    crate::messages::method::CANCEL_REQUEST,
    crate::messages::method::CONFIG_SET,
    crate::messages::method::BACKGROUND_ALL,
    crate::messages::method::STOP_ALL_TASKS,
    crate::messages::method::TASK_BACKGROUND,
    crate::messages::method::TASK_STOP,
    crate::messages::method::TASK_SEND,
    crate::messages::method::ASK_ASIDE,
];

/// Which tasks `session/listTasks` (and the model's `task_list`) return.
///
/// `active` is every task still `running` — foreground or background, with
/// or without output, including parked subagents. `terminal` is `completed`,
/// `failed` and `stopped`. `all` is both, the behaviour when no filter is
/// given. Filtering is presentation only: no record, output or completion is
/// affected.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum TaskStatusFilter {
    Active,
    Terminal,
    #[default]
    All,
}

impl TaskStatusFilter {
    /// Whether a task in a terminal (`true`) or running (`false`) state passes.
    pub fn admits(self, terminal: bool) -> bool {
        match self {
            TaskStatusFilter::Active => !terminal,
            TaskStatusFilter::Terminal => terminal,
            TaskStatusFilter::All => true,
        }
    }

    /// Parses the wire spelling; `None` for anything else.
    pub fn parse(value: &str) -> Option<Self> {
        match value {
            "active" => Some(TaskStatusFilter::Active),
            "terminal" => Some(TaskStatusFilter::Terminal),
            "all" => Some(TaskStatusFilter::All),
            _ => None,
        }
    }
}

/// `session/listTasks`. Every field is optional; `{}` lists all tasks.
#[derive(Debug, Clone, Serialize, Deserialize, Default, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ListTasksParams {
    /// Absent means `all`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub status_filter: Option<TaskStatusFilter>,
}

/// A side question (`session/askAside`).
#[derive(Debug, Clone, Serialize, Deserialize, Default, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct AskAsideParams {
    pub question: String,
}

/// Names one registered task (`task/background`, `task/stop`).
#[derive(Debug, Clone, Serialize, Deserialize, Default, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct TaskIdParams {
    pub task_id: String,
}

/// A user message for one running subagent (`task/send`).
#[derive(Debug, Clone, Serialize, Deserialize, Default, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct TaskSendParams {
    pub task_id: String,
    pub text: String,
}

#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SetModelParams {
    #[serde(default)]
    pub model: String,
}

#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct HooksInfoParams {
    #[serde(default)]
    pub index: i32,
}

#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct HooksTrustParams {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub project_path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hook_hash: Option<String>,
}

#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct RewindParams {
    /// Exchanges to remove. The legacy handler treats absence or zero as one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub n: Option<u32>,
}

#[derive(Debug, Serialize, Deserialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ForkParams {}

#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct GetStateParams {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sections: Option<Vec<String>>,
}

#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct GetEventsParams {
    #[serde(default)]
    pub engine_instance_id: String,
    #[serde(default)]
    pub after_cursor: i64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub limit: Option<i64>,
}

/// `session/pendingMessages` — non-destructive recovery of engine-owned user
/// notifications (Stage 2 `notify_user`).
///
/// `after_cursor` is the **bus's own cursor**, not an `EventBus` seq (see
/// `coda_agent::message` module docs) — `0` means "from the beginning".
/// Public and valid **before** `initialize`: an API-only external frontend
/// must be able to recover pending notifications without any local state.
///
/// `engine_instance_id`, when supplied, fences the read exactly like
/// `session/getHistory`'s: the bus is engine-process-scoped (a fresh process
/// starts a fresh bus at cursor `0`), so a cursor minted by a previous
/// process is not a position in the current one's ring. Supplying it lets a
/// client detect a silent engine replacement instead of reading a
/// coincidentally-valid cursor into the wrong process's notifications.
#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PendingMessagesParams {
    #[serde(default)]
    pub after_cursor: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub limit: Option<i64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub engine_instance_id: Option<String>,
}

#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct GetHistoryParams {
    /// Absent means the live session; saved IDs are scoped to the engine workspace.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub session_id: Option<String>,
    /// Exact history-reset fence; stale values are refused, not ignored.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub history_epoch: Option<i64>,
    /// Exact committed-length fence across pages of one logical read.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub expected_history_length: Option<i64>,
    /// Refuse a read from a replacement engine instance.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub engine_instance_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub since_index: Option<i64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub limit: Option<i64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub include_live: Option<bool>,
}

#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ListSessionsParams {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub limit: Option<i64>,
}

#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ResolveRequestParams {
    #[serde(default)]
    pub request_id: String,
    /// Validated against the pending request: `{allow}`, `{answer}` or `{approve}`.
    #[serde(default)]
    pub outcome: Value,
}

#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct CancelRequestParams {
    #[serde(default)]
    pub request_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

#[derive(Debug, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ConfigSetParams {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub value: Value,
}

#[derive(Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct GetPendingRequestsResult {
    pub requests: Vec<crate::state::PendingRequestDto>,
    pub engine_instance_id: String,
}

#[derive(Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum RequestResolutionState {
    Resolved,
}

#[derive(Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ResolveRequestResult {
    pub ok: bool,
    pub state: RequestResolutionState,
    pub request_id: String,
    pub outcome: String,
}

#[derive(Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct CancelRequestResult {
    pub ok: bool,
    pub request_id: String,
    pub applied_default: String,
    pub outcome: String,
    /// Echoes the optional reason; the existing response uses null when absent.
    pub reason: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn list_tasks_params_accept_the_three_filters_and_refuse_others() {
        let parse = |v: Value| serde_json::from_value::<ListTasksParams>(v);
        assert_eq!(parse(serde_json::json!({})).unwrap().status_filter, None);
        for (wire, filter) in [
            ("active", TaskStatusFilter::Active),
            ("terminal", TaskStatusFilter::Terminal),
            ("all", TaskStatusFilter::All),
        ] {
            let params = parse(serde_json::json!({ "statusFilter": wire })).unwrap();
            assert_eq!(params.status_filter, Some(filter));
            assert_eq!(TaskStatusFilter::parse(wire), Some(filter));
        }
        let error = parse(serde_json::json!({ "statusFilter": "done" })).unwrap_err().to_string();
        assert!(error.contains("done") && error.contains("active"), "{error}");
        assert_eq!(TaskStatusFilter::parse("Active"), None);
    }

    #[test]
    fn active_admits_only_running_and_terminal_only_finished_tasks() {
        assert!(TaskStatusFilter::Active.admits(false) && !TaskStatusFilter::Active.admits(true));
        assert!(TaskStatusFilter::Terminal.admits(true) && !TaskStatusFilter::Terminal.admits(false));
        assert!(TaskStatusFilter::All.admits(true) && TaskStatusFilter::All.admits(false));
    }
}
