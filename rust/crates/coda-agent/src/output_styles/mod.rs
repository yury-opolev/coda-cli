//! Named output styles (personas) applied to the system prompt.
//!
//! Matches C# `OutputStyles/OutputStyle.cs` + `BuiltInOutputStyles.cs`.
//!
//! Built-in styles are compile-time constants.  Plugin styles are registered
//! at startup into a process-wide registry (same semantics as C#); tests that
//! mutate the registry MUST call [`BuiltInOutputStyles::clear_plugin_styles`]
//! in teardown.

use std::sync::Mutex;

// ─────────────────────────────────────────────────────────────────────────────
// OutputStyle
// ─────────────────────────────────────────────────────────────────────────────

/// A named persona that appends guidance to the system prompt.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OutputStyle {
    pub name: &'static str,
    pub description: &'static str,
    pub system_prompt_suffix: &'static str,
}

// ─────────────────────────────────────────────────────────────────────────────
// Built-in styles
// ─────────────────────────────────────────────────────────────────────────────

const DEFAULT: OutputStyle = OutputStyle {
    name: "default",
    description: "Standard balanced responses with no additional style guidance.",
    system_prompt_suffix: "",
};

const CONCISE: OutputStyle = OutputStyle {
    name: "concise",
    description: "Terse, minimal prose — give the answer with as few words as possible.",
    system_prompt_suffix: "Respond as tersely as possible. Use bullet points and short sentences. \
        Omit all preamble, filler, and unnecessary explanation. Prefer code over prose. \
        If a single word or line suffices, use it. Never restate what the user said. \
        Every word must earn its place.",
};

const EXPLANATORY: OutputStyle = OutputStyle {
    name: "explanatory",
    description: "Teach as you go — explain reasoning, concepts, and trade-offs.",
    system_prompt_suffix: "Adopt a teaching tone. As you work through tasks, explain your \
        reasoning, highlight relevant concepts, and surface important trade-offs or alternatives \
        the user should understand. Define technical terms on first use. When you make a decision, \
        say why. Help the user build mental models, not just get answers.",
};

const CODE_REVIEWER: OutputStyle = OutputStyle {
    name: "code-reviewer",
    description: "Focus on reviewing and critiquing code — spot issues, suggest improvements.",
    system_prompt_suffix: "Act as a thorough code reviewer. Prioritize correctness, clarity, \
        performance, security, and maintainability. When examining code, call out bugs, \
        anti-patterns, missed edge cases, and unclear naming. Suggest concrete improvements with \
        brief rationale. Be direct and specific — generic praise without substance is unhelpful. \
        If the code is good, say so briefly and explain why.",
};

static ALL_BUILTIN: &[OutputStyle] = &[DEFAULT, CONCISE, EXPLANATORY, CODE_REVIEWER];

// ─────────────────────────────────────────────────────────────────────────────
// Plugin registry
// ─────────────────────────────────────────────────────────────────────────────

/// Dynamically-registered plugin output styles.
#[derive(Clone, PartialEq, Eq)]
pub struct DynOutputStyle {
    pub name: String,
    pub description: String,
    pub system_prompt_suffix: String,
}

static PLUGIN_STYLES: Mutex<Vec<DynOutputStyle>> = Mutex::new(Vec::new());

// ─────────────────────────────────────────────────────────────────────────────
// BuiltInOutputStyles
// ─────────────────────────────────────────────────────────────────────────────

pub struct BuiltInOutputStyles;

impl BuiltInOutputStyles {
    /// All built-in styles in display order; never includes plugin styles.
    pub fn all() -> &'static [OutputStyle] {
        ALL_BUILTIN
    }

    /// Register a plugin-contributed style.
    ///
    /// Returns `false` (and drops the registration) when the name collides with
    /// a built-in style — built-in names are protected.
    pub fn register_plugin(style: DynOutputStyle) -> bool {
        for builtin in ALL_BUILTIN {
            if builtin.name.eq_ignore_ascii_case(&style.name) {
                return false;
            }
        }
        let mut guard = PLUGIN_STYLES.lock().unwrap();
        guard.retain(|s| !s.name.eq_ignore_ascii_case(&style.name));
        guard.push(style);
        true
    }

    /// Clear all plugin-registered styles. Call in test teardown.
    pub fn clear_plugin_styles() {
        PLUGIN_STYLES.lock().unwrap().clear();
    }

    /// All plugin-registered styles (not including built-ins).
    pub fn plugin_styles() -> Vec<DynOutputStyle> {
        PLUGIN_STYLES.lock().unwrap().clone()
    }

    /// Returns `true` if the name matches a built-in or a registered plugin style.
    pub fn is_known(name: Option<&str>) -> bool {
        let name = match name {
            Some(n) if !n.is_empty() => n,
            _ => return false,
        };
        if ALL_BUILTIN.iter().any(|s| s.name.eq_ignore_ascii_case(name)) {
            return true;
        }
        PLUGIN_STYLES.lock().unwrap().iter().any(|s| s.name.eq_ignore_ascii_case(name))
    }

    /// Resolve a style name to a system-prompt suffix.
    ///
    /// `None`, `"default"`, or any unknown name returns the empty string so the
    /// default style (no suffix) is always returned for unknown inputs.
    pub fn resolve_suffix(name: Option<&str>) -> String {
        match name {
            None => String::new(),
            Some(n) if n.is_empty() || n.eq_ignore_ascii_case("default") => String::new(),
            Some(n) => {
                for s in ALL_BUILTIN {
                    if s.name.eq_ignore_ascii_case(n) {
                        return s.system_prompt_suffix.to_owned();
                    }
                }
                let guard = PLUGIN_STYLES.lock().unwrap();
                if let Some(p) = guard.iter().find(|s| s.name.eq_ignore_ascii_case(n)) {
                    return p.system_prompt_suffix.clone();
                }
                String::new() // unknown → default (no suffix)
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    fn teardown() {
        BuiltInOutputStyles::clear_plugin_styles();
    }

    #[test]
    fn default_suffix_is_empty() {
        assert!(BuiltInOutputStyles::resolve_suffix(None).is_empty());
        assert!(BuiltInOutputStyles::resolve_suffix(Some("default")).is_empty());
        assert!(BuiltInOutputStyles::resolve_suffix(Some("DEFAULT")).is_empty());
    }

    #[test]
    fn concise_suffix_is_non_empty() {
        let s = BuiltInOutputStyles::resolve_suffix(Some("concise"));
        assert!(!s.is_empty());
        assert!(s.contains("tersely"), "concise suffix should mention 'tersely'");
    }

    #[test]
    fn unknown_name_falls_back_to_default() {
        let s = BuiltInOutputStyles::resolve_suffix(Some("nonexistent"));
        assert!(s.is_empty(), "unknown style must fall back to empty suffix");
    }

    #[test]
    fn is_known_returns_true_for_builtins() {
        assert!(BuiltInOutputStyles::is_known(Some("concise")));
        assert!(BuiltInOutputStyles::is_known(Some("CONCISE")));
        assert!(!BuiltInOutputStyles::is_known(Some("unknown")));
        assert!(!BuiltInOutputStyles::is_known(None));
    }

    #[test]
    fn plugin_registration_collisions_with_builtins_are_dropped() {
        let clash = DynOutputStyle {
            name: "concise".into(),
            description: "override attempt".into(),
            system_prompt_suffix: "bad".into(),
        };
        let ok = BuiltInOutputStyles::register_plugin(clash);
        assert!(!ok, "registration should be rejected for built-in name");
        // built-in suffix must still be intact
        let s = BuiltInOutputStyles::resolve_suffix(Some("concise"));
        assert!(s.contains("tersely"));
        teardown();
    }

    #[test]
    fn plugin_style_is_resolved() {
        let style = DynOutputStyle {
            name: "pirate".into(),
            description: "Talk like a pirate".into(),
            system_prompt_suffix: "Speak in the voice of a pirate. Arr!".into(),
        };
        let ok = BuiltInOutputStyles::register_plugin(style);
        assert!(ok);
        assert!(BuiltInOutputStyles::is_known(Some("pirate")));
        let s = BuiltInOutputStyles::resolve_suffix(Some("pirate"));
        assert!(s.contains("Arr!"));
        teardown();
    }

    #[test]
    fn all_returns_four_builtin_styles() {
        let all = BuiltInOutputStyles::all();
        assert_eq!(all.len(), 4);
        assert_eq!(all[0].name, "default");
        assert_eq!(all[1].name, "concise");
    }
}
