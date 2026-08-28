//! Claude.ai OAuth 2.0 Authorization-Code + PKCE provider.
//!
//! The flow:
//! 1. `begin_login` generates a PKCE verifier/challenge and a CSRF state,
//!    builds the authorization URL, and returns a [`ClaudeAiLoginFlow`].
//! 2. The host opens the authorization URL (e.g. via the system browser).
//! 3. The loopback listener captures `code` and `state` from the redirect.
//! 4. `finish_login` verifies the state (CSRF protection) and exchanges the
//!    code for an access + refresh token.
//! 5. On subsequent requests, `needs_refresh` / `refresh` renew the
//!    short-lived access token using the durable refresh token.
//!
//! Token exchange and refresh both POST JSON to the token endpoint; the
//! token endpoint URL is injectable so tests never hit the real network.

use std::time::Duration;

use async_trait::async_trait;
use serde::Deserialize;

use crate::credential::{AccountInfo, Credential, CredentialKind};
use crate::error::AuthError;
use crate::loopback::LoopbackListener;
use crate::pkce;
use crate::provider::AuthProvider;
use crate::secret::Secret;

/// Provider id.
pub const PROVIDER_ID: &str = "claude-ai";

/// OAuth beta header required for subscription auth.
pub const OAUTH_BETA_HEADER: &str = "oauth-2025-04-20";

/// Refresh 5 minutes before the access token expires (same as the C# client).
const REFRESH_BUFFER: Duration = Duration::from_secs(5 * 60);

/// Claude.ai OAuth configuration.
#[derive(Debug, Clone)]
pub struct ClaudeAiConfig {
    pub client_id: String,
    pub authorize_url: String,
    pub token_url: String,
}

impl ClaudeAiConfig {
    /// Production configuration.
    pub fn production(client_id: impl Into<String>) -> Self {
        Self {
            client_id: client_id.into(),
            authorize_url: "https://claude.com/cai/oauth/authorize".into(),
            token_url: "https://platform.claude.com/v1/oauth/token".into(),
        }
    }

    /// Override the token URL — used by tests to point at a local server.
    pub fn with_token_url(mut self, url: impl Into<String>) -> Self {
        self.token_url = url.into();
        self
    }

    /// Override the authorize URL.
    pub fn with_authorize_url(mut self, url: impl Into<String>) -> Self {
        self.authorize_url = url.into();
        self
    }
}

/// An in-progress Claude.ai login (holds the PKCE verifier and CSRF state).
pub struct ClaudeAiLoginFlow {
    provider: ClaudeAiProvider,
    /// The URL the host must open in the browser.
    pub authorize_url: String,
    /// CSRF state; must match the value in the redirect.
    pub state: String,
    verifier: Secret<String>,
    redirect_uri: String,
}

impl ClaudeAiLoginFlow {
    /// Complete the login by exchanging the authorization code.
    ///
    /// Verifies `returned_state` against the stored state — a mismatch aborts
    /// the login because it indicates a CSRF attack or a stale redirect.
    pub async fn finish(
        self,
        code: &str,
        returned_state: &str,
    ) -> Result<Credential, AuthError> {
        if returned_state != self.state {
            return Err(AuthError::StateMismatch);
        }
        self.provider
            .exchange_code(code, &self.state, self.verifier.expose(), &self.redirect_uri)
            .await
    }
}

/// Claude.ai OAuth provider.
pub struct ClaudeAiProvider {
    config: ClaudeAiConfig,
    http: reqwest::Client,
}

impl ClaudeAiProvider {
    pub fn new(config: ClaudeAiConfig) -> Self {
        let http = reqwest::Client::builder()
            // 15 s matches the axios timeout used by the TypeScript client.
            .timeout(Duration::from_secs(15))
            .build()
            .expect("failed to build HTTP client");
        Self { config, http }
    }

    /// Begin the loopback authorization-code flow.
    ///
    /// Returns a [`ClaudeAiLoginFlow`] whose `authorize_url` the host should
    /// open in a browser, and whose `finish` method completes the exchange.
    pub async fn begin_login(
        &self,
        scopes: &[&str],
    ) -> Result<ClaudeAiLoginFlow, AuthError> {
        let listener = LoopbackListener::bind().await?;
        let redirect_uri = listener.redirect_uri();

        let verifier = pkce::generate_code_verifier();
        let challenge = pkce::generate_code_challenge(&verifier);
        let state = pkce::generate_state();

        let authorize_url = build_authorize_url(
            &self.config.authorize_url,
            &self.config.client_id,
            &redirect_uri,
            scopes,
            &challenge,
            &state,
        );

        Ok(ClaudeAiLoginFlow {
            provider: ClaudeAiProvider {
                config: self.config.clone(),
                http: self.http.clone(),
            },
            authorize_url,
            state,
            verifier: Secret::new(verifier),
            redirect_uri,
        })
    }

    /// Exchange an authorization code for an access/refresh token.
    ///
    /// Called by `ClaudeAiLoginFlow::finish`; also exposed so tests can drive
    /// the exchange directly without going through the loopback listener.
    pub(crate) async fn exchange_code(
        &self,
        code: &str,
        state: &str,
        verifier: &str,
        redirect_uri: &str,
    ) -> Result<Credential, AuthError> {
        let body = serde_json::json!({
            "grant_type": "authorization_code",
            "code": code,
            "redirect_uri": redirect_uri,
            "client_id": self.config.client_id,
            "code_verifier": verifier,
            "state": state,
        });
        let response = self.post_token(&body).await?;
        Ok(token_response_to_credential(response, None))
    }

    async fn do_refresh(&self, credential: &Credential) -> Result<Credential, AuthError> {
        let refresh_token = credential
            .refresh_token
            .as_ref()
            .ok_or_else(|| {
                AuthError::CannotRefresh(
                    PROVIDER_ID.into(),
                    "no refresh token stored".into(),
                )
            })?
            .expose()
            .clone();

        let body = serde_json::json!({
            "grant_type": "refresh_token",
            "refresh_token": refresh_token,
            "client_id": self.config.client_id,
        });
        let response = self.post_token(&body).await?;
        // Keep the old refresh token if the server did not issue a new one.
        let fallback = credential.refresh_token.clone();
        Ok(token_response_to_credential(response, fallback))
    }

    async fn post_token(&self, body: &serde_json::Value) -> Result<OAuthTokenResponse, AuthError> {
        let response = self
            .http
            .post(&self.config.token_url)
            .json(body)
            .send()
            .await
            .map_err(|e| AuthError::Transport(e.to_string()))?;

        let status = response.status().as_u16();
        let text = response
            .text()
            .await
            .map_err(|e| AuthError::Transport(e.to_string()))?;

        if status / 100 != 2 {
            return Err(AuthError::OAuth { status, body: text });
        }

        serde_json::from_str(&text).map_err(Into::into)
    }
}

#[async_trait]
impl AuthProvider for ClaudeAiProvider {
    fn provider_id(&self) -> &str {
        PROVIDER_ID
    }

    fn needs_refresh(&self, credential: &Credential) -> bool {
        credential.kind == CredentialKind::OAuth
            && credential
                .expires_at
                .map(|exp| {
                    chrono::Utc::now() + chrono::Duration::from_std(REFRESH_BUFFER).unwrap() >= exp
                })
                .unwrap_or(false)
    }

    async fn refresh(&self, credential: &Credential) -> Result<Credential, AuthError> {
        self.do_refresh(credential).await
    }

    fn auth_headers(&self, credential: &Credential) -> Result<Vec<(String, String)>, AuthError> {
        let token = credential
            .access_token
            .as_ref()
            .ok_or_else(|| {
                AuthError::NotFound(format!(
                    "provider '{PROVIDER_ID}' has no access token; log in first"
                ))
            })?
            .expose()
            .clone();

        Ok(vec![
            ("authorization".into(), format!("Bearer {token}")),
            ("anthropic-beta".into(), OAUTH_BETA_HEADER.into()),
        ])
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

fn build_authorize_url(
    base: &str,
    client_id: &str,
    redirect_uri: &str,
    scopes: &[&str],
    challenge: &str,
    state: &str,
) -> String {
    let mut params: Vec<(&str, String)> = vec![
        ("code", "true".into()),
        ("client_id", client_id.into()),
        ("response_type", "code".into()),
        ("redirect_uri", redirect_uri.into()),
        ("scope", scopes.join(" ")),
        ("code_challenge", challenge.into()),
        ("code_challenge_method", "S256".into()),
        ("state", state.into()),
    ];

    let pairs: Vec<String> = params
        .drain(..)
        .map(|(k, v)| format!("{}={}", form_encode(k), form_encode(&v)))
        .collect();

    format!("{}?{}", base, pairs.join("&"))
}

/// URL-encodes a value using `+` for spaces (matching `URLSearchParams`).
fn form_encode(value: &str) -> String {
    let mut out = String::new();
    for byte in value.bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                out.push(byte as char);
            }
            b' ' => out.push('+'),
            other => {
                out.push_str(&format!("%{other:02X}"));
            }
        }
    }
    out
}

fn token_response_to_credential(
    resp: OAuthTokenResponse,
    fallback_refresh: Option<Secret<String>>,
) -> Credential {
    let expires_at = resp.expires_in.map(|secs| {
        chrono::Utc::now() + chrono::Duration::seconds(secs)
    });

    let scopes = resp
        .scope
        .as_deref()
        .unwrap_or("")
        .split_whitespace()
        .map(str::to_string)
        .collect();

    let account = build_account_info(&resp);

    Credential {
        provider_id: PROVIDER_ID.into(),
        kind: CredentialKind::OAuth,
        access_token: resp.access_token.map(Secret::new),
        refresh_token: resp.refresh_token.map(Secret::new).or(fallback_refresh),
        api_key: None,
        expires_at,
        scopes,
        account,
    }
}

fn build_account_info(resp: &OAuthTokenResponse) -> Option<AccountInfo> {
    if resp.account.is_none() && resp.organization.is_none() {
        return None;
    }
    Some(AccountInfo {
        account_uuid: resp.account.as_ref().and_then(|a| a.uuid.clone()),
        email_address: resp.account.as_ref().and_then(|a| a.email_address.clone()),
        organization_uuid: resp.organization.as_ref().and_then(|o| o.uuid.clone()),
    })
}

// ── Token endpoint DTOs ───────────────────────────────────────────────────────

#[derive(Deserialize)]
struct OAuthTokenResponse {
    access_token: Option<String>,
    refresh_token: Option<String>,
    expires_in: Option<i64>,
    scope: Option<String>,
    account: Option<OAuthAccount>,
    organization: Option<OAuthOrg>,
}

#[derive(Deserialize)]
struct OAuthAccount {
    uuid: Option<String>,
    email_address: Option<String>,
}

#[derive(Deserialize)]
struct OAuthOrg {
    uuid: Option<String>,
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration as StdDuration;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpListener;

    /// Starts a one-shot HTTP server that returns a fixed JSON body.
    async fn mock_token_server(status: u16, body: &'static str) -> String {
        let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let port = listener.local_addr().expect("addr").port();

        tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.expect("accept");
            let mut buf = vec![0u8; 4096];
            let _ = socket.read(&mut buf).await;

            let reason = if status == 200 { "OK" } else { "Error" };
            let response = format!(
                "HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                body.len()
            );
            let _ = socket.write_all(response.as_bytes()).await;
        });

        format!("http://127.0.0.1:{port}")
    }

    fn config(token_url: &str) -> ClaudeAiConfig {
        ClaudeAiConfig::production("test-client-id").with_token_url(token_url)
    }

    fn provider(token_url: &str) -> ClaudeAiProvider {
        ClaudeAiProvider::new(config(token_url))
    }

    #[test]
    fn oauth_state_mismatch_is_rejected() {
        // Build a login flow by hand to test the state-verification guard.
        let verifier = pkce::generate_code_verifier();
        let state = "real_state".to_string();
        let flow = ClaudeAiLoginFlow {
            provider: provider("http://unused"),
            authorize_url: "http://unused".into(),
            state: state.clone(),
            verifier: Secret::new(verifier),
            redirect_uri: "http://localhost:0/callback".into(),
        };

        // A different state → should be rejected even without hitting the network.
        let rt = tokio::runtime::Runtime::new().unwrap();
        let err = rt
            .block_on(flow.finish("code", "tampered_state"))
            .unwrap_err();
        assert!(
            matches!(err, AuthError::StateMismatch),
            "expected StateMismatch, got {err:?}"
        );
    }

    #[tokio::test]
    async fn exchange_code_parses_token_response() {
        let body = r#"{
            "access_token": "acc_abc123",
            "refresh_token": "ref_xyz456",
            "expires_in": 3600,
            "scope": "user:inference user:profile"
        }"#;
        let url = mock_token_server(200, body).await;

        let p = provider(&url);
        let cred = p
            .exchange_code("code", "state", "verifier", "http://localhost/cb")
            .await
            .expect("exchange");

        assert_eq!(cred.provider_id, PROVIDER_ID);
        assert_eq!(
            cred.access_token.as_ref().map(|s| s.expose().as_str()),
            Some("acc_abc123")
        );
        assert_eq!(
            cred.refresh_token.as_ref().map(|s| s.expose().as_str()),
            Some("ref_xyz456")
        );
        assert_eq!(cred.scopes, ["user:inference", "user:profile"]);
    }

    #[tokio::test]
    async fn refresh_falls_back_to_existing_refresh_token_when_server_omits_it() {
        let body = r#"{"access_token": "new_acc", "expires_in": 3600}"#;
        let url = mock_token_server(200, body).await;

        let old_cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("old_acc".into())),
            refresh_token: Some(Secret::new("keep_me".into())),
            api_key: None,
            expires_at: Some(chrono::Utc::now()),
            scopes: Vec::new(),
            account: None,
        };

        let p = provider(&url);
        let new_cred = p.refresh(&old_cred).await.expect("refresh");

        assert_eq!(
            new_cred.access_token.as_ref().map(|s| s.expose().as_str()),
            Some("new_acc")
        );
        // The original refresh token must be preserved.
        assert_eq!(
            new_cred.refresh_token.as_ref().map(|s| s.expose().as_str()),
            Some("keep_me")
        );
    }

    #[tokio::test]
    async fn oauth_error_response_is_surfaced() {
        let url = mock_token_server(400, r#"{"error":"invalid_grant"}"#).await;
        let p = provider(&url);
        let err = p
            .exchange_code("bad_code", "state", "verifier", "http://localhost/cb")
            .await
            .unwrap_err();
        assert!(
            matches!(err, AuthError::OAuth { status: 400, .. }),
            "expected OAuth error, got {err:?}"
        );
    }

    #[test]
    fn needs_refresh_is_true_when_token_nearly_expired() {
        let expires_at = chrono::Utc::now() + chrono::Duration::minutes(2);
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("token".into())),
            refresh_token: Some(Secret::new("ref".into())),
            api_key: None,
            expires_at: Some(expires_at),
            scopes: Vec::new(),
            account: None,
        };
        let p = provider("http://unused");
        assert!(p.needs_refresh(&cred));
    }

    #[test]
    fn needs_refresh_is_false_when_token_has_ample_time() {
        let expires_at = chrono::Utc::now() + chrono::Duration::hours(1);
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("token".into())),
            refresh_token: None,
            api_key: None,
            expires_at: Some(expires_at),
            scopes: Vec::new(),
            account: None,
        };
        let p = provider("http://unused");
        assert!(!p.needs_refresh(&cred));
    }

    #[test]
    fn auth_headers_contains_bearer_and_beta_header() {
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("my_token".into())),
            refresh_token: None,
            api_key: None,
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        };
        let headers = ClaudeAiProvider::new(ClaudeAiConfig::production("x"))
            .auth_headers(&cred)
            .expect("headers");

        let auth = headers.iter().find(|(k, _)| k == "authorization");
        assert!(auth.is_some(), "expected authorization header");
        assert!(auth.unwrap().1.starts_with("Bearer "));

        let beta = headers.iter().find(|(k, _)| k == "anthropic-beta");
        assert!(beta.is_some(), "expected anthropic-beta header");
    }

    #[test]
    fn auth_headers_do_not_contain_the_raw_token() {
        // The header VALUE contains the token, but the token itself must NOT appear
        // at log-site if we ever accidentally format the headers vec.
        // We assert here that there is no way to get the token from the debug output
        // of the Credential itself; the header value is intentionally the token
        // because that is what HTTP requires.
        let token = "extremely_secret_bearer_token_xyz";
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new(token.into())),
            refresh_token: None,
            api_key: None,
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        };
        let debug = format!("{cred:?}");
        assert!(
            !debug.contains(token),
            "token must not appear in Credential Debug output; got: {debug}"
        );
    }

    #[test]
    fn loopback_listener_uses_localhost_redirect_uri() {
        // The redirect_uri must start with http://localhost (loopback host).
        // Build a synthetic flow to check the format.
        let rt = tokio::runtime::Runtime::new().unwrap();
        let listener = rt.block_on(LoopbackListener::bind()).expect("bind");
        let uri = listener.redirect_uri();
        assert!(
            uri.starts_with("http://localhost:"),
            "redirect_uri must be a loopback URL: {uri}"
        );
        assert!(uri.ends_with("/callback"), "redirect_uri must end with /callback: {uri}");
    }

    #[test]
    fn loopback_timeout_is_login_cancelled() {
        let rt = tokio::runtime::Runtime::new().unwrap();
        let listener = rt.block_on(LoopbackListener::bind()).expect("bind");
        let err = rt
            .block_on(listener.wait_for_callback(StdDuration::from_millis(50)))
            .unwrap_err();
        assert!(matches!(err, AuthError::LoginCancelled(_)));
    }
}
