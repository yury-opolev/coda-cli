//! `StateSnapshot` and its section DTOs — the wire shape for `session/getState`.
//!
//! See `docs/superpowers/plans/2026-09-08-serve-api-implementation.md` §2.3.
//! This module defines pure, dependency-free DTOs; the projection from live
//! engine state into these types lives in `coda-serve` (`state/` modules),
//! which is the only crate that touches `coda_llm`/`coda_agent` types.
//!
//! Optional fields remain unknown when the engine cannot report them.
//! Configuration distinguishes captured active-turn values from next-turn
//! settings; pending requests are the engine's authoritative registry.

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

use crate::history::HistoryEntry;
use crate::messages::CapabilityEntry;

// ─────────────────────────────────────────────────────────────────────────────
// Lifecycle / activity
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum EngineLifecycle {
    Initializing,
    Ready,
    Busy,
    Stopping,
    Stopped,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum ActivityPhase {
    Preparing,
    WaitingForModel,
    Reasoning,
    Responding,
    RunningTools,
    AwaitingUserInput,
    Compacting,
    /// An administrative session mutation that owns the single-flight slot
    /// exactly like a turn does — `session/fork`, `session/rewind`. These
    /// rewrite the conversation, so a prompt must not run against a history
    /// they are half way through replacing.
    Maintenance,
}

// ─────────────────────────────────────────────────────────────────────────────
// Steering
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum SteeringOutcomeKind {
    Delivered,
    Recalled,
    CancelledTurnEnded,
    Rejected,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SteeringPendingDto {
    pub message_id: String,
    pub enqueued_at: String,
    /// The full original text (§2.3a) — never a preview. Capped; see
    /// `text_truncated`.
    pub text: String,
    pub text_length: i64,
    pub text_truncated: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SteeringOutcomeDto {
    pub message_id: String,
    pub outcome: SteeringOutcomeKind,
    pub at: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub turn_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct SteeringQueueState {
    pub pending_count: i64,
    pub pending: Vec<SteeringPendingDto>,
    /// Bounded ring, newest last.
    pub outcomes: Vec<SteeringOutcomeDto>,
    pub outcomes_truncated: bool,
    pub retained_outcomes: i64,
}

impl Default for SteeringQueueState {
    fn default() -> Self {
        Self {
            pending_count: 0,
            pending: Vec::new(),
            outcomes: Vec::new(),
            outcomes_truncated: false,
            retained_outcomes: 0,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tools
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum ToolCallStateStatus {
    Queued,
    Running,
    AwaitingPermission,
    Completed,
    Failed,
    Cancelled,
    Skipped,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ToolCallState {
    pub call_id: String,
    pub batch_id: String,
    pub turn_id: String,
    pub tool_name: String,
    pub status: ToolCallStateStatus,
    pub started_at: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub elapsed_ms: Option<i64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ended_at: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub is_error: Option<bool>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub result_summary: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ToolsState {
    pub active: Vec<ToolCallState>,
    /// Bounded ring of recently completed calls.
    pub recently_completed: Vec<ToolCallState>,
    /// `true` once the bounded ring has dropped at least one call this
    /// session: the two lists above are then explicitly **not** the complete
    /// set of calls, and a client must not treat them as one (C3).
    #[serde(default)]
    pub truncated: bool,
    /// The advertised bound on how many calls are retained in total.
    #[serde(default)]
    pub retained: i64,
}

// ─────────────────────────────────────────────────────────────────────────────
// Turn / activity
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ModelRequestState {
    pub request_id: String,
    pub started_at: String,
    pub observed_reasoning: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ToolBatchRef {
    pub batch_id: String,
    pub started_at: String,
    pub call_ids: Vec<String>,
}

/// Background/subagent operation counters. `None` means "unknown" (services
/// not yet initialised) — never a confident zero.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ConcurrentCounters {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub background_tasks: Option<i64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scheduled_runs: Option<i64>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ActiveConfig {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provider_id: Option<String>,
    pub model: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub effort: Option<String>,
    pub effort_is_auto: bool,
    pub permission_mode: String,
    /// `"default" | "sessionOverride" | "startup"`.
    pub system_prompt_source: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct TurnState {
    pub turn_id: String,
    /// When the turn began, as the **engine's** UTC wall clock.
    ///
    /// Useful for display, but never for measuring duration from a different
    /// machine: a remote client's clock may be skewed, and re-deriving the
    /// elapsed time from this string makes a reconnecting timer jump. Use
    /// [`Self::elapsed_ms`] for that.
    pub started_at: String,
    /// How long this turn has been running, measured by the **server's own
    /// monotonic clock** at the instant the snapshot was taken.
    ///
    /// This is what a rehydrating or remote client seeds its timer from:
    /// it needs no clock agreement, is immune to wall-clock adjustments, and
    /// never rewinds across a resync. Absent only when the engine did not
    /// report it (an older build) — never a confident `0`, which would look
    /// like a turn that just started.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub elapsed_ms: Option<i64>,
    pub phase: ActivityPhase,
    pub phase_since: String,
    /// How long the turn has been in [`Self::phase`], on the same monotonic
    /// clock as [`Self::elapsed_ms`]. "Waiting for the model for 40 s" is a
    /// different fact from "this turn started 4 minutes ago", and a client
    /// showing a per-phase spinner needs the former.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub phase_elapsed_ms: Option<i64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model_request: Option<ModelRequestState>,
    pub batches: Vec<ToolBatchRef>,
    /// The in-flight turn projected as the same `HistoryEntry` DTOs used by
    /// `session/getHistory` (§2.5). Committed history + `liveEntries` is the
    /// whole conversation as of `StateSnapshot.cursor`.
    pub live_entries: Vec<HistoryEntry>,
    pub live_truncated: bool,
    pub live_omitted_bytes: i64,
    pub active_config: ActiveConfig,
    #[serde(default)]
    pub concurrent: ConcurrentCounters,
}

/// Why a turn failed, in a form that is safe to retain and to serve from
/// `session/getState` for the rest of the session.
///
/// `AgentError`/`LlmError` `Display` and `Debug` carry provider-authored text
/// and can embed a credential-bearing URL or a request body. None of that may
/// end up here: a `lastTurnOutcome` is polled indefinitely by any connected
/// client, so it carries only a fixed classification token, the HTTP status
/// when there was one, and — at most — one allowlisted request-parameter path
/// extracted by `coda_llm::diagnostics` (never free text, never a message).
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct TurnErrorSummary {
    /// Closed set: `"cancelled"`, `"incomplete"`, `"agent.other"`,
    /// `"engine.<reason>"`, or `"llm.<coda_llm::diagnostics::category>"`.
    pub category: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub status: Option<i64>,
    /// A bounded, allowlisted request-parameter path when the provider named
    /// exactly one from a fixed set — never derived from a message body.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parameter: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct LastTurnOutcome {
    pub turn_id: String,
    pub ended_at: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stop_reason: Option<String>,
    pub interrupted: bool,
    /// Safe classification only — see [`TurnErrorSummary`]. The raw provider
    /// message is still returned once, synchronously, in the `session/prompt`
    /// reply; it is never retained here.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<TurnErrorSummary>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Pending reverse requests
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq, Hash)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum PendingRequestKind {
    Permission,
    Question,
    PlanApproval,
}

impl PendingRequestKind {
    pub fn as_str(self) -> &'static str {
        match self {
            PendingRequestKind::Permission => "permission",
            PendingRequestKind::Question => "question",
            PendingRequestKind::PlanApproval => "planApproval",
        }
    }

    /// The outcome applied when nothing answers: a fault is never a grant.
    pub fn fail_closed_default(self) -> &'static str {
        match self {
            PendingRequestKind::Permission => "deny",
            PendingRequestKind::Question => "noAnswer",
            PendingRequestKind::PlanApproval => "reject",
        }
    }
}

/// One outstanding server-initiated request, as seen by a client that did not
/// issue the original `request/*` round-trip (or reconnected after it).
///
/// `display` carries only what a UI needs to render the prompt: a tool name, a
/// capped input preview, the question and its options, or a capped plan. It
/// never carries a credential, a header value or an environment value.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct PendingRequestDto {
    /// Opaque handle, bound to the engine instance that minted it. A handle
    /// from a previous engine process is rejected, never silently re-applied
    /// to whatever happens to be pending now.
    pub request_id: String,
    pub kind: PendingRequestKind,
    pub issued_at: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub turn_id: Option<String>,
    /// The provider tool-call id, when the request is attached to one.
    ///
    /// **Reserved, and always omitted by the Rust engine.** The
    /// permission/question/plan-approval seams it would have to come from do
    /// not carry the id, and inventing a plausible one would be worse than
    /// saying nothing. `requests.callCorrelation` is advertised as
    /// `supported: false` for exactly this reason: correlate on `turnId`
    /// together with the `event/toolCall` stream instead. The field stays on
    /// the wire shape so a host that *does* have the id can populate it
    /// without a contract change.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub call_id: Option<String>,
    pub display: serde_json::Value,
    /// `"deny" | "noAnswer" | "reject"`.
    pub fail_closed_default: String,
}



#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct UsagePair {
    pub input_tokens: i64,
    pub output_tokens: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Default)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct UsageState {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_response: Option<UsagePair>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub session: Option<UsagePair>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub context_limit: Option<i64>,
    #[serde(default)]
    pub unknown_fields: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct Limits {
    pub ring_envelopes: i64,
    pub ring_bytes: i64,
    pub live_bytes_cap: i64,
    pub outcomes_retained: i64,
    pub history_block_bytes_cap: i64,
    /// Largest `limit` `session/getHistory` will honour. A larger request is
    /// clamped to this (`truncated`/`nextIndex` still say there is more); a
    /// `limit` below 1 is a typed `-32602`. Published so the ceiling is
    /// discoverable rather than folklore.
    pub max_history_page: i64,
    /// The same ceiling for `session/listSessions`.
    pub max_session_page: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct EffectiveConfig {
    /// Captured into the running turn; `None` when idle.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub active: Option<ActiveConfig>,
    /// What the next turn will use — read live from the engine's mutable
    /// config at snapshot time.
    pub next: ActiveConfig,
    /// Derived, never invented: the fields where `active` and `next` really
    /// differ, each tagged with when the pending value actually takes hold.
    /// Empty when idle (there is no captured config to differ from).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub differing: Vec<ConfigDifference>,
}

/// One field where the running turn's captured config differs from what the
/// next turn / next check will use.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct ConfigDifference {
    pub key: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub active: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub next: Option<String>,
    pub applies_when: crate::config::AppliesWhen,
}

/// Computes [`EffectiveConfig::differing`] from an active/next pair.
///
/// There is no pending-change scheduler in the engine: these values are read
/// at turn-build time, so "differing" is a *derivation*, not a queue. Each key
/// is tagged with the real `appliesAt` the engine implements.
pub fn config_differences(active: &ActiveConfig, next: &ActiveConfig) -> Vec<ConfigDifference> {
    use crate::config::AppliesWhen;
    let mut out = Vec::new();
    let mut push = |key: &str, a: Option<String>, n: Option<String>, w: AppliesWhen| {
        if a != n {
            out.push(ConfigDifference { key: key.to_string(), active: a, next: n, applies_when: w });
        }
    };
    push(
        "model",
        Some(active.model.clone()),
        Some(next.model.clone()),
        AppliesWhen::NextTurn,
    );
    push("effort", active.effort.clone(), next.effort.clone(), AppliesWhen::NextTurn);
    push(
        "permissionMode",
        Some(active.permission_mode.clone()),
        Some(next.permission_mode.clone()),
        AppliesWhen::NextPermissionCheck,
    );
    push(
        "systemPrompt",
        Some(active.system_prompt_source.clone()),
        Some(next.system_prompt_source.clone()),
        AppliesWhen::NextTurn,
    );
    push(
        "provider",
        active.provider_id.clone(),
        next.provider_id.clone(),
        AppliesWhen::NewEngineInstance,
    );
    out
}

// ─────────────────────────────────────────────────────────────────────────────
// StateSnapshot
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct StateSnapshot {
    pub contract_version: String,
    pub engine_instance_id: String,
    pub session_id: String,
    pub workspace_path: String,
    /// Every event with `seq <= cursor` is already reflected in this
    /// snapshot; no event with `seq > cursor` is (§2.5).
    pub cursor: i64,
    /// Bumped on fork/rewind/compact/resume (§2.5).
    pub history_epoch: i64,
    /// Number of **committed** conversation messages as of this snapshot.
    ///
    /// This is the fence between committed history and `turn.liveEntries`
    /// (I1): the in-flight turn is *never* included here, and the moment it
    /// is, `turn` is already `None`. A client may therefore concatenate
    /// `history[..historyLength]` with `turn.liveEntries` without ever
    /// counting the same content twice, at any instant, in any interleaving.
    pub history_length: i64,
    pub lifecycle: EngineLifecycle,
    pub initialized: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_turn_outcome: Option<LastTurnOutcome>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub turn: Option<TurnState>,
    pub steering: SteeringQueueState,
    pub tools: ToolsState,
    /// Outstanding server-initiated requests (permission / question / plan
    /// approval). A reconnecting client discovers what the engine is waiting
    /// for here rather than inferring it from silence.
    #[serde(default)]
    pub requests: Vec<PendingRequestDto>,
    pub config: EffectiveConfig,
    pub usage: UsageState,
    pub limits: Limits,
    pub capabilities: HashMap<String, CapabilityEntry>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapshot_round_trips_through_json() {
        let snap = StateSnapshot {
            contract_version: "2026-09-1".into(),
            engine_instance_id: "e1".into(),
            session_id: "s1".into(),
            workspace_path: "/tmp".into(),
            cursor: 42,
            history_epoch: 0,
            history_length: 7,
            lifecycle: EngineLifecycle::Ready,
            initialized: true,
            last_turn_outcome: None,
            turn: None,
            steering: SteeringQueueState::default(),
            tools: ToolsState::default(),
            requests: Vec::new(),
            config: EffectiveConfig {
                active: None,
                differing: Vec::new(),
                next: ActiveConfig {
                    provider_id: Some("anthropic".into()),
                    model: "claude-opus-4-5".into(),
                    effort: None,
                    effort_is_auto: true,
                    permission_mode: "default".into(),
                    system_prompt_source: "default".into(),
                },
            },
            usage: UsageState::default(),
            limits: Limits {
                ring_envelopes: 2048,
                ring_bytes: 4 * 1024 * 1024,
                live_bytes_cap: 256 * 1024,
                outcomes_retained: 200,
                history_block_bytes_cap: 64 * 1024,
                max_history_page: 500,
                max_session_page: 200,
            },
            capabilities: HashMap::new(),
        };
        let v = serde_json::to_value(&snap).unwrap();
        assert_eq!(v["cursor"], 42);
        // Absent optionals must be omitted, not null.
        assert!(v.get("lastTurnOutcome").is_none());
        assert!(v.get("turn").is_none());
        let back: StateSnapshot = serde_json::from_value(v).unwrap();
        assert_eq!(back, snap);
    }

    #[test]
    fn concurrent_counters_default_to_unknown_not_zero() {
        let c = ConcurrentCounters::default();
        assert!(c.background_tasks.is_none());
        assert!(c.scheduled_runs.is_none());
    }
}
