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

    /// The plugin-contributed definitions, in registration order.
    pub fn plugin_agents(&self) -> &[SubagentDefinition] {
        &self.plugin_agents
    }

    /// Resolves a subagent type name, accepting a plugin agent's short name.
    ///
    /// Priority: built-in > exact plugin type (`plugin:agent`) > the unique
    /// plugin agent whose short name matches > general-purpose fallback. A
    /// short name shared by agents of several plugins is an error naming every
    /// candidate rather than a silent pick.
    pub fn try_resolve(&self, agent_type: Option<&str>) -> Result<SubagentDefinition, String> {
        if let Some(t) = agent_type.map(str::trim).filter(|t| !t.is_empty()) {
            if !BuiltInAgents::is_builtin(Some(t)) {
                if let Some(def) = self.plugin_agents.iter().find(|d| d.agent_type.eq_ignore_ascii_case(t)) {
                    return Ok(def.clone());
                }
                let short: Vec<&SubagentDefinition> = self
                    .plugin_agents
                    .iter()
                    .filter(|d| {
                        d.agent_type
                            .rsplit_once(':')
                            .is_some_and(|(_, s)| s.eq_ignore_ascii_case(t))
                    })
                    .collect();
                match short.as_slice() {
                    [one] => return Ok((*one).clone()),
                    [] => {}
                    many => {
                        let names: Vec<&str> = many.iter().map(|d| d.agent_type.as_str()).collect();
                        return Err(format!(
                            "Subagent type '{t}' is ambiguous; use one of: {}",
                            names.join(", ")
                        ));
                    }
                }
            }
        }
        Ok(self.resolve(agent_type))
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
    fn plugin_agents_resolve_by_qualified_or_unique_short_name() {
        let registry = SubagentRegistry::new(vec![
            def("deep-review:skeptic", "skeptic body", false),
            def("deep-review:advocate", "a1", false),
            def("other:advocate", "a2", false),
        ]);
        assert_eq!(registry.try_resolve(Some("deep-review:skeptic")).unwrap().system_prompt_body, "skeptic body");
        assert_eq!(registry.try_resolve(Some("SKEPTIC")).unwrap().agent_type, "deep-review:skeptic");
        let err = registry.try_resolve(Some("advocate")).unwrap_err();
        assert!(err.contains("deep-review:advocate") && err.contains("other:advocate"), "{err}");
        assert_eq!(registry.try_resolve(Some("explore")).unwrap().agent_type, "explore");
        assert_eq!(registry.try_resolve(Some("nobody")).unwrap().agent_type, "general-purpose");
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
