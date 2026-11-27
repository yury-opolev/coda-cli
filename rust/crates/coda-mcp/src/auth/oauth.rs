//! MCP OAuth 2.1 + PKCE authorization flow.
//!
//! Mirrors `McpOAuthProvider.cs` (and `DefaultMcpOAuthReauthenticator.cs`).
//!
//! On a 401 challenge, this provider:
//! 1. Fetches RFC 9728 protected-resource metadata (from the challenge or the
//!    well-known origin path).
//! 2. Fetches RFC 8414 / OIDC authorization-server metadata.
//! 3. Resolves a client id (configured → cached DCR → fresh DCR).
//! 4. Builds the authorize URL with PKCE + RFC 8707 `resource` parameter
//!    (omitted for Entra v2.0 issuers, which reject it).
//! 5. Opens a browser and waits for the loopback redirect.
//! 6. Validates state (CSRF) and iss (RFC 9207) from the callback.
//! 7. Exchanges the code for an access + refresh token.
//! 8. Persists the token keyed by canonical resource URI.
//!
//! Refresh tokens are used silently on expiry; a failed refresh deletes the
//! stored token so the next request triggers a fresh 401 → auth flow.

use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use coda_auth::{
    CredentialStore,
    loopback::LoopbackListener,
    pkce,
    secret::Secret,
};
use serde::Deserialize;

use super::{
    metadata_client::McpAuthMetadataClient,
    provider::McpAuthProvider,
    types::{
        AuthorizationServerMetadata, McpAuthConfig, McpAuthMode, McpAuthResult, McpAuthStatus,
        McpClientIdResolution, McpClientRegistration, McpStoredToken,
        ProtectedResourceMetadata, WwwAuthenticateChallenge, canonical_resource_uri,
    },
};
use super::entra;

// ── OAuthTokenResponse ────────────────────────────────────────────────────────

/// JSON body returned by the token endpoint.
#[derive(Debug, Deserialize)]
struct OAuthTokenResponse {
    access_token: Option<Secret<String>>,
    refresh_token: Option<Secret<String>>,
    expires_in: Option<i64>,
    scope: Option<String>,
}

// ── McpOAuthProvider ──────────────────────────────────────────────────────────

/// Full MCP OAuth 2.1 + PKCE flow for one HTTP server.
///
/// Thread-safe: wrapped in `Arc` by callers.
pub struct McpOAuthProvider {
    http: reqwest::Client,
    metadata: McpAuthMetadataClient,
    store: Arc<dyn CredentialStore>,
    canonical_resource: String,
    config: McpAuthConfig,
    interactive: bool,
    /// Called with the authorization URL to open the system browser.
    open_browser: Arc<dyn Fn(&str) -> Result<(), String> + Send + Sync>,
    /// Optional structured log callback (used by the TUI to show progress).
    log: Option<Arc<dyn Fn(&str) + Send + Sync>>,
    /// Most recent user-facing failure, for status surfaces.
    last_error: std::sync::Mutex<Option<String>>,
    /// Expiry of the current access token (Unix seconds), when known.
    expires_at: std::sync::Mutex<Option<i64>>,
}

impl McpOAuthProvider {
    /// Construct a provider for one HTTP MCP server.
    ///
    /// - `url` — the MCP server URL; used to derive the canonical resource URI.
    /// - `store` — credential store for access/refresh tokens and cached DCR.
    /// - `config` — `auth` block from `.mcp.json`.
    /// - `interactive` — set to `false` in headless mode; `handle_unauthorized`
    ///   will log a help message but not open a browser.
    /// - `open_browser` — optional override; defaults to `default_open_browser`.
    /// - `log` — optional progress callback.
    pub fn new(
        http: reqwest::Client,
        url: &reqwest::Url,
        store: Arc<dyn CredentialStore>,
        config: McpAuthConfig,
        interactive: bool,
        open_browser: Option<Arc<dyn Fn(&str) -> Result<(), String> + Send + Sync>>,
        log: Option<Arc<dyn Fn(&str) + Send + Sync>>,
    ) -> Self {
        let canonical_resource = canonical_resource_uri(url);
        let metadata = McpAuthMetadataClient::new(http.clone());
        let open_browser = open_browser
            .unwrap_or_else(|| Arc::new(default_open_browser));
        Self {
            http,
            metadata,
            store,
            canonical_resource,
            config,
            interactive,
            open_browser,
            log,
            last_error: std::sync::Mutex::new(None),
            expires_at: std::sync::Mutex::new(None),
        }
    }

    /// Secret-free snapshot of this provider's auth state.
    pub fn status_snapshot(&self) -> McpAuthStatus {
        McpAuthStatus {
            mode: self.config.mode,
            account: None,
            expires_at_unix: *lock(&self.expires_at),
            last_error: lock(&self.last_error).clone(),
        }
    }

    pub(crate) fn record_error(&self, msg: &str) {
        *lock(&self.last_error) = Some(msg.to_owned());
    }

    pub(crate) fn clear_error(&self) {
        *lock(&self.last_error) = None;
    }

    fn record_expiry(&self, expires_at_unix: Option<i64>) {
        *lock(&self.expires_at) = expires_at_unix.filter(|&v| v > 0);
    }

    fn token_key(&self) -> String {
        format!("mcp-token:{}", self.canonical_resource)
    }

    fn client_key(issuer: &str) -> String {
        format!("mcp-client:{issuer}")
    }

    fn display_resource(&self) -> &str {
        self.canonical_resource.split('?').next().unwrap_or(&self.canonical_resource)
    }

    fn log(&self, msg: &str) {
        if let Some(f) = &self.log {
            f(msg);
        }
    }

    fn failure(&self, msg: &str) -> McpAuthResult {
        self.log(msg);
        self.record_error(msg);
        McpAuthResult { succeeded: false, error: Some(msg.to_owned()) }
    }

    /// Proactively force a fresh authorization without waiting for a 401.
    /// Cached DCR registrations are retained.
    pub async fn force_reauthorize(&self) -> McpAuthResult {
        // The browser flow is also the fallback for `entra-broker` servers.
        if !matches!(self.config.mode, McpAuthMode::OAuth | McpAuthMode::EntraBroker) {
            return self.failure("MCP OAuth reauthentication requires OAuth authentication mode.");
        }
        if !self.interactive {
            return self.failure("MCP OAuth reauthentication requires an interactive session.");
        }

        // Delete the current token so `acquire_token` runs the full flow.
        if let Err(e) = self.store.delete(&self.token_key()).await {
            tracing::debug!(error = %e, "failed to clear stored token for reauth");
            return self.failure(
                "MCP OAuth reauthentication could not clear the stored token. Please retry.",
            );
        }
        self.record_expiry(None);

        match self.acquire_token(None, true).await {
            Ok(r) => r,
            Err(AcquireError::Cancelled) => {
                let msg = "MCP OAuth reauthentication was canceled.";
                self.record_error(msg);
                McpAuthResult { succeeded: false, error: Some(msg.into()) }
            }
            Err(AcquireError::Other(msg)) => {
                self.record_error(&msg);
                McpAuthResult { succeeded: false, error: Some(msg) }
            }
        }
    }

    /// Load a valid access token from the store, refreshing if needed.
    async fn load_token(&self) -> Option<McpStoredToken> {
        let loaded = match self.store.get(&self.token_key()).await {
            Ok(Some(json)) => serde_json::from_str::<McpStoredToken>(&json).ok(),
            _ => None,
        };
        self.record_expiry(loaded.as_ref().map(|t| t.expires_at_unix));
        loaded
    }

    /// Attempt to refresh `stored` using its refresh token.
    ///
    /// On any failure the stored token is deleted so the next request can
    /// trigger a fresh 401 → full flow.
    async fn refresh_token(&self, stored: &McpStoredToken) -> Option<McpStoredToken> {
        let refresh_token = stored.refresh_token.as_ref()?.expose().clone();
        let discovery = stored.discovery_url();

        let as_meta = self
            .metadata
            .get_authorization_server_metadata(discovery)
            .await?;

        let mut form = vec![
            ("grant_type".to_owned(), "refresh_token".to_owned()),
            ("refresh_token".to_owned(), refresh_token),
            ("client_id".to_owned(), stored.client_id.clone()),
        ];
        form.extend(resource_param(
            is_entra_flow(&as_meta.issuer, discovery),
            &self.canonical_resource,
        ));

        let token_resp = match self.token_request(&as_meta.token_endpoint, &form).await {
            Ok(t) => t,
            Err(_) => {
                // Refresh rejected → delete so next request re-authorizes.
                let _ = self.store.delete(&self.token_key()).await;
                self.record_expiry(None);
                return None;
            }
        };

        let access = token_resp.access_token?;
        // A rotated refresh token replaces the old one; otherwise keep existing.
        let new_refresh = token_resp.refresh_token.or_else(|| stored.refresh_token.clone());

        self.persist_token(
            access,
            new_refresh,
            token_resp.expires_in,
            TokenOrigin {
                issuer: &stored.issuer,
                discovery_issuer: discovery,
                client_id: &stored.client_id,
            },
            token_resp.scope.as_deref().unwrap_or(&stored.scope),
        )
        .await
        .ok()
    }

    /// Persist a token. Every successful acquisition or refresh goes through
    /// here, so it also clears any previously recorded failure.
    async fn persist_token(
        &self,
        access_token: Secret<String>,
        refresh_token: Option<Secret<String>>,
        expires_in: Option<i64>,
        origin: TokenOrigin<'_>,
        scope: &str,
    ) -> Result<McpStoredToken, coda_auth::AuthError> {
        let now_unix = unix_now();
        let expires_at_unix = expires_in
            .filter(|&v| v > 0)
            .map(|v| now_unix + v)
            .unwrap_or(0);

        let stored = McpStoredToken {
            access_token,
            refresh_token,
            expires_at_unix,
            scope: scope.to_owned(),
            issuer: origin.issuer.to_owned(),
            client_id: origin.client_id.to_owned(),
            discovery_issuer: Some(origin.discovery_issuer.to_owned())
                .filter(|d| !d.is_empty() && d != origin.issuer),
        };

        let json = serde_json::to_string(&stored).map_err(|e| {
            coda_auth::AuthError::Serialization(e)
        })?;
        self.store.set(&self.token_key(), &json).await?;
        self.record_expiry(Some(stored.expires_at_unix));
        *lock(&self.last_error) = None;
        Ok(stored)
    }

    /// POST a form to the token endpoint and parse the response.
    async fn token_request(
        &self,
        endpoint: &str,
        form: &[(String, String)],
    ) -> Result<OAuthTokenResponse, OAuthExchangeError> {
        let response = self
            .http
            .post(endpoint)
            .form(form)
            .header("Accept", "application/json")
            .send()
            .await
            .map_err(|e| OAuthExchangeError::Transport(e.to_string()))?;

        let status = response.status().as_u16();
        let body = response.text().await.unwrap_or_default();

        if status < 200 || status >= 300 {
            return Err(OAuthExchangeError::Status(status, body));
        }

        serde_json::from_str::<OAuthTokenResponse>(&body)
            .map_err(|e| OAuthExchangeError::Parse(e.to_string()))
    }

    /// Resolve a client id: configured → cached DCR → fresh DCR.
    async fn resolve_client_id(
        &self,
        as_meta: &AuthorizationServerMetadata,
        redirect_uri: &str,
    ) -> McpClientIdResolution {
        // 1. Configured client id takes priority.
        if let Some(id) = &self.config.client_id {
            if !id.is_empty() {
                return McpClientIdResolution::success(id.clone());
            }
        }

        // 2. Cached DCR registration.
        let client_key = Self::client_key(&as_meta.issuer);
        if let Ok(Some(json)) = self.store.get(&client_key).await {
            if let Ok(cached) = serde_json::from_str::<McpClientRegistration>(&json) {
                if !cached.client_id.is_empty() {
                    return McpClientIdResolution::success(cached.client_id);
                }
            }
        }

        // 3. Dynamic Client Registration.
        let reg_endpoint = match &as_meta.registration_endpoint {
            Some(ep) => ep.clone(),
            None => {
                return McpClientIdResolution::failure(format!(
                    "Authorization server {} does not advertise dynamic client registration. \
                     Configure auth.clientId or use an authenticated stdio proxy.",
                    as_meta.issuer
                ));
            }
        };

        let reg = self
            .metadata
            .register_client(
                &reg_endpoint,
                redirect_uri,
                &["authorization_code", "refresh_token"],
            )
            .await;

        match reg {
            Some(r) if !r.client_id.is_empty() => {
                // Cache the registration.
                if let Ok(json) = serde_json::to_string(&r) {
                    let _ = self.store.set(&client_key, &json).await;
                }
                McpClientIdResolution::success(r.client_id)
            }
            _ => McpClientIdResolution::failure(format!(
                "Dynamic client registration failed at {reg_endpoint}. \
                 Configure auth.clientId or use an authenticated stdio proxy."
            )),
        }
    }

    /// Run the full authorization-code + PKCE flow, or a forced reauth.
    async fn acquire_token(
        &self,
        challenge: Option<&WwwAuthenticateChallenge>,
        redact_messages: bool,
    ) -> Result<McpAuthResult, AcquireError> {
        if let Some(tenant) = self.config.tenant.as_deref() {
            if !is_valid_tenant(tenant) {
                return Ok(self.failure(&format!(
                    "Invalid auth.tenant '{tenant}': expected a tenant id, domain, \
                     'organizations', 'common' or 'consumers' (letters, digits, '.' and '-')."
                )));
            }
        }

        // 1. Protected-resource metadata (from challenge or well-known path).
        let metadata_url =
            resolve_resource_metadata_url(challenge, &self.canonical_resource);
        let prm = self
            .metadata
            .get_protected_resource_metadata(&metadata_url)
            .await;

        if prm.is_none() {
            let msg = if redact_messages {
                "Could not discover OAuth protected-resource metadata. Verify the MCP server URL and try again.".to_owned()
            } else {
                format!("No authorization server advertised by {}.", self.display_resource())
            };
            return Ok(self.failure(&msg));
        }
        let prm = prm.as_ref();

        let issuer_candidate = prm
            .and_then(|p| p.authorization_servers.first().map(String::as_str))
            .unwrap_or("");
        if issuer_candidate.is_empty() {
            let msg = if redact_messages {
                "The MCP server did not advertise an OAuth authorization server.".to_owned()
            } else {
                format!("No authorization server advertised by {}.", self.display_resource())
            };
            return Ok(self.failure(&msg));
        }
        let issuer_candidate = issuer_candidate_for(issuer_candidate, self.config.tenant.as_deref());
        let issuer_candidate = issuer_candidate.as_str();

        // 2. Authorization-server metadata.
        let as_meta = self
            .metadata
            .get_authorization_server_metadata(issuer_candidate)
            .await;
        let Some(as_meta) = as_meta else {
            let msg = if redact_messages {
                "Could not discover OAuth authorization-server metadata. Verify the server configuration and try again.".to_owned()
            } else {
                format!("Could not discover authorization-server metadata for {issuer_candidate}.")
            };
            return Ok(self.failure(&msg));
        };

        // 3. Bind the loopback listener first so DCR can register the exact redirect URI.
        let bound = match &self.config.redirect_uri {
            Some(template) => LoopbackListener::bind_template(template).await,
            None => LoopbackListener::bind().await,
        };
        let listener = match bound {
            Ok(l) => l,
            Err(coda_auth::AuthError::InvalidInput { detail, .. }) => {
                return Ok(self.failure(&format!(
                    "Invalid auth.redirectUri '{}': {detail}",
                    self.config.redirect_uri.as_deref().unwrap_or_default()
                )));
            }
            Err(e) => return Err(AcquireError::Other(format!("loopback bind failed: {e}"))),
        };
        let redirect_uri = listener.redirect_uri();

        let resolution = self.resolve_client_id(&as_meta, &redirect_uri).await;
        let client_id = match &resolution.client_id {
            Some(id) => id.clone(),
            None => {
                let msg = if redact_messages {
                    "No OAuth client id is available. Configure auth.clientId or enable dynamic client registration.".to_owned()
                } else {
                    resolution
                        .error
                        .unwrap_or_else(|| format!(
                            "No client id available for {}. Configure auth.clientId or use an authenticated stdio proxy.",
                            as_meta.issuer
                        ))
                };
                return Ok(self.failure(&msg));
            }
        };

        // 4. Scope selection.
        let entra_flow = is_entra_flow(&as_meta.issuer, issuer_candidate);
        let scopes = scopes_for_request(
            challenge,
            &self.config,
            prm,
            &as_meta,
            &self.canonical_resource,
            entra_flow,
        );

        // 5. PKCE + authorize URL.
        let verifier = pkce::generate_code_verifier();
        let code_challenge = pkce::generate_code_challenge(&verifier);
        let state = pkce::generate_state();

        let mut auth_params: Vec<(String, String)> = vec![
            ("response_type".to_owned(), "code".to_owned()),
            ("client_id".to_owned(), client_id.clone()),
            ("redirect_uri".to_owned(), redirect_uri.clone()),
            ("code_challenge".to_owned(), code_challenge),
            ("code_challenge_method".to_owned(), "S256".to_owned()),
            ("state".to_owned(), state.clone()),
        ];
        auth_params.extend(resource_param(entra_flow, &self.canonical_resource));
        if !scopes.is_empty() {
            auth_params.push(("scope".to_owned(), scopes.join(" ")));
        }

        let auth_url =
            build_authorize_url(&as_meta.authorization_endpoint, &auth_params);

        let open_msg = if redact_messages {
            "Opening browser to authorize MCP server…".to_owned()
        } else {
            format!("Opening browser to authorize MCP server {}…", self.display_resource())
        };
        self.log(&open_msg);

        // Open the browser; surface the URL AFTER so that if the opener rejects
        // a hostile scheme the URL is never shown as copy-pasteable text.
        if let Err(e) = (self.open_browser)(&auth_url) {
            // A scheme-rejection failure from the opener is still a validation
            // failure; surface it and abort.
            return Ok(self.failure(&format!("Failed to open browser: {e}")));
        }
        self.log(&format!("Authorization URL: {auth_url}"));

        // 6. Wait for the redirect.
        let redirect = listener
            .wait_for_callback(Duration::from_secs(300))
            .await
            .map_err(|e| {
                if matches!(e, coda_auth::AuthError::LoginCancelled(_)) {
                    AcquireError::Cancelled
                } else {
                    AcquireError::Other(e.to_string())
                }
            })?;

        // 7. Validate the redirect.
        if let Some(err) = &redirect.error {
            return Ok(self.failure(&format!(
                "Authorization server returned an error: {err}"
            )));
        }
        if redirect.state.as_deref() != Some(&state) {
            return Err(AcquireError::Other(
                "OAuth state mismatch (possible CSRF); aborting login.".into(),
            ));
        }
        validate_callback_iss(
            redirect.iss.as_deref(),
            &as_meta.issuer,
            as_meta.issuer_parameter_supported,
        )
        .map_err(AcquireError::Other)?;
        let code = match &redirect.code {
            Some(c) => c.clone(),
            None => {
                return Err(AcquireError::Other(
                    "Authorization response did not include a code.".into(),
                ));
            }
        };

        // 8. Exchange the code.
        let mut form = vec![
            ("grant_type".to_owned(), "authorization_code".to_owned()),
            ("code".to_owned(), code),
            ("redirect_uri".to_owned(), redirect_uri),
            ("client_id".to_owned(), client_id.clone()),
            ("code_verifier".to_owned(), verifier),
        ];
        form.extend(resource_param(entra_flow, &self.canonical_resource));
        let token_resp = match self.token_request(&as_meta.token_endpoint, &form).await {
            Ok(t) => t,
            Err(OAuthExchangeError::Status(s, body)) => {
                tracing::debug!(status = s, "token exchange failed: {body}");
                let msg = if redact_messages {
                    "OAuth authorization completed without an access token. Please retry.".to_owned()
                } else {
                    format!("Token exchange failed for {}.", self.display_resource())
                };
                return Ok(self.failure(&msg));
            }
            Err(e) => {
                return Ok(self.failure(&format!("Token exchange error: {e}")));
            }
        };

        let Some(access_token) = token_resp.access_token else {
            let msg = if redact_messages {
                "OAuth authorization completed without an access token. Please retry.".to_owned()
            } else {
                format!("Token exchange returned no access token for {}.", self.display_resource())
            };
            return Ok(self.failure(&msg));
        };

        let scopes_joined = scopes.join(" ");
        let scope = token_resp
            .scope
            .as_deref()
            .filter(|s| !s.is_empty())
            .unwrap_or(&scopes_joined)
            .to_owned();

        self.persist_token(
            access_token,
            token_resp.refresh_token,
            token_resp.expires_in,
            TokenOrigin {
                issuer: &as_meta.issuer,
                discovery_issuer: issuer_candidate,
                client_id: &client_id,
            },
            &scope,
        )
        .await
        .map_err(|e| AcquireError::Other(e.to_string()))?;

        let done_msg = if redact_messages {
            "Authorized MCP server.".to_owned()
        } else {
            format!("Authorized MCP server {}.", self.display_resource())
        };
        self.log(&done_msg);
        Ok(McpAuthResult { succeeded: true, error: None })
    }
}

const NEEDS_INTERACTIVE_ERROR: &str =
    "MCP server requires authorization. Run `coda` interactively to sign in";

/// Whether `msg` is the headless provider's "sign in interactively" error
/// (as opposed to a more specific failure).
pub(crate) fn is_needs_interactive_error(msg: &str) -> bool {
    msg.starts_with(NEEDS_INTERACTIVE_ERROR)
}

#[async_trait]
impl McpAuthProvider for McpOAuthProvider {
    async fn get_access_token(&self) -> Option<Secret<String>> {
        let stored = self.load_token().await?;
        let now = unix_now();
        if !stored.is_expired(now) {
            return Some(stored.access_token);
        }
        // Attempt silent refresh.
        let refreshed = self.refresh_token(&stored).await?;
        Some(refreshed.access_token)
    }

    async fn handle_unauthorized(&self, www_authenticate: Option<&str>) -> bool {
        let challenge = WwwAuthenticateChallenge::parse(www_authenticate);

        if !self.interactive {
            let msg = format!("{NEEDS_INTERACTIVE_ERROR} to {}.", self.display_resource());
            self.log(&msg);
            self.record_error(&msg);
            return false;
        }

        match self.acquire_token(challenge.as_ref(), false).await {
            Ok(r) => r.succeeded,
            Err(AcquireError::Cancelled) => {
                let msg = format!("Authorization canceled for {}.", self.display_resource());
                self.log(&msg);
                self.record_error(&msg);
                false
            }
            Err(AcquireError::Other(msg)) => {
                let msg = format!("Authorization failed: {msg}");
                self.log(&msg);
                self.record_error(&msg);
                false
            }
        }
    }

    fn status(&self) -> Option<McpAuthStatus> {
        Some(self.status_snapshot())
    }

    async fn login(&self) -> McpAuthResult {
        self.force_reauthorize().await
    }

    async fn logout(&self) -> McpAuthResult {
        if let Err(e) = self.store.delete(&self.token_key()).await {
            tracing::debug!(error = %e, "failed to delete stored MCP token on logout");
            return self.failure("Could not remove the stored token. Please retry.");
        }
        self.record_expiry(None);
        self.clear_error();
        McpAuthResult { succeeded: true, error: None }
    }
}

// ── DefaultMcpOAuthReauthenticator ────────────────────────────────────────────

/// Starts proactive OAuth reauthentication for an HTTP MCP server (mirrors
/// `DefaultMcpOAuthReauthenticator.cs`).
pub struct McpOAuthReauthenticator {
    http: reqwest::Client,
    store: Arc<dyn CredentialStore>,
    open_browser: Option<Arc<dyn Fn(&str) -> Result<(), String> + Send + Sync>>,
    log: Option<Arc<dyn Fn(&str) + Send + Sync>>,
}

impl McpOAuthReauthenticator {
    pub fn new(
        http: reqwest::Client,
        store: Arc<dyn CredentialStore>,
        open_browser: Option<Arc<dyn Fn(&str) -> Result<(), String> + Send + Sync>>,
        log: Option<Arc<dyn Fn(&str) + Send + Sync>>,
    ) -> Self {
        Self { http, store, open_browser, log }
    }

    pub async fn reauthenticate(
        &self,
        url: &reqwest::Url,
        config: &McpAuthConfig,
    ) -> McpAuthResult {
        if !matches!(config.mode, McpAuthMode::OAuth | McpAuthMode::EntraBroker) {
            return McpAuthResult {
                succeeded: false,
                error: Some(
                    "MCP OAuth reauthentication requires OAuth authentication mode.".into(),
                ),
            };
        }

        let provider = McpOAuthProvider::new(
            self.http.clone(),
            url,
            Arc::clone(&self.store),
            config.clone(),
            true,
            self.open_browser.clone(),
            self.log.clone(),
        );
        provider.force_reauthorize().await
    }
}

// ── internal helpers ──────────────────────────────────────────────────────────

#[derive(Debug)]
enum AcquireError {
    Cancelled,
    Other(String),
}

/// Where a token came from, as persisted alongside it.
#[derive(Debug, Clone, Copy)]
struct TokenOrigin<'a> {
    /// Issuer advertised in the AS metadata (used for `iss` checks).
    issuer: &'a str,
    /// URL the AS metadata was discovered from (used again on refresh).
    discovery_issuer: &'a str,
    client_id: &'a str,
}

#[derive(Debug)]
enum OAuthExchangeError {
    Transport(String),
    Status(u16, String),
    Parse(String),
}

impl std::fmt::Display for OAuthExchangeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Transport(e) => write!(f, "transport error: {e}"),
            Self::Status(s, b) => write!(f, "HTTP {s}: {}", truncate(b, 200)),
            Self::Parse(e) => write!(f, "parse error: {e}"),
        }
    }
}

/// Determine the resource metadata URL from the challenge or the canonical
/// resource's well-known path.
pub(crate) fn resolve_resource_metadata_url(
    challenge: Option<&WwwAuthenticateChallenge>,
    canonical_resource: &str,
) -> String {
    if let Some(rm) = challenge.and_then(|c| c.resource_metadata.as_deref()) {
        if !rm.is_empty() {
            if let Ok(url) = rm.parse::<reqwest::Url>() {
                return url.to_string();
            }
        }
    }

    // Derive the well-known path from the canonical resource origin.
    let Ok(base) = canonical_resource.parse::<reqwest::Url>() else {
        return format!("{canonical_resource}/.well-known/oauth-protected-resource");
    };
    let origin = format!(
        "{}://{}",
        base.scheme(),
        base.host_str().unwrap_or("")
    );
    let port_str = base.port().map(|p| format!(":{p}")).unwrap_or_default();
    format!("{origin}{port_str}/.well-known/oauth-protected-resource")
}

/// Select the scopes for the authorization request, following the C# priority
/// order: challenge scope → config scopes → PRM scopes → AS scopes.
/// Appends `offline_access` when the AS supports it.
pub(crate) fn select_scopes(
    challenge: Option<&WwwAuthenticateChallenge>,
    config: &McpAuthConfig,
    prm: Option<&ProtectedResourceMetadata>,
    as_meta: &AuthorizationServerMetadata,
) -> Vec<String> {
    let mut scopes: Vec<String> = if let Some(scope) = challenge.and_then(|c| c.scope.as_deref()) {
        if !scope.trim().is_empty() {
            return append_offline_access(
                scope.split_whitespace().map(str::to_owned).collect(),
                as_meta,
            );
        }
        Vec::new()
    } else {
        Vec::new()
    };

    if scopes.is_empty() {
        scopes = if !config.scopes.is_empty() {
            config.scopes.clone()
        } else if let Some(prm) = prm {
            if !prm.scopes_supported.is_empty() {
                prm.scopes_supported.clone()
            } else {
                as_meta.scopes_supported.clone()
            }
        } else {
            as_meta.scopes_supported.clone()
        };
    }

    append_offline_access(scopes, as_meta)
}

fn append_offline_access(
    mut scopes: Vec<String>,
    as_meta: &AuthorizationServerMetadata,
) -> Vec<String> {
    if as_meta.scopes_supported.iter().any(|s| s == "offline_access")
        && !scopes.iter().any(|s| s == "offline_access")
    {
        scopes.push("offline_access".to_owned());
    }
    scopes
}

/// The RFC 8707 `resource` parameter for authorize/token requests.
///
/// Entra v2.0 rejects `resource` (the resource is carried by the scopes), so it
/// is omitted for Entra v2.0 flows and sent unchanged everywhere else.
pub(crate) fn resource_param(entra_flow: bool, canonical_resource: &str) -> Option<(String, String)> {
    if entra_flow {
        None
    } else {
        Some(("resource".to_owned(), canonical_resource.to_owned()))
    }
}

/// Whether the authorization server is Entra v2.0, judged by either the
/// advertised issuer or the URL its metadata was discovered from (Entra
/// `common`/`organizations` advertise a templated `{tenantid}` issuer).
pub(crate) fn is_entra_flow(as_issuer: &str, discovery_issuer: &str) -> bool {
    entra::is_entra_v2_issuer(as_issuer) || entra::is_entra_v2_issuer(discovery_issuer)
}

/// Scopes for the authorization request.
///
/// Entra v2.0 flows never fall back to the AS `scopes_supported` (that would
/// yield a Microsoft Graph token); they derive a resource scope instead and
/// always request `offline_access`, without which Entra issues no refresh
/// token. Other servers use [`select_scopes`].
pub(crate) fn scopes_for_request(
    challenge: Option<&WwwAuthenticateChallenge>,
    config: &McpAuthConfig,
    prm: Option<&ProtectedResourceMetadata>,
    as_meta: &AuthorizationServerMetadata,
    canonical_resource: &str,
    entra_flow: bool,
) -> Vec<String> {
    if !entra_flow {
        return select_scopes(challenge, config, prm, as_meta);
    }
    entra::ensure_offline_access(entra::derive_scopes(
        &config.scopes,
        challenge.and_then(|c| c.scope.as_deref()),
        prm.map(|p| p.scopes_supported.as_slice()).unwrap_or_default(),
        canonical_resource,
    ))
}

/// A tenant must be a single path segment: a GUID, domain or alias.
pub(crate) fn is_valid_tenant(tenant: &str) -> bool {
    !tenant.is_empty()
        && tenant.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'.' || b == b'-')
        && tenant.bytes().any(|b| b != b'.')
}

/// Apply a configured tenant to an Entra authorization server advertised by
/// the resource; other issuers are returned unchanged.
pub(crate) fn issuer_candidate_for(advertised: &str, tenant: Option<&str>) -> String {
    match (tenant, entra::EntraAuthority::parse(advertised)) {
        (Some(t), Some(authority)) => authority.with_tenant(t).issuer_v2(),
        _ => advertised.to_owned(),
    }
}

/// RFC 9207 check of the `iss` callback parameter: when present it must equal
/// the issuer; when absent it is only an error if the AS advertised support.
pub(crate) fn validate_callback_iss(
    received: Option<&str>,
    issuer: &str,
    advertised: bool,
) -> Result<(), String> {
    match received {
        Some(iss) if iss != issuer => Err("OAuth issuer (iss) mismatch; aborting login.".into()),
        Some(_) => Ok(()),
        None if advertised => {
            Err("Authorization response missing required iss parameter; aborting login.".into())
        }
        None => Ok(()),
    }
}

/// Lock a std mutex, recovering the value if a holder panicked.
fn lock<T>(m: &std::sync::Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
}

/// Build an authorize URL with the given query parameters.
fn build_authorize_url(endpoint: &str, params: &[(String, String)]) -> String {
    let mut url = reqwest::Url::parse(endpoint).unwrap_or_else(|_| {
        reqwest::Url::parse("https://example.com").unwrap()
    });
    {
        let mut q = url.query_pairs_mut();
        for (k, v) in params {
            if !v.is_empty() {
                q.append_pair(k, v);
            }
        }
    }
    url.to_string()
}

/// Truncates to a byte budget without splitting a character.
///
/// A raw `&s[..max]` panics when `max` lands inside a multi-byte character —
/// reachable here with an ordinary non-Latin response body. See BUG 8.
fn truncate(s: &str, max: usize) -> &str {
    if s.len() <= max {
        return s;
    }
    let mut end = max;
    while end > 0 && !s.is_char_boundary(end) {
        end -= 1;
    }
    &s[..end]
}

fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// Open the system browser for the authorization URL.
///
/// Uses the platform's standard mechanism. If the opener fails, returns the
/// error as a string so the caller can surface it without crashing.
fn default_open_browser(url: &str) -> Result<(), String> {
    #[cfg(target_os = "windows")]
    {
        // `start` is a shell command, not an executable; must use cmd.exe.
        std::process::Command::new("cmd.exe")
            .args(["/c", "start", "", url])
            .spawn()
            .map_err(|e| e.to_string())?;
        Ok(())
    }
    #[cfg(target_os = "macos")]
    {
        std::process::Command::new("open")
            .arg(url)
            .spawn()
            .map_err(|e| e.to_string())?;
        Ok(())
    }
    #[cfg(not(any(target_os = "windows", target_os = "macos")))]
    {
        std::process::Command::new("xdg-open")
            .arg(url)
            .spawn()
            .map_err(|e| e.to_string())?;
        Ok(())
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::auth::types::{AuthorizationServerMetadata, McpAuthConfig, McpAuthMode};

    /// BUG 8 class: this renders a third-party authorization server's response
    /// body, so a non-Latin error message used to panic instead of being
    /// shortened.
    #[test]
    fn truncate_never_splits_a_character() {
        for ch in ['\u{00E9}', '\u{2500}', '\u{1F600}'] {
            let s = format!("{}{ch}tail", "a".repeat(199));
            let cut = truncate(&s, 200);
            assert!(s.starts_with(cut), "must be a prefix");
            assert!(cut.len() <= 200, "budget exceeded: {}", cut.len());
        }
        let jp = "認可サーバーのエラーです。".repeat(40);
        assert!(jp.starts_with(truncate(&jp, 200)));
        assert_eq!(truncate("short", 200), "short");
    }

    fn make_as_meta(scopes: Vec<&str>) -> AuthorizationServerMetadata {
        AuthorizationServerMetadata {
            issuer: "https://as.example.com".into(),
            authorization_endpoint: "https://as.example.com/auth".into(),
            token_endpoint: "https://as.example.com/token".into(),
            registration_endpoint: None,
            scopes_supported: scopes.into_iter().map(str::to_owned).collect(),
            issuer_parameter_supported: false,
        }
    }

    // ── select_scopes ────────────────────────────────────────────────────────

    #[test]
    fn select_scopes_uses_challenge_scope_first() {
        let challenge = WwwAuthenticateChallenge {
            resource_metadata: None,
            scope: Some("read write".into()),
            error: None,
        };
        let config = McpAuthConfig::oauth_default();
        let as_meta = make_as_meta(vec!["admin"]);
        let scopes = select_scopes(Some(&challenge), &config, None, &as_meta);
        assert!(scopes.contains(&"read".to_owned()));
        assert!(scopes.contains(&"write".to_owned()));
        assert!(!scopes.contains(&"admin".to_owned()));
    }

    #[test]
    fn select_scopes_falls_back_to_config_when_no_challenge() {
        let config = McpAuthConfig {
            mode: McpAuthMode::OAuth,
            client_id: None,
            scopes: vec!["config_scope".into()],
            bearer_token: None,
            ..Default::default()
        };
        let as_meta = make_as_meta(vec!["as_scope"]);
        let scopes = select_scopes(None, &config, None, &as_meta);
        assert_eq!(scopes, vec!["config_scope"]);
    }

    #[test]
    fn select_scopes_appends_offline_access_when_as_supports_it() {
        let config = McpAuthConfig::oauth_default();
        let as_meta = make_as_meta(vec!["read", "offline_access"]);
        let scopes = select_scopes(None, &config, None, &as_meta);
        assert!(scopes.contains(&"offline_access".to_owned()));
    }

    #[test]
    fn select_scopes_does_not_duplicate_offline_access() {
        let challenge = WwwAuthenticateChallenge {
            resource_metadata: None,
            scope: Some("read offline_access".into()),
            error: None,
        };
        let config = McpAuthConfig::oauth_default();
        let as_meta = make_as_meta(vec!["read", "offline_access"]);
        let scopes = select_scopes(Some(&challenge), &config, None, &as_meta);
        assert_eq!(scopes.iter().filter(|s| *s == "offline_access").count(), 1);
    }

    // ── resolve_resource_metadata_url ────────────────────────────────────────

    #[test]
    fn resolve_metadata_url_uses_challenge_resource_metadata() {
        let challenge = WwwAuthenticateChallenge {
            resource_metadata: Some("https://custom.example.com/.well-known/prm".into()),
            scope: None,
            error: None,
        };
        let url = resolve_resource_metadata_url(
            Some(&challenge),
            "https://mcp.example.com/",
        );
        assert_eq!(url, "https://custom.example.com/.well-known/prm");
    }

    #[test]
    fn resolve_metadata_url_derives_from_origin_when_no_challenge() {
        let url = resolve_resource_metadata_url(None, "https://mcp.example.com/some/path");
        assert!(
            url.contains("/.well-known/oauth-protected-resource"),
            "got: {url}"
        );
        assert!(url.starts_with("https://mcp.example.com"), "got: {url}");
    }

    // ── Entra v2.0 adaptations ───────────────────────────────────────────────

    const ENTRA_V2: &str = "https://login.microsoftonline.com/tid/v2.0";

    #[test]
    fn resource_param_is_omitted_for_entra_flows_only() {
        assert_eq!(resource_param(true, "https://mcp.example.com/mcp"), None);
        assert_eq!(
            resource_param(false, "https://mcp.example.com/mcp"),
            Some(("resource".to_owned(), "https://mcp.example.com/mcp".to_owned()))
        );
        assert!(is_entra_flow(ENTRA_V2, "https://as.example.com"));
        assert!(is_entra_flow(
            "https://login.microsoftonline.com/{tenantid}/v2.0",
            "http://127.0.0.1:1/common/v2.0"
        ));
        assert!(is_entra_flow("https://as.example.com", ENTRA_V2));
        // Entra v1 issuers are not v2.0 endpoints; keep the generic behaviour.
        assert!(!is_entra_flow("https://sts.windows.net/tid/", "https://sts.windows.net/tid/"));
        assert!(!is_entra_flow("https://as.example.com", "https://as.example.com"));
    }

    #[test]
    fn entra_scopes_never_fall_back_to_as_scopes_supported() {
        let config = McpAuthConfig::oauth_default();
        let as_meta = make_as_meta(vec!["openid", "profile", "User.Read", "offline_access"]);
        let resource = "https://mcp.example.com/mcp";
        assert_eq!(
            scopes_for_request(None, &config, None, &as_meta, resource, true),
            vec!["https://mcp.example.com/.default", "offline_access"]
        );
        let prm = ProtectedResourceMetadata {
            resource: None,
            authorization_servers: Vec::new(),
            scopes_supported: vec!["api://x/.default".into()],
        };
        assert_eq!(
            scopes_for_request(None, &config, Some(&prm), &as_meta, resource, true),
            vec!["api://x/.default", "offline_access"]
        );
        let challenge = WwwAuthenticateChallenge {
            resource_metadata: None,
            scope: Some("api://c/read".into()),
            error: None,
        };
        assert_eq!(
            scopes_for_request(Some(&challenge), &config, Some(&prm), &as_meta, resource, true),
            vec!["api://c/read", "offline_access"]
        );
        // Non-Entra servers keep the generic selection, AS fallback included.
        assert_eq!(
            scopes_for_request(None, &config, None, &as_meta, resource, false),
            select_scopes(None, &config, None, &as_meta)
        );
        assert_eq!(
            scopes_for_request(None, &config, None, &as_meta, resource, false),
            vec!["openid", "profile", "User.Read", "offline_access"]
        );
    }

    #[test]
    fn tenant_must_be_a_single_safe_segment() {
        for ok in ["contoso.com", "organizations", "72f988bf-86f1-41af-91ab-2d7cd011db47"] {
            assert!(is_valid_tenant(ok), "{ok:?} should be valid");
        }
        for bad in ["", "a/b", "..", ".", "con toso", "x?y", "t%2F", "tenant#"] {
            assert!(!is_valid_tenant(bad), "{bad:?} should be invalid");
        }
    }

    #[tokio::test]
    async fn invalid_tenant_is_a_reported_failure() {
        let p = provider(
            McpAuthConfig { tenant: Some("contoso/evil".into()), ..Default::default() },
            true,
            "http://127.0.0.1:9/mcp",
        );
        assert!(!p.handle_unauthorized(None).await);
        let err = p.status_snapshot().last_error.expect("error recorded");
        assert!(err.contains("Invalid auth.tenant"), "got {err}");
    }

    #[tokio::test]
    async fn persist_token_clears_last_error() {
        let p = provider(McpAuthConfig::default(), false, "https://mcp.example.com/mcp");
        p.record_error("earlier failure");
        p.persist_token(Secret::new("at".into()), None, None, origin("https://as"), "s")
            .await
            .unwrap();
        assert_eq!(p.status_snapshot().last_error, None);
    }

    const TEMPLATED_ISSUER: &str = "https://login.microsoftonline.com/{tenantid}/v2.0";

    /// An Entra-`common`-shaped AS: metadata lives under `/common/v2.0` but
    /// advertises the templated `{tenantid}` issuer.
    async fn entra_common_server() -> (u16, crate::auth::test_http::Requests) {
        crate::auth::test_http::spawn_recording_server(Arc::new(|path: &str, port: u16| {
            let origin = format!("http://127.0.0.1:{port}");
            match path {
                "/.well-known/oauth-protected-resource" => (
                    200,
                    serde_json::json!({
                        "resource": format!("{origin}/mcp"),
                        "authorization_servers": [format!("{origin}/common/v2.0")],
                    })
                    .to_string(),
                ),
                "/common/v2.0/.well-known/openid-configuration" => (
                    200,
                    serde_json::json!({
                        "issuer": TEMPLATED_ISSUER,
                        "authorization_endpoint": format!("{origin}/common/oauth2/v2.0/authorize"),
                        "token_endpoint": format!("{origin}/common/oauth2/v2.0/token"),
                        "scopes_supported": ["openid", "profile", "email", "offline_access"],
                    })
                    .to_string(),
                ),
                "/common/oauth2/v2.0/token" => (
                    200,
                    serde_json::json!({
                        "access_token": "refreshed",
                        "refresh_token": "rotated",
                        "expires_in": 3600,
                    })
                    .to_string(),
                ),
                _ => (404, "{}".to_owned()),
            }
        }))
        .await
    }

    fn token_bodies(requests: &crate::auth::test_http::Requests) -> Vec<String> {
        requests
            .lock()
            .unwrap()
            .iter()
            .filter(|(path, _)| path.ends_with("/token"))
            .map(|(_, body)| body.clone())
            .collect()
    }

    #[tokio::test]
    async fn refresh_rediscovers_from_the_discovery_url_not_the_templated_issuer() {
        let (port, requests) = entra_common_server().await;
        let discovery = format!("http://127.0.0.1:{port}/common/v2.0");
        let p = provider(McpAuthConfig::default(), false, &format!("http://127.0.0.1:{port}/mcp"));
        p.record_error("earlier failure");
        let expired = McpStoredToken {
            access_token: Secret::new("old".into()),
            refresh_token: Some(Secret::new("r1".into())),
            expires_at_unix: 1,
            scope: "s offline_access".into(),
            issuer: TEMPLATED_ISSUER.into(),
            client_id: "cid".into(),
            discovery_issuer: Some(discovery.clone()),
        };
        p.store.set(&p.token_key(), &serde_json::to_string(&expired).unwrap()).await.unwrap();

        let token = p.get_access_token().await.expect("refresh succeeds");
        assert_eq!(token.expose(), "refreshed");

        let bodies = token_bodies(&requests);
        assert_eq!(bodies.len(), 1, "token endpoint reached once: {bodies:?}");
        assert!(bodies[0].contains("grant_type=refresh_token"), "got {}", bodies[0]);
        assert!(!bodies[0].contains("resource="), "Entra refresh must omit resource: {}", bodies[0]);

        let stored: McpStoredToken =
            serde_json::from_str(&p.store.get(&p.token_key()).await.unwrap().unwrap()).unwrap();
        assert_eq!(stored.issuer, TEMPLATED_ISSUER);
        assert_eq!(stored.discovery_url(), discovery);
        assert_eq!(stored.refresh_token.unwrap().expose(), "rotated");
        assert_eq!(p.status_snapshot().last_error, None);
    }

    #[tokio::test]
    async fn refresh_of_a_legacy_token_uses_its_issuer_and_keeps_resource() {
        let server = crate::auth::test_http::spawn_recording_server(Arc::new(
            |path: &str, port: u16| {
                let origin = format!("http://127.0.0.1:{port}");
                match path {
                    "/.well-known/oauth-authorization-server" => (
                        200,
                        serde_json::json!({
                            "issuer": origin,
                            "authorization_endpoint": format!("{origin}/authorize"),
                            "token_endpoint": format!("{origin}/token"),
                        })
                        .to_string(),
                    ),
                    "/token" => (200, r#"{"access_token":"new","expires_in":60}"#.to_owned()),
                    _ => (404, "{}".to_owned()),
                }
            },
        ))
        .await;
        let (port, requests) = server;
        let p = provider(McpAuthConfig::default(), false, &format!("http://127.0.0.1:{port}/mcp"));
        let legacy = serde_json::json!({
            "access_token": "old",
            "refresh_token": "r1",
            "expires_at_unix": 1,
            "scope": "s",
            "issuer": format!("http://127.0.0.1:{port}"),
            "client_id": "cid",
        });
        p.store.set(&p.token_key(), &legacy.to_string()).await.unwrap();

        assert_eq!(p.get_access_token().await.expect("refresh").expose(), "new");
        let bodies = token_bodies(&requests);
        assert_eq!(bodies.len(), 1);
        assert!(bodies[0].contains("resource="), "got {}", bodies[0]);
    }

    #[tokio::test]
    async fn entra_authorize_url_omits_resource_and_requests_offline_access() {
        let (port, _) = entra_common_server().await;
        let captured: Arc<std::sync::Mutex<Option<String>>> = Arc::default();
        let sink = Arc::clone(&captured);
        let p = McpOAuthProvider::new(
            reqwest::Client::new(),
            &reqwest::Url::parse(&format!("http://127.0.0.1:{port}/mcp")).unwrap(),
            Arc::new(coda_auth::InMemoryStore::new()),
            McpAuthConfig { client_id: Some("cid".into()), ..Default::default() },
            true,
            Some(Arc::new(move |url: &str| {
                *sink.lock().unwrap() = Some(url.to_owned());
                Err("captured".to_owned())
            })),
            None,
        );
        assert!(!p.handle_unauthorized(None).await);

        let url = captured.lock().unwrap().clone().expect("authorize URL opened");
        let url = reqwest::Url::parse(&url).unwrap();
        assert_eq!(url.path(), "/common/oauth2/v2.0/authorize");
        let params: std::collections::HashMap<String, String> =
            url.query_pairs().into_owned().collect();
        assert!(!params.contains_key("resource"), "got {url}");
        assert_eq!(
            params.get("scope").map(String::as_str),
            Some(format!("http://127.0.0.1:{port}/.default offline_access").as_str())
        );
        assert_eq!(params.get("client_id").map(String::as_str), Some("cid"));
    }

    #[test]
    fn validate_callback_iss_keeps_rfc9207_semantics() {
        let issuer = "https://as.example.com";
        assert!(validate_callback_iss(Some(issuer), issuer, true).is_ok());
        assert!(validate_callback_iss(Some(issuer), issuer, false).is_ok());
        assert!(validate_callback_iss(Some("https://evil.example.com"), issuer, false).is_err());
        assert!(validate_callback_iss(Some("https://evil.example.com"), issuer, true).is_err());
        assert!(validate_callback_iss(None, issuer, false).is_ok());
        let err = validate_callback_iss(None, issuer, true).unwrap_err();
        assert!(err.contains("missing"), "got {err}");
    }

    #[test]
    fn issuer_candidate_for_applies_tenant_override_to_entra_authorities() {
        assert_eq!(
            issuer_candidate_for("https://login.microsoftonline.com/common/v2.0", Some("contoso.com")),
            "https://login.microsoftonline.com/contoso.com/v2.0"
        );
        assert_eq!(
            issuer_candidate_for("https://login.microsoftonline.com/common/v2.0", None),
            "https://login.microsoftonline.com/common/v2.0"
        );
        assert_eq!(
            issuer_candidate_for("https://as.example.com", Some("contoso.com")),
            "https://as.example.com"
        );
    }

    fn origin(issuer: &str) -> TokenOrigin<'_> {
        TokenOrigin { issuer, discovery_issuer: issuer, client_id: "cid" }
    }

    fn provider(config: McpAuthConfig, interactive: bool, url: &str) -> McpOAuthProvider {
        McpOAuthProvider::new(
            reqwest::Client::new(),
            &reqwest::Url::parse(url).unwrap(),
            Arc::new(coda_auth::InMemoryStore::new()),
            config,
            interactive,
            Some(Arc::new(|_: &str| Err("no browser in tests".to_owned()))),
            None,
        )
    }

    #[tokio::test]
    async fn status_snapshot_records_failure_text() {
        let p = provider(
            McpAuthConfig { mode: McpAuthMode::Bearer, ..Default::default() },
            true,
            "https://mcp.example.com/mcp",
        );
        assert_eq!(p.status_snapshot().last_error, None);
        let r = p.force_reauthorize().await;
        assert!(!r.succeeded);
        let status = p.status_snapshot();
        assert_eq!(status.mode, McpAuthMode::Bearer);
        assert_eq!(status.account, None);
        assert_eq!(status.last_error, r.error);
        assert!(status.last_error.unwrap().contains("requires OAuth"));
    }

    #[tokio::test]
    async fn force_reauthorize_accepts_entra_broker_mode() {
        let p = provider(
            McpAuthConfig { mode: McpAuthMode::EntraBroker, ..Default::default() },
            false,
            "https://mcp.example.com/mcp",
        );
        let r = p.force_reauthorize().await;
        assert!(r.error.unwrap().contains("interactive session"));
    }

    #[tokio::test]
    async fn status_snapshot_tracks_token_expiry() {
        let p = provider(McpAuthConfig::default(), false, "https://mcp.example.com/mcp");
        assert_eq!(p.status_snapshot().expires_at_unix, None);
        p.persist_token(Secret::new("at".into()), None, Some(3600), origin("https://as"), "s")
            .await
            .unwrap();
        let exp = p.status_snapshot().expires_at_unix.expect("expiry recorded");
        assert!(exp > unix_now());
        p.persist_token(Secret::new("at".into()), None, None, origin("https://as"), "s")
            .await
            .unwrap();
        assert_eq!(p.status_snapshot().expires_at_unix, None);

        let fresh = provider(McpAuthConfig::default(), false, "https://mcp.example.com/mcp");
        let fresh = McpOAuthProvider { store: Arc::clone(&p.store), ..fresh };
        p.persist_token(Secret::new("at".into()), None, Some(60), origin("https://as"), "s")
            .await
            .unwrap();
        assert!(fresh.get_access_token().await.is_some());
        assert!(fresh.status_snapshot().expires_at_unix.is_some());
    }

    #[tokio::test]
    async fn invalid_redirect_uri_template_is_a_reported_failure() {
        let server = crate::auth::test_http::spawn_server(Arc::new(|path: &str, port: u16| {
            let origin = format!("http://127.0.0.1:{port}");
            match path {
                "/.well-known/oauth-protected-resource" => (
                    200,
                    serde_json::json!({
                        "resource": format!("{origin}/mcp"),
                        "authorization_servers": [origin],
                    })
                    .to_string(),
                ),
                "/.well-known/oauth-authorization-server" => (
                    200,
                    serde_json::json!({
                        "issuer": origin,
                        "authorization_endpoint": format!("{origin}/authorize"),
                        "token_endpoint": format!("{origin}/token"),
                    })
                    .to_string(),
                ),
                _ => (404, "{}".to_owned()),
            }
        }))
        .await;
        let p = provider(
            McpAuthConfig {
                redirect_uri: Some("https://localhost/cb".into()),
                client_id: Some("cid".into()),
                ..Default::default()
            },
            true,
            &format!("http://127.0.0.1:{server}/mcp"),
        );
        assert!(!p.handle_unauthorized(None).await);
        let err = p.status_snapshot().last_error.expect("error recorded");
        assert!(err.contains("Invalid auth.redirectUri"), "got {err}");
    }

    #[tokio::test]
    async fn logout_deletes_the_stored_token_and_login_reauthorizes() {
        let p = provider(McpAuthConfig::default(), false, "https://mcp.example.com/mcp");
        p.persist_token(Secret::new("at".into()), None, Some(3600), origin("https://as"), "s")
            .await
            .unwrap();
        assert!(p.status().unwrap().expires_at_unix.is_some());

        assert!(p.logout().await.succeeded);
        assert!(p.get_access_token().await.is_none());
        let status = p.status().unwrap();
        assert_eq!(status.expires_at_unix, None);
        assert_eq!(status.last_error, None);

        // `login` is `force_reauthorize`, which needs an interactive session.
        let r = p.login().await;
        assert!(r.error.unwrap().contains("interactive session"));
    }
}
