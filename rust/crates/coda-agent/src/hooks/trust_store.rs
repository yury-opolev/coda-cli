//! Hook trust store: persists trust decisions to disk.
//!
//! Matches C# `HookTrustStore.cs`.
//!
//! The file lives at `~/.coda/hook-trust.json`.  Keys are SHA-256 hashes of
//! the canonical project path (lower-cased); values are arrays of hook content
//! hashes.  Atomic writes keep the file consistent under crashes.

use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::Mutex;

use sha2::{Sha256, Digest};
use serde_json::Value;

// ─────────────────────────────────────────────────────────────────────────────
// Trait
// ─────────────────────────────────────────────────────────────────────────────

/// Persistent trust decision store for project-scoped hooks.
pub trait HookTrustStore: Send + Sync {
    fn is_trusted(&self, project_path: &str, hook_hash: &str) -> bool;
    fn trust(&self, project_path: &str, hook_hash: &str);
    fn revoke(&self, project_path: &str, hook_hash: &str);
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory implementation (for tests)
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory trust store for unit tests.
pub struct InMemoryHookTrustStore {
    trusted: Mutex<HashMap<String, HashSet<String>>>,
}

impl InMemoryHookTrustStore {
    pub fn new() -> Self {
        Self { trusted: Mutex::new(HashMap::new()) }
    }
}

impl Default for InMemoryHookTrustStore {
    fn default() -> Self {
        Self::new()
    }
}

impl HookTrustStore for InMemoryHookTrustStore {
    fn is_trusted(&self, project_path: &str, hook_hash: &str) -> bool {
        let key = project_key(project_path);
        self.trusted
            .lock()
            .unwrap()
            .get(&key)
            .map_or(false, |set| set.contains(hook_hash))
    }

    fn trust(&self, project_path: &str, hook_hash: &str) {
        let key = project_key(project_path);
        self.trusted
            .lock()
            .unwrap()
            .entry(key)
            .or_default()
            .insert(hook_hash.to_owned());
    }

    fn revoke(&self, project_path: &str, hook_hash: &str) {
        let key = project_key(project_path);
        let mut guard = self.trusted.lock().unwrap();
        if let Some(set) = guard.get_mut(&key) {
            set.remove(hook_hash);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// File-backed implementation
// ─────────────────────────────────────────────────────────────────────────────

/// File-backed trust store.  Atomic writes keep the file consistent under crashes.
pub struct FileTrustStore {
    path: PathBuf,
    lock: Mutex<()>,
}

impl FileTrustStore {
    /// `user_settings_dir` defaults to `$CODA_SETTINGS_DIR` then the home directory.
    pub fn new(user_settings_dir: Option<&str>) -> Self {
        let home = user_settings_dir
            .map(str::to_owned)
            .or_else(|| std::env::var("CODA_SETTINGS_DIR").ok())
            .unwrap_or_else(|| {
                dirs_next::home_dir()
                    .unwrap_or_else(|| PathBuf::from("."))
                    .to_string_lossy()
                    .into_owned()
            });
        let dir = PathBuf::from(&home).join(".coda");
        Self { path: dir.join("hook-trust.json"), lock: Mutex::new(()) }
    }

    fn load(&self) -> HashMap<String, HashSet<String>> {
        let Ok(text) = std::fs::read_to_string(&self.path) else {
            return HashMap::new();
        };
        let Ok(Value::Object(obj)) = serde_json::from_str::<Value>(&text) else {
            return HashMap::new();
        };
        obj.into_iter()
            .filter_map(|(k, v)| {
                let arr = v.as_array()?;
                let set: HashSet<String> = arr
                    .iter()
                    .filter_map(|h| h.as_str().map(str::to_owned))
                    .collect();
                Some((k, set))
            })
            .collect()
    }

    fn save(&self, data: &HashMap<String, HashSet<String>>) {
        let obj: serde_json::Map<String, Value> = data
            .iter()
            .map(|(k, set)| {
                let mut sorted: Vec<&str> = set.iter().map(String::as_str).collect();
                sorted.sort_unstable();
                (k.clone(), Value::Array(sorted.iter().map(|h| Value::String(h.to_string())).collect()))
            })
            .collect();
        let json = serde_json::to_string_pretty(&Value::Object(obj))
            .unwrap_or_else(|_| "{}".into());

        if let Some(dir) = self.path.parent() {
            let _ = std::fs::create_dir_all(dir);
        }
        // Atomic write: write to a temp file then rename.
        let tmp = self.path.with_extension("tmp");
        if std::fs::write(&tmp, json.as_bytes()).is_ok() {
            let _ = std::fs::rename(&tmp, &self.path);
        }
    }
}

impl HookTrustStore for FileTrustStore {
    fn is_trusted(&self, project_path: &str, hook_hash: &str) -> bool {
        let _g = self.lock.lock().unwrap();
        let key = project_key(project_path);
        self.load().get(&key).map_or(false, |s| s.contains(hook_hash))
    }

    fn trust(&self, project_path: &str, hook_hash: &str) {
        let _g = self.lock.lock().unwrap();
        let key = project_key(project_path);
        let mut data = self.load();
        data.entry(key).or_default().insert(hook_hash.to_owned());
        self.save(&data);
    }

    fn revoke(&self, project_path: &str, hook_hash: &str) {
        let _g = self.lock.lock().unwrap();
        let key = project_key(project_path);
        let mut data = self.load();
        if let Some(set) = data.get_mut(&key) {
            set.remove(hook_hash);
            if set.is_empty() {
                data.remove(&key);
            }
        }
        self.save(&data);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helper
// ─────────────────────────────────────────────────────────────────────────────

fn project_key(path: &str) -> String {
    let canonical = path.to_lowercase();
    let hash = Sha256::digest(canonical.as_bytes());
    hash.iter().map(|b| format!("{b:02x}")).collect()
}

// Minimal dirs_next shim — only used in FileTrustStore, which tests don't call.
mod dirs_next {
    pub fn home_dir() -> Option<std::path::PathBuf> {
        #[cfg(windows)]
        {
            std::env::var("USERPROFILE")
                .ok()
                .map(std::path::PathBuf::from)
        }
        #[cfg(not(windows))]
        {
            std::env::var("HOME")
                .ok()
                .map(std::path::PathBuf::from)
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn in_memory_trust_round_trip() {
        let store = InMemoryHookTrustStore::new();
        let project = "/my/project";
        let hash = "abc123";
        assert!(!store.is_trusted(project, hash));
        store.trust(project, hash);
        assert!(store.is_trusted(project, hash));
        store.revoke(project, hash);
        assert!(!store.is_trusted(project, hash));
    }

    #[test]
    fn different_projects_are_isolated() {
        let store = InMemoryHookTrustStore::new();
        store.trust("/project/a", "hash1");
        assert!(!store.is_trusted("/project/b", "hash1"));
    }

    #[test]
    fn revoking_nonexistent_hash_is_noop() {
        let store = InMemoryHookTrustStore::new();
        // Should not panic.
        store.revoke("/project", "nonexistent");
    }
}
