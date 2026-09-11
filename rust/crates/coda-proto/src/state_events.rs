//! Payloads of the capability-gated state notifications.
//!
//! The state publisher serializes these records; the bus adds `seq` and
//! `engineInstanceId`. Schemas use `Sequenced<T>` to describe the complete
//! notification params, not the JSON-RPC envelope.

use serde::{Deserialize, Serialize};

use crate::state::{
    ActiveConfig, ActivityPhase, ConfigDifference, EngineLifecycle,
    PendingRequestDto, PendingRequestKind, TurnErrorSummary,
};

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Sequenced<T> {
    pub seq: i64,
    pub engine_instance_id: String,
    #[serde(flatten)]
    pub payload: T,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ActivityEvent {
    pub turn_id: String,
    pub phase: ActivityPhase,
    pub phase_since: String,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct LifecycleEvent {
    pub lifecycle: EngineLifecycle,
    pub initialized: bool,
}

pub type SteeringQueueEvent = crate::state::SteeringQueueState;

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ActiveConfigChanged {
    pub turn_id: String,
    pub active: ActiveConfig,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct NextConfigChanged {
    pub key: String,
    pub next: ActiveConfig,
    pub active: Option<ActiveConfig>,
    pub differing: Vec<ConfigDifference>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(untagged)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum ConfigChangedEvent {
    Active(ActiveConfigChanged),
    Next(NextConfigChanged),
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SessionChangedEvent {
    pub reason: String,
    pub session_id: String,
    pub history_epoch: i64,
    pub history_length: i64,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct TurnEndedEvent {
    pub turn_id: String,
    pub ended_at: String,
    pub stop_reason: Option<String>,
    pub interrupted: bool,
    pub error: Option<TurnErrorSummary>,
    pub history_epoch: i64,
    pub history_length: i64,
    pub finalized_call_ids: Vec<String>,
    pub tools_truncated: bool,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct RequestPendingEvent {
    pub request: PendingRequestDto,
    pub requests: Vec<PendingRequestDto>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct RequestResolvedEvent {
    pub request_id: String,
    pub kind: PendingRequestKind,
    pub outcome: String,
    pub requests: Vec<PendingRequestDto>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct EventsDroppedEvent {
    pub from_cursor: i64,
    pub to_cursor: i64,
    pub reason: String,
}

/// Payload of `event/agentMessage` (Stage 2 `notify_user`). Same shape as
/// `session/pendingMessages`' list entries (`AgentMessageDto`) so a client
/// can merge live and recovered notifications with one type.
pub type AgentMessageEvent = crate::responses::AgentMessageDto;
