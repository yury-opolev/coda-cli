//! [`AuthService`] — the one owner of the login/logout/status transaction.
//!
//! # What it owns, and what it refuses to own
//!
//! It owns: preparing a login, committing it, logging out, reporting status,
//! selecting a provider, and probing a connection. It owns exactly one
//! [`CredentialManager`] for the profile, because a fresh manager per call
//! would come with a fresh refresh gate and a fresh coordinator, which is how
//! a refresh resurrects a credential a logout just deleted.
//!
//! It refuses to own the engine. It never spawns, kills or repoints one; the
//! host does that, and the order matters:
//!
//! ```text
//! prepare_login  →  stop and await the engine you own  →  commit_login
//!                →  start a fresh engine  →  read provider/model back  →  verify
//! ```
//!
//! A logout deliberately leaves the host **disconnected**. It does not pick
//! another account, does not fall back to an ambient key, and does not start
//! anything.
//!
//! # Cancellation
//!
//! Before the commit, cancellation is free: `prepare_login` owns the loopback
//! listener and the device poller inside its own future, so dropping it closes
//! them and nothing has been written.
//!
//! Once a commit begins it must reach a terminal state, so
//! [`AuthService::commit_login`] starts it on its own task the moment it is
//! called. Dropping the returned handle does not abandon a half-written
//! profile; a host tearing its UI down calls
//! [`AuthService::await_pending_commit`] and waits for the outcome.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::Duration;

use coda_llm::LlmClient;

use crate::coordination::{CommitCoordinator, AUTH_COMMIT_KEY};
use crate::credential::Credential;
use crate::error::AuthError;
use crate::failure::AuthFailure;
use crate::manager::CredentialManager;
use crate::provider::api_key::{self, ApiKeyProvider};
use crate::provider::claude_ai::{ClaudeAiConfig, ClaudeAiProvider, ALL_OAUTH_SCOPES, DEFAULT_LOGIN_TIMEOUT};
use crate::provider::copilot::{
    resolve_copilot_config, CopilotDeployment, CopilotDeploymentChoice, CopilotProvider,
};
use crate::provider::AuthProvider;
use crate::secret::Secret;
use crate::service::copilot_context::{
    copilot_connection, ContextBoundCopilotProvider, CopilotConnection, CopilotContext,
    CopilotContextCell, PendingCopilotContext,
};
use crate::service::endpoint::{self, AnthropicEndpoint, EndpointError};
use crate::service::environment::{AuthEnvironment, ProcessEnvironment};
use crate::service::identity::ProviderIdentity;
use crate::service::login::{
    ApiKeySource, AuthEvidence, LoginRequest, LoginUi, PrepareFailure, PreparedCredential,
    PreparedLogin, UnverifiedPolicy,
};
use crate::service::selection::{select_in_context, Selection, SelectionContext, SelectionError};
use crate::service::settings::{AuthSettings, AuthSettingsPatch, AuthSettingsPort, InMemoryAuthSettings};
use crate::service::status::{
    selection_entries, AuthStatus, LogoutFailure, LogoutReport, ProviderStatus, StoredState,
};
use crate::service::transaction::{
    self, Baseline, CommitInterruption, CommitOutcome, CommitRequest, CommitStep,
};
use crate::service::verify::{
    verify_client, UnverifiedReason, VerificationOutcome, VerificationReport,
};
use crate::store::{AuthStorage, CredentialStore, ProfileCredentialStore};

/// The public OAuth client id Coda uses for Claude.ai. A well-known public
/// identifier, not a secret.
pub const CLAUDE_AI_CLIENT_ID: &str = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

/// The shared authentication service.
pub struct AuthService {
    store: Arc<ProfileCredentialStore>,
    coordinator: Arc<dyn CommitCoordinator>,
    settings: Arc<dyn AuthSettingsPort>,
    environment: Arc<dyn AuthEnvironment>,
    /// One manager for the whole profile: one refresh gate, one coordinator.
    manager: Arc<CredentialManager>,
    claude: ClaudeAiConfig,
    login_timeout: Duration,
    /// Set when the provider context could not be resolved (an unreadable
    /// settings port, or a saved Copilot deployment this build cannot turn
    /// into endpoints). The affected provider is then not registered at all,
    /// rather than registered against the public defaults.
    provider_context_error: Option<AuthFailure>,
    /// The Copilot endpoints in force, republished by every Copilot commit.
    ///
    /// The manager's provider reads this at refresh time, so the durable token
    /// of the account that is signed in now is exchanged at *its* tenant. See
    /// [`crate::service::copilot_context`].
    copilot_context: Arc<CopilotContextCell>,
    in_flight: Arc<AtomicUsize>,
    finished: Arc<tokio::sync::Notify>,
}

impl std::fmt::Debug for AuthService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AuthService")
            .field("login_timeout", &self.login_timeout)
            .field("provider_context_error", &self.provider_context_error)
            .finish_non_exhaustive()
    }
}

/// Builds an [`AuthService`].
pub struct AuthServiceBuilder {
    store: Arc<ProfileCredentialStore>,
    coordinator: Arc<dyn CommitCoordinator>,
    settings: Option<Arc<dyn AuthSettingsPort>>,
    environment: Option<Arc<dyn AuthEnvironment>>,
    claude: Option<ClaudeAiConfig>,
    login_timeout: Option<Duration>,
}

impl AuthServiceBuilder {
    /// The settings port the transaction commits `defaultProvider` and
    /// `githubEnterpriseDomain` through.
    pub fn with_settings(mut self, settings: Arc<dyn AuthSettingsPort>) -> Self {
        self.settings = Some(settings);
        self
    }

    /// The environment lookup used for the ambient API key and the
    /// `GH_COPILOT_*` overrides.
    pub fn with_environment(mut self, environment: Arc<dyn AuthEnvironment>) -> Self {
        self.environment = Some(environment);
        self
    }

    /// The Claude.ai OAuth endpoints (tests point these at a local socket).
    pub fn with_claude_config(mut self, claude: ClaudeAiConfig) -> Self {
        self.claude = Some(claude);
        self
    }

    /// How long a browser login may wait for its redirect.
    pub fn with_login_timeout(mut self, timeout: Duration) -> Self {
        self.login_timeout = Some(timeout);
        self
    }

    /// Build a service that can sign in.
    ///
    /// Fails when the provider context cannot be established: the settings
    /// port will not answer, or the saved Copilot deployment is not something
    /// this build can turn into endpoints. Neither may be papered over with
    /// the public defaults — that would send an enterprise user's device
    /// authorization, and every later token refresh, to github.com.
    ///
    /// Asynchronous because establishing that context reads the stored
    /// credential *together with* the configuration, under the coordination
    /// writers take. Sampling them separately is how a context ends up
    /// describing one account while a credential belongs to another.
    ///
    /// A host that needs to *report* the problem (or sign out) rather than
    /// sign in uses [`Self::build_degraded`].
    pub async fn build(self) -> Result<AuthService, AuthError> {
        let (service, context_error) = self.assemble().await;
        match context_error {
            Some(error) => Err(error),
            None => Ok(service),
        }
    }

    /// Build a service for reporting and signing out when the provider context
    /// is broken.
    ///
    /// The affected provider is deliberately **not registered**: it cannot be
    /// signed in to, and the manager cannot refresh its credential, because
    /// either would have to invent endpoints. The reason is recorded and
    /// surfaces in [`AuthService::status`].
    pub async fn build_degraded(self) -> AuthService {
        self.assemble().await.0
    }

    async fn assemble(self) -> (AuthService, Option<AuthError>) {
        let settings = self
            .settings
            .unwrap_or_else(|| Arc::new(InMemoryAuthSettings::new()) as Arc<dyn AuthSettingsPort>);
        let environment = self
            .environment
            .unwrap_or_else(|| Arc::new(ProcessEnvironment) as Arc<dyn AuthEnvironment>);
        let claude = self
            .claude
            .unwrap_or_else(|| ClaudeAiConfig::production(CLAUDE_AI_CLIENT_ID));

        let mut providers: Vec<Arc<dyn AuthProvider>> = vec![
            Arc::new(ApiKeyProvider),
            Arc::new(ClaudeAiProvider::new(claude.clone())),
        ];

        // The Copilot context: the endpoints *and* the credential they serve,
        // read as one, under the section every writer takes. A configuration
        // resolved here and a credential read a moment later could describe
        // two different accounts — and this context is what every later
        // refresh and every header is checked against.
        //
        // The provider is registered against the *cell*, not against a
        // captured configuration: a later login publishes the deployment it
        // actually authenticated to, and the refresh follows it.
        let (copilot_context, context_error) =
            resolve_initial_copilot_context(&self.store, self.coordinator.as_ref(), settings.as_ref(), environment.as_ref())
                .await;
        if let Some(cell) = &copilot_context {
            providers.push(Arc::new(ContextBoundCopilotProvider::new(Arc::clone(cell))));
        }
        let provider_context_error = context_error.as_ref().map(AuthFailure::classify);
        // A service whose Copilot context could not be resolved still needs a
        // cell so a *later* login can publish one; it is bound to nothing, so
        // nothing can be refreshed or presented through it.
        let copilot_context = copilot_context.unwrap_or_else(|| {
            CopilotContextCell::initial(
                crate::provider::copilot::CopilotConfig::default_public(),
                crate::provider::copilot::CopilotDeployment::Public,
                None,
            )
        });

        let manager = Arc::new(CredentialManager::with_coordinator(
            Arc::clone(&self.store) as Arc<dyn CredentialStore>,
            providers,
            Arc::clone(&self.coordinator),
        ));

        let service = AuthService {
            store: self.store,
            coordinator: self.coordinator,
            settings,
            environment,
            manager,
            claude,
            login_timeout: self.login_timeout.unwrap_or(DEFAULT_LOGIN_TIMEOUT),
            provider_context_error,
            copilot_context,
            in_flight: Arc::new(AtomicUsize::new(0)),
            finished: Arc::new(tokio::sync::Notify::new()),
        };
        (service, context_error)
    }
}

/// Reads the stored Copilot credential and resolves its endpoints as one
/// coherent snapshot, inside the profile's commit section.
///
/// The credential is read with the non-migrating read and validated against
/// the provider it is filed under, so nothing is bound to a blob that belongs
/// to a different provider — and no provider has seen it yet, which is the
/// point: binding after a `get_credential` would be too late, because that
/// call can already have refreshed at endpoints the credential does not
/// belong to.
pub(crate) async fn resolve_initial_copilot_context(
    store: &Arc<ProfileCredentialStore>,
    coordinator: &dyn CommitCoordinator,
    settings: &dyn AuthSettingsPort,
    environment: &dyn AuthEnvironment,
) -> (Option<Arc<CopilotContextCell>>, Option<AuthError>) {
    let section = match coordinator.begin(AUTH_COMMIT_KEY).await {
        Ok(section) => section,
        Err(error) => return (None, Some(error)),
    };

    let stored = match read_bindable_copilot_credential(store).await {
        Ok(credential) => credential,
        Err(error) => return (None, Some(error)),
    };
    let resolved = match resolve_copilot_context(settings, environment) {
        Ok(resolved) => resolved,
        Err(error) => return (None, Some(error)),
    };
    let cell = CopilotContextCell::initial(
        resolved.config,
        resolved.deployment,
        stored.as_ref(),
    );
    drop(section);
    (Some(cell), None)
}

/// The stored Copilot credential, or `None` when there is none.
///
/// A blob that does not parse, or that belongs to another provider, is an
/// error: binding to it would be binding to something we cannot identify, and
/// treating it as absent would bind to nothing while a credential is sitting
/// there.
async fn read_bindable_copilot_credential(
    store: &Arc<ProfileCredentialStore>,
) -> Result<Option<Credential>, AuthError> {
    let identity = ProviderIdentity::GithubCopilot;
    let Some(raw) = store.read_only(&identity.store_key()).await? else {
        return Ok(None);
    };
    let credential: Credential = serde_json::from_str(&raw)?;
    if credential.provider_id != identity.stored_id() {
        return Err(AuthError::CredentialProviderMismatch {
            expected: identity.stored_id().to_owned(),
            actual: crate::error::sanitize_key(&credential.provider_id),
        });
    }
    Ok(Some(credential))
}

/// Resolves the Copilot endpoints from the saved deployment and the
/// environment, propagating both failure modes.
///
/// A settings port that will not answer is a failure here, not an empty saved
/// domain: "no tenant saved" and "we could not find out" lead to different
/// endpoints, and only one of them is safe to authorize against.
fn resolve_copilot_context(
    settings: &dyn AuthSettingsPort,
    environment: &dyn AuthEnvironment,
) -> Result<crate::provider::copilot::ResolvedCopilotConfig, AuthError> {
    let saved = settings.load()?.github_enterprise_domain;
    resolve_copilot_config(None, saved.as_deref(), |key| environment.var(key))
}

impl AuthService {
    /// Start building a service over an already-resolved profile store.
    pub fn builder(
        store: Arc<ProfileCredentialStore>,
        coordinator: Arc<dyn CommitCoordinator>,
    ) -> AuthServiceBuilder {
        AuthServiceBuilder {
            store,
            coordinator,
            settings: None,
            environment: None,
            claude: None,
            login_timeout: None,
        }
    }

    /// The service for a resolved profile — the production constructor.
    ///
    /// Fails when the provider context cannot be established; see
    /// [`AuthServiceBuilder::build`]. A host that wants to report the problem
    /// instead of signing in uses
    /// [`AuthServiceBuilder::build_degraded`].
    pub async fn from_storage(
        storage: &AuthStorage,
        settings: Arc<dyn AuthSettingsPort>,
    ) -> Result<Self, AuthError> {
        Self::builder(Arc::clone(&storage.profile), Arc::clone(&storage.coordinator))
            .with_settings(settings)
            .build()
            .await
    }

    /// [`Self::from_storage`] in reporting mode: never fails, cannot sign in
    /// to a provider whose context is broken.
    pub async fn from_storage_degraded(
        storage: &AuthStorage,
        settings: Arc<dyn AuthSettingsPort>,
    ) -> Self {
        Self::builder(Arc::clone(&storage.profile), Arc::clone(&storage.coordinator))
            .with_settings(settings)
            .build_degraded()
            .await
    }

    /// The one credential manager for this profile.
    ///
    /// Hosts build their credential sources from this, so a token refreshed
    /// for a request and a login committed here go through the same gate.
    pub fn manager(&self) -> Arc<CredentialManager> {
        Arc::clone(&self.manager)
    }

    /// The Copilot endpoints currently in force — the ones the last committed
    /// login authenticated against, or the ones bound at construction.
    ///
    /// **Reporting only.** To build a client, use
    /// [`Self::copilot_connection`]: taking the context here and a source
    /// separately samples the cell twice, and a login landing in between
    /// yields the previous tenant's URL paired with a source that believes it
    /// is current.
    pub fn copilot_context(&self) -> Arc<CopilotContext> {
        self.copilot_context.current()
    }

    /// The endpoints and the credential source for them, from **one**
    /// snapshot.
    ///
    /// Build the client from `context` and give it `source`. The source stops
    /// authenticating the moment a login replaces that context, so a client
    /// whose base URL belongs to the previous tenant fails closed instead of
    /// being handed the new account's token; the host's answer is to take a
    /// fresh connection and rebuild.
    pub fn copilot_connection(&self) -> CopilotConnection {
        copilot_connection(Arc::clone(&self.manager), Arc::clone(&self.copilot_context))
    }

    /// The store this service commits to.
    pub fn store(&self) -> Arc<dyn CredentialStore> {
        Arc::clone(&self.store) as Arc<dyn CredentialStore>
    }

    /// A **fresh** credential source for the exported `ANTHROPIC_API_KEY`, or
    /// `None` when this process has no such variable.
    ///
    /// This is what a host checks an environment selection with: there is no
    /// stored credential to read, and probing through the store would report
    /// "no credential on this machine" for a login that deliberately saved
    /// none.
    ///
    /// A new source every call, on purpose:
    /// [`CredentialSource::last_failure_was_local`][coda_llm::CredentialSource::last_failure_was_local]
    /// describes the last attempt the source it is read from made, so a probe
    /// must not share one with anything else issuing requests.
    pub fn environment_api_key_source(&self) -> Option<Arc<dyn coda_llm::CredentialSource>> {
        self.environment.var(api_key::ENV_VAR)?;
        Some(Arc::new(crate::credential_source::EnvironmentApiKeySource::new(Arc::clone(
            &self.environment,
        ))) as Arc<dyn coda_llm::CredentialSource>)
    }

    /// Where this process sends Anthropic **API-key** requests.
    ///
    /// One resolution, through the same environment port every other decision
    /// uses, so the pre-commit probe, the post-commit connection check and the
    /// engine that follows cannot disagree about which host receives the key.
    /// An invalid override is an error here, never a silent fallback to
    /// Anthropic's own host.
    ///
    /// Deliberately scoped to the console-key identity: a Claude.ai
    /// subscription and GitHub Copilot keep their own endpoint configuration.
    pub fn anthropic_endpoint(&self) -> Result<AnthropicEndpoint, EndpointError> {
        endpoint::resolve_in(None, self.environment.as_ref())
    }

    /// Where a Copilot sign-in that made `choice` would actually authorize.
    ///
    /// The same resolution [`Self::prepare_login`] performs, exposed so a host
    /// can *disclose the destination before the device code is requested*
    /// rather than inferring one from its own look at the environment. Two
    /// independent readings are how a screen ends up naming public github.com
    /// while the request goes to a tenant an exported variable selected.
    ///
    /// Reads the saved deployment and this service's environment port; sends
    /// nothing and writes nothing.
    pub fn resolve_copilot_deployment(
        &self,
        choice: Option<&CopilotDeploymentChoice>,
    ) -> Result<crate::provider::copilot::ResolvedCopilotConfig, AuthError> {
        let saved = self.settings.load()?.github_enterprise_domain;
        resolve_copilot_config(choice, saved.as_deref(), |key| self.environment.var(key))
    }

    /// The authorization endpoint a Claude.ai subscription login opens.
    ///
    /// Exposed for disclosure only. Hosts show the **host** of this, never the
    /// URL: the authorize URL carries the CSRF state and the redirect the
    /// browser will be sent to.
    pub fn claude_authorize_url(&self) -> &str {
        &self.claude.authorize_url
    }

    // ── Status ───────────────────────────────────────────────────────────────

    /// What the profile holds. Performs no refresh, no network call and no
    /// migration; see [`crate::service::status`].
    ///
    /// The per-entry facts and the *selection* are separate answers on
    /// purpose. A slot that cannot be read is still reported as unreadable
    /// (that is what a user needs to see), while the selection refuses: an
    /// unreadable credential or an unreadable settings file means the account
    /// this machine is signed in as is not knowable, and neither an ambient
    /// key nor the credential that happens to parse may stand in for it.
    pub async fn status(&self) -> Result<AuthStatus, AuthFailure> {
        let providers = self.provider_states().await;
        let (settings, settings_error) = match self.settings.load() {
            Ok(settings) => (settings, None),
            Err(error) => (AuthSettings::default(), Some(AuthFailure::classify(&error))),
        };
        let entries = selection_entries(&providers);
        let saved_default = match settings_error {
            Some(failure) => Err(failure),
            None => Ok(settings.default_provider.as_deref()),
        };
        let environment_api_key = self.ambient_api_key();
        let selection = select_in_context(SelectionContext {
            explicit: None,
            saved_default,
            entries: &entries,
            ambient_api_key: environment_api_key,
        });

        Ok(AuthStatus {
            providers,
            selection,
            // Reported as read; `settings_error` says when it could not be.
            saved_default: settings.default_provider,
            github_enterprise_domain: settings.github_enterprise_domain,
            environment_api_key,
            anthropic_endpoint: self.anthropic_endpoint(),
            settings_error,
            provider_context_error: self.provider_context_error,
        })
    }

    /// The provider this profile resolves to, with no explicit override.
    pub async fn selected_provider(&self) -> Result<Selection, SelectionError> {
        self.select(None).await
    }

    /// The provider for an explicit request (a `--provider` flag, a command
    /// argument). Fails closed; see [`crate::service::selection`].
    ///
    /// Built from the same context as [`Self::status`], so an unreadable
    /// credential or settings file is `Unavailable` here too rather than
    /// becoming "you are not signed in".
    pub async fn select(&self, explicit: Option<&str>) -> Result<Selection, SelectionError> {
        let providers = self.provider_states().await;
        let entries = selection_entries(&providers);
        let saved_default = match self.settings.load() {
            Ok(settings) => Ok(settings.default_provider),
            Err(error) => Err(AuthFailure::classify(&error)),
        };
        select_in_context(SelectionContext {
            explicit,
            saved_default: saved_default.as_ref().map(|saved| saved.as_deref()).map_err(|e| *e),
            entries: &entries,
            ambient_api_key: self.ambient_api_key(),
        })
    }

    // ── Login ────────────────────────────────────────────────────────────────

    /// Authenticate, without touching the profile.
    ///
    /// Dropping the returned future cancels the login: the loopback listener
    /// and the device poller it owns close with it, and nothing was written.
    pub async fn prepare_login(
        &self,
        request: LoginRequest,
        ui: &dyn LoginUi,
    ) -> Result<PreparedLogin, PrepareFailure> {
        self.prepare_login_with_probe(request, ui, None).await
    }

    /// [`Self::prepare_login`] with an explicit client for the pre-commit
    /// credential check (tests, and hosts that already built one).
    pub async fn prepare_login_with_probe(
        &self,
        request: LoginRequest,
        ui: &dyn LoginUi,
        probe: Option<Arc<dyn LlmClient>>,
    ) -> Result<PreparedLogin, PrepareFailure> {
        let baseline = Baseline::capture(&self.store)
            .await
            .map_err(|error| PrepareFailure::from_error(&error))?;
        // An environment login displaces the stored console key as well: the
        // key it selects lives in the process, so leaving a saved copy behind
        // would let a later read answer with the credential this login was
        // meant to stop using. That makes it a displacement to disclose — and
        // to check before, not after.
        let environment_only = request.identity() == ProviderIdentity::AnthropicApiKey
            && request.api_key_source == ApiKeySource::Environment;
        let replaces = self
            .stored_identities()
            .await
            .map_err(|failure| PrepareFailure::Failed(failure))?
            .into_iter()
            .filter(|identity| environment_only || *identity != request.identity())
            .collect::<Vec<_>>();

        let (credential, evidence, deployment, settings, copilot) = match request.identity() {
            ProviderIdentity::AnthropicApiKey => {
                self.prepare_api_key(&request, ui, probe, &replaces).await?
            }
            ProviderIdentity::ClaudeAi => self.prepare_claude(ui).await?,
            ProviderIdentity::GithubCopilot => self.prepare_copilot(&request, ui).await?,
        };

        Ok(PreparedLogin {
            identity: request.identity(),
            credential,
            settings,
            baseline,
            evidence,
            replaces,
            deployment,
            copilot,
        })
    }

    /// Commit a prepared login.
    ///
    /// The transaction starts immediately, on its own task: see the module
    /// docs on cancellation. Await the returned handle for the outcome, or
    /// [`Self::await_pending_commit`] during teardown.
    ///
    /// The in-flight marker is an RAII guard *moved into the task*, so it is
    /// released when the transaction ends — including when the task panics or
    /// is aborted. A host awaiting teardown can therefore never be left
    /// waiting on a commit that has already stopped existing.
    pub fn commit_login(&self, prepared: PreparedLogin) -> CommitHandle {
        let store = Arc::clone(&self.store);
        let coordinator = Arc::clone(&self.coordinator);
        let settings = Arc::clone(&self.settings);

        let request = match prepared.credential {
            PreparedCredential::Stored(credential) => {
                CommitRequest::new(prepared.identity, credential)
            }
            // Nothing to write: the key stays in the environment, and the
            // commit records the choice and clears what it displaces. This is
            // also why no Copilot context can ride along — the constructor
            // takes no credential for one to bind to, and the transaction
            // refuses a request that carries one anyway.
            PreparedCredential::Environment => CommitRequest::environment_api_key(),
        }
        .with_settings(prepared.settings)
        .with_baseline(prepared.baseline);
        // A Copilot login also carries the deployment it authenticated
        // against; the commit publishes it so the manager refreshes the new
        // durable token at the endpoints it belongs to, not at whichever
        // tenant this process resolved when it started.
        let request = match (prepared.copilot, &self.copilot_context) {
            (Some(resolved), cell) => request.with_context(PendingCopilotContext::new(
                Arc::clone(cell),
                resolved.config,
                resolved.deployment,
            )),
            (None, _) => request,
        };

        let guard = InFlight::enter(Arc::clone(&self.in_flight), Arc::clone(&self.finished));

        // Spawning needs a runtime. Without one nothing has run, and that is
        // reported rather than silently dropped — and the guard is released
        // here, so teardown does not wait for a task that never existed.
        let Ok(handle) = tokio::runtime::Handle::try_current() else {
            drop(guard);
            return CommitHandle::ready(CommitOutcome::Indeterminate {
                cause: CommitInterruption::NotStarted,
            });
        };

        let task = handle.spawn(async move {
            // Held for exactly as long as the transaction runs.
            let _in_flight = guard;
            transaction::commit(&store, &coordinator, settings.as_ref(), request).await
        });
        CommitHandle::task(task)
    }

    /// Wait until no commit is running.
    ///
    /// A host closing down calls this so its teardown observes a coherent
    /// profile instead of racing a write it can no longer see.
    pub async fn await_pending_commit(&self) {
        loop {
            let notified = self.finished.notified();
            if self.in_flight.load(Ordering::SeqCst) == 0 {
                return;
            }
            notified.await;
        }
    }

    // ── Logout ───────────────────────────────────────────────────────────────

    /// Remove a stored credential (or every stored credential when `identity`
    /// is `None`) and clear a saved choice that named it.
    ///
    /// This is a compound transaction, like a login: the credentials and the
    /// saved choice go together, because a profile that is signed out while
    /// `defaultProvider` still names the deleted account is not a coherent
    /// state. A failure at any step is undone and reported through
    /// [`LogoutFailure`]; a rollback that itself fails says so, rather than
    /// returning a plain error while a credential has actually been lost.
    ///
    /// The host is left deliberately disconnected: nothing else is selected,
    /// and no engine is started. See [`LogoutReport`] for what the user is
    /// told.
    pub async fn logout(
        &self,
        identity: Option<ProviderIdentity>,
    ) -> Result<LogoutReport, LogoutFailure> {
        let _section = self.coordinator.begin(AUTH_COMMIT_KEY).await.map_err(|error| {
            LogoutFailure::Failed {
                step: CommitStep::EnterSection,
                failure: AuthFailure::classify(&error),
                rolled_back: false,
            }
        })?;

        let targets: Vec<ProviderIdentity> = match identity {
            Some(identity) => vec![identity],
            None => ProviderIdentity::ALL.to_vec(),
        };

        // Snapshot first: every later step can be undone from this.
        let mut previous = Vec::new();
        for identity in &targets {
            let key = identity.store_key();
            let blob = self.store.read_only(&key).await.map_err(|error| {
                LogoutFailure::Failed {
                    step: CommitStep::Snapshot,
                    failure: AuthFailure::classify(&error),
                    rolled_back: false,
                }
            })?;
            let retired = self.store.is_retired(&key).await.map_err(|error| {
                LogoutFailure::Failed {
                    step: CommitStep::Snapshot,
                    failure: AuthFailure::classify(&error),
                    rolled_back: false,
                }
            })?;
            previous.push((*identity, blob, retired));
        }

        let settings_before = self.settings.load().map_err(|error| LogoutFailure::Failed {
            step: CommitStep::ReadSettings,
            failure: AuthFailure::classify(&error),
            rolled_back: false,
        })?;

        let mut removed = Vec::new();
        for (identity, blob, _) in &previous {
            if blob.is_none() {
                continue;
            }
            if let Err(error) = self.store.delete(&identity.store_key()).await {
                return Err(self
                    .undo_logout(&previous, CommitStep::RemoveDisplaced(*identity), &error)
                    .await);
            }
            removed.push(*identity);
        }

        let clears_default = settings_before
            .default_provider
            .as_deref()
            .and_then(ProviderIdentity::parse)
            .map(|saved| targets.contains(&saved))
            .unwrap_or(false);
        if clears_default {
            let patch = AuthSettingsPatch::empty().with_default_provider(None);
            if let Err(error) = self.settings.apply(&patch) {
                return Err(self
                    .undo_logout(&previous, CommitStep::ApplySettings, &error)
                    .await);
            }
        }

        Ok(LogoutReport {
            removed,
            cleared_default_provider: clears_default,
            environment_api_key_still_available: self.ambient_api_key(),
        })
    }

    // ── Verification ─────────────────────────────────────────────────────────

    /// Probe a live client with an uncached model listing.
    ///
    /// Never runs a completion, never writes to a session. See
    /// [`crate::service::verify`] for what a failure does and does not mean.
    pub async fn verify(&self, client: &dyn LlmClient) -> VerificationReport {
        verify_client(client).await
    }

    /// [`Self::verify`] with the credential source the client authenticates
    /// with, so a local store failure is not reported as a provider refusal.
    pub async fn verify_with_source(
        &self,
        client: &dyn LlmClient,
        source: &dyn coda_llm::CredentialSource,
    ) -> VerificationReport {
        crate::service::verify::verify_client_with_source(client, Some(source)).await
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    fn ambient_api_key(&self) -> bool {
        self.environment.var(api_key::ENV_VAR).is_some()
    }

    /// Every identity's slot, read once, with per-entry faults preserved.
    async fn provider_states(&self) -> Vec<ProviderStatus> {
        crate::service::status::read_provider_states(self.store.as_ref()).await
    }

    /// The identities this login would displace: every *readable* stored
    /// credential that is not the one being signed in to.
    ///
    /// An unreadable slot is a refusal, not a displacement: preparing a login
    /// that would delete something we cannot even read is how a recoverable
    /// credential is lost.
    async fn stored_identities(&self) -> Result<Vec<ProviderIdentity>, AuthFailure> {
        let mut stored = Vec::new();
        for status in self.provider_states().await {
            match status.state {
                StoredState::Present(_) => stored.push(status.identity),
                StoredState::Absent => {}
                StoredState::Unreadable(failure) => return Err(failure),
            }
        }
        Ok(stored)
    }

    /// Undo a logout, reporting the first restoration failure rather than
    /// pretending the profile is intact.
    ///
    /// Only credentials and their markers need undoing: the settings write is
    /// the last step and is atomic, so a failure there changed nothing.
    async fn undo_logout(
        &self,
        previous: &[(ProviderIdentity, Option<String>, bool)],
        failed_step: CommitStep,
        error: &AuthError,
    ) -> LogoutFailure {
        let failure = AuthFailure::classify(error);

        for (identity, blob, retired) in previous {
            let key = identity.store_key();
            if let Err(restore_error) = self.store.restore_primary(&key, blob.as_deref()).await {
                return LogoutFailure::RestorationFailed {
                    failed_step,
                    failure,
                    restore_step: CommitStep::RestoreCredential(*identity),
                    restore_failure: AuthFailure::classify(&restore_error),
                };
            }
            if let Err(restore_error) = self.store.set_retired(&key, *retired).await {
                return LogoutFailure::RestorationFailed {
                    failed_step,
                    failure,
                    restore_step: CommitStep::RestoreRetirement(*identity),
                    restore_failure: AuthFailure::classify(&restore_error),
                };
            }
        }

        LogoutFailure::Failed { step: failed_step, failure, rolled_back: true }
    }

    fn settings_patch_for(&self, identity: ProviderIdentity) -> AuthSettingsPatch {
        AuthSettingsPatch::empty().with_default_provider(Some(identity.engine_id().to_owned()))
    }

    async fn prepare_api_key(
        &self,
        request: &LoginRequest,
        ui: &dyn LoginUi,
        probe: Option<Arc<dyn LlmClient>>,
        replaces: &[ProviderIdentity],
    ) -> Result<PreparedResult, PrepareFailure> {
        // Resolved first, before a key is prompted for or read out of the
        // environment: if this process cannot say where an API-key request
        // goes, it must not obtain a key at all — not to probe with, not to
        // store. A refusal here has touched nothing.
        let endpoint = self
            .anthropic_endpoint()
            .map_err(|reason| PrepareFailure::EndpointRejected { reason })?;

        let entered: Secret<String> = match request.api_key_source {
            ApiKeySource::Prompt => ui.api_key().await.map_err(|e| PrepareFailure::from_error(&e))?,
            ApiKeySource::Environment => match self.environment.var(api_key::ENV_VAR) {
                Some(value) => Secret::new(value),
                None => {
                    return Err(PrepareFailure::EnvironmentKeyMissing);
                }
            },
        };

        // For an environment login this credential exists for the check only.
        // It is never returned, so nothing downstream — the commit, a log, a
        // `Debug` — has a copy of the key to write.
        let credential = ApiKeyProvider::prepare_credential(entered.expose())
            .map_err(|error| PrepareFailure::from_error(&error))?;

        // Preparing a key proves nothing. Prove it before this login is
        // allowed to remove an account that currently works: `without_validation`
        // is a convenience for a first sign-in, not a way to trade a working
        // connection for an unchecked one. For an environment login `replaces`
        // includes the stored console key, so that key is not displaced
        // unchecked either.
        let must_validate = request.validate || !replaces.is_empty();
        let evidence = if must_validate {
            self.validate_api_key(&credential, probe, &endpoint, request.unverified, replaces)
                .await?
        } else {
            AuthEvidence::None
        };

        let prepared = match request.api_key_source {
            ApiKeySource::Prompt => PreparedCredential::Stored(credential),
            ApiKeySource::Environment => {
                drop(credential);
                PreparedCredential::Environment
            }
        };

        Ok((
            prepared,
            evidence,
            None,
            self.settings_patch_for(ProviderIdentity::AnthropicApiKey),
            None,
        ))
    }

    async fn validate_api_key(
        &self,
        credential: &Credential,
        probe: Option<Arc<dyn LlmClient>>,
        endpoint: &AnthropicEndpoint,
        policy: UnverifiedPolicy,
        replaces: &[ProviderIdentity],
    ) -> Result<AuthEvidence, PrepareFailure> {
        let displaces_a_working_account = !replaces.is_empty();
        let client: Arc<dyn LlmClient> = match probe {
            Some(client) => client,
            None => {
                let key = credential
                    .api_key
                    .as_ref()
                    .map(|secret| secret.expose().clone())
                    .unwrap_or_default();
                // The resolved endpoint, not the client's built-in default:
                // the key this probe proves has to be checked against the same
                // host the engine will spend it at.
                match coda_llm::anthropic::AnthropicClient::new(
                    coda_llm::anthropic::AnthropicConfig::api_key(key)
                        .with_base_url(endpoint.base_url().to_owned()),
                ) {
                    Ok(client) => Arc::new(client) as Arc<dyn LlmClient>,
                    Err(_) => {
                        return self.unverified_or_error(
                            policy,
                            AuthFailure::Network,
                            displaces_a_working_account,
                        )
                    }
                }
            }
        };

        let report = verify_client(client.as_ref()).await;
        match report.outcome {
            VerificationOutcome::Verified { .. } => Ok(AuthEvidence::ProbeSucceeded),
            // A confirmed refusal: the working credential stays, and this
            // login stops here. The status is the one the provider actually
            // answered with — never a fabricated 401, which would claim a
            // response nobody received.
            VerificationOutcome::Rejected { status } => {
                Err(PrepareFailure::Rejected(match status {
                    Some(status) => AuthFailure::OAuthRejected { status },
                    None => AuthFailure::InvalidInput,
                }))
            }
            // Could not check. That is not the same as a rejection, and the
            // reason it could not be checked is carried through rather than
            // being reported as a network fault it may not be.
            VerificationOutcome::Unverified { reason } => self.unverified_or_error(
                policy,
                unverifiable_failure(reason),
                displaces_a_working_account,
            ),
        }
    }

    /// Whether an unchecked credential may be prepared anyway.
    ///
    /// Only on a *distinct, explicit* decision by the host — and even then the
    /// evidence stays [`AuthEvidence::None`], so nothing downstream can read
    /// the bypass as a successful authentication.
    fn unverified_or_error(
        &self,
        policy: UnverifiedPolicy,
        reason: AuthFailure,
        displaces_a_working_account: bool,
    ) -> Result<AuthEvidence, PrepareFailure> {
        match policy {
            UnverifiedPolicy::AllowedByHost => Ok(AuthEvidence::None),
            UnverifiedPolicy::Refuse => Err(PrepareFailure::ValidationUnavailable {
                reason,
                displaces_a_working_account,
            }),
        }
    }

    async fn prepare_claude(&self, ui: &dyn LoginUi) -> Result<PreparedResult, PrepareFailure> {
        let provider = ClaudeAiProvider::new(self.claude.clone());
        let flow = provider
            .begin_login(ALL_OAUTH_SCOPES)
            .await
            .map_err(|error| PrepareFailure::from_error(&error))?;

        // The host opens the URL. It lives for this login only: it must not be
        // logged, replayed or copied anywhere durable.
        if let Err(error) = ui.authorization_url(&flow.authorize_url).await {
            flow.cancel();
            return Err(PrepareFailure::from_error(&error));
        }

        // The flow owns the loopback listener until this await resolves;
        // dropping this future closes it.
        let credential = flow
            .wait_for_credential(self.login_timeout)
            .await
            .map_err(|error| PrepareFailure::from_error(&error))?;

        Ok((
            PreparedCredential::Stored(credential),
            // The provider exchanged a token: authentication is proven, even
            // if a later model probe cannot confirm entitlement.
            AuthEvidence::TokenExchange,
            None,
            self.settings_patch_for(ProviderIdentity::ClaudeAi),
            None,
        ))
    }

    async fn prepare_copilot(
        &self,
        request: &LoginRequest,
        ui: &dyn LoginUi,
    ) -> Result<PreparedResult, PrepareFailure> {
        // Refuse before anything is authorized. A device-code flow started
        // against guessed endpoints sends the user's authorization — and the
        // token that follows — to a host they did not configure.
        if let Some(failure) = self.provider_context_error {
            return Err(PrepareFailure::Failed(failure));
        }
        let saved = self
            .settings
            .load()
            .map_err(|error| PrepareFailure::from_error(&error))?
            .github_enterprise_domain;
        let choice = match &request.deployment {
            Some(choice) => Some(choice.clone()),
            None => ui
                .copilot_deployment(saved.as_deref())
                .await
                .map_err(|error| PrepareFailure::from_error(&error))?,
        };

        let resolved = resolve_copilot_config(choice.as_ref(), saved.as_deref(), |key| {
            self.environment.var(key)
        })
        .map_err(|error| PrepareFailure::from_error(&error))?;

        let provider = CopilotProvider::new(resolved.config.clone());
        let credential = provider
            .login_with_device_code(|prompt| async move { ui.device_code(prompt).await })
            .await
            .map_err(|error| PrepareFailure::from_error(&error))?;

        // Record the tenant this login actually authenticated against, taken
        // from the resolved endpoints rather than from what was asked for.
        let domain = match &resolved.deployment {
            CopilotDeployment::Enterprise { domain } => Some(domain.clone()),
            CopilotDeployment::Public | CopilotDeployment::Custom { .. } => None,
        };
        let mut settings = self.settings_patch_for(ProviderIdentity::GithubCopilot);
        settings = match (&choice, &domain) {
            // An explicit public choice must also clear a saved tenant, or the
            // engine would keep routing inference to it.
            (Some(CopilotDeploymentChoice::Public), _) => {
                settings.with_github_enterprise_domain(None)
            }
            (_, Some(domain)) => settings.with_github_enterprise_domain(Some(domain.clone())),
            // No explicit choice and no tenant: leave the saved value alone.
            _ => settings,
        };

        // The *resolved* configuration travels with the login. Re-deriving it
        // later would let an exported `GH_COPILOT_ENTERPRISE_DOMAIN` overrule
        // the deployment the user just chose.
        Ok((
            PreparedCredential::Stored(credential),
            AuthEvidence::TokenExchange,
            domain,
            settings,
            Some(resolved),
        ))
    }
}

/// The safe classification for a credential that could not be *checked*.
///
/// None of these is a refusal: the closest honest reading of each is what the
/// probe could not establish, not an answer the provider gave.
fn unverifiable_failure(reason: UnverifiedReason) -> AuthFailure {
    match reason {
        UnverifiedReason::Unreachable => AuthFailure::Network,
        // The store could not produce a credential to check with.
        UnverifiedReason::CredentialUnavailable => AuthFailure::NoCredential,
        // The provider answered, but not about the identity: an entitlement
        // answer, an unrelated error, or an empty list. `CannotRefresh` is the
        // closed classification that says "this credential could not be put to
        // use" without claiming it was refused.
        UnverifiedReason::Forbidden
        | UnverifiedReason::ProviderError { .. }
        | UnverifiedReason::NoModelsListed => AuthFailure::CannotRefresh,
    }
}

type PreparedResult = (
    PreparedCredential,
    AuthEvidence,
    Option<String>,
    AuthSettingsPatch,
    Option<crate::provider::copilot::ResolvedCopilotConfig>,
);

/// Marks a commit as running for as long as it lives.
///
/// A plain increment/decrement pair around the transaction is not enough: if
/// the task panics or is aborted, the decrement never runs and every later
/// `await_pending_commit` waits forever. `Drop` runs on an unwind, so this
/// does.
struct InFlight {
    count: Arc<AtomicUsize>,
    finished: Arc<tokio::sync::Notify>,
}

impl InFlight {
    fn enter(count: Arc<AtomicUsize>, finished: Arc<tokio::sync::Notify>) -> Self {
        count.fetch_add(1, Ordering::SeqCst);
        Self { count, finished }
    }
}

impl Drop for InFlight {
    fn drop(&mut self) {
        self.count.fetch_sub(1, Ordering::SeqCst);
        self.finished.notify_waiters();
    }
}

/// A running commit.
///
/// The transaction is already in flight when this is handed back; awaiting it
/// yields the outcome, and dropping it does not stop it.
pub struct CommitHandle {
    inner: CommitHandleInner,
}

enum CommitHandleInner {
    Running(tokio::task::JoinHandle<CommitOutcome>),
    /// An outcome decided before any task existed; taken on first poll.
    Ready(Option<CommitOutcome>),
}

impl CommitHandle {
    fn task(task: tokio::task::JoinHandle<CommitOutcome>) -> Self {
        Self { inner: CommitHandleInner::Running(task) }
    }

    fn ready(outcome: CommitOutcome) -> Self {
        Self { inner: CommitHandleInner::Ready(Some(outcome)) }
    }
}

impl std::future::Future for CommitHandle {
    type Output = CommitOutcome;

    fn poll(
        mut self: std::pin::Pin<&mut Self>,
        cx: &mut std::task::Context<'_>,
    ) -> std::task::Poll<Self::Output> {
        match &mut self.inner {
            CommitHandleInner::Ready(outcome) => std::task::Poll::Ready(
                outcome.take().expect("a ready commit handle is polled once"),
            ),
            CommitHandleInner::Running(task) => match std::pin::Pin::new(task).poll(cx) {
                std::task::Poll::Pending => std::task::Poll::Pending,
                std::task::Poll::Ready(Ok(outcome)) => std::task::Poll::Ready(outcome),
                // The task carrying the transaction died. We did not observe
                // it finish and we did not observe a rollback, so neither a
                // clean failure nor an intact profile may be claimed.
                std::task::Poll::Ready(Err(_)) => {
                    std::task::Poll::Ready(CommitOutcome::Indeterminate {
                        cause: CommitInterruption::TaskFailed,
                    })
                }
            },
        }
    }
}

