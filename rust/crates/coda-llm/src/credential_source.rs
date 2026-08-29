//! The `CredentialSource` seam: lets provider clients fetch auth headers
//! dynamically instead of baking a token into their config at construction
//! time.
//!
//! # Dependency direction
//!
//! This trait lives in `coda-llm` (a leaf crate) so that `coda-auth` can
//! implement it without creating a circular dependency.  `coda-auth` may
//! depend on `coda-llm`; the reverse is forbidden.
//!
//! # Usage
//!
//! ```rust,ignore
//! let source = Arc::new(CredentialManagerSource::new(manager, "anthropic-api-key"));
//! let config = AnthropicConfig::api_key("")          // static key left empty
//!     .with_credential_source(source);               // dynamic auth wins
//! ```

/// Produces HTTP authentication headers for a single provider request.
///
/// Called immediately before every HTTP send so a refreshed credential (e.g. an
/// expiring OAuth access token) is picked up automatically without recreating
/// the client.  `None` means no credential is currently available and the client
/// falls back to its static config (api-key or bearer token hard-coded in the
/// config struct).
#[async_trait::async_trait]
pub trait CredentialSource: std::fmt::Debug + Send + Sync {
    /// Fetch the auth headers to inject, if credentials are available.
    ///
    /// Each returned tuple is a `(header-name, header-value)` pair added to the
    /// outgoing HTTP request.  Returning the same header name multiple times
    /// replaces the previous value; returning `None` is a "no-op" signal.
    async fn auth_headers(&self) -> Option<Vec<(String, String)>>;
}
