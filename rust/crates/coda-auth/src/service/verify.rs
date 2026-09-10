//! Proving a connection actually works.
//!
//! # What counts as proof
//!
//! Only an **uncached, live** provider call. [`LlmClient::refresh_models`] is
//! that call: the Copilot client invalidates its process cache and really
//! issues the request, and the Anthropic client has no cache to begin with.
//! `list_models` is not proof — it may answer from a warm cache or a catalogue
//! on disk — and neither is an empty list, which is what a provider that
//! answered but granted nothing looks like.
//!
//! No completion is ever run: verification must not cost the user money, and
//! must not put a message into a session or its history.
//!
//! # What a failure means
//!
//! A model endpoint that refuses is not necessarily a bad credential. A Claude
//! subscription's `/v1/models` behaves differently from a console key's, and
//! Copilot's entitlement can be missing on an account that is signed in
//! perfectly well. So:
//!
//! * a `401` — and an `Unauthorized` a client reports without a status — is
//!   [`VerificationOutcome::Rejected`]: the provider refused the identity;
//! * a `403` is deliberately **not** a rejection. It is an entitlement answer,
//!   and it is reported as [`UnverifiedReason::Forbidden`] — *we could not
//!   check* — precisely because concluding "your credential is invalid" from
//!   it would delete a working one. That distinction is a decision this code
//!   makes, not an observation about how a given provider behaves;
//! * anything else — transport, `404`, a server error — is
//!   [`VerificationOutcome::Unverified`] too, which says *we could not check*,
//!   not *you are signed out*.
//!
//! An OAuth login that already exchanged a token is signed in even when this
//! probe cannot confirm it; the host reports "signed in, unverified" and does
//! not undo the login.
//!
//! # Where the probe is sent
//!
//! For the Anthropic console key, the host that answers is resolved by
//! [`crate::service::endpoint`] — `ANTHROPIC_BASE_URL` and its precedence — so
//! this check proves the key against the same host the engine will later spend
//! it at. Verifying at one host and spending at another proves nothing.

use coda_llm::{FailureKind, LlmClient, LlmError};

/// The result of a probe.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum VerificationOutcome {
    /// The provider answered with at least one model.
    Verified { models: usize },
    /// The provider refused the credential.
    Rejected { status: Option<u16> },
    /// The check could not be completed, or proved nothing.
    Unverified { reason: UnverifiedReason },
}

/// Why a probe proved nothing. A closed set: no server text is retained.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UnverifiedReason {
    /// The provider could not be reached.
    Unreachable,
    /// No request was ever sent, because this machine could not produce a
    /// credential: the store would not open, the credential would not decrypt,
    /// it is not there, or the connection it belonged to was replaced.
    ///
    /// Deliberately **not** a rejection. Nothing was asked of the provider, so
    /// nothing was refused, and telling a user to sign in again is how a
    /// recoverable credential gets overwritten.
    CredentialUnavailable,
    /// The provider answered `403` for the model list.
    ///
    /// Deliberately *not* a rejection. A Claude.ai subscription answers the
    /// console `/v1/models` endpoint differently from an API key, and a
    /// Copilot account can lack model-listing entitlement while being signed
    /// in perfectly well. Concluding "your token is invalid" from this is a
    /// guess, and acting on it would delete a working credential.
    Forbidden,
    /// The provider answered with an error other than an authentication one.
    ProviderError { status: Option<u16> },
    /// The provider answered, but listed no models — which proves nothing
    /// about the credential.
    NoModelsListed,
}

impl std::fmt::Display for UnverifiedReason {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Unreachable => f.write_str("the provider could not be reached"),
            Self::CredentialUnavailable => f.write_str(
                "the stored credential could not be read on this machine, so nothing was sent to \
                 the provider and nothing was refused",
            ),
            Self::Forbidden => {
                f.write_str("this account is not allowed to list models (HTTP 403)")
            }
            Self::ProviderError { status: Some(status) } => {
                write!(f, "the provider returned HTTP {status} for the model list")
            }
            Self::ProviderError { status: None } => {
                f.write_str("the provider returned an unexpected response for the model list")
            }
            Self::NoModelsListed => f.write_str("the provider listed no models"),
        }
    }
}

/// A probe result together with the identity that was probed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VerificationReport {
    /// The provider id the client itself reports — the public identity, not
    /// the one we hoped for.
    pub provider_id: Option<String>,
    pub outcome: VerificationOutcome,
}

impl VerificationReport {
    /// Whether the connection was proven to work.
    pub fn is_verified(&self) -> bool {
        matches!(self.outcome, VerificationOutcome::Verified { .. })
    }

    /// Whether the provider actively refused the credential.
    pub fn is_rejected(&self) -> bool {
        matches!(self.outcome, VerificationOutcome::Rejected { .. })
    }
}

impl std::fmt::Display for VerificationReport {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let provider = self.provider_id.as_deref().unwrap_or("the provider");
        match &self.outcome {
            VerificationOutcome::Verified { models } => {
                write!(f, "connected to {provider}; {models} models available")
            }
            VerificationOutcome::Rejected { status } => match status {
                Some(status) => write!(f, "{provider} rejected the credential (HTTP {status}); sign in again"),
                None => write!(f, "{provider} rejected the credential; sign in again"),
            },
            VerificationOutcome::Unverified { reason } => write!(
                f,
                "signed in to {provider}, but the connection could not be verified: {reason}",
            ),
        }
    }
}

/// Probe `client` with an uncached model listing.
pub async fn verify_client(client: &dyn LlmClient) -> VerificationReport {
    verify_client_with_source(client, None).await
}

/// [`verify_client`], told which credential source the client authenticates
/// with.
///
/// The source is what makes "the provider refused you" distinguishable from
/// "this machine could not produce a credential". Both stop the request, and
/// both arrive as an authentication error, but only the first is a reason to
/// sign in again — the second is a reason *not* to, because whatever is in the
/// store may still be recoverable.
pub async fn verify_client_with_source(
    client: &dyn LlmClient,
    source: Option<&dyn coda_llm::CredentialSource>,
) -> VerificationReport {
    let provider_id = Some(client.provider_id().to_owned());
    // refresh_models, never list_models: a warm cache or an on-disk catalogue
    // would answer without touching the provider at all.
    let outcome = match client.refresh_models().await {
        Ok(models) if models.is_empty() => VerificationOutcome::Unverified {
            reason: UnverifiedReason::NoModelsListed,
        },
        Ok(models) => VerificationOutcome::Verified { models: models.len() },
        // Asked first: a source that failed here never let a request out, so
        // whatever the error looks like, the provider did not produce it.
        Err(_) if source.is_some_and(|source| source.last_failure_was_local()) => {
            VerificationOutcome::Unverified { reason: UnverifiedReason::CredentialUnavailable }
        }
        Err(error) => classify(&error),
    };
    VerificationReport { provider_id, outcome }
}

fn classify(error: &LlmError) -> VerificationOutcome {
    match error {
        LlmError::Unauthorized(_) => VerificationOutcome::Rejected { status: None },
        // 401 is the provider refusing the identity.
        LlmError::Api { status: 401, .. } => VerificationOutcome::Rejected { status: Some(401) },
        // 403 is an entitlement answer, not an identity one — see
        // `UnverifiedReason::Forbidden`.
        LlmError::Api { status: 403, .. } => {
            VerificationOutcome::Unverified { reason: UnverifiedReason::Forbidden }
        }
        LlmError::Api { status, .. } => VerificationOutcome::Unverified {
            reason: UnverifiedReason::ProviderError { status: Some(*status) },
        },
        LlmError::Transport(_) | LlmError::IncompleteStream | LlmError::Cancelled => {
            VerificationOutcome::Unverified { reason: UnverifiedReason::Unreachable }
        }
        LlmError::Protocol(_) => VerificationOutcome::Unverified {
            reason: UnverifiedReason::ProviderError { status: None },
        },
    }
}

/// Whether a probe failure should be retried later rather than acted on.
pub fn is_transient(error: &LlmError) -> bool {
    error.kind() == FailureKind::Transient
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_forbidden_model_list_is_not_a_claim_that_the_token_is_invalid_by_itself() {
        // 403 on a *subscription's* model endpoint is common; concluding the
        // token is invalid from it would delete a working credential.
        let forbidden = classify(&LlmError::Api {
            status: 403,
            message: "forbidden".into(),
            kind: FailureKind::Permanent,
            retry_after: None,
            body: None,
        });
        assert_eq!(
            forbidden,
            VerificationOutcome::Unverified { reason: UnverifiedReason::Forbidden }
        );

        // 401 is the provider refusing the identity outright.
        let rejected = classify(&LlmError::Api {
            status: 401,
            message: "unauthorized".into(),
            kind: FailureKind::Permanent,
            retry_after: None,
            body: None,
        });
        assert_eq!(rejected, VerificationOutcome::Rejected { status: Some(401) });

        let unverified = classify(&LlmError::Api {
            status: 404,
            message: "not found".into(),
            kind: FailureKind::Permanent,
            retry_after: None,
            body: None,
        });
        assert!(matches!(unverified, VerificationOutcome::Unverified { .. }));
    }

    #[test]
    fn no_message_carries_the_server_text() {
        let report = VerificationReport {
            provider_id: Some("anthropic".into()),
            outcome: classify(&LlmError::Api {
                status: 500,
                message: "SECRET-INTERNAL-DETAIL".into(),
                kind: FailureKind::Transient,
                retry_after: None,
                body: Some("SECRET-BODY".into()),
            }),
        };
        let rendered = report.to_string();
        assert!(!rendered.contains("SECRET"), "{rendered}");
    }
}
