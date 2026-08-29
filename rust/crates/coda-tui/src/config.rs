//! Local Coda state under `~/.coda` and the project directory.
//!
//! `coda serve` is not the only seam into the engine: much of what the TUI
//! needs to read and change lives in JSON files that both processes share.
//! Reading them directly is what lets the MCP and task browsers work, and lets
//! model and plugin changes actually take effect.
//!
//! Every writer here preserves keys it does not model. The engine stores
//! settings this front-end knows nothing about, and a naive round-trip through
//! a typed struct would silently delete them.

use std::path::{Path, PathBuf};

use serde_json::{Map, Value};

/// Locations of the files the TUI shares with the engine.
#[derive(Debug, Clone)]
pub struct Paths {
    /// `~/.coda`
    pub user_root: PathBuf,
    /// The session working directory.
    pub project_root: PathBuf,
}

impl Paths {
    /// Resolves the standard locations for a session in `project_root`.
    pub fn new(project_root: impl Into<PathBuf>) -> Self {
        let user_root = dirs_home()
            .map(|home| home.join(".coda"))
            .unwrap_or_else(|| PathBuf::from(".coda"));
        Self {
            user_root,
            project_root: project_root.into(),
        }
    }

    /// Overrides the user root, used by tests and `CODA_HOME`.
    pub fn with_user_root(mut self, root: impl Into<PathBuf>) -> Self {
        self.user_root = root.into();
        self
    }

    pub fn settings(&self) -> PathBuf {
        self.user_root.join("settings.json")
    }

    pub fn plugin_state(&self) -> PathBuf {
        self.user_root.join("plugin-state.json")
    }

    /// User-scoped MCP configuration.
    pub fn user_mcp(&self) -> PathBuf {
        self.user_root.join(".mcp.json")
    }

    /// Project-scoped MCP configuration.
    pub fn project_mcp(&self) -> PathBuf {
        self.project_root.join(".mcp.json")
    }

    pub fn task_logs(&self) -> PathBuf {
        self.user_root.join("task-logs")
    }

    /// `~/.coda/logs` — default location for tracing log files.
    pub fn logs(&self) -> PathBuf {
        self.user_root.join("logs")
    }

    /// Project-scoped skills directory (`.coda/skills/<name>/`).
    pub fn skills_project(&self) -> PathBuf {
        self.project_root.join(".coda").join("skills")
    }

    /// User-scoped skills directory (`~/.coda/skills/<name>/`).
    pub fn skills_user(&self) -> PathBuf {
        self.user_root.join("skills")
    }
}

fn dirs_home() -> Option<PathBuf> {
    if let Ok(explicit) = std::env::var("CODA_HOME") {
        if !explicit.is_empty() {
            return Some(PathBuf::from(explicit));
        }
    }
    directories::BaseDirs::new().map(|dirs| dirs.home_dir().to_path_buf())
}

#[derive(Debug, thiserror::Error)]
pub enum ConfigError {
    #[error("failed to read {path}: {source}")]
    Read {
        path: PathBuf,
        #[source]
        source: std::io::Error,
    },
    #[error("failed to write {path}: {source}")]
    Write {
        path: PathBuf,
        #[source]
        source: std::io::Error,
    },
    #[error("{path} is not valid JSON: {source}")]
    Parse {
        path: PathBuf,
        #[source]
        source: serde_json::Error,
    },
}

/// Reads a JSON document, treating a missing file as an empty object.
fn read_json(path: &Path) -> Result<Value, ConfigError> {
    match std::fs::read_to_string(path) {
        Ok(text) if text.trim().is_empty() => Ok(Value::Object(Map::new())),
        Ok(text) => serde_json::from_str(&text).map_err(|source| ConfigError::Parse {
            path: path.to_path_buf(),
            source,
        }),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            Ok(Value::Object(Map::new()))
        }
        Err(source) => Err(ConfigError::Read {
            path: path.to_path_buf(),
            source,
        }),
    }
}

/// Writes a JSON document atomically.
///
/// The temporary file is removed if the rename fails, so an interrupted write
/// cannot leave stray `.tmp` files accumulating beside the real one.
fn write_json(path: &Path, value: &Value) -> Result<(), ConfigError> {
    let parent = path.parent().unwrap_or(Path::new("."));
    std::fs::create_dir_all(parent).map_err(|source| ConfigError::Write {
        path: path.to_path_buf(),
        source,
    })?;

    let text = serde_json::to_string_pretty(value).unwrap_or_else(|_| "{}".to_string());
    let temp = parent.join(format!(
        ".{}.{}.tmp",
        path.file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("config"),
        std::process::id()
    ));

    let write = || -> std::io::Result<()> {
        std::fs::write(&temp, text.as_bytes())?;
        std::fs::rename(&temp, path)
    };

    match write() {
        Ok(()) => Ok(()),
        Err(source) => {
            let _ = std::fs::remove_file(&temp);
            Err(ConfigError::Write {
                path: path.to_path_buf(),
                source,
            })
        }
    }
}

// ---------------------------------------------------------------------------
// settings.json
// ---------------------------------------------------------------------------

/// `~/.coda/settings.json`, kept as raw JSON so unmodelled keys survive.
#[derive(Debug, Clone)]
pub struct Settings {
    path: PathBuf,
    root: Value,
}

impl Settings {
    pub fn load(paths: &Paths) -> Result<Self, ConfigError> {
        let path = paths.settings();
        Ok(Self {
            root: read_json(&path)?,
            path,
        })
    }

    pub fn save(&self) -> Result<(), ConfigError> {
        write_json(&self.path, &self.root)
    }

    fn object_mut(&mut self, key: &str) -> &mut Map<String, Value> {
        let root = self
            .root
            .as_object_mut()
            .expect("settings root is always an object");
        root.entry(key.to_string())
            .or_insert_with(|| Value::Object(Map::new()));
        // Replace a non-object value rather than panicking on malformed input.
        if !root[key].is_object() {
            root[key] = Value::Object(Map::new());
        }
        root[key].as_object_mut().expect("just ensured")
    }

    pub fn default_provider(&self) -> Option<&str> {
        self.root.get("defaultProvider")?.as_str()
    }

    /// The model configured for a provider, falling back to `defaultModel`.
    pub fn model_for(&self, provider: &str) -> Option<&str> {
        self.root
            .get("modelByProvider")
            .and_then(|m| m.get(provider))
            .and_then(Value::as_str)
            .or_else(|| self.root.get("defaultModel").and_then(Value::as_str))
    }

    /// Sets the model for a provider, matching `SettingsWriter`'s layout.
    pub fn set_model_for(&mut self, provider: &str, model: &str) {
        self.object_mut("modelByProvider")
            .insert(provider.to_string(), Value::String(model.to_string()));
    }

    pub fn effort_for(&self, provider: &str, model: &str) -> Option<&str> {
        self.root
            .get("effortByModel")?
            .get(format!("{provider}/{model}"))?
            .as_str()
    }

    pub fn set_effort_for(&mut self, provider: &str, model: &str, effort: Option<&str>) {
        let key = format!("{provider}/{model}");
        let map = self.object_mut("effortByModel");
        match effort {
            Some(effort) => {
                map.insert(key, Value::String(effort.to_string()));
            }
            None => {
                map.remove(&key);
            }
        }
    }

    pub fn theme(&self) -> Option<&str> {
        self.root.get("theme")?.as_str()
    }

    pub fn set_theme(&mut self, theme: &str) {
        self.root
            .as_object_mut()
            .expect("object")
            .insert("theme".into(), Value::String(theme.to_string()));
    }

    pub fn tool_display_mode(&self) -> Option<&str> {
        self.root.get("toolDisplayMode")?.as_str()
    }

    pub fn set_tool_display_mode(&mut self, mode: &str) {
        self.root
            .as_object_mut()
            .expect("object")
            .insert("toolDisplayMode".into(), Value::String(mode.to_string()));
    }

    pub fn output_style(&self) -> Option<&str> {
        self.root.get("outputStyle")?.as_str()
    }

    pub fn set_output_style(&mut self, style: &str) {
        self.root
            .as_object_mut()
            .expect("object")
            .insert("outputStyle".into(), Value::String(style.to_string()));
    }

    pub fn permission_mode(&self) -> Option<&str> {
        self.root.get("permissionMode")?.as_str()
    }

    pub fn set_permission_mode(&mut self, mode: &str) {
        self.root
            .as_object_mut()
            .expect("object")
            .insert("permissionMode".into(), Value::String(mode.to_string()));
    }

    pub fn set_default_provider(&mut self, provider: &str) {
        self.root
            .as_object_mut()
            .expect("object")
            .insert("defaultProvider".into(), Value::String(provider.to_string()));
    }

    /// All custom HTTP headers as (name, value) pairs; values are opaque strings.
    pub fn custom_headers(&self) -> Vec<(String, String)> {
        self.root
            .get("customHeaders")
            .and_then(Value::as_object)
            .map(|obj| {
                obj.iter()
                    .filter_map(|(k, v)| v.as_str().map(|s| (k.clone(), s.to_string())))
                    .collect()
            })
            .unwrap_or_default()
    }

    pub fn set_custom_header(&mut self, name: &str, value: &str) {
        self.object_mut("customHeaders")
            .insert(name.to_string(), Value::String(value.to_string()));
    }

    pub fn remove_custom_header(&mut self, name: &str) {
        if let Some(headers) = self
            .root
            .as_object_mut()
            .and_then(|obj| obj.get_mut("customHeaders"))
            .and_then(Value::as_object_mut)
        {
            headers.remove(name);
        }
    }

    // -- Telemetry -----------------------------------------------------------

    fn telemetry_val(&self) -> Option<&Value> {
        self.root.get("telemetry")
    }

    pub fn log_enabled(&self) -> bool {
        self.telemetry_val()
            .and_then(|t| t.get("enabled"))
            .and_then(Value::as_bool)
            .unwrap_or(false)
    }

    pub fn log_level(&self) -> &str {
        self.telemetry_val()
            .and_then(|t| t.get("minLevel"))
            .and_then(Value::as_str)
            .unwrap_or("info")
    }

    pub fn log_to_stderr(&self) -> bool {
        self.telemetry_val()
            .and_then(|t| t.get("logToStderr"))
            .and_then(Value::as_bool)
            .unwrap_or(false)
    }

    pub fn log_directory_override(&self) -> Option<&str> {
        self.telemetry_val()
            .and_then(|t| t.get("directoryOverride"))
            .and_then(Value::as_str)
    }

    pub fn set_telemetry(&mut self, enabled: bool, level: &str, stderr: bool) {
        let telemetry = self.object_mut("telemetry");
        telemetry.insert("enabled".into(), Value::Bool(enabled));
        telemetry.insert("minLevel".into(), Value::String(level.to_string()));
        telemetry.insert("logToStderr".into(), Value::Bool(stderr));
    }

    // -- Marketplaces --------------------------------------------------------

    /// Registered marketplace registries as (name, source-URL-or-path) pairs.
    pub fn marketplaces(&self) -> Vec<(String, String)> {
        self.root
            .get("marketplaces")
            .and_then(Value::as_object)
            .map(|obj| {
                obj.iter()
                    .filter_map(|(k, v)| v.as_str().map(|s| (k.clone(), s.to_string())))
                    .collect()
            })
            .unwrap_or_default()
    }

    pub fn add_marketplace(&mut self, name: &str, source: &str) {
        self.object_mut("marketplaces")
            .insert(name.to_string(), Value::String(source.to_string()));
    }

    pub fn remove_marketplace(&mut self, name: &str) -> bool {
        if let Some(obj) = self
            .root
            .as_object_mut()
            .and_then(|obj| obj.get_mut("marketplaces"))
            .and_then(Value::as_object_mut)
        {
            obj.remove(name).is_some()
        } else {
            false
        }
    }

    /// The raw document, for diagnostics.
    pub fn raw(&self) -> &Value {
        &self.root
    }
}

// ---------------------------------------------------------------------------
// plugin-state.json
// ---------------------------------------------------------------------------

/// `~/.coda/plugin-state.json`, which decides whether a plugin loads.
#[derive(Debug, Clone)]
pub struct PluginState {
    path: PathBuf,
    root: Value,
}

impl PluginState {
    pub fn load(paths: &Paths) -> Result<Self, ConfigError> {
        let path = paths.plugin_state();
        Ok(Self {
            root: read_json(&path)?,
            path,
        })
    }

    pub fn save(&self) -> Result<(), ConfigError> {
        write_json(&self.path, &self.root)
    }

    fn list(&self, key: &str) -> Vec<String> {
        self.root
            .get(key)
            .and_then(Value::as_array)
            .map(|items| {
                items
                    .iter()
                    .filter_map(Value::as_str)
                    .map(str::to_string)
                    .collect()
            })
            .unwrap_or_default()
    }

    fn set_list(&mut self, key: &str, values: Vec<String>) {
        let array = values.into_iter().map(Value::String).collect();
        self.root
            .as_object_mut()
            .expect("object")
            .insert(key.to_string(), Value::Array(array));
    }

    pub fn is_disabled(&self, name: &str) -> bool {
        self.list("disabledPlugins").iter().any(|p| p == name)
    }

    /// Enables or disables a plugin, keeping both lists consistent.
    pub fn set_enabled(&mut self, name: &str, enabled: bool) {
        let mut disabled = self.list("disabledPlugins");
        let mut explicit = self.list("explicitlyEnabled");

        if enabled {
            disabled.retain(|p| p != name);
            if !explicit.iter().any(|p| p == name) {
                explicit.push(name.to_string());
            }
        } else {
            explicit.retain(|p| p != name);
            if !disabled.iter().any(|p| p == name) {
                disabled.push(name.to_string());
            }
        }

        self.set_list("disabledPlugins", disabled);
        self.set_list("explicitlyEnabled", explicit);
    }
}

// ---------------------------------------------------------------------------
// .mcp.json
// ---------------------------------------------------------------------------

/// Where an MCP server definition came from.
///
/// Mirrors `coda_mcp::config::McpScope`; kept as a separate type so the TUI
/// API is not coupled to the coda-mcp crate's internal shape.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Scope {
    Project,
    User,
}

impl Scope {
    pub fn label(self) -> &'static str {
        match self {
            Scope::Project => "project",
            Scope::User => "user",
        }
    }
}

/// One configured MCP server — a display-only view.
///
/// Actual `env` values are intentionally absent so the TUI never stores
/// or displays secrets. The parsing is delegated to `coda_mcp::config` so
/// the file-format logic lives in exactly one place.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpServer {
    pub name: String,
    pub scope: Scope,
    /// `"stdio"` or `"http"`.
    pub transport: &'static str,
    pub command: Option<String>,
    pub args: Vec<String>,
    pub url: Option<String>,
    pub enabled: bool,
    /// Environment variable names only; values may hold secrets.
    pub env_keys: Vec<String>,
}

impl McpServer {
    /// A one-line description of how this server is reached.
    pub fn target(&self) -> String {
        match (&self.command, &self.url) {
            (Some(command), _) => {
                if self.args.is_empty() {
                    command.clone()
                } else {
                    format!("{command} {}", self.args.join(" "))
                }
            }
            (None, Some(url)) => url.clone(),
            (None, None) => String::new(),
        }
    }
}

/// Reads MCP servers from the project and user configuration files.
///
/// Delegates the file-format parsing to `coda_mcp::config::load_all` so the
/// JSON parsing logic lives in one place. Project definitions shadow user
/// definitions of the same name.
pub fn load_mcp_servers(paths: &Paths) -> Result<Vec<McpServer>, ConfigError> {
    let raw = coda_mcp::config::load_all(&paths.user_mcp(), &paths.project_mcp());
    let mut servers: Vec<McpServer> = raw.into_iter().map(raw_to_display).collect();
    servers.sort_by(|a, b| a.name.cmp(&b.name));
    Ok(servers)
}

fn raw_to_display(raw: coda_mcp::config::McpRawServer) -> McpServer {
    let scope = match raw.scope {
        coda_mcp::config::McpScope::Project => Scope::Project,
        coda_mcp::config::McpScope::User => Scope::User,
    };
    let transport = raw.transport(); // call before moving fields
    let mut env_keys: Vec<String> = raw.env.into_keys().collect();
    env_keys.sort(); // stable order for tests that check exact equality
    McpServer {
        name: raw.name,
        scope,
        transport,
        command: raw.command,
        args: raw.args,
        url: raw.url,
        enabled: !raw.disabled,
        env_keys,
    }
}

/// Enables or disables an MCP server in whichever file defines it.
pub fn set_mcp_enabled(paths: &Paths, name: &str, enabled: bool) -> Result<bool, ConfigError> {
    for path in [paths.project_mcp(), paths.user_mcp()] {
        let mut document = read_json(&path)?;
        let Some(entry) = document
            .get_mut("mcpServers")
            .and_then(Value::as_object_mut)
            .and_then(|servers| servers.get_mut(name))
        else {
            continue;
        };
        if let Some(entry) = entry.as_object_mut() {
            entry.insert("enabled".to_string(), Value::Bool(enabled));
            write_json(&path, &document)?;
            return Ok(true);
        }
    }
    Ok(false)
}

// ---------------------------------------------------------------------------
// task-logs
// ---------------------------------------------------------------------------

/// A background task discovered from its persisted log.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TaskLog {
    pub id: String,
    pub session_id: String,
    pub path: PathBuf,
    pub size_bytes: u64,
}

/// Lists persisted task logs, newest first.
///
/// Passing a session id restricts the listing to that session's directory,
/// which is what the browser wants; passing `None` lists every session.
pub fn list_task_logs(paths: &Paths, session_id: Option<&str>) -> Vec<TaskLog> {
    let root = paths.task_logs();
    let mut logs = Vec::new();

    let sessions: Vec<PathBuf> = match session_id {
        Some(id) => vec![root.join(id)],
        None => std::fs::read_dir(&root)
            .into_iter()
            .flatten()
            .flatten()
            .filter(|entry| entry.path().is_dir())
            .map(|entry| entry.path())
            .collect(),
    };

    for session in sessions {
        let Some(session_name) = session
            .file_name()
            .and_then(|n| n.to_str())
            .map(str::to_string)
        else {
            continue;
        };

        let Ok(entries) = std::fs::read_dir(&session) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("log") {
                continue;
            }
            let Some(id) = path.file_stem().and_then(|s| s.to_str()) else {
                continue;
            };
            logs.push(TaskLog {
                id: id.to_string(),
                session_id: session_name.clone(),
                size_bytes: entry.metadata().map(|m| m.len()).unwrap_or(0),
                path,
            });
        }
    }

    // Newest first, which for monotonic task ids is descending id order.
    logs.sort_by(|a, b| b.id.cmp(&a.id));
    logs
}

/// Reads the tail of a task log.
///
/// Logs are unbounded, so only the last `max_lines` are returned; a task that
/// has produced megabytes of output must not be able to stall the UI.
pub fn read_task_log_tail(path: &Path, max_lines: usize) -> Vec<String> {
    let Ok(text) = std::fs::read_to_string(path) else {
        return Vec::new();
    };
    let lines: Vec<&str> = text.lines().collect();
    let start = lines.len().saturating_sub(max_lines);
    lines[start..]
        .iter()
        .map(|line| coda_render::text::sanitize(line))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn temp_paths() -> (tempdir::TempDir, Paths) {
        let dir = tempdir::TempDir::new();
        let paths = Paths::new(dir.path().join("project")).with_user_root(dir.path().join("user"));
        std::fs::create_dir_all(&paths.user_root).expect("user root");
        std::fs::create_dir_all(&paths.project_root).expect("project root");
        (dir, paths)
    }

    /// A minimal scoped temporary directory, to avoid a dev-dependency.
    mod tempdir {
        use std::path::{Path, PathBuf};

        pub struct TempDir(PathBuf);

        impl TempDir {
            pub fn new() -> Self {
                let base = std::env::temp_dir().join(format!(
                    "coda-tui-test-{}-{:?}",
                    std::process::id(),
                    std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .map(|d| d.as_nanos())
                        .unwrap_or(0)
                ));
                std::fs::create_dir_all(&base).expect("temp dir");
                Self(base)
            }

            pub fn path(&self) -> &Path {
                &self.0
            }
        }

        impl Drop for TempDir {
            fn drop(&mut self) {
                let _ = std::fs::remove_dir_all(&self.0);
            }
        }
    }

    fn write(path: &Path, value: Value) {
        std::fs::create_dir_all(path.parent().unwrap()).expect("parent");
        std::fs::write(path, serde_json::to_string_pretty(&value).unwrap()).expect("write");
    }

    #[test]
    fn a_missing_settings_file_loads_as_empty() {
        let (_dir, paths) = temp_paths();
        let settings = Settings::load(&paths).expect("load");
        assert!(settings.model_for("anything").is_none());
    }

    #[test]
    fn reads_the_model_for_a_provider() {
        let (_dir, paths) = temp_paths();
        write(
            &paths.settings(),
            json!({ "modelByProvider": { "github-copilot": "claude-opus-5" } }),
        );

        let settings = Settings::load(&paths).expect("load");
        assert_eq!(settings.model_for("github-copilot"), Some("claude-opus-5"));
    }

    #[test]
    fn falls_back_to_the_default_model_for_an_unconfigured_provider() {
        let (_dir, paths) = temp_paths();
        write(
            &paths.settings(),
            json!({ "defaultModel": "claude-opus-4-8", "modelByProvider": {} }),
        );

        let settings = Settings::load(&paths).expect("load");
        assert_eq!(settings.model_for("other"), Some("claude-opus-4-8"));
    }

    #[test]
    fn writing_a_model_preserves_every_other_setting() {
        let (_dir, paths) = temp_paths();
        write(
            &paths.settings(),
            json!({
                "telemetry": { "enabled": true, "level": "debug" },
                "modelByProvider": { "claude-ai": "claude-opus-4-8" },
                "permissions": { "allow": ["danger(safe:*)"] },
                "somethingThisClientDoesNotModel": 42
            }),
        );

        let mut settings = Settings::load(&paths).expect("load");
        settings.set_model_for("github-copilot", "gpt-5.6-sol");
        settings.save().expect("save");

        let reloaded = read_json(&paths.settings()).expect("reread");
        assert_eq!(reloaded["somethingThisClientDoesNotModel"], 42);
        assert_eq!(reloaded["telemetry"]["level"], "debug");
        assert_eq!(reloaded["permissions"]["allow"][0], "danger(safe:*)");
        assert_eq!(reloaded["modelByProvider"]["claude-ai"], "claude-opus-4-8");
        assert_eq!(reloaded["modelByProvider"]["github-copilot"], "gpt-5.6-sol");
    }

    #[test]
    fn writes_effort_keyed_by_provider_and_model() {
        let (_dir, paths) = temp_paths();
        let mut settings = Settings::load(&paths).expect("load");
        settings.set_effort_for("github-copilot", "claude-opus-5", Some("high"));
        settings.save().expect("save");

        let reloaded = Settings::load(&paths).expect("reload");
        assert_eq!(
            reloaded.effort_for("github-copilot", "claude-opus-5"),
            Some("high")
        );
    }

    #[test]
    fn clearing_effort_removes_the_key() {
        let (_dir, paths) = temp_paths();
        let mut settings = Settings::load(&paths).expect("load");
        settings.set_effort_for("p", "m", Some("high"));
        settings.set_effort_for("p", "m", None);
        settings.save().expect("save");

        assert!(Settings::load(&paths).expect("reload").effort_for("p", "m").is_none());
    }

    #[test]
    fn recovers_from_a_malformed_section() {
        let (_dir, paths) = temp_paths();
        write(&paths.settings(), json!({ "modelByProvider": "not-an-object" }));

        let mut settings = Settings::load(&paths).expect("load");
        settings.set_model_for("p", "m");
        settings.save().expect("save");

        assert_eq!(Settings::load(&paths).expect("reload").model_for("p"), Some("m"));
    }

    #[test]
    fn reports_a_malformed_settings_document() {
        let (_dir, paths) = temp_paths();
        std::fs::write(paths.settings(), "{ not json").expect("write");
        assert!(matches!(
            Settings::load(&paths),
            Err(ConfigError::Parse { .. })
        ));
    }

    #[test]
    fn an_atomic_write_leaves_no_temporary_file_behind() {
        let (_dir, paths) = temp_paths();
        let mut settings = Settings::load(&paths).expect("load");
        settings.set_theme("cool-dark");
        settings.save().expect("save");

        let strays: Vec<_> = std::fs::read_dir(&paths.user_root)
            .expect("read dir")
            .flatten()
            .filter(|e| e.file_name().to_string_lossy().ends_with(".tmp"))
            .collect();
        assert!(strays.is_empty(), "left {} temp files behind", strays.len());
    }

    #[test]
    fn toggling_a_plugin_updates_both_lists() {
        let (_dir, paths) = temp_paths();
        let mut state = PluginState::load(&paths).expect("load");

        state.set_enabled("mine", false);
        assert!(state.is_disabled("mine"));

        state.set_enabled("mine", true);
        assert!(!state.is_disabled("mine"));

        state.save().expect("save");
        let reloaded = PluginState::load(&paths).expect("reload");
        assert!(!reloaded.is_disabled("mine"));
    }

    #[test]
    fn toggling_a_plugin_preserves_installed_versions() {
        let (_dir, paths) = temp_paths();
        write(
            &paths.plugin_state(),
            json!({
                "disabledPlugins": [],
                "installedVersions": { "p": { "version": "1.0.0", "source": "local" } }
            }),
        );

        let mut state = PluginState::load(&paths).expect("load");
        state.set_enabled("p", false);
        state.save().expect("save");

        let reloaded = read_json(&paths.plugin_state()).expect("reread");
        assert_eq!(reloaded["installedVersions"]["p"]["version"], "1.0.0");
        assert_eq!(reloaded["disabledPlugins"][0], "p");
    }

    #[test]
    fn disabling_a_plugin_twice_does_not_duplicate_it() {
        let (_dir, paths) = temp_paths();
        let mut state = PluginState::load(&paths).expect("load");
        state.set_enabled("p", false);
        state.set_enabled("p", false);
        state.save().expect("save");

        let reloaded = read_json(&paths.plugin_state()).expect("reread");
        assert_eq!(reloaded["disabledPlugins"].as_array().unwrap().len(), 1);
    }

    #[test]
    fn reads_mcp_servers_from_the_user_file() {
        let (_dir, paths) = temp_paths();
        write(
            &paths.user_mcp(),
            json!({
                "mcpServers": {
                    "memory": { "command": "memory.exe", "env": { "DATA": "x" } },
                    "remote": { "url": "https://example.com/mcp" }
                }
            }),
        );

        let servers = load_mcp_servers(&paths).expect("load");
        assert_eq!(servers.len(), 2);

        let memory = servers.iter().find(|s| s.name == "memory").expect("memory");
        assert_eq!(memory.transport, "stdio");
        assert_eq!(memory.scope, Scope::User);
        assert_eq!(memory.env_keys, vec!["DATA"]);
        assert!(memory.enabled);

        let remote = servers.iter().find(|s| s.name == "remote").expect("remote");
        assert_eq!(remote.transport, "http");
        assert_eq!(remote.target(), "https://example.com/mcp");
    }

    #[test]
    fn a_project_server_shadows_a_user_server_of_the_same_name() {
        let (_dir, paths) = temp_paths();
        write(
            &paths.user_mcp(),
            json!({ "mcpServers": { "shared": { "command": "user.exe" } } }),
        );
        write(
            &paths.project_mcp(),
            json!({ "mcpServers": { "shared": { "command": "project.exe" } } }),
        );

        let servers = load_mcp_servers(&paths).expect("load");
        assert_eq!(servers.len(), 1);
        assert_eq!(servers[0].scope, Scope::Project);
        assert_eq!(servers[0].command.as_deref(), Some("project.exe"));
    }

    #[test]
    fn includes_command_arguments_in_the_target() {
        let (_dir, paths) = temp_paths();
        write(
            &paths.user_mcp(),
            json!({ "mcpServers": { "s": { "command": "node", "args": ["server.js", "--port", "1"] } } }),
        );

        let servers = load_mcp_servers(&paths).expect("load");
        assert_eq!(servers[0].target(), "node server.js --port 1");
    }

    #[test]
    fn an_explicitly_disabled_server_is_reported_as_disabled() {
        let (_dir, paths) = temp_paths();
        write(
            &paths.user_mcp(),
            json!({ "mcpServers": { "s": { "command": "x", "enabled": false } } }),
        );
        assert!(!load_mcp_servers(&paths).expect("load")[0].enabled);
    }

    #[test]
    fn toggling_a_server_rewrites_only_its_own_file() {
        let (_dir, paths) = temp_paths();
        write(
            &paths.user_mcp(),
            json!({
                "mcpServers": { "s": { "command": "x" }, "other": { "command": "y" } },
                "unrelatedKey": true
            }),
        );

        assert!(set_mcp_enabled(&paths, "s", false).expect("toggle"));

        let reloaded = read_json(&paths.user_mcp()).expect("reread");
        assert_eq!(reloaded["mcpServers"]["s"]["enabled"], false);
        assert_eq!(reloaded["mcpServers"]["other"]["command"], "y");
        assert_eq!(reloaded["unrelatedKey"], true);
    }

    #[test]
    fn toggling_an_unknown_server_reports_that_it_was_not_found() {
        let (_dir, paths) = temp_paths();
        assert!(!set_mcp_enabled(&paths, "nope", false).expect("toggle"));
    }

    #[test]
    fn no_mcp_files_yields_no_servers() {
        let (_dir, paths) = temp_paths();
        assert!(load_mcp_servers(&paths).expect("load").is_empty());
    }

    #[test]
    fn lists_task_logs_for_a_session_newest_first() {
        let (_dir, paths) = temp_paths();
        let session = paths.task_logs().join("s1");
        std::fs::create_dir_all(&session).expect("session dir");
        std::fs::write(session.join("task-0001.log"), "first").expect("write");
        std::fs::write(session.join("task-0002.log"), "second").expect("write");
        std::fs::write(session.join("notes.txt"), "ignored").expect("write");

        let logs = list_task_logs(&paths, Some("s1"));
        assert_eq!(logs.len(), 2, "non-log files must be ignored");
        assert_eq!(logs[0].id, "task-0002", "newest first");
        assert_eq!(logs[1].id, "task-0001");
        assert_eq!(logs[0].session_id, "s1");
        assert!(logs[0].size_bytes > 0);
    }

    #[test]
    fn lists_task_logs_across_every_session() {
        let (_dir, paths) = temp_paths();
        for session in ["a", "b"] {
            let dir = paths.task_logs().join(session);
            std::fs::create_dir_all(&dir).expect("dir");
            std::fs::write(dir.join("task-0001.log"), "x").expect("write");
        }
        assert_eq!(list_task_logs(&paths, None).len(), 2);
    }

    #[test]
    fn a_missing_task_log_directory_yields_nothing() {
        let (_dir, paths) = temp_paths();
        assert!(list_task_logs(&paths, Some("nope")).is_empty());
        assert!(list_task_logs(&paths, None).is_empty());
    }

    #[test]
    fn reads_only_the_tail_of_a_task_log() {
        let (_dir, paths) = temp_paths();
        let session = paths.task_logs().join("s1");
        std::fs::create_dir_all(&session).expect("dir");
        let body: String = (1..=100).map(|i| format!("line {i}\n")).collect();
        let path = session.join("task-0001.log");
        std::fs::write(&path, body).expect("write");

        let tail = read_task_log_tail(&path, 10);
        assert_eq!(tail.len(), 10);
        assert_eq!(tail[0], "line 91");
        assert_eq!(tail[9], "line 100");
    }

    #[test]
    fn a_task_log_tail_is_sanitized() {
        let (_dir, paths) = temp_paths();
        let session = paths.task_logs().join("s1");
        std::fs::create_dir_all(&session).expect("dir");
        let path = session.join("t.log");
        std::fs::write(&path, "\u{1b}[31mred\u{1b}[0m\n").expect("write");

        assert_eq!(read_task_log_tail(&path, 10), vec!["red"]);
    }

    #[test]
    fn reading_a_missing_log_yields_nothing() {
        assert!(read_task_log_tail(Path::new("does-not-exist.log"), 10).is_empty());
    }

    #[test]
    fn output_style_round_trips_through_settings() {
        let (_dir, paths) = temp_paths();
        let mut settings = Settings::load(&paths).expect("load");
        assert!(settings.output_style().is_none());
        settings.set_output_style("concise");
        settings.save().expect("save");
        let reloaded = Settings::load(&paths).expect("reload");
        assert_eq!(reloaded.output_style(), Some("concise"));
    }

    #[test]
    fn permission_mode_round_trips_through_settings() {
        let (_dir, paths) = temp_paths();
        let mut settings = Settings::load(&paths).expect("load");
        assert!(settings.permission_mode().is_none());
        settings.set_permission_mode("bypass");
        settings.save().expect("save");
        let reloaded = Settings::load(&paths).expect("reload");
        assert_eq!(reloaded.permission_mode(), Some("bypass"));
    }

    #[test]
    fn custom_headers_can_be_set_and_read() {
        let (_dir, paths) = temp_paths();
        let mut settings = Settings::load(&paths).expect("load");
        assert!(settings.custom_headers().is_empty());
        settings.set_custom_header("X-Org", "my-org");
        settings.set_custom_header("X-Role", "admin");
        settings.save().expect("save");
        let reloaded = Settings::load(&paths).expect("reload");
        let headers = reloaded.custom_headers();
        assert!(headers.iter().any(|(k, v)| k == "X-Org" && v == "my-org"));
        assert!(headers.iter().any(|(k, v)| k == "X-Role" && v == "admin"));
    }

    #[test]
    fn removing_a_custom_header_leaves_others_intact() {
        let (_dir, paths) = temp_paths();
        let mut settings = Settings::load(&paths).expect("load");
        settings.set_custom_header("X-A", "1");
        settings.set_custom_header("X-B", "2");
        settings.remove_custom_header("X-A");
        settings.save().expect("save");
        let reloaded = Settings::load(&paths).expect("reload");
        let headers = reloaded.custom_headers();
        assert!(headers.iter().all(|(k, _)| k != "X-A"));
        assert!(headers.iter().any(|(k, _)| k == "X-B"));
    }

    #[test]
    fn telemetry_settings_round_trip() {
        let (_dir, paths) = temp_paths();
        let mut settings = Settings::load(&paths).expect("load");
        assert!(!settings.log_enabled());
        settings.set_telemetry(true, "debug", true);
        settings.save().expect("save");
        let reloaded = Settings::load(&paths).expect("reload");
        assert!(reloaded.log_enabled());
        assert_eq!(reloaded.log_level(), "debug");
        assert!(reloaded.log_to_stderr());
    }

    #[test]
    fn disabling_telemetry_is_persisted() {
        let (_dir, paths) = temp_paths();
        let mut settings = Settings::load(&paths).expect("load");
        settings.set_telemetry(true, "debug", false);
        settings.set_telemetry(false, "debug", false);
        settings.save().expect("save");
        assert!(!Settings::load(&paths).expect("reload").log_enabled());
    }

    #[test]
    fn marketplace_add_and_remove_round_trip() {
        let (_dir, paths) = temp_paths();
        let mut settings = Settings::load(&paths).expect("load");
        assert!(settings.marketplaces().is_empty());
        settings.add_marketplace("community", "https://example.com/plugins.json");
        settings.save().expect("save");
        let reloaded = Settings::load(&paths).expect("reload");
        let markets = reloaded.marketplaces();
        assert_eq!(markets.len(), 1);
        assert_eq!(markets[0].0, "community");
        assert_eq!(markets[0].1, "https://example.com/plugins.json");
    }

    #[test]
    fn removing_a_marketplace_returns_whether_it_existed() {
        let (_dir, paths) = temp_paths();
        let mut settings = Settings::load(&paths).expect("load");
        settings.add_marketplace("a", "src://a");
        assert!(settings.remove_marketplace("a"));
        assert!(!settings.remove_marketplace("nope"));
    }

    #[test]
    fn paths_logs_is_under_user_root() {
        let (_dir, paths) = temp_paths();
        assert_eq!(paths.logs(), paths.user_root.join("logs"));
    }

    #[test]
    fn paths_skills_project_is_under_project_root() {
        let (_dir, paths) = temp_paths();
        assert_eq!(
            paths.skills_project(),
            paths.project_root.join(".coda").join("skills")
        );
    }

    #[test]
    fn paths_skills_user_is_under_user_root() {
        let (_dir, paths) = temp_paths();
        assert_eq!(paths.skills_user(), paths.user_root.join("skills"));
    }
}
