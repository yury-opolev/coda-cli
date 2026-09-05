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
    pub const SET_GOAL: &str = "session/setGoal";
    pub const SET_EFFORT: &str = "session/setEffort";
    /// Switches the model for subsequent turns, without restarting.
    pub const SET_MODEL: &str = "session/setModel";
    pub const SET_PERMISSION_MODE: &str = "session/setPermissionMode";
    pub const REASONING_CAPABILITY: &str = "model/reasoningCapability";

    pub const SCHEDULE_LIST: &str = "session/scheduleList";
    pub const SCHEDULE_CREATE: &str = "session/scheduleCreate";
    pub const SCHEDULE_DELETE: &str = "session/scheduleDelete";

    pub const HOOKS_LIST: &str = "hooks/list";
    pub const HOOKS_INFO: &str = "hooks/info";
    pub const HOOKS_TRUST: &str = "hooks/trust";

    pub const SKILLS_LIST: &str = "skills/list";
    pub const PLUGINS_LIST: &str = "plugins/list";
    pub const COMPACT: &str = "session/compact";
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
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct InitializeParams {
    pub protocol_version: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub client_info: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub api_key: Option<String>,
    /// Resumes an existing session when supplied.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub session_id: Option<String>,
}

impl InitializeParams {
    pub fn new(client_info: impl Into<String>) -> Self {
        Self {
            protocol_version: PROTOCOL_VERSION.to_string(),
            client_info: Some(client_info.into()),
            api_key: None,
            session_id: None,
        }
    }

    pub fn resume(mut self, session_id: impl Into<String>) -> Self {
        self.session_id = Some(session_id.into());
        self
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InitializeResult {
    pub protocol_version: String,
    pub session_id: String,
    pub server_info: String,
    #[serde(default)]
    pub telemetry_log_path: Option<String>,
}

/// Shared `{ "ok": true }` shape used by interrupt and shutdown.
#[derive(Debug, Clone, Deserialize)]
pub struct OkResult {
    #[serde(default)]
    pub ok: bool,
}

// ---------------------------------------------------------------------------
// Prompting
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
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
pub struct WireImage {
    /// `image/png`, `image/jpeg`, `image/gif` or `image/webp`.
    pub media_type: String,
    pub base64: String,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
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
pub struct SteerParams {
    pub text: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SteerResult {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message_id: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Default)]
pub struct RecallSteeringResult {
    #[serde(default)]
    pub messages: Vec<RecalledSteeringMessage>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
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
pub struct HistoryResult {
    #[serde(default)]
    pub messages: Vec<WireMessage>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct WireMessage {
    /// `"user"` or `"assistant"`.
    pub role: String,
    #[serde(default)]
    pub content: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MessagesParams {
    pub since_index: i32,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
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
pub struct ModelsParams {
    pub refresh: bool,
}

#[derive(Debug, Clone, Deserialize, Default)]
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
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WireModel {
    pub id: String,
    #[serde(default)]
    pub display_name: Option<String>,
    #[serde(default)]
    pub context_limit: Option<i64>,
}

impl WireModel {
    pub fn label(&self) -> &str {
        self.display_name.as_deref().unwrap_or(&self.id)
    }
}

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
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
pub struct SetEffortParams {
    /// `"low"`, `"medium"`, `"high"`, `"max"`, `"auto"`, or `None` to clear.
    pub effort: Option<String>,
}

/// Switches the live permission mode for the running session.
///
/// The mode is session state, not a setting: applying it through the engine is
/// what lets `/yolo` take effect on the next tool call instead of asking the
/// user to restart.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SetPermissionModeParams {
    /// `"default"`, `"acceptEdits"`, `"plan"` or `"bypassPermissions"`.
    pub mode: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct SetPermissionModeResult {
    #[serde(default)]
    pub ok: bool,
    /// The mode actually in force after the call.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub applied: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Default)]
pub struct SetEffortResult {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub applied: Option<String>,
    #[serde(default)]
    pub note: Option<String>,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ReasoningCapabilityResult {
    #[serde(default)]
    pub supported: bool,
    #[serde(default)]
    pub levels: Vec<String>,
    #[serde(default)]
    pub supports_auto: bool,
}

// ---------------------------------------------------------------------------
// Compaction
// ---------------------------------------------------------------------------

/// Params for `session/compact`.
#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct CompactParams {
    /// Optional override for the summarisation system prompt.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub instructions: Option<String>,
}

/// Result of `session/compact`.
#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
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
pub struct ScheduleListResult {
    #[serde(default)]
    pub schedules: Vec<ScheduledTask>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
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
pub struct ScheduleDeleteParams {
    pub id: String,
}

// ---------------------------------------------------------------------------
// Skills, plugins and hooks
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Deserialize, Default)]
pub struct SkillsListResult {
    #[serde(default)]
    pub skills: Vec<WireSkill>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
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
pub struct PluginsListResult {
    #[serde(default)]
    pub plugins: Vec<WirePlugin>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
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
pub struct HooksListResult {
    #[serde(default)]
    pub hooks: Vec<WireHook>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
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

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PermissionRequest {
    #[serde(default)]
    pub tool_name: String,
    #[serde(default)]
    pub input_preview: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct PermissionResponse {
    pub allow: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QuestionRequest {
    #[serde(default)]
    pub question: String,
    #[serde(default)]
    pub options: Vec<String>,
    #[serde(default)]
    pub multi_select: bool,
    #[serde(default)]
    pub allow_free_text: bool,
}

#[derive(Debug, Clone, Serialize)]
pub struct QuestionResponse {
    pub answer: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct PlanApprovalRequest {
    #[serde(default)]
    pub plan: String,
}

#[derive(Debug, Clone, Serialize)]
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