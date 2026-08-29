//! Pure method routing: `(method, params) -> Result<Value, RpcError>`.
//!
//! This module is I/O free and has no dependency on the agent, a process, or
//! any async runtime beyond what `#[async_trait]` needs.  All tests use a
//! fake backend; no real session, transport, or LLM is involved.

use async_trait::async_trait;
use serde::Deserialize;
use serde_json::Value;

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
}

/// `session/scheduleCreate` — `prompt` is required on the wire.
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
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ScheduleDeleteParams {
    #[serde(default)]
    pub id: Option<String>,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct HooksInfoParams {
    #[serde(default)]
    pub index: i32,
}

#[derive(Debug, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct HooksTrustParams {
    #[serde(default)]
    pub project_path: Option<String>,
    #[serde(default)]
    pub hook_hash: Option<String>,
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
    async fn session_set_goal(&self, p: SetGoalParams) -> Result<Value, RpcError>;
    async fn session_set_effort(&self, p: SetEffortParams) -> Result<Value, RpcError>;
    async fn model_reasoning_capability(&self) -> Result<Value, RpcError>;
    async fn session_schedule_list(&self) -> Result<Value, RpcError>;
    async fn session_schedule_create(&self, p: ScheduleCreateParams) -> Result<Value, RpcError>;
    async fn session_schedule_delete(&self, p: ScheduleDeleteParams) -> Result<Value, RpcError>;
    async fn hooks_list(&self) -> Result<Value, RpcError>;
    async fn hooks_info(&self, p: HooksInfoParams) -> Result<Value, RpcError>;
    async fn hooks_trust(&self, p: HooksTrustParams) -> Result<Value, RpcError>;
    async fn skills_list(&self) -> Result<Value, RpcError>;
    async fn plugins_list(&self) -> Result<Value, RpcError>;
}

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
fn optional<T: for<'de> serde::Deserialize<'de> + Default>(params: Option<Value>) -> T {
    params.and_then(|v| serde_json::from_value(v).ok()).unwrap_or_default()
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
        "session/setGoal" => backend.session_set_goal(optional(params)).await,
        "session/setEffort" => backend.session_set_effort(optional(params)).await,
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
        async fn session_set_goal(&self, _p: SetGoalParams) -> Result<Value, RpcError> {
            Ok(json!({ "ok": true }))
        }
        async fn session_set_effort(&self, p: SetEffortParams) -> Result<Value, RpcError> {
            match p.effort.as_deref() {
                Some("bad") => Ok(json!({ "ok": false })),
                _ => Ok(json!({ "ok": true })),
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
        async fn session_set_goal(&self, _p: SetGoalParams) -> Result<Value, RpcError> {
            Err(RpcError { code: self.0, message: self.1.into() })
        }
        async fn session_set_effort(&self, _p: SetEffortParams) -> Result<Value, RpcError> {
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
}
