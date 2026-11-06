//! Turns a [`PluginSet`] into what the serve host wires: the skill catalogue,
//! plugin agents, granted directories, hooks, LSP servers and the
//! `plugins/list` inventory. Pure apart from reading skill files, so the
//! host's composition stays a handful of calls.

use std::collections::{HashMap, HashSet};
use std::path::Path;

use coda_agent::hooks::{HookContentHash, UserHook};
use coda_agent::lsp::LspServerConfig;
use coda_agent::subagents::SubagentDefinition;
use coda_agent::tools::SkillEntry;
use coda_agent::PluginAgentLoader;
use serde_json::{json, Value};

use super::components;
use super::trust::{PluginTrustStore, KIND_HOOK, KIND_LSP_SERVER, KIND_MCP_SERVER};
use super::PluginSet;
use crate::skills::{self, SkillInfo};

/// Everything the host needs from the installed plugins.
pub struct PluginWiring {
    pub skills: Vec<SkillInfo>,
    pub catalog: Vec<SkillEntry>,
    pub agents: Vec<SubagentDefinition>,
    /// Plugin roots and non-project skill directories: file tools may read
    /// there so skills can open their sibling files.
    pub granted_directories: HashSet<String>,
    /// Plugin hooks; unapproved ones are disabled.
    pub hooks: Vec<UserHook>,
    /// Content hashes of approved plugin hooks, to pre-trust for the session.
    pub trusted_hook_hashes: Vec<String>,
    pub lsp_servers: HashMap<String, LspServerConfig>,
}

impl PluginWiring {
    pub fn build(workspace: &Path, plugins: &PluginSet, trust: &PluginTrustStore) -> Self {
        let agents = plugin_agents(plugins);
        let mut agents_by_plugin: HashMap<String, Vec<String>> = HashMap::new();
        for a in &agents {
            if let Some((plugin, _)) = a.agent_type.split_once(':') {
                agents_by_plugin.entry(plugin.to_owned()).or_default().push(a.agent_type.clone());
            }
        }
        let skills = skills::discover(workspace, plugins);
        let catalog = skills::catalog(&skills, &agents_by_plugin);

        let mut granted_directories: HashSet<String> =
            plugins.active().map(|p| p.root.to_string_lossy().into_owned()).collect();
        for s in skills.iter().filter(|s| s.plugin.is_none() && s.source_path.is_none()) {
            if let Some(dir) = s.file.parent() {
                granted_directories.insert(dir.to_string_lossy().into_owned());
            }
        }

        let mut hooks = Vec::new();
        let mut trusted_hook_hashes = Vec::new();
        for (hook, approved) in components::plugin_hooks(plugins, trust) {
            if approved {
                trusted_hook_hashes.push(HookContentHash::compute(&hook));
            }
            hooks.push(hook);
        }

        Self {
            skills,
            catalog,
            agents,
            granted_directories,
            hooks,
            trusted_hook_hashes,
            lsp_servers: components::approved_lsp_servers(plugins, trust),
        }
    }

    /// `(type, description)` pairs for the `task` tool description.
    pub fn agent_summaries(&self) -> Vec<(String, String)> {
        self.agents.iter().map(|a| (a.agent_type.clone(), a.description.clone())).collect()
    }
}

/// Every active plugin's agents, namespaced `<plugin>:<agent>`.
pub fn plugin_agents(plugins: &PluginSet) -> Vec<SubagentDefinition> {
    let mut out = Vec::new();
    for plugin in plugins.active() {
        for dir in &plugin.agent_dirs {
            out.extend(PluginAgentLoader::load_for_plugin(dir, &plugin.name, plugin.source.is_project(), &plugin.root));
        }
    }
    out
}

/// The `plugins/list` result.
pub fn inventory(workspace: &Path, plugins: &PluginSet, trust: &PluginTrustStore) -> Value {
    let skills = skills::discover(workspace, plugins);
    let agents = plugin_agents(plugins);
    let items: Vec<Value> = plugins
        .plugins
        .iter()
        .map(|p| {
            let (mut skill_names, mut command_names) = (Vec::new(), Vec::new());
            for s in skills.iter().filter(|s| s.plugin.as_deref() == Some(p.name.as_str())) {
                if s.file.file_name().is_some_and(|f| f == "SKILL.md") {
                    skill_names.push(s.name.clone());
                } else {
                    command_names.push(s.name.clone());
                }
            }
            let prefix = format!("{}:", p.name);
            let agent_names: Vec<String> =
                agents.iter().filter(|a| a.agent_type.starts_with(&prefix)).map(|a| a.agent_type.clone()).collect();
            let names = |kind: &str| -> Vec<String> {
                p.exec.iter().filter(|c| c.kind == kind).map(|c| c.name.clone()).collect()
            };
            let hook_events: Vec<String> = p
                .exec
                .iter()
                .filter(|c| c.kind == KIND_HOOK)
                .flat_map(|c| c.definition.as_object().map(|m| m.keys().cloned().collect::<Vec<_>>()).unwrap_or_default())
                .collect();
            let components = json!({
                "skills": skill_names,
                "commands": command_names,
                "agents": agent_names,
                "mcpServers": names(KIND_MCP_SERVER),
                "hooks": hook_events,
                "lspServers": names(KIND_LSP_SERVER),
            });
            let pending: Vec<String> = trust.pending(p).iter().map(|c| c.label()).collect();
            super::inventory_item(p, components, trust.state(p).as_str(), &pending)
        })
        .collect();
    json!({ "plugins": items, "errors": plugins.errors })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::plugins::test_support::{plugin, write};
    use crate::plugins::{discover_plugins, PluginSettings};

    #[test]
    fn wiring_and_inventory_attribute_components_to_their_plugin() {
        let ws = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let root = home.path().join("plugins/deep");
        plugin(&root, "deep-review");
        write(
            &root.join("hooks/hooks.json"),
            r#"{"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"echo"}]}]}}"#,
        );
        let set = discover_plugins(ws.path(), home.path(), &PluginSettings::default());
        let mut trust = PluginTrustStore::load(home.path());

        let wiring = PluginWiring::build(ws.path(), &set, &trust);
        assert_eq!(wiring.agents.len(), 1);
        assert_eq!(wiring.agents[0].agent_type, "deep-review:skeptic");
        let entry = wiring.catalog.iter().find(|e| e.name == "deep-review:review").unwrap();
        assert_eq!(entry.plugin_agents, vec!["deep-review:skeptic".to_owned()]);
        assert!(wiring.granted_directories.contains(&*set.plugins[0].root.to_string_lossy()));
        assert_eq!(wiring.hooks.len(), 1);
        assert!(!wiring.hooks[0].enabled, "unapproved hooks never run");
        assert!(wiring.trusted_hook_hashes.is_empty());

        let inv = inventory(ws.path(), &set, &trust);
        let item = &inv["plugins"][0];
        assert_eq!(item["name"], "deep-review");
        assert_eq!(item["source"], "user");
        assert_eq!(item["trust"], "pending");
        assert_eq!(item["trusted"], false);
        assert_eq!(item["components"]["skills"][0], "deep-review:review");
        assert_eq!(item["components"]["agents"][0], "deep-review:skeptic");
        assert_eq!(item["components"]["hooks"][0], "SessionStart");
        assert_eq!(item["pendingApproval"][0], "hook:hooks");

        let p = &set.plugins[0];
        trust.approve(p, &p.exec[0]);
        let wiring = PluginWiring::build(ws.path(), &set, &trust);
        assert!(wiring.hooks[0].enabled);
        assert_eq!(wiring.trusted_hook_hashes.len(), 1);
        assert_eq!(inventory(ws.path(), &set, &trust)["plugins"][0]["trust"], "approved");
    }
}
