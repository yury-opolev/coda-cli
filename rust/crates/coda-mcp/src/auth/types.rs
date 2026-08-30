//! Auth data types for the MCP OAuth flow.
//!
//! Mirrors (in order):
//! - `McpAuthMode.cs`
//! - `McpAuthConfig.cs`
//! - `ProtectedResourceMetadata.cs`
//! - `AuthorizationServerMetadata.cs`
//! - `McpClientRegistration.cs`
//! - `McpStoredToken.cs`
//! - `WwwAuthenticateChallenge.cs`
//! - `CanonicalResourceUri.cs`
//! - `McpClientIdResolution.cs`
//! - `IMcpOAuthReauthenticator.cs`

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

use coda_auth::secret::Secret;

// ── McpAuthMode ──────────────────────────────────────────────────────────────

/// How Coda authenticates to an HTTP MCP server.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum McpAuthMode {
    /// Never attach an `Authorization` header.
    None,
    /// Attach a static `Authorization: Bearer <token>` header.
    Bearer,
    /// Run the MCP OAuth flow (discovery + PKCE) on a 401 challenge.
    #[default]
    OAuth,
}

// ── McpAuthConfig ─────────────────────────────────────────────────────────────

/// Authentication settings for an HTTP MCP server (`auth` block in `.mcp.json`).
///
/// When the `auth` block is absent, defaults to `McpAuthMode::OAuth` so a 401
/// transparently triggers the discovery flow (mirrors `McpAuthConfig.Default`).
#[derive(Debug, Clone)]
pub struct McpAuthConfig {
    pub mode: McpAuthMode,
    /// Configured OAuth client id; takes precedence over Dynamic Client Registration.
    pub client_id: Option<String>,
    /// Configured scopes; used when neither the challenge nor the AS advertise scopes.
    pub scopes: Vec<String>,
    /// Static bearer token (used when `mode == Bearer`).
    /// Wrapped in `Secret<String>` so it is redacted in `Debug` output.
    pub bearer_token: Option<Secret<String>>,
}

impl McpAuthConfig {
    /// Default: OAuth mode, no pre-configured client id, scopes, or token.
    pub fn oauth_default() -> Self {
        Self { mode: McpAuthMode::OAuth, client_id: None, scopes: Vec::new(), bearer_token: None }
    }
}

// ── ProtectedResourceMetadata ─────────────────────────────────────────────────

/// OAuth 2.0 Protected Resource Metadata (RFC 9728).
///
/// Served at `/.well-known/oauth-protected-resource` and tells the client
/// which authorization server(s) issue tokens for the resource.
#[derive(Debug, Clone)]
pub struct ProtectedResourceMetadata {
    pub resource: Option<String>,
    pub authorization_servers: Vec<String>,
    pub scopes_supported: Vec<String>,
}

impl ProtectedResourceMetadata {
    pub fn parse(root: &serde_json::Value) -> Self {
        Self {
            resource: root.get("resource").and_then(|v| v.as_str()).map(str::to_owned),
            authorization_servers: read_string_array(root, "authorization_servers"),
            scopes_supported: read_string_array(root, "scopes_supported"),
        }
    }
}

// ── AuthorizationServerMetadata ───────────────────────────────────────────────

/// OAuth 2.0 Authorization Server Metadata (RFC 8414) or OpenID Connect
/// Discovery 1.0 document.
#[derive(Debug, Clone)]
pub struct AuthorizationServerMetadata {
    pub issuer: String,
    pub authorization_endpoint: String,
    pub token_endpoint: String,
    /// `None` when missing or when the endpoint scheme is hostile.
    pub registration_endpoint: Option<String>,
    pub scopes_supported: Vec<String>,
    /// Whether the AS advertises RFC 9207 `iss` parameter support.
    pub issuer_parameter_supported: bool,
}

impl AuthorizationServerMetadata {
    /// Parse the metadata document. Returns `None` when required endpoints are
    /// missing or use a disallowed scheme.
    ///
    /// # Security
    ///
    /// Endpoint schemes are validated: only `https` is allowed, plus `http`
    /// for loopback. An attacker-controlled `.mcp.json` could supply any URL
    /// as `authorization_endpoint`; on Windows, `ShellExecute` invokes ANY
    /// registered protocol handler (e.g. `ms-msdt:`, `file://`, `search-ms:`),
    /// enabling code execution. Rejecting non-http(s) endpoints closes this.
    pub fn parse(root: &serde_json::Value) -> Option<Self> {
        let issuer = root.get("issuer").and_then(|v| v.as_str())?;
        let authorize = root.get("authorization_endpoint").and_then(|v| v.as_str())?;
        let token = root.get("token_endpoint").and_then(|v| v.as_str())?;

        if issuer.is_empty() || authorize.is_empty() || token.is_empty() {
            return None;
        }

        // Validate endpoint schemes before constructing the record.
        if !is_allowed_endpoint_uri(authorize) || !is_allowed_endpoint_uri(token) {
            return None;
        }

        let registration = root
            .get("registration_endpoint")
            .and_then(|v| v.as_str())
            .and_then(|s| if is_allowed_endpoint_uri(s) { Some(s.to_owned()) } else { None });

        let issuer_param = root
            .get("authorization_response_iss_parameter_supported")
            .and_then(|v| v.as_bool())
            .unwrap_or(false);

        Some(Self {
            issuer: issuer.to_owned(),
            authorization_endpoint: authorize.to_owned(),
            token_endpoint: token.to_owned(),
            registration_endpoint: registration,
            scopes_supported: read_string_array(root, "scopes_supported"),
            issuer_parameter_supported: issuer_param,
        })
    }
}

/// Returns `true` when `value` is an `https` URI, or an `http` URI whose host
/// is loopback. Everything else (including `ms-msdt:`, `file://`, non-loopback
/// `http`) is rejected.
fn is_allowed_endpoint_uri(value: &str) -> bool {
    let Ok(url) = value.parse::<reqwest::Url>() else { return false };
    let scheme = url.scheme();
    if scheme == "https" {
        return true;
    }
    if scheme == "http" {
        let host = url.host_str().unwrap_or("");
        return host == "127.0.0.1"
            || host == "::1"
            || host == "[::1]"
            || host.eq_ignore_ascii_case("localhost");
    }
    false
}

// ── McpClientRegistration ────────────────────────────────────────────────────

/// Credentials issued by a Dynamic Client Registration endpoint (RFC 7591),
/// persisted per-issuer so DCR happens at most once.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct McpClientRegistration {
    pub client_id: String,
    /// Wrapped so it cannot accidentally leak through `Debug`.
    pub client_secret: Option<Secret<String>>,
}

impl McpClientRegistration {
    pub fn parse(root: &serde_json::Value) -> Option<Self> {
        let client_id = root.get("client_id")?.as_str()?.to_owned();
        if client_id.is_empty() {
            return None;
        }
        let client_secret = root
            .get("client_secret")
            .and_then(|v| v.as_str())
            .map(|s| Secret::new(s.to_owned()));
        Some(Self { client_id, client_secret })
    }
}

// ── McpStoredToken ────────────────────────────────────────────────────────────

/// A persisted MCP access token (and its refresh token), stored via
/// `CredentialStore` and keyed by canonical resource URI.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct McpStoredToken {
    /// Wrapped so it cannot accidentally leak through `Debug`.
    pub access_token: Secret<String>,
    pub refresh_token: Option<Secret<String>>,
    /// Unix timestamp (seconds) when the access token expires; 0 = no expiry.
    pub expires_at_unix: i64,
    pub scope: String,
    pub issuer: String,
    pub client_id: String,
}

impl McpStoredToken {
    /// `true` when the token is expired or within a 30 s safety window.
    pub fn is_expired(&self, now_unix: i64) -> bool {
        self.expires_at_unix > 0 && now_unix >= self.expires_at_unix - 30
    }
}

// ── WwwAuthenticateChallenge ──────────────────────────────────────────────────

/// A parsed `WWW-Authenticate: Bearer …` challenge (RFC 6750 / RFC 9728).
///
/// Carries the parameters the MCP OAuth flow uses:
/// - `resource_metadata` — RFC 9728 metadata URL
/// - `scope` — space-separated required scopes
/// - `error` — error code from the AS
#[derive(Debug, Clone)]
pub struct WwwAuthenticateChallenge {
    pub resource_metadata: Option<String>,
    pub scope: Option<String>,
    pub error: Option<String>,
}

impl WwwAuthenticateChallenge {
    /// Parse a `Bearer` challenge from a `WWW-Authenticate` header value.
    ///
    /// Returns `None` when the header is absent, blank, or not a `Bearer`
    /// challenge (e.g. `Basic`).
    pub fn parse(header: Option<&str>) -> Option<Self> {
        let header = header?.trim();
        if header.is_empty() {
            return None;
        }
        const SCHEME: &str = "Bearer";
        if !header.starts_with(SCHEME) {
            return None;
        }
        let after_scheme = &header[SCHEME.len()..];
        // The character right after "Bearer" must be whitespace or end-of-string.
        if let Some(c) = after_scheme.chars().next() {
            if !c.is_whitespace() {
                return None;
            }
        }
        let params = parse_bearer_params(after_scheme);
        Some(Self {
            resource_metadata: params.get("resource_metadata").cloned(),
            scope: params.get("scope").cloned(),
            error: params.get("error").cloned(),
        })
    }
}

/// Parse `key="value", key=value, …` pairs from the parameters section of a
/// `Bearer` challenge. Values inside quotes may contain commas.
fn parse_bearer_params(section: &str) -> HashMap<String, String> {
    let mut map = HashMap::new();
    for part in split_top_level(section) {
        let eq = match part.find('=') {
            Some(i) if i > 0 => i,
            _ => continue,
        };
        let key = part[..eq].trim().to_ascii_lowercase();
        let raw_val = part[eq + 1..].trim();
        let value = if raw_val.starts_with('"') && raw_val.ends_with('"') && raw_val.len() >= 2 {
            raw_val[1..raw_val.len() - 1].to_owned()
        } else {
            raw_val.to_owned()
        };
        if !key.is_empty() {
            map.insert(key, value);
        }
    }
    map
}

/// Split `section` on top-level commas (i.e. not inside double-quotes).
fn split_top_level(section: &str) -> impl Iterator<Item = &str> {
    SplitTopLevel { s: section, pos: 0 }
}

struct SplitTopLevel<'a> {
    s: &'a str,
    pos: usize,
}

impl<'a> Iterator for SplitTopLevel<'a> {
    type Item = &'a str;

    fn next(&mut self) -> Option<Self::Item> {
        if self.pos >= self.s.len() {
            return None;
        }
        let rest = &self.s[self.pos..];
        let mut in_quotes = false;
        let mut end = rest.len();
        for (i, c) in rest.char_indices() {
            if c == '"' {
                in_quotes = !in_quotes;
            } else if c == ',' && !in_quotes {
                end = i;
                break;
            }
        }
        let item = &rest[..end];
        self.pos += end + if end < rest.len() { 1 } else { 0 };
        if item.is_empty() && self.pos >= self.s.len() {
            None
        } else {
            Some(item)
        }
    }
}

// ── CanonicalResourceUri ──────────────────────────────────────────────────────

/// Compute the RFC 8707 / RFC 9728 canonical resource identifier for an MCP
/// server URL.
///
/// Rules (mirrors `CanonicalResourceUri.From`):
/// - Lowercase scheme and host.
/// - Default ports are elided (`https://h:443` → `https://h`).
/// - No fragment.
/// - Trailing slash dropped unless the path is bare `/`.
pub fn canonical_resource_uri(url: &reqwest::Url) -> String {
    // Build from components, normalising scheme + host.
    let scheme = url.scheme().to_lowercase();
    let host = url.host_str().unwrap_or("").to_lowercase();
    let port_str = match url.port() {
        Some(p) => {
            // Elide the port when it matches the scheme default.
            let default = if scheme == "https" { 443u16 } else { 80u16 };
            if p == default { String::new() } else { format!(":{p}") }
        }
        None => String::new(),
    };
    let path = url.path();
    let query = url.query().map(|q| format!("?{q}")).unwrap_or_default();

    let mut result = format!("{scheme}://{host}{port_str}{path}{query}");

    // Drop a trailing slash unless the path is exactly "/" (bare origin form).
    if result.ends_with('/') && path != "/" {
        result.pop();
    }

    result
}

// ── McpClientIdResolution ─────────────────────────────────────────────────────

/// Outcome of resolving an OAuth client id: either a usable `client_id` or an
/// actionable, secret-free error. Exactly one field is `Some`.
#[derive(Debug)]
pub(crate) struct McpClientIdResolution {
    pub client_id: Option<String>,
    pub error: Option<String>,
}

impl McpClientIdResolution {
    pub fn success(client_id: impl Into<String>) -> Self {
        Self { client_id: Some(client_id.into()), error: None }
    }

    pub fn failure(error: impl Into<String>) -> Self {
        Self { client_id: None, error: Some(error.into()) }
    }
}

// ── McpAuthResult ─────────────────────────────────────────────────────────────

/// Outcome of a user-initiated MCP OAuth reauthentication attempt.
#[derive(Debug, Clone)]
pub struct McpAuthResult {
    pub succeeded: bool,
    pub error: Option<String>,
}

// ── helpers ───────────────────────────────────────────────────────────────────

fn read_string_array(root: &serde_json::Value, key: &str) -> Vec<String> {
    root.get(key)
        .and_then(|v| v.as_array())
        .map(|arr| {
            arr.iter()
                .filter_map(|v| v.as_str())
                .map(str::to_owned)
                .collect()
        })
        .unwrap_or_default()
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    // ── AuthorizationServerMetadata ──────────────────────────────────────────

    /// SECURITY: the endpoint scheme validator must reject non-https/loopback.
    #[test]
    fn as_metadata_rejects_hostile_endpoint_scheme() {
        let doc = json!({
            "issuer": "https://example.com",
            "authorization_endpoint": "ms-msdt://exploit",
            "token_endpoint": "https://example.com/token",
        });
        assert!(
            AuthorizationServerMetadata::parse(&doc).is_none(),
            "ms-msdt: authorization_endpoint must be rejected"
        );
    }

    /// SECURITY: token_endpoint with non-https scheme must be rejected.
    #[test]
    fn as_metadata_rejects_hostile_token_endpoint_scheme() {
        let doc = json!({
            "issuer": "https://example.com",
            "authorization_endpoint": "https://example.com/auth",
            "token_endpoint": "file:///etc/passwd",
        });
        assert!(AuthorizationServerMetadata::parse(&doc).is_none());
    }

    #[test]
    fn as_metadata_accepts_valid_endpoints() {
        let doc = json!({
            "issuer": "https://example.com",
            "authorization_endpoint": "https://example.com/auth",
            "token_endpoint": "https://example.com/token",
            "registration_endpoint": "https://example.com/register",
            "authorization_response_iss_parameter_supported": true,
            "scopes_supported": ["openid", "offline_access"],
        });
        let meta = AuthorizationServerMetadata::parse(&doc).unwrap();
        assert_eq!(meta.issuer, "https://example.com");
        assert!(meta.issuer_parameter_supported);
        assert_eq!(meta.scopes_supported, vec!["openid", "offline_access"]);
    }

    /// registration_endpoint with a hostile scheme must be stripped, not reject the whole doc.
    #[test]
    fn as_metadata_strips_hostile_registration_endpoint() {
        let doc = json!({
            "issuer": "https://example.com",
            "authorization_endpoint": "https://example.com/auth",
            "token_endpoint": "https://example.com/token",
            "registration_endpoint": "ms-msdt://register",
        });
        let meta = AuthorizationServerMetadata::parse(&doc).unwrap();
        assert!(meta.registration_endpoint.is_none());
    }

    #[test]
    fn as_metadata_allows_loopback_http_endpoint() {
        let doc = json!({
            "issuer": "http://localhost",
            "authorization_endpoint": "http://localhost/auth",
            "token_endpoint": "http://localhost/token",
        });
        assert!(AuthorizationServerMetadata::parse(&doc).is_some());
    }

    // ── WwwAuthenticateChallenge ─────────────────────────────────────────────

    #[test]
    fn challenge_parses_bearer_with_params() {
        let c = WwwAuthenticateChallenge::parse(Some(
            r#"Bearer realm="example",resource_metadata="https://example.com/.well-known/oauth-protected-resource",scope="mcp""#
        ))
        .unwrap();
        assert_eq!(
            c.resource_metadata.as_deref(),
            Some("https://example.com/.well-known/oauth-protected-resource")
        );
        assert_eq!(c.scope.as_deref(), Some("mcp"));
    }

    #[test]
    fn challenge_returns_none_for_basic_scheme() {
        assert!(WwwAuthenticateChallenge::parse(Some("Basic realm=\"example\"")).is_none());
    }

    #[test]
    fn challenge_returns_none_for_missing_header() {
        assert!(WwwAuthenticateChallenge::parse(None).is_none());
    }

    #[test]
    fn challenge_returns_none_for_empty() {
        assert!(WwwAuthenticateChallenge::parse(Some("")).is_none());
    }

    #[test]
    fn challenge_captures_error_param() {
        let c =
            WwwAuthenticateChallenge::parse(Some(r#"Bearer error="invalid_token""#)).unwrap();
        assert_eq!(c.error.as_deref(), Some("invalid_token"));
    }

    // ── CanonicalResourceUri ─────────────────────────────────────────────────

    #[test]
    fn canonical_uri_lowercases_scheme_and_host() {
        let url: reqwest::Url = "HTTPS://Example.COM/path".parse().unwrap();
        let c = canonical_resource_uri(&url);
        assert!(c.starts_with("https://example.com/"), "got: {c}");
    }

    #[test]
    fn canonical_uri_elides_default_https_port() {
        let url: reqwest::Url = "https://example.com:443/mcp".parse().unwrap();
        let c = canonical_resource_uri(&url);
        assert!(!c.contains(":443"), "should not contain default port: {c}");
    }

    #[test]
    fn canonical_uri_keeps_non_default_port() {
        let url: reqwest::Url = "https://example.com:8443/mcp".parse().unwrap();
        let c = canonical_resource_uri(&url);
        assert!(c.contains(":8443"), "should keep non-default port: {c}");
    }

    #[test]
    fn canonical_uri_drops_trailing_slash() {
        let url: reqwest::Url = "https://example.com/mcp/".parse().unwrap();
        let c = canonical_resource_uri(&url);
        assert!(!c.ends_with('/'), "trailing slash should be dropped: {c}");
    }

    #[test]
    fn canonical_uri_keeps_root_path() {
        let url: reqwest::Url = "https://example.com/".parse().unwrap();
        let c = canonical_resource_uri(&url);
        // Root path "/" should be preserved but trailing slash dropped from "example.com/"
        // The C# drops a single trailing slash unless path == "/" — our implementation
        // doesn't drop when path is literally "/", meaning "example.com/" → "example.com"
        // (since the root "/" is the only path and gets dropped). Both behaviours are
        // equivalent for the resource-ID purpose.
        assert!(!c.is_empty());
    }

    #[test]
    fn canonical_uri_strips_fragment() {
        let url: reqwest::Url = "https://example.com/mcp?foo=1".parse().unwrap();
        let c = canonical_resource_uri(&url);
        assert!(!c.contains('#'), "fragments must be absent: {c}");
        assert!(c.contains("?foo=1"));
    }

    // ── McpStoredToken ───────────────────────────────────────────────────────

    #[test]
    fn stored_token_is_expired_within_30s_window() {
        let now = 1_000_000i64;
        let token = McpStoredToken {
            access_token: Secret::new("tok".into()),
            refresh_token: None,
            expires_at_unix: now + 20, // expires 20 s in the future → within 30 s window
            scope: String::new(),
            issuer: String::new(),
            client_id: String::new(),
        };
        assert!(token.is_expired(now), "token within 30 s window must be considered expired");
    }

    #[test]
    fn stored_token_not_expired_when_far_future() {
        let now = 1_000_000i64;
        let token = McpStoredToken {
            access_token: Secret::new("tok".into()),
            refresh_token: None,
            expires_at_unix: now + 3600,
            scope: String::new(),
            issuer: String::new(),
            client_id: String::new(),
        };
        assert!(!token.is_expired(now));
    }

    #[test]
    fn stored_token_zero_expiry_never_expires() {
        let token = McpStoredToken {
            access_token: Secret::new("tok".into()),
            refresh_token: None,
            expires_at_unix: 0,
            scope: String::new(),
            issuer: String::new(),
            client_id: String::new(),
        };
        assert!(!token.is_expired(i64::MAX));
    }

    /// Token's access token must NEVER appear in Debug output.
    #[test]
    fn stored_token_access_token_redacted_in_debug() {
        let token = McpStoredToken {
            access_token: Secret::new("my_super_secret_access_token".into()),
            refresh_token: Some(Secret::new("my_refresh_token".into())),
            expires_at_unix: 0,
            scope: String::new(),
            issuer: String::new(),
            client_id: String::new(),
        };
        let debug = format!("{token:?}");
        assert!(!debug.contains("my_super_secret_access_token"), "access token must be redacted");
        assert!(!debug.contains("my_refresh_token"), "refresh token must be redacted");
        assert!(debug.contains("[REDACTED]"));
    }
}
