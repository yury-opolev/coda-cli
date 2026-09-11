//! Pure method routing: `(method, params) -> Result<Value, RpcError>`.
//!
//! This module is I/O free and has no dependency on the agent, a process, or
//! any async runtime beyond what `#[async_trait]` needs.  All tests use a
//! fake backend; no real session, transport, or LLM is involved.

use async_trait::async_trait;
use serde::Deserialize;
use serde_json::Value;

pub use coda_proto::requests::{
    CancelRequestParams, ConfigSetParams, GetEventsParams, GetHistoryParams,
    GetStateParams, ListSessionsParams, PendingMessagesParams, ResolveRequestParams, SetModelParams,
    HooksInfoParams, HooksTrustParams, ForkParams, RewindParams,
};

// ─────────────────────────────────────────────────────────────────────────────
// Error
// ─────────────────────────────────────────────────────────────────────────────

/// A JSON-RPC protocol error returned by backend methods or the dispatcher.
#[derive(Debug, Clone, PartialEq)]
pub struct RpcError {
    pub code: i64,
    pub message: String,
}

impl RpcError {
    pub fn method_not_found(method: &str) -> Self {
        Self { code: -32601, message: format!("Method not found: {method}") }
    }
    pub fn invalid_params(msg: impl Into<String>) -> Self {
        Self { code: -32602, message: msg.into() }
    }
    pub fn internal(msg: impl Into<String>) -> Self {
        Self { code: -32603, message: msg.into() }
    }
    pub fn cancelled() -> Self {
        Self { code: -32603, message: "cancelled".into() }
    }
    pub fn unauthorized(msg: impl Into<String>) -> Self {
        Self { code: -32001, message: msg.into() }
    }
    pub fn session_not_found() -> Self {
        Self { code: -32002, message: "Session not found".into() }
    }
    /// `skills/trust` is always refused in serve mode.
    pub fn skills_trust_refused() -> Self {
        Self { code: -32600, message: "skills/trust is not permitted in serve mode".into() }
    }
    /// `hooks/trust` validation failure (e.g. hash mismatch).
    pub fn hooks_trust_invalid(msg: impl Into<String>) -> Self {
        Self { code: -32600, message: msg.into() }
    }
    /// A state-dependent method was called before `initialize` (§2.7).
    ///
    /// Read-only discovery is deliberately still allowed, and the
    /// pre-existing legacy routes keep the behaviour they have always had —
    /// this gate is applied to the new Stage D methods only.
    pub fn not_initialized(method: &str) -> Self {
        Self {
            code: coda_proto::messages::error_code::NOT_INITIALIZED,
            message: format!(
                "{method} requires an initialized session; call `initialize` first"
            ),
        }
    }
    /// `session/getHistory` was asked for an epoch the engine has moved past.
    pub fn stale_epoch(requested: i64, current: i64) -> Self {
        Self {
            code: coda_proto::messages::error_code::STALE_EPOCH,
            message: format!(
                "historyEpoch {requested} is stale (the engine is at {current}); \
                 fork/rewind/compact/resume invalidated those indices — re-read from index 0"
            ),
        }
    }
    /// A page was requested against a committed-history fence that has since
    /// moved, so continuing would duplicate or skip content.
    pub fn history_fence_moved(expected: i64, current: i64) -> Self {
        Self {
            code: coda_proto::messages::error_code::HISTORY_FENCE_MOVED,
            message: format!(
                "expectedHistoryLength {expected} no longer matches the committed length \
                 {current}; a turn completed mid-read — re-snapshot before continuing"
            ),
        }
    }
    pub fn unknown_request(msg: impl Into<String>) -> Self {
        Self { code: coda_proto::messages::error_code::UNKNOWN_REQUEST, message: msg.into() }
    }
    pub fn request_kind_mismatch(msg: impl Into<String>) -> Self {
        Self {
            code: coda_proto::messages::error_code::REQUEST_KIND_MISMATCH,
            message: msg.into(),
        }
    }
    pub fn instance_changed(msg: impl Into<String>) -> Self {
        Self { code: error_code::INSTANCE_CHANGED, message: msg.into() }
    }
}

/// Coda-specific JSON-RPC error codes used outside the `RpcError` factory
/// methods (e.g. by `EventBus::get_events`, which needs a typed code the
/// client can match on without stringly-typed message parsing).
pub mod error_code {
    /// `session/getEvents` was called with an `engineInstanceId` that no
    /// longer matches the running process — never a silent replay of another
    /// process's stream.
    pub const INSTANCE_CHANGED: i64 = -32010;
    pub use coda_proto::messages::error_code::{
        HISTORY_FENCE_MOVED, NOT_INITIALIZED, REQUEST_KIND_MISMATCH, STALE_EPOCH, UNKNOWN_REQUEST,
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Param structs
//
// Lightweight structs capturing only what dispatch needs.  Optional fields
// default to `None` so a missing key does not error on its own.
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct InitParams {
    #[serde(default)]
    pub protocol_version: String,
    #[serde(default)]
    pub client_info: Option<String>,
    #[serde(default)]
    pub api_key: Option<String>,
    #[serde(default)]
    pub session_id: Option<String>,
    /// Additive, optional (§2.1): absent means legacy behaviour, no gated
    /// event method is ever emitted for this connection.
    #[serde(default)]
    pub client_capabilities: Option<coda_proto::messages::ClientCapabilities>,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct PromptParams {
    #[serde(default)]
    pub text: Option<String>,
    #[serde(default)]
    pub images: Option<Vec<Value>>,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct SteerParams {
    #[serde(default)]
    pub text: String,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct MessagesParams {
    #[serde(default)]
    pub since_index: i32,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ModelsParams {
    #[serde(default)]
    pub refresh: bool,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct SetGoalParams {
    #[serde(default)]
    pub goal: Option<String>,
    #[serde(default)]
    pub max_duration: Option<String>,
    #[serde(default)]
    pub max_continuations: Option<i32>,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct SetEffortParams {
    #[serde(default)]
    pub effort: Option<String>,
    /// Canonical model the caller believed was active; when it no longer
    /// matches the live model the engine rejects the call without mutating.
    #[serde(default)]
    pub expected_model: Option<String>,
    /// Provider the caller believed was connected; guarded like `expected_model`.
    #[serde(default)]
    pub expected_provider: Option<String>,
}

/// `model/adjustEffort` — steps a *specific* model's reasoning-effort level up
/// or down by one rung, server-authoritatively.
///
/// Unlike `session/setEffort`, this never activates or changes the active model:
/// it edits the target model's own per-model preference, and only touches the
/// live level when the target happens to be the active model. The engine owns
/// the ladder (`[auto?, levels…]`) and clamps at the ends rather than wrapping.
#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct AdjustEffortParams {
    /// The model whose effort to step. Must be a model the engine knows (live
    /// list or catalogue), not an arbitrary string.
    #[serde(default)]
    pub model: String,
    /// `-1` steps toward automatic/lower, `+1` toward higher. Any other value
    /// is rejected as invalid params.
    #[serde(default)]
    pub direction: i32,
    /// The provider the caller believed was connected; when it no longer
    /// matches, the call is rejected without mutating anything.
    #[serde(default)]
    pub expected_provider: Option<String>,
}

/// `session/setPermissionMode` — the live mode for the running session.
#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct SetPermissionModeParams {
    #[serde(default)]
    pub mode: String,
}

/// `session/setSystemPrompt` — override the system prompt for this session only.
/// `None` or empty text clears any existing override.
#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct SetSystemPromptParams {
    #[serde(default)]
    pub text: Option<String>,
}

/// `session/scheduleCreate` — `prompt` is required on the wire.
///
/// The bound fields are deliberately typed loosely: `max_runs` arrives as raw
/// JSON so that a negative, fractional or out-of-range value reaches the shared
/// validator and is refused with exactly the same message the `schedule_create`
/// tool produces, rather than being coerced by one deserializer and rejected by
/// the other.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScheduleCreateParams {
    pub prompt: String,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub every: Option<String>,
    #[serde(default)]
    pub at: Option<String>,
    #[serde(default)]
    pub cron: Option<String>,
    #[serde(default)]
    pub time_zone: Option<String>,
    /// Absolute ISO-8601 deadline. Mutually exclusive with `expires_in`.
    #[serde(default)]
    pub expires_at: Option<String>,
    /// Relative deadline (`30m`, `2h`, `7d`), resolved once at creation.
    #[serde(default)]
    pub expires_in: Option<String>,
    /// Accepted launch attempts before the schedule stops itself.
    #[serde(default)]
    pub max_runs: Option<Value>,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ScheduleDeleteParams {
    #[serde(default)]
    pub id: Option<String>,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct CompactParams {
    /// Optional override for the summarisation system prompt.
    #[serde(default)]
    pub instructions: Option<String>,
}

/// Server-side pagination validation stays with the advertised engine limits;
/// the deserialized wire types are shared with clients and schema generation.
pub trait NormalizeQuery {
    fn normalise(&mut self) -> Result<(), RpcError>;
}

impl NormalizeQuery for GetHistoryParams {
    /// Rejects nonsensical page requests and clamps an over-large one.
    ///
    /// A `limit` of `0` or a negative one used to fall through to the default
    /// page size, so a client that computed a bad limit silently got 100
    /// entries and no signal. It is now a typed `-32602`. Likewise a negative
    /// index or fence: those are not positions in any conversation.
    ///
    /// Over-max is deliberately **clamped**, not refused — the ceiling is
    /// advertised as `limits.maxHistoryPage` and `truncated`/`nextIndex`
    /// already tell a client there is more to fetch.
    fn normalise(&mut self) -> Result<(), RpcError> {
        self.limit = normalise_limit(self.limit, crate::history::MAX_HISTORY_LIMIT)?;
        reject_negative("sinceIndex", self.since_index)?;
        reject_negative("expectedHistoryLength", self.expected_history_length)?;
        reject_negative("historyEpoch", self.history_epoch)?;
        Ok(())
    }
}

/// A supplied page size must be at least 1; an over-large one is clamped to
/// the advertised ceiling.
fn normalise_limit(limit: Option<i64>, max: i64) -> Result<Option<i64>, RpcError> {
    match limit {
        None => Ok(None),
        Some(n) if n < 1 => Err(RpcError::invalid_params(format!(
            "limit must be at least 1 (got {n}); omit it to use the default page size"
        ))),
        Some(n) => Ok(Some(n.min(max))),
    }
}

fn reject_negative(field: &str, value: Option<i64>) -> Result<(), RpcError> {
    match value {
        Some(n) if n < 0 => {
            Err(RpcError::invalid_params(format!("{field} must not be negative (got {n})")))
        }
        _ => Ok(()),
    }
}

impl NormalizeQuery for ListSessionsParams {
    fn normalise(&mut self) -> Result<(), RpcError> {
        self.limit = normalise_limit(self.limit, crate::history::MAX_SESSION_LIMIT)?;
        Ok(())
    }
}

/// Ceiling for `session/pendingMessages`' `limit`. Matches the message bus's
/// own default ring capacity (`coda_agent::message::DEFAULT_RING_CAPACITY`):
/// asking for more than the ring can ever hold is never meaningful.
pub const MAX_PENDING_MESSAGES_LIMIT: i64 = coda_agent::message::DEFAULT_RING_CAPACITY as i64;

impl NormalizeQuery for PendingMessagesParams {
    /// A `limit` of `0` used to fall through to "no limit" inside the bus
    /// itself (see `coda_agent::message::MessageBus::user_since`'s own
    /// defense-in-depth fix) — here it is refused outright with a typed
    /// `-32602`, exactly like `session/getHistory`'s `limit`, so a caller
    /// never has to guess whether an empty page meant "caught up" or "asked
    /// for zero by mistake".
    fn normalise(&mut self) -> Result<(), RpcError> {
        self.limit = normalise_limit(self.limit, MAX_PENDING_MESSAGES_LIMIT)?;
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ServeBackend trait — one method per protocol operation
// ─────────────────────────────────────────────────────────────────────────────

/// One method per server operation.  Implementations may be real
/// (agent-backed) or fake (test).  The `dispatch` function handles JSON
/// deserialization and error mapping; these methods receive typed params and
/// return an already-serializable `Value`.
///
/// `skills/trust` is NOT a backend method: the dispatcher always returns
/// `-32600` without calling any backend.
#[async_trait]
pub trait ServeBackend: Send + Sync {
    async fn initialize(&self, p: InitParams) -> Result<Value, RpcError>;
    async fn shutdown(&self) -> Result<Value, RpcError>;
    async fn session_prompt(&self, p: PromptParams) -> Result<Value, RpcError>;
    async fn session_interrupt(&self) -> Result<Value, RpcError>;
    async fn session_steer(&self, p: SteerParams) -> Result<Value, RpcError>;
    async fn session_recall_steering(&self) -> Result<Value, RpcError>;
    async fn session_history(&self) -> Result<Value, RpcError>;
    async fn session_messages(&self, p: MessagesParams) -> Result<Value, RpcError>;
    async fn session_models(&self, p: ModelsParams) -> Result<Value, RpcError>;
    /// Authoritative snapshot of engine state (Slice 0 / Stage C, §2.2).
    /// Valid before `initialize` (read-only discovery, §2.7).
    async fn session_get_state(&self, p: GetStateParams) -> Result<Value, RpcError>;
    /// Bounded event replay with an explicit gap/`oldestAvailableCursor`
    /// (§2.2, §2.5). Valid before `initialize`.
    async fn session_get_events(&self, p: GetEventsParams) -> Result<Value, RpcError>;
    async fn session_set_goal(&self, p: SetGoalParams) -> Result<Value, RpcError>;
    async fn session_set_effort(&self, p: SetEffortParams) -> Result<Value, RpcError>;
    /// Steps a specific model's reasoning-effort level up or down one rung,
    /// without activating or changing the active model.
    async fn model_adjust_effort(&self, p: AdjustEffortParams) -> Result<Value, RpcError>;
    /// Switches the model for subsequent turns.
    async fn session_set_model(&self, p: SetModelParams) -> Result<Value, RpcError>;
    async fn session_set_permission_mode(
        &self,
        p: SetPermissionModeParams,
    ) -> Result<Value, RpcError>;
    /// Sets a session-only custom system prompt. Never written to disk.
    async fn session_set_system_prompt(&self, p: SetSystemPromptParams) -> Result<Value, RpcError>;
    async fn model_reasoning_capability(&self) -> Result<Value, RpcError>;
    async fn session_schedule_list(&self) -> Result<Value, RpcError>;
    async fn session_schedule_create(&self, p: ScheduleCreateParams) -> Result<Value, RpcError>;
    async fn session_schedule_delete(&self, p: ScheduleDeleteParams) -> Result<Value, RpcError>;
    async fn hooks_list(&self) -> Result<Value, RpcError>;
    async fn hooks_info(&self, p: HooksInfoParams) -> Result<Value, RpcError>;
    async fn hooks_trust(&self, p: HooksTrustParams) -> Result<Value, RpcError>;
    async fn skills_list(&self) -> Result<Value, RpcError>;
    async fn plugins_list(&self) -> Result<Value, RpcError>;
    async fn session_compact(&self, p: CompactParams) -> Result<Value, RpcError>;
    async fn session_fork(&self, p: ForkParams) -> Result<Value, RpcError>;
    async fn session_rewind(&self, p: RewindParams) -> Result<Value, RpcError>;

    // ── Stage D ──────────────────────────────────────────────────────────
    /// UI-safe rich history, read at the same consistency boundary as
    /// `session/getState`. Requires initialization.
    async fn session_get_history(&self, p: GetHistoryParams) -> Result<Value, RpcError>;
    /// Saved transcripts in the current workspace. Read-only discovery:
    /// valid **before** `initialize` so a client can choose a session to
    /// resume without starting one first.
    async fn session_list_sessions(&self, p: ListSessionsParams) -> Result<Value, RpcError>;
    /// Outstanding server-initiated requests. Requires initialization.
    async fn session_get_pending_requests(&self) -> Result<Value, RpcError>;
    /// Answer a pending request out of band. Requires initialization.
    async fn session_resolve_request(&self, p: ResolveRequestParams) -> Result<Value, RpcError>;
    /// Apply a pending request's fail-closed default. Requires initialization.
    async fn session_cancel_request(&self, p: CancelRequestParams) -> Result<Value, RpcError>;
    /// Read-only configuration inventory. Valid before `initialize`.
    async fn config_describe(&self) -> Result<Value, RpcError>;
    /// Mutates session-scoped configuration. Requires initialization.
    async fn config_set(&self, p: ConfigSetParams) -> Result<Value, RpcError>;
    /// Read-only, secret-free MCP inventory. Valid before `initialize`.
    async fn mcp_list(&self) -> Result<Value, RpcError>;
    /// Non-destructive recovery of engine-owned user notifications (Stage 2
    /// `notify_user`). Public and valid before `initialize` (§ same
    /// discovery rationale as `session/getHistory`/`session/listSessions`).
    async fn session_pending_messages(&self, p: PendingMessagesParams) -> Result<Value, RpcError>;

    /// Whether `initialize` has completed on this connection.
    ///
    /// The gate is enforced in [`dispatch`] rather than in each handler so
    /// the boundary is one visible table instead of a rule every future
    /// method has to remember.
    fn is_initialized(&self) -> bool;
}

/// The **new** (Stage D) methods that require an initialized session.
///
/// Deliberately not applied to the pre-existing routes: those have shipped
/// without a gate, and retroactively refusing them would be a breaking change
/// to clients that work today (§2.7, F14). Read-only discovery —
/// `session/getState`, `session/getEvents`, `session/listSessions`,
/// `config/describe`, `mcp/list` — stays valid before `initialize` by design.
pub use coda_proto::requests::INITIALIZATION_GATED_METHODS;

// ─────────────────────────────────────────────────────────────────────────────
// dispatch — the pure router
// ─────────────────────────────────────────────────────────────────────────────

/// Parse required params, returning `-32602` when absent or invalid.
fn required<T: for<'de> serde::Deserialize<'de>>(params: Option<Value>) -> Result<T, RpcError> {
    let v =
        params.ok_or_else(|| RpcError::invalid_params("params required for this method"))?;
    serde_json::from_value(v).map_err(|e| RpcError::invalid_params(e.to_string()))
}

/// Parse optional params, falling back to the type's `Default` when absent.
///
/// **Legacy routes only.** A present-but-malformed object silently defaults
/// here, which is the behaviour the pre-existing routes shipped with;
/// retroactively refusing it would break clients that work today. Everything
/// introduced by this contract uses [`optional_strict`] instead.
fn optional<T: for<'de> serde::Deserialize<'de> + Default>(params: Option<Value>) -> T {
    params.and_then(|v| serde_json::from_value(v).ok()).unwrap_or_default()
}

/// Parse optional params **strictly**: absent (or `null`) may default, but a
/// value that was actually supplied has to parse.
///
/// This is the difference between "the client did not ask for a fence" and
/// "the client asked for a fence and we could not read it". Silently
/// defaulting the second case turned `{"historyEpoch": "abc"}` into an
/// unfenced page 0 and `{"sections": "turn"}` into "send every section" —
/// exactly the requests whose whole purpose is to be exact.
///
/// Unrecognised keys are still ignored, so forward compatibility is
/// unaffected; only a *recognised* field of the wrong type is refused.
fn optional_strict<T: for<'de> serde::Deserialize<'de> + Default>(
    params: Option<Value>,
) -> Result<T, RpcError> {
    match params {
        None | Some(Value::Null) => Ok(T::default()),
        Some(v) => serde_json::from_value(v).map_err(|e| RpcError::invalid_params(e.to_string())),
    }
}

/// Route `(method, params)` to the correct backend method.
///
/// Returns `Ok(Value)` on success or `Err(RpcError)` on protocol / backend
/// error.  The caller wraps this into a JSON-RPC response.
pub async fn dispatch(
    method: &str,
    params: Option<Value>,
    backend: &dyn ServeBackend,
) -> Result<Value, RpcError> {
    if INITIALIZATION_GATED_METHODS.contains(&method) && !backend.is_initialized() {
        return Err(RpcError::not_initialized(method));
    }
    match method {
        "initialize" => backend.initialize(optional(params)).await,
        "shutdown" => backend.shutdown().await,
        "session/prompt" => backend.session_prompt(optional(params)).await,
        "session/interrupt" => backend.session_interrupt().await,
        "session/steer" => backend.session_steer(required(params)?).await,
        "session/recallSteering" => backend.session_recall_steering().await,
        "session/history" => backend.session_history().await,
        "session/messages" => backend.session_messages(required(params)?).await,
        "session/models" => backend.session_models(optional(params)).await,
        "session/getState" => backend.session_get_state(optional_strict(params)?).await,
        "session/getEvents" => backend.session_get_events(required(params)?).await,
        "session/setGoal" => backend.session_set_goal(optional(params)).await,
        "session/setEffort" => backend.session_set_effort(optional(params)).await,
        "model/adjustEffort" => backend.model_adjust_effort(required(params)?).await,
        "session/setModel" => backend.session_set_model(required(params)?).await,
        "session/setPermissionMode" => {
            backend.session_set_permission_mode(required(params)?).await
        }
        "session/setSystemPrompt" => {
            backend.session_set_system_prompt(optional(params)).await
        }
        "model/reasoningCapability" => backend.model_reasoning_capability().await,
        "session/scheduleList" => backend.session_schedule_list().await,
        "session/scheduleCreate" => backend.session_schedule_create(required(params)?).await,
        "session/scheduleDelete" => backend.session_schedule_delete(optional(params)).await,
        "hooks/list" => backend.hooks_list().await,
        "hooks/info" => backend.hooks_info(required(params)?).await,
        "hooks/trust" => backend.hooks_trust(required(params)?).await,
        "skills/list" => backend.skills_list().await,
        "skills/trust" => {
            // Always refused in serve mode — spec: always error -32600.
            Err(RpcError::skills_trust_refused())
        }
        "plugins/list" => backend.plugins_list().await,
        "session/compact" => backend.session_compact(optional(params)).await,
        "session/fork" => backend.session_fork(optional(params)).await,
        "session/rewind" => backend.session_rewind(optional(params)).await,
        "session/getHistory" => {
            let mut p: GetHistoryParams = optional_strict(params)?;
            p.normalise()?;
            backend.session_get_history(p).await
        }
        "session/listSessions" => {
            let mut p: ListSessionsParams = optional_strict(params)?;
            p.normalise()?;
            backend.session_list_sessions(p).await
        }
        "session/getPendingRequests" => backend.session_get_pending_requests().await,
        "session/resolveRequest" => backend.session_resolve_request(required(params)?).await,
        "session/cancelRequest" => backend.session_cancel_request(required(params)?).await,
        "config/describe" => backend.config_describe().await,
        "config/set" => backend.config_set(required(params)?).await,
        "mcp/list" => backend.mcp_list().await,
        "session/pendingMessages" => {
            let mut p: PendingMessagesParams = optional_strict(params)?;
            p.normalise()?;
            backend.session_pending_messages(p).await
        }
        _ => Err(RpcError::method_not_found(method)),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    // ── Fake backend for dispatch tests ──────────────────────────────────────

    struct FakeBackend;

    #[async_trait]
    impl ServeBackend for FakeBackend {
        async fn initialize(&self, _p: InitParams) -> Result<Value, RpcError> {
            Ok(json!({ "protocolVersion": "1", "sessionId": "s1", "serverInfo": "test" }))
        }
        async fn shutdown(&self) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true }))
        }
        async fn session_prompt(&self, _p: PromptParams) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true, "interrupted": false }))
        }
        async fn session_interrupt(&self) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true }))
        }
        async fn session_steer(&self, p: SteerParams) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true, "messageId": p.text }))
        }
        async fn session_recall_steering(&self) -> Result<Value, RpcError> {
            Ok(json!({ "messages": [] }))
        }
        async fn session_history(&self) -> Result<Value, RpcError> {
            Ok(json!({ "messages": [] }))
        }
        async fn session_messages(&self, p: MessagesParams) -> Result<Value, RpcError> {
            Ok(json!({ "messages": [], "nextIndex": p.since_index }))
        }
        async fn session_models(&self, _p: ModelsParams) -> Result<Value, RpcError> {
            Ok(json!({ "source": "builtin", "models": [] }))
        }
        async fn session_get_state(&self, p: GetStateParams) -> Result<Value, RpcError> {
            Ok(json!({ "sections": p.sections }))
        }
        async fn session_get_events(&self, _p: GetEventsParams) -> Result<Value, RpcError> {
            Ok(json!({}))
        }
        async fn session_set_goal(&self, _p: SetGoalParams) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true }))
        }
        async fn session_set_permission_mode(
            &self,
            p: SetPermissionModeParams,
        ) -> Result<Value, RpcError> {
            match p.mode.as_str() {
                "bypassPermissions" => Ok(json!({ "ok": true, "applied": "bypassPermissions" })),
                _ => Ok(json!({ "ok": false, "applied": "default" })),
            }
        }
        async fn session_set_model(&self, _p: SetModelParams) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true }))
        }
        async fn session_set_system_prompt(&self, p: SetSystemPromptParams) -> Result<Value, RpcError> {
            let cleared = p.text.as_deref().map(str::trim).unwrap_or("").is_empty();
            Ok(json!({ "ok": true, "cleared": cleared }))
        }

        async fn session_set_effort(&self, p: SetEffortParams) -> Result<Value, RpcError> {
            match p.effort.as_deref() {
                Some("bad") => Ok(json!({ "ok": false })),
                _ => Ok(json!({ "ok": true })),
            }
        }
        async fn model_adjust_effort(&self, p: AdjustEffortParams) -> Result<Value, RpcError> {
            match p.direction {
                -1 | 1 => Ok(json!({
                    "ok": true, "model": p.model, "providerId": "test",
                    "active": false, "note": "",
                })),
                other => Err(RpcError::invalid_params(format!("bad direction {other}"))),
            }
        }
        async fn model_reasoning_capability(&self) -> Result<Value, RpcError> {
            Ok(json!({ "supported": false, "levels": [], "supportsAuto": false }))
        }
        async fn session_schedule_list(&self) -> Result<Value, RpcError> {
            Ok(json!({ "schedules": [] }))
        }
        async fn session_schedule_create(
            &self,
            _p: ScheduleCreateParams,
        ) -> Result<Value, RpcError> {
            Err(RpcError::invalid_params("schedule create not supported in stub"))
        }
        async fn session_schedule_delete(
            &self,
            p: ScheduleDeleteParams,
        ) -> Result<Value, RpcError> {
            match p.id {
                None => Err(RpcError::invalid_params("missing id")),
                Some(id) => Err(RpcError::invalid_params(format!("not found: {id}"))),
            }
        }
        async fn hooks_list(&self) -> Result<Value, RpcError> {
            Ok(json!({ "hooks": [] }))
        }
        async fn hooks_info(&self, _p: HooksInfoParams) -> Result<Value, RpcError> {
            Err(RpcError::invalid_params("bad hook index"))
        }
        async fn hooks_trust(&self, p: HooksTrustParams) -> Result<Value, RpcError> {
            let project_path = p
                .project_path
                .ok_or_else(|| RpcError::invalid_params("missing projectPath"))?;
            let hook_hash = p
                .hook_hash
                .ok_or_else(|| RpcError::invalid_params("missing hookHash"))?;
            Ok(json!({ "ok": true, "projectPath": project_path, "hookHash": hook_hash }))
        }
        async fn skills_list(&self) -> Result<Value, RpcError> {
            Ok(json!({ "skills": [] }))
        }
        async fn plugins_list(&self) -> Result<Value, RpcError> {
            Ok(json!({ "plugins": [] }))
        }
        async fn session_compact(&self, _p: CompactParams) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true, "messagesBefore": 4, "messagesAfter": 2 }))
        }
        async fn session_fork(&self, _p: ForkParams) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true, "newSessionId": "forked0000000" }))
        }
        async fn session_rewind(&self, _p: RewindParams) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true, "removed": 1, "remaining": 2 }))
        }

        // ── Stage D ──────────────────────────────────────────────────────
        async fn session_get_history(&self, p: GetHistoryParams) -> Result<Value, RpcError> {
            // Echoes the *normalised* params so a dispatcher test can prove
            // what actually reached the backend.
            Ok(json!({ "sessionId": p.session_id, "limit": p.limit, "entries": [] }))
        }
        async fn session_list_sessions(&self, p: ListSessionsParams) -> Result<Value, RpcError> {
            Ok(json!({ "limit": p.limit, "sessions": [] }))
        }
        async fn session_get_pending_requests(&self) -> Result<Value, RpcError> {
            Ok(json!({ "requests": [] }))
        }
        async fn session_resolve_request(&self, p: ResolveRequestParams) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true, "requestId": p.request_id }))
        }
        async fn session_cancel_request(&self, p: CancelRequestParams) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true, "requestId": p.request_id }))
        }
        async fn config_describe(&self) -> Result<Value, RpcError> {
            Ok(json!({ "entries": [] }))
        }
        async fn config_set(&self, p: ConfigSetParams) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true, "key": p.key }))
        }
        async fn mcp_list(&self) -> Result<Value, RpcError> {
            Ok(json!({ "servers": [], "enabled": true, "managerAvailable": false }))
        }
        async fn session_pending_messages(&self, p: PendingMessagesParams) -> Result<Value, RpcError> {
            Ok(json!({ "messages": [], "nextCursor": p.after_cursor, "gap": false, "truncated": false }))
        }
        fn is_initialized(&self) -> bool {
            true
        }
    }

    /// A backend that has not been initialized, so the Stage D gate applies.
    struct UninitialisedBackend(FakeBackend);

    #[async_trait]
    impl ServeBackend for UninitialisedBackend {
        async fn initialize(&self, p: InitParams) -> Result<Value, RpcError> {
            self.0.initialize(p).await
        }
        async fn shutdown(&self) -> Result<Value, RpcError> {
            self.0.shutdown().await
        }
        async fn session_prompt(&self, p: PromptParams) -> Result<Value, RpcError> {
            self.0.session_prompt(p).await
        }
        async fn session_interrupt(&self) -> Result<Value, RpcError> {
            self.0.session_interrupt().await
        }
        async fn session_steer(&self, p: SteerParams) -> Result<Value, RpcError> {
            self.0.session_steer(p).await
        }
        async fn session_recall_steering(&self) -> Result<Value, RpcError> {
            self.0.session_recall_steering().await
        }
        async fn session_history(&self) -> Result<Value, RpcError> {
            self.0.session_history().await
        }
        async fn session_messages(&self, p: MessagesParams) -> Result<Value, RpcError> {
            self.0.session_messages(p).await
        }
        async fn session_models(&self, p: ModelsParams) -> Result<Value, RpcError> {
            self.0.session_models(p).await
        }
        async fn session_get_state(&self, p: GetStateParams) -> Result<Value, RpcError> {
            self.0.session_get_state(p).await
        }
        async fn session_get_events(&self, p: GetEventsParams) -> Result<Value, RpcError> {
            self.0.session_get_events(p).await
        }
        async fn session_set_goal(&self, p: SetGoalParams) -> Result<Value, RpcError> {
            self.0.session_set_goal(p).await
        }
        async fn session_set_effort(&self, p: SetEffortParams) -> Result<Value, RpcError> {
            self.0.session_set_effort(p).await
        }
        async fn model_adjust_effort(&self, p: AdjustEffortParams) -> Result<Value, RpcError> {
            self.0.model_adjust_effort(p).await
        }
        async fn session_set_model(&self, p: SetModelParams) -> Result<Value, RpcError> {
            self.0.session_set_model(p).await
        }
        async fn session_set_permission_mode(
            &self,
            p: SetPermissionModeParams,
        ) -> Result<Value, RpcError> {
            self.0.session_set_permission_mode(p).await
        }
        async fn session_set_system_prompt(
            &self,
            p: SetSystemPromptParams,
        ) -> Result<Value, RpcError> {
            self.0.session_set_system_prompt(p).await
        }
        async fn model_reasoning_capability(&self) -> Result<Value, RpcError> {
            self.0.model_reasoning_capability().await
        }
        async fn session_schedule_list(&self) -> Result<Value, RpcError> {
            self.0.session_schedule_list().await
        }
        async fn session_schedule_create(
            &self,
            p: ScheduleCreateParams,
        ) -> Result<Value, RpcError> {
            self.0.session_schedule_create(p).await
        }
        async fn session_schedule_delete(
            &self,
            p: ScheduleDeleteParams,
        ) -> Result<Value, RpcError> {
            self.0.session_schedule_delete(p).await
        }
        async fn hooks_list(&self) -> Result<Value, RpcError> {
            self.0.hooks_list().await
        }
        async fn hooks_info(&self, p: HooksInfoParams) -> Result<Value, RpcError> {
            self.0.hooks_info(p).await
        }
        async fn hooks_trust(&self, p: HooksTrustParams) -> Result<Value, RpcError> {
            self.0.hooks_trust(p).await
        }
        async fn skills_list(&self) -> Result<Value, RpcError> {
            self.0.skills_list().await
        }
        async fn plugins_list(&self) -> Result<Value, RpcError> {
            self.0.plugins_list().await
        }
        async fn session_compact(&self, p: CompactParams) -> Result<Value, RpcError> {
            self.0.session_compact(p).await
        }
        async fn session_fork(&self, p: ForkParams) -> Result<Value, RpcError> {
            self.0.session_fork(p).await
        }
        async fn session_rewind(&self, p: RewindParams) -> Result<Value, RpcError> {
            self.0.session_rewind(p).await
        }
        async fn session_get_history(&self, p: GetHistoryParams) -> Result<Value, RpcError> {
            self.0.session_get_history(p).await
        }
        async fn session_list_sessions(&self, p: ListSessionsParams) -> Result<Value, RpcError> {
            self.0.session_list_sessions(p).await
        }
        async fn session_get_pending_requests(&self) -> Result<Value, RpcError> {
            self.0.session_get_pending_requests().await
        }
        async fn session_resolve_request(&self, p: ResolveRequestParams) -> Result<Value, RpcError> {
            self.0.session_resolve_request(p).await
        }
        async fn session_cancel_request(&self, p: CancelRequestParams) -> Result<Value, RpcError> {
            self.0.session_cancel_request(p).await
        }
        async fn config_describe(&self) -> Result<Value, RpcError> {
            self.0.config_describe().await
        }
        async fn config_set(&self, p: ConfigSetParams) -> Result<Value, RpcError> {
            self.0.config_set(p).await
        }
        async fn mcp_list(&self) -> Result<Value, RpcError> {
            self.0.mcp_list().await
        }
        async fn session_pending_messages(&self, p: PendingMessagesParams) -> Result<Value, RpcError> {
            self.0.session_pending_messages(p).await
        }
        fn is_initialized(&self) -> bool {
            false
        }
    }

    // ── Optional params: absent may default, malformed may not (review F2)

    /// A present-but-malformed typed field on a **new** API must be a typed
    /// `-32602`, never a silently-defaulted whole object.
    ///
    /// `optional()` deserialised the entire params object or fell back to
    /// `Default`, so `{"historyEpoch": "abc"}` produced *no* epoch fence at
    /// all: the caller asked for a fenced page and got an unfenced page 0
    /// with no way to tell.
    #[tokio::test]
    async fn a_malformed_history_fence_is_refused_not_silently_dropped() {
        for bad in [
            json!({ "historyEpoch": "not-a-number" }),
            json!({ "expectedHistoryLength": "12" }),
            json!({ "engineInstanceId": 7 }),
            json!({ "sinceIndex": "0" }),
            json!({ "includeLive": "yes" }),
            json!({ "limit": 1.5 }),
            json!([1, 2, 3]),
        ] {
            let err = dispatch("session/getHistory", Some(bad.clone()), &FakeBackend)
                .await
                .expect_err("a malformed fence must never yield an unfenced page: {bad}");
            assert_eq!(err.code, -32602, "params {bad} must be invalidParams, got {err:?}");
        }
    }

    #[tokio::test]
    async fn a_malformed_state_section_filter_is_refused_not_silently_dropped() {
        for bad in [json!({ "sections": "turn" }), json!({ "sections": [1] }), json!("turn")] {
            let err = dispatch("session/getState", Some(bad.clone()), &FakeBackend)
                .await
                .expect_err("a malformed section filter must not become 'every section'");
            assert_eq!(err.code, -32602, "params {bad} must be invalidParams, got {err:?}");
        }
    }

    #[tokio::test]
    async fn a_malformed_session_list_limit_is_refused() {
        let err = dispatch("session/listSessions", Some(json!({ "limit": "50" })), &FakeBackend)
            .await
            .expect_err("a malformed limit must not become the default page size");
        assert_eq!(err.code, -32602);
    }

    /// Absent, `null` and `{}` all still mean "use the defaults": strictness
    /// applies to values that were actually supplied.
    #[tokio::test]
    async fn absent_or_null_optional_params_still_default() {
        for params in [None, Some(Value::Null), Some(json!({}))] {
            for method in ["session/getHistory", "session/getState", "session/listSessions"] {
                assert!(
                    dispatch(method, params.clone(), &FakeBackend).await.is_ok(),
                    "{method} must accept absent optional params"
                );
            }
        }
    }

    /// Forward compatibility is unaffected: an *unrecognised* key is ignored,
    /// only a recognised key of the wrong type is refused.
    #[tokio::test]
    async fn an_unrecognised_field_is_still_ignored() {
        let r = dispatch(
            "session/getHistory",
            Some(json!({ "limit": 5, "somethingFromANewerClient": { "a": 1 } })),
            &FakeBackend,
        )
        .await;
        assert!(r.is_ok(), "unknown keys must stay forward-compatible: {r:?}");
    }

    /// The legacy routes keep the tolerant behaviour they shipped with. This
    /// is a deliberate compatibility boundary, not an oversight: strictness
    /// is applied to the methods introduced by this contract only.
    #[tokio::test]
    async fn legacy_routes_keep_their_tolerant_optional_parsing() {
        for (method, params) in [
            ("session/prompt", json!({ "text": 5 })),
            ("session/models", json!({ "refresh": "yes" })),
            ("session/compact", json!({ "instructions": 1 })),
        ] {
            assert!(
                dispatch(method, Some(params), &FakeBackend).await.is_ok(),
                "{method} predates the strict rule; changing it would break shipped clients"
            );
        }
    }

    // ── Page limits are validated, not silently reinterpreted ────────────

    #[tokio::test]
    async fn a_non_positive_page_limit_is_refused_rather_than_becoming_the_default() {
        for (method, params) in [
            ("session/getHistory", json!({ "limit": 0 })),
            ("session/getHistory", json!({ "limit": -5 })),
            ("session/listSessions", json!({ "limit": 0 })),
            ("session/listSessions", json!({ "limit": -1 })),
        ] {
            let err = dispatch(method, Some(params.clone()), &FakeBackend)
                .await
                .expect_err("{method} {params} must not answer a nonsensical page size");
            assert_eq!(err.code, -32602, "{method} {params}");
            assert!(err.message.contains("limit"), "the message must name the field: {err:?}");
        }
    }

    #[tokio::test]
    async fn a_negative_index_or_fence_is_refused() {
        for params in [
            json!({ "sinceIndex": -1 }),
            json!({ "expectedHistoryLength": -1 }),
            json!({ "historyEpoch": -1 }),
        ] {
            let err = dispatch("session/getHistory", Some(params.clone()), &FakeBackend)
                .await
                .expect_err("{params} is not a position in any conversation");
            assert_eq!(err.code, -32602, "{params}");
        }
    }

    /// Over-max is **clamped**, and the ceiling is discoverable through
    /// `limits.maxHistoryPage` / `limits.maxSessionPage` rather than being
    /// folklore. `truncated`/`nextIndex` already tell a client there is more.
    #[tokio::test]
    async fn an_over_max_page_limit_is_clamped_to_the_advertised_ceiling() {
        let r = dispatch("session/getHistory", Some(json!({ "limit": 1_000_000 })), &FakeBackend)
            .await
            .expect("an over-large page is clamped, not refused");
        assert_eq!(r["limit"], crate::history::MAX_HISTORY_LIMIT);
        let r = dispatch("session/listSessions", Some(json!({ "limit": 9999 })), &FakeBackend)
            .await
            .expect("an over-large page is clamped, not refused");
        assert_eq!(r["limit"], crate::history::MAX_SESSION_LIMIT);
    }

    // ── The initialization gate (§2.7, F14) ─────────────────────────────

    #[tokio::test]
    async fn state_dependent_stage_d_methods_require_initialization() {
        let backend = UninitialisedBackend(FakeBackend);
        for method in INITIALIZATION_GATED_METHODS {
            let err = dispatch(method, Some(json!({ "requestId": "req-x-1", "key": "model" })), &backend)
                .await
                .expect_err("{method} must be gated");
            assert_eq!(
                err.code,
                coda_proto::messages::error_code::NOT_INITIALIZED,
                "{method} must return a typed notInitialized"
            );
        }
    }

    #[tokio::test]
    async fn read_only_discovery_stays_valid_before_initialize() {
        let backend = UninitialisedBackend(FakeBackend);
        for (method, params) in [
            ("session/listSessions", json!({})),
            ("config/describe", json!({})),
            ("mcp/list", json!({})),
            ("session/getState", json!({})),
            ("session/getEvents", json!({ "engineInstanceId": "e", "afterCursor": 0 })),
        ] {
            assert!(
                dispatch(method, Some(params), &backend).await.is_ok(),
                "{method} is read-only discovery and must work before initialize"
            );
        }
    }

    /// The gate is deliberately **not** retrofitted onto routes that shipped
    /// without one: refusing them now would break clients that work today.
    #[tokio::test]
    async fn pre_existing_routes_keep_their_current_pre_initialize_behaviour() {
        let backend = UninitialisedBackend(FakeBackend);
        for (method, params) in [
            ("session/prompt", json!({ "text": "hi" })),
            ("session/history", json!({})),
            ("session/setModel", json!({ "model": "m" })),
            ("session/compact", json!({})),
            ("session/fork", json!({})),
        ] {
            let result = dispatch(method, Some(params), &backend).await;
            assert!(
                result.is_ok(),
                "{method} predates the gate; its behaviour must not change"
            );
        }
    }

    #[tokio::test]
    async fn an_initialized_backend_passes_the_gate() {
        for method in INITIALIZATION_GATED_METHODS {
            let result =
                dispatch(method, Some(json!({ "requestId": "req-x-1", "key": "model" })), &FakeBackend)
                    .await;
            assert!(result.is_ok(), "{method} must be routed once initialized: {result:?}");
        }
    }

    // ── Method routing tests ─────────────────────────────────────────────────

    #[tokio::test]
    async fn dispatches_initialize() {
        let r = dispatch("initialize", Some(json!({"protocolVersion":"1"})), &FakeBackend).await;
        assert!(r.is_ok(), "{r:?}");
        assert_eq!(r.unwrap()["protocolVersion"], "1");
    }

    #[tokio::test]
    async fn dispatches_shutdown() {
        let r = dispatch("shutdown", None, &FakeBackend).await;
        assert_eq!(r.unwrap()["ok"], true);
    }

    #[tokio::test]
    async fn dispatches_session_prompt() {
        let r =
            dispatch("session/prompt", Some(json!({"text":"hi"})), &FakeBackend).await.unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["interrupted"], false);
    }

    #[tokio::test]
    async fn dispatches_session_interrupt() {
        let r = dispatch("session/interrupt", None, &FakeBackend).await.unwrap();
        assert_eq!(r["ok"], true);
    }

    #[tokio::test]
    async fn dispatches_session_steer() {
        let r = dispatch(
            "session/steer",
            Some(json!({"text":"go"})),
            &FakeBackend,
        )
        .await
        .unwrap();
        assert_eq!(r["ok"], true);
    }

    #[tokio::test]
    async fn dispatches_session_recall_steering() {
        let r = dispatch("session/recallSteering", None, &FakeBackend).await.unwrap();
        assert!(r["messages"].is_array());
    }

    #[tokio::test]
    async fn dispatches_session_history() {
        let r = dispatch("session/history", None, &FakeBackend).await.unwrap();
        assert!(r["messages"].is_array());
    }

    #[tokio::test]
    async fn dispatches_session_messages() {
        let r = dispatch(
            "session/messages",
            Some(json!({"sinceIndex":5})),
            &FakeBackend,
        )
        .await
        .unwrap();
        assert!(r["messages"].is_array());
        assert_eq!(r["nextIndex"], 5);
    }

    #[tokio::test]
    async fn dispatches_session_pending_messages() {
        let r = dispatch(
            "session/pendingMessages",
            Some(json!({"afterCursor": 3})),
            &FakeBackend,
        )
        .await
        .unwrap();
        assert!(r["messages"].is_array());
        assert_eq!(r["nextCursor"], 3);
    }

    #[tokio::test]
    async fn pending_messages_rejects_a_negative_after_cursor() {
        // `afterCursor` is wire-typed as an unsigned bus cursor; a negative
        // number is a malformed value, not a legitimate position, and must
        // be a typed refusal rather than silently reinterpreted or defaulted.
        let err = dispatch(
            "session/pendingMessages",
            Some(json!({"afterCursor": -1})),
            &FakeBackend,
        )
        .await
        .unwrap_err();
        assert_eq!(err.code, -32602);
    }

    #[tokio::test]
    async fn pending_messages_rejects_a_zero_limit() {
        // A `limit` of `0` must never silently fall back to "no limit" (the
        // bus-level bug this guards against): it is refused outright.
        let err = dispatch(
            "session/pendingMessages",
            Some(json!({"afterCursor": 0, "limit": 0})),
            &FakeBackend,
        )
        .await
        .unwrap_err();
        assert_eq!(err.code, -32602);
    }

    #[tokio::test]
    async fn pending_messages_clamps_an_oversized_limit_rather_than_refusing_it() {
        let r = dispatch(
            "session/pendingMessages",
            Some(json!({"afterCursor": 0, "limit": 999_999})),
            &FakeBackend,
        )
        .await
        .unwrap();
        // The FakeBackend just echoes what it was handed back via nextCursor
        // above; the clamp itself is proven by not erroring — the exact
        // ceiling value is asserted directly against `normalise_limit` in
        // `history::tests` and reused here via the same constant.
        assert!(r["messages"].is_array());
    }

    #[tokio::test]
    async fn pending_messages_accepts_a_future_oversized_cursor_without_erroring() {
        // A cursor far beyond anything the bus has ever issued is not
        // malformed — it must not park the client forever; it is simply
        // "nothing new yet".
        let r = dispatch(
            "session/pendingMessages",
            Some(json!({"afterCursor": u64::MAX})),
            &FakeBackend,
        )
        .await
        .unwrap();
        assert_eq!(r["nextCursor"].as_u64(), Some(u64::MAX));
    }

    #[tokio::test]
    async fn dispatches_session_models() {
        let r =
            dispatch("session/models", Some(json!({"refresh":false})), &FakeBackend).await.unwrap();
        assert_eq!(r["source"], "builtin");
        assert!(r["models"].is_array());
    }

    #[tokio::test]
    async fn dispatches_session_set_goal() {
        let r = dispatch(
            "session/setGoal",
            Some(json!({"goal":"do it","maxContinuations":3})),
            &FakeBackend,
        )
        .await
        .unwrap();
        assert_eq!(r["ok"], true);
    }

    #[tokio::test]
    async fn dispatches_session_set_effort_valid() {
        let r = dispatch(
            "session/setEffort",
            Some(json!({"effort":"medium"})),
            &FakeBackend,
        )
        .await
        .unwrap();
        assert_eq!(r["ok"], true);
    }

    #[tokio::test]
    async fn dispatches_model_adjust_effort_routes_and_returns_ok() {
        let r = dispatch(
            "model/adjustEffort",
            Some(json!({ "model": "m", "direction": 1 })),
            &FakeBackend,
        )
        .await
        .unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["model"], "m");
    }

    /// An invalid `direction` is a protocol error (-32602), not an `ok:false`.
    #[tokio::test]
    async fn dispatches_model_adjust_effort_invalid_direction_is_error() {
        let err = dispatch(
            "model/adjustEffort",
            Some(json!({ "model": "m", "direction": 2 })),
            &FakeBackend,
        )
        .await
        .unwrap_err();
        assert_eq!(err.code, -32602);
    }

    /// `model/adjustEffort` requires params (model + direction).
    #[tokio::test]
    async fn dispatches_model_adjust_effort_requires_params() {
        let err = dispatch("model/adjustEffort", None, &FakeBackend).await.unwrap_err();
        assert_eq!(err.code, -32602);
    }

    /// `session/setEffort` with an unsupported value returns `ok:false`, NOT an error.
    #[tokio::test]
    async fn dispatches_session_set_effort_unsupported_returns_ok_false_not_error() {
        let r = dispatch(
            "session/setEffort",
            Some(json!({"effort":"bad"})),
            &FakeBackend,
        )
        .await;
        // Must be Ok (not Err) but ok:false.
        let val = r.expect("must not error for unsupported effort");
        assert_eq!(val["ok"], false, "unsupported effort must return ok:false, not an error");
    }

    #[tokio::test]
    async fn dispatches_model_reasoning_capability() {
        let r = dispatch("model/reasoningCapability", None, &FakeBackend).await.unwrap();
        assert_eq!(r["supported"], false);
        assert!(r["levels"].is_array());
    }

    #[tokio::test]
    async fn dispatches_session_schedule_list() {
        let r = dispatch("session/scheduleList", None, &FakeBackend).await.unwrap();
        assert!(r["schedules"].is_array());
    }

    #[tokio::test]
    async fn dispatches_session_schedule_create_returns_invalid_params_when_prompt_missing() {
        let err =
            dispatch("session/scheduleCreate", Some(json!({"every":"1h"})), &FakeBackend)
                .await
                .unwrap_err();
        assert_eq!(err.code, -32602, "missing prompt must yield -32602");
    }

    #[tokio::test]
    async fn dispatches_session_schedule_delete_missing_id() {
        let err = dispatch(
            "session/scheduleDelete",
            Some(json!({})),
            &FakeBackend,
        )
        .await
        .unwrap_err();
        assert_eq!(err.code, -32602);
    }

    #[tokio::test]
    async fn dispatches_hooks_list() {
        let r = dispatch("hooks/list", None, &FakeBackend).await.unwrap();
        assert!(r["hooks"].is_array());
    }

    #[tokio::test]
    async fn dispatches_hooks_info_returns_invalid_params_for_bad_index() {
        let err =
            dispatch("hooks/info", Some(json!({"index":9999})), &FakeBackend).await.unwrap_err();
        assert_eq!(err.code, -32602);
    }

    #[tokio::test]
    async fn dispatches_hooks_trust_ok() {
        let r = dispatch(
            "hooks/trust",
            Some(json!({"projectPath":"/tmp","hookHash":"abc"})),
            &FakeBackend,
        )
        .await
        .unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["projectPath"], "/tmp");
    }

    #[tokio::test]
    async fn dispatches_hooks_trust_missing_project_path_returns_32602() {
        let err = dispatch(
            "hooks/trust",
            Some(json!({"hookHash":"abc"})),
            &FakeBackend,
        )
        .await
        .unwrap_err();
        assert_eq!(err.code, -32602);
    }

    #[tokio::test]
    async fn dispatches_skills_list() {
        let r = dispatch("skills/list", None, &FakeBackend).await.unwrap();
        assert!(r["skills"].is_array());
    }

    /// `skills/trust` is ALWAYS refused — -32600 regardless of params.
    #[tokio::test]
    async fn skills_trust_always_returns_32600() {
        let err = dispatch("skills/trust", None, &FakeBackend).await.unwrap_err();
        assert_eq!(err.code, -32600, "skills/trust must always return -32600");
    }

    /// Verify `skills/trust` is refused even when params are provided.
    #[tokio::test]
    async fn skills_trust_refused_with_params_too() {
        let err =
            dispatch("skills/trust", Some(json!({"skill":"x"})), &FakeBackend).await.unwrap_err();
        assert_eq!(err.code, -32600);
    }

    #[tokio::test]
    async fn dispatches_plugins_list() {
        let r = dispatch("plugins/list", None, &FakeBackend).await.unwrap();
        assert!(r["plugins"].is_array());
    }

    /// Unknown method returns -32601.
    #[tokio::test]
    async fn unknown_method_returns_32601() {
        let err = dispatch("unknown/method", None, &FakeBackend).await.unwrap_err();
        assert_eq!(err.code, -32601);
    }

    /// Another unknown method to confirm general coverage.
    #[tokio::test]
    async fn another_unknown_method_returns_32601() {
        let err = dispatch("totally/bogus", Some(json!({})), &FakeBackend).await.unwrap_err();
        assert_eq!(err.code, -32601);
    }

    /// `session/steer` with missing params (no `text` key at all, but params empty JSON).
    #[tokio::test]
    async fn session_steer_empty_params_is_ok_because_text_has_default() {
        // `text` has #[serde(default)], so an empty object is acceptable.
        let r = dispatch("session/steer", Some(json!({"text":""})), &FakeBackend).await;
        assert!(r.is_ok(), "steer with empty text must not error: {r:?}");
    }

    /// `session/steer` without params returns -32602.
    #[tokio::test]
    async fn session_steer_without_params_returns_32602() {
        let err = dispatch("session/steer", None, &FakeBackend).await.unwrap_err();
        assert_eq!(err.code, -32602);
    }

    /// `session/messages` without params returns -32602.
    #[tokio::test]
    async fn session_messages_without_params_returns_32602() {
        let err = dispatch("session/messages", None, &FakeBackend).await.unwrap_err();
        assert_eq!(err.code, -32602);
    }

    // ── FakeBackend returning specific error codes ───────────────────────────

    struct ErrorBackend(i64, &'static str);

    #[async_trait]
    impl ServeBackend for ErrorBackend {
        async fn initialize(&self, _p: InitParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn shutdown(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_prompt(&self, _p: PromptParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_interrupt(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_steer(&self, _p: SteerParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_recall_steering(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_history(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_messages(&self, _p: MessagesParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_models(&self, _p: ModelsParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_get_state(&self, _p: GetStateParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_get_events(&self, _p: GetEventsParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_set_goal(&self, _p: SetGoalParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_set_permission_mode(
        &self,
        _p: SetPermissionModeParams,
    ) -> Result<Value, RpcError> {
        Ok(serde_json::json!({ "ok": true, "applied": "default" }))
    }

    async fn session_set_model(&self, _p: SetModelParams) -> Result<Value, RpcError> {

        Ok(json!({ "ok": true }))

    }

    async fn session_set_system_prompt(&self, _p: SetSystemPromptParams) -> Result<Value, RpcError> {
        Err(RpcError { code: self.0, message: self.1.into() })
    }

    async fn session_set_effort(&self, _p: SetEffortParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn model_adjust_effort(&self, _p: AdjustEffortParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn model_reasoning_capability(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_schedule_list(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_schedule_create(
            &self,
            _p: ScheduleCreateParams,
        ) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_schedule_delete(
            &self,
            _p: ScheduleDeleteParams,
        ) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn hooks_list(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn hooks_info(&self, _p: HooksInfoParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn hooks_trust(&self, _p: HooksTrustParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn skills_list(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn plugins_list(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_compact(&self, _p: CompactParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_fork(&self, _p: ForkParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_rewind(&self, _p: RewindParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_get_history(&self, _p: GetHistoryParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_list_sessions(&self, _p: ListSessionsParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_get_pending_requests(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_resolve_request(&self, _p: ResolveRequestParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_cancel_request(&self, _p: CancelRequestParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn config_describe(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn config_set(&self, _p: ConfigSetParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn mcp_list(&self) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_pending_messages(&self, _p: PendingMessagesParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        fn is_initialized(&self) -> bool {
            true
        }
    }

    #[tokio::test]
    async fn backend_error_32001_unauthorized_is_propagated() {
        let b = ErrorBackend(-32001, "auth required");
        let err = dispatch("initialize", Some(json!({})), &b).await.unwrap_err();
        assert_eq!(err.code, -32001);
    }

    #[tokio::test]
    async fn backend_error_32002_session_not_found_is_propagated() {
        let b = ErrorBackend(-32002, "session not found");
        let err = dispatch("session/prompt", Some(json!({})), &b).await.unwrap_err();
        assert_eq!(err.code, -32002);
    }

    #[tokio::test]
    async fn backend_error_32603_internal_is_propagated() {
        let b = ErrorBackend(-32603, "internal");
        let err = dispatch("shutdown", None, &b).await.unwrap_err();
        assert_eq!(err.code, -32603);
    }

    // ── session/compact dispatch tests ─────────────────────────────────────

    #[tokio::test]
    async fn dispatches_session_compact_no_params() {
        let r = dispatch("session/compact", None, &FakeBackend).await.unwrap();
        assert_eq!(r["ok"], true);
        assert!(r["messagesBefore"].is_number());
        assert!(r["messagesAfter"].is_number());
    }

    #[tokio::test]
    async fn dispatches_session_compact_with_instructions() {
        let r = dispatch(
            "session/compact",
            Some(json!({"instructions": "be brief"})),
            &FakeBackend,
        )
        .await
        .unwrap();
        assert_eq!(r["ok"], true);
    }

    #[tokio::test]
    async fn session_compact_empty_params_object_is_ok() {
        let r = dispatch("session/compact", Some(json!({})), &FakeBackend).await.unwrap();
        assert_eq!(r["ok"], true);
    }

    // ── session/fork + session/rewind dispatch tests ─────────────────────────

    #[tokio::test]
    async fn dispatches_session_fork() {
        let r = dispatch("session/fork", None, &FakeBackend).await.unwrap();
        assert_eq!(r["ok"], true);
        assert!(r["newSessionId"].is_string());
    }

    #[tokio::test]
    async fn dispatches_session_fork_with_empty_params() {
        let r = dispatch("session/fork", Some(json!({})), &FakeBackend).await.unwrap();
        assert_eq!(r["ok"], true);
    }

    #[tokio::test]
    async fn dispatches_session_rewind_defaults() {
        let r = dispatch("session/rewind", None, &FakeBackend).await.unwrap();
        assert_eq!(r["ok"], true);
    }

    #[tokio::test]
    async fn dispatches_session_rewind_with_n() {
        let r =
            dispatch("session/rewind", Some(json!({"n": 3})), &FakeBackend).await.unwrap();
        assert_eq!(r["ok"], true);
    }
}
