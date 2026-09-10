//! Typed payloads for the Coda `serve` protocol.
//!
//! The C# host serialises with `JsonNamingPolicy.CamelCase` and
//! `DefaultIgnoreCondition.WhenWritingNull`, so every field is camelCase and
//! optional fields are *absent* rather than `null`. `#[serde(default)]` on the
//! optional fields therefore matters: a missing key must not be an error.
//!
//! Source of truth: `src/Coda.Sdk/Serve/Messages/` in the C# tree.

use serde::{Deserialize, Serialize};

/// Protocol version this client speaks (`ServeMethods.ProtocolVersion`).
pub const PROTOCOL_VERSION: &str = "1";

/// Method names the client may call.
pub mod method {
    pub const INITIALIZE: &str = "initialize";
    pub const SHUTDOWN: &str = "shutdown";

    pub const PROMPT: &str = "session/prompt";
    pub const INTERRUPT: &str = "session/interrupt";
    pub const STEER: &str = "session/steer";
    pub const RECALL_STEERING: &str = "session/recallSteering";
    pub const HISTORY: &str = "session/history";
    pub const MESSAGES: &str = "session/messages";
    pub const MODELS: &str = "session/models";
    /// Authoritative snapshot of engine state (Slice 0 / Stage C, §2.2).
    pub const GET_STATE: &str = "session/getState";
    /// Bounded event replay with an explicit gap/`oldestAvailableCursor`.
    pub const GET_EVENTS: &str = "session/getEvents";
    pub const SET_GOAL: &str = "session/setGoal";
    pub const SET_EFFORT: &str = "session/setEffort";
    /// Steps a specific model's reasoning effort up/down one rung without
    /// activating or changing the active model (`ServeMethods.AdjustModelEffort`).
    pub const ADJUST_MODEL_EFFORT: &str = "model/adjustEffort";
    /// Switches the model for subsequent turns, without restarting.
    pub const SET_MODEL: &str = "session/setModel";
    pub const SET_PERMISSION_MODE: &str = "session/setPermissionMode";
    pub const SET_SYSTEM_PROMPT: &str = "session/setSystemPrompt";
    pub const REASONING_CAPABILITY: &str = "model/reasoningCapability";

    pub const SCHEDULE_LIST: &str = "session/scheduleList";
    pub const SCHEDULE_CREATE: &str = "session/scheduleCreate";
    pub const SCHEDULE_DELETE: &str = "session/scheduleDelete";

    pub const HOOKS_LIST: &str = "hooks/list";
    pub const HOOKS_INFO: &str = "hooks/info";
    pub const HOOKS_TRUST: &str = "hooks/trust";

    pub const SKILLS_LIST: &str = "skills/list";
    pub const SKILLS_TRUST: &str = "skills/trust";
    pub const PLUGINS_LIST: &str = "plugins/list";
    pub const COMPACT: &str = "session/compact";
    /// Branch the live conversation into a new session id, freezing the
    /// original. Named here rather than spelled out at each call site so the
    /// bootstrap and the slash command cannot drift apart.
    pub const FORK: &str = "session/fork";
    pub const REWIND: &str = "session/rewind";

    // ── Stage D (§2.2) ───────────────────────────────────────────────────
    /// UI-safe rich history for the live session or a validated saved one,
    /// read at the same consistency boundary as `session/getState`.
    pub const GET_HISTORY: &str = "session/getHistory";
    /// Saved transcripts in the current workspace. Read-only; valid before
    /// `initialize` so a client can choose a session before resuming one.
    pub const LIST_SESSIONS: &str = "session/listSessions";
    /// Outstanding server-initiated requests.
    pub const GET_PENDING_REQUESTS: &str = "session/getPendingRequests";
    /// Answer a pending request out of band (i.e. not by replying to the
    /// original `request/*` round-trip).
    pub const RESOLVE_REQUEST: &str = "session/resolveRequest";
    /// Apply a pending request's fail-closed default and stop waiting.
    pub const CANCEL_REQUEST: &str = "session/cancelRequest";
    pub const CONFIG_DESCRIBE: &str = "config/describe";
    pub const CONFIG_SET: &str = "config/set";
    /// Read-only, secret-free MCP server inventory.
    pub const MCP_LIST: &str = "mcp/list";
}

/// Method names the server may call on us. Each expects a reply.
pub mod server_method {
    pub const PERMISSION: &str = "request/permission";
    pub const QUESTION: &str = "request/question";
    pub const PLAN_APPROVAL: &str = "request/planApproval";
}

/// Coda-specific JSON-RPC error codes.
pub mod error_code {
    pub const UNAUTHORIZED: i64 = -32001;
    pub const SESSION_NOT_FOUND: i64 = -32002;
    /// A method that mutates state or is scoped to a turn was called before
    /// `initialize`. Read-only discovery is deliberately still allowed.
    pub const NOT_INITIALIZED: i64 = -32011;
    /// `session/getHistory` was called with a `historyEpoch` the engine has
    /// already moved past (fork/rewind/compact/resume), so the client's
    /// indices refer to a conversation that no longer exists. Never answered
    /// with a partially-valid page.
    pub const STALE_EPOCH: i64 = -32012;
    /// The committed-history fence moved between two pages of the same
    /// logical read, so continuing would duplicate or skip content.
    pub const HISTORY_FENCE_MOVED: i64 = -32013;
    /// A pending-request handle does not exist (or was already resolved).
    pub const UNKNOWN_REQUEST: i64 = -32014;
    /// The outcome offered does not match the pending request's kind.
    pub const REQUEST_KIND_MISMATCH: i64 = -32015;
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

/// Optional capability negotiation from the client (§2.1 of the serve API
/// implementation plan). Absence of the whole struct, or of any field inside
/// it, means legacy behaviour: no gated event method is emitted.
#[derive(Debug, Clone, Serialize, Deserialize, Default, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ClientCapabilities {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub state_events: Option<bool>,
    /// Reserved reader hint; it does not gate `session/getHistory`.
    /// The `history.rich` capability reports that method's availability.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub rich_history: Option<bool>,
    /// Reserved preference: the current engine does not negotiate an outgoing
    /// frame-size limit from this value. See `events.payloadLimitNegotiation`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_event_payload_bytes: Option<i64>,
}

impl ClientCapabilities {
    /// Whether the client negotiated the new `event/*` state/queue/lifecycle
    /// notification methods.
    pub fn wants_state_events(&self) -> bool {
        self.state_events.unwrap_or(false)
    }
}

/// One entry in `InitializeResult.capabilities` — whether a named capability
/// is supported by this engine build, with an explanatory reason when not.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct CapabilityEntry {
    pub supported: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

impl CapabilityEntry {
    pub fn supported() -> Self {
        Self { supported: true, reason: None }
    }
    pub fn unsupported(reason: impl Into<String>) -> Self {
        Self { supported: false, reason: Some(reason.into()) }
    }
}

/// Contract version stamped on every `InitializeResult` since the serve API
/// implementation plan's Slice 0. Independent of `PROTOCOL_VERSION`, which
/// stays `"1"` for wire-envelope compatibility.
pub const CONTRACT_VERSION: &str = "2026-09-1";

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct InitializeParams {
    pub protocol_version: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub client_info: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub api_key: Option<String>,
    /// Resumes an existing session when supplied.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub session_id: Option<String>,
    /// Additive, optional (§2.1): absent means legacy behaviour.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub client_capabilities: Option<ClientCapabilities>,
}

impl InitializeParams {
    pub fn new(client_info: impl Into<String>) -> Self {
        Self {
            protocol_version: PROTOCOL_VERSION.to_string(),
            client_info: Some(client_info.into()),
            api_key: None,
            session_id: None,
            client_capabilities: None,
        }
    }

    pub fn resume(mut self, session_id: impl Into<String>) -> Self {
        self.session_id = Some(session_id.into());
        self
    }

    pub fn with_client_capabilities(mut self, caps: ClientCapabilities) -> Self {
        self.client_capabilities = Some(caps);
        self
    }
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct InitializeResult {
    pub protocol_version: String,
    pub session_id: String,
    pub server_info: String,
    #[serde(default)]
    pub telemetry_log_path: Option<String>,
    /// Additive fields since Slice 0 (§2.1). All `#[serde(default)]` so a
    /// legacy engine's response — missing these keys entirely — still parses.
    #[serde(default)]
    pub contract_version: Option<String>,
    #[serde(default)]
    pub engine_instance_id: Option<String>,
    /// The authoritative start cursor for `session/getEvents`, valid at the
    /// moment this `initialize` call returned.
    #[serde(default)]
    pub event_cursor: Option<i64>,
    #[serde(default)]
    pub capabilities: Option<std::collections::HashMap<String, CapabilityEntry>>,
}

/// The current Rust engine's initialize response. Unlike `InitializeResult`,
/// which tolerates legacy peers, these contract fields are always emitted.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct InitializeResponse {
    pub protocol_version: String,
    pub session_id: String,
    pub server_info: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub telemetry_log_path: Option<String>,
    pub contract_version: String,
    pub engine_instance_id: String,
    pub event_cursor: i64,
    pub capabilities: std::collections::HashMap<String, CapabilityEntry>,
}

/// Shared `{ "ok": true }` shape used by interrupt and shutdown.
#[derive(Debug, Clone, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct OkResult {
    #[serde(default)]
    pub ok: bool,
}

// ---------------------------------------------------------------------------
// Prompting
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PromptParams {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub images: Vec<WireImage>,
}

impl PromptParams {
    pub fn text(text: impl Into<String>) -> Self {
        Self {
            text: Some(text.into()),
            images: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct WireImage {
    /// `image/png`, `image/jpeg`, `image/gif` or `image/webp`.
    pub media_type: String,
    pub base64: String,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PromptResult {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub stop_reason: Option<String>,
    #[serde(default)]
    pub interrupted: bool,
    #[serde(default)]
    pub goal_status: Option<WireGoalStatus>,
    #[serde(default)]
    pub error: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct WireGoalStatus {
    /// `"Met"` or `"Unmet"`; the field is omitted entirely for `None`.
    pub outcome: String,
    #[serde(default)]
    pub remaining: Option<String>,
    #[serde(default)]
    pub continuations: i32,
    #[serde(default)]
    pub elapsed_seconds: f64,
    #[serde(default)]
    pub escalated: bool,
    #[serde(default)]
    pub extension_used: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SteerParams {
    pub text: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SteerResult {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message_id: Option<String>,
    /// Rejection classification such as `noActiveTurn`, `turnEnding` or `emptyText`.
    #[serde(default)]
    pub rejected_reason: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct RecallSteeringResult {
    #[serde(default)]
    pub messages: Vec<RecalledSteeringMessage>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct RecalledSteeringMessage {
    pub id: String,
    pub text: String,
    #[serde(default)]
    pub enqueued_at: Option<String>,
}

// ---------------------------------------------------------------------------
// History
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Deserialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct HistoryResult {
    #[serde(default)]
    pub messages: Vec<WireMessage>,
}

#[derive(Debug, Clone, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct WireMessage {
    /// `"user"` or `"assistant"`.
    pub role: String,
    #[serde(default)]
    pub content: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct MessagesParams {
    pub since_index: i32,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct MessagesResult {
    #[serde(default)]
    pub messages: Vec<WireMessage>,
    #[serde(default)]
    pub next_index: i32,
}

// ---------------------------------------------------------------------------
// Models, goals and effort
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ModelsParams {
    pub refresh: bool,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ModelsResult {
    /// `"live"`, `"catalog"` or `"builtin"`.
    #[serde(default)]
    pub source: String,
    #[serde(default)]
    pub models: Vec<WireModel>,
    /// The model the engine will actually use.
    ///
    /// The list alone does not say which entry is active, and the obvious
    /// guess — the first — is only whatever order the provider returned.
    #[serde(default)]
    pub model: Option<String>,
    /// The provider whose credential the engine connected with.
    ///
    /// Not necessarily `defaultProvider` from settings: the engine uses the
    /// credential it actually found. A client saving a model preference must
    /// key it by this, or it writes where the engine will never read.
    #[serde(default)]
    pub provider_id: Option<String>,
}

impl ModelsResult {
    /// How the active model should be shown, preferring its display name.
    pub fn active_label(&self) -> Option<&str> {
        let active = self.model.as_deref()?;
        let named = self
            .models
            .iter()
            .find(|m| m.id == active)
            .map(WireModel::label);
        // An id the list does not contain is still the truth about what is
        // running — showing it beats showing an unrelated entry.
        Some(named.unwrap_or(active))
    }

    /// The context limit of the active model, if the list describes it.
    pub fn active_context_limit(&self) -> Option<i64> {
        let active = self.model.as_deref()?;
        self.models.iter().find(|m| m.id == active)?.context_limit
    }

    /// What the active model charges, per million tokens in and out.
    ///
    /// `None` when the catalogue does not price it. A missing price shows
    /// nothing rather than zero, because a cost of "$0.00" reads as free.
    pub fn active_price(&self) -> Option<(f64, f64)> {
        let active = self.model.as_deref()?;
        let model = self.models.iter().find(|m| m.id == active)?;
        Some((model.input_cost?, model.output_cost?))
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct WireModel {
    pub id: String,
    #[serde(default)]
    pub display_name: Option<String>,
    #[serde(default)]
    pub context_limit: Option<i64>,
    /// US dollars per million input tokens, when the catalogue knows.
    #[serde(default)]
    pub input_cost: Option<f64>,
    /// US dollars per million output tokens, when the catalogue knows.
    #[serde(default)]
    pub output_cost: Option<f64>,
    /// Reasoning-effort levels the model advertises, lowest to highest.
    ///
    /// Empty when the provider reported nothing; this is *not* the same as
    /// "unsupported" — the caller must not conclude absence means unsupported.
    #[serde(default)]
    pub reasoning_levels: Vec<String>,
    /// The effort level effectively in force for this model right now.
    ///
    /// For the *active* model this is the live effective level (after clamps
    /// and any per-model session override), so a browser can show what is
    /// actually in force rather than a possibly-stale persisted value. For
    /// other rows it is the session override or saved preference, when known.
    /// `None` means automatic / none.
    #[serde(default)]
    pub effort: Option<String>,
}

impl WireModel {
    pub fn label(&self) -> &str {
        self.display_name.as_deref().unwrap_or(&self.id)
    }
}

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SetGoalParams {
    /// `None` clears the active goal.
    pub goal: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_duration: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_continuations: Option<i32>,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SetGoalResult {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub goal: Option<String>,
    #[serde(default)]
    pub max_duration: Option<String>,
    #[serde(default)]
    pub max_continuations: Option<i32>,
}

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SetEffortParams {
    /// `"low"`, `"medium"`, `"high"`, `"xhigh"`, `"max"`, `"auto"`, or `None` to clear.
    pub effort: Option<String>,
    /// The model the caller believed was active when it built this request.
    ///
    /// When present, the engine rejects the call (without mutating anything) if
    /// the active model has since changed, so a picker opened for one model can
    /// never silently reconfigure another. `None` skips the guard.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub expected_model: Option<String>,
    /// The provider the caller believed was connected. Guarded exactly like
    /// `expected_model`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub expected_provider: Option<String>,
}

/// Switches the live permission mode for the running session.
///
/// The mode is session state, not a setting: applying it through the engine is
/// what lets `/yolo` take effect on the next tool call instead of asking the
/// user to restart.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SetPermissionModeParams {
    /// `"default"`, `"acceptEdits"`, `"plan"` or `"bypassPermissions"`.
    pub mode: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SetPermissionModeResult {
    #[serde(default)]
    pub ok: bool,
    /// The mode actually in force after the call.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub applied: Option<String>,
}

/// Sets a session-only custom system prompt that **fully replaces** the
/// engine's built-in system prompt (it is not appended to it). Session-only:
/// never written to settings.
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SetSystemPromptParams {
    /// The full system prompt text. `None` or empty string clears any override.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SetSystemPromptResult {
    #[serde(default)]
    pub ok: bool,
    /// Non-empty when the prompt was cleared via a None/empty `text`.
    #[serde(default)]
    pub cleared: bool,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SetEffortResult {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub applied: Option<String>,
    /// The effective current effort level after the call.
    ///
    /// Matches `applied` on success; on failure reflects whatever was in force
    /// before the (rejected) call. Absent when no effort is set at all.
    #[serde(default)]
    pub current: Option<String>,
    #[serde(default)]
    pub note: Option<String>,
}

/// Parameters for `model/adjustEffort`.
///
/// Steps the *target* model's reasoning effort one rung up (`+1`) or down
/// (`-1`) along the engine-owned ladder `[auto?, levels…]`. Unlike
/// [`SetEffortParams`] this never activates or changes the active model — it
/// edits the target's own per-model preference and only touches the live level
/// when the target happens to be active.
#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ModelAdjustEffortParams {
    /// The model to step. Must be a model the engine knows (live list or
    /// catalogue), not an arbitrary string.
    pub model: String,
    /// `-1` toward automatic/lower, `+1` toward higher. Any other value is
    /// rejected with `-32602`.
    pub direction: i32,
    /// The provider the caller believed was connected; when it no longer
    /// matches, the engine rejects the call without mutating anything.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub expected_provider: Option<String>,
}

/// Result of `model/adjustEffort`.
#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ModelEffortResult {
    #[serde(default)]
    pub ok: bool,
    /// The canonical model id the preference was keyed under — never a display
    /// name — so a client persists the per-model save under exactly the key the
    /// engine reads it back with.
    #[serde(default)]
    pub model: String,
    /// The provider that model is served by, paired with `model` for the
    /// canonical `(provider, model)` identity.
    #[serde(default)]
    pub provider_id: String,
    /// The effective level in force for the target after the step. `None` means
    /// automatic / none — a boundary or an unchanged step reports the truth,
    /// never a phantom level change.
    #[serde(default)]
    pub current: Option<String>,
    /// Whether the target is the currently active model. An inactive edit
    /// changes only the stored preference, leaving the running model untouched.
    #[serde(default)]
    pub active: bool,
    /// Always present (may be empty): a clamp at a bound or a refusal explains
    /// itself here.
    #[serde(default)]
    pub note: String,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ReasoningCapabilityResult {
    #[serde(default)]
    pub supported: bool,
    #[serde(default)]
    pub levels: Vec<String>,
    #[serde(default)]
    pub supports_auto: bool,
    /// The effort level currently in force on the session, if any.
    ///
    /// `None` means "automatic / not set" — an honest, unambiguous default the
    /// picker uses to pre-select rather than always defaulting to `high`.
    #[serde(default)]
    pub current: Option<String>,
    /// `true` when the capability could not be determined yet (for example a
    /// Copilot model before the model list has been fetched).
    ///
    /// Distinct from `supported: false`, which is a positive statement that the
    /// model has no reasoning effort. Callers must treat the two differently:
    /// indeterminate is "unknown, do not lie", unsupported is "known absent".
    #[serde(default)]
    pub indeterminate: bool,
    /// The canonical model id the capability describes.
    ///
    /// This is the engine's own id for the active model, never a display name,
    /// so a caller can persist a per-model preference under exactly the key the
    /// engine reads it back with. `None` when the engine could not identify it.
    #[serde(default)]
    pub model: Option<String>,
    /// The provider that model is served by, paired with `model` to form the
    /// canonical `(provider, model)` identity the picker persists against.
    #[serde(default)]
    pub provider_id: Option<String>,
}

// ---------------------------------------------------------------------------
// Compaction
// ---------------------------------------------------------------------------

/// Params for `session/compact`.
#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct CompactParams {
    /// Optional override for the summarisation system prompt.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub instructions: Option<String>,
}

/// Result of `session/compact`.
#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct CompactResult {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub messages_before: i64,
    #[serde(default)]
    pub messages_after: i64,
    #[serde(default)]
    pub tokens_before: Option<i64>,
    #[serde(default)]
    pub tokens_after: Option<i64>,
    #[serde(default)]
    pub error: Option<String>,
}

// ---------------------------------------------------------------------------
// Schedules
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Deserialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ScheduleListResult {
    #[serde(default)]
    pub schedules: Vec<ScheduledTask>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ScheduledTask {
    pub id: String,
    #[serde(default)]
    pub name: Option<String>,
    /// `"interval"`, `"at"` or `"cron"`.
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub prompt: String,
    #[serde(default)]
    pub rule: String,
    #[serde(default)]
    pub time_zone: Option<String>,
    #[serde(default)]
    pub next_run_utc: Option<String>,
    /// `"idle"`, `"running"` or `"pending"`.
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub active_task_id: Option<String>,
    #[serde(default)]
    pub last_outcome: Option<String>,
}

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ScheduleCreateParams {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    pub prompt: String,
    /// Exactly one of `every`, `at` or `cron` must be set.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub every: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub at: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub cron: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub time_zone: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ScheduleDeleteParams {
    pub id: String,
}

// ---------------------------------------------------------------------------
// Skills, plugins and hooks
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Deserialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SkillsListResult {
    #[serde(default)]
    pub skills: Vec<WireSkill>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct WireSkill {
    pub name: String,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default)]
    pub origin: Option<String>,
    #[serde(default)]
    pub enabled: bool,
    #[serde(default)]
    pub user_invocable: bool,
    #[serde(default)]
    pub source_path: Option<String>,
    #[serde(default)]
    pub argument_hint: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PluginsListResult {
    #[serde(default)]
    pub plugins: Vec<WirePlugin>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct WirePlugin {
    pub name: String,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub enabled: bool,
    #[serde(default)]
    pub trusted: bool,
    #[serde(default)]
    pub is_external: bool,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct HooksListResult {
    #[serde(default)]
    pub hooks: Vec<WireHook>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct WireHook {
    #[serde(default)]
    pub index: i32,
    #[serde(default)]
    pub event: String,
    #[serde(default)]
    pub handler_type: Option<String>,
    #[serde(default)]
    pub matcher: Option<String>,
    #[serde(default)]
    pub scope: Option<String>,
    #[serde(default)]
    pub enabled: bool,
}

// ---------------------------------------------------------------------------
// Server-initiated requests
// ---------------------------------------------------------------------------

/// The opaque public handle for a server-initiated request, carried on the
/// `request/*` params themselves.
///
/// Without it a client that answers the raw round-trip and *also* discovers
/// the same request through `session/getPendingRequests` has no way to tell
/// the two descriptions apart, so it either renders the prompt twice or drops
/// the original responder — and dropping a responder **declines** the request.
/// The value is the same opaque handle `PendingRequestDto.requestId` carries,
/// and is bound to the engine instance that minted it. It is **never** parsed:
/// its internal shape is not part of the contract.
///
/// Additive and optional: a legacy engine omits it, and a client that has
/// never heard of it ignores it.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PermissionRequest {
    #[serde(default)]
    pub tool_name: String,
    #[serde(default)]
    pub input_preview: String,
    #[serde(default)]
    pub request_id: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PermissionResponse {
    pub allow: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct QuestionRequest {
    #[serde(default)]
    pub question: String,
    #[serde(default)]
    pub options: Vec<String>,
    #[serde(default)]
    pub multi_select: bool,
    #[serde(default)]
    pub allow_free_text: bool,
    /// See [`PermissionRequest::request_id`].
    #[serde(default)]
    pub request_id: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct QuestionResponse {
    pub answer: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PlanApprovalRequest {
    #[serde(default)]
    pub plan: String,
    /// See [`PermissionRequest::request_id`].
    #[serde(default)]
    pub request_id: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PlanApprovalResponse {
    pub approve: bool,
}

// ---------------------------------------------------------------------------
// Correlation
// ---------------------------------------------------------------------------

/// Ids that tie tool calls, progress and results together.
///
/// All four are optional on the wire. A call and its result correlate only when
/// every populated component matches, which is what lets two same-named tools
/// running concurrently stay distinct.
#[derive(Debug, Clone, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Correlation {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub root_turn_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub activity_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub call_id: Option<String>,
    /// `"root:<rootTurnId>"` or `"subagent:<taskId>"`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_id: Option<String>,
    /// Additive alias for `activity_id` (§2.6 of the serve API implementation
    /// plan): equals today's per-batch `activity_id` value verbatim.
    /// `activity_id` is never re-rooted or removed; this is a second name for
    /// the same value so new clients can adopt the documented identity model
    /// without any existing field changing meaning.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub batch_id: Option<String>,
}

impl Correlation {
    /// Whether every id needed for exact correlation is present.
    pub fn is_complete(&self) -> bool {
        self.root_turn_id.is_some()
            && self.activity_id.is_some()
            && self.call_id.is_some()
            && self.source_id.is_some()
    }

    /// Whether this call originated from a subagent rather than the root turn.
    pub fn is_subagent(&self) -> bool {
        self.source_id
            .as_deref()
            .is_some_and(|id| id.starts_with("subagent:"))
    }

    /// The task id when this came from a subagent.
    pub fn subagent_task_id(&self) -> Option<&str> {
        self.source_id.as_deref()?.strip_prefix("subagent:")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    // ── Reverse-request public handle (Stage E) ──────────────────────────

    #[test]
    fn a_reverse_request_carries_the_public_handle_it_is_also_listed_under() {
        // A client answering the raw round-trip must be able to recognise the
        // *same* request when it also appears in `session/getPendingRequests`,
        // without parsing the opaque handle or guessing at its shape.
        let permission: PermissionRequest = serde_json::from_value(json!({
            "toolName": "edit",
            "inputPreview": "src/main.rs",
            "requestId": "req-engine-7-3",
        }))
        .expect("permission request parses");
        assert_eq!(permission.request_id.as_deref(), Some("req-engine-7-3"));

        let question: QuestionRequest = serde_json::from_value(json!({
            "question": "Which?",
            "options": ["a", "b"],
            "requestId": "req-engine-7-4",
        }))
        .expect("question request parses");
        assert_eq!(question.request_id.as_deref(), Some("req-engine-7-4"));

        let plan: PlanApprovalRequest = serde_json::from_value(json!({
            "plan": "do it",
            "requestId": "req-engine-7-5",
        }))
        .expect("plan approval request parses");
        assert_eq!(plan.request_id.as_deref(), Some("req-engine-7-5"));
    }

    #[test]
    fn a_legacy_engine_that_omits_the_handle_still_parses() {
        // Additive means additive: the field's absence is legal, and it must
        // never be back-filled with a fabricated handle.
        let permission: PermissionRequest =
            serde_json::from_value(json!({ "toolName": "edit", "inputPreview": "x" }))
                .expect("legacy permission request parses");
        assert_eq!(permission.request_id, None);

        let plan: PlanApprovalRequest =
            serde_json::from_value(json!({ "plan": "x" })).expect("legacy plan parses");
        assert_eq!(plan.request_id, None);
    }

    #[test]
    fn initialize_params_serialise_as_camel_case() {
        let params = InitializeParams::new("coda-tui");
        assert_eq!(
            serde_json::to_value(&params).unwrap(),
            json!({ "protocolVersion": "1", "clientInfo": "coda-tui" })
        );
    }

    #[test]
    fn initialize_params_omit_absent_optionals() {
        let value = serde_json::to_value(InitializeParams::new("x")).unwrap();
        assert!(value.get("apiKey").is_none());
        assert!(value.get("sessionId").is_none());
    }

    #[test]
    fn initialize_params_carry_a_resumed_session_id() {
        let params = InitializeParams::new("coda-tui").resume("abc123");
        let value = serde_json::to_value(&params).unwrap();
        assert_eq!(value["sessionId"], "abc123");
    }

    #[test]
    fn initialize_result_parses_without_the_optional_telemetry_path() {
        let result: InitializeResult = serde_json::from_value(json!({
            "protocolVersion": "1",
            "sessionId": "s1",
            "serverInfo": "coda"
        }))
        .expect("parse");
        assert_eq!(result.session_id, "s1");
        assert!(result.telemetry_log_path.is_none());
    }

    #[test]
    fn prompt_params_omit_an_empty_image_list() {
        let value = serde_json::to_value(PromptParams::text("hi")).unwrap();
        assert_eq!(value, json!({ "text": "hi" }));
    }

    #[test]
    fn prompt_result_parses_a_successful_turn() {
        let result: PromptResult = serde_json::from_value(json!({
            "ok": true, "stopReason": "end_turn", "interrupted": false
        }))
        .expect("parse");
        assert!(result.ok);
        assert_eq!(result.stop_reason.as_deref(), Some("end_turn"));
        assert!(result.goal_status.is_none());
    }

    #[test]
    fn prompt_result_parses_an_interrupted_turn_without_a_stop_reason() {
        let result: PromptResult =
            serde_json::from_value(json!({ "ok": false, "interrupted": true })).expect("parse");
        assert!(result.interrupted);
        assert!(result.stop_reason.is_none());
    }

    #[test]
    fn prompt_result_parses_a_goal_status() {
        let result: PromptResult = serde_json::from_value(json!({
            "ok": true,
            "interrupted": false,
            "goalStatus": {
                "outcome": "Unmet",
                "remaining": "tests still failing",
                "continuations": 3,
                "elapsedSeconds": 42.5,
                "escalated": true,
                "extensionUsed": false
            }
        }))
        .expect("parse");
        let goal = result.goal_status.expect("goal status");
        assert_eq!(goal.outcome, "Unmet");
        assert_eq!(goal.continuations, 3);
        assert!(goal.escalated);
    }

    #[test]
    fn model_falls_back_to_its_id_when_unnamed() {
        let model: WireModel = serde_json::from_value(json!({ "id": "gpt-5" })).expect("parse");
        assert_eq!(model.label(), "gpt-5");
    }

    #[test]
    fn model_prefers_its_display_name() {
        let model: WireModel =
            serde_json::from_value(json!({ "id": "gpt-5", "displayName": "GPT-5" }))
                .expect("parse");
        assert_eq!(model.label(), "GPT-5");
    }

    #[test]
    fn set_goal_params_serialise_a_null_goal_to_clear_it() {
        let value = serde_json::to_value(SetGoalParams::default()).unwrap();
        assert_eq!(value, json!({ "goal": null }));
    }

    #[test]
    fn permission_response_serialises_the_allow_flag() {
        assert_eq!(
            serde_json::to_value(PermissionResponse { allow: true }).unwrap(),
            json!({ "allow": true })
        );
    }

    #[test]
    fn question_request_parses_its_options() {
        let request: QuestionRequest = serde_json::from_value(json!({
            "question": "Which?", "options": ["a", "b"],
            "multiSelect": false, "allowFreeText": true
        }))
        .expect("parse");
        assert_eq!(request.options, vec!["a", "b"]);
        assert!(request.allow_free_text);
    }

    #[test]
    fn correlation_detects_a_complete_id_set() {
        let correlation = Correlation {
            root_turn_id: Some("t".into()),
            activity_id: Some("a".into()),
            call_id: Some("c".into()),
            source_id: Some("root:t".into()),
            batch_id: Some("a".into()),
        };
        assert!(correlation.is_complete());
        assert!(!correlation.is_subagent());
    }

    #[test]
    fn correlation_detects_a_partial_id_set() {
        let correlation = Correlation {
            root_turn_id: Some("t".into()),
            ..Default::default()
        };
        assert!(!correlation.is_complete());
    }

    #[test]
    fn correlation_extracts_a_subagent_task_id() {
        let correlation = Correlation {
            source_id: Some("subagent:task-9".into()),
            ..Default::default()
        };
        assert!(correlation.is_subagent());
        assert_eq!(correlation.subagent_task_id(), Some("task-9"));
    }

    #[test]
    fn schedule_task_parses_with_only_required_fields() {
        let task: ScheduledTask = serde_json::from_value(json!({ "id": "s1" })).expect("parse");
        assert_eq!(task.id, "s1");
        assert!(task.name.is_none());
    }

    #[test]
    fn results_tolerate_completely_empty_objects() {
        // The host omits null fields, so every optional must have a default.
        let _: PromptResult = serde_json::from_value(json!({})).expect("prompt");
        let _: ModelsResult = serde_json::from_value(json!({})).expect("models");
        let _: HistoryResult = serde_json::from_value(json!({})).expect("history");
        let _: SkillsListResult = serde_json::from_value(json!({})).expect("skills");
        let _: PluginsListResult = serde_json::from_value(json!({})).expect("plugins");
        let _: HooksListResult = serde_json::from_value(json!({})).expect("hooks");
        let _: ScheduleListResult = serde_json::from_value(json!({})).expect("schedules");
        let _: RecallSteeringResult = serde_json::from_value(json!({})).expect("steering");
    }

    #[test]
    fn the_active_model_is_labelled_from_the_list() {
        let result: ModelsResult = serde_json::from_value(json!({
            "source": "live",
            "model": "claude-opus-4-6",
            "models": [
                { "id": "claude-opus-5", "displayName": "Claude Opus 5", "contextLimit": 200000 },
                { "id": "claude-opus-4-6", "displayName": "Claude Opus 4.6", "contextLimit": 150000 }
            ]
        }))
        .expect("models");

        // Not the first entry -- that is the bug this exists to prevent.
        assert_eq!(result.active_label(), Some("Claude Opus 4.6"));
        assert_eq!(result.active_context_limit(), Some(150_000));
    }

    #[test]
    fn an_active_model_missing_from_the_list_still_names_itself() {
        // The engine is running it whatever the catalogue says, and naming an
        // unrelated entry instead would be a confident lie.
        let result: ModelsResult = serde_json::from_value(json!({
            "model": "some-unlisted-model",
            "models": [{ "id": "claude-opus-5", "displayName": "Claude Opus 5" }]
        }))
        .expect("models");

        assert_eq!(result.active_label(), Some("some-unlisted-model"));
        assert_eq!(result.active_context_limit(), None);
    }

    #[test]
    fn an_engine_that_reports_no_active_model_gets_no_label() {
        // An older engine omits the field. Better to leave the status bar
        // as it was than to invent an answer from list order.
        let result: ModelsResult = serde_json::from_value(json!({
            "models": [{ "id": "claude-opus-5", "displayName": "Claude Opus 5" }]
        }))
        .expect("models");

        assert_eq!(result.active_label(), None);
        assert_eq!(result.provider_id, None);
    }
}