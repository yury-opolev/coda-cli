//! The TUI's half of the public serve API.
//!
//! Everything in this module talks to the engine the way any other client
//! would: over the documented JSON-RPC contract, with no privileged access to
//! the engine's files or in-process types. That is the point — the terminal
//! front-end is the contract's first conformance client, so a gap in the API
//! shows up here as a missing feature rather than being quietly routed around
//! with a filesystem read.
//!
//! Layout:
//!
//! - [`view`] — the event fence (`seq`/`cursor`/`engineInstanceId`) and the
//!   decoded `stateEvents` frames.
//! - [`history`] — rebuilding the visible conversation from
//!   `session/getHistory` / `TurnState.liveEntries`.
//! - [`requests`] — outstanding permission/question/plan decisions, from both
//!   the raw round-trip and out-of-band discovery.
//! - [`boot`] — the bootstrap order every front-end shares: spawn the core,
//!   discover sessions read-only, `initialize`, then fork if asked.
//!
//! This module is deliberately separate from `app/`: `app/mod.rs` is the event
//! loop and is held to a line ceiling by `tests/conventions.rs`, and "how the
//! API works" is its own responsibility rather than more of the loop's.

pub mod boot;
pub mod history;
pub mod requests;
pub mod view;

use coda_client::{ClientError, Connection};
use coda_proto::config::ConfigDescribeResult;
use coda_proto::history::{GetHistoryResult, ListSessionsResult};
use coda_proto::mcp::McpListResult;
use coda_proto::messages::method;
use coda_proto::state::{PendingRequestDto, StateSnapshot};
use serde_json::{json, Value};

pub use view::{client_capabilities, Frame, Reception, ServeView, StateFrame};

/// Issues a request and deserialises its result.
async fn fetch<T: serde::de::DeserializeOwned>(
    connection: &Connection,
    rpc_method: &str,
    params: Option<Value>,
) -> Result<T, ClientError> {
    let value = connection.request(rpc_method, params).await?;
    serde_json::from_value(value).map_err(ClientError::Serde)
}

/// `session/getState` — the authoritative snapshot, with the cursor it is
/// exact at.
pub async fn get_state(connection: &Connection) -> Result<StateSnapshot, ClientError> {
    fetch(connection, method::GET_STATE, Some(json!({}))).await
}

/// One bounded, fenced read of the live conversation.
///
/// Every field is explicit because every default is wrong for a client that
/// wants to *show* a conversation: omitting `sinceIndex` asks for the oldest
/// page, and omitting the fences asks the engine to serve a window from a
/// conversation that may already have been replaced.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct HistoryWindow {
    /// Refuses the read unless it is still the same process.
    pub engine_instance_id: Option<String>,
    /// Refuses the read unless it is still the same conversation.
    pub history_epoch: Option<i64>,
    /// Refuses the read unless the committed count is still what the client
    /// computed its window from.
    pub expected_history_length: Option<i64>,
    /// Where the window starts. `Some(0)` is the oldest page; the tail is
    /// [`HistoryWindow::tail_start`].
    pub since_index: Option<i64>,
    pub limit: Option<i64>,
}

impl HistoryWindow {
    /// Where a window of `limit` entries must start to end at the newest one.
    pub fn tail_start(history_length: i64, limit: i64) -> i64 {
        (history_length - limit.max(1)).max(0)
    }

    fn params(&self) -> serde_json::Value {
        let mut params = json!({ "includeLive": true });
        let object = params.as_object_mut().expect("an object");
        if let Some(instance) = &self.engine_instance_id {
            object.insert("engineInstanceId".into(), json!(instance));
        }
        for (key, value) in [
            ("historyEpoch", self.history_epoch),
            ("expectedHistoryLength", self.expected_history_length),
            ("sinceIndex", self.since_index),
            ("limit", self.limit),
        ] {
            if let Some(value) = value {
                object.insert(key.into(), json!(value));
            }
        }
        params
    }
}

/// `session/getHistory` for the live session, including the in-flight turn.
///
/// The window is stated in full rather than left to the engine's defaults: a
/// read with no `sinceIndex` returns the *oldest* page, which is exactly the
/// opposite of what a screen showing a conversation needs.
pub async fn get_history(
    connection: &Connection,
    window: &HistoryWindow,
) -> Result<GetHistoryResult, ClientError> {
    fetch(connection, method::GET_HISTORY, Some(window.params())).await
}

/// `session/listSessions` — saved transcripts in this workspace.
///
/// Valid **before** `initialize`, which is what lets `--resume`/`--continue`
/// pick a session without the front-end reading `.coda/sessions` itself.
pub async fn list_sessions(
    connection: &Connection,
    limit: Option<i64>,
) -> Result<ListSessionsResult, ClientError> {
    let params = match limit {
        Some(limit) => json!({ "limit": limit }),
        None => json!({}),
    };
    fetch(connection, method::LIST_SESSIONS, Some(params)).await
}

/// `session/getPendingRequests` — what the engine is currently waiting on.
pub async fn get_pending_requests(
    connection: &Connection,
) -> Result<Vec<PendingRequestDto>, ClientError> {
    let value = connection.request(method::GET_PENDING_REQUESTS, Some(json!({}))).await?;
    let requests = value.get("requests").cloned().unwrap_or_else(|| json!([]));
    serde_json::from_value(requests).map_err(ClientError::Serde)
}

/// `session/resolveRequest` — answer a request whose raw round-trip this
/// client does not own.
pub async fn resolve_request(
    connection: &Connection,
    request_id: &str,
    outcome: Value,
) -> Result<Value, ClientError> {
    connection
        .request(
            method::RESOLVE_REQUEST,
            Some(json!({ "requestId": request_id, "outcome": outcome })),
        )
        .await
}

/// `session/cancelRequest` — apply the fail-closed default and stop waiting.
pub async fn cancel_request(
    connection: &Connection,
    request_id: &str,
    reason: &str,
) -> Result<Value, ClientError> {
    connection
        .request(
            method::CANCEL_REQUEST,
            Some(json!({ "requestId": request_id, "reason": reason })),
        )
        .await
}

/// `config/describe` — the engine's own catalogue of configuration keys.
pub async fn config_describe(
    connection: &Connection,
) -> Result<ConfigDescribeResult, ClientError> {
    fetch(connection, method::CONFIG_DESCRIBE, Some(json!({}))).await
}

/// `mcp/list` — read-only MCP server inventory as the engine sees it.
pub async fn mcp_list(connection: &Connection) -> Result<McpListResult, ClientError> {
    fetch(connection, method::MCP_LIST, Some(json!({}))).await
}

/// The allowed values `config/describe` publishes for one key.
///
/// Returns `None` when the engine does not describe the key at all, which is
/// different from "the key has no allowed values" and must not be flattened
/// into an empty list.
pub fn allowed_values<'a>(
    described: &'a ConfigDescribeResult,
    key: &str,
) -> Option<&'a [coda_proto::config::AllowedValue]> {
    described
        .entries
        .iter()
        .find(|entry| entry.key == key)
        .and_then(|entry| entry.allowed_values.as_deref())
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_proto::config::{AllowedValue, AppliesWhen, ConfigEntryDto, ConfigOwner};

    fn described(entries: Vec<ConfigEntryDto>) -> ConfigDescribeResult {
        ConfigDescribeResult { entries }
    }

    fn entry(key: &str, allowed: Option<Vec<AllowedValue>>) -> ConfigEntryDto {
        ConfigEntryDto {
            key: key.into(),
            owner: ConfigOwner::Session,
            applies_at: AppliesWhen::NextTurn,
            mutable: true,
            reason: None,
            value: None,
            active_value: None,
            allowed_values: allowed,
            allowed_values_from: None,
            description: String::new(),
        }
    }

    #[test]
    fn an_undescribed_key_is_unknown_rather_than_empty() {
        // "The engine never mentioned this key" and "this key accepts nothing"
        // are different answers, and only one of them should silence a menu.
        let catalog = described(vec![entry("model", None)]);
        assert!(allowed_values(&catalog, "outputStyle").is_none());
    }

    #[test]
    fn described_allowed_values_are_returned_verbatim() {
        let catalog = described(vec![entry(
            "outputStyle",
            Some(vec![AllowedValue::described("concise", "Short answers")]),
        )]);
        let values = allowed_values(&catalog, "outputStyle").expect("described");
        assert_eq!(values.len(), 1);
        assert_eq!(values[0].value, "concise");
        assert_eq!(values[0].description.as_deref(), Some("Short answers"));
    }
}
