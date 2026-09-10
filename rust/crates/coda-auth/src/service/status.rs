//! What the profile currently holds — read, never refreshed.
//!
//! Status is a report on *stored metadata plus availability*, and that is a
//! deliberate limit:
//!
//! * it performs no network call, so showing status cannot fail because a
//!   provider is down, and cannot spend a rate-limited token refresh;
//! * it performs no migration, so looking at status never moves a credential
//!   between backends as a side effect;
//! * it enumerates all three identities, so a second credential that should
//!   not be there is visible rather than hidden behind the first one found;
//! * a credential it cannot read is reported as unreadable — never as "no
//!   credential", which would send the user through a login that overwrites
//!   something still recoverable.
//!
//! Availability of an ambient `ANTHROPIC_API_KEY` is reported *separately*
//! from the selection and from whatever a running engine happens to be using:
//! "a key is exported" and "this is the account you are signed in as" are
//! different statements.

use crate::credential::{AccountInfo, Credential, CredentialKind};
use crate::failure::AuthFailure;
use crate::service::endpoint::{AnthropicEndpoint, EndpointError};
use crate::service::identity::ProviderIdentity;
use crate::service::selection::{Selection, SelectionError};
use crate::service::transaction::CommitStep;

/// Non-secret metadata about a stored credential.
///
/// Account and expiry may be shown by an authorized host UI. They never appear
/// in `Debug`, because that is what reaches logs.
#[derive(Clone, PartialEq)]
pub struct CredentialSummary {
    pub kind: CredentialKind,
    pub expires_at: Option<chrono::DateTime<chrono::Utc>>,
    pub scopes: Vec<String>,
    pub account: Option<AccountInfo>,
}

impl std::fmt::Debug for CredentialSummary {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CredentialSummary")
            .field("kind", &self.kind)
            .field("expires_at", &self.expires_at)
            .field("scopes", &self.scopes)
            .field("account", &self.account.as_ref().map(|_| "[hidden]"))
            .finish()
    }
}

impl CredentialSummary {
    pub(crate) fn of(credential: &Credential) -> Self {
        Self {
            kind: credential.kind,
            expires_at: credential.expires_at,
            scopes: credential.scopes.clone(),
            account: credential.account.clone(),
        }
    }

    /// The account's display name, when the provider supplied one.
    pub fn account_label(&self) -> Option<&str> {
        let account = self.account.as_ref()?;
        account
            .email_address
            .as_deref()
            .or(account.account_uuid.as_deref())
            .or(account.organization_uuid.as_deref())
    }

    /// Whether the access token is past its expiry. `None` when the provider
    /// gave no expiry.
    pub fn is_expired(&self) -> Option<bool> {
        self.expires_at.map(|expiry| expiry <= chrono::Utc::now())
    }
}

/// What one identity's slot in the profile holds.
#[derive(Debug, Clone, PartialEq)]
pub enum StoredState {
    /// Nothing is stored for this identity.
    Absent,
    /// A credential is stored.
    Present(CredentialSummary),
    /// Something is stored but could not be read: locked, corrupt, written by
    /// another user or machine, or in a format this build does not understand.
    Unreadable(AuthFailure),
}

/// One identity's status.
#[derive(Debug, Clone, PartialEq)]
pub struct ProviderStatus {
    pub identity: ProviderIdentity,
    pub state: StoredState,
}

impl ProviderStatus {
    pub fn is_present(&self) -> bool {
        matches!(self.state, StoredState::Present(_))
    }
}

/// Read one identity's slot without refreshing or migrating anything.
///
/// This is the single reader behind both the service's status and the
/// engine's provider selection, so the two cannot disagree about whether a
/// credential is absent, present, or unreadable.
pub async fn read_stored_state(
    store: &crate::store::ProfileCredentialStore,
    identity: ProviderIdentity,
) -> StoredState {
    use crate::store::CredentialStore;

    // read_only: status and selection never migrate a credential between
    // backends, and never refresh one.
    match store.read_only(&identity.store_key()).await {
        Ok(None) => StoredState::Absent,
        Ok(Some(raw)) => match serde_json::from_str::<Credential>(&raw) {
            Ok(credential) if credential.provider_id == identity.stored_id() => {
                StoredState::Present(CredentialSummary::of(&credential))
            }
            // A credential filed under the wrong provider is a real fault, not
            // an absence: it must never be handed to a provider.
            Ok(_) => StoredState::Unreadable(AuthFailure::ProviderMismatch),
            Err(error) => StoredState::Unreadable(AuthFailure::classify(
                &crate::error::AuthError::Serialization(error),
            )),
        },
        Err(error) => StoredState::Unreadable(AuthFailure::classify(&error)),
    }
}

/// [`read_stored_state`] for every identity, in the fixed order.
pub async fn read_provider_states(
    store: &crate::store::ProfileCredentialStore,
) -> Vec<ProviderStatus> {
    let mut providers = Vec::with_capacity(ProviderIdentity::ALL.len());
    for identity in ProviderIdentity::ALL {
        providers.push(ProviderStatus { identity, state: read_stored_state(store, identity).await });
    }
    providers
}

/// Projects reported states onto the selector's view of them, keeping
/// "unreadable" distinct from "absent".
pub fn selection_entries(
    providers: &[ProviderStatus],
) -> Vec<(ProviderIdentity, crate::service::selection::StoredEntry)> {
    use crate::service::selection::StoredEntry;

    providers
        .iter()
        .map(|status| {
            let entry = match &status.state {
                StoredState::Absent => StoredEntry::Absent,
                StoredState::Present(_) => StoredEntry::Present,
                StoredState::Unreadable(failure) => StoredEntry::Unreadable(*failure),
            };
            (status.identity, entry)
        })
        .collect()
}

/// The whole picture, as stored.
#[derive(Debug, Clone)]
pub struct AuthStatus {
    /// All three identities, always, in a fixed order.
    pub providers: Vec<ProviderStatus>,
    /// What the shared selector makes of it — including why it refuses.
    pub selection: Result<Selection, SelectionError>,
    /// The saved `defaultProvider`, as written.
    pub saved_default: Option<String>,
    /// The saved Copilot tenant, if any.
    pub github_enterprise_domain: Option<String>,
    /// Whether `ANTHROPIC_API_KEY` is exported. Availability only: it says
    /// nothing about which account is selected or running.
    pub environment_api_key: bool,
    /// Where an Anthropic **API-key** request would go from this process, or
    /// why the configured endpoint was refused.
    ///
    /// Reported rather than acted on: `status` refreshes nothing and contacts
    /// nobody, and an invalid `ANTHROPIC_BASE_URL` is exactly the situation in
    /// which a user needs to be able to read their own configuration back.
    pub anthropic_endpoint: Result<AnthropicEndpoint, EndpointError>,
    /// Set when the settings file could not be read; the rest of the report is
    /// still valid.
    pub settings_error: Option<AuthFailure>,
    /// Set when a provider's context (its saved deployment) could not be
    /// resolved. That provider is not registered: it cannot be signed in to
    /// and its credential cannot be refreshed until the configuration is
    /// fixed, because either would have to invent endpoints.
    pub provider_context_error: Option<AuthFailure>,
}

impl AuthStatus {
    /// The status of one identity.
    pub fn provider(&self, identity: ProviderIdentity) -> &ProviderStatus {
        self.providers
            .iter()
            .find(|status| status.identity == identity)
            .expect("every identity is always reported")
    }

    /// Identities with a readable stored credential.
    pub fn stored(&self) -> Vec<ProviderIdentity> {
        self.providers
            .iter()
            .filter(|status| status.is_present())
            .map(|status| status.identity)
            .collect()
    }

    /// Identities whose stored credential could not be read.
    pub fn unreadable(&self) -> Vec<(ProviderIdentity, AuthFailure)> {
        self.providers
            .iter()
            .filter_map(|status| match &status.state {
                StoredState::Unreadable(failure) => Some((status.identity, *failure)),
                _ => None,
            })
            .collect()
    }

    /// `true` when more than one credential is stored, or one is unreadable —
    /// states a user should be told about even though the engine may still run.
    pub fn has_inconsistency(&self) -> bool {
        self.stored().len() > 1 || !self.unreadable().is_empty()
    }
}

/// What a logout did, and what it deliberately did not do.
#[derive(Debug, Clone)]
pub struct LogoutReport {
    /// Identities whose stored credential was removed.
    pub removed: Vec<ProviderIdentity>,
    /// Whether `defaultProvider` was cleared because it named a removed
    /// account.
    pub cleared_default_provider: bool,
    /// Whether an exported `ANTHROPIC_API_KEY` is still present — the host is
    /// now disconnected, but this machine can still authenticate with it if
    /// someone chooses to.
    pub environment_api_key_still_available: bool,
}

/// Why a logout could not finish.
///
/// A logout removes credentials *and* clears the saved choice that named them:
/// leaving one done and the other not produces a profile that is signed out
/// but still points at a deleted account. So it is undone, and what happened
/// is reported — never returned as a bare failure while a credential has
/// actually been lost.
#[derive(Debug, Clone)]
pub enum LogoutFailure {
    /// The logout failed at `step`. `rolled_back` says whether anything had to
    /// be undone; either way the profile is back to what it was.
    Failed {
        step: CommitStep,
        failure: AuthFailure,
        rolled_back: bool,
    },
    /// The logout failed *and* the profile could not be put back: a credential
    /// may be gone, or a saved choice may name an account that no longer
    /// exists.
    RestorationFailed {
        failed_step: CommitStep,
        failure: AuthFailure,
        restore_step: CommitStep,
        restore_failure: AuthFailure,
    },
}

impl LogoutFailure {
    /// Whether the profile is known to be in the state it was in before.
    pub fn profile_is_intact(&self) -> bool {
        matches!(self, Self::Failed { .. })
    }

    /// The safe classification of the original failure.
    pub fn failure(&self) -> AuthFailure {
        match self {
            Self::Failed { failure, .. } | Self::RestorationFailed { failure, .. } => *failure,
        }
    }
}

impl std::fmt::Display for LogoutFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Failed { step, failure, rolled_back } => {
                let tail = if *rolled_back {
                    "; the stored credential was left in place"
                } else {
                    "; nothing was changed"
                };
                write!(f, "signing out failed while {step}: {failure}{tail}")
            }
            Self::RestorationFailed { failed_step, failure, restore_step, restore_failure } => {
                write!(
                    f,
                    "signing out failed while {failed_step}: {failure}. Undoing it also failed \
                     while {restore_step}: {restore_failure}. The saved credentials may be \
                     incomplete; check the authentication status before signing in again",
                )
            }
        }
    }
}

impl std::error::Error for LogoutFailure {}

impl std::fmt::Display for LogoutReport {
    /// The honest summary a host prints. It never claims a provider-side
    /// revocation, and never implies other processes were touched.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        if self.removed.is_empty() {
            f.write_str("No stored credential was removed.")?;
        } else {
            let names: Vec<&str> = self.removed.iter().map(|i| i.label()).collect();
            write!(f, "Removed the stored credential for {}.", names.join(", "))?;
        }
        if self.cleared_default_provider {
            f.write_str(" The saved provider choice was cleared.")?;
        }
        f.write_str(
            " This session is now disconnected; no other provider was selected automatically.",
        )?;
        if self.environment_api_key_still_available {
            f.write_str(
                " ANTHROPIC_API_KEY is still set in this environment, so this machine can still \
                 authenticate with it until you unset it.",
            )?;
        }
        f.write_str(
            " Engines already running in other processes or shells keep their own connection, \
             and nothing was revoked at the provider.",
        )
    }
}
