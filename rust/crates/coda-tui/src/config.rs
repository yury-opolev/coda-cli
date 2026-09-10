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
    /// The value cannot be persisted as given.
    #[error("{0}")]
    Invalid(String),
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
        Ok(text) => {
            let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
            if text.trim().is_empty() {
                return Ok(Value::Object(Map::new()));
            }
            serde_json::from_str(text).map_err(|source| ConfigError::Parse {
                path: path.to_path_buf(),
                source,
            })
        }
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
///
/// A `Settings` value remembers the document it loaded (`original`) as well as
/// the one being edited (`root`). Saving persists the *difference* between
/// them, under the shared settings lock, against whatever the file holds at
/// that moment — see [`coda_boot::settings_store`]. Writing `root` back
/// wholesale would revert everything another process changed in the meantime,
/// which is exactly how a freshly committed `defaultProvider` used to
/// disappear when the user next changed their theme.
#[derive(Debug, Clone)]
pub struct Settings {
    path: PathBuf,
    original: Value,
    root: Value,
}

impl Settings {
    pub fn load(paths: &Paths) -> Result<Self, ConfigError> {
        let path = paths.settings();
        let root = read_json(&path)?;
        Ok(Self { original: root.clone(), root, path })
    }

    /// Persist only what changed since this value was loaded, or since the
    /// last successful save.
    ///
    /// The baseline advances on success. Without that, every later save keeps
    /// re-applying the earlier edits, which would overwrite whatever another
    /// process wrote to those keys in the meantime — the same reversion this
    /// delta save exists to prevent, one save later.
    pub fn save(&mut self) -> Result<(), ConfigError> {
        coda_boot::settings_store::save_changes(&self.path, &self.original, &self.root).map_err(
            |error| match error {
                coda_boot::settings_store::SettingsError::Parse { path, source } => {
                    ConfigError::Parse { path, source }
                }
                coda_boot::settings_store::SettingsError::Read { path, source } => {
                    ConfigError::Read { path, source }
                }
                other => ConfigError::Write {
                    path: self.path.clone(),
                    source: std::io::Error::other(other.to_string()),
                },
            },
        )?;
        self.original = self.root.clone();
        Ok(())
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

    fn telemetry_field(&self, key: &str) -> Option<&Value> {
        self.root.get("telemetry")?.as_object()?.get(key)
    }

    /// Whether telemetry is on. Absent means off, matching the C# default.
    pub fn telemetry_enabled(&self) -> bool {
        self.telemetry_field("enabled")
            .and_then(Value::as_bool)
            .unwrap_or(false)
    }

    pub fn telemetry_level(&self) -> Option<&str> {
        self.telemetry_field("minLevel").and_then(Value::as_str)
    }

    pub fn telemetry_stderr(&self) -> bool {
        self.telemetry_field("logToStderr")
            .and_then(Value::as_bool)
            .unwrap_or(false)
    }

    /// An empty settings object rooted at `path`, for tests and for the case
    /// where `settings.json` does not exist yet.
    ///
    /// The baseline is empty too, so saving writes only what the caller sets —
    /// it never asserts that every other key should be removed.
    pub fn empty_at(path: PathBuf) -> Self {
        Self {
            path,
            original: Value::Object(Map::new()),
            root: Value::Object(Map::new()),
        }
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
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Scope {
    Project,
    /// The default for a new server: user scope applies everywhere, which is
    /// what someone adding a server usually wants.
    #[default]
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
/// `env_raw` carries the *unresolved* values as written in `.mcp.json`
/// (e.g. `coda-secret:store/key` references). They are safe to show in the
/// editor because they are never resolved here — actual secrets stay in the
/// credential store.
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
    /// Raw (unresolved) environment variable values as configured in `.mcp.json`,
    /// sorted by key. References such as `coda-secret:store/key` are shown
    /// as-is; they are never resolved here.
    pub env_raw: Vec<(String, String)>,
    /// Environment variable names only; kept for fast display and listing.
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
/// **Moved.** The file parsing lives in [`crate::local::mcp`] — the local
/// maintenance adapter — because it reads a file that only this client's own
/// machine has. Re-exported here so the existing editor call sites are
/// unchanged; the display path for an ordinary session is the engine's
/// `mcp/list`.
pub use crate::local::mcp::load_mcp_servers;

/// The immutable identity a server was opened on.
///
/// Captured when the editor opens and passed unchanged to [`save_mcp_server`]
/// so a save always knows *exactly* which on-disk entry it is replacing —
/// scope included. A name alone cannot distinguish a user entry that is
/// shadowed by a project entry of the same name, and acting on the wrong one
/// would delete an unrelated server.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpServerId {
    pub scope: Scope,
    pub name: String,
}

impl McpServerId {
    pub fn new(scope: Scope, name: impl Into<String>) -> Self {
        Self {
            scope,
            name: name.into(),
        }
    }
}

/// A server as the editor works with it, before it reaches disk.
///
/// Deliberately carries only what the config model round-trips. Offering a
/// field that the loader drops on save — OAuth credentials, for instance —
/// would silently discard whatever the user typed, which is worse than not
/// offering it at all.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct McpDraft {
    pub name: String,
    pub scope: Scope,
    /// `"stdio"` or `"http"`.
    pub transport: String,
    pub command: String,
    /// Arguments as a JSON array string, e.g. `["-y", "server"]`.
    /// An empty string means no arguments.
    pub args: String,
    pub url: String,
    pub enabled: bool,
    /// Environment variables as a JSON object mapping names to string values,
    /// e.g. `{ "API_KEY": "coda-secret:store/key" }`. A JSON object is used
    /// rather than `KEY=VALUE` lines so values may contain newlines, `=`,
    /// leading/trailing whitespace or Unicode and still round-trip exactly.
    /// Values are the *unresolved* text from `.mcp.json`; `coda-secret:`
    /// references are shown verbatim and never resolved into the UI.
    pub env: String,
}

impl McpDraft {
    /// A draft for a new server, defaulting to the shape most people want.
    pub fn new() -> Self {
        Self {
            transport: "stdio".to_string(),
            scope: Scope::User,
            enabled: true,
            ..Default::default()
        }
    }

    /// A draft describing an existing server.
    pub fn from_server(server: &McpServer) -> Self {
        let args = if server.args.is_empty() {
            String::new()
        } else {
            serde_json::to_string(&server.args).unwrap_or_default()
        };
        let env = if server.env_raw.is_empty() {
            String::new()
        } else {
            let mut map = Map::new();
            for (k, v) in &server.env_raw {
                map.insert(k.clone(), Value::String(v.clone()));
            }
            serde_json::to_string_pretty(&Value::Object(map)).unwrap_or_default()
        };
        Self {
            name: server.name.clone(),
            scope: server.scope,
            transport: server.transport.to_string(),
            command: server.command.clone().unwrap_or_default(),
            args,
            url: server.url.clone().unwrap_or_default(),
            enabled: server.enabled,
            env,
        }
    }

    /// Why this draft cannot be saved, if it cannot.
    ///
    /// Checked before writing rather than after, so a half-valid server never
    /// reaches the file and fails at connect time with a worse message.
    pub fn validation_error(&self) -> Option<String> {
        if self.name.trim().is_empty() {
            return Some("A name is required.".to_string());
        }
        if self.name.contains(char::is_whitespace) {
            return Some("A name cannot contain spaces.".to_string());
        }
        match self.transport.as_str() {
            "stdio" if self.command.trim().is_empty() => {
                return Some("A stdio server needs a command.".to_string());
            }
            "http" if self.url.trim().is_empty() => {
                return Some("An HTTP server needs a URL.".to_string());
            }
            "http" if !self.url.starts_with("http://") && !self.url.starts_with("https://") => {
                return Some("The URL must start with http:// or https://.".to_string());
            }
            _ => {}
        }
        if self.transport == "stdio" {
            if let Err(e) = parse_args_json(&self.args) {
                return Some(e);
            }
        }
        if let Err(e) = parse_env_json(&self.env) {
            return Some(e);
        }
        None
    }
}

/// Parses a JSON array text into a list of argument strings.
///
/// An empty string or `[]` is accepted and means no arguments.
fn parse_args_json(text: &str) -> Result<Vec<String>, String> {
    let trimmed = text.trim();
    if trimmed.is_empty() || trimmed == "[]" {
        return Ok(Vec::new());
    }
    let arr: Vec<serde_json::Value> = serde_json::from_str(trimmed).map_err(|_| {
        r#"Arguments must be a valid JSON array, e.g. ["-y", "server"]."#.to_string()
    })?;
    arr.into_iter()
        .map(|v| {
            v.as_str()
                .map(str::to_string)
                .ok_or_else(|| "Each argument must be a string.".to_string())
        })
        .collect()
}

/// Parses the environment JSON object into an ordered list of name/value pairs.
///
/// An empty string or `{}` means no environment variables. Values are taken
/// verbatim, so whitespace, `=`, newlines and Unicode survive untouched.
///
/// Names must be non-empty and contain no whitespace, NUL or `=`. Values must
/// contain no NUL. On Windows environment names are case-insensitive, so
/// `Path` and `PATH` collide; elsewhere they are distinct. Error messages
/// never echo a value, so a secret-bearing entry cannot leak into a notice.
fn parse_env_json(text: &str) -> Result<Vec<(String, String)>, String> {
    let trimmed = text.trim();
    if trimmed.is_empty() || trimmed == "{}" {
        return Ok(Vec::new());
    }
    let value: Value = serde_json::from_str(trimmed)
        .map_err(|_| r#"Environment must be a JSON object, e.g. {"KEY": "value"}."#.to_string())?;
    let Value::Object(obj) = value else {
        return Err(r#"Environment must be a JSON object, e.g. {"KEY": "value"}."#.to_string());
    };

    let mut entries = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for (key, val) in &obj {
        if key.is_empty() {
            return Err("Environment variable name cannot be empty.".to_string());
        }
        if key.chars().any(char::is_whitespace) {
            return Err(format!(
                "Environment variable name cannot contain whitespace: {key:?}"
            ));
        }
        if key.contains('\0') {
            return Err(format!(
                "Environment variable name cannot contain a NUL byte: {key:?}"
            ));
        }
        if key.contains('=') {
            return Err(format!("Environment variable name cannot contain '=': {key:?}"));
        }
        let Value::String(v) = val else {
            // Deliberately does not print the value: it may be a secret.
            return Err(format!("Environment variable {key:?} must be a string."));
        };
        if v.contains('\0') {
            return Err(format!(
                "Environment variable {key:?} has a value containing a NUL byte."
            ));
        }
        // Windows environment names are case-insensitive; other platforms are
        // not. Normalise the dedup key accordingly.
        let dedup = if cfg!(windows) {
            key.to_uppercase()
        } else {
            key.clone()
        };
        if !seen.insert(dedup) {
            return Err(format!(
                "Duplicate environment variable (names are case-insensitive on Windows): {key}"
            ));
        }
        entries.push((key.clone(), v.clone()));
    }
    Ok(entries)
}

/// Removes a named server from a single `.mcp.json` file if it is present.
fn remove_server_from_file(path: &Path, name: &str) -> Result<(), ConfigError> {
    let mut doc = read_json(path)?;
    if let Some(servers) = doc.get_mut("mcpServers").and_then(Value::as_object_mut) {
        if servers.remove(name).is_some() {
            return write_json(path, &doc);
        }
    }
    Ok(())
}

/// The `.mcp.json` path that backs a given scope.
fn mcp_path_for(paths: &Paths, scope: Scope) -> PathBuf {
    match scope {
        Scope::User => paths.user_mcp(),
        Scope::Project => paths.project_mcp(),
    }
}

/// Reads a `.mcp.json` document and rejects a shape the editor cannot safely
/// rewrite, *before* any write happens.
///
/// A root that is not an object, or an `mcpServers` that is present but not an
/// object, would otherwise be silently reset to an empty object and the user's
/// data destroyed. Erroring here leaves the (unusual) file untouched.
fn read_mcp_document(path: &Path) -> Result<Value, ConfigError> {
    let doc = read_json(path)?;
    if !doc.is_object() {
        return Err(ConfigError::Invalid(format!(
            "{} is not a valid MCP config (expected a JSON object at the top level).",
            path.display()
        )));
    }
    if let Some(servers) = doc.get("mcpServers") {
        if !servers.is_object() {
            return Err(ConfigError::Invalid(format!(
                "{} has a malformed \"mcpServers\" (expected a JSON object).",
                path.display()
            )));
        }
    }
    Ok(doc)
}

/// Keys the save owns outright and rebuilds from the draft every time.
///
/// Everything else on an entry (`auth`, `headers`, future settings) is treated
/// as opaque and carried across untouched. `type` is owned because a stale
/// `"type": "http"` left on a server switched to stdio would make the loader
/// treat it as HTTP; it is rewritten from the chosen transport instead.
const MANAGED_ENTRY_KEYS: &[&str] = &["command", "args", "url", "type", "disabled", "enabled", "env"];

/// Writes a server into the `.mcp.json` for its scope.
///
/// `original` is the immutable `(scope, name)` the editor opened on, or `None`
/// for a brand-new server. It — not the draft's current name/scope — decides
/// which on-disk entry is being replaced, so a user entry shadowed by a
/// project entry of the same name is never confused for its neighbour.
///
/// Behaviour:
/// - **Add** (`original` is `None`): the name must not already exist in the
///   target scope. The opposite scope is left completely untouched, so a
///   deliberately shadowed pair is preserved.
/// - **Edit / rename in the same scope**: a single atomic write replaces the
///   old entry (and drops the old name on a rename).
/// - **Move across scopes**: the destination is written *first*; only after it
///   is safely on disk is the source entry removed. If that removal fails the
///   call reports the half-done state rather than claiming success.
///
/// Unknown fields are preserved from the *exact* original entry (its own
/// scope's file), so auth, headers and future settings survive an edit or a
/// cross-scope move. Renaming or moving onto an existing name is a collision
/// and is rejected before anything is written.
pub fn save_mcp_server(
    paths: &Paths,
    draft: &McpDraft,
    original: Option<&McpServerId>,
) -> Result<(), ConfigError> {
    if let Some(problem) = draft.validation_error() {
        return Err(ConfigError::Invalid(problem));
    }

    let dest_path = mcp_path_for(paths, draft.scope);
    let cross_file = original.map_or(false, |id| id.scope != draft.scope);

    // Read and validate every file we will touch *before* writing any of them,
    // so a malformed source or destination aborts without corrupting either.
    let mut dest_doc = read_mcp_document(&dest_path)?;
    let source_doc = match original {
        Some(id) if cross_file => Some(read_mcp_document(&mcp_path_for(paths, id.scope))?),
        _ => None,
    };

    // The exact entry being replaced, read from its own scope's file. For a
    // same-scope edit that is the destination doc; for a move it is the source.
    let source_entry: Option<Map<String, Value>> = original.and_then(|id| {
        let doc = source_doc.as_ref().unwrap_or(&dest_doc);
        doc.get("mcpServers")
            .and_then(Value::as_object)
            .and_then(|servers| servers.get(&id.name))
            .and_then(Value::as_object)
            .cloned()
    });

    // Parse args and env — already validated above, so unwrap is safe.
    let parsed_args = parse_args_json(&draft.args).expect("already validated");
    let args_values: Vec<Value> = parsed_args.into_iter().map(Value::String).collect();
    let env_entries = parse_env_json(&draft.env).expect("already validated");

    // Build the new JSON entry from the managed fields.
    let mut entry = Map::new();
    let is_http = draft.transport.as_str() == "http";
    if is_http {
        entry.insert("url".into(), Value::String(draft.url.trim().to_string()));
    } else {
        entry.insert(
            "command".into(),
            Value::String(draft.command.trim().to_string()),
        );
        if !args_values.is_empty() {
            entry.insert("args".into(), Value::Array(args_values));
        }
    }
    if !draft.enabled {
        entry.insert("disabled".into(), Value::Bool(true));
    }
    if !env_entries.is_empty() {
        let mut env_map = Map::new();
        for (k, v) in &env_entries {
            env_map.insert(k.clone(), Value::String(v.clone()));
        }
        entry.insert("env".into(), Value::Object(env_map));
    }

    // Carry across every unmodelled field from the exact original entry, then
    // rewrite `type` from the chosen transport so a switched server can never
    // keep a transport marker that contradicts its fields.
    if let Some(existing) = &source_entry {
        for (key, value) in existing {
            if !MANAGED_ENTRY_KEYS.contains(&key.as_str()) {
                entry.entry(key.clone()).or_insert(value.clone());
            }
        }
    }
    if is_http {
        // Preserve an explicit HTTP transport spelling if it had one, else be
        // explicit about the transport we are writing.
        let type_ = source_entry
            .as_ref()
            .and_then(|e| e.get("type"))
            .and_then(Value::as_str)
            .filter(|t| matches!(*t, "http" | "streamable-http"))
            .unwrap_or("http")
            .to_string();
        entry.insert("type".into(), Value::String(type_));
    }
    // stdio intentionally writes no `type`: its absence is what the loader
    // reads as stdio, and any inherited `type` was dropped above.

    let servers = dest_doc
        .as_object_mut()
        .expect("validated as object")
        .entry("mcpServers".to_string())
        .or_insert_with(|| Value::Object(Map::new()))
        .as_object_mut()
        .expect("validated as object");

    // A destination entry under the new name that is *not* the very server we
    // opened on is somebody else — refuse to overwrite it.
    let editing_same_slot =
        original.map_or(false, |id| !cross_file && id.name == draft.name);
    if servers.contains_key(&draft.name) && !editing_same_slot {
        return Err(ConfigError::Invalid(format!(
            "A server named '{}' already exists in the {} scope.",
            draft.name,
            draft.scope.label()
        )));
    }

    servers.insert(draft.name.clone(), Value::Object(entry));

    // A same-scope rename drops the old name in the same write so the file
    // never transiently holds two entries for one logical server.
    if let Some(id) = original {
        if !cross_file && id.name != draft.name {
            servers.remove(&id.name);
        }
    }

    // Write the destination. On a same-scope edit this is the whole operation;
    // if it fails the original entry is still intact.
    write_json(&dest_path, &dest_doc)?;

    // A move writes the destination first and only then retires the source. If
    // the source removal fails the new copy is already safe, so we report the
    // half-done state instead of a bare success or a misleading error.
    if cross_file {
        let id = original.expect("cross_file implies an original");
        let source_path = mcp_path_for(paths, id.scope);
        if let Err(err) = remove_server_from_file(&source_path, &id.name) {
            return Err(ConfigError::Invalid(format!(
                "Saved '{}' to the {} scope, but removing the old copy from the {} scope failed: {err}",
                draft.name,
                draft.scope.label(),
                id.scope.label()
            )));
        }
    }

    Ok(())
}

/// Removes a server from whichever file defines it.
pub fn delete_mcp_server(paths: &Paths, name: &str) -> Result<bool, ConfigError> {
    let mut removed = false;
    for path in [paths.project_mcp(), paths.user_mcp()] {
        let mut document = read_json(&path)?;
        let Some(servers) = document
            .get_mut("mcpServers")
            .and_then(Value::as_object_mut)
        else {
            continue;
        };
        if servers.remove(name).is_some() {
            write_json(&path, &document)?;
            removed = true;
        }
    }
    Ok(removed)
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

    pub(super) fn temp_paths() -> (tempdir::TempDir, Paths) {
        let dir = tempdir::TempDir::new();
        let paths = Paths::new(dir.path().join("project")).with_user_root(dir.path().join("user"));
        std::fs::create_dir_all(&paths.user_root).expect("user root");
        std::fs::create_dir_all(&paths.project_root).expect("project root");
        (dir, paths)
    }

    /// A minimal scoped temporary directory, to avoid a dev-dependency.
    pub(super) mod tempdir {
        use std::path::{Path, PathBuf};
        use std::sync::atomic::{AtomicU64, Ordering};

        /// Guarantees a distinct directory per instance.
        ///
        /// The wall clock alone is not enough: Windows updates it on a ~15 ms
        /// tick, so two tests constructed inside the same tick collide, and
        /// whichever finishes first deletes the directory the other is still
        /// using — a failure that only shows up under parallel execution.
        static COUNTER: AtomicU64 = AtomicU64::new(0);

        pub struct TempDir(PathBuf);

        impl TempDir {
            pub fn new() -> Self {
                let base = std::env::temp_dir().join(format!(
                    "coda-tui-test-{}-{}-{}",
                    std::process::id(),
                    std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .map(|d| d.as_nanos())
                        .unwrap_or(0),
                    COUNTER.fetch_add(1, Ordering::Relaxed)
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
    fn json_config_accepts_optional_utf8_bom_without_rewriting() {
        let (_dir, paths) = temp_paths();
        for path in [paths.settings(), paths.user_mcp(), paths.project_mcp()] {
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            for prefix in ["", "\u{feff}"] {
                let content = format!("{prefix}{{\"preserved\":\"value\"}}");
                std::fs::write(&path, &content).unwrap();
                assert_eq!(read_json(&path).unwrap(), json!({"preserved": "value"}));
                assert_eq!(std::fs::read_to_string(&path).unwrap(), content);
            }
        }
    }

    #[test]
    fn bom_prefixed_settings_preserve_model_selection() {
        let (_dir, paths) = temp_paths();
        std::fs::write(
            paths.settings(),
            "\u{feff}{\"modelByProvider\":{\"github-copilot\":\"claude-opus-5\"}}",
        ).unwrap();
        assert_eq!(
            Settings::load(&paths).unwrap().model_for("github-copilot"),
            Some("claude-opus-5"),
        );
    }

    #[test]
    fn json_config_bom_does_not_hide_malformed_content() {
        let (_dir, paths) = temp_paths();
        for content in ["\u{feff}{broken", "\u{feff}\u{feff}{}", " \u{feff}{}"] {
            std::fs::write(paths.settings(), content).unwrap();
            assert!(matches!(read_json(&paths.settings()), Err(ConfigError::Parse { .. })));
            assert_eq!(std::fs::read_to_string(paths.settings()).unwrap(), content);
        }
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
    fn a_second_save_does_not_reassert_the_first_edit_over_someone_elses_update() {
        // The baseline must move with each successful save. Otherwise every
        // later save keeps re-applying the earlier edit, overwriting whatever
        // another process wrote to that key in between.
        let (_dir, paths) = temp_paths();
        write(&paths.settings(), json!({ "theme": "dark", "outputStyle": "plain" }));

        let mut settings = Settings::load(&paths).expect("load");
        settings.set_theme("solarized");
        settings.save().expect("first save");

        // Another process changes the very key this instance last wrote.
        let mut other = Settings::load(&paths).expect("load");
        other.set_theme("high-contrast");
        other.save().expect("concurrent save");

        // This instance now saves an unrelated change.
        settings.set_output_style("verbose");
        settings.save().expect("second save");

        let reloaded = read_json(&paths.settings()).expect("reread");
        assert_eq!(reloaded["outputStyle"], "verbose", "the new edit must land");
        assert_eq!(
            reloaded["theme"], "high-contrast",
            "a second save must not re-apply the first edit over a newer value"
        );
    }

    #[test]
    fn saving_does_not_revert_a_key_another_process_committed() {
        // The auth transaction writes `defaultProvider` while this Settings
        // instance is alive. Writing back the whole loaded document would undo
        // a sign-in the user just completed.
        let (_dir, paths) = temp_paths();
        write(&paths.settings(), json!({ "theme": "dark", "defaultProvider": "github-copilot" }));

        let mut settings = Settings::load(&paths).expect("load");
        settings.set_theme("solarized");

        coda_boot::settings_store::apply_patch(
            &paths.settings(),
            &coda_auth::service::AuthSettingsPatch::empty()
                .with_default_provider(Some("claude-ai".into())),
        )
        .expect("the auth commit lands first");

        settings.save().expect("save");

        let reloaded = read_json(&paths.settings()).expect("reread");
        assert_eq!(reloaded["theme"], "solarized", "this writer's own change must land");
        assert_eq!(
            reloaded["defaultProvider"], "claude-ai",
            "a stale root snapshot must not revert the committed provider"
        );
    }

    #[test]
    fn saving_preserves_a_concurrent_change_to_another_providers_model() {
        let (_dir, paths) = temp_paths();
        write(
            &paths.settings(),
            json!({ "modelByProvider": { "github-copilot": "gpt-5", "claude-ai": "opus" } }),
        );

        let mut settings = Settings::load(&paths).expect("load");
        settings.set_model_for("claude-ai", "opus-5.1");

        // Another process changes a different row of the same object.
        let mut concurrent = Settings::load(&paths).expect("load");
        concurrent.set_model_for("github-copilot", "gpt-6");
        concurrent.save().expect("save");

        settings.save().expect("save");

        let reloaded = read_json(&paths.settings()).expect("reread");
        assert_eq!(reloaded["modelByProvider"]["claude-ai"], "opus-5.1");
        assert_eq!(
            reloaded["modelByProvider"]["github-copilot"], "gpt-6",
            "an unrelated concurrent edit must survive"
        );
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

#[cfg(test)]
mod mcp_editing_tests {
    use super::*;

    fn temp() -> (super::tests::tempdir::TempDir, Paths) {
        super::tests::temp_paths()
    }

    fn stdio(name: &str) -> McpDraft {
        McpDraft {
            name: name.into(),
            scope: Scope::User,
            transport: "stdio".into(),
            command: "npx".into(),
            args: "[\"-y\",\"server-everything\"]".into(),
            url: String::new(),
            enabled: true,
            env: String::new(),
        }
    }

    #[test]
    fn a_saved_server_reads_back_the_way_it_was_written() {
        let (_dir, paths) = temp();
        save_mcp_server(&paths, &stdio("everything"), None).expect("save");

        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "everything").expect("saved");
        assert_eq!(saved.transport, "stdio");
        assert_eq!(saved.command.as_deref(), Some("npx"));
        assert_eq!(saved.args, vec!["-y", "server-everything"]);
        assert!(saved.enabled);
    }

    #[test]
    fn an_http_server_round_trips_its_url() {
        let (_dir, paths) = temp();
        let draft = McpDraft {
            transport: "http".into(),
            url: "https://example.com/mcp".into(),
            command: String::new(),
            args: String::new(),
            ..stdio("remote")
        };
        save_mcp_server(&paths, &draft, None).expect("save");

        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "remote").expect("saved");
        assert_eq!(saved.transport, "http");
        assert_eq!(saved.url.as_deref(), Some("https://example.com/mcp"));
        assert!(saved.command.is_none(), "an http server kept a command");
    }

    #[test]
    fn changing_scope_moves_the_entry_and_leaves_nothing_behind() {
        // Moving a server to another scope is an *edit* with an explicit
        // identity: two files defining one name means the loader serves
        // whichever it saw first, and disabling one would appear to do nothing.
        let (_dir, paths) = temp();
        save_mcp_server(&paths, &stdio("moving"), None).expect("save to user");

        let mut moved = stdio("moving");
        moved.scope = Scope::Project;
        save_mcp_server(&paths, &moved, Some(&McpServerId::new(Scope::User, "moving")))
            .expect("move to project");

        let servers = load_mcp_servers(&paths).expect("load");
        let matching: Vec<_> = servers.iter().filter(|s| s.name == "moving").collect();
        assert_eq!(matching.len(), 1, "the server exists in both scopes");
        assert_eq!(matching[0].scope, Scope::Project);
    }

    #[test]
    fn adding_a_name_that_already_exists_in_the_scope_is_rejected() {
        let (_dir, paths) = temp();
        save_mcp_server(&paths, &stdio("dup"), None).expect("first add");
        // A brand-new add (no identity) must not clobber an existing server.
        assert!(
            save_mcp_server(&paths, &stdio("dup"), None).is_err(),
            "adding an existing name was allowed"
        );
    }

    #[test]
    fn adding_does_not_disturb_a_shadowed_entry_in_the_other_scope() {
        // A project entry may deliberately shadow a user entry of the same
        // name; adding to one scope must never delete the other.
        let (_dir, paths) = temp();
        save_mcp_server(&paths, &stdio("shared"), None).expect("user add");
        let mut project = stdio("shared");
        project.scope = Scope::Project;
        save_mcp_server(&paths, &project, None).expect("project add");

        // Both files still define the server.
        let user_doc = read_json(&paths.user_mcp()).expect("read user");
        let project_doc = read_json(&paths.project_mcp()).expect("read project");
        assert!(!user_doc["mcpServers"]["shared"].is_null(), "user copy was removed");
        assert!(
            !project_doc["mcpServers"]["shared"].is_null(),
            "project copy was removed"
        );
    }

    #[test]
    fn editing_a_shadowed_entry_touches_only_that_scope() {
        // A user entry and a project entry share a name. Editing the user one
        // must change only the user file and leave the project entry intact.
        let (_dir, paths) = temp();
        let mut user = stdio("shadowed");
        user.command = "user-cmd".into();
        save_mcp_server(&paths, &user, None).expect("user add");
        let mut project = stdio("shadowed");
        project.scope = Scope::Project;
        project.command = "project-cmd".into();
        save_mcp_server(&paths, &project, None).expect("project add");

        let mut edited = user.clone();
        edited.command = "user-cmd-2".into();
        save_mcp_server(&paths, &edited, Some(&McpServerId::new(Scope::User, "shadowed")))
            .expect("edit user copy");

        let user_doc = read_json(&paths.user_mcp()).expect("read user");
        let project_doc = read_json(&paths.project_mcp()).expect("read project");
        assert_eq!(user_doc["mcpServers"]["shadowed"]["command"], "user-cmd-2");
        assert_eq!(
            project_doc["mcpServers"]["shadowed"]["command"],
            "project-cmd",
            "the opposite-scope entry was disturbed"
        );
    }

    #[test]
    fn saving_preserves_keys_this_build_does_not_model() {
        // A server written by a newer version must not be quietly stripped of
        // its settings just because it was opened in the editor.
        let (_dir, paths) = temp();
        let path = paths.user_mcp();
        write_json(
            &path,
            &serde_json::json!({
                "mcpServers": {
                    "everything": { "command": "old", "futureSetting": { "keep": true } }
                }
            }),
        )
        .expect("seed");

        save_mcp_server(
            &paths,
            &stdio("everything"),
            Some(&McpServerId::new(Scope::User, "everything")),
        )
        .expect("save");

        let document = read_json(&path).expect("read");
        let entry = &document["mcpServers"]["everything"];
        assert_eq!(entry["command"], serde_json::json!("npx"), "the edit was lost");
        assert_eq!(
            entry["futureSetting"]["keep"],
            serde_json::json!(true),
            "an unmodelled setting was stripped"
        );
    }

    #[test]
    fn a_disabled_server_stays_disabled() {
        let (_dir, paths) = temp();
        let mut draft = stdio("off");
        draft.enabled = false;
        save_mcp_server(&paths, &draft, None).expect("save");

        let servers = load_mcp_servers(&paths).expect("load");
        assert!(!servers.iter().find(|s| s.name == "off").expect("saved").enabled);
    }

    #[test]
    fn deleting_removes_it_from_disk() {
        let (_dir, paths) = temp();
        save_mcp_server(&paths, &stdio("gone"), None).expect("save");
        assert!(delete_mcp_server(&paths, "gone").expect("delete"));
        assert!(!load_mcp_servers(&paths).expect("load").iter().any(|s| s.name == "gone"));
        assert!(!delete_mcp_server(&paths, "gone").expect("delete again"));
    }

    #[test]
    fn an_invalid_draft_is_refused_before_it_reaches_the_file() {
        let (_dir, paths) = temp();
        for (draft, why) in [
            (McpDraft { name: String::new(), ..stdio("x") }, "no name"),
            (McpDraft { name: "two words".into(), ..stdio("x") }, "spaced name"),
            (McpDraft { command: String::new(), ..stdio("x") }, "no command"),
            (
                McpDraft { transport: "http".into(), url: String::new(), ..stdio("x") },
                "no url",
            ),
            (
                McpDraft { transport: "http".into(), url: "example.com".into(), ..stdio("x") },
                "url without a scheme",
            ),
        ] {
            assert!(draft.validation_error().is_some(), "{why} was accepted");
            assert!(save_mcp_server(&paths, &draft, None).is_err(), "{why} reached the file");
        }
    }

    // -- Args round-trip tests -----------------------------------------------

    #[test]
    fn args_with_spaces_survive_a_round_trip() {
        // split_whitespace would shatter "C:\Program Files\server.js" into
        // three tokens; the JSON array preserves it as one argument.
        let (_dir, paths) = temp();
        let draft = McpDraft {
            args: r#"["node","C:\\Program Files\\server.js","--port","8080"]"#.into(),
            ..stdio("spaced")
        };
        save_mcp_server(&paths, &draft, None).expect("save");
        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "spaced").expect("saved");
        assert_eq!(
            saved.args,
            vec!["node", r"C:\Program Files\server.js", "--port", "8080"]
        );
    }

    #[test]
    fn empty_args_survive_a_round_trip() {
        let (_dir, paths) = temp();
        let draft = McpDraft { args: String::new(), ..stdio("noargs") };
        save_mcp_server(&paths, &draft, None).expect("save");
        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "noargs").expect("saved");
        assert!(saved.args.is_empty(), "empty args did not round-trip");
    }

    #[test]
    fn args_with_unicode_and_quotes_survive_a_round_trip() {
        let (_dir, paths) = temp();
        let draft = McpDraft {
            args: r#"["--name","héllo wörld","--flag","it's \"quoted\""]"#.into(),
            ..stdio("unicode")
        };
        save_mcp_server(&paths, &draft, None).expect("save");
        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "unicode").expect("saved");
        assert_eq!(saved.args[0], "--name");
        assert_eq!(saved.args[1], "héllo wörld");
        assert_eq!(saved.args[3], r#"it's "quoted""#);
    }

    #[test]
    fn invalid_args_json_is_rejected() {
        let draft = McpDraft { args: "not json".into(), ..stdio("x") };
        assert!(draft.validation_error().is_some(), "invalid JSON was accepted");
    }

    #[test]
    fn non_string_args_are_rejected() {
        let draft = McpDraft { args: "[1, 2, 3]".into(), ..stdio("x") };
        assert!(draft.validation_error().is_some(), "non-string elements were accepted");
    }

    // -- Env round-trip tests ------------------------------------------------

    #[test]
    fn env_values_survive_a_round_trip() {
        let (_dir, paths) = temp();
        let draft = McpDraft {
            env: r#"{ "API_KEY": "secret123", "BASE_URL": "https://api.example.com" }"#.into(),
            ..stdio("env-test")
        };
        save_mcp_server(&paths, &draft, None).expect("save");
        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "env-test").expect("saved");
        assert!(saved.env_keys.contains(&"API_KEY".to_string()));
        assert!(saved.env_keys.contains(&"BASE_URL".to_string()));
        assert_eq!(
            saved.env_raw.iter().find(|(k, _)| k == "API_KEY").map(|(_, v)| v.as_str()),
            Some("secret123")
        );
    }

    #[test]
    fn env_value_containing_equals_is_preserved() {
        // Values that themselves contain '=' must survive verbatim.
        let (_dir, paths) = temp();
        let draft = McpDraft {
            env: r#"{ "CONN": "host=localhost;port=5432" }"#.into(),
            ..stdio("eq-val")
        };
        save_mcp_server(&paths, &draft, None).expect("save");
        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "eq-val").expect("saved");
        assert_eq!(
            saved.env_raw.iter().find(|(k, _)| k == "CONN").map(|(_, v)| v.as_str()),
            Some("host=localhost;port=5432")
        );
    }

    #[test]
    fn env_value_whitespace_and_unicode_survive_verbatim() {
        // Leading/trailing whitespace and Unicode must not be trimmed or
        // mangled — the old KEY=VALUE parser trimmed the whole line.
        let (_dir, paths) = temp();
        let draft = McpDraft {
            env: r#"{ "PADDED": "  spaced value  ", "GREET": "héllo wörld" }"#.into(),
            ..stdio("ws")
        };
        save_mcp_server(&paths, &draft, None).expect("save");
        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "ws").expect("saved");
        assert_eq!(
            saved.env_raw.iter().find(|(k, _)| k == "PADDED").map(|(_, v)| v.as_str()),
            Some("  spaced value  "),
            "value whitespace was stripped"
        );
        assert_eq!(
            saved.env_raw.iter().find(|(k, _)| k == "GREET").map(|(_, v)| v.as_str()),
            Some("héllo wörld")
        );
    }

    #[test]
    fn env_multiline_value_round_trips() {
        // A JSON object represents newlines directly, unlike KEY=VALUE lines.
        let (_dir, paths) = temp();
        let draft = McpDraft {
            env: "{ \"PEM\": \"line1\\nline2\\nline3\" }".into(),
            ..stdio("multiline")
        };
        save_mcp_server(&paths, &draft, None).expect("save");
        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "multiline").expect("saved");
        assert_eq!(
            saved.env_raw.iter().find(|(k, _)| k == "PEM").map(|(_, v)| v.as_str()),
            Some("line1\nline2\nline3")
        );
    }

    #[test]
    fn env_secret_reference_is_preserved_not_resolved() {
        let (_dir, paths) = temp();
        let draft = McpDraft {
            env: r#"{ "TOKEN": "coda-secret:store/my-key" }"#.into(),
            ..stdio("secret-ref")
        };
        save_mcp_server(&paths, &draft, None).expect("save");
        let doc = read_json(&paths.user_mcp()).expect("read");
        assert_eq!(
            doc["mcpServers"]["secret-ref"]["env"]["TOKEN"],
            "coda-secret:store/my-key",
            "the secret reference was resolved or altered"
        );
    }

    #[cfg(windows)]
    #[test]
    fn env_names_are_case_insensitive_on_windows() {
        let draft = McpDraft {
            env: r#"{ "Path": "a", "PATH": "b" }"#.into(),
            ..stdio("x")
        };
        assert!(
            draft.validation_error().is_some(),
            "case-only duplicate was accepted on Windows"
        );
    }

    #[cfg(not(windows))]
    #[test]
    fn env_names_are_case_sensitive_off_windows() {
        let draft = McpDraft {
            env: r#"{ "Path": "a", "PATH": "b" }"#.into(),
            ..stdio("x")
        };
        assert!(
            draft.validation_error().is_none(),
            "case-only names were wrongly rejected off Windows"
        );
    }

    #[test]
    fn env_name_with_whitespace_is_rejected() {
        let draft = McpDraft { env: r#"{ "BAD KEY": "v" }"#.into(), ..stdio("x") };
        assert!(draft.validation_error().is_some(), "whitespace name was accepted");
    }

    #[test]
    fn empty_env_key_is_rejected() {
        let draft = McpDraft { env: r#"{ "": "value" }"#.into(), ..stdio("x") };
        assert!(draft.validation_error().is_some(), "empty key was accepted");
    }

    #[test]
    fn non_object_env_is_rejected() {
        let draft = McpDraft { env: r#"["not","an","object"]"#.into(), ..stdio("x") };
        assert!(draft.validation_error().is_some(), "a non-object env was accepted");
    }

    #[test]
    fn non_string_env_value_is_rejected() {
        let draft = McpDraft { env: r#"{ "N": 42 }"#.into(), ..stdio("x") };
        assert!(draft.validation_error().is_some(), "a non-string value was accepted");
    }

    #[test]
    fn env_error_does_not_echo_the_value() {
        // A malformed object must not leak the secret-bearing value into the
        // error message.
        let draft = McpDraft {
            env: r#"{ "TOKEN": 12345 }"#.into(),
            ..stdio("x")
        };
        let err = draft.validation_error().expect("should be rejected");
        assert!(!err.contains("12345"), "the value leaked into the error: {err}");
    }

    #[test]
    fn empty_env_text_means_no_env_vars() {
        let (_dir, paths) = temp();
        save_mcp_server(&paths, &stdio("noenv"), None).expect("save");
        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "noenv").expect("saved");
        assert!(saved.env_keys.is_empty());
        assert!(saved.env_raw.is_empty());
    }

    // -- Rename and collision tests ------------------------------------------

    #[test]
    fn rename_retains_env_auth_and_unknown_fields() {
        let (_dir, paths) = temp();
        let path = paths.user_mcp();
        super::write_json(
            &path,
            &serde_json::json!({
                "mcpServers": {
                    "old-name": {
                        "command": "node",
                        "env": { "KEY": "val" },
                        "auth": { "type": "bearer", "token": "tok" },
                        "futureSetting": 42
                    }
                }
            }),
        )
        .expect("seed");
        let renamed = McpDraft {
            name: "new-name".into(),
            args: String::new(),
            ..stdio("old-name")
        };
        save_mcp_server(&paths, &renamed, Some(&McpServerId::new(Scope::User, "old-name")))
            .expect("save");

        let doc = super::read_json(&path).expect("read");
        // Old name must be gone.
        assert!(doc["mcpServers"]["old-name"].is_null(), "old name persists");
        // New entry must exist with preserved fields.
        let entry = &doc["mcpServers"]["new-name"];
        assert!(!entry.is_null(), "new name missing");
        assert_eq!(entry["auth"]["type"], "bearer", "auth was stripped on rename");
        assert_eq!(entry["futureSetting"], 42, "unknown field was stripped");
    }

    #[test]
    fn rename_to_existing_name_is_rejected() {
        let (_dir, paths) = temp();
        save_mcp_server(&paths, &stdio("alpha"), None).expect("save alpha");
        save_mcp_server(&paths, &stdio("beta"), None).expect("save beta");

        let collide = McpDraft { name: "beta".into(), ..stdio("alpha") };
        assert!(
            save_mcp_server(&paths, &collide, Some(&McpServerId::new(Scope::User, "alpha"))).is_err(),
            "collision was not detected"
        );
        // Both originals must still exist.
        let servers = load_mcp_servers(&paths).expect("load");
        assert!(servers.iter().any(|s| s.name == "alpha"), "alpha was destroyed");
        assert!(servers.iter().any(|s| s.name == "beta"), "beta was destroyed");
    }

    #[test]
    fn moving_onto_an_existing_name_in_the_other_scope_is_rejected() {
        // A cross-scope move whose destination name is already taken must be
        // refused before either file is touched — collision detection has to
        // cover moves, not just same-scope renames.
        let (_dir, paths) = temp();
        save_mcp_server(&paths, &stdio("srv"), None).expect("user srv");
        let mut project = stdio("srv");
        project.scope = Scope::Project;
        project.command = "project-cmd".into();
        save_mcp_server(&paths, &project, None).expect("project srv");

        // Move the user srv into the project scope, where "srv" already exists.
        let mut moved = stdio("srv");
        moved.scope = Scope::Project;
        assert!(
            save_mcp_server(&paths, &moved, Some(&McpServerId::new(Scope::User, "srv"))).is_err(),
            "the move clobbered an existing project server"
        );
        // Both originals survive untouched.
        let user_doc = read_json(&paths.user_mcp()).expect("read user");
        let project_doc = read_json(&paths.project_mcp()).expect("read project");
        assert!(!user_doc["mcpServers"]["srv"].is_null(), "user srv was destroyed");
        assert_eq!(
            project_doc["mcpServers"]["srv"]["command"],
            "project-cmd",
            "project srv was overwritten"
        );
    }

    #[test]
    fn failed_validation_leaves_old_entry_intact() {
        let (_dir, paths) = temp();
        save_mcp_server(&paths, &stdio("existing"), None).expect("initial save");

        // Try to save an invalid draft under the same name.
        let bad = McpDraft { command: String::new(), ..stdio("existing") };
        assert!(save_mcp_server(&paths, &bad, Some(&McpServerId::new(Scope::User, "existing"))).is_err());

        // Original must still be intact.
        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "existing").expect("should exist");
        assert_eq!(saved.command.as_deref(), Some("npx"), "original command was overwritten");
    }

    #[test]
    fn a_malformed_source_file_aborts_before_overwriting() {
        // An edit whose file is not the shape we can rewrite must error and
        // leave the (unusual) file exactly as it was, not reset it to {}.
        let (_dir, paths) = temp();
        let path = paths.user_mcp();
        std::fs::create_dir_all(path.parent().unwrap()).expect("mkdir");
        std::fs::write(&path, r#"{"mcpServers": ["not","an","object"]}"#).expect("seed");

        let draft = stdio("whatever");
        assert!(
            save_mcp_server(&paths, &draft, Some(&McpServerId::new(Scope::User, "whatever"))).is_err(),
            "a malformed mcpServers was silently overwritten"
        );
        // The original bytes are still there.
        let text = std::fs::read_to_string(&path).expect("read");
        assert!(text.contains("[\"not\",\"an\",\"object\"]") || text.contains("not"), "the file was reset: {text}");
    }

    #[test]
    fn switching_http_to_stdio_removes_the_http_type_marker() {
        // A stale `"type": "http"` left on a server switched to stdio would
        // make the loader treat it as HTTP. It must be rewritten away.
        let (_dir, paths) = temp();
        let path = paths.user_mcp();
        super::write_json(
            &path,
            &serde_json::json!({
                "mcpServers": {
                    "switch": { "type": "http", "url": "https://example.com/mcp", "headers": { "X": "1" } }
                }
            }),
        )
        .expect("seed");

        // Re-open as stdio with a command.
        let to_stdio = McpDraft {
            name: "switch".into(),
            scope: Scope::User,
            transport: "stdio".into(),
            command: "npx".into(),
            args: String::new(),
            url: String::new(),
            enabled: true,
            env: String::new(),
        };
        save_mcp_server(&paths, &to_stdio, Some(&McpServerId::new(Scope::User, "switch")))
            .expect("switch to stdio");

        let doc = read_json(&path).expect("read");
        let entry = &doc["mcpServers"]["switch"];
        assert!(entry.get("type").is_none(), "the http type marker survived");
        assert!(entry.get("url").is_none(), "the url survived a switch to stdio");
        assert_eq!(entry["command"], "npx");
        // It must now load as a stdio server.
        let servers = load_mcp_servers(&paths).expect("load");
        let saved = servers.iter().find(|s| s.name == "switch").expect("saved");
        assert_eq!(saved.transport, "stdio", "the server still loads as HTTP");
    }

    #[test]
    fn cross_scope_move_preserves_auth_and_headers() {
        // Unknown fields live only in the source file, so a move must read the
        // *source* entry to carry auth/headers across — not the (empty)
        // destination.
        let (_dir, paths) = temp();
        let user_path = paths.user_mcp();
        super::write_json(
            &user_path,
            &serde_json::json!({
                "mcpServers": {
                    "api": {
                        "type": "http",
                        "url": "https://api.example.com/mcp",
                        "headers": { "X-Api": "abc" },
                        "auth": { "type": "bearer", "token": "tok" },
                        "futureSetting": 7
                    }
                }
            }),
        )
        .expect("seed");

        let moved = McpDraft {
            name: "api".into(),
            scope: Scope::Project,
            transport: "http".into(),
            command: String::new(),
            args: String::new(),
            url: "https://api.example.com/mcp".into(),
            enabled: true,
            env: String::new(),
        };
        save_mcp_server(&paths, &moved, Some(&McpServerId::new(Scope::User, "api")))
            .expect("cross-scope move");

        let project_doc = read_json(&paths.project_mcp()).expect("read project");
        let entry = &project_doc["mcpServers"]["api"];
        assert_eq!(entry["headers"]["X-Api"], "abc", "headers were lost on the move");
        assert_eq!(entry["auth"]["token"], "tok", "auth was lost on the move");
        assert_eq!(entry["futureSetting"], 7, "unknown field was lost on the move");
        // The source copy is gone.
        let user_doc = read_json(&user_path).expect("read user");
        assert!(user_doc["mcpServers"]["api"].is_null(), "the source copy survived");
    }

    #[test]
    fn cross_scope_rename_writes_destination_before_removing_source() {
        // The new entry must be reachable even if the remove of the old file
        // were to fail. We test the happy path: source is removed and only one
        // copy remains.
        let (_dir, paths) = temp();
        save_mcp_server(&paths, &stdio("move-me"), None).expect("save in user scope");

        let moved = McpDraft {
            name: "moved".into(),
            scope: Scope::Project,
            args: String::new(),
            ..stdio("move-me")
        };
        save_mcp_server(&paths, &moved, Some(&McpServerId::new(Scope::User, "move-me")))
            .expect("cross-scope rename");

        let servers = load_mcp_servers(&paths).expect("load");
        let by_name: Vec<_> = servers.iter().filter(|s| s.name == "moved" || s.name == "move-me").collect();
        assert_eq!(by_name.len(), 1, "expected exactly one server after rename, got {}", by_name.len());
        assert_eq!(by_name[0].name, "moved");
        assert_eq!(by_name[0].scope, Scope::Project);
    }

    #[test]
    fn from_server_round_trips_env_raw_values() {
        let server = super::McpServer {
            name: "s".into(),
            scope: Scope::User,
            transport: "stdio",
            command: Some("cmd".into()),
            args: vec!["-x".into()],
            url: None,
            enabled: true,
            env_raw: vec![
                ("API_KEY".into(), "coda-secret:store/my-key".into()),
                ("BASE_URL".into(), "https://api.example.com".into()),
            ],
            env_keys: vec!["API_KEY".into(), "BASE_URL".into()],
        };
        let draft = McpDraft::from_server(&server);
        // Env comes back as a JSON object with the secret reference intact.
        let parsed = super::parse_env_json(&draft.env).expect("env round-trips");
        assert_eq!(
            parsed.iter().find(|(k, _)| k == "API_KEY").map(|(_, v)| v.as_str()),
            Some("coda-secret:store/my-key"),
            "secret ref was resolved or dropped"
        );
        assert_eq!(
            parsed.iter().find(|(k, _)| k == "BASE_URL").map(|(_, v)| v.as_str()),
            Some("https://api.example.com")
        );
        // Args should be a JSON array.
        assert_eq!(draft.args, "[\"-x\"]");
    }
}
