//! Tool-name matcher for hooks.
//!
//! Matches C# `HookMatcher.cs`.
//!
//! A matcher compiles to an anchored, case-insensitive regex:
//! `^(?:<pattern>)$`.  An invalid pattern falls back to case-insensitive
//! exact string equality so a misconfigured hook does not blow up the agent.
//!
//! Regexes are cached in a `DashMap`-free way using a `Mutex<HashMap>`.
//! Patterns come from user/project/plugin settings and are not hot paths, so
//! the interpreted engine and a simple cache are fast enough.

use std::collections::HashMap;
use std::sync::Mutex;

use regex::Regex;

static CACHE: Mutex<Option<HashMap<String, Option<Regex>>>> = Mutex::new(None);

pub struct HookMatcher;

impl HookMatcher {
    /// Returns `true` when `tool_name` satisfies `pattern`.
    ///
    /// A `None` or empty pattern matches everything.
    pub fn matches(pattern: Option<&str>, tool_name: &str) -> bool {
        match pattern {
            None | Some("") => true,
            Some(p) => {
                let regex = Self::get_or_compile(p);
                match &regex {
                    Some(r) => r.is_match(tool_name),
                    // Invalid regex — fall back to case-insensitive exact equality.
                    None => p.eq_ignore_ascii_case(tool_name),
                }
            }
        }
    }

    fn get_or_compile(pattern: &str) -> Option<Regex> {
        let mut guard = CACHE.lock().unwrap();
        let cache = guard.get_or_insert_with(HashMap::new);
        if let Some(cached) = cache.get(pattern) {
            return cached.clone();
        }
        // Prefix with (?i) for case-insensitive matching and anchor the pattern.
        let anchored = format!("(?i)^(?:{pattern})$");
        let compiled = Regex::new(&anchored).ok();
        cache.insert(pattern.to_owned(), compiled.clone());
        compiled
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn none_pattern_matches_any_tool() {
        assert!(HookMatcher::matches(None, "read_file"));
        assert!(HookMatcher::matches(None, "bash"));
    }

    #[test]
    fn empty_pattern_matches_any_tool() {
        assert!(HookMatcher::matches(Some(""), "read_file"));
    }

    #[test]
    fn exact_pattern_matches_exact_name() {
        assert!(HookMatcher::matches(Some("bash"), "bash"));
        assert!(!HookMatcher::matches(Some("bash"), "read_file"));
    }

    #[test]
    fn regex_alternation_matches_multiple() {
        assert!(HookMatcher::matches(Some("bash|run_command"), "bash"));
        assert!(HookMatcher::matches(Some("bash|run_command"), "run_command"));
        assert!(!HookMatcher::matches(Some("bash|run_command"), "read_file"));
    }

    #[test]
    fn matching_is_case_insensitive() {
        assert!(HookMatcher::matches(Some("BASH"), "bash"));
        assert!(HookMatcher::matches(Some("bash"), "BASH"));
    }

    #[test]
    fn anchoring_prevents_partial_match() {
        // "read" must not match "x_read_file"
        assert!(!HookMatcher::matches(Some("read"), "x_read_file"));
        assert!(!HookMatcher::matches(Some("read"), "read_file"));
        assert!(HookMatcher::matches(Some("read"), "read"));
    }

    #[test]
    fn invalid_regex_falls_back_to_exact_equality() {
        // "[invalid" is not a valid regex.
        // The fallback is case-insensitive exact equality.
        assert!(!HookMatcher::matches(Some("[invalid"), "bash"));
        assert!(HookMatcher::matches(Some("[invalid"), "[invalid"));
    }
}
