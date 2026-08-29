//! SHA-256 content hash of a hook's behaviorally-significant fields.
//!
//! Matches C# `HookContentHash.cs`.
//!
//! The hash is the trust key: editing a trusted hook's command or URL changes
//! the hash and causes a re-prompt rather than inheriting the previous trust
//! decision.  Only behavioral fields are hashed; cosmetic fields (e.g. source
//! path) are excluded.

use sha2::{Sha256, Digest};

use super::UserHook;

pub struct HookContentHash;

impl HookContentHash {
    /// A short human-readable identifier for a hook (command, url, or handler type).
    pub fn hook_id(hook: &UserHook) -> String {
        if let Some(cmd) = &hook.command {
            return cmd.clone();
        }
        if let Some(url) = &hook.url {
            return url.clone();
        }
        format!("[{}]", hook.handler_type.as_deref().unwrap_or("command"))
    }

    /// Hex-encoded SHA-256 of the hook's behavioral fields.
    ///
    /// Fields that only affect display are excluded.  `plugin_origin` is
    /// included so updating a plugin re-prompts rather than inheriting the
    /// prior approval.
    pub fn compute(hook: &UserHook) -> String {
        // Build a canonical JSON string in a deterministic field order.
        // `serde_json::to_string` with `preserve_order` feature gives us
        // insertion order; we build the object manually for a stable order.
        let mutates_sorted: Option<Vec<&str>> = hook.mutates.as_ref().map(|v| {
            let mut sorted: Vec<&str> = v.iter().map(String::as_str).collect();
            sorted.sort_unstable();
            sorted
        });

        // Canonical representation — same fields as C# `HookContentHash.cs`.
        let canonical = serde_json::json!({
            "event": hook.event.to_lowercase(),
            "handlerType": hook.effective_handler_type().to_lowercase(),
            "command": hook.command,
            "url": hook.url,
            "hookPrompt": hook.hook_prompt,
            "agentType": hook.agent_type,
            "matcher": hook.matcher,
            "timeoutSeconds": hook.timeout_seconds,
            "failOpen": hook.fail_open,
            "unattendedDecision": hook.unattended_decision.as_deref().map(str::to_lowercase),
            "allowSystemPromptReplace": hook.allow_system_prompt_replace,
            "mutates": mutates_sorted,
            "pluginName": hook.plugin_origin.as_ref().map(|(n, _)| n),
            "pluginVersion": hook.plugin_origin.as_ref().map(|(_, v)| v),
        });

        let json = serde_json::to_string(&canonical).expect("json serialization cannot fail");
        let hash = Sha256::digest(json.as_bytes());
        hash.iter().map(|b| format!("{b:02x}")).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hooks::{HookScope, UserHook};

    fn make_hook(event: &str, command: &str) -> UserHook {
        UserHook {
            event: event.into(),
            command: Some(command.into()),
            matcher: None,
            timeout_seconds: None,
            fail_open: None,
            unattended_decision: None,
            allow_system_prompt_replace: false,
            mutates: None,
            handler_type: None,
            url: None,
            hook_prompt: None,
            agent_type: None,
            enabled: true,
            scope: HookScope::User,
            plugin_origin: None,
        }
    }

    #[test]
    fn same_hook_produces_same_hash() {
        let h = make_hook("PreToolUse", "echo hello");
        let hash1 = HookContentHash::compute(&h);
        let hash2 = HookContentHash::compute(&h);
        assert_eq!(hash1, hash2);
    }

    #[test]
    fn different_command_produces_different_hash() {
        let h1 = make_hook("PreToolUse", "echo hello");
        let h2 = make_hook("PreToolUse", "echo world");
        assert_ne!(HookContentHash::compute(&h1), HookContentHash::compute(&h2));
    }

    #[test]
    fn different_event_produces_different_hash() {
        let h1 = make_hook("PreToolUse", "echo hello");
        let h2 = make_hook("PostToolUse", "echo hello");
        assert_ne!(HookContentHash::compute(&h1), HookContentHash::compute(&h2));
    }

    #[test]
    fn hash_is_hex_string() {
        let h = make_hook("Stop", "notify");
        let hash = HookContentHash::compute(&h);
        assert_eq!(hash.len(), 64, "SHA-256 hex is 64 chars");
        assert!(hash.chars().all(|c| c.is_ascii_hexdigit()));
    }

    #[test]
    fn plugin_origin_is_included_in_hash() {
        let mut h = make_hook("PreToolUse", "echo hi");
        let hash_before = HookContentHash::compute(&h);
        h.plugin_origin = Some(("my-plugin".into(), "1.0.0".into()));
        let hash_after = HookContentHash::compute(&h);
        assert_ne!(hash_before, hash_after, "adding plugin_origin must change the hash");
    }
}
