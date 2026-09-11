//! ServeHost implements [ServeBackend] with a real agent loop.
//! See module doc for live vs stubbed breakdown.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use async_trait::async_trait;
use chrono::Utc;
use coda_agent::{
    AgentError, AgentLoopBuilder, CompactionService, GoalBudget, GoalOutcome, GoalStatus,
    GoalSupervisor, HookContentHash, HookRunner, HookScope, HookTrustGuard, HookTrustStore,
    InMemoryHookTrustStore, NullScheduleLifecycleSink, ScheduleRuntime, SubagentFactory,
    TaskManagerRunner, TodoStore, TokenEstimator, ToolQuarantine, ToolRegistry, UserHook,
    SessionTranscriptStore, fork_session, rewind_session, session_id_is_valid,
};
use coda_agent::agent::stop::UserQuestionPrompt;
use coda_agent::events::{AgentEvent, AgentSink};
use coda_agent::goal::ForkedAgent;
use coda_agent::hooks::runner::{HookExecutor, ShellHookExecutor};
use coda_agent::lsp::{LspServerConfig, LspServerManager, LspServerMapBuilder};
use coda_agent::permission::{
    ModePermissionPrompt, PermissionMode, PermissionModeState, PermissionPrompt,
};
use coda_agent::scheduling::{
    ScheduleDefinitionDraft, ScheduleKind, ScheduleTerminalOutcome, ScheduledTaskStore,
};
use coda_agent::subagents::{SubagentHost, MAX_CONCURRENT_SUBAGENTS};
use coda_agent::tasks::TaskManager;
use coda_agent::tool::{PlanApprover, UserQuestion};
use coda_agent::tools::built_in_tools;
use coda_auth::{
    CredentialManager, CredentialManagerSource,
    provider::AuthProvider,
};
use coda_auth::provider::copilot::{
    CopilotConfig as AuthCopilotConfig, CopilotDeployment, ResolvedCopilotConfig,
};
use coda_auth::service::{
    ContextBoundCopilotProvider, CopilotContextCell, CredentialOrigin, ProviderIdentity, Selection,
    SelectionContext, SelectionError, SelectionSource,
};
use coda_auth::store::{AuthStorage, CredentialStore, Profile};
use coda_llm::anthropic::{AnthropicClient, AnthropicConfig};
use coda_llm::reasoning::COPILOT_PROVIDER_ID;
use coda_llm::{
    ChatRequest, Content, CopilotClient, CopilotConfig,
    CredentialSource, Effort, LlmClient, Message, ReasoningCapability, Role,
    resolve_applied_level, resolve_reasoning,
};
use coda_mcp::McpClientManager;
use coda_proto::messages::{CONTRACT_VERSION, PROTOCOL_VERSION};
use serde::Serialize;
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

use crate::dispatch::{
    CancelRequestParams, CompactParams, ConfigSetParams, ForkParams, GetEventsParams,
    GetHistoryParams, GetStateParams, HooksInfoParams, HooksTrustParams, InitParams,
    ListSessionsParams, MessagesParams, ModelsParams, PromptParams, ResolveRequestParams,
    RewindParams, RpcError, ScheduleCreateParams, ScheduleDeleteParams, ServeBackend,
    SetEffortParams, SetGoalParams, SetModelParams, SetPermissionModeParams,
    SetSystemPromptParams, SteerParams,
};
use crate::dispatch::AdjustEffortParams;
use crate::prompts::{PromptChannel, WirePermissionPrompt, WirePlanApprover, WireUserQuestion};
use crate::mcp::McpBundle;
use crate::session::Session;
use crate::state::{EngineState, SteeringStateObserver, TurnEnd};
use coda_proto::state::ActivityPhase;
use crate::state_sink::StateSink;
use crate::capabilities::capability_catalog;
use crate::sink::ServeSink;

// ─────────────────────────────────────────────────────────────────────────────
// Wire result structs — null-valued fields are OMITTED, never `null`
// ─────────────────────────────────────────────────────────────────────────────

type InitializeResponse = coda_proto::messages::InitializeResponse;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct PromptResponse {
    ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    stop_reason: Option<String>,
    interrupted: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    goal_status: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SteerResponse {
    ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    message_id: Option<String>,
    /// Additive (§2.7): `ok:false` used to be mute. `"noActiveTurn"` — no
    /// turn is currently running; `"turnEnding"` — the inbox raced sealed;
    /// `"emptyText"` — nothing to send.
    #[serde(skip_serializing_if = "Option::is_none")]
    rejected_reason: Option<&'static str>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct RecalledMessage {
    id: String,
    text: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    enqueued_at: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct WireHistoryMessage {
    role: String,
    content: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WireModel {
    pub id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub display_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub context_limit: Option<i64>,
    /// US dollars per million input tokens, when the catalogue knows.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub input_cost: Option<f64>,
    /// US dollars per million output tokens, when the catalogue knows.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub output_cost: Option<f64>,
    /// Reasoning-effort levels the model advertises, lowest to highest.
    ///
    /// Omitted (rather than serialised as `[]`) when empty, matching the
    /// behaviour of the Copilot API for models that report nothing.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub reasoning_levels: Vec<String>,
    /// The effort level in force for this model right now.
    ///
    /// For the active model this is the live effective level (after clamps and
    /// any per-model session override); for other rows it is the session
    /// override or saved preference. Omitted when automatic / none, so a client
    /// shows the current level rather than a stale persisted one.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub effort: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SetGoalResponse {
    ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    goal: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    max_duration: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    max_continuations: Option<i32>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SetEffortResponse {
    ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    applied: Option<String>,
    /// The effective effort level after the call, reflecting any clamp or
    /// auto-clear. Omitted when no effort is set (cleared or never set).
    #[serde(skip_serializing_if = "Option::is_none")]
    current: Option<String>,
    /// Always present. The C# host emits `note: ""` on success rather than
    /// omitting it — only *null* properties are dropped, and an empty string
    /// is not null. A client that distinguishes "absent" from "empty" would
    /// see two different engines here, so this field is not optional.
    note: String,
}

/// Result of `model/adjustEffort`: the target model's per-model effort after
/// stepping one rung.
///
/// `model`/`providerId` are the *canonical* identity the client persists the
/// per-model preference under, so a caller never keys a save by a display name
/// or a stale guess. `current` reports the *effective* level actually in force
/// for the target (omitted when automatic). `active` says whether the target is
/// the running model — an inactive edit changes only the stored preference.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ModelEffortResult {
    ok: bool,
    model: String,
    provider_id: String,
    /// The effective level after the step; omitted / `null` means automatic.
    #[serde(skip_serializing_if = "Option::is_none")]
    current: Option<String>,
    /// Whether the target model is the currently active one.
    active: bool,
    /// Always present (may be empty), mirroring `setEffort`'s note contract:
    /// a boundary clamp or a refusal explains itself here rather than lying
    /// with a phantom level change.
    note: String,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CompactResponse {
    ok: bool,
    messages_before: i64,
    messages_after: i64,
    #[serde(skip_serializing_if = "Option::is_none")]
    tokens_before: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    tokens_after: Option<i64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

type ForkResponse = coda_proto::responses::ForkResponse;
type RewindResponse = coda_proto::responses::RewindResponse;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ScheduledTaskResponse {
    id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    name: Option<String>,
    kind: String,
    prompt: String,
    rule: String,
    time_zone: String,
    next_run_utc: String,
    state: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    active_task_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    last_outcome: Option<String>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Internal state
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Default, Clone)]
struct GoalParams {
    goal: Option<String>,
    max_duration: Option<String>,
    max_continuations: Option<i32>,
}

// ─────────────────────────────────────────────────────────────────────────────
// TurnSink — captures stop_reason from the agent's Stop event
// ─────────────────────────────────────────────────────────────────────────────

struct TurnSink {
    inner: Arc<dyn AgentSink>,
    stop_reason: Mutex<Option<String>>,
}

impl TurnSink {
    fn new(inner: Arc<dyn AgentSink>) -> Arc<Self> {
        Arc::new(Self { inner, stop_reason: Mutex::new(None) })
    }

    fn take_stop_reason(&self) -> Option<String> {
        self.stop_reason.lock().expect("turn sink poisoned").clone()
    }
}

impl AgentSink for TurnSink {
    fn emit(&self, event: AgentEvent) {
        if let AgentEvent::Stop { ref stop_reason } = event {
            *self.stop_reason.lock().expect("turn sink poisoned") = stop_reason.clone();
        }
        self.inner.emit(event);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// LlmForkedAgent — ForkedAgent for GoalSupervisor, uses the session client
// ─────────────────────────────────────────────────────────────────────────────

struct LlmForkedAgent {
    client: Arc<dyn LlmClient>,
    model: String,
}

#[async_trait]
impl ForkedAgent for LlmForkedAgent {
    async fn run(
        &self,
        system: &str,
        messages: Vec<Message>,
        cancel: CancellationToken,
    ) -> anyhow::Result<String> {
        let request = ChatRequest::new(self.model.clone(), messages)
            .with_system(system.to_string())
            .with_max_tokens(512);
        let stream = self.client.stream(request).await?;
        tokio::select! {
            result = stream.collect() => Ok(result?.text),
            _ = cancel.cancelled() => anyhow::bail!("cancelled"),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SessionServices — built lazily on the first prompt when the client is known
// ─────────────────────────────────────────────────────────────────────────────

struct SessionServices {
    hook_runner: Arc<HookRunner>,
    subagent_host: Arc<SubagentHost>,
    schedule_runtime: Arc<ScheduleRuntime>,
}

// ─────────────────────────────────────────────────────────────────────────────
// ServeHost
// ─────────────────────────────────────────────────────────────────────────────

/// Maps the wire spelling of a permission mode onto the enum.
///
/// The spellings match the C# `PermissionModeNames`, so a settings file or a
/// hook written for either build means the same thing in both.
fn parse_permission_mode(value: &str) -> Option<PermissionMode> {
    match value.trim().to_ascii_lowercase().as_str() {
        "default" | "ask" => Some(PermissionMode::Default),
        "acceptedits" | "accept-edits" | "edits" => Some(PermissionMode::AcceptEdits),
        "plan" => Some(PermissionMode::Plan),
        "bypasspermissions" | "bypass" | "yolo" => Some(PermissionMode::BypassPermissions),
        _ => None,
    }
}

/// The wire spelling for a mode, for reporting what was applied.
fn wire_permission_mode(mode: PermissionMode) -> &'static str {
    match mode {
        PermissionMode::Default => "default",
        PermissionMode::AcceptEdits => "acceptEdits",
        PermissionMode::Plan => "plan",
        PermissionMode::BypassPermissions => "bypassPermissions",
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// StartupOptions — validated-once engine startup configuration (Findings I2/I4)
// ─────────────────────────────────────────────────────────────────────────────

/// Tri-state startup reasoning-effort override.
///
/// The distinction between `Auto` and `Unset` is deliberate: `--effort auto`
/// is an *explicit* request for automatic effort and must take precedence over
/// the saved per-model preference, while the absence of any override falls back
/// to that saved preference (Finding I4 — "explicit-auto precedence").
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum StartupEffort {
    /// No CLI/env override — fall back to the saved per-model preference.
    #[default]
    Unset,
    /// Explicit "automatic": clear effort and do NOT read the saved preference.
    Auto,
    /// Explicit level.
    Level(Effort),
}

/// A fatal startup-configuration error.
///
/// Its message is safe to surface to the user and to logs: it never embeds a
/// secret value (api keys are described, never quoted).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StartupError(pub String);

impl std::fmt::Display for StartupError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.0)
    }
}

impl std::error::Error for StartupError {}

/// Canonicalises a user-facing provider alias to the engine's provider id.
///
/// Delegates to [`coda_auth::service::canonical_engine_provider`], the single
/// alias table shared with the auth service, so a name the CLI accepts cannot
/// mean something different here. Unknown aliases are lower-cased and returned
/// unchanged so that provider selection rejects them explicitly rather than
/// silently rewriting them to a working provider.
///
/// Note the deliberate asymmetry it carries: the *stored* API-key credential
/// is `anthropic-api-key`, while the engine and `settings.json` call that
/// identity `anthropic`. `claude-ai` is a different account on the same
/// transport and keeps its own id.
pub fn canonical_provider(raw: &str) -> String {
    coda_auth::service::canonical_engine_provider(raw)
}

/// Where this process may send an Anthropic **API-key** request, decided once
/// and carried whole.
///
/// A `Result` in a field would say the same thing, but this names the third
/// state that matters: *nothing was resolved at all*. A refusal is not an
/// absence — an absent endpoint means "the default host stands", which is
/// exactly the answer a refusal must never be turned into. Keeping the two
/// apart is what lets a refused `ANTHROPIC_BASE_URL` travel through a host
/// whose provider does not care about it (Copilot, Claude.ai) and still stop
/// an API-key client built later by `initialize(apiKey)`.
#[derive(Clone, Debug, PartialEq, Eq, Default)]
pub enum ApiKeyEndpoint {
    /// Nothing has been resolved (tests, embedding hosts). An explicit
    /// `--endpoint` is validated on demand instead; nothing reads the
    /// environment, so a variable set by one test cannot perturb another
    /// (Finding I4).
    #[default]
    Unresolved,
    /// The base URL in force for Anthropic API-key requests, already through
    /// the resolver.
    Approved(String),
    /// The configured endpoint was refused. The message is safe to print (it
    /// never echoes the rejected value) and is the error every API-key client
    /// in this process fails with.
    Refused(String),
}

/// Validated engine startup options, parsed exactly once.
///
/// In production [`StartupOptions::from_env`] reads and validates every
/// `CODA_SERVE_*` variable a single time in the transport; in tests the fields
/// are set explicitly. Crucially, [`ServeHost::build`] reads *this struct*, not
/// the process environment, so a `CODA_SERVE_*` variable set by one test can
/// never perturb an unrelated host constructed in parallel (Finding I4). An
/// explicitly-invalid value fails startup instead of silently defaulting
/// (Finding I2).
#[derive(Clone, Default)]
pub struct StartupOptions {
    pub model: Option<String>,
    pub effort: StartupEffort,
    pub permission_mode: Option<PermissionMode>,
    pub system_prompt: Option<String>,
    pub goal: Option<String>,
    pub goal_max_duration: Option<String>,
    pub goal_max_continuations: Option<i32>,
    /// Canonical provider id requested via `--provider`. Client selection
    /// happens in the transport before the host is built; retained for the
    /// mismatch guard on `initialize`.
    pub provider: Option<String>,
    /// Explicit API key (`--api-key`). Never logged; redacted in `Debug`.
    pub api_key: Option<String>,
    /// Explicit endpoint base URL (`--endpoint`). Requires `api_key`.
    pub endpoint: Option<String>,
    /// What this process resolved for the Anthropic **API-key** endpoint,
    /// success *or* refusal, decided once by [`StartupOptions::from_env`].
    ///
    /// Carries the whole precedence — `--endpoint`, then `ANTHROPIC_BASE_URL`,
    /// then the default host — already decided, so nothing downstream re-reads
    /// the environment and reaches a different answer, and a refusal cannot
    /// decay into "no endpoint configured" on the way to a client.
    ///
    /// It is consulted **only** where an API-key client is actually built. A
    /// Copilot or Claude.ai session resolves its own endpoints and starts
    /// normally whatever this holds.
    pub anthropic_endpoint: ApiKeyEndpoint,
}

impl std::fmt::Debug for StartupOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StartupOptions")
            .field("model", &self.model)
            .field("effort", &self.effort)
            .field("permission_mode", &self.permission_mode)
            .field("system_prompt", &self.system_prompt.as_ref().map(|_| "<set>"))
            .field("goal", &self.goal)
            .field("goal_max_duration", &self.goal_max_duration)
            .field("goal_max_continuations", &self.goal_max_continuations)
            .field("provider", &self.provider)
            // Never surface the key itself, in Debug or anywhere else.
            .field("api_key", &self.api_key.as_ref().map(|_| "***"))
            .field("endpoint", &self.endpoint)
            // The resolved decision is deliberately absent: it either repeats
            // `endpoint` or came from the environment, and a resolved base URL
            // can carry a path — which can carry a tenant id.
            .finish_non_exhaustive()
    }
}

impl StartupOptions {
    /// Reads and validates every `CODA_SERVE_*` startup variable exactly once.
    ///
    /// Returns a [`StartupError`] for any explicitly-invalid value (bad effort,
    /// unknown permission mode, non-integer / negative continuation budget,
    /// non-positive or unparseable goal timeout, or an endpoint without a key).
    pub fn from_env() -> Result<Self, StartupError> {
        fn non_empty(var: &str) -> Option<String> {
            std::env::var(var)
                .ok()
                .map(|s| s.trim().to_owned())
                .filter(|s| !s.is_empty())
        }

        let model = non_empty("CODA_SERVE_MODEL");

        let effort = match non_empty("CODA_SERVE_EFFORT") {
            None => StartupEffort::Unset,
            Some(raw) if raw.eq_ignore_ascii_case("auto") => StartupEffort::Auto,
            Some(raw) => match Effort::parse(&raw) {
                Some(e) => StartupEffort::Level(e),
                None => {
                    return Err(StartupError(format!(
                        "invalid CODA_SERVE_EFFORT '{raw}' \
                         (expected low, medium, high, xhigh, max, or auto)"
                    )))
                }
            },
        };

        let permission_mode = match non_empty("CODA_SERVE_PERMISSION_MODE") {
            None => None,
            Some(raw) => match parse_permission_mode(&raw) {
                Some(m) => Some(m),
                None => {
                    return Err(StartupError(format!(
                        "invalid CODA_SERVE_PERMISSION_MODE '{raw}' \
                         (expected default, acceptEdits, plan, or bypassPermissions)"
                    )))
                }
            },
        };

        let system_prompt = non_empty("CODA_SERVE_SYSTEM_PROMPT");
        let goal = non_empty("CODA_SERVE_GOAL");
        let goal_max_duration = non_empty("CODA_SERVE_GOAL_TIMEOUT");
        let goal_max_continuations = match non_empty("CODA_SERVE_GOAL_MAX_CONTINUATIONS") {
            None => None,
            Some(raw) => match raw.parse::<i32>() {
                Ok(n) => Some(n),
                Err(_) => {
                    return Err(StartupError(format!(
                        "invalid CODA_SERVE_GOAL_MAX_CONTINUATIONS '{raw}' \
                         (expected a non-negative integer)"
                    )))
                }
            },
        };

        let provider = non_empty("CODA_SERVE_PROVIDER").map(|p| canonical_provider(&p));
        let api_key = non_empty("CODA_SERVE_API_KEY");
        let endpoint = non_empty("CODA_SERVE_ENDPOINT");

        let opts = Self {
            model,
            effort,
            permission_mode,
            system_prompt,
            goal,
            goal_max_duration,
            goal_max_continuations,
            provider,
            api_key,
            endpoint,
            anthropic_endpoint: ApiKeyEndpoint::Unresolved,
        };
        opts.validate()?;
        // Resolved after `validate`, so `--endpoint requires --api-key` is
        // still decided on the explicit value alone: `ANTHROPIC_BASE_URL` is
        // ordinary user configuration and must work with a stored or exported
        // key, which have no `--api-key` to pair with.
        let anthropic_endpoint =
            Self::resolve_api_key_endpoint(opts.endpoint.as_deref(), |name| {
                std::env::var(name).ok()
            })?;
        Ok(Self { anthropic_endpoint, ..opts })
    }

    /// Decides the Anthropic API-key endpoint from an injected environment.
    ///
    /// Pure but for `env`, and deliberately asymmetric about *when* a refusal
    /// becomes fatal:
    ///
    /// - an explicit `--endpoint` is refused **here**. It cannot be anything
    ///   but an API-key configuration — clap requires `--api-key` beside it —
    ///   so there is no provider for which it is irrelevant, and failing at
    ///   the point of configuration is the clearest place to say so.
    /// - `ANTHROPIC_BASE_URL` is *captured* as [`ApiKeyEndpoint::Refused`].
    ///   It is ordinary user configuration scoped to one identity: an engine
    ///   signed in to GitHub Copilot or Claude.ai must start normally, exactly
    ///   as `coda auth login copilot` does with the same variable set. The
    ///   refusal is not discarded — it travels with the options and fails
    ///   every API-key client this process could still build, including one
    ///   built later by `initialize(apiKey)` on a Copilot host.
    ///
    /// A refusal must never be answered by quietly building a client at
    /// Anthropic's own host: a user who pointed this engine at a gateway would
    /// then send their key to a completely different place than the one they
    /// configured.
    pub(crate) fn resolve_api_key_endpoint(
        explicit: Option<&str>,
        env: impl Fn(&str) -> Option<String>,
    ) -> Result<ApiKeyEndpoint, StartupError> {
        let has_explicit = explicit.map(str::trim).is_some_and(|value| !value.is_empty());
        match coda_auth::service::endpoint::resolve(explicit, env) {
            Ok(resolved) => Ok(ApiKeyEndpoint::Approved(resolved.base_url().to_owned())),
            Err(reason) if has_explicit => Err(StartupError(format!(
                "invalid Anthropic endpoint from the configured --endpoint: {reason}"
            ))),
            Err(reason) => Ok(ApiKeyEndpoint::Refused(format!(
                "invalid Anthropic endpoint from {}: {reason}",
                coda_auth::service::ANTHROPIC_BASE_URL_ENV
            ))),
        }
    }

    /// Validates budget shape and cross-field constraints. Never clamps: an
    /// out-of-range value is an error, not something to be silently coerced.
    pub fn validate(&self) -> Result<(), StartupError> {
        validate_goal_budget(self.goal_max_duration.as_deref(), self.goal_max_continuations)?;
        if self.endpoint.is_some() && self.api_key.as_deref().unwrap_or("").is_empty() {
            return Err(StartupError(
                "an --endpoint override requires an explicit --api-key".into(),
            ));
        }
        Ok(())
    }

    /// The Anthropic **API-key** base URL in force — or the refusal that must
    /// stop any API-key client this process builds.
    ///
    /// `Ok(None)` means "nothing is configured, the client's default host
    /// stands"; it is never how a refusal is reported.
    ///
    /// Production startup resolves this once in [`Self::from_env`]. A
    /// programmatically constructed value (tests, embedding hosts) has no
    /// resolution, so the explicit `--endpoint` is validated here instead —
    /// through the same pure resolver, with no environment read, so such a
    /// caller cannot slip a raw, unvalidated URL past the rules that every
    /// other path is held to.
    pub(crate) fn api_key_endpoint(&self) -> Result<Option<String>, StartupError> {
        match &self.anthropic_endpoint {
            ApiKeyEndpoint::Approved(base) => Ok(Some(base.clone())),
            ApiKeyEndpoint::Refused(message) => Err(StartupError(message.clone())),
            ApiKeyEndpoint::Unresolved => {
                let Some(explicit) =
                    self.endpoint.as_deref().map(str::trim).filter(|value| !value.is_empty())
                else {
                    return Ok(None);
                };
                match Self::resolve_api_key_endpoint(Some(explicit), |_| None)? {
                    ApiKeyEndpoint::Approved(base) => Ok(Some(base)),
                    // No environment is consulted here, so the only refusal
                    // reachable is the explicit one — and that is returned as
                    // `Err` by the call above. Mapped rather than panicked on.
                    ApiKeyEndpoint::Refused(message) => Err(StartupError(message)),
                    ApiKeyEndpoint::Unresolved => Ok(None),
                }
            }
        }
    }

    fn goal_params(&self) -> GoalParams {
        GoalParams {
            goal: self.goal.clone(),
            max_duration: self.goal_max_duration.clone(),
            max_continuations: self.goal_max_continuations,
        }
    }
}

/// Validates a goal budget: a present timeout must parse to a strictly positive
/// duration, and a present continuation budget must not be negative. Shared by
/// startup parsing and the live `session/setGoal` path so both agree.
fn validate_goal_budget(
    max_duration: Option<&str>,
    max_continuations: Option<i32>,
) -> Result<(), StartupError> {
    if let Some(dur) = max_duration {
        match parse_duration(Some(dur)) {
            Some(d) if d.is_zero() => {
                return Err(StartupError(format!(
                    "invalid goal timeout '{dur}': duration must be greater than zero"
                )))
            }
            Some(_) => {}
            None => {
                return Err(StartupError(format!(
                    "invalid goal timeout '{dur}' (expected e.g. \"30m\", \"2h\", \"90s\")"
                )))
            }
        }
    }
    if let Some(n) = max_continuations {
        if n < 0 {
            return Err(StartupError(format!(
                "invalid goal max-continuations {n}: must not be negative"
            )));
        }
    }
    Ok(())
}

/// The scalar configuration a turn actually runs under, held behind one
/// synchronous lock (CONFIG) so every reader sees a coherent record.
///
/// Model, effort, provider and the session system-prompt override used to be
/// four independent mutexes, read one at a time. Any pair of reads could
/// straddle a commit, which is how a snapshot came to advertise a model whose
/// effort had been resolved for a *different* model, and how `effort: null`
/// could be published next to `effortIsAuto: false` — a state the engine is
/// never actually in. Committing and reading them as one record makes those
/// combinations unrepresentable rather than merely unlikely.
#[derive(Clone)]
struct RuntimeConfig {
    model: String,
    /// Whether `model` was **chosen** (startup `--model`, `session/setModel`,
    /// `config/set model`) rather than resolved as a default.
    ///
    /// Wiring a provider re-resolves the model for it so a default resolved
    /// for a provider that turned out not to be the one connected is never
    /// sent to a provider that has never heard of it. That reasoning does not
    /// extend to a model the operator picked: discarding it to populate
    /// `providerId` would silently run something other than what was asked
    /// for. Internal only — it is not part of any wire record.
    model_is_explicit: bool,
    effort: Option<Effort>,
    /// Session-only custom system prompt. Shared behind an `Arc` so a
    /// coherent read of the record never copies a prompt that may be very
    /// large; the text is cloned only where it is actually needed (building
    /// the agent, answering `config/describe`).
    system_prompt: Option<Arc<str>>,
    /// The provider id of whatever is in `client`, mirrored here.
    ///
    /// `config.next.providerId` and a turn's `activeConfig` are built on
    /// synchronous paths, and reaching for the async client mutex there is
    /// either impossible (`try_claim_turn` cannot `.await`) or a needless
    /// contention point on the lock a running turn holds. `None` means
    /// *genuinely* not wired — never "this code path could not be bothered to
    /// look". Written wherever `client` is written, and only from
    /// `LlmClient::provider_id()`.
    provider_id: Option<String>,
}

/// Never reveals the system-prompt override: a session prompt can carry
/// whatever the operator pasted into it, and a `Debug` of the host must not
/// be the thing that puts it in a log.
impl std::fmt::Debug for RuntimeConfig {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RuntimeConfig")
            .field("model", &self.model)
            .field("model_is_explicit", &self.model_is_explicit)
            .field("effort", &self.effort)
            .field("system_prompt_source", &self.system_prompt_source())
            .field("provider_id", &self.provider_id)
            .finish()
    }
}

impl RuntimeConfig {
    /// `default` or `sessionOverride` — the only thing about the prompt that
    /// is ever published.
    fn system_prompt_source(&self) -> &'static str {
        if self.system_prompt.is_some() { "sessionOverride" } else { "default" }
    }

    /// The wire projection of this record.
    ///
    /// `effort` and `effortIsAuto` are derived from **one** read of one
    /// value, so they cannot contradict each other.
    fn active_config(&self, permission_mode: PermissionMode) -> coda_proto::state::ActiveConfig {
        let effort = self.effort;
        coda_proto::state::ActiveConfig {
            provider_id: self.provider_id.clone(),
            model: self.model.clone(),
            effort: effort.map(|e| e.as_str().to_string()),
            effort_is_auto: effort.is_none(),
            permission_mode: wire_permission_mode(permission_mode).to_string(),
            system_prompt_source: self.system_prompt_source().to_string(),
        }
    }
}

/// A coherent capture of everything a turn is built from: the runtime record
/// plus the permission mode in force at the same instant.
///
/// The agent is built from this record and the turn publishes *this same*
/// record — never a second read — so what the engine tells the world it is
/// running is always what it actually asked the provider for.
#[derive(Clone, Debug)]
struct RuntimeCapture {
    config: RuntimeConfig,
    permission_mode: PermissionMode,
}

impl RuntimeCapture {
    fn active_config(&self) -> coda_proto::state::ActiveConfig {
        self.config.active_config(self.permission_mode)
    }
}

pub struct ServeHost {
    session: Arc<Session>,
    sink: Arc<ServeSink>,
    /// Wired by `initialize`; `None` until then.
    client: tokio::sync::Mutex<Option<Arc<dyn LlmClient>>>,
    /// The single synchronous cell holding the scalar configuration in force
    /// (CONFIG). Every commit writes it and publishes the resulting
    /// `config.next` in one critical section; every reader that needs more
    /// than one of its fields captures the whole record once.
    runtime: Arc<Mutex<RuntimeConfig>>,
    tools: Arc<ToolRegistry>,
    permission_prompt: Arc<dyn PermissionPrompt>,
    /// The live permission mode, shared with the prompt above so a change here
    /// is observed by the next tool decision without restarting the engine.
    permission_mode: Arc<PermissionModeState>,
    user_question: Arc<WireUserQuestion>,
    plan_approver: Arc<WirePlanApprover>,
    /// Retained so `session/getPendingRequests`, `session/resolveRequest` and
    /// `session/cancelRequest` reach the **same** pending registry the raw
    /// `request/*` responses do — one registry, one exactly-once resolution.
    prompt_channel: Arc<PromptChannel>,
    todos: Arc<TodoStore>,
    working_dir: String,
    pending_startup_effort: Mutex<Option<StartupEffort>>,
    /// Session-only, per-model reasoning-effort overrides.
    ///
    /// Keyed by model id. Presence of a key means "the user made an explicit
    /// choice for this model this session"; a value of `None` means they chose
    /// automatic. This is deliberately distinct from *absence* of a key, which
    /// means "no session override — fall back to the saved preference". The
    /// stored `Effort` is the level the user *requested*, re-resolved against
    /// each model's capability so switching models never carries a stale level.
    effort_overrides: Mutex<HashMap<String, Option<Effort>>>,
    /// Serialises configuration mutations so a commit that awaits a provider
    /// capability lookup cannot interleave with another one.
    ///
    /// This is the **writer** lock and the outermost of them all: it is held
    /// across `.await` points (provider validation), it is never taken by a
    /// reader, and it is never acquired while a synchronous lock is held. The
    /// synchronous commit itself is `CONFIG -> STATE -> BUS`, held through
    /// publication, so no reader can observe a runtime the public state does
    /// not yet describe.
    config_commit: tokio::sync::Mutex<()>,
    goal_params: Mutex<GoalParams>,
    /// The Anthropic **API-key** base URL in force for this engine, resolved
    /// once at startup from `serve --endpoint`, then `ANTHROPIC_BASE_URL`,
    /// Where this host may send an Anthropic **API-key** request — or the
    /// refusal that must stop it from sending one at all.
    ///
    /// `Ok(None)` is "nothing configured, the default host stands".
    /// `Err(..)` is a configured endpoint the resolver refused, kept for the
    /// life of the host: an engine that started as Copilot with a refused
    /// `ANTHROPIC_BASE_URL` must still refuse a later `initialize(apiKey)`
    /// rather than route that key to the default host.
    ///
    /// Retained so a later `initialize(apiKey)` rebuilds the client at this
    /// URL rather than silently reverting to the default host (Finding I1) —
    /// and so a gateway configured for this process is honoured by the
    /// stored-key and exported-key paths too, not only by an explicit
    /// `--api-key`.
    ///
    /// Anthropic API keys only: the Copilot and Claude.ai clients resolve
    /// their own endpoints, never see this value, and are never blocked by a
    /// refusal recorded in it.
    configured_endpoint: Result<Option<String>, StartupError>,
    current_cancel: Mutex<Option<CancellationToken>>,
    /// `true` while a `session/prompt` or `session/compact` is running.
    turn_active: Mutex<bool>,
    /// The active session id: starts as the initial session id, changes on fork.
    /// Transcripts are always written to this id.
    current_session_id: Mutex<String>,
    // ── Session-scoped services ──────────────────────────────────────────────
    task_manager: Arc<TaskManager>,
    schedule_store: Arc<ScheduledTaskStore>,
    lsp_manager: Arc<LspServerManager>,
    /// Trust decisions for project-scoped hooks; persists within the session.
    hook_trust_store: Arc<InMemoryHookTrustStore>,
    /// Hooks loaded once at session start with scopes stamped by the loader.
    user_hooks: Vec<UserHook>,
    /// SubagentHost, HookRunner, ScheduleRuntime — built lazily on first prompt.
    session_services: tokio::sync::Mutex<Option<Arc<SessionServices>>>,
    /// Live MCP manager for connected servers; `None` when MCP is disabled or
    /// no servers are configured. Retained so the servers can be shut down.
    mcp_manager: Option<Arc<McpClientManager>>,
    /// MCP connection/config failures to surface to the user on `initialize`,
    /// once a client is listening. Drained when emitted.
    pending_mcp_notices: Mutex<Vec<String>>,
    /// Set by `session/interrupt` when no cancel token is yet published.
    /// Cleared and applied immediately when the next turn publishes its token.
    pending_interrupt: Mutex<bool>,
    /// Diagnostic from failed provider selection or credential setup at startup.
    ///
    /// Scoped to this host instance so a failed attempt on one host cannot
    /// contaminate another (unlike a process-global OnceLock). Set during
    /// construction from the shared provider resolver. The diagnostic is not
    /// shown once a real client is available. `None` means no startup fault.
    startup_provider_diagnostic: Option<String>,
    /// The provider that was configured/requested at startup, used to give
    /// more targeted "no credentials" messages.
    ///
    /// Set from `startup_opts.provider` (explicit `--provider`) or from the
    /// `defaultProvider` field in `settings.json`. Does not imply authentication
    /// succeeded — only that this provider was expected.
    startup_configured_provider: Option<String>,
    /// The explicit `--provider` request, kept verbatim.
    ///
    /// Distinct from [`Self::startup_configured_provider`], which also carries
    /// a saved default: only this one is a *choice made by this invocation*,
    /// and the lazy wiring must preserve it rather than re-deciding without it.
    startup_requested_provider: Option<String>,
    /// The profile this host authenticates against, for the wiring that
    /// happens after startup.
    ///
    /// `None` in every fixture that was never given one — and deliberately so:
    /// a host with no context does not go looking at the developer's
    /// credentials, settings or environment when a prompt arrives.
    provider_context: std::sync::OnceLock<Arc<ProviderContext>>,
    /// The process-level diagnostic root (role/run_id/logger only — no
    /// session or turn identity, which is layered on explicitly per
    /// request/turn via [`coda_diagnostics::scope`] and never stored here as
    /// mutable state). `None` in every test/library context that never
    /// established one; diagnostics are then simply skipped.
    diagnostics: Option<coda_diagnostics::DiagnosticContext>,
    /// The single authority for everything in `StateSnapshot` (Slice 0 /
    /// Stage C, §3). Shares the `EventBus` inside `sink` so cursors always
    /// agree. Runtime handles (`client`, `config_commit`, `session_services`)
    /// stay above, as inputs `EngineState` mirrors only derived scalars from.
    engine_state: Arc<EngineState>,
    /// Bridges `session.steering`'s synchronous observer hook to
    /// `engine_state`; also tracks which turn is currently delivering so
    /// `event/steeringOutcome` can tag `delivered` with the owning `turnId`.
    steering_observer: Arc<SteeringStateObserver>,
    /// Test seam: invoked inside `run_prompt_inner` after the agent has been
    /// built and before the turn's resolved `activeConfig` is published.
    ///
    /// That is the exact window in which a configuration setter could land
    /// between "what the agent was built with" and "what the turn tells the
    /// world it is running", so it is the window a test has to be able to
    /// enter deterministically — a scheduling race would prove nothing.
    #[cfg(test)]
    after_agent_build_hook: Mutex<Option<Arc<dyn Fn(&ServeHost) + Send + Sync>>>,
}

impl ServeHost {
    /// Production constructor — client starts as `None` until `initialize`.
    ///
    /// MCP is intentionally **not** connected here: MCP startup is async and
    /// must be completed before the host is built. In `serve_stdio`, call
    /// `connect_mcp` first and pass the resulting [`McpBundle`] to
    /// [`ServeHost::new_with_optional_client_and_mcp`] instead. This
    /// constructor is only used in tests and in contexts where no MCP is
    /// needed.
    pub fn new(
        sink: Arc<ServeSink>,
        prompt_channel: Arc<PromptChannel>,
        working_dir: String,
    ) -> Arc<Self> {
        // No pre-built client: model resolved from settings.defaultProvider fallback.
        // No startup overrides: this constructor is hermetic (Finding I4).
        Self::build(
            None,
            sink,
            prompt_channel,
            working_dir,
            None,
            McpBundle::disabled(),
            StartupOptions::default(),
            None,
            None,
        )
    }

    /// Test constructor — pre-built client with automatic initial effort,
    /// independent of the developer's saved effort preference.
    pub fn new_with_client(
        client: Arc<dyn LlmClient>,
        sink: Arc<ServeSink>,
        prompt_channel: Arc<PromptChannel>,
        working_dir: String,
    ) -> Arc<Self> {
        // Finding 3: resolve model from the credential that is actually connected.
        let provider_id = client.provider_id().to_owned();
        Self::build(
            Some(client),
            sink,
            prompt_channel,
            working_dir,
            Some(&provider_id),
            McpBundle::disabled(),
            StartupOptions { effort: StartupEffort::Auto, ..Default::default() },
            None,
            None,
        )
    }

    /// Construct with an optional client, a pre-connected MCP bundle, and an
    /// optional Copilot diagnostic from the credential probe.
    ///
    /// This is the production entry used by the stdio transport: the MCP
    /// servers are connected before the host is built (connecting is async;
    /// the registry is immutable once assembled), then handed in here, along
    /// with the validated [`StartupOptions`] parsed once by the transport.
    ///
    /// `copilot_diagnostic` carries any error message from the Copilot
    /// credential probe (see [`build_copilot_from_store`]).  It is scoped to
    /// this host instance; a failed probe on one host never contaminates
    /// another.
    ///
    /// `diagnostics` is the process-level root diagnostic context, captured
    /// by the transport from the ambient [`coda_diagnostics::current`] on the
    /// same task the process entrypoint established it on (never read here
    /// via a global — that context is not visible across the `tokio::spawn`
    /// boundary between the transport and per-request dispatch tasks).
    pub(crate) fn new_with_optional_client_and_mcp(
        client: Option<Arc<dyn LlmClient>>,
        sink: Arc<ServeSink>,
        prompt_channel: Arc<PromptChannel>,
        working_dir: String,
        mcp: McpBundle,
        startup: StartupOptions,
        copilot_diagnostic: Option<String>,
        diagnostics: Option<coda_diagnostics::DiagnosticContext>,
    ) -> Arc<Self> {
        let provider_id = client.as_ref().map(|c| c.provider_id().to_owned());
        Self::build(
            client,
            sink,
            prompt_channel,
            working_dir,
            provider_id.as_deref(),
            mcp,
            startup,
            copilot_diagnostic,
            diagnostics,
        )
    }

    /// Test constructor — injects a client together with an MCP bundle so
    /// integration tests can exercise the full MCP tool path hermetically.
    #[cfg(test)]
    pub(crate) fn new_with_client_and_mcp(
        client: Arc<dyn LlmClient>,
        sink: Arc<ServeSink>,
        prompt_channel: Arc<PromptChannel>,
        working_dir: String,
        mcp: McpBundle,
    ) -> Arc<Self> {
        let provider_id = client.provider_id().to_owned();
        Self::build(
            Some(client),
            sink,
            prompt_channel,
            working_dir,
            Some(&provider_id),
            mcp,
            StartupOptions::default(),
            None,
            None,
        )
    }

    /// Test constructor — injects a client together with explicit startup
    /// options, without reading the process environment (Finding I4).
    #[cfg(test)]
    pub(crate) fn new_with_client_and_options(
        client: Arc<dyn LlmClient>,
        sink: Arc<ServeSink>,
        prompt_channel: Arc<PromptChannel>,
        working_dir: String,
        startup: StartupOptions,
    ) -> Arc<Self> {
        let provider_id = client.provider_id().to_owned();
        Self::build(
            Some(client),
            sink,
            prompt_channel,
            working_dir,
            Some(&provider_id),
            McpBundle::disabled(),
            startup,
            None,
            None,
        )
    }

    fn build(
        client: Option<Arc<dyn LlmClient>>,
        sink: Arc<ServeSink>,
        prompt_channel: Arc<PromptChannel>,
        working_dir: String,
        connected_provider: Option<&str>,
        mcp: McpBundle,
        startup_opts: StartupOptions,
        copilot_diagnostic: Option<String>,
        diagnostics: Option<coda_diagnostics::DiagnosticContext>,
    ) -> Arc<Self> {
        let wire_perm = Arc::new(WirePermissionPrompt { channel: Arc::clone(&prompt_channel) });
        // Startup permission mode override comes from the validated options
        // (e.g. `coda serve --yolo`). Defaults to `Default` when not set.
        let initial_permission_mode = startup_opts
            .permission_mode
            .unwrap_or(PermissionMode::Default);
        // The mode state is held by the host rather than buried inside the
        // prompt, so `/yolo` and `/permissions` can switch it on a live
        // session. Built with `new()` the prompt owns a state nothing can
        // reach, which is why changing the mode used to require restarting
        // the engine.
        let permission_mode = Arc::new(PermissionModeState::new(initial_permission_mode));
        let permission_prompt: Arc<dyn PermissionPrompt> = Arc::new(
            ModePermissionPrompt::new_with_state(Arc::clone(&permission_mode), Some(wire_perm)),
        );
        let user_question = Arc::new(WireUserQuestion { channel: Arc::clone(&prompt_channel) });
        let plan_approver = Arc::new(WirePlanApprover { channel: Arc::clone(&prompt_channel) });

        // Built-ins first, then MCP tools + management tools. The registry is
        // last-write-wins on name collision; MCP names are `mcp__…`-prefixed so
        // they cannot shadow a built-in. This same shared registry is handed to
        // the subagent host, so subagents see the MCP tools too.
        let McpBundle { tools: mcp_tools, manager: mcp_manager, notices: mcp_notices } = mcp;
        let tools = Arc::new(ToolRegistry::new(
            built_in_tools().into_iter().chain(mcp_tools),
        ));

        // Resolve startup model from the connected provider (Finding 3).
        let startup = crate::settings::resolve_for_provider(connected_provider);

        let session_id = Uuid::new_v4().to_string();
        let task_manager = TaskManager::with_defaults(&session_id);
        let schedule_store = ScheduledTaskStore::new();

        let settings_json = load_settings_value();
        let lsp_configs = load_lsp_configs(&settings_json, &working_dir);
        let lsp_manager = Arc::new(LspServerManager::new(lsp_configs, Some(working_dir.clone())));
        let user_hooks = load_user_hooks(&working_dir);
        let hook_trust_store = Arc::new(InMemoryHookTrustStore::new());

        // Load the per-model effort saved by the TUI. The key mirrors the TUI's
        // `Settings::effort_for` format so the two sides agree on where to read
        // and write. A startup `--effort <level>` override (from the validated
        // options) takes precedence when present; `--effort auto` explicitly
        // clears effort without reading the saved preference.
        // Model: use the startup override when provided, else provider-based resolution.
        let startup_model = startup_opts.model.clone().unwrap_or(startup.model);
        let initial_effort = match startup_opts.effort {
            StartupEffort::Level(e) => Some(e),
            StartupEffort::Auto => None,
            StartupEffort::Unset => {
                effort_from_settings(&settings_json, &startup.provider_id, &startup_model)
            }
        };
        let initial_system_prompt = startup_opts.system_prompt.clone();
        let startup_goals = startup_opts.goal_params();
        let configured_endpoint = startup_opts.api_key_endpoint();

        // Derive the startup_configured_provider: explicit --provider flag first,
        // then the defaultProvider from settings, then None (no expectation set).
        let startup_requested_provider = startup_opts.provider.clone();
        let startup_configured_provider: Option<String> = startup_opts.provider.clone().or_else(|| {
            settings_json
                .get("defaultProvider")
                .and_then(Value::as_str)
                .filter(|s| !s.trim().is_empty())
                .map(str::to_owned)
        });

        let bus = sink.bus();
        // The public read model is seeded with the *real* startup
        // configuration before anything can read it: model, effort, permission
        // mode, prompt source and the provider actually wired (genuinely
        // absent when nothing is, never a plausible fallback name). A state
        // seeded with a placeholder would answer the first `session/getState`
        // with a configuration the engine was never in.
        let startup_runtime = RuntimeConfig {
            model: startup_model,
            // `serve --model X` is a choice, not a default: provider wiring
            // must not overwrite it.
            model_is_explicit: startup_opts.model.is_some(),
            effort: initial_effort,
            system_prompt: initial_system_prompt.map(Arc::from),
            provider_id: client.as_ref().map(|c| c.provider_id().to_owned()),
        };
        let engine_state = Arc::new(EngineState::new(
            Arc::clone(&bus),
            session_id.clone(),
            working_dir.clone(),
            capability_catalog(),
            startup_runtime.active_config(initial_permission_mode),
        ));
        let steering_observer = SteeringStateObserver::new(Arc::clone(&engine_state));
        // The registry publishes pending/resolved transitions through
        // `EngineState`, which owns the transaction (and therefore the
        // cursor/bus ordering). `EngineState` never calls back into the
        // registry — it mirrors the projected list — so `REQUESTS -> STATE ->
        // BUS` cannot invert.
        prompt_channel
            .registry()
            .set_observer(Arc::clone(&engine_state) as Arc<dyn crate::state::requests::RequestObserver>);

        Arc::new(Self {
            session: Session::with_steering_observer(
                session_id.clone(),
                Some(Arc::clone(&steering_observer) as Arc<dyn coda_agent::steering::SteeringObserver>),
            ),
            sink,
            client: tokio::sync::Mutex::new(client),
            runtime: Arc::new(Mutex::new(startup_runtime)),
            tools,
            permission_prompt,
            permission_mode,
            user_question,
            plan_approver,
            prompt_channel,
            todos: Arc::new(TodoStore::new()),
            working_dir,
            pending_startup_effort: Mutex::new(match startup_opts.effort {
                StartupEffort::Unset => None,
                override_ => Some(override_),
            }),
            effort_overrides: Mutex::new(HashMap::new()),
            config_commit: tokio::sync::Mutex::new(()),
            goal_params: Mutex::new(startup_goals),
            configured_endpoint,
            current_cancel: Mutex::new(None),
            turn_active: Mutex::new(false),
            current_session_id: Mutex::new(session_id),
            task_manager,
            schedule_store,
            lsp_manager,
            hook_trust_store,
            user_hooks,
            session_services: tokio::sync::Mutex::new(None),
            mcp_manager,
            pending_mcp_notices: Mutex::new(mcp_notices),
            pending_interrupt: Mutex::new(false),
            startup_provider_diagnostic: copilot_diagnostic,
            startup_configured_provider,
            startup_requested_provider,
            provider_context: std::sync::OnceLock::new(),
            diagnostics,
            engine_state,
            steering_observer,
            #[cfg(test)]
            after_agent_build_hook: Mutex::new(None),
        })
    }

    /// Give this host the profile it authenticates against.
    ///
    /// Called once, by the transport, before serving. A host that never
    /// receives one performs no credential lookup at all: that is what keeps
    /// the client-less test fixtures off the developer's real profile.
    pub(crate) fn set_provider_context(&self, context: Arc<ProviderContext>) {
        let _ = self.provider_context.set(context);
    }

    /// Return the active session id (may change after `session/fork`).
    fn active_session_id(&self) -> String {
        self.current_session_id.lock().expect("session_id poisoned").clone()
    }

    fn current_model(&self) -> String {
        self.runtime.lock().expect("runtime config poisoned").model.clone()
    }

    fn current_effort(&self) -> Option<Effort> {
        self.runtime.lock().expect("runtime config poisoned").effort
    }

    /// One coherent read of everything a turn is built from.
    ///
    /// The permission mode is read *inside* CONFIG — with the guard actually
    /// bound, not a temporary that has already been dropped: every host-side
    /// write to the mode is a config commit, so pairing them under the lock
    /// is what makes the captured record a real state of the engine rather
    /// than a mixture of two.
    fn capture_runtime(&self) -> RuntimeCapture {
        let guard = self.runtime.lock().expect("runtime config poisoned");
        let capture = RuntimeCapture {
            config: guard.clone(),
            permission_mode: self.permission_mode.get(),
        };
        drop(guard);
        capture
    }

    /// Commits a configuration mutation and publishes the resulting
    /// `config.next` **in the same critical section** (`CONFIG -> STATE ->
    /// BUS`), if the engine is still alive.
    ///
    /// Releasing CONFIG before publishing is not enough: a turn claimed in
    /// that window would capture the new runtime and publish an `activeConfig`
    /// the public `config.next` did not yet mention. Holding it through
    /// publication makes the two indivisible — a reader sees either the whole
    /// old configuration or the whole new one.
    ///
    /// `mutate` runs *inside* the STATE transaction, which is also where the
    /// stopping/stopped check happens, so a commit that lost the race with
    /// `shutdown` changes no runtime field, no `config.next` and publishes
    /// nothing; the caller is told with an error rather than a false success.
    /// A pre-check here could not achieve that: writers park on provider
    /// lookups and can be overtaken while they wait.
    ///
    /// Callers that must validate against a provider first (an `.await`) hold
    /// [`Self::config_commit`] across that validation; this function must
    /// never be given anything to await, which is why it takes a synchronous
    /// closure — and that closure must not take any further lock.
    fn commit_config<R>(
        &self,
        key: &'static str,
        mutate: impl FnOnce(&mut RuntimeConfig) -> R,
    ) -> Result<R, RpcError> {
        let mut guard = self.runtime.lock().expect("runtime config poisoned");
        let mut applied: Option<R> = None;
        // STATE -> BUS while CONFIG is still held. `EngineState` never calls
        // back into the host, so this cannot invert into CONFIG again; the
        // callback only writes the record already locked above and reads the
        // permission-mode atomic.
        let admitted = self.engine_state.commit_next_config(key, || {
            applied = Some(mutate(&mut guard));
            guard.active_config(self.permission_mode.get())
        });
        drop(guard);
        match applied {
            Some(out) if admitted => Ok(out),
            _ => Err(RpcError::internal(
                "engine is shutting down; the configuration was not changed",
            )),
        }
    }

    /// Builds the "no credentials" [`RpcError`] for `session/prompt` and
    /// `session/compact`.
    ///
    /// Distinguishes three cases:
    /// 1. **Provider setup error** — startup selection, configuration or
    ///    credential loading failed; the scoped diagnostic describes it.
    /// 2. **Known provider, no credential** — a provider was configured but
    ///    had no stored credential.
    /// 3. **Generic** — no provider was configured and no credential was found.
    ///
    /// Authentication maintenance runs on the engine host, which may be a
    /// different machine from the client presenting this message.
    fn no_client_error(&self) -> RpcError {
        if let Some(diag) = &self.startup_provider_diagnostic {
            return RpcError::unauthorized(format!(
                "Provider configuration or credential error: {diag}. \
                 Run `coda auth status` on the engine host, correct its provider \
                 configuration or credentials, then restart the engine."
            ));
        }
        match &self.startup_configured_provider {
            Some(p) if p.contains("copilot") || p.contains("github") => {
                RpcError::unauthorized(
                    "No GitHub Copilot credential found. \
                     Check your saved credentials and provider configuration, then restart Coda.",
                )
            }
            Some(p) => {
                RpcError::unauthorized(format!(
                    "No credential found for provider '{p}'. \
                     Check provider configuration and restart Coda."
                ))
            }
            None => RpcError::unauthorized(
                "No credentials configured. \
                 Set ANTHROPIC_API_KEY, pass apiKey in initialize, or configure a provider.",
            ),
        }
    }

    async fn apply_pending_startup_effort(&self) -> Result<(), RpcError> {
        let pending = *self.pending_startup_effort.lock().expect("startup effort poisoned");
        let Some(pending) = pending else { return Ok(()) };
        let level = match pending {
            StartupEffort::Level(level) => Some(level.as_str().to_owned()),
            StartupEffort::Auto => None,
            StartupEffort::Unset => return Ok(()),
        };
        let result = self.session_set_effort(SetEffortParams {
            effort: level, ..Default::default()
        }).await?;
        if result.get("ok").and_then(Value::as_bool) != Some(true) {
            return Err(RpcError::invalid_params(format!(
                "startup effort was not applied: {}",
                result.get("note").and_then(Value::as_str).unwrap_or("unsupported level"),
            )));
        }
        *self.pending_startup_effort.lock().expect("startup effort poisoned") = None;
        Ok(())
    }

    /// Test-only: set the current model without going through the RPC path.
    ///
    /// Goes through the same commit as production, so a test can never leave
    /// the runtime and the public state disagreeing about the model.
    #[cfg(test)]
    fn set_model_for_test(&self, model: &str) {
        self.commit_config("model", |rc| {
            rc.model = model.to_owned();
            // Stands in for `session/setModel`, so it is a choice too.
            rc.model_is_explicit = true;
        })
        .expect("the engine is live in this test");
    }

    /// Test-only: set (or clear) the session system-prompt override without
    /// going through the RPC path.
    #[cfg(test)]
    fn set_system_prompt_for_test(&self, text: Option<&str>) {
        self.commit_config("systemPrompt", |rc| {
            rc.system_prompt = text.map(Arc::from);
        })
        .expect("the engine is live in this test");
    }

    /// Resolves the reasoning capability of a model **against a specific
    /// client**, together with whether the answer is *indeterminate* rather
    /// than a positive statement.
    ///
    /// Deliberately an associated function taking the client explicitly: a
    /// caller wiring a new provider must not accidentally ask the client the
    /// host is still holding, which is either the previous provider or none
    /// at all and answers a different question.
    ///
    /// Copilot models advertise their levels at runtime, so the truth comes
    /// from the model listing. Before that listing is available (no client, or
    /// the request failed) the answer is *unknown*, not "unsupported" —
    /// conflating the two silently drops the user's configured effort. For a
    /// Copilot model the second element is `true` in exactly that case.
    /// Anthropic models resolve from static id rules and are never
    /// indeterminate.
    async fn resolve_capability_for(
        client: Option<&Arc<dyn LlmClient>>,
        model: &str,
    ) -> (ReasoningCapability, bool) {
        let (provider, advertised) = match client {
            Some(client) => {
                let provider = client.provider_id().to_owned();
                let advertised = client.list_models().await.ok().and_then(|models| {
                    models
                        .into_iter()
                        .find(|m| m.id.eq_ignore_ascii_case(model))
                        .map(|m| m.reasoning_levels)
                });
                (provider, advertised)
            }
            None => (crate::settings::FALLBACK_PROVIDER.to_owned(), None),
        };

        let indeterminate =
            provider.eq_ignore_ascii_case(COPILOT_PROVIDER_ID) && advertised.is_none();
        let capability = resolve_reasoning(&provider, model, advertised.as_deref());
        (capability, indeterminate)
    }

    /// [`Self::resolve_capability_for`] against the client the host currently
    /// holds.
    async fn resolve_capability(&self, model: &str) -> (ReasoningCapability, bool) {
        let client = self.client.lock().await.clone();
        Self::resolve_capability_for(client.as_ref(), model).await
    }

    /// The level *intended* for `(provider, model)`: an explicit session
    /// override if one exists, otherwise the saved preference. This is the
    /// intent, before it is validated against what the model can honour.
    fn requested_effort_for(&self, provider: &str, model: &str) -> Option<Effort> {
        if let Some(explicit) = self.effort_overrides.lock().expect("effort poisoned").get(model) {
            return *explicit;
        }
        effort_from_settings(&load_settings_value(), provider, model)
    }

    /// The startup `--effort` intent while it is still pending.
    ///
    /// `Some(Some(level))` / `Some(None)` (explicit automatic) means the
    /// operator asked for a level that has not been applied yet, and it
    /// outranks a saved preference; `None` means nothing is pending.
    fn pending_startup_effort_intent(&self) -> Option<Option<Effort>> {
        match *self.pending_startup_effort.lock().expect("startup effort poisoned") {
            Some(StartupEffort::Level(level)) => Some(Some(level)),
            Some(StartupEffort::Auto) => Some(None),
            Some(StartupEffort::Unset) | None => None,
        }
    }

    /// Resolves the effective effort for `model` from the session override or
    /// the saved preference, validated against the model's capability.
    ///
    /// **Pure**: it awaits the provider but mutates nothing, so a caller can
    /// resolve the level for a model it has not committed yet — and a caller
    /// that is cancelled while resolving leaves no half-applied switch behind.
    /// The commit is a separate, synchronous step.
    ///
    /// A session override for the model wins over the saved preference, an
    /// explicit "automatic" override clears the level, and a level the model
    /// cannot honour is clamped or dropped rather than sent verbatim.
    async fn resolve_effort_for_model(&self, model: &str) -> Option<Effort> {
        // One look at the client: the provider the intent is keyed by and the
        // provider the capability is resolved against are then the same one.
        let client = self.client.lock().await.clone();
        let provider = client
            .as_ref()
            .map(|c| c.provider_id().to_owned())
            .unwrap_or_else(|| crate::settings::FALLBACK_PROVIDER.to_owned());
        let requested = self.requested_effort_for(&provider, model);
        let (capability, indeterminate) = Self::resolve_capability_for(client.as_ref(), model).await;
        resolve_effective_effort(&capability, indeterminate, requested)
    }

    /// Wires a client on first use, if nothing is wired yet, and commits the
    /// configuration that implies.
    ///
    /// This runs the **same selection** startup ran, through the same
    /// [`ProviderContext`], with the same explicit `--provider` request
    /// preserved. That matters: startup can legitimately end with no client
    /// because the selection *refused* — a saved or requested provider whose
    /// credential is missing or unreadable. Reaching for `ANTHROPIC_API_KEY`
    /// here would quietly connect the user to a different account than the one
    /// they chose, one prompt later, and the refusal startup printed would be
    /// silently reversed. So a refusal stays a refusal: nothing is wired, and
    /// the caller's "no credentials" path runs.
    ///
    /// An *unconfigured* profile with an exported key still resolves to that
    /// key — that is the selector's `AmbientEnvKey` case, not a fallback.
    ///
    /// A host with no context (every client-less fixture) wires nothing and
    /// reads no credentials at all.
    ///
    /// Discovering a provider changes both the provider in force and the
    /// model resolved for it, so it is a configuration commit like any other:
    /// the writer lock is taken *before* the client mutex — the same order
    /// every setter uses (`session_set_model` holds it across
    /// `resolve_capability`'s client lock), so the two can never deadlock
    /// against each other — and the change is announced. Silent wiring used
    /// to leave `config.next` describing a provider the engine had already
    /// left, with no event to correct it.
    async fn wire_client_from_env_if_missing(&self) -> Result<(), RpcError> {
        // The writer lock is taken *before* the client mutex — the same order
        // every setter uses (`session_set_model` holds it across
        // `resolve_capability`'s client lock), so the two can never deadlock
        // against each other.
        let _commit = self.config_commit.lock().await;
        let mut guard = self.client.lock().await;
        if guard.is_some() {
            return Ok(());
        }
        let Some(context) = self.provider_context.get() else { return Ok(()) };

        let selection = match context.select(self.startup_requested_provider.as_deref()).await {
            Ok(selection) => selection,
            // NoCredentials is the ordinary client-less start; every other
            // refusal (NeedsLogin, Ambiguous, Unavailable, UnknownProvider) is
            // a decision that must not be overridden by looking elsewhere.
            Err(_) => return Ok(()),
        };
        // The endpoint is an API-key concern. A refusal captured at startup
        // bars *that* identity and no other: Copilot and Claude.ai resolve
        // their own endpoints and wire normally. It is checked here rather
        // than left to `build_client` because an injected context may read a
        // different environment from the one this host resolved, and the
        // refusal this host captured is the one that has to hold.
        let endpoint = match self.configured_endpoint.as_ref() {
            Ok(endpoint) => endpoint.as_deref(),
            Err(_) if selection.identity != ProviderIdentity::AnthropicApiKey => None,
            Err(refusal) => {
                eprintln!("coda: {refusal}");
                return Ok(());
            }
        };
        let new_client = match context
            .build_client(selection, None, endpoint)
            .await
        {
            Ok(client) => client,
            Err(_) => return Ok(()),
        };
        // Admission first: a wiring that lost the race with `shutdown` must
        // leave the client unwired rather than half-adopt a provider the
        // public state was refused permission to describe.
        //
        // The client mutex is deliberately still held across this: the commit
        // resolves the new provider's capability, and releasing here would
        // expose a window in which the published record names a provider
        // whose client is not installed yet. Readers wait for one provider
        // round-trip instead of being handed a mismatched pair — and this
        // path only runs once, before any client exists. It is an async
        // mutex, so nothing is blocked, and no synchronous lock is held
        // across the await.
        self.commit_wired_provider(&new_client).await?;
        *guard = Some(new_client);
        Ok(())
    }

    /// Commits the configuration implied by having just wired `client`.
    ///
    /// Shared by `initialize(apiKey)` and the lazy env wiring that
    /// `session/prompt` and `session/compact` perform, so all three announce
    /// the provider the same way and apply the same model and effort rules.
    ///
    /// Finding 3 (why the model moves at all): the startup model is resolved
    /// for whichever provider was *expected*, so adopting the connected
    /// provider's model stops a default resolved for one provider being sent
    /// to another that has never heard of it. That reasoning only covers a
    /// **default**; a model the operator selected (`--model`,
    /// `session/setModel`, `config/set model`) is kept, because populating
    /// provider metadata is not a reason to run a different model than the
    /// one that was asked for.
    ///
    /// The effort moves with the model. Re-picking a model while leaving the
    /// level in force would run a level resolved for a different
    /// provider/model pair — possibly one this provider does not support at
    /// all — so it is re-resolved here, against the client **being wired**
    /// (never the one the host still holds), and committed with the model and
    /// the provider as one change. A pending `--effort` still outranks a
    /// saved preference; it is applied verbatim afterwards by
    /// `apply_pending_startup_effort`, and honouring it here keeps the two
    /// from disagreeing in between.
    ///
    /// Callers hold [`Self::config_commit`] across this call, so the record
    /// read below cannot change before the commit.
    async fn commit_wired_provider(&self, client: &Arc<dyn LlmClient>) -> Result<(), RpcError> {
        let provider_id = client.provider_id().to_owned();
        let (current_model, model_is_explicit) = {
            let rc = self.runtime.lock().expect("runtime config poisoned");
            (rc.model.clone(), rc.model_is_explicit)
        };
        let model = if model_is_explicit {
            current_model
        } else {
            // Reads the settings file, so it must happen before the commit.
            crate::settings::model_for_provider(&provider_id)
        };
        let requested = match self.pending_startup_effort_intent() {
            Some(pending) => pending,
            None => self.requested_effort_for(&provider_id, &model),
        };
        let (capability, indeterminate) =
            Self::resolve_capability_for(Some(client), &model).await;
        let effort = resolve_effective_effort(&capability, indeterminate, requested);

        self.commit_config("model", move |rc| {
            rc.model = model;
            rc.effort = effort;
            rc.provider_id = Some(provider_id);
        })
    }

    /// The provider id of the connected client, or the fallback before wiring.
    async fn connected_provider(&self) -> String {
        match self.client.lock().await.clone() {
            Some(c) => c.provider_id().to_owned(),
            None => crate::settings::FALLBACK_PROVIDER.to_owned(),
        }
    }

    /// The provider id of the wired client, or `None` when nothing is wired.
    ///
    /// The synchronous counterpart of [`Self::connected_provider`], reading
    /// the mirror in the runtime record rather than the async client mutex,
    /// and reporting *unknown* as `None` instead of substituting a fallback
    /// name. Everything that must not invent a provider (`config.next`, a
    /// turn's `activeConfig`) reads the record itself; this stays as the
    /// narrow accessor for assertions about the mirror.
    #[cfg(test)]
    fn wired_provider_id(&self) -> Option<String> {
        self.runtime.lock().expect("runtime config poisoned").provider_id.clone()
    }

    /// The effort intent for `model`, as an [`Effort`], shared by the model-list
    /// annotation and the arrow-stepping RPC.
    ///
    /// Answered against a **captured** record rather than live reads: the
    /// active model reports the effective level that belongs to *that* record
    /// (never a level committed after the answer began); any other row
    /// reports the intent that would apply — the session override if one
    /// exists, otherwise the saved preference. `None` means automatic / none.
    fn target_effort(
        &self,
        captured: &RuntimeConfig,
        provider: &str,
        model: &str,
    ) -> Option<Effort> {
        if model.eq_ignore_ascii_case(&captured.model) {
            return captured.effort;
        }
        self.requested_effort_for(provider, model)
    }

    /// The effort level to surface for `model` on a model-list row.
    ///
    /// The active model shows the effective level of the record this answer
    /// was built from, so a browser never displays a level belonging to a
    /// different model; other rows show the intent that would apply — the
    /// session override if one exists, otherwise the saved preference. `None`
    /// means automatic / none.
    fn effort_hint_for(
        &self,
        captured: &RuntimeConfig,
        provider: &str,
        model: &str,
    ) -> Option<String> {
        self.target_effort(captured, provider, model).map(|e| e.as_str().to_owned())
    }

    /// The canonical id of `model` if the engine knows it — the active model,
    /// the live list, or the catalogue.
    ///
    /// Returning `None` for an unknown id is what stops a typo from creating a
    /// per-model override the user can neither see nor clear; the caller turns
    /// that into an `invalid_params` rejection.
    async fn resolve_known_model(&self, model: &str) -> Option<String> {
        let active = self.current_model();
        if model.eq_ignore_ascii_case(&active) {
            return Some(active);
        }
        if let Some(client) = self.client.lock().await.clone() {
            if let Ok(models) = client.list_models().await {
                if let Some(m) = models.iter().find(|m| m.id.eq_ignore_ascii_case(model)) {
                    return Some(m.id.clone());
                }
            }
        }
        catalog_models()
            .into_iter()
            .find(|m| m.id.eq_ignore_ascii_case(model))
            .map(|m| m.id)
    }

    /// Serialises a [`ModelEffortResult`] into a wire value.
    fn effort_result(
        ok: bool,
        model: &str,
        provider: &str,
        current: Option<String>,
        active: bool,
        note: String,
    ) -> Result<Value, RpcError> {
        let resp = ModelEffortResult {
            ok,
            model: model.to_owned(),
            provider_id: provider.to_owned(),
            current,
            active,
            note,
        };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    /// Stamps each row of a model list with the effort in force for it.
    fn annotate_effort(&self, models: &mut [WireModel], captured: &RuntimeConfig, provider: &str) {
        for m in models.iter_mut() {
            m.effort = self.effort_hint_for(captured, provider, &m.id);
        }
    }

    fn build_goal_supervisor(&self, client: Arc<dyn LlmClient>) -> Option<GoalSupervisor> {
        let params = self.goal_params.lock().expect("goal poisoned").clone();
        let goal_text = params.goal.filter(|g| !g.trim().is_empty())?;
        let max_cont = params.max_continuations.unwrap_or(5).max(0) as u32;
        let max_dur = parse_duration(params.max_duration.as_deref())
            .unwrap_or(Duration::from_secs(30 * 60));
        let judge = Box::new(LlmForkedAgent { client, model: self.current_model() });
        Some(GoalSupervisor::new(judge, goal_text, GoalBudget::start_now(max_dur, max_cont, 0.5), None))
    }

    /// Get or initialise the per-session services (SubagentHost, HookRunner,
    /// ScheduleRuntime). Called on the first prompt once the client is known.
    /// Subsequent calls return the cached `Arc<SessionServices>`.
    async fn get_or_init_services(
        &self,
        client: Arc<dyn LlmClient>,
    ) -> Arc<SessionServices> {
        let mut guard = self.session_services.lock().await;
        if let Some(ref svc) = *guard {
            return Arc::clone(svc);
        }
        let svc = Arc::new(self.build_session_services(client));
        *guard = Some(Arc::clone(&svc));
        svc
    }

    /// Construct all session-scoped services that require a live client.
    ///
    /// Called at most once per session (the first time a prompt succeeds).
    ///
    /// # Security invariants
    /// 1. `hook_free_subagent` is built *without* a HookRunner so agent-type
    ///    hooks cannot re-trigger further hooks (unbounded recursion).
    /// 2. Hook scope is stamped by the *loader* (in `load_user_hooks`), never
    ///    read from the hook JSON. `HookScope` is `#[serde(skip)]` in the
    ///    coda_agent definition, so any `"scope"` field in JSON is silently
    ///    discarded and replaced with `HookScope::Project` (untrusted default).
    fn build_session_services(&self, client: Arc<dyn LlmClient>) -> SessionServices {
        let runtime = Arc::clone(&self.runtime);
        let model_source: Arc<dyn Fn() -> String + Send + Sync> = Arc::new(move || {
            runtime.lock().expect("runtime config poisoned").model.clone()
        });
        // 1. Hook-free subagent (prevents hook re-entrancy for agent-type hooks).
        let hook_free_subagent = SubagentHost::with_defaults(
            Arc::clone(&client),
            Arc::clone(&self.permission_prompt),
            Arc::clone(&self.permission_mode),
            Arc::clone(&self.tools),
            Arc::clone(&self.task_manager),
            self.working_dir.clone(),
        ).with_model_source(Arc::clone(&model_source));

        // 2. Trust guard using the session-scoped trust store.
        let trust_guard = HookTrustGuard::new(
            Arc::clone(&self.hook_trust_store) as Arc<dyn HookTrustStore>,
            self.working_dir.clone(),
            None, // headless: untrusted project hooks are refused without interactive prompt
        );

        // 3. HookRunner with the hook-free subagent factory so agent hooks
        //    cannot re-enter the hook system.
        let executor: Arc<dyn HookExecutor> = Arc::new(ShellHookExecutor);
        let hook_runner = Arc::new(HookRunner::build(
            self.user_hooks.clone(),
            executor,
            Some(Arc::new(trust_guard)),
            None,
            Some(hook_free_subagent as Arc<dyn SubagentFactory>),
            Vec::new(), // no HTTP allowlist; http hooks require explicit opt-in
        ));

        // 4. Main subagent host (with the hook runner).
        let main_subagent = SubagentHost::new(
            Arc::clone(&client),
            Arc::clone(&self.permission_prompt),
            Arc::clone(&self.permission_mode),
            Arc::clone(&self.tools),
            Arc::new(ToolQuarantine::new()),
            Arc::clone(&self.task_manager),
            self.current_model(),
            4096,
            500,
            self.working_dir.clone(),
            Some(Arc::clone(&hook_runner)),
            MAX_CONCURRENT_SUBAGENTS,
        ).with_model_source(model_source);

        // 5. Schedule runtime — fires due scheduled tasks via the main subagent.
        let runner = TaskManagerRunner::new(
            Arc::clone(&self.task_manager),
            Arc::clone(&main_subagent) as Arc<dyn SubagentFactory>,
        );
        let schedule_runtime = ScheduleRuntime::new(
            Arc::clone(&self.schedule_store),
            runner,
            Arc::new(NullScheduleLifecycleSink),
        );

        SessionServices {
            hook_runner,
            subagent_host: main_subagent,
            schedule_runtime,
        }
    }

    fn try_claim_initialization(&self) -> Result<InitializationGuard<'_>, RpcError> {
        let mut busy = self.turn_active.lock().expect("turn_active poisoned");
        if *busy {
            return Err(RpcError::internal("another operation is already in progress; busy"));
        }
        if !self.engine_state.begin_initialization() {
            return Err(RpcError::internal("cannot initialize a stopping or stopped engine"));
        }
        *busy = true;
        Ok(InitializationGuard {
            flag: &self.turn_active,
            state: &self.engine_state,
            released: false,
        })
    }

    /// Attempt to atomically claim the turn slot.
    ///
    /// Returns a guard on success, or `None` if another prompt, compaction or
    /// administrative mutation is already running.
    ///
    /// C6: the *public* turn opens here, not later. The single-flight flag
    /// and `StateSnapshot.lifecycle`/`turn.phase` are set under the same lock
    /// acquisition, so there is no window in which the engine refuses a
    /// second prompt as "busy" while still reporting itself idle. Everything
    /// after this point — credential lookup, session-service construction,
    /// building the agent — is covered by the claimed phase.
    ///
    /// I3: a prompt claim also **opens the steering inbox**, here, before
    /// `preparing` is observable. `Agent::run` used to be the first thing to
    /// unseal it, so a client that saw `preparing` and steered immediately
    /// was told `turnEnding` — the queue was still sealed from the *previous*
    /// turn. `open_for_turn` preserves anything already queued; it only
    /// unseals. Lock order is TURN → INBOX → STATE → BUS throughout.
    ///
    /// The guard releases on drop rather than at an explicit call site. That
    /// matters because a serve task can be *cancelled* — if the client
    /// disconnects mid-turn the future is dropped, and a release that only
    /// runs on the `Ok`/`Err` paths would never execute. The slot would stay
    /// claimed for the life of the process and every later prompt would be
    /// refused as busy, with no way to recover short of a restart. For the
    /// same reason the guard also finalises the **public** state: a cancelled
    /// or panicking turn used to leave `lifecycle: busy` and a frozen `turn`
    /// in every snapshot forever.
    ///
    /// C1: admission is decided by `EngineState::begin_turn`, inside the STATE
    /// transaction, so a claim that raced `shutdown` is refused there rather
    /// than by a pre-check that can go stale between the look and the claim.
    /// Nothing observable happens on refusal: the runtime slot is never taken,
    /// the steering inbox is neither opened nor sealed, no turn observer is
    /// installed and nothing is published.
    fn try_claim_turn(
        &self,
        turn_id: &str,
        user_text: &str,
        kind: TurnKind,
    ) -> Result<TurnGuard<'_>, ClaimRefused> {
        let mut busy = self.turn_active.lock().expect("turn_active poisoned");
        if *busy {
            return Err(ClaimRefused::Busy);
        }
        // TURN -> CONFIG -> STATE -> BUS. CONFIG is held across `begin_turn`
        // so the configuration this turn captures is exactly the one the
        // public `config.next` describes: a commit is either wholly before
        // this claim or wholly after it, never half-observed by it.
        let admitted = {
            let runtime = self.runtime.lock().expect("runtime config poisoned");
            let active_config = runtime.active_config(self.permission_mode.get());
            self.engine_state.begin_turn(turn_id, user_text, active_config, kind.phase())
        };
        if !admitted {
            return Err(ClaimRefused::Stopped);
        }
        *busy = true;
        if kind.accepts_steering() {
            // Only unseals; anything already queued is preserved.
            self.session.steering.open_for_turn();
        } else {
            // No agent loop will run, so a message accepted here could only
            // ever be dropped at the end. Seal — but only if the queue is
            // empty, so an operator draft that is already waiting is never
            // discarded by an administrative operation.
            self.session.steering.try_seal_empty();
        }
        self.steering_observer.set_current_turn(Some(turn_id.to_string()));
        drop(busy);
        Ok(TurnGuard {
            flag: &self.turn_active,
            steering: &self.session.steering,
            state: &self.engine_state,
            observer: &self.steering_observer,
            turn_id: turn_id.to_string(),
        })
    }
}

/// Why a claim of the single-flight slot was refused.
///
/// "Busy" and "the engine is shutting down" are different answers and a
/// client acts on them differently: one is worth retrying, the other never
/// is. Collapsing them into one message told an operator to wait for a turn
/// that will never end.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ClaimRefused {
    /// Another prompt, compaction or maintenance operation holds the slot.
    Busy,
    /// The engine is stopping or stopped; no new work is admitted.
    Stopped,
}

impl ClaimRefused {
    /// The refusal as an RPC error. `busy_note` is the operation-specific
    /// wording for a genuinely busy engine, preserved verbatim per call site.
    fn into_error(self, busy_note: &str) -> RpcError {
        match self {
            ClaimRefused::Busy => RpcError::internal(busy_note.to_owned()),
            ClaimRefused::Stopped => RpcError::internal(
                "engine is shutting down; no new turn was started".to_owned(),
            ),
        }
    }
}

/// Initialization shares the runtime slot but is not an agent turn. Release
/// the slot before publishing readiness, including on error or cancellation.
struct InitializationGuard<'a> {
    flag: &'a std::sync::Mutex<bool>,
    state: &'a EngineState,
    released: bool,
}

impl InitializationGuard<'_> {
    fn release(&mut self, completed: bool) -> bool {
        let mut busy = self.flag.lock().unwrap_or_else(|p| p.into_inner());
        *busy = false;
        self.released = true;
        self.state.finish_initialization(completed)
    }

    fn finish(mut self) -> Result<(), RpcError> {
        if self.release(true) {
            Ok(())
        } else {
            Err(RpcError::internal("engine shut down during initialization"))
        }
    }
}

impl Drop for InitializationGuard<'_> {
    fn drop(&mut self) {
        if !self.released {
            self.release(false);
        }
    }
}

/// What a claim of the single-flight slot is for. Every kind is equally
/// "busy" — a second prompt is refused for all of them — but they differ in
/// the phase they publish and in whether mid-operation steering is meaningful.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TurnKind {
    /// `session/prompt`: the agent loop runs and can consume steering.
    Prompt,
    /// `session/compact`: no agent loop, so a steer would only be queued and
    /// then dropped at the end. The inbox is sealed (only when already empty,
    /// so a waiting draft is never discarded) and `session/steer` answers
    /// `turnEnding` rather than accepting a message it cannot deliver.
    Compaction,
    /// `session/fork`, `session/rewind`: administrative history mutations.
    Maintenance,
}

impl TurnKind {
    fn phase(self) -> ActivityPhase {
        match self {
            TurnKind::Prompt => ActivityPhase::Preparing,
            TurnKind::Compaction => ActivityPhase::Compacting,
            TurnKind::Maintenance => ActivityPhase::Maintenance,
        }
    }

    fn accepts_steering(self) -> bool {
        matches!(self, TurnKind::Prompt)
    }

    fn stop_reason(self) -> Option<String> {
        match self {
            TurnKind::Prompt => None,
            TurnKind::Compaction => Some("compaction".into()),
            TurnKind::Maintenance => Some("maintenance".into()),
        }
    }
}

/// Classifies a turn failure into something safe to retain and serve from
/// `session/getState` indefinitely (S3).
///
/// `AgentError`'s `Display` interpolates `LlmError`, whose text can carry
/// provider-authored content and, for `Transport`/`Protocol`/`Unauthorized`,
/// a credential-bearing URL. None of that may reach `lastTurnOutcome`, which
/// any connected client can poll for the rest of the session. The raw message
/// is still returned once, synchronously, in the `session/prompt` reply —
/// existing behaviour, and not retained anywhere.
fn safe_turn_error(err: &AgentError) -> Option<coda_proto::state::TurnErrorSummary> {
    use coda_proto::state::TurnErrorSummary;
    match err {
        // The `interrupted` flag already says this; there is no failure.
        AgentError::Cancelled => None,
        AgentError::Llm(e) => Some(TurnErrorSummary {
            category: format!("llm.{}", coda_llm::diagnostics::category(e)),
            status: coda_llm::diagnostics::status(e).map(i64::from),
            parameter: coda_llm::diagnostics::parameter(e),
        }),
        AgentError::Other(_) => Some(TurnErrorSummary {
            category: "agent.other".into(),
            status: None,
            parameter: None,
        }),
        // A typed terminal abort raised by a tool (today: an operator
        // question that was never answered). The reason is already a fixed
        // classification token, never provider text, so it is safe to retain.
        AgentError::Aborted { reason } => Some(TurnErrorSummary {
            category: format!("agent.aborted.{reason}"),
            status: None,
            parameter: None,
        }),
    }
}

/// The same rule for a failure the engine itself raised before or instead of
/// the agent loop: only a fixed token, never the message.
fn safe_engine_error(code: i64) -> coda_proto::state::TurnErrorSummary {
    coda_proto::state::TurnErrorSummary {
        category: match code {
            -32001 => "engine.unauthorized".into(),
            -32602 => "engine.invalidParams".into(),
            _ => "engine.failed".into(),
        },
        status: None,
        parameter: None,
    }
}

/// Releases the turn slot when dropped, including on cancellation or panic,
/// and finalises the public turn state idempotently (C6).
struct TurnGuard<'a> {
    flag: &'a std::sync::Mutex<bool>,
    steering: &'a coda_agent::SteeringInbox,
    state: &'a EngineState,
    observer: &'a SteeringStateObserver,
    turn_id: String,
}

impl Drop for TurnGuard<'_> {
    fn drop(&mut self) {
        // Lock order TURN -> INBOX -> STATE -> BUS (I5): `session/steer`
        // holds TURN across its enqueue, so the release path must take TURN
        // first and only then touch the inbox, or the two invert.
        let mut busy = match self.flag.lock() {
            Ok(g) => g,
            // A poisoned lock still has to release the slot, otherwise one
            // panic would wedge the session permanently.
            Err(poisoned) => poisoned.into_inner(),
        };
        self.steering.close_for_turn();
        // Idempotent by turn id: on the normal path the caller already ended
        // the turn with its exact outcome and this is a no-op — including
        // when a *later* turn has since claimed the slot, which this must
        // never clobber. It only takes effect when the future was cancelled,
        // an early preflight failed, or the turn panicked — precisely the
        // cases that used to leave the public state stuck in `busy` forever.
        self.state.end_turn(TurnEnd {
            turn_id: self.turn_id.clone(),
            stop_reason: None,
            interrupted: true,
            error: Some(coda_proto::state::TurnErrorSummary {
                category: "incomplete".into(),
                status: None,
                parameter: None,
            }),
            history_length: None,
            wire: None,
        });
        self.observer.set_current_turn(None);
        // I1: the runtime flag is cleared *before* `ready` is published, and
        // both happen under TURN. So a client that reads `ready` is reading a
        // state written after the slot was already free — "the snapshot says
        // ready" therefore implies "a prompt is accepted". The opposite
        // staleness (still `busy` for an instant after the slot frees) is the
        // safe direction: it only ever under-promises.
        *busy = false;
        self.state.release_turn();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ServeBackend
// ─────────────────────────────────────────────────────────────────────────────

#[async_trait]
impl ServeBackend for ServeHost {
    async fn initialize(&self, p: InitParams) -> Result<Value, RpcError> {
        let initialization = self.try_claim_initialization()?;
        // Negotiate before resume publishes its sessionChanged event, but only
        // after claiming the slot: a refused handshake must have no side effects.
        if p.client_capabilities.as_ref().is_some_and(|c| c.wants_state_events()) {
            self.engine_state.bus_ref().enable_state_events();
        }

        let mut resumed = false;
        // If the client supplied a session_id, attempt to resume it.
        // Security: session_id comes from an untrusted wire message — validate it
        // before using it as a file-system key.
        if let Some(ref req_id) = p.session_id {
            if !session_id_is_valid(req_id) {
                // Invalid id format — treat as not found.
                return Err(RpcError::session_not_found());
            }
            let store = SessionTranscriptStore::new(&self.working_dir);
            match store.load(req_id).await {
                Some(messages) => {
                    // Adopt the resumed id so future saves go to the right file.
                    *self.current_session_id.lock().expect("session_id poisoned") =
                        req_id.clone();
                    resumed = true;
                    // F2: resume changes the whole conversation view; announce it.
                    // F3 (review): seeding the committed history and announcing
                    // the new epoch/fence are one critical section under
                    // HISTORY (order HISTORY -> STATE -> BUS, no `.await`
                    // inside), so no concurrent read can be handed the resumed
                    // conversation under the pre-resume `historyEpoch`.
                    let mut committed =
                        self.session.history.lock().expect("history poisoned");
                    *committed = messages;
                    let restored = committed.len() as i64;
                    self.engine_state.session_changed("resume", req_id.clone(), restored);
                }
                None => {
                    // Session not found in the working directory.
                    return Err(RpcError::session_not_found());
                }
            }
        }

        if let Some(ctx) = &self.diagnostics {
            let session_ctx = ctx.with_session(self.active_session_id());
            session_ctx.record(if resumed {
                coda_diagnostics::Event::SessionResumed
            } else {
                coda_diagnostics::Event::SessionInitialized
            });
        }

        // Wire an explicitly provided API key; otherwise leave client as-is
        // (lazy credential lookup happens on first session/prompt).
        if let Some(ref key) = p.api_key {
            // Retain any endpoint configured for this host: rebuilding the
            // client at the default host would silently redirect an
            // explicitly-configured proxy/self-hosted deployment to
            // api.anthropic.com (Finding I1).
            //
            // A refusal captured at startup is equally binding here, and this
            // is the case it exists for: an engine that started as Copilot —
            // legitimately, because the refused endpoint was irrelevant to it
            // — must not answer a wire-supplied API key by sending it to the
            // default host. The key is not adopted, and nothing is wired.
            let endpoint = match self.configured_endpoint.as_ref() {
                Ok(endpoint) => endpoint.as_deref(),
                Err(refusal) => {
                    return Err(RpcError::invalid_params(format!(
                        "the supplied API key cannot be used: {refusal}"
                    )))
                }
            };
            if let Some(c) = build_anthropic_at(key, endpoint) {
                // Scoped deliberately: `apply_pending_startup_effort` below
                // goes through `session/setEffort`, which takes this same
                // writer lock, and a tokio mutex is not re-entrant. The guard
                // must be released before that call or initialize deadlocks
                // against itself.
                let _commit = self.config_commit.lock().await;
                let mut client = self.client.lock().await;
                // Admission is decided inside the commit, under STATE: a
                // handshake overtaken by `shutdown` adopts nothing at all —
                // not the client, not the model, not the provider — and
                // publishes nothing. The provider mirror moves with the
                // client and is announced with it, so `config.next` never
                // reports "unknown" for a provider that is in fact wired, and
                // a `stateEvents` client is never left with a configuration
                // the engine has left. An explicitly selected model survives
                // the wiring, and the effort is re-resolved against the
                // client being wired (see `commit_wired_provider`).
                self.commit_wired_provider(&c).await?;
                *client = Some(c);
            }
        }
        self.apply_pending_startup_effort().await?;
        // Capability negotiation already happened at the top of this method
        // (§2.1, S4): absent `clientCapabilities` means legacy behaviour — no
        // gated `event/*` method is ever written to the connection.
        initialization.finish()?;
        let telemetry_log_path = self
            .diagnostics
            .as_ref()
            .and_then(|ctx| ctx.logger().status().path)
            .map(|p| p.display().to_string());
        let resp = InitializeResponse {
            protocol_version: PROTOCOL_VERSION.into(),
            session_id: self.active_session_id(),
            // Must match the C# engine verbatim: clients key off this string,
            // so reporting the crate name here would be a silent parity break.
            server_info: "coda".into(),
            telemetry_log_path,
            contract_version: CONTRACT_VERSION.into(),
            engine_instance_id: self.engine_state.bus_ref().engine_instance_id().to_string(),
            event_cursor: self.engine_state.bus_ref().cursor(),
            capabilities: capability_catalog(),
        };

        // Surface any MCP connection/config failures now that a client is
        // listening. A broken optional server must be reported, never hidden,
        // and never turned into a hard failure of the session.
        let notices: Vec<String> = {
            let mut pending = self.pending_mcp_notices.lock().expect("mcp notices poisoned");
            std::mem::take(&mut *pending)
        };
        for message in notices {
            self.sink.emit(AgentEvent::Error { message });
        }

        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn shutdown(&self) -> Result<Value, RpcError> {
        // S5: `stopping`/`stopped` were reachable values in the wire
        // `EngineLifecycle` enum that nothing ever set, so a client could
        // never observe a shutdown it was told to expect. They are published
        // for real now, around the work that actually stops the engine.
        self.engine_state.shutdown_started();
        if let Some(c) = self.current_cancel.lock().expect("cancel poisoned").take() {
            c.cancel();
        }
        // Shut down the schedule runtime if it was ever started.
        let services = self.session_services.lock().await.clone();
        if let Some(svc) = services {
            svc.schedule_runtime.shutdown().await;
        }
        // Shut down any connected MCP servers so child processes do not leak.
        if let Some(manager) = &self.mcp_manager {
            manager.shutdown().await;
        }
        self.engine_state.shutdown_completed();
        Ok(json!({ "ok": true }))
    }

    async fn session_prompt(&self, p: PromptParams) -> Result<Value, RpcError> {
        let provider = self.connected_provider().await;
        // Mint the canonical turnId unconditionally, before any preflight
        // check (image validation, turn-slot claim): every prompt gets a
        // stable id regardless of whether it is ultimately accepted, so
        // diagnostics and the wire identity model never disagree about which
        // attempt failed preflight.
        let turn_id = uuid::Uuid::new_v4().to_string();
        let turn_ctx = self.diagnostics.as_ref().map(|ctx| {
            ctx.with_session(self.active_session_id())
                .with_turn(turn_id.clone())
                .with_provider_model(Some(provider), Some(self.current_model()))
        });
        let run = async {
        self.apply_pending_startup_effort().await?;
        // Validate images BEFORE claiming the turn slot so a bad image
        // never leaves the host stuck in "busy" state.
        if let Some(images) = p.images.as_deref() {
            for img in images {
                let media_type = img["mediaType"].as_str().unwrap_or("");
                match media_type {
                    "image/png" | "image/jpeg" | "image/gif" | "image/webp" => {}
                    other => {
                        return Err(RpcError::invalid_params(format!(
                            "unsupported image media type: {other}"
                        )));
                    }
                }
                let b64 = img["base64"].as_str().unwrap_or("");
                if let Err(e) = validate_base64(b64) {
                    return Err(RpcError::invalid_params(format!(
                        "invalid base64 encoding: {e}"
                    )));
                }
            }
        }

        // Claim the turn slot; the guard releases it on every exit path,
        // including cancellation, and finalises the public turn with it.
        let _turn = self
            .try_claim_turn(&turn_id, &p.text.clone().unwrap_or_default(), TurnKind::Prompt)
            .map_err(|refused| {
                refused.into_error("another prompt is already in progress; busy")
            })?;

        let result = self.run_prompt_inner(p, turn_id.clone()).await;
        if let Err(e) = &result {
            // The turn was public from the moment the slot was claimed, so a
            // failure after that point has to be published as a real outcome
            // rather than left for the guard's generic fallback. Only a
            // classification is retained — never the message (S3).
            self.engine_state.end_turn(TurnEnd {
                turn_id: turn_id.clone(),
                stop_reason: None,
                interrupted: false,
                error: Some(safe_engine_error(e.code)),
                history_length: None,
                wire: None,
            });
        }
        result
        };
        match turn_ctx {
            Some(ctx) => {
                ctx.record(coda_diagnostics::Event::TurnStart);
                let result = coda_diagnostics::scope(ctx.clone(), run).await;
                if let Err(error) = &result {
                    ctx.record(coda_diagnostics::Event::TurnFailed {
                        category: match error.code {
                            -32001 => "unauthorized",
                            -32602 => "invalid_params",
                            _ => "preflight",
                        },
                        status: None,
                    });
                }
                result
            }
            None => run.await,
        }
    }

    async fn session_interrupt(&self) -> Result<Value, RpcError> {
        {
            let busy = self.turn_active.lock().expect("turn_active poisoned");
            if *busy && self.steering_observer.turn_id().is_none() {
                return Err(RpcError::internal(
                    "no interruptible turn is active; use shutdown to stop initialization",
                ));
            }
        }
        let token = self.current_cancel.lock().expect("cancel poisoned").take();
        if let Some(c) = token {
            c.cancel();
        } else {
            // No turn is running yet (the cancel token is published slightly
            // after the turn slot is claimed). Record a pending interrupt so
            // run_prompt_inner will cancel as soon as it publishes the token.
            // C# defers the interrupt to the next published token (Finding 4).
            *self.pending_interrupt.lock().expect("pending_interrupt poisoned") = true;
        }
        Ok(json!({ "ok": true }))
    }

    async fn session_steer(&self, p: SteerParams) -> Result<Value, RpcError> {
        // I5: the busy check and the enqueue are performed on **one**
        // serialized path. Previously `turn_active` was read under its own
        // lock and `enqueue` then ran under the inbox's, so a turn could end
        // between the two and the reported `rejectedReason` was a guess:
        // `noActiveTurn` and `turnEnding` were not distinguishable in the
        // race. Holding TURN across the enqueue linearises them, in the
        // global lock order TURN -> INBOX -> STATE -> BUS (the same order
        // `TurnGuard::drop` takes them, so no inversion is possible).
        let resp = {
            let busy = self.turn_active.lock().expect("turn_active poisoned");
            if !*busy || self.steering_observer.turn_id().is_none() {
                SteerResponse { ok: false, message_id: None, rejected_reason: Some("noActiveTurn") }
            } else {
                // Attempt to enqueue; the inbox may have been sealed racing
                // the turn end, or the text may be empty — each reported with
                // its exact reason rather than a mute `ok:false` (§2.7).
                match self.session.steering.enqueue_with_reason(&p.text) {
                    Ok(entry) => {
                        SteerResponse { ok: true, message_id: Some(entry.id), rejected_reason: None }
                    }
                    Err(coda_agent::steering::SteerRejectReason::Sealed) => {
                        SteerResponse { ok: false, message_id: None, rejected_reason: Some("turnEnding") }
                    }
                    Err(coda_agent::steering::SteerRejectReason::EmptyText) => {
                        SteerResponse { ok: false, message_id: None, rejected_reason: Some("emptyText") }
                    }
                }
            }
        };
        // A refusal never enters the queue and never receives a message id,
        // so it is reported exactly once, synchronously, as this reply's
        // `rejectedReason` — the engine does not fabricate an id in order to
        // put a phantom entry in the outcomes ring (see
        // `SteeringStateObserver::on_rejected`). Because the check and the
        // enqueue above are now one serialized step, that reason is exact
        // rather than a guess about which side of a race we were on.
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_recall_steering(&self) -> Result<Value, RpcError> {
        // Snapshot `enqueuedAt` before recalling: `recall_all` synchronously
        // drives the steering observer, which removes these entries from
        // `EngineState`'s pending projection.
        let pending_before = self.engine_state.steering_pending_snapshot();
        let recalled = self.session.steering.recall_all();
        let messages: Vec<RecalledMessage> = recalled
            .iter()
            .map(|entry| RecalledMessage {
                id: entry.id.clone(),
                text: entry.text.clone(),
                enqueued_at: pending_before
                    .iter()
                    .find(|p| p.message_id == entry.id)
                    .map(|p| p.enqueued_at.clone()),
            })
            .collect();
        Ok(json!({ "messages": messages }))
    }

    async fn session_get_state(&self, p: GetStateParams) -> Result<Value, RpcError> {
        // `sections` was accepted and then silently ignored, so a client
        // asking for a subset got the whole snapshot back and had no way to
        // tell. Section filtering is not implemented in this stage; say so
        // explicitly rather than answer a different question than was asked.
        // The matching `state.sectionFilter` capability is advertised
        // `supported: false` with the same reason.
        if p.sections.as_ref().is_some_and(|s| !s.is_empty()) {
            return Err(RpcError::invalid_params(
                "sections filtering is not supported by this engine (capability state.sectionFilter is false); omit `sections` to receive the whole snapshot",
            ));
        }
        let bus = self.engine_state.bus_ref();
        let limits = coda_proto::state::Limits {
            ring_envelopes: bus.ring_envelopes_limit(),
            ring_bytes: bus.ring_bytes_limit(),
            live_bytes_cap: crate::state::live::DEFAULT_LIVE_BYTES_CAP as i64,
            outcomes_retained: crate::state::DEFAULT_OUTCOMES_RETAINED as i64,
            history_block_bytes_cap: crate::state::DEFAULT_HISTORY_BLOCK_BYTES_CAP,
            max_history_page: crate::history::MAX_HISTORY_LIMIT,
            max_session_page: crate::history::MAX_SESSION_LIMIT,
        };
        // STATE-only: the configuration is read from the same captured state
        // as the cursor, so a commit can never land between "what config is
        // in force" and "which events this snapshot already covers".
        let snapshot = self.engine_state.project(coda_proto::messages::CONTRACT_VERSION, limits);
        serde_json::to_value(&snapshot).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_get_events(&self, p: GetEventsParams) -> Result<Value, RpcError> {
        let limit = p.limit.filter(|l| *l > 0).unwrap_or(500).max(0) as usize;
        let result = self.engine_state.bus_ref().get_events(&p.engine_instance_id, p.after_cursor, limit)?;
        serde_json::to_value(&result).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_history(&self) -> Result<Value, RpcError> {
        let history = self.session.history.lock().expect("history poisoned");
        let messages = project_history(&history);
        Ok(json!({ "messages": messages }))
    }

    async fn session_messages(&self, p: MessagesParams) -> Result<Value, RpcError> {
        let history = self.session.history.lock().expect("history poisoned");
        let all = project_history(&history);
        let since = p.since_index.max(0) as usize;
        let slice: Vec<_> = all.into_iter().skip(since).collect();
        let next_index = (since + slice.len()) as i32;
        Ok(json!({ "messages": slice, "nextIndex": next_index }))
    }

    async fn session_models(&self, p: ModelsParams) -> Result<Value, RpcError> {
        // The active model and provider travel with the list. Without them a
        // client can only guess which entry is in use, and the obvious guess —
        // the first — is whatever order the provider returned. That made the
        // status bar name a model the engine was not using, and made switching
        // look as though it had not been saved.
        //
        // One capture is the whole basis of the answer: the model named as
        // active and the level annotated onto its row come from the same
        // record, so a setter landing while the provider list is in flight
        // cannot make the reply pair one model with another's effort.
        let captured = self.capture_runtime().config;
        let active = captured.model.clone();
        let client = self.client.lock().await.clone();
        let Some(client) = client else {
            // No client yet: return a catalog so the user can see model options
            // rather than an empty list that looks like "no models exist".
            // Finding 2: never collapse "could not determine" into "none exist".
            let provider_id = captured
                .provider_id
                .clone()
                .unwrap_or_else(|| crate::settings::FALLBACK_PROVIDER.to_owned());
            let mut catalog = catalog_models();
            self.annotate_effort(&mut catalog, &captured, &provider_id);
            let catalog = serde_json::to_value(&catalog)
                .map_err(|e| RpcError::internal(e.to_string()))?;
            return Ok(json!({
                "source": "catalog",
                "models": catalog,
                "model": active,
                "providerId": provider_id,
            }));
        };
        let provider_id = client.provider_id().to_owned();
        let result =
            if p.refresh { client.refresh_models().await } else { client.list_models().await };
        match result {
            Ok(models) if !models.is_empty() => {
                // A live list says what the provider offers; the catalogue
                // says what it costs. The provider does not report prices, so
                // without this join a live list has none and the cost quietly
                // disappears whenever the network is up.
                let catalog = crate::catalog::ModelCatalog::load();
                let mut wire: Vec<WireModel> = models
                    .into_iter()
                    .map(|m| {
                        let priced = catalog.find(Some(&provider_id), &m.id);
                        WireModel {
                            display_name: m.display_name,
                            context_limit: m.context_limit.map(|n| n as i64),
                            input_cost: priced.and_then(|c| c.cost).map(|c| c.input),
                            output_cost: priced.and_then(|c| c.cost).map(|c| c.output),
                            reasoning_levels: m.reasoning_levels,
                            effort: None,
                            id: m.id,
                        }
                    })
                    .collect();
                self.annotate_effort(&mut wire, &captured, &provider_id);
                let v = serde_json::to_value(&wire)
                    .map_err(|e| RpcError::internal(e.to_string()))?;
                Ok(json!({
                    "source": "live",
                    "models": v,
                    "model": active,
                    "providerId": provider_id,
                }))
            }
            // Fetch error OR empty live list: fall back to the catalog so a
            // transient network failure or an expired token does not present
            // the user with zero models (Finding 2).
            _ => {
                let mut catalog = catalog_models();
                self.annotate_effort(&mut catalog, &captured, &provider_id);
                let catalog = serde_json::to_value(&catalog)
                    .map_err(|e| RpcError::internal(e.to_string()))?;
                Ok(json!({
                    "source": "catalog",
                    "models": catalog,
                    "model": active,
                    "providerId": provider_id,
                }))
            }
        }
    }

    async fn session_set_goal(&self, p: SetGoalParams) -> Result<Value, RpcError> {
        // Validate budget shape before storing — the same rules the startup
        // path enforces (positive duration, non-negative continuations). An
        // out-of-range value is rejected with -32602, never silently coerced.
        if let Err(e) = validate_goal_budget(p.max_duration.as_deref(), p.max_continuations) {
            return Err(RpcError::invalid_params(e.to_string()));
        }
        {
            let mut s = self.goal_params.lock().expect("goal poisoned");
            s.goal = p.goal.clone();
            s.max_duration = p.max_duration.clone();
            s.max_continuations = p.max_continuations;
        }
        let resp = SetGoalResponse {
            ok: true,
            goal: p.goal,
            max_duration: p.max_duration,
            max_continuations: p.max_continuations,
        };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_set_permission_mode(
        &self,
        p: SetPermissionModeParams,
    ) -> Result<Value, RpcError> {
        // An unknown mode is refused rather than falling back to a default: a
        // caller asking for "bypassPermissions" and silently getting "ask"
        // would be told it worked while every tool still prompted, and a
        // caller naming a mode we do not know must never be quietly granted a
        // more permissive one.
        let Some(mode) = parse_permission_mode(&p.mode) else {
            return Ok(serde_json::json!({
                "ok": false,
                "applied": wire_permission_mode(self.permission_mode.get()),
            }));
        };

        // The mode itself lives in the shared atomic the tool loop reads at
        // every permission check, but it is committed *through* the config
        // lock so the value that is announced and the value that is captured
        // into a turn can never come from two different instants.
        //
        // Published so a `stateEvents` client converges on `config.next`
        // without polling — this is a real change with a real scope
        // (`nextPermissionCheck`), and a silent one would make the config
        // section a poll-only feed dressed as a push feed.
        self.commit_config("permissionMode", |_| self.permission_mode.set(mode))?;
        Ok(serde_json::json!({
            "ok": true,
            "applied": wire_permission_mode(mode),
        }))
    }

    async fn session_set_system_prompt(&self, p: SetSystemPromptParams) -> Result<Value, RpcError> {
        let new_prompt = p.text
            .map(|t| t.trim().to_owned())
            .filter(|t| !t.is_empty());
        let cleared = new_prompt.is_none();
        // The event carries only the *source* (`default`/`sessionOverride`),
        // never the prompt text — an override can be long and is not state a
        // push feed should broadcast.
        self.commit_config("systemPrompt", |rc| {
            rc.system_prompt = new_prompt.as_deref().map(Arc::from);
        })?;
        Ok(json!({
            "ok": true,
            "cleared": cleared,
            "text": new_prompt,
        }))
    }

    async fn session_set_model(&self, p: SetModelParams) -> Result<Value, RpcError> {
        let requested = p.model.trim();
        if requested.is_empty() {
            return serde_json::to_value(coda_proto::responses::SetModelResult::Refused {
                ok: false, note: "No model given.".into(),
            }).map_err(|error| RpcError::internal(error.to_string()));
        }

        // Serialise against every other config commit: holding this for the
        // whole operation guarantees the effort resolved below is resolved
        // against the same model that is committed, and that a concurrent
        // setter sees a consistent (model, effort) pair rather than a
        // half-applied switch.
        let _guard = self.config_commit.lock().await;

        // Resolve the effort the *requested* model would run at BEFORE
        // touching the runtime. The lookup awaits the provider, and a runtime
        // mutated before that await is observable — by a reader, by a turn
        // claim, and by a cancellation that drops this future — as a model
        // paired with the previous model's effort. Nothing is mutated until
        // both halves of the change are known.
        let effective_effort = self.resolve_effort_for_model(requested).await;

        // Takes effect on the next turn: the agent is rebuilt from the
        // committed record each time, so there is nothing to invalidate and
        // nothing to restart. The running turn keeps the model it started
        // with, which is the only coherent answer — swapping mid-turn would
        // leave one exchange split across two models.
        //
        // Model and its re-resolved effort move together, under one lock,
        // published as one event: no reader can see one without the other.
        self.commit_config("model", |rc| {
            rc.model = requested.to_owned();
            rc.model_is_explicit = true;
            rc.effort = effective_effort;
        })?;

        serde_json::to_value(coda_proto::responses::SetModelResult::Selected {
            ok: true, model: requested.to_owned(),
            effort: effective_effort.map(|e| e.as_str().to_owned()),
        }).map_err(|error| RpcError::internal(error.to_string()))
    }

    async fn session_set_effort(&self, p: SetEffortParams) -> Result<Value, RpcError> {
        // Serialise against every other config commit so the model this
        // effort is resolved and committed against cannot change underneath
        // us. Without this, the `resolve_capability` await below could
        // straddle a model switch and write a level resolved for the
        // previous model.
        let _guard = self.config_commit.lock().await;

        let model = self.current_model();
        let current = || self.current_effort().map(|e| e.as_str().to_owned());

        // Canonical-identity guard. The picker was opened for a specific
        // (provider, model); if the active identity has since changed, the
        // request is stale — reject it *without mutating* anything, rather than
        // silently reconfiguring whatever model happens to be active now.
        if let Some(expected) = p.expected_model.as_deref() {
            if !expected.eq_ignore_ascii_case(&model) {
                let resp = SetEffortResponse {
                    ok: false,
                    applied: None,
                    current: current(),
                    note: format!(
                        "active model is now {model}, not {expected}; effort not changed"
                    ),
                };
                return serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()));
            }
        }
        if let Some(expected) = p.expected_provider.as_deref() {
            let provider = self.connected_provider().await;
            if !expected.eq_ignore_ascii_case(&provider) {
                let resp = SetEffortResponse {
                    ok: false,
                    applied: None,
                    current: current(),
                    note: format!(
                        "active provider is now {provider}, not {expected}; effort not changed"
                    ),
                };
                return serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()));
            }
        }

        let (capability, indeterminate) = self.resolve_capability(&model).await;

        // "auto" and a missing value both mean "clear to automatic". Recorded
        // as an *explicit* override (value `None`) so switching away and back
        // restores automatic rather than silently re-reading the saved level.
        let is_auto = matches!(
            p.effort.as_deref().map(str::trim),
            None | Some("") | Some("auto") | Some("Auto") | Some("AUTO")
        );
        if is_auto {
            // Commit first: a refused commit must not leave a session
            // override recorded for a change that never took effect.
            self.commit_config("effort", |rc| rc.effort = None)?;
            self.effort_overrides
                .lock()
                .expect("effort poisoned")
                .insert(model, None);
            return serde_json::to_value(&SetEffortResponse {
                ok: true,
                applied: None,
                current: None,
                note: String::new(),
            })
            .map_err(|e| RpcError::internal(e.to_string()));
        }

        let raw = p.effort.as_deref().unwrap_or_default().trim();

        // Syntactically invalid → ok:false, current unchanged.
        let Some(requested) = Effort::parse(raw) else {
            let resp = SetEffortResponse {
                ok: false,
                applied: None,
                current: current(),
                note: format!("unsupported effort: {raw}"),
            };
            return serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()));
        };

        // Validate against the model's capability. Indeterminate capability is
        // accepted optimistically (do not lie by dropping the user's choice);
        // a known capability clamps `max`→`high` and rejects anything the model
        // cannot honour, so `current` reports the *effective* level, never a
        // success-shaped value the backend would quietly ignore.
        match resolve_effective_effort(&capability, indeterminate, Some(requested)) {
            Some(effective) => {
                // Commit first — see the automatic branch above.
                self.commit_config("effort", |rc| rc.effort = Some(effective))?;
                self.effort_overrides
                    .lock()
                    .expect("effort poisoned")
                    .insert(model.clone(), Some(requested));
                let note = if effective == requested {
                    String::new()
                } else {
                    format!("{} applies as {} on {}", requested.as_str(), effective.as_str(), model)
                };
                let resp = SetEffortResponse {
                    ok: true,
                    applied: Some(effective.as_str().into()),
                    current: Some(effective.as_str().into()),
                    note,
                };
                serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
            }
            None => {
                // Known-unsupported for this model: reject rather than fake it.
                let note = if !capability.supported {
                    format!("model {model} does not support reasoning effort")
                } else {
                    let highest = capability.levels.last().map(String::as_str).unwrap_or("?");
                    format!("{raw} not supported (model stops at {highest})")
                };
                let resp = SetEffortResponse {
                    ok: false,
                    applied: None,
                    current: current(),
                    note,
                };
                serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
            }
        }
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    async fn model_adjust_effort(&self, p: AdjustEffortParams) -> Result<Value, RpcError> {
        // Direction is a single rung either way; anything else is a client bug,
        // not a level the engine can honour.
        let step: i32 = match p.direction {
            -1 => -1,
            1 => 1,
            other => {
                return Err(RpcError::invalid_params(format!(
                    "direction must be -1 or 1, got {other}"
                )));
            }
        };
        let model = p.model.trim().to_owned();
        if model.is_empty() {
            return Err(RpcError::invalid_params("model is required"));
        }

        // Hold the config writer lock across selection, capability, current
        // read and commit: a concurrent `set_model`/`set_effort` must not
        // retarget us midway and let us write a level resolved against a
        // different model.
        let _guard = self.config_commit.lock().await;

        // One captured record for the whole answer: the writer lock keeps it
        // current, and every level reported below is the one belonging to the
        // model reported next to it.
        let captured = self.capture_runtime().config;
        let provider = self.connected_provider().await;
        let active = captured.model.clone();

        // Provider guard: a picker opened while one provider was connected must
        // never silently reconfigure a different provider's model. Reject
        // without mutating anything.
        if let Some(expected) = p.expected_provider.as_deref() {
            if !expected.eq_ignore_ascii_case(&provider) {
                let current = self
                    .target_effort(&captured, &provider, &model)
                    .map(|e| e.as_str().to_owned());
                return Self::effort_result(
                    false,
                    &model,
                    &provider,
                    current,
                    model.eq_ignore_ascii_case(&active),
                    format!("active provider is now {provider}, not {expected}; effort not changed"),
                );
            }
        }

        // The target must be a model the engine actually knows, so a typo cannot
        // create an unreachable per-model override.
        let Some(canonical) = self.resolve_known_model(&model).await else {
            return Err(RpcError::invalid_params(format!("unknown model: {model}")));
        };
        let is_active = canonical.eq_ignore_ascii_case(&active);

        let (capability, indeterminate) = self.resolve_capability(&canonical).await;

        // Without a known level set there is nothing to step through: refuse
        // with a useful note rather than guess a ladder. `indeterminate` (a
        // Copilot model before its list is fetched) is distinct from a positive
        // "unsupported", so the note says which.
        if !capability.supported || capability.levels.is_empty() {
            let note = if indeterminate {
                format!("reasoning levels for {canonical} are not known yet")
            } else {
                format!("model {canonical} does not support reasoning effort")
            };
            let current = self
                .target_effort(&captured, &provider, &canonical)
                .map(|e| e.as_str().to_owned());
            return Self::effort_result(false, &canonical, &provider, current, is_active, note);
        }

        // The ladder the arrows walk: automatic first (when the model allows
        // it), then each supported level lowest to highest.
        let mut ladder: Vec<Option<Effort>> = Vec::new();
        if capability.supports_auto {
            ladder.push(None);
        }
        for level in &capability.levels {
            if let Some(effort) = Effort::parse(level) {
                ladder.push(Some(effort));
            }
        }

        // The current rung: the target's intent (active → live effective,
        // otherwise override or saved), normalised through the capability so
        // the value is always one the ladder contains and the index is valid.
        let requested = self.target_effort(&captured, &provider, &canonical);
        let current = resolve_effective_effort(&capability, indeterminate, requested);
        let cur_idx = ladder.iter().position(|rung| *rung == current).unwrap_or(0);

        let last = ladder.len().saturating_sub(1);
        let new_idx = if step < 0 { cur_idx.saturating_sub(1) } else { (cur_idx + 1).min(last) };
        let new_effort = ladder[new_idx];

        // Commit. Record the target's OWN override — an explicit auto (`None`)
        // included, so it stays distinct from "unset" and survives a switch
        // away and back. Only touch the live level when the target is the
        // active model: editing an inactive row must never disturb what runs.
        if is_active {
            // This *is* a change to what the next turn will run, so it goes
            // through the same commit as every other setter and is announced
            // like one. It used to mutate the live level silently, leaving a
            // `stateEvents` client permanently behind. An inactive model's
            // preference changes nothing about the next turn, so it stays
            // unannounced. Committing before the override is recorded keeps a
            // refused commit from leaving a preference behind.
            self.commit_config("effort", |rc| rc.effort = new_effort)?;
        }
        self.effort_overrides
            .lock()
            .expect("effort poisoned")
            .insert(canonical.clone(), new_effort);

        // At a boundary the level is unchanged; say so rather than imply a move.
        let note = if new_idx == cur_idx {
            let bound = if step < 0 { "lowest" } else { "highest" };
            format!("already at the {bound} level")
        } else {
            String::new()
        };
        Self::effort_result(
            true,
            &canonical,
            &provider,
            new_effort.map(|e| e.as_str().to_owned()),
            is_active,
            note,
        )
    }


    async fn model_reasoning_capability(&self) -> Result<Value, RpcError> {
        // Copilot models advertise their levels at runtime, so consult the
        // model listing; Anthropic models resolve from static rules on the id.
        // The `indeterminate` flag distinguishes "unknown yet" (Copilot before
        // the model list arrives) from a positive "unsupported", so the picker
        // can refuse to lie in either direction.
        //
        // The whole answer — model, provider, capability and current level —
        // is built from ONE captured record. Re-reading the level after the
        // provider round-trip used to report the level of whichever model a
        // setter had just switched to, under the name of the model this call
        // asked about.
        let captured = self.capture_runtime().config;
        let model = captured.model.clone();
        let provider_id = captured
            .provider_id
            .clone()
            .unwrap_or_else(|| crate::settings::FALLBACK_PROVIDER.to_owned());
        // Only a client that still belongs to the captured provider can speak
        // for it; anything else would answer about a different provider.
        let client = self
            .client
            .lock()
            .await
            .clone()
            .filter(|c| c.provider_id().eq_ignore_ascii_case(&provider_id));
        let (capability, indeterminate) = Self::resolve_capability_for(client.as_ref(), &model).await;
        Ok(json!({
            "supported": capability.supported,
            // The C# sends an empty list when unsupported rather than the
            // levels it would otherwise have reported.
            "levels": if capability.supported { capability.levels } else { Vec::new() },
            "supportsAuto": capability.supports_auto,
            "current": captured.effort.map(|e| e.as_str().to_owned()),
            "indeterminate": indeterminate,
            // Canonical identity so the picker persists a per-model preference
            // under the same (provider, model) key the engine reads back, never
            // a display name.
            "model": model,
            "providerId": provider_id,
        }))
    }

    async fn session_schedule_list(&self) -> Result<Value, RpcError> {
        let schedules: Vec<ScheduledTaskResponse> =
            self.schedule_store.items().iter().map(scheduled_task_to_wire).collect();
        serde_json::to_value(&schedules)
            .map(|v| json!({ "schedules": v }))
            .map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_schedule_create(&self, p: ScheduleCreateParams) -> Result<Value, RpcError> {
        let rc = [p.every.is_some(), p.at.is_some(), p.cron.is_some()]
            .iter()
            .filter(|&&b| b)
            .count();
        if rc != 1 {
            return Err(RpcError::invalid_params(
                "exactly one of every, at, or cron must be provided",
            ));
        }
        let tz = p.time_zone.clone().unwrap_or_else(|| "UTC".into());
        let now = Utc::now();

        let draft = if let Some(ref every) = p.every {
            let interval = parse_duration(Some(every)).ok_or_else(|| {
                RpcError::invalid_params(format!("invalid 'every' duration: {every:?}"))
            })?;
            let next_run_utc = now + chrono::Duration::seconds(interval.as_secs() as i64);
            ScheduleDefinitionDraft {
                name: p.name.clone(),
                kind: ScheduleKind::Interval,
                prompt: p.prompt.clone(),
                interval: Some(interval),
                at_utc: None,
                cron: None,
                time_zone_id: tz,
                next_run_utc,
            }
        } else if let Some(ref at) = p.at {
            let at_utc = chrono::DateTime::parse_from_rfc3339(at)
                .map(|d| d.with_timezone(&Utc))
                .map_err(|e| RpcError::invalid_params(format!("invalid 'at' datetime: {e}")))?;
            ScheduleDefinitionDraft {
                name: p.name.clone(),
                kind: ScheduleKind::At,
                prompt: p.prompt.clone(),
                interval: None,
                at_utc: Some(at_utc),
                cron: None,
                time_zone_id: tz,
                next_run_utc: at_utc,
            }
        } else {
            let cron_expr = p.cron.clone().unwrap();
            // next_run_utc = now; the runtime will advance to the proper boundary.
            ScheduleDefinitionDraft {
                name: p.name.clone(),
                kind: ScheduleKind::Cron,
                prompt: p.prompt.clone(),
                interval: None,
                at_utc: None,
                cron: Some(cron_expr),
                time_zone_id: tz,
                next_run_utc: now,
            }
        };

        let task = self.schedule_store.add(draft, now);
        let resp = scheduled_task_to_wire(&task);
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_schedule_delete(&self, p: ScheduleDeleteParams) -> Result<Value, RpcError> {
        let id = p.id.ok_or_else(|| RpcError::invalid_params("missing id"))?;
        if self.schedule_store.remove(&id) {
            Ok(json!({ "ok": true }))
        } else {
            Err(RpcError::invalid_params(format!("schedule not found: {id}")))
        }
    }

    async fn hooks_list(&self) -> Result<Value, RpcError> {
        let hooks: Vec<Value> = self
            .user_hooks
            .iter()
            .enumerate()
            .map(|(i, h)| {
                let mut obj = serde_json::to_value(h)
                    .unwrap_or_else(|_| Value::Object(Default::default()));
                if let Some(m) = obj.as_object_mut() {
                    m.insert("index".into(), i.into());
                    // Expose the loader-stamped scope (serde(skip) means it's
                    // not in the serialized form — add it explicitly here).
                    let scope_str = match h.scope {
                        HookScope::User => "user",
                        HookScope::Project => "project",
                    };
                    m.insert("scope".into(), scope_str.into());
                    let trusted = match h.scope {
                        HookScope::User => h.plugin_origin.is_none(),
                        HookScope::Project => self.hook_trust_store.is_trusted(
                            &self.working_dir,
                            &HookContentHash::compute(h),
                        ),
                    };
                    m.insert("trusted".into(), trusted.into());
                }
                obj
            })
            .collect();
        Ok(json!({ "hooks": hooks }))
    }

    async fn hooks_info(&self, p: HooksInfoParams) -> Result<Value, RpcError> {
        let index = p.index as usize;
        let h = self
            .user_hooks
            .get(index)
            .ok_or_else(|| RpcError::invalid_params("hook index out of range"))?;
        let mut obj = serde_json::to_value(h)
            .unwrap_or_else(|_| Value::Object(Default::default()));
        if let Some(m) = obj.as_object_mut() {
            m.insert("index".into(), index.into());
            let scope_str = match h.scope {
                HookScope::User => "user",
                HookScope::Project => "project",
            };
            m.insert("scope".into(), scope_str.into());
            let trusted = match h.scope {
                HookScope::User => h.plugin_origin.is_none(),
                HookScope::Project => self.hook_trust_store.is_trusted(
                    &self.working_dir,
                    &HookContentHash::compute(h),
                ),
            };
            m.insert("trusted".into(), trusted.into());
        }
        Ok(obj)
    }

    async fn hooks_trust(&self, p: HooksTrustParams) -> Result<Value, RpcError> {
        let pp = p.project_path.ok_or_else(|| RpcError::invalid_params("missing projectPath"))?;
        let hh = p.hook_hash.ok_or_else(|| RpcError::invalid_params("missing hookHash"))?;
        self.hook_trust_store.trust(&pp, &hh);
        serde_json::to_value(coda_proto::responses::HooksTrustResult {
            ok: true, project_path: pp, hook_hash: hh,
        }).map_err(|error| RpcError::internal(error.to_string()))
    }

    async fn skills_list(&self) -> Result<Value, RpcError> {
        let found = crate::skills::discover(std::path::Path::new(&self.working_dir));
        serde_json::to_value(serde_json::json!({ "skills": found }))
            .map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn plugins_list(&self) -> Result<Value, RpcError> {
        Ok(json!({ "plugins": [] }))
    }

    async fn session_compact(&self, p: CompactParams) -> Result<Value, RpcError> {
        // Require credentials (same guard as session/prompt), and wire them
        // through the same announced commit — a client wired here used to
        // leave `config.next.providerId` reporting "not wired" for the rest
        // of the session.
        self.wire_client_from_env_if_missing().await?;
        let client = {
            let g = self.client.lock().await;
            g.clone().ok_or_else(|| self.no_client_error())?
        };

        // Claim the turn slot so a concurrent prompt is blocked; the guard
        // releases it on every exit path, including cancellation. C6: an
        // explicit compaction is exactly as busy as a prompt, so it opens a
        // public turn too — in the `compacting` phase — rather than being an
        // invisible busy period.
        let compaction_turn_id = uuid::Uuid::new_v4().to_string();
        let _turn = self
            .try_claim_turn(&compaction_turn_id, "", TurnKind::Compaction)
            .map_err(|refused| {
                refused.into_error("another prompt is already in progress; busy")
            })?;

        let result = self.run_compact_inner(client, p).await;
        self.engine_state.end_turn(TurnEnd {
            turn_id: compaction_turn_id,
            stop_reason: TurnKind::Compaction.stop_reason(),
            interrupted: matches!(&result, Err(e) if e.message == "cancelled"),
            // S3: only a classification — a compaction failure can carry
            // provider text in its RPC message, which is never retained.
            error: result.as_ref().err().map(|e| safe_engine_error(e.code)),
            history_length: Some(self.session.history.lock().expect("history poisoned").len() as i64),
            wire: None,
        });
        result
    }

    async fn session_fork(&self, _p: ForkParams) -> Result<Value, RpcError> {
        // I2: forking rewrites the whole conversation, so it must own the
        // single-flight slot for its entire duration. Reading `turn_active`
        // and *then* awaiting the fork left a window in which a new prompt
        // claimed the slot and raced this history replacement.
        let turn_id = uuid::Uuid::new_v4().to_string();
        let _turn = self
            .try_claim_turn(&turn_id, "", TurnKind::Maintenance)
            .map_err(|refused| refused.into_error("cannot fork while a turn is in progress"))?;
        let result = self.run_fork_inner().await;
        self.engine_state.end_turn(TurnEnd {
            turn_id,
            stop_reason: TurnKind::Maintenance.stop_reason(),
            interrupted: false,
            error: result.as_ref().err().map(|e| safe_engine_error(e.code)),
            // `session_changed` already re-fenced `historyLength`.
            history_length: None,
            wire: None,
        });
        result
    }

    async fn session_rewind(&self, p: RewindParams) -> Result<Value, RpcError> {
        // I2: same reasoning as `session_fork`.
        let turn_id = uuid::Uuid::new_v4().to_string();
        let _turn = self
            .try_claim_turn(&turn_id, "", TurnKind::Maintenance)
            .map_err(|refused| refused.into_error("cannot rewind while a turn is in progress"))?;
        let result = self.run_rewind_inner(p).await;
        self.engine_state.end_turn(TurnEnd {
            turn_id,
            stop_reason: TurnKind::Maintenance.stop_reason(),
            interrupted: false,
            error: result.as_ref().err().map(|e| safe_engine_error(e.code)),
            history_length: None,
            wire: None,
        });
        result
    }

    // ── Stage D ──────────────────────────────────────────────────────────

    fn is_initialized(&self) -> bool {
        self.engine_state.is_initialized()
    }

    /// Rich history for the live session or a validated saved transcript.
    ///
    /// The live read takes HISTORY and then STATE — the same order (and the
    /// same instant) the turn commit uses — so the committed prefix, the
    /// `historyLength` fence, the `historyEpoch` and `liveEntries` in one
    /// response cannot disagree. The projection itself happens outside both
    /// locks.
    async fn session_get_history(&self, p: GetHistoryParams) -> Result<Value, RpcError> {
        let current = self.active_session_id();
        let requested = p.session_id.clone().filter(|id| !id.is_empty());

        let result = match requested {
            Some(id) if id != current => {
                // A saved transcript: only a validated id, only this
                // workspace's store. The engine never accepts a path, and
                // never parses `.coda/sessions/*.json` outside the store.
                if !coda_agent::session::session_id_is_valid(&id) {
                    return Err(RpcError::invalid_params(
                        "sessionId is not a valid session identifier",
                    ));
                }
                let store = SessionTranscriptStore::new(&self.working_dir);
                let Some(messages) = store.load(&id).await else {
                    return Err(RpcError::session_not_found());
                };
                crate::history::build_saved_result(
                    &p,
                    &id,
                    self.engine_state.bus_ref().engine_instance_id(),
                    &messages,
                )?
            }
            _ => {
                // One consistent read, in the same lock order (and at the
                // same instant) the turn commit uses. Only the requested page
                // is copied, so an unbounded conversation is never cloned
                // wholesale to answer one bounded request.
                let (page, view) = {
                    let guard = self.session.history.lock().expect("history poisoned");
                    let view = self.engine_state.history_view();
                    let total = guard.len() as i64;
                    let (start, end) = crate::history::page_bounds(&p, total);
                    let messages = guard[start as usize..end as usize].to_vec();
                    (crate::history::CommittedPage { total, start, messages }, view)
                };
                crate::history::build_live_result(&p, &page, &view)?
            }
        };
        serde_json::to_value(&result).map_err(|e| RpcError::internal(e.to_string()))
    }

    /// Saved transcripts in the current workspace, read through
    /// `SessionTranscriptStore` so an external client never parses
    /// `.coda/sessions/*.json` and never sees a filesystem path.
    async fn session_list_sessions(&self, p: ListSessionsParams) -> Result<Value, RpcError> {
        let limit = crate::history::clamp_limit(
            p.limit,
            crate::history::DEFAULT_SESSION_LIMIT,
            crate::history::MAX_SESSION_LIMIT,
        );
        let store = SessionTranscriptStore::new(&self.working_dir);
        let all = store.list();
        let total = all.len() as i64;
        let current = self.active_session_id();
        let sessions: Vec<_> = all
            .iter()
            .take(limit as usize)
            .map(|s| crate::history::project_summary(s, s.id == current))
            .collect();
        let result = coda_proto::history::ListSessionsResult {
            truncated: total > sessions.len() as i64,
            total_known: total,
            sessions,
        };
        serde_json::to_value(&result).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_get_pending_requests(&self) -> Result<Value, RpcError> {
        let requests = self.prompt_channel.registry().list();
        serde_json::to_value(coda_proto::requests::GetPendingRequestsResult {
            requests,
            engine_instance_id: self.engine_state.bus_ref().engine_instance_id().to_owned(),
        }).map_err(|error| RpcError::internal(error.to_string()))
    }

    /// Answer a pending request out of band.
    ///
    /// Shape and kind are validated **before** the entry is consumed, so a
    /// wrong-kind or malformed payload leaves the request outstanding for the
    /// caller to correct, and a duplicate can never grant twice.
    async fn session_resolve_request(&self, p: ResolveRequestParams) -> Result<Value, RpcError> {
        let registry = self.prompt_channel.registry();
        match registry
            .resolve_with(&p.request_id, |kind| crate::prompts::outcome_from_rpc(kind, &p.outcome))
        {
            Ok(outcome) => serde_json::to_value(coda_proto::requests::ResolveRequestResult {
                ok: true,
                state: coda_proto::requests::RequestResolutionState::Resolved,
                request_id: p.request_id,
                outcome: outcome.label().to_owned(),
            }).map_err(|error| RpcError::internal(error.to_string())),
            Err(e) => Err(resolve_error_to_rpc(e, &p.request_id)),
        }
    }

    /// Apply a pending request's fail-closed default and stop waiting.
    ///
    /// This never guesses an answer and never closes the engine's pipes: it
    /// resolves the one request with `deny` / `noAnswer` / `reject` and leaves
    /// every other request, and the connection, untouched.
    async fn session_cancel_request(&self, p: CancelRequestParams) -> Result<Value, RpcError> {
        let registry = self.prompt_channel.registry();
        let mut applied_default = String::new();
        let result = registry.resolve_with(&p.request_id, |kind| {
            applied_default = kind.fail_closed_default().to_string();
            Ok(crate::state::requests::RequestOutcome::fail_closed(
                kind,
                coda_tool::NoAnswerReason::Declined,
            ))
        });
        match result {
            Ok(outcome) => serde_json::to_value(coda_proto::requests::CancelRequestResult {
                ok: true,
                request_id: p.request_id,
                applied_default,
                outcome: outcome.label().to_owned(),
                reason: p.reason,
            }).map_err(|error| RpcError::internal(error.to_string())),
            Err(e) => Err(resolve_error_to_rpc(e, &p.request_id)),
        }
    }

    async fn config_describe(&self) -> Result<Value, RpcError> {
        let entries = crate::config_api::describe(&self.config_facts().await);
        serde_json::to_value(&coda_proto::config::ConfigDescribeResult { entries })
            .map_err(|e| RpcError::internal(e.to_string()))
    }

    /// Delegates to the existing validated methods. Never writes a settings
    /// file, never applies a silent default, never accepts a key the catalog
    /// reports as immutable.
    async fn config_set(&self, p: ConfigSetParams) -> Result<Value, RpcError> {
        self.apply_config_set(p).await
    }

    async fn mcp_list(&self) -> Result<Value, RpcError> {
        let result = self.mcp_inventory().await;
        serde_json::to_value(&result).map_err(|e| RpcError::internal(e.to_string()))
    }
}

/// Maps a registry refusal onto a typed JSON-RPC error.
fn resolve_error_to_rpc(e: crate::state::requests::ResolveError, request_id: &str) -> RpcError {
    use crate::state::requests::ResolveError;
    match e {
        ResolveError::InstanceMismatch { expected } => RpcError::instance_changed(format!(
            "request handle `{request_id}` was minted by a different engine process; \
             this engine is instance {expected}. Re-read session/getPendingRequests."
        )),
        ResolveError::MalformedHandle => {
            RpcError::invalid_params(format!("`{request_id}` is not a request handle"))
        }
        ResolveError::Unknown => RpcError::unknown_request(format!(
            "request `{request_id}` is not pending — it was never issued, or it has already \
             been resolved (a duplicate reply never resolves a second time)"
        )),
        ResolveError::KindMismatch { expected } => RpcError::request_kind_mismatch(format!(
            "request `{request_id}` is a `{}` request; the outcome offered is for a \
             different kind and was refused without consuming it",
            expected.as_str()
        )),
        ResolveError::MalformedOutcome { detail } => RpcError::invalid_params(format!(
            "{detail} — request `{request_id}` is still pending"
        )),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ServeHost private helpers
// ─────────────────────────────────────────────────────────────────────────────

impl ServeHost {
    /// Gathers everything `config/describe` states about this engine.
    ///
    /// Reads only what the engine already holds. It never opens a settings
    /// file to dump it, and it never touches a credential store.
    async fn config_facts(&self) -> crate::config_api::ConfigFacts {
        // Every `.await` happens FIRST, so the configuration snapshot below is
        // taken in one lock-held instant and cannot straddle a commit.
        let mcp_server_count = match &self.mcp_manager {
            Some(manager) => Some(manager.connected_status().await.len() as i64),
            None => None,
        };

        let mut output_styles: Vec<coda_proto::config::AllowedValue> =
            coda_agent::BuiltInOutputStyles::all()
                .iter()
                .map(|s| coda_proto::config::AllowedValue::described(s.name, s.description))
                .collect();
        output_styles.extend(
            coda_agent::BuiltInOutputStyles::plugin_styles()
                .into_iter()
                .map(|s| coda_proto::config::AllowedValue::described(s.name, s.description)),
        );

        let goal = self.goal_params.lock().expect("goal poisoned").goal.clone();

        // CONFIG -> STATE with the guard **bound** for the whole tuple, and no
        // `.await` inside: the prompt text, the source derived from it, the
        // model, the single read of effort, the permission mode and the
        // running turn's config all describe the same instant. Read
        // separately, `effort`/`effortIsAuto` could report a level with
        // "automatic", or `null` with "not automatic" — a state the engine is
        // never in, and one `config/describe` used to publish as an
        // *unknown* effort.
        let (runtime, permission_mode, active) = {
            let guard = self.runtime.lock().expect("runtime config poisoned");
            let permission_mode = self.permission_mode.get();
            let active = self.engine_state.active_turn_config();
            let runtime = guard.clone();
            drop(guard);
            (runtime, permission_mode, active)
        };
        let effort = runtime.effort;
        let model = runtime.model.clone();

        // Deliberately *not* `resolve_capability`: that performs a provider
        // `list_models()` round-trip, and `config/describe` is advertised as
        // read-only discovery that is valid before `initialize`. A describe
        // call must not become a network request. The static ladder is used
        // instead, and a model whose ladder is only knowable from a live
        // listing (Copilot) reports `None` — unknown, never "supports
        // nothing". `model/reasoningCapability` remains the method that does
        // ask the provider.
        let provider_for_ladder = runtime
            .provider_id
            .clone()
            .unwrap_or_else(|| crate::settings::FALLBACK_PROVIDER.to_owned());
        let indeterminate = provider_for_ladder.eq_ignore_ascii_case(COPILOT_PROVIDER_ID);
        let capability = resolve_reasoning(&provider_for_ladder, &model, None);

        crate::config_api::ConfigFacts {
            provider_id: runtime.provider_id.clone(),
            model,
            effort: effort.map(|e| e.as_str().to_string()),
            effort_is_auto: effort.is_none(),
            effort_levels: (capability.supported && !indeterminate)
                .then(|| capability.levels.iter().map(|l| l.to_string()).collect()),
            effort_supports_auto: capability.supports_auto,
            permission_mode: wire_permission_mode(permission_mode).to_string(),
            system_prompt: runtime.system_prompt.as_deref().map(str::to_owned),
            goal,
            active_model: active.as_ref().map(|c| c.model.clone()),
            active_effort: active.as_ref().and_then(|c| c.effort.clone()),
            active_permission_mode: active.as_ref().map(|c| c.permission_mode.clone()),
            mcp_enabled: !crate::mcp::mcp_disabled(),
            mcp_server_count,
            output_styles,
        }
    }

    /// `config/set` — delegate to the existing validated method for the key.
    ///
    /// Refuses anything the catalog reports as immutable, with the same
    /// reason, so `describe` and `set` cannot drift apart. `effective` is read
    /// back from the engine afterwards, never echoed from the request.
    async fn apply_config_set(&self, p: ConfigSetParams) -> Result<Value, RpcError> {
        use coda_proto::config::ConfigSetResult;

        let key = p.key.trim().to_string();
        let applied_at = crate::config_api::applies_at(&key);

        if !crate::config_api::MUTABLE_KEYS.contains(&key.as_str()) {
            // Reuse the catalog's own reason rather than writing a second one.
            let reason = crate::config_api::describe(&self.config_facts().await)
                .into_iter()
                .find(|e| e.key == key)
                .and_then(|e| e.reason)
                .unwrap_or_else(|| format!("`{key}` is not a configuration key this engine owns"));
            return serde_json::to_value(&ConfigSetResult {
                ok: false,
                key,
                applied_at,
                effective: None,
                error: Some(reason),
            })
            .map_err(|e| RpcError::internal(e.to_string()));
        }

        let (ok, error) = match key.as_str() {
            "model" => {
                let Some(model) = p.value.as_str() else {
                    return Err(RpcError::invalid_params("`model` must be a string"));
                };
                let r = self.session_set_model(SetModelParams { model: model.to_string() }).await?;
                (r["ok"].as_bool().unwrap_or(false), r["note"].as_str().map(str::to_string))
            }
            "effort" => {
                // `null` and `"auto"` both mean "clear the explicit level" —
                // the same two spellings `session/setEffort` already accepts.
                let effort = match &p.value {
                    Value::Null => None,
                    Value::String(s) if s.eq_ignore_ascii_case("auto") => None,
                    Value::String(s) => Some(s.clone()),
                    _ => return Err(RpcError::invalid_params("`effort` must be a string or null")),
                };
                let r = self
                    .session_set_effort(SetEffortParams {
                        effort,
                        expected_model: None,
                        expected_provider: None,
                    })
                    .await?;
                (r["ok"].as_bool().unwrap_or(false), r["note"].as_str().map(str::to_string))
            }
            "permissionMode" => {
                let Some(mode) = p.value.as_str() else {
                    return Err(RpcError::invalid_params("`permissionMode` must be a string"));
                };
                let r = self
                    .session_set_permission_mode(SetPermissionModeParams { mode: mode.to_string() })
                    .await?;
                let ok = r["ok"].as_bool().unwrap_or(false);
                (
                    ok,
                    (!ok).then(|| {
                        format!("`{mode}` is not a permission mode this engine recognises")
                    }),
                )
            }
            "systemPrompt" => {
                let text = match &p.value {
                    Value::Null => None,
                    Value::String(s) => Some(s.clone()),
                    _ => {
                        return Err(RpcError::invalid_params(
                            "`systemPrompt` must be a string or null",
                        ));
                    }
                };
                let r = self.session_set_system_prompt(SetSystemPromptParams { text }).await?;
                (r["ok"].as_bool().unwrap_or(false), None)
            }
            // Unreachable: guarded by MUTABLE_KEYS above.
            other => return Err(RpcError::invalid_params(format!("unknown config key `{other}`"))),
        };

        // Read back what the engine now actually holds.
        let effective = crate::config_api::describe(&self.config_facts().await)
            .into_iter()
            .find(|e| e.key == key)
            .and_then(|e| e.value);

        serde_json::to_value(&ConfigSetResult { ok, key, applied_at, effective, error })
            .map_err(|e| RpcError::internal(e.to_string()))
    }

    /// The `mcp/list` inventory: the configured file layer plus whatever the
    /// running manager knows. Performs no connection attempt and no
    /// credential-store read.
    async fn mcp_inventory(&self) -> coda_proto::mcp::McpListResult {
        let enabled = !crate::mcp::mcp_disabled();
        let configured = if enabled {
            let (user_mcp, project_mcp) =
                coda_mcp::config::resolve_paths(std::path::Path::new(&self.working_dir));
            let project_mcp = if crate::mcp::project_mcp_disabled() {
                std::path::PathBuf::new()
            } else {
                project_mcp
            };
            coda_mcp::config::load_all(&user_mcp, &project_mcp)
        } else {
            Vec::new()
        };
        match &self.mcp_manager {
            Some(manager) => {
                let status = manager.connected_status().await;
                crate::mcp_list::build(&configured, Some(&status), enabled)
            }
            None => crate::mcp_list::build(&configured, None, enabled),
        }
    }

    /// Inner implementation of `session/fork` — called with the single-flight
    /// slot already claimed, so no prompt can race the history replacement.
    async fn run_fork_inner(&self) -> Result<Value, RpcError> {
        let history = self.session.history.lock().expect("history poisoned").clone();
        let source_id = self.active_session_id();

        let new_id = fork_session(&self.working_dir, Some(&source_id), &history, None).await;

        // Adopt the new id so future turns write to the forked session.
        *self.current_session_id.lock().expect("session_id poisoned") = new_id.clone();
        // F2: fork used to be silent — bump historyEpoch and announce it, so
        // a client's `sinceIndex` is never valid across the reset invisibly.
        //
        // F3 (review): the announcement happens **while HISTORY is held**
        // (lock order HISTORY -> STATE -> BUS, no `.await` inside), so a
        // concurrent `session/getHistory` can never take HISTORY between the
        // reset and its announcement and be handed a page from the new
        // conversation under the old `historyEpoch`. The length is read from
        // the guard at that instant rather than from the pre-`await` clone.
        {
            let committed = self.session.history.lock().expect("history poisoned");
            self.engine_state.session_changed("fork", new_id.clone(), committed.len() as i64);
        }

        let resp = ForkResponse { ok: true, new_session_id: new_id };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    /// Inner implementation of `session/rewind` — see `run_fork_inner`.
    async fn run_rewind_inner(&self, p: RewindParams) -> Result<Value, RpcError> {
        let n = p.n.unwrap_or(1).max(1) as usize;
        let mut history = self.session.history.lock().expect("history poisoned").clone();
        let removed = rewind_session(&mut history, n);
        let remaining = history.len();

        // Persist the rewound history before it becomes the committed view,
        // so no snapshot can see a fence the transcript does not back.
        let session_id = self.active_session_id();
        let store = SessionTranscriptStore::new(&self.working_dir);
        let _ = store.save(&session_id, &history, None).await;

        // F2: rewind used to be silent. F3 (review): replacement and
        // announcement are one critical section — see `run_fork_inner`.
        {
            let mut committed = self.session.history.lock().expect("history poisoned");
            *committed = history;
            self.engine_state.session_changed("rewind", session_id, committed.len() as i64);
        }

        let resp = RewindResponse { ok: true, removed, remaining };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    /// Inner implementation of the prompt turn — called after the turn slot is claimed
    /// and image validation has passed. `turn_id` was already minted in
    /// `session_prompt`, unconditionally, before any preflight check.
    async fn run_prompt_inner(&self, p: PromptParams, turn_id: String) -> Result<Value, RpcError> {
        // Lazy credential lookup on first use — env var only (fast path).
        // Keyring is checked once at process startup in serve_stdio().
        self.wire_client_from_env_if_missing().await?;

        // Require a wired client.
        let client = {
            let g = self.client.lock().await;
            g.clone().ok_or_else(|| self.no_client_error())?
        };

        // Append user message to a local copy of history.
        let user_msg = build_user_message(&p);
        let mut history = self.session.history.lock().expect("history poisoned").clone();
        if !user_msg.content.is_empty() {
            history.push(user_msg);
        }

        // Per-turn cancel token.
        let cancel = CancellationToken::new();
        *self.current_cancel.lock().expect("cancel poisoned") = Some(cancel.clone());

        // Finding 4: apply any pending interrupt recorded before this turn's
        // token was published. A session/interrupt that arrived in the window
        // between turn-claim and token-publication set this flag rather than
        // cancelling a non-existent token.
        {
            let mut pi = self.pending_interrupt.lock().expect("pending_interrupt poisoned");
            if *pi {
                *pi = false;
                cancel.cancel();
            }
        }

        // Optional goal supervisor.
        let goal = self.build_goal_supervisor(Arc::clone(&client));

        // Initialise session-scoped services lazily (Finding 1: first turn only).
        let services = self.get_or_init_services(Arc::clone(&client)).await;

        // Build the agent loop with all services wired (Finding 1).
        let uq_goal = Arc::clone(&self.user_question) as Arc<dyn UserQuestionPrompt>;
        let uq_tool = Arc::clone(&self.user_question) as Arc<dyn UserQuestion>;
        let pa = Arc::clone(&self.plan_approver) as Arc<dyn PlanApprover>;

        // ONE coherent read of the configuration this turn runs under, taken
        // after every await that precedes the build. Everything below — the
        // model and effort handed to the builder, the system-prompt override
        // and its source, and the `activeConfig` this turn publishes — comes
        // from this record and is never re-read. Reading them again after the
        // builder would let a setter landing in between make the public
        // `activeConfig` describe an agent that was never run.
        let captured = self.capture_runtime();

        let agent = AgentLoopBuilder::new(
            Arc::clone(&client),
            Arc::clone(&self.permission_prompt),
            Arc::clone(&self.tools),
        )
        .with_permission_mode_state(Arc::clone(&self.permission_mode))
        .with_model(captured.config.model.clone())
        .with_working_directory(&self.working_dir)
        .with_effort(captured.config.effort)
        .with_steering(Arc::clone(&self.session.steering))
        .with_user_question(uq_goal)
        .with_tool_user_question(uq_tool)
        .with_plan_approver(pa)
        .with_todos(Arc::clone(&self.todos))
        .with_task_manager(Arc::clone(&self.task_manager))
        .with_schedule_store(Arc::clone(&self.schedule_store))
        .with_lsp_manager(Arc::clone(&self.lsp_manager))
        .with_subagent_factory(Arc::clone(&services.subagent_host) as Arc<dyn SubagentFactory>)
        .with_hook_runner(Arc::clone(&services.hook_runner));

        // Apply the session-only system prompt override the captured record
        // carries. The text is cloned here, outside CONFIG, and only because
        // the builder needs to own it.
        let agent = match captured.config.system_prompt.as_deref() {
            Some(prompt) => agent.with_system_prompt(prompt.to_owned()),
            None => agent,
        };

        let agent = agent.build();

        // Test seam (see the field's documentation): lets a test land a
        // configuration change in the window between building the agent and
        // publishing what this turn is running.
        #[cfg(test)]
        {
            let hook = self.after_agent_build_hook.lock().expect("hook poisoned").clone();
            if let Some(hook) = hook {
                hook(self);
            }
        }

        let turn_ctx = coda_diagnostics::current().map(|ctx| {
            ctx.with_provider_model(
                Some(client.provider_id().to_owned()),
                Some(captured.config.model.clone()),
            )
        });

        // Activity phase machine (Slice 0 / Stage C): the public turn was
        // already opened when the single-flight slot was claimed (C6), so
        // everything above — credential lookup, session services, agent
        // construction — was already visible as `busy`/`preparing`. Now that
        // the client is resolved, replace the placeholder `activeConfig` with
        // the one this turn is actually running under: the very record the
        // agent above was built from, with only the provider filled in from
        // the client that was resolved for it.
        let mut active_config = captured.active_config();
        active_config.provider_id = Some(client.provider_id().to_owned());
        self.engine_state.turn_config_resolved(&turn_id, active_config);

        // Run through TurnSink (captures stop_reason) wrapping StateSink,
        // which now owns both halves of every event: the state transition
        // *and* the legacy wire notification, published as one transaction
        // so a snapshot's cursor can never disagree with its own content.
        let state_sink: Arc<dyn AgentSink> = Arc::new(StateSink::new(Arc::clone(&self.engine_state)));
        let turn_sink = TurnSink::new(state_sink);
        // Provenance for every reverse request this run raises. The agent
        // awaits tool execution — and therefore permission checks, questions,
        // plan approval, goal escalation and *foreground* subagents — inline
        // on this task, so all of them are attributed to this turn. Work the
        // run detaches (a background subagent, a scheduled run) starts a fresh
        // task and is deliberately attributed to nothing: it is not this turn
        // waiting on the operator. See `crate::turn_scope`.
        let run_fut = crate::turn_scope::in_turn(
            turn_id.clone(),
            agent.run(&mut history, turn_sink.as_ref(), goal, cancel),
        );
        let run_result = match &turn_ctx {
            Some(ctx) => coda_diagnostics::scope(ctx.clone(), run_fut).await,
            None => run_fut.await,
        };
        // Seal before announcing completion, closing the enqueue race even
        // while history persistence and final notifications are still running.
        self.session.steering.close_for_turn();
        let stop_reason = turn_sink.take_stop_reason();

        // Clear cancel token.
        *self.current_cancel.lock().expect("cancel poisoned") = None;

        // Map result to wire fields.
        let (ok, interrupted, goal_status, error) = match &run_result {
            Ok(gs) => (true, false, wire_goal_status(gs), None),
            Err(AgentError::Cancelled) => (true, true, None, None),
            Err(e) => (false, false, None, Some(e.to_string())),
        };

        if let Some(ctx) = &turn_ctx {
            match &run_result {
                Ok(_) => ctx.record(coda_diagnostics::Event::TurnEnd { stop_reason: stop_reason.clone() }),
                Err(AgentError::Cancelled) => {
                    ctx.record(coda_diagnostics::Event::TurnEnd { stop_reason: Some("cancelled".into()) })
                }
                Err(AgentError::Llm(llm_err)) => ctx.record(coda_diagnostics::Event::TurnFailed {
                    category: coda_llm::diagnostics::category(llm_err),
                    status: coda_llm::diagnostics::status(llm_err),
                }),
                Err(AgentError::Other(_)) => {
                    ctx.record(coda_diagnostics::Event::TurnFailed { category: "other", status: None })
                }
                // Metadata only: the reason is a classification token, and
                // diagnostics never carry operator-visible content.
                Err(AgentError::Aborted { .. }) => {
                    ctx.record(coda_diagnostics::Event::TurnFailed { category: "aborted", status: None })
                }
            }
        }

        // I1: the transcript is persisted **before** the turn reaches its
        // terminal outcome. Committing history and then awaiting the write
        // left an `.await` inside the window where a snapshot could see the
        // same turn in both committed history and `turn.liveEntries`; and
        // finishing the turn before the write made `lifecycle: ready`
        // reachable while `session/prompt` still answered "busy". While this
        // write runs the turn is honestly still in flight: `busy`, live view
        // intact, `historyLength` unmoved — so nothing is ever double
        // counted. A failed write must not fail the turn — match C#
        // "best-effort seam".
        {
            let session_id = self.active_session_id();
            let store = SessionTranscriptStore::new(&self.working_dir);
            let _ = store.save(&session_id, &history, None).await;
        }

        // The terminal outcome, as one transaction: the committed length, the
        // live reset, the finalisation of any in-flight tool call, the
        // `event/turnComplete` frame and the gated `event/turnEnded` all
        // become visible at the same instant. The history lock is held across
        // it (lock order HISTORY -> STATE -> BUS, no `.await` inside).
        //
        // `event/turnComplete` is still published before the RPC response is
        // sent, preserving the existing ordering guarantee. Availability
        // (`lifecycle: ready`) is published later, by the turn guard, at the
        // moment the single-flight slot is actually released.
        let turn_complete = coda_proto::events::Event::TurnComplete {
            stop_reason: stop_reason.clone(),
            interrupted,
            root_turn_id: None,
            activity_id: None,
        }
        .to_notification();
        {
            let mut committed = self.session.history.lock().expect("history poisoned");
            *committed = history.clone();
            let history_length = committed.len() as i64;
            self.engine_state.end_turn(TurnEnd {
                turn_id,
                stop_reason: stop_reason.clone(),
                interrupted,
                error: run_result.as_ref().err().and_then(safe_turn_error),
                history_length: Some(history_length),
                wire: turn_complete,
            });
        }
        self.steering_observer.set_current_turn(None);

        // Emit TurnComplete BEFORE the response is sent (ordering guarantee).
        // It is published from inside the `end_turn` transaction above so it
        // cannot be observed out of step with the state it announces.

        let resp = PromptResponse { ok, stop_reason, interrupted, goal_status, error };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn run_compact_inner(
        &self,
        client: Arc<dyn LlmClient>,
        p: CompactParams,
    ) -> Result<Value, RpcError> {
        // Snapshot current history without holding the lock.
        let history = self.session.history.lock().expect("history poisoned").clone();
        let messages_before = history.len() as i64;

        if history.is_empty() {
            return serde_json::to_value(&CompactResponse {
                ok: true,
                messages_before: 0,
                messages_after: 0,
                tokens_before: None,
                tokens_after: None,
                error: None,
            })
            .map_err(|e| RpcError::internal(e.to_string()));
        }

        let tokens_before = TokenEstimator::estimate(&history) as i64;

        // Per-operation cancel token — wired so session/interrupt cancels this too.
        let cancel = CancellationToken::new();
        *self.current_cancel.lock().expect("cancel poisoned") = Some(cancel.clone());

        let fork: Arc<dyn ForkedAgent> =
            Arc::new(LlmForkedAgent { client, model: self.current_model() });
        let service = CompactionService::new(fork);

        let (compacted, summary) =
            service.compact(&history, p.instructions.as_deref(), cancel.clone()).await;

        *self.current_cancel.lock().expect("cancel poisoned") = None;

        let was_cancelled = cancel.is_cancelled();

        if was_cancelled {
            // Emit the cancellation event; history is unchanged.
            self.sink.emit(AgentEvent::CompactionCancelled {
                hook_command: String::new(),
                trigger: "cancelled".into(),
            });
            return Err(RpcError::cancelled());
        }

        match summary {
            Some(_) => {
                // Compaction succeeded — atomically replace history.
                let tokens_after = TokenEstimator::estimate(&compacted) as i64;
                let session_id = self.active_session_id();
                // F2: compact used to replace history wholesale, silently.
                // F3 (review): replacement and announcement are one critical
                // section under HISTORY — see `run_fork_inner`.
                let messages_after = {
                    let mut committed = self.session.history.lock().expect("history poisoned");
                    *committed = compacted;
                    let after = committed.len() as i64;
                    self.engine_state.session_changed("compact", session_id, after);
                    after
                };
                serde_json::to_value(&CompactResponse {
                    ok: true,
                    messages_before,
                    messages_after,
                    tokens_before: Some(tokens_before),
                    tokens_after: Some(tokens_after),
                    error: None,
                })
                .map_err(|e| RpcError::internal(e.to_string()))
            }
            None => {
                // Summariser returned empty — original history preserved.
                serde_json::to_value(&CompactResponse {
                    ok: true,
                    messages_before,
                    messages_after: messages_before,
                    tokens_before: Some(tokens_before),
                    tokens_after: None,
                    error: Some(
                        "summariser returned empty response; history unchanged".into(),
                    ),
                })
                .map_err(|e| RpcError::internal(e.to_string()))
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Credential helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Builds the startup client using the *shared* provider selector.
///
/// An explicit `api_key` short-circuits everything: the caller named the
/// credential. Otherwise the decision is
/// [`coda_auth::service::select_provider`] — the same function the auth
/// service, `coda auth status` and the TUI use — over three facts: what is
/// stored, what `defaultProvider` says, and whether `ANTHROPIC_API_KEY` is
/// exported. That is what makes the engine and the CLI agree about which
/// account this machine is signed in to.
///
/// The diagnostic is `Some` whenever the engine *could* have connected but did
/// not: a saved choice whose credential is missing, several stored
/// credentials, an unreadable store, a provider whose client failed to build.
/// It is `None` for the two honest silences — a clean success, and a profile
/// with nothing configured and nothing available, which starts client-less for
/// discovery rather than inventing an authenticated fallback.
pub(crate) async fn try_build_client_with_diagnostic(
    api_key: Option<&str>,
) -> (Option<Arc<dyn LlmClient>>, Option<String>) {
    if let Some(key) = api_key {
        if !key.trim().is_empty() {
            return (build_anthropic(key), None);
        }
    }

    let context = match ProviderContext::from_env() {
        Ok(context) => context,
        Err(e) => {
            // A profile that cannot be opened is emphatically not a logged-out
            // user: saying so invites a login that overwrites what is there.
            let diagnostic = sanitize_auth_error(&e);
            eprintln!("coda: credential storage unavailable: {diagnostic}");
            return (None, Some(diagnostic));
        }
    };

    let selection = match context.select(None).await {
        Ok(selection) => selection,
        // Nothing configured, nothing available: start client-less.
        Err(SelectionError::NoCredentials) => return (None, None),
        Err(error) => {
            let diagnostic = error.to_string();
            eprintln!("coda: no provider selected: {diagnostic}");
            return (None, Some(diagnostic));
        }
    };

    match context.build_client(selection, None, None).await {
        Ok(client) => (Some(client), None),
        Err(StartupError(message)) => {
            eprintln!("coda: {message}");
            (None, Some(message))
        }
    }
}

/// Everything the engine needs to answer "which account is this machine signed
/// in as, and how do I talk to it?" — for **one profile**.
///
/// Credentials, the settings that carry the saved choice and the Copilot
/// tenant, and the environment overrides all belong together. Reading the
/// store from an injected profile while resolving the tenant from the ambient
/// process is how a test reaches the developer's real credentials, and how a
/// production path signs a user in to one place and then routes their requests
/// to another.
pub(crate) struct ProviderContext {
    storage: Arc<AuthStorage>,
    settings_path: PathBuf,
    env: Arc<dyn Fn(&str) -> Option<String> + Send + Sync>,
}

impl std::fmt::Debug for ProviderContext {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ProviderContext")
            .field("settings_path", &self.settings_path)
            .field("storage", &self.storage)
            .finish_non_exhaustive()
    }
}

impl ProviderContext {
    /// The context this process runs under: the profile from `CODA_HOME`, its
    /// settings file, and the real environment.
    pub(crate) fn from_env() -> Result<Self, coda_auth::AuthError> {
        Self::for_profile(&Profile::from_env())
    }

    /// The context for an explicit profile — the seam tests use to stay off
    /// the developer's real credentials *and* their real settings.
    pub(crate) fn for_profile(profile: &Profile) -> Result<Self, coda_auth::AuthError> {
        let storage = credential_storage_for(profile)?;
        Ok(Self {
            storage,
            settings_path: profile.root().join(".coda").join("settings.json"),
            env: Arc::new(|key: &str| std::env::var(key).ok().filter(|v| !v.trim().is_empty())),
        })
    }

    /// Replace the environment lookup (tests, and hosts that already resolved
    /// their overrides).
    #[cfg_attr(not(test), allow(dead_code))]
    pub(crate) fn with_env(
        mut self,
        env: impl Fn(&str) -> Option<String> + Send + Sync + 'static,
    ) -> Self {
        self.env = Arc::new(env);
        self
    }

    #[cfg_attr(not(test), allow(dead_code))]
    pub(crate) fn storage(&self) -> &Arc<AuthStorage> {
        &self.storage
    }

    /// The ambient `ANTHROPIC_API_KEY` for this context.
    ///
    /// Normalised with [`coda_auth::provider::api_key::normalize_key`] — the
    /// same function the login's pre-commit probe validates with — so the key
    /// this engine puts in the `x-api-key` header is byte-for-byte the key the
    /// login proved. A padded variable checked one way and spent another is a
    /// "connected" report about a request that never authenticates.
    fn ambient_api_key(&self) -> Option<String> {
        (self.env)(coda_auth::provider::api_key::ENV_VAR)
            .map(|key| coda_auth::provider::api_key::normalize_key(&key).to_owned())
            .filter(|key| !key.is_empty())
    }

    /// Where this context sends Anthropic **API-key** requests.
    ///
    /// One resolver, this context's environment: the engine and
    /// `coda auth status` cannot disagree about the host, and a refused
    /// override is a startup error rather than a silent redirect to
    /// Anthropic's own host.
    ///
    /// The message names the source that actually failed: blaming
    /// `ANTHROPIC_BASE_URL` for a bad `--endpoint` sends a user to look at a
    /// variable they never set.
    fn anthropic_endpoint(
        &self,
        explicit: Option<&str>,
    ) -> Result<coda_auth::service::AnthropicEndpoint, StartupError> {
        let env = Arc::clone(&self.env);
        let has_explicit = explicit.map(str::trim).is_some_and(|value| !value.is_empty());
        coda_auth::service::endpoint::resolve(explicit, move |name| env(name)).map_err(|reason| {
            let source = if has_explicit {
                "the configured --endpoint"
            } else {
                coda_auth::service::ANTHROPIC_BASE_URL_ENV
            };
            StartupError(format!("invalid Anthropic endpoint from {source}: {reason}"))
        })
    }

    /// Resolve the provider, failing closed.
    ///
    /// Uses [`coda_auth::service::select_in_context`] — the same function the
    /// auth service uses — over the same three facts, so the engine and
    /// `coda auth status` cannot disagree. In particular an unreadable
    /// credential or settings file is `Unavailable`, never "not signed in".
    pub(crate) async fn select(
        &self,
        explicit: Option<&str>,
    ) -> Result<Selection, SelectionError> {
        let providers = coda_auth::service::read_provider_states(&self.storage.profile).await;
        let entries = coda_auth::service::selection_entries(&providers);
        let saved = match crate::settings::saved_default_provider_at(&self.settings_path) {
            Ok(saved) => Ok(saved),
            Err(error) => Err(coda_auth::AuthFailure::classify(
                &coda_auth::AuthError::Store(error.to_string()),
            )),
        };
        coda_auth::service::select_in_context(SelectionContext {
            explicit,
            saved_default: saved.as_ref().map(|saved| saved.as_deref()).map_err(|e| *e),
            entries: &entries,
            ambient_api_key: self.ambient_api_key().is_some(),
        })
    }

    /// Build the client for an already-resolved selection, from *this*
    /// profile's credentials and *this* profile's tenant configuration.
    ///
    /// `endpoint` is the caller's *explicit* Anthropic endpoint, if it has one.
    /// It is resolved here — through the one shared resolver, against *this*
    /// context's environment — so no console-key path can reach a host the
    /// resolver did not approve, and a refused `ANTHROPIC_BASE_URL` fails
    /// closed instead of falling back to Anthropic's own host. Passing a value
    /// this engine already resolved is harmless: an explicit input still wins,
    /// and re-validating an approved URL yields the same URL.
    pub(crate) async fn build_client(
        &self,
        selection: Selection,
        api_key: Option<&str>,
        endpoint: Option<&str>,
    ) -> Result<Arc<dyn LlmClient>, StartupError> {
        match selection.identity {
            ProviderIdentity::AnthropicApiKey => {
                let base = self.anthropic_endpoint(endpoint)?;
                let base = Some(base.base_url());
                // Order: an explicitly supplied key, then the stored credential
                // (which a `coda auth login` wrote), then the ambient variable.
                // All three are the *same* identity, so none of them is a
                // fallback to a different account.
                if let Some(key) = api_key.map(str::trim).filter(|k| !k.is_empty()) {
                    return build_anthropic_at(key, base).ok_or_else(|| {
                        StartupError("failed to construct the Anthropic client".into())
                    });
                }
                if selection.origin == CredentialOrigin::Stored {
                    let (client, diagnostic) =
                        build_anthropic_from_storage(&self.storage, base).await;
                    return client.ok_or_else(|| anthropic_startup_error(diagnostic.as_deref()));
                }
                let key = self.ambient_api_key().ok_or_else(|| {
                    StartupError(
                        "provider 'anthropic' requested but no API key is available \
                         (pass --api-key, set ANTHROPIC_API_KEY, or sign in)"
                            .into(),
                    )
                })?;
                build_anthropic_at(&key, base)
                    .ok_or_else(|| StartupError("failed to construct the Anthropic client".into()))
            }
            ProviderIdentity::GithubCopilot => {
                // One snapshot: the endpoints *and* the credential they serve
                // are read under the same commit section, so a login landing
                // between them cannot pair a new token with old endpoints.
                let (cell, auth_config) = self.bind_copilot_context().await?;
                let (client, diagnostic) =
                    build_copilot_with_context(&self.storage, cell, auth_config).await;
                if let Some(message) = &diagnostic {
                    eprintln!("coda: Copilot credential error at startup: {message}");
                }
                client.ok_or_else(|| copilot_startup_error(diagnostic.as_deref()))
            }
            ProviderIdentity::ClaudeAi => {
                let (client, diagnostic) = build_claude_from_storage(&self.storage).await;
                if let Some(message) = &diagnostic {
                    eprintln!("coda: Claude credential error at startup: {message}");
                }
                client.ok_or_else(|| claude_startup_error(diagnostic.as_deref()))
            }
        }
    }

    /// The Copilot endpoints for this context, with the deployment they
    /// actually contact.
    ///
    /// An invalid or unreadable configuration is an error, never the public
    /// defaults: signing an enterprise user's requests to github.com is worse
    /// than refusing to start.
    ///
    /// Callers on the production path must take it through
    /// [`Self::bind_copilot_context`], which reads it in the same section as
    /// the credential.
    fn copilot_deployment(&self) -> Result<ResolvedCopilotConfig, StartupError> {
        let env = Arc::clone(&self.env);
        crate::settings::resolve_copilot_deployment_from(Some(&self.settings_path), move |key| {
            env(key)
        })
        .map_err(|error| {
            let diagnostic = match &error {
                crate::settings::CopilotConfigError::Endpoint(inner) => sanitize_auth_error(inner),
                other => other.to_string(),
            };
            eprintln!("coda: invalid Copilot configuration: {diagnostic}");
            StartupError(format!("invalid Copilot configuration: {diagnostic}"))
        })
    }

    /// Resolves the Copilot endpoints and reads the credential they serve as
    /// **one snapshot**, inside the profile's commit section.
    ///
    /// Acquisition order, and why it is this way:
    ///
    /// 1. take `AUTH_COMMIT_KEY` — the section every writer takes;
    /// 2. resolve the configuration (settings file + this context's
    ///    environment): local reads only, no network;
    /// 3. read the raw stored credential with the non-migrating read and
    ///    validate the provider it is filed under;
    /// 4. bind the two together into the context cell;
    /// 5. **release** the section — everything after this (building the
    ///    manager, the first `get_credential`, any refresh) happens outside
    ///    it, so no network call is ever made under the lock and nothing
    ///    nests a second acquisition.
    ///
    /// Resolving the configuration *before* step 1 was the gap: a login
    /// committing in between produced old endpoints paired with the new
    /// account's token.
    async fn bind_copilot_context(
        &self,
    ) -> Result<(Arc<CopilotContextCell>, AuthCopilotConfig), StartupError> {
        use coda_auth::coordination::AUTH_COMMIT_KEY;

        let section = self
            .storage
            .coordinator
            .begin(AUTH_COMMIT_KEY)
            .await
            .map_err(|error| {
                StartupError(format!(
                    "credential storage unavailable: {}",
                    sanitize_auth_error(&error)
                ))
            })?;

        let resolved = self.copilot_deployment()?;
        let credential = read_stored_copilot_credential(&self.storage).await.map_err(|error| {
            StartupError(format!(
                "GitHub Copilot credential error: {}",
                sanitize_auth_error(&error)
            ))
        })?;
        let cell = CopilotContextCell::initial(
            resolved.config.clone(),
            resolved.deployment,
            credential.as_ref(),
        );
        drop(section);
        Ok((cell, resolved.config))
    }

    /// The endpoints alone, for callers that do not need the deployment.
    #[cfg_attr(not(test), allow(dead_code))]
    fn copilot_config(&self) -> Result<AuthCopilotConfig, StartupError> {
        Ok(self.copilot_deployment()?.config)
    }
}

/// Builds an Anthropic client for an explicit console key.
///
/// The raw environment lookup that used to sit beside this
/// (`try_build_from_env`) is deliberately gone: every path that decides
/// *which account* to connect to now goes through
/// [`ProviderContext::select`], so a chosen provider whose credential is
/// missing can no longer be answered by whatever key happens to be exported.
pub(crate) fn build_anthropic(key: &str) -> Option<Arc<dyn LlmClient>> {
    AnthropicClient::new(AnthropicConfig::api_key(key))
        .ok()
        .map(|c| Arc::new(c) as Arc<dyn LlmClient>)
}

/// Builds an Anthropic API-key client at an **already-resolved** base URL when
/// one is given, otherwise at the client's default host.
///
/// Deliberately not a resolver: every caller reaches it through
/// [`ProviderContext::build_client`] or through a value
/// [`StartupOptions::api_key_endpoint`] already validated, so there is
/// exactly one place that decides which host an API key may be sent to
/// (Finding I1, extended to `ANTHROPIC_BASE_URL`).
pub(crate) fn build_anthropic_at(key: &str, endpoint: Option<&str>) -> Option<Arc<dyn LlmClient>> {
    let mut config = AnthropicConfig::api_key(key);
    if let Some(url) = endpoint.map(str::trim).filter(|u| !u.is_empty()) {
        config = config.with_base_url(url.to_owned());
    }
    AnthropicClient::new(config)
        .ok()
        .map(|c| Arc::new(c) as Arc<dyn LlmClient>)
}

/// Public OAuth client id for Anthropic (Claude.ai) subscription auth. This is
/// a well-known public identifier, not a secret. Shared with the auth service
/// so a login and the engine that follows it use the same client id.
const CLAUDE_AI_CLIENT_ID: &str = coda_auth::service::CLAUDE_AI_CLIENT_ID;

/// Produces a safe, single-line summary of an auth error.
///
/// Used wherever a credential failure reaches the user — the Copilot and
/// Claude client paths, MCP secret resolution — so no path invents its own
/// wording or leaks server-derived text.
///
/// The classification itself lives in `coda_auth::AuthFailure`, which is the
/// single closed classifier shared with the login flows: it matches every
/// `AuthError` variant explicitly, so a new variant with server-derived
/// content is a compile-time error there rather than a silent leak here.
/// Callers add provider-specific context (“GitHub Copilot: …”) around this
/// message; they never re-derive it.
pub(crate) fn sanitize_auth_error(e: &coda_auth::AuthError) -> String {
    coda_auth::AuthFailure::classify(e).to_string()
}

/// Selects the LLM client for an explicitly-requested provider (Finding C2).
///
/// This *fails closed*: if the requested provider's credential is unavailable
/// it returns a [`StartupError`] rather than probing or silently substituting a
/// different provider (never "use Copilot for anthropic"). `api_key`/`endpoint`
/// apply only to the `anthropic` (console key) provider.
///
/// The provider name and the credential lookup both go through the shared
/// selector, so `--provider anthropic` now also finds a key stored by
/// `coda auth login` — the same account, a different place to keep it — while
/// still refusing to answer with a different account.
pub(crate) async fn build_client_for_provider(
    provider: &str,
    api_key: Option<&str>,
    endpoint: Option<&str>,
) -> Result<Arc<dyn LlmClient>, StartupError> {
    let context = ProviderContext::from_env().map_err(|error| {
        StartupError(format!("credential storage unavailable: {}", sanitize_auth_error(&error)))
    })?;
    build_client_for_provider_with(&context, provider, api_key, endpoint).await
}

/// [`build_client_for_provider`] against an explicit context — the seam tests
/// use to stay off the developer's real credentials and settings.
pub(crate) async fn build_client_for_provider_with(
    context: &ProviderContext,
    provider: &str,
    api_key: Option<&str>,
    endpoint: Option<&str>,
) -> Result<Arc<dyn LlmClient>, StartupError> {
    // An explicit key names the credential, so the anthropic identity is
    // satisfied by it even with nothing stored and nothing exported.
    let explicit_key = api_key.map(str::trim).filter(|key| !key.is_empty());
    let selection = match context.select(Some(provider)).await {
        Ok(selection) => selection,
        Err(SelectionError::NeedsLogin { identity, .. })
            if identity == ProviderIdentity::AnthropicApiKey && explicit_key.is_some() =>
        {
            Selection {
                identity,
                source: SelectionSource::Explicit,
                origin: CredentialOrigin::Environment,
            }
        }
        Err(error) => return Err(StartupError(error.to_string())),
    };
    context.build_client(selection, explicit_key, endpoint).await
}

/// The startup error for a missing or unreadable Anthropic API key.
fn anthropic_startup_error(diagnostic: Option<&str>) -> StartupError {
    match diagnostic {
        Some(message) => StartupError(format!("Anthropic API key error: {message}")),
        None => StartupError(
            "provider 'anthropic' requested but no API key is available \
             (pass --api-key, set ANTHROPIC_API_KEY, or sign in)"
                .into(),
        ),
    }
}

fn copilot_startup_error(diagnostic: Option<&str>) -> StartupError {
    match diagnostic {
        Some(message) => StartupError(format!("GitHub Copilot credential error: {message}")),
        None => StartupError(
            "No GitHub Copilot credential found. Check your saved credentials and provider configuration."
                .into(),
        ),
    }
}

/// The startup error for Claude.ai, keeping "could not read the credential"
/// distinct from "there is no credential".
///
/// Telling a user with an unreadable credential to sign in hides the fault and
/// invites a fresh login that overwrites what is still there.
fn claude_startup_error(diagnostic: Option<&str>) -> StartupError {
    match diagnostic {
        Some(message) => StartupError(format!("Claude.ai credential error: {message}")),
        None => StartupError(
            "provider 'claude-ai' requested but no Claude.ai credential is available \
             (sign in first)"
                .into(),
        ),
    }
}

/// The credential storage for the profile this engine runs under.
///
/// One factory, one answer. Every credential reader in the engine — the
/// Copilot client, the Claude client, MCP secret resolution — goes through
/// here, so they cannot disagree about where the credentials are.
///
/// The backend is decided by the platform and the profile, never by which
/// credential files happen to exist: an earlier version chose DPAPI only when
/// a Copilot credential was present, so a Claude-only user was silently routed
/// to a different backend from the one their credential lived in.
///
/// `pub(crate)` so that `mcp.rs` can share the same storage.
pub(crate) fn credential_storage() -> Result<Arc<AuthStorage>, coda_auth::AuthError> {
    credential_storage_for(&Profile::from_env())
}

/// [`credential_storage`] for an explicit profile — the seam tests use to stay
/// off the developer's real credentials.
pub(crate) fn credential_storage_for(
    profile: &Profile,
) -> Result<Arc<AuthStorage>, coda_auth::AuthError> {
    coda_auth::store::open_profile_storage(profile).map(Arc::new)
}

/// Builds the Copilot client for a resolved profile from an **already
/// immutable** configuration.
///
/// This resolves nothing: it reads the credential under the profile's commit
/// section and binds it to the configuration it was given. That is only safe
/// when the configuration cannot change behind the caller's back — a test with
/// a fixed config, or an embedder that has pinned one. The production path
/// must use [`ProviderContext::bind_copilot_context`], which reads the
/// configuration *and* the credential inside the same section; resolving the
/// configuration first leaves a window in which a login pairs a new token with
/// old endpoints.
#[cfg_attr(not(test), allow(dead_code))]
pub(crate) async fn build_copilot_from_storage(
    storage: &AuthStorage,
    auth_config: AuthCopilotConfig,
    deployment: CopilotDeployment,
) -> (Option<Arc<dyn LlmClient>>, Option<String>) {
    let cell = match bind_copilot_context(storage, auth_config.clone(), deployment).await {
        Ok(cell) => cell,
        Err(error) => return (None, Some(sanitize_auth_error(&error))),
    };
    build_copilot_with_context(storage, cell, auth_config).await
}

/// Builds the Copilot client from a context that has already been bound.
///
/// Takes no section: the caller released it before calling, and everything
/// here (the first credential read, any refresh, the HTTP client) belongs
/// outside it.
pub(crate) async fn build_copilot_with_context(
    storage: &AuthStorage,
    cell: Arc<CopilotContextCell>,
    auth_config: AuthCopilotConfig,
) -> (Option<Arc<dyn LlmClient>>, Option<String>) {
    let manager = Arc::new(CredentialManager::from_storage(
        storage,
        [Arc::new(ContextBoundCopilotProvider::new(Arc::clone(&cell))) as Arc<dyn AuthProvider>],
    ));
    build_copilot_from_manager(manager, cell, auth_config).await
}

/// Reads the stored Copilot credential and pins it to `auth_config`, inside
/// the profile's commit section.
async fn bind_copilot_context(
    storage: &AuthStorage,
    auth_config: AuthCopilotConfig,
    deployment: CopilotDeployment,
) -> Result<Arc<CopilotContextCell>, coda_auth::AuthError> {
    use coda_auth::coordination::AUTH_COMMIT_KEY;

    let section = storage.coordinator.begin(AUTH_COMMIT_KEY).await?;
    let credential = read_stored_copilot_credential(storage).await?;
    let cell = CopilotContextCell::initial(auth_config, deployment, credential.as_ref());
    drop(section);
    Ok(cell)
}

/// The stored Copilot credential, validated against the provider it is filed
/// under. Never migrates and never refreshes; safe to call inside the section.
async fn read_stored_copilot_credential(
    storage: &AuthStorage,
) -> Result<Option<coda_auth::Credential>, coda_auth::AuthError> {
    let Some(raw) = storage.store.read_only("llmauth:github-copilot").await? else {
        return Ok(None);
    };
    let credential: coda_auth::Credential = serde_json::from_str(&raw)?;
    if credential.provider_id != "github-copilot" {
        return Err(coda_auth::AuthError::CredentialProviderMismatch {
            expected: "github-copilot".into(),
            actual: "another provider".into(),
        });
    }
    Ok(Some(credential))
}

/// Core of the Copilot client-from-keyring path with injectable dependencies.
///
/// Returns `(Some(client), None)` on success.
/// Returns `(None, None)` when no credential is stored ("not signed in").
/// Returns `(None, Some(diagnostic))` when a store or refresh error occurs.
///
/// Separating `Ok(None)` (not signed in) from `Err(_)` (store/refresh failure)
/// is the key correctness property: swallowing `Err` as `None` previously hid
/// token-refresh failures, making the engine silently appear as if no Copilot
/// credential existed at all.
///
/// Coordination is process-local here: use [`build_copilot_from_storage`] for
/// a profile on disk, which other processes share.
///
/// `pub(crate)` so the injectable path is reachable from unit tests.
#[cfg_attr(not(test), allow(dead_code))]
pub(crate) async fn build_copilot_from_store(
    store: Arc<dyn CredentialStore>,
    auth_config: AuthCopilotConfig,
) -> (Option<Arc<dyn LlmClient>>, Option<String>) {
    let raw = match store.read_only("llmauth:github-copilot").await {
        Ok(raw) => raw,
        Err(error) => return (None, Some(sanitize_auth_error(&error))),
    };
    let credential = match raw.as_deref().map(serde_json::from_str::<coda_auth::Credential>) {
        Some(Ok(credential)) => Some(credential),
        Some(Err(error)) => {
            return (
                None,
                Some(sanitize_auth_error(&coda_auth::AuthError::Serialization(error))),
            )
        }
        None => None,
    };
    let cell = CopilotContextCell::initial(
        auth_config.clone(),
        CopilotDeployment::Public,
        credential.as_ref(),
    );
    let manager = Arc::new(CredentialManager::new(
        store,
        [Arc::new(ContextBoundCopilotProvider::new(Arc::clone(&cell))) as Arc<dyn AuthProvider>],
    ));
    build_copilot_from_manager(manager, cell, auth_config).await
}

/// Builds the Copilot client from an already-wired credential manager.
async fn build_copilot_from_manager(
    manager: Arc<CredentialManager>,
    cell: Arc<CopilotContextCell>,
    auth_config: AuthCopilotConfig,
) -> (Option<Arc<dyn LlmClient>>, Option<String>) {
    match manager.get_credential("github-copilot").await {
        Ok(Some(_)) => {}
        // Not signed in: normal "not logged in" case, no diagnostic.
        Ok(None) => return (None, None),
        // Store or refresh error: the user IS configured but something went
        // wrong (expired token, network failure, keyring error).  Return a
        // safe diagnostic so the caller can surface a meaningful message.
        Err(e) => {
            return (None, Some(sanitize_auth_error(&e)));
        }
    }
    // The source is pinned to the context this client is being built for, so
    // it stops authenticating if the profile moves to another account or
    // deployment — this client's base URL cannot follow.
    let connection = coda_auth::service::copilot_connection(Arc::clone(&manager), cell);
    let source: Arc<dyn CredentialSource> = connection.source;

    // The credential source supplies the Authorization header; identity
    // headers come from the resolved config. Without these the API rejects
    // requests with "missing Editor-Version header for IDE auth", and the
    // engine appears to have no models at all.
    //
    // api_base_url must be set explicitly: CopilotConfig::with_token("") always
    // defaults to the public github.com endpoint. An enterprise tenant's
    // inference endpoint (e.g. https://copilot-api.octocorp.ghe.com) is only
    // in auth_config.api_base_url, so without this call every enterprise
    // request goes to the wrong host.
    let config = CopilotConfig::with_token("")
        .with_credential_source(source)
        .with_base_url(auth_config.api_base_url.clone())
        .with_header("editor-version", auth_config.editor_version.clone())
        .with_header("editor-plugin-version", auth_config.editor_plugin_version.clone())
        .with_header("copilot-integration-id", auth_config.integration_id.clone())
        .with_header("user-agent", auth_config.user_agent.clone())
        .with_header("x-github-api-version", "2026-06-01");

    match CopilotClient::new(config) {
        Ok(client) => (Some(Arc::new(client)), None),
        Err(_) => (None, Some("could not construct the Copilot HTTP client".into())),
    }
}

/// Core of the Claude.ai client path with the storage injected.
///
/// The `anthropic-beta` OAuth header is required for subscription auth or the
/// API rejects every request.
///
/// The client is given the `claude-ai` identity explicitly. It speaks to the
/// same Anthropic endpoint as a console API key, but it is a different
/// account: the engine's public provider id comes from
/// `LlmClient::provider_id`, and everything keyed off it — the saved model row,
/// the effort preference, the provider shown to the user — would otherwise
/// report a subscription as an API key.
pub(crate) async fn build_claude_from_storage(
    storage: &AuthStorage,
) -> (Option<Arc<dyn LlmClient>>, Option<String>) {
    use coda_auth::provider::claude_ai::{ClaudeAiConfig, ClaudeAiProvider, OAUTH_BETA_HEADER, PROVIDER_ID};

    let provider = ClaudeAiProvider::new(ClaudeAiConfig::production(CLAUDE_AI_CLIENT_ID));
    let manager = Arc::new(CredentialManager::from_storage(
        storage,
        [Arc::new(provider) as Arc<dyn AuthProvider>],
    ));
    match manager.get_credential(PROVIDER_ID).await {
        Ok(Some(_)) => {}
        // No credential is the normal "not signed in" case.
        Ok(None) => return (None, None),
        // An unreadable credential is not the same thing.
        Err(e) => return (None, Some(sanitize_auth_error(&e))),
    }
    let source: Arc<dyn CredentialSource> =
        Arc::new(CredentialManagerSource::new(Arc::clone(&manager), PROVIDER_ID));
    let config = AnthropicConfig {
        extra_headers: vec![("anthropic-beta".into(), OAUTH_BETA_HEADER.into())],
        ..AnthropicConfig::api_key("")
            .with_identity(PROVIDER_ID)
            .with_credential_source(source)
    };
    match AnthropicClient::new(config) {
        Ok(client) => (Some(Arc::new(client) as Arc<dyn LlmClient>), None),
        Err(_) => (None, Some("could not construct the Claude.ai HTTP client".into())),
    }
}

/// Builds an Anthropic client from the API key stored in the profile.
///
/// This is the path that was missing: `--provider anthropic` used to consult
/// only an explicit flag and the environment, so a key saved by
/// `coda auth login` was invisible to the engine that was supposed to use it.
///
/// The credential is read through the profile's [`CredentialManager`], so a
/// key removed by a logout stops working immediately: the manager-backed
/// source returns an error rather than `Ok(None)`, and the client refuses the
/// request instead of falling back to a stale static token.
///
/// Returns `(Some(client), None)` on success, `(None, None)` when no key is
/// stored, and `(None, Some(diagnostic))` when one exists but could not be
/// read.
pub(crate) async fn build_anthropic_from_storage(
    storage: &AuthStorage,
    endpoint: Option<&str>,
) -> (Option<Arc<dyn LlmClient>>, Option<String>) {
    use coda_auth::provider::api_key::{ApiKeyProvider, PROVIDER_ID};

    let manager = Arc::new(CredentialManager::from_storage(
        storage,
        [Arc::new(ApiKeyProvider) as Arc<dyn AuthProvider>],
    ));
    match manager.get_credential(PROVIDER_ID).await {
        Ok(Some(_)) => {}
        Ok(None) => return (None, None),
        Err(e) => return (None, Some(sanitize_auth_error(&e))),
    }
    let source: Arc<dyn CredentialSource> =
        Arc::new(CredentialManagerSource::new(Arc::clone(&manager), PROVIDER_ID));
    // The engine identity for a stored console key is `anthropic`, which is
    // also the client's default: the stored id (`anthropic-api-key`) is a
    // storage detail and never reaches the engine.
    let mut config = AnthropicConfig::api_key("").with_credential_source(source);
    if let Some(url) = endpoint.map(str::trim).filter(|u| !u.is_empty()) {
        config = config.with_base_url(url.to_owned());
    }
    match AnthropicClient::new(config) {
        Ok(client) => (Some(Arc::new(client) as Arc<dyn LlmClient>), None),
        Err(_) => (None, Some("could not construct the Anthropic HTTP client".into())),
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Map history to wire format: role lowercased, text blocks concatenated,
/// non-text blocks dropped (spec: "non-text blocks are ignored, not rendered").
fn project_history(history: &[Message]) -> Vec<WireHistoryMessage> {
    history
        .iter()
        .map(|msg| {
            let content: String = msg
                .content
                .iter()
                .filter_map(|b| match b {
                    Content::Text(t) => Some(t.as_str()),
                    _ => None,
                })
                .collect::<Vec<_>>()
                .join("");
            WireHistoryMessage { role: msg.role.as_str().to_string(), content }
        })
        .collect()
}

fn build_user_message(p: &PromptParams) -> Message {
    let mut content = Vec::new();
    if let Some(text) = &p.text {
        if !text.is_empty() {
            content.push(Content::Text(text.clone()));
        }
    }
    for img in p.images.as_deref().unwrap_or(&[]) {
        if let (Some(mt), Some(b64)) = (img["mediaType"].as_str(), img["base64"].as_str()) {
            content.push(Content::Image { media_type: mt.into(), base64: b64.into() });
        }
    }
    Message::new(Role::User, content)
}

fn wire_goal_status(gs: &GoalStatus) -> Option<Value> {
    if gs.outcome == GoalOutcome::None {
        return None;
    }
    let outcome = match gs.outcome {
        GoalOutcome::Met => "Met",
        GoalOutcome::Unmet => "Unmet",
        GoalOutcome::None => return None,
    };
    let mut m = serde_json::Map::new();
    m.insert("outcome".into(), json!(outcome));
    if let Some(r) = &gs.remaining {
        m.insert("remaining".into(), json!(r));
    }
    m.insert("continuations".into(), json!(gs.continuations));
    m.insert("elapsedSeconds".into(), json!(gs.elapsed.as_secs_f64()));
    m.insert("escalated".into(), json!(gs.escalated));
    m.insert("extensionUsed".into(), json!(gs.extension_used));
    Some(Value::Object(m))
}

fn parse_duration(s: Option<&str>) -> Option<Duration> {
    let s = s?;
    if let Some(n) = s.strip_suffix('m').and_then(|n| n.trim().parse::<u64>().ok()) {
        return Some(Duration::from_secs(n * 60));
    }
    if let Some(n) = s.strip_suffix('h').and_then(|n| n.trim().parse::<u64>().ok()) {
        return Some(Duration::from_secs(n * 3600));
    }
    if let Some(n) = s.strip_suffix('s').and_then(|n| n.trim().parse::<u64>().ok()) {
        return Some(Duration::from_secs(n));
    }
    None
}

/// Lightweight base64 character-set + length validator.
///
/// Does not actually decode; just checks that all characters are in the
/// standard base64 alphabet and that the string is a multiple of 4 bytes
/// (which rules out truncated/corrupted inputs like `"not valid base64!!"`).
fn validate_base64(s: &str) -> Result<(), String> {
    if s.is_empty() {
        return Ok(());
    }
    let bytes = s.as_bytes();
    // Find where padding starts.
    let content_end = bytes.iter().rposition(|&b| b != b'=').map(|i| i + 1).unwrap_or(0);
    // Content bytes must all be standard base64 characters.
    for &b in &bytes[..content_end] {
        if !matches!(b, b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'+' | b'/') {
            return Err(format!("non-base64 character 0x{b:02x} in image data"));
        }
    }
    // Total length must be a multiple of 4.
    if bytes.len() % 4 != 0 {
        return Err(format!(
            "base64 length {} is not a multiple of 4",
            bytes.len()
        ));
    }
    // At most 2 padding bytes.
    let padding = bytes.len() - content_end;
    if padding > 2 {
        return Err("excessive padding in base64 data".into());
    }
    Ok(())
}

// ─────────────────────────────────────────────────────────────────────────────
// Settings / config helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Read the user's settings.json as a JSON value (best-effort; empty object on failure).
/// Honors the `CODA_HOME` profile-root override. Tolerates a leading BOM.
fn load_settings_value() -> Value {
    crate::settings::load_settings_json(&coda_auth::coda_dir().join("settings.json"))
}

/// Reads the per-model effort the TUI saved under `effortByModel`.
///
/// The key format mirrors the TUI's `Settings::effort_for` key, so both sides
/// agree on where to read and write without either depending on the other.
fn effort_from_settings(settings: &Value, provider: &str, model: &str) -> Option<Effort> {
    let key = format!("{provider}/{model}");
    let raw = settings
        .get("effortByModel")?
        .get(&key)?
        .as_str()?;
    Effort::parse(raw)
}

/// Resolves a *requested* effort to the level actually in force for a model.
///
/// The three cases mirror the picker's mental model:
///
/// - `None` requested → `None` in force (automatic).
/// - a level, capability *indeterminate* → keep the requested level rather than
///   drop it; the truth is not yet known and dropping it would silently lose a
///   user's choice (the exact bug the C# `ResolveStoredLevel` warns about).
/// - a level, capability *known* → clamp/reject via [`resolve_applied_level`],
///   so `max` on a high-only model becomes `high` and an unsupported level
///   falls back to automatic rather than being sent verbatim.
fn resolve_effective_effort(
    capability: &ReasoningCapability,
    indeterminate: bool,
    requested: Option<Effort>,
) -> Option<Effort> {
    let requested = requested?;
    if indeterminate {
        return Some(requested);
    }
    resolve_applied_level(capability, Some(requested.as_str())).and_then(|s| Effort::parse(&s))
}

/// Build the merged LSP server config from settings + plugins.
fn load_lsp_configs(
    settings: &Value,
    _working_dir: &str,
) -> HashMap<String, LspServerConfig> {
    let settings_servers = settings
        .get("lspServers")
        .map(|v| LspServerConfig::parse_map(v))
        .unwrap_or_default();
    LspServerMapBuilder::build(&settings_servers, &HashMap::new())
}

/// Load hooks from both the user settings file and the project settings file,
/// stamping the scope by source — never from the JSON content.
///
/// # Security invariant
/// `UserHook.scope` is `#[serde(skip)]` in coda_agent, so its `Default` value
/// (`HookScope::Project`) is applied on every deserialization regardless of
/// what the JSON says. This function then overwrites the scope based solely on
/// which file the hook came from. A hostile project cannot claim `User` scope.
pub(crate) fn load_user_hooks(working_dir: &str) -> Vec<UserHook> {
    let mut hooks = Vec::new();

    // User-scoped: ~/.coda/settings.json (honors the CODA_HOME override)
    {
        let path = coda_auth::coda_dir().join("settings.json");
        for mut h in load_hooks_from_file(&path) {
            h.scope = HookScope::User; // stamped by loader, not from JSON
            hooks.push(h);
        }
    }

    // Project-scoped: <cwd>/.coda/settings.json
    let project_path =
        std::path::Path::new(working_dir).join(".coda").join("settings.json");
    for mut h in load_hooks_from_file(&project_path) {
        h.scope = HookScope::Project; // stamped by loader (also the serde default)
        hooks.push(h);
    }

    hooks
}

/// Parse hooks out of a settings JSON file. Returns an empty vec on any error.
pub(crate) fn load_hooks_from_file(path: &Path) -> Vec<UserHook> {
    let text = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(_) => return Vec::new(),
    };
    let value: Value = match serde_json::from_str(&text) {
        Ok(v) => v,
        Err(_) => return Vec::new(),
    };
    let hooks_array = match value.get("hooks").and_then(|h| h.as_array()) {
        Some(a) => a.clone(),
        None => return Vec::new(),
    };
    hooks_array
        .iter()
        .filter_map(|h| serde_json::from_value::<UserHook>(h.clone()).ok())
        .collect()
}

// ─────────────────────────────────────────────────────────────────────────────
// Model catalogue fallback (Finding 2)
// ─────────────────────────────────────────────────────────────────────────────

/// A non-empty built-in catalogue returned when live model fetching fails or
/// before credentials are available. Mirrors the C# fallback that ensures the
/// user always sees some models rather than an empty list.
/// The bundled catalogue, as wire models.
///
/// Was a hand-written list with no prices and context limits typed in beside
/// the names. The snapshot carries both, and stays right when a provider
/// changes them.
fn catalog_models() -> Vec<WireModel> {
    let catalog = crate::catalog::ModelCatalog::load();
    let mut seen: Vec<WireModel> = Vec::new();
    for provider in ["anthropic", "github-copilot"] {
        for model in catalog.models_for(provider) {
            // The same model is offered by more than one provider — a
            // Copilot-hosted Claude keeps its Anthropic id — and listing it
            // twice would show the user a duplicate.
            if seen.iter().any(|m| m.id == model.id) {
                continue;
            }
            // Anthropic advertises nothing at runtime, so derive the level set
            // from the static id rules; a Copilot model's levels are only known
            // once the live list is fetched, so leave them empty (indeterminate)
            // in the catalogue fallback rather than guess.
            let reasoning_levels = resolve_reasoning(provider, &model.id, None).levels;
            seen.push(WireModel {
                id: model.id,
                display_name: model.display_name,
                context_limit: model.context_limit,
                input_cost: model.cost.map(|c| c.input),
                output_cost: model.cost.map(|c| c.output),
                reasoning_levels,
                effort: None,
            });
        }
    }
    seen
}

// ─────────────────────────────────────────────────────────────────────────────
// Schedule wire-format helper
// ─────────────────────────────────────────────────────────────────────────────

fn scheduled_task_to_wire(t: &coda_agent::scheduling::ScheduledTask) -> ScheduledTaskResponse {
    let (kind_str, rule) = match t.kind {
        ScheduleKind::Interval => {
            let secs = t.interval.unwrap_or(0.0);
            ("interval".to_owned(), format_interval_secs(secs))
        }
        ScheduleKind::At => (
            "at".to_owned(),
            t.at_utc
                .map(|d| d.to_rfc3339())
                .unwrap_or_default(),
        ),
        ScheduleKind::Cron => (
            "cron".to_owned(),
            t.cron.clone().unwrap_or_default(),
        ),
    };
    ScheduledTaskResponse {
        id: t.id.clone(),
        name: t.name.clone(),
        kind: kind_str,
        prompt: t.prompt.clone(),
        rule,
        time_zone: t.time_zone_id.clone(),
        next_run_utc: t.next_run_utc.to_rfc3339(),
        state: "idle".into(),
        active_task_id: None,
        last_outcome: t.last_terminal_outcome.as_ref().map(|o| {
            match o.outcome {
                ScheduleTerminalOutcome::Succeeded => "succeeded",
                ScheduleTerminalOutcome::Failed => "failed",
                ScheduleTerminalOutcome::Stopped => "stopped",
            }
            .to_owned()
        }),
    }
}

fn format_interval_secs(secs_f64: f64) -> String {
    let secs = secs_f64.round() as u64;
    if secs >= 3600 && secs % 3600 == 0 {
        format!("{}h", secs / 3600)
    } else if secs >= 60 && secs % 60 == 0 {
        format!("{}m", secs / 60)
    } else {
        format!("{}s", secs)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::sync::mpsc;

    fn make_host() -> Arc<ServeHost> {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        ServeHost::new(sink, ch, ".".into())
    }

    /// The turn slot must survive a cancelled turn.
    ///
    /// A serve task is cancellable: if the client disconnects mid-prompt the
    /// future is simply dropped, and neither the `Ok` nor the `Err` arm runs.
    /// Releasing only at those call sites would leave the slot claimed for the
    /// life of the process, so every later prompt would be refused as busy
    /// with no recovery short of a restart.
    #[tokio::test]
    async fn a_dropped_turn_releases_the_slot() {
        let host = make_host();
        {
            let _turn = host.try_claim_turn("test-turn", "", TurnKind::Prompt).expect("first claim succeeds");
            assert!(
                host.try_claim_turn("test-turn", "", TurnKind::Prompt).is_err(),
                "the slot must be held while a turn is in flight"
            );
        } // guard dropped here, as it would be on cancellation

        assert!(
            host.try_claim_turn("test-turn", "", TurnKind::Prompt).is_ok(),
            "dropping the turn must release the slot, or the session wedges forever"
        );
    }

    // ── C6: the public turn opens with the single-flight claim ────────────
    #[tokio::test]
    async fn claiming_the_turn_slot_makes_the_engine_publicly_busy_before_anything_is_built() {
        let host = make_host();
        host.engine_state.mark_initialized();
        let idle = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(idle["lifecycle"], "ready");
        assert!(idle["turn"].is_null());

        let guard = host
            .try_claim_turn("turn-1", "do the thing", TurnKind::Prompt)
            .expect("claim succeeds");
        let busy = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(
            busy["lifecycle"], "busy",
            "the flag that refuses a second prompt and the public lifecycle must never disagree"
        );
        assert_eq!(busy["turn"]["phase"], "preparing");
        assert_eq!(busy["turn"]["turnId"], "turn-1");
        assert_eq!(
            busy["turn"]["liveEntries"][0]["blocks"][0]["text"], "do the thing",
            "the accepted prompt is visible from the instant the turn is claimed"
        );

        drop(guard);
        let after = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(after["lifecycle"], "ready", "a dropped guard must finalise the public turn too");
        assert!(after["turn"].is_null(), "a cancelled turn must not stay frozen in every later snapshot");
        assert_eq!(after["lastTurnOutcome"]["turnId"], "turn-1");
        assert_eq!(after["lastTurnOutcome"]["interrupted"], true);
    }

    #[tokio::test]
    async fn an_explicit_compaction_is_observably_busy_and_compacting() {
        let host = make_host();
        let guard = host
            .try_claim_turn("compact-1", "", TurnKind::Compaction)
            .expect("claim succeeds");
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["lifecycle"], "busy");
        assert_eq!(
            state["turn"]["phase"], "compacting",
            "compaction blocks prompts exactly like a turn, so it must be visible exactly like one"
        );
        drop(guard);
        assert_eq!(
            host.session_get_state(GetStateParams::default()).await.unwrap()["lifecycle"],
            "ready"
        );
    }

    #[tokio::test]
    async fn a_prompt_that_fails_preflight_still_finalises_the_public_turn() {
        // No credentials are wired, so the prompt fails after the slot was
        // claimed and the turn was already public.
        let dir = tempfile::tempdir().unwrap();
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new(sink, ch, dir.path().to_string_lossy().into_owned());

        let err = host
            .session_prompt(PromptParams { text: Some("hello".into()), images: None })
            .await
            .expect_err("no credentials means the prompt cannot run");

        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["lifecycle"], "ready", "a failed preflight must not leave the engine busy");
        assert!(state["turn"].is_null());
        assert_eq!(
            state["lastTurnOutcome"]["error"]["category"], "engine.unauthorized",
            "the failure must be reported as this turn's outcome, classified, not silently dropped"
        );
        assert!(!err.message.is_empty());
        assert!(
            host.try_claim_turn("next", "", TurnKind::Prompt).is_ok(),
            "the slot must be reusable after a failed preflight"
        );
    }

    #[tokio::test]
    async fn get_state_rejects_a_section_filter_it_does_not_implement() {
        let host = make_host();
        let err = host
            .session_get_state(GetStateParams { sections: Some(vec!["tools".into()]) })
            .await
            .expect_err("an unimplemented filter must be refused, never silently ignored");
        assert_eq!(err.code, -32602);
        assert!(err.message.contains("state.sectionFilter"));
        // An absent or empty filter is the ordinary whole-snapshot request.
        assert!(host.session_get_state(GetStateParams { sections: Some(vec![]) }).await.is_ok());
        assert!(host.session_get_state(GetStateParams::default()).await.is_ok());
    }

    // ── diagnostics wiring ───────────────────────────────────────────────────

    fn test_diagnostics(dir: &std::path::Path) -> coda_diagnostics::DiagnosticContext {
        let logger = coda_diagnostics::Logger::open(
            coda_diagnostics::Options {
                directory: dir.to_path_buf(),
                file: None,
                role: coda_diagnostics::ProcessRole::Serve,
                version: "test".into(),
                verbosity: coda_diagnostics::Verbosity::Normal,
            },
            coda_diagnostics::Limits::default(),
        )
        .expect("logger opens");
        coda_diagnostics::DiagnosticContext::root(Arc::new(logger), "run-1")
    }

    fn recorded_lines(ctx: &coda_diagnostics::DiagnosticContext) -> Vec<serde_json::Value> {
        let path = ctx.logger().status().path.expect("a log path");
        std::fs::read_to_string(path)
            .unwrap()
            .lines()
            .filter(|l| !l.is_empty())
            .map(|l| serde_json::from_str(l).unwrap())
            .collect()
    }

    /// A host built without a diagnostics root (every other test in this
    /// module) must omit `telemetryLogPath` — this is the existing contract
    /// asserted by `initialize_always_succeeds`/`transport.rs`'s tests, and it
    /// must keep holding for hosts that never opted into real diagnostics.
    #[tokio::test]
    async fn a_host_with_a_diagnostics_root_reports_its_real_log_path() {
        let dir = tempfile::tempdir().unwrap();
        let ctx = test_diagnostics(dir.path());

        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_optional_client_and_mcp(
            None,
            sink,
            ch,
            ".".into(),
            crate::mcp::McpBundle::disabled(),
            StartupOptions::default(),
            None,
            Some(ctx.clone()),
        );

        let result = host.initialize(InitParams::default()).await.unwrap();
        let reported = result["telemetryLogPath"].as_str().expect("a real path is reported");
        let expected = ctx.logger().status().path.unwrap();
        assert_eq!(reported, expected.display().to_string());

        let lines = recorded_lines(&ctx);
        assert!(lines.iter().any(|l| l["kind"] == "session_initialized"));
    }

    #[tokio::test]
    async fn a_successful_turn_records_start_and_end_with_full_correlation() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::Usage;

        let dir = tempfile::tempdir().unwrap();
        let ctx = test_diagnostics(dir.path());
        let client = ScriptedClient::new(vec![vec![
            StreamEvent::TextDelta("hi".into()),
            StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO },
        ]]);

        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_optional_client_and_mcp(
            Some(client),
            sink,
            ch,
            ".".into(),
            crate::mcp::McpBundle::disabled(),
            StartupOptions::default(),
            None,
            Some(ctx.clone()),
        );

        host.initialize(InitParams::default()).await.unwrap();
        let result = host
            .session_prompt(PromptParams { text: Some("hello".into()), images: None })
            .await
            .expect("prompt succeeds");
        assert!(result["ok"].as_bool().unwrap_or(false));

        let lines = recorded_lines(&ctx);
        let start = lines.iter().find(|l| l["kind"] == "turn_start").expect("turn_start recorded");
        let end = lines.iter().find(|l| l["kind"] == "turn_end").expect("turn_end recorded");
        assert!(start["session_id"].as_str().is_some());
        assert!(start["turn_id"].as_str().is_some());
        assert_eq!(start["turn_id"], end["turn_id"], "start/end share the same turn id");
        assert_eq!(end["stop_reason"], "end_turn");
        assert_eq!(start["provider"], "scripted");
    }

    #[tokio::test]
    async fn a_failed_turn_records_a_safe_turn_failed_with_no_raw_error_text() {
        use async_trait::async_trait;

        struct AlwaysFailsClient;
        #[async_trait]
        impl LlmClient for AlwaysFailsClient {
            fn provider_id(&self) -> &str {
                "scripted"
            }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                Err(coda_llm::LlmError::Api {
                    status: 400,
                    message: "Missing required parameter: 'input[22].summary'. secret=sk-live-abc".into(),
                    kind: coda_llm::FailureKind::Permanent,
                    retry_after: None,
                    body: Some(
                        r#"{"error":{"type":"invalid_request_error","param":"input[22].summary","message":"Missing required parameter: 'input[22].summary'. secret=sk-live-abc"}}"#
                            .into(),
                    ),
                })
            }
        }

        let dir = tempfile::tempdir().unwrap();
        let ctx = test_diagnostics(dir.path());
        let client: Arc<dyn LlmClient> = Arc::new(AlwaysFailsClient);

        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_optional_client_and_mcp(
            Some(client),
            sink,
            ch,
            ".".into(),
            crate::mcp::McpBundle::disabled(),
            StartupOptions::default(),
            None,
            Some(ctx.clone()),
        );

        host.initialize(InitParams::default()).await.unwrap();
        let result = host
            .session_prompt(PromptParams { text: Some("hello".into()), images: None })
            .await
            .expect("session_prompt returns a response even on turn failure");
        assert!(!result["ok"].as_bool().unwrap_or(true));

        let lines = recorded_lines(&ctx);
        let failure = lines.iter().find(|l| l["kind"] == "turn_failed").expect("turn_failed recorded");
        assert_eq!(failure["status"], 400);
        assert_eq!(failure["category"], "client_error");

        let content = std::fs::read_to_string(ctx.logger().status().path.unwrap()).unwrap();
        assert!(!content.contains("sk-live-abc"), "raw error text must never be persisted");
        assert!(!content.contains("Missing required parameter"));
    }

    /// Two concurrent prompts must not both run: the loser is refused rather
    /// than interleaving writes into the same history.
    #[tokio::test]
    async fn a_second_concurrent_claim_is_refused() {
        let host = make_host();
        let first = host.try_claim_turn("test-turn", "", TurnKind::Prompt).expect("first claim succeeds");
        assert!(host.try_claim_turn("test-turn", "", TurnKind::Prompt).is_err(), "second concurrent claim must be refused");
        drop(first);
        assert!(host.try_claim_turn("test-turn", "", TurnKind::Prompt).is_ok(), "the slot is reusable once released");
    }

    // ── I1: "ready" and "a prompt is accepted" must agree ─────────────────

    fn host_with_events(dir: &std::path::Path) -> (Arc<ServeHost>, mpsc::UnboundedReceiver<Vec<u8>>) {
        let (tx, rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new(sink, ch, dir.to_string_lossy().into_owned());
        host.engine_state.bus_ref().enable_state_events();
        host.engine_state.mark_initialized();
        (host, rx)
    }

    fn drain_events(rx: &mut mpsc::UnboundedReceiver<Vec<u8>>) -> Vec<(String, Value)> {
        let mut out = Vec::new();
        while let Ok(frame) = rx.try_recv() {
            let text = String::from_utf8(frame).unwrap();
            let body = text.find("\r\n\r\n").map(|i| i + 4).unwrap_or(0);
            let msg: Value = serde_json::from_str(&text[body..]).unwrap();
            out.push((msg["method"].as_str().unwrap().to_string(), msg["params"].clone()));
        }
        out
    }

    // ── Config commit atomicity: CONFIG -> STATE -> BUS ───────────────────

    fn config_events(events: &[(String, Value)]) -> Vec<Value> {
        events
            .iter()
            .filter(|(m, _)| m == "event/configChanged")
            .map(|(_, p)| p.clone())
            .collect()
    }

    /// A host wired to a client that advertises two models with a full
    /// reasoning ladder, with `stateEvents` negotiated so every config commit
    /// is observable on the wire.
    fn commit_host(
        dir: &std::path::Path,
    ) -> (Arc<ServeHost>, mpsc::UnboundedReceiver<Vec<u8>>) {
        let client = LevelsClient::arc(vec![
            model_with_levels("model-a", &["low", "medium", "high"]),
            model_with_levels("model-b", &["low", "medium", "high"]),
        ]);
        let (tx, rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_client(
            client,
            sink,
            ch,
            dir.to_string_lossy().into_owned(),
        );
        host.engine_state.bus_ref().enable_state_events();
        host.engine_state.mark_initialized();
        host.set_model_for_test("model-a");
        (host, rx)
    }

    /// A `session/setModel` that is still waiting on the provider must be
    /// invisible: the runtime it mutates and the config it publishes move
    /// together, at the end, or a reader in that window is handed a model
    /// whose effort was resolved for a *different* model.
    #[tokio::test]
    async fn a_set_model_awaiting_the_provider_changes_nothing_until_it_commits() {
        let dir = tempfile::tempdir().unwrap();
        let (host, mut rx) = commit_host(dir.path());
        host.session_set_effort(SetEffortParams {
            effort: Some("high".into()),
            ..Default::default()
        })
        .await
        .unwrap();
        let _ = drain_events(&mut rx);

        // Block the provider lookup the setter must await before it can know
        // which effort the requested model can honour.
        let client_guard = host.client.lock().await;

        let mut pending =
            Box::pin(host.session_set_model(SetModelParams { model: "model-b".into() }));
        // Proven parked: the future is polled to its first pending await —
        // the client mutex we hold — before anything is asserted. A spawn plus
        // a yield could pass without the setter ever having started.
        tokio::select! {
            biased;
            r = &mut pending => panic!("the setter must wait for the provider: {r:?}"),
            _ = std::future::ready(()) => {}
        }

        assert_eq!(
            host.current_model(),
            "model-a",
            "the runtime must not move before the change can be published"
        );
        assert_eq!(host.current_effort(), Some(Effort::High));
        let mid = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(mid["config"]["next"]["model"], "model-a");
        assert_eq!(mid["config"]["next"]["effort"], "high");
        assert!(
            config_events(&drain_events(&mut rx)).is_empty(),
            "an uncommitted change must not be announced"
        );

        drop(client_guard);
        let applied = pending.await.unwrap();
        assert_eq!(applied["ok"], true);
        assert_eq!(applied["model"], "model-b");

        let after = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(after["config"]["next"]["model"], "model-b");
        let published = config_events(&drain_events(&mut rx));
        assert_eq!(published.len(), 1, "one change, one announcement: {published:?}");
        assert_eq!(published[0]["next"]["model"], "model-b");
        assert_eq!(
            published[0]["next"]["effort"], after["config"]["next"]["effort"],
            "the announced next config and the snapshot must not disagree"
        );
    }

    /// Cancelling a setter mid-flight must leave no torn runtime behind: the
    /// model and the effort are either both the old pair or both the new one,
    /// and nothing is announced for a change that never happened.
    #[tokio::test]
    async fn a_cancelled_set_model_leaves_the_runtime_and_the_feed_untouched() {
        let dir = tempfile::tempdir().unwrap();
        let (host, mut rx) = commit_host(dir.path());
        host.session_set_effort(SetEffortParams {
            effort: Some("high".into()),
            ..Default::default()
        })
        .await
        .unwrap();
        let _ = drain_events(&mut rx);

        let client_guard = host.client.lock().await;
        let mut pending =
            Box::pin(host.session_set_model(SetModelParams { model: "model-b".into() }));
        // Cancellation is only meaningful once the setter is proven to be
        // inside the parked path.
        tokio::select! {
            biased;
            r = &mut pending => panic!("the setter must wait for the provider: {r:?}"),
            _ = std::future::ready(()) => {}
        }

        drop(pending); // exactly what a disconnecting client does
        drop(client_guard);

        assert_eq!(host.current_model(), "model-a", "a cancelled setter must not half-apply");
        assert_eq!(host.current_effort(), Some(Effort::High));
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["config"]["next"]["model"], "model-a");
        assert_eq!(state["config"]["next"]["effort"], "high");
        assert!(
            config_events(&drain_events(&mut rx)).is_empty(),
            "a change that never happened must not be announced"
        );
    }

    /// A turn claimed while a commit is still awaiting the provider must
    /// capture the *old* config, and the snapshot must never pair a new
    /// `activeConfig` with an older `nextConfig` (or the reverse).
    #[tokio::test]
    async fn a_turn_claimed_against_a_pending_commit_sees_one_coherent_config() {
        let dir = tempfile::tempdir().unwrap();
        let (host, mut rx) = commit_host(dir.path());
        let _ = drain_events(&mut rx);

        let client_guard = host.client.lock().await;
        let mut pending =
            Box::pin(host.session_set_model(SetModelParams { model: "model-b".into() }));
        tokio::select! {
            biased;
            r = &mut pending => panic!("the setter must wait for the provider: {r:?}"),
            _ = std::future::ready(()) => {}
        }

        let _turn = host
            .try_claim_turn("t-race", "go", TurnKind::Prompt)
            .expect("the slot is free");
        let during = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(
            during["turn"]["activeConfig"]["model"], "model-a",
            "the turn runs what was committed, never a half-applied switch"
        );
        assert_eq!(during["config"]["active"]["model"], "model-a");
        assert_eq!(during["config"]["next"]["model"], "model-a");
        assert!(
            during["config"]["differing"]
                .as_array()
                .is_none_or(|d| d.is_empty()),
            "nothing differs yet: {}",
            during["config"]
        );

        drop(client_guard);
        pending.await.unwrap();

        let after = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(
            after["config"]["active"]["model"], "model-a",
            "the running turn keeps the model it started with"
        );
        assert_eq!(after["config"]["next"]["model"], "model-b");
        let differing = after["config"]["differing"].as_array().unwrap();
        assert!(
            differing.iter().any(|d| d["key"] == "model"),
            "the difference the next turn would apply must be reported: {differing:?}"
        );
    }

    /// `effort` and `effortIsAuto` are two views of one value, so they can
    /// only be derived from one read. Read twice, a commit landing between
    /// them manufactures `effort: null` with `effortIsAuto: false` — a state
    /// the engine is never in.
    #[tokio::test]
    async fn effort_and_its_auto_flag_are_always_derived_from_one_read() {
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = commit_host(dir.path());

        for effort in [Some("high"), None] {
            host.session_set_effort(SetEffortParams {
                effort: effort.map(str::to_owned),
                ..Default::default()
            })
            .await
            .unwrap();

            let state = host.session_get_state(GetStateParams::default()).await.unwrap();
            let next = &state["config"]["next"];
            assert_eq!(
                next["effort"].is_null(),
                next["effortIsAuto"].as_bool().unwrap(),
                "effort/effortIsAuto must describe the same value: {next}"
            );

            let described = host.config_describe().await.unwrap();
            let entry = described["entries"]
                .as_array()
                .unwrap()
                .iter()
                .find(|e| e["key"] == "effort")
                .cloned()
                .expect("an effort entry");
            assert!(
                !entry["value"].is_null(),
                "describe must never report an unknown effort for a live engine: {entry}"
            );
            assert_eq!(
                entry["value"],
                if effort.is_some() { json!("high") } else { json!("auto") }
            );
        }
    }

    /// Stepping the *active* model's effort changes what the next turn will
    /// run, so it must be announced like every other config commit. It used
    /// to mutate the live level silently, leaving a `stateEvents` client
    /// permanently out of date.
    #[tokio::test]
    async fn adjusting_the_active_models_effort_announces_the_new_next_config() {
        let dir = tempfile::tempdir().unwrap();
        let (host, mut rx) = commit_host(dir.path());
        host.session_set_effort(SetEffortParams {
            effort: Some("low".into()),
            ..Default::default()
        })
        .await
        .unwrap();
        let _ = drain_events(&mut rx);

        let r = host
            .model_adjust_effort(AdjustEffortParams {
                model: "model-a".into(),
                direction: 1,
                expected_provider: None,
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["current"], "medium");

        let published = config_events(&drain_events(&mut rx));
        assert_eq!(published.len(), 1, "one commit, one announcement: {published:?}");
        assert_eq!(published[0]["key"], "effort");
        assert_eq!(published[0]["next"]["effort"], "medium");

        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["config"]["next"]["effort"], "medium");
    }

    /// Stepping an *inactive* model's effort changes only that model's stored
    /// preference. It must not be announced as a change to the next turn's
    /// config, which is not affected.
    #[tokio::test]
    async fn adjusting_an_inactive_models_effort_does_not_move_the_next_config() {
        let dir = tempfile::tempdir().unwrap();
        let (host, mut rx) = commit_host(dir.path());
        host.session_set_effort(SetEffortParams {
            effort: Some("low".into()),
            ..Default::default()
        })
        .await
        .unwrap();
        let _ = drain_events(&mut rx);

        host.model_adjust_effort(AdjustEffortParams {
            model: "model-b".into(),
            direction: 1,
            expected_provider: None,
        })
        .await
        .unwrap();

        assert!(
            config_events(&drain_events(&mut rx)).is_empty(),
            "an inactive model's preference is not the next turn's config"
        );
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["config"]["next"]["model"], "model-a");
        assert_eq!(state["config"]["next"]["effort"], "low");
    }

    /// A setter parked on the provider lookup while the engine shuts down
    /// must not commit when it finally wakes.
    ///
    /// The window is real: the writer holds its own lock across the provider
    /// await, so a `shutdown` can complete in the middle of it. A pre-check
    /// before the await cannot close it, and a commit that lands afterwards
    /// mutates a stopped engine's runtime and publishes an event past the
    /// terminal lifecycle — after which the snapshot describes a
    /// configuration nothing will ever run.
    #[tokio::test]
    async fn a_setter_parked_across_a_shutdown_cannot_commit_into_a_stopped_engine() {
        let dir = tempfile::tempdir().unwrap();
        let (host, mut rx) = commit_host(dir.path());
        host.session_set_effort(SetEffortParams {
            effort: Some("high".into()),
            ..Default::default()
        })
        .await
        .unwrap();

        let client_guard = host.client.lock().await;
        let mut pending =
            Box::pin(host.session_set_model(SetModelParams { model: "model-b".into() }));
        tokio::select! {
            biased;
            r = &mut pending => panic!("the setter must wait for the provider: {r:?}"),
            _ = std::future::ready(()) => {}
        }

        host.shutdown().await.unwrap();
        let stopped = host.session_get_state(GetStateParams::default()).await.unwrap();
        let _ = drain_events(&mut rx);

        drop(client_guard);
        let result = pending.await;
        assert!(
            result.is_err(),
            "a commit that arrives after shutdown must fail, never report success: {result:?}"
        );

        assert_eq!(host.current_model(), "model-a", "a refused commit changes no runtime field");
        assert_eq!(host.current_effort(), Some(Effort::High));
        let after = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(
            after["cursor"], stopped["cursor"],
            "a refused commit must publish nothing at all"
        );
        assert_eq!(after["config"]["next"]["model"], "model-a");
        assert!(config_events(&drain_events(&mut rx)).is_empty());
    }

    /// Every configuration setter shares that boundary: once the engine is
    /// stopped none of them may mutate the runtime, move `config.next` or
    /// publish, and each must say so rather than answer with a success.
    #[tokio::test]
    async fn configuration_setters_are_refused_once_the_engine_is_stopped() {
        let dir = tempfile::tempdir().unwrap();
        let (host, mut rx) = commit_host(dir.path());
        host.engine_state.shutdown_started();
        host.engine_state.shutdown_completed();
        let stopped = host.session_get_state(GetStateParams::default()).await.unwrap();
        let _ = drain_events(&mut rx);

        assert!(
            host.session_set_permission_mode(SetPermissionModeParams { mode: "plan".into() })
                .await
                .is_err(),
            "setPermissionMode must not succeed on a stopped engine"
        );
        assert_eq!(
            host.permission_mode.get(),
            PermissionMode::Default,
            "the shared mode the tool loop reads must not have moved"
        );
        assert!(
            host.session_set_system_prompt(SetSystemPromptParams { text: Some("nope".into()) })
                .await
                .is_err(),
            "setSystemPrompt must not succeed on a stopped engine"
        );
        assert!(
            host.session_set_effort(SetEffortParams {
                effort: Some("high".into()),
                ..Default::default()
            })
            .await
            .is_err(),
            "setEffort must not succeed on a stopped engine"
        );
        assert!(
            host.session_set_model(SetModelParams { model: "model-b".into() }).await.is_err(),
            "setModel must not succeed on a stopped engine"
        );
        assert!(
            host.model_adjust_effort(AdjustEffortParams {
                model: "model-a".into(),
                direction: 1,
                expected_provider: None,
            })
            .await
            .is_err(),
            "adjustEffort on the active model must not succeed on a stopped engine"
        );

        assert_eq!(host.current_model(), "model-a");
        assert_eq!(host.current_effort(), None);
        let after = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(after["cursor"], stopped["cursor"], "nothing may be published after stopped");
        assert_eq!(after["config"]["next"]["model"], "model-a");
        assert_eq!(after["config"]["next"]["permissionMode"], "default");
        assert_eq!(after["config"]["next"]["systemPromptSource"], "default");
        assert!(config_events(&drain_events(&mut rx)).is_empty());
    }

    /// Wiring a provider must announce the provider — and nothing else.
    ///
    /// The pre-existing rule ("Finding 3": adopt the model the newly
    /// connected provider resolves to) exists so a *default* model resolved
    /// for the previous provider is not sent to a provider that has never
    /// heard of it. It is not a licence to discard a model the operator
    /// actually chose: `serve --model` is an explicit selection, and losing
    /// it merely to populate `config.next.providerId` silently runs a
    /// different model than the one that was asked for.
    #[tokio::test]
    async fn an_explicit_startup_model_survives_the_wiring_that_announces_its_provider() {
        let dir = tempfile::tempdir().unwrap();
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_optional_client_and_mcp(
            None,
            sink,
            ch,
            dir.path().to_string_lossy().into_owned(),
            crate::mcp::McpBundle::disabled(),
            StartupOptions {
                model: Some("operator-chosen-model".into()),
                endpoint: Some("http://127.0.0.1:1".into()),
                ..StartupOptions::default()
            },
            None,
            None,
        );
        host.engine_state.bus_ref().enable_state_events();

        host.initialize(InitParams {
            api_key: Some("test-key".into()),
            ..Default::default()
        })
        .await
        .unwrap();

        assert_eq!(
            host.current_model(),
            "operator-chosen-model",
            "wiring a provider must not discard an explicitly selected model"
        );
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["config"]["next"]["model"], "operator-chosen-model");
        assert_eq!(
            state["config"]["next"]["providerId"], "anthropic",
            "the provider it did learn must still be announced: {}",
            state["config"]
        );
        let published = config_events(&drain_events(&mut rx));
        let last = published.last().expect("wiring announces the provider");
        assert_eq!(last["next"]["providerId"], "anthropic");
        assert_eq!(last["next"]["model"], "operator-chosen-model");
    }

    /// The same for a model chosen at runtime through `session/setModel`
    /// before any credential was wired.
    #[tokio::test]
    async fn a_model_selected_through_set_model_survives_provider_wiring() {
        let dir = tempfile::tempdir().unwrap();
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_optional_client_and_mcp(
            None,
            sink,
            ch,
            dir.path().to_string_lossy().into_owned(),
            crate::mcp::McpBundle::disabled(),
            StartupOptions {
                endpoint: Some("http://127.0.0.1:1".into()),
                ..StartupOptions::default()
            },
            None,
            None,
        );

        let selected = host
            .session_set_model(SetModelParams { model: "operator-chosen-model".into() })
            .await
            .unwrap();
        assert_eq!(selected["ok"], true);

        host.initialize(InitParams {
            api_key: Some("test-key".into()),
            ..Default::default()
        })
        .await
        .unwrap();

        assert_eq!(
            host.current_model(),
            "operator-chosen-model",
            "a selection made through session/setModel must outlive credential wiring"
        );
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["config"]["next"]["model"], "operator-chosen-model");
        assert_eq!(state["config"]["next"]["providerId"], "anthropic");
    }

    /// The counterpart, so the rule above is not over-applied: a model that
    /// was never chosen — the startup default resolved before any provider
    /// was known — is still replaced by the one the newly wired provider
    /// resolves to. That is the whole point of the original behaviour.
    #[tokio::test]
    async fn an_unselected_default_model_is_still_re_resolved_when_a_provider_is_wired() {
        let dir = tempfile::tempdir().unwrap();
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_optional_client_and_mcp(
            None,
            sink,
            ch,
            dir.path().to_string_lossy().into_owned(),
            crate::mcp::McpBundle::disabled(),
            StartupOptions {
                endpoint: Some("http://127.0.0.1:1".into()),
                ..StartupOptions::default()
            },
            None,
            None,
        );

        host.initialize(InitParams {
            api_key: Some("test-key".into()),
            ..Default::default()
        })
        .await
        .unwrap();

        assert_eq!(
            host.current_model(),
            crate::settings::model_for_provider("anthropic"),
            "an unchosen default must still follow the provider that was wired"
        );
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["config"]["next"]["model"], host.current_model());
        assert_eq!(state["config"]["next"]["providerId"], "anthropic");
    }

    /// The lazy wiring `session/compact` and the first `session/prompt`
    /// perform, exercised directly through the commit they share, in both
    /// directions: a chosen model survives, an unchosen default follows the
    /// provider, and either way the provider is announced.
    #[tokio::test]
    async fn lazy_wiring_announces_the_provider_without_discarding_a_selected_model() {
        let dir = tempfile::tempdir().unwrap();

        // (a) Nothing was ever chosen: the provider's model is adopted.
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new(sink, ch, dir.path().to_string_lossy().into_owned());
        host.engine_state.bus_ref().enable_state_events();
        host.engine_state.mark_initialized();
        let _ = drain_events(&mut rx);

        host.commit_wired_provider(&(NamedClient::arc("anthropic") as Arc<dyn LlmClient>)).await.expect("a live engine admits it");
        assert_eq!(host.current_model(), crate::settings::model_for_provider("anthropic"));
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["config"]["next"]["providerId"], "anthropic");
        assert_eq!(
            config_events(&drain_events(&mut rx)).last().expect("announced")["next"]["providerId"],
            "anthropic"
        );

        // (b) A model was selected first: only the provider moves.
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new(sink, ch, dir.path().to_string_lossy().into_owned());
        host.engine_state.bus_ref().enable_state_events();
        host.engine_state.mark_initialized();
        host.session_set_model(SetModelParams { model: "operator-chosen-model".into() })
            .await
            .unwrap();
        let _ = drain_events(&mut rx);

        host.commit_wired_provider(&(NamedClient::arc("anthropic") as Arc<dyn LlmClient>)).await.expect("a live engine admits it");
        assert_eq!(
            host.current_model(),
            "operator-chosen-model",
            "compaction or a first prompt discovering a credential must not re-pick the model"
        );
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["config"]["next"]["model"], "operator-chosen-model");
        assert_eq!(state["config"]["next"]["providerId"], "anthropic");
        let announced = config_events(&drain_events(&mut rx));
        let last = announced.last().expect("the provider is still announced");
        assert_eq!(last["next"]["providerId"], "anthropic");
        assert_eq!(last["next"]["model"], "operator-chosen-model");
    }

    /// A stopped engine must refuse a prompt outright.
    ///
    /// The transport stays open after `shutdown` (a client can still read
    /// state), so nothing but the claim itself stops a late prompt: it used to
    /// be admitted, set `lifecycle: busy` over `stopped`, run a real model
    /// request, re-enable every configuration commit for the duration, and
    /// publish `ready` when the guard released. Relying on transport EOF to
    /// prevent that is not a guarantee.
    #[tokio::test]
    async fn a_prompt_after_shutdown_is_refused_without_resurrecting_the_engine() {
        let (host, client) = make_capturing_host();
        host.engine_state.bus_ref().enable_state_events();
        host.engine_state.mark_initialized();

        host.shutdown().await.unwrap();
        let stopped = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(stopped["lifecycle"], "stopped");

        let refused = host
            .session_prompt(PromptParams { text: Some("hello".into()), images: None })
            .await;
        assert!(refused.is_err(), "a stopped engine must refuse a prompt: {refused:?}");
        assert!(
            client.last_model().is_none(),
            "no provider request may be made after shutdown"
        );

        let after = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(
            after["lifecycle"], "stopped",
            "a refused prompt must not resurrect a stopped engine"
        );
        assert!(after["turn"].is_null(), "no public turn may be opened");
        assert_eq!(
            after["cursor"], stopped["cursor"],
            "a refused claim must publish nothing at all"
        );
        assert!(
            !*host.turn_active.lock().unwrap(),
            "a refused claim must not leave the runtime slot held"
        );

        // The steering inbox must not have been opened for a turn that never
        // started.
        let steer = host.session_steer(SteerParams { text: "late".into() }).await.unwrap();
        assert_eq!(steer["ok"], false);
        assert_eq!(steer["rejectedReason"], "noActiveTurn");

        // Read-only state stays available — refusing work is not the same as
        // refusing to answer.
        assert_eq!(after["initialized"], true);
    }

    /// The same boundary for every other operation that claims the runtime
    /// slot, taken while the engine is still `stopping` — the window a late
    /// claim actually lands in.
    #[tokio::test]
    async fn slot_claiming_operations_are_refused_while_the_engine_is_stopping() {
        let (host, _client) = make_capturing_host();
        host.engine_state.bus_ref().enable_state_events();
        host.engine_state.mark_initialized();
        host.engine_state.shutdown_started();

        let stopping = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(stopping["lifecycle"], "stopping");

        assert!(host.session_fork(ForkParams::default()).await.is_err(), "fork");
        assert!(host.session_rewind(RewindParams::default()).await.is_err(), "rewind");
        assert!(host.session_compact(CompactParams::default()).await.is_err(), "compact");
        assert!(
            host.session_prompt(PromptParams { text: Some("hi".into()), images: None })
                .await
                .is_err(),
            "prompt"
        );

        let after = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(after["lifecycle"], "stopping", "a refusal must not change the lifecycle");
        assert!(after["turn"].is_null());
        assert_eq!(after["cursor"], stopping["cursor"], "refusals publish nothing");
        assert!(!*host.turn_active.lock().unwrap());
        assert!(
            after.get("lastTurnOutcome").is_none() || after["lastTurnOutcome"].is_null(),
            "a turn that was never admitted has no outcome: {}",
            after["lastTurnOutcome"]
        );
    }

    /// Wiring a provider re-resolves the effort against the **client that is
    /// being wired**, not the one the host is still holding.
    ///
    /// A default model re-picked for the newly connected provider used to
    /// keep whatever level was in force — a level resolved for a different
    /// provider/model pair, quite possibly one the new provider does not
    /// support at all. Resolving it against the old `self.client` (still
    /// `None`, or still the previous provider) answers the wrong question.
    #[tokio::test]
    async fn wiring_a_provider_resolves_effort_against_the_client_being_wired() {
        let dir = tempfile::tempdir().unwrap();
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new(sink, ch, dir.path().to_string_lossy().into_owned());
        host.engine_state.bus_ref().enable_state_events();
        host.engine_state.mark_initialized();

        // A level chosen while nothing is wired: the capability is
        // indeterminate, so the request is accepted optimistically.
        let model = host.current_model();
        host.session_set_effort(SetEffortParams {
            effort: Some("high".into()),
            ..Default::default()
        })
        .await
        .unwrap();
        assert_eq!(host.current_effort(), Some(Effort::High));
        let _ = drain_events(&mut rx);

        // The provider that actually connects advertises no reasoning levels
        // for that model. Asking the *old* client (there is none) would keep
        // `high`; asking the new one is the only correct answer.
        let wired: Arc<dyn LlmClient> = LevelsClient::arc(vec![model_with_levels(&model, &[])]);
        host.commit_wired_provider(&wired).await.expect("a live engine admits it");

        assert_eq!(
            host.current_effort(),
            None,
            "effort must be re-resolved against the provider that was just wired"
        );
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert!(state["config"]["next"]["effort"].is_null());
        assert_eq!(state["config"]["next"]["effortIsAuto"], true);
        assert_eq!(state["config"]["next"]["providerId"], "github-copilot");

        let published = config_events(&drain_events(&mut rx));
        assert_eq!(
            published.len(),
            1,
            "model, effort and provider are one change and one event: {published:?}"
        );
        assert!(published[0]["next"]["effort"].is_null());
        assert_eq!(published[0]["next"]["providerId"], "github-copilot");
    }

    /// A model list request parked on the provider must answer from one
    /// record: the active model it names and the effort it annotates that
    /// model's row with cannot come from two different instants.
    #[tokio::test]
    async fn the_model_list_annotates_the_active_row_from_the_record_it_named() {
        let client = GatedModelsClient::arc(
            "github-copilot",
            vec![
                model_with_levels("model-a", &["low", "medium", "high"]),
                model_with_levels("model-b", &["low", "medium", "high"]),
            ],
        );
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_client(
            Arc::clone(&client) as Arc<dyn LlmClient>,
            sink,
            ch,
            ".".into(),
        );
        host.set_model_for_test("model-a");
        host.commit_config("effort", |rc| rc.effort = Some(Effort::High)).unwrap();

        let mut listing = Box::pin(host.session_models(ModelsParams::default()));
        // Prove the call is parked inside the provider lookup before anything
        // else happens — no sleeps, no scheduling assumptions.
        tokio::select! {
            biased;
            r = &mut listing => panic!("the listing must wait for the provider: {r:?}"),
            entered = client.entered.acquire() => { entered.unwrap().forget(); }
        }

        // A setter lands while the listing is parked.
        host.commit_config("model", |rc| {
            rc.model = "model-b".into();
            rc.effort = Some(Effort::Low);
        })
        .unwrap();

        client.release.add_permits(1);
        let listed = listing.await.unwrap();

        assert_eq!(listed["model"], "model-a", "the answer names the record it read");
        let row = listed["models"]
            .as_array()
            .unwrap()
            .iter()
            .find(|m| m["id"] == "model-a")
            .cloned()
            .expect("the active model is in the list");
        assert_eq!(
            row["effort"], "high",
            "the active row must carry the effort of the model it belongs to, not a later one: {listed}"
        );
    }

    /// The same for `model/reasoningCapability`: the model it reports and the
    /// current level it reports are one record, and the provider it names is
    /// the one they were read against.
    #[tokio::test]
    async fn reasoning_capability_reports_one_coherent_record() {
        let client = GatedModelsClient::arc(
            "github-copilot",
            vec![model_with_levels("model-a", &["low", "medium", "high"])],
        );
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_client(
            Arc::clone(&client) as Arc<dyn LlmClient>,
            sink,
            ch,
            ".".into(),
        );
        host.set_model_for_test("model-a");
        host.commit_config("effort", |rc| rc.effort = Some(Effort::High)).unwrap();

        let mut capability = Box::pin(host.model_reasoning_capability());
        tokio::select! {
            biased;
            r = &mut capability => panic!("the capability lookup must wait for the provider: {r:?}"),
            entered = client.entered.acquire() => { entered.unwrap().forget(); }
        }

        host.commit_config("model", |rc| {
            rc.model = "model-b".into();
            rc.effort = Some(Effort::Low);
        })
        .unwrap();

        client.release.add_permits(1);
        let answer = capability.await.unwrap();

        assert_eq!(answer["model"], "model-a");
        assert_eq!(
            answer["current"], "high",
            "the level reported must belong to the model reported: {answer}"
        );
        assert_eq!(answer["providerId"], "github-copilot");
    }

    /// What the turn *says* it is running and what was actually sent to the
    /// provider are one record, captured once. A setter landing between the
    /// agent being built and the turn publishing its config used to make the
    /// public `activeConfig` describe an agent that was never run.
    #[tokio::test]
    async fn the_published_active_config_describes_the_request_that_was_actually_sent() {
        let (host, client) = make_capturing_host();
        host.engine_state.bus_ref().enable_state_events();
        host.engine_state.mark_initialized();
        host.session_set_system_prompt(SetSystemPromptParams {
            text: Some("Be extremely terse.".into()),
        })
        .await
        .unwrap();

        // A setter lands in the window between building the agent and
        // publishing what the turn runs.
        *host.after_agent_build_hook.lock().unwrap() = Some(Arc::new(|h: &ServeHost| {
            h.set_model_for_test("intruder-model");
            h.set_system_prompt_for_test(None);
        }));

        let captured_active = {
            let host = Arc::clone(&host);
            tokio::spawn(async move {
                host.session_prompt(PromptParams { text: Some("hello".into()), images: None })
                    .await
                    .unwrap();
                host.engine_state.active_turn_config()
            })
        };
        let _ = captured_active.await.unwrap();

        // The turn is over, so read what it published rather than the live
        // state: the last `config.active` announcement describes the run.
        let published = host
            .engine_state
            .bus_ref()
            .get_events(host.engine_state.bus_ref().engine_instance_id(), 0, 500)
            .unwrap()
            .events
            .into_iter()
            .filter(|e| e.method == "event/configChanged" && e.params.get("active").is_some())
            .filter_map(|e| e.params.get("active").cloned())
            .next_back()
            .expect("the turn publishes the config it resolved");

        assert_eq!(
            published["model"], json!(client.last_model().expect("a request was sent")),
            "the published activeConfig must name the model the provider was actually asked for"
        );
        assert_eq!(
            published["systemPromptSource"], "sessionOverride",
            "the source must describe the prompt that was actually sent: {published}"
        );
        assert!(
            client.last_system_prompt().unwrap_or_default().contains("Be extremely terse."),
            "the captured override is what reaches the provider"
        );
        assert_eq!(
            published["effort"].as_str().map(str::to_owned),
            client.last_effort().map(|e| e.as_str().to_owned()),
            "the published effort must be the effort the request carried: {published}"
        );
    }

    /// `initialize(apiKey)` wires a provider and then applies the startup
    /// effort. Both are configuration commits, so they must complete (no
    /// writer-lock re-entrancy) and be announced.
    #[tokio::test]
    async fn initialize_wires_a_provider_and_announces_the_config_it_applied() {
        let dir = tempfile::tempdir().unwrap();
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_optional_client_and_mcp(
            None,
            sink,
            ch,
            dir.path().to_string_lossy().into_owned(),
            crate::mcp::McpBundle::disabled(),
            StartupOptions {
                // Unroutable on purpose: the capability lookup must fail fast
                // rather than reach the network from a test.
                endpoint: Some("http://127.0.0.1:1".into()),
                effort: StartupEffort::Level(Effort::Medium),
                // Explicit, so the assertion does not depend on whichever
                // model the machine running the tests has saved for this
                // provider (the saved row decides effort support).
                model: Some("claude-opus-4-8".into()),
                ..StartupOptions::default()
            },
            None,
            None,
        );
        host.engine_state.bus_ref().enable_state_events();

        // Before anything is wired the provider is genuinely unknown, and
        // must be omitted rather than guessed.
        let before = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert!(
            before["config"]["next"].get("providerId").is_none(),
            "an unwired provider is absent, never a fallback name: {}",
            before["config"]
        );

        let init = tokio::time::timeout(
            std::time::Duration::from_secs(20),
            host.initialize(InitParams {
                protocol_version: PROTOCOL_VERSION.into(),
                api_key: Some("test-key".into()),
                ..Default::default()
            }),
        )
        .await
        .expect("initialize must not deadlock against its own config writer lock")
        .expect("initialize succeeds");
        assert_eq!(init["protocolVersion"], PROTOCOL_VERSION);

        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(
            state["config"]["next"]["providerId"], "anthropic",
            "a wired provider must be reported: {}",
            state["config"]
        );
        assert_eq!(state["config"]["next"]["model"], host.current_model());
        assert_eq!(
            state["config"]["next"]["effort"].as_str().map(str::to_owned),
            host.current_effort().map(|e| e.as_str().to_owned()),
            "the applied startup effort must be the announced one"
        );

        let published = config_events(&drain_events(&mut rx));
        assert!(
            !published.is_empty(),
            "wiring a provider changes the next turn's config and must be announced"
        );
        let last = published.last().unwrap();
        assert_eq!(last["next"]["providerId"], "anthropic");
        assert_eq!(last["next"]["model"], state["config"]["next"]["model"]);
        assert_eq!(last["next"]["effort"], state["config"]["next"]["effort"]);
    }

    /// `config/describe` answers from one coherent snapshot: the prompt text
    /// it returns, the source it reports, and the next config a snapshot
    /// shows must all describe the same instant.
    #[tokio::test]
    async fn config_describe_is_coherent_across_a_clear_and_a_switch() {
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = commit_host(dir.path());

        let prompt_entry = |described: &Value| -> Value {
            described["entries"]
                .as_array()
                .unwrap()
                .iter()
                .find(|e| e["key"] == "systemPrompt")
                .cloned()
                .expect("a systemPrompt entry")
        };

        host.session_set_system_prompt(SetSystemPromptParams {
            text: Some("Be terse.".into()),
        })
        .await
        .unwrap();
        let described = host.config_describe().await.unwrap();
        let entry = prompt_entry(&described);
        assert_eq!(entry["value"]["source"], "sessionOverride");
        assert_eq!(entry["value"]["text"], "Be terse.");
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["config"]["next"]["systemPromptSource"], "sessionOverride");

        host.session_set_model(SetModelParams { model: "model-b".into() }).await.unwrap();
        host.session_set_system_prompt(SetSystemPromptParams { text: None }).await.unwrap();

        let described = host.config_describe().await.unwrap();
        let entry = prompt_entry(&described);
        assert_eq!(entry["value"]["source"], "default");
        assert!(entry["value"]["text"].is_null());
        let model = described["entries"]
            .as_array()
            .unwrap()
            .iter()
            .find(|e| e["key"] == "model")
            .cloned()
            .expect("a model entry");
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(model["value"], "model-b");
        assert_eq!(state["config"]["next"]["model"], "model-b");
        assert_eq!(state["config"]["next"]["systemPromptSource"], "default");
    }

    #[tokio::test]
    async fn a_finished_turn_still_holding_the_slot_never_reports_ready() {
        // The window this closes: `end_turn` used to publish `ready` while the
        // guard still owned `turn_active`, so `session/getState` said the
        // engine was available and `session/prompt` answered "busy".
        let dir = tempfile::tempdir().unwrap();
        let (host, mut rx) = host_with_events(dir.path());

        let guard = host.try_claim_turn("t1", "hello", TurnKind::Prompt).expect("claim succeeds");
        // Execution has finished: terminal outcome reached, slot still held.
        assert!(host.engine_state.end_turn(TurnEnd {
            turn_id: "t1".into(),
            stop_reason: Some("end_turn".into()),
            history_length: Some(2),
            ..Default::default()
        }));

        let finalizing = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(
            finalizing["lifecycle"], "busy",
            "the engine still owns the single-flight slot, so it must not advertise itself as ready"
        );
        assert!(finalizing["turn"].is_null(), "the turn itself is terminal");
        assert_eq!(finalizing["historyLength"], 2, "the committed fence already moved");
        assert!(
            host.try_claim_turn("t2", "", TurnKind::Prompt).is_err(),
            "a prompt really is refused in this window — which is exactly why `ready` would be a lie"
        );

        drop(guard);

        let ready = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(ready["lifecycle"], "ready");
        assert_eq!(ready["lastTurnOutcome"]["turnId"], "t1");
        assert!(
            host.try_claim_turn("t2", "", TurnKind::Prompt).is_ok(),
            "once the snapshot says ready, a prompt must actually be accepted"
        );

        // And the availability change reached a state-events client.
        let events = drain_events(&mut rx);
        assert!(
            events.iter().any(|(m, p)| m == "event/lifecycle" && p["lifecycle"] == "ready"),
            "becoming available again must be published, not discovered by polling"
        );
        assert!(events.iter().any(|(m, p)| m == "event/turnEnded" && p["turnId"] == "t1"));
    }

    /// A maintenance/compaction turn never reaches `turn_config_resolved`, so
    /// its `activeConfig` is the placeholder built when the slot was claimed.
    /// That placeholder must name the wired provider too, or the turn section
    /// reports "unknown" for a fact the engine plainly has.
    #[tokio::test]
    async fn a_turns_placeholder_config_names_the_wired_provider_too() {
        let dir = tempfile::tempdir().unwrap();
        let host = make_host_in_dir(dir.path().to_str().unwrap(), ScriptedClient::new(vec![]));
        host.engine_state.mark_initialized();

        let _turn = host
            .try_claim_turn("t1", "", TurnKind::Maintenance)
            .expect("the slot is free");
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(
            state["turn"]["activeConfig"]["providerId"], "scripted",
            "the placeholder must be honest, not a permanent unknown: {}",
            state["turn"]
        );
    }

    /// The turn section must carry the engine's own monotonic duration, so a
    /// reconnecting client seeds its timer from the server rather than from a
    /// clock it cannot trust.
    #[tokio::test]
    async fn a_state_snapshot_carries_the_servers_own_turn_duration() {
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = host_with_events(dir.path());
        let _turn = host
            .try_claim_turn("t1", "hello", TurnKind::Prompt)
            .expect("the slot is free");

        let first = host.session_get_state(GetStateParams::default()).await.unwrap();
        let started_at = first["turn"]["startedAt"].as_str().expect("UTC start").to_string();
        let elapsed = first["turn"]["elapsedMs"].as_i64().expect("server-measured duration");
        assert!(first["turn"]["phaseElapsedMs"].as_i64().is_some(), "and the phase clock too");

        tokio::time::sleep(Duration::from_millis(30)).await;
        let second = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert!(
            second["turn"]["elapsedMs"].as_i64().unwrap() >= elapsed,
            "a resync must continue the timer, never restart it"
        );
        assert_eq!(
            second["turn"]["startedAt"].as_str().unwrap(),
            started_at,
            "the UTC start is unchanged — elapsedMs is additional, not a replacement"
        );
    }

    // ── Provenance of a reverse request (background subagents) ────────────

    /// Background subagents and scheduled runs share the engine's
    /// `PromptChannel` (their `AgentEvent`s go to a null sink, but their
    /// permission prompts reach the wire) and they run on their own
    /// `tokio::spawn`ed tasks, so they outlive the turn that started them.
    ///
    /// Attributing their approvals to "whatever turn happens to be running
    /// now" invents provenance: a background approval raised during an
    /// unrelated *later* foreground turn would be stamped with that turn's id
    /// and would drag its public phase to `awaitingUserInput` while it was
    /// streaming perfectly happily. Unknown origin must be `null`.
    #[tokio::test]
    async fn a_background_request_is_not_attributed_to_an_unrelated_foreground_turn() {
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = host_with_events(dir.path());
        let _turn = host
            .try_claim_turn("t-foreground", "hi", TurnKind::Prompt)
            .expect("the slot is free");
        host.engine_state.tool_batch_started("b1", vec!["c1".into()]);

        // A detached task is exactly what `SubagentHost::spawn` does for a
        // background subagent: it inherits no execution scope.
        let registry = Arc::clone(host.prompt_channel.registry());
        let _keep = tokio::spawn(async move {
            let (numeric, _handle) = registry.mint_id();
            registry.register(
                numeric,
                coda_proto::state::PendingRequestKind::Permission,
                json!({ "toolName": "run_command" }),
                None,
            )
        })
        .await
        .expect("the background task runs");

        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["requests"].as_array().map(Vec::len), Some(1), "still discoverable");
        assert!(
            state["requests"][0].get("turnId").is_none(),
            "unknown origin must be omitted, never borrowed from an unrelated turn: {}",
            state["requests"][0]
        );
        assert_eq!(
            state["turn"]["phase"], "runningTools",
            "and the foreground turn's public phase must be untouched: {}",
            state["turn"]
        );
    }

    /// The counterpart, so the fix above cannot be satisfied by simply never
    /// attributing anything: work the turn awaits **inline** — which is every
    /// tool permission check, question, plan approval and foreground subagent
    /// — is attributed to it and does park its phase.
    #[tokio::test]
    async fn the_running_turns_own_request_is_attributed_and_parks_its_phase() {
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = host_with_events(dir.path());
        let _turn = host
            .try_claim_turn("t-foreground", "hi", TurnKind::Prompt)
            .expect("the slot is free");
        host.engine_state.tool_batch_started("b1", vec!["c1".into()]);

        let registry = Arc::clone(host.prompt_channel.registry());
        let _keep = crate::turn_scope::in_turn("t-foreground", async {
            let (numeric, _handle) = registry.mint_id();
            registry.register(
                numeric,
                coda_proto::state::PendingRequestKind::Question,
                json!({ "question": "which one?" }),
                None,
            )
        })
        .await;

        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["requests"][0]["turnId"], "t-foreground");
        assert_eq!(
            state["turn"]["phase"], "awaitingUserInput",
            "the turn really is blocked on the operator: {}",
            state["turn"]
        );
    }

    /// A background approval outstanding across a turn boundary must not
    /// leak into the next turn's phase either — the case the shared
    /// `PromptChannel` makes possible.
    #[tokio::test]
    async fn a_background_request_outstanding_across_a_turn_boundary_stays_unattributed() {
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = host_with_events(dir.path());

        // Raised while the first turn runs, from detached execution.
        let first = host.try_claim_turn("t-first", "one", TurnKind::Prompt).expect("slot");
        let registry = Arc::clone(host.prompt_channel.registry());
        let _keep = tokio::spawn(async move {
            let (numeric, _handle) = registry.mint_id();
            registry.register(
                numeric,
                coda_proto::state::PendingRequestKind::Permission,
                json!({ "toolName": "run_command" }),
                None,
            )
        })
        .await
        .expect("the background task runs");
        drop(first);

        // A second, unrelated turn opens while it is still outstanding.
        let _second = host.try_claim_turn("t-second", "two", TurnKind::Prompt).expect("slot");
        host.engine_state.tool_batch_started("b1", vec!["c1".into()]);

        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["turn"]["turnId"], "t-second");
        assert!(
            state["requests"][0].get("turnId").is_none(),
            "an approval from before this turn must not acquire its identity: {}",
            state["requests"][0]
        );
        assert_eq!(
            state["turn"]["phase"], "runningTools",
            "and must not park the new turn on an operator it is not waiting for: {}",
            state["turn"]
        );
    }

    // ── F3 (review): epoch, fence and messages move as one ────────────────

    /// `config.next.providerId` is the provider the *next* turn would use. It
    /// was hard-wired to `None` in `session/getState`, so a client saw
    /// "unknown" even with a real client wired — and `config/describe` was the
    /// only surface that told the truth. "Unknown" must mean genuinely
    /// unknown, not "this code path did not bother to look".
    #[tokio::test]
    async fn get_state_reports_the_real_provider_in_the_next_config() {
        let dir = tempfile::tempdir().unwrap();
        let host = make_host_in_dir(dir.path().to_str().unwrap(), ScriptedClient::new(vec![]));
        host.engine_state.mark_initialized();

        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(
            state["config"]["next"]["providerId"], "scripted",
            "a wired client's provider must be reported, not omitted as unknown: {}",
            state["config"]
        );

        // And it agrees with the other surface that reports the same fact.
        let described = host.config_describe().await.unwrap();
        let provider = described["entries"]
            .as_array()
            .unwrap()
            .iter()
            .find(|e| e["key"] == "provider")
            .expect("a provider entry");
        assert_eq!(
            provider["value"], state["config"]["next"]["providerId"],
            "config/describe and getState must not disagree about the provider"
        );
    }

    /// The counterpart: with no client wired the provider genuinely is not
    /// known, and it must be **omitted** rather than defaulted to a plausible
    /// name.
    #[tokio::test]
    async fn get_state_omits_the_provider_when_none_is_actually_wired() {
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = host_with_events(dir.path());

        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert!(
            state["config"]["next"].get("providerId").is_none(),
            "'not resolved yet' must be absent, never null and never a guess: {}",
            state["config"]
        );
    }

    /// A `stateEvents` client that follows `event/configChanged` must end up
    /// with the same `config.next` a snapshot would give it. Publishing the
    /// provider in one and omitting it in the other makes a converging client
    /// *lose* the provider it already knew.
    #[tokio::test]
    async fn a_published_next_config_carries_the_same_provider_the_snapshot_does() {
        let dir = tempfile::tempdir().unwrap();
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_client(
            ScriptedClient::new(vec![]),
            sink,
            ch,
            dir.path().to_string_lossy().into_owned(),
        );
        host.engine_state.bus_ref().enable_state_events();
        host.engine_state.mark_initialized();

        host.session_set_permission_mode(SetPermissionModeParams { mode: "plan".into() })
            .await
            .unwrap();

        let events = drain_events(&mut rx);
        let (_, changed) = events
            .iter()
            .rev()
            .find(|(m, _)| m == "event/configChanged")
            .expect("a config change must be published");
        let snapshot = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(
            changed["next"]["providerId"], snapshot["config"]["next"]["providerId"],
            "a converging client must not lose the provider it already had: {changed}"
        );
        assert_eq!(changed["next"]["providerId"], "scripted");
    }

    /// A history reset (`rewind`/`fork`/`compact`/`resume`) replaces the
    /// committed messages **and** bumps `historyEpoch`/`historyLength`. Those
    /// were two separate critical sections: the HISTORY lock was released
    /// after the replacement and re-taken (as STATE) for the announcement, so
    /// a concurrent `session/getHistory` could slip in between and be handed
    /// a page from the *new* conversation while its pinned `historyEpoch`
    /// still matched the *old* one — the exact fence the parameter exists to
    /// provide.
    ///
    /// The invariant asserted here is the client-visible one: any page the
    /// engine accepts under epoch `E` describes the conversation as it was at
    /// epoch `E`. A stale-epoch rejection is the correct alternative answer;
    /// a silently-reset page is not.
    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn a_page_accepted_under_a_pinned_epoch_never_describes_a_reset_conversation() {
        const COMMITTED: usize = 6;
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = host_with_events(dir.path());

        for round in 0..30 {
            *host.session.history.lock().unwrap() =
                (0..COMMITTED).map(|i| Message::user(format!("m{i}"))).collect();
            host.engine_state.session_changed("resume", format!("s{round}"), COMMITTED as i64);
            let pinned = host.engine_state.history_view().history_epoch;

            let reader = {
                let host = Arc::clone(&host);
                tokio::spawn(async move {
                    for _ in 0..2_000 {
                        let p = GetHistoryParams {
                            history_epoch: Some(pinned),
                            limit: Some(500),
                            ..Default::default()
                        };
                        if let Ok(v) = host.session_get_history(p).await {
                            assert_eq!(
                                v["historyLength"], COMMITTED as i64,
                                "a page accepted under epoch {pinned} must describe that epoch's \
                                 conversation, not the one the reset produced: {v}"
                            );
                            assert_eq!(v["historyEpoch"], pinned);
                            assert_eq!(v["entries"].as_array().unwrap().len(), COMMITTED);
                        }
                    }
                })
            };

            host.session_rewind(RewindParams { n: Some(4) }).await.unwrap();
            reader.await.expect("the reader must not observe a torn reset");

            // And afterwards the two really did move together.
            let guard = host.session.history.lock().unwrap();
            let view = host.engine_state.history_view();
            assert_eq!(guard.len(), 2);
            assert_eq!(view.history_length, guard.len() as i64);
            assert_ne!(view.history_epoch, pinned, "a reset must always bump the epoch");
        }
    }

    /// Every reset path must announce the length it *actually* committed,
    /// read at the instant of the announcement rather than from a copy taken
    /// before an `await`. `session/fork` announced `history.len()` captured
    /// before `fork_session(...).await`, which is only the same number by
    /// luck.
    #[tokio::test]
    async fn every_reset_path_announces_the_length_it_actually_committed() {
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = host_with_events(dir.path());
        *host.session.history.lock().unwrap() =
            (0..5).map(|i| Message::user(format!("m{i}"))).collect();

        let check = |label: &str| {
            let committed = host.session.history.lock().unwrap().len() as i64;
            let view = host.engine_state.history_view();
            assert_eq!(
                view.history_length, committed,
                "after {label} the announced fence must equal the committed length"
            );
        };

        host.session_fork(ForkParams {}).await.unwrap();
        check("fork");
        host.session_rewind(RewindParams { n: Some(2) }).await.unwrap();
        check("rewind");
        host.session_rewind(RewindParams { n: Some(100) }).await.unwrap();
        check("rewind past the start");
    }

    // ── I2: administrative history mutations own the slot ─────────────────

    #[tokio::test]
    async fn fork_and_rewind_own_the_turn_slot_for_their_whole_duration() {
        let dir = tempfile::tempdir().unwrap();
        let (host, mut rx) = host_with_events(dir.path());
        *host.session.history.lock().unwrap() =
            vec![Message::user("hi"), Message::assistant("hello")];

        host.session_fork(ForkParams {}).await.unwrap();
        let events = drain_events(&mut rx);
        assert!(
            events.iter().any(|(m, p)| m == "event/activity" && p["phase"] == "maintenance"),
            "a fork must claim the slot and say so — reading `turn_active` and then awaiting let a prompt race the history replacement"
        );
        assert!(events.iter().any(|(m, _)| m == "event/sessionChanged"));
        assert!(events.iter().any(|(m, _)| m == "event/turnEnded"));
        assert_eq!(
            host.session_get_state(GetStateParams::default()).await.unwrap()["lifecycle"],
            "ready",
            "the slot must be released again afterwards"
        );

        host.session_rewind(RewindParams { n: Some(1) }).await.unwrap();
        let events = drain_events(&mut rx);
        assert!(events.iter().any(|(m, p)| m == "event/activity" && p["phase"] == "maintenance"));
        assert!(host.try_claim_turn("after", "", TurnKind::Prompt).is_ok());
    }

    #[tokio::test]
    async fn a_prompt_and_a_history_mutation_can_never_overlap_in_either_order() {
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = host_with_events(dir.path());

        // A maintenance operation owns the slot: a prompt is refused.
        {
            let _maintenance =
                host.try_claim_turn("fork-1", "", TurnKind::Maintenance).expect("claim succeeds");
            let err = host
                .session_prompt(PromptParams { text: Some("hello".into()), images: None })
                .await
                .expect_err("a prompt must not run against a half-replaced history");
            assert!(err.message.contains("busy"));
        }

        // And the other way round: a prompt owns the slot, so fork and rewind
        // are refused instead of racing it.
        {
            let _prompt = host.try_claim_turn("p-1", "", TurnKind::Prompt).expect("claim succeeds");
            assert!(host.session_fork(ForkParams {}).await.is_err());
            assert!(host.session_rewind(RewindParams { n: Some(1) }).await.is_err());
        }
        assert!(host.try_claim_turn("after", "", TurnKind::Prompt).is_ok());
    }

    // ── I3: a publicly `preparing` turn must accept steering ──────────────

    #[tokio::test]
    async fn steering_is_accepted_as_soon_as_a_turn_is_publicly_preparing() {
        // `Agent::run` used to be the first thing to unseal the inbox, so a
        // client that saw `preparing` and steered immediately was told
        // `turnEnding` — the queue was still sealed by the *previous* turn.
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = host_with_events(dir.path());

        // First turn runs and finishes, sealing the inbox exactly as
        // `run_prompt_inner` and the guard do.
        {
            let _first = host.try_claim_turn("t1", "first", TurnKind::Prompt).expect("claim succeeds");
            host.session.steering.close_for_turn();
        }

        let _second = host.try_claim_turn("t2", "second", TurnKind::Prompt).expect("claim succeeds");
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["turn"]["phase"], "preparing");

        let resp = host
            .session_steer(SteerParams { text: "steer the new turn".into() })
            .await
            .unwrap();
        assert_eq!(
            resp["ok"], true,
            "a turn the engine publicly reports as preparing must accept steering: {resp}"
        );
        assert!(resp["rejectedReason"].is_null());
        assert!(host.session.steering.has_pending());
    }

    /// A follow-up sent while the model is working must be visible in what
    /// the engine reports *while the turn is still running*, not only once it
    /// commits — and it must appear exactly once afterwards.
    ///
    /// The whole round trip, through the real agent loop: the operator steers
    /// mid-turn, the agent drains the inbox before its next model request, and
    /// a client polling `session/getState` / `session/getHistory` in the
    /// window that follows is shown the message the model actually received.
    /// Before this, that window reported a `delivered` outcome over a
    /// conversation with no trace of the text in it.
    #[tokio::test]
    async fn a_delivered_steer_is_visible_mid_turn_and_committed_exactly_once() {
        use coda_llm::anthropic::StreamEvent;
        use std::sync::atomic::{AtomicUsize, Ordering};

        struct PausingClient {
            calls: AtomicUsize,
            paused: [tokio::sync::Notify; 2],
            release: [tokio::sync::Notify; 2],
            script: Mutex<std::collections::VecDeque<Vec<StreamEvent>>>,
        }
        #[async_trait]
        impl LlmClient for PausingClient {
            fn provider_id(&self) -> &str {
                "scripted"
            }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                let call = self.calls.fetch_add(1, Ordering::SeqCst);
                if call < 2 {
                    self.paused[call].notify_one();
                    self.release[call].notified().await;
                }
                let events = self.script.lock().unwrap().pop_front().expect("a scripted turn");
                let (tx, rx) = mpsc::channel(64);
                tokio::spawn(async move {
                    for event in events {
                        let _ = tx.send(Ok(event)).await;
                    }
                });
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }

        let dir = tempfile::tempdir().unwrap();
        let fixture = dir.path().join("fixture.txt");
        std::fs::write(&fixture, "fixture contents").unwrap();
        let client = Arc::new(PausingClient {
            calls: AtomicUsize::new(0),
            paused: [tokio::sync::Notify::new(), tokio::sync::Notify::new()],
            release: [tokio::sync::Notify::new(), tokio::sync::Notify::new()],
            script: Mutex::new(
                vec![outside_read_turn(&fixture, "c1"), file_test_done()].into_iter().collect(),
            ),
        });
        let host = make_host_in_dir(dir.path().to_str().unwrap(), client.clone());
        host.engine_state.mark_initialized();

        let run = {
            let host = host.clone();
            tokio::spawn(async move {
                host.session_prompt(PromptParams { text: Some("start".into()), images: None }).await
            })
        };

        // The first model request is held open, which is where a real
        // operator would type the follow-up.
        tokio::time::timeout(Duration::from_secs(5), client.paused[0].notified()).await.unwrap();
        let steer = host
            .session_steer(SteerParams { text: "operator correction".into() })
            .await
            .unwrap();
        assert_eq!(steer["ok"], true, "{steer}");
        let message_id = steer["messageId"].as_str().expect("an accepted steer is given an id");
        client.release[0].notify_one();

        // The agent drains the inbox before its *next* request, so by the
        // time the second one is held open the message has reached the model.
        tokio::time::timeout(Duration::from_secs(5), client.paused[1].notified()).await.unwrap();

        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["steering"]["pendingCount"], 0);
        assert!(
            state["steering"]["outcomes"]
                .as_array()
                .unwrap()
                .iter()
                .any(|o| o["messageId"] == message_id && o["outcome"] == "delivered"),
            "the delivery is reported: {}",
            state["steering"]
        );
        let live = state["turn"]["liveEntries"].as_array().expect("a turn is running");
        let steered: Vec<&Value> = live
            .iter()
            .filter(|entry| entry["steeringMessageId"] == message_id)
            .collect();
        assert_eq!(
            steered.len(),
            1,
            "the same snapshot that reports the delivery must contain the message: {live:#?}"
        );
        assert_eq!(steered[0]["blocks"][0]["text"], "operator correction");
        assert_eq!(steered[0]["role"], "user");
        assert_eq!(steered[0]["entryKind"], "userPrompt");

        let history = host
            .session_get_history(GetHistoryParams { include_live: Some(true), ..Default::default() })
            .await
            .unwrap();
        assert_eq!(
            history["liveEntries"]
                .as_array()
                .unwrap()
                .iter()
                .filter(|e| e["blocks"][0]["text"] == "operator correction")
                .count(),
            1,
            "a client rebuilding the conversation mid-turn must see it too"
        );
        assert!(
            !history["entries"]
                .as_array()
                .unwrap()
                .iter()
                .any(|e| e["blocks"][0]["text"] == "operator correction"),
            "it is not committed yet; the live projection is where it lives"
        );

        client.release[1].notify_one();
        let result = tokio::time::timeout(Duration::from_secs(5), run).await.unwrap().unwrap().unwrap();
        assert_eq!(result["ok"], true);

        // Committed exactly once, and the live projection that carried it is
        // gone — so a client that appends `entries ++ liveEntries` cannot end
        // up with the message twice.
        let history = host
            .session_get_history(GetHistoryParams { include_live: Some(true), ..Default::default() })
            .await
            .unwrap();
        let committed = history["entries"].as_array().unwrap();
        assert_eq!(
            committed
                .iter()
                .flat_map(|e| e["blocks"].as_array().unwrap())
                .filter(|b| b["text"] == "operator correction")
                .count(),
            1,
            "the follow-up must be persisted once: {committed:#?}"
        );
        assert!(
            history["liveEntries"].as_array().is_none_or(|live| live.is_empty()),
            "the turn is over; nothing is still live"
        );
    }

    #[tokio::test]
    async fn claiming_a_turn_never_discards_a_message_already_queued() {
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = host_with_events(dir.path());
        host.session.steering.open_for_turn();
        let queued = host.session.steering.enqueue("queued before the claim").expect("accepted");

        let _turn = host.try_claim_turn("t1", "go", TurnKind::Prompt).expect("claim succeeds");
        let pending = host.engine_state.steering_pending_snapshot();
        assert_eq!(
            pending.iter().filter(|p| p.message_id == queued.id).count(),
            1,
            "opening the inbox for a new turn must only unseal it, never clear it"
        );
        assert!(host.session.steering.has_pending());
    }

    #[tokio::test]
    async fn a_compaction_does_not_accept_steering_it_could_never_deliver() {
        let dir = tempfile::tempdir().unwrap();
        let (host, _rx) = host_with_events(dir.path());
        let _compaction =
            host.try_claim_turn("c1", "", TurnKind::Compaction).expect("claim succeeds");
        let resp = host.session_steer(SteerParams { text: "steer".into() }).await.unwrap();
        assert_eq!(resp["ok"], false);
        assert_eq!(
            resp["rejectedReason"], "turnEnding",
            "no agent loop is running, so a queued message could only ever be dropped"
        );
    }

    // ── S3: a polled outcome must never carry provider text ───────────────

    #[tokio::test]
    async fn a_failed_turns_public_outcome_never_carries_raw_provider_text() {
        use async_trait::async_trait;

        const SENTINEL: &str = "sk-live-SENTINEL-DO-NOT-LEAK";

        struct LeakyClient;
        #[async_trait]
        impl LlmClient for LeakyClient {
            fn provider_id(&self) -> &str {
                "scripted"
            }
            async fn stream(
                &self,
                _: coda_llm::ChatRequest,
            ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                Err(coda_llm::LlmError::Api {
                    status: 400,
                    message: format!("Missing required parameter: 'input[7].summary'. token={SENTINEL}"),
                    kind: coda_llm::FailureKind::Permanent,
                    retry_after: None,
                    body: Some(format!(
                        r#"{{"error":{{"type":"invalid_request_error","param":"input[7].summary","message":"token={SENTINEL}"}}}}"#
                    )),
                })
            }
        }

        let dir = tempfile::tempdir().unwrap();
        let host = make_host_in_dir(dir.path().to_str().unwrap(), Arc::new(LeakyClient));
        host.engine_state.mark_initialized();

        let result = host
            .session_prompt(PromptParams { text: Some("hello".into()), images: None })
            .await
            .expect("session_prompt answers even on turn failure");
        // The raw message is still returned once, synchronously, to the caller.
        assert!(result["error"].as_str().unwrap().contains(SENTINEL));

        // But the indefinitely-pollable state carries only a classification.
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        let serialised = serde_json::to_string(&state).unwrap();
        assert!(
            !serialised.contains(SENTINEL),
            "lastTurnOutcome is polled for the rest of the session; it must never retain provider text"
        );
        assert!(!serialised.contains("Missing required parameter"));
        let error = &state["lastTurnOutcome"]["error"];
        assert_eq!(error["category"], "llm.client_error");
        assert_eq!(error["status"], 400);
        assert_eq!(
            error["parameter"], "input[7].summary",
            "the one allowlisted, bounded field is still reported"
        );
    }

    // ── S4: negotiation must precede anything gated ───────────────────────

    #[tokio::test]
    async fn a_resuming_client_that_negotiated_state_events_receives_the_session_change() {
        let dir = tempfile::tempdir().unwrap();
        // Persist a session to resume.
        let store = SessionTranscriptStore::new(dir.path().to_str().unwrap());
        let session_id = uuid::Uuid::new_v4().to_string();
        store
            .save(&session_id, &[Message::user("hi"), Message::assistant("hello")], None)
            .await
            .expect("seeding a resumable transcript must succeed");

        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new(sink, ch, dir.path().to_string_lossy().into_owned());

        host.initialize(InitParams {
            session_id: Some(session_id.clone()),
            client_capabilities: Some(coda_proto::messages::ClientCapabilities {
                state_events: Some(true),
                ..Default::default()
            }),
            ..Default::default()
        })
        .await
        .expect("resume succeeds");

        let events = drain_events(&mut rx);
        assert!(
            events
                .iter()
                .any(|(m, p)| m == "event/sessionChanged" && p["reason"] == "resume"),
            "negotiation happens first, so the resume's own sessionChanged reaches the client that asked for it"
        );
        assert_eq!(
            host.session_get_state(GetStateParams::default()).await.unwrap()["historyEpoch"],
            1
        );
    }

    // ── S5: shutdown states are published, not decorative ─────────────────

    #[tokio::test]
    async fn shutdown_reports_stopping_and_then_stopped() {
        let dir = tempfile::tempdir().unwrap();
        let (host, mut rx) = host_with_events(dir.path());
        host.shutdown().await.unwrap();

        let lifecycles: Vec<String> = drain_events(&mut rx)
            .into_iter()
            .filter(|(m, _)| m == "event/lifecycle")
            .map(|(_, p)| p["lifecycle"].as_str().unwrap().to_string())
            .collect();
        assert!(lifecycles.contains(&"stopping".to_string()));
        assert_eq!(lifecycles.last().map(String::as_str), Some("stopped"));
        assert_eq!(
            host.session_get_state(GetStateParams::default()).await.unwrap()["lifecycle"],
            "stopped"
        );
    }

    /// Diagnostic: does the engine find a usable provider on this machine?
    ///
    /// Touches the credential store and may perform a token exchange, so it is
    /// ignored by default. Run explicitly with:
    /// `cargo test -p coda-serve credential_diagnostic -- --ignored --nocapture`
    #[ignore]
    #[tokio::test]
    async fn credential_diagnostic() {
        let storage = match credential_storage() {
            Ok(storage) => storage,
            Err(e) => {
                eprintln!("credential storage unavailable: {e}");
                return;
            }
        };
        eprintln!("backend: {:?} at {}", storage.backend, storage.primary_dir.display());
        match storage.store.get("llmauth:github-copilot").await {
            Ok(Some(v)) => eprintln!("raw credential read: {} bytes", v.len()),
            Ok(None) => eprintln!("raw credential: NOT FOUND"),
            Err(e) => eprintln!("raw credential read failed: {e}"),
        }

        // Uses the same profile-scoped context the engine uses, so the saved
        // domain and the environment are read exactly as production reads them.
        let context = match ProviderContext::from_env() {
            Ok(context) => context,
            Err(e) => {
                eprintln!("provider context unavailable: {e}");
                return;
            }
        };
        let auth_config = match context.copilot_config() {
            Ok(c) => {
                eprintln!("auth config: api_base_url = {}", c.api_base_url);
                c
            }
            Err(e) => {
                eprintln!("auth config error: {e}");
                return;
            }
        };
        let manager = Arc::new(CredentialManager::from_storage(
            &storage,
            [Arc::new(coda_auth::provider::CopilotProvider::new(auth_config))
                as Arc<dyn AuthProvider>],
        ));
        match manager.get_credential("github-copilot").await {
            Ok(Some(_)) => eprintln!("manager: credential OK"),
            Ok(None) => eprintln!("manager: NONE"),
            Err(e) => eprintln!("manager: ERROR {e}"),
        }

        let (client, diag) = try_build_client_with_diagnostic(None).await;
        if let Some(d) = diag {
            eprintln!("client diagnostic: {d}");
        }
        match client {
            Some(c) => match c.list_models().await {
                Ok(m) => eprintln!("client built; models: {}", m.len()),
                Err(e) => eprintln!("client built; list_models failed: {e}"),
            },
            None => eprintln!("client: NOT BUILT"),
        }
    }

    #[tokio::test]
    async fn initialize_returns_32002_for_session_resume() {
        let host = make_host();
        let err = host
            .initialize(InitParams {
                protocol_version: "1".into(),
                session_id: Some("old-id".into()),
                api_key: None,
                client_info: None,
                ..Default::default()
            })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32002);
    }

    #[tokio::test]
    async fn initialize_always_succeeds() {
        // initialize must succeed even without credentials (engine_contract test
        // calls it without an apiKey).
        let host = make_host();
        let result = host.initialize(InitParams::default()).await.unwrap();
        assert_eq!(result["protocolVersion"], "1");
        assert!(result["sessionId"].is_string());
        assert!(result.get("telemetryLogPath").is_none(), "must omit absent telemetryLogPath");
    }

    #[tokio::test]
    async fn initialization_refuses_an_active_turn_before_changing_state() {
        let host = make_host();
        host.initialize(InitParams::default()).await.unwrap();
        let _turn = host.try_claim_turn("running", "keep this turn", TurnKind::Prompt).unwrap();
        let before = host.session_get_state(GetStateParams::default()).await.unwrap();
        let result = host.initialize(InitParams::default()).await;
        assert!(result.is_err(), "initialize must not mutate a running session");
        let after = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(after["cursor"], before["cursor"], "a refused initialize must publish nothing");
        assert_eq!(after["turn"]["turnId"], "running");
        assert_eq!(after["lifecycle"], "busy");
    }

    #[tokio::test]
    async fn initialization_owns_the_slot_across_awaits_without_inventing_a_turn() {
        let host = make_host();
        let client = host.client.lock().await;
        let mut initializing = Box::pin(host.initialize(InitParams {
            api_key: Some("test-initialize-key".into()),
            ..Default::default()
        }));
        tokio::select! {
            biased;
            result = &mut initializing => panic!("initialize must wait for the client lock: {result:?}"),
            _ = std::future::ready(()) => {}
        }
        assert!(
            host.try_claim_turn("racing-prompt", "", TurnKind::Prompt).is_err(),
            "a prompt must not run while initialize is waiting"
        );
        assert!(
            host.initialize(InitParams::default()).await.is_err(),
            "a second initialize must not race the first"
        );
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["lifecycle"], "initializing");
        assert!(state.get("turn").is_none());
        let steering = host.session_steer(SteerParams { text: "not a future prompt".into() }).await.unwrap();
        assert_eq!(steering["ok"], false, "handshakes cannot consume steering");
        assert_eq!(steering["rejectedReason"], "noActiveTurn");
        drop(client);
        let response = initializing.await.unwrap();
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["lifecycle"], "ready");
        assert_eq!(state["initialized"], true);
        assert_eq!(response["eventCursor"], state["cursor"]);
        assert!(state.get("lastTurnOutcome").is_none(), "handshake is not a model turn");
        assert!(host.try_claim_turn("next", "", TurnKind::Prompt).is_ok());
    }

    #[tokio::test]
    async fn initialization_cancellation_releases_the_slot_without_completing_handshake() {
        let host = make_host();
        let model = host.current_model();
        let provider = host.wired_provider_id();
        let client = host.client.lock().await;
        let mut initializing = Box::pin(host.initialize(InitParams {
            api_key: Some("test-initialize-key".into()),
            ..Default::default()
        }));
        tokio::select! {
            biased;
            result = &mut initializing => panic!("initialize must wait for the client lock: {result:?}"),
            _ = std::future::ready(()) => {}
        }
        assert!(*host.turn_active.lock().unwrap(), "initialize must own the shared slot");
        drop(initializing);
        drop(client);
        assert!(!*host.turn_active.lock().unwrap());
        assert!(!host.is_initialized());
        assert_eq!(host.current_model(), model, "a cancelled client replacement must not change its model mirror");
        assert_eq!(host.wired_provider_id(), provider, "a cancelled client replacement must not change its provider mirror");
        host.initialize(InitParams::default()).await.unwrap();
        assert!(host.is_initialized());
    }

    #[tokio::test]
    async fn initialization_refuses_a_stopped_engine() {
        let host = make_host();
        host.engine_state.shutdown_started();
        host.engine_state.shutdown_completed();
        assert!(host.initialize(InitParams::default()).await.is_err());
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["lifecycle"], "stopped");
        assert_eq!(state["initialized"], false);
    }

    #[tokio::test]
    async fn initialization_cannot_complete_after_shutdown() {
        let host = make_host();
        let client = host.client.lock().await;
        let mut initializing = Box::pin(host.initialize(InitParams {
            api_key: Some("test-initialize-key".into()),
            ..Default::default()
        }));
        tokio::select! {
            biased;
            result = &mut initializing => panic!("initialize must wait for the client lock: {result:?}"),
            _ = std::future::ready(()) => {}
        }
        host.shutdown().await.unwrap();
        let stopped = host.session_get_state(GetStateParams::default()).await.unwrap();
        drop(client);
        assert!(initializing.await.is_err(), "a stopped engine cannot complete its handshake");
        let after = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(after["lifecycle"], "stopped");
        assert_eq!(after["initialized"], false);
        assert_eq!(after["cursor"], stopped["cursor"], "stale release must publish nothing");
        assert!(!*host.turn_active.lock().unwrap());
    }

    #[tokio::test]
    async fn initialization_failure_without_state_change_does_not_advance_cursor() {
        let host = make_host();
        let before = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert!(host.initialize(InitParams {
            session_id: Some("../invalid-session".into()),
            ..Default::default()
        }).await.is_err());
        let after = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(after["cursor"], before["cursor"]);
        assert_eq!(after["initialized"], false);
    }

    #[tokio::test]
    async fn initialization_interrupt_does_not_cancel_a_future_prompt() {
        let host = make_host();
        let client = host.client.lock().await;
        let mut initializing = Box::pin(host.initialize(InitParams {
            api_key: Some("test-initialize-key".into()),
            ..Default::default()
        }));
        tokio::select! {
            biased;
            result = &mut initializing => panic!("initialize must wait for the client lock: {result:?}"),
            _ = std::future::ready(()) => {}
        }
        let interrupted = host.session_interrupt().await;
        assert!(!*host.pending_interrupt.lock().unwrap(), "interrupting a handshake must not arm cancellation for a later turn");
        assert!(interrupted.is_err(), "unsupported handshake interruption must be explicit");
        drop(client);
        initializing.await.unwrap();
    }

    #[tokio::test]
    async fn initialization_failed_resume_preserves_ready_session_and_releases_slot() {
        let host = make_host();
        let initial = host.initialize(InitParams::default()).await.unwrap();
        assert!(host.initialize(InitParams {
            session_id: Some("../not-a-session".into()),
            ..Default::default()
        }).await.is_err());
        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["sessionId"], initial["sessionId"]);
        assert_eq!(state["lifecycle"], "ready");
        assert!(state.get("lastTurnOutcome").is_none());
        assert!(host.try_claim_turn("next", "", TurnKind::Prompt).is_ok());
    }

    #[tokio::test]
    async fn session_prompt_without_credentials_returns_32001() {
        let host = make_host();
        // No client wired, no ANTHROPIC_API_KEY env (in the test environment).
        // Since lazy lookup also finds nothing, session/prompt returns -32001.
        // Skip this test if ANTHROPIC_API_KEY is set (it would find a real client).
        if std::env::var("ANTHROPIC_API_KEY").is_ok() {
            return;
        }
        let err = host
            .session_prompt(PromptParams { text: Some("hi".into()), images: None })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32001);
    }

    #[test]
    fn project_history_concatenates_text_blocks() {
        let msgs = vec![
            Message::user("hello"),
            Message::new(
                Role::Assistant,
                vec![Content::Text("a".into()), Content::Text("b".into())],
            ),
        ];
        let p = project_history(&msgs);
        assert_eq!(p[0].role, "user");
        assert_eq!(p[0].content, "hello");
        assert_eq!(p[1].content, "ab");
    }

    #[test]
    fn project_history_drops_non_text_blocks() {
        use coda_llm::Correlation;
        let msgs = vec![Message::new(
            Role::Assistant,
            vec![
                Content::ToolUse {
                    id: "t1".into(),
                    name: "read_file".into(),
                    input_json: "{}".into(),
                    correlation: Correlation::default(),
                },
                Content::Text("done".into()),
            ],
        )];
        let p = project_history(&msgs);
        assert_eq!(p[0].content, "done");
    }

    #[tokio::test]
    async fn interrupted_turn_cannot_deliver_stranded_steering_on_the_next_turn() {
        struct InterruptedClient {
            started: tokio::sync::Notify,
            calls: std::sync::atomic::AtomicUsize,
            requests: Mutex<Vec<coda_llm::ChatRequest>>,
            held_stream: Mutex<Option<mpsc::Sender<Result<coda_llm::anthropic::StreamEvent, coda_llm::LlmError>>>>,
        }
        #[async_trait]
        impl LlmClient for InterruptedClient {
            fn provider_id(&self) -> &str { "scripted" }
            async fn stream(&self, request: coda_llm::ChatRequest) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                self.requests.lock().unwrap().push(request.clone());
                if self.calls.fetch_add(1, std::sync::atomic::Ordering::SeqCst) == 0 {
                    let (tx, rx) = mpsc::channel(1);
                    *self.held_stream.lock().unwrap() = Some(tx);
                    self.started.notify_one();
                    return Ok(coda_llm::ResponseStream::new(rx));
                }
                ScriptedClient::new(vec![file_test_done()]).stream(request).await
            }
        }
        let dir = tempfile::tempdir().unwrap();
        let client = Arc::new(InterruptedClient {
            started: tokio::sync::Notify::new(),
            calls: std::sync::atomic::AtomicUsize::new(0),
            requests: Mutex::new(Vec::new()),
            held_stream: Mutex::new(None),
        });
        let host = make_host_in_dir(dir.path().to_str().unwrap(), client.clone());
        let first = {
            let host = host.clone();
            tokio::spawn(async move {
                host.session_prompt(PromptParams { text: Some("first turn".into()), images: None }).await
            })
        };
        tokio::time::timeout(Duration::from_secs(2), client.started.notified()).await.unwrap();
        assert_eq!(host.session_steer(SteerParams { text: "stranded-original".into() }).await.unwrap()["ok"], true);
        host.session_interrupt().await.unwrap();
        let result = tokio::time::timeout(Duration::from_secs(2), first).await.unwrap().unwrap().unwrap();
        assert_eq!(result["interrupted"], true);
        assert!(!host.session.steering.has_pending(), "stranded messages must not auto-deliver later");
        assert!(host.session.steering.enqueue("late old-turn input").is_none(), "turn end must seal, not reopen, the inbox");
        host.session_prompt(PromptParams { text: Some("edited replacement".into()), images: None }).await.unwrap();
        let requests = client.requests.lock().unwrap();
        assert!(!requests.last().unwrap().messages.iter().any(|message| message.text().contains("stranded-original")));
    }

    // ── Close/cancel/race outcomes reach EngineState (Slice 0 / Stage C) ────
    #[tokio::test]
    async fn interrupted_turn_reports_cancelled_turn_ended_and_clears_the_turn_in_state() {
        struct InterruptedClient {
            started: tokio::sync::Notify,
            held_stream: Mutex<Option<mpsc::Sender<Result<coda_llm::anthropic::StreamEvent, coda_llm::LlmError>>>>,
        }
        #[async_trait]
        impl LlmClient for InterruptedClient {
            fn provider_id(&self) -> &str { "scripted" }
            async fn stream(&self, _request: coda_llm::ChatRequest) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                let (tx, rx) = mpsc::channel(1);
                *self.held_stream.lock().unwrap() = Some(tx);
                self.started.notify_one();
                Ok(coda_llm::ResponseStream::new(rx))
            }
        }
        let dir = tempfile::tempdir().unwrap();
        let client = Arc::new(InterruptedClient {
            started: tokio::sync::Notify::new(),
            held_stream: Mutex::new(None),
        });
        let host = make_host_in_dir(dir.path().to_str().unwrap(), client.clone());
        let run = {
            let host = host.clone();
            tokio::spawn(async move {
                host.session_prompt(PromptParams { text: Some("go".into()), images: None }).await
            })
        };
        tokio::time::timeout(Duration::from_secs(2), client.started.notified()).await.unwrap();

        // A steering message queued just before the interrupt races the turn
        // end — it must surface as a `cancelledTurnEnded` outcome, not vanish
        // silently (§2.7 F1 gap this closes).
        assert_eq!(host.session_steer(SteerParams { text: "never delivered".into() }).await.unwrap()["ok"], true);
        host.session_interrupt().await.unwrap();

        let result = tokio::time::timeout(Duration::from_secs(2), run).await.unwrap().unwrap().unwrap();
        assert_eq!(result["interrupted"], true);

        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert!(state["turn"].is_null(), "an interrupted turn must not leave a stale `turn` in the snapshot");
        let last = &state["lastTurnOutcome"];
        assert_eq!(last["interrupted"], true);
        assert!(state["lifecycle"] == "ready", "lifecycle must return to ready after interrupt, not stay busy");

        let outcomes = state["steering"]["outcomes"].as_array().unwrap();
        assert!(
            outcomes.iter().any(|o| o["outcome"] == "cancelledTurnEnded"),
            "the raced steering message must be reported as cancelledTurnEnded, not dropped silently: {outcomes:?}"
        );
        assert_eq!(state["steering"]["pendingCount"], 0);
    }

    #[tokio::test]
    async fn pending_steering_is_withdrawn_when_the_turn_guard_exits_early() {
        let host = make_host();
        let turn = host.try_claim_turn("test-turn", "", TurnKind::Prompt).unwrap();
        assert_eq!(host.session_steer(SteerParams { text: "not delivered".into() }).await.unwrap()["ok"], true);
        drop(turn);
        assert!(!host.session.steering.has_pending());
        assert!(host.session.steering.enqueue("late").is_none());
    }

    #[tokio::test]
    async fn pending_recall_withdraws_only_undelivered_messages() {
        let host = make_host();
        let _turn = host.try_claim_turn("test-turn", "", TurnKind::Prompt).unwrap();
        let delivered = host.session_steer(SteerParams { text: "already delivered".into() }).await.unwrap();
        let taken = host.session.steering.take_all_for_delivery();
        assert_eq!(taken[0].id, delivered["messageId"]);
        let pending = host.session_steer(SteerParams { text: "edit me".into() }).await.unwrap();
        let recalled = host.session_recall_steering().await.unwrap();
        assert_eq!(recalled["messages"].as_array().unwrap().len(), 1);
        assert_eq!(recalled["messages"][0]["id"], pending["messageId"]);
        assert_eq!(recalled["messages"][0]["text"], "edit me");
        assert!(!host.session.steering.has_pending(), "recalled text must no longer be deliverable");
        assert!(host.session_recall_steering().await.unwrap()["messages"].as_array().unwrap().is_empty());
        assert!(host.session.steering.take_all_for_delivery().is_empty());
    }

    #[tokio::test]
    async fn setting_a_mode_takes_effect_without_a_restart() {
        // The whole point: PermissionModeState is shared with the prompt, so a
        // change here is seen by the next tool decision. Built the old way the
        // prompt owned a state nothing could reach, which is why /yolo used to
        // ask the user to restart.
        let host = make_host();
        let before = host.permission_mode.get();
        assert_eq!(before, PermissionMode::Default);

        let result = host
            .session_set_permission_mode(SetPermissionModeParams {
                mode: "bypassPermissions".into(),
            })
            .await
            .unwrap();

        assert_eq!(result["ok"], serde_json::json!(true));
        assert_eq!(result["applied"], serde_json::json!("bypassPermissions"));
        assert_eq!(host.permission_mode.get(), PermissionMode::BypassPermissions);
    }

    #[tokio::test]
    async fn an_unknown_mode_is_refused_and_changes_nothing() {
        // Never quietly grant a mode we do not recognise, and never report
        // success for one we did not apply.
        let host = make_host();
        host.permission_mode.set(PermissionMode::Plan);

        let result = host
            .session_set_permission_mode(SetPermissionModeParams {
                mode: "ludicrous".into(),
            })
            .await
            .unwrap();

        assert_eq!(result["ok"], serde_json::json!(false));
        assert_eq!(result["applied"], serde_json::json!("plan"));
        assert_eq!(host.permission_mode.get(), PermissionMode::Plan);
    }

    #[tokio::test]
    async fn mode_spellings_match_the_c_sharp_names() {
        for (wire, expected) in [
            ("default", PermissionMode::Default),
            ("acceptEdits", PermissionMode::AcceptEdits),
            ("plan", PermissionMode::Plan),
            ("bypassPermissions", PermissionMode::BypassPermissions),
            // Aliases the C# also accepts.
            ("yolo", PermissionMode::BypassPermissions),
            ("edits", PermissionMode::AcceptEdits),
        ] {
            assert_eq!(parse_permission_mode(wire), Some(expected), "{wire}");
        }
        assert_eq!(parse_permission_mode("nonsense"), None);
    }

    #[tokio::test]
    async fn set_effort_valid_returns_ok_true() {
        let host = make_host();
        let r = host
            .session_set_effort(SetEffortParams { effort: Some("high".into()), ..Default::default() })
            .await
            .unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["applied"], "high");
        // The C# host emits note:"" rather than omitting it — only null
        // properties are dropped, and an empty string is not null. This test
        // previously asserted the field was absent, which agreed with a real
        // parity bug the differential harness caught.
        assert_eq!(r["note"], "", "note must be present and empty, matching the C# engine");
    }

    #[tokio::test]
    async fn set_effort_clear_omits_applied() {
        let host = make_host();
        let r =
            host.session_set_effort(SetEffortParams { effort: None, ..Default::default() }).await.unwrap();
        assert_eq!(r["ok"], true);
        assert!(r.get("applied").is_none());
    }

    #[tokio::test]
    async fn set_effort_unsupported_is_ok_false_not_error() {
        let host = make_host();
        let r = host
            .session_set_effort(SetEffortParams { effort: Some("ludicrous".into()), ..Default::default() })
            .await
            .unwrap();
        assert_eq!(r["ok"], false, "unsupported effort must yield ok:false, not an Err");
    }

    #[tokio::test]
    async fn xhigh_effort_is_accepted_and_reported_in_current() {
        let host = make_host();
        let r = host
            .session_set_effort(SetEffortParams { effort: Some("xhigh".into()), ..Default::default() })
            .await
            .unwrap();
        assert_eq!(r["ok"], true, "xhigh must be accepted");
        assert_eq!(r["applied"], "xhigh");
        assert_eq!(r["current"], "xhigh", "current must echo applied on success");
    }

    #[tokio::test]
    async fn failed_set_effort_reports_current_unchanged() {
        let host = make_host();
        // Establish "high" first.
        host.session_set_effort(SetEffortParams { effort: Some("high".into()), ..Default::default() })
            .await
            .unwrap();
        // Now try an invalid level.
        let r = host
            .session_set_effort(SetEffortParams { effort: Some("ludicrous".into()), ..Default::default() })
            .await
            .unwrap();
        assert_eq!(r["ok"], false);
        // current should still reflect the pre-existing "high".
        assert_eq!(r["current"], "high", "current must not change on failure");
    }

    #[test]
    fn effort_from_settings_reads_saved_value() {
        let settings = serde_json::json!({
            "effortByModel": {
                "github-copilot/claude-opus-5": "xhigh"
            }
        });
        assert_eq!(
            effort_from_settings(&settings, "github-copilot", "claude-opus-5"),
            Some(Effort::Xhigh)
        );
    }

    #[test]
    fn effort_from_settings_returns_none_when_not_saved() {
        let settings = serde_json::json!({ "effortByModel": {} });
        assert!(effort_from_settings(&settings, "github-copilot", "claude-opus-5").is_none());
    }

    #[test]
    fn effort_from_settings_returns_none_for_invalid_value() {
        let settings = serde_json::json!({
            "effortByModel": {
                "github-copilot/claude-opus-5": "ludicrous"
            }
        });
        assert!(effort_from_settings(&settings, "github-copilot", "claude-opus-5").is_none());
    }

    // ── resolve_effective_effort (Finding 6) ────────────────────────────────

    #[test]
    fn effective_effort_keeps_supported_level_verbatim() {
        let opus = resolve_reasoning("anthropic", "claude-opus-4.8", None);
        assert_eq!(
            resolve_effective_effort(&opus, false, Some(Effort::Xhigh)),
            Some(Effort::Xhigh),
            "xhigh is a real Opus level and must not be clamped"
        );
    }

    #[test]
    fn effective_effort_clamps_max_to_high_where_max_is_unavailable() {
        let sonnet = resolve_reasoning("anthropic", "claude-sonnet-4.6", None);
        assert_eq!(
            resolve_effective_effort(&sonnet, false, Some(Effort::Max)),
            Some(Effort::High),
            "max must clamp to high, matching the documented fallback"
        );
    }

    #[test]
    fn effective_effort_drops_a_level_the_model_cannot_honour() {
        let levels = vec!["low".to_owned(), "medium".to_owned(), "high".to_owned()];
        let copilot = resolve_reasoning("github-copilot", "some-model", Some(&levels));
        assert_eq!(
            resolve_effective_effort(&copilot, false, Some(Effort::Xhigh)),
            None,
            "xhigh must not be faked as high on a high-only model"
        );
    }

    #[test]
    fn effective_effort_keeps_the_request_when_capability_is_indeterminate() {
        let unknown = ReasoningCapability::unsupported();
        assert_eq!(
            resolve_effective_effort(&unknown, true, Some(Effort::Xhigh)),
            Some(Effort::Xhigh),
            "indeterminate capability must not silently drop the user's choice"
        );
    }

    #[test]
    fn effective_effort_auto_stays_auto() {
        let opus = resolve_reasoning("anthropic", "claude-opus-4.8", None);
        assert_eq!(resolve_effective_effort(&opus, false, None), None);
    }

    // ── session_set_model applies per-model effort (Finding 2) ──────────────

    #[tokio::test]
    async fn switching_models_does_not_carry_a_stale_effort() {
        // make_host has no client, so capability is indeterminate (Copilot) and
        // valid levels are accepted optimistically. Set an override on model A,
        // switch to B: B has no override and no saved preference, so the level
        // in force must clear rather than leak A's choice.
        let host = make_host();
        host.set_model_for_test("model-a");
        host.session_set_effort(SetEffortParams { effort: Some("high".into()), ..Default::default() })
            .await
            .unwrap();
        assert_eq!(host.current_effort(), Some(Effort::High));

        host.session_set_model(SetModelParams { model: "model-b".into() })
            .await
            .unwrap();
        assert_eq!(host.current_effort(), None, "B must not inherit A's effort");
    }

    #[tokio::test]
    async fn a_per_model_session_override_is_restored_on_return() {
        let host = make_host();
        host.set_model_for_test("model-a");
        host.session_set_effort(SetEffortParams { effort: Some("high".into()), ..Default::default() })
            .await
            .unwrap();

        host.session_set_model(SetModelParams { model: "model-b".into() })
            .await
            .unwrap();
        assert_eq!(host.current_effort(), None);

        // Returning to A restores its session override, not automatic.
        host.session_set_model(SetModelParams { model: "model-a".into() })
            .await
            .unwrap();
        assert_eq!(host.current_effort(), Some(Effort::High));
    }

    #[tokio::test]
    async fn explicit_auto_override_is_distinct_from_no_override() {
        let host = make_host();
        host.set_model_for_test("model-a");
        // Explicit auto on A.
        host.session_set_effort(SetEffortParams { effort: Some("auto".into()), ..Default::default() })
            .await
            .unwrap();
        host.session_set_model(SetModelParams { model: "model-b".into() })
            .await
            .unwrap();
        // Returning to A keeps the explicit auto (still None), and the override
        // map records the key so it is not re-read from saved settings.
        host.session_set_model(SetModelParams { model: "model-a".into() })
            .await
            .unwrap();
        assert_eq!(host.current_effort(), None);
        assert!(
            host.effort_overrides.lock().unwrap().contains_key("model-a"),
            "explicit auto must be recorded as an override, not treated as absent"
        );
    }

    // ── canonical-identity guard (stale rejection without mutation) ──────────

    #[tokio::test]
    async fn stale_expected_model_is_rejected_without_mutation() {
        let host = make_host();
        host.set_model_for_test("model-a");
        host.session_set_effort(SetEffortParams { effort: Some("high".into()), ..Default::default() })
            .await
            .unwrap();
        // The user switched to model-b, but a picker opened for model-a now
        // tries to apply. The engine must refuse and leave the level untouched.
        host.set_model_for_test("model-b");
        let r = host
            .session_set_effort(SetEffortParams {
                effort: Some("low".into()),
                expected_model: Some("model-a".into()),
                ..Default::default()
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], false, "a stale expected model must be rejected");
        // No mutation: model-b never had an override recorded, and model-a's
        // level is unchanged.
        assert!(
            !host.effort_overrides.lock().unwrap().contains_key("model-b"),
            "a rejected call must not record an override for the active model"
        );
        host.session_set_model(SetModelParams { model: "model-a".into() })
            .await
            .unwrap();
        assert_eq!(
            host.current_effort(),
            Some(Effort::High),
            "model-a's original effort must survive a stale rejection"
        );
    }

    // ── model/adjustEffort: arrow stepping without activating the model ──────

    use coda_llm::ModelInfo;

    /// A Copilot-shaped client that advertises a fixed model list with
    /// reasoning levels, so capability resolves *determinately* (not the
    /// indeterminate fallback `make_host` yields) and the models are "known".
    struct LevelsClient {
        models: Vec<ModelInfo>,
    }
    impl LevelsClient {
        fn arc(models: Vec<ModelInfo>) -> Arc<Self> {
            Arc::new(Self { models })
        }
    }

    #[async_trait]
    impl LlmClient for LevelsClient {
        fn provider_id(&self) -> &str {
            // Match FALLBACK_PROVIDER so effort_from_settings keys line up and
            // capability resolves via the Copilot advertised-levels path.
            "github-copilot"
        }
        async fn stream(
            &self,
            _: coda_llm::ChatRequest,
        ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
            unreachable!("adjustEffort never streams")
        }
        async fn list_models(&self) -> Result<Vec<ModelInfo>, coda_llm::LlmError> {
            Ok(self.models.clone())
        }
    }

    fn model_with_levels(id: &str, levels: &[&str]) -> ModelInfo {
        let mut m = ModelInfo::new(id);
        m.reasoning_levels = levels.iter().map(|s| (*s).to_owned()).collect();
        m
    }

    /// A client that only carries a provider identity — enough to stand in
    /// for "the credential that was just discovered" in wiring tests.
    struct NamedClient {
        provider: String,
    }

    impl NamedClient {
        fn arc(provider: &str) -> Arc<Self> {
            Arc::new(Self { provider: provider.to_owned() })
        }
    }

    #[async_trait]
    impl LlmClient for NamedClient {
        fn provider_id(&self) -> &str {
            &self.provider
        }
        async fn stream(
            &self,
            _: coda_llm::ChatRequest,
        ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
            unreachable!("these tests never stream")
        }
    }

    /// A client whose `list_models` parks until the test releases it, and
    /// which signals that it has been entered.
    ///
    /// This is the deterministic barrier the read-only RPC tests need: a
    /// request can be proven to be *inside* the provider lookup before a
    /// setter is allowed to land, without sleeping or guessing at the
    /// scheduler.
    struct GatedModelsClient {
        provider: String,
        models: Vec<ModelInfo>,
        entered: tokio::sync::Semaphore,
        release: tokio::sync::Semaphore,
    }

    impl GatedModelsClient {
        fn arc(provider: &str, models: Vec<ModelInfo>) -> Arc<Self> {
            Arc::new(Self {
                provider: provider.to_owned(),
                models,
                entered: tokio::sync::Semaphore::new(0),
                release: tokio::sync::Semaphore::new(0),
            })
        }
    }

    #[async_trait]
    impl LlmClient for GatedModelsClient {
        fn provider_id(&self) -> &str {
            &self.provider
        }
        async fn stream(
            &self,
            _: coda_llm::ChatRequest,
        ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
            unreachable!("these tests never stream")
        }
        async fn list_models(&self) -> Result<Vec<ModelInfo>, coda_llm::LlmError> {
            self.entered.add_permits(1);
            self.release.acquire().await.expect("gate open").forget();
            Ok(self.models.clone())
        }
    }

    fn levels_host(models: Vec<ModelInfo>, active: &str) -> Arc<ServeHost> {
        let client = LevelsClient::arc(models);
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_client(client, sink, ch, ".".into());
        host.set_model_for_test(active);
        host
    }

    /// Editing an inactive model steps only that model's stored preference: the
    /// active model, its live effort, and the history are all left untouched.
    #[tokio::test]
    async fn adjust_inactive_model_leaves_active_untouched() {
        let host = levels_host(
            vec![
                model_with_levels("model-a", &["low", "medium", "high"]),
                model_with_levels("model-b", &["low", "medium", "high"]),
            ],
            "model-a",
        );
        // Give the active model A a concrete level.
        host.session_set_effort(SetEffortParams {
            effort: Some("high".into()),
            ..Default::default()
        })
        .await
        .unwrap();
        assert_eq!(host.current_effort(), Some(Effort::High));
        let history_before = host.session.history.lock().unwrap().len();

        // Step inactive B up from auto → low.
        let r = host
            .model_adjust_effort(AdjustEffortParams {
                model: "model-b".into(),
                direction: 1,
                expected_provider: None,
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["model"], "model-b");
        assert_eq!(r["providerId"], "github-copilot");
        assert_eq!(r["current"], "low");
        assert_eq!(r["active"], false, "B is not the active model");

        // The active model and its live effort are unchanged.
        assert_eq!(host.current_model(), "model-a");
        assert_eq!(host.current_effort(), Some(Effort::High), "A's live effort must not move");
        assert_eq!(host.session.history.lock().unwrap().len(), history_before);

        // Switching to B now applies the edited override.
        host.session_set_model(SetModelParams { model: "model-b".into() })
            .await
            .unwrap();
        assert_eq!(host.current_effort(), Some(Effort::Low), "B's edited override applies on switch");
    }

    /// Editing the active model applies immediately to the live level.
    #[tokio::test]
    async fn adjust_active_model_applies_immediately() {
        let host = levels_host(vec![model_with_levels("model-a", &["low", "medium", "high"])], "model-a");
        // From auto, +1 → low.
        let r = host
            .model_adjust_effort(AdjustEffortParams {
                model: "model-a".into(),
                direction: 1,
                expected_provider: None,
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["active"], true);
        assert_eq!(r["current"], "low");
        assert_eq!(host.current_effort(), Some(Effort::Low), "the active model's live effort must move");
    }

    /// The ladder is `[auto, levels…]`; stepping never wraps and never emits a
    /// level the model does not advertise.
    #[tokio::test]
    async fn adjust_walks_auto_then_levels_and_clamps_at_bounds() {
        let host = levels_host(vec![model_with_levels("model-a", &["low", "medium", "high"])], "model-a");
        let up = |dir: i32| AdjustEffortParams {
            model: "model-a".into(),
            direction: dir,
            expected_provider: None,
        };

        // auto -1 clamps at the bottom (still auto), and says so.
        let r = host.model_adjust_effort(up(-1)).await.unwrap();
        assert_eq!(r["ok"], true);
        assert!(r.get("current").is_none(), "auto is reported as absent, not a phantom level");
        assert!(r["note"].as_str().unwrap().contains("lowest"));

        // Walk all the way up: low, medium, high.
        for expected in ["low", "medium", "high"] {
            let r = host.model_adjust_effort(up(1)).await.unwrap();
            assert_eq!(r["current"], expected);
            assert_eq!(r["note"], "", "a real step carries no boundary note");
        }
        // At the top, +1 clamps and reports the unchanged level truthfully.
        let r = host.model_adjust_effort(up(1)).await.unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["current"], "high", "no change=true lie: the top level is reported");
        assert!(r["note"].as_str().unwrap().contains("highest"));
        assert_eq!(host.current_effort(), Some(Effort::High));
    }

    /// A model that advertises a single level still steps between auto and that
    /// level, and never produces an invalid level.
    #[tokio::test]
    async fn adjust_single_level_model_toggles_auto_and_the_level() {
        let host = levels_host(vec![model_with_levels("solo", &["high"])], "solo");
        let step = |dir: i32| AdjustEffortParams {
            model: "solo".into(),
            direction: dir,
            expected_provider: None,
        };
        let r = host.model_adjust_effort(step(1)).await.unwrap();
        assert_eq!(r["current"], "high");
        let r = host.model_adjust_effort(step(1)).await.unwrap();
        assert_eq!(r["current"], "high", "clamped at the single top rung");
        let r = host.model_adjust_effort(step(-1)).await.unwrap();
        assert!(r.get("current").is_none(), "back down to auto");
    }

    /// An `expectedProvider` that no longer matches rejects without mutating.
    #[tokio::test]
    async fn adjust_provider_mismatch_rejects_without_mutation() {
        let host = levels_host(vec![model_with_levels("model-a", &["low", "high"])], "model-a");
        let r = host
            .model_adjust_effort(AdjustEffortParams {
                model: "model-a".into(),
                direction: 1,
                expected_provider: Some("anthropic".into()),
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], false, "a stale provider must be rejected");
        assert!(
            !host.effort_overrides.lock().unwrap().contains_key("model-a"),
            "a rejected call must not record an override"
        );
        assert_eq!(host.current_effort(), None, "the live level must be untouched");
    }

    /// An unknown model id (a typo) is rejected as invalid params, not silently
    /// stored.
    #[tokio::test]
    async fn adjust_unknown_model_is_invalid_params() {
        let host = levels_host(vec![model_with_levels("model-a", &["low", "high"])], "model-a");
        let err = host
            .model_adjust_effort(AdjustEffortParams {
                model: "model-zzz-typo".into(),
                direction: 1,
                expected_provider: None,
            })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32602);
        assert!(!host.effort_overrides.lock().unwrap().contains_key("model-zzz-typo"));
    }

    /// An invalid direction is rejected as invalid params before any mutation.
    #[tokio::test]
    async fn adjust_invalid_direction_is_invalid_params() {
        let host = levels_host(vec![model_with_levels("model-a", &["low", "high"])], "model-a");
        for bad in [0, 2, -2, 5] {
            let err = host
                .model_adjust_effort(AdjustEffortParams {
                    model: "model-a".into(),
                    direction: bad,
                    expected_provider: None,
                })
                .await
                .unwrap_err();
            assert_eq!(err.code, -32602, "direction {bad} must be rejected");
        }
    }

    /// A model with no known level set (indeterminate) is refused with a useful
    /// note rather than a guessed ladder — and nothing is mutated.
    #[tokio::test]
    async fn adjust_indeterminate_model_refuses_with_note() {
        // The active model is known (resolve_known_model accepts the active
        // model) but the client advertises NO list for it, so a Copilot
        // model's capability is genuinely indeterminate — not a positive
        // "unsupported".
        let host = levels_host(vec![], "mystery");
        let r = host
            .model_adjust_effort(AdjustEffortParams {
                model: "mystery".into(),
                direction: 1,
                expected_provider: None,
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], false);
        assert!(r["note"].as_str().unwrap().contains("not known"));
        assert!(!host.effort_overrides.lock().unwrap().contains_key("mystery"));
    }

    /// The result reports the canonical (model, providerId) identity so a client
    /// persists the per-model save under exactly the engine's key, even when the
    /// request used different casing.
    #[tokio::test]
    async fn adjust_reports_canonical_identity_for_persistence() {
        let host = levels_host(vec![model_with_levels("Model-A", &["low", "high"])], "Model-A");
        let r = host
            .model_adjust_effort(AdjustEffortParams {
                model: "model-a".into(), // different casing than the known id
                direction: 1,
                expected_provider: None,
            })
            .await
            .unwrap();
        assert_eq!(r["model"], "Model-A", "the canonical id, not the request casing");
        assert_eq!(r["providerId"], "github-copilot");
    }

    #[tokio::test]
    async fn a_matching_expected_model_is_applied() {
        let host = make_host();
        host.set_model_for_test("model-a");
        let r = host
            .session_set_effort(SetEffortParams {
                effort: Some("high".into()),
                expected_model: Some("model-a".into()),
                ..Default::default()
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], true, "a matching identity must be accepted");
        assert_eq!(host.current_effort(), Some(Effort::High));
    }

    // ── reasoning capability carries canonical identity ─────────────────────

    #[tokio::test]
    async fn reasoning_capability_reports_canonical_model_and_provider() {
        let host = make_host();
        host.set_model_for_test("claude-opus-5");
        let r = host.model_reasoning_capability().await.unwrap();
        assert_eq!(r["model"], "claude-opus-5", "the canonical model id must be reported");
        assert!(r["providerId"].is_string(), "a provider id must accompany the model");
    }

    // ── model list surfaces the active model's live effort ──────────────────

    #[tokio::test]
    async fn model_list_stamps_the_active_row_with_current_effort() {
        let host = make_host();
        host.set_model_for_test("model-a");
        host.session_set_effort(SetEffortParams { effort: Some("high".into()), ..Default::default() })
            .await
            .unwrap();
        let mut rows = vec![
            WireModel {
                id: "model-a".into(),
                display_name: None,
                context_limit: None,
                input_cost: None,
                output_cost: None,
                reasoning_levels: Vec::new(),
                effort: None,
            },
            WireModel {
                id: "model-b".into(),
                display_name: None,
                context_limit: None,
                input_cost: None,
                output_cost: None,
                reasoning_levels: Vec::new(),
                effort: None,
            },
        ];
        host.annotate_effort(&mut rows, &host.capture_runtime().config, "github-copilot");
        assert_eq!(
            rows[0].effort.as_deref(),
            Some("high"),
            "the active row must carry the effective effort of the record it belongs to"
        );
        assert_eq!(rows[1].effort, None, "an unset non-active row carries no effort");
    }

    #[tokio::test]
    async fn set_goal_stores_params_and_omits_absent_optionals() {
        let host = make_host();
        let r = host
            .session_set_goal(SetGoalParams {
                goal: None,
                max_duration: None,
                max_continuations: None,
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], true);
        assert!(r.get("goal").is_none());
        assert!(r.get("maxDuration").is_none());
        assert!(r.get("maxContinuations").is_none());
    }

    #[test]
    fn wire_model_omits_optional_fields_when_none() {
        let m = WireModel {
            id: "x".into(),
            display_name: None,
            context_limit: None,
            input_cost: None,
            output_cost: None,
            reasoning_levels: Vec::new(),
            effort: None,
        };
        let v = serde_json::to_value(&m).unwrap();
        assert!(v.get("displayName").is_none());
        assert!(v.get("contextLimit").is_none());
        // An unpriced model must carry no price at all rather than a zero,
        // which a client would read as free.
        assert!(v.get("inputCost").is_none());
        assert!(v.get("outputCost").is_none());
    }

    #[test]
    fn the_catalogue_prices_the_models_it_lists() {
        // The whole point of bundling the snapshot: a model list that says
        // what each one costs.
        let models = catalog_models();
        assert!(!models.is_empty(), "the catalogue listed nothing");
        assert!(
            models.iter().any(|m| m.input_cost.is_some() && m.output_cost.is_some()),
            "not one model carried a price"
        );
        assert!(
            models.iter().any(|m| m.context_limit.is_some()),
            "not one model carried a context limit"
        );
    }

    #[test]
    fn parse_duration_handles_all_suffixes() {
        assert_eq!(parse_duration(Some("5m")), Some(Duration::from_secs(300)));
        assert_eq!(parse_duration(Some("2h")), Some(Duration::from_secs(7200)));
        assert_eq!(parse_duration(Some("30s")), Some(Duration::from_secs(30)));
        assert!(parse_duration(Some("bad")).is_none());
        assert!(parse_duration(None).is_none());
    }

    // ── Bug-fix: set_goal with invalid maxDuration returns -32602 ────────────

    #[tokio::test]
    async fn set_goal_with_invalid_max_duration_returns_32602() {
        let host = make_host();
        let err = host
            .session_set_goal(SetGoalParams {
                goal: Some("do it".into()),
                max_duration: Some("not-a-duration".into()),
                max_continuations: None,
            })
            .await
            .unwrap_err();
        assert_eq!(
            err.code, -32602,
            "invalid maxDuration must return -32602, not a success"
        );
        assert!(
            err.message.to_lowercase().contains("timeout")
                || err.message.to_lowercase().contains("duration"),
            "error message must describe the bad timeout/duration: {}", err.message
        );
    }

    #[tokio::test]
    async fn set_goal_with_valid_max_duration_is_accepted() {
        let host = make_host();
        let r = host
            .session_set_goal(SetGoalParams {
                goal: Some("ship it".into()),
                max_duration: Some("30m".into()),
                max_continuations: None,
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["maxDuration"], "30m");
    }

    // ── Bug-fix: steer when no turn running returns ok:false ─────────────────

    #[tokio::test]
    async fn steer_when_no_turn_running_returns_ok_false() {
        let host = make_host();
        // Steering inbox is sealed when no turn is running.
        let r = host
            .session_steer(SteerParams { text: "steer without a turn".into() })
            .await
            .unwrap();
        assert_eq!(r["ok"], false, "steer with no turn running must return ok:false");
        assert!(
            r.get("messageId").is_none(),
            "rejected steer must not produce a messageId"
        );
    }

    // ── Bug-fix: concurrent prompt claim rejected ────────────────────────────

    #[tokio::test]
    async fn session_compact_without_credentials_returns_32001() {
        if std::env::var("ANTHROPIC_API_KEY").is_ok() {
            return;
        }
        let host = make_host();
        let err = host
            .session_compact(CompactParams { instructions: None })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32001);
    }

    // ── validate_base64 unit tests ────────────────────────────────────────────

    #[test]
    fn validate_base64_accepts_valid_input() {
        // A tiny real PNG's base64
        let png_b64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        assert!(validate_base64(png_b64).is_ok());
        assert!(validate_base64("").is_ok());
        assert!(validate_base64("AAAA").is_ok());
        assert!(validate_base64("AAA=").is_ok());
        assert!(validate_base64("AA==").is_ok());
    }

    #[test]
    fn validate_base64_rejects_invalid_characters() {
        // "!" is not a valid base64 character
        assert!(validate_base64("not valid base64!!").is_err());
        // Spaces are not valid
        assert!(validate_base64("AA AA").is_err());
    }

    // ── image media type validation ───────────────────────────────────────────

    #[tokio::test]
    async fn session_prompt_rejects_unsupported_image_media_type() {
        if std::env::var("ANTHROPIC_API_KEY").is_ok() {
            return;
        }
        let host = make_host();
        use serde_json::json;
        let err = host
            .session_prompt(PromptParams {
                text: Some("hi".into()),
                images: Some(vec![json!({"mediaType":"image/bmp","base64":"AAAA"})]),
            })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32602);
        assert!(
            err.message.to_lowercase().contains("unsupported image media type"),
            "error must mention unsupported media type: {}",
            err.message
        );
    }

    #[tokio::test]
    async fn session_prompt_rejects_invalid_base64_in_image() {
        if std::env::var("ANTHROPIC_API_KEY").is_ok() {
            return;
        }
        let host = make_host();
        use serde_json::json;
        let err = host
            .session_prompt(PromptParams {
                text: Some("hi".into()),
                images: Some(vec![json!({"mediaType":"image/png","base64":"not valid base64!!"})]),
            })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32602);
        assert!(
            err.message.to_lowercase().contains("base64"),
            "error must mention base64: {}",
            err.message
        );
    }

    // ── Finding 1: task_list live-turn integration test ───────────────────────

    /// Scripted LLM client for use in integration tests.
    struct ScriptedClient {
        sequences: Mutex<std::collections::VecDeque<Vec<coda_llm::anthropic::StreamEvent>>>,
    }

    impl ScriptedClient {
        fn new(sequences: Vec<Vec<coda_llm::anthropic::StreamEvent>>) -> Arc<Self> {
            Arc::new(Self {
                sequences: Mutex::new(sequences.into_iter().collect()),
            })
        }
    }

    #[async_trait]
    impl LlmClient for ScriptedClient {
        fn provider_id(&self) -> &str {
            "scripted"
        }
        async fn stream(
            &self,
            _: coda_llm::ChatRequest,
        ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
            let events = self
                .sequences
                .lock()
                .unwrap()
                .pop_front()
                .expect("ScriptedClient ran out of scripted sequences");
            let (tx, rx) = tokio::sync::mpsc::channel(64);
            tokio::spawn(async move {
                for ev in events {
                    let _ = tx.send(Ok(ev)).await;
                }
            });
            Ok(coda_llm::ResponseStream::new(rx))
        }
    }

    // ── Stage D: an unanswered question must never become an answer ───────

    /// A counting scripted client: records every provider request so a test
    /// can prove that *no* follow-up model request was issued.
    struct CountingClient {
        sequences: Mutex<std::collections::VecDeque<Vec<coda_llm::anthropic::StreamEvent>>>,
        requests: Mutex<Vec<coda_llm::ChatRequest>>,
    }

    impl CountingClient {
        fn new(sequences: Vec<Vec<coda_llm::anthropic::StreamEvent>>) -> Arc<Self> {
            Arc::new(Self {
                sequences: Mutex::new(sequences.into_iter().collect()),
                requests: Mutex::new(Vec::new()),
            })
        }
        fn request_count(&self) -> usize {
            self.requests.lock().unwrap().len()
        }
    }

    #[async_trait]
    impl LlmClient for CountingClient {
        fn provider_id(&self) -> &str {
            "scripted"
        }
        async fn stream(
            &self,
            request: coda_llm::ChatRequest,
        ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
            self.requests.lock().unwrap().push(request);
            let events = self
                .sequences
                .lock()
                .unwrap()
                .pop_front()
                .expect("CountingClient ran out of scripted sequences");
            let (tx, rx) = tokio::sync::mpsc::channel(64);
            tokio::spawn(async move {
                for ev in events {
                    let _ = tx.send(Ok(ev)).await;
                }
            });
            Ok(coda_llm::ResponseStream::new(rx))
        }
    }

    /// The exact turn the plan requires: a real `ask_user_question` ToolUse
    /// with options `["Delete", "Keep"]`, where no controller ever answers.
    fn ask_delete_or_keep_turn() -> Vec<coda_llm::anthropic::StreamEvent> {
        use coda_llm::anthropic::StreamEvent;
        vec![
            StreamEvent::ToolUse(coda_llm::Content::ToolUse {
                id: "q-1".into(),
                name: "ask_user_question".into(),
                input_json: serde_json::json!({
                    "question": "Delete the production database?",
                    "options": ["Delete", "Keep"],
                })
                .to_string(),
                correlation: Default::default(),
            }),
            StreamEvent::Done {
                stop_reason: Some("tool_use".into()),
                usage: coda_llm::Usage::ZERO,
            },
        ]
    }

    /// SECURITY: when the controller never answers a question, the engine must
    /// not fabricate `"User answered: Delete"` (the first option) and must not
    /// issue a follow-up model request on the strength of that fabrication.
    ///
    /// Exactly one provider request may be observed: the one that produced the
    /// `ask_user_question` call itself.
    #[tokio::test]
    async fn an_unanswered_question_never_becomes_an_answer_and_never_continues_the_loop() {
        let dir = tempfile::tempdir().unwrap();
        // Only ONE scripted sequence: a second `stream()` call would panic,
        // but the assertion below is explicit so the failure is readable.
        let client = CountingClient::new(vec![ask_delete_or_keep_turn()]);

        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let channel = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_client(
            client.clone(),
            sink,
            Arc::clone(&channel),
            dir.path().to_string_lossy().into_owned(),
        );

        let prompt = {
            let host = Arc::clone(&host);
            tokio::spawn(async move {
                host.session_prompt(PromptParams { text: Some("tidy up".into()), images: None })
                    .await
            })
        };

        // Wait until the engine has actually issued `request/question`, then
        // simulate the controller vanishing (EOF / dropped connection).
        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        loop {
            assert!(std::time::Instant::now() < deadline, "engine never issued request/question");
            if let Ok(frame) = rx.try_recv() {
                if String::from_utf8_lossy(&frame).contains("request/question") {
                    break;
                }
                continue;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        channel.fail_all_pending();

        let result = tokio::time::timeout(Duration::from_secs(10), prompt)
            .await
            .expect("the turn must not hang on an unanswered question")
            .expect("task")
            .expect("the RPC itself must answer, not error at the protocol level");

        assert_eq!(
            client.request_count(),
            1,
            "SECURITY: an unanswered question must not drive a follow-up model request"
        );
        assert_eq!(result["ok"], false, "the turn must report a typed failure, not success");

        // Nothing in the committed conversation may claim the user answered.
        let history = host.session.history.lock().unwrap().clone();
        let transcript: String = history.iter().flat_map(|m| m.content.iter()).map(|c| match c {
            coda_llm::Content::Text(t) => t.clone(),
            coda_llm::Content::ToolResult { content, .. } => content.clone(),
            _ => String::new(),
        }).collect::<Vec<_>>().join("\n");
        assert!(
            !transcript.contains("User answered"),
            "SECURITY: a lost connection must never be recorded as an answer: {transcript}"
        );
        assert!(
            !transcript.contains("Delete\n") && !transcript.ends_with("Delete"),
            "SECURITY: the first option must never be substituted for a real answer: {transcript}"
        );
    }

    /// Before Finding 1 was fixed, `task_list` returned "Task manager is not
    /// available." because `with_task_manager` was never called on the agent
    /// loop. After the fix it must return the real (empty) task list.
    #[tokio::test]
    async fn task_list_tool_is_wired_and_returns_task_list_not_error() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::{Content, Correlation, Usage};

        let client = ScriptedClient::new(vec![
            // Turn 1: model calls task_list with empty input.
            vec![
                StreamEvent::ToolUse(Content::ToolUse {
                    id: "call-1".into(),
                    name: "task_list".into(),
                    input_json: "{}".into(),
                    correlation: Correlation::default(),
                }),
                StreamEvent::Done {
                    stop_reason: Some("tool_use".into()),
                    usage: Usage { input_tokens: 10, output_tokens: 5, ..Usage::ZERO },
                },
            ],
            // Turn 2: model receives the tool result and ends.
            vec![
                StreamEvent::TextDelta("done".into()),
                StreamEvent::Done {
                    stop_reason: Some("end_turn".into()),
                    usage: Usage { input_tokens: 20, output_tokens: 5, ..Usage::ZERO },
                },
            ],
        ]);

        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_client(client, sink, ch, ".".into());

        let result = host
            .session_prompt(PromptParams { text: Some("list tasks".into()), images: None })
            .await
            .expect("session_prompt must succeed");

        assert!(
            result["ok"].as_bool().unwrap_or(false),
            "prompt must succeed: {result:?}"
        );

        // Inspect the ToolResult block that task_list produced.
        let history = host.session.history.lock().expect("history poisoned").clone();
        let tool_result_content: Option<String> = history.iter().find_map(|msg| {
            msg.content.iter().find_map(|block| match block {
                Content::ToolResult { tool_use_id, content, .. }
                    if tool_use_id == "call-1" =>
                {
                    Some(content.clone())
                }
                _ => None,
            })
        });

        let content =
            tool_result_content.expect("task_list ToolResult must be present in history");

        assert!(
            !content.contains("not available"),
            "task_list must not return 'not available' — task_manager was not wired.\n\
             Got: {content:?}"
        );
        // An empty task manager returns "No tasks." — verify the real store path ran.
        assert!(
            content.contains("No tasks") || content.contains("task-"),
            "expected a real task-list response, got: {content:?}"
        );
    }

    // ── Finding 2: session_models fallback is non-empty ───────────────────────

    #[tokio::test]
    async fn session_models_without_client_returns_catalog_not_empty() {
        let host = make_host();
        let r = host
            .session_models(ModelsParams { refresh: false })
            .await
            .unwrap();
        let source = r["source"].as_str().unwrap_or("");
        let models = r["models"].as_array().expect("models must be an array");
        assert_ne!(source, "builtin", "source 'builtin' is gone; should be 'catalog'");
        assert!(
            !models.is_empty(),
            "models must not be empty — a transient failure must not hide all models"
        );
    }

    // ── Finding 3: model resolves from connected provider not defaultProvider ─

    #[tokio::test]
    async fn model_is_resolved_from_connected_provider_not_settings_default_provider() {
        // An Anthropic-API-key-based client reports "anthropic" as provider.
        // The model must come from settings.modelByProvider["anthropic"],
        // NOT from settings.modelByProvider["github-copilot"].
        // We verify by checking that new_with_client uses client.provider_id().
        let client = ScriptedClient::new(vec![]); // provider_id = "scripted"
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        // Settings are not real here, so the model resolves to FALLBACK_MODEL.
        let host = ServeHost::new_with_client(client, sink, ch, ".".into());
        // Provider should be "scripted" (from the client), not "github-copilot".
        // The model must still be a non-empty string (falls back to FALLBACK_MODEL).
        let model = host.current_model();
        assert!(
            !model.is_empty(),
            "model must not be empty after connecting a client"
        );
    }

    // ── Finding 4: pending interrupt flag ────────────────────────────────────

    #[tokio::test]
    async fn interrupt_before_turn_starts_sets_pending_flag() {
        let host = make_host();
        // No turn running; session_interrupt must set the pending flag.
        host.session_interrupt().await.unwrap();
        assert!(
            *host.pending_interrupt.lock().unwrap(),
            "pending_interrupt must be set when interrupt arrives before any turn"
        );
    }

    #[tokio::test]
    async fn pending_flag_is_cleared_after_second_interrupt_lands_on_running_turn() {
        let host = make_host();
        // Simulate a turn in progress by publishing a cancel token.
        let cancel = CancellationToken::new();
        *host.current_cancel.lock().unwrap() = Some(cancel.clone());
        // Now interrupt cancels the token directly (not the pending flag).
        host.session_interrupt().await.unwrap();
        assert!(cancel.is_cancelled(), "interrupt must cancel the running token");
        assert!(
            !*host.pending_interrupt.lock().unwrap(),
            "pending flag must NOT be set when a real token was found"
        );
    }

    // ── Security: hook scope stamped by loader, not read from JSON ────────────

    /// Security mutation-verified: if the `#[serde(skip)]` attribute on
    /// `UserHook.scope` were removed, deserialization would read `"scope":"user"`
    /// from the JSON and this test would fail (hook would have User scope
    /// instead of the Project default). Run `cargo clean -p coda-serve && cargo
    /// test` after applying that mutation to confirm the test catches it.
    #[test]
    fn json_cannot_claim_user_scope_in_project_settings() {
        let dir = std::env::temp_dir().join(format!(
            "coda-scope-sec-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("settings.json");

        // A hostile project settings file claiming user scope.
        std::fs::write(
            &path,
            serde_json::json!({
                "hooks": [{ "event": "PreToolUse", "command": "evil.sh", "scope": "user" }]
            })
            .to_string(),
        )
        .unwrap();

        let hooks = load_hooks_from_file(&path);

        // serde(skip) must prevent JSON from granting User scope.
        assert_eq!(hooks.len(), 1, "expected one hook to be loaded");
        assert_eq!(
            hooks[0].scope,
            HookScope::Project,
            "a hook claiming 'user' scope in JSON must default to Project (untrusted) \
             because UserHook.scope is #[serde(skip)] — JSON cannot grant User scope"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn loader_stamps_user_scope_for_user_settings_and_project_for_cwd_settings() {
        let dir = std::env::temp_dir().join(format!(
            "coda-scope-loader-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        std::fs::create_dir_all(&dir).unwrap();

        let hook_json = serde_json::json!({
            "hooks": [{ "event": "PreToolUse", "command": "hook.sh", "scope": "user" }]
        });

        let file = dir.join("settings.json");
        std::fs::write(&file, hook_json.to_string()).unwrap();

        // When loaded as user settings — loader stamps User scope.
        let mut user_hooks = load_hooks_from_file(&file);
        for h in &mut user_hooks {
            h.scope = HookScope::User;
        }
        assert_eq!(user_hooks[0].scope, HookScope::User);

        // When loaded as project settings — scope stays Project (no stamp needed,
        // serde(skip) default is Project).
        let project_hooks = load_hooks_from_file(&file);
        assert_eq!(
            project_hooks[0].scope,
            HookScope::Project,
            "freshly deserialized hook must be Project regardless of JSON content"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    // ── Session persistence: initialize with known session_id resumes ──────────

    #[tokio::test]
    async fn initialize_with_unknown_session_id_returns_32002() {
        let host = make_host();
        let err = host
            .initialize(InitParams {
                protocol_version: "1".into(),
                session_id: Some("unknownidxyz0".into()),
                api_key: None,
                client_info: None,
                ..Default::default()
            })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32002, "unknown session id must return -32002");
    }

    #[tokio::test]
    async fn initialize_with_path_traversal_session_id_returns_32002() {
        let host = make_host();
        // Path traversal ids must never reach the filesystem — they are rejected
        // by is_valid() and return -32002, not an I/O error.
        for bad_id in &["../secret", "../../etc/passwd", r"..\..\evil", "bad/id"] {
            let err = host
                .initialize(InitParams {
                    protocol_version: "1".into(),
                    session_id: Some(bad_id.to_string()),
                    api_key: None,
                    client_info: None,
                    ..Default::default()
                })
                .await
                .unwrap_err();
            assert_eq!(
                err.code, -32002,
                "path traversal id {bad_id:?} must return -32002, not an I/O error"
            );
        }
    }

    // ── Session persistence E2E: persist → resume → verify ────────────────────
    //
    // This test proves the full path from prompt → persist → resume is wired
    // at both ends.  It uses a ScriptedClient (defined above) so no network
    // call is made.

    fn make_host_in_dir(working_dir: &str, client: Arc<dyn LlmClient>) -> Arc<ServeHost> {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        ServeHost::new_with_client(client, sink, ch, working_dir.into())
    }

    fn outside_read_turn(path: &std::path::Path, id: &str) -> Vec<coda_llm::anthropic::StreamEvent> {
        use coda_llm::anthropic::StreamEvent;
        vec![
            StreamEvent::ToolUse(coda_llm::Content::ToolUse {
                id: id.into(), name: "read_file".into(),
                input_json: serde_json::json!({"path":path}).to_string(),
                correlation: Default::default(),
            }),
            StreamEvent::Done { stop_reason: Some("tool_use".into()), usage: coda_llm::Usage::ZERO },
        ]
    }

    fn file_test_done() -> Vec<coda_llm::anthropic::StreamEvent> {
        use coda_llm::anthropic::StreamEvent;
        vec![
            StreamEvent::TextDelta("done".into()),
            StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: coda_llm::Usage::ZERO },
        ]
    }

    #[tokio::test]
    async fn yolo_file_access_matches_startup_and_rpc_permission_modes() {
        for mode in [PermissionMode::Default, PermissionMode::Plan, PermissionMode::AcceptEdits, PermissionMode::BypassPermissions] {
            for via_rpc in [false, true] {
                let dir = tempfile::tempdir().unwrap();
                let repo = dir.path().join("repo");
                std::fs::create_dir(&repo).unwrap();
                let outside = dir.path().join("outside.txt");
                std::fs::write(&outside, "outside fixture").unwrap();
                let client = ScriptedClient::new(vec![outside_read_turn(&outside, "read"), file_test_done()]);
                let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
                let host = ServeHost::new_with_optional_client_and_mcp(
                    Some(client), Arc::new(ServeSink::new(tx.clone())), Arc::new(PromptChannel::new(tx)),
                    repo.to_string_lossy().into_owned(), McpBundle::disabled(),
                    StartupOptions { permission_mode: if via_rpc { None } else { Some(mode) }, ..Default::default() },
                    None, None,
                );
                if via_rpc {
                    host.session_set_permission_mode(SetPermissionModeParams {
                        mode: wire_permission_mode(mode).into(),
                    }).await.unwrap();
                }
                assert_eq!(host.session_prompt(PromptParams { text: Some("read fixture".into()), images: None }).await.unwrap()["ok"], true);
                let history = host.session.history.lock().unwrap();
                let (content, failed) = history.iter().flat_map(|m| &m.content).find_map(|block| match block {
                    coda_llm::Content::ToolResult { content, is_error, .. } => Some((content, *is_error)),
                    _ => None,
                }).unwrap();
                assert_eq!(failed, mode != PermissionMode::BypassPermissions, "{mode:?}, rpc={via_rpc}: {content}");
                if !failed { assert!(content.contains("outside fixture")); }
            }
        }
    }

    #[tokio::test]
    async fn yolo_file_access_allows_builtin_write_outside_repo() {
        use coda_llm::anthropic::StreamEvent;
        let dir = tempfile::tempdir().unwrap();
        let repo = dir.path().join("repo");
        std::fs::create_dir(&repo).unwrap();
        let outside = dir.path().join("created.txt");
        let client = ScriptedClient::new(vec![vec![
            StreamEvent::ToolUse(coda_llm::Content::ToolUse {
                id: "write".into(), name: "write_file".into(),
                input_json: json!({"path":outside,"content":"written outside"}).to_string(),
                correlation: Default::default(),
            }),
            StreamEvent::Done { stop_reason: Some("tool_use".into()), usage: coda_llm::Usage::ZERO },
        ], file_test_done()]);
        let host = make_host_in_dir(repo.to_str().unwrap(), client);
        host.session_set_permission_mode(SetPermissionModeParams { mode: "bypassPermissions".into() }).await.unwrap();
        host.session_prompt(PromptParams { text: Some("write fixture".into()), images: None }).await.unwrap();
        assert_eq!(std::fs::read_to_string(outside).unwrap(), "written outside");
    }

    #[tokio::test]
    async fn yolo_file_access_switches_both_directions_within_one_turn() {
        struct SwitchingClient {
            inner: Arc<ScriptedClient>,
            host: Mutex<std::sync::Weak<ServeHost>>,
            calls: std::sync::atomic::AtomicUsize,
        }
        #[async_trait]
        impl LlmClient for SwitchingClient {
            fn provider_id(&self) -> &str { "scripted" }
            async fn stream(&self, request: coda_llm::ChatRequest) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
                let call = self.calls.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                if call == 1 || call == 2 {
                    let host = self.host.lock().unwrap().upgrade().unwrap();
                    host.session_set_permission_mode(SetPermissionModeParams {
                        mode: if call == 1 { "bypassPermissions" } else { "default" }.into(),
                    }).await.unwrap();
                }
                self.inner.stream(request).await
            }
        }
        let dir = tempfile::tempdir().unwrap();
        let repo = dir.path().join("repo");
        std::fs::create_dir(&repo).unwrap();
        let outside = dir.path().join("outside.txt");
        std::fs::write(&outside, "fixture").unwrap();
        let client = Arc::new(SwitchingClient {
            inner: ScriptedClient::new(vec![
                outside_read_turn(&outside, "first"), outside_read_turn(&outside, "second"),
                outside_read_turn(&outside, "third"), file_test_done(),
            ]),
            host: Mutex::new(std::sync::Weak::new()),
            calls: std::sync::atomic::AtomicUsize::new(0),
        });
        let host = make_host_in_dir(repo.to_str().unwrap(), client.clone());
        *client.host.lock().unwrap() = Arc::downgrade(&host);
        host.session_prompt(PromptParams { text: Some("read fixture".into()), images: None }).await.unwrap();
        let history = host.session.history.lock().unwrap();
        let failed: Vec<_> = history.iter().flat_map(|m| &m.content).filter_map(|block| match block {
            coda_llm::Content::ToolResult { is_error, .. } => Some(*is_error), _ => None,
        }).collect();
        assert_eq!(failed, [true, false, true]);
    }

    #[tokio::test]
    async fn cached_subagent_services_use_the_current_model_for_each_new_run() {
        use coda_agent::events::NullSink;
        use coda_agent::scheduling::ScheduledAgentRunner;
        use coda_agent::subagents::SubagentRequest;

        let dir = tempfile::tempdir().unwrap();
        let client = CountingClient::new(vec![file_test_done(); 5]);
        let host = make_host_in_dir(dir.path().to_str().unwrap(), client.clone());
        host.session_set_model(SetModelParams { model: "model-a".into() }).await.unwrap();
        let services = host.get_or_init_services(client.clone()).await;
        let request = || SubagentRequest::foreground("general-purpose", "reply", "child", 1);
        services.subagent_host.spawn(request(), Arc::new(NullSink), CancellationToken::new())
            .await.unwrap();

        host.session_set_model(SetModelParams { model: "model-b".into() }).await.unwrap();
        assert!(Arc::ptr_eq(&services, &host.get_or_init_services(client.clone()).await));
        services.subagent_host.spawn(request(), Arc::new(NullSink), CancellationToken::new())
            .await.unwrap();

        let mut explicit = request();
        explicit.model = Some("explicit-model".into());
        services.subagent_host.spawn(explicit, Arc::new(NullSink), CancellationToken::new())
            .await.unwrap();

        let runner = TaskManagerRunner::new(host.task_manager.clone(), services.subagent_host.clone());
        let (done, completed) = tokio::sync::oneshot::channel();
        let done = Mutex::new(Some(done));
        runner.start("scheduled reply".into(), "scheduled".into(), Arc::new(move |_| {
            if let Some(done) = done.lock().unwrap().take() {
                let _ = done.send(());
            }
        })).unwrap();
        tokio::time::timeout(Duration::from_secs(5), completed).await.unwrap().unwrap();

        let mut background = request();
        background.foreground = false;
        let id = services.subagent_host.spawn(background, Arc::new(NullSink), CancellationToken::new())
            .await.unwrap();
        tokio::time::timeout(Duration::from_secs(5), async {
            while !host.task_manager.get(&id).unwrap().status.is_terminal() {
                tokio::task::yield_now().await;
            }
        }).await.unwrap();
        let models: Vec<_> = client.requests.lock().unwrap().iter()
            .map(|request| request.model.clone()).collect();
        assert_eq!(models, ["model-a", "model-b", "explicit-model", "model-b", "model-b"]);
    }

    #[tokio::test]
    async fn yolo_file_access_is_shared_with_existing_subagent_host() {
        use coda_agent::events::{AgentEvent, CollectingSink};
        use coda_agent::subagents::SubagentRequest;
        let dir = tempfile::tempdir().unwrap();
        let repo = dir.path().join("repo");
        std::fs::create_dir(&repo).unwrap();
        let outside = dir.path().join("outside.txt");
        std::fs::write(&outside, "fixture").unwrap();
        let client = ScriptedClient::new(vec![
            outside_read_turn(&outside, "first"), file_test_done(),
            outside_read_turn(&outside, "second"), file_test_done(),
        ]);
        let host = make_host_in_dir(repo.to_str().unwrap(), client.clone());
        let services = host.build_session_services(client);
        for mode in [PermissionMode::BypassPermissions, PermissionMode::Default] {
            host.permission_mode.set(mode);
            let sink = Arc::new(CollectingSink::new());
            services.subagent_host.spawn(
                SubagentRequest::foreground("general-purpose", "read fixture", "test", 1),
                sink.clone(), CancellationToken::new(),
            ).await.unwrap();
            let results: Vec<_> = sink.take().into_iter().filter_map(|event| match event {
                AgentEvent::ToolResult { is_error, .. } => Some(is_error), _ => None,
            }).collect();
            assert_eq!(results, [mode != PermissionMode::BypassPermissions]);
        }
    }

    #[tokio::test]
    async fn session_persists_after_prompt_and_resumes_correctly() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::Usage;

        let dir = tempfile::tempdir().unwrap();
        let working_dir = dir.path().to_str().unwrap().to_owned();

        // ── Phase 1: send a prompt, let the fake LLM respond ─────────────────
        let client = ScriptedClient::new(vec![vec![
            StreamEvent::TextDelta("The answer is 42.".into()),
            StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO },
        ]]);
        let host1 = make_host_in_dir(&working_dir, client);

        // Initialize without a session_id to get a fresh session.
        let init_r = host1.initialize(InitParams::default()).await.unwrap();
        let session_id = init_r["sessionId"].as_str().unwrap().to_owned();
        assert!(
            !session_id.is_empty(),
            "initialize must return a session_id"
        );

        // Send a prompt — the fake LLM will respond and the history will be saved.
        let prompt_r = host1
            .session_prompt(PromptParams {
                text: Some("What is the answer?".into()),
                images: None,
            })
            .await
            .unwrap();
        assert_eq!(prompt_r["ok"], true, "prompt must succeed");

        // Verify the session file was written.
        let session_file = dir
            .path()
            .join(".coda")
            .join("sessions")
            .join(format!("{session_id}.json"));
        assert!(
            session_file.exists(),
            "session file must be written after a prompt: {session_file:?}"
        );

        // ── Phase 2: resume the session in a new host ─────────────────────────
        let client2 = ScriptedClient::new(vec![]); // no more LLM calls expected
        let host2 = make_host_in_dir(&working_dir, client2);

        let init_r2 = host2
            .initialize(InitParams {
                protocol_version: "1".into(),
                session_id: Some(session_id.clone()),
                api_key: None,
                client_info: None,
                ..Default::default()
            })
            .await
            .unwrap();

        assert_eq!(
            init_r2["sessionId"].as_str().unwrap(),
            session_id,
            "resumed session must echo back the requested id"
        );

        // The history must be the loaded transcript (user prompt + assistant reply).
        let history_r = host2.session_history().await.unwrap();
        let messages = history_r["messages"].as_array().expect("messages array");
        assert!(
            messages.len() >= 2,
            "resumed history must contain at least the user turn and assistant reply, got {}",
            messages.len()
        );

        let first_msg = &messages[0];
        assert_eq!(first_msg["role"], "user");
        assert!(
            first_msg["content"].as_str().unwrap_or("").contains("What is the answer"),
            "first message must be the user prompt"
        );

        let second_msg = &messages[1];
        assert_eq!(second_msg["role"], "assistant");
        assert!(
            second_msg["content"].as_str().unwrap_or("").contains("42"),
            "second message must be the assistant reply"
        );

        // F2: resume used to be silent too — the resuming host's state must
        // report the bumped historyEpoch and the resumed session id.
        let resumed_state = host2.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(resumed_state["historyEpoch"], 1, "resume must bump historyEpoch");
        assert_eq!(resumed_state["sessionId"], session_id);
    }

    // ── session/fork tests ────────────────────────────────────────────────────

    #[tokio::test]
    async fn fork_returns_new_session_id() {
        let dir = tempfile::tempdir().unwrap();
        let working_dir = dir.path().to_str().unwrap().to_owned();
        let client = ScriptedClient::new(vec![]);
        let host = make_host_in_dir(&working_dir, client);

        let r = host.session_fork(ForkParams {}).await.unwrap();
        assert_eq!(r["ok"], true);
        let new_id = r["newSessionId"].as_str().unwrap();
        assert!(!new_id.is_empty(), "fork must return a non-empty newSessionId");
    }

    // F2: fork/rewind/compact used to be silent (no historyEpoch bump, no
    // event). This closes the gap the plan explicitly names.
    #[tokio::test]
    async fn fork_bumps_history_epoch_and_is_never_invisible() {
        let dir = tempfile::tempdir().unwrap();
        let working_dir = dir.path().to_str().unwrap().to_owned();
        let client = ScriptedClient::new(vec![]);
        let host = make_host_in_dir(&working_dir, client);

        let before = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(before["historyEpoch"], 0);

        host.session_fork(ForkParams {}).await.unwrap();

        let after = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(after["historyEpoch"], 1, "fork must bump historyEpoch");
    }

    #[tokio::test]
    async fn rewind_bumps_history_epoch() {
        let dir = tempfile::tempdir().unwrap();
        let working_dir = dir.path().to_str().unwrap().to_owned();
        let client = ScriptedClient::new(vec![]);
        let host = make_host_in_dir(&working_dir, client);
        *host.session.history.lock().unwrap() = vec![Message::user("hi"), Message::assistant("hello")];

        host.session_rewind(RewindParams { n: Some(1) }).await.unwrap();

        let state = host.session_get_state(GetStateParams::default()).await.unwrap();
        assert_eq!(state["historyEpoch"], 1, "rewind must bump historyEpoch");
    }

    #[tokio::test]
    async fn fork_changes_active_session_id() {
        let dir = tempfile::tempdir().unwrap();
        let working_dir = dir.path().to_str().unwrap().to_owned();
        let client = ScriptedClient::new(vec![]);
        let host = make_host_in_dir(&working_dir, client);

        let original_id = host.active_session_id();
        let r = host.session_fork(ForkParams {}).await.unwrap();
        let new_id = r["newSessionId"].as_str().unwrap();

        assert_ne!(
            new_id, original_id,
            "fork must produce a different id from the original"
        );
        assert_eq!(
            host.active_session_id(),
            new_id,
            "host must adopt the forked id as the active session"
        );
    }

    #[tokio::test]
    async fn fork_persists_history_under_new_id() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::Usage;

        let dir = tempfile::tempdir().unwrap();
        let working_dir = dir.path().to_str().unwrap().to_owned();

        let client = ScriptedClient::new(vec![vec![
            StreamEvent::TextDelta("reply".into()),
            StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO },
        ]]);
        let host = make_host_in_dir(&working_dir, client);

        // Build some history via a prompt.
        host.session_prompt(PromptParams { text: Some("hello".into()), images: None })
            .await
            .unwrap();

        let r = host.session_fork(ForkParams {}).await.unwrap();
        let new_id = r["newSessionId"].as_str().unwrap();

        // The forked session file must exist.
        let forked_file = dir
            .path()
            .join(".coda")
            .join("sessions")
            .join(format!("{new_id}.json"));
        assert!(forked_file.exists(), "forked session file must exist: {forked_file:?}");
    }

    // ── session/rewind tests ──────────────────────────────────────────────────

    #[tokio::test]
    async fn rewind_removes_last_exchange() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::Usage;

        let dir = tempfile::tempdir().unwrap();
        let working_dir = dir.path().to_str().unwrap().to_owned();

        let client = ScriptedClient::new(vec![
            vec![
                StreamEvent::TextDelta("first reply".into()),
                StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO },
            ],
            vec![
                StreamEvent::TextDelta("second reply".into()),
                StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO },
            ],
        ]);
        let host = make_host_in_dir(&working_dir, client);

        host.session_prompt(PromptParams { text: Some("q1".into()), images: None })
            .await
            .unwrap();
        host.session_prompt(PromptParams { text: Some("q2".into()), images: None })
            .await
            .unwrap();

        let len_before = host.session.history.lock().unwrap().len();
        assert_eq!(len_before, 4, "expected 2 user + 2 assistant messages");

        let r = host.session_rewind(RewindParams { n: Some(1) }).await.unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["removed"], 1);
        assert_eq!(r["remaining"], 2);

        let len_after = host.session.history.lock().unwrap().len();
        assert_eq!(len_after, 2, "after rewind(1), only q1+reply should remain");
    }

    #[tokio::test]
    async fn rewind_defaults_to_one_exchange() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::Usage;

        let dir = tempfile::tempdir().unwrap();
        let working_dir = dir.path().to_str().unwrap().to_owned();

        let client = ScriptedClient::new(vec![vec![
            StreamEvent::TextDelta("reply".into()),
            StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO },
        ]]);
        let host = make_host_in_dir(&working_dir, client);
        host.session_prompt(PromptParams { text: Some("hi".into()), images: None })
            .await
            .unwrap();

        // n=None defaults to 1.
        let r = host.session_rewind(RewindParams { n: None }).await.unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["removed"], 1);

        let empty = host.session.history.lock().unwrap().len();
        assert_eq!(empty, 0, "after rewind(default=1) on one exchange, history must be empty");
    }

    #[tokio::test]
    async fn rewind_persists_updated_history() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::Usage;

        let dir = tempfile::tempdir().unwrap();
        let working_dir = dir.path().to_str().unwrap().to_owned();

        let client = ScriptedClient::new(vec![vec![
            StreamEvent::TextDelta("answer".into()),
            StreamEvent::Done { stop_reason: Some("end_turn".into()), usage: Usage::ZERO },
        ]]);
        let host = make_host_in_dir(&working_dir, client);
        let _ = host.initialize(InitParams::default()).await.unwrap();

        host.session_prompt(PromptParams { text: Some("q".into()), images: None })
            .await
            .unwrap();

        host.session_rewind(RewindParams { n: Some(1) }).await.unwrap();

        // Load the session file — it must reflect the rewound (empty) history.
        // After rewinding, history is empty so save() skips the write — the file
        // may have the old content or not exist. What matters is the in-memory
        // history is empty.
        let _store = coda_agent::SessionTranscriptStore::new(dir.path());
        let in_memory = host.session.history.lock().unwrap().len();
        assert_eq!(in_memory, 0, "in-memory history must be empty after rewind");
    }

    #[tokio::test]
    async fn session_models_reports_which_model_is_active() {
        // The list alone says nothing about which entry is in use, so a client
        // showing "the current model" had to guess -- and guessed `first()`,
        // which is whatever order the provider happened to return. The status
        // bar then named a model the engine was not using, and switching to
        // another one appeared not to stick.
        let host = make_host();
        let result = host
            .session_models(ModelsParams { refresh: false })
            .await
            .expect("models");

        let active = result["model"]
            .as_str()
            .expect("session/models must name the active model");
        assert!(!active.is_empty(), "the active model must not be blank");
        assert_eq!(
            active,
            host.current_model(),
            "the reported model must be the one the engine will actually use"
        );
    }

    #[tokio::test]
    async fn setting_the_model_takes_effect_without_a_restart() {
        // The agent is rebuilt from current_model() every turn, so a switch
        // needs no restart. Writing the setting and bouncing the process cost
        // the running session, and failed outright with -32002 when that
        // session had not been written to disk yet.
        let host = make_host();
        let before = host.current_model();

        let result = host
            .session_set_model(SetModelParams { model: "claude-opus-4-8".into() })
            .await
            .expect("setModel");
        assert_eq!(result["ok"], true);
        assert_eq!(host.current_model(), "claude-opus-4-8");
        assert_ne!(host.current_model(), before, "the model did not change");
    }

    #[tokio::test]
    async fn an_empty_model_is_refused_rather_than_blanking_the_setting() {
        // A blank would leave the engine with no model at all, which fails at
        // the next turn rather than here where it can be reported.
        let host = make_host();
        let before = host.current_model();

        for blank in ["", "   "] {
            let result = host
                .session_set_model(SetModelParams { model: blank.into() })
                .await
                .expect("setModel must not error");
            assert_eq!(result["ok"], false, "{blank:?} was accepted");
        }
        assert_eq!(host.current_model(), before, "the model was blanked");
    }

    // ── MCP engine integration ────────────────────────────────────────────────

    /// Builds an MCP bundle backed by an in-memory fake server advertising one
    /// tool named `tool_name` that returns `result_text`. Returns the bundle,
    /// the live manager, and a handle counting how many `tools/call` requests
    /// actually reach the server. Fully hermetic: no process, no network.
    async fn fake_mcp_bundle(
        tool_name: &str,
        result_text: &str,
    ) -> (McpBundle, Arc<McpClientManager>, coda_mcp::manager::test_support::FakeServerHandle) {
        use coda_mcp::manager::test_support::connect_fake_server;

        let manager = Arc::new(McpClientManager::new());
        let tools_json = serde_json::json!([{
            "name": tool_name,
            "description": "fake echo tool",
            "inputSchema": { "type": "object", "properties": {} }
        }]);
        let handle = connect_fake_server(&manager, "fake-server", tools_json, result_text).await;

        let mut tools = manager.tools().await;
        tools.extend(coda_mcp::management_tools::mcp_management_tools(Arc::clone(&manager)));

        let bundle = McpBundle {
            tools,
            manager: Some(Arc::clone(&manager)),
            notices: Vec::new(),
        };
        (bundle, manager, handle)
    }

    fn host_with_mcp(client: Arc<dyn LlmClient>, mcp: McpBundle) -> Arc<ServeHost> {
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        ServeHost::new_with_client_and_mcp(client, sink, ch, ".".into(), mcp)
    }

    /// The primary blocker: with MCP wired, an advertised tool must be
    /// registered as a deferred (tool_search-discoverable) tool, reach the
    /// model as a callable tool, pass the permission gate, and execute against
    /// the remote server.
    #[tokio::test]
    async fn mcp_tool_is_registered_deferred_and_executes_through_permission_gate() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::{Content, Correlation, Usage};

        let (bundle, _manager, handle) = fake_mcp_bundle("echo", "fake-result").await;
        let tool_name = coda_mcp::tool::namespaced_name("fake-server", "echo");

        let client = ScriptedClient::new(vec![
            vec![
                StreamEvent::ToolUse(Content::ToolUse {
                    id: "call-1".into(),
                    name: tool_name.clone(),
                    input_json: "{}".into(),
                    correlation: Correlation::default(),
                }),
                StreamEvent::Done {
                    stop_reason: Some("tool_use".into()),
                    usage: Usage { input_tokens: 10, output_tokens: 5, ..Usage::ZERO },
                },
            ],
            vec![
                StreamEvent::TextDelta("done".into()),
                StreamEvent::Done {
                    stop_reason: Some("end_turn".into()),
                    usage: Usage { input_tokens: 20, output_tokens: 5, ..Usage::ZERO },
                },
            ],
        ]);

        let host = host_with_mcp(client, bundle);

        // The tool is in the same registry that is cloned into the subagent
        // host (build_session_services does `Arc::clone(&self.tools)`), so this
        // also proves subagents see it.
        let registered =
            host.tools.resolve(&tool_name).expect("mcp tool must be registered in the engine");
        assert!(registered.should_defer(), "mcp tools must be deferred (tool_search only)");
        assert!(!registered.is_read_only(), "mcp tools must never be read-only");

        // Allow the tool to run.
        host.permission_mode.set(PermissionMode::BypassPermissions);

        let result = host
            .session_prompt(PromptParams { text: Some("use echo".into()), images: None })
            .await
            .expect("session_prompt must succeed");
        assert_eq!(result["ok"], true, "prompt must succeed: {result:?}");

        assert_eq!(
            handle.call_count(),
            1,
            "the allowed tool must reach the remote server exactly once"
        );

        let history = host.session.history.lock().expect("history poisoned").clone();
        let content = history
            .iter()
            .find_map(|msg| {
                msg.content.iter().find_map(|b| match b {
                    Content::ToolResult { tool_use_id, content, .. } if tool_use_id == "call-1" => {
                        Some(content.clone())
                    }
                    _ => None,
                })
            })
            .expect("the MCP tool result must be in history");
        assert!(
            content.contains("fake-result"),
            "the tool result must carry the remote output: {content:?}"
        );
    }

    /// The permission gate must gate MCP tools: a denial (here via Plan mode)
    /// must stop the tool before any remote call is made.
    #[tokio::test]
    async fn denied_mcp_tool_never_reaches_the_remote_server() {
        use coda_llm::anthropic::StreamEvent;
        use coda_llm::{Content, Correlation, Usage};

        let (bundle, _manager, handle) = fake_mcp_bundle("echo", "should-not-run").await;
        let tool_name = coda_mcp::tool::namespaced_name("fake-server", "echo");

        let client = ScriptedClient::new(vec![
            vec![
                StreamEvent::ToolUse(Content::ToolUse {
                    id: "call-1".into(),
                    name: tool_name.clone(),
                    input_json: "{}".into(),
                    correlation: Correlation::default(),
                }),
                StreamEvent::Done {
                    stop_reason: Some("tool_use".into()),
                    usage: Usage { input_tokens: 10, output_tokens: 5, ..Usage::ZERO },
                },
            ],
            vec![
                StreamEvent::TextDelta("understood".into()),
                StreamEvent::Done {
                    stop_reason: Some("end_turn".into()),
                    usage: Usage { input_tokens: 20, output_tokens: 5, ..Usage::ZERO },
                },
            ],
        ]);

        let host = host_with_mcp(client, bundle);

        // Plan mode denies mutating tools without an interactive prompt.
        host.permission_mode.set(PermissionMode::Plan);

        host.session_prompt(PromptParams { text: Some("use echo".into()), images: None })
            .await
            .expect("session_prompt must succeed even when a tool is denied");

        assert_eq!(
            handle.call_count(),
            0,
            "a denied tool must NEVER reach the remote server"
        );

        let history = host.session.history.lock().expect("history poisoned").clone();
        let content = history
            .iter()
            .find_map(|msg| {
                msg.content.iter().find_map(|b| match b {
                    Content::ToolResult { tool_use_id, content, .. } if tool_use_id == "call-1" => {
                        Some(content.clone())
                    }
                    _ => None,
                })
            })
            .expect("a denied tool must still produce a ToolResult block");
        assert!(
            content.contains("Permission denied"),
            "denied tool result must say so: {content:?}"
        );
    }

    /// `shutdown` must tear down the MCP servers so their child processes do
    /// not leak; after it, the servers are gone.
    #[tokio::test]
    async fn shutdown_disconnects_mcp_servers() {
        let (bundle, manager, _handle) = fake_mcp_bundle("echo", "x").await;
        let client = ScriptedClient::new(vec![]);
        let host = host_with_mcp(client, bundle);

        // Before shutdown the server answers.
        assert!(
            manager.call_tool("fake-server", "echo", &serde_json::json!({})).await.is_ok(),
            "server must be connected before shutdown"
        );

        host.shutdown().await.expect("shutdown must succeed");

        assert!(
            manager.call_tool("fake-server", "echo", &serde_json::json!({})).await.is_err(),
            "after shutdown the MCP server must be disconnected"
        );
    }

    /// MCP connection/config failures collected at startup must be surfaced to
    /// the user via an `event/error` notification on `initialize`, and drained
    /// so they are not repeated.
    #[tokio::test]
    async fn mcp_connection_failures_are_surfaced_on_initialize() {
        let bundle = McpBundle {
            tools: Vec::new(),
            manager: None,
            notices: vec!["MCP server 'broken' failed to start and was skipped: boom".into()],
        };
        let (tx, mut rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let client = ScriptedClient::new(vec![]);
        let host = ServeHost::new_with_client_and_mcp(client, sink, ch, ".".into(), bundle);

        host.initialize(InitParams::default()).await.expect("initialize");

        let frame = rx.try_recv().expect("a notice must be emitted on initialize");
        let text = String::from_utf8(frame).expect("utf8");
        assert!(text.contains("event/error"), "notice must be an event/error: {text}");
        assert!(text.contains("broken"), "notice must name the failing server: {text}");

        // Drained: a second initialize emits nothing further.
        host.initialize(InitParams::default()).await.expect("second initialize");
        assert!(
            rx.try_recv().is_err(),
            "startup notices must be surfaced once, not on every initialize"
        );
    }

    // ─────────────────────────────────────────────────────────────────────────
    // New parity tests: system prompt, startup overrides, goal, permission mode
    // ─────────────────────────────────────────────────────────────────────────

    /// A CapturingClient records the most recent ChatRequest so tests can
    /// assert on what the engine actually sent to the LLM.
    struct CapturingClient {
        last_request: Mutex<Option<coda_llm::ChatRequest>>,
        sequence: Mutex<std::collections::VecDeque<Vec<coda_llm::anthropic::StreamEvent>>>,
    }

    impl CapturingClient {
        fn new(events: Vec<coda_llm::anthropic::StreamEvent>) -> Arc<Self> {
            Arc::new(Self {
                last_request: Mutex::new(None),
                sequence: Mutex::new(std::collections::VecDeque::from(vec![events])),
            })
        }

        fn last_system_prompt(&self) -> Option<String> {
            self.last_request.lock().unwrap().as_ref()?.system.clone()
        }

        fn last_model(&self) -> Option<String> {
            Some(self.last_request.lock().unwrap().as_ref()?.model.clone())
        }

        fn last_effort(&self) -> Option<Effort> {
            self.last_request.lock().unwrap().as_ref()?.effort
        }
    }

    #[async_trait]
    impl LlmClient for CapturingClient {
        fn provider_id(&self) -> &str { "capturing" }

        async fn stream(
            &self,
            request: coda_llm::ChatRequest,
        ) -> Result<coda_llm::ResponseStream, coda_llm::LlmError> {
            *self.last_request.lock().unwrap() = Some(request);
            let events = self
                .sequence
                .lock()
                .unwrap()
                .pop_front()
                .unwrap_or_else(|| {
                    vec![coda_llm::anthropic::StreamEvent::Done {
                        stop_reason: Some("end_turn".into()),
                        usage: coda_llm::Usage::ZERO,
                    }]
                });
            let (tx, rx) = tokio::sync::mpsc::channel(64);
            tokio::spawn(async move {
                for ev in events { let _ = tx.send(Ok(ev)).await; }
            });
            Ok(coda_llm::ResponseStream::new(rx))
        }
    }

    fn make_capturing_host() -> (Arc<ServeHost>, Arc<CapturingClient>) {
        make_capturing_host_with_options(StartupOptions::default())
    }

    /// Builds a capturing host with explicit startup options, without reading
    /// the process environment (Finding I4 — hermetic constructors).
    fn make_capturing_host_with_options(
        opts: StartupOptions,
    ) -> (Arc<ServeHost>, Arc<CapturingClient>) {
        let events = vec![coda_llm::anthropic::StreamEvent::Done {
            stop_reason: Some("end_turn".into()),
            usage: coda_llm::Usage::ZERO,
        }];
        let client = CapturingClient::new(events);
        let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
        let sink = Arc::new(ServeSink::new(tx.clone()));
        let ch = Arc::new(PromptChannel::new(tx));
        let host = ServeHost::new_with_client_and_options(
            Arc::clone(&client) as Arc<dyn LlmClient>,
            sink,
            ch,
            ".".into(),
            opts,
        );
        (host, client)
    }

    /// `session/setSystemPrompt` stores the text and it reaches the LLM.
    #[tokio::test]
    async fn custom_system_prompt_reaches_the_llm() {
        let (host, client) = make_capturing_host();

        let r = host
            .session_set_system_prompt(SetSystemPromptParams {
                text: Some("Be extremely terse.".into()),
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["cleared"], false);

        host.session_prompt(PromptParams { text: Some("hello".into()), images: None })
            .await
            .unwrap();

        let system = client.last_system_prompt();
        assert!(
            system.as_deref().unwrap_or("").contains("Be extremely terse."),
            "custom system prompt must reach the LLM, got: {system:?}"
        );
    }

    /// Clearing the prompt (empty text) removes the override.
    #[tokio::test]
    async fn clearing_system_prompt_removes_override() {
        let (host, client) = make_capturing_host();

        host.session_set_system_prompt(SetSystemPromptParams {
            text: Some("Override.".into()),
        })
        .await
        .unwrap();

        let clear = host
            .session_set_system_prompt(SetSystemPromptParams { text: None })
            .await
            .unwrap();
        assert_eq!(clear["cleared"], true);

        host.session_prompt(PromptParams { text: Some("hi".into()), images: None })
            .await
            .unwrap();

        let system = client.last_system_prompt();
        assert!(
            !system.as_deref().unwrap_or("").contains("Override."),
            "cleared prompt must not reach the LLM: {system:?}"
        );
    }

    /// `session/setSystemPrompt` with empty string also clears.
    #[tokio::test]
    async fn empty_string_clears_system_prompt() {
        let (host, _) = make_capturing_host();
        let r = host
            .session_set_system_prompt(SetSystemPromptParams { text: Some(String::new()) })
            .await
            .unwrap();
        assert_eq!(r["cleared"], true);
    }

    /// `session/setModel` changes the model in force for subsequent turns.
    #[tokio::test]
    async fn set_model_rpc_changes_active_model() {
        let host = make_host();
        let before = host.current_model();

        let r = host
            .session_set_model(SetModelParams { model: "my-custom-model".into() })
            .await
            .unwrap();
        assert_eq!(r["ok"], true);
        assert_ne!(host.current_model(), before);
        assert_eq!(host.current_model(), "my-custom-model");
    }

    /// `session/setPermissionMode` with `bypassPermissions` enables yolo mode.
    #[tokio::test]
    async fn permission_mode_bypass_permissions_takes_effect() {
        let host = make_host();
        assert_eq!(host.permission_mode.get(), PermissionMode::Default);

        let r = host
            .session_set_permission_mode(SetPermissionModeParams {
                mode: "bypassPermissions".into(),
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(host.permission_mode.get(), PermissionMode::BypassPermissions);
    }

    /// `session/setGoal` stores goal text and budget; validated before prompt.
    #[tokio::test]
    async fn set_goal_stores_all_params() {
        let host = make_host();
        let r = host
            .session_set_goal(SetGoalParams {
                goal: Some("Implement feature X".into()),
                max_duration: Some("1h".into()),
                max_continuations: Some(10),
            })
            .await
            .unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["goal"], "Implement feature X");
        assert_eq!(r["maxDuration"], "1h");
        assert_eq!(r["maxContinuations"], 10);
        // Verify the state is actually stored.
        let stored = host.goal_params.lock().unwrap().clone();
        assert_eq!(stored.goal.as_deref(), Some("Implement feature X"));
        assert_eq!(stored.max_continuations, Some(10));
    }

    /// An unknown permission mode is refused without changing the active mode.
    #[tokio::test]
    async fn unknown_permission_mode_is_refused() {
        let host = make_host();
        host.permission_mode.set(PermissionMode::Plan);
        let r = host
            .session_set_permission_mode(SetPermissionModeParams { mode: "alien".into() })
            .await
            .unwrap();
        assert_eq!(r["ok"], false, "unknown mode must be refused");
        assert_eq!(host.permission_mode.get(), PermissionMode::Plan, "mode must be unchanged");
    }

    /// Goal parameters supplied via startup options take effect before prompts,
    /// with no process-environment mutation (Finding I4).
    #[tokio::test]
    async fn startup_goal_options_are_applied() {
        let opts = StartupOptions {
            goal: Some("ship it".into()),
            goal_max_duration: Some("30m".into()),
            goal_max_continuations: Some(5),
            ..StartupOptions::default()
        };
        let (host, _client) = make_capturing_host_with_options(opts);

        let stored = host.goal_params.lock().unwrap().clone();
        assert_eq!(stored.goal.as_deref(), Some("ship it"));
        assert_eq!(stored.max_duration.as_deref(), Some("30m"));
        assert_eq!(stored.max_continuations, Some(5));
    }

    /// A startup model override wins over provider-based resolution.
    #[tokio::test]
    async fn startup_model_option_overrides_default() {
        let opts = StartupOptions {
            model: Some("test-model-override".into()),
            ..StartupOptions::default()
        };
        let (host, _client) = make_capturing_host_with_options(opts);
        assert_eq!(host.current_model(), "test-model-override");
    }

    /// A `bypassPermissions` startup permission mode sets yolo mode.
    #[tokio::test]
    async fn startup_permission_mode_option_sets_yolo() {
        let opts = StartupOptions {
            permission_mode: Some(PermissionMode::BypassPermissions),
            ..StartupOptions::default()
        };
        let (host, _client) = make_capturing_host_with_options(opts);
        assert_eq!(host.permission_mode.get(), PermissionMode::BypassPermissions);
    }

    /// A startup system prompt reaches the LLM.
    #[tokio::test]
    async fn startup_system_prompt_option_is_wired() {
        let opts = StartupOptions {
            system_prompt: Some("You are a pirate.".into()),
            ..StartupOptions::default()
        };
        let (host, client) = make_capturing_host_with_options(opts);

        host.session_prompt(PromptParams { text: Some("ahoy".into()), images: None })
            .await
            .unwrap();

        let system = client.last_system_prompt();
        assert!(
            system.as_deref().unwrap_or("").contains("You are a pirate."),
            "startup system prompt must reach the LLM: {system:?}"
        );
    }

    /// An explicit `--effort auto` startup override clears effort and does NOT
    /// fall back to the saved per-model preference (Finding I4 — explicit-auto
    /// precedence).
    #[tokio::test]
    async fn startup_effort_auto_option_clears_without_reading_settings() {
        let opts = StartupOptions {
            effort: StartupEffort::Auto,
            ..StartupOptions::default()
        };
        let (host, _client) = make_capturing_host_with_options(opts);
        assert_eq!(host.current_effort(), None, "explicit auto must clear effort");
    }

    /// An explicit startup effort level is applied as the active level.
    #[tokio::test]
    async fn startup_effort_level_option_is_applied() {
        let opts = StartupOptions {
            effort: StartupEffort::Level(Effort::High),
            ..StartupOptions::default()
        };
        let (host, _client) = make_capturing_host_with_options(opts);
        assert_eq!(host.current_effort(), Some(Effort::High));
    }

    #[tokio::test]
    async fn startup_effort_is_resolved_against_the_connected_model_on_initialize() {
        let opts = StartupOptions {
            model: Some("claude-sonnet-4.6".into()),
            effort: StartupEffort::Level(Effort::Max),
            ..StartupOptions::default()
        };
        let (host, _) = make_capturing_host_with_options(opts);
        host.initialize(InitParams::default()).await.unwrap();
        assert_eq!(host.current_effort(), Some(Effort::High));
        assert!(host.pending_startup_effort.lock().unwrap().is_none());
        assert_eq!(host.model_reasoning_capability().await.unwrap()["current"], "high");
    }

    #[tokio::test]
    async fn startup_effort_rejects_a_known_unsupported_model_before_prompt() {
        let opts = StartupOptions {
            model: Some("claude-haiku-4.5".into()),
            effort: StartupEffort::Level(Effort::High),
            ..StartupOptions::default()
        };
        let (host, client) = make_capturing_host_with_options(opts);
        assert!(host.initialize(InitParams::default()).await.is_err());
        assert!(host.apply_pending_startup_effort().await.is_err());
        assert!(client.last_system_prompt().is_none());
    }

    /// Apply-order invariant (Finding C1): the CLI applies model/provider
    /// FIRST and effort LAST. When effort is recorded before a model switch it
    /// is attached to the previous model and dropped; applied after the switch
    /// it sticks. This exercises the host directly — not just clap parsing.
    #[tokio::test]
    async fn effort_survives_model_switch_only_in_correct_order() {
        // Wrong order: effort recorded under one model, THEN a switch to the
        // final model. The high level is attached to the previous model and
        // dropped by the switch.
        let (wrong, _c1) = make_capturing_host();
        wrong
            .session_set_model(SetModelParams { model: "claude-opus-5".into() })
            .await
            .unwrap();
        wrong
            .session_set_effort(SetEffortParams {
                effort: Some("high".into()),
                expected_model: None,
                expected_provider: None,
            })
            .await
            .unwrap();
        assert_eq!(wrong.current_effort(), Some(Effort::High), "sanity: recorded under opus-5");
        wrong
            .session_set_model(SetModelParams { model: "claude-opus-4.8".into() })
            .await
            .unwrap();
        assert_ne!(
            wrong.current_effort(),
            Some(Effort::High),
            "effort applied before the model switch must not carry over"
        );

        // Correct order: model THEN effort. The high level is recorded under
        // the active model and survives.
        let (right, client) = make_capturing_host();
        right
            .session_set_model(SetModelParams { model: "claude-opus-4.8".into() })
            .await
            .unwrap();
        right
            .session_set_effort(SetEffortParams {
                effort: Some("high".into()),
                expected_model: None,
                expected_provider: None,
            })
            .await
            .unwrap();
        assert_eq!(right.current_effort(), Some(Effort::High));

        // And the model the effort was resolved against is the one actually
        // sent to the LLM on the next turn.
        right
            .session_prompt(PromptParams { text: Some("go".into()), images: None })
            .await
            .unwrap();
        assert_eq!(client.last_model().as_deref(), Some("claude-opus-4.8"));
    }

    /// `canonical_provider` maps aliases onto `coda_auth` provider ids and
    /// leaves unknown values untouched (for explicit downstream rejection).
    #[test]
    fn canonical_provider_maps_known_aliases() {
        assert_eq!(canonical_provider("Anthropic"), "anthropic");
        assert_eq!(canonical_provider("api-key"), "anthropic");
        assert_eq!(canonical_provider("copilot"), "github-copilot");
        assert_eq!(canonical_provider("github"), "github-copilot");
        assert_eq!(canonical_provider("claude"), "claude-ai");
        assert_eq!(canonical_provider("subscription"), "claude-ai");
        assert_eq!(canonical_provider("totally-unknown"), "totally-unknown");
    }

    /// Provider selection fails closed for an unknown provider — never falling
    /// back to a working one (Finding C2).
    #[tokio::test]
    async fn build_client_for_unknown_provider_fails_closed() {
        let err = build_client_for_provider("not-a-provider", None, None)
            .await
            .err()
            .expect("must fail closed");
        assert!(err.to_string().contains("unknown provider"), "{err}");
    }

    /// Requesting `anthropic` without any key fails closed rather than probing
    /// another provider's credential.
    #[tokio::test]
    async fn build_client_for_anthropic_without_key_fails_closed() {
        // Ensure the ambient key is absent for this check.
        let saved = std::env::var("ANTHROPIC_API_KEY").ok();
        std::env::remove_var("ANTHROPIC_API_KEY");
        let err = build_client_for_provider("anthropic", None, None)
            .await
            .err()
            .expect("must fail closed");
        if let Some(v) = saved {
            std::env::set_var("ANTHROPIC_API_KEY", v);
        }
        assert!(err.to_string().contains("no API key"), "{err}");
    }

    /// An explicit anthropic key + endpoint builds an Anthropic client (not a
    /// fallback provider), honouring the endpoint.
    #[tokio::test]
    async fn build_client_for_anthropic_with_key_and_endpoint() {
        let client =
            build_client_for_provider("anthropic", Some("sk-test"), Some("https://proxy.example.com"))
                .await
                .expect("anthropic client");
        assert_eq!(client.provider_id(), "anthropic");
    }

    /// StartupOptions validation rejects a non-positive goal timeout and a
    /// negative continuation budget rather than silently coercing (Finding I2).
    #[test]
    fn startup_options_validate_rejects_bad_goal_budget() {
        let zero = StartupOptions {
            goal_max_duration: Some("0m".into()),
            ..StartupOptions::default()
        };
        assert!(zero.validate().is_err(), "zero-length timeout must be rejected");

        let neg = StartupOptions {
            goal_max_continuations: Some(-1),
            ..StartupOptions::default()
        };
        assert!(neg.validate().is_err(), "negative continuations must be rejected");

        let bad = StartupOptions {
            goal_max_duration: Some("banana".into()),
            ..StartupOptions::default()
        };
        assert!(bad.validate().is_err(), "unparseable timeout must be rejected");

        let ok = StartupOptions {
            goal: Some("do it".into()),
            goal_max_duration: Some("30m".into()),
            goal_max_continuations: Some(0),
            ..StartupOptions::default()
        };
        assert!(ok.validate().is_ok(), "zero continuations is explicitly allowed");
    }

    /// An endpoint without a key is rejected at validation time (Finding I1/I2).
    #[test]
    fn startup_options_validate_rejects_endpoint_without_key() {
        let opts = StartupOptions {
            endpoint: Some("https://proxy.example.com".into()),
            ..StartupOptions::default()
        };
        assert!(opts.validate().is_err());
    }

    /// `ANTHROPIC_BASE_URL` is ordinary user configuration and carries no
    /// `--api-key` to pair with, so the "endpoint requires a key" invariant
    /// must keep being decided on the *explicit* endpoint alone.
    ///
    /// Proved on the struct rather than on `from_env`, which reads the process
    /// environment and could not run beside its sibling tests.
    #[test]
    fn the_resolved_base_never_turns_an_env_override_into_an_explicit_endpoint() {
        let resolved = StartupOptions {
            anthropic_endpoint: ApiKeyEndpoint::Approved("http://127.0.0.1:9999".into()),
            ..StartupOptions::default()
        };
        assert!(
            resolved.validate().is_ok(),
            "an environment override must not require an explicit --api-key"
        );
        assert_eq!(
            resolved.api_key_endpoint().expect("approved").as_deref(),
            Some("http://127.0.0.1:9999")
        );

        // With no resolution performed (tests, embedding hosts) the explicit
        // endpoint stands alone, which is what those callers already rely on.
        let explicit = StartupOptions {
            api_key: Some("sk-x".into()),
            endpoint: Some("  https://proxy.example.com  ".into()),
            ..StartupOptions::default()
        };
        assert_eq!(
            explicit.api_key_endpoint().expect("valid").as_deref(),
            Some("https://proxy.example.com")
        );
        assert_eq!(StartupOptions::default().api_key_endpoint().expect("nothing set"), None);
    }

    /// A programmatically constructed `StartupOptions` cannot slip a raw,
    /// unvalidated `--endpoint` past the resolver by simply not having gone
    /// through `from_env`.
    ///
    /// The check is pure — no environment is read — so it stays hermetic
    /// (Finding I4) while still refusing a URL that would put the key on the
    /// wire in clear.
    #[test]
    fn an_unresolved_explicit_endpoint_is_validated_rather_than_taken_raw() {
        for bad in [
            "http://gateway.example.com",
            "https://gateway.example.com/?key=LEAKED",
            "not-a-url",
        ] {
            let opts = StartupOptions {
                api_key: Some("sk-x".into()),
                endpoint: Some(bad.to_owned()),
                ..StartupOptions::default()
            };
            let error = opts.api_key_endpoint().expect_err("{bad} must be refused");
            assert!(error.0.contains("--endpoint"), "{bad}: {}", error.0);
            assert!(!error.0.contains("LEAKED"), "{bad}: {}", error.0);
        }
    }

    /// The resolution keeps a refused `ANTHROPIC_BASE_URL` and a refused
    /// `--endpoint` apart, because they are decided at different times.
    ///
    /// An explicit endpoint can only exist beside an explicit key, so it is
    /// fatal where it is configured. The variable is scoped to one identity —
    /// an engine signed in to Copilot or Claude.ai must start — so it is
    /// *captured*, and spends the rest of the process failing every API-key
    /// client instead of quietly becoming "no endpoint configured".
    #[test]
    fn an_environment_refusal_is_captured_while_an_explicit_one_is_fatal() {
        let env = |name: &str| {
            (name == coda_auth::service::ANTHROPIC_BASE_URL_ENV)
                .then(|| "http://gateway.example.com".to_owned())
        };

        let captured = StartupOptions::resolve_api_key_endpoint(None, env)
            .expect("an environment refusal must not fail startup");
        let ApiKeyEndpoint::Refused(message) = &captured else {
            panic!("expected a captured refusal, got {captured:?}");
        };
        assert!(message.contains("ANTHROPIC_BASE_URL"), "{message}");

        // Captured, not discarded: every API-key consumer still fails.
        let opts = StartupOptions { anthropic_endpoint: captured, ..StartupOptions::default() };
        assert!(opts.validate().is_ok(), "a refused variable is not a bad --endpoint");
        let error = opts.api_key_endpoint().expect_err("the refusal must survive");
        assert!(error.0.contains("ANTHROPIC_BASE_URL"), "{}", error.0);

        // An explicit endpoint outranks the variable and fails where it is
        // configured, naming itself rather than the variable.
        let error = StartupOptions::resolve_api_key_endpoint(Some("ftp://proxy.example.com"), env)
            .expect_err("an explicit refusal is fatal");
        assert!(error.0.contains("--endpoint"), "{}", error.0);
        assert!(!error.0.contains("ANTHROPIC_BASE_URL"), "{}", error.0);

        // And a valid explicit endpoint still outranks a refused variable.
        let resolved =
            StartupOptions::resolve_api_key_endpoint(Some("https://proxy.example.com"), env)
                .expect("explicit wins");
        assert_eq!(resolved, ApiKeyEndpoint::Approved("https://proxy.example.com".into()));
    }

    /// The exported key the engine spends must be normalised exactly the way
    /// the login's pre-commit probe normalised the key it proved. A `trim()`
    /// here and `normalize_key` there would let a padded variable pass a check
    /// and then fail every request.
    #[test]
    fn the_ambient_key_is_normalised_the_way_the_login_normalises_it() {
        let dir = tempfile::tempdir().expect("temp profile");
        let context = ProviderContext::for_profile(&Profile::isolated(dir.path()))
            .expect("open")
            .with_env(|name| {
                (name == coda_auth::provider::api_key::ENV_VAR)
                    .then(|| "\u{1}\r\n  sk-ant-PADDED \t\u{2}".to_owned())
            });
        assert_eq!(context.ambient_api_key().as_deref(), Some("sk-ant-PADDED"));
        assert_eq!(
            context.ambient_api_key().as_deref(),
            Some(coda_auth::provider::api_key::normalize_key("\u{1}\r\n  sk-ant-PADDED \t\u{2}"))
        );
    }

    /// A refused `ANTHROPIC_BASE_URL` is an error on every console-key path —
    /// never a quiet fall back to Anthropic's own host, which is where the
    /// key would then go.
    #[tokio::test]
    async fn a_refused_environment_endpoint_fails_closed_rather_than_defaulting() {
        let dir = tempfile::tempdir().expect("temp profile");
        let context = ProviderContext::for_profile(&Profile::isolated(dir.path()))
            .expect("open")
            .with_env(|name| match name {
                "ANTHROPIC_BASE_URL" => Some("http://gateway.example.com".to_owned()),
                _ => None,
            });
        let error = context
            .anthropic_endpoint(None)
            .expect_err("a plaintext non-loopback host must be refused");
        assert!(error.0.contains("ANTHROPIC_BASE_URL"), "{}", error.0);

        // Every API-key origin goes through the same resolution, so all of
        // them refuse: an explicitly supplied key included.
        let selection = Selection {
            identity: ProviderIdentity::AnthropicApiKey,
            source: coda_auth::service::SelectionSource::Explicit,
            origin: CredentialOrigin::Environment,
        };
        assert!(context.build_client(selection, Some("sk-explicit"), None).await.is_err());
    }

    /// The override moves Anthropic API-key requests and nothing else: a
    /// Copilot client resolves its own endpoints and never sees it.
    #[tokio::test]
    async fn the_anthropic_override_does_not_reach_the_copilot_endpoints() {
        let dir = tempfile::tempdir().expect("temp profile");
        let context = ProviderContext::for_profile(&Profile::isolated(dir.path()))
            .expect("open")
            .with_env(|name| match name {
                "ANTHROPIC_BASE_URL" => Some("http://127.0.0.1:9".to_owned()),
                _ => None,
            });
        let config = context.copilot_config().expect("the public defaults resolve");
        assert_eq!(config.api_base_url, "https://api.githubcopilot.com");
        assert!(!config.device_code_url.contains("127.0.0.1"));
    }

    /// `StartupOptions` never leaks the api key through its `Debug` impl.
    #[test]
    fn startup_options_debug_redacts_api_key() {
        let opts = StartupOptions {
            api_key: Some("sk-super-secret".into()),
            ..StartupOptions::default()
        };
        let rendered = format!("{opts:?}");
        assert!(!rendered.contains("sk-super-secret"), "api key must not appear in Debug");
        assert!(rendered.contains("***"));
    }

    /// A host configured with an explicit endpoint retains it across an
    /// `initialize(apiKey)` rebuild instead of reverting to the default host
    /// (Finding I1).
    #[tokio::test]
    async fn initialize_with_key_retains_configured_endpoint() {
        let opts = StartupOptions {
            api_key: Some("sk-initial".into()),
            endpoint: Some("https://proxy.example.com".into()),
            ..StartupOptions::default()
        };
        let (host, _client) = make_capturing_host_with_options(opts);
        assert_eq!(
            host.configured_endpoint.as_ref().expect("approved").as_deref(),
            Some("https://proxy.example.com")
        );

        // Rebuild the client from a wire-supplied key.
        host.initialize(InitParams {
            session_id: None,
            api_key: Some("sk-from-wire".into()),
            ..Default::default()
        })
        .await
        .unwrap();

        // The rebuilt client must be an Anthropic client pointed at the
        // configured endpoint, not the default host.
        let client = host.client.lock().await.clone().expect("client rebuilt");
        assert_eq!(client.provider_id(), "anthropic");
        // Prove the endpoint reached the concrete client, not just that the
        // field was retained (Finding I1).
        let endpoint = host.configured_endpoint.as_ref().expect("approved").clone();
        let rebuilt = build_anthropic_at("sk-from-wire", endpoint.as_deref())
            .expect("anthropic client");
        // build_anthropic_at is what initialize uses; confirm the same inputs
        // yield the configured endpoint via the concrete client accessor.
        let concrete = coda_llm::anthropic::AnthropicClient::new(
            coda_llm::anthropic::AnthropicConfig::api_key("sk-from-wire")
                .with_base_url("https://proxy.example.com"),
        )
        .unwrap();
        assert_eq!(concrete.base_url(), "https://proxy.example.com");
        assert_eq!(rebuilt.provider_id(), "anthropic");
    }

    /// A refusal captured at startup survives for the life of the host.
    ///
    /// This is the case the whole carried-decision shape exists for: the
    /// engine started as something other than the API-key identity (a Copilot
    /// session, legitimately unaffected by a refused `ANTHROPIC_BASE_URL`),
    /// and a client then hands it a key over the wire. Answering that by
    /// building at Anthropic's own host would send the key to a place the user
    /// never configured — quietly, and one handshake after the refusal was
    /// reported. So `initialize` refuses, and nothing is wired.
    #[tokio::test]
    async fn initialize_with_key_refuses_a_captured_endpoint_refusal_rather_than_defaulting() {
        let opts = StartupOptions {
            anthropic_endpoint: ApiKeyEndpoint::Refused(
                "invalid Anthropic endpoint from ANTHROPIC_BASE_URL: plain http …".into(),
            ),
            ..StartupOptions::default()
        };
        let (host, _client) = make_capturing_host_with_options(opts);

        // The host it started as: whatever was wired at build time.
        let before = host.client.lock().await.clone().expect("a startup client");

        let error = host
            .initialize(InitParams {
                session_id: None,
                api_key: Some("sk-from-wire".into()),
                ..Default::default()
            })
            .await
            .expect_err("a captured refusal must not be answered with the default host");
        assert!(error.message.contains("ANTHROPIC_BASE_URL"), "{}", error.message);
        assert!(!error.message.contains("sk-from-wire"), "{}", error.message);

        let after = host.client.lock().await.clone().expect("the client is unchanged");
        assert!(
            Arc::ptr_eq(&before, &after),
            "a refused endpoint still rebuilt the client"
        );
    }

    /// `session/setGoal` rejects a negative continuation budget with -32602
    /// rather than clamping it (Finding I2).
    #[tokio::test]
    async fn set_goal_rejects_negative_continuations() {
        let host = make_host();
        let err = host
            .session_set_goal(SetGoalParams {
                goal: Some("x".into()),
                max_duration: None,
                max_continuations: Some(-3),
            })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32602, "negative continuations must be invalid params");
    }

    /// `session/setSystemPrompt` dispatches correctly.
    #[tokio::test]
    async fn dispatch_set_system_prompt_is_routed() {
        use crate::dispatch::dispatch;
        let host = make_host();
        let r = dispatch(
            "session/setSystemPrompt",
            Some(serde_json::json!({ "text": "test prompt" })),
            host.as_ref(),
        )
        .await
        .unwrap();
        assert_eq!(r["ok"], true);
        assert_eq!(r["cleared"], false);
    }

    // ── credential storage selection ─────────────────────────────────────────
    //
    // Every credential reader in the engine — the Copilot and Claude clients,
    // MCP secret resolution — must land on the *same* store for a profile, and
    // that store must not depend on which credential files happen to exist.
    // These tests run against isolated temporary profiles: no real credential,
    // keyring entry, or network call is touched.

    mod credential_storage_tests {
        use super::*;
        use coda_auth::store::Profile;

        /// The backend a profile resolves to must be the same whether or not a
        /// Copilot credential is sitting in the directory. Choosing DPAPI only
        /// when a Copilot file exists is how a Claude-only user ended up on a
        /// different backend from the one their credential was written to.
        #[test]
        fn the_copilot_credential_file_does_not_decide_the_backend() {
            let dir = tempfile::tempdir().unwrap();
            let profile = Profile::isolated(dir.path());

            let empty = credential_storage_for(&profile).expect("open empty profile");

            let creds = profile.credentials_dir();
            std::fs::create_dir_all(&creds).unwrap();
            std::fs::write(creds.join("llmauth_github-copilot.cred"), b"decoy").unwrap();
            let with_copilot = credential_storage_for(&profile).expect("open again");

            assert_eq!(
                empty.backend, with_copilot.backend,
                "a credential file must not change the backend"
            );
        }

        /// A profile the engine cannot open must fail loudly. Handing back an
        /// empty store instead makes a signed-in user look logged out — and
        /// the next login overwrites whatever was there.
        #[test]
        fn an_unusable_profile_is_an_error_not_an_empty_store() {
            let dir = tempfile::tempdir().unwrap();
            let profile = Profile::isolated(dir.path());
            let creds = profile.credentials_dir();
            std::fs::create_dir_all(&creds).unwrap();
            // A key file no build of ours wrote: the credentials beside it may
            // be unreadable, so nothing may be written here.
            std::fs::write(creds.join("key.bin"), vec![9u8; 100]).unwrap();

            let err = credential_storage_for(&profile)
                .err()
                .expect("an unusable profile must not resolve to a store");
            assert!(
                matches!(err, coda_auth::AuthError::StoreIncompatible { .. }),
                "expected an incompatible-storage error, got {err:?}"
            );
        }

        /// The store must be usable by MCP secret resolution, which shares it.
        #[tokio::test]
        async fn the_resolved_store_serves_mcp_secrets_too() {
            let dir = tempfile::tempdir().unwrap();
            let profile = Profile::isolated(dir.path());
            let storage = credential_storage_for(&profile).expect("open");
            storage.store.set("mcp:github/token", "secret").await.unwrap();
            assert_eq!(
                storage.store.get("mcp:github/token").await.unwrap().as_deref(),
                Some("secret")
            );
        }
    }

    // ── Provider selection and credential construction ───────────────────────
    //
    // These run against isolated temporary profiles and a loopback HTTP
    // server: no real credential, keyring entry, settings file or provider is
    // touched. What they pin down is that the engine's answer to "which
    // account is this machine signed in as?" is the *shared* selector's
    // answer, and that a credential written through the auth service is the
    // one the engine then uses.

    mod provider_selection_tests {
        use super::*;
        use coda_auth::coordination::LocalCoordinator;
        use coda_auth::credential::{Credential, CredentialKind};
        use coda_auth::secret::Secret;
        use coda_auth::service::{
            ApiKeySource, AuthService, AuthSettings, AuthSettingsPort, CommitOutcome,
            InMemoryAuthSettings, LoginRequest, LoginUi, MapEnvironment, ProviderIdentity,
            SelectionSource,
        };
        use coda_auth::store::Profile;
        use std::sync::atomic::{AtomicUsize, Ordering};

        fn isolated() -> (tempfile::TempDir, ProviderContext) {
            let dir = tempfile::tempdir().expect("temp dir");
            let context =
                ProviderContext::for_profile(&Profile::isolated(dir.path())).expect("open");
            (dir, context)
        }

        /// The settings file that belongs to an isolated profile.
        fn settings_path(dir: &tempfile::TempDir) -> std::path::PathBuf {
            let path = dir.path().join(".coda").join("settings.json");
            std::fs::create_dir_all(path.parent().unwrap()).expect("settings dir");
            path
        }

        fn write_settings(dir: &tempfile::TempDir, value: serde_json::Value) {
            std::fs::write(settings_path(dir), value.to_string()).expect("write settings");
        }

        fn credential(provider: &str, kind: CredentialKind) -> Credential {
            Credential {
                provider_id: provider.into(),
                kind,
                access_token: matches!(kind, CredentialKind::OAuth)
                    .then(|| Secret::new("access-token".into())),
                refresh_token: None,
                api_key: matches!(kind, CredentialKind::ApiKey)
                    .then(|| Secret::new("sk-ant-stored".into())),
                expires_at: None,
                scopes: Vec::new(),
                account: None,
            }
        }

        async fn seed(storage: &AuthStorage, credential: &Credential) {
            storage
                .store
                .set(
                    &format!("llmauth:{}", credential.provider_id),
                    &serde_json::to_string(credential).unwrap(),
                )
                .await
                .expect("seed");
        }

        struct KeyUi(String);

        #[async_trait::async_trait]
        impl LoginUi for KeyUi {
            async fn api_key(&self) -> Result<Secret<String>, coda_auth::AuthError> {
                Ok(Secret::new(self.0.clone()))
            }
        }

        /// A loopback server that records the headers of every request.
        struct RecordingApi {
            base_url: String,
            requests: Arc<Mutex<Vec<String>>>,
            hits: Arc<AtomicUsize>,
        }

        impl RecordingApi {
            async fn start(body: &'static str) -> Self {
                use tokio::io::{AsyncReadExt, AsyncWriteExt};
                let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.expect("bind");
                let port = listener.local_addr().expect("addr").port();
                let requests = Arc::new(Mutex::new(Vec::new()));
                let hits = Arc::new(AtomicUsize::new(0));
                let (task_requests, task_hits) = (Arc::clone(&requests), Arc::clone(&hits));
                tokio::spawn(async move {
                    while let Ok((mut socket, _)) = listener.accept().await {
                        let mut buf = vec![0u8; 8192];
                        let read = socket.read(&mut buf).await.unwrap_or(0);
                        task_requests
                            .lock()
                            .unwrap()
                            .push(String::from_utf8_lossy(&buf[..read]).to_string());
                        task_hits.fetch_add(1, Ordering::SeqCst);
                        let response = format!(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                            body.len()
                        );
                        let _ = socket.write_all(response.as_bytes()).await;
                    }
                });
                Self { base_url: format!("http://127.0.0.1:{port}"), requests, hits }
            }

            fn last_request(&self) -> Option<String> {
                self.requests.lock().unwrap().last().cloned()
            }

            fn hits(&self) -> usize {
                self.hits.load(Ordering::SeqCst)
            }
        }

        /// The lazy wiring that `session/prompt` and `session/compact` perform
        /// must not undo a startup refusal.
        ///
        /// The profile has a saved Copilot choice with no credential and an
        /// exported `ANTHROPIC_API_KEY`. Startup correctly wires nothing. If
        /// the first prompt reached for the ambient key, the user would be
        /// silently connected to a different account than the one they chose.
        #[tokio::test]
        async fn a_saved_choice_without_a_credential_is_not_undone_by_a_prompt() {
            for lazy in ["prompt", "compact"] {
                let (dir, context) = isolated();
                write_settings(&dir, serde_json::json!({ "defaultProvider": "github-copilot" }));
                let context = context.with_env(|key| {
                    (key == "ANTHROPIC_API_KEY").then(|| "sk-ambient".to_owned())
                });
                let host = client_less_host_with(context);

                let error = match lazy {
                    "prompt" => host
                        .session_prompt(PromptParams { text: Some("go".into()), images: None })
                        .await
                        .err(),
                    _ => host.session_compact(CompactParams::default()).await.err(),
                };

                assert!(error.is_some(), "{lazy}: an unwired engine must refuse the turn");
                assert!(
                    host.client.lock().await.is_none(),
                    "{lazy}: the refusal must stand; no provider may be wired from the ambient key"
                );
                let state = host.session_get_state(GetStateParams::default()).await.unwrap();
                assert!(
                    state["config"]["next"].get("providerId").is_none(),
                    "{lazy}: no provider change may be announced: {}",
                    state["config"]
                );
            }
        }

        /// The same holds for an explicit `--provider` request: the lazy path
        /// must re-run *that* selection, not a fresh one without it.
        #[tokio::test]
        async fn an_explicit_provider_request_survives_into_the_lazy_wiring() {
            let (dir, context) = isolated();
            // Nothing saved, so a selection that forgot `--provider` would see
            // an unconfigured profile and take the ambient key.
            write_settings(&dir, serde_json::json!({}));
            let context = context
                .with_env(|key| (key == "ANTHROPIC_API_KEY").then(|| "sk-ambient".to_owned()));
            let host = client_less_host_with_options(
                context,
                StartupOptions { provider: Some("claude-ai".into()), ..StartupOptions::default() },
            );

            let _ = host
                .session_prompt(PromptParams { text: Some("go".into()), images: None })
                .await;

            assert!(
                host.client.lock().await.is_none(),
                "the requested provider has no credential; the ambient key is a different account"
            );
        }

        /// An unreadable credential for the chosen provider is a storage
        /// fault, and equally must not be answered with the ambient key.
        #[tokio::test]
        async fn an_unreadable_choice_is_not_answered_with_the_ambient_key() {
            let (dir, context) = isolated();
            write_settings(&dir, serde_json::json!({ "defaultProvider": "github-copilot" }));
            context
                .storage()
                .store
                .set("llmauth:github-copilot", "{not json")
                .await
                .expect("seed");
            let context = context
                .with_env(|key| (key == "ANTHROPIC_API_KEY").then(|| "sk-ambient".to_owned()));
            let host = client_less_host_with(context);

            let _ = host
                .session_prompt(PromptParams { text: Some("go".into()), images: None })
                .await;

            assert!(host.client.lock().await.is_none());
        }

        /// The legitimate unconfigured case still works: nothing stored,
        /// nothing chosen, an exported key — that is the selector's ambient
        /// case, and the prompt wires it.
        #[tokio::test]
        async fn an_unconfigured_profile_still_wires_the_ambient_key() {
            let (_dir, context) = isolated();
            let context = context
                .with_env(|key| (key == "ANTHROPIC_API_KEY").then(|| "sk-ambient".to_owned()));
            let host = client_less_host_with(context);

            let _ = host
                .session_prompt(PromptParams { text: Some("go".into()), images: None })
                .await;

            let client = host.client.lock().await.clone();
            assert_eq!(
                client.map(|c| c.provider_id().to_owned()).as_deref(),
                Some("anthropic"),
                "an unconfigured profile with an exported key is the ambient case"
            );
        }

        /// The ambient case is exactly where a captured endpoint refusal has to
        /// bite. The profile is unconfigured and a key is exported, so the
        /// lazy wiring resolves the **API-key** identity — the one identity
        /// `ANTHROPIC_BASE_URL` configures — and this host recorded a refusal
        /// for it at startup.
        ///
        /// The injected context deliberately has *no* `ANTHROPIC_BASE_URL` in
        /// its environment: if the host re-resolved instead of consulting the
        /// decision it carries, the key would be wired to the default host and
        /// the refusal reported at startup would be silently reversed one
        /// prompt later.
        #[tokio::test]
        async fn a_captured_endpoint_refusal_stops_the_lazy_api_key_wiring() {
            let (_dir, context) = isolated();
            let context = context
                .with_env(|key| (key == "ANTHROPIC_API_KEY").then(|| "sk-ambient".to_owned()));
            let host = client_less_host_with_options(
                context,
                StartupOptions {
                    anthropic_endpoint: ApiKeyEndpoint::Refused(
                        "invalid Anthropic endpoint from ANTHROPIC_BASE_URL: refused".into(),
                    ),
                    ..StartupOptions::default()
                },
            );

            let _ = host
                .session_prompt(PromptParams { text: Some("go".into()), images: None })
                .await;

            assert!(
                host.client.lock().await.is_none(),
                "a refused endpoint must not be answered by wiring the key to the default host"
            );
        }

        /// …and bites *only* there. A refusal recorded for the API-key
        /// identity must not stop the lazy wiring from selecting a different
        /// account: Claude.ai resolves its own endpoint and never sees this
        /// value.
        ///
        /// Proved by where the attempt fails. With the refusal leaking across
        /// identities, the selection is abandoned before the credential is
        /// ever read; here the stored Claude.ai credential is read and used.
        #[tokio::test]
        async fn a_captured_endpoint_refusal_does_not_reach_the_claude_wiring() {
            let (dir, context) = isolated();
            write_settings(&dir, serde_json::json!({ "defaultProvider": "claude-ai" }));
            seed(context.storage(), &credential("claude-ai", CredentialKind::OAuth)).await;
            let host = client_less_host_with_options(
                context,
                StartupOptions {
                    anthropic_endpoint: ApiKeyEndpoint::Refused(
                        "invalid Anthropic endpoint from ANTHROPIC_BASE_URL: refused".into(),
                    ),
                    ..StartupOptions::default()
                },
            );

            let _ = host
                .session_prompt(PromptParams { text: Some("go".into()), images: None })
                .await;

            let client = host.client.lock().await.clone();
            assert_eq!(
                client.map(|c| c.provider_id().to_owned()).as_deref(),
                Some("claude-ai"),
                "a refused Anthropic API-key endpoint blocked a Claude.ai session"
            );
        }

        /// The same scope rule at the construction seam: a refused
        /// `ANTHROPIC_BASE_URL` in a context's own environment fails the
        /// API-key identity and leaves Claude.ai alone. If it leaked, the
        /// error would name the variable instead of the credential.
        #[tokio::test]
        async fn a_refused_environment_endpoint_never_fails_a_claude_client() {
            let (_dir, context) = isolated();
            let context = context.with_env(|name| match name {
                "ANTHROPIC_BASE_URL" => Some("http://gateway.example.com".to_owned()),
                _ => None,
            });
            seed(context.storage(), &credential("claude-ai", CredentialKind::OAuth)).await;

            let selection = Selection {
                identity: ProviderIdentity::ClaudeAi,
                source: SelectionSource::SoleStored,
                origin: CredentialOrigin::Stored,
            };
            let client = context
                .build_client(selection, None, None)
                .await
                .expect("a refused Anthropic endpoint must not fail a Claude.ai client");
            assert_eq!(client.provider_id(), "claude-ai");
        }

        /// A host that was never given a context reads nothing at all — this
        /// is what keeps every client-less fixture off the developer's real
        /// credentials.
        #[tokio::test]
        async fn a_host_without_a_context_performs_no_credential_lookup() {
            let host = make_host();
            let _ = host
                .session_prompt(PromptParams { text: Some("go".into()), images: None })
                .await;
            assert!(host.client.lock().await.is_none());
        }

        fn client_less_host_with(context: ProviderContext) -> Arc<ServeHost> {
            client_less_host_with_options(context, StartupOptions::default())
        }

        fn client_less_host_with_options(
            context: ProviderContext,
            startup: StartupOptions,
        ) -> Arc<ServeHost> {
            let (tx, _rx) = mpsc::unbounded_channel::<Vec<u8>>();
            let sink = Arc::new(ServeSink::new(tx.clone()));
            let ch = Arc::new(PromptChannel::new(tx));
            let host = ServeHost::new_with_optional_client_and_mcp(
                None,
                sink,
                ch,
                std::env::temp_dir().to_string_lossy().into_owned(),
                crate::mcp::McpBundle::disabled(),
                startup,
                None,
                None,
            );
            host.set_provider_context(Arc::new(context));
            host
        }

        /// The production path must read the configuration and the credential
        /// as one snapshot.
        ///
        /// A competing commit is held against the profile's section while the
        /// construction is paused inside its configuration read. Whatever
        /// order the two end up in, the client must be built from a *coherent*
        /// pair: the old account's token to the old endpoints, or the new
        /// account's to the new ones — never a new token to an abandoned host.
        /// Resolving the configuration before taking the section (the shape
        /// this test was written against) produces exactly that mixture.
        #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
        async fn the_engine_reads_the_copilot_config_and_credential_as_one_snapshot() {
            use coda_auth::coordination::AUTH_COMMIT_KEY;

            let old_host = RecordingApi::start(r#"{"data":[{"id":"gpt-5"}]}"#).await;
            let new_host = RecordingApi::start(r#"{"data":[{"id":"gpt-5"}]}"#).await;

            let (_dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            let mut old_account = credential("github-copilot", CredentialKind::OAuth);
            old_account.access_token = Some(Secret::new("old-account-token".into()));
            seed(&storage, &old_account).await;

            // The base URL this context resolves to, and a gate that pauses
            // the resolution once — *after* it has sampled the value, which is
            // what a configuration read does.
            let base = Arc::new(Mutex::new(old_host.base_url.clone()));
            let (reached_tx, reached_rx) = std::sync::mpsc::sync_channel::<()>(1);
            let (go_tx, go_rx) = std::sync::mpsc::sync_channel::<()>(1);
            let gate = Mutex::new(Some((reached_tx, go_rx)));
            let base_for_env = Arc::clone(&base);
            let context = context.with_env(move |key| {
                if key != "GH_COPILOT_API_BASE_URL" {
                    return None;
                }
                let sampled = base_for_env.lock().unwrap().clone();
                if let Some((reached, go)) = gate.lock().unwrap().take() {
                    let _ = reached.send(());
                    let _ = go.recv();
                }
                Some(sampled)
            });

            let build = tokio::spawn(async move {
                let selection = context.select(Some("copilot")).await.expect("selection");
                context.build_client(selection, None, None).await
            });

            // The construction is inside its configuration read.
            reached_rx.recv().expect("the configuration resolution starts");

            // A real commit by another holder of the profile: it takes the
            // section, replaces the account, and moves the deployment.
            let (wrote_tx, wrote_rx) = std::sync::mpsc::sync_channel::<()>(1);
            let commit_storage = Arc::clone(&storage);
            let commit_base = Arc::clone(&base);
            let new_base = new_host.base_url.clone();
            let competitor = tokio::spawn(async move {
                let _section = commit_storage
                    .coordinator
                    .begin(AUTH_COMMIT_KEY)
                    .await
                    .expect("section");
                let mut new_account = credential("github-copilot", CredentialKind::OAuth);
                new_account.access_token = Some(Secret::new("new-account-token".into()));
                new_account.refresh_token = Some(Secret::new("ghu_new_account".into()));
                seed(&commit_storage, &new_account).await;
                *commit_base.lock().unwrap() = new_base;
                let _ = wrote_tx.send(());
            });

            // If the construction is not holding the section, the commit lands
            // now — that is the interleaving the old ordering mixes up. If it
            // is holding the section, the commit waits, and this returns.
            let _ = wrote_rx.recv_timeout(std::time::Duration::from_millis(500));

            go_tx.send(()).expect("release the configuration read");
            let client = build.await.expect("the build task finishes").expect("a client");
            client.refresh_models().await.expect("the bound account works");
            competitor.await.expect("the competing commit finishes");

            // Whichever host was contacted, it must have been given its own
            // account's token.
            let old_request = old_host.last_request().unwrap_or_default().to_ascii_lowercase();
            let new_request = new_host.last_request().unwrap_or_default().to_ascii_lowercase();
            assert!(
                !old_request.contains("new-account-token"),
                "a new account's token reached the endpoints it does not belong to: {old_request}"
            );
            assert!(
                !new_request.contains("old-account-token"),
                "an old account's token reached the new deployment: {new_request}"
            );
            assert_eq!(
                old_host.hits() + new_host.hits(),
                1,
                "exactly one request was made, and it was a coherent pair"
            );
        }

        /// A Copilot client built by the engine is bound to the credential it
        /// read, so another process signing a different account in cannot have
        /// that account's token sent to this client's inference host.
        #[tokio::test]
        async fn an_engine_copilot_client_refuses_an_account_stored_by_another_process() {
            use coda_auth::provider::copilot::{CopilotConfig, CopilotDeployment};

            let (_dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            let mine = credential("github-copilot", CredentialKind::OAuth);
            seed(&storage, &mine).await;

            let api = RecordingApi::start(r#"{"data":[{"id":"gpt-5"}]}"#).await;
            let auth_config = CopilotConfig {
                // No exchange: the stored token is used as-is, so this test is
                // about which *account* the client presents, not refreshing.
                copilot_token_url: None,
                use_exchange: false,
                api_base_url: api.base_url.clone(),
                ..CopilotConfig::default_public()
            };
            let (client, diagnostic) = build_copilot_from_storage(
                &storage,
                auth_config,
                CopilotDeployment::Public,
            )
            .await;
            assert!(diagnostic.is_none(), "{diagnostic:?}");
            let client = client.expect("the stored credential builds a client");
            client.refresh_models().await.expect("the bound credential works");
            let hits = api.hits();

            // Out of band: another process signs a different account in.
            let mut theirs = credential("github-copilot", CredentialKind::OAuth);
            theirs.access_token = Some(Secret::new("someone-elses-token".into()));
            theirs.refresh_token = Some(Secret::new("ghu_someone_else".into()));
            seed(&storage, &theirs).await;

            let error = client
                .refresh_models()
                .await
                .expect_err("another account's token must not be sent to this client's host");
            assert!(
                matches!(error, coda_llm::LlmError::Unauthorized(_)),
                "expected an authentication refusal, got {error:?}"
            );
            assert_eq!(api.hits(), hits, "and no request may carry it");

            // Rebuilding is the answer: a fresh client binds to the account
            // that is stored now.
            let auth_config = CopilotConfig {
                copilot_token_url: None,
                use_exchange: false,
                api_base_url: api.base_url.clone(),
                ..CopilotConfig::default_public()
            };
            let (rebuilt, _) =
                build_copilot_from_storage(&storage, auth_config, CopilotDeployment::Public).await;
            rebuilt
                .expect("rebuilt client")
                .refresh_models()
                .await
                .expect("the account stored now works through a fresh client");
            let request = api.last_request().expect("a request reached the provider");
            assert!(
                request.to_ascii_lowercase().contains("someone-elses-token"),
                "the rebuilt client carries the account that is stored now: {request}"
            );
        }

        /// A key committed through the auth service is the key a freshly built
        /// engine client authenticates with.
        #[tokio::test]
        async fn a_stored_api_key_is_used_by_a_freshly_built_engine_client() {
            let (dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            let settings: Arc<dyn AuthSettingsPort> = Arc::new(InMemoryAuthSettings::new());
            let service = AuthService::builder(
                Arc::clone(&storage.profile),
                Arc::clone(&storage.coordinator),
            )
            .with_settings(Arc::clone(&settings))
            .with_environment(Arc::new(MapEnvironment::new(&[])))
            .build()
            .await
            .expect("a clean context builds");

            let prepared = service
                .prepare_login(
                    LoginRequest::api_key(ApiKeySource::Prompt).without_validation(),
                    &KeyUi("sk-ant-written-by-the-service".into()),
                )
                .await
                .expect("prepared");
            let outcome = service.commit_login(prepared).await;
            assert!(matches!(outcome, CommitOutcome::Committed { .. }), "{outcome:?}");

            // A *fresh* storage handle, as a newly started engine would open.
            let reopened = credential_storage_for(&Profile::isolated(dir.path())).expect("reopen");
            let api = RecordingApi::start(r#"{"data":[{"id":"claude-opus-5"}]}"#).await;
            let (client, diagnostic) =
                build_anthropic_from_storage(&reopened, Some(&api.base_url)).await;
            assert!(diagnostic.is_none(), "{diagnostic:?}");
            let client = client.expect("a stored key must build a client");
            assert_eq!(client.provider_id(), "anthropic");

            let models = client.refresh_models().await.expect("model listing");
            assert_eq!(models.len(), 1);
            let request = api.last_request().expect("a request reached the provider");
            assert!(
                request.to_ascii_lowercase().contains("x-api-key: sk-ant-written-by-the-service"),
                "the stored key must be the credential on the wire: {request}"
            );
        }

        /// After the credential is removed, the same client must stop
        /// authenticating rather than fall back to a token it cached earlier.
        #[tokio::test]
        async fn a_removed_key_cannot_authenticate_through_a_stale_fallback() {
            let (_dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            seed(&storage, &credential("anthropic-api-key", CredentialKind::ApiKey)).await;
            let api = RecordingApi::start(r#"{"data":[{"id":"claude-opus-5"}]}"#).await;
            let (client, _) = build_anthropic_from_storage(&storage, Some(&api.base_url)).await;
            let client = client.expect("client");
            client.refresh_models().await.expect("the first call works");
            let hits_before = api.hits();

            storage.store.delete("llmauth:anthropic-api-key").await.expect("logout");

            let error = client
                .refresh_models()
                .await
                .expect_err("a removed credential must stop the request");
            assert!(
                matches!(error, coda_llm::LlmError::Unauthorized(_)),
                "expected an authentication failure, got {error:?}"
            );
            assert_eq!(api.hits(), hits_before, "no request may be sent without a credential");

            // And a newly built client reports "not signed in", not an error.
            let (rebuilt, diagnostic) = build_anthropic_from_storage(&storage, None).await;
            assert!(rebuilt.is_none());
            assert!(diagnostic.is_none(), "an absent credential needs no diagnostic");
        }

        /// A Claude subscription keeps its own public identity even though it
        /// shares the Anthropic transport with the console key.
        #[tokio::test]
        async fn the_claude_client_reports_the_subscription_identity() {
            let (_dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            seed(&storage, &credential("claude-ai", CredentialKind::OAuth)).await;

            let (client, diagnostic) = build_claude_from_storage(&storage).await;
            assert!(diagnostic.is_none(), "{diagnostic:?}");
            let client = client.expect("claude client");
            assert_eq!(
                client.provider_id(),
                "claude-ai",
                "a subscription must not be reported as an API key"
            );

            // And the model row it reads is the subscription's, not the key's.
            let settings = serde_json::json!({
                "modelByProvider": { "claude-ai": "subscription-model", "anthropic": "key-model" }
            });
            let resolved =
                crate::settings::resolve_for_provider_from(&settings, Some(client.provider_id()));
            assert_eq!(resolved.model, "subscription-model");
        }

        /// A Copilot client must be built from the *injected* profile and the
        /// *injected* settings — never from an ambient store or an ambient
        /// tenant configuration.
        #[tokio::test]
        async fn a_copilot_client_uses_the_injected_profile_and_tenant() {
            let (dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            seed(&storage, &credential("github-copilot", CredentialKind::OAuth)).await;

            let api = RecordingApi::start(r#"{"data":[{"id":"gpt-5"}]}"#).await;
            // The tenant comes from this profile's settings; the inference
            // endpoint is redirected to the local recorder.
            write_settings(&dir, serde_json::json!({ "githubEnterpriseDomain": "octocorp.ghe.com" }));
            let base = api.base_url.clone();
            let context = context.with_env(move |key| match key {
                "GH_COPILOT_API_BASE_URL" => Some(base.clone()),
                _ => None,
            });

            let selection = context.select(Some("copilot")).await.expect("selection");
            let client = context
                .build_client(selection, None, None)
                .await
                .expect("the injected credential must build a client");
            assert_eq!(client.provider_id(), "github-copilot");

            let models = client.refresh_models().await.expect("model listing");
            assert_eq!(models.len(), 1);
            let request = api.last_request().expect("a request reached the provider");
            let lowered = request.to_ascii_lowercase();
            assert!(
                lowered.contains("authorization: bearer access-token"),
                "the injected credential must be the one on the wire: {request}"
            );
            assert!(
                lowered.contains("editor-version:"),
                "the resolved tenant config supplies the identity headers: {request}"
            );
        }

        /// An unreadable tenant configuration must stop the Copilot path
        /// rather than fall back to public github.com.
        #[tokio::test]
        async fn an_invalid_tenant_configuration_stops_the_copilot_client() {
            let (dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            seed(&storage, &credential("github-copilot", CredentialKind::OAuth)).await;
            write_settings(
                &dir,
                serde_json::json!({ "githubEnterpriseDomain": "https://octocorp.ghe.com/path" }),
            );

            let selection = context.select(Some("copilot")).await.expect("selection");
            let error = context
                .build_client(selection, None, None)
                .await
                .err()
                .expect("an unresolvable tenant must not become public github.com");
            assert!(error.to_string().contains("Copilot configuration"), "{error}");
        }

        /// The engine's selection is the shared selector's selection, case by
        /// case — including the cases where something cannot be read.
        #[tokio::test]
        async fn the_engine_and_the_service_agree_on_every_selection_case() {
            // Explicit provider with no credential: needs login, never a
            // fallback to the Copilot credential that is right there.
            let (_dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            seed(&storage, &credential("github-copilot", CredentialKind::OAuth)).await;
            let error = context.select(Some("claude-ai")).await.expect_err("must fail closed");
            assert!(
                matches!(error, SelectionError::NeedsLogin { identity, .. } if identity == ProviderIdentity::ClaudeAi),
                "{error:?}"
            );

            // Nothing chosen: the sole stored credential.
            let selection = context
                .select(None)
                .await
                .expect("one stored credential is unambiguous");
            assert_eq!(selection.identity, ProviderIdentity::GithubCopilot);
            assert_eq!(selection.source, SelectionSource::SoleStored);

            // Two stored credentials are an ambiguity, not a map order.
            seed(&storage, &credential("claude-ai", CredentialKind::OAuth)).await;
            let error = context
                .select(None)
                .await
                .expect_err("two accounts must not be resolved silently");
            assert!(matches!(error, SelectionError::Ambiguous { .. }), "{error:?}");

            // An empty profile selects nothing, which is not a failure.
            let (_empty_dir, empty) = isolated();
            let error = empty.select(None).await.expect_err("an empty profile selects nothing");
            assert!(matches!(error, SelectionError::NoCredentials), "{error:?}");
        }

        /// The engine refuses in exactly the cases the service refuses, for
        /// inputs it could not read.
        #[tokio::test]
        async fn unreadable_inputs_make_the_engine_selection_unavailable_too() {
            // A saved choice that cannot be read.
            for broken in [
                serde_json::json!({ "defaultProvider": 7 }).to_string(),
                "{ not json".to_string(),
            ] {
                let (dir, context) = isolated();
                let storage = Arc::clone(context.storage());
                seed(&storage, &credential("github-copilot", CredentialKind::OAuth)).await;
                std::fs::write(settings_path(&dir), broken).expect("write");

                let error = context
                    .select(None)
                    .await
                    .expect_err("a settings file we cannot read is not 'no choice'");
                assert!(matches!(error, SelectionError::Unavailable { .. }), "{error:?}");
            }

            // A settings *path* that cannot be read at all (a directory here).
            let (dir, context) = isolated();
            std::fs::create_dir_all(settings_path(&dir)).expect("directory in place of the file");
            let error = context.select(None).await.expect_err("an I/O failure is not 'no choice'");
            assert!(matches!(error, SelectionError::Unavailable { .. }), "{error:?}");

            // An unreadable credential beside a readable one.
            let (_dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            storage.store.set("llmauth:claude-ai", "{not json").await.expect("seed");
            seed(&storage, &credential("github-copilot", CredentialKind::OAuth)).await;
            let error = context
                .select(None)
                .await
                .expect_err("'the only account is Copilot' is not knowable here");
            assert!(matches!(error, SelectionError::Unavailable { .. }), "{error:?}");

            // ...and the service, over the same profile, says the same thing.
            let service = AuthService::builder(
                Arc::clone(&storage.profile),
                Arc::clone(&storage.coordinator),
            )
            .with_environment(Arc::new(MapEnvironment::new(&[])))
            .build()
            .await
            .expect("a clean context builds");
            assert!(
                matches!(
                    service.selected_provider().await,
                    Err(SelectionError::Unavailable { .. })
                ),
                "the engine and the service must agree"
            );

            // Naming the unreadable credential explicitly is still a storage
            // fault, not a missing login.
            let error = context
                .select(Some("claude-ai"))
                .await
                .expect_err("an unreadable credential must not read as 'sign in'");
            assert!(matches!(error, SelectionError::Unavailable { .. }), "{error:?}");
        }

        /// An explicitly requested provider with a stored credential builds
        /// that provider's client — and an unknown name is still refused.
        #[tokio::test]
        async fn an_explicit_provider_uses_its_stored_credential() {
            let (_dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            seed(&storage, &credential("anthropic-api-key", CredentialKind::ApiKey)).await;

            let client = build_client_for_provider_with(&context, "anthropic", None, None)
                .await
                .expect("the stored key satisfies the request");
            assert_eq!(client.provider_id(), "anthropic");

            let error = build_client_for_provider_with(&context, "not-a-provider", None, None)
                .await
                .err()
                .expect("an unknown provider must be refused");
            assert!(error.to_string().contains("unknown provider"), "{error}");

            let error = build_client_for_provider_with(&context, "claude-ai", None, None)
                .await
                .err()
                .expect("a provider without a credential must fail closed");
            assert!(error.to_string().contains("sign in"), "{error}");
        }

        /// The saved model row keeps working under the spelling an older build
        /// wrote for the API-key identity.
        #[test]
        fn the_api_key_model_row_is_read_under_its_legacy_spelling() {
            let legacy = serde_json::json!({
                "defaultModel": "fallback",
                "modelByProvider": { "anthropic-api-key": "legacy-row" }
            });
            assert_eq!(
                crate::settings::resolve_for_provider_from(&legacy, Some("anthropic")).model,
                "legacy-row"
            );

            // The canonical row wins when both are present.
            let both = serde_json::json!({
                "modelByProvider": { "anthropic": "canonical-row", "anthropic-api-key": "legacy-row" }
            });
            assert_eq!(
                crate::settings::resolve_for_provider_from(&both, Some("anthropic")).model,
                "canonical-row"
            );
        }

        /// The saved provider is read from settings; a blank one is not a
        /// choice, and a file we cannot read is a fault, not "unconfigured".
        #[test]
        fn the_saved_provider_is_read_and_a_corrupt_file_is_not_silence() {
            let dir = tempfile::tempdir().unwrap();
            let path = dir.path().join("settings.json");
            std::fs::write(&path, r#"{"defaultProvider":"claude-ai"}"#).unwrap();
            assert_eq!(
                crate::settings::saved_default_provider_at(&path).expect("read").as_deref(),
                Some("claude-ai")
            );

            std::fs::write(&path, r#"{"defaultProvider":"   "}"#).unwrap();
            assert_eq!(crate::settings::saved_default_provider_at(&path).expect("read"), None);

            std::fs::write(&path, "{ not json").unwrap();
            assert!(
                crate::settings::saved_default_provider_at(&path).is_err(),
                "a corrupt settings file must not read as 'no provider chosen'"
            );

            std::fs::write(&path, r#"{"defaultProvider":7}"#).unwrap();
            assert!(crate::settings::saved_default_provider_at(&path).is_err());

            // An absent file is a first run, which is not an error.
            assert_eq!(
                crate::settings::saved_default_provider_at(&dir.path().join("absent.json"))
                    .expect("read"),
                None
            );
        }

        /// The auth service and the engine read the same saved choice — the
        /// service writes `defaultProvider` to the profile's settings file,
        /// and the engine's next selection is that account.
        #[tokio::test]
        async fn a_committed_login_is_what_the_engine_selects_next() {
            let (dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            // The real file-backed port, at this profile's settings path, so
            // the two sides are reading and writing the same document.
            let settings: Arc<dyn AuthSettingsPort> =
                Arc::new(coda_boot::settings_store::SettingsFile::at(settings_path(&dir)));
            let service = AuthService::builder(
                Arc::clone(&storage.profile),
                Arc::clone(&storage.coordinator),
            )
            .with_settings(Arc::clone(&settings))
            .with_environment(Arc::new(MapEnvironment::new(&[])))
            .build()
            .await
            .expect("a clean context builds");

            let prepared = service
                .prepare_login(
                    LoginRequest::api_key(ApiKeySource::Prompt).without_validation(),
                    &KeyUi("sk-ant-key".into()),
                )
                .await
                .expect("prepared");
            assert!(matches!(
                service.commit_login(prepared).await,
                CommitOutcome::Committed { .. }
            ));

            assert_eq!(
                settings.load().expect("read back"),
                AuthSettings {
                    default_provider: Some("anthropic".into()),
                    github_enterprise_domain: None
                }
            );

            let selection = context
                .select(None)
                .await
                .expect("the engine selects the account that was just committed");
            assert_eq!(selection.identity, ProviderIdentity::AnthropicApiKey);
            assert_eq!(selection.source, SelectionSource::SavedDefault);

            // And the service agrees.
            let service_selection = service.selected_provider().await.expect("service selection");
            assert_eq!(service_selection.identity, selection.identity);
            assert_eq!(service_selection.source, selection.source);
        }

        /// Coordination is not a local invention: two services over the same
        /// profile share one commit section.
        #[tokio::test]
        async fn the_profile_coordinator_is_shared_not_per_service() {
            let (_dir, context) = isolated();
            let storage = Arc::clone(context.storage());
            let first = AuthService::builder(
                Arc::clone(&storage.profile),
                Arc::clone(&storage.coordinator),
            )
            .build()
            .await
            .expect("builds");
            let second = AuthService::builder(
                Arc::clone(&storage.profile),
                Arc::clone(&storage.coordinator),
            )
            .build()
            .await
            .expect("builds");
            assert!(!Arc::ptr_eq(&first.manager(), &second.manager()));
            let _ = LocalCoordinator::new();
            assert!(Arc::ptr_eq(&storage.coordinator, &storage.coordinator));
        }
    }

    // ── Claude credential diagnostics ────────────────────────────────────────
    //
    // A credential that cannot be read is not a user who never signed in.
    // Telling them to "sign in first" hides a storage fault and invites a
    // fresh login that overwrites the credential that was still there.

    mod claude_diagnostic_tests {
        use super::*;
        use coda_auth::store::{AuthStorage, BackendKind, InMemoryStore};

        /// A store whose reads fail the way an unreadable profile does.
        struct UnreadableStore;

        #[async_trait::async_trait]
        impl CredentialStore for UnreadableStore {
            async fn get(&self, key: &str) -> Result<Option<String>, coda_auth::AuthError> {
                Err(coda_auth::AuthError::StoreUndecryptable {
                    key: key.to_owned(),
                    detail: "the file does not decrypt with this profile's key".into(),
                })
            }
            async fn set(&self, _: &str, _: &str) -> Result<(), coda_auth::AuthError> {
                Ok(())
            }
            async fn delete(&self, _: &str) -> Result<(), coda_auth::AuthError> {
                Ok(())
            }
        }

        fn storage_over(store: Arc<dyn CredentialStore>) -> AuthStorage {
            AuthStorage::over_primary(
                store,
                BackendKind::EncryptedFile,
                std::env::temp_dir().join("coda-claude-diagnostic-tests"),
            )
        }

        /// Nothing stored is the ordinary "not signed in" case: no diagnostic.
        #[tokio::test]
        async fn an_empty_store_is_reported_as_not_signed_in() {
            let storage = storage_over(Arc::new(InMemoryStore::new()));
            let (client, diagnostic) = build_claude_from_storage(&storage).await;
            assert!(client.is_none());
            assert!(diagnostic.is_none(), "an absent credential needs no diagnostic");
        }

        /// A credential that exists but cannot be read must produce a
        /// diagnostic, and it must not read as "sign in first".
        #[tokio::test]
        async fn an_unreadable_credential_produces_a_diagnostic() {
            let storage = storage_over(Arc::new(UnreadableStore));
            let (client, diagnostic) = build_claude_from_storage(&storage).await;
            assert!(client.is_none());
            let diagnostic = diagnostic.expect("a storage failure must be reported");
            assert!(
                diagnostic.contains("decrypt"),
                "the diagnostic must describe the storage fault: {diagnostic}"
            );

            let startup = claude_startup_error(Some(&diagnostic));
            assert!(
                !startup.0.contains("sign in first"),
                "a storage fault must not be presented as a missing login: {}",
                startup.0
            );
            assert!(startup.0.contains(&diagnostic), "the reason must reach the user");
        }

        /// With nothing stored, the startup error is still the sign-in prompt.
        #[test]
        fn a_missing_credential_still_asks_the_user_to_sign_in() {
            let startup = claude_startup_error(None);
            assert!(startup.0.contains("sign in"), "got {}", startup.0);
        }
    }

    // ── build_copilot_from_store: enterprise routing and error classification ─
    //
    // These tests verify the three host-wiring properties guaranteed by the
    // refactor:
    //   1. Public default (no env): no diagnostic, client built when cred present.
    //   2. Enterprise domain: enterprise api_base_url is applied to the client.
    //   3. Refresh error: classified as (None, Some(diag)), NOT (None, None).
    // Tests run entirely against an InMemoryStore — no real keyring is touched.

    mod build_copilot_from_store_tests {
        use super::*;
        use coda_auth::store::InMemoryStore;
        use coda_auth::{Credential, CredentialKind, Secret};

        /// A Copilot credential with an expiry well in the future — no refresh
        /// will be triggered, which keeps these tests hermetic (no network).
        fn fresh_credential() -> Credential {
            Credential {
                provider_id: "github-copilot".into(),
                kind: CredentialKind::OAuth,
                access_token: Some(Secret::new("fake-copilot-token".into())),
                refresh_token: Some(Secret::new("fake-github-token".into())),
                api_key: None,
                expires_at: Some(chrono::Utc::now() + chrono::Duration::hours(24)),
                scopes: Vec::new(),
                account: None,
            }
        }

        /// Seeds an InMemoryStore with the given credential under the manager key.
        async fn seed(store: &InMemoryStore, cred: &Credential) {
            let json = serde_json::to_string(cred).unwrap();
            store.set("llmauth:github-copilot", &json).await.unwrap();
        }

        /// An empty store means "not signed in" — no diagnostic should be set.
        #[tokio::test]
        async fn empty_store_returns_none_without_diagnostic() {
            let store = Arc::new(InMemoryStore::new());
            let (client, diag) = build_copilot_from_store(store, AuthCopilotConfig::default_public()).await;
            assert!(client.is_none(), "no client when no credential is stored");
            assert!(diag.is_none(), "no diagnostic for 'not signed in'");
        }

        /// A stored, still-valid credential builds a client without a diagnostic.
        #[tokio::test]
        async fn valid_credential_builds_client_no_diagnostic() {
            let store = Arc::new(InMemoryStore::new());
            seed(&store, &fresh_credential()).await;

            let (client, diag) = build_copilot_from_store(
                Arc::clone(&store) as Arc<dyn CredentialStore>,
                AuthCopilotConfig::default_public(),
            )
            .await;
            assert!(client.is_some(), "client must be built when credential is present");
            assert!(diag.is_none(), "no diagnostic on success");
        }

        /// With an enterprise auth config the client is still built and the
        /// enterprise api_base_url from the config is used (not the public default).
        ///
        /// CopilotConfig::with_token("") defaults to "https://api.githubcopilot.com";
        /// build_copilot_from_store must override that with auth_config.api_base_url.
        /// We verify by ensuring the enterprise config round-trips correctly and the
        /// client builds — the api_base_url is set in the config before the client
        /// is constructed, so a build success proves it was accepted.
        #[tokio::test]
        async fn enterprise_config_builds_client_with_enterprise_base_url() {
            let store = Arc::new(InMemoryStore::new());
            seed(&store, &fresh_credential()).await;

            let enterprise_config =
                AuthCopilotConfig::for_enterprise("octocorp.ghe.com").expect("valid enterprise config");
            assert_eq!(
                enterprise_config.api_base_url, "https://copilot-api.octocorp.ghe.com",
                "pre-condition: enterprise config must have enterprise api_base_url"
            );

            let (client, diag) = build_copilot_from_store(
                Arc::clone(&store) as Arc<dyn CredentialStore>,
                enterprise_config,
            )
            .await;
            assert!(client.is_some(), "enterprise client must build when credential is present");
            assert!(diag.is_none(), "no diagnostic on success");
        }

        /// Regression guard: verifies that `build_copilot_from_store` applies
        /// `auth_config.api_base_url` to the `CopilotConfig` builder.
        ///
        /// Strategy: override `api_base_url` to point at a local mock server.
        /// Call `list_models()` on the returned client.  If `.with_base_url()` is
        /// present, the request reaches the mock and the assertion passes.  If
        /// `.with_base_url()` were removed, the client would default to
        /// `https://api.githubcopilot.com`, the mock would never receive a
        /// connection, and the assertion would fail.
        #[tokio::test]
        async fn enterprise_api_base_url_is_wired_to_copilot_config() {
            use tokio::io::{AsyncReadExt, AsyncWriteExt};
            use tokio::net::TcpListener;
            use std::sync::atomic::{AtomicBool, Ordering};

            // Spawn a mock models endpoint that returns an empty list.
            let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
            let port = listener.local_addr().unwrap().port();
            let reached = Arc::new(AtomicBool::new(false));
            let reached2 = Arc::clone(&reached);
            tokio::spawn(async move {
                let Ok((mut socket, _)) = listener.accept().await else { return };
                let mut buf = vec![0u8; 4096];
                let _ = socket.read(&mut buf).await;
                reached2.store(true, Ordering::SeqCst);
                let body = r#"{"models":[],"object":"list"}"#;
                let resp = format!(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                let _ = socket.write_all(resp.as_bytes()).await;
            });

            let store = Arc::new(InMemoryStore::new());
            seed(&store, &fresh_credential()).await;

            // Build an enterprise config whose api_base_url points at the mock.
            // All other enterprise fields are set correctly; only the inference
            // endpoint is redirected to localhost so no real network is needed.
            let enterprise_config = AuthCopilotConfig {
                api_base_url: format!("http://127.0.0.1:{port}"),
                ..AuthCopilotConfig::for_enterprise("octocorp.ghe.com").unwrap()
            };

            let (client, diag) = build_copilot_from_store(
                Arc::clone(&store) as Arc<dyn CredentialStore>,
                enterprise_config,
            )
            .await;
            assert!(client.is_some(), "client must build");
            assert!(diag.is_none());

            // Call list_models() — if api_base_url was wired correctly the
            // request goes to http://127.0.0.1:{port}/models (the mock).
            let _ = tokio::time::timeout(
                std::time::Duration::from_secs(3),
                client.unwrap().list_models(),
            )
            .await;

            assert!(
                reached.load(Ordering::SeqCst),
                "enterprise api_base_url must be used: mock server was never reached; \
                 if this fails, .with_base_url() was likely removed from build_copilot_from_store"
            );
        }

        /// from_env_lookup with GH_COPILOT_ENTERPRISE_DOMAIN produces an enterprise
        /// config — the same path that try_build_copilot_from_keyring takes at startup.
        #[test]
        fn from_env_lookup_enterprise_domain_produces_enterprise_api_base_url() {
            let config = AuthCopilotConfig::from_env_lookup(|key| {
                if key == "GH_COPILOT_ENTERPRISE_DOMAIN" {
                    Some("octocorp.ghe.com".to_owned())
                } else {
                    None
                }
            })
            .expect("enterprise config from env");

            assert_eq!(
                config.api_base_url, "https://copilot-api.octocorp.ghe.com",
                "enterprise domain must produce enterprise inference base URL"
            );
            assert_eq!(
                config.device_code_url, "https://octocorp.ghe.com/login/device/code",
                "device-code URL must point to the enterprise host"
            );
        }

        /// An explicit env override on top of enterprise domain is applied
        /// (proving the layered precedence: enterprise base + individual override).
        #[test]
        fn env_override_on_enterprise_base_applies_individual_endpoint() {
            let config = AuthCopilotConfig::from_env_lookup(|key| match key {
                "GH_COPILOT_ENTERPRISE_DOMAIN" => Some("octocorp.ghe.com".to_owned()),
                "GH_COPILOT_API_BASE_URL" => Some("https://proxy.internal/copilot".to_owned()),
                _ => None,
            })
            .expect("config");

            // The overridden endpoint wins.
            assert_eq!(config.api_base_url, "https://proxy.internal/copilot");
            // The non-overridden enterprise endpoints are still correct.
            assert_eq!(config.device_code_url, "https://octocorp.ghe.com/login/device/code");
        }

        /// When a stored credential needs refresh and the token exchange returns
        /// HTTP 401, build_copilot_from_store must return (None, Some(diagnostic))
        /// rather than (None, None).  Returning None silently was the pre-fix
        /// behaviour — it hid real auth failures behind "not signed in".
        ///
        /// We simulate this with a store that returns a Store-level error (the
        /// same Err branch in CredentialManager::get_credential that a failed
        /// token refresh produces). A failing store is simpler than a mock HTTP
        /// server and tests exactly the property we care about: Err → diagnostic.
        #[tokio::test]
        async fn credential_error_returns_diagnostic_not_silent_none() {
            use async_trait::async_trait;
            use coda_auth::AuthError;

            /// A store that always returns a Store error on get.
            struct FailingStore;
            #[async_trait]
            impl CredentialStore for FailingStore {
                async fn get(&self, _: &str) -> Result<Option<String>, AuthError> {
                    Err(AuthError::Store("simulated keyring failure".into()))
                }
                async fn set(&self, _: &str, _: &str) -> Result<(), AuthError> {
                    Ok(())
                }
                async fn delete(&self, _: &str) -> Result<(), AuthError> {
                    Ok(())
                }
            }

            let store: Arc<dyn CredentialStore> = Arc::new(FailingStore);
            let (client, diag) = build_copilot_from_store(store, AuthCopilotConfig::default_public()).await;
            assert!(client.is_none(), "a store error must not produce a client");
            assert!(
                diag.is_some(),
                "a credential store error must produce a diagnostic (was silently None before the fix)"
            );
            let msg = diag.unwrap();
            assert!(
                msg.contains("keyring") || msg.contains("store") || msg.contains("Store"),
                "diagnostic must mention the failure cause; got: {msg}"
            );
        }

        /// When a stored credential needs refresh and the exchange endpoint returns
        /// a 401, the error must be surfaced as a diagnostic rather than swallowed.
        /// Uses a local mock HTTP server that accepts one request and returns 401.
        #[tokio::test]
        async fn refresh_401_returns_diagnostic_not_silent_none() {
            use tokio::io::{AsyncReadExt, AsyncWriteExt};
            use tokio::net::TcpListener;

            // Spawn a TLS-less mock: normally the validator would reject http://.
            // Bypass by setting a raw URL directly on the already-built config.
            let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
            let port = listener.local_addr().unwrap().port();
            tokio::spawn(async move {
                let Ok((mut socket, _)) = listener.accept().await else { return };
                let mut buf = vec![0u8; 4096];
                let _ = socket.read(&mut buf).await;
                let resp = b"HTTP/1.1 401 Unauthorized\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
                let _ = socket.write_all(resp).await;
            });

            // Seed a credential that looks near-expiry so a refresh is triggered.
            let expired = Credential {
                provider_id: "github-copilot".into(),
                kind: CredentialKind::OAuth,
                access_token: Some(Secret::new("expired-copilot-token".into())),
                refresh_token: Some(Secret::new("fake-github-token".into())),
                api_key: None,
                expires_at: Some(chrono::Utc::now() - chrono::Duration::hours(1)),
                scopes: Vec::new(),
                account: None,
            };

            let store = Arc::new(InMemoryStore::new());
            let json = serde_json::to_string(&expired).unwrap();
            store.set("llmauth:github-copilot", &json).await.unwrap();

            // Build the config manually, bypassing the https:// validation so the
            // mock HTTP server can be reached (the validator runs at config time,
            // not at exchange time — set the URL after validation).
            let base = AuthCopilotConfig::default_public();
            let mock_exchange = format!("http://127.0.0.1:{port}/token");
            let config = AuthCopilotConfig {
                copilot_token_url: Some(mock_exchange),
                ..base
            };

            let (client, diag) = build_copilot_from_store(
                Arc::clone(&store) as Arc<dyn CredentialStore>,
                config,
            )
            .await;
            assert!(client.is_none(), "a 401 refresh must not produce a client");
            assert!(
                diag.is_some(),
                "a 401 refresh error must produce a diagnostic (was silently None before the fix)"
            );
        }

        // ── Host-factory wiring tests ─────────────────────────────────────────
        //
        // These tests prove that `try_build_copilot_from_keyring` (via
        // `resolve_copilot_config`) actually reads the saved domain from a
        // settings file without requiring GH_COPILOT_ENTERPRISE_DOMAIN to be set.
        // They use `build_copilot_from_store` with `resolve_copilot_config_from`
        // (injectable settings path) to stay hermetic.

        /// Host startup with a saved enterprise domain — no env var set.
        ///
        /// Verifies the full path: saved settings → resolve_copilot_config_from
        /// → enterprise config → build_copilot_from_store → enterprise client built.
        #[tokio::test]
        async fn host_startup_uses_saved_enterprise_domain_without_env_var() {
            // Write a settings file with the enterprise domain.
            let dir = tempfile::TempDir::new().unwrap();
            let settings_path = dir.path().join("settings.json");
            std::fs::write(
                &settings_path,
                r#"{"githubEnterpriseDomain": "octocorp.ghe.com"}"#,
            )
            .unwrap();

            let auth_config = crate::settings::resolve_copilot_config_from(
                Some(&settings_path),
                // No env var set — pure settings-file path.
                |_key| None,
            )
            .expect("enterprise config from saved domain");

            assert_eq!(
                auth_config.api_base_url, "https://copilot-api.octocorp.ghe.com",
                "saved domain must be used as inference base URL without GH_COPILOT_ENTERPRISE_DOMAIN"
            );
            assert_eq!(
                auth_config.device_code_url, "https://octocorp.ghe.com/login/device/code",
                "device-code URL must use saved domain"
            );
            assert_eq!(
                auth_config.copilot_token_url.as_deref(),
                Some("https://api.octocorp.ghe.com/copilot_internal/v2/token"),
                "token exchange URL must use saved domain"
            );

            // With a stored credential the enterprise client must be built.
            let store = Arc::new(InMemoryStore::new());
            seed(&store, &fresh_credential()).await;
            let (client, diag) = build_copilot_from_store(
                Arc::clone(&store) as Arc<dyn CredentialStore>,
                auth_config,
            )
            .await;
            assert!(client.is_some(), "enterprise client must build from saved domain");
            assert!(diag.is_none());
        }

        /// Env var wins over saved domain when both are present.
        #[test]
        fn env_var_wins_over_saved_domain_in_resolve_copilot_config_from() {
            let dir = tempfile::TempDir::new().unwrap();
            let settings_path = dir.path().join("settings.json");
            std::fs::write(
                &settings_path,
                r#"{"githubEnterpriseDomain": "saved.ghe.com"}"#,
            )
            .unwrap();

            let config = crate::settings::resolve_copilot_config_from(
                Some(&settings_path),
                |key| {
                    if key == "GH_COPILOT_ENTERPRISE_DOMAIN" {
                        Some("env-wins.ghe.com".into())
                    } else {
                        None
                    }
                },
            )
            .expect("config");

            assert_eq!(
                config.api_base_url, "https://copilot-api.env-wins.ghe.com",
                "env var must win over saved domain"
            );
        }

        /// An invalid saved domain (path component) fails rather than routing
        /// enterprise credentials to github.com.
        #[test]
        fn invalid_saved_domain_in_host_startup_fails_safely() {
            let dir = tempfile::TempDir::new().unwrap();
            let settings_path = dir.path().join("settings.json");
            std::fs::write(
                &settings_path,
                r#"{"githubEnterpriseDomain": "evil.com/path"}"#,
            )
            .unwrap();

            let result =
                crate::settings::resolve_copilot_config_from(Some(&settings_path), |_| None);
            assert!(
                result.is_err(),
                "an invalid saved domain must fail, not silently route to public"
            );
        }

        /// A BOM-prefixed settings file is parsed correctly for the saved domain.
        #[tokio::test]
        async fn bom_settings_file_enterprise_domain_is_resolved() {
            let dir = tempfile::TempDir::new().unwrap();
            let settings_path = dir.path().join("settings.json");
            let content = "\u{feff}{\"githubEnterpriseDomain\": \"bom.ghe.com\"}";
            std::fs::write(&settings_path, content).unwrap();

            let config = crate::settings::resolve_copilot_config_from(
                Some(&settings_path),
                |_| None,
            )
            .expect("BOM settings must parse correctly");

            assert_eq!(config.api_base_url, "https://copilot-api.bom.ghe.com");
        }

        /// The Copilot diagnostic is scoped to the host instance.
        ///
        /// A failed credential probe on one host must not appear in a second host
        /// built without any credential error (the OnceLock design would have
        /// contaminated the second host; the struct-field design does not).
        #[test]
        fn explicit_copilot_failure_preserves_diagnostic_without_fake_commands() {
            let message = copilot_startup_error(Some("token refresh failed (HTTP 401)")).to_string();
            assert!(message.contains("GitHub Copilot"));
            assert!(message.contains("HTTP 401"));
            assert!(!message.contains("no Copilot credential"));

            let missing = copilot_startup_error(None).to_string();
            assert!(missing.contains("No GitHub Copilot credential found"));
            assert!(!missing.contains("auth login"));
            assert!(!missing.contains("/login"));
            assert!(!missing.contains("ANTHROPIC_API_KEY"));
        }

        #[test]
        fn copilot_auth_diagnostics_never_echo_untrusted_error_details() {
            use coda_auth::AuthError;
            let secret = "SENSITIVE_ERROR_DETAIL";
            let errors = [
                AuthError::OAuth { status: 401, body: secret.into() },
                AuthError::Transport(format!("https://example.invalid/?token={secret}")),
                AuthError::Store(secret.into()),
                AuthError::CannotRefresh("github-copilot".into(), secret.into()),
                AuthError::NotFound(secret.into()),
                AuthError::UnknownProvider(secret.into()),
                AuthError::LoginCancelled(secret.into()),
                AuthError::InvalidUrl(secret.into()),
                AuthError::Io(std::io::Error::other(secret)),
            ];
            for error in errors {
                let message = sanitize_auth_error(&error);
                assert!(!message.contains(secret), "{message}");
                assert!(!message.contains("https://"), "{message}");
            }
        }

        #[test]
        fn startup_diagnostics_do_not_invent_a_provider_identity() {
            for diagnostic in [
                "Anthropic API key error: the credential could not be read",
                "Claude.ai credential error: token refresh failed",
                "credential storage unavailable",
                "invalid Anthropic endpoint from ANTHROPIC_BASE_URL",
            ] {
                let (tx, _rx) = tokio::sync::mpsc::unbounded_channel::<Vec<u8>>();
                let host = ServeHost::new_with_optional_client_and_mcp(
                    None,
                    Arc::new(crate::sink::ServeSink::new(tx.clone())),
                    Arc::new(crate::prompts::PromptChannel::new(tx)),
                    ".".into(),
                    crate::mcp::McpBundle::disabled(),
                    StartupOptions::default(),
                    Some(diagnostic.into()),
                    None,
                );
                let error = host.no_client_error();
                assert!(error.message.contains(diagnostic));
                assert!(!error.message.contains("Copilot"), "{}", error.message);
            }
        }

        #[test]
        fn copilot_diagnostic_is_scoped_to_host_not_process_global() {
            let (tx, _rx) = tokio::sync::mpsc::unbounded_channel::<Vec<u8>>();
            let sink = Arc::new(crate::sink::ServeSink::new(tx.clone()));
            let ch = Arc::new(crate::prompts::PromptChannel::new(tx));

            // Host 1: built with a Copilot credential error diagnostic.
            let host_with_error = ServeHost::new_with_optional_client_and_mcp(
                None,
                Arc::clone(&sink),
                Arc::clone(&ch),
                ".".into(),
                crate::mcp::McpBundle::disabled(),
                StartupOptions::default(),
                Some("test: token refresh failed (HTTP 401)".into()),
                None,
            );

            // Host 2: built with a successful client and no diagnostic.
            let client = super::super::tests::ScriptedClient::new(vec![]);
            let host_clean = ServeHost::new_with_client(
                client,
                Arc::clone(&sink),
                ch,
                ".".into(),
            );

            // Preserve the diagnostic without inventing an identity it did not name.
            let err1 = host_with_error.no_client_error();
            assert!(
                err1.message.contains("token refresh failed (HTTP 401)")
                    && err1.message.contains("credential"),
                "host_with_error must preserve its credential diagnostic; got: {}",
                err1.message
            );

            // Host 2 must not be contaminated by host 1's diagnostic.
            // With a client wired, session/prompt will succeed and no_client_error
            // is never called; but the field itself must be None.
            assert!(
                host_clean.startup_provider_diagnostic.is_none(),
                "a successfully-built host must not carry a stale diagnostic from another host"
            );
        }
    }
}

