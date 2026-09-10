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
/// the client. `Ok(None)` explicitly opts out of overriding static config.
/// An authoritative stored-credential source must return an error when its
/// credential is removed or cannot be read, so an old static token is not reused.
#[async_trait::async_trait]
pub trait CredentialSource: std::fmt::Debug + Send + Sync {
    /// Fetch the auth headers to inject, if credentials are available.
    ///
    /// Each returned tuple is a `(header-name, header-value)` pair added to the
    /// outgoing HTTP request.  Returning the same header name multiple times
    /// replaces the previous value, case-insensitively. `anthropic-beta`
    /// feature lists are merged so required request flags survive auth overrides.
    /// An error stops the request before HTTP send.
    async fn auth_headers(&self) -> Result<Option<Vec<(String, String)>>, crate::LlmError>;

    /// Whether the most recent [`Self::auth_headers`] failure happened *here*
    /// — this machine could not produce a credential — rather than at the
    /// provider.
    ///
    /// # Why this exists
    ///
    /// A source that cannot read its store, or that has been superseded, must
    /// still fail the request closed, and the only error channel it has is
    /// [`crate::LlmError`]. `Unauthorized` is the honest "this request cannot
    /// be authenticated", but a caller diagnosing *the credential* cannot tell
    /// it apart from a provider that answered `401` — and reporting "the
    /// provider rejected your credential; sign in again" when no request was
    /// ever sent sends a user to overwrite a credential that was merely
    /// unreadable.
    ///
    /// # This answer is only meaningful on a source you own
    ///
    /// It describes the *last* attempt this source made, so it is only safe to
    /// read on a source that is not shared with anything else making requests
    /// concurrently. A caller that wants to diagnose a credential — the CLI's
    /// post-login check, a host's "test this connection" — must build a
    /// **dedicated** source for that probe and read the flag from it. Reading
    /// it from the long-lived source an engine is streaming through can
    /// observe another request's outcome.
    ///
    /// The default is `false`: a source that does not track provenance is
    /// assumed to be reporting the provider's answer, which is the safe
    /// reading for a static source that never fails locally at all.
    fn last_failure_was_local(&self) -> bool {
        false
    }
}
