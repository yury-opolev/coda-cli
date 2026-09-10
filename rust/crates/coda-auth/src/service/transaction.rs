//! The login commit: one section, one coherent outcome.
//!
//! # What it changes
//!
//! A login is not one write. It is:
//!
//! 1. the new credential,
//! 2. the removal of the credentials it displaces (this product keeps exactly
//!    one), and
//! 3. the settings keys that record *which* account was chosen and, for
//!    Copilot, which tenant it lives in.
//!
//! One login has no step 1 at all: choosing the exported `ANTHROPIC_API_KEY`
//! ([`CommitRequest::environment_api_key`]) records the *selection* and
//! removes what it displaces — including the stored key of the very identity
//! being selected — without ever serializing the key. The value stays in the
//! process that exported it; a later process without the variable finds a
//! saved choice and no credential, which is a refusal, not a silent fallback.
//!
//! Those must all land, or none of them. A profile with a Claude credential
//! and `defaultProvider: github-copilot` is not a working state, and neither
//! is one where a working account was deleted and its replacement never
//! written.
//!
//! # How it is made atomic enough
//!
//! There is no transactional filesystem underneath, so this does the next best
//! thing:
//!
//! * everything happens inside the profile's single commit section
//!   ([`AUTH_COMMIT_KEY`]), so no other cooperating process writes in the
//!   middle of it;
//! * before the first write, the credential *values* of all three identities,
//!   their retirement markers, and the owned settings keys are snapshotted;
//! * any failure is followed by a rollback to that snapshot: the same
//!   credential value, the same presence or absence, the same marker state.
//!   The restoration is **semantic, not archaeological** — a credential is put
//!   back into the profile's *primary* backend, which is where a reader will
//!   look for it, not into whichever legacy source it may originally have been
//!   adopted from (writing there is not this transaction's business, and the
//!   adoption source may not even be writable);
//! * a rollback that itself fails is reported as
//!   [`CommitOutcome::RestorationFailed`], naming the step, and explicitly
//!   does not authorize an engine restart.
//!
//! Everything here uses **raw store operations**. [`CredentialManager`]'s
//! `store_credential` and `logout` take the same section themselves, so
//! calling them from in here would deadlock; that is a real hazard, and
//! `a_commit_holds_the_section_and_still_completes` is the test that catches
//! it.
//!
//! [`CredentialManager`]: crate::manager::CredentialManager

use std::sync::Arc;

use crate::coordination::{CommitCoordinator, AUTH_COMMIT_KEY};
use crate::credential::{Credential, CredentialKind};
use crate::error::AuthError;
use crate::failure::AuthFailure;
use crate::service::copilot_context::{CopilotContext, PendingCopilotContext};
use crate::service::identity::ProviderIdentity;
use crate::service::settings::{AuthSettingsPatch, AuthSettingsPort};
use crate::store::{CredentialStore, ProfileCredentialStore};

/// A step of the transaction, named so a failure report can say what went
/// wrong without quoting anything untrusted.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CommitStep {
    /// Reading the state the transaction would have to restore.
    Snapshot,
    /// Reading the settings keys the transaction owns.
    ReadSettings,
    /// Entering the profile's commit section.
    EnterSection,
    /// Writing the new credential.
    StoreCredential,
    /// Removing the credential of a provider being replaced.
    RemoveDisplaced(ProviderIdentity),
    /// Writing `defaultProvider` / `githubEnterpriseDomain`.
    ApplySettings,
    /// Putting a credential back during a rollback.
    RestoreCredential(ProviderIdentity),
    /// Putting a retirement marker back during a rollback.
    RestoreRetirement(ProviderIdentity),
    /// Putting the settings keys back during a rollback.
    RestoreSettings,
}

impl std::fmt::Display for CommitStep {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Snapshot => f.write_str("reading the current credentials"),
            Self::ReadSettings => f.write_str("reading the saved provider settings"),
            Self::EnterSection => f.write_str("waiting for exclusive access to the profile"),
            Self::StoreCredential => f.write_str("saving the new credential"),
            Self::RemoveDisplaced(identity) => {
                write!(f, "removing the credential for {}", identity.engine_id())
            }
            Self::ApplySettings => f.write_str("saving the provider settings"),
            Self::RestoreCredential(identity) => {
                write!(f, "restoring the credential for {}", identity.engine_id())
            }
            Self::RestoreRetirement(identity) => {
                write!(f, "restoring the sign-out record for {}", identity.engine_id())
            }
            Self::RestoreSettings => f.write_str("restoring the provider settings"),
        }
    }
}

/// How a commit ended.
///
/// Only [`CommitOutcome::Committed`] permits the host to start an engine: see
/// [`CommitOutcome::engine_may_start`].
#[derive(Debug)]
pub enum CommitOutcome {
    /// The credential, the displacements and the settings all landed.
    Committed {
        identity: ProviderIdentity,
        /// Providers whose stored credential was removed by this login.
        replaced: Vec<ProviderIdentity>,
        /// Whether any owned settings key actually changed.
        settings_changed: bool,
    },
    /// The profile changed to a different account while the login was being
    /// prepared. Nothing was written: the newer connection stands.
    Superseded {
        /// The account the profile holds now, when it is unambiguous.
        current: Option<ProviderIdentity>,
    },
    /// The commit failed. `rolled_back` says whether anything had to be undone;
    /// either way the profile is back to what it was.
    Failed {
        step: CommitStep,
        failure: AuthFailure,
        rolled_back: bool,
    },
    /// The commit was interrupted before it could reach a terminal state — the
    /// task carrying it panicked, was aborted, or could not be started.
    ///
    /// Nothing here may be presented as a success *or* as a clean failure: the
    /// profile may hold the new credential, the old one, or both, and no
    /// rollback was observed to complete. The honest report is "we do not
    /// know"; the honest action is to show status and let the user decide.
    Indeterminate { cause: CommitInterruption },
    /// The commit failed *and* the profile could not be put back.
    ///
    /// This is the outcome that must never be dressed up: neither the old nor
    /// the new connection can be claimed to work.
    RestorationFailed {
        failed_step: CommitStep,
        failure: AuthFailure,
        restore_step: CommitStep,
        restore_failure: AuthFailure,
    },
}

/// Why a commit never reached a terminal state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CommitInterruption {
    /// The task running the transaction panicked or was aborted.
    TaskFailed,
    /// There was no runtime to run the transaction on, so it never started.
    ///
    /// Nothing was written in this case, but it is still not a *failed*
    /// commit: it is a host that called this from the wrong place, and it is
    /// reported rather than silently succeeding or silently doing nothing.
    NotStarted,
}

impl std::fmt::Display for CommitInterruption {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::TaskFailed => f.write_str("the sign-in task stopped unexpectedly"),
            Self::NotStarted => f.write_str("the sign-in could not be started"),
        }
    }
}

impl CommitOutcome {
    /// Whether the host may start an engine on the strength of this outcome.
    pub fn engine_may_start(&self) -> bool {
        matches!(self, Self::Committed { .. })
    }

    /// Whether the profile is known to be in the state it was in before the
    /// commit.
    ///
    /// False for an interrupted commit as well as a failed restoration: in
    /// neither case did anyone observe the profile being put back.
    pub fn profile_is_intact(&self) -> bool {
        !matches!(self, Self::RestorationFailed { .. } | Self::Indeterminate { .. })
    }
}

impl std::fmt::Display for CommitOutcome {
    /// Safe wording: step names are a closed set and failures are already
    /// classified, so no store key, server body or secret can appear here.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Committed { identity, .. } => write!(f, "signed in to {}", identity.label()),
            Self::Superseded { current } => match current {
                Some(identity) => write!(
                    f,
                    "another sign-in completed first; this machine is now connected to {}",
                    identity.label()
                ),
                None => f.write_str("the saved credentials changed while signing in; nothing was written"),
            },
            Self::Failed { step, failure, rolled_back } => {
                let tail = if *rolled_back {
                    "; the previous connection was restored"
                } else {
                    "; nothing was changed"
                };
                write!(f, "sign-in failed while {step}: {failure}{tail}")
            }
            Self::RestorationFailed { failed_step, failure, restore_step, restore_failure } => write!(
                f,
                "sign-in failed while {failed_step}: {failure}. Undoing it also failed while \
                 {restore_step}: {restore_failure}. The saved credentials may be incomplete; \
                 no connection was started",
            ),
            Self::Indeterminate { cause } => write!(
                f,
                "{cause}, so how far it got is unknown. The saved credentials may be incomplete; \
                 no connection was started. Check the authentication status before signing in \
                 again",
            ),
        }
    }
}

/// What a credential looks like for the purpose of "is this still the same
/// account?".
///
/// Deliberately *not* the raw blob: a normal token refresh rewrites the blob
/// while the account stays the same, and a login that refused to commit
/// because the engine refreshed a token in the meantime would be unusable.
/// What it does capture is the kind and an account discriminator — the account
/// identifiers for OAuth, a digest of the key material for an API key (which
/// has no other identity).
#[derive(Clone, PartialEq, Eq)]
pub struct CredentialFingerprint {
    kind: CredentialKind,
    account: Option<String>,
}

impl std::fmt::Debug for CredentialFingerprint {
    /// The account discriminator can be a digest of key material; it never
    /// prints.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CredentialFingerprint")
            .field("kind", &self.kind)
            .field("account", &self.account.as_ref().map(|_| "[REDACTED]"))
            .finish()
    }
}

impl CredentialFingerprint {
    fn of(credential: &Credential) -> Self {
        let account = match credential.kind {
            CredentialKind::OAuth => credential.account.as_ref().and_then(|account| {
                account
                    .account_uuid
                    .clone()
                    .or_else(|| account.email_address.clone())
                    .or_else(|| account.organization_uuid.clone())
            }),
            // An API key is its own identity; hash it so two different keys are
            // two different accounts without the key itself being retained.
            CredentialKind::ApiKey => credential.api_key.as_ref().map(|key| {
                use base64::Engine as _;
                use sha2::Digest;
                base64::engine::general_purpose::URL_SAFE_NO_PAD
                    .encode(sha2::Sha256::digest(key.expose().as_bytes()))
            }),
        };
        Self { kind: credential.kind, account }
    }
}

/// What the profile held when a login was prepared.
///
/// Captured before the network work starts and re-checked inside the commit
/// section, so a login that took a minute cannot overwrite an account somebody
/// signed in to in the meantime.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Baseline {
    entries: Vec<(ProviderIdentity, Option<CredentialFingerprint>)>,
}

impl Baseline {
    /// Read the current fingerprints of all three identities.
    ///
    /// Uses the non-migrating read: a baseline must not move credentials
    /// between backends as a side effect.
    pub async fn capture(store: &Arc<ProfileCredentialStore>) -> Result<Self, AuthError> {
        let mut entries = Vec::with_capacity(ProviderIdentity::ALL.len());
        for identity in ProviderIdentity::ALL {
            let raw = store.read_only(&identity.store_key()).await?;
            entries.push((identity, raw.as_deref().and_then(fingerprint_of)));
        }
        Ok(Self { entries })
    }

    fn from_snapshot(snapshot: &[Entry]) -> Self {
        Self {
            entries: snapshot
                .iter()
                .map(|entry| {
                    (entry.identity, entry.blob.as_deref().and_then(fingerprint_of))
                })
                .collect(),
        }
    }
}

/// A blob that will not parse has no fingerprint; it compares equal to itself
/// as "unreadable", which keeps a corrupt entry from being read as a change.
fn fingerprint_of(raw: &str) -> Option<CredentialFingerprint> {
    serde_json::from_str::<Credential>(raw)
        .ok()
        .map(|credential| CredentialFingerprint::of(&credential))
}

/// What the transaction is asked to do.
pub struct CommitRequest {
    identity: ProviderIdentity,
    intent: CredentialIntent,
    settings: AuthSettingsPatch,
    baseline: Option<Baseline>,
    context: Option<PendingCopilotContext>,
}

/// What a commit persists for the identity it signs in to.
///
/// A closed, private choice rather than an `Option<Credential>`: "no
/// credential" is only ever a legitimate state for the console API key, whose
/// value the process environment already carries. An OAuth identity with
/// nothing to store is not a state this transaction can be asked for, because
/// there is no constructor that produces one.
enum CredentialIntent {
    /// Write this credential, and remove the ones it displaces.
    Stored(Credential),
    /// Record the selection only. The key stays in `ANTHROPIC_API_KEY`, is
    /// never serialized, and the stored copy of that same identity — readable
    /// now or merely resurrectable later — is removed and retired with it.
    EnvironmentApiKey,
}

impl std::fmt::Debug for CommitRequest {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CommitRequest")
            .field("identity", &self.identity)
            .field("credential", &self.intent.describe())
            .field("settings", &self.settings)
            .finish_non_exhaustive()
    }
}

impl CredentialIntent {
    /// A safe description: never the credential, and never a claim that a key
    /// is being saved when it is not.
    fn describe(&self) -> &'static str {
        match self {
            Self::Stored(_) => "[REDACTED]",
            Self::EnvironmentApiKey => "none (the environment carries the key)",
        }
    }
}

impl CommitRequest {
    /// A commit of `credential` as `identity`, changing no settings.
    pub fn new(identity: ProviderIdentity, credential: Credential) -> Self {
        Self {
            identity,
            intent: CredentialIntent::Stored(credential),
            settings: AuthSettingsPatch::empty(),
            baseline: None,
            context: None,
        }
    }

    /// A commit that records the console API key as the chosen provider
    /// **without storing a key**.
    ///
    /// The value stays where the user exported it. What lands in the profile
    /// is the selection alone, plus the removal — and the retirement — of the
    /// stored key this replaces, so no later read can quietly answer with a
    /// saved credential instead of the environment.
    ///
    /// There is deliberately no parameter: a key passed in here would be a key
    /// that could be written, and this is the constructor whose whole purpose
    /// is that no such value exists inside the transaction.
    pub fn environment_api_key() -> Self {
        Self {
            identity: ProviderIdentity::AnthropicApiKey,
            intent: CredentialIntent::EnvironmentApiKey,
            settings: AuthSettingsPatch::empty(),
            baseline: None,
            context: None,
        }
    }

    /// The provider context this login authenticated against, published as
    /// part of the commit so a refresh of the new credential can never use the
    /// endpoints of the account it replaced.
    pub fn with_context(mut self, context: PendingCopilotContext) -> Self {
        self.context = Some(context);
        self
    }

    /// The settings keys this login should record.
    pub fn with_settings(mut self, settings: AuthSettingsPatch) -> Self {
        self.settings = settings;
        self
    }

    /// The state the login was prepared against.
    ///
    /// Without one, the commit does not check for a concurrent account change;
    /// with one, a different account appearing meanwhile yields
    /// [`CommitOutcome::Superseded`].
    pub fn with_baseline(mut self, baseline: Baseline) -> Self {
        self.baseline = Some(baseline);
        self
    }

    /// The identity being committed.
    pub fn identity(&self) -> ProviderIdentity {
        self.identity
    }
}

/// One credential's prior state.
struct Entry {
    identity: ProviderIdentity,
    blob: Option<String>,
    retired: bool,
}

/// Run the transaction. See the module docs for the guarantees.
pub async fn commit(
    store: &Arc<ProfileCredentialStore>,
    coordinator: &Arc<dyn CommitCoordinator>,
    settings: &dyn AuthSettingsPort,
    request: CommitRequest,
) -> CommitOutcome {
    // The credential must belong to the identity it is being filed under
    // before anything is locked or written — and a request that stores nothing
    // must not be carrying a provider context, which only ever binds a
    // credential this commit would write.
    let json = match &request.intent {
        CredentialIntent::Stored(credential) => {
            if credential.provider_id != request.identity.stored_id() {
                return CommitOutcome::Failed {
                    step: CommitStep::StoreCredential,
                    failure: AuthFailure::ProviderMismatch,
                    rolled_back: false,
                };
            }
            match serde_json::to_string(credential) {
                Ok(json) => Some(json),
                Err(error) => {
                    return CommitOutcome::Failed {
                        step: CommitStep::StoreCredential,
                        failure: AuthFailure::classify(&AuthError::Serialization(error)),
                        rolled_back: false,
                    }
                }
            }
        }
        CredentialIntent::EnvironmentApiKey => {
            // Publishing a Copilot context here would bind endpoints to a
            // credential that is never written — a binding nothing could ever
            // satisfy, established before any of it was checked. Refused
            // before the section is entered, so it cannot half-happen.
            if request.context.is_some() {
                return CommitOutcome::Failed {
                    step: CommitStep::StoreCredential,
                    failure: AuthFailure::ProviderMismatch,
                    rolled_back: false,
                };
            }
            None
        }
    };

    // Everything below happens inside the one section every cooperating
    // process takes. Nothing in here calls a manager method that would take it
    // again.
    let _section = match coordinator.begin(AUTH_COMMIT_KEY).await {
        Ok(guard) => guard,
        Err(error) => return failed(CommitStep::EnterSection, &error, false),
    };

    // ── Snapshot ─────────────────────────────────────────────────────────────
    let mut snapshot = Vec::with_capacity(ProviderIdentity::ALL.len());
    for identity in ProviderIdentity::ALL {
        let key = identity.store_key();
        let blob = match store.read_only(&key).await {
            Ok(blob) => blob,
            Err(error) => return failed(CommitStep::Snapshot, &error, false),
        };
        let retired = match store.is_retired(&key).await {
            Ok(retired) => retired,
            Err(error) => return failed(CommitStep::Snapshot, &error, false),
        };
        snapshot.push(Entry { identity, blob, retired });
    }

    // ── Concurrency check ────────────────────────────────────────────────────
    if let Some(baseline) = &request.baseline {
        let current = Baseline::from_snapshot(&snapshot);
        if &current != baseline {
            let stored: Vec<ProviderIdentity> = snapshot
                .iter()
                .filter(|entry| entry.blob.is_some())
                .map(|entry| entry.identity)
                .collect();
            let current = match stored.as_slice() {
                [only] => Some(*only),
                _ => None,
            };
            return CommitOutcome::Superseded { current };
        }
    }

    let settings_before = match settings.load() {
        Ok(settings) => settings,
        Err(error) => return failed(CommitStep::ReadSettings, &error, false),
    };
    let settings_patch = request.settings.changes_against(&settings_before);

    // ── Mutation ─────────────────────────────────────────────────────────────
    //
    // The provider context is published *before* the credential write, bound
    // to the credential being written. That ordering is what removes both
    // wrong-host windows: while the old credential is still stored it no
    // longer matches the published binding (so it cannot be refreshed against
    // the new endpoints), and the moment the new one is stored the endpoints
    // it belongs to are already in force.
    let published_context = match &request.intent {
        CredentialIntent::Stored(credential) => request
            .context
            .as_ref()
            .map(|context| (context, context.publish(credential))),
        // Already refused above: an environment selection publishes nothing.
        CredentialIntent::EnvironmentApiKey => None,
    };

    let key = request.identity.store_key();
    if let Some(json) = &json {
        if let Err(error) = store.set(&key, json).await {
            // Nothing has changed yet — except the context, which goes back.
            if let Some((context, previous)) = published_context {
                context.restore(previous);
            }
            return failed(CommitStep::StoreCredential, &error, false);
        }
        // A sign-in supersedes a previous sign-out of the same account.
        if let Err(error) = store.set_retired(&key, false).await {
            return rollback(store, settings, &snapshot, &settings_before, None, published_context,
                            CommitStep::StoreCredential, &error).await;
        }
    }

    let mut replaced = Vec::new();
    for entry in &snapshot {
        let is_target = entry.identity == request.identity;
        let remove = match &request.intent {
            CredentialIntent::Stored(_) => !is_target && entry.blob.is_some(),
            // The environment carries the key now, so the stored copy of that
            // same identity goes too — and its removal is published even when
            // nothing readable is there, because a legacy source that is
            // unavailable *at this moment* must not be able to come back with
            // an old key and answer in place of the environment.
            CredentialIntent::EnvironmentApiKey => is_target || entry.blob.is_some(),
        };
        if !remove {
            continue;
        }
        // The ordinary delete: it fans out to the legacy sources and records
        // the retirement intent that keeps them from resurrecting later.
        if let Err(error) = store.delete(&entry.identity.store_key()).await {
            return rollback(store, settings, &snapshot, &settings_before, None, published_context,
                            CommitStep::RemoveDisplaced(entry.identity), &error).await;
        }
        // Only what was actually there is reported as replaced: a retirement
        // published over an absent slot removed nothing the user had.
        if entry.blob.is_some() {
            replaced.push(entry.identity);
        }
    }

    let settings_changed = !settings_patch.is_empty();
    if settings_changed {
        if let Err(error) = settings.apply(&settings_patch) {
            return rollback(store, settings, &snapshot, &settings_before, Some(&settings_patch),
                            published_context, CommitStep::ApplySettings, &error).await;
        }
    }

    CommitOutcome::Committed { identity: request.identity, replaced, settings_changed }
}

fn failed(step: CommitStep, error: &AuthError, rolled_back: bool) -> CommitOutcome {
    CommitOutcome::Failed { step, failure: AuthFailure::classify(error), rolled_back }
}

/// Put the profile back exactly as the snapshot found it.
///
/// Credential *bytes*, presence/absence and retirement markers are all
/// restored, and the settings keys the transaction actually changed are put
/// back. The first restoration failure stops the rollback and is reported: a
/// half-restored profile that claims success is worse than one that says which
/// step failed.
async fn rollback(
    store: &Arc<ProfileCredentialStore>,
    settings: &dyn AuthSettingsPort,
    snapshot: &[Entry],
    settings_before: &crate::service::settings::AuthSettings,
    applied_settings: Option<&AuthSettingsPatch>,
    published_context: Option<(&PendingCopilotContext, Arc<CopilotContext>)>,
    failed_step: CommitStep,
    failure: &AuthError,
) -> CommitOutcome {
    let failure = AuthFailure::classify(failure);

    // The endpoints go back first: nothing that follows may be refreshed
    // against a deployment this commit did not establish.
    if let Some((context, previous)) = published_context {
        context.restore(previous);
    }

    if let Some(patch) = applied_settings {
        let inverse = patch.inverse_of(settings_before);
        if let Err(error) = settings.apply(&inverse) {
            return restoration_failed(failed_step, failure, CommitStep::RestoreSettings, &error);
        }
    }

    for entry in snapshot {
        let key = entry.identity.store_key();
        if let Err(error) = store.restore_primary(&key, entry.blob.as_deref()).await {
            return restoration_failed(
                failed_step,
                failure,
                CommitStep::RestoreCredential(entry.identity),
                &error,
            );
        }
        if let Err(error) = store.set_retired(&key, entry.retired).await {
            return restoration_failed(
                failed_step,
                failure,
                CommitStep::RestoreRetirement(entry.identity),
                &error,
            );
        }
    }

    CommitOutcome::Failed { step: failed_step, failure, rolled_back: true }
}

fn restoration_failed(
    failed_step: CommitStep,
    failure: AuthFailure,
    restore_step: CommitStep,
    restore_error: &AuthError,
) -> CommitOutcome {
    CommitOutcome::RestorationFailed {
        failed_step,
        failure,
        restore_step,
        restore_failure: AuthFailure::classify(restore_error),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::secret::Secret;

    fn api_key(key: &str) -> Credential {
        Credential {
            provider_id: "anthropic-api-key".into(),
            kind: CredentialKind::ApiKey,
            access_token: None,
            refresh_token: None,
            api_key: Some(Secret::new(key.into())),
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        }
    }

    #[test]
    fn a_fingerprint_never_prints_its_discriminator() {
        let fingerprint = CredentialFingerprint::of(&api_key("sk-ant-secret-value"));
        let rendered = format!("{fingerprint:?}");
        assert!(!rendered.contains("sk-ant-secret-value"), "{rendered}");
        assert!(!rendered.contains(fingerprint.account.as_deref().unwrap()), "{rendered}");
    }

    #[test]
    fn two_different_api_keys_are_two_different_accounts() {
        assert_ne!(
            CredentialFingerprint::of(&api_key("sk-one")),
            CredentialFingerprint::of(&api_key("sk-two"))
        );
        assert_eq!(
            CredentialFingerprint::of(&api_key("sk-one")),
            CredentialFingerprint::of(&api_key("sk-one"))
        );
    }

    #[test]
    fn a_commit_request_debug_keeps_the_credential_out() {
        let request = CommitRequest::new(ProviderIdentity::AnthropicApiKey, api_key("sk-secret"));
        assert!(!format!("{request:?}").contains("sk-secret"));
    }

    #[test]
    fn an_environment_request_never_claims_a_credential_is_being_saved() {
        let request = CommitRequest::environment_api_key();
        let rendered = format!("{request:?}");
        assert!(!rendered.contains("REDACTED"), "there is no secret here to redact: {rendered}");
        assert!(rendered.contains("environment"), "{rendered}");
        assert_eq!(request.identity(), ProviderIdentity::AnthropicApiKey);
    }
}
