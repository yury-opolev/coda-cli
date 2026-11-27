//! `McpAuthProvider` trait and its built-in implementations.
//!
//! Mirrors `IMcpAuthProvider.cs` and `StaticBearerAuthProvider.cs`.

use async_trait::async_trait;
use coda_auth::secret::Secret;

use super::types::{McpAuthMode, McpAuthResult, McpAuthStatus};

/// Supplies and refreshes the bearer token an `McpHttpClient` attaches to its
/// requests.
///
/// - `get_access_token` is called before each request and returns the current
///   token (refreshing if needed), or `None` if no token is available.
/// - `handle_unauthorized` is called on a 401 response; it should run the
///   auth flow and return `true` when a fresh token is now available and the
///   request should be retried.
#[async_trait]
pub trait McpAuthProvider: Send + Sync {
    /// Return a currently valid access token, or `None`.
    async fn get_access_token(&self) -> Option<Secret<String>>;

    /// React to a 401. Returns `true` when a token is now available and the
    /// caller should retry the request.
    ///
    /// `www_authenticate` is the value of the `WWW-Authenticate` response
    /// header (if present).
    async fn handle_unauthorized(&self, www_authenticate: Option<&str>) -> bool;

    /// Secret-free auth state for status surfaces; `None` when not tracked.
    fn status(&self) -> Option<McpAuthStatus> {
        None
    }

    /// User-initiated sign-in (`/mcp login <name>`).
    async fn login(&self) -> McpAuthResult {
        unsupported("Sign-in")
    }

    /// User-initiated sign-out (`/mcp logout <name>`).
    async fn logout(&self) -> McpAuthResult {
        unsupported("Sign-out")
    }
}

fn unsupported(action: &str) -> McpAuthResult {
    McpAuthResult {
        succeeded: false,
        error: Some(format!("{action} is not supported for this server's auth mode.")),
    }
}

// ── StaticBearerAuthProvider ──────────────────────────────────────────────────

/// An `McpAuthProvider` that attaches a fixed bearer token (`auth.mode = "bearer"`).
///
/// It cannot recover from a 401 — `handle_unauthorized` always returns `false`.
pub struct StaticBearerAuthProvider {
    token: Secret<String>,
}

impl StaticBearerAuthProvider {
    /// Panics when `token` is empty (mirrors `ArgumentException.ThrowIfNullOrEmpty`).
    pub fn new(token: impl Into<String>) -> Self {
        let token = token.into();
        assert!(!token.is_empty(), "bearer token must not be empty");
        Self { token: Secret::new(token) }
    }
}

#[async_trait]
impl McpAuthProvider for StaticBearerAuthProvider {
    async fn get_access_token(&self) -> Option<Secret<String>> {
        Some(self.token.clone())
    }

    async fn handle_unauthorized(&self, _www_authenticate: Option<&str>) -> bool {
        // A static bearer token cannot be refreshed; the caller must reconfigure.
        false
    }

    fn status(&self) -> Option<McpAuthStatus> {
        Some(McpAuthStatus {
            mode: McpAuthMode::Bearer,
            account: None,
            expires_at_unix: None,
            last_error: None,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn static_bearer_returns_token() {
        let p = StaticBearerAuthProvider::new("my_token");
        let t = p.get_access_token().await.unwrap();
        assert_eq!(t.expose(), "my_token");
    }

    #[tokio::test]
    async fn static_bearer_cannot_recover_from_401() {
        let p = StaticBearerAuthProvider::new("tok");
        assert!(!p.handle_unauthorized(Some("Bearer error=\"invalid_token\"")).await);
    }

    #[tokio::test]
    async fn static_bearer_reports_bearer_status_and_no_login() {
        let p = StaticBearerAuthProvider::new("tok");
        assert_eq!(p.status().unwrap().mode, McpAuthMode::Bearer);
        let r = p.login().await;
        assert!(!r.succeeded);
        assert!(r.error.unwrap().contains("not supported"));
        assert!(!p.logout().await.succeeded);
    }

    /// The token must never appear in `Debug` output.
    #[test]
    fn static_bearer_token_redacted_in_debug() {
        let p = StaticBearerAuthProvider::new("super_secret_bearer_value_xyz");
        let debug = format!("{:?}", p.token);
        assert!(!debug.contains("super_secret_bearer_value_xyz"), "token must be redacted");
        assert!(debug.contains("[REDACTED]"));
    }
}
