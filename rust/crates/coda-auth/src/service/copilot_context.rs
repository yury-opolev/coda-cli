//! The Copilot endpoints a refresh is allowed to use, bound to the credential
//! it belongs to.
//!
//! # The hole this closes
//!
//! A Copilot credential is refreshed by sending its **durable GitHub token** to
//! the tenant's exchange endpoint. The endpoints come from a
//! [`CopilotConfig`], and a manager that captured one at construction keeps
//! using it. So after signing in to a different deployment — public → an
//! enterprise tenant, or the reverse — the *new* durable token was still being
//! exchanged at the *old* host. That is a credential sent to the wrong server,
//! not merely a stale label.
//!
//! # Why not just re-resolve the settings
//!
//! Re-reading `githubEnterpriseDomain` plus `GH_COPILOT_ENTERPRISE_DOMAIN` at
//! refresh time looks simpler and is wrong: an exported environment domain
//! outranks the saved one, so a user who explicitly signed in to *public*
//! GitHub would silently have their refresh pointed back at the tenant the
//! variable names. The choice made during login is the authority, so what is
//! published here is the **already-resolved configuration that login used**,
//! not a recipe for resolving it again.
//!
//! # The two races, and the binding that closes them
//!
//! The config and the credential must come from the same commit — an old token
//! must not reach a new host, and a new token must not reach an old one. Two
//! properties give that:
//!
//! 1. **Publication happens inside the commit section, before the credential
//!    is written**, and is undone if the commit rolls back. So there is never a
//!    moment where the store holds the new credential and this cell holds the
//!    old endpoints.
//! 2. **Every published context names the credential it belongs to**, by a
//!    digest of that credential's durable token. A refresh whose credential
//!    does not match is refused before any network call. The durable token
//!    survives a refresh (only the short-lived Copilot token is replaced), so
//!    ordinary refreshes keep matching; a *different login* does not.
//!
//! A generation counter is checked again after the network call, so a context
//! that changed mid-refresh discards the result instead of persisting a
//! credential minted against endpoints that are no longer current.

use std::sync::{Arc, RwLock};

use async_trait::async_trait;

use crate::credential::Credential;
use crate::credential_source::CredentialManagerSource;
use crate::error::AuthError;
use crate::manager::CredentialManager;
use crate::provider::copilot::{CopilotConfig, CopilotDeployment, CopilotProvider, PROVIDER_ID};
use crate::provider::AuthProvider;

/// One published Copilot context.
pub struct CopilotContext {
    /// Monotonic; changes on every publication.
    pub generation: u64,
    /// The resolved endpoints. This is what login actually used.
    pub config: CopilotConfig,
    /// What those endpoints contact, derived from the endpoints themselves.
    pub deployment: CopilotDeployment,
    /// Digest of the credential this context was published for.
    ///
    /// `None` means **matches nothing**, not "matches anything". A context
    /// with no credential behind it — a profile that is not signed in to
    /// Copilot, or a service built before its binding could be established —
    /// must refuse every credential, or another process storing a *different*
    /// account would have its token refreshed against these endpoints.
    bound: Option<String>,
    // Keep HTTP connections and the exchange-absence latch scoped to this context.
    provider: Arc<CopilotProvider>,
}

impl std::fmt::Debug for CopilotContext {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CopilotContext")
            .field("generation", &self.generation)
            .field("deployment", &self.deployment)
            .field("config", &self.config)
            .field("bound", &self.bound.as_ref().map(|_| "[REDACTED]"))
            .finish()
    }
}

impl CopilotContext {
    /// Whether `credential` is the one this context was published for.
    ///
    /// An unbound context matches nothing: see [`CopilotContext::bound`].
    pub fn matches(&self, credential: &Credential) -> bool {
        match (&self.bound, bind_to(credential)) {
            (Some(bound), Some(candidate)) => *bound == candidate,
            _ => false,
        }
    }

    /// Whether this context has a credential behind it at all.
    pub fn is_bound(&self) -> bool {
        self.bound.is_some()
    }
}

/// A digest of the credential this context serves — stable across refreshes of
/// the same login, different for a different one, and never the token itself.
///
/// The durable GitHub token is the key when there is one: a refresh replaces
/// only the short-lived Copilot token, so the binding survives it. A
/// credential that has no durable token cannot be refreshed at all, and is
/// keyed by its access token so an existing (including .NET-written)
/// credential keeps working while its profile and context agree.
fn bind_to(credential: &Credential) -> Option<String> {
    use base64::Engine as _;
    use sha2::Digest;

    credential
        .refresh_token
        .as_ref()
        .or(credential.access_token.as_ref())
        .map(|token| {
            base64::engine::general_purpose::URL_SAFE_NO_PAD
                .encode(sha2::Sha256::digest(token.expose().as_bytes()))
        })
}

/// The current Copilot context, shared by everything that refreshes.
#[derive(Debug)]
pub struct CopilotContextCell {
    state: RwLock<Arc<CopilotContext>>,
}

impl CopilotContextCell {
    /// The context a service or engine starts with: the resolved endpoints,
    /// bound to the credential that was **read in the same commit section**.
    ///
    /// Passing `None` for the credential is the honest "nothing is stored"
    /// case, and produces a context that matches nothing — so a credential
    /// another process stores later cannot be refreshed against these
    /// endpoints without a rebuild.
    ///
    /// Reading the credential and resolving the configuration must happen
    /// together, under the coordination writers take: sampling them
    /// separately is how a context ends up describing one account and a
    /// credential another.
    pub fn initial(
        config: CopilotConfig,
        deployment: CopilotDeployment,
        credential: Option<&Credential>,
    ) -> Arc<Self> {
        Arc::new(Self {
            state: RwLock::new(Arc::new(CopilotContext {
                generation: 0,
                provider: Arc::new(CopilotProvider::new(config.clone())),
                config,
                deployment,
                bound: credential.and_then(bind_to),
            })),
        })
    }

    /// The context in force right now.
    pub fn current(&self) -> Arc<CopilotContext> {
        Arc::clone(&self.state.read().expect("copilot context is never poisoned"))
    }

    /// Publish `config` as the context for `credential`, returning the context
    /// it replaced so a failed commit can put it back.
    pub fn publish(
        &self,
        config: CopilotConfig,
        deployment: CopilotDeployment,
        credential: &Credential,
    ) -> Arc<CopilotContext> {
        let provider = Arc::new(CopilotProvider::new(config.clone()));
        let mut state = self.state.write().expect("copilot context is never poisoned");
        let previous = Arc::clone(&state);
        *state = Arc::new(CopilotContext {
            generation: previous.generation + 1,
            provider,
            config,
            deployment,
            bound: bind_to(credential),
        });
        previous
    }

    /// Put a previously published context back, with a fresh generation so an
    /// in-flight refresh that snapshotted the abandoned one still discards its
    /// result.
    pub fn restore(&self, previous: Arc<CopilotContext>) {
        let mut state = self.state.write().expect("copilot context is never poisoned");
        *state = Arc::new(CopilotContext {
            generation: state.generation + 1,
            provider: Arc::clone(&previous.provider),
            config: previous.config.clone(),
            deployment: previous.deployment.clone(),
            bound: previous.bound.clone(),
        });
    }
}

/// The Copilot provider the manager registers: it resolves its endpoints from
/// the cell at the moment of the refresh, and refuses pairings that did not
/// come from the same commit.
pub struct ContextBoundCopilotProvider {
    cell: Arc<CopilotContextCell>,
}

impl ContextBoundCopilotProvider {
    pub fn new(cell: Arc<CopilotContextCell>) -> Self {
        Self { cell }
    }

    fn provider_for(context: &CopilotContext) -> &CopilotProvider {
        &context.provider
    }
}

impl std::fmt::Debug for ContextBoundCopilotProvider {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ContextBoundCopilotProvider")
            .field("generation", &self.cell.current().generation)
            .finish_non_exhaustive()
    }
}

#[async_trait]
impl AuthProvider for ContextBoundCopilotProvider {
    fn provider_id(&self) -> &str {
        PROVIDER_ID
    }

    fn needs_refresh(&self, credential: &Credential) -> bool {
        Self::provider_for(&self.cell.current()).needs_refresh(credential)
    }

    async fn refresh(&self, credential: &Credential) -> Result<Credential, AuthError> {
        let context = self.cell.current();

        // The pairing check, before anything leaves this process: a credential
        // that is not the one this context was published for must not be sent
        // to this context's host. An unbound context matches nothing.
        if !context.matches(credential) {
            return Err(mismatched_credential());
        }

        let refreshed = Self::provider_for(&context).refresh(credential).await?;

        // The context changed while the exchange was in flight: the result was
        // minted against endpoints that are no longer the ones in force, so it
        // is discarded rather than persisted.
        if self.cell.current().generation != context.generation {
            return Err(AuthError::CannotRefresh(
                PROVIDER_ID.into(),
                "the Copilot deployment changed while the token was being refreshed".into(),
            ));
        }
        Ok(refreshed)
    }

    fn auth_headers(&self, credential: &Credential) -> Result<Vec<(String, String)>, AuthError> {
        // Headers are the other way a token escapes, and they escape without
        // any network call of ours. A commit that published a new context
        // while the old credential is still stored — an `Indeterminate`
        // outcome, or the window inside a commit — must not let that
        // credential be presented against the new deployment, nor the new
        // account's token be presented against the old one.
        let context = self.cell.current();
        if !context.matches(credential) {
            return Err(mismatched_credential());
        }
        Self::provider_for(&context).auth_headers(credential)
    }
}

/// The one refusal for a credential that does not belong to the context in
/// force. Closed wording: no endpoint, no account, no token material.
fn mismatched_credential() -> AuthError {
    AuthError::CannotRefresh(
        PROVIDER_ID.into(),
        "the stored GitHub Copilot credential does not belong to the connection this \
         process established; reconnect before using it"
            .into(),
    )
}

/// A credential source pinned to the Copilot context it was created for.
///
/// # Why pinning matters more than which manager it uses
///
/// An inference client captures its *base URL* when it is built; only its auth
/// headers are dynamic. So a client built for one tenant, still held by a
/// caller after a login moved the profile to another, would take the new
/// account's freshly minted token and send it to the old tenant's inference
/// host. Keeping one shared manager alive is not what makes that safe — and
/// replacing the manager would not fix it either, because the stale *client*
/// is the thing holding the wrong URL.
///
/// What makes it safe is that a reference issued under one context stops
/// authenticating when that context is replaced. This source therefore fails
/// closed: the caller gets an authentication error and must rebuild its client
/// from the current context, which is exactly the moment the new base URL is
/// picked up. Nothing about the new account leaves this process in between.
///
/// The refusal is checked *before* the credential is read, so it costs no
/// store access, no refresh and no network call.
pub struct ContextScopedSource {
    inner: CredentialManagerSource,
    cell: Arc<CopilotContextCell>,
    /// The generation this reference was issued under.
    generation: u64,
    /// A stale reference is a *local* refusal: this process declined to use a
    /// credential against endpoints it no longer belongs to, and no request
    /// was sent. Reporting it as a provider rejection would tell a user to
    /// sign in again when the only thing wrong is a client that must rebuild.
    stale_refusal: std::sync::atomic::AtomicBool,
}

impl std::fmt::Debug for ContextScopedSource {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ContextScopedSource")
            .field("generation", &self.generation)
            .field("current", &self.cell.current().generation)
            .finish_non_exhaustive()
    }
}

impl ContextScopedSource {
    pub fn new(manager: Arc<CredentialManager>, cell: Arc<CopilotContextCell>) -> Self {
        let generation = cell.current().generation;
        Self::pinned(manager, cell, generation)
    }

    /// A source pinned to an already-taken snapshot, so a caller that holds
    /// the context can build the matching source without sampling again.
    pub fn pinned(
        manager: Arc<CredentialManager>,
        cell: Arc<CopilotContextCell>,
        generation: u64,
    ) -> Self {
        Self {
            inner: CredentialManagerSource::new(manager, PROVIDER_ID),
            cell,
            generation,
            stale_refusal: std::sync::atomic::AtomicBool::new(false),
        }
    }

    /// Whether the context this reference was issued under is still in force.
    pub fn is_current(&self) -> bool {
        self.cell.current().generation == self.generation
    }
}

/// The endpoints and the source for them, from one snapshot.
///
/// This is the accessor hosts should use; see [`CopilotConnection`].
pub fn copilot_connection(
    manager: Arc<CredentialManager>,
    cell: Arc<CopilotContextCell>,
) -> CopilotConnection {
    let context = cell.current();
    let source = Arc::new(ContextScopedSource::pinned(
        manager,
        cell,
        context.generation,
    )) as Arc<dyn coda_llm::CredentialSource>;
    CopilotConnection { context, source }
}

#[async_trait]
impl coda_llm::CredentialSource for ContextScopedSource {
    async fn auth_headers(&self) -> Result<Option<Vec<(String, String)>>, coda_llm::LlmError> {
        use std::sync::atomic::Ordering;

        // Checked before the lookup, so a reference that is already stale
        // costs no store access, no refresh and no network call...
        if !self.is_current() {
            self.stale_refusal.store(true, Ordering::SeqCst);
            return Err(stale_reference());
        }
        self.stale_refusal.store(false, Ordering::SeqCst);
        let headers = self.inner.auth_headers().await?;
        // ...and again afterwards, because the lookup itself takes time: a
        // login that lands during it would otherwise have its brand-new token
        // handed back to this reference — whose client still points at the
        // previous deployment's inference host.
        if !self.is_current() {
            self.stale_refusal.store(true, Ordering::SeqCst);
            return Err(stale_reference());
        }
        Ok(headers)
    }

    fn last_failure_was_local(&self) -> bool {
        self.stale_refusal.load(std::sync::atomic::Ordering::SeqCst)
            || self.inner.last_failure_was_local()
    }
}

/// The refusal a reference issued under a replaced context returns.
fn stale_reference() -> coda_llm::LlmError {
    coda_llm::LlmError::Unauthorized(
        "the connected GitHub Copilot deployment changed; reconnect before sending another \
         request"
            .into(),
    )
}

/// A Copilot connection: the endpoints and the credential source that belong
/// together, taken from **one** snapshot.
///
/// Acquiring them separately is a race in itself — a login landing between the
/// two calls yields an old base URL paired with a source that considers itself
/// current, which is precisely the combination that sends a new account's
/// token to the previous tenant.
pub struct CopilotConnection {
    /// The endpoints to build the client with.
    pub context: Arc<CopilotContext>,
    /// The source to give that client. It stops authenticating when
    /// `context` is replaced.
    pub source: Arc<dyn coda_llm::CredentialSource>,
}

impl std::fmt::Debug for CopilotConnection {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CopilotConnection")
            .field("context", &self.context)
            .finish_non_exhaustive()
    }
}

/// A context update that belongs to a commit: applied inside the commit
/// section, undone if the commit fails.
pub struct PendingCopilotContext {
    cell: Arc<CopilotContextCell>,
    config: CopilotConfig,
    deployment: CopilotDeployment,
}

impl std::fmt::Debug for PendingCopilotContext {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PendingCopilotContext")
            .field("deployment", &self.deployment)
            .finish_non_exhaustive()
    }
}

impl PendingCopilotContext {
    pub fn new(
        cell: Arc<CopilotContextCell>,
        config: CopilotConfig,
        deployment: CopilotDeployment,
    ) -> Self {
        Self { cell, config, deployment }
    }

    pub(crate) fn publish(&self, credential: &Credential) -> Arc<CopilotContext> {
        self.cell.publish(self.config.clone(), self.deployment.clone(), credential)
    }

    pub(crate) fn restore(&self, previous: Arc<CopilotContext>) {
        self.cell.restore(previous);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::credential::CredentialKind;
    use crate::secret::Secret;

    fn credential(durable: &str) -> Credential {
        Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("copilot-token".into())),
            refresh_token: Some(Secret::new(durable.into())),
            api_key: None,
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        }
    }

    #[test]
    fn a_published_context_never_prints_its_binding() {
        let cell = CopilotContextCell::initial(
            CopilotConfig::default_public(),
            CopilotDeployment::Public,
            None,
        );
        cell.publish(
            CopilotConfig::default_public(),
            CopilotDeployment::Public,
            &credential("ghu_SECRET_DURABLE_TOKEN"),
        );
        let rendered = format!("{:?}", cell.current());
        assert!(!rendered.contains("ghu_SECRET_DURABLE_TOKEN"), "{rendered}");
    }

    #[test]
    fn a_refresh_of_the_same_login_keeps_matching_the_binding() {
        // A refresh replaces the short-lived token and keeps the durable one,
        // so the binding must not invalidate itself after one refresh.
        let first = credential("ghu_durable");
        let mut refreshed = first.clone();
        refreshed.access_token = Some(Secret::new("copilot-token-2".into()));
        assert_eq!(bind_to(&first), bind_to(&refreshed));
        assert_ne!(bind_to(&first), bind_to(&credential("ghu_other_login")));
    }

    #[test]
    fn restoring_a_context_still_advances_the_generation() {
        let cell = CopilotContextCell::initial(
            CopilotConfig::default_public(),
            CopilotDeployment::Public,
            None,
        );
        let previous = cell.publish(
            CopilotConfig::default_public(),
            CopilotDeployment::Public,
            &credential("ghu_a"),
        );
        let published = cell.current().generation;
        cell.restore(previous);
        assert!(
            cell.current().generation > published,
            "an in-flight refresh must still see the context move"
        );
        assert!(cell.current().bound.is_none(), "the restored context is the one it replaced");
    }
}

