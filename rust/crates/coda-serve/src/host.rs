//! ServeHost implements [ServeBackend] with a real agent loop.
//! See module doc for live vs stubbed breakdown.

use std::sync::{Arc, Mutex};
use std::time::Duration;

use async_trait::async_trait;
use coda_agent::{
    AgentError, AgentLoopBuilder, CompactionService, GoalBudget, GoalOutcome, GoalStatus,
    GoalSupervisor, TodoStore, TokenEstimator, ToolRegistry,
};
use coda_agent::agent::stop::UserQuestionPrompt;
use coda_agent::events::{AgentEvent, AgentSink};
use coda_agent::goal::ForkedAgent;
use coda_agent::permission::{ModePermissionPrompt, PermissionMode, PermissionPrompt};
use coda_agent::tool::{PlanApprover, UserQuestion};
use coda_agent::tools::built_in_tools;
use coda_auth::{
    CredentialManager, CredentialManagerSource,
    provider::{AuthProvider, CopilotProvider},
};
use coda_auth::provider::copilot::CopilotConfig as AuthCopilotConfig;
use coda_auth::store::{CredentialStore, DpapiStore, KeyringStore, EncryptedFileStore};
use coda_llm::anthropic::{AnthropicClient, AnthropicConfig};
use coda_llm::{
    ChatRequest, Content, CopilotClient, CopilotConfig,
    CredentialSource, Effort, LlmClient, Message, Role,
};
use coda_proto::messages::PROTOCOL_VERSION;
use serde::Serialize;
use serde_json::{Value, json};
use tokio_util::sync::CancellationToken;
use uuid::Uuid;

use crate::dispatch::{
    CompactParams, HooksInfoParams, HooksTrustParams, InitParams, MessagesParams, ModelsParams,
    PromptParams, RpcError, ScheduleCreateParams, ScheduleDeleteParams, ServeBackend,
    SetEffortParams, SetGoalParams, SteerParams,
};
use crate::prompts::{PromptChannel, WirePermissionPrompt, WirePlanApprover, WireUserQuestion};
use crate::session::{Session, SteeringLogEntry};
use crate::sink::ServeSink;

// ─────────────────────────────────────────────────────────────────────────────
// Wire result structs — null-valued fields are OMITTED, never `null`
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct InitializeResponse {
    protocol_version: String,
    session_id: String,
    server_info: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    telemetry_log_path: Option<String>,
}

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
    /// Always present. The C# host emits `note: ""` on success rather than
    /// omitting it — only *null* properties are dropped, and an empty string
    /// is not null. A client that distinguishes "absent" from "empty" would
    /// see two different engines here, so this field is not optional.
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
    inner: Arc<ServeSink>,
    stop_reason: Mutex<Option<String>>,
}

impl TurnSink {
    fn new(inner: Arc<ServeSink>) -> Arc<Self> {
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
// ServeHost
// ─────────────────────────────────────────────────────────────────────────────

pub struct ServeHost {
    session: Arc<Session>,
    sink: Arc<ServeSink>,
    /// Wired by `initialize`; `None` until then.
    client: tokio::sync::Mutex<Option<Arc<dyn LlmClient>>>,
    tools: Arc<ToolRegistry>,
    permission_prompt: Arc<dyn PermissionPrompt>,
    user_question: Arc<WireUserQuestion>,
    plan_approver: Arc<WirePlanApprover>,
    todos: Arc<TodoStore>,
    working_dir: String,
    model: Mutex<String>,
    effort: Mutex<Option<Effort>>,
    goal_params: Mutex<GoalParams>,
    current_cancel: Mutex<Option<CancellationToken>>,
    /// `true` while a `session/prompt` or `session/compact` is running.
    turn_active: Mutex<bool>,
}

impl ServeHost {
    /// Production constructor — client starts as `None` until `initialize`.
    pub fn new(
        sink: Arc<ServeSink>,
        prompt_channel: Arc<PromptChannel>,
        working_dir: String,
    ) -> Arc<Self> {
        Self::build(None, sink, prompt_channel, working_dir)
    }

    /// Test constructor — pre-built client is injected directly.
    pub fn new_with_client(
        client: Arc<dyn LlmClient>,
        sink: Arc<ServeSink>,
        prompt_channel: Arc<PromptChannel>,
        working_dir: String,
    ) -> Arc<Self> {
        Self::build(Some(client), sink, prompt_channel, working_dir)
    }

    fn build(
        client: Option<Arc<dyn LlmClient>>,
        sink: Arc<ServeSink>,
        prompt_channel: Arc<PromptChannel>,
        working_dir: String,
    ) -> Arc<Self> {
        let wire_perm = Arc::new(WirePermissionPrompt { channel: Arc::clone(&prompt_channel) });
        let permission_prompt: Arc<dyn PermissionPrompt> =
            Arc::new(ModePermissionPrompt::new(PermissionMode::Default, Some(wire_perm)));
        let user_question = Arc::new(WireUserQuestion { channel: Arc::clone(&prompt_channel) });
        let plan_approver = Arc::new(WirePlanApprover { channel: Arc::clone(&prompt_channel) });
        let tools = Arc::new(ToolRegistry::new(built_in_tools()));
        Arc::new(Self {
            session: Session::new(Uuid::new_v4().to_string()),
            sink,
            client: tokio::sync::Mutex::new(client),
            tools,
            permission_prompt,
            user_question,
            plan_approver,
            todos: Arc::new(TodoStore::new()),
            working_dir,
            model: Mutex::new("claude-opus-4-5".into()),
            effort: Mutex::new(None),
            goal_params: Mutex::new(GoalParams::default()),
            current_cancel: Mutex::new(None),
            turn_active: Mutex::new(false),
        })
    }

    fn current_model(&self) -> String {
        self.model.lock().expect("model poisoned").clone()
    }

    fn current_effort(&self) -> Option<Effort> {
        *self.effort.lock().expect("effort poisoned")
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

    /// Attempt to atomically claim the turn slot.
    ///
    /// Returns a guard on success, or `None` if another prompt or compact is
    /// already running.
    ///
    /// The guard releases on drop rather than at an explicit call site. That
    /// matters because a serve task can be *cancelled* — if the client
    /// disconnects mid-turn the future is dropped, and a release that only
    /// runs on the `Ok`/`Err` paths would never execute. The slot would stay
    /// claimed for the life of the process and every later prompt would be
    /// refused as busy, with no way to recover short of a restart.
    fn try_claim_turn(&self) -> Option<TurnGuard<'_>> {
        let mut busy = self.turn_active.lock().expect("turn_active poisoned");
        if *busy {
            None
        } else {
            *busy = true;
            drop(busy);
            Some(TurnGuard { flag: &self.turn_active })
        }
    }
}

/// Releases the turn slot when dropped, including on cancellation or panic.
struct TurnGuard<'a> {
    flag: &'a std::sync::Mutex<bool>,
}

impl Drop for TurnGuard<'_> {
    fn drop(&mut self) {
        // A poisoned lock still has to release the slot, otherwise one panic
        // would wedge the session permanently.
        match self.flag.lock() {
            Ok(mut busy) => *busy = false,
            Err(poisoned) => *poisoned.into_inner() = false,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ServeBackend
// ─────────────────────────────────────────────────────────────────────────────

#[async_trait]
impl ServeBackend for ServeHost {
    async fn initialize(&self, p: InitParams) -> Result<Value, RpcError> {
        // Resume not yet implemented — return -32002 per spec.
        if p.session_id.is_some() {
            return Err(RpcError::session_not_found());
        }
        // Wire an explicitly provided API key; otherwise leave client as-is
        // (lazy credential lookup happens on first session/prompt).
        if let Some(ref key) = p.api_key {
            if let Some(c) = build_anthropic(key) {
                *self.client.lock().await = Some(c);
            }
        }
        let resp = InitializeResponse {
            protocol_version: PROTOCOL_VERSION.into(),
            session_id: self.session.session_id.clone(),
            // Must match the C# engine verbatim: clients key off this string,
            // so reporting the crate name here would be a silent parity break.
            server_info: "coda".into(),
            telemetry_log_path: None,
        };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn shutdown(&self) -> Result<Value, RpcError> {
        if let Some(c) = self.current_cancel.lock().expect("cancel poisoned").take() {
            c.cancel();
        }
        Ok(json!({ "ok": true }))
    }

    async fn session_prompt(&self, p: PromptParams) -> Result<Value, RpcError> {
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
        // including cancellation.
        let Some(_turn) = self.try_claim_turn() else {
            return Err(RpcError::internal(
                "another prompt is already in progress; busy",
            ));
        };

        self.run_prompt_inner(p).await
    }

    async fn session_interrupt(&self) -> Result<Value, RpcError> {
        if let Some(c) = self.current_cancel.lock().expect("cancel poisoned").take() {
            c.cancel();
        }
        Ok(json!({ "ok": true }))
    }

    async fn session_steer(&self, p: SteerParams) -> Result<Value, RpcError> {
        // Steer is only valid for a turn in flight. Check turn_active first;
        // the inbox may be open even when no turn is running (it starts open).
        let is_busy = *self.turn_active.lock().expect("turn_active poisoned");
        if !is_busy {
            return serde_json::to_value(&SteerResponse { ok: false, message_id: None })
                .map_err(|e| RpcError::internal(e.to_string()));
        }
        // Attempt to enqueue; the inbox may have been sealed racing the turn end.
        let Some(entry) = self.session.steering.enqueue(&p.text) else {
            return serde_json::to_value(&SteerResponse { ok: false, message_id: None })
                .map_err(|e| RpcError::internal(e.to_string()));
        };
        let id = entry.id.clone();
        let now = now_rfc3339();
        self.session
            .steering_log
            .lock()
            .expect("steering log poisoned")
            .push(SteeringLogEntry { id: id.clone(), text: p.text, enqueued_at: now });
        let resp = SteerResponse { ok: true, message_id: Some(id) };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_recall_steering(&self) -> Result<Value, RpcError> {
        let messages: Vec<RecalledMessage> = self
            .session
            .steering_log
            .lock()
            .expect("log poisoned")
            .iter()
            .map(|e| RecalledMessage {
                id: e.id.clone(),
                text: e.text.clone(),
                enqueued_at: Some(e.enqueued_at.clone()),
            })
            .collect();
        Ok(json!({ "messages": messages }))
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
        let client = self.client.lock().await.clone();
        let Some(client) = client else {
            return Ok(json!({ "source": "builtin", "models": [] }));
        };
        let result =
            if p.refresh { client.refresh_models().await } else { client.list_models().await };
        match result {
            Ok(models) if !models.is_empty() => {
                let wire: Vec<WireModel> = models
                    .into_iter()
                    .map(|m| WireModel {
                        id: m.id,
                        display_name: m.display_name,
                        context_limit: m.context_limit.map(|n| n as i64),
                    })
                    .collect();
                let v = serde_json::to_value(&wire)
                    .map_err(|e| RpcError::internal(e.to_string()))?;
                Ok(json!({ "source": "live", "models": v }))
            }
            _ => Ok(json!({ "source": "builtin", "models": [] })),
        }
    }

    async fn session_set_goal(&self, p: SetGoalParams) -> Result<Value, RpcError> {
        // Validate maxDuration before storing — return -32602 for unrecognised formats.
        if let Some(ref dur) = p.max_duration {
            if parse_duration(Some(dur.as_str())).is_none() {
                return Err(RpcError::invalid_params(format!(
                    "invalid maxDuration: {dur:?} — expected e.g. \"30m\", \"2h\", \"90s\""
                )));
            }
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

    async fn session_set_effort(&self, p: SetEffortParams) -> Result<Value, RpcError> {
        match p.effort.as_deref() {
            None => {
                *self.effort.lock().expect("effort poisoned") = None;
                serde_json::to_value(&SetEffortResponse {
                    ok: true,
                    applied: None,
                    note: String::new(),
                })
                .map_err(|e| RpcError::internal(e.to_string()))
            }
            Some(raw) => match Effort::parse(raw) {
                Some(e) => {
                    *self.effort.lock().expect("effort poisoned") = Some(e);
                    let resp = SetEffortResponse {
                        ok: true,
                        applied: Some(e.as_str().into()),
                        note: String::new(),
                    };
                    serde_json::to_value(&resp).map_err(|err| RpcError::internal(err.to_string()))
                }
                None => {
                    // Unsupported value → ok:false, NOT an error.
                    let resp = SetEffortResponse {
                        ok: false,
                        applied: None,
                        note: format!("unsupported effort: {raw}"),
                    };
                    serde_json::to_value(&resp).map_err(|err| RpcError::internal(err.to_string()))
                }
            },
        }
    }

    // ── Stubs ─────────────────────────────────────────────────────────────────

    async fn model_reasoning_capability(&self) -> Result<Value, RpcError> {
        Ok(json!({ "supported": false, "levels": [], "supportsAuto": false }))
    }

    async fn session_schedule_list(&self) -> Result<Value, RpcError> {
        Ok(json!({ "schedules": [] }))
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
        let (kind, rule) = if let Some(ref e) = p.every {
            ("interval", e.clone())
        } else if let Some(ref a) = p.at {
            ("at", a.clone())
        } else {
            ("cron", p.cron.clone().unwrap())
        };
        let resp = ScheduledTaskResponse {
            id: Uuid::new_v4().to_string(),
            name: p.name,
            kind: kind.into(),
            prompt: p.prompt,
            rule,
            time_zone: p.time_zone.unwrap_or_else(|| "UTC".into()),
            next_run_utc: now_rfc3339(),
            state: "idle".into(),
            active_task_id: None,
            last_outcome: None,
        };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_schedule_delete(&self, p: ScheduleDeleteParams) -> Result<Value, RpcError> {
        let id = p.id.ok_or_else(|| RpcError::invalid_params("missing id"))?;
        Err(RpcError::invalid_params(format!("schedule not found: {id}")))
    }

    async fn hooks_list(&self) -> Result<Value, RpcError> {
        Ok(json!({ "hooks": [] }))
    }

    async fn hooks_info(&self, _p: HooksInfoParams) -> Result<Value, RpcError> {
        Err(RpcError::invalid_params("hook index out of range"))
    }

    async fn hooks_trust(&self, p: HooksTrustParams) -> Result<Value, RpcError> {
        let pp = p.project_path.ok_or_else(|| RpcError::invalid_params("missing projectPath"))?;
        let hh = p.hook_hash.ok_or_else(|| RpcError::invalid_params("missing hookHash"))?;
        Ok(json!({ "ok": true, "projectPath": pp, "hookHash": hh }))
    }

    async fn skills_list(&self) -> Result<Value, RpcError> {
        Ok(json!({ "skills": [] }))
    }

    async fn plugins_list(&self) -> Result<Value, RpcError> {
        Ok(json!({ "plugins": [] }))
    }

    async fn session_compact(&self, p: CompactParams) -> Result<Value, RpcError> {
        // Require credentials (same guard as session/prompt).
        {
            let mut guard = self.client.lock().await;
            if guard.is_none() {
                *guard = try_build_from_env();
            }
        }
        let client = {
            let g = self.client.lock().await;
            g.clone().ok_or_else(|| {
                RpcError::unauthorized(
                    "no credentials; set ANTHROPIC_API_KEY or provide apiKey in initialize",
                )
            })?
        };

        // Claim the turn slot so a concurrent prompt is blocked; the guard
        // releases it on every exit path, including cancellation.
        let Some(_turn) = self.try_claim_turn() else {
            return Err(RpcError::internal(
                "another prompt is already in progress; busy",
            ));
        };

        self.run_compact_inner(client, p).await
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ServeHost private helpers
// ─────────────────────────────────────────────────────────────────────────────

impl ServeHost {
    /// Inner implementation of the prompt turn — called after the turn slot is claimed
    /// and image validation has passed.
    async fn run_prompt_inner(&self, p: PromptParams) -> Result<Value, RpcError> {
        // Lazy credential lookup on first use — env var only (fast path).
        // Keyring is checked once at process startup in serve_stdio().
        {
            let mut guard = self.client.lock().await;
            if guard.is_none() {
                *guard = try_build_from_env();
            }
        }

        // Require a wired client.
        let client = {
            let g = self.client.lock().await;
            g.clone().ok_or_else(|| {
                RpcError::unauthorized(
                    "no credentials; set ANTHROPIC_API_KEY or provide apiKey in initialize",
                )
            })?
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

        // Optional goal supervisor.
        let goal = self.build_goal_supervisor(Arc::clone(&client));

        // Build the agent loop.
        let uq_goal = Arc::clone(&self.user_question) as Arc<dyn UserQuestionPrompt>;
        let uq_tool = Arc::clone(&self.user_question) as Arc<dyn UserQuestion>;
        let pa = Arc::clone(&self.plan_approver) as Arc<dyn PlanApprover>;

        let agent = AgentLoopBuilder::new(
            Arc::clone(&client),
            Arc::clone(&self.permission_prompt),
            Arc::clone(&self.tools),
        )
        .with_model(self.current_model())
        .with_working_directory(&self.working_dir)
        .with_effort(self.current_effort())
        .with_steering(Arc::clone(&self.session.steering))
        .with_user_question(uq_goal)
        .with_tool_user_question(uq_tool)
        .with_plan_approver(pa)
        .with_todos(Arc::clone(&self.todos))
        .build();

        // Run through TurnSink to capture stop_reason.
        let turn_sink = TurnSink::new(Arc::clone(&self.sink));
        let run_result = agent.run(&mut history, turn_sink.as_ref(), goal, cancel).await;
        let stop_reason = turn_sink.take_stop_reason();

        // Clear cancel token.
        *self.current_cancel.lock().expect("cancel poisoned") = None;

        // Persist updated history.
        *self.session.history.lock().expect("history poisoned") = history;

        // Map result to wire fields.
        let (ok, interrupted, goal_status, error) = match &run_result {
            Ok(gs) => (true, false, wire_goal_status(gs), None),
            Err(AgentError::Cancelled) => (true, true, None, None),
            Err(e) => (false, false, None, Some(e.to_string())),
        };

        // Emit TurnComplete BEFORE the response is sent (ordering guarantee).
        self.sink.emit(AgentEvent::TurnComplete {
            stop_reason: stop_reason.clone(),
            interrupted,
            root_turn_id: None,
            activity_id: None,
        });

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
                let messages_after = compacted.len() as i64;
                let tokens_after = TokenEstimator::estimate(&compacted) as i64;
                *self.session.history.lock().expect("history poisoned") = compacted;
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

/// Try to build an LLM client. Preference: explicit key → env → Copilot keyring.
pub(crate) async fn try_build_client(api_key: Option<&str>) -> Option<Arc<dyn LlmClient>> {
    if let Some(key) = api_key {
        if !key.trim().is_empty() {
            return build_anthropic(key);
        }
    }
    if let Some(c) = try_build_from_env() {
        return Some(c);
    }
    try_build_copilot_from_keyring().await
}

/// Fast env-variable-only lookup (no I/O, no keyring).
pub(crate) fn try_build_from_env() -> Option<Arc<dyn LlmClient>> {
    if let Ok(key) = std::env::var("ANTHROPIC_API_KEY") {
        if !key.trim().is_empty() {
            return build_anthropic(&key);
        }
    }
    None
}

pub(crate) fn build_anthropic(key: &str) -> Option<Arc<dyn LlmClient>> {
    AnthropicClient::new(AnthropicConfig::api_key(key))
        .ok()
        .map(|c| Arc::new(c) as Arc<dyn LlmClient>)
}

/// Chooses a credential backend, preferring the one the C# build already uses.
///
/// On Windows the C# store is DPAPI files under `~/.coda/credentials`, keyed
/// `llmauth:<provider>` — the same key this crate uses. Reading it first means
/// an existing installation keeps working when the engine is swapped for the
/// Rust one; probing the OS keyring first would find nothing and silently log
/// every current user out.
///
/// The keyring and encrypted-file stores remain as fallbacks for credentials
/// written by the Rust build itself, and for non-Windows hosts.
fn credential_store() -> Arc<dyn CredentialStore> {
    #[cfg(windows)]
    {
        let dpapi = DpapiStore::default_location();
        if dpapi.contains("llmauth:github-copilot") {
            return Arc::new(dpapi);
        }
    }
    match KeyringStore::probe() {
        Ok(()) => Arc::new(KeyringStore::new()),
        Err(_) => Arc::new(EncryptedFileStore::default()),
    }
}

async fn try_build_copilot_from_keyring() -> Option<Arc<dyn LlmClient>> {
    let store = credential_store();
    let manager = Arc::new(CredentialManager::new(
        store,
        [Arc::new(CopilotProvider::new(AuthCopilotConfig::default_public()))
            as Arc<dyn AuthProvider>],
    ));
    match manager.get_credential("github-copilot").await {
        Ok(Some(_)) => {}
        // Absent credentials are the normal "not logged in" case, and a failed
        // lookup is usually an expired refresh — neither is worth writing to
        // stderr on every startup, since the caller falls back cleanly.
        Ok(None) | Err(_) => return None,
    }
    let source: Arc<dyn CredentialSource> =
        Arc::new(CredentialManagerSource::new(Arc::clone(&manager), "github-copilot"));

    // The credential source supplies only Authorization; editor identity comes
    // from the config by design. Without these the API rejects every request
    // with "missing Editor-Version header for IDE auth", so the engine appears
    // to have no models at all.
    let auth_config = AuthCopilotConfig::default_public();
    let config = CopilotConfig::with_token("")
        .with_credential_source(source)
        .with_header("editor-version", auth_config.editor_version.clone())
        .with_header("editor-plugin-version", auth_config.editor_plugin_version.clone())
        .with_header("copilot-integration-id", auth_config.integration_id.clone())
        .with_header("user-agent", auth_config.user_agent.clone())
        .with_header("x-github-api-version", "2026-06-01");

    CopilotClient::new(config).ok().map(|c| Arc::new(c) as Arc<dyn LlmClient>)
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

fn now_rfc3339() -> String {
    chrono::Utc::now().to_rfc3339()
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
            let _turn = host.try_claim_turn().expect("first claim succeeds");
            assert!(
                host.try_claim_turn().is_none(),
                "the slot must be held while a turn is in flight"
            );
        } // guard dropped here, as it would be on cancellation

        assert!(
            host.try_claim_turn().is_some(),
            "dropping the turn must release the slot, or the session wedges forever"
        );
    }

    /// Two concurrent prompts must not both run: the loser is refused rather
    /// than interleaving writes into the same history.
    #[tokio::test]
    async fn a_second_concurrent_claim_is_refused() {
        let host = make_host();
        let first = host.try_claim_turn().expect("first claim succeeds");
        assert!(host.try_claim_turn().is_none(), "second concurrent claim must be refused");
        drop(first);
        assert!(host.try_claim_turn().is_some(), "the slot is reusable once released");
    }

    /// Diagnostic: does the engine find a usable provider on this machine?
    ///
    /// Touches the credential store and may perform a token exchange, so it is
    /// ignored by default. Run explicitly with:
    /// `cargo test -p coda-serve credential_diagnostic -- --ignored --nocapture`
    #[ignore]
    #[tokio::test]
    async fn credential_diagnostic() {
        let store = credential_store();
        match store.get("llmauth:github-copilot").await {
            Ok(Some(v)) => eprintln!("raw credential read: {} bytes", v.len()),
            Ok(None) => eprintln!("raw credential: NOT FOUND"),
            Err(e) => eprintln!("raw credential read failed: {e}"),
        }

        let manager = Arc::new(CredentialManager::new(
            credential_store(),
            [Arc::new(CopilotProvider::new(AuthCopilotConfig::default_public()))
                as Arc<dyn AuthProvider>],
        ));
        match manager.get_credential("github-copilot").await {
            Ok(Some(_)) => eprintln!("manager: credential OK"),
            Ok(None) => eprintln!("manager: NONE"),
            Err(e) => eprintln!("manager: ERROR {e}"),
        }

        match try_build_client(None).await {
            Some(client) => match client.list_models().await {
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
    async fn set_effort_valid_returns_ok_true() {
        let host = make_host();
        let r = host
            .session_set_effort(SetEffortParams { effort: Some("high".into()) })
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
            host.session_set_effort(SetEffortParams { effort: None }).await.unwrap();
        assert_eq!(r["ok"], true);
        assert!(r.get("applied").is_none());
    }

    #[tokio::test]
    async fn set_effort_unsupported_is_ok_false_not_error() {
        let host = make_host();
        let r = host
            .session_set_effort(SetEffortParams { effort: Some("ludicrous".into()) })
            .await
            .unwrap();
        assert_eq!(r["ok"], false, "unsupported effort must yield ok:false, not an Err");
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
        let m = WireModel { id: "x".into(), display_name: None, context_limit: None };
        let v = serde_json::to_value(&m).unwrap();
        assert!(v.get("displayName").is_none());
        assert!(v.get("contextLimit").is_none());
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
            err.message.to_lowercase().contains("maxduration"),
            "error message must mention maxDuration: {}", err.message
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
}
