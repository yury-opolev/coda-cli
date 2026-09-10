//! Safe, closed classification of authentication failures.
//!
//! [`AuthError`] is the *internal* error type: its `Display` deliberately
//! carries diagnostic detail — raw OAuth response bodies, transport URLs,
//! store keys, provider ids taken from configuration. None of that may reach a
//! log line, a CLI message, or a TUI pane.
//!
//! [`AuthFailure`] is the UI-facing type. It is a closed enum that retains no
//! reference to the originating error and stores nothing but two bounded,
//! non-textual details: an HTTP status code and an [`std::io::ErrorKind`].
//! Because the classifier matches every [`AuthError`] variant explicitly (no
//! wildcard arm), a new variant is a compile error here rather than a silent
//! fall-through to a generic — or worse, a leaky — message.
//!
//! This is the *only* classifier: hosts render provider-specific context
//! (“GitHub Copilot: …”) around the message, they do not re-derive it.

use std::fmt;

use crate::error::AuthError;

/// A UI-facing authentication failure.
///
/// `Debug` is derived and is safe: no variant holds server- or user-supplied
/// text.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AuthFailure {
    /// No credential is stored for the provider.
    NoCredential,
    /// The provider is not registered with the credential manager.
    UnknownProvider,
    /// The stored credential cannot be refreshed (no refresh token, or the
    /// provider refuses).
    CannotRefresh,
    /// The authorization server rejected the request. Only the HTTP status is
    /// kept; the response body is discarded.
    OAuthRejected { status: u16 },
    /// A network-level failure while talking to the authorization server.
    Network,
    /// The credential store failed for an unclassified reason.
    Store,
    /// Credential storage exists in a format this build cannot read.
    StoreIncompatible,
    /// A credential exists but could not be decrypted.
    StoreUndecryptable,
    /// The configured credential backend name is not recognised.
    StoreBackendUnknown,
    /// A storage backend is not available on this host.
    StoreUnavailable { backend: StoreBackend },
    /// More than one provider has a stored credential.
    AmbiguousCredentials,
    /// A credential was presented under the wrong provider.
    ProviderMismatch,
    /// A stored or received credential blob could not be parsed.
    CredentialParse,
    /// Filesystem failure; only the error kind is kept.
    Io { kind: std::io::ErrorKind },
    /// The OAuth `state` parameter did not match (possible CSRF).
    StateMismatch,
    /// The login was cancelled, denied, or timed out.
    Cancelled,
    /// An endpoint URL is invalid or insecure.
    InvalidEndpoint,
    /// User-supplied login input was rejected before a credential was built.
    InvalidInput,
}

/// The storage backends that can report themselves unavailable.
///
/// A closed enum rather than the backend string from [`AuthError`], so no
/// arbitrary text can ride out through the safe message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StoreBackend {
    Keyring,
    Dpapi,
    File,
    /// A backend this build does not name explicitly.
    Other,
}

impl StoreBackend {
    fn classify(raw: &str) -> Self {
        let lowered = raw.trim().to_ascii_lowercase();
        match lowered.as_str() {
            "keyring" => Self::Keyring,
            "dpapi" => Self::Dpapi,
            "file" | "encrypted-file" | "encrypted_file" => Self::File,
            _ => Self::Other,
        }
    }

    fn label(self) -> &'static str {
        match self {
            Self::Keyring => "keyring",
            Self::Dpapi => "dpapi",
            Self::File => "file",
            Self::Other => "configured",
        }
    }
}

impl AuthFailure {
    /// Classify an [`AuthError`] into a safe failure.
    ///
    /// Every variant is matched explicitly on purpose: adding an `AuthError`
    /// variant must break this build rather than silently produce an
    /// "unknown" success-shaped result.
    pub fn classify(error: &AuthError) -> Self {
        match error {
            AuthError::NotFound(_) => Self::NoCredential,
            AuthError::UnknownProvider(_) => Self::UnknownProvider,
            AuthError::CannotRefresh(_, _) => Self::CannotRefresh,
            // `body` is raw server response text — never surfaced.
            AuthError::OAuth { status, .. } => Self::OAuthRejected { status: *status },
            // Transport messages routinely embed the request URL (which can
            // carry a token in its query).
            AuthError::Transport(_) => Self::Network,
            AuthError::Store(_) => Self::Store,
            AuthError::StoreIncompatible { .. } => Self::StoreIncompatible,
            AuthError::StoreUndecryptable { .. } => Self::StoreUndecryptable,
            AuthError::StoreUnavailable { backend, .. } => {
                Self::StoreUnavailable { backend: StoreBackend::classify(backend) }
            }
            AuthError::StoreBackendUnknown { .. } => Self::StoreBackendUnknown,
            AuthError::CredentialProviderMismatch { .. } => Self::ProviderMismatch,
            AuthError::AmbiguousCredentials { .. } => Self::AmbiguousCredentials,
            // serde errors quote the offending input.
            AuthError::Serialization(_) => Self::CredentialParse,
            AuthError::Io(e) => Self::Io { kind: e.kind() },
            AuthError::StateMismatch => Self::StateMismatch,
            AuthError::LoginCancelled(_) => Self::Cancelled,
            AuthError::InvalidUrl(_) => Self::InvalidEndpoint,
            AuthError::InvalidInput { .. } => Self::InvalidInput,
        }
    }

    /// `true` when retrying the same operation could plausibly succeed.
    pub fn is_transient(self) -> bool {
        matches!(self, Self::Network | Self::Io { .. })
    }

    /// `true` when the user aborted, denied, or let the login expire.
    pub fn is_cancelled(self) -> bool {
        matches!(self, Self::Cancelled)
    }

    /// `true` when the remedy is to sign in again.
    pub fn needs_login(self) -> bool {
        matches!(self, Self::NoCredential | Self::CannotRefresh | Self::AmbiguousCredentials)
    }
}

impl fmt::Display for AuthFailure {
    /// Renders the single-line message shown to the user.
    ///
    /// Wording is stable: hosts prefix it with provider context and tests
    /// assert on it.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NoCredential => f.write_str("no credential found"),
            Self::UnknownProvider => f.write_str("authentication provider not registered"),
            Self::CannotRefresh => f.write_str("credential cannot be refreshed"),
            Self::OAuthRejected { status } => {
                write!(f, "token refresh failed (HTTP {status})")
            }
            Self::Network => f.write_str("network error during authentication"),
            Self::Store => f.write_str("credential store error"),
            Self::StoreIncompatible => f.write_str(
                "the saved credentials are in a format this build cannot read; \
                 they were left untouched",
            ),
            Self::StoreUndecryptable => f.write_str(
                "the saved credential exists but could not be decrypted \
                 (it may belong to another user or machine)",
            ),
            Self::StoreBackendUnknown => {
                f.write_str("the configured credential backend is not recognised")
            }
            Self::StoreUnavailable { backend } => write!(
                f,
                "the {} credential store is not available on this host",
                backend.label()
            ),
            Self::AmbiguousCredentials => f.write_str(
                "more than one provider has a stored credential; sign out and sign in again",
            ),
            Self::ProviderMismatch => f.write_str("the credential does not belong to this provider"),
            Self::CredentialParse => f.write_str("credential parse error"),
            Self::Io { kind } => write!(f, "credential I/O error ({kind:?})"),
            Self::StateMismatch => f.write_str("OAuth state mismatch (possible CSRF)"),
            Self::Cancelled => f.write_str("login cancelled or timed out"),
            Self::InvalidEndpoint => f.write_str("the configured endpoint URL is not valid"),
            Self::InvalidInput => f.write_str("the value entered is not valid"),
        }
    }
}

/// Convenience for callers that only want the safe text.
pub fn safe_message(error: &AuthError) -> String {
    AuthFailure::classify(error).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_variant_message_is_a_single_line_without_the_raw_detail() {
        let sentinel = "RAW_SENTINEL_VALUE";
        let errors = [
            AuthError::NotFound(sentinel.into()),
            AuthError::UnknownProvider(sentinel.into()),
            AuthError::CannotRefresh(sentinel.into(), sentinel.into()),
            AuthError::OAuth { status: 500, body: sentinel.into() },
            AuthError::Transport(sentinel.into()),
            AuthError::Store(sentinel.into()),
            AuthError::StoreIncompatible { path: sentinel.into(), detail: sentinel.into() },
            AuthError::StoreUndecryptable { key: sentinel.into(), detail: sentinel.into() },
            AuthError::StoreUnavailable { backend: sentinel.into(), detail: sentinel.into() },
            AuthError::StoreBackendUnknown { value: sentinel.into() },
            AuthError::CredentialProviderMismatch {
                expected: sentinel.into(),
                actual: sentinel.into(),
            },
            AuthError::AmbiguousCredentials { providers: sentinel.into() },
            AuthError::Io(std::io::Error::other(sentinel)),
            AuthError::StateMismatch,
            AuthError::LoginCancelled(sentinel.into()),
            AuthError::InvalidUrl(sentinel.into()),
            AuthError::InvalidInput { field: sentinel.into(), detail: sentinel.into() },
        ];
        for error in errors {
            let message = safe_message(&error);
            assert!(!message.contains(sentinel), "leaked: {message}");
            assert!(!message.contains('\n'), "message must be one line: {message}");
        }
    }

    #[test]
    fn an_unnamed_backend_does_not_echo_its_name() {
        let failure = AuthFailure::classify(&AuthError::StoreUnavailable {
            backend: "SecretService@host".into(),
            detail: "x".into(),
        });
        assert_eq!(failure, AuthFailure::StoreUnavailable { backend: StoreBackend::Other });
        assert!(!failure.to_string().contains("SecretService"));
    }

    #[test]
    fn known_backends_keep_their_label() {
        for (raw, expected) in [
            ("keyring", StoreBackend::Keyring),
            ("DPAPI", StoreBackend::Dpapi),
            ("file", StoreBackend::File),
        ] {
            let failure = AuthFailure::classify(&AuthError::StoreUnavailable {
                backend: raw.into(),
                detail: "x".into(),
            });
            assert_eq!(failure, AuthFailure::StoreUnavailable { backend: expected });
        }
    }

    #[test]
    fn classification_flags_match_the_variants() {
        assert!(AuthFailure::Network.is_transient());
        assert!(AuthFailure::Cancelled.is_cancelled());
        assert!(AuthFailure::NoCredential.needs_login());
        assert!(!AuthFailure::StateMismatch.is_transient());
    }
}
