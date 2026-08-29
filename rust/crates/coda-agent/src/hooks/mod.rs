//! Hooks system: user-configured lifecycle callbacks.
//!
//! Matches C# `Hooks/` — the hook event definitions, matcher, runner,
//! trust store, and the decision shapes that let a hook block a tool,
//! rewrite input/output, or update permissions.
//!
//! # Fail-open / fail-closed policy per event
//!
//! | Event | Fail policy | Rationale |
//! |---|---|---|
//! | `UserPromptSubmit` | **fail-closed** | A policy gate that permits on error is no gate |
//! | `PreToolUse` | **fail-closed** | Same: gate integrity |
//! | `PermissionRequest` | **fail-closed** | A broken permission gate must never grant |
//! | `PostToolUse` | fail-open | Tool already ran; side-effects cannot be undone |
//! | `Stop` | fail-open | A broken stop hook must not trap the agent in a loop |
//! | `SessionStart` | fail-open | Never block the user from starting a session |
//! | `SessionEnd` | fail-open | Never block shutdown |
//! | `Notification` | fail-open | Observation-only |
//! | `AgentResponse` | fail-open | A broken hook must not discard the response |
//! | `SubagentStart` | **fail-closed** | A broken hook must not let an unshaped subagent run |
//! | `SubagentStop` | fail-open | Must not lose completed subagent work |
//! | `PreCompact` | fail-open | Must not block or corrupt compaction |
//! | `PostCompact` | fail-open | Must not lose a completed compaction |

pub mod content_hash;
pub mod matcher;
pub mod output;
pub mod policy;
pub mod runner;
pub mod trust_guard;
pub mod trust_store;

pub use content_hash::HookContentHash;
pub use matcher::HookMatcher;
pub use output::{
    AgentResponseResult, HookOutput, PermissionRequestResult, PostToolUseResult,
    PreCompactResult, PostCompactResult, SubagentStartResult, SubagentStopResult,
    UserHookResult,
};
pub use policy::HookEventPolicy;
pub use runner::HookRunner;
pub use trust_guard::HookTrustGuard;
pub use trust_store::{HookTrustStore, InMemoryHookTrustStore};

// ─────────────────────────────────────────────────────────────────────────────
// UserHook (the configuration record)
// ─────────────────────────────────────────────────────────────────────────────

/// Scope of a user hook: where it was loaded from.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub enum HookScope {
    /// Loaded from `~/.coda/settings.json`. Trusted implicitly.
    User,
    /// Loaded from `<cwd>/.coda/settings.json`. Requires explicit trust.
    Project,
}

impl Default for HookScope {
    fn default() -> Self {
        // Fail-safe: if scope is ever constructed without an explicit assignment
        // (e.g. the field is skipped during deserialization), default to the
        // *untrusted* scope. Defaulting to User would silently grant implicit
        // trust to any hook whose scope was not explicitly stamped by the loader.
        HookScope::Project
    }
}

/// A single user-configured hook that fires on an agent lifecycle event.
///
/// The four handler types are `command` (default), `http`, `prompt`, and
/// `agent`.  `command` is used when `handler_type` is `None`.
#[derive(Debug, Clone, serde::Deserialize, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UserHook {
    /// Lifecycle event name: e.g. `"PreToolUse"`, `"UserPromptSubmit"`.
    pub event: String,

    /// Shell command for `command`-type hooks.
    pub command: Option<String>,

    /// Tool-name filter as a regex pattern (anchored).  `None` → match all.
    pub matcher: Option<String>,

    /// Per-hook timeout override in seconds.  `None` → use event default.
    pub timeout_seconds: Option<u64>,

    /// Per-hook fail-open override. `None` → use event default.
    pub fail_open: Option<bool>,

    /// Resolution when a hook returns `decision:"ask"` and no interactive user.
    /// `"allow"` or `"deny"` (case-insensitive); anything else → `"deny"`.
    pub unattended_decision: Option<String>,

    /// Opt-in flag: let a `UserPromptSubmit` hook return a system-prompt replacement.
    #[serde(default)]
    pub allow_system_prompt_replace: bool,

    /// Output fields this hook may mutate. Used by the TUI to decide buffering.
    pub mutates: Option<Vec<String>>,

    /// Handler type: `"command"`, `"http"`, `"prompt"`, or `"agent"`.
    pub handler_type: Option<String>,

    /// URL for `http`-type hooks.
    pub url: Option<String>,

    /// Natural-language rule for `prompt`/`agent`-type hooks.
    pub hook_prompt: Option<String>,

    /// Subagent type for `agent`-type hooks.
    pub agent_type: Option<String>,

    /// Whether this hook is enabled.
    #[serde(default = "default_true")]
    pub enabled: bool,

    /// Scope of this hook. Never read from JSON: the loader stamps it by
    /// source file after deserialization, mirroring C# SettingsLoader which
    /// force-overwrites scope based on which file it came from. Using
    /// `serde(skip)` ensures a hostile repository cannot claim user scope
    /// (implicitly trusted, no prompt) in its `.coda/settings.json`.
    #[serde(skip)]
    pub scope: HookScope,

    /// Plugin origin (name + version).  `None` for user-authored hooks.
    pub plugin_origin: Option<(String, String)>,
}

fn default_true() -> bool {
    true
}

impl UserHook {
    /// The effective handler type: inferred from present fields when not explicit.
    pub fn effective_handler_type(&self) -> &str {
        match self.handler_type.as_deref() {
            Some(h) => h,
            None if self.url.is_some() => "http",
            None if self.hook_prompt.is_some() => "prompt",
            _ => "command",
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    // The default scope must be the fail-safe (untrusted) one so that any hook
    // whose scope is not explicitly stamped by the loader cannot gain implicit trust.
    #[test]
    fn default_scope_is_project_not_user() {
        assert_eq!(
            HookScope::default(),
            HookScope::Project,
            "default scope must be Project (untrusted), not User (implicitly trusted)"
        );
    }

    // A JSON payload claiming "scope":"user" must NOT produce a trusted scope
    // because the field is serde(skip): the loader must stamp it by source file.
    #[test]
    fn json_with_explicit_user_scope_is_ignored() {
        let json = r#"{
            "event": "PreToolUse",
            "command": "evil.sh",
            "scope": "user"
        }"#;
        let hook: UserHook = serde_json::from_str(json).expect("must parse");
        assert_eq!(
            hook.scope,
            HookScope::Project,
            "scope from JSON must be ignored; hook must default to untrusted Project scope"
        );
    }

    // A JSON payload omitting scope must also not produce a trusted hook.
    #[test]
    fn json_omitting_scope_defaults_to_untrusted() {
        let json = r#"{"event":"SessionStart","command":"./setup.sh"}"#;
        let hook: UserHook = serde_json::from_str(json).expect("must parse");
        assert_eq!(
            hook.scope,
            HookScope::Project,
            "missing scope field must default to Project (untrusted), not User"
        );
    }
}
