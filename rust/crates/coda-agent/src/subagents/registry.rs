//! `SubagentRegistry` — resolves subagent type names against plugin-contributed
//! definitions, falling back to built-in agents for unknown or null types.
//!
//! Mirrors C# `Subagents/SubagentRegistry.cs`.

use super::{BuiltInAgents, SubagentDefinition};

/// Resolves subagent type names, preferring built-ins for defense-in-depth and
/// falling back to plugin-provided definitions for unknown types.
///
/// Built-in types are checked FIRST so a plugin-contributed agent whose type
/// name collides with a built-in is silently ignored. `PluginComponentComposer`
/// rejects such collisions at compose time; this order is an additional guard.
pub struct SubagentRegistry {
    plugin_agents: Vec<SubagentDefinition>,
}

impl SubagentRegistry {
    /// Creates a registry with an optional list of plugin-contributed agent definitions.
    pub fn new(plugin_agents: impl Into<Vec<SubagentDefinition>>) -> Self {
        Self {
            plugin_agents: plugin_agents.into(),
        }
    }

    /// Creates an empty registry (no plugin agents).
    pub fn empty() -> Self {
        Self { plugin_agents: Vec::new() }
    }

    /// Resolves a subagent type name.
    ///
    /// Priority: built-in (case-insensitive) > plugin-provided > fallback to general-purpose.
    pub fn resolve(&self, agent_type: Option<&str>) -> SubagentDefinition {
        if let Some(t) = agent_type.filter(|t| !t.is_empty()) {
            // Built-in check first (defense-in-depth).
            if BuiltInAgents::is_builtin(Some(t)) {
                return BuiltInAgents::resolve(Some(t));
            }
            // Plugin-provided definitions.
            for def in &self.plugin_agents {
                if def.agent_type.eq_ignore_ascii_case(t) {
                    return def.clone();
                }
            }
        }
        // Unknown type or None → fall back to general-purpose.
        BuiltInAgents::resolve(agent_type)
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    fn def(agent_type: &str, body: &str, ro: bool) -> SubagentDefinition {
        SubagentDefinition {
            agent_type: agent_type.to_owned(),
            description: "desc".to_owned(),
            system_prompt_body: body.to_owned(),
            read_only_tools_only: ro,
            default_model: None,
        }
    }

    #[test]
    fn resolves_plugin_agent_by_type() {
        let registry = SubagentRegistry::new(vec![def("my-reviewer", "You review code.", true)]);
        let resolved = registry.resolve(Some("my-reviewer"));
        assert_eq!(resolved.agent_type, "my-reviewer");
        assert_eq!(resolved.system_prompt_body, "You review code.");
        assert!(resolved.read_only_tools_only);
    }

    #[test]
    fn resolves_plugin_agent_case_insensitively() {
        let registry = SubagentRegistry::new(vec![def("my-reviewer", "body", false)]);
        assert_eq!(registry.resolve(Some("MY-REVIEWER")).agent_type, "my-reviewer");
        assert_eq!(registry.resolve(Some("My-Reviewer")).agent_type, "my-reviewer");
    }

    #[test]
    fn falls_back_to_built_in_for_unknown_type() {
        let registry = SubagentRegistry::empty();
        let resolved = registry.resolve(Some("general-purpose"));
        assert_eq!(resolved.agent_type, "general-purpose");
    }

    #[test]
    fn null_type_falls_back_to_built_in() {
        let registry = SubagentRegistry::empty();
        let resolved = registry.resolve(None);
        assert_eq!(resolved.agent_type, "general-purpose");

        let resolved2 = registry.resolve(Some("totally-unknown-type"));
        assert_eq!(resolved2.agent_type, "general-purpose");
    }

    #[test]
    fn plugin_agent_shadows_unknown_type() {
        let registry = SubagentRegistry::new(vec![def("custom-type", "Custom body", false)]);
        let resolved = registry.resolve(Some("custom-type"));
        assert_eq!(resolved.agent_type, "custom-type");
        assert_eq!(resolved.system_prompt_body, "Custom body");
    }

    #[test]
    fn built_in_takes_priority_over_plugin_with_same_name() {
        // Plugin tries to shadow "general-purpose" — must be ignored.
        let registry = SubagentRegistry::new(vec![def("general-purpose", "evil body", false)]);
        let resolved = registry.resolve(Some("general-purpose"));
        // Must return the real built-in, not the plugin shadow.
        assert_ne!(resolved.system_prompt_body, "evil body");
        assert_eq!(resolved.agent_type, "general-purpose");
    }
}
