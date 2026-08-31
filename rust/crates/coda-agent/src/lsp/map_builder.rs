//! `LspServerMapBuilder` — merges LSP server configurations from plugin directories
//! and explicit settings into a unified server map.
//!
//! Mirrors C# `Coda.Agent.Lsp.LspServerMapBuilder`.
//!
//! # Precedence
//! Settings entries **win** on exact-key clashes.
//! Plugin keys are namespaced (`plugin:<name>:<server>`) so real clashes are rare.

use std::collections::HashMap;

use super::config::LspServerConfig;

/// Merges LSP server configurations from plugins and settings.
pub struct LspServerMapBuilder;

impl LspServerMapBuilder {
    /// Builds the merged LSP server map.
    ///
    /// Plugin servers form the base; settings servers are overlaid on top
    /// (settings win on any exact-key clash).
    ///
    /// `plugin_servers` must already be filtered to plugins the user enabled and
    /// approved — this method does not re-check trust.
    pub fn build(
        settings_servers: &HashMap<String, LspServerConfig>,
        plugin_servers: &HashMap<String, LspServerConfig>,
    ) -> HashMap<String, LspServerConfig> {
        let mut merged: HashMap<String, LspServerConfig> = plugin_servers.clone();
        for (name, config) in settings_servers {
            merged.insert(name.clone(), config.clone());
        }
        merged
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::lsp::plugin_loader::PluginLspServerLoader;
    use std::path::{Path, PathBuf};

    struct TempDir(PathBuf);

    impl TempDir {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "coda-agent-lsp-map-tests-{}",
                uuid::Uuid::new_v4()
            ));
            std::fs::create_dir_all(&path).unwrap();
            Self(path)
        }
        fn path(&self) -> &Path { &self.0 }
        fn create_plugin(&self, name: &str, cmd: &str) -> PathBuf {
            let dir = self.path().join(name);
            std::fs::create_dir_all(&dir).unwrap();
            let content = format!(
                r#"{{"name":"{name}","lspServers":{{"py":{{"command":"{cmd}","extensionToLanguage":{{".py":"python"}}}}}}}}"#
            );
            std::fs::write(dir.join("plugin.json"), content).unwrap();
            dir
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn make_config(command: &str) -> LspServerConfig {
        LspServerConfig {
            command: command.to_owned(),
            args: vec![],
            extension_to_language: [(".ts".to_owned(), "typescript".to_owned())].into(),
            env: HashMap::new(),
            initialization_options: None,
            startup_timeout_ms: None,
        }
    }

    #[test]
    fn merges_plugin_and_settings_servers() {
        let base = TempDir::new();
        base.create_plugin("myplugin", "pylsp");
        let settings = [("ts".to_owned(), make_config("tsls"))].into_iter().collect();
        let plugins = PluginLspServerLoader::load(&[base.path()]);

        let result = LspServerMapBuilder::build(&settings, &plugins);

        assert!(result.contains_key("ts"), "ts from settings must be present");
        assert!(result.contains_key("plugin:myplugin:py"), "plugin:myplugin:py must be present");
        assert_eq!(result.len(), 2);
    }

    #[test]
    fn settings_wins_on_exact_key_clash() {
        let base = TempDir::new();
        base.create_plugin("myplugin", "pylsp-from-plugin");
        let plugins = PluginLspServerLoader::load(&[base.path()]);

        // Force the same scoped key in settings to cause a clash.
        let settings = [("plugin:myplugin:py".to_owned(), make_config("pylsp-from-settings"))]
            .into_iter()
            .collect();

        let result = LspServerMapBuilder::build(&settings, &plugins);

        assert_eq!(
            result["plugin:myplugin:py"].command,
            "pylsp-from-settings",
            "settings must win on a clash"
        );
    }

    #[test]
    fn no_plugins_returns_only_settings() {
        let settings = [("ts".to_owned(), make_config("tsls"))].into_iter().collect();
        let result = LspServerMapBuilder::build(&settings, &HashMap::new());

        assert_eq!(result.len(), 1);
        assert!(result.contains_key("ts"));
    }

    #[test]
    fn no_settings_returns_only_plugins() {
        let base = TempDir::new();
        base.create_plugin("myplugin", "pylsp");
        let plugins = PluginLspServerLoader::load(&[base.path()]);

        let result = LspServerMapBuilder::build(&HashMap::new(), &plugins);

        assert_eq!(result.len(), 1);
        assert!(result.contains_key("plugin:myplugin:py"));
    }
}
