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

/// Anthropic API-key provider.
pub struct ApiKeyProvider;

impl ApiKeyProvider {
    /// Build a credential from a literal key.
    ///
    /// Use this to create the credential before persisting it.
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
