//! Anthropic API-key provider.
//!
//! No interactive login or refresh — the key is supplied directly or read
//! from the `ANTHROPIC_API_KEY` environment variable.  The provider just
//! wraps it in a [`Credential`] and produces the `x-api-key` header.

use async_trait::async_trait;

use crate::credential::{Credential, CredentialKind};
use crate::error::AuthError;
use crate::provider::AuthProvider;
use crate::secret::Secret;

/// Provider id used as the store key suffix.
pub const PROVIDER_ID: &str = "anthropic-api-key";

/// Environment variable consulted when no key is passed in.
pub const ENV_VAR: &str = "ANTHROPIC_API_KEY";

/// The one normalisation applied to key material, wherever it comes from.
///
/// Surrounding whitespace *and* control characters are stripped: a pasted key
/// routinely carries a trailing newline, and an exported one can pick up a
/// stray carriage return from a script. It matters that this is one function
/// rather than a `trim()` here and a `trim()` there — the value the pre-commit
/// probe validates has to be byte-for-byte the value the engine later puts in
/// the `x-api-key` header, or a login says "connected" about a key the engine
/// never sends.
///
/// Nothing inside the key is touched: only the ends.
pub fn normalize_key(entered: &str) -> &str {
    entered.trim_matches(|c: char| c.is_whitespace() || c.is_control())
}

/// Anthropic API-key provider.
pub struct ApiKeyProvider;

impl ApiKeyProvider {
    /// Build a credential from a literal key.
    ///
    /// Use this to create the credential before persisting it. It does not
    /// validate: [`ApiKeyProvider::prepare_credential`] is the login seam.
    pub fn credential(api_key: impl Into<String>) -> Credential {
        Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::ApiKey,
            access_token: None,
            refresh_token: None,
            api_key: Some(Secret::new(api_key.into())),
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        }
    }

    /// Validate user-entered key material and build the credential to commit.
    ///
    /// This is the login-validation seam: surrounding whitespace is stripped
    /// (a pasted key routinely carries a trailing newline) and an empty or
    /// whitespace-only entry is rejected, because storing it would produce a
    /// credential that looks present and fails at the first request with an
    /// opaque 401.
    ///
    /// **Nothing here proves the key works.** No network call is made — a
    /// prepared credential is a well-formed one, not a verified one. Callers
    /// that need proof must probe with the credential *after* committing it,
    /// and must not report "signed in" on the strength of this call alone.
    ///
    /// The rejection never echoes the entered value: the value is the secret.
    pub fn prepare_credential(entered: &str) -> Result<Credential, AuthError> {
        let trimmed = normalize_key(entered);
        if trimmed.is_empty() {
            return Err(AuthError::InvalidInput {
                field: "API key".into(),
                detail: "it must not be empty".into(),
            });
        }
        Ok(Self::credential(trimmed))
    }

    /// Read the API key from the environment variable.
    pub fn from_env() -> Option<Credential> {
        std::env::var(ENV_VAR)
            .ok()
            .filter(|v| !v.is_empty())
            .map(Self::credential)
    }
}

#[async_trait]
impl AuthProvider for ApiKeyProvider {
    fn provider_id(&self) -> &str {
        PROVIDER_ID
    }

    fn needs_refresh(&self, _credential: &Credential) -> bool {
        // API keys do not expire.
        false
    }

    async fn refresh(&self, credential: &Credential) -> Result<Credential, AuthError> {
        // Nothing to refresh; return the credential unchanged.
        Ok(credential.clone())
    }

    fn auth_headers(&self, credential: &Credential) -> Result<Vec<(String, String)>, AuthError> {
        let key = credential
            .api_key
            .as_ref()
            .map(|s| s.expose().clone())
            .or_else(|| std::env::var(ENV_VAR).ok().filter(|v| !v.is_empty()))
            .ok_or_else(|| {
                AuthError::NotFound(format!(
                    "no API key for provider '{PROVIDER_ID}' \
                     (set {ENV_VAR} or supply a key at login)"
                ))
            })?;

        Ok(vec![("x-api-key".into(), key)])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn credential_does_not_expose_key_in_debug() {
        let cred = ApiKeyProvider::credential("sk-ant-secret-key-123");
        let debug = format!("{cred:?}");
        assert!(
            !debug.contains("sk-ant-secret-key-123"),
            "API key must not appear in Debug output; got: {debug}"
        );
    }

    #[test]
    fn auth_headers_returns_x_api_key() {
        let cred = ApiKeyProvider::credential("my-key");
        let headers = ApiKeyProvider.auth_headers(&cred).expect("headers");
        let key_header = headers.iter().find(|(k, _)| k == "x-api-key");
        assert!(key_header.is_some(), "expected an x-api-key header");
        assert_eq!(key_header.unwrap().1, "my-key");
    }

    #[test]
    fn auth_headers_returns_error_when_no_key() {
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::ApiKey,
            api_key: None,
            access_token: None,
            refresh_token: None,
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        };
        // Temporarily clear the env var so no key can be found.
        let _guard = EnvGuard::clear(ENV_VAR);
        assert!(ApiKeyProvider.auth_headers(&cred).is_err());
    }

    #[test]
    fn needs_refresh_is_always_false() {
        let cred = ApiKeyProvider::credential("k");
        assert!(!ApiKeyProvider.needs_refresh(&cred));
    }

    #[test]
    fn a_blank_entry_is_rejected_before_a_credential_exists() {
        for blank in ["", " ", "\t\r\n", "\u{a0}"] {
            let err = ApiKeyProvider::prepare_credential(blank).expect_err("must be rejected");
            assert!(
                matches!(err, AuthError::InvalidInput { .. }),
                "blank entry {blank:?} produced {err:?}"
            );
        }
    }

    #[test]
    fn a_pasted_key_is_trimmed_and_stays_secret() {
        let cred = ApiKeyProvider::prepare_credential("\n sk-ant-PASTED-key \r\n").expect("key");
        assert_eq!(
            cred.api_key.as_ref().map(|s| s.expose().as_str()),
            Some("sk-ant-PASTED-key")
        );
        assert!(!format!("{cred:?}").contains("PASTED"));
    }

    /// The probe and the engine must agree byte-for-byte, so the normalisation
    /// is one function and it strips control characters a plain `trim` leaves
    /// behind.
    #[test]
    fn the_shared_normalisation_strips_control_characters_a_plain_trim_keeps() {
        let padded = "\u{1}\r\n sk-ant-PADDED-key \t\u{2}";
        assert_eq!(normalize_key(padded), "sk-ant-PADDED-key");
        assert_ne!(padded.trim(), "sk-ant-PADDED-key", "a plain trim is not enough");
        let cred = ApiKeyProvider::prepare_credential(padded).expect("key");
        assert_eq!(
            cred.api_key.as_ref().map(|s| s.expose().as_str()),
            Some(normalize_key(padded))
        );
    }

    #[test]
    fn the_rejection_never_echoes_the_entered_value() {
        // A value that is whitespace-only is all we can reject on content, but
        // the error text must never carry entered material regardless.
        let err = ApiKeyProvider::prepare_credential("   ").expect_err("rejected");
        let rendered = format!("{err} {err:?}");
        assert!(rendered.contains("API key"));
        assert!(!rendered.contains("sk-"));
    }

    #[test]
    fn the_provider_id_is_not_the_engine_alias() {
        assert_eq!(PROVIDER_ID, "anthropic-api-key");
        assert_ne!(PROVIDER_ID, "anthropic");
        assert_eq!(ApiKeyProvider.provider_id(), PROVIDER_ID);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    struct EnvGuard {
        name: &'static str,
        prior: Option<String>,
    }

    impl EnvGuard {
        fn clear(name: &'static str) -> Self {
            let prior = std::env::var(name).ok();
            std::env::remove_var(name);
            Self { name, prior }
        }
    }

    impl Drop for EnvGuard {
        fn drop(&mut self) {
            match &self.prior {
                Some(v) => std::env::set_var(self.name, v),
                None => std::env::remove_var(self.name),
            }
        }
    }
}
