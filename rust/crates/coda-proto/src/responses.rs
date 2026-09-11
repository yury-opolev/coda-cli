//! Shared response shapes for model, hook and session mutations.

use serde::{Deserialize, Serialize};

#[derive(Debug, Serialize, Deserialize)]
#[serde(untagged)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum SetModelResult {
    Selected { ok: bool, model: String, effort: Option<String> },
    Refused { ok: bool, note: String },
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct HooksTrustResult {
    pub ok: bool,
    pub project_path: String,
    pub hook_hash: String,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ForkResponse {
    pub ok: bool,
    pub new_session_id: String,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct RewindResponse {
    pub ok: bool,
    pub removed: usize,
    pub remaining: usize,
}

/// One engine-owned user notification (Stage 2 `notify_user`), as returned by
/// `session/pendingMessages` and carried in `event/agentMessage`.
///
/// `cursor` is the message bus's own cursor — NOT the `EventBus` seq that
/// wraps the live notification. `source` is one of `"scheduledTask"` |
/// `"subagent"` | `"main"`. `taskId`/`scheduleDefinitionId` are the *trusted*
/// provenance an external client can correlate against `TaskManager`/schedule
/// state — `label` is a display string only and is not guaranteed unique.
/// Receiving this only means the bus accepted the notification; it never
/// means the user has seen it.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct AgentMessageDto {
    pub id: String,
    pub cursor: i64,
    pub label: String,
    pub text: String,
    pub context: Option<String>,
    pub source: String,
    /// The trusted task id this notification is scoped to (`None` for
    /// `source == "main"`, which is not a task at all).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub task_id: Option<String>,
    /// The scheduled definition id this notification originated from, set
    /// exactly when `source == "scheduledTask"`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schedule_definition_id: Option<String>,
}

/// Exact bounds of notifications this client lost to ring eviction before it
/// could read them — see `coda_agent::message::DroppedRange`. Present exactly
/// when `gap` is `true`.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct DroppedRangeDto {
    pub from: i64,
    pub to: i64,
    pub count: i64,
}

/// Result of `session/pendingMessages`.
///
/// `gap: true` means some notifications between the caller's `afterCursor`
/// and the oldest one still retained were evicted and can never be
/// recovered — reported honestly rather than silently resuming, with the
/// exact evicted range/count in `dropped`.
/// `truncated: true` means more messages exist beyond this page; call again
/// with `nextCursor`.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PendingMessagesResult {
    pub messages: Vec<AgentMessageDto>,
    pub next_cursor: i64,
    pub gap: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub dropped: Option<DroppedRangeDto>,
    pub truncated: bool,
}
