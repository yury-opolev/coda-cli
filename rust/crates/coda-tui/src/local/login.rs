//! The sign-in flow itself, without a terminal, an engine or an application.
//!
//! # Why this is not part of `app::auth`
//!
//! There are two hosts for exactly one flow. The running application signs in
//! from `/login`, and the launcher signs in *before an engine exists at all* —
//! a first run, or a launch whose explicitly-selected provider has no
//! credential. The second host has no `App`, no `Connection` and nothing to
//! restart, and inventing a dead one so the wizard could be reached would be a
//! lie the rest of the code would then have to keep believing.
//!
//! So the parts that are the same live here: the challenge/preparation
//! plumbing, the [`LoginUi`] the terminal implements, the uncancellable commit
//! task, and the launch a committed account implies. What differs — stopping
//! and awaiting a running engine, booting a replacement, resuming a session —
//! stays with the host that owns those things.
//!
//! # Two rules this module exists to keep
//!
//! * **Nothing slow runs on the caller's thread.** Preparation, the commit and
//!   the engine shutdown that must precede it are tasks; the host learns what
//!   happened through [`FlowEvent`]. A host that awaited a commit inline would
//!   stop drawing and stop accepting `Ctrl+C` for as long as a credential
//!   transaction takes.
//! * **A retracted flow can never commit.** Every event carries the
//!   `generation` of the flow that produced it, and cancelling bumps it. A
//!   preparation that finishes after the user pressed Escape is dropped rather
//!   than being adopted by whatever flow started next.
//!
//! A commit, once started, is never cancelled: there is no half-written
//! credential transaction that a later run could reason about.

use std::future::Future;
use std::sync::Arc;
use std::sync::Mutex;

use async_trait::async_trait;
use coda_auth::error::AuthError;
use coda_auth::provider::copilot::CopilotDeploymentChoice;
use coda_auth::provider::DeviceCodePrompt;
use coda_auth::service::{
    AuthService, CommitOutcome, LoginRequest, LoginUi, LogoutFailure, LogoutReport, PrepareFailure,
    PreparedLogin, ProviderIdentity, StoredState,
};
use coda_auth::Secret;
use coda_boot::auth_cli::{
    render_anthropic_endpoint, render_authorization_host, render_deployment_of, render_replacement,
    sanitize,
};
use coda_client::EngineCommand;
use tokio::sync::{mpsc, oneshot};

/// The environment variables a per-provider launch must own outright.
///
/// Inherited values for any of these silently overrule the account the user
/// just chose: a stale `CODA_SERVE_PROVIDER` reconnects the old one, a
/// `CODA_SERVE_API_KEY` bypasses the store entirely, and a `CODA_SERVE_MODEL`
/// names a model that belongs to a provider that is no longer connected.
const OWNED_ENGINE_ENV: &[&str] = &[
    "CODA_SERVE_PROVIDER",
    "CODA_SERVE_MODEL",
    "CODA_SERVE_API_KEY",
    "CODA_SERVE_ENDPOINT",
];

/// The Copilot tenant variable, which is owned only by a Copilot login.
pub(crate) const COPILOT_TENANT_ENV: &str = "GH_COPILOT_ENTERPRISE_DOMAIN";

/// Command-line options that name a credential or a provider-specific model.
///
/// Dropped when a login re-launches the engine: they were the *previous*
/// account's instructions, and `--endpoint` is only meaningful next to the
/// `--api-key` it overrides.
const REPLACED_ARGS: &[&str] = &["--provider", "--model", "--api-key", "--endpoint"];

/// What a login is signing in to, kept for the wording after the commit.
#[derive(Debug, Clone)]
pub(crate) struct Target {
    pub(crate) identity: ProviderIdentity,
    pub(crate) deployment: Option<CopilotDeploymentChoice>,
    pub(crate) environment_only: bool,
}

// ── Events ───────────────────────────────────────────────────────────────────

/// Something a flow task has to tell its host.
///
/// Every variant carries the `generation` of the flow that produced it. A host
/// compares it with the flow's current generation and drops anything older:
/// that is what stops a preparation the user cancelled from committing, and
/// stops a commit belonging to an abandoned attempt from being adopted by the
/// next one.
pub(crate) enum FlowEvent {
    /// The provider is asking for something. Replaces what the challenge
    /// surface shows; never appended to anything durable.
    Challenge { generation: u64, title: String, lines: Vec<String> },
    /// The preparation finished. `None` means it was cancelled, in which case
    /// nothing was written and no engine was stopped.
    Prepared {
        generation: u64,
        result: Option<Box<Result<PreparedLogin, PrepareFailure>>>,
    },
    /// The credential transaction finished. It was never cancellable.
    Committed { generation: u64, outcome: Box<CommitOutcome> },
    /// A sign-out finished, after the engine it owned had gone.
    LoggedOut {
        generation: u64,
        report: Box<Result<LogoutReport, LogoutFailure>>,
    },
}

impl FlowEvent {
    pub(crate) fn generation(&self) -> u64 {
        match self {
            Self::Challenge { generation, .. }
            | Self::Prepared { generation, .. }
            | Self::Committed { generation, .. }
            | Self::LoggedOut { generation, .. } => *generation,
        }
    }
}

impl std::fmt::Debug for FlowEvent {
    /// Never prints a challenge. The `lines` of a challenge carry an
    /// authorization URL or a device code, and a `Debug` is exactly the kind
    /// of thing that ends up in a log.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Challenge { generation, .. } => write!(f, "Challenge({generation}, [REDACTED])"),
            Self::Prepared { generation, result } => write!(
                f,
                "Prepared({generation}, {})",
                match result {
                    None => "cancelled",
                    Some(result) if result.is_ok() => "ok",
                    Some(_) => "failed",
                }
            ),
            Self::Committed { generation, outcome } => write!(
                f,
                "Committed({generation}, {})",
                match outcome.as_ref() {
                    CommitOutcome::Committed { .. } => "committed",
                    _ => "not committed",
                }
            ),
            Self::LoggedOut { generation, report } => {
                write!(f, "LoggedOut({generation}, {})", report.is_ok())
            }
        }
    }
}

// ── The host side of a login ─────────────────────────────────────────────────

/// How a login opens an authorization address, when it may open one at all.
///
/// A value rather than a direct call to the launcher so a test can hand the
/// flow a process it owns and then prove the cancellation path actually reaps
/// it — without a browser, and without a window on the machine running the
/// suite.
pub(crate) type BrowserOpener =
    Arc<dyn Fn(&str) -> Result<tokio::process::Child, String> + Send + Sync>;

/// The production opener: this desktop's browser.
pub(crate) fn system_browser() -> BrowserOpener {
    Arc::new(|url: &str| {
        coda_boot::browser::launch_authorization_url(url).map_err(|error| error.to_string())
    })
}

/// The terminal's [`LoginUi`].
///
/// Every callback publishes to the surface and returns; none of them blocks
/// the flow on a person, because the key was already collected on the choice
/// surface before any network work started.
struct TuiLoginUi<E> {
    tx: mpsc::UnboundedSender<E>,
    generation: u64,
    api_key: Mutex<Option<Secret<String>>>,
    opener: Option<BrowserOpener>,
    launchers: Mutex<Vec<tokio::process::Child>>,
}

impl<E: From<FlowEvent> + Send + 'static> TuiLoginUi<E> {
    fn new(
        tx: mpsc::UnboundedSender<E>,
        generation: u64,
        api_key: Option<Secret<String>>,
        opener: Option<BrowserOpener>,
    ) -> Self {
        Self {
            tx,
            generation,
            api_key: Mutex::new(api_key),
            opener,
            launchers: Mutex::new(Vec::new()),
        }
    }

    fn publish(&self, title: &str, lines: Vec<String>) {
        let _ = self.tx.send(
            FlowEvent::Challenge {
                generation: self.generation,
                title: title.to_owned(),
                lines,
            }
            .into(),
        );
    }

    /// A launcher that starts is not evidence a browser opened, which is why
    /// the address is shown first and unconditionally.
    fn open(&self, url: &str) -> Option<String> {
        let opener = self.opener.as_ref()?;
        match opener(url) {
            Ok(child) => {
                self.launchers.lock().expect("launchers").push(child);
                None
            }
            Err(error) => Some(format!(
                "A browser could not be started here ({error}); open the address above yourself."
            )),
        }
    }

    /// Reaps the launchers this login started, once its future has been
    /// dropped. One still running when the grace expires is left alone: on
    /// some desktops it *is* the browser.
    async fn release(&self) {
        let launchers = {
            let mut held = self.launchers.lock().expect("launchers");
            std::mem::take(&mut *held)
        };
        if launchers.is_empty() {
            return;
        }
        let _ = tokio::time::timeout(std::time::Duration::from_secs(2), async {
            for mut child in launchers {
                let _ = child.wait().await;
            }
        })
        .await;
    }
}

#[async_trait]
impl<E: From<FlowEvent> + Send + Sync + 'static> LoginUi for TuiLoginUi<E> {
    async fn api_key(&self) -> Result<Secret<String>, AuthError> {
        self.api_key
            .lock()
            .expect("api key")
            .take()
            .ok_or_else(|| AuthError::LoginCancelled("no API key was entered".into()))
    }

    async fn authorization_url(&self, url: &str) -> Result<(), AuthError> {
        let mut lines = vec![
            "Finish signing in at this address:".to_owned(),
            format!("  {}", sanitize(url, 600)),
            String::new(),
            "Waiting for the provider to answer. Nothing has been saved yet.".to_owned(),
        ];
        if let Some(problem) = self.open(url) {
            lines.push(problem);
        }
        self.publish("Authorize in your browser", lines);
        Ok(())
    }

    async fn device_code(&self, prompt: DeviceCodePrompt) -> Result<(), AuthError> {
        let target =
            prompt.verification_uri_complete.as_deref().unwrap_or(&prompt.verification_uri);
        let mut lines = vec![
            format!("Enter the code   {}", sanitize(&prompt.user_code, 64)),
            format!("at               {}", sanitize(&prompt.verification_uri, 600)),
            String::new(),
            "Waiting for the provider to answer. Nothing has been saved yet.".to_owned(),
        ];
        if let Some(problem) = self.open(target) {
            lines.push(problem);
        }
        self.publish("Enter the device code", lines);
        Ok(())
    }

    async fn copilot_deployment(
        &self,
        _saved_domain: Option<&str>,
    ) -> Result<Option<CopilotDeploymentChoice>, AuthError> {
        // Already answered on the choice surface, before anything was sent.
        Ok(None)
    }
}

// ── The flow ─────────────────────────────────────────────────────────────────

/// One sign-in (or sign-out), and the tasks it owns.
///
/// Generic over the host's event type so a host with more to report — an
/// application that also boots and verifies an engine — funnels everything
/// through one channel and therefore one `select!` arm.
pub(crate) struct LoginFlow<E> {
    tx: mpsc::UnboundedSender<E>,
    rx: Option<mpsc::UnboundedReceiver<E>>,
    /// Bumped by every start and every cancellation, so an event from a flow
    /// the user retracted can be recognised and dropped.
    generation: u64,
    /// The task that opens the profile and reads what the choice surface
    /// needs. Cancellable and writes nothing, but it is a *stage*: a second
    /// flow may not start while it runs, and its answer must be dropped if the
    /// user walked away from it.
    opening: Option<tokio::task::JoinHandle<()>>,
    /// The preparation task and the switch that cancels it.
    prepare: Option<(tokio::task::JoinHandle<()>, oneshot::Sender<()>)>,
    /// Preparation tasks that have been *asked* to stop and are running their
    /// own cleanup.
    ///
    /// Retained rather than aborted: a preparation owns the browser processes
    /// it launched, and killing the task at the await point skips the reaping
    /// that its own cleanup does. They are awaited, bounded, when the flow
    /// closes.
    retiring: Vec<tokio::task::JoinHandle<()>>,
    /// The uncancellable task: a credential transaction, or a sign-out.
    uncancellable: Option<tokio::task::JoinHandle<()>>,
    /// The service the running flow commits through, held so the commit and
    /// the check use the profile the preparation captured.
    service: Option<Arc<AuthService>>,
    target: Option<Target>,
}

/// How long a retired preparation is given to finish its own cleanup.
///
/// Bounded on purpose: reaping a launcher that is itself the browser must not
/// hold the terminal open, and the flow's cleanup already declines to kill
/// anything.
const RETIRE_GRACE: std::time::Duration = std::time::Duration::from_secs(3);

impl<E: From<FlowEvent> + Send + Sync + 'static> Default for LoginFlow<E> {
    fn default() -> Self {
        Self::new()
    }
}

impl<E> std::fmt::Debug for LoginFlow<E> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LoginFlow")
            .field("generation", &self.generation)
            .field("opening", &self.opening.is_some())
            .field("preparing", &self.prepare.is_some())
            .field("committing", &self.uncancellable.is_some())
            .field("target", &self.target)
            .finish()
    }
}

impl<E: From<FlowEvent> + Send + Sync + 'static> LoginFlow<E> {
    pub(crate) fn new() -> Self {
        let (tx, rx) = mpsc::unbounded_channel();
        Self {
            tx,
            rx: Some(rx),
            generation: 0,
            opening: None,
            prepare: None,
            retiring: Vec::new(),
            uncancellable: None,
            service: None,
            target: None,
        }
    }

    /// The receiver, taken exactly once by whatever pumps the flow.
    pub(crate) fn take_events(&mut self) -> mpsc::UnboundedReceiver<E> {
        self.rx.take().expect("the authentication events are pumped exactly once")
    }

    /// A sender for events a host produces on the flow's behalf.
    pub(crate) fn sender(&self) -> mpsc::UnboundedSender<E> {
        self.tx.clone()
    }

    pub(crate) fn generation(&self) -> u64 {
        self.generation
    }

    /// Whether an event is from the flow that is running now.
    pub(crate) fn is_current(&self, generation: u64) -> bool {
        generation == self.generation
    }

    pub(crate) fn is_preparing(&self) -> bool {
        self.prepare.is_some()
    }

    /// Whether the profile is being opened for a flow that has not asked the
    /// user anything yet.
    pub(crate) fn is_opening(&self) -> bool {
        self.opening.is_some()
    }

    /// Whether a transaction is running. Nothing may cancel it and nothing may
    /// start a second flow while it does.
    pub(crate) fn is_committing(&self) -> bool {
        self.uncancellable.is_some()
    }

    pub(crate) fn is_active(&self) -> bool {
        self.is_opening() || self.is_preparing() || self.is_committing()
    }

    pub(crate) fn service(&self) -> Option<Arc<AuthService>> {
        self.service.clone()
    }

    pub(crate) fn hold_service(&mut self, service: Arc<AuthService>) {
        self.service = Some(service);
    }

    pub(crate) fn target(&self) -> Option<&Target> {
        self.target.as_ref()
    }

    /// Forgets the finished flow's service and target without disturbing the
    /// generation: work already reported against it (an engine boot, a
    /// connection check) still belongs to it.
    pub(crate) fn clear(&mut self) {
        self.prepare = None;
        self.service = None;
        self.target = None;
    }

    /// Starts the cancellable stage that opens the profile.
    ///
    /// Opening a credential store means a keychain round-trip, a settings file
    /// and — for a login — resolving a provider's endpoints. On the event loop
    /// that is a terminal which does not draw, does not scroll and does not
    /// answer `Ctrl+C` while it happens. Here it is a task like every other
    /// slow thing, and it carries the flow's generation so an answer that
    /// arrives after the user walked away opens nothing.
    ///
    /// It writes nothing, so cancelling is free.
    pub(crate) fn begin_opening<F, Fut>(&mut self, work: F) -> u64
    where
        F: FnOnce(u64, mpsc::UnboundedSender<E>) -> Fut,
        Fut: Future<Output = ()> + Send + 'static,
    {
        self.generation += 1;
        self.service = None;
        self.target = None;
        let generation = self.generation;
        self.opening = Some(tokio::spawn(work(generation, self.tx.clone())));
        generation
    }

    /// Marks the opening stage as finished, once its answer has arrived.
    pub(crate) fn settle_opening(&mut self) {
        self.opening = None;
    }

    /// Starts a cancellable preparation. Writes nothing; stops nothing.
    pub(crate) fn begin(
        &mut self,
        service: Arc<AuthService>,
        request: LoginRequest,
        key: Option<Secret<String>>,
        target: Target,
        opener: Option<BrowserOpener>,
    ) -> u64 {
        self.generation += 1;
        let generation = self.generation;
        self.opening = None;
        self.service = Some(Arc::clone(&service));
        self.target = Some(target);

        let tx = self.tx.clone();
        let (cancel_tx, cancel_rx) = oneshot::channel();
        let handle = tokio::spawn(async move {
            let ui = TuiLoginUi::<E>::new(tx.clone(), generation, key, opener);
            // The preparation future owns the loopback listener and the device
            // poller, so losing the race drops and closes them.
            let prepared = tokio::select! {
                biased;
                _ = cancel_rx => None,
                result = service.prepare_login(request, &ui) => Some(Box::new(result)),
            };
            ui.release().await;
            let _ = tx.send(FlowEvent::Prepared { generation, result: prepared }.into());
        });
        self.prepare = Some((handle, cancel_tx));
        generation
    }

    /// Commits `prepared`, after `before` has finished.
    ///
    /// `before` is the host's "make this safe": for an application it stops
    /// and **awaits** the engine it owns, because a child that outlives the
    /// credential is a child still spending it. It runs on the task rather
    /// than on the caller so the event loop keeps drawing and keeps accepting
    /// `Ctrl+C` while a process is going away.
    pub(crate) fn commit<F>(&mut self, prepared: PreparedLogin, before: F)
    where
        F: Future<Output = ()> + Send + 'static,
    {
        let Some(service) = self.service.clone() else {
            return;
        };
        self.prepare = None;
        let generation = self.generation;
        let tx = self.tx.clone();
        let handle = tokio::spawn(async move {
            before.await;
            let outcome = service.commit_login(prepared).await;
            // The transaction may have been handed to another task inside the
            // service; the profile is only settled once that has finished.
            service.await_pending_commit().await;
            let _ = tx
                .send(FlowEvent::Committed { generation, outcome: Box::new(outcome) }.into());
        });
        self.uncancellable = Some(handle);
    }

    /// Signs out, after `before` has finished. Uncancellable for the same
    /// reason a commit is.
    pub(crate) fn logout<F>(
        &mut self,
        service: Arc<AuthService>,
        identity: Option<ProviderIdentity>,
        before: F,
    ) where
        F: Future<Output = ()> + Send + 'static,
    {
        self.generation += 1;
        let generation = self.generation;
        self.opening = None;
        self.service = Some(Arc::clone(&service));
        let tx = self.tx.clone();
        let handle = tokio::spawn(async move {
            before.await;
            let report = service.logout(identity).await;
            service.await_pending_commit().await;
            let _ = tx.send(FlowEvent::LoggedOut { generation, report: Box::new(report) }.into());
        });
        self.uncancellable = Some(handle);
    }

    /// Marks the uncancellable task as finished, once its event has arrived.
    pub(crate) fn settle(&mut self) {
        self.uncancellable = None;
    }

    /// Abandons whatever is in flight that has written nothing. Returns
    /// whether anything actually was.
    ///
    /// A commit is never cancelled here — there is nothing to cancel that
    /// would not leave a profile half-written — so a flow that has reached one
    /// is left exactly as it is, and its generation is not disturbed.
    ///
    /// The preparation task is *asked* to stop rather than killed. Aborting it
    /// at its await point dropped the listener and the poller — which is what
    /// the select already does — but also skipped the task's own cleanup,
    /// which is the only thing that reaps the browser processes the login
    /// launched. `tokio::process::Child` does not kill on drop, so those were
    /// simply forgotten. The handle is kept and awaited, bounded, by
    /// [`Self::close`].
    pub(crate) fn cancel(&mut self) -> bool {
        if self.is_committing() {
            return false;
        }
        // A read, and one that has published nothing: nothing to unwind.
        let was_opening = self.opening.is_some();
        if let Some(handle) = self.opening.take() {
            handle.abort();
            self.retiring.push(handle);
        }
        let was_preparing = self.prepare.is_some();
        if let Some((handle, cancel)) = self.prepare.take() {
            // The task drops the loopback listener and the device poller
            // itself, then reaps whatever it launched, then reports a
            // preparation this generation no longer belongs to.
            let _ = cancel.send(());
            self.retiring.push(handle);
        }
        self.retiring.retain(|handle| !handle.is_finished());
        // Anything still in the channel from this attempt is now stale.
        self.generation += 1;
        self.service = None;
        self.target = None;
        was_opening || was_preparing
    }

    /// Shuts the flow down as its host exits.
    ///
    /// A preparation is cancelled — it has written nothing, so dropping it is
    /// free — and a transaction is **awaited**, because one abandoned
    /// half-written would leave a profile the process then reported as
    /// unchanged.
    ///
    /// Every retired preparation is awaited too, within a bound: that is where
    /// a cancelled login's browser processes are reaped, and an application
    /// that exited without it left them behind.
    pub(crate) async fn close(&mut self) {
        if let Some(handle) = self.opening.take() {
            handle.abort();
            self.retiring.push(handle);
        }
        if let Some((handle, cancel)) = self.prepare.take() {
            let _ = cancel.send(());
            self.retiring.push(handle);
        }
        for handle in std::mem::take(&mut self.retiring) {
            // Bounded, and then abandoned rather than killed: a launcher that
            // is still running when the grace expires is, on some desktops,
            // the browser itself.
            if tokio::time::timeout(RETIRE_GRACE, handle).await.is_err() {
                break;
            }
        }
        if let Some(handle) = self.uncancellable.take() {
            let _ = handle.await;
        }
        if let Some(service) = self.service.take() {
            service.await_pending_commit().await;
        }
        self.target = None;
    }
}

// ── Disclosure ───────────────────────────────────────────────────────────────

/// Where a sign-in to `identity` would send an authorization and a credential,
/// resolved through the service's own configuration.
///
/// Read from [`AuthService`] rather than from a second look at the
/// environment: two independent readings are exactly how a screen ends up
/// naming public github.com while the request goes to the tenant an exported
/// variable selected. Hosts only ever show hosts, never URLs — an authorize
/// URL carries the CSRF state and the loopback redirect.
pub(crate) fn destination_lines(
    service: &AuthService,
    identity: ProviderIdentity,
    deployment: Option<&CopilotDeploymentChoice>,
) -> Vec<String> {
    match identity {
        ProviderIdentity::GithubCopilot => match service.resolve_copilot_deployment(deployment) {
            Ok(resolved) => vec![render_deployment_of(
                &resolved.deployment,
                &resolved.config.api_base_url,
                &resolved.endpoint_overrides,
            )],
            Err(error) => vec![format!(
                "The GitHub Copilot deployment could not be resolved ({}), so nothing will be \
                 sent. Correct the saved tenant or the GH_COPILOT_* variables in this \
                 environment first.",
                coda_auth::failure::AuthFailure::classify(&error)
            )],
        },
        ProviderIdentity::ClaudeAi => {
            vec![render_authorization_host("Claude.ai", service.claude_authorize_url())]
        }
        ProviderIdentity::AnthropicApiKey => {
            vec![render_anthropic_endpoint(&service.anthropic_endpoint())]
        }
    }
}

/// The deployment a *committed* account is connected to, said once, safely,
/// after the transaction landed.
pub(crate) fn committed_destination(
    service: &AuthService,
    identity: ProviderIdentity,
    deployment: Option<&CopilotDeploymentChoice>,
) -> String {
    let where_to = destination_lines(service, identity, deployment).join(" ");
    format!("Connected account: {}. {where_to}", identity.label())
}

/// What a sign-in discloses *before* anything is entered: which accounts are
/// stored, what this one would remove, and where it would send a credential.
pub(crate) async fn disclosure(
    service: &AuthService,
    requested: Option<ProviderIdentity>,
) -> Vec<String> {
    let mut lines = Vec::new();
    match service.status().await {
        Ok(status) => {
            let stored: Vec<ProviderIdentity> = status
                .providers
                .iter()
                .filter(|provider| matches!(provider.state, StoredState::Present(_)))
                .map(|provider| provider.identity)
                .collect();
            match requested {
                Some(identity) => {
                    let replaced: Vec<ProviderIdentity> =
                        stored.iter().copied().filter(|other| *other != identity).collect();
                    let text = render_replacement(identity, &replaced);
                    if !text.is_empty() {
                        lines.push(text);
                    }
                }
                None if !stored.is_empty() => lines.push(format!(
                    "Coda keeps one saved account at a time; {} {} stored now and will be \
                     removed by a sign-in to something else.",
                    stored.iter().map(|i| i.label()).collect::<Vec<_>>().join(" and "),
                    if stored.len() == 1 { "is" } else { "are" },
                )),
                None => {}
            }
            // Where this sign-in would actually go, before there is anything
            // to send. Shown for the account the form starts on; the flow
            // says it again, for the account actually chosen, before the
            // preparation sends its first byte.
            //
            // With no account named the form can answer with any of them, so
            // every one of their hosts is named — including Claude.ai's
            // authorization host, which is as much a destination as an
            // API-key endpoint is.
            lines.extend(destination_lines(
                service,
                requested.unwrap_or(ProviderIdentity::AnthropicApiKey),
                None,
            ));
            if requested.is_none() {
                lines.extend(destination_lines(service, ProviderIdentity::ClaudeAi, None));
                lines.extend(destination_lines(service, ProviderIdentity::GithubCopilot, None));
            }
            if status.environment_api_key {
                lines.push(
                    "ANTHROPIC_API_KEY is exported here, so the environment option below has \
                     something to select."
                        .to_owned(),
                );
            }
        }
        Err(failure) => lines.push(format!(
            "The stored authentication could not be read ({failure}); nothing has been changed. \
             Signing in now may overwrite a credential that is still recoverable."
        )),
    }
    lines
}

// ── Rebuilding the launch ────────────────────────────────────────────────────

/// The launch for `identity`, derived from the one this session started with.
///
/// Three things have to be true of the result, and none of them is automatic:
///
/// * the *previous* account's instructions must be gone — a stale
///   `--provider`, `--provider=x`, `--model` or `--api-key` would reconnect
///   what the user just replaced;
/// * the new choice must survive the spawn — removals are applied **after**
///   additions ([`coda_client::Engine::spawn`]), so a conflicting `env_remove`
///   entry would erase the variable that carries the choice;
/// * an explicitly public Copilot login must *remove* an inherited
///   `GH_COPILOT_ENTERPRISE_DOMAIN` rather than merely not setting it, or the
///   new account's token is sent to the old tenant.
///
/// Everything else — the working directory, diagnostics forwarding, unrelated
/// engine arguments — is kept: those are the user's own startup intent and
/// have nothing to do with which account is connected.
pub(crate) fn engine_command_for(
    base: &EngineCommand,
    identity: ProviderIdentity,
    deployment: Option<&CopilotDeploymentChoice>,
) -> EngineCommand {
    let mut command = base.clone();
    command.args = strip_replaced_args(&command.args);

    // Which variables this launch decides, and therefore which inherited or
    // previously-set ones must not be allowed to answer instead.
    let mut owned: Vec<String> = OWNED_ENGINE_ENV.iter().map(|key| (*key).to_owned()).collect();
    if identity == ProviderIdentity::GithubCopilot && deployment.is_some() {
        owned.push(COPILOT_TENANT_ENV.to_owned());
    }
    let owns = |key: &std::ffi::OsStr| {
        owned.iter().any(|name| key.eq_ignore_ascii_case(std::ffi::OsStr::new(name)))
    };

    command.env.retain(|(key, _)| !owns(key));
    command.env_remove.retain(|key| !owns(key));

    command = command.env("CODA_SERVE_PROVIDER", identity.engine_id());
    // Not "unset": an inherited raw key or endpoint would bypass the account
    // that was just committed, and an empty value is still a present variable.
    command = command
        .env_remove("CODA_SERVE_API_KEY")
        .env_remove("CODA_SERVE_ENDPOINT")
        .env_remove("CODA_SERVE_MODEL");

    match (identity, deployment) {
        (ProviderIdentity::GithubCopilot, Some(CopilotDeploymentChoice::Public)) => {
            command = command.env_remove(COPILOT_TENANT_ENV);
        }
        (ProviderIdentity::GithubCopilot, Some(CopilotDeploymentChoice::Enterprise(domain))) => {
            command = command.env(COPILOT_TENANT_ENV, domain.as_str());
        }
        // No explicit deployment, or a provider the variable does not apply
        // to: the engine resolves it from the profile the commit just wrote.
        _ => {}
    }
    command
}

/// Drops `--flag value` and `--flag=value` for every replaced option.
fn strip_replaced_args(args: &[std::ffi::OsString]) -> Vec<std::ffi::OsString> {
    let mut out = Vec::with_capacity(args.len());
    let mut skip_value = false;
    for arg in args {
        if skip_value {
            skip_value = false;
            continue;
        }
        let text = arg.to_string_lossy();
        let name = text.split('=').next().unwrap_or_default();
        if REPLACED_ARGS.iter().any(|flag| name.eq_ignore_ascii_case(flag)) {
            // Only a bare flag consumes the next word; `--provider=x` is
            // already complete.
            skip_value = !text.contains('=');
            continue;
        }
        out.push(arg.clone());
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::OsString;

    fn base() -> EngineCommand {
        EngineCommand::new("coda").arg("serve").working_dir("C:\\work")
    }

    fn args_of(command: &EngineCommand) -> Vec<String> {
        command.args.iter().map(|a| a.to_string_lossy().into_owned()).collect()
    }

    fn env_of(command: &EngineCommand, key: &str) -> Option<String> {
        command
            .env
            .iter()
            .find(|(name, _)| name.eq_ignore_ascii_case(OsString::from(key).as_os_str()))
            .map(|(_, value)| value.to_string_lossy().into_owned())
    }

    fn removes(command: &EngineCommand, key: &str) -> bool {
        command
            .env_remove
            .iter()
            .any(|name| name.eq_ignore_ascii_case(OsString::from(key).as_os_str()))
    }

    #[test]
    fn a_stale_provider_argument_cannot_undo_the_new_choice() {
        // Both spellings, and the value that follows the bare one.
        let stale = base()
            .arg("--provider")
            .arg("github-copilot")
            .arg("--model=gpt-5")
            .arg("--keep-me");
        let rebuilt = engine_command_for(&stale, ProviderIdentity::ClaudeAi, None);
        assert_eq!(args_of(&rebuilt), vec!["serve", "--keep-me"]);
        assert_eq!(env_of(&rebuilt, "CODA_SERVE_PROVIDER").as_deref(), Some("claude-ai"));
        assert_eq!(rebuilt.working_dir, Some(std::path::PathBuf::from("C:\\work")));
    }

    #[test]
    fn a_previous_provider_variable_is_replaced_rather_than_appended() {
        let stale = base().env("CODA_SERVE_PROVIDER", "github-copilot");
        let rebuilt = engine_command_for(&stale, ProviderIdentity::AnthropicApiKey, None);
        let set: Vec<&(OsString, OsString)> = rebuilt
            .env
            .iter()
            .filter(|(key, _)| key == OsString::from("CODA_SERVE_PROVIDER").as_os_str())
            .collect();
        assert_eq!(set.len(), 1, "two provider variables would be resolved by spawn order");
        assert_eq!(env_of(&rebuilt, "CODA_SERVE_PROVIDER").as_deref(), Some("anthropic"));
    }

    #[test]
    fn an_inherited_raw_key_or_model_cannot_bypass_the_account_that_was_just_saved() {
        let rebuilt = engine_command_for(&base(), ProviderIdentity::ClaudeAi, None);
        for key in ["CODA_SERVE_API_KEY", "CODA_SERVE_ENDPOINT", "CODA_SERVE_MODEL"] {
            assert!(removes(&rebuilt, key), "{key} could still be inherited");
        }
    }

    #[test]
    fn an_explicitly_public_copilot_login_removes_an_inherited_enterprise_tenant() {
        // Not "does not set it": the variable is inherited, and removals are
        // applied last, so removing is the only thing that works.
        let inherited = base().env(COPILOT_TENANT_ENV, "octocorp.ghe.com");
        let rebuilt = engine_command_for(
            &inherited,
            ProviderIdentity::GithubCopilot,
            Some(&CopilotDeploymentChoice::Public),
        );
        assert_eq!(env_of(&rebuilt, COPILOT_TENANT_ENV), None);
        assert!(removes(&rebuilt, COPILOT_TENANT_ENV));
    }

    #[test]
    fn an_enterprise_login_survives_a_conflicting_removal() {
        // `env_remove` is applied after `env` when the child is spawned, so a
        // leftover removal would erase the tenant this login just chose.
        let conflicting = base().env_remove(COPILOT_TENANT_ENV);
        let rebuilt = engine_command_for(
            &conflicting,
            ProviderIdentity::GithubCopilot,
            Some(&CopilotDeploymentChoice::Enterprise("octocorp.ghe.com".into())),
        );
        assert_eq!(env_of(&rebuilt, COPILOT_TENANT_ENV).as_deref(), Some("octocorp.ghe.com"));
        assert!(!removes(&rebuilt, COPILOT_TENANT_ENV));
    }

    #[test]
    fn a_non_copilot_login_leaves_the_tenant_variable_alone() {
        // It does not apply to this account, and clearing it would silently
        // change what a later Copilot sign-in resolves.
        let inherited = base().env(COPILOT_TENANT_ENV, "octocorp.ghe.com");
        let rebuilt = engine_command_for(&inherited, ProviderIdentity::ClaudeAi, None);
        assert_eq!(env_of(&rebuilt, COPILOT_TENANT_ENV).as_deref(), Some("octocorp.ghe.com"));
        assert!(!removes(&rebuilt, COPILOT_TENANT_ENV));
    }

    #[test]
    fn unrelated_startup_intent_is_preserved() {
        let rich = base()
            .arg("--engine-arg-of-some-kind")
            .env("CODA_DIAGNOSTIC_DIR", "C:\\logs")
            .env_remove("SOME_OTHER_SECRET");
        let rebuilt = engine_command_for(&rich, ProviderIdentity::ClaudeAi, None);
        assert!(args_of(&rebuilt).contains(&"--engine-arg-of-some-kind".to_owned()));
        assert_eq!(env_of(&rebuilt, "CODA_DIAGNOSTIC_DIR").as_deref(), Some("C:\\logs"));
        assert!(removes(&rebuilt, "SOME_OTHER_SECRET"));
    }

    /// A cancelled flow's events must be recognisable as stale, or the next
    /// attempt adopts a preparation the user retracted.
    #[test]
    fn cancelling_moves_the_flow_past_everything_the_previous_attempt_will_report() {
        let mut flow: LoginFlow<FlowEvent> = LoginFlow::new();
        let before = flow.generation();
        assert!(flow.is_current(before));
        flow.cancel();
        assert!(!flow.is_current(before), "an event from the cancelled attempt still looks current");
    }

    #[test]
    fn a_challenge_never_prints_what_it_carries() {
        let event = FlowEvent::Challenge {
            generation: 1,
            title: "Authorize".into(),
            lines: vec!["https://example.invalid/oauth?code=secret-code".into()],
        };
        let printed = format!("{event:?}");
        assert!(!printed.contains("secret-code"), "{printed}");
        assert!(printed.contains("REDACTED"), "{printed}");
    }
}
