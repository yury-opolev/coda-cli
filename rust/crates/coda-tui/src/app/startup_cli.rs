//! Pre-TUI startup CLI overrides applied via RPC after `initialize`.
//!
//! Each method mirrors the pattern of `apply_cli_effort`: call the appropriate
//! RPC, abort if rejected, and never write to disk (session-only).

use coda_proto::messages::{method, SetGoalParams, SetPermissionModeParams, SetSystemPromptParams};
use serde_json::Value;

use super::App;

/// Canonicalises a user-facing provider alias to a `coda_auth` provider id.
///
/// Kept in step with `coda_serve::host::canonical_provider`; duplicated here
/// only because `coda-tui` does not depend on `coda-serve`.
fn canonical_provider(raw: &str) -> String {
    match raw.trim().to_ascii_lowercase().as_str() {
        "anthropic" | "anthropic-api-key" | "api-key" | "apikey" => "anthropic".into(),
        "claude-ai" | "claude" | "claudeai" | "anthropic-subscription" | "subscription" => {
            "claude-ai".into()
        }
        "github-copilot" | "copilot" | "github" => "github-copilot".into(),
        other => other.to_owned(),
    }
}

impl App {
    /// Switches the active model for the session. Session-only: does not write
    /// to `settings.json`. Aborts the launch (error returned) on rejection.
    pub async fn apply_cli_model(&mut self, model: &str) -> anyhow::Result<()> {
        let result: Value = self
            .fetch(method::SET_MODEL, Some(serde_json::json!({ "model": model })))
            .await
            .map_err(|e| anyhow::anyhow!("could not apply --model: {e}"))?;
        let ok = result.get("ok").and_then(|v| v.as_bool()).unwrap_or(false);
        if !ok {
            let note = result
                .get("note")
                .and_then(|n| n.as_str())
                .unwrap_or("rejected");
            anyhow::bail!("--model {model} was not applied: {note}");
        }
        Ok(())
    }

    /// Sets the permission mode for the session. Session-only.
    ///
    /// Recognised modes: `default`, `acceptEdits`, `plan`, `bypassPermissions`
    /// (and their aliases `ask`, `edits`, `yolo`). An unrecognised mode is
    /// rejected rather than silently ignored.
    pub async fn apply_cli_permission_mode(&mut self, mode: &str) -> anyhow::Result<()> {
        let params = serde_json::to_value(SetPermissionModeParams { mode: mode.to_owned() })?;
        let result: Value = self
            .fetch(method::SET_PERMISSION_MODE, Some(params))
            .await
            .map_err(|e| anyhow::anyhow!("could not apply --permission-mode: {e}"))?;
        let ok = result.get("ok").and_then(|v| v.as_bool()).unwrap_or(false);
        if !ok {
            let applied = result
                .get("applied")
                .and_then(|n| n.as_str())
                .unwrap_or("unknown");
            anyhow::bail!(
                "--permission-mode {mode} was not applied (active: {applied}); \
                 valid modes: default, acceptEdits, plan, bypassPermissions"
            );
        }
        Ok(())
    }

    /// Sets a goal (and optional budget) for the session. Session-only.
    pub async fn apply_cli_goal(
        &mut self,
        goal: Option<&str>,
        max_duration: Option<&str>,
        max_continuations: Option<i32>,
    ) -> anyhow::Result<()> {
        let params = serde_json::to_value(SetGoalParams {
            goal: goal.map(str::to_owned),
            max_duration: max_duration.map(str::to_owned),
            max_continuations,
        })?;
        let result: Value = self
            .fetch(method::SET_GOAL, Some(params))
            .await
            .map_err(|e| anyhow::anyhow!("could not apply goal parameters: {e}"))?;
        let ok = result.get("ok").and_then(|v| v.as_bool()).unwrap_or(false);
        if !ok {
            anyhow::bail!("goal parameters were not applied");
        }
        Ok(())
    }

    /// Verifies the engine actually connected the requested provider account.
    ///
    /// Sets a custom system prompt for the session. Session-only: never written
    /// to settings. Empty text clears any existing override.
    pub async fn apply_cli_system_prompt(&mut self, text: &str) -> anyhow::Result<()> {
        let params = serde_json::to_value(SetSystemPromptParams {
            text: Some(text.to_owned()),
        })?;
        let result: Value = self
            .fetch(method::SET_SYSTEM_PROMPT, Some(params))
            .await
            .map_err(|e| anyhow::anyhow!("could not apply --system-prompt: {e}"))?;
        let ok = result.get("ok").and_then(|v| v.as_bool()).unwrap_or(false);
        if !ok {
            anyhow::bail!("--system-prompt was not applied");
        }
        Ok(())
    }

    /// Verifies the engine actually connected the requested provider account.
    ///
    /// Queries the engine's active identity and fails the launch when it does
    /// not match `expected_provider` (Finding C2). This guards against a custom
    /// engine that ignored the request and against any silent fallback, so a
    /// session never talks to a different provider than the user asked for.
    pub async fn verify_cli_provider(&mut self, expected_provider: &str) -> anyhow::Result<()> {
        let result: Value = self
            .fetch(method::REASONING_CAPABILITY, None)
            .await
            .map_err(|e| anyhow::anyhow!("could not verify --provider: {e}"))?;
        let active = result
            .get("providerId")
            .and_then(|v| v.as_str())
            .unwrap_or("");
        if !canonical_provider(active).eq_ignore_ascii_case(&canonical_provider(expected_provider)) {
            anyhow::bail!(
                "requested provider '{expected_provider}' but the engine connected \
                 '{active}'; refusing to run against a different account"
            );
        }
        Ok(())
    }
}
