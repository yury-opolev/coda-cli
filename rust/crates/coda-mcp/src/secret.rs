//! MCP secret management: credential-store-backed secrets and config resolution.
//!
//! Mirrors `McpSecretStore.cs` and `McpSecretResolver.cs`.
//!
//! ## Key namespace
//!
//! Secrets are stored under `mcp:<server>/<field>` (distinct from the
//! `llmauth:<provider>` namespace used by the auth system). A versioned
//! key is `mcp:<server>/<field>/<hex32>`.
//!
//! ## Reference format
//!
//! A value that begins with `coda-secret:` is a reference to the credential
//! store: `coda-secret:<key>`. These are written into `.mcp.json` instead of
//! the plaintext secret, and resolved at connection time.
//!
//! Values containing `${ENV_VAR}` are substituted with the environment
//! variable value at resolution time.

use std::collections::HashMap;

use coda_auth::CredentialStore;

/// Prefix that marks a value stored (encrypted) in the credential store.
pub const SECRET_REF_PREFIX: &str = "coda-secret:";

// ── McpSecretStore ─────────────────────────────────────────────────────────────

/// Key for a server's secret field, e.g. `mcp:github/env/TOKEN`.
///
/// Server names in practice are JSON object keys and must not contain `/`.
pub fn key_for(server: &str, field: &str) -> String {
    assert!(!server.is_empty() && !field.is_empty(), "server and field must be non-empty");
    format!("mcp:{server}/{field}")
}

/// Returns whether `store_key` belongs to this server's field.
///
/// A managed key is either the canonical `mcp:<server>/<field>` or a versioned
/// child `mcp:<server>/<field>/<32-hex-chars>` written by `stage`.
pub fn is_owned_key(server: &str, field: &str, store_key: &str) -> bool {
    if server.is_empty() || field.is_empty() {
        return false;
    }
    let canonical = key_for(server, field);

    if store_key == canonical {
        return true;
    }

    let prefix_with_slash = format!("{canonical}/");
    if let Some(rest) = store_key.strip_prefix(&prefix_with_slash) {
        // Version suffix must be exactly 32 lowercase hex characters.
        return rest.len() == 32 && rest.chars().all(|c| c.is_ascii_hexdigit());
    }

    false
}

/// Extract the credential-store key from a `coda-secret:<key>` reference.
///
/// Returns `None` when the value is not a secret reference, is blank, or
/// contains characters that are unsafe in a key (control characters,
/// non-space whitespace, etc.).
///
/// This validation mirrors `McpSecretStore.TryGetStoreKey` in C#. The C#
/// also rejects Unicode surrogates (impossible in Rust's UTF-8 strings) and
/// Format-category characters (validated here conservatively).
pub fn try_get_store_key(value: &str) -> Option<String> {
    let candidate = value.strip_prefix(SECRET_REF_PREFIX)?;

    if candidate.is_empty() {
        return None;
    }

    // Must not have leading or trailing whitespace.
    let first = candidate.chars().next()?;
    let last = candidate.chars().last()?;
    if first.is_whitespace() || last.is_whitespace() {
        return None;
    }

    // Reject control characters and non-space whitespace.
    // The C# also rejects UnicodeCategory.Format characters; since Rust std
    // does not expose unicode categories, we conservatively reject any character
    // below U+0020 (control range) and the well-known format characters:
    //   U+00AD (soft hyphen), U+200B-U+200F (zero-width spaces / marks),
    //   U+2028-U+2029 (line/paragraph separators), U+FEFF (BOM/ZWNBSP).
    const FORMAT_CHARS: &[char] = &[
        '\u{00AD}', '\u{200B}', '\u{200C}', '\u{200D}', '\u{200E}', '\u{200F}',
        '\u{2028}', '\u{2029}', '\u{FEFF}',
    ];
    for c in candidate.chars() {
        if c.is_control() {
            return None;
        }
        if c.is_whitespace() && c != ' ' {
            return None;
        }
        if FORMAT_CHARS.contains(&c) {
            return None;
        }
    }

    Some(candidate.to_owned())
}

/// Store `value` under `mcp:<server>/<field>` and return the `coda-secret:`
/// reference to write into `.mcp.json`.
pub async fn store(
    credential_store: &dyn CredentialStore,
    server: &str,
    field: &str,
    value: &str,
) -> Result<String, coda_auth::AuthError> {
    let key = key_for(server, field);
    credential_store.set(&key, value).await?;
    Ok(format!("{SECRET_REF_PREFIX}{key}"))
}

/// A secret binding: a typed field and its credential-store key.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpSecretBinding {
    pub field: String,
    pub store_key: String,
}

/// Collect all `coda-secret:` references from a server's resolved config
/// (env values, headers, bearer token) as `McpSecretBinding` entries.
///
/// Used to delete secrets when a server is removed.
pub fn collect_bindings(
    env: &HashMap<String, String>,
    headers: &HashMap<String, String>,
    bearer_token: Option<&str>,
) -> Vec<McpSecretBinding> {
    let mut bindings = Vec::new();

    for (name, value) in env {
        if let Some(key) = try_get_store_key(value) {
            bindings.push(McpSecretBinding { field: format!("env/{name}"), store_key: key });
        }
    }
    for (name, value) in headers {
        if let Some(key) = try_get_store_key(value) {
            bindings.push(McpSecretBinding { field: format!("header/{name}"), store_key: key });
        }
    }
    if let Some(token) = bearer_token {
        if let Some(key) = try_get_store_key(token) {
            bindings.push(McpSecretBinding { field: "auth/token".to_owned(), store_key: key });
        }
    }

    // Sort for determinism (mirrors C# `.OrderBy`).
    bindings.sort_by(|a, b| {
        a.field.cmp(&b.field).then_with(|| a.store_key.cmp(&b.store_key))
    });
    bindings.dedup();
    bindings
}

/// Delete all credential-store keys from the given list.
///
/// Blank keys are ignored; duplicates are skipped. Store failures propagate.
pub async fn delete_keys(
    credential_store: &dyn CredentialStore,
    keys: impl Iterator<Item = &str>,
) -> Result<(), coda_auth::AuthError> {
    let mut seen = std::collections::HashSet::new();
    for key in keys {
        let key = key.trim();
        if key.is_empty() || !seen.insert(key.to_owned()) {
            continue;
        }
        credential_store.delete(key).await?;
    }
    Ok(())
}

// ── McpSecretResolver ─────────────────────────────────────────────────────────

/// Resolve secret-looking values in a map.
///
/// - `coda-secret:<key>` → decrypt from credential store.
/// - `${ENV_VAR}` anywhere → substitute the environment variable.
/// - Anything else → unchanged.
///
/// A missing secret or unset env var resolves to an empty string (never leaks
/// the reference itself). When `log` is provided, it is called for missing
/// secrets/env vars.
pub async fn resolve_value(
    value: &str,
    credential_store: &dyn CredentialStore,
    log: Option<&dyn Fn(&str)>,
) -> String {
    if let Some(key) = value.strip_prefix(SECRET_REF_PREFIX) {
        match credential_store.get(key).await {
            Ok(Some(secret)) => return secret,
            Ok(None) => {
                if let Some(f) = log {
                    f(&format!(
                        "MCP secret '{key}' was not found in the credential store; \
                         using an empty value."
                    ));
                }
                return String::new();
            }
            Err(_) => return String::new(),
        }
    }

    if value.contains("${") {
        return resolve_env_vars(value, log);
    }

    value.to_owned()
}

/// Resolve a map of string values (env / headers), returning a new map with
/// all `coda-secret:` and `${ENV_VAR}` references expanded.
pub async fn resolve_map(
    map: &HashMap<String, String>,
    credential_store: &dyn CredentialStore,
    log: Option<&dyn Fn(&str)>,
) -> HashMap<String, String> {
    if map.is_empty() {
        return HashMap::new();
    }
    let mut result = HashMap::with_capacity(map.len());
    for (k, v) in map {
        result.insert(k.clone(), resolve_value(v, credential_store, log).await);
    }
    result
}

/// Substitute `${NAME}` placeholders in `value` with the current environment.
fn resolve_env_vars(value: &str, log: Option<&dyn Fn(&str)>) -> String {
    let mut result = String::with_capacity(value.len());
    let mut rest = value;

    while let Some(start) = rest.find("${") {
        result.push_str(&rest[..start]);
        let after_dollar = &rest[start + 2..];
        if let Some(end) = after_dollar.find('}') {
            let name = &after_dollar[..end];
            let resolved = match std::env::var(name) {
                Ok(v) => v,
                Err(_) => {
                    if let Some(f) = log {
                        f(&format!(
                            "Environment variable '{name}' referenced in MCP config \
                             is not set; using an empty value."
                        ));
                    }
                    String::new()
                }
            };
            result.push_str(&resolved);
            rest = &after_dollar[end + 1..];
        } else {
            // Unclosed `${` — pass through literally.
            result.push_str(&rest[start..]);
            rest = "";
            break;
        }
    }
    result.push_str(rest);
    result
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use coda_auth::store::InMemoryStore;

    // ── key_for / is_owned_key ────────────────────────────────────────────────

    #[test]
    fn key_for_uses_mcp_namespace() {
        assert_eq!(key_for("github", "env/TOKEN"), "mcp:github/env/TOKEN");
    }

    #[test]
    fn is_owned_key_matches_canonical() {
        assert!(is_owned_key("srv", "env/X", "mcp:srv/env/X"));
    }

    #[test]
    fn is_owned_key_matches_versioned() {
        let key = format!("mcp:srv/env/X/{}", "a".repeat(32));
        assert!(is_owned_key("srv", "env/X", &key));
    }

    #[test]
    fn is_owned_key_rejects_wrong_server() {
        assert!(!is_owned_key("other", "env/X", "mcp:srv/env/X"));
    }

    #[test]
    fn is_owned_key_rejects_partial_prefix() {
        // "mcp:srv" is not a prefix of "mcp:srvother/env/X"
        assert!(!is_owned_key("srv", "env/X", "mcp:srvother/env/X"));
    }

    #[test]
    fn is_owned_key_rejects_version_with_non_hex() {
        let key = format!("mcp:srv/env/X/{}", "z".repeat(32));
        // 'z' is not a hex digit.
        assert!(!is_owned_key("srv", "env/X", &key));
    }

    #[test]
    fn is_owned_key_rejects_wrong_version_length() {
        let key = format!("mcp:srv/env/X/{}", "a".repeat(16));
        assert!(!is_owned_key("srv", "env/X", &key));
    }

    // ── try_get_store_key ────────────────────────────────────────────────────

    #[test]
    fn try_get_store_key_extracts_key() {
        assert_eq!(
            try_get_store_key("coda-secret:mcp:srv/env/TOKEN"),
            Some("mcp:srv/env/TOKEN".to_owned())
        );
    }

    #[test]
    fn try_get_store_key_returns_none_for_non_ref() {
        assert!(try_get_store_key("plaintext").is_none());
    }

    #[test]
    fn try_get_store_key_rejects_empty_key() {
        assert!(try_get_store_key("coda-secret:").is_none());
    }

    #[test]
    fn try_get_store_key_rejects_leading_whitespace() {
        assert!(try_get_store_key("coda-secret: key").is_none());
    }

    #[test]
    fn try_get_store_key_rejects_control_chars() {
        assert!(try_get_store_key("coda-secret:key\x00value").is_none());
    }

    // ── resolve_value ────────────────────────────────────────────────────────

    #[tokio::test]
    async fn resolve_value_retrieves_stored_secret() {
        let store = InMemoryStore::new();
        store.set("mcp:srv/env/X", "secret_value").await.unwrap();
        let resolved =
            resolve_value("coda-secret:mcp:srv/env/X", &store, None).await;
        assert_eq!(resolved, "secret_value");
    }

    #[tokio::test]
    async fn resolve_value_missing_secret_yields_empty() {
        let store = InMemoryStore::new();
        let logged = std::cell::RefCell::new(String::new());
        let resolved = resolve_value(
            "coda-secret:mcp:missing/key",
            &store,
            Some(&|msg: &str| *logged.borrow_mut() = msg.to_owned()),
        )
        .await;
        let logged = logged.into_inner();
        assert!(resolved.is_empty());
        assert!(logged.contains("not found"), "log: {logged}");
    }

    #[tokio::test]
    async fn resolve_value_substitutes_env_var() {
        std::env::set_var("CODA_TEST_MCP_SECRET_VAR", "env_val");
        let store = InMemoryStore::new();
        let resolved =
            resolve_value("prefix_${CODA_TEST_MCP_SECRET_VAR}_suffix", &store, None).await;
        assert_eq!(resolved, "prefix_env_val_suffix");
        std::env::remove_var("CODA_TEST_MCP_SECRET_VAR");
    }

    #[tokio::test]
    async fn resolve_value_unset_env_var_yields_empty_with_log() {
        let store = InMemoryStore::new();
        let logged = std::cell::RefCell::new(String::new());
        let resolved = resolve_value(
            "${CODA_TEST_DEFINITELY_NOT_SET_12345}",
            &store,
            Some(&|msg: &str| *logged.borrow_mut() = msg.to_owned()),
        )
        .await;
        let logged = logged.into_inner();
        assert!(resolved.is_empty());
        assert!(logged.contains("not set"), "log: {logged}");
    }

    #[tokio::test]
    async fn resolve_value_plain_value_is_unchanged() {
        let store = InMemoryStore::new();
        let resolved = resolve_value("plain-value", &store, None).await;
        assert_eq!(resolved, "plain-value");
    }

    // ── collect_bindings ─────────────────────────────────────────────────────

    #[test]
    fn collect_bindings_includes_env_headers_and_token() {
        let env: HashMap<_, _> = [("TOKEN".to_owned(), "coda-secret:mcp:s/env/TOKEN".to_owned())]
            .into_iter()
            .collect();
        let headers: HashMap<_, _> =
            [("X-Key".to_owned(), "coda-secret:mcp:s/header/X-Key".to_owned())]
                .into_iter()
                .collect();
        let bindings = collect_bindings(&env, &headers, Some("coda-secret:mcp:s/auth/token"));
        assert_eq!(bindings.len(), 3);
        let fields: Vec<_> = bindings.iter().map(|b| b.field.as_str()).collect();
        assert!(fields.contains(&"auth/token"), "{fields:?}");
        assert!(fields.contains(&"env/TOKEN"), "{fields:?}");
        assert!(fields.contains(&"header/X-Key"), "{fields:?}");
    }
}
