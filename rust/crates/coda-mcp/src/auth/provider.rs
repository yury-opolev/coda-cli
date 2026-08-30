//! `McpAuthProvider` trait and its built-in implementations.
//!
//! Mirrors `IMcpAuthProvider.cs` and `StaticBearerAuthProvider.cs`.

use async_trait::async_trait;
use coda_auth::secret::Secret;

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

    /// The token must never appear in `Debug` output.
    #[test]
    fn static_bearer_token_redacted_in_debug() {
        let p = StaticBearerAuthProvider::new("super_secret_bearer_value_xyz");
        let debug = format!("{:?}", p.token);
        assert!(!debug.contains("super_secret_bearer_value_xyz"), "token must be redacted");
        assert!(debug.contains("[REDACTED]"));
    }
}
