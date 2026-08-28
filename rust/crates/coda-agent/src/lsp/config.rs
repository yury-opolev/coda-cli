//! LSP server configuration, parsed from `settings.json`'s `lspServers` map.

use std::collections::HashMap;

use serde_json::Value;

/// Configuration for a single LSP server, read from `settings.json`.
#[derive(Debug, Clone)]
pub struct LspServerConfig {
    pub command: String,
    pub args: Vec<String>,
    /// `".rs" → "rust"` (extensions normalized to lowercase with leading dot).
    pub extension_to_language: HashMap<String, String>,
    pub env: HashMap<String, String>,
    pub initialization_options: Option<Value>,
    pub startup_timeout_ms: Option<u64>,
}

impl LspServerConfig {
    /// Parse one server entry from a JSON object.
    ///
    /// Returns `None` when any required field is missing or invalid.
    /// Mirrors `LspServerConfigParser.ParseEntry` from C#.
    pub fn parse(obj: &Value) -> Option<Self> {
        let command = obj.get("command").and_then(Value::as_str)?;
        if command.trim().is_empty() {
            return None;
        }
        // transport: only stdio (or absent) supported
        let transport = obj.get("transport").and_then(Value::as_str);
        if transport.is_some_and(|t| !t.eq_ignore_ascii_case("stdio")) {
            return None;
        }

        let ext_map_obj = obj.get("extensionToLanguage").and_then(Value::as_object)?;
        if ext_map_obj.is_empty() {
            return None;
        }
        let mut extension_to_language = HashMap::new();
        for (raw_ext, lang_val) in ext_map_obj {
            let lang = lang_val.as_str().filter(|s| !s.is_empty())?;
            let normalized = normalize_extension(raw_ext);
            extension_to_language.insert(normalized, lang.to_string());
        }
        if extension_to_language.is_empty() {
            return None;
        }

        let args = obj
            .get("args")
            .and_then(Value::as_array)
            .map(|a| a.iter().filter_map(Value::as_str).map(str::to_string).collect())
            .unwrap_or_default();

        let env = obj
            .get("env")
            .and_then(Value::as_object)
            .map(|e| {
                e.iter()
                    .filter_map(|(k, v)| v.as_str().map(|s| (k.clone(), s.to_string())))
                    .collect()
            })
            .unwrap_or_default();

        let initialization_options = obj.get("initializationOptions").cloned();

        let startup_timeout_ms = obj
            .get("startupTimeoutMs")
            .and_then(Value::as_u64);

        Some(Self {
            command: command.to_string(),
            args,
            extension_to_language,
            env,
            initialization_options,
            startup_timeout_ms,
        })
    }

    /// Parse a `lspServers` JSON object (name → config entries).
    pub fn parse_map(servers_obj: &Value) -> HashMap<String, Self> {
        let Some(obj) = servers_obj.as_object() else { return HashMap::new() };
        obj.iter()
            .filter_map(|(name, val)| Self::parse(val).map(|c| (name.clone(), c)))
            .collect()
    }
}

fn normalize_extension(ext: &str) -> String {
    let lower = ext.to_lowercase();
    if lower.starts_with('.') { lower } else { format!(".{lower}") }
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn parse_minimal_valid_entry() {
        let v = json!({
            "command": "rust-analyzer",
            "extensionToLanguage": { ".rs": "rust" }
        });
        let c = LspServerConfig::parse(&v).expect("should parse");
        assert_eq!(c.command, "rust-analyzer");
        assert_eq!(c.extension_to_language.get(".rs").map(String::as_str), Some("rust"));
    }

    #[test]
    fn parse_normalizes_extension_without_dot() {
        let v = json!({
            "command": "tsserver",
            "extensionToLanguage": { "ts": "typescript" }
        });
        let c = LspServerConfig::parse(&v).unwrap();
        assert!(c.extension_to_language.contains_key(".ts"));
    }

    #[test]
    fn parse_returns_none_for_missing_command() {
        let v = json!({ "extensionToLanguage": { ".rs": "rust" } });
        assert!(LspServerConfig::parse(&v).is_none());
    }

    #[test]
    fn parse_returns_none_for_empty_extension_map() {
        let v = json!({ "command": "server", "extensionToLanguage": {} });
        assert!(LspServerConfig::parse(&v).is_none());
    }

    #[test]
    fn parse_skips_non_stdio_transport() {
        let v = json!({
            "command": "server",
            "transport": "tcp",
            "extensionToLanguage": { ".rs": "rust" }
        });
        assert!(LspServerConfig::parse(&v).is_none());
    }

    #[test]
    fn parse_reads_optional_fields() {
        let v = json!({
            "command": "server",
            "extensionToLanguage": { ".rs": "rust" },
            "args": ["--stdio"],
            "env": { "RUST_LOG": "info" },
            "startupTimeoutMs": 10000,
            "initializationOptions": { "checkOnSave": true }
        });
        let c = LspServerConfig::parse(&v).unwrap();
        assert_eq!(c.args, vec!["--stdio"]);
        assert_eq!(c.env.get("RUST_LOG").map(String::as_str), Some("info"));
        assert_eq!(c.startup_timeout_ms, Some(10000));
        assert!(c.initialization_options.is_some());
    }
}
