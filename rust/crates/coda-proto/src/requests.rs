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
];

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
