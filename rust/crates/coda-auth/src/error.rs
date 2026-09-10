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

    /// The credential storage found on disk is in a format this build cannot
    /// use (a foreign key file, or credential files written by another
    /// backend).  Never treated as "no credential": acting on that assumption
    /// would overwrite recoverable data.
    #[error("credential storage at {path} is not usable by this build: {detail}")]
    StoreIncompatible { path: String, detail: String },

    /// A credential exists but could not be decrypted — a rotated key, a copy
    /// from another user or machine, or a corrupt file.  Distinct from
    /// [`AuthError::NotFound`] so the caller can say "unreadable" instead of
    /// silently prompting for a fresh login and overwriting it.
    #[error("the stored credential for '{key}' could not be decrypted: {detail}")]
    StoreUndecryptable { key: String, detail: String },

    /// An optional storage backend is not usable on this host at all — no
    /// Secret Service on a headless Linux box, an OS feature that is absent.
    /// Distinct from a backend that exists but refuses access, which may be
    /// holding a credential and must never be treated as "nothing there".
    #[error("the {backend} credential store is not available on this host: {detail}")]
    StoreUnavailable { backend: String, detail: String },

    /// An explicit credential-backend override named a backend that does not
    /// exist.  Falling back to a default here would silently use storage the
    /// caller did not ask for.
    #[error("unknown credential backend '{value}'; expected one of: auto, dpapi, file")]
    StoreBackendUnknown { value: String },

    /// A credential was handed to the store under a provider it does not
    /// belong to; writing it would file an account under the wrong key.
    #[error("a credential for '{actual}' cannot be stored as provider '{expected}'")]
    CredentialProviderMismatch { expected: String, actual: String },

    /// More than one provider has a stored credential. Exactly one is the
    /// invariant, so picking one of them would be an arbitrary choice with an
    /// account attached to it.
    #[error("more than one provider has a stored credential ({providers}); sign out and sign in again")]
    AmbiguousCredentials { providers: String },

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

    /// Login input supplied by the user was rejected before any credential was
    /// built (a blank API key, for example).
    ///
    /// `field` names what was rejected and `detail` explains why; neither ever
    /// carries the value itself, because the value is the secret.
    #[error("the value supplied for {field} is not valid: {detail}")]
    InvalidInput { field: String, detail: String },
}

impl AuthError {
    pub(crate) fn store(msg: impl Into<String>) -> Self {
        AuthError::Store(msg.into())
    }

    pub(crate) fn incompatible(path: &std::path::Path, detail: impl Into<String>) -> Self {
        AuthError::StoreIncompatible {
            path: path.display().to_string(),
            detail: detail.into(),
        }
    }

    pub(crate) fn undecryptable(key: &str, detail: impl Into<String>) -> Self {
        AuthError::StoreUndecryptable {
            key: sanitize_key(key),
            detail: detail.into(),
        }
    }
}

/// Renders a store key (or provider id) safely for an error message.
///
/// Keys reach us from configuration and from remote responses, so a key is
/// untrusted text: it must not be able to smuggle control characters into a
/// log line, and an over-long value must not push the real message out of
/// view.  This is display-only; storage always uses the original key.
pub(crate) fn sanitize_key(key: &str) -> String {
    const MAX: usize = 64;
    let mut out: String = key
        .chars()
        .take(MAX)
        .map(|c| if c.is_control() { '\u{fffd}' } else { c })
        .collect();
    if key.chars().count() > MAX {
        out.push('…');
    }
    out
}
