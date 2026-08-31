//! MCP OAuth 2.1 + PKCE authorization flow.
//!
//! Mirrors `McpOAuthProvider.cs` (and `DefaultMcpOAuthReauthenticator.cs`).
//!
//! On a 401 challenge, this provider:
//! 1. Fetches RFC 9728 protected-resource metadata (from the challenge or the
//!    well-known origin path).
//! 2. Fetches RFC 8414 / OIDC authorization-server metadata.
//! 3. Resolves a client id (configured → cached DCR → fresh DCR).
//! 4. Builds the authorize URL with PKCE + RFC 8707 `resource` parameter.
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
        AuthorizationServerMetadata, McpAuthConfig, McpAuthMode, McpAuthResult,
        McpClientIdResolution, McpClientRegistration, McpStoredToken,
        ProtectedResourceMetadata, WwwAuthenticateChallenge, canonical_resource_uri,
    },
};

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
        }
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
        McpAuthResult { succeeded: false, error: Some(msg.to_owned()) }
    }

    /// Proactively force a fresh authorization without waiting for a 401.
    /// Cached DCR registrations are retained.
    pub async fn force_reauthorize(&self) -> McpAuthResult {
        if self.config.mode != McpAuthMode::OAuth {
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

        match self.acquire_token(None, true).await {
            Ok(r) => r,
            Err(AcquireError::Cancelled) => {
                McpAuthResult { succeeded: false, error: Some("MCP OAuth reauthentication was canceled.".into()) }
            }
            Err(AcquireError::Other(msg)) => McpAuthResult { succeeded: false, error: Some(msg) },
        }
    }

    /// Load a valid access token from the store, refreshing if needed.
    async fn load_token(&self) -> Option<McpStoredToken> {
        let json = self.store.get(&self.token_key()).await.ok()??;
        serde_json::from_str(&json).ok()
    }

    /// Attempt to refresh `stored` using its refresh token.
    ///
    /// On any failure the stored token is deleted so the next request can
    /// trigger a fresh 401 → full flow.
    async fn refresh_token(&self, stored: &McpStoredToken) -> Option<McpStoredToken> {
        let refresh_token = stored.refresh_token.as_ref()?.expose().clone();

        let as_meta = self
            .metadata
            .get_authorization_server_metadata(&stored.issuer)
            .await?;

        let form = vec![
            ("grant_type".to_owned(), "refresh_token".to_owned()),
            ("refresh_token".to_owned(), refresh_token),
            ("client_id".to_owned(), stored.client_id.clone()),
            ("resource".to_owned(), self.canonical_resource.clone()),
        ];

        let token_resp = match self.token_request(&as_meta.token_endpoint, &form).await {
            Ok(t) => t,
            Err(_) => {
                // Refresh rejected → delete so next request re-authorizes.
                let _ = self.store.delete(&self.token_key()).await;
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
            &stored.issuer,
            &stored.client_id,
            token_resp.scope.as_deref().unwrap_or(&stored.scope),
        )
        .await
        .ok()
    }

    async fn persist_token(
        &self,
        access_token: Secret<String>,
        refresh_token: Option<Secret<String>>,
        expires_in: Option<i64>,
        issuer: &str,
        client_id: &str,
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
            issuer: issuer.to_owned(),
            client_id: client_id.to_owned(),
        };

        let json = serde_json::to_string(&stored).map_err(|e| {
            coda_auth::AuthError::Serialization(e)
        })?;
        self.store.set(&self.token_key(), &json).await?;
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
        let listener = LoopbackListener::bind()
            .await
            .map_err(|e| AcquireError::Other(format!("loopback bind failed: {e}")))?;
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
        let scopes = select_scopes(challenge, &self.config, prm, &as_meta);

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
            ("resource".to_owned(), self.canonical_resource.clone()),
        ];
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
        if let Some(iss) = &redirect.iss {
            if iss != &as_meta.issuer {
                return Err(AcquireError::Other("OAuth issuer (iss) mismatch; aborting login.".into()));
            }
        } else if as_meta.issuer_parameter_supported {
            return Err(AcquireError::Other(
                "Authorization response missing required iss parameter; aborting login.".into(),
            ));
        }
        let code = match &redirect.code {
            Some(c) => c.clone(),
            None => {
                return Err(AcquireError::Other(
                    "Authorization response did not include a code.".into(),
                ));
            }
        };

        // 8. Exchange the code.
        let form = vec![
            ("grant_type".to_owned(), "authorization_code".to_owned()),
            ("code".to_owned(), code),
            ("redirect_uri".to_owned(), redirect_uri),
            ("client_id".to_owned(), client_id.clone()),
            ("code_verifier".to_owned(), verifier),
            ("resource".to_owned(), self.canonical_resource.clone()),
        ];
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
            &as_meta.issuer,
            &client_id,
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
            self.log(&format!(
                "MCP server requires authorization. Run `coda` interactively to sign in to {}.",
                self.display_resource()
            ));
            return false;
        }

        match self.acquire_token(challenge.as_ref(), false).await {
            Ok(r) => r.succeeded,
            Err(AcquireError::Cancelled) => {
                self.log(&format!(
                    "Authorization canceled for {}.",
                    self.display_resource()
                ));
                false
            }
            Err(AcquireError::Other(msg)) => {
                self.log(&format!("Authorization failed: {msg}"));
                false
            }
        }
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
        if config.mode != McpAuthMode::OAuth {
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

fn truncate(s: &str, max: usize) -> &str {
    if s.len() <= max { s } else { &s[..max] }
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
}
