//! `ServeHost` implements [`ServeBackend`] for a real (or stub) session.
//!
//! ## Stub status
//!
//! The following methods are fully stubbed and do NOT drive a real agent:
//!
//! | Method | Stub behaviour |
//! |---|---|
//! | `session/prompt` | Emits `TurnComplete` and returns `{ok:true,interrupted:false}` |
//! | `session/interrupt` | No-op; returns `{ok:true}` |
//! | `session/steer` | Appends to steering (no delivery); returns `{ok:true}` |
//! | `session/recallSteering` | Returns current in-memory steering list |
//! | `session/history` | Returns in-memory history (always empty without a real agent) |
//! | `session/messages` | Returns slice of in-memory history |
//! | `session/models` | Returns `{source:"builtin",models:[]}` |
//! | `session/setGoal` | Stores params; no real goal supervisor |
//! | `session/setEffort` | Accepts `low/medium/high/max/auto`; rejects unknowns as `ok:false` |
//! | `model/reasoningCapability` | Returns `{supported:false,levels:[],supportsAuto:false}` |
//! | `session/scheduleList` | Returns `{schedules:[]}` |
//! | `session/scheduleCreate` | Validates rule count; returns a synthetic task |
//! | `session/scheduleDelete` | Always returns not-found |
//! | `hooks/list` | Returns `{hooks:[]}` |
//! | `hooks/info` | Returns bad-index error |
//! | `hooks/trust` | Validates fields; returns success |
//! | `skills/list` | Returns `{skills:[]}` |
//! | `plugins/list` | Returns `{plugins:[]}` |

use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use coda_agent::events::AgentEvent;
use coda_agent::AgentSink;
use coda_proto::messages::PROTOCOL_VERSION;
use serde::Serialize;
use serde_json::{Value, json};
use uuid::Uuid;

use crate::dispatch::{
    HooksInfoParams, HooksTrustParams, InitParams, MessagesParams, ModelsParams,
    PromptParams, RpcError, ScheduleCreateParams, ScheduleDeleteParams, ServeBackend,
    SetEffortParams, SetGoalParams, SteerParams,
};
use crate::session::{HistoryEntry, Session};
use crate::sink::ServeSink;

// ─────────────────────────────────────────────────────────────────────────────
// Wire result structs — null-valued fields must be OMITTED, never `null`.
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
struct WireModel {
    id: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    display_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    context_limit: Option<i64>,
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
    #[serde(skip_serializing_if = "Option::is_none")]
    note: Option<String>,
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
// Steering entry
// ─────────────────────────────────────────────────────────────────────────────

struct SteeringEntry {
    id: String,
    text: String,
    enqueued_at: String,
}

// ─────────────────────────────────────────────────────────────────────────────
// Goal / effort state
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Default)]
struct GoalState {
    goal: Option<String>,
    max_duration: Option<String>,
    max_continuations: Option<i32>,
}

// ─────────────────────────────────────────────────────────────────────────────
// ServeHost
// ─────────────────────────────────────────────────────────────────────────────

/// Live backend — implements [`ServeBackend`].
///
/// Most methods are stubs; see module-level documentation for the full list.
pub struct ServeHost {
    session: Arc<Session>,
    sink: Arc<ServeSink>,
    steering: Mutex<Vec<SteeringEntry>>,
    goal: Mutex<GoalState>,
    effort: Mutex<Option<String>>,
}

impl ServeHost {
    pub fn new(sink: Arc<ServeSink>) -> Arc<Self> {
        Arc::new(Self {
            session: Session::new(Uuid::new_v4().to_string()),
            sink,
            steering: Mutex::new(Vec::new()),
            goal: Mutex::new(GoalState::default()),
            effort: Mutex::new(None),
        })
    }

    /// Create a host with a specific session id (useful for resume / tests).
    pub fn with_session_id(sink: Arc<ServeSink>, session_id: impl Into<String>) -> Arc<Self> {
        Arc::new(Self {
            session: Session::new(session_id),
            sink,
            steering: Mutex::new(Vec::new()),
            goal: Mutex::new(GoalState::default()),
            effort: Mutex::new(None),
        })
    }
}

// ─────────────────────────────────────────────────────────────────────────────

#[async_trait]
impl ServeBackend for ServeHost {
    async fn initialize(&self, _p: InitParams) -> Result<Value, RpcError> {
        let resp = InitializeResponse {
            protocol_version: PROTOCOL_VERSION.into(),
            session_id: self.session.session_id.clone(),
            server_info: "coda-serve".into(),
            telemetry_log_path: None,
        };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn shutdown(&self) -> Result<Value, RpcError> {
        Ok(json!({ "ok": true }))
    }

    /// STUB: no real agent is run.
    ///
    /// Emits `event/turnComplete` (ordering guarantee) then returns the result.
    async fn session_prompt(&self, p: PromptParams) -> Result<Value, RpcError> {
        // Store the user message in history (stub).
        if let Some(text) = &p.text {
            if !text.is_empty() {
                self.session.push(HistoryEntry { role: "user".into(), content: text.clone() });
            }
        }

        // Emit TurnComplete BEFORE returning the result — ordering guarantee.
        self.sink.emit(AgentEvent::TurnComplete {
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            root_turn_id: None,
            activity_id: None,
        });

        let resp = PromptResponse {
            ok: true,
            stop_reason: Some("end_turn".into()),
            interrupted: false,
            goal_status: None,
            error: None,
        };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_interrupt(&self) -> Result<Value, RpcError> {
        Ok(json!({ "ok": true }))
    }

    async fn session_steer(&self, p: SteerParams) -> Result<Value, RpcError> {
        let id = Uuid::new_v4().to_string();
        let now = chrono_now_rfc3339();
        self.steering.lock().expect("steering poisoned").push(SteeringEntry {
            id: id.clone(),
            text: p.text,
            enqueued_at: now,
        });
        let resp = SteerResponse { ok: true, message_id: Some(id) };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_recall_steering(&self) -> Result<Value, RpcError> {
        let messages: Vec<RecalledMessage> = self
            .steering
            .lock()
            .expect("steering poisoned")
            .iter()
            .map(|e| RecalledMessage {
                id: e.id.clone(),
                text: e.text.clone(),
                enqueued_at: Some(e.enqueued_at.clone()),
            })
            .collect();
        serde_json::to_value(&json!({ "messages": messages }))
            .map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_history(&self) -> Result<Value, RpcError> {
        let messages: Vec<WireHistoryMessage> = self
            .session
            .history()
            .into_iter()
            .map(|e| WireHistoryMessage { role: e.role, content: e.content })
            .collect();
        serde_json::to_value(&json!({ "messages": messages }))
            .map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_messages(&self, p: MessagesParams) -> Result<Value, RpcError> {
        let history = self.session.history();
        let since = p.since_index.max(0) as usize;
        let slice: Vec<WireHistoryMessage> = history
            .into_iter()
            .skip(since)
            .map(|e| WireHistoryMessage { role: e.role, content: e.content })
            .collect();
        let next_index = (since + slice.len()) as i32;
        serde_json::to_value(&json!({ "messages": slice, "nextIndex": next_index }))
            .map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_models(&self, _p: ModelsParams) -> Result<Value, RpcError> {
        let models: Vec<WireModel> = vec![];
        serde_json::to_value(&json!({ "source": "builtin", "models": models }))
            .map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_set_goal(&self, p: SetGoalParams) -> Result<Value, RpcError> {
        let mut state = self.goal.lock().expect("goal poisoned");
        state.goal = p.goal.clone();
        state.max_duration = p.max_duration.clone();
        state.max_continuations = p.max_continuations;
        let resp = SetGoalResponse {
            ok: true,
            goal: p.goal,
            max_duration: p.max_duration,
            max_continuations: p.max_continuations,
        };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn session_set_effort(&self, p: SetEffortParams) -> Result<Value, RpcError> {
        let known =
            ["low", "medium", "high", "max", "auto"].contains(&p.effort.as_deref().unwrap_or(""));
        if !known && p.effort.is_some() {
            // Unsupported value → ok:false, NOT an error.
            let resp = SetEffortResponse {
                ok: false,
                applied: None,
                note: Some(format!(
                    "unsupported effort: {}",
                    p.effort.as_deref().unwrap_or("")
                )),
            };
            return serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()));
        }
        *self.effort.lock().expect("effort poisoned") = p.effort.clone();
        let resp = SetEffortResponse { ok: true, applied: p.effort, note: None };
        serde_json::to_value(&resp).map_err(|e| RpcError::internal(e.to_string()))
    }

    async fn model_reasoning_capability(&self) -> Result<Value, RpcError> {
        Ok(json!({ "supported": false, "levels": [], "supportsAuto": false }))
    }

    async fn session_schedule_list(&self) -> Result<Value, RpcError> {
        Ok(json!({ "schedules": [] }))
    }

    async fn session_schedule_create(&self, p: ScheduleCreateParams) -> Result<Value, RpcError> {
        // Validate: exactly one scheduling rule must be set.
        let rule_count =
            [p.every.is_some(), p.at.is_some(), p.cron.is_some()].iter().filter(|&&b| b).count();
        if rule_count != 1 {
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
            kind: kind.to_string(),
            prompt: p.prompt,
            rule,
            time_zone: p.time_zone.unwrap_or_else(|| "UTC".into()),
            next_run_utc: chrono_now_rfc3339(),
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
        let project_path =
            p.project_path.ok_or_else(|| RpcError::invalid_params("missing projectPath"))?;
        let hook_hash =
            p.hook_hash.ok_or_else(|| RpcError::invalid_params("missing hookHash"))?;
        Ok(json!({ "ok": true, "projectPath": project_path, "hookHash": hook_hash }))
    }

    async fn skills_list(&self) -> Result<Value, RpcError> {
        Ok(json!({ "skills": [] }))
    }

    async fn plugins_list(&self) -> Result<Value, RpcError> {
        Ok(json!({ "plugins": [] }))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

fn chrono_now_rfc3339() -> String {
    // Use a fixed timestamp in tests; for production, use a real clock.
    // For the stub, a static value is fine.
    "2025-01-01T00:00:00Z".into()
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
        let sink = Arc::new(ServeSink::new(tx));
        ServeHost::new(sink)
    }

    // ── initialize ────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn initialize_returns_protocol_version_and_session_id() {
        let host = make_host();
        let result = host.initialize(InitParams::default()).await.unwrap();
        assert_eq!(result["protocolVersion"], PROTOCOL_VERSION);
        assert!(result["sessionId"].is_string());
        assert!(!result["sessionId"].as_str().unwrap().is_empty());
        assert_eq!(result["serverInfo"], "coda-serve");
    }

    /// `telemetryLogPath` is optional and must be OMITTED (not null) when absent.
    #[tokio::test]
    async fn initialize_omits_telemetry_log_path() {
        let host = make_host();
        let result = host.initialize(InitParams::default()).await.unwrap();
        assert!(
            result.get("telemetryLogPath").is_none(),
            "telemetryLogPath must be absent (not null) when not configured: got {result}"
        );
    }

    // ── session/prompt null-omission ──────────────────────────────────────────

    #[tokio::test]
    async fn prompt_result_omits_goal_status_when_none() {
        let host = make_host();
        let result = host.session_prompt(PromptParams::default()).await.unwrap();
        assert!(result["ok"].as_bool().unwrap());
        assert!(
            result.get("goalStatus").is_none(),
            "goalStatus must be absent when no goal is active: got {result}"
        );
    }

    #[tokio::test]
    async fn prompt_result_omits_error_when_none() {
        let host = make_host();
        let result = host.session_prompt(PromptParams::default()).await.unwrap();
        assert!(
            result.get("error").is_none(),
            "error must be absent when no error occurred: got {result}"
        );
    }

    // ── session/steer null-omission ───────────────────────────────────────────

    #[tokio::test]
    async fn steer_result_includes_message_id() {
        let host = make_host();
        let result =
            host.session_steer(SteerParams { text: "go".into() }).await.unwrap();
        assert_eq!(result["ok"], true);
        assert!(result["messageId"].is_string());
    }

    // ── session/setEffort ────────────────────────────────────────────────────

    #[tokio::test]
    async fn set_effort_valid_returns_ok_true() {
        let host = make_host();
        let result =
            host.session_set_effort(SetEffortParams { effort: Some("medium".into()) }).await.unwrap();
        assert_eq!(result["ok"], true);
        assert_eq!(result["applied"], "medium");
        assert!(result.get("note").is_none(), "note must be absent on success");
    }

    #[tokio::test]
    async fn set_effort_clear_omits_applied() {
        let host = make_host();
        let result =
            host.session_set_effort(SetEffortParams { effort: None }).await.unwrap();
        assert_eq!(result["ok"], true);
        assert!(
            result.get("applied").is_none(),
            "applied must be absent when effort is cleared: got {result}"
        );
    }

    /// Unsupported effort returns `ok:false`, NOT an error.
    #[tokio::test]
    async fn set_effort_unsupported_returns_ok_false_not_error() {
        let host = make_host();
        let result =
            host.session_set_effort(SetEffortParams { effort: Some("warp-speed".into()) })
                .await
                .unwrap(); // must NOT be an Err
        assert_eq!(
            result["ok"], false,
            "unsupported effort must return ok:false, not an error"
        );
    }

    // ── session/setGoal null-omission ─────────────────────────────────────────

    #[tokio::test]
    async fn set_goal_omits_optional_fields_when_absent() {
        let host = make_host();
        let result = host
            .session_set_goal(SetGoalParams { goal: None, max_duration: None, max_continuations: None })
            .await
            .unwrap();
        assert_eq!(result["ok"], true);
        assert!(result.get("goal").is_none(), "goal must be absent when None: got {result}");
        assert!(
            result.get("maxDuration").is_none(),
            "maxDuration must be absent when None: got {result}"
        );
        assert!(
            result.get("maxContinuations").is_none(),
            "maxContinuations must be absent when None: got {result}"
        );
    }

    // ── session/scheduleCreate ────────────────────────────────────────────────

    #[tokio::test]
    async fn schedule_create_with_every_succeeds() {
        let host = make_host();
        let result = host
            .session_schedule_create(ScheduleCreateParams {
                prompt: "do it".into(),
                name: None,
                every: Some("1h".into()),
                at: None,
                cron: None,
                time_zone: None,
            })
            .await
            .unwrap();
        assert_eq!(result["kind"], "interval");
        assert_eq!(result["state"], "idle");
        assert!(result["id"].is_string());
        // Optional fields omitted when None.
        assert!(result.get("name").is_none(), "name must be absent when None");
        assert!(result.get("activeTaskId").is_none(), "activeTaskId must be absent when None");
        assert!(result.get("lastOutcome").is_none(), "lastOutcome must be absent when None");
    }

    #[tokio::test]
    async fn schedule_create_requires_one_rule() {
        let host = make_host();
        let err = host
            .session_schedule_create(ScheduleCreateParams {
                prompt: "do it".into(),
                name: None,
                every: None,
                at: None,
                cron: None,
                time_zone: None,
            })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32602);
    }

    #[tokio::test]
    async fn schedule_create_two_rules_is_invalid() {
        let host = make_host();
        let err = host
            .session_schedule_create(ScheduleCreateParams {
                prompt: "do it".into(),
                name: None,
                every: Some("1h".into()),
                at: None,
                cron: Some("0 * * * *".into()),
                time_zone: None,
            })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32602);
    }

    // ── session/scheduleDelete ────────────────────────────────────────────────

    #[tokio::test]
    async fn schedule_delete_with_missing_id_returns_32602() {
        let host = make_host();
        let err = host
            .session_schedule_delete(ScheduleDeleteParams { id: None })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32602);
    }

    // ── hooks/trust ───────────────────────────────────────────────────────────

    #[tokio::test]
    async fn hooks_trust_ok_with_both_fields() {
        let host = make_host();
        let result = host
            .hooks_trust(HooksTrustParams {
                project_path: Some("/repo".into()),
                hook_hash: Some("abc123".into()),
            })
            .await
            .unwrap();
        assert_eq!(result["ok"], true);
        assert_eq!(result["projectPath"], "/repo");
        assert_eq!(result["hookHash"], "abc123");
    }

    #[tokio::test]
    async fn hooks_trust_missing_project_path_returns_32602() {
        let host = make_host();
        let err = host
            .hooks_trust(HooksTrustParams { project_path: None, hook_hash: Some("x".into()) })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32602);
    }

    #[tokio::test]
    async fn hooks_trust_missing_hook_hash_returns_32602() {
        let host = make_host();
        let err = host
            .hooks_trust(HooksTrustParams {
                project_path: Some("/p".into()),
                hook_hash: None,
            })
            .await
            .unwrap_err();
        assert_eq!(err.code, -32602);
    }

    // ── WireModel null-omission ───────────────────────────────────────────────

    #[test]
    fn wire_model_omits_optional_fields_when_none() {
        let m = WireModel {
            id: "model-1".into(),
            display_name: None,
            context_limit: None,
        };
        let v = serde_json::to_value(&m).unwrap();
        assert_eq!(v["id"], "model-1");
        assert!(v.get("displayName").is_none(), "displayName must be absent when None");
        assert!(v.get("contextLimit").is_none(), "contextLimit must be absent when None");
    }

    #[test]
    fn wire_model_includes_optional_fields_when_some() {
        let m = WireModel {
            id: "m2".into(),
            display_name: Some("Model 2".into()),
            context_limit: Some(200000),
        };
        let v = serde_json::to_value(&m).unwrap();
        assert_eq!(v["displayName"], "Model 2");
        assert_eq!(v["contextLimit"], 200000);
    }
}
