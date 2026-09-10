//! Cooperative, delta-based persistence for `settings.json`.
//!
//! # The problem this solves
//!
//! `settings.json` has several writers, in several processes: the terminal
//! front-end (theme, model, effort, MCP marketplaces), and the authentication
//! transaction (`defaultProvider`, `githubEnterpriseDomain`). The natural
//! implementation — load the document, keep it in memory, write it back on
//! save — silently reverts everything anyone else changed in between. A user
//! signs in to Claude, changes their theme, and finds themselves back on the
//! old provider.
//!
//! Two rules make that impossible:
//!
//! 1. **Every write takes one exclusive lock** on a `.lock` file beside the
//!    settings, so processes serialize.
//! 2. **Every write is a delta.** Inside the lock, the file is re-read and only
//!    the keys the writer actually changed — measured against the values it
//!    originally loaded — are applied to *that* document. Then the whole thing
//!    is replaced atomically. A key nobody touched keeps whatever the file says,
//!    including a value written by another process a millisecond ago.
//!
//! # Where this lives, and why
//!
//! In `coda-boot`, because it is shared by two hosts that must not depend on
//! each other: the auth service's settings port (this module implements
//! [`AuthSettingsPort`]) and the terminal front-end's own settings writer,
//! which calls [`save_changes`]. The engine's settings module stays read-only.
//!
//! # Failure handling
//!
//! A missing file is an empty document — that is a first run. Anything else is
//! an error: invalid JSON, a root that is not an object, or an owned key with
//! the wrong type. None of those may be "fixed" by writing a fresh empty
//! object over whatever is there.

use std::path::{Path, PathBuf};

use coda_auth::error::AuthError;
use coda_auth::service::{AuthSettings, AuthSettingsPatch, AuthSettingsPort};
use serde_json::{Map, Value};

/// `defaultProvider` — the saved provider choice.
pub const DEFAULT_PROVIDER_KEY: &str = "defaultProvider";
/// `githubEnterpriseDomain` — the Copilot tenant.
pub const GITHUB_ENTERPRISE_DOMAIN_KEY: &str = "githubEnterpriseDomain";

/// What can go wrong reading or writing settings.
#[derive(Debug, thiserror::Error)]
pub enum SettingsError {
    #[error("cannot read {path}: {source}")]
    Read {
        path: PathBuf,
        #[source]
        source: std::io::Error,
    },
    #[error("cannot write {path}: {source}")]
    Write {
        path: PathBuf,
        #[source]
        source: std::io::Error,
    },
    #[error("cannot lock {path} for writing: {source}")]
    Lock {
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
    #[error("{path} must contain a JSON object")]
    NotAnObject { path: PathBuf },
    #[error("{path}: '{key}' must be a string or absent")]
    NotAString { path: PathBuf, key: &'static str },
}

impl SettingsError {
    /// A safe, single-line summary for a user-facing message.
    ///
    /// Deliberately not `Display`: the parse variant carries a
    /// `serde_json::Error`, whose text quotes the offending input — and the
    /// offending input is a line of the user's settings file, which may sit
    /// next to anything. The path and the *kind* of fault are enough to act
    /// on, and neither can carry a secret.
    pub fn safe_summary(&self) -> String {
        let path = self.path().display();
        match self {
            Self::Read { .. } => format!("{path} could not be read"),
            Self::Write { .. } => format!("{path} could not be written"),
            Self::Lock { .. } => format!("{path} could not be locked for writing"),
            Self::Parse { .. } => format!("{path} is not valid JSON"),
            Self::NotAnObject { .. } => format!("{path} does not contain a JSON object"),
            Self::NotAString { key, .. } => {
                format!("{path} has a '{key}' that is not a string")
            }
        }
    }

    /// The file the fault is about.
    pub fn path(&self) -> &Path {
        match self {
            Self::Read { path, .. }
            | Self::Write { path, .. }
            | Self::Lock { path, .. }
            | Self::Parse { path, .. }
            | Self::NotAnObject { path }
            | Self::NotAString { path, .. } => path,
        }
    }
}

impl From<SettingsError> for AuthError {
    /// The auth service speaks [`AuthError`]; the detail here is a path and a
    /// key name, never a credential, so it is safe to carry — and it is
    /// classified before it reaches a user anyway.
    fn from(error: SettingsError) -> Self {
        match error {
            SettingsError::Read { source, .. } | SettingsError::Write { source, .. } => {
                AuthError::Io(source)
            }
            SettingsError::Lock { source, .. } => AuthError::Io(source),
            other => AuthError::Store(other.to_string()),
        }
    }
}

/// The path of the user's settings file for this profile (honours `CODA_HOME`).
pub fn user_settings_path() -> PathBuf {
    coda_auth::coda_dir().join("settings.json")
}

/// Read the settings document.
///
/// `Ok(None)` means the file does not exist. A file that exists but cannot be
/// parsed, or whose root is not an object, is an error: replacing it with an
/// empty object would destroy a user's configuration.
pub fn read_document(path: &Path) -> Result<Option<Value>, SettingsError> {
    let text = match std::fs::read_to_string(path) {
        Ok(text) => text,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(source) => return Err(SettingsError::Read { path: path.to_path_buf(), source }),
    };
    // A BOM is tolerated on read and never re-emitted on write.
    let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
    if text.trim().is_empty() {
        return Ok(Some(Value::Object(Map::new())));
    }
    let value: Value = serde_json::from_str(text)
        .map_err(|source| SettingsError::Parse { path: path.to_path_buf(), source })?;
    if !value.is_object() {
        return Err(SettingsError::NotAnObject { path: path.to_path_buf() });
    }
    Ok(Some(value))
}

/// Persist the difference between `original` (what the caller loaded) and
/// `edited` (what it now holds) into the current contents of `path`.
///
/// Keys the caller did not change are left exactly as the file has them, so a
/// concurrent writer's work survives. Nested objects are merged key by key, so
/// two writers touching different rows of `modelByProvider` do not overwrite
/// each other.
pub fn save_changes(path: &Path, original: &Value, edited: &Value) -> Result<(), SettingsError> {
    with_lock(path, || {
        let mut document = read_document(path)?.unwrap_or_else(|| Value::Object(Map::new()));
        merge_delta(&mut document, original, edited);
        write_atomic(path, &document)
    })
}

/// Apply a patch of the auth-owned keys to the file's current contents.
pub fn apply_patch(path: &Path, patch: &AuthSettingsPatch) -> Result<(), SettingsError> {
    with_lock(path, || {
        let mut document = read_document(path)?.unwrap_or_else(|| Value::Object(Map::new()));
        let object = document
            .as_object_mut()
            .ok_or_else(|| SettingsError::NotAnObject { path: path.to_path_buf() })?;
        set_key(object, DEFAULT_PROVIDER_KEY, &patch.default_provider);
        set_key(object, GITHUB_ENTERPRISE_DOMAIN_KEY, &patch.github_enterprise_domain);
        write_atomic(path, &document)
    })
}

/// Read the auth-owned keys.
pub fn read_owned(path: &Path) -> Result<AuthSettings, SettingsError> {
    let Some(document) = read_document(path)? else {
        return Ok(AuthSettings::default());
    };
    let object = document
        .as_object()
        .ok_or_else(|| SettingsError::NotAnObject { path: path.to_path_buf() })?;
    Ok(AuthSettings {
        default_provider: string_key(object, DEFAULT_PROVIDER_KEY, path)?,
        github_enterprise_domain: string_key(object, GITHUB_ENTERPRISE_DOMAIN_KEY, path)?,
    })
}

/// The auth service's writable view of `settings.json`.
#[derive(Debug, Clone)]
pub struct SettingsFile {
    path: PathBuf,
}

impl SettingsFile {
    /// The settings file at `path`.
    pub fn at(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    /// The user's settings file for this profile.
    pub fn for_user() -> Self {
        Self::at(user_settings_path())
    }

    /// The file this port writes.
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Read the owned keys, as [`AuthSettingsPort::load`] but with the file
    /// error preserved.
    pub fn read(&self) -> Result<AuthSettings, SettingsError> {
        read_owned(&self.path)
    }
}

impl AuthSettingsPort for SettingsFile {
    fn load(&self) -> Result<AuthSettings, AuthError> {
        read_owned(&self.path).map_err(AuthError::from)
    }

    fn apply(&self, patch: &AuthSettingsPatch) -> Result<(), AuthError> {
        if patch.is_empty() {
            return Ok(());
        }
        apply_patch(&self.path, patch).map_err(AuthError::from)
    }
}

// ── Internals ────────────────────────────────────────────────────────────────

fn string_key(
    object: &Map<String, Value>,
    key: &'static str,
    path: &Path,
) -> Result<Option<String>, SettingsError> {
    match object.get(key) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(value)) if value.trim().is_empty() => Ok(None),
        Some(Value::String(value)) => Ok(Some(value.clone())),
        // A wrong type is a fault to report, not an absence to paper over: the
        // value may be what a user meant to configure.
        Some(_) => Err(SettingsError::NotAString { path: path.to_path_buf(), key }),
    }
}

fn set_key(object: &mut Map<String, Value>, key: &str, change: &Option<Option<String>>) {
    match change {
        None => {}
        Some(None) => {
            object.remove(key);
        }
        Some(Some(value)) => {
            object.insert(key.to_owned(), Value::String(value.clone()));
        }
    }
}

/// Applies to `document` exactly what changed between `original` and `edited`.
fn merge_delta(document: &mut Value, original: &Value, edited: &Value) {
    let (Some(target), Some(edited_object)) = (document.as_object_mut(), edited.as_object()) else {
        // The caller's edit is not an object (or the file is not): the edit
        // wins wholesale, which is the only meaning "save this" can have.
        *document = edited.clone();
        return;
    };
    let empty = Map::new();
    let original_object = original.as_object().unwrap_or(&empty);

    for (key, edited_value) in edited_object {
        let original_value = original_object.get(key);
        if original_value == Some(edited_value) {
            // Untouched by this writer: whatever the file says stands.
            continue;
        }
        match (target.get_mut(key), original_value, edited_value) {
            // Both sides are objects: merge key by key so two writers editing
            // different rows do not overwrite each other.
            (Some(current @ Value::Object(_)), _, Value::Object(_)) => {
                let original_child = original_value.cloned().unwrap_or(Value::Object(Map::new()));
                merge_delta(current, &original_child, edited_value);
            }
            _ => {
                target.insert(key.clone(), edited_value.clone());
            }
        }
    }

    // Keys this writer deleted.
    for key in original_object.keys() {
        if !edited_object.contains_key(key) {
            target.remove(key);
        }
    }
}

/// Runs `operation` while holding the settings lock for `path`.
fn with_lock<T>(
    path: &Path,
    operation: impl FnOnce() -> Result<T, SettingsError>,
) -> Result<T, SettingsError> {
    use fs2::FileExt;

    let parent = path.parent().unwrap_or(Path::new("."));
    std::fs::create_dir_all(parent)
        .map_err(|source| SettingsError::Write { path: path.to_path_buf(), source })?;

    let lock_path = lock_path_for(path);
    let lock = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(&lock_path)
        .map_err(|source| SettingsError::Lock { path: lock_path.clone(), source })?;
    lock.lock_exclusive()
        .map_err(|source| SettingsError::Lock { path: lock_path.clone(), source })?;

    let result = operation();
    // Released on drop as well; unlocking explicitly keeps the window short
    // even if the caller holds the returned value for a while.
    let _ = FileExt::unlock(&lock);
    result
}

/// The lock file beside the settings. Named `.lock` so nothing that scans the
/// directory for configuration finds it.
fn lock_path_for(path: &Path) -> PathBuf {
    let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("settings.json");
    path.with_file_name(format!(".{name}.lock"))
}

/// Writes `value` by replacing `path` atomically. The scratch file is removed
/// if the rename fails, so an interrupted write leaves nothing behind.
fn write_atomic(path: &Path, value: &Value) -> Result<(), SettingsError> {
    let parent = path.parent().unwrap_or(Path::new("."));
    let text = serde_json::to_string_pretty(value)
        .map_err(|source| SettingsError::Parse { path: path.to_path_buf(), source })?;
    let scratch = parent.join(format!(
        ".{}.{}.tmp",
        path.file_stem().and_then(|s| s.to_str()).unwrap_or("settings"),
        std::process::id()
    ));

    let write = || -> std::io::Result<()> {
        std::fs::write(&scratch, text.as_bytes())?;
        std::fs::rename(&scratch, path)
    };
    match write() {
        Ok(()) => Ok(()),
        Err(source) => {
            let _ = std::fs::remove_file(&scratch);
            Err(SettingsError::Write { path: path.to_path_buf(), source })
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn a_delta_of_nothing_changes_nothing() {
        let mut document = json!({ "theme": "dark", "defaultProvider": "claude-ai" });
        let original = json!({ "theme": "dark" });
        merge_delta(&mut document, &original, &original);
        assert_eq!(document, json!({ "theme": "dark", "defaultProvider": "claude-ai" }));
    }

    #[test]
    fn a_lock_file_is_hidden_and_named_after_the_settings() {
        let path = lock_path_for(Path::new("/home/u/.coda/settings.json"));
        assert_eq!(path.file_name().unwrap(), ".settings.json.lock");
    }

    #[test]
    fn a_blank_owned_value_reads_as_absent() {
        let object = json!({ "defaultProvider": "  " });
        let value = string_key(object.as_object().unwrap(), "defaultProvider", Path::new("s"))
            .expect("blank is not a type error");
        assert_eq!(value, None);
    }
}
