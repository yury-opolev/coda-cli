//! Minimal settings reader for the engine.
//!
//! The engine needs the active provider, model, and Copilot routing. The TUI has a
//! richer settings module, but the dependency direction forbids
//! `coda-serve → coda-tui`, and pulling the whole front-end config surface in
//! here would be worse than a small focused reader.
//!
//! Deliberately read-only: the engine never writes settings. Writes belong to
//! the front-end, which already preserves unknown keys and writes atomically.

use std::path::{Path, PathBuf};

use serde_json::Value;
use coda_auth::provider::copilot::CopilotConfig as AuthCopilotConfig;

/// The model used when settings say nothing.
///
/// Chosen to exist in the Copilot catalogue: an id that no provider offers
/// resolves to no capability and fails every prompt, which is exactly the bug
/// a hardcoded default caused here before.
pub const FALLBACK_MODEL: &str = "claude-opus-5";

/// The provider assumed when settings say nothing and no credential is connected.
pub const FALLBACK_PROVIDER: &str = "github-copilot";

/// Locates `~/.coda/settings.json`, honoring the `CODA_HOME` profile-root
/// override so serve reads the same isolated profile the credential and catalog
/// paths do.
fn settings_path() -> Option<PathBuf> {
    Some(coda_auth::coda_dir().join("settings.json"))
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
    let value = read_settings_json(path).unwrap_or_default();
    resolve_for_provider_from(&value, provider)
}

pub(crate) fn resolve_for_provider_from(value: &Value, provider: Option<&str>) -> StartupModel {
    let provider_id = crate::host::canonical_provider(provider.unwrap_or(FALLBACK_PROVIDER));

    // The saved model row is keyed by the engine provider id, but an older
    // build wrote the API key's row under its *stored* id
    // (`anthropic-api-key`). Reading only the canonical key would silently
    // change that user's model, so every spelling this identity has ever had
    // is consulted, most canonical first.
    let model_keys: Vec<&str> = match coda_auth::service::ProviderIdentity::parse(&provider_id) {
        Some(identity) => identity.settings_model_keys().to_vec(),
        None => vec![provider_id.as_str()],
    };
    let by_provider = value
        .get("modelByProvider")
        .and_then(Value::as_object)
        .and_then(|map| {
            model_keys
                .iter()
                .find_map(|key| map.get(*key).and_then(Value::as_str))
        })
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

/// The saved `defaultProvider`, as written, for an explicit settings path.
///
/// `Ok(None)` means the key is absent or blank — no choice was made. An
/// unreadable or malformed settings file is an **error**, not "no choice":
/// treating it as unconfigured would let a corrupt file quietly move the user
/// off the account they chose, which is the same failure as reading a locked
/// credential store as "logged out".
pub(crate) fn saved_default_provider_at(
    path: &Path,
) -> Result<Option<String>, CopilotConfigError> {
    let Some(value) = read_settings_json_checked(path)? else {
        return Ok(None);
    };
    let object = value.as_object().ok_or(CopilotConfigError::InvalidSettings)?;
    match object.get("defaultProvider") {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(provider)) => {
            let trimmed = provider.trim();
            Ok((!trimmed.is_empty()).then(|| trimmed.to_owned()))
        }
        Some(_) => Err(CopilotConfigError::InvalidSettings),
    }
}

/// Returns the model to use for the given connected provider.
///
/// Convenience wrapper around [`resolve_for_provider`].
pub fn model_for_provider(provider: &str) -> String {
    resolve_for_provider(Some(provider)).model
}

/// Reads the startup model from a specific settings file.
pub fn resolve_at(path: &Path) -> StartupModel {
    let parsed = read_settings_json(path);
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

// ─────────────────────────────────────────────────────────────────────────────
// Enterprise Copilot configuration resolver
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, thiserror::Error)]
pub(crate) enum CopilotConfigError {
    #[error("cannot read settings.json; check file access before retrying")]
    ReadSettings,
    #[error("settings.json contains invalid JSON")]
    ParseSettings,
    #[error("settings.json must contain an object with a string or null githubEnterpriseDomain")]
    InvalidSettings,
    #[error("invalid Copilot endpoint configuration")]
    Endpoint(#[from] coda_auth::AuthError),
}

/// Resolves the [`AuthCopilotConfig`] by layering:
///
/// 1. `GH_COPILOT_ENTERPRISE_DOMAIN` (and other `GH_COPILOT_*` overrides) from
///    the caller's environment lookup — a non-blank value wins unconditionally.
/// 2. `githubEnterpriseDomain` from the **saved settings file** at the given
///    path (the profile's, never an ambient one).
/// 3. Public github.com defaults when neither source has a domain.
///
/// **No environment mutation.** This function never writes to the process
/// environment; it passes a custom lookup closure to
/// [`coda_auth::provider::copilot::resolve_copilot_config`], the one shared
/// resolver, which the login flows use as well (with an explicit deployment
/// choice, which this engine path never makes).
///
/// An absent file permits public defaults. An unreadable or invalid file fails
/// closed so losing the saved enterprise domain never redirects its credentials.
///
/// Accepts an explicit file path (so a profile-scoped context, and tests, use
/// their own settings) and an explicit env lookup (so tests never write real
/// process-environment variables).
#[cfg_attr(not(test), allow(dead_code))]
pub(crate) fn resolve_copilot_config_from(
    settings_path: Option<&Path>,
    env: impl Fn(&str) -> Option<String>,
) -> Result<AuthCopilotConfig, CopilotConfigError> {
    Ok(resolve_copilot_deployment_from(settings_path, env)?.config)
}

/// As [`resolve_copilot_config_from`], but keeps the resolver's report of which
/// deployment the endpoints actually contact and which env overrides applied.
pub(crate) fn resolve_copilot_deployment_from(
    settings_path: Option<&Path>,
    env: impl Fn(&str) -> Option<String>,
) -> Result<coda_auth::provider::copilot::ResolvedCopilotConfig, CopilotConfigError> {
    // Read the saved domain once; the resolver uses it as the fallback for the
    // enterprise-domain key only.
    let settings = settings_path.map(read_settings_json_checked).transpose()?.flatten();
    let saved_domain = match settings.as_ref() {
        None => None,
        Some(Value::Object(object)) => match object.get("githubEnterpriseDomain") {
            None | Some(Value::Null) => None,
            Some(Value::String(domain)) => Some(domain.clone()),
            _ => return Err(CopilotConfigError::InvalidSettings),
        },
        _ => return Err(CopilotConfigError::InvalidSettings),
    };

    Ok(coda_auth::provider::copilot::resolve_copilot_config(
        // The engine never overrides the deployment: it uses whatever the user
        // signed in to. Only the login flow passes an explicit choice.
        None,
        saved_domain.as_deref(),
        env,
    )?)
}

// ─────────────────────────────────────────────────────────────────────────────
// Internal helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Read `path` as JSON, tolerating a leading UTF-8 BOM (`\u{feff}`).
///
/// Returns `None` when the file is absent, unreadable, or contains invalid JSON.
/// `coda-mcp` and `coda-tui` apply the same single-expression strip; no shared
/// helper is introduced — the expression is one line.
fn read_settings_json(path: &Path) -> Option<Value> {
    read_settings_json_checked(path).ok().flatten()
}

fn read_settings_json_checked(path: &Path) -> Result<Option<Value>, CopilotConfigError> {
    let text = match std::fs::read_to_string(path) {
        Ok(text) => text,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(_) => return Err(CopilotConfigError::ReadSettings),
    };
    let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
    serde_json::from_str(text)
        .map(Some)
        .map_err(|_| CopilotConfigError::ParseSettings)
}

/// Returns the raw settings document, using an empty object when unavailable.
/// Used by the host for effort / LSP settings that also live in `settings.json`.
pub(crate) fn load_settings_json(path: &Path) -> Value {
    read_settings_json(path).unwrap_or_else(|| Value::Object(Default::default()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn provider_aliases_use_the_canonical_saved_model() {
        let settings = json!({
            "defaultModel": "wrong-fallback",
            "modelByProvider": {
                "github-copilot": "copilot-saved",
                "claude-ai": "subscription-saved",
                "anthropic": "api-saved"
            }
        });
        for (alias, canonical, expected) in [
            ("copilot", "github-copilot", "copilot-saved"),
            ("github", "github-copilot", "copilot-saved"),
            ("claude", "claude-ai", "subscription-saved"),
            ("subscription", "claude-ai", "subscription-saved"),
            ("api-key", "anthropic", "api-saved"),
        ] {
            let resolved = resolve_for_provider_from(&settings, Some(alias));
            assert_eq!(resolved.provider_id, canonical);
            assert_eq!(resolved.model, expected, "{alias}");
        }
    }

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

    // ── resolve_copilot_config_from tests ─────────────────────────────────────

    fn no_env(_key: &str) -> Option<String> { None }

    fn env_of(pairs: &[(&str, &str)]) -> impl Fn(&str) -> Option<String> {
        let owned: Vec<(String, String)> =
            pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect();
        move |key| owned.iter().find(|(k, _)| k == key).map(|(_, v)| v.clone())
    }

    fn temp_settings(content: &str) -> (tempfile::TempDir, PathBuf) {
        let dir = tempfile::TempDir::new().expect("tempdir");
        let path = dir.path().join("settings.json");
        std::fs::write(&path, content).expect("write settings");
        (dir, path)
    }

    /// No env, no saved domain → public default endpoints.
    #[test]
    fn no_env_no_saved_domain_produces_public_default() {
        let config = resolve_copilot_config_from(None, no_env).expect("config");
        assert_eq!(config.api_base_url, "https://api.githubcopilot.com");
        assert_eq!(config.device_code_url, "https://github.com/login/device/code");
    }

    /// Saved domain with no env → enterprise endpoints used.
    #[test]
    fn saved_domain_with_no_env_produces_enterprise_config() {
        let (_dir, path) = temp_settings(
            r#"{"githubEnterpriseDomain": "octocorp.ghe.com"}"#,
        );
        let config = resolve_copilot_config_from(Some(&path), no_env).expect("config");
        assert_eq!(config.api_base_url, "https://copilot-api.octocorp.ghe.com");
        assert_eq!(config.device_code_url, "https://octocorp.ghe.com/login/device/code");
    }

    /// Env override wins over saved domain when both are set.
    #[test]
    fn env_override_wins_over_saved_domain() {
        let (_dir, path) = temp_settings(
            r#"{"githubEnterpriseDomain": "saved.ghe.com"}"#,
        );
        let config = resolve_copilot_config_from(
            Some(&path),
            env_of(&[("GH_COPILOT_ENTERPRISE_DOMAIN", "env.ghe.com")]),
        )
        .expect("config");
        assert_eq!(
            config.api_base_url, "https://copilot-api.env.ghe.com",
            "env var must win over saved domain"
        );
        assert_eq!(config.device_code_url, "https://env.ghe.com/login/device/code");
    }

    /// Empty env var does NOT override saved domain.
    #[test]
    fn blank_env_var_does_not_suppress_saved_domain() {
        let (_dir, path) = temp_settings(
            r#"{"githubEnterpriseDomain": "octocorp.ghe.com"}"#,
        );
        let config = resolve_copilot_config_from(
            Some(&path),
            env_of(&[("GH_COPILOT_ENTERPRISE_DOMAIN", "")]),
        )
        .expect("config");
        assert_eq!(
            config.api_base_url, "https://copilot-api.octocorp.ghe.com",
            "blank env var must not suppress saved domain"
        );
    }

    /// A settings file with a BOM is parsed correctly.
    #[test]
    fn bom_settings_file_is_parsed_correctly() {
        // U+FEFF BOM followed by valid JSON.
        let content = "\u{feff}{\"githubEnterpriseDomain\": \"bom.ghe.com\"}";
        let (_dir, path) = temp_settings(content);
        let config = resolve_copilot_config_from(Some(&path), no_env).expect("config");
        assert_eq!(config.api_base_url, "https://copilot-api.bom.ghe.com");
    }

    /// An invalid saved domain (contains a path) fails rather than silently
    /// routing to the public github.com default, which would send enterprise
    /// credentials to the wrong host.
    #[test]
    fn invalid_saved_domain_fails_rather_than_falling_back_to_public() {
        let (_dir, path) = temp_settings(
            r#"{"githubEnterpriseDomain": "evil.com/path"}"#,
        );
        let result = resolve_copilot_config_from(Some(&path), no_env);
        assert!(
            result.is_err(),
            "a domain with a path component must be rejected, not silently defaulted to public"
        );
    }

    /// An unreadable enterprise setting must never silently select public routing.
    #[test]
    fn malformed_copilot_settings_fail_closed() {
        for content in [
            "{ this is not json",
            "\u{feff}\u{feff}{}",
            "[]",
            r#"{"githubEnterpriseDomain":123}"#,
        ] {
            let (_dir, path) = temp_settings(content);
            assert!(resolve_copilot_config_from(Some(&path), no_env).is_err());
        }
        let dir = tempfile::TempDir::new().unwrap();
        assert!(resolve_copilot_config_from(Some(dir.path()), no_env).is_err());
    }

    /// Missing settings file falls back to public default.
    #[test]
    fn absent_settings_file_falls_back_to_public_default() {
        let config =
            resolve_copilot_config_from(Some(Path::new("nonexistent/settings.json")), no_env)
                .expect("absent settings must not error");
        assert_eq!(config.api_base_url, "https://api.githubcopilot.com");
    }

    /// An explicit GH_COPILOT_API_BASE_URL env override applies on top of a
    /// saved enterprise domain (endpoint-level precedence).
    #[test]
    fn endpoint_env_override_applies_on_top_of_saved_domain() {
        let (_dir, path) = temp_settings(
            r#"{"githubEnterpriseDomain": "octocorp.ghe.com"}"#,
        );
        let config = resolve_copilot_config_from(
            Some(&path),
            env_of(&[("GH_COPILOT_API_BASE_URL", "https://proxy.internal/copilot")]),
        )
        .expect("config");
        // Enterprise domain is still used for auth endpoints...
        assert_eq!(config.device_code_url, "https://octocorp.ghe.com/login/device/code");
        // ...but the inference base URL is overridden.
        assert_eq!(config.api_base_url, "https://proxy.internal/copilot");
    }

    /// The resolver reports what the endpoints actually contact, so a caller
    /// can disclose a redirected auth host instead of labelling it with the
    /// deployment the user thinks they are on.
    #[test]
    fn the_resolved_deployment_describes_the_real_auth_host() {
        use coda_auth::provider::copilot::CopilotDeployment;

        let (_dir, path) = temp_settings(r#"{"githubEnterpriseDomain": "octocorp.ghe.com"}"#);
        let saved = resolve_copilot_deployment_from(Some(&path), no_env).expect("config");
        assert_eq!(
            saved.deployment,
            CopilotDeployment::Enterprise { domain: "octocorp.ghe.com".into() }
        );
        assert!(saved.endpoint_overrides.is_empty());

        let public = resolve_copilot_deployment_from(None, no_env).expect("config");
        assert_eq!(public.deployment, CopilotDeployment::Public);

        let redirected = resolve_copilot_deployment_from(
            None,
            env_of(&[("GH_COPILOT_DEVICE_CODE_URL", "https://proxy.internal/login/device/code")]),
        )
        .expect("config");
        assert_eq!(
            redirected.deployment,
            CopilotDeployment::Custom { auth_host: "proxy.internal".into() }
        );
        assert_eq!(redirected.endpoint_overrides, ["GH_COPILOT_DEVICE_CODE_URL"]);
    }
}


