//! Minimal settings reader for the engine.
//!
//! The engine needs only the active provider and its model. The TUI has a
//! richer settings module, but the dependency direction forbids
//! `coda-serve → coda-tui`, and pulling the whole front-end config surface in
//! here to read two fields would be worse than a small focused reader.
//!
//! Deliberately read-only: the engine never writes settings. Writes belong to
//! the front-end, which already preserves unknown keys and writes atomically.

use std::path::{Path, PathBuf};

use serde_json::Value;

/// The model used when settings say nothing.
///
/// Chosen to exist in the Copilot catalogue: an id that no provider offers
/// resolves to no capability and fails every prompt, which is exactly the bug
/// a hardcoded default caused here before.
pub const FALLBACK_MODEL: &str = "claude-opus-5";

/// The provider assumed when settings say nothing.
pub const FALLBACK_PROVIDER: &str = "github-copilot";

/// Locates `~/.coda/settings.json`.
fn settings_path() -> Option<PathBuf> {
    directories::UserDirs::new().map(|d| d.home_dir().join(".coda").join("settings.json"))
}

/// The provider and model the engine should start with.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StartupModel {
    pub provider_id: String,
    pub model: String,
}

/// Resolves the startup provider and model from a settings document.
///
/// Mirrors the C#: `modelByProvider[provider]` selects the model, falling back
/// to a top-level `model`, then to the built-in default. A provider named in
/// `defaultProvider` but absent from `modelByProvider` still resolves, because
/// a half-configured file should not leave the engine with no model at all.
pub fn resolve_from(value: &Value) -> StartupModel {
    let provider_id = value
        .get("defaultProvider")
        .and_then(Value::as_str)
        .filter(|s| !s.trim().is_empty())
        .unwrap_or(FALLBACK_PROVIDER)
        .to_owned();

    let by_provider = value
        .get("modelByProvider")
        .and_then(Value::as_object)
        .and_then(|map| map.get(&provider_id))
        .and_then(Value::as_str)
        .filter(|s| !s.trim().is_empty());

    let top_level = value
        .get("model")
        .and_then(Value::as_str)
        .filter(|s| !s.trim().is_empty());

    let model = by_provider
        .or(top_level)
        .unwrap_or(FALLBACK_MODEL)
        .to_owned();

    StartupModel { provider_id, model }
}

/// Reads the startup model from a specific settings file.
pub fn resolve_at(path: &Path) -> StartupModel {
    let parsed = std::fs::read_to_string(path)
        .ok()
        .and_then(|text| serde_json::from_str::<Value>(&text).ok());
    match parsed {
        Some(value) => resolve_from(&value),
        // A missing or corrupt settings file must not stop the engine starting.
        None => StartupModel {
            provider_id: FALLBACK_PROVIDER.into(),
            model: FALLBACK_MODEL.into(),
        },
    }
}

/// Reads the startup model from the user's settings.
pub fn resolve() -> StartupModel {
    match settings_path() {
        Some(path) => resolve_at(&path),
        None => StartupModel {
            provider_id: FALLBACK_PROVIDER.into(),
            model: FALLBACK_MODEL.into(),
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn the_model_comes_from_the_provider_specific_map() {
        let value = json!({
            "defaultProvider": "github-copilot",
            "modelByProvider": { "github-copilot": "claude-opus-5", "claude-ai": "claude-opus-4-8" }
        });
        let resolved = resolve_from(&value);
        assert_eq!(resolved.provider_id, "github-copilot");
        assert_eq!(resolved.model, "claude-opus-5");
    }

    #[test]
    fn a_different_default_provider_selects_a_different_model() {
        let value = json!({
            "defaultProvider": "claude-ai",
            "modelByProvider": { "github-copilot": "claude-opus-5", "claude-ai": "claude-opus-4-8" }
        });
        assert_eq!(resolve_from(&value).model, "claude-opus-4-8");
    }

    #[test]
    fn a_top_level_model_is_used_when_the_map_has_no_entry() {
        let value = json!({ "defaultProvider": "github-copilot", "model": "some-model" });
        assert_eq!(resolve_from(&value).model, "some-model");
    }

    /// A provider named but not mapped must still yield a usable model, or the
    /// engine starts with nothing to talk to.
    #[test]
    fn a_half_configured_file_still_resolves_a_model() {
        let value = json!({ "defaultProvider": "github-copilot", "modelByProvider": {} });
        assert_eq!(resolve_from(&value).model, FALLBACK_MODEL);
    }

    #[test]
    fn an_empty_document_falls_back_completely() {
        let resolved = resolve_from(&json!({}));
        assert_eq!(resolved.provider_id, FALLBACK_PROVIDER);
        assert_eq!(resolved.model, FALLBACK_MODEL);
    }

    #[test]
    fn blank_values_are_ignored_rather_than_used_verbatim() {
        let value = json!({
            "defaultProvider": "   ",
            "modelByProvider": { "github-copilot": "" },
            "model": ""
        });
        let resolved = resolve_from(&value);
        assert_eq!(resolved.provider_id, FALLBACK_PROVIDER);
        assert_eq!(resolved.model, FALLBACK_MODEL, "an empty string is not a model id");
    }

    /// A corrupt settings file must not prevent the engine from starting.
    #[test]
    fn a_corrupt_settings_file_falls_back() {
        let dir = std::env::temp_dir().join(format!(
            "coda-settings-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        std::fs::create_dir_all(&dir).expect("dir");
        let path = dir.join("settings.json");
        std::fs::write(&path, "{ this is not json").expect("write");

        let resolved = resolve_at(&path);
        assert_eq!(resolved.model, FALLBACK_MODEL);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_missing_settings_file_falls_back() {
        let resolved = resolve_at(Path::new("no-such-directory/settings.json"));
        assert_eq!(resolved.provider_id, FALLBACK_PROVIDER);
        assert_eq!(resolved.model, FALLBACK_MODEL);
    }
}
