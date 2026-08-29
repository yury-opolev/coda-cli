//! Hook decision output types.
//!
//! Matches C# `Hooks/UserHookResult.cs`, `PostToolUseResult.cs`,
//! `PermissionRequestResult.cs`, `AgentResponseResult.cs`,
//! `SubagentStartResult.cs`, `SubagentStopResult.cs`,
//! `PreCompactResult.cs`, `PostCompactResult.cs`.

// ─────────────────────────────────────────────────────────────────────────────
// Raw hook output (parsed from subprocess stdout)
// ─────────────────────────────────────────────────────────────────────────────

/// The raw parsed output of a single hook execution.
///
/// A hook outputs JSON to stdout; missing fields default as shown.  Non-JSON
/// or empty stdout is treated as `{}` (no decision, no mutation, continue).
#[derive(Debug, Clone, Default)]
pub struct HookOutput {
    /// `"allow"`, `"block"`, `"deny"`, or `"ask"`.  Default: `"allow"`.
    pub decision: Option<String>,
    /// Human-readable reason for the decision.
    pub reason: Option<String>,
    /// Whether execution should continue to the next hook.  Default: `true`.
    pub continue_execution: bool,
    /// Merged hook-specific output (a JSON object), or `None`.
    pub specific: Option<serde_json::Value>,
}

impl HookOutput {
    /// A no-op output (allow, continue).
    pub const fn no_op() -> Self {
        Self {
            decision: None,
            reason: None,
            continue_execution: true,
            specific: None,
        }
    }

    /// Parse the JSON output of a hook subprocess.
    ///
    /// Invalid JSON, non-object JSON, or an empty string all become the no-op
    /// output — the hook had nothing to say.
    pub fn parse(stdout: &str) -> Self {
        let trimmed = stdout.trim();
        if trimmed.is_empty() {
            return Self::no_op();
        }
        let Ok(serde_json::Value::Object(mut map)) = serde_json::from_str::<serde_json::Value>(trimmed) else {
            return Self::no_op();
        };

        let decision = map.remove("decision").and_then(|v| v.as_str().map(str::to_owned));
        let reason = map.remove("reason").and_then(|v| v.as_str().map(str::to_owned));
        let continue_execution = map
            .remove("continue")
            .and_then(|v| v.as_bool())
            .unwrap_or(true);
        let specific = map
            .remove("hookSpecificOutput")
            .filter(|v| v.is_object());

        Self { decision, reason, continue_execution, specific }
    }

    /// `true` when the decision is `"block"` or `"deny"`.
    pub fn is_blocking(&self) -> bool {
        matches!(
            self.decision.as_deref().map(str::to_lowercase).as_deref(),
            Some("block") | Some("deny")
        )
    }

    /// `true` when the decision is `"allow"` or absent.
    pub fn is_allow(&self) -> bool {
        matches!(
            self.decision.as_deref().map(str::to_lowercase).as_deref(),
            None | Some("allow")
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PreToolUse result
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone)]
pub struct UserHookResult {
    pub block: bool,
    pub reason: Option<String>,
    pub by_hook_command: Option<String>,
    /// Replacement tool input JSON (full replacement, never merged).
    pub modified_input: Option<serde_json::Value>,
}

impl UserHookResult {
    pub const ALLOW: Self = Self {
        block: false,
        reason: None,
        by_hook_command: None,
        modified_input: None,
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// PostToolUse result
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Default)]
pub struct PostToolUseResult {
    /// Replacement for the result text the model sees.
    pub modified_result: Option<String>,
    /// When set, the hook wants to block (inject a message into history).
    pub block_reason: Option<String>,
    pub by_hook_command: Option<String>,
}

impl PostToolUseResult {
    pub const NO_CHANGE: Self =
        Self { modified_result: None, block_reason: None, by_hook_command: None };
}

// ─────────────────────────────────────────────────────────────────────────────
// PermissionRequest result
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PermissionDecision {
    Allow,
    Deny,
    Ask,
}

#[derive(Debug, Clone)]
pub struct PermissionRequestResult {
    pub decision: PermissionDecision,
    pub reason: Option<String>,
    pub by_hook_command: Option<String>,
}

impl PermissionRequestResult {
    /// No hook changed the decision — fall back to the interactive prompt.
    pub const PROMPT: Self = Self {
        decision: PermissionDecision::Ask,
        reason: None,
        by_hook_command: None,
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// AgentResponse result
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Default)]
pub struct AgentResponseResult {
    /// Replacement text for the model's final response.
    pub modified_response: Option<String>,
    /// Content to display to the user (may differ from modified_response).
    pub display_content: Option<String>,
    pub by_hook_command: Option<String>,
}

impl AgentResponseResult {
    pub const NO_CHANGE: Self =
        Self { modified_response: None, display_content: None, by_hook_command: None };
}

// ─────────────────────────────────────────────────────────────────────────────
// SubagentStart result
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Default)]
pub struct SubagentStartResult {
    pub block: bool,
    pub reason: Option<String>,
    pub by_hook_command: Option<String>,
    /// Replacement prompt text.
    pub modified_prompt: Option<String>,
    /// Additional context prepended before the prompt.
    pub additional_context: Option<String>,
    /// System-prompt suffix to append.
    pub append_system_prompt: Option<String>,
}

impl SubagentStartResult {
    pub const ALLOW: Self = Self {
        block: false,
        reason: None,
        by_hook_command: None,
        modified_prompt: None,
        additional_context: None,
        append_system_prompt: None,
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// SubagentStop result
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Default)]
pub struct SubagentStopResult {
    /// Replacement for the subagent's return value.
    pub modified_result: Option<String>,
    /// When set, the hook wants to re-run the subagent with this message injected.
    pub block_reason: Option<String>,
    pub by_hook_command: Option<String>,
}

impl SubagentStopResult {
    pub const NO_CHANGE: Self =
        Self { modified_result: None, block_reason: None, by_hook_command: None };
}

// ─────────────────────────────────────────────────────────────────────────────
// PreCompact result
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Default)]
pub struct PreCompactResult {
    /// When `true`, the hook cancels compaction.
    pub cancel: bool,
    pub by_hook_command: Option<String>,
    /// Replacement summarisation instructions.
    pub instructions_override: Option<String>,
}

impl PreCompactResult {
    pub const ALLOW: Self =
        Self { cancel: false, by_hook_command: None, instructions_override: None };
}

// ─────────────────────────────────────────────────────────────────────────────
// PostCompact result
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, Default)]
pub struct PostCompactResult {
    /// Additional context to inject into history after compaction.
    pub additional_context: Option<String>,
    pub by_hook_command: Option<String>,
}

impl PostCompactResult {
    pub const NO_CHANGE: Self =
        Self { additional_context: None, by_hook_command: None };
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_empty_stdout_is_noop() {
        let o = HookOutput::parse("");
        assert!(!o.is_blocking());
        assert!(o.continue_execution);
    }

    #[test]
    fn parse_invalid_json_is_noop() {
        let o = HookOutput::parse("not json at all");
        assert!(!o.is_blocking());
        assert!(o.continue_execution);
    }

    #[test]
    fn parse_block_decision() {
        let o = HookOutput::parse(r#"{"decision":"block","reason":"denied"}"#);
        assert!(o.is_blocking());
        assert_eq!(o.reason.as_deref(), Some("denied"));
    }

    #[test]
    fn parse_deny_decision() {
        let o = HookOutput::parse(r#"{"decision":"deny"}"#);
        assert!(o.is_blocking());
    }

    #[test]
    fn parse_allow_decision() {
        let o = HookOutput::parse(r#"{"decision":"allow"}"#);
        assert!(o.is_allow());
        assert!(!o.is_blocking());
    }

    #[test]
    fn parse_continue_false() {
        let o = HookOutput::parse(r#"{"continue":false}"#);
        assert!(!o.continue_execution);
    }

    #[test]
    fn parse_hook_specific_output() {
        let o = HookOutput::parse(
            r#"{"hookSpecificOutput":{"modifiedInput":{"path":"/new"}}}"#,
        );
        assert!(o.specific.is_some());
    }
}
