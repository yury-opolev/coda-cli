//! Error types for the auth crate.

/// All errors that the auth crate can produce.
#[derive(Debug, thiserror::Error)]
pub enum AuthError {
    /// No credential is stored for the given provider.
    #[error("no credential for provider '{0}'; log in first")]
    NotFound(String),

    /// Provider not registered with the manager.
    #[error("provider '{0}' is not registered")]
    UnknownProvider(String),

    /// The stored credential cannot be used and has no refresh token.
    #[error("credential for '{0}' cannot be refreshed: {1}")]
    CannotRefresh(String, String),

    /// OAuth / token-exchange server returned an error.
    #[error("OAuth error (HTTP {status}): {body}")]
    OAuth { status: u16, body: String },

    /// Network-level transport failure.
    #[error("transport error: {0}")]
    Transport(String),

    /// Credential store failure (keyring or encrypted file).
    #[error("credential store error: {0}")]
    Store(String),

    /// JSON serialization or deserialization failed.
    #[error("serialization error: {0}")]
    Serialization(#[from] serde_json::Error),

    /// I/O error (file-backed store only).
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),

    /// OAuth state parameter did not match — possible CSRF.
    #[error("OAuth state mismatch (possible CSRF); aborting login")]
    StateMismatch,

    /// Login was cancelled or timed out.
    #[error("login cancelled: {0}")]
    LoginCancelled(String),

    /// The PKCE token-exchange URL is invalid or insecure.
    #[error("invalid token-exchange URL: {0}")]
    InvalidUrl(String),
}

impl AuthError {
    pub(crate) fn store(msg: impl Into<String>) -> Self {
        AuthError::Store(msg.into())
    }
}
