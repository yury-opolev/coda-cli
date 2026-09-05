//! `.mcp.json` configuration loading.
//!
//! Two files are merged in increasing precedence order:
//! - User: `~/.coda/.mcp.json` (lowest)
//! - Project: `<cwd>/.mcp.json` (highest, shadows user)
//!
//! `coda-tui/src/config.rs` delegates to this module so the JSON parsing
//! logic lives in exactly one place. The TUI adapts `McpRawServer` into its
//! own display type; this module retains the actual `env` values needed to
//! launch the servers.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use serde_json::Value;

/// Where a server definition was loaded from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum McpScope {
    User,
    Project,
}

impl McpScope {
    pub fn label(self) -> &'static str {
        match self {
            McpScope::User => "user",
            McpScope::Project => "project",
        }
    }
}

/// A raw MCP server entry as read directly from `.mcp.json`.
///
/// This type carries all fields, including sensitive `env` values, so callers
/// that only need display info should project out what they need rather than
/// storing the whole struct.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpRawServer {
    pub name: String,
    pub scope: McpScope,
    /// `Some` for stdio servers; `None` for HTTP servers.
    pub command: Option<String>,
    pub args: Vec<String>,
    /// Actual environment variable values (may contain secrets).
    pub env: HashMap<String, String>,
    /// `Some` for HTTP servers; `None` for stdio servers.
    pub url: Option<String>,
    /// `true` when `"disabled": true` is set in the file.
    pub disabled: bool,
}

impl McpRawServer {
    /// A one-line human-readable description of where this server is reached.
    pub fn target(&self) -> String {
        match (&self.command, &self.url) {
            (Some(cmd), _) if self.args.is_empty() => cmd.clone(),
            (Some(cmd), _) => format!("{cmd} {}", self.args.join(" ")),
            (None, Some(url)) => url.clone(),
            (None, None) => String::new(),
        }
    }

    /// `"stdio"` or `"http"` based on which fields are populated.
    pub fn transport(&self) -> &'static str {
        if self.command.is_some() { "stdio" } else { "http" }
    }
}

/// A server that the manager can actually connect to: it is an enabled stdio
/// server with a non-empty command.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpConnectable {
    pub name: String,
    pub command: String,
    pub args: Vec<String>,
    pub env: HashMap<String, String>,
}

/// An enabled HTTP MCP server that the manager can connect to.
#[derive(Debug, Clone)]
pub struct McpHttpConnectable {
    pub name: String,
    pub url: String,
    /// Static headers sent on every request.
    pub headers: HashMap<String, String>,
    /// Authentication configuration.
    pub auth: crate::auth::types::McpAuthConfig,
}

// ── Public API ────────────────────────────────────────────────────────────────

/// Load all servers from both files, project shadowing user, including
/// disabled and HTTP servers. Intended for `/mcp list` display.
///
/// Returns servers sorted by scope (user first) then name. Files that do not
/// exist are silently treated as empty.
pub fn load_all(user_mcp: &Path, project_mcp: &Path) -> Vec<McpRawServer> {
    let user = load_file(user_mcp, McpScope::User);
    let project = load_file(project_mcp, McpScope::Project);

    // Project shadows user by name.
    let mut result: Vec<McpRawServer> = user
        .into_iter()
        .filter(|u| !project.iter().any(|p| p.name == u.name))
        .collect();
    result.extend(project);
    result
}

/// Load only enabled stdio servers, project shadowing user.
///
/// Used by `McpClientManager` to decide which servers to start.
pub fn load_connectable(user_mcp: &Path, project_mcp: &Path) -> Vec<McpConnectable> {
    load_all(user_mcp, project_mcp)
        .into_iter()
        .filter(|s| !s.disabled)
        .filter_map(|s| {
            s.command.map(|cmd| McpConnectable {
                name: s.name,
                command: cmd,
                args: s.args,
                env: s.env,
            })
        })
        .collect()
}

/// Like [`load_connectable`] but also returns config error notices for files
/// that exist but are unreadable or malformed.
///
/// - Missing files → silently empty (healthy)
/// - Unreadable or invalid-JSON files → included notice, servers still connect
/// - Valid files → notices empty
///
/// Used by the engine at startup so users learn about broken configs without
/// having to compare the config file and the server list themselves.
pub fn load_connectable_checked(
    user_mcp: &Path,
    project_mcp: &Path,
) -> (Vec<McpConnectable>, Vec<String>) {
    let (user_servers, user_notice) = load_file_checked(user_mcp, McpScope::User);
    let (project_servers, project_notice) = load_file_checked(project_mcp, McpScope::Project);

    let mut notices = Vec::new();
    if let Some(n) = user_notice {
        notices.push(n);
    }
    if let Some(n) = project_notice {
        notices.push(n);
    }

    // Project shadows user by name (same logic as load_all).
    let mut merged: Vec<McpConnectable> = user_servers
        .into_iter()
        .filter(|u| !project_servers.iter().any(|p| p.name == u.name))
        .filter(|s| !s.disabled)
        .filter_map(|s| s.command.map(|cmd| McpConnectable { name: s.name, command: cmd, args: s.args, env: s.env }))
        .collect();
    let project_connectable: Vec<McpConnectable> = project_servers
        .into_iter()
        .filter(|s| !s.disabled)
        .filter_map(|s| s.command.map(|cmd| McpConnectable { name: s.name, command: cmd, args: s.args, env: s.env }))
        .collect();
    merged.extend(project_connectable);

    (merged, notices)
}

/// Load only enabled HTTP servers, project shadowing user.
///
/// Used by `McpClientManager` to connect HTTP MCP servers at startup.
pub fn load_http_connectable(user_mcp: &Path, project_mcp: &Path) -> Vec<McpHttpConnectable> {
    load_http_all(user_mcp, project_mcp)
        .into_iter()
        .filter(|s| !s.disabled)
        .map(|s| McpHttpConnectable {
            name: s.name,
            url: s.url,
            headers: s.headers,
            auth: s.auth,
        })
        .collect()
}

/// Like [`load_http_connectable`] but also returns config error notices for
/// files that exist but are unreadable or malformed.
pub fn load_http_connectable_checked(
    user_mcp: &Path,
    project_mcp: &Path,
) -> (Vec<McpHttpConnectable>, Vec<String>) {
    let (user_http, user_notice) = load_http_file_checked(user_mcp);
    let (project_http, project_notice) = load_http_file_checked(project_mcp);

    let mut notices = Vec::new();
    if let Some(n) = user_notice {
        notices.push(n);
    }
    if let Some(n) = project_notice {
        notices.push(n);
    }

    let mut merged: Vec<McpHttpConnectable> = user_http
        .into_iter()
        .filter(|u| !project_http.iter().any(|p| p.name == u.name))
        .filter(|s| !s.disabled)
        .map(|s| McpHttpConnectable { name: s.name, url: s.url, headers: s.headers, auth: s.auth })
        .collect();
    let project_connectables: Vec<McpHttpConnectable> = project_http
        .into_iter()
        .filter(|s| !s.disabled)
        .map(|s| McpHttpConnectable { name: s.name, url: s.url, headers: s.headers, auth: s.auth })
        .collect();
    merged.extend(project_connectables);

    (merged, notices)
}

// ── Internal HTTP server storage ──────────────────────────────────────────────

/// All HTTP server definitions, including disabled ones (for /mcp list).
fn load_http_all(user_mcp: &Path, project_mcp: &Path) -> Vec<RawHttpEntry> {
    let user = load_http_file(user_mcp);
    let project = load_http_file(project_mcp);

    let mut result: Vec<RawHttpEntry> = user
        .into_iter()
        .filter(|u| !project.iter().any(|p| p.name == u.name))
        .collect();
    result.extend(project);
    result
}

/// A parsed HTTP server entry (internal to config loading).
struct RawHttpEntry {
    name: String,
    url: String,
    headers: HashMap<String, String>,
    auth: crate::auth::types::McpAuthConfig,
    disabled: bool,
}

fn load_http_file(path: &Path) -> Vec<RawHttpEntry> {
    load_http_file_checked(path).0
}

/// Like [`load_http_file`] but returns a config error notice when the file
/// exists but is unreadable or malformed.
fn load_http_file_checked(path: &Path) -> (Vec<RawHttpEntry>, Option<String>) {
    let text = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return (Vec::new(), None),
        Err(e) => {
            return (
                Vec::new(),
                Some(format!("MCP config '{}' could not be read: {e}", path.display())),
            );
        }
    };
    let doc: Value = match serde_json::from_str(&text) {
        Ok(v) => v,
        Err(e) => {
            return (
                Vec::new(),
                Some(format!("MCP config '{}' contains invalid JSON: {e}", path.display())),
            );
        }
    };
    let servers = match doc.get("mcpServers") {
        None => return (Vec::new(), None),
        Some(v) => match v.as_object() {
            None => {
                return (
                    Vec::new(),
                    Some(format!(
                        "MCP config '{}': 'mcpServers' must be an object",
                        path.display()
                    )),
                );
            }
            Some(s) => s,
        },
    };
    let entries = servers
        .iter()
        .filter_map(|(name, def)| parse_http_entry(name, def))
        .collect();
    (entries, None)
}

fn parse_http_entry(name: &str, def: &Value) -> Option<RawHttpEntry> {
    let type_ = def.get("type").and_then(Value::as_str);
    let is_http = matches!(type_, Some("http") | Some("streamable-http"));
    // Also accept URL-only entries with no command as HTTP.
    let has_command = def.get("command").is_some();
    let has_url = def.get("url").is_some();
    if !is_http && (has_command || !has_url) {
        return None;
    }

    let url = def.get("url").and_then(Value::as_str)?.to_owned();
    let disabled_flag = def.get("disabled").and_then(Value::as_bool).unwrap_or(false);
    let enabled_flag = def.get("enabled").and_then(Value::as_bool).unwrap_or(true);
    let disabled = disabled_flag || !enabled_flag;

    Some(RawHttpEntry {
        name: name.to_owned(),
        url,
        headers: string_map(def, "headers"),
        auth: parse_auth_config(def),
        disabled,
    })
}

impl From<RawHttpEntry> for McpHttpConnectable {
    fn from(e: RawHttpEntry) -> Self {
        McpHttpConnectable { name: e.name, url: e.url, headers: e.headers, auth: e.auth }
    }
}

/// Parse the `auth` block of an HTTP server entry.
pub(crate) fn parse_auth_config(def: &Value) -> crate::auth::types::McpAuthConfig {
    use crate::auth::types::{McpAuthConfig, McpAuthMode};
    use coda_auth::secret::Secret;

    let Some(auth) = def.get("auth") else {
        return McpAuthConfig::oauth_default();
    };
    if !auth.is_object() {
        return McpAuthConfig::oauth_default();
    }

    let mode = auth.get("mode").and_then(Value::as_str);
    let parsed_mode = match mode.map(|m| m.to_ascii_lowercase()).as_deref() {
        Some("none") => McpAuthMode::None,
        Some("bearer") => McpAuthMode::Bearer,
        _ => McpAuthMode::OAuth,
    };

    let client_id = auth
        .get("clientId")
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .map(str::to_owned);

    let scopes = auth
        .get("scopes")
        .and_then(Value::as_array)
        .map(|arr| {
            arr.iter()
                .filter_map(Value::as_str)
                .map(str::to_owned)
                .collect()
        })
        .unwrap_or_default();

    let bearer_token = auth
        .get("token")
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .map(|s| Secret::new(s.to_owned()));

    McpAuthConfig { mode: parsed_mode, client_id, scopes, bearer_token }
}

/// Resolve the standard `.mcp.json` paths from a project root.
///
/// `CODA_USER_MCP_DIR` overrides the user directory; otherwise `~/.coda` is
/// used. This mirrors the C# `McpConfig.FilePath` resolution.
pub fn resolve_paths(project_root: &Path) -> (PathBuf, PathBuf) {
    let user_dir = std::env::var("CODA_USER_MCP_DIR")
        .ok()
        .filter(|v| !v.is_empty())
        .map(PathBuf::from)
        .or_else(|| {
            directories::BaseDirs::new().map(|b| b.home_dir().join(".coda"))
        })
        .unwrap_or_else(|| PathBuf::from(".coda"));

    (user_dir.join(".mcp.json"), project_root.join(".mcp.json"))
}

// ── Private helpers ───────────────────────────────────────────────────────────

/// Parse the `mcpServers` map from a `.mcp.json` file. Non-existent files and
/// parse errors are silently treated as empty (matching C# `McpConfig.Parse`).
fn load_file(path: &Path, scope: McpScope) -> Vec<McpRawServer> {
    load_file_checked(path, scope).0
}

/// Parse a `.mcp.json` file, distinguishing absent files from malformed ones.
///
/// - File absent → `(empty, None)` — healthy
/// - File exists but unreadable → `(empty, Some(notice))`
/// - File exists but invalid JSON → `(empty, Some(notice))`
/// - `mcpServers` key present but not an object → `(empty, Some(notice))`
/// - Valid file → `(entries, None)`
fn load_file_checked(path: &Path, scope: McpScope) -> (Vec<McpRawServer>, Option<String>) {
    let text = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return (Vec::new(), None),
        Err(e) => {
            return (
                Vec::new(),
                Some(format!("MCP config '{}' could not be read: {e}", path.display())),
            );
        }
    };
    let doc: Value = match serde_json::from_str(&text) {
        Ok(v) => v,
        Err(e) => {
            return (
                Vec::new(),
                Some(format!("MCP config '{}' contains invalid JSON: {e}", path.display())),
            );
        }
    };
    match doc.get("mcpServers") {
        None => (Vec::new(), None),
        Some(v) => match v.as_object() {
            None => (
                Vec::new(),
                Some(format!(
                    "MCP config '{}': 'mcpServers' must be an object",
                    path.display(),
                )),
            ),
            Some(servers) => {
                let entries = servers
                    .iter()
                    .filter_map(|(name, def)| parse_entry(name, def, scope))
                    .collect();
                (entries, None)
            }
        },
    }
}

/// Parse one server definition from a JSON value into a `McpRawServer`.
///
/// Returns `None` when the entry cannot be used (e.g. unknown transport type
/// or a stdio entry with no command).
pub fn parse_entry(name: &str, def: &Value, scope: McpScope) -> Option<McpRawServer> {
    let type_ = def.get("type").and_then(Value::as_str);
    // Support both "disabled": true (C# convention) and "enabled": false
    // (legacy TUI convention) so configs written by either client work.
    let disabled_flag = def.get("disabled").and_then(Value::as_bool).unwrap_or(false);
    let enabled_flag = def.get("enabled").and_then(Value::as_bool).unwrap_or(true);
    let disabled = disabled_flag || !enabled_flag;

    match type_ {
        None | Some("stdio") => {
            let command = def.get("command").and_then(Value::as_str).map(str::to_string);
            let url = def.get("url").and_then(Value::as_str).map(str::to_string);

            // No type specified: infer transport from content. A URL-only
            // entry (no command) is treated as HTTP for display purposes
            // even without "type": "http", matching the TUI's convention.
            if command.is_none() {
                if let Some(url) = url {
                    return Some(McpRawServer {
                        name: name.to_string(),
                        scope,
                        command: None,
                        args: Vec::new(),
                        env: HashMap::new(),
                        url: Some(url),
                        disabled,
                    });
                }
                // A stdio entry without a command or URL is invalid.
                return None;
            }

            let args = def
                .get("args")
                .and_then(Value::as_array)
                .map(|a| a.iter().filter_map(Value::as_str).map(str::to_string).collect())
                .unwrap_or_default();
            let env = string_map(def, "env");
            Some(McpRawServer {
                name: name.to_string(),
                scope,
                command,
                args,
                env,
                url: None,
                disabled,
            })
        }
        Some("http") | Some("streamable-http") => {
            let url = def.get("url").and_then(Value::as_str).map(str::to_string);
            Some(McpRawServer {
                name: name.to_string(),
                scope,
                command: None,
                args: Vec::new(),
                env: HashMap::new(),
                url,
                disabled,
            })
        }
        _ => None, // Unknown transport (e.g. legacy "sse") → skip
    }
}

fn string_map(def: &Value, key: &str) -> HashMap<String, String> {
    def.get(key)
        .and_then(Value::as_object)
        .map(|obj| {
            obj.iter()
                .filter_map(|(k, v)| v.as_str().map(|s| (k.clone(), s.to_string())))
                .collect()
        })
        .unwrap_or_default()
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    struct TempDir(PathBuf);

    /// Guarantees a distinct directory per instance.
    ///
    /// Neither the wall clock nor `subsec_nanos` is sufficient on its own:
    /// Windows advances the clock on a ~15 ms tick and `subsec_nanos` wraps
    /// every second, so parallel tests can land on the same path — and
    /// whichever finishes first deletes the directory the other is using.
    static TEMP_DIR_COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

    impl TempDir {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "coda-mcp-config-test-{}-{}-{}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap_or_default()
                    .as_nanos(),
                TEMP_DIR_COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
            ));
            std::fs::create_dir_all(&path).expect("temp dir");
            Self(path)
        }

        fn path(&self) -> &Path {
            &self.0
        }

        fn write(&self, name: &str, content: &str) -> PathBuf {
            let p = self.0.join(name);
            std::fs::write(&p, content).expect("write");
            p
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn load_all_reads_stdio_server() {
        let dir = TempDir::new();
        let user = dir.write(
            "user.mcp.json",
            r#"{"mcpServers":{"my-server":{"command":"npx","args":["my-pkg"],"env":{"TOKEN":"abc"}}}}"#,
        );
        let project = dir.write("project.mcp.json", "{}");

        let servers = load_all(&user, &project);
        assert_eq!(servers.len(), 1);
        let s = &servers[0];
        assert_eq!(s.name, "my-server");
        assert_eq!(s.command.as_deref(), Some("npx"));
        assert_eq!(s.args, vec!["my-pkg"]);
        assert_eq!(s.env.get("TOKEN").map(String::as_str), Some("abc"));
        assert!(!s.disabled);
        assert_eq!(s.scope, McpScope::User);
    }

    #[test]
    fn project_shadows_user_by_name() {
        let dir = TempDir::new();
        let user = dir.write(
            "user.mcp.json",
            r#"{"mcpServers":{"shared":{"command":"old"}}}"#,
        );
        let project = dir.write(
            "project.mcp.json",
            r#"{"mcpServers":{"shared":{"command":"new"}}}"#,
        );

        let servers = load_all(&user, &project);
        assert_eq!(servers.len(), 1);
        assert_eq!(servers[0].command.as_deref(), Some("new"));
        assert_eq!(servers[0].scope, McpScope::Project);
    }

    #[test]
    fn disabled_server_excluded_from_connectable() {
        let dir = TempDir::new();
        let user = dir.write(
            "user.mcp.json",
            r#"{"mcpServers":{"s":{"command":"cmd","disabled":true}}}"#,
        );
        let project = dir.write("project.mcp.json", "{}");

        let all = load_all(&user, &project);
        assert_eq!(all.len(), 1);
        assert!(all[0].disabled);

        let connectable = load_connectable(&user, &project);
        assert!(connectable.is_empty(), "disabled server must be excluded");
    }

    #[test]
    fn enabled_false_also_disables_server() {
        // Support the TUI's legacy "enabled": false convention.
        let dir = TempDir::new();
        let user = dir.write(
            "user.mcp.json",
            r#"{"mcpServers":{"s":{"command":"cmd","enabled":false}}}"#,
        );
        let project = dir.write("project.mcp.json", "{}");

        let all = load_all(&user, &project);
        assert_eq!(all.len(), 1);
        assert!(all[0].disabled, "\"enabled\":false must be treated as disabled");

        let connectable = load_connectable(&user, &project);
        assert!(connectable.is_empty());
    }

    #[test]
    fn http_server_excluded_from_connectable() {
        let dir = TempDir::new();
        let user = dir.write(
            "user.mcp.json",
            r#"{"mcpServers":{"http-srv":{"type":"http","url":"https://example.com/mcp"}}}"#,
        );
        let project = dir.write("project.mcp.json", "{}");

        let connectable = load_connectable(&user, &project);
        assert!(connectable.is_empty(), "HTTP server must be excluded");
    }

    #[test]
    fn missing_command_skipped_in_stdio_entry() {
        let dir = TempDir::new();
        let user = dir.write(
            "user.mcp.json",
            r#"{"mcpServers":{"bad":{"args":["--flag"]}}}"#,
        );
        let project = dir.write("project.mcp.json", "{}");

        let servers = load_all(&user, &project);
        assert!(servers.is_empty(), "entry without command must be skipped");
    }

    #[test]
    fn missing_file_treated_as_empty() {
        let dir = TempDir::new();
        let user = dir.path().join("nonexistent-user.mcp.json");
        let project = dir.path().join("nonexistent-project.mcp.json");
        let servers = load_all(&user, &project);
        assert!(servers.is_empty());
    }

    #[test]
    fn malformed_json_treated_as_empty() {
        let dir = TempDir::new();
        let user = dir.write("bad.mcp.json", "{ this is not json }");
        let project = dir.write("ok.mcp.json", "{}");
        let servers = load_all(&user, &project);
        assert!(servers.is_empty());
    }

    #[test]
    fn both_user_and_project_servers_returned_when_no_overlap() {
        let dir = TempDir::new();
        let user = dir.write(
            "user.mcp.json",
            r#"{"mcpServers":{"u":{"command":"ucmd"}}}"#,
        );
        let project = dir.write(
            "project.mcp.json",
            r#"{"mcpServers":{"p":{"command":"pcmd"}}}"#,
        );

        let servers = load_all(&user, &project);
        assert_eq!(servers.len(), 2);
        let names: Vec<&str> = servers.iter().map(|s| s.name.as_str()).collect();
        assert!(names.contains(&"u"));
        assert!(names.contains(&"p"));
    }

    #[test]
    fn load_connectable_returns_env_values() {
        let dir = TempDir::new();
        let user = dir.write(
            "user.mcp.json",
            r#"{"mcpServers":{"s":{"command":"cmd","env":{"K":"V"}}}}"#,
        );
        let project = dir.write("project.mcp.json", "{}");

        let items = load_connectable(&user, &project);
        assert_eq!(items[0].env.get("K").map(String::as_str), Some("V"));
    }

    #[test]
    fn unknown_transport_type_is_skipped() {
        let dir = TempDir::new();
        let user = dir.write(
            "user.mcp.json",
            r#"{"mcpServers":{"s":{"type":"sse","url":"https://x.com"}}}"#,
        );
        let project = dir.write("project.mcp.json", "{}");

        let servers = load_all(&user, &project);
        assert!(servers.is_empty(), "legacy SSE must be skipped");
    }

    #[test]
    fn target_returns_command_and_args() {
        let s = McpRawServer {
            name: "s".into(),
            scope: McpScope::User,
            command: Some("npx".into()),
            args: vec!["pkg".into(), "--flag".into()],
            env: HashMap::new(),
            url: None,
            disabled: false,
        };
        assert_eq!(s.target(), "npx pkg --flag");
    }
}
