//! Permission rules: parse, match, and the thread-safe live rule store.
//!
//! # Rule syntax
//!
//! - `toolName` — matches any call to that tool.
//! - `toolName(prefix:*)` — word-boundary glob: matches `command == prefix`
//!   **or** `command` starts with `prefix + " "`. So `git:*` matches `git`
//!   and `git push` but **not** `gitk`.
//! - `toolName(prefix)` — bare `StartsWith`: `git` matches `gitk` too.
//!
//! Tool-name comparison is case-insensitive. When the `"command"` JSON property
//! is absent, matching falls back to the raw `inputJson` string.

use std::sync::RwLock;

use serde_json;

/// A single allow or deny rule.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PermissionRule {
    pub tool_name: String,
    /// `None` → match any input. `Some("prefix:*")` → word-boundary glob.
    /// `Some("prefix")` → bare StartsWith.
    pub arg_pattern: Option<String>,
}

impl PermissionRule {
    /// Parse a rule string. Inverse of `to_rule_string`; the two round-trip cleanly.
    pub fn parse(rule: &str) -> Self {
        let Some(paren) = rule.find('(') else {
            return Self { tool_name: rule.to_owned(), arg_pattern: None };
        };
        let tool_name = rule[..paren].to_owned();
        let close = rule.rfind(')');
        let arg_pattern = if let Some(close) = close.filter(|&c| c > paren) {
            &rule[paren + 1..close]
        } else {
            &rule[paren + 1..]
        };
        Self { tool_name, arg_pattern: Some(arg_pattern.to_owned()) }
    }

    /// The canonical string form; always round-trips through `parse`.
    pub fn to_rule_string(&self) -> String {
        match &self.arg_pattern {
            None => self.tool_name.clone(),
            Some(pat) => format!("{}({})", self.tool_name, pat),
        }
    }

    /// Returns `true` when this rule matches `tool_name` and `input_json`.
    pub fn matches(&self, tool_name: &str, input_json: &str) -> bool {
        if !self.tool_name.eq_ignore_ascii_case(tool_name) {
            return false;
        }
        let Some(pattern) = &self.arg_pattern else {
            return true;
        };
        let command = extract_command_text(input_json);
        let command = command.trim();
        if let Some(prefix) = pattern.strip_suffix(":*") {
            // Word-boundary glob: exact match OR starts with `prefix + " "`.
            command.eq_ignore_ascii_case(prefix)
                || command.to_lowercase().starts_with(&format!("{} ", prefix.to_lowercase()))
        } else {
            // Bare StartsWith — no word boundary.
            command.to_lowercase().starts_with(&pattern.to_lowercase())
        }
    }
}

/// Extract the value of the `"command"` JSON property, or fall back to the raw string.
fn extract_command_text(input_json: &str) -> String {
    if let Ok(v) = serde_json::from_str::<serde_json::Value>(input_json) {
        if let Some(s) = v.get("command").and_then(|c| c.as_str()) {
            return s.to_owned();
        }
    }
    // Not valid JSON, or no "command" property — fall back to the raw string.
    input_json.to_owned()
}

// ── Rule store ──────────────────────────────────────────────────────────────

/// Session-scoped, mutable, thread-safe store of permission rules.
///
/// Read on every permission decision; rules added mid-session take effect on
/// the very next tool call. Deny rules are always checked before allow rules.
pub struct PermissionRuleStore {
    inner: RwLock<RuleSet>,
}

struct RuleSet {
    allow: Vec<PermissionRule>,
    deny: Vec<PermissionRule>,
}

impl PermissionRuleStore {
    pub fn new(
        allow: impl IntoIterator<Item = PermissionRule>,
        deny: impl IntoIterator<Item = PermissionRule>,
    ) -> Self {
        Self {
            inner: RwLock::new(RuleSet {
                allow: allow.into_iter().collect(),
                deny: deny.into_iter().collect(),
            }),
        }
    }

    /// Snapshot both lists atomically (single lock acquisition).
    pub fn snapshot(&self) -> (Vec<PermissionRule>, Vec<PermissionRule>) {
        let guard = self.inner.read().unwrap();
        (guard.deny.clone(), guard.allow.clone())
    }

    pub fn allow_rules(&self) -> Vec<PermissionRule> {
        self.inner.read().unwrap().allow.clone()
    }

    pub fn deny_rules(&self) -> Vec<PermissionRule> {
        self.inner.read().unwrap().deny.clone()
    }

    pub fn add_allow(&self, rules: impl IntoIterator<Item = PermissionRule>) {
        self.inner.write().unwrap().allow.extend(rules);
    }

    pub fn add_deny(&self, rules: impl IntoIterator<Item = PermissionRule>) {
        self.inner.write().unwrap().deny.extend(rules);
    }

    /// Returns the first matching rule prefixed with its list name, or `None`.
    ///
    /// Deny is checked first — deny always wins.
    pub fn find_matched_rule(&self, tool_name: &str, input_json: &str) -> Option<String> {
        let (deny, allow) = self.snapshot();
        for rule in &deny {
            if rule.matches(tool_name, input_json) {
                return Some(format!("deny:{}", rule.to_rule_string()));
            }
        }
        for rule in &allow {
            if rule.matches(tool_name, input_json) {
                return Some(format!("allow:{}", rule.to_rule_string()));
            }
        }
        None
    }
}

impl Default for PermissionRuleStore {
    fn default() -> Self {
        Self::new([], [])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // ── PermissionRule parse / to_rule_string ──────────────────────────────

    #[test]
    fn plain_tool_name_parses_with_no_pattern() {
        let rule = PermissionRule::parse("run_command");
        assert_eq!(rule.tool_name, "run_command");
        assert_eq!(rule.arg_pattern, None);
    }

    #[test]
    fn rule_with_pattern_parses_correctly() {
        let rule = PermissionRule::parse("run_command(git:*)");
        assert_eq!(rule.tool_name, "run_command");
        assert_eq!(rule.arg_pattern.as_deref(), Some("git:*"));
    }

    #[test]
    fn bare_prefix_pattern_parses_correctly() {
        let rule = PermissionRule::parse("run_command(git)");
        assert_eq!(rule.arg_pattern.as_deref(), Some("git"));
    }

    #[test]
    fn rule_string_round_trips() {
        for s in ["run_command", "run_command(git:*)", "run_command(git)", "danger(safe:*)"] {
            assert_eq!(PermissionRule::parse(s).to_rule_string(), s, "round-trip failed for {s}");
        }
    }

    // ── Rule matching: spec §8 item 11 ────────────────────────────────────

    // `git:*` matches "git" (exact) and "git push" (word boundary), but NOT "gitk".
    #[test]
    fn glob_pattern_matches_exact_command() {
        let rule = PermissionRule::parse("run_command(git:*)");
        assert!(rule.matches("run_command", r#"{"command":"git"}"#));
    }

    #[test]
    fn glob_pattern_matches_command_with_args() {
        let rule = PermissionRule::parse("run_command(git:*)");
        assert!(rule.matches("run_command", r#"{"command":"git push"}"#));
        assert!(rule.matches("run_command", r#"{"command":"git commit -m msg"}"#));
    }

    #[test]
    fn glob_pattern_does_not_match_gitk() {
        let rule = PermissionRule::parse("run_command(git:*)");
        // "gitk" starts with "git" but there is no word boundary.
        assert!(!rule.matches("run_command", r#"{"command":"gitk"}"#));
    }

    // Bare prefix has no word boundary — "git" matches "gitk".
    #[test]
    fn bare_prefix_matches_any_command_starting_with_prefix() {
        let rule = PermissionRule::parse("run_command(git)");
        assert!(rule.matches("run_command", r#"{"command":"git"}"#));
        assert!(rule.matches("run_command", r#"{"command":"git push"}"#));
        assert!(rule.matches("run_command", r#"{"command":"gitk"}"#));
    }

    // Plain tool name (no pattern) matches regardless of input.
    #[test]
    fn plain_rule_matches_any_input() {
        let rule = PermissionRule::parse("run_command");
        assert!(rule.matches("run_command", r#"{"command":"anything"}"#));
        assert!(rule.matches("run_command", "{}"));
    }

    // "command" field extraction vs raw fallback.
    #[test]
    fn command_field_is_extracted_from_json() {
        let rule = PermissionRule::parse("run_command(git:*)");
        // "command" property present → use its value.
        assert!(rule.matches("run_command", r#"{"command":"git status","cwd":"/tmp"}"#));
    }

    #[test]
    fn falls_back_to_raw_json_when_command_absent() {
        let rule = PermissionRule::parse("run_command(git)");
        // No "command" field; raw JSON is `{"args":"git push"}` which does NOT start with "git".
        assert!(!rule.matches("run_command", r#"{"args":"git push"}"#));
    }

    #[test]
    fn falls_back_to_raw_string_for_invalid_json() {
        let rule = PermissionRule::parse("run_command(git)");
        // Non-JSON input is used verbatim.
        assert!(rule.matches("run_command", "git push args"));
    }

    // Tool name comparison is case-insensitive.
    #[test]
    fn tool_name_match_is_case_insensitive() {
        let rule = PermissionRule::parse("Run_Command(git:*)");
        assert!(rule.matches("run_command", r#"{"command":"git"}"#));
        assert!(!rule.matches("other_tool", r#"{"command":"git"}"#));
    }

    // ── PermissionRuleStore ────────────────────────────────────────────────

    // Spec §8 item 12: deny beats allow.
    #[test]
    fn deny_beats_allow_in_rule_store() {
        let store = PermissionRuleStore::new(
            [PermissionRule::parse("run_command")], // allow
            [PermissionRule::parse("run_command")], // deny
        );
        assert_eq!(
            store.find_matched_rule("run_command", "{}"),
            Some("deny:run_command".to_string())
        );
    }

    #[test]
    fn allow_rule_matched_when_no_deny() {
        let store = PermissionRuleStore::new(
            [PermissionRule::parse("safe_tool")],
            [],
        );
        assert_eq!(
            store.find_matched_rule("safe_tool", "{}"),
            Some("allow:safe_tool".to_string())
        );
    }

    #[test]
    fn no_match_returns_none() {
        let store = PermissionRuleStore::new(
            [PermissionRule::parse("other")],
            [PermissionRule::parse("another")],
        );
        assert_eq!(store.find_matched_rule("unknown", "{}"), None);
    }

    #[test]
    fn rules_added_after_construction_take_effect() {
        let store = PermissionRuleStore::default();
        store.add_deny([PermissionRule::parse("dangerous")]);
        assert!(store.find_matched_rule("dangerous", "{}").unwrap().starts_with("deny:"));
    }

    #[test]
    fn snapshot_returns_deny_then_allow() {
        let store = PermissionRuleStore::new(
            [PermissionRule::parse("allow_tool")],
            [PermissionRule::parse("deny_tool")],
        );
        let (deny, allow) = store.snapshot();
        assert_eq!(deny[0].tool_name, "deny_tool");
        assert_eq!(allow[0].tool_name, "allow_tool");
    }
}
