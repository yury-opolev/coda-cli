//! [`CredentialManagerSource`] — bridges [`CredentialManager`] into the
//! `coda-llm` [`CredentialSource`] seam.
//!
//! # Design
//!
//! The dependency arrow is `coda-auth → coda-llm` (coda-llm is a leaf crate);
//! the reverse is forbidden.  [`CredentialSource`] is therefore defined in
//! `coda-llm`; this module implements it in terms of a live
//! [`CredentialManager`].
//!
//! On each `auth_headers()` call the manager is asked for the current
//! credential for the configured provider; the manager auto-refreshes if the
//! stored token is near expiry (single-flight per provider).

use std::sync::Arc;

use coda_llm::{CredentialSource, LlmError};

use crate::credential::{Credential, CredentialKind};
use crate::manager::CredentialManager;
use crate::failure::AuthFailure;

/// Adapts a [`CredentialManager`] for one provider into a [`CredentialSource`].
///
/// Calling `auth_headers()` asks the manager for the registered provider's
/// current HTTP authentication headers, refreshing when needed. Missing
/// credentials and failures are errors, never permission to reuse static auth:
///
/// - `ApiKey` credential → `x-api-key: <key>` (Anthropic console key)
/// - `OAuth` credential  → `Authorization: Bearer <access_token>`
///
/// The Copilot client additionally receives editor identification headers that
/// must be set via `CopilotConfig::with_header`; this source only provides auth.
pub struct CredentialManagerSource {
    manager: Arc<CredentialManager>,
    provider_id: String,
    /// Whether the last failure was this machine's rather than the provider's.
    ///
    /// A store that will not open, a credential that will not decrypt, and a
    /// credential that is simply not there are all *local*: no request was
    /// sent and the provider refused nothing. A refresh the authorization
    /// server actually rejected is not local — that is the provider answering.
    local_failure: std::sync::atomic::AtomicBool,
}

impl std::fmt::Debug for CredentialManagerSource {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CredentialManagerSource")
            .field("provider_id", &self.provider_id)
            .finish_non_exhaustive()
    }
}

impl CredentialManagerSource {
    pub fn new(manager: Arc<CredentialManager>, provider_id: impl Into<String>) -> Self {
        Self {
            manager,
            provider_id: provider_id.into(),
            local_failure: std::sync::atomic::AtomicBool::new(false),
        }
    }
}

#[async_trait::async_trait]
impl CredentialSource for CredentialManagerSource {
    async fn auth_headers(&self) -> Result<Option<Vec<(String, String)>>, LlmError> {
        use std::sync::atomic::Ordering;

        let headers = match self.manager.get_auth_headers(&self.provider_id).await {
            Ok(headers) => headers,
            Err(error) => {
                let failure = AuthFailure::classify(&error);
                self.local_failure.store(is_local(failure), Ordering::SeqCst);
                return Err(source_failure(failure));
            }
        };
        if headers.is_empty() {
            // A credential that produces no headers is a local fault too: the
            // provider was never asked anything.
            self.local_failure.store(true, Ordering::SeqCst);
            return Err(LlmError::Unauthorized(
                "the stored credential produced no authentication headers".into(),
            ));
        }
        self.local_failure.store(false, Ordering::SeqCst);
        Ok(Some(headers))
    }

    fn last_failure_was_local(&self) -> bool {
        self.local_failure.load(std::sync::atomic::Ordering::SeqCst)
    }
}

/// Whether a classified failure happened on this machine.
///
/// Only an authorization server that actually answered counts as the
/// provider's word; everything else — the store, the filesystem, a missing
/// credential, a credential that cannot be parsed — happened here.
fn is_local(failure: AuthFailure) -> bool {
    !matches!(failure, AuthFailure::OAuthRejected { .. })
}

/// Authenticates with the `ANTHROPIC_API_KEY` exported in this process.
///
/// This is the source for the selection that stores no key. It reads the
/// variable **on every call**, through the same environment port the service
/// selects with, so:
///
/// * no copy of the key is retained anywhere — not in a store, not in a
///   config, not in this struct;
/// * a variable that disappears fails the next request closed, instead of a
///   captured value outliving the environment it came from.
///
/// A missing variable is a *local* failure: no request was sent and the
/// provider refused nothing.
pub struct EnvironmentApiKeySource {
    environment: Arc<dyn crate::service::AuthEnvironment>,
    local_failure: std::sync::atomic::AtomicBool,
}

impl std::fmt::Debug for EnvironmentApiKeySource {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("EnvironmentApiKeySource").finish_non_exhaustive()
    }
}

impl EnvironmentApiKeySource {
    /// A source over `environment`.
    ///
    /// Build a fresh one per probe: [`CredentialSource::last_failure_was_local`]
    /// describes the last attempt *this* source made.
    pub fn new(environment: Arc<dyn crate::service::AuthEnvironment>) -> Self {
        Self { environment, local_failure: std::sync::atomic::AtomicBool::new(false) }
    }
}

#[async_trait::async_trait]
impl CredentialSource for EnvironmentApiKeySource {
    async fn auth_headers(&self) -> Result<Option<Vec<(String, String)>>, LlmError> {
        use std::sync::atomic::Ordering;

        match self.environment.var(crate::provider::api_key::ENV_VAR) {
            Some(key) => {
                let credential = crate::provider::api_key::ApiKeyProvider::prepare_credential(&key)
                    .map_err(|error| {
                        self.local_failure.store(true, Ordering::SeqCst);
                        source_failure(AuthFailure::classify(&error))
                    })?;
                self.local_failure.store(false, Ordering::SeqCst);
                Ok(Some(credential_to_auth_headers(&credential)))
            }
            None => {
                self.local_failure.store(true, Ordering::SeqCst);
                Err(LlmError::Unauthorized(
                    "ANTHROPIC_API_KEY is not set in this process, and no key is stored".into(),
                ))
            }
        }
    }

    fn last_failure_was_local(&self) -> bool {
        self.local_failure.load(std::sync::atomic::Ordering::SeqCst)
    }
}

fn source_failure(failure: AuthFailure) -> LlmError {
    let message = failure.to_string();
    match failure {
        AuthFailure::OAuthRejected { status: 401 | 403 } => LlmError::Unauthorized(message),
        AuthFailure::OAuthRejected { status } => LlmError::Api {
            status, message, kind: coda_llm::error::classify(status),
            retry_after: None, body: None,
        },
        _ if failure.is_transient() => LlmError::Transport(message),
        _ => LlmError::Unauthorized(message),
    }
}

/// Convert a [`Credential`] to the HTTP auth headers expected by the provider.
///
/// This is the canonical mapping; the same logic is tested in isolation so
/// header expectations can be verified without a live HTTP server.
pub fn credential_to_auth_headers(credential: &Credential) -> Vec<(String, String)> {
    match credential.kind {
        CredentialKind::ApiKey => {
            if let Some(key) = &credential.api_key {
                // Anthropic API-key auth: x-api-key header.
                vec![("x-api-key".to_owned(), key.expose().clone())]
            } else {
                vec![]
            }
        }
        CredentialKind::OAuth => {
            if let Some(token) = &credential.access_token {
                // OAuth bearer token: Authorization: Bearer <token> (Anthropic + Copilot).
                vec![(
                    "authorization".to_owned(),
                    format!("Bearer {}", token.expose()),
                )]
            } else {
                vec![]
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::credential::{Credential, CredentialKind};
    use crate::secret::Secret;

    fn api_key_credential(provider: &str, key: &str) -> Credential {
        Credential {
            provider_id: provider.to_owned(),
            kind: CredentialKind::ApiKey,
            api_key: Some(Secret::new(key.to_owned())),
            access_token: None,
            refresh_token: None,
            expires_at: None,
            scopes: vec![],
            account: None,
        }
    }

    fn oauth_credential(provider: &str, token: &str) -> Credential {
        Credential {
            provider_id: provider.to_owned(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new(token.to_owned())),
            refresh_token: None,
            api_key: None,
            expires_at: None,
            scopes: vec!["user:inference".into()],
            account: None,
        }
    }

    // ── IMPORTANT 2: prove a refreshed Credential produces exactly the expected headers ──

    /// An Anthropic API-key credential must produce `x-api-key: <key>`.
    #[test]
    fn anthropic_api_key_credential_produces_x_api_key_header() {
        let cred = api_key_credential("anthropic-api-key", "sk-ant-test-1234");
        let headers = credential_to_auth_headers(&cred);

        assert_eq!(headers.len(), 1, "exactly one header expected");
        let (name, value) = &headers[0];
        assert_eq!(name, "x-api-key", "Anthropic API key must use x-api-key header");
        assert_eq!(value, "sk-ant-test-1234", "key must pass through unchanged");
    }

    /// An OAuth bearer token (Claude.ai subscription) must produce
    /// `Authorization: Bearer <token>`.
    #[test]
    fn oauth_credential_produces_authorization_bearer_header() {
        let cred = oauth_credential("claude-ai", "oauth-access-token-abc");
        let headers = credential_to_auth_headers(&cred);

        assert_eq!(headers.len(), 1);
        let (name, value) = &headers[0];
        assert_eq!(name, "authorization");
        assert!(
            value.starts_with("Bearer "),
            "OAuth token must produce 'Authorization: Bearer ...' header, got: {value}"
        );
        assert!(value.contains("oauth-access-token-abc"), "token must be included verbatim");
    }

    /// A GitHub Copilot OAuth credential must produce `Authorization: Bearer <token>`.
    /// Editor identification headers (editor-version etc.) come from CopilotConfig,
    /// not from the credential source.
    #[test]
    fn copilot_oauth_credential_produces_authorization_bearer_header() {
        let cred = oauth_credential("github-copilot", "ghu_copilot-token-xyz");
        let headers = credential_to_auth_headers(&cred);

        assert_eq!(headers.len(), 1, "auth produces one header; editor headers come from config");
        let (name, value) = &headers[0];
        assert_eq!(name, "authorization");
        assert!(
            value.starts_with("Bearer "),
            "Copilot bearer must start with 'Bearer ', got: {value}"
        );
        assert!(value.contains("ghu_copilot-token-xyz"), "token must be included verbatim");
    }

    /// An OAuth credential missing an `access_token` produces no auth headers
    /// (manager should not have persisted such a credential, but be safe).
    #[test]
    fn oauth_credential_without_token_produces_no_headers() {
        let cred = Credential {
            provider_id: "github-copilot".into(),
            kind: CredentialKind::OAuth,
            access_token: None, // no token
            refresh_token: None,
            api_key: None,
            expires_at: None,
            scopes: vec![],
            account: None,
        };
        let headers = credential_to_auth_headers(&cred);
        assert!(headers.is_empty(), "empty credential must produce no headers");
    }

    /// CredentialManagerSource fetches credentials from the live manager and
    /// maps them to the correct headers.
    #[tokio::test]
    async fn credential_manager_source_produces_correct_headers() {
        use crate::manager::CredentialManager;
        use crate::provider::ApiKeyProvider;
        use crate::store::{CredentialStore, InMemoryStore};

        // Pre-load an API-key credential into the in-memory store.
        let store = Arc::new(InMemoryStore::new());
        let cred = api_key_credential(crate::provider::api_key::PROVIDER_ID, "sk-ant-refreshed-99");
        store.set(
            &format!("llmauth:{}", crate::provider::api_key::PROVIDER_ID),
            &serde_json::to_string(&cred).unwrap(),
        ).await.unwrap();

        let manager = Arc::new(CredentialManager::new(
            store,
            [Arc::new(ApiKeyProvider) as Arc<dyn crate::provider::AuthProvider>],
        ));

        let source = CredentialManagerSource::new(
            Arc::clone(&manager),
            crate::provider::api_key::PROVIDER_ID,
        );

        let headers = source.auth_headers().await.expect("credential lookup succeeds")
            .expect("should produce headers");
        assert_eq!(headers.len(), 1, "exactly one auth header");
        assert_eq!(headers[0].0, "x-api-key");
        assert_eq!(headers[0].1, "sk-ant-refreshed-99");
    }
}
