//! Per-event timeout and fail-open defaults for user hooks.
//!
//! Matches C# `HookEventPolicy.cs`.

/// Default timeout and fail-open policy for a specific hook event.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HookEventDefaults {
    /// How long a hook subprocess may run before it is killed (seconds).
    pub timeout_seconds: u64,
    /// When `true`, a failing or timed-out hook is treated as allow.
    /// When `false`, it blocks (fail-closed).
    pub fail_open: bool,
}

/// Per-event defaults.  Unknown events fall back to `(10s, fail_open=true)`.
pub struct HookEventPolicy;

impl HookEventPolicy {
    /// Returns the effective defaults for an event name (case-insensitive).
    pub fn get(event_name: &str) -> HookEventDefaults {
        // Fail-closed events: gate integrity requires that errors are treated as blocks.
        // Fail-open events: errors must not interrupt normal agent operation.
        match event_name.to_ascii_lowercase().as_str() {
            "userpromptsubmit" => HookEventDefaults { timeout_seconds: 30, fail_open: false },
            "pretooluse" => HookEventDefaults { timeout_seconds: 10, fail_open: false },
            "permissionrequest" => HookEventDefaults { timeout_seconds: 10, fail_open: false },
            "posttooluse" => HookEventDefaults { timeout_seconds: 10, fail_open: true },
            "stop" => HookEventDefaults { timeout_seconds: 10, fail_open: true },
            "sessionstart" => HookEventDefaults { timeout_seconds: 10, fail_open: true },
            "sessionend" => HookEventDefaults { timeout_seconds: 2, fail_open: true },
            "notification" => HookEventDefaults { timeout_seconds: 10, fail_open: true },
            "agentresponse" => HookEventDefaults { timeout_seconds: 10, fail_open: true },
            // SubagentStart is fail-closed: a broken hook must not let an unshaped subagent run.
            "subagentstart" => HookEventDefaults { timeout_seconds: 10, fail_open: false },
            // SubagentStop is fail-open: must not lose the completed subagent work.
            "subagentclosure" | "subagentstop" => HookEventDefaults { timeout_seconds: 10, fail_open: true },
            // Compaction hooks: fail-open — a broken hook must not block or corrupt compaction.
            "precompact" => HookEventDefaults { timeout_seconds: 10, fail_open: true },
            "postcompact" => HookEventDefaults { timeout_seconds: 10, fail_open: true },
            // Unknown events: fail-open by default.
            _ => HookEventDefaults { timeout_seconds: 10, fail_open: true },
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pre_tool_use_is_fail_closed() {
        let p = HookEventPolicy::get("PreToolUse");
        assert!(!p.fail_open, "PreToolUse must be fail-closed");
    }

    #[test]
    fn user_prompt_submit_is_fail_closed() {
        let p = HookEventPolicy::get("UserPromptSubmit");
        assert!(!p.fail_open, "UserPromptSubmit must be fail-closed");
    }

    #[test]
    fn permission_request_is_fail_closed() {
        let p = HookEventPolicy::get("PermissionRequest");
        assert!(!p.fail_open, "PermissionRequest must be fail-closed");
    }

    #[test]
    fn post_tool_use_is_fail_open() {
        let p = HookEventPolicy::get("PostToolUse");
        assert!(p.fail_open, "PostToolUse must be fail-open");
    }

    #[test]
    fn stop_hook_is_fail_open() {
        let p = HookEventPolicy::get("Stop");
        assert!(p.fail_open, "Stop must be fail-open");
    }

    #[test]
    fn subagent_start_is_fail_closed() {
        let p = HookEventPolicy::get("SubagentStart");
        assert!(!p.fail_open, "SubagentStart must be fail-closed");
    }

    #[test]
    fn subagent_stop_is_fail_open() {
        let p = HookEventPolicy::get("SubagentStop");
        assert!(p.fail_open, "SubagentStop must be fail-open");
    }

    #[test]
    fn pre_compact_is_fail_open() {
        let p = HookEventPolicy::get("PreCompact");
        assert!(p.fail_open, "PreCompact must be fail-open");
    }

    #[test]
    fn unknown_event_is_fail_open() {
        let p = HookEventPolicy::get("UnknownEventXyz");
        assert!(p.fail_open, "Unknown events default to fail-open");
    }

    #[test]
    fn lookup_is_case_insensitive() {
        let lower = HookEventPolicy::get("pretooluse");
        let upper = HookEventPolicy::get("PRETOOLUSE");
        let mixed = HookEventPolicy::get("PreToolUse");
        assert_eq!(lower.fail_open, upper.fail_open);
        assert_eq!(lower.fail_open, mixed.fail_open);
    }
}
