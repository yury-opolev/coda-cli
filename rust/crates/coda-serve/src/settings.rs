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

/// The provider assumed when settings say nothing and no credential is connected.
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
/// to `defaultModel` (the canonical C# key), then to the built-in default.
/// The legacy `model` key is also accepted as a secondary fallback for backwards
/// compatibility with existing settings files. A provider named in `defaultProvider`
/// but absent from `modelByProvider` still resolves, because a half-configured
/// file should not leave the engine with no model at all.
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

    // C# uses `defaultModel`; fall back to the legacy `model` key for backwards compat.
    let top_level = value
        .get("defaultModel")
        .or_else(|| value.get("model"))
        .and_then(Value::as_str)
        .filter(|s| !s.trim().is_empty());

    let model = by_provider
        .or(top_level)
        .unwrap_or(FALLBACK_MODEL)
        .to_owned();

    StartupModel { provider_id, model }
}

/// Resolves the startup model for the credential that was **actually** connected.
///
/// Unlike [`resolve`], which reads `defaultProvider` from settings, this uses
/// the provider id that the already-connected `LlmClient` reports — the one
/// whose credential was found on this machine. This prevents a mismatch where
/// the client is Anthropic (because `ANTHROPIC_API_KEY` is set) but the model
/// is a Copilot id (because settings say `defaultProvider: github-copilot`).
///
/// Resolution: `modelByProvider[provider]` → `defaultModel` → `model` → built-in default.
///
/// When `provider` is `None`, falls back to `FALLBACK_PROVIDER` (same as [`resolve`]).
pub fn resolve_for_provider(provider: Option<&str>) -> StartupModel {
    match settings_path() {
        Some(p) => resolve_for_provider_at(&p, provider),
        None => StartupModel {
            provider_id: provider.unwrap_or(FALLBACK_PROVIDER).into(),
            model: FALLBACK_MODEL.into(),
        },
    }
}

/// Like [`resolve_for_provider`] but reads from an explicit file path.
/// Exposed for testing; production callers use [`resolve_for_provider`].
pub fn resolve_for_provider_at(path: &Path, provider: Option<&str>) -> StartupModel {
    let value = std::fs::read_to_string(path)
        .ok()
        .and_then(|s| serde_json::from_str::<Value>(&s).ok())
        .unwrap_or_default();
    resolve_for_provider_from(&value, provider)
}

fn resolve_for_provider_from(value: &Value, provider: Option<&str>) -> StartupModel {
    let provider_id = provider.unwrap_or(FALLBACK_PROVIDER).to_owned();

    let by_provider = value
        .get("modelByProvider")
        .and_then(Value::as_object)
        .and_then(|map| map.get(&provider_id))
        .and_then(Value::as_str)
        .filter(|s| !s.trim().is_empty());

    let top_level = value
        .get("defaultModel")
        .or_else(|| value.get("model"))
        .and_then(Value::as_str)
        .filter(|s| !s.trim().is_empty());

    let model = by_provider
        .or(top_level)
        .unwrap_or(FALLBACK_MODEL)
        .to_owned();

    StartupModel { provider_id, model }
}

/// Returns the model to use for the given connected provider.
///
/// Convenience wrapper around [`resolve_for_provider`].
pub fn model_for_provider(provider: &str) -> String {
    resolve_for_provider(Some(provider)).model
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
    fn the_default_model_key_is_used_when_the_map_has_no_entry() {
        let value = json!({ "defaultProvider": "github-copilot", "defaultModel": "some-model" });
        assert_eq!(resolve_from(&value).model, "some-model");
    }

    #[test]
    fn the_legacy_model_key_is_accepted_for_backwards_compatibility() {
        let value = json!({ "defaultProvider": "github-copilot", "model": "legacy-model" });
        assert_eq!(resolve_from(&value).model, "legacy-model");
    }

    #[test]
    fn default_model_wins_over_legacy_model_key() {
        let value = json!({
            "defaultProvider": "github-copilot",
            "defaultModel": "new-model",
            "model": "legacy-model"
        });
        assert_eq!(resolve_from(&value).model, "new-model", "defaultModel takes priority over model");
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

    // ── resolve_for_provider tests ────────────────────────────────────────────

    #[test]
    fn resolve_for_provider_uses_connected_provider_not_default_provider() {
        // This test verifies the core of Finding 3: the model is chosen based on
        // the credential that was actually connected, not settings.defaultProvider.
        let dir = std::env::temp_dir().join(format!(
            "coda-settings-provider-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        std::fs::create_dir_all(&dir).expect("dir");
        let path = dir.join("settings.json");
        std::fs::write(
            &path,
            serde_json::json!({
                "defaultProvider": "github-copilot",
                "modelByProvider": {
                    "github-copilot": "gpt-5",
                    "anthropic": "claude-opus-4-8"
                }
            })
            .to_string(),
        )
        .expect("write");

        // resolve() would pick github-copilot + gpt-5.
        // resolve_for_provider("anthropic") must pick anthropic + claude-opus-4-8.
        let resolved = resolve_at(&path);
        assert_eq!(resolved.provider_id, "github-copilot");
        assert_eq!(resolved.model, "gpt-5");

        let resolved_for = resolve_for_provider_at(&path, Some("anthropic"));
        assert_eq!(resolved_for.provider_id, "anthropic");
        assert_eq!(
            resolved_for.model, "claude-opus-4-8",
            "model must come from the connected provider, not defaultProvider"
        );

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn resolve_for_provider_falls_back_to_default_model_when_no_provider_entry() {
        let dir = std::env::temp_dir().join(format!(
            "coda-settings-fback-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        std::fs::create_dir_all(&dir).expect("dir");
        let path = dir.join("settings.json");
        std::fs::write(
            &path,
            serde_json::json!({
                "defaultModel": "fallback-model",
                "modelByProvider": {}
            })
            .to_string(),
        )
        .expect("write");

        let resolved = resolve_for_provider_at(&path, Some("anthropic"));
        assert_eq!(resolved.model, "fallback-model",
            "defaultModel must be the fallback when the provider has no entry");

        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn resolve_for_provider_none_uses_fallback_provider() {
        let resolved = resolve_for_provider(None);
        assert_eq!(resolved.provider_id, FALLBACK_PROVIDER);
    }
}
