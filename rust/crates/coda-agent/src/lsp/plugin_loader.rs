//! `PluginLspServerLoader` — discovers and loads LSP server configurations from
//! plugin directories.
//!
//! Mirrors C# `Coda.Agent.Lsp.PluginLspServerLoader`.
//!
//! # Security
//! Plugin content is untrusted.  Guards:
//! - **Path containment**: string-path declarations in `lspServers` that resolve
//!   outside the plugin directory are silently rejected (path traversal prevention).
//! - **Absolute paths rejected**: an absolute path in a string declaration is rejected.
//! - **Pre-filtered callers**: `load_for_plugin_directories` is the form the
//!   composition root uses; callers are responsible for passing only the directories
//!   of plugins that have already been enabled and approved by the user.
//!   An LSP server starts a process, so that decision must happen before this point.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use serde_json::Value;

use super::config::LspServerConfig;

/// Loads LSP server configurations from plugin directories.
pub struct PluginLspServerLoader;

impl PluginLspServerLoader {
    /// Loads LSP servers from an explicit list of already-approved plugin directories.
    ///
    /// Callers pass the directories of plugins that have already cleared trust and
    /// approval gates.  This method does not re-check trust — that is the caller's job.
    pub fn load_for_plugin_directories<P: AsRef<Path>>(plugin_dirs: &[P]) -> HashMap<String, LspServerConfig> {
        let mut result = HashMap::new();
        for dir in plugin_dirs {
            let dir = dir.as_ref();
            let plugin_json = dir.join("plugin.json");
            if !plugin_json.is_file() {
                continue;
            }
            if let Err(_) = load_plugin(dir, &plugin_json, &mut result) {
                // tolerant: skip this plugin, continue with others
            }
        }
        result
    }

    /// Loads and scopes all LSP servers discovered by scanning the given base directories.
    ///
    /// Each entry under `base_dirs` is treated as a plugins base directory; subdirectories
    /// that contain a `plugin.json` are treated as plugin directories and loaded.
    ///
    /// Each server key is scoped as `plugin:<pluginName>:<serverName>`.
    /// Malformed or missing files are silently skipped.
    pub fn load<P: AsRef<Path>>(base_dirs: &[P]) -> HashMap<String, LspServerConfig> {
        let mut result = HashMap::new();
        for base in base_dirs {
            let base = base.as_ref();
            if !base.is_dir() {
                continue;
            }
            let entries = match std::fs::read_dir(base) {
                Ok(e) => e,
                Err(_) => continue,
            };
            for entry in entries.flatten() {
                let plugin_dir = entry.path();
                if !plugin_dir.is_dir() {
                    continue;
                }
                let plugin_json = plugin_dir.join("plugin.json");
                if !plugin_json.is_file() {
                    continue;
                }
                let _ = load_plugin(&plugin_dir, &plugin_json, &mut result);
                // tolerant: continue on error
            }
        }
        result
    }
}

// ── Private helpers ───────────────────────────────────────────────────────────

fn load_plugin(
    plugin_dir: &Path,
    plugin_json_path: &Path,
    result: &mut HashMap<String, LspServerConfig>,
) -> Result<(), ()> {
    let text = std::fs::read_to_string(plugin_json_path).map_err(|_| ())?;
    let root: Value = serde_json::from_str(&text).map_err(|_| ())?;
    let root_obj = root.as_object().ok_or(())?;

    // Determine plugin name: prefer "name" field, fall back to directory name.
    let dir_name = plugin_dir
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or("")
        .to_owned();
    let plugin_name = root_obj
        .get("name")
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .unwrap_or(&dir_name)
        .to_owned();
    let plugin_name = if plugin_name.is_empty() { dir_name.clone() } else { plugin_name };

    // Collect raw server map; .lsp.json first, then plugin.json lspServers (later wins).
    let mut servers: HashMap<String, LspServerConfig> = HashMap::new();

    // 1. .lsp.json file
    let lsp_json = plugin_dir.join(".lsp.json");
    if lsp_json.is_file() {
        load_server_map_from_file(&lsp_json, &mut servers);
    }

    // 2. plugin.json lspServers field
    if let Some(lsp_servers_node) = root_obj.get("lspServers") {
        load_from_declaration(lsp_servers_node, plugin_dir, &mut servers);
    }

    // Scope and resolve each collected server.
    for (server_name, config) in servers {
        let resolved = resolve_plugin_environment(config, plugin_dir);
        let scoped_key = format!("plugin:{plugin_name}:{server_name}");
        result.insert(scoped_key, resolved);
    }

    Ok(())
}

fn load_server_map_from_file(file: &Path, target: &mut HashMap<String, LspServerConfig>) {
    let text = match std::fs::read_to_string(file) {
        Ok(t) => t,
        Err(_) => return,
    };
    let node: Value = match serde_json::from_str(&text) {
        Ok(v) => v,
        Err(_) => return,
    };
    if let Some(obj) = node.as_object() {
        merge_server_map(&Value::Object(obj.clone()), target);
    }
}

fn load_from_declaration(
    declaration: &Value,
    plugin_dir: &Path,
    target: &mut HashMap<String, LspServerConfig>,
) {
    // Normalise to an iterable list.
    let items: Vec<&Value> = match declaration {
        Value::Array(arr) => arr.iter().collect(),
        other => vec![other],
    };

    for item in items {
        if item.is_null() {
            continue;
        }
        if let Some(rel_path) = item.as_str() {
            // String path — validate containment then load.
            if let Some(resolved) = validate_path_within_plugin(plugin_dir, rel_path) {
                load_server_map_from_file(&resolved, target);
            }
        } else if let Some(inline_obj) = item.as_object() {
            merge_server_map(&Value::Object(inline_obj.clone()), target);
        }
    }
}

fn merge_server_map(servers_value: &Value, target: &mut HashMap<String, LspServerConfig>) {
    let parsed = LspServerConfig::parse_map(servers_value);
    for (name, cfg) in parsed {
        target.insert(name, cfg);
    }
}

/// Expand `${CLAUDE_PLUGIN_ROOT}` and `${VAR}` patterns in a string value.
fn resolve_variables(value: &str, plugin_root: &Path) -> String {
    let plugin_root_str = plugin_root.to_string_lossy();

    // Replace ${CLAUDE_PLUGIN_ROOT} literally first.
    let result = value.replace("${CLAUDE_PLUGIN_ROOT}", plugin_root_str.as_ref());

    // Then replace remaining ${VAR} from environment.
    let mut out = String::with_capacity(result.len());
    let mut rest = result.as_str();
    while let Some(start) = rest.find("${") {
        out.push_str(&rest[..start]);
        let after = &rest[start + 2..];
        if let Some(end) = after.find('}') {
            let var_name = &after[..end];
            // CLAUDE_PLUGIN_ROOT was already handled above; if somehow present, use plugin_root.
            let replacement = if var_name == "CLAUDE_PLUGIN_ROOT" {
                plugin_root_str.to_string()
            } else {
                std::env::var(var_name).unwrap_or_default()
            };
            out.push_str(&replacement);
            rest = &after[end + 1..];
        } else {
            // Unclosed brace — keep as-is.
            out.push_str("${");
            rest = after;
        }
    }
    out.push_str(rest);
    out
}

fn resolve_plugin_environment(config: LspServerConfig, plugin_dir: &Path) -> LspServerConfig {
    let resolved_command = resolve_variables(&config.command, plugin_dir);
    let resolved_args: Vec<String> = config
        .args
        .iter()
        .map(|a| resolve_variables(a, plugin_dir))
        .collect();

    // Build env: expand existing env values, then inject CLAUDE_PLUGIN_ROOT.
    let mut resolved_env: HashMap<String, String> = config
        .env
        .iter()
        .map(|(k, v)| (k.clone(), resolve_variables(v, plugin_dir)))
        .collect();
    // CLAUDE_PLUGIN_ROOT is injected verbatim (not variable-expanded).
    resolved_env.insert(
        "CLAUDE_PLUGIN_ROOT".to_owned(),
        plugin_dir.to_string_lossy().into_owned(),
    );

    LspServerConfig {
        command: resolved_command,
        args: resolved_args,
        extension_to_language: config.extension_to_language,
        env: resolved_env,
        initialization_options: config.initialization_options,
        startup_timeout_ms: config.startup_timeout_ms,
    }
}

/// Validates that `relative_path` stays inside `plugin_dir`.
///
/// Returns `None` for absolute paths or paths that escape the plugin directory
/// (path traversal prevention).
///
/// # Security
/// Both sides receive a trailing separator before the prefix comparison so that
/// a plugin dir of `/a` does not contain the sibling `/a-evil`.
fn validate_path_within_plugin(plugin_dir: &Path, relative_path: &str) -> Option<PathBuf> {
    // Reject absolute paths.
    if Path::new(relative_path).is_absolute() {
        return None;
    }

    let resolved_plugin = match plugin_dir.canonicalize() {
        Ok(p) => p,
        Err(_) => plugin_dir.to_path_buf(),
    };
    let resolved_file = resolved_plugin.join(relative_path);
    // Normalize without requiring the file to exist.
    let resolved_file = normalize_path(&resolved_file);

    let plugin_with_sep = {
        let mut p = resolved_plugin.to_string_lossy().into_owned();
        if !p.ends_with(std::path::MAIN_SEPARATOR) {
            p.push(std::path::MAIN_SEPARATOR);
        }
        p.to_lowercase()
    };
    let file_str = resolved_file.to_string_lossy().to_lowercase();

    if file_str.starts_with(&plugin_with_sep) {
        Some(resolved_file)
    } else {
        None
    }
}

/// Normalize a path lexically (resolve `.` and `..` without touching the filesystem).
fn normalize_path(path: &Path) -> PathBuf {
    let mut components = Vec::new();
    for component in path.components() {
        use std::path::Component::*;
        match component {
            CurDir => {}
            ParentDir => {
                if matches!(components.last(), Some(Normal(_))) {
                    components.pop();
                } else {
                    components.push(component);
                }
            }
            c => components.push(c),
        }
    }
    components.iter().collect()
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    struct TempDir(PathBuf);

    impl TempDir {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "coda-agent-lsp-plugin-tests-{}",
                uuid::Uuid::new_v4()
            ));
            std::fs::create_dir_all(&path).unwrap();
            Self(path)
        }
        fn path(&self) -> &Path { &self.0 }
        fn create_plugin(&self, name: &str, content: &str) -> PathBuf {
            let dir = self.path().join(name);
            std::fs::create_dir_all(&dir).unwrap();
            std::fs::write(dir.join("plugin.json"), content).unwrap();
            dir
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn server_json(cmd: &str, ext: &str, lang: &str) -> String {
        format!(
            r#"{{"command":"{cmd}","extensionToLanguage":{{"{ext}":"{lang}"}}}}"#
        )
    }

    #[test]
    fn inline_lsp_servers_in_plugin_json_loaded_and_scoped() {
        let base = TempDir::new();
        base.create_plugin("myplugin", &format!(
            r#"{{"name":"myplugin","lspServers":{{"ts":{}}}}}"#,
            server_json("tsls", ".ts", "typescript")
        ));

        let result = PluginLspServerLoader::load(&[base.path()]);

        assert!(result.contains_key("plugin:myplugin:ts"), "expected plugin:myplugin:ts");
        assert_eq!(result["plugin:myplugin:ts"].command, "tsls");
    }

    #[test]
    fn lsp_json_file_loaded() {
        let base = TempDir::new();
        let dir = base.create_plugin("pyplugin", r#"{"name":"pyplugin"}"#);
        std::fs::write(
            dir.join(".lsp.json"),
            r#"{"py":{"command":"pyls","extensionToLanguage":{".py":"python"}}}"#,
        ).unwrap();

        let result = PluginLspServerLoader::load(&[base.path()]);

        assert!(result.contains_key("plugin:pyplugin:py"), "expected plugin:pyplugin:py");
        assert_eq!(result["plugin:pyplugin:py"].command, "pyls");
    }

    #[test]
    fn string_path_declaration_loaded() {
        let base = TempDir::new();
        let dir = base.create_plugin("strplugin", r#"{"name":"strplugin","lspServers":"servers.json"}"#);
        std::fs::write(
            dir.join("servers.json"),
            r#"{"rb":{"command":"rubocop","extensionToLanguage":{".rb":"ruby"}}}"#,
        ).unwrap();

        let result = PluginLspServerLoader::load(&[base.path()]);

        assert!(result.contains_key("plugin:strplugin:rb"), "expected plugin:strplugin:rb");
    }

    /// SECURITY: path traversal in string declarations must be rejected.
    #[test]
    fn path_traversal_declaration_rejected() {
        let base = TempDir::new();
        // escape file sits outside the plugin dir
        let escape = base.path().join("escape.json");
        std::fs::write(&escape, r#"{"evil":{"command":"evil","extensionToLanguage":{".x":"x"}}}"#).unwrap();

        // plugin.json references ../escape.json (traversal)
        base.create_plugin("travplugin", r#"{"name":"travplugin","lspServers":"../escape.json"}"#);

        let result = PluginLspServerLoader::load(&[base.path()]);

        // Must not contain the traversal server
        assert!(
            !result.keys().any(|k| k.contains("evil")),
            "path traversal must be rejected; got keys: {:?}",
            result.keys().collect::<Vec<_>>()
        );
    }

    #[test]
    fn claude_plugin_root_resolved_in_command_and_injected_into_env() {
        let base = TempDir::new();
        let plugin_dir = base.create_plugin("rootplugin", &format!(
            r#"{{"name":"rootplugin","lspServers":{{"x":{{"command":"${{CLAUDE_PLUGIN_ROOT}}/bin/ls","args":["--flag","${{CLAUDE_PLUGIN_ROOT}}"],"extensionToLanguage":{{".x":"x"}}}}}}}}"#
        ));

        let result = PluginLspServerLoader::load(&[base.path()]);

        assert!(result.contains_key("plugin:rootplugin:x"), "expected plugin:rootplugin:x");
        let cfg = &result["plugin:rootplugin:x"];

        let expected_root = plugin_dir.to_string_lossy().to_lowercase();
        assert!(
            cfg.command.to_lowercase().starts_with(&expected_root),
            "command must start with plugin dir, got: {}",
            cfg.command
        );
        assert_eq!(cfg.args.len(), 2, "args must have 2 elements");
        assert_eq!(
            cfg.args[1].to_lowercase(),
            plugin_dir.to_string_lossy().to_lowercase()
        );
        assert!(cfg.env.contains_key("CLAUDE_PLUGIN_ROOT"), "CLAUDE_PLUGIN_ROOT must be injected");
        assert_eq!(
            cfg.env["CLAUDE_PLUGIN_ROOT"].to_lowercase(),
            plugin_dir.to_string_lossy().to_lowercase()
        );
    }

    #[test]
    fn malformed_plugin_json_skipped_others_kept() {
        let base = TempDir::new();
        // Bad plugin: garbage plugin.json
        let bad = base.path().join("badplugin");
        std::fs::create_dir_all(&bad).unwrap();
        std::fs::write(bad.join("plugin.json"), "NOT JSON {{{{").unwrap();

        // Good plugin
        base.create_plugin("goodplugin", &format!(
            r#"{{"name":"goodplugin","lspServers":{{"go":{}}}}}"#,
            server_json("gopls", ".go", "go")
        ));

        let result = PluginLspServerLoader::load(&[base.path()]);

        assert!(result.contains_key("plugin:goodplugin:go"), "valid plugin must be loaded");
        assert!(
            !result.keys().any(|k| k.starts_with("plugin:badplugin:")),
            "malformed plugin must be skipped"
        );
    }

    #[test]
    fn two_plugins_both_namespaced() {
        let base = TempDir::new();
        base.create_plugin("alpha", &format!(
            r#"{{"name":"alpha","lspServers":{{"a":{}}}}}"#,
            server_json("acmd", ".a", "alang")
        ));
        base.create_plugin("beta", &format!(
            r#"{{"name":"beta","lspServers":{{"b":{}}}}}"#,
            server_json("bcmd", ".b", "blang")
        ));

        let result = PluginLspServerLoader::load(&[base.path()]);

        assert!(result.contains_key("plugin:alpha:a"), "expected plugin:alpha:a");
        assert!(result.contains_key("plugin:beta:b"), "expected plugin:beta:b");
    }

    #[test]
    fn no_plugin_dirs_returns_empty() {
        let result = PluginLspServerLoader::load::<&Path>(&[Path::new("/no/such/dir/9999")]);
        assert!(result.is_empty());
    }

    #[test]
    fn array_declaration_mixed() {
        let base = TempDir::new();
        let dir = base.create_plugin("arrayplugin", &format!(
            r#"{{"name":"arrayplugin","lspServers":[{{"a":{}}}, "more.json"]}}"#,
            r#"{"command":"acmd","extensionToLanguage":{".a":"alang"}}"#
        ));
        std::fs::write(
            dir.join("more.json"),
            r#"{"b":{"command":"bcmd","extensionToLanguage":{".b":"blang"}}}"#,
        ).unwrap();

        let result = PluginLspServerLoader::load(&[base.path()]);

        assert!(result.contains_key("plugin:arrayplugin:a"), "expected a from inline");
        assert!(result.contains_key("plugin:arrayplugin:b"), "expected b from more.json");
    }
}
