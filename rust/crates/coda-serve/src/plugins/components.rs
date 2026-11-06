//! A plugin's executable components (MCP servers, hooks, LSP servers): how
//! they are read from the manifest, and how approved ones are turned into the
//! engine's own configuration types.
//!
//! `${CLAUDE_PLUGIN_ROOT}` and `${CODA_PLUGIN_ROOT}` are expanded in every
//! string of an MCP server or hook definition, and both are exported to the
//! processes they start.

use std::collections::HashMap;
use std::path::Path;

use coda_agent::hooks::{HookScope, UserHook};
use coda_agent::lsp::{LspServerConfig, PluginLspServerLoader};
use coda_mcp::config::{McpConnectable, McpHttpConnectable};
use serde_json::{Map, Value};

use super::manifest::{self, PluginManifest};
use super::trust::{ExecComponent, PluginTrustStore, KIND_HOOK, KIND_LSP_SERVER, KIND_MCP_SERVER};
use super::{PluginDescriptor, PluginSet};

/// Reads every executable component a manifest declares (or the plugin
/// ships in the default locations). Problems are pushed to `errors`.
pub fn exec_components(
    root: &Path,
    m: &PluginManifest,
    errors: &mut Vec<String>,
) -> Vec<ExecComponent> {
    let mut out = Vec::new();

    let mcp = match &m.mcp_servers {
        Some(decl) => declared_map(root, decl, "mcpServers", errors),
        None => default_file(root, ".mcp.json", "mcpServers", errors),
    };
    for (name, definition) in mcp {
        out.push(ExecComponent {
            kind: KIND_MCP_SERVER,
            name,
            definition,
        });
    }

    let hooks = match &m.hooks {
        Some(decl) => declared_map(root, decl, "hooks", errors),
        None => default_file(root, "hooks/hooks.json", "hooks", errors),
    };
    if !hooks.is_empty() {
        out.push(ExecComponent {
            kind: KIND_HOOK,
            name: "hooks".into(),
            definition: Value::Object(hooks),
        });
    }

    let mut lsp = default_file(root, ".lsp.json", "lspServers", errors);
    if let Some(decl) = &m.lsp_servers {
        let items: Vec<&Value> = match decl {
            Value::Array(items) => items.iter().collect(),
            other => vec![other],
        };
        for item in items {
            lsp.extend(declared_map(root, item, "lspServers", errors));
        }
    }
    for (name, definition) in lsp {
        out.push(ExecComponent {
            kind: KIND_LSP_SERVER,
            name,
            definition,
        });
    }
    out
}

/// An inline object, or a contained relative path to a JSON file holding
/// either `{ "<key>": {…} }` or the bare map.
fn declared_map(
    root: &Path,
    decl: &Value,
    key: &str,
    errors: &mut Vec<String>,
) -> Map<String, Value> {
    match decl {
        Value::Object(obj) => unwrap_key(obj, key),
        Value::String(rel) => match manifest::resolve_contained(root, rel) {
            Ok(path) => read_map(&path, key, errors),
            Err(e) => {
                errors.push(format!("{key}: {e}"));
                Map::new()
            }
        },
        _ => {
            errors.push(format!("{key} must be an object or a relative path"));
            Map::new()
        }
    }
}

fn default_file(root: &Path, rel: &str, key: &str, errors: &mut Vec<String>) -> Map<String, Value> {
    let path = root.join(rel);
    if path.is_file() {
        read_map(&path, key, errors)
    } else {
        Map::new()
    }
}

fn read_map(path: &Path, key: &str, errors: &mut Vec<String>) -> Map<String, Value> {
    let parsed = std::fs::read_to_string(path)
        .map_err(|e| e.to_string())
        .and_then(|t| {
            serde_json::from_str::<Value>(t.trim_start_matches('\u{feff}'))
                .map_err(|e| e.to_string())
        });
    match parsed {
        Ok(Value::Object(obj)) => unwrap_key(&obj, key),
        Ok(_) => {
            errors.push(format!(
                "{key}: {} must contain a JSON object",
                path.display()
            ));
            Map::new()
        }
        Err(e) => {
            errors.push(format!("{key}: cannot read {}: {e}", path.display()));
            Map::new()
        }
    }
}

fn unwrap_key(obj: &Map<String, Value>, key: &str) -> Map<String, Value> {
    match obj.get(key) {
        Some(Value::Object(inner)) => inner.clone(),
        _ => obj.clone(),
    }
}

/// Expands both plugin-root variables in every string of `v`.
fn expand_value(v: &Value, root: &Path) -> Value {
    match v {
        Value::String(s) => Value::String(super::expand_root(s, root)),
        Value::Array(a) => Value::Array(a.iter().map(|x| expand_value(x, root)).collect()),
        Value::Object(m) => Value::Object(
            m.iter()
                .map(|(k, x)| (k.clone(), expand_value(x, root)))
                .collect(),
        ),
        other => other.clone(),
    }
}

fn root_env(root: &Path) -> HashMap<String, String> {
    let root = root.to_string_lossy().into_owned();
    HashMap::from([
        ("CLAUDE_PLUGIN_ROOT".to_owned(), root.clone()),
        ("CODA_PLUGIN_ROOT".to_owned(), root),
    ])
}

/// The MCP server name a plugin server is registered under. Tool names
/// become `mcp__plugin_<plugin>_<server>__<tool>`.
pub fn mcp_server_name(plugin: &str, server: &str) -> String {
    format!("plugin_{plugin}_{server}")
}

/// Approved MCP servers of every active plugin.
pub fn approved_mcp_servers(
    plugins: &PluginSet,
    trust: &PluginTrustStore,
) -> (Vec<McpConnectable>, Vec<McpHttpConnectable>) {
    let mut stdio = Vec::new();
    let mut http = Vec::new();
    for plugin in plugins.active() {
        let mut map = Map::new();
        for c in plugin.exec.iter().filter(|c| c.kind == KIND_MCP_SERVER) {
            if trust.is_approved(plugin, c) {
                map.insert(
                    mcp_server_name(&plugin.name, &c.name),
                    expand_value(&c.definition, &plugin.root),
                );
            }
        }
        let (mut s, h) = coda_mcp::config::connectables_from_map(&Value::Object(map));
        for server in &mut s {
            for (k, v) in root_env(&plugin.root) {
                server.env.entry(k).or_insert(v);
            }
        }
        stdio.extend(s);
        http.extend(h);
    }
    (stdio, http)
}

/// Every hook of every active plugin, in Coda's [`UserHook`] form. Hooks
/// whose plugin approval is missing come back **disabled**: they are listed
/// (so `hooks/list` shows them) but never run and never block a gate.
/// The returned flag per hook says whether it is approved.
pub fn plugin_hooks(plugins: &PluginSet, trust: &PluginTrustStore) -> Vec<(UserHook, bool)> {
    let mut out = Vec::new();
    for plugin in plugins.active() {
        for c in plugin.exec.iter().filter(|c| c.kind == KIND_HOOK) {
            let approved = trust.is_approved(plugin, c);
            for mut hook in hooks_from_definition(plugin, &c.definition) {
                hook.enabled = hook.enabled && approved;
                out.push((hook, approved));
            }
        }
    }
    out
}

/// Converts the Claude hooks format
/// `{ "<Event>": [ { "matcher"?, "hooks": [ { "type": "command", "command", "timeout"? } ] } ] }`.
fn hooks_from_definition(plugin: &PluginDescriptor, events: &Value) -> Vec<UserHook> {
    let mut out = Vec::new();
    let Some(events) = events.as_object() else {
        return out;
    };
    for (event, groups) in events {
        for group in groups.as_array().into_iter().flatten() {
            let matcher = group
                .get("matcher")
                .and_then(Value::as_str)
                .filter(|m| !m.is_empty() && *m != "*")
                .map(str::to_owned);
            for handler in group
                .get("hooks")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
            {
                let kind = handler
                    .get("type")
                    .and_then(Value::as_str)
                    .unwrap_or("command");
                if kind != "command" {
                    tracing::debug!(
                        plugin = plugin.name.as_str(),
                        kind,
                        "skipping unsupported plugin hook type"
                    );
                    continue;
                }
                let Some(command) = handler.get("command").and_then(Value::as_str) else {
                    continue;
                };
                out.push(UserHook {
                    event: event.clone(),
                    command: Some(super::expand_root(command, &plugin.root)),
                    matcher: matcher.clone(),
                    timeout_seconds: handler.get("timeout").and_then(Value::as_u64),
                    fail_open: None,
                    unattended_decision: None,
                    allow_system_prompt_replace: false,
                    mutates: None,
                    handler_type: Some("command".into()),
                    url: None,
                    hook_prompt: None,
                    agent_type: None,
                    enabled: true,
                    scope: if plugin.source.is_project() {
                        HookScope::Project
                    } else {
                        HookScope::User
                    },
                    plugin_origin: Some((
                        plugin.name.clone(),
                        plugin.version.clone().unwrap_or_default(),
                    )),
                    env: root_env(&plugin.root),
                });
            }
        }
    }
    out
}

/// Approved LSP servers of every active plugin, scoped `plugin:<p>:<s>`.
pub fn approved_lsp_servers(
    plugins: &PluginSet,
    trust: &PluginTrustStore,
) -> HashMap<String, LspServerConfig> {
    let mut out = HashMap::new();
    for plugin in plugins.active() {
        let mut map = Map::new();
        for c in plugin.exec.iter().filter(|c| c.kind == KIND_LSP_SERVER) {
            if trust.is_approved(plugin, c) {
                map.insert(c.name.clone(), c.definition.clone());
            }
        }
        if !map.is_empty() {
            out.extend(PluginLspServerLoader::configs_for_plugin(
                &Value::Object(map),
                &plugin.root,
                &plugin.name,
            ));
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::plugins::test_support::{plugin, write};
    use crate::plugins::{discover_plugins, PluginSettings};

    fn exec_of(root: &Path, manifest: &str) -> (Vec<ExecComponent>, Vec<String>) {
        write(&root.join(".claude-plugin/plugin.json"), manifest);
        let (m, _) = manifest::load(root).unwrap().unwrap();
        let mut errors = Vec::new();
        (exec_components(root, &m, &mut errors), errors)
    }

    #[test]
    fn inline_file_and_default_declarations_are_read() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path();
        write(
            &root.join("servers.json"),
            r#"{"mcpServers":{"b":{"command":"b"}}}"#,
        );
        write(&root.join("bare.json"), r#"{"c":{"command":"c"}}"#);
        let (exec, errors) = exec_of(root, r#"{"name":"p","mcpServers":{"a":{"command":"a"}}}"#);
        assert!(errors.is_empty());
        assert_eq!(
            exec.iter().map(|c| c.label()).collect::<Vec<_>>(),
            vec!["mcpServer:a"]
        );

        let (exec, _) = exec_of(root, r#"{"name":"p","mcpServers":"./servers.json"}"#);
        assert_eq!(exec[0].name, "b");
        let (exec, _) = exec_of(root, r#"{"name":"p","mcpServers":"bare.json"}"#);
        assert_eq!(exec[0].name, "c");

        write(
            &root.join(".mcp.json"),
            r#"{"mcpServers":{"d":{"command":"d"}}}"#,
        );
        write(
            &root.join("hooks/hooks.json"),
            r#"{"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"x"}]}]}}"#,
        );
        let (exec, _) = exec_of(root, r#"{"name":"p"}"#);
        let labels: Vec<String> = exec.iter().map(|c| c.label()).collect();
        assert_eq!(labels, vec!["mcpServer:d", "hook:hooks"]);

        let (exec, errors) = exec_of(root, r#"{"name":"p","mcpServers":"../escape.json"}"#);
        assert!(exec.iter().all(|c| c.kind != KIND_MCP_SERVER));
        assert!(errors[0].contains("escapes"), "{errors:?}");
    }

    fn one_plugin(manifest: &str) -> (tempfile::TempDir, tempfile::TempDir, PluginSet) {
        let ws = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let root = home.path().join("plugins/p");
        plugin(&root, "p");
        write(&root.join(".claude-plugin/plugin.json"), manifest);
        let set = discover_plugins(ws.path(), home.path(), &PluginSettings::default());
        (ws, home, set)
    }

    #[test]
    fn mcp_servers_need_approval_and_get_the_root_expanded() {
        let (_ws, home, set) = one_plugin(
            r#"{"name":"p","version":"1","mcpServers":{
                "s":{"command":"${CLAUDE_PLUGIN_ROOT}/srv","args":["${CODA_PLUGIN_ROOT}/cfg"]},
                "h":{"type":"http","url":"http://localhost/${CODA_PLUGIN_ROOT}"}}}"#,
        );
        let mut trust = PluginTrustStore::load(home.path());
        let (stdio, http) = approved_mcp_servers(&set, &trust);
        assert!(
            stdio.is_empty() && http.is_empty(),
            "nothing starts before approval"
        );
        let p = &set.plugins[0];
        for c in &p.exec {
            trust.approve(p, c);
        }
        let (stdio, http) = approved_mcp_servers(&set, &trust);
        let root = p.root.to_string_lossy().into_owned();
        assert_eq!(stdio[0].name, "plugin_p_s");
        assert_eq!(stdio[0].command, format!("{root}/srv"));
        assert_eq!(stdio[0].args, vec![format!("{root}/cfg")]);
        assert_eq!(stdio[0].env["CLAUDE_PLUGIN_ROOT"], root);
        assert_eq!(http[0].name, "plugin_p_h");
    }

    #[test]
    fn hooks_are_converted_and_disabled_until_approved() {
        let (_ws, home, set) = one_plugin(
            r#"{"name":"p","version":"2","hooks":{"hooks":{"SessionStart":[
                {"matcher":"startup","hooks":[{"type":"command","command":"${CLAUDE_PLUGIN_ROOT}/start.ps1","timeout":5}]}
            ]}}}"#,
        );
        let mut trust = PluginTrustStore::load(home.path());
        let hooks = plugin_hooks(&set, &trust);
        assert_eq!(hooks.len(), 1);
        let (h, approved) = &hooks[0];
        assert!(!approved && !h.enabled);
        let root = set.plugins[0].root.to_string_lossy().into_owned();
        assert_eq!(
            h.command.as_deref(),
            Some(format!("{root}/start.ps1").as_str())
        );
        assert_eq!(h.matcher.as_deref(), Some("startup"));
        assert_eq!(h.timeout_seconds, Some(5));
        assert_eq!(h.plugin_origin, Some(("p".into(), "2".into())));
        assert_eq!(h.scope, HookScope::User);
        assert_eq!(h.env["CODA_PLUGIN_ROOT"], root);

        let p = &set.plugins[0];
        trust.approve(p, &p.exec[0]);
        assert!(plugin_hooks(&set, &trust)[0].0.enabled);
    }

    #[test]
    fn lsp_servers_need_approval() {
        let (_ws, home, set) = one_plugin(
            r#"{"name":"p","lspServers":{"x":{"command":"${CODA_PLUGIN_ROOT}/ls","extensionToLanguage":{".x":"x"}}}}"#,
        );
        let mut trust = PluginTrustStore::load(home.path());
        assert!(approved_lsp_servers(&set, &trust).is_empty());
        let p = &set.plugins[0];
        trust.approve(p, &p.exec[0]);
        assert!(approved_lsp_servers(&set, &trust).contains_key("plugin:p:x"));
    }
}
