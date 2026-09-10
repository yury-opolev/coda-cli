//! Signing in, switching provider and signing out, from the terminal.
//!
//! # The order, and why each step is where it is
//!
//! ```text
//! gate            AccessMode first, before a credential store is opened
//!   ↓
//! claim           the exclusive surface goes up before anything is read, so
//!                 a refusal is visible and has read nothing
//!   ↓
//! open            a cancellable task: the profile, what it holds, and where
//!                 a sign-in to the named account would go. Nothing is
//!                 awaited on the loop, which keeps drawing and keeps
//!                 accepting Ctrl+C while a keychain answers
//!   ↓
//! choose          an exclusive surface: account, deployment, key source,
//!                 and the disclosure of what this will replace
//!   ↓
//! disclose        where the account that was *chosen* will be authorized,
//!                 from the service's own resolved configuration, before the
//!                 preparation sends anything
//!   ↓
//! prepare         a cancellable task: browser / device code / key probe.
//!                 Writes nothing. The engine that is running keeps running.
//!   ↓
//! stop and await  on the commit's own task: the engine this application owns
//!                 is asked to stop and then *waited for*. A shutdown that was
//!                 merely sent leaves a child holding the credential that is
//!                 about to be deleted.
//!   ↓
//! commit          one transaction, on that same task, never cancelled
//!   ↓
//! boot            a fresh engine, told explicitly which account to use,
//!                 resuming this session through the public API
//!   ↓
//! read back       models, effort and the described configuration are re-read
//!                 from the new process, and the connection is verified
//! ```
//!
//! A sign-out runs the same first three stages and then goes straight to
//! *stop and await* and its own uncancellable transaction. It holds an
//! exclusive surface throughout, and [`App::engine_start_allowed`] refuses
//! anything that would start an engine while it does: between "the engine was
//! stopped" and "the credential was deleted" there is a window in which a
//! `/resume` would spawn a child on the account being disconnected.
//!
//! Everything slow happens on a task and reports back through
//! [`AuthEvent`], so the terminal keeps drawing and accepting keys while a
//! credential store is being opened, a browser is open, a device code is being
//! polled, an engine is going away, a credential is being written, or a new
//! engine is starting. `on_auth_event` itself never awaits an opening, a
//! shutdown, a transaction or a sign-out.
//!
//! # What never happens here
//!
//! * No authorization URL, device code or key ever reaches the transcript, the
//!   command history, the clipboard or the diagnostic log. They exist on the
//!   exclusive surface and nowhere else.
//! * No engine this client did not start is ever stopped, and its host's
//!   credentials are never read — see [`crate::local::auth::refusal`].
//! * Nothing is committed while a turn is running, and no running turn is
//!   implicitly cancelled to make room for a sign-in.
//! * Nothing a *retracted* flow reports is ever acted on: every event carries
//!   its flow's generation, and cancelling moves past it.
//!
//! The flow itself — the challenge plumbing, the uncancellable transaction,
//! the launch a committed account implies — lives in [`crate::local::login`],
//! because the launcher's engine-less first-run wizard runs the same one.

use std::sync::Arc;

use coda_auth::provider::copilot::CopilotDeploymentChoice;
use coda_auth::service::{
    ApiKeySource, AuthService, CommitOutcome, LoginRequest, LogoutFailure, LogoutReport,
    PreparedLogin, ProviderIdentity, VerificationReport,
};
use coda_auth::Secret;
use coda_boot::auth_cli::{
    render_commit, render_environment_replacement, render_logout, render_logout_failure,
    render_prepare_failure, render_replacement, sanitize, NO_BROWSER_ENV,
};
use futures::future::BoxFuture;
use tokio::sync::mpsc;

use super::App;
use crate::local::auth::{refusal, AuthPort, Opening};
use crate::local::login::{
    committed_destination, destination_lines, disclosure, engine_command_for, FlowEvent, LoginFlow,
    Target,
};
use crate::surface::auth::{AuthChallengeSurface, AuthChoiceSurface};
use crate::transcript::NoticeLevel;

/// The environment variables a per-provider launch must own outright, the
/// tenant variable and the arguments a re-launch drops all live with the
/// launch they rebuild — see [`crate::local::login`].

// ── Events ───────────────────────────────────────────────────────────────────

/// Something an authentication task has to tell the loop.
///
/// The flow's own events arrive through [`FlowEvent`]; the two this
/// application adds are about the engine, which the flow knows nothing about.
/// All of them carry the generation of the flow they belong to, so work
/// reported after the user retracted a sign-in is dropped rather than adopted
/// by whatever started next.
pub(crate) enum AuthEvent {
    /// The shared flow: a challenge, a preparation, a commit, a sign-out.
    Flow(FlowEvent),
    /// The profile was opened for a flow that has not asked anything yet, and
    /// everything the next surface needs was read with it.
    Opened { generation: u64, result: Box<Result<Opened, String>> },
    /// A read-only account report finished. It belongs to no flow.
    Reported { text: String },
    /// A replacement engine either started or did not.
    Booted { generation: u64, result: Box<Result<crate::api::boot::Booted, String>> },
    /// The post-commit connection check finished. `None` means no client could
    /// be built at all — not a rejection.
    Verified { generation: u64, report: Option<Box<VerificationReport>> },
}

/// What an opening stage was opened *for*.
///
/// Carried with the answer rather than remembered on the application, so a
/// stage that is retracted while it runs leaves nothing behind to be applied
/// to whatever started next.
pub(crate) enum Opened {
    /// A sign-in, with what the choice surface discloses before anything is
    /// typed.
    Login {
        service: Arc<AuthService>,
        disclosure: Vec<String>,
        saved_domain: Option<String>,
        requested: Option<ProviderIdentity>,
    },
    /// A sign-out of one account, or of everything.
    Logout { service: Arc<AuthService>, identity: Option<ProviderIdentity> },
}

impl From<FlowEvent> for AuthEvent {
    fn from(event: FlowEvent) -> Self {
        Self::Flow(event)
    }
}

impl AuthEvent {
    fn generation(&self) -> u64 {
        match self {
            Self::Flow(event) => event.generation(),
            Self::Booted { generation, .. }
            | Self::Verified { generation, .. }
            | Self::Opened { generation, .. } => *generation,
            // A read-only report belongs to no flow. It is answered before the
            // generation gate; a value no flow can ever have is what makes
            // that fail closed rather than silently adopting it if the
            // short-circuit is ever removed.
            Self::Reported { .. } => u64::MAX,
        }
    }
}

impl std::fmt::Debug for AuthEvent {
    /// Never prints a challenge. The `lines` of a challenge carry an
    /// authorization URL or a device code, and a `Debug` is exactly the kind
    /// of thing that ends up in a log.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Flow(event) => write!(f, "{event:?}"),
            Self::Opened { generation, result } => write!(
                f,
                "Opened({generation}, {})",
                if result.is_ok() { "opened" } else { "failed" }
            ),
            Self::Reported { .. } => write!(f, "Reported"),
            Self::Booted { generation, result } => write!(
                f,
                "Booted({generation}, {})",
                if result.is_ok() { "started" } else { "failed" }
            ),
            Self::Verified { generation, report } => {
                write!(f, "Verified({generation}, {})", report.is_some())
            }
        }
    }
}

/// The running flow, if any, plus the engine work that follows a commit.
pub(crate) struct AuthState {
    flow: LoginFlow<AuthEvent>,
    booting: Option<tokio::task::JoinHandle<()>>,
    verifying: Option<tokio::task::JoinHandle<()>>,
    /// A read-only `/provider` report being assembled off the loop.
    ///
    /// Deliberately not part of [`LoginFlow::is_active`]: it writes nothing,
    /// stops nothing and starts nothing, so it must not refuse a sign-in or a
    /// resume. A second one is refused while it runs.
    reporting: Option<tokio::task::JoinHandle<()>>,
    /// Whether this process may launch a browser for an authorization URL.
    ///
    /// Decided once, from `CODA_AUTH_NO_BROWSER`, and held rather than re-read
    /// at each login: a test drives the flow with it off, so no test can open
    /// a window on the machine running it.
    browser_allowed: bool,
    /// How an authorization address is opened, when it may be.
    ///
    /// `None` means "this desktop's browser". A test substitutes a process it
    /// owns, which is how the cancellation path's reaping is proven rather
    /// than asserted about a comment.
    opener: Option<crate::local::login::BrowserOpener>,
}

impl std::fmt::Debug for AuthState {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AuthState")
            .field("flow", &self.flow)
            .field("booting", &self.booting.is_some())
            .finish()
    }
}

impl Default for AuthState {
    fn default() -> Self {
        Self::new()
    }
}

impl AuthState {
    pub(crate) fn new() -> Self {
        Self {
            flow: LoginFlow::new(),
            booting: None,
            verifying: None,
            reporting: None,
            browser_allowed: coda_boot::auth_cli::browser_allowed(
                std::env::var_os(NO_BROWSER_ENV).as_deref(),
            ),
            opener: None,
        }
    }

    /// The receiver, taken exactly once by whatever pumps the flow — the event
    /// loop in production, a test driving `on_auth_event` directly.
    pub(crate) fn take_events(&mut self) -> mpsc::UnboundedReceiver<AuthEvent> {
        self.flow.take_events()
    }

    /// Whether a flow is in progress and a second one must be refused.
    pub(crate) fn is_active(&self) -> bool {
        self.flow.is_active() || self.booting.is_some()
    }

    /// How this login opens an address, or `None` when it may not open one.
    fn opener(&self) -> Option<crate::local::login::BrowserOpener> {
        if !self.browser_allowed {
            return None;
        }
        Some(self.opener.clone().unwrap_or_else(crate::local::login::system_browser))
    }
}

// ── The application ──────────────────────────────────────────────────────────

impl App {
    /// Whether an engine is believed to be answering.
    ///
    /// False after a deliberate disconnection — a logout, or a sign-in whose
    /// replacement engine did not start. The loop stops reading the closed
    /// inbound channel, stops polling for recovery and refuses to submit,
    /// while the transcript, the draft and every local command keep working.
    pub fn engine_connected(&self) -> bool {
        self.engine_connected
    }

    /// Replaces the port a login opens its profile through.
    ///
    /// Production uses this machine's profile. A test uses an isolated
    /// temporary one, and can then assert how many times it was opened at all.
    pub fn set_auth_port(&mut self, port: AuthPort) {
        self.auth_port = port;
    }

    /// The port, so a test can read the counter it keeps.
    pub fn auth_port(&self) -> &AuthPort {
        &self.auth_port
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    /// `/login [provider]`, `/provider <provider>` — one flow, two spellings.
    ///
    /// Nothing here awaits: opening a credential store is a keychain
    /// round-trip, a settings file and a provider's endpoint resolution, and
    /// awaiting that on the loop is a terminal that does not draw, does not
    /// scroll and does not answer `Ctrl+C` while it happens. The exclusive
    /// surface goes up first — so a refusal is visible and nothing has been
    /// read when one happens — and the reading is a cancellable task.
    pub(crate) async fn cmd_login(&mut self, requested: Option<&str>) {
        // The gate and the busy check both come before the profile is opened:
        // a refused command must not have read anything at all.
        if !self.auth_allowed("Signing in") {
            return;
        }
        let requested = match requested.map(str::trim).filter(|name| !name.is_empty()) {
            Some(name) => match ProviderIdentity::parse(name) {
                Some(identity) => Some(identity),
                None => {
                    // Refused where it was typed rather than rewritten into a
                    // provider that happens to work.
                    self.notice(
                        format!(
                            "'{}' is not one of this product's providers. Choose one of: {}.",
                            sanitize(name, 40),
                            ProviderIdentity::ALL
                                .iter()
                                .map(|identity| identity.engine_id())
                                .collect::<Vec<_>>()
                                .join(", ")
                        ),
                        NoticeLevel::Warning,
                    );
                    return;
                }
            },
            None => None,
        };

        // Claimed before the store is touched. An exclusive surface that
        // cannot be pushed means a prompt is waiting for an answer, and a
        // sign-in that read the profile and *then* refused would have probed
        // the operator's credentials for a command that did nothing.
        if !self.surfaces.push(Box::new(AuthChallengeSurface::new(
            "Connect an account",
            vec![
                "Reading the accounts saved on this machine.".to_owned(),
                String::new(),
                "Nothing has been changed. Esc leaves without connecting anything.".to_owned(),
            ],
            true,
        ))) {
            self.notice(
                "A prompt is already open and must be answered first; nothing was changed.",
                NoticeLevel::Warning,
            );
            return;
        }

        let port = self.auth_port.clone();
        self.auth.flow.begin_opening(move |generation, tx| async move {
            let result = match port.open(Opening::Strict).await {
                Ok(service) => {
                    let disclosure = disclosure(&service, requested).await;
                    let saved_domain = service
                        .status()
                        .await
                        .ok()
                        .and_then(|status| status.github_enterprise_domain);
                    Ok(Opened::Login { service, disclosure, saved_domain, requested })
                }
                Err(message) => Err(message),
            };
            let _ = tx.send(AuthEvent::Opened { generation, result: Box::new(result) });
        });
        self.dirty = true;
    }

    /// `/logout [provider]`.
    ///
    /// The surface is not decoration. Between "the engine was stopped" and
    /// "the credential was deleted" the transaction cannot be cancelled, and a
    /// live composer in that window is an invitation to start something —
    /// a `/resume`, a sessions browser — that would spawn a child on the
    /// credential being removed. The surface says what is happening and the
    /// engine-start guard refuses the rest.
    pub(crate) async fn cmd_logout(&mut self, requested: Option<&str>) {
        if !self.auth_allowed("Signing out") {
            return;
        }
        let identity = match requested.map(str::trim).filter(|name| !name.is_empty()) {
            Some(name) => match ProviderIdentity::parse(name) {
                Some(identity) => Some(identity),
                None => {
                    self.notice(
                        format!("'{}' is not one of this product's providers.", sanitize(name, 40)),
                        NoticeLevel::Warning,
                    );
                    return;
                }
            },
            None => None,
        };

        if !self.surfaces.push(Box::new(AuthChallengeSurface::new(
            "Signing out",
            vec![
                "Stopping the engine this session started, then removing the credential."
                    .to_owned(),
                String::new(),
                "The conversation and anything you have typed are kept.".to_owned(),
            ],
            true,
        ))) {
            self.notice(
                "A prompt is already open and must be answered first; nothing was changed.",
                NoticeLevel::Warning,
            );
            return;
        }

        let port = self.auth_port.clone();
        self.auth.flow.begin_opening(move |generation, tx| async move {
            // Reporting and signing out must keep working *because* a saved
            // deployment is broken — that is exactly when they are needed.
            let result = port
                .open(Opening::Degraded)
                .await
                .map(|service| Opened::Logout { service, identity });
            let _ = tx.send(AuthEvent::Opened { generation, result: Box::new(result) });
        });
        self.dirty = true;
    }

    /// The report from a sign-out that has finished.
    fn on_logged_out(&mut self, report: Result<LogoutReport, LogoutFailure>) {
        self.retire_auth_surface();
        match report {
            Ok(report) => {
                self.notice(render_logout(&report), NoticeLevel::Info);
                if report.environment_api_key_still_available {
                    // Said, never acted on: reconnecting to the exported key
                    // would undo the sign-out the user just asked for.
                    self.notice(
                        "ANTHROPIC_API_KEY is still exported in this environment. Coda has \
                         *not* reconnected with it — run /login to choose an account when you \
                         want one.",
                        NoticeLevel::Info,
                    );
                }
                self.notice(
                    "Disconnected. The conversation and anything you had typed are kept; \
                     /login, /provider and /setup still work.",
                    NoticeLevel::Info,
                );
            }
            Err(failure) => self.notice(render_logout_failure(&failure), NoticeLevel::Error),
        }
    }

    /// `/setup` — the same flow, entered from the wizard rather than a name.
    pub(crate) async fn cmd_setup(&mut self) {
        if let Some(message) = refusal(self.access_mode, "Connecting an account") {
            self.notice(message, NoticeLevel::Warning);
            return;
        }
        self.cmd_login(None).await;
    }

    /// Starts the read-only account report `/provider` prints.
    ///
    /// A task, for the same reason a sign-in's opening is one: this reads a
    /// keychain and a settings file, and the loop must keep drawing while it
    /// does. It is not part of the flow — it writes nothing, stops nothing and
    /// starts nothing — so it never refuses a sign-in or a resume.
    pub(in crate::app) fn report_accounts(&mut self, preamble: String) {
        if self.auth.reporting.is_some() {
            self.notice(
                "The saved accounts are already being read; the answer is on its way.",
                NoticeLevel::Warning,
            );
            return;
        }
        let port = self.auth_port.clone();
        let tx = self.auth.flow.sender();
        self.auth.reporting = Some(tokio::spawn(async move {
            let mut out = preamble;
            match port.open(Opening::Degraded).await {
                Ok(service) => match service.status().await {
                    Ok(status) => {
                        let context =
                            status.provider_context_error.is_none().then(|| service.copilot_context());
                        out.push_str(&coda_boot::auth_cli::render_status(
                            &status,
                            context.as_deref(),
                        ));
                        out.push_str(
                            "\n\nUse /login <provider> to connect a different account, or \
                             /logout to disconnect.",
                        );
                    }
                    Err(failure) => out.push_str(&format!(
                        "The stored authentication could not be read: {failure}. Nothing was \
                         changed."
                    )),
                },
                Err(message) => out.push_str(&message),
            }
            let _ = tx.send(AuthEvent::Reported { text: out });
        }));
        self.dirty = true;
    }

    /// The profile opened — or did not — for a flow that has not asked
    /// anything yet.
    fn on_opened(&mut self, result: Result<Opened, String>) {
        self.auth.flow.settle_opening();
        self.dirty = true;
        let opened = match result {
            Ok(opened) => opened,
            Err(message) => {
                self.retire_auth_surface();
                self.auth.flow.cancel();
                self.notice(message, NoticeLevel::Error);
                return;
            }
        };
        match opened {
            Opened::Login { service, disclosure, saved_domain, requested } => {
                let surface =
                    AuthChoiceSurface::open("Connect an account", disclosure, requested, saved_domain);
                if !self.surfaces.replace_top(Box::new(surface)) {
                    // The waiting surface is gone: something else took the
                    // screen while the profile was being read. Nothing was
                    // written, so the attempt simply ends.
                    self.auth.flow.cancel();
                    self.notice(
                        "The sign-in surface was closed, so the sign-in was cancelled. Nothing \
                         was changed.",
                        NoticeLevel::Warning,
                    );
                    return;
                }
                // Held so the commit and the check use the profile this flow
                // opened, not one re-read after the fact.
                self.auth.flow.hold_service(service);
            }
            Opened::Logout { service, identity } => {
                // Before the credential goes: a child that outlived its token
                // is a child still spending it. Stopping and *awaiting* it
                // happens on the sign-out's own task, so the loop keeps
                // drawing and Ctrl+C keeps working while a process goes away.
                let stopped = self.stop_engine_for_auth("Signing out");
                self.surfaces.replace_top(Box::new(AuthChallengeSurface::new(
                    "Signing out",
                    vec![
                        "Removing the credential.".to_owned(),
                        String::new(),
                        "This step is not cancellable.".to_owned(),
                    ],
                    false,
                )));
                self.auth.flow.logout(service, identity, stopped);
            }
        }
    }

    /// Whether a connection-changing command may run at all.
    ///
    /// Every reason to refuse is checked here, *before* a credential store or
    /// a settings file is opened: a refusal that had already read the
    /// operator's profile would be a refusal in name only.
    fn auth_allowed(&mut self, what: &str) -> bool {
        if let Some(message) = refusal(self.access_mode, what) {
            self.notice(message, NoticeLevel::Warning);
            return false;
        }
        if self.auth.is_active() {
            self.notice(
                "A sign-in is already in progress; finish or cancel it first.",
                NoticeLevel::Warning,
            );
            return false;
        }
        if self.state.is_busy() || self.turn.is_some() {
            // Never implicitly cancelled: the turn is the user's work, and
            // silently interrupting it to change accounts is a worse outcome
            // than being told to wait.
            self.notice(
                "A turn is running. Changing the connection would interrupt it, so nothing was \
                 changed — stop it with Esc, or wait for it to finish.",
                NoticeLevel::Warning,
            );
            return false;
        }
        true
    }

    // ── Surface actions ──────────────────────────────────────────────────────

    /// The choice surface submitted: read it, close it, and start preparing.
    pub(in crate::app) fn start_prepared_login(&mut self) {
        // Read before closing: an answer that cannot be acted on must leave
        // the form exactly where it was, with everything typed still in it.
        let choice = {
            let Some(surface) = self.surfaces.top() else {
                return;
            };
            let Some(form) = surface.as_any().downcast_ref::<AuthChoiceSurface>() else {
                self.notice("Could not sign in: unexpected surface.", NoticeLevel::Error);
                return;
            };
            form.choice()
        };
        let choice = match choice {
            Ok(choice) => choice,
            // The surface refuses an unusable answer itself and stays open, so
            // reaching here means something else emitted the action. Refused
            // rather than resolved into a quieter default: an explicit choice
            // is never silently rewritten.
            Err(reason) => {
                self.notice(reason, NoticeLevel::Warning);
                self.dirty = true;
                return;
            }
        };
        self.surfaces.pop();
        let Some(service) = self.auth.flow.service() else {
            self.notice(
                "The sign-in lost its profile before it started; nothing was changed.",
                NoticeLevel::Error,
            );
            return;
        };

        let environment_only = choice.use_environment_key;
        let identity = choice.identity;
        let request = match identity {
            ProviderIdentity::ClaudeAi => LoginRequest::claude_ai(),
            ProviderIdentity::GithubCopilot => LoginRequest::copilot(choice.deployment.clone()),
            ProviderIdentity::AnthropicApiKey => LoginRequest::api_key(if environment_only {
                ApiKeySource::Environment
            } else {
                ApiKeySource::Prompt
            }),
        };
        let key = choice
            .api_key
            .filter(|key| !key.trim().is_empty())
            .map(|key| Secret::new(key.trim().to_owned()));
        if identity == ProviderIdentity::AnthropicApiKey && !environment_only && key.is_none() {
            self.notice("No API key was entered; nothing was changed.", NoticeLevel::Warning);
            self.auth.flow.cancel();
            self.dirty = true;
            return;
        }

        // Where this sign-in will actually go, for the account that was
        // *chosen* — not for the one the form happened to open on — and said
        // before the preparation sends its first byte.
        let mut lines = vec![format!("Signing in to {}.", identity.label())];
        lines.extend(destination_lines(&service, identity, choice.deployment.as_ref()));
        lines.push(String::new());
        lines.push(
            "Nothing has been saved, and the engine you are connected to is still running."
                .to_owned(),
        );
        let waiting = AuthChallengeSurface::new("Connecting", lines, true);
        if !self.surfaces.push(Box::new(waiting)) {
            // Refused rather than silently left waiting on a promise nobody
            // can see: no task is started at all.
            self.notice(
                "A prompt opened first and must be answered; the sign-in was not started and \
                 nothing was changed.",
                NoticeLevel::Warning,
            );
            self.auth.flow.cancel();
            self.dirty = true;
            return;
        }

        let opener = self.auth.opener();
        self.auth.flow.begin(
            service,
            request,
            key,
            Target { identity, deployment: choice.deployment, environment_only },
            opener,
        );
        self.dirty = true;
    }

    /// Escape, or a cancel from the challenge surface.
    pub(in crate::app) fn cancel_auth(&mut self) {
        if self.auth.flow.is_committing() {
            // There is nothing to cancel that would not leave the profile
            // half-written, and the surface already says so.
            self.notice(
                "The credential is being written and cannot be cancelled; it will finish in a \
                 moment.",
                NoticeLevel::Warning,
            );
            self.dirty = true;
            return;
        }
        self.retire_auth_surface();
        let was_live = self.auth.flow.cancel();
        if was_live {
            // Whatever was in flight — the profile being opened, a preparation
            // waiting on a provider — is now stale and will be dropped; saying
            // this here is what makes an immediate Escape visibly do
            // something.
            self.notice(
                "Sign-in cancelled. Nothing was saved and the connection is unchanged.",
                NoticeLevel::Info,
            );
        }
        self.dirty = true;
    }

    fn retire_auth_surface(&mut self) {
        while self.surfaces.top().is_some_and(|surface| {
            surface.as_any().is::<AuthChoiceSurface>()
                || surface.as_any().is::<AuthChallengeSurface>()
        }) {
            self.surfaces.pop();
        }
    }

    // ── Events ───────────────────────────────────────────────────────────────

    pub(in crate::app) async fn on_auth_event(&mut self, event: AuthEvent) {
        // A report belongs to no flow: it changed nothing, so nothing about a
        // retracted sign-in makes it wrong. Answered before the gate.
        if let AuthEvent::Reported { text } = event {
            self.auth.reporting = None;
            self.output(text);
            self.dirty = true;
            return;
        }
        // A flow the user retracted may still have tasks in flight. Their
        // results are dropped here rather than being adopted by whatever
        // started next: a preparation that finished after Escape must not
        // commit, and a boot belonging to an abandoned attempt must not become
        // this session's engine.
        if !self.auth.flow.is_current(event.generation()) {
            match event {
                AuthEvent::Flow(FlowEvent::Committed { .. } | FlowEvent::LoggedOut { .. }) => {
                    self.auth.flow.settle()
                }
                // An engine started for an attempt that no longer exists is a
                // child this process owns and nobody will ever talk to. It is
                // stopped here rather than left running on a credential the
                // user retracted.
                AuthEvent::Booted { result, .. } => {
                    self.auth.booting = None;
                    if let Ok(booted) = *result {
                        let grace = self.shutdown_grace;
                        tokio::spawn(async move {
                            let _ = booted.engine.shutdown(grace).await;
                        });
                    }
                }
                // Opened for an attempt that was retracted. The service is
                // dropped unused: nothing was written, nothing was stopped,
                // and no surface may appear for a command the user left.
                _ => {}
            }
            return;
        }
        match event {
            AuthEvent::Reported { .. } => {}
            AuthEvent::Opened { result, .. } => self.on_opened(*result),
            AuthEvent::Flow(FlowEvent::Challenge { title, lines, .. }) => {
                // Replaced, not appended: one copy of a device code exists at
                // a time and it goes away with the surface.
                if !self.surfaces.replace_top(Box::new(AuthChallengeSurface::new(
                    title, lines, true,
                ))) {
                    self.cancel_auth();
                    self.notice(
                        "The sign-in surface was closed, so the sign-in was cancelled.",
                        NoticeLevel::Warning,
                    );
                }
                self.dirty = true;
            }
            AuthEvent::Flow(FlowEvent::Prepared { result: None, .. }) => {
                self.retire_auth_surface();
                self.auth.flow.cancel();
                self.dirty = true;
            }
            AuthEvent::Flow(FlowEvent::Prepared { result: Some(result), .. }) => {
                if self.auth.flow.target().is_none() {
                    // Cancelled after the preparation had already finished but
                    // before this reached the loop. The credential in hand is
                    // simply dropped: it was never written, and honouring a
                    // decision the user has retracted would be worse than
                    // losing a token exchange they can repeat.
                    self.retire_auth_surface();
                    self.auth.flow.clear();
                    self.dirty = true;
                    return;
                }
                match *result {
                    Ok(prepared) => self.start_commit(prepared),
                    Err(failure) => {
                        self.retire_auth_surface();
                        // Nothing was stopped and nothing was written: the
                        // engine that was running is still the one running.
                        self.notice(render_prepare_failure(&failure), NoticeLevel::Error);
                        self.auth.flow.cancel();
                        self.dirty = true;
                    }
                }
            }
            AuthEvent::Flow(FlowEvent::Committed { outcome, .. }) => {
                self.auth.flow.settle();
                self.on_committed(*outcome);
            }
            AuthEvent::Flow(FlowEvent::LoggedOut { report, .. }) => {
                self.auth.flow.settle();
                self.auth.flow.clear();
                self.on_logged_out(*report);
                self.dirty = true;
            }
            AuthEvent::Booted { result, .. } => {
                self.auth.booting = None;
                match *result {
                    Ok(booted) => {
                        self.adopt_engine(booted);
                        self.notice(
                            "Connected. Reading the provider, model and effort back from the \
                             new engine.",
                            NoticeLevel::Info,
                        );
                        self.load_models().await;
                    }
                    Err(error) => {
                        let (what, account) = match self.auth.flow.target() {
                            Some(target) if target.environment_only => (
                                "Provider selection saved; engine startup failed",
                                target.identity.label(),
                            ),
                            Some(target) => {
                                ("Credentials saved; engine startup failed", target.identity.label())
                            }
                            None => ("Credentials saved; engine startup failed", "the account"),
                        };
                        // Emphatically not "the sign-in failed": it did not.
                        self.notice(
                            format!(
                                "{what}: {error}. {account} is signed in on this machine and \
                                 nothing was rolled back — the conversation and your draft are \
                                 kept. Try /provider again, or restart Coda."
                            ),
                            NoticeLevel::Error,
                        );
                    }
                }
                self.auth.flow.clear();
                self.dirty = true;
            }
            AuthEvent::Verified { report, .. } => {
                self.auth.verifying = None;
                match report {
                    Some(report) => self.notice(
                        report.to_string(),
                        if report.is_rejected() {
                            NoticeLevel::Error
                        } else if report.is_verified() {
                            NoticeLevel::Info
                        } else {
                            NoticeLevel::Warning
                        },
                    ),
                    None => self.notice(
                        "The credential is saved, but it could not be checked from here. Use \
                         /provider to see what this profile is set to.",
                        NoticeLevel::Warning,
                    ),
                }
                self.dirty = true;
            }
        }
    }

    /// Stop, then commit — on a task, so the loop keeps drawing.
    ///
    /// Everything that has to happen *before* the profile is mutated happens
    /// inside that task: the engine this application owns is asked to stop and
    /// then awaited, because a child that outlives the credential is a child
    /// still spending it. The transaction itself is never cancelled.
    fn start_commit(&mut self, prepared: PreparedLogin) {
        if self.auth.flow.service().is_none() {
            self.notice("The sign-in lost its profile; nothing was written.", NoticeLevel::Error);
            self.retire_auth_surface();
            self.auth.flow.cancel();
            return;
        }
        let identity = prepared.identity();
        let environment_only = prepared.uses_environment_key();

        // Disclosed before the transaction, by name, while it is still true
        // that nothing has been removed.
        let replacement = if environment_only {
            render_environment_replacement(prepared.replaces())
        } else {
            render_replacement(identity, prepared.replaces())
        };
        if !replacement.is_empty() {
            self.notice(replacement, NoticeLevel::Warning);
        }

        // From here there is nothing to cancel that would not leave the
        // profile half-written, and the surface says so.
        self.surfaces.replace_top(Box::new(AuthChallengeSurface::new(
            "Saving",
            vec![
                "Stopping the engine, then saving the credential.".to_owned(),
                String::new(),
                "This step is not cancellable.".to_owned(),
            ],
            false,
        )));

        let stopped = self.stop_engine_for_auth("Signing in");
        self.auth.flow.commit(prepared, stopped);
        self.dirty = true;
    }

    /// What the credential transaction did, once it has finished.
    fn on_committed(&mut self, outcome: CommitOutcome) {
        let target = self.auth.flow.target().cloned();
        let service = self.auth.flow.service();
        self.retire_auth_surface();
        self.notice(render_commit(&outcome), NoticeLevel::Info);

        match outcome {
            CommitOutcome::Committed { .. } => {
                let (identity, deployment, environment_only) = match target {
                    Some(target) => (target.identity, target.deployment, target.environment_only),
                    None => {
                        self.notice(
                            "The credential was saved, but this session lost track of which \
                             account it belongs to; no engine was started. Run /provider.",
                            NoticeLevel::Error,
                        );
                        self.auth.flow.clear();
                        self.dirty = true;
                        return;
                    }
                };
                if environment_only {
                    self.notice(
                        "No API key was stored: Coda will use the ANTHROPIC_API_KEY exported in \
                         the environment it runs in, so a shell without that variable is not \
                         signed in.",
                        NoticeLevel::Info,
                    );
                }
                if let Some(service) = &service {
                    // Said once, after the fact, about the account that is
                    // actually connected — hosts only, never a URL.
                    self.notice(
                        committed_destination(service, identity, deployment.as_ref()),
                        NoticeLevel::Info,
                    );
                }
                self.spawn_engine_boot(identity, deployment.as_ref());
                if let Some(service) = service {
                    self.spawn_verification(service, identity, environment_only);
                }
            }
            // Nothing was written, or it was put back. The engine is stopped
            // either way, and saying so is the honest report.
            CommitOutcome::Superseded { .. } | CommitOutcome::Failed { .. } => {
                self.notice(
                    "No engine was started, so this session is disconnected. The conversation \
                     and your draft are kept; run /provider to try again.",
                    NoticeLevel::Warning,
                );
                self.auth.flow.clear();
            }
            // Neither the old nor the new connection may be claimed to work,
            // and no engine may be started on the strength of either.
            CommitOutcome::Indeterminate { .. } | CommitOutcome::RestorationFailed { .. } => {
                self.notice(
                    "This session is disconnected and no engine was started. Check `coda auth \
                     status` before signing in again; the conversation and your draft are kept.",
                    NoticeLevel::Error,
                );
                self.auth.flow.clear();
            }
        }
        self.dirty = true;
    }

    /// Stops and awaits the engine this application owns.
    ///
    /// Both halves matter. The outstanding requests are declined for *this*
    /// instance first, so a decision the engine is blocked on is answered
    /// rather than abandoned to a `Drop` that cannot tell a live request from
    /// a replaced one. Then the process is asked to stop over the connection
    /// and **waited for**, which is the part that makes the credential safe to
    /// delete afterwards.
    ///
    /// The bookkeeping happens now — it is local and instant — and the waiting
    /// happens in the returned future, which its caller hands to the task that
    /// will do the writing. That is what keeps the loop responsive while a
    /// process is going away, without letting the write start early.
    fn stop_engine_for_auth(&mut self, why: &str) -> BoxFuture<'static, ()> {
        if !self.engine_connected {
            return Box::pin(async {});
        }
        self.notice(
            format!("{why}: stopping the engine this session started, and waiting for it."),
            NoticeLevel::Info,
        );
        // A restart staged but not yet swapped in is still a child this
        // process started.
        let staged = self.restarted.take().map(|(engine, _)| engine);
        let owned = self.owned_engine.take();

        let instance = self.view.engine_instance_id().map(str::to_string);
        self.pending.decline_live(instance.as_deref());
        self.pending.clear();
        self.set_engine_connected(false);

        let connection = self.connection.clone();
        let grace = self.shutdown_grace;
        Box::pin(async move {
            // Over the connection first, so the engine can close its MCP
            // servers and flush the session instead of being killed after a
            // grace period.
            let _ = tokio::time::timeout(
                grace,
                connection.request(
                    coda_proto::messages::method::SHUTDOWN,
                    Some(serde_json::json!({})),
                ),
            )
            .await;
            for engine in [staged, owned].into_iter().flatten() {
                let _ = engine.shutdown(grace).await;
            }
        })
    }

    fn spawn_engine_boot(
        &mut self,
        identity: ProviderIdentity,
        deployment: Option<&CopilotDeploymentChoice>,
    ) {
        let command = engine_command_for(&self.engine_command, identity, deployment);
        self.engine_command = command.clone();
        let session_id = self.state.session_id.clone();
        let tx = self.auth.flow.sender();
        let generation = self.auth.flow.generation();
        let handle = tokio::spawn(async move {
            let intent = match session_id {
                Some(id) => coda_boot::SessionIntent::Resume(id),
                None => coda_boot::SessionIntent::New,
            };
            let mut result = crate::api::boot::boot(command.clone(), &intent, "coda-tui").await;
            // A session that was never written cannot be resumed; the engine
            // is right to refuse the id, and starting fresh is better than
            // reporting a sign-in that worked as a failure.
            if matches!(result, Err(crate::api::boot::BootError::NotFound(_))) {
                result =
                    crate::api::boot::boot(command, &coda_boot::SessionIntent::New, "coda-tui")
                        .await;
            }
            let _ = tx.send(AuthEvent::Booted {
                generation,
                result: Box::new(result.map_err(|e| e.to_string())),
            });
        });
        self.auth.booting = Some(handle);
    }

    fn spawn_verification(
        &mut self,
        service: Arc<AuthService>,
        identity: ProviderIdentity,
        environment_only: bool,
    ) {
        let tx = self.auth.flow.sender();
        let generation = self.auth.flow.generation();
        let handle = tokio::spawn(async move {
            // One uncached model listing, never a completion, through a
            // credential source built for this probe alone — the same helper
            // `coda auth login` uses, so the two cannot disagree about what
            // "verified" means.
            let endpoint = service.anthropic_endpoint().ok();
            let report = coda_boot::auth_cli::verify_login(
                &service,
                identity,
                environment_only,
                endpoint.as_ref(),
            )
            .await;
            let _ = tx.send(AuthEvent::Verified { generation, report: report.map(Box::new) });
        });
        self.auth.verifying = Some(handle);
    }

    // ── Teardown ─────────────────────────────────────────────────────────────

    /// Closes the authentication flow down as the application exits.
    ///
    /// A preparation is cancelled — it has written nothing, so dropping it is
    /// free — and a commit (or a sign-out) is *awaited*, because a transaction
    /// abandoned half-written would leave a profile this process then reported
    /// as unchanged. Nothing may be written after this returns.
    pub(crate) async fn close_auth_out(&mut self) {
        if let Some(handle) = self.auth.booting.take() {
            handle.abort();
        }
        if let Some(handle) = self.auth.verifying.take() {
            handle.abort();
        }
        // A read that changes nothing: dropped, not waited for.
        if let Some(handle) = self.auth.reporting.take() {
            handle.abort();
        }
        self.auth.flow.close().await;
    }

    // ── First run ────────────────────────────────────────────────────────────

    /// What this profile looks like before an engine is started.
    ///
    /// Asked through the shared selector, never by guessing at files: a
    /// credential that cannot be read is not an absent one, and a settings
    /// file that will not parse is not a first run.
    ///
    /// Bounded, and for the same reason every engine read is: this runs
    /// before the loop starts, so an unbounded wait on a credential store
    /// that is slow — a locked keychain, a keyring daemon that never answers
    /// — held the whole terminal, and there was no frame on screen to say
    /// what it was waiting for. A store that does not answer in time is not
    /// "you are signed out", so like a store that fails to open it says
    /// nothing and leaves the engine's own evidence to speak.
    pub(crate) async fn preflight(&mut self) {
        // An API-only session has nothing to preflight and says nothing: a
        // healthy remote engine must start quietly. Checked first, so this
        // path opens nothing at all.
        if refusal(self.access_mode, "Connecting an account").is_some() {
            return;
        }
        let bound = self.metadata_timeout;
        let Ok(Ok(service)) =
            tokio::time::timeout(bound, self.auth_port.open(Opening::Degraded)).await
        else {
            // Opening the store failed, or took longer than this client will
            // wait. That is not "you are signed out", so it does not open a
            // wizard; the engine is already connected and running, which is
            // the evidence that matters.
            return;
        };
        let Ok(state) =
            tokio::time::timeout(bound, crate::setup::first_run_state(service.as_ref())).await
        else {
            return;
        };
        match state {
            crate::setup::FirstRun::Ready => {}
            crate::setup::FirstRun::NoCredentials => {
                self.notice(crate::setup::WELCOME_TEXT, NoticeLevel::Info);
            }
            crate::setup::FirstRun::SelectedMissing { reason, .. } => {
                self.notice(reason, NoticeLevel::Warning)
            }
            crate::setup::FirstRun::Unusable(reason) => self.notice(reason, NoticeLevel::Warning),
        }
    }
}


#[cfg(test)]
mod lifecycle {
    use super::*;
    use crate::app::serve::tests::{app_with, notices, Harness};
    use crate::local::AccessMode;
    use async_trait::async_trait;
    use coda_auth::error::AuthError;
    use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
    use std::path::{Path, PathBuf};
    use std::sync::Mutex;
    use std::time::Duration;

    /// The key a test types. Asserted absent from everything durable.
    const TEST_KEY: &str = "sk-ant-test-key-do-not-log";

    // ── A provider that is a socket this test owns ───────────────────────────

    /// What the loopback "provider" does with a request.
    #[derive(Clone, Copy, PartialEq, Eq)]
    enum Provider {
        /// Answers `/v1/models` with one model.
        Models,
        /// Refuses the credential outright.
        Rejects,
        /// Accepts the connection and never answers, so a preparation is
        /// genuinely still running when the test cancels it.
        Hangs,
        /// A GitHub device grant that authorizes on the first poll, exchanges
        /// for a Copilot token, and serves models to it.
        DeviceGrants,
        /// A GitHub device grant that never authorizes, so the poller is
        /// genuinely still polling when the test cancels it.
        DevicePending,
        /// A Claude.ai token endpoint that refuses the exchange, which is what
        /// "the provider said no" actually looks like.
        ClaudeRefuses,
    }

    /// Every request the fixture served, oldest first, as `METHOD path`.
    type Wire = Arc<Mutex<Vec<String>>>;

    /// Starts the fixture, and returns both the base URL to point the service
    /// at and the log of what it was actually asked for — which is how "the
    /// poller stopped" is proven rather than assumed.
    async fn provider_recording(
        behaviour: Provider,
    ) -> ((String, tokio::task::JoinHandle<()>), Wire) {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let port = listener.local_addr().expect("addr").port();
        let wire: Wire = Arc::new(Mutex::new(Vec::new()));
        let recorder = Arc::clone(&wire);
        let handle = tokio::spawn(async move {
            loop {
                let Ok((mut socket, _)) = listener.accept().await else { return };
                if behaviour == Provider::Hangs {
                    // Held open deliberately: dropping it would answer with a
                    // transport error, which is not "still waiting".
                    tokio::spawn(async move {
                        tokio::time::sleep(Duration::from_secs(120)).await;
                        drop(socket);
                    });
                    continue;
                }
                let recorder = Arc::clone(&recorder);
                tokio::spawn(async move {
                    use tokio::io::{AsyncReadExt, AsyncWriteExt};
                    let mut buffer = [0u8; 8192];
                    let read = socket.read(&mut buffer).await.unwrap_or(0);
                    let request = String::from_utf8_lossy(&buffer[..read]).into_owned();
                    let line = request.lines().next().unwrap_or_default().to_owned();
                    let mut parts = line.split_whitespace();
                    let method = parts.next().unwrap_or_default().to_owned();
                    let target = parts.next().unwrap_or_default().to_owned();
                    let path = target.split(['?', '#']).next().unwrap_or(&target).to_owned();
                    recorder.lock().expect("wire").push(format!("{method} {path}"));

                    let json = |status: &str, body: String| {
                        format!(
                            "HTTP/1.1 {status}\r\nContent-Type: application/json\r\n\
                             Content-Length: {}\r\nConnection: close\r\n\r\n{body}",
                            body.len()
                        )
                    };
                    let models = || {
                        json(
                            "200 OK",
                            "{\"data\":[{\"id\":\"claude-test\"}]}".to_owned(),
                        )
                    };
                    let response = match path.as_str() {
                        "/login/device/code" => json(
                            "200 OK",
                            "{\"device_code\":\"fixture-device-code\",\"user_code\":\
                             \"CODA-FIXTURE\",\"verification_uri\":\
                             \"https://example.invalid/device\",\"expires_in\":900,\
                             \"interval\":1}"
                                .to_owned(),
                        ),
                        "/login/oauth/access_token" => match behaviour {
                            Provider::DeviceGrants => json(
                                "200 OK",
                                "{\"access_token\":\"fixture-durable-token\",\"token_type\":\
                                 \"bearer\",\"scope\":\"read:user\"}"
                                    .to_owned(),
                            ),
                            // Never authorized: the poller keeps asking, which
                            // is what makes a cancellation observable.
                            _ => json("200 OK", "{\"error\":\"authorization_pending\"}".to_owned()),
                        },
                        "/copilot_internal/v2/token" => json(
                            "200 OK",
                            format!(
                                "{{\"token\":\"fixture-copilot-token\",\"expires_at\":{}}}",
                                std::time::SystemTime::now()
                                    .duration_since(std::time::UNIX_EPOCH)
                                    .map(|d| d.as_secs() as i64)
                                    .unwrap_or(0)
                                    + 86_400
                            ),
                        ),
                        // The Claude.ai token exchange. A refusal here is a
                        // real provider "no", not a transport error.
                        "/v1/oauth/token" => match behaviour {
                            Provider::ClaudeRefuses => {
                                json("400 Bad Request", "{\"error\":\"invalid_grant\"}".to_owned())
                            }
                            _ => json(
                                "200 OK",
                                "{\"access_token\":\"fixture-claude-token\",\"refresh_token\":\
                                 \"fixture-claude-refresh\",\"expires_in\":3600,\"scope\":\
                                 \"user:inference\"}"
                                    .to_owned(),
                            ),
                        },
                        _ => match behaviour {
                            Provider::Rejects => {
                                json("401 Unauthorized", "{\"error\":\"no\"}".to_owned())
                            }
                            _ => models(),
                        },
                    };
                    let _ = socket.write_all(response.as_bytes()).await;
                    let _ = socket.flush().await;
                });
            }
        });
        ((format!("http://127.0.0.1:{port}"), handle), wire)
    }

    // ── A coordinator that observes the world at the moment of the commit ────

    /// A child this test owns, and an in-process peer that answers for it.
    ///
    /// A `Booted` carries two independent things — the process this session
    /// becomes responsible for, and the connection it talks over — and that is
    /// exactly what this builds. The child is real, so ownership and shutdown
    /// are real; the peer is in-process, so no engine, provider or credential
    /// is involved in proving what adopting one does.
    #[cfg(windows)]
    async fn fake_booted() -> (crate::api::boot::Booted, coda_client::ConnectionTasks, Wire) {
        use coda_proto::{encode_frame, FrameDecoder};
        let command = coda_client::EngineCommand::new("powershell.exe")
            .arg("-NoProfile")
            .arg("-NonInteractive")
            .arg("-Command")
            .arg("Start-Sleep -Seconds 120");
        let (engine, _child_inbound) = coda_client::Engine::spawn(command).expect("a child starts");

        let (client_side, server_side) = tokio::io::duplex(256 * 1024);
        let (client_read, client_write) = tokio::io::split(client_side);
        let (connection, inbound, tasks) = coda_client::connect(client_read, client_write);

        let asked: Wire = Arc::new(Mutex::new(Vec::new()));
        let recorder = Arc::clone(&asked);
        tokio::spawn(async move {
            use tokio::io::{AsyncReadExt, AsyncWriteExt};
            let (mut read, mut write) = tokio::io::split(server_side);
            let mut decoder = FrameDecoder::new();
            let mut buffer = [0u8; 8192];
            loop {
                let count = match read.read(&mut buffer).await {
                    Ok(0) | Err(_) => return,
                    Ok(count) => count,
                };
                decoder.feed(&buffer[..count]);
                while let Ok(Some(frame)) = decoder.next_frame() {
                    let Ok(request) = serde_json::from_slice::<serde_json::Value>(&frame) else {
                        continue;
                    };
                    let Some(method) = request["method"].as_str() else { continue };
                    recorder.lock().expect("wire").push(method.to_owned());
                    let response = serde_json::json!({
                        "jsonrpc": "2.0",
                        "id": request["id"],
                        "result": { "models": [] },
                    });
                    let bytes = serde_json::to_vec(&response).expect("response");
                    if write.write_all(&encode_frame(&bytes)).await.is_err() {
                        return;
                    }
                }
            }
        });

        let booted = crate::api::boot::Booted {
            engine,
            inbound,
            connection,
            initialize: coda_proto::messages::InitializeResult {
                protocol_version: "1".into(),
                session_id: "resumed-session".into(),
                server_info: "in-process".into(),
                telemetry_log_path: None,
                contract_version: Some(coda_proto::messages::CONTRACT_VERSION.into()),
                engine_instance_id: Some("e2".into()),
                event_cursor: Some(0),
                capabilities: None,
            },
            session_id: "resumed-session".into(),
            forked_from: None,
            notices: Vec::new(),
        };
        (booted, tasks, asked)
    }

    /// A launch that leaves a file behind if it ever runs.
    ///
    /// The evidence for "no child was started": a marker a spawned process
    /// creates, rather than a count of something this test controls.
    #[cfg(windows)]
    fn marker_command(marker: &Path) -> coda_client::EngineCommand {
        coda_client::EngineCommand::new("powershell.exe")
            .arg("-NoProfile")
            .arg("-NonInteractive")
            .arg("-Command")
            .arg(format!(
                "New-Item -ItemType File -Force -Path '{}' | Out-Null",
                marker.display()
            ))
    }

    /// Records, at the instant the credential transaction enters its section,
    /// whether the engine child was already gone.
    ///
    /// This is the barrier the ordering claim rests on. Asserting after the
    /// fact that the process eventually exited would pass even if the
    /// credential had been deleted first, which is the failure that matters:
    /// a child that outlives its credential is a child still spending it.
    #[derive(Debug)]
    struct Barrier {
        inner: coda_auth::coordination::LocalCoordinator,
        held_by_child: PathBuf,
        /// One entry per commit section entered, in order.
        observations: Arc<Mutex<Vec<bool>>>,
    }

    #[async_trait]
    impl coda_auth::coordination::CommitCoordinator for Barrier {
        async fn begin(
            &self,
            key: &str,
        ) -> Result<coda_auth::coordination::CommitGuard, AuthError> {
            let gone = handle_released(&self.held_by_child).await;
            self.observations.lock().expect("barrier").push(gone);
            self.inner.begin(key).await
        }
    }

    /// Whether the exclusively-held file can be opened again — which on
    /// Windows is true only once the process holding it has actually gone.
    ///
    /// Bounded so a handle closed microseconds after the process object is
    /// signalled is not read as "still running"; a child that is genuinely
    /// alive holds it for two minutes and is never mistaken for a dead one.
    async fn handle_released(path: &Path) -> bool {
        for _ in 0..100 {
            if std::fs::OpenOptions::new().read(true).open(path).is_ok() {
                return true;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        false
    }

    /// A child that holds `path` open exclusively until it is stopped.
    ///
    /// Not a `coda serve`: what is under test is *ownership*, and a process
    /// that provably holds an OS resource is stronger evidence than one that
    /// answers a protocol.
    #[cfg(windows)]
    async fn child_holding(path: &Path) -> coda_client::Engine {
        std::fs::write(path, b"held").expect("seed the held file");
        let script = format!(
            "$f=[System.IO.File]::Open('{}','Open','Read','None'); Start-Sleep -Seconds 120",
            path.display()
        );
        let command = coda_client::EngineCommand::new("powershell.exe")
            .arg("-NoProfile")
            .arg("-NonInteractive")
            .arg("-Command")
            .arg(script);
        let (engine, _inbound) = coda_client::Engine::spawn(command).expect("the child starts");
        // The handle has to actually be held before the test can conclude
        // anything from it being free later. Generous, because starting a
        // shell on a loaded machine is not fast and a slow start is not the
        // property under test.
        for _ in 0..1200 {
            if std::fs::OpenOptions::new().read(true).open(path).is_err() {
                return engine;
            }
            tokio::time::sleep(Duration::from_millis(25)).await;
        }
        panic!("the child never took the handle, so the barrier would prove nothing");
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    struct Fixture {
        harness: Harness,
        events: mpsc::UnboundedReceiver<AuthEvent>,
        profile: tempfile::TempDir,
        observations: Arc<Mutex<Vec<bool>>>,
        /// What the loopback provider was actually asked for, in order.
        wire: Wire,
        _provider: tokio::task::JoinHandle<()>,
    }

    impl Fixture {
        async fn new(behaviour: Provider, environment_key: bool) -> Self {
            Self::with_barrier(behaviour, environment_key, None).await
        }

        async fn with_barrier(
            behaviour: Provider,
            environment_key: bool,
            held_by_child: Option<PathBuf>,
        ) -> Self {
            let profile = tempfile::tempdir().expect("temp profile");
            let ((base_url, provider_task), wire) = provider_recording(behaviour).await;

            // Every provider endpoint this fixture can reach is a socket the
            // test owns: the Anthropic host, the GitHub device-code, token and
            // exchange endpoints, and the Copilot inference host. They are set
            // through the *shipping* configuration seams — the same ones an
            // enterprise proxy uses — so what runs is the real flow.
            let mut environment = vec![
                ("ANTHROPIC_BASE_URL".to_owned(), base_url.clone()),
                (
                    "GH_COPILOT_DEVICE_CODE_URL".to_owned(),
                    format!("{base_url}/login/device/code"),
                ),
                (
                    "GH_COPILOT_TOKEN_URL".to_owned(),
                    format!("{base_url}/login/oauth/access_token"),
                ),
                (
                    "GH_COPILOT_COPILOT_TOKEN_URL".to_owned(),
                    format!("{base_url}/copilot_internal/v2/token"),
                ),
                ("GH_COPILOT_API_BASE_URL".to_owned(), base_url.clone()),
            ];
            if environment_key {
                environment.push(("ANTHROPIC_API_KEY".to_owned(), TEST_KEY.to_owned()));
            }
            let claude = coda_auth::provider::claude_ai::ClaudeAiConfig::production("test-client")
                .with_authorize_url(format!("{base_url}/authorize"))
                .with_token_url(format!("{base_url}/v1/oauth/token"));

            let child_was_gone = Arc::new(Mutex::new(Vec::new()));
            let root = profile.path().to_path_buf();
            let observed = Arc::clone(&child_was_gone);
            let port = AuthPort::from_factory(move |opening| {
                let root = root.clone();
                let environment = environment.clone();
                let held = held_by_child.clone();
                let observed = Arc::clone(&observed);
                let claude = claude.clone();
                Box::pin(async move {
                    let storage = coda_auth::store::open_profile_storage(
                        &coda_auth::store::Profile::isolated(&root),
                    )
                    .map_err(|error| error.to_string())?;
                    let coordinator: Arc<dyn coda_auth::coordination::CommitCoordinator> =
                        match held {
                            Some(held_by_child) => Arc::new(Barrier {
                                inner: coda_auth::coordination::LocalCoordinator::new(),
                                held_by_child,
                                observations: observed,
                            }),
                            None => Arc::clone(&storage.coordinator),
                        };
                    let pairs: Vec<(&str, &str)> = environment
                        .iter()
                        .map(|(key, value)| (key.as_str(), value.as_str()))
                        .collect();
                    let builder = AuthService::builder(Arc::clone(&storage.profile), coordinator)
                        .with_settings(Arc::new(
                            coda_boot::settings_store::SettingsFile::at(
                                root.join("settings.json"),
                            ),
                        ))
                        .with_claude_config(claude)
                        // Bounded so a test that never answers a browser
                        // redirect fails in seconds rather than sitting out a
                        // production login window.
                        .with_login_timeout(Duration::from_secs(20))
                        .with_environment(Arc::new(
                            coda_auth::service::MapEnvironment::new(&pairs),
                        )
                            as Arc<dyn coda_auth::service::AuthEnvironment>);
                    match opening {
                        Opening::Strict => builder
                            .build()
                            .await
                            .map(Arc::new)
                            .map_err(|error| error.to_string()),
                        Opening::Degraded => Ok(Arc::new(builder.build_degraded().await)),
                    }
                })
            });

            let mut harness = app_with(AccessMode::TrustedLocal, |_, _| serde_json::json!({}));
            harness.app.set_auth_port(port);
            harness.app.needs_resync = false;
            harness.app.needs_rehydrate = false;
            // Nothing here may open a window on the machine running the suite.
            harness.app.auth.browser_allowed = false;
            // Short enough that a test proving the ordering does not sleep
            // through the production grace period.
            harness.app.shutdown_grace = Duration::from_millis(300);
            let events = harness.app.auth.take_events();
            Fixture {
                harness,
                events,
                profile,
                observations: child_was_gone,
                wire,
                _provider: provider_task,
            }
        }

        /// Runs `/login <provider>` and types `key` into the surface it opens.
        async fn login_with_key(&mut self, provider: &str, key: &str) {
            self.harness.app.cmd_login(Some(provider)).await;
            self.choice_surface().await;
            for ch in key.chars() {
                self.press(KeyCode::Char(ch));
            }
            self.submit().await;
        }

        /// Pumps until the profile the command opened has produced the choice
        /// form, exactly as the loop would.
        ///
        /// Opening a credential store is a task now: the command claims the
        /// screen and reads nothing, and the form appears when the read
        /// answers. A test that typed straight after the command would be
        /// asserting against a loop that does not exist.
        async fn choice_surface(&mut self) {
            for _ in 0..4 {
                if self
                    .harness
                    .app
                    .surfaces
                    .top()
                    .is_some_and(|surface| surface.as_any().is::<AuthChoiceSurface>())
                {
                    return;
                }
                if !self.pump_within(Duration::from_secs(10)).await {
                    break;
                }
            }
            panic!("the sign-in form never opened: {}", self.said());
        }

        fn press(&mut self, code: KeyCode) {
            let _ = self
                .harness
                .app
                .surfaces
                .handle_key(KeyEvent::new(code, KeyModifiers::NONE));
        }

        /// Presses Enter and performs whatever the surface asked for — the
        /// same path the event loop takes.
        async fn submit(&mut self) {
            let outcome = self
                .harness
                .app
                .surfaces
                .handle_key(KeyEvent::new(KeyCode::Enter, KeyModifiers::NONE));
            if let crate::surface::stack::StackOutcome::Action(action) = outcome {
                self.harness.app.apply_surface_action(action).await;
            }
        }

        /// Pumps one authentication event, exactly as the loop would.
        async fn pump(&mut self) -> bool {
            self.pump_within(Duration::from_secs(20)).await
        }

        async fn pump_within(&mut self, bound: Duration) -> bool {
            match tokio::time::timeout(bound, self.events.recv()).await {
                Ok(Some(event)) => {
                    self.harness.app.on_auth_event(event).await;
                    true
                }
                _ => false,
            }
        }

        /// Pumps until nothing more arrives promptly, or `most` events have
        /// been handled — the loop's steady state, without waiting out a
        /// timeout on a channel that never closes.
        async fn drain(&mut self, most: usize) {
            for _ in 0..most {
                if !self.pump_within(Duration::from_secs(10)).await {
                    return;
                }
            }
        }

        /// Pumps until the credential transaction (or the sign-out) has
        /// finished, which is now a task rather than something the command
        /// awaited inline.
        async fn settle_transaction(&mut self) {
            for _ in 0..8 {
                if !self.harness.app.auth.flow.is_active() && !self.harness.app.auth.is_active() {
                    return;
                }
                if !self.pump_within(Duration::from_secs(20)).await {
                    return;
                }
            }
        }

        async fn stored(&self) -> Vec<ProviderIdentity> {
            let service = self
                .harness
                .app
                .auth_port
                .open(Opening::Degraded)
                .await
                .expect("the test profile opens");
            service
                .status()
                .await
                .expect("status")
                .providers
                .iter()
                .filter(|provider| {
                    matches!(provider.state, coda_auth::service::StoredState::Present(_))
                })
                .map(|provider| provider.identity)
                .collect()
        }

        fn said(&self) -> String {
            notices(&self.harness.app).join("\n")
        }
    }

    // ── The properties ───────────────────────────────────────────────────────

    #[cfg(windows)]
    #[tokio::test(flavor = "multi_thread")]
    async fn the_engine_this_client_owns_is_provably_gone_before_the_credential_is_written() {
        let held = tempfile::tempdir().expect("temp");
        let held = held.path().join("engine.hold");
        let mut fixture =
            Fixture::with_barrier(Provider::Models, false, Some(held.clone())).await;
        fixture.harness.app.owned_engine = Some(child_holding(&held).await);

        fixture.login_with_key("api-key", TEST_KEY).await;
        assert!(fixture.pump().await, "the preparation never reported");
        // The commit is a task now: the ordering under test is inside it, and
        // it reports back when the transaction has finished.
        fixture.settle_transaction().await;

        // Read before anything else opens the profile again: the last section
        // entered is the credential transaction's own.
        let observations = fixture.observations.lock().expect("barrier").clone();
        assert!(
            observations.len() >= 2,
            "the credential transaction never ran, so the ordering proves nothing: {}",
            fixture.said()
        );
        assert_eq!(
            observations.last(),
            Some(&true),
            "the credential transaction started while the engine this client owns was still \
             running — that process is still spending the credential being replaced"
        );
        assert!(
            fixture.stored().await.contains(&ProviderIdentity::AnthropicApiKey),
            "the login did not actually commit: {}",
            fixture.said()
        );
        assert!(!fixture.harness.app.engine_connected());
        drop(fixture);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_commit_that_lands_but_whose_engine_will_not_start_is_never_reported_as_a_failed_sign_in()
    {
        // The harness engine command names a program that does not exist, so
        // the replacement launch fails for real.
        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.composer.set_text("a draft nobody may lose");
        let before = fixture.harness.app.state.transcript.len();

        fixture.login_with_key("api-key", TEST_KEY).await;
        assert!(fixture.pump().await, "the preparation never reported");
        // The commit, then the launch attempt and the connection check, in
        // whichever order they finish.
        fixture.drain(3).await;

        let said = fixture.said();
        assert!(
            said.contains("Credentials saved; engine startup failed"),
            "a saved credential was reported as a failed sign-in: {said}"
        );
        assert!(!said.contains("sign-in failed"), "{said}");
        assert!(!fixture.harness.app.engine_connected(), "the session must be disconnected");
        assert_eq!(
            fixture.harness.app.composer.text(),
            "a draft nobody may lose",
            "the draft was lost"
        );
        assert!(fixture.harness.app.state.transcript.len() >= before, "the conversation was lost");
        assert!(fixture.stored().await.contains(&ProviderIdentity::AnthropicApiKey));
    }

    #[cfg(windows)]
    #[tokio::test(flavor = "multi_thread")]
    async fn a_logout_cannot_leave_a_managed_child_holding_the_credential_it_deleted() {
        // The same barrier as the sign-in: a logout that removes the token
        // while the engine it started is still running has not signed anybody
        // out — that process keeps spending it until it happens to stop.
        let held = tempfile::tempdir().expect("temp");
        let held = held.path().join("engine.hold");
        let mut fixture =
            Fixture::with_barrier(Provider::Models, false, Some(held.clone())).await;
        fixture.harness.app.owned_engine = Some(child_holding(&held).await);

        fixture.harness.app.cmd_logout(None).await;
        fixture.settle_transaction().await;

        let observations = fixture.observations.lock().expect("barrier").clone();
        assert!(observations.len() >= 2, "the logout transaction never ran");
        assert_eq!(
            observations.last(),
            Some(&true),
            "the logout deleted the credential while the engine using it was still running"
        );
        assert!(!fixture.harness.app.engine_connected());
        drop(fixture);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_disconnected_session_refuses_engine_commands_instead_of_failing_on_a_dead_socket() {
        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.cmd_logout(None).await;
        fixture.settle_transaction().await;
        let before = fixture.harness.calls.lock().expect("calls").len();

        fixture
            .harness
            .app
            .run_command(crate::commands::parse("/models").expect("a command"))
            .await;

        assert_eq!(
            fixture.harness.calls.lock().expect("calls").len(),
            before,
            "a disconnected session issued an engine request anyway"
        );
        let said = fixture.said();
        assert!(
            said.contains("this session is disconnected") || said.contains("connection is closed"),
            "{said}"
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn nothing_is_polled_or_read_after_a_deliberate_disconnection() {
        // A closed inbound channel answers `None` immediately and forever, and
        // a recovery schedule against a dead connection is a poll that can
        // only fail. Both are how a disconnected session burns a core.
        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.cmd_logout(None).await;
        fixture.settle_transaction().await;
        assert!(!fixture.harness.app.engine_connected());

        fixture.harness.app.needs_resync = true;
        fixture.harness.app.needs_rehydrate = true;
        let before = fixture.harness.calls.lock().expect("calls").len();
        for _ in 0..5 {
            fixture.harness.app.settle_with_engine_at(std::time::Instant::now()).await;
        }
        assert_eq!(
            fixture.harness.calls.lock().expect("calls").len(),
            before,
            "the client kept polling an engine it deliberately disconnected from"
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_logout_never_reconnects_to_an_exported_key_and_keeps_the_ui_usable() {
        let mut fixture = Fixture::new(Provider::Models, true).await;
        fixture.harness.app.composer.set_text("still here");

        fixture.harness.app.cmd_logout(None).await;
        fixture.settle_transaction().await;

        let said = fixture.said();
        assert!(said.contains("ANTHROPIC_API_KEY is still exported"), "{said}");
        assert!(said.contains("*not* reconnected"), "{said}");
        assert!(!fixture.harness.app.engine_connected());
        assert!(fixture.harness.app.owned_engine.is_none());
        assert!(
            fixture.harness.app.restarted.is_none(),
            "a logout started an engine on the exported key"
        );
        assert_eq!(fixture.harness.app.composer.text(), "still here");

        // And the local commands still work while disconnected.
        fixture.harness.app.cmd_login(Some("api-key")).await;
        assert!(
            fixture.harness.app.surfaces.top().is_some(),
            "a disconnected session could not start a sign-in: {}",
            fixture.said()
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_refused_credential_leaves_the_running_engine_and_the_profile_exactly_as_they_were()
    {
        let mut fixture = Fixture::new(Provider::Rejects, false).await;
        fixture.login_with_key("api-key", TEST_KEY).await;
        assert!(fixture.pump().await, "the preparation never reported");

        assert!(
            fixture.harness.app.engine_connected(),
            "a refused sign-in stopped the engine that was working"
        );
        assert!(
            fixture.stored().await.is_empty(),
            "a refused credential was written to the profile"
        );
        assert!(fixture.harness.app.surfaces.is_empty(), "the sign-in surface was left open");
        assert!(fixture.said().contains("refused"), "{}", fixture.said());
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn cancelling_a_live_preparation_writes_nothing_and_stops_nothing() {
        // The provider never answers, so the preparation is genuinely still
        // in flight when Escape arrives.
        let mut fixture = Fixture::new(Provider::Hangs, false).await;
        fixture.login_with_key("api-key", TEST_KEY).await;
        assert!(fixture.harness.app.auth.is_active(), "nothing was actually preparing");

        // Escape, through the surface, exactly as a keypress would.
        let outcome = fixture
            .harness
            .app
            .surfaces
            .handle_key(KeyEvent::new(KeyCode::Esc, KeyModifiers::NONE));
        match outcome {
            crate::surface::stack::StackOutcome::Action(action) => {
                fixture.harness.app.apply_surface_action(action).await
            }
            _ => panic!("Escape did not reach the flow"),
        }

        assert!(!fixture.harness.app.auth.is_active());
        assert!(fixture.harness.app.surfaces.is_empty());
        assert!(
            fixture.harness.app.engine_connected(),
            "a cancelled sign-in stopped the engine that was working"
        );
        assert!(fixture.stored().await.is_empty(), "a cancelled sign-in wrote a credential");
        assert!(fixture.said().contains("Nothing was saved"), "{}", fixture.said());
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn an_environment_login_stores_no_key_and_says_so() {
        let mut fixture = Fixture::new(Provider::Models, true).await;
        fixture.harness.app.cmd_login(Some("api-key")).await;
        fixture.choice_surface().await;
        // Move to the key-source question and choose the environment.
        fixture.press(KeyCode::BackTab);
        fixture.press(KeyCode::Down);
        fixture.submit().await;
        assert!(fixture.pump().await, "the preparation never reported");
        fixture.settle_transaction().await;

        assert!(
            fixture.stored().await.is_empty(),
            "an environment login stored a key it promised not to store"
        );
        let said = fixture.said();
        assert!(said.contains("No API key was stored"), "{said}");
        assert!(!said.contains(TEST_KEY), "the key reached the transcript: {said}");
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn no_path_ever_puts_the_key_into_anything_durable() {
        for behaviour in [Provider::Models, Provider::Rejects] {
            let mut fixture = Fixture::new(behaviour, false).await;
            fixture.login_with_key("api-key", TEST_KEY).await;
            fixture.drain(4).await;

            let said = fixture.said();
            let printed: String = fixture
                .harness
                .app
                .state
                .transcript
                .blocks()
                .iter()
                .map(|block| format!("{block:?}"))
                .collect();
            assert!(!said.contains(TEST_KEY), "{said}");
            assert!(!printed.contains(TEST_KEY), "the key reached a transcript block");
            assert!(
                !format!("{:?}", fixture.harness.app.auth).contains(TEST_KEY),
                "the key is reachable through a Debug"
            );
            drop(fixture);
        }
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_second_sign_in_is_refused_while_one_is_running() {
        let mut fixture = Fixture::new(Provider::Hangs, false).await;
        fixture.login_with_key("api-key", TEST_KEY).await;
        let before = fixture.harness.app.auth_port().opens();

        fixture.harness.app.cmd_login(Some("claude")).await;
        assert_eq!(
            fixture.harness.app.auth_port().opens(),
            before,
            "a refused second sign-in opened the credential store anyway"
        );
        assert!(fixture.said().contains("already in progress"), "{}", fixture.said());
        // The live challenge is still the surface on screen.
        assert_eq!(fixture.harness.app.surfaces.len(), 1);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_first_run_is_announced_but_a_profile_with_a_credential_starts_quietly() {
        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.preflight().await;
        assert!(fixture.said().contains("No account is connected"), "{}", fixture.said());

        let mut connected = Fixture::new(Provider::Models, true).await;
        connected.harness.app.preflight().await;
        assert!(connected.said().is_empty(), "a healthy start was not quiet: {}", connected.said());
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn an_api_only_session_says_nothing_at_all_at_startup() {
        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.access_mode = AccessMode::ApiOnly;
        fixture.harness.app.preflight().await;
        assert!(fixture.said().is_empty(), "{}", fixture.said());
        assert_eq!(
            fixture.harness.app.auth_port().opens(),
            0,
            "an API-only startup probed this machine's credentials"
        );
        let _ = fixture.profile;
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_form_names_where_a_sign_in_will_go_before_anything_is_typed() {
        // The disclosure is not decoration: on a machine where GH_COPILOT_*
        // overrides are exported, "GitHub Copilot" alone is not the truth
        // about where a device authorization is about to be sent.
        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.cmd_login(Some("github-copilot")).await;
        fixture.choice_surface().await;
        let drawn = fixture
            .harness
            .app
            .surfaces
            .render(ratatui::layout::Rect::new(0, 0, 100, 60), &coda_render::theme::Theme::default())
            .into_iter()
            .flat_map(|surface| surface.lines)
            .flat_map(|line| {
                line.spans.into_iter().map(|span| span.content.into_owned()).collect::<Vec<_>>()
            })
            .collect::<Vec<_>>()
            .join(" ");
        assert!(
            drawn.contains("GitHub Copilot deployment"),
            "the form did not say where this would authorize: {drawn}"
        );
        assert!(
            drawn.contains("127.0.0.1") || drawn.contains("endpoint overrides"),
            "the form named a deployment the overrides in force contradict: {drawn}"
        );

        // And an API-key sign-in names the host the key would be sent to.
        fixture.harness.app.cancel_auth();
        fixture.harness.app.cmd_login(Some("api-key")).await;
        fixture.choice_surface().await;
        let drawn = fixture
            .harness
            .app
            .surfaces
            .render(ratatui::layout::Rect::new(0, 0, 100, 60), &coda_render::theme::Theme::default())
            .into_iter()
            .flat_map(|surface| surface.lines)
            .flat_map(|line| {
                line.spans.into_iter().map(|span| span.content.into_owned()).collect::<Vec<_>>()
            })
            .collect::<Vec<_>>()
            .join(" ");
        assert!(
            drawn.contains("Anthropic API-key endpoint"),
            "the form did not say where the key would go: {drawn}"
        );

        // And with no account named, the form can answer with any of them, so
        // every destination is named — including Claude.ai's authorization
        // host, which is as much a place a credential goes as an endpoint is.
        fixture.harness.app.cancel_auth();
        fixture.harness.app.cmd_setup().await;
        fixture.choice_surface().await;
        let drawn = fixture
            .harness
            .app
            .surfaces
            .render(ratatui::layout::Rect::new(0, 0, 100, 60), &coda_render::theme::Theme::default())
            .into_iter()
            .flat_map(|surface| surface.lines)
            .flat_map(|line| {
                line.spans.into_iter().map(|span| span.content.into_owned()).collect::<Vec<_>>()
            })
            .collect::<Vec<_>>()
            .join(" ");
        for host in ["Anthropic API-key endpoint", "Claude.ai", "GitHub Copilot deployment"] {
            assert!(
                drawn.contains(host),
                "an unnamed sign-in did not disclose {host}: {drawn}"
            );
        }
    }

    // ── The browser and device flows, against sockets this test owns ─────────
    //
    // No provider, no browser and no person: the authorization URL is read off
    // the challenge exactly as a browser would receive it, and the redirect is
    // performed by the test. `CODA_AUTH_NO_BROWSER` keeps the real launcher
    // out of it.

    /// The live challenge's lines — the only place a URL or code exists.
    fn challenge_lines(app: &App) -> Vec<String> {
        app.surfaces
            .top()
            .and_then(|surface| surface.as_any().downcast_ref::<AuthChallengeSurface>())
            .map(|challenge| challenge.lines().to_vec())
            .unwrap_or_default()
    }

    /// The authorization URL the flow published, as a browser would get it.
    fn authorize_url(app: &App) -> String {
        challenge_lines(app)
            .into_iter()
            .find(|line| line.trim_start().starts_with("http"))
            .expect("the challenge published no address")
            .trim()
            .to_owned()
    }

    /// One query parameter of a URL, percent-decoded.
    ///
    /// Hand-rolled rather than pulling a URL crate into the front-end for a
    /// test: what is being read back is a query string the flow wrote, and the
    /// decoding it needs is two hex digits.
    fn query_value(url: &str, key: &str) -> Option<String> {
        let query = url.split_once('?')?.1;
        let raw = query.split('&').find_map(|pair| {
            let (name, value) = pair.split_once('=')?;
            (name == key).then(|| value.to_owned())
        })?;
        let bytes = raw.replace('+', " ").into_bytes();
        let mut out = Vec::with_capacity(bytes.len());
        let mut index = 0;
        while index < bytes.len() {
            if bytes[index] == b'%' && index + 2 < bytes.len() {
                let hex = std::str::from_utf8(&bytes[index + 1..index + 3]).ok()?;
                if let Ok(byte) = u8::from_str_radix(hex, 16) {
                    out.push(byte);
                    index += 3;
                    continue;
                }
            }
            out.push(bytes[index]);
            index += 1;
        }
        String::from_utf8(out).ok()
    }

    /// The port of a `http://127.0.0.1:PORT/...` loopback redirect.
    fn redirect_port(authorize: &str) -> u16 {
        let redirect =
            query_value(authorize, "redirect_uri").expect("no redirect_uri in the authorize URL");
        let after_scheme = redirect.split("//").nth(1).expect("a loopback redirect has a host");
        let authority = after_scheme.split('/').next().unwrap_or_default();
        authority
            .rsplit(':')
            .next()
            .and_then(|port| port.parse().ok())
            .expect("a loopback redirect has a port")
    }

    /// Performs the browser's half: calls the loopback redirect the flow is
    /// listening on, with the state it issued.
    async fn visit_redirect(authorize: &str, query: &str) {
        let state = query_value(authorize, "state").expect("no state in the authorize URL");
        let port = redirect_port(authorize);
        let path = format!("/callback?{query}&state={state}");
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        let mut socket = tokio::net::TcpStream::connect(("127.0.0.1", port))
            .await
            .expect("the loopback listener is accepting");
        let request = format!("GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        socket.write_all(request.as_bytes()).await.expect("send the redirect");
        let mut answer = Vec::new();
        let _ = socket.read_to_end(&mut answer).await;
    }

    /// Whether anything is still listening on the redirect's port.
    async fn redirect_port_is_open(authorize: &str) -> bool {
        tokio::net::TcpStream::connect(("127.0.0.1", redirect_port(authorize))).await.is_ok()
    }

    /// Runs `/login <provider>` and submits the form unchanged.
    async fn login_as(fixture: &mut Fixture, provider: &str) {
        fixture.harness.app.cmd_login(Some(provider)).await;
        fixture.choice_surface().await;
        fixture.submit().await;
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_browser_sign_in_completes_through_the_loopback_redirect_it_advertised() {
        let mut fixture = Fixture::new(Provider::Models, false).await;
        login_as(&mut fixture, "claude").await;
        // The challenge carries the address; nothing else does.
        assert!(fixture.pump().await, "no challenge was published");
        let authorize = authorize_url(&fixture.harness.app);

        visit_redirect(&authorize, "code=fixture-code").await;
        assert!(fixture.pump().await, "the preparation never reported");
        fixture.settle_transaction().await;

        assert_eq!(
            fixture.stored().await,
            vec![ProviderIdentity::ClaudeAi],
            "the browser sign-in did not connect the account: {}",
            fixture.said()
        );
        // And nothing durable carries the address or the code.
        let said = fixture.said();
        assert!(!said.contains("code=fixture-code"), "{said}");
        assert!(!said.contains(authorize.as_str()), "the authorization URL reached the transcript");
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn cancelling_a_browser_sign_in_closes_the_socket_it_was_listening_on() {
        let mut fixture = Fixture::new(Provider::Models, false).await;
        login_as(&mut fixture, "claude").await;
        assert!(fixture.pump().await, "no challenge was published");
        let authorize = authorize_url(&fixture.harness.app);
        assert!(
            redirect_port_is_open(&authorize).await,
            "the flow advertised a redirect nothing was listening on"
        );

        fixture.harness.app.cancel_auth();

        // The listener is owned by the preparation future, so it closes when
        // that future is dropped — which is what cancelling actually does.
        let mut closed = false;
        for _ in 0..100 {
            if !redirect_port_is_open(&authorize).await {
                closed = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
        assert!(closed, "a cancelled browser sign-in left its loopback listener open");
        assert!(fixture.stored().await.is_empty(), "a cancelled sign-in wrote a credential");
        assert!(
            fixture.harness.app.engine_connected(),
            "a cancelled sign-in stopped the engine that was working"
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_provider_that_refuses_the_exchange_leaves_the_engine_and_the_profile_alone() {
        let mut fixture = Fixture::new(Provider::ClaudeRefuses, false).await;
        // Something is connected before the attempt, and must still be after.
        fixture.login_with_key("api-key", TEST_KEY).await;
        assert!(fixture.pump().await, "the first preparation never reported");
        fixture.settle_transaction().await;
        fixture.drain(2).await;
        assert_eq!(fixture.stored().await, vec![ProviderIdentity::AnthropicApiKey]);

        login_as(&mut fixture, "claude").await;
        assert!(fixture.pump().await, "no challenge was published");
        let authorize = authorize_url(&fixture.harness.app);
        visit_redirect(&authorize, "code=fixture-code").await;
        assert!(fixture.pump().await, "the refusal never reported");

        assert_eq!(
            fixture.stored().await,
            vec![ProviderIdentity::AnthropicApiKey],
            "a refused exchange replaced the account that was working"
        );
        assert!(fixture.harness.app.surfaces.is_empty(), "the sign-in surface was left open");
        assert!(!fixture.harness.app.auth.is_active());
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_device_sign_in_switches_the_connected_account_at_the_host_it_disclosed() {
        let mut fixture = Fixture::new(Provider::DeviceGrants, false).await;
        // An account is connected first, so this is a *switch*.
        fixture.login_with_key("api-key", TEST_KEY).await;
        assert!(fixture.pump().await, "the first preparation never reported");
        fixture.settle_transaction().await;
        fixture.drain(2).await;
        assert_eq!(fixture.stored().await, vec![ProviderIdentity::AnthropicApiKey]);

        fixture.harness.app.cmd_login(Some("github-copilot")).await;
        fixture.choice_surface().await;
        fixture.submit().await;
        // The disclosure names the host this will authorize at, before the
        // device code is requested.
        let waiting = challenge_lines(&fixture.harness.app).join("\n");
        assert!(
            waiting.contains("GitHub Copilot deployment"),
            "the sign-in did not disclose where it was about to authorize: {waiting}"
        );
        assert!(
            waiting.contains("endpoint overrides") || waiting.contains("127.0.0.1"),
            "the disclosure did not reflect the overrides actually in force: {waiting}"
        );

        assert!(fixture.pump().await, "no device code was published");
        assert!(
            challenge_lines(&fixture.harness.app).join("\n").contains("CODA-FIXTURE"),
            "the device code never reached the surface"
        );
        assert!(fixture.pump().await, "the preparation never reported");
        fixture.settle_transaction().await;

        assert_eq!(
            fixture.stored().await,
            vec![ProviderIdentity::GithubCopilot],
            "the switch did not land: {}",
            fixture.said()
        );
        let asked: Vec<String> = fixture.wire.lock().expect("wire").clone();
        assert!(
            asked.iter().any(|entry| entry.contains("/login/device/code")),
            "the device flow never reached the host it disclosed: {asked:?}"
        );
        assert!(
            asked.iter().any(|entry| entry.contains("/copilot_internal/v2/token")),
            "the durable token was never exchanged at the tenant: {asked:?}"
        );
        // Said once, after the fact, and safely.
        assert!(
            fixture.said().contains("Connected account: GitHub Copilot"),
            "{}",
            fixture.said()
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn cancelling_a_device_sign_in_stops_the_poller() {
        let mut fixture = Fixture::new(Provider::DevicePending, false).await;
        fixture.harness.app.cmd_login(Some("github-copilot")).await;
        fixture.choice_surface().await;
        fixture.submit().await;
        assert!(fixture.pump().await, "no device code was published");

        // Let it poll at least once, then take it away.
        tokio::time::sleep(Duration::from_millis(1_200)).await;
        let polled_before = fixture
            .wire
            .lock()
            .expect("wire")
            .iter()
            .filter(|entry| entry.contains("/login/oauth/access_token"))
            .count();
        assert!(polled_before >= 1, "the device poller never asked the provider anything");

        fixture.harness.app.cancel_auth();
        tokio::time::sleep(Duration::from_millis(2_500)).await;

        let polled_after = fixture
            .wire
            .lock()
            .expect("wire")
            .iter()
            .filter(|entry| entry.contains("/login/oauth/access_token"))
            .count();
        assert_eq!(
            polled_after, polled_before,
            "a cancelled device sign-in kept polling the provider"
        );
        assert!(fixture.stored().await.is_empty());
    }

    // ── Races: what a retracted flow may never do ────────────────────────────

    #[tokio::test(flavor = "multi_thread")]
    async fn a_commit_reported_by_a_retracted_flow_is_never_adopted() {
        // The exact race: an attempt is cancelled, a second one is started,
        // and only then does the first one's commit reach the loop. Acting on
        // it would connect an account nobody asked for — and, worse, would do
        // it under the *new* attempt's target.
        let mut fixture = Fixture::new(Provider::Hangs, false).await;
        fixture.login_with_key("api-key", TEST_KEY).await;
        let stale = fixture.harness.app.auth.flow.generation();
        fixture.harness.app.cancel_auth();

        // A second attempt, live, with a target of its own.
        login_as(&mut fixture, "claude").await;
        assert!(fixture.harness.app.auth.flow.is_preparing());
        let surfaces_before = fixture.harness.app.surfaces.len();

        fixture
            .harness
            .app
            .on_auth_event(AuthEvent::Flow(FlowEvent::Committed {
                generation: stale,
                outcome: Box::new(coda_auth::service::CommitOutcome::Committed {
                    identity: ProviderIdentity::ClaudeAi,
                    replaced: Vec::new(),
                    settings_changed: true,
                }),
            }))
            .await;

        assert!(
            fixture.harness.app.auth.booting.is_none(),
            "an engine was started for a sign-in the user had cancelled"
        );
        assert!(
            !fixture.said().contains("Connected account"),
            "a retracted sign-in was reported as connected: {}",
            fixture.said()
        );
        assert!(
            fixture.harness.app.auth.flow.is_preparing(),
            "a stale commit ended the attempt that was actually running"
        );
        assert_eq!(
            fixture.harness.app.surfaces.len(),
            surfaces_before,
            "a stale commit closed the live attempt's surface"
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_challenge_from_a_retracted_flow_never_replaces_the_live_one() {
        let mut fixture = Fixture::new(Provider::Hangs, false).await;
        fixture.login_with_key("api-key", TEST_KEY).await;
        let stale = fixture.harness.app.auth.flow.generation();
        fixture.harness.app.cancel_auth();
        assert!(fixture.harness.app.surfaces.is_empty());

        // A second attempt is now the one on screen.
        login_as(&mut fixture, "claude").await;
        let live = fixture.harness.app.surfaces.top_title().expect("a surface is open");

        fixture
            .harness
            .app
            .on_auth_event(AuthEvent::Flow(FlowEvent::Challenge {
                generation: stale,
                title: "Authorize the cancelled attempt".into(),
                lines: vec!["http://example.invalid/authorize?state=x".into()],
            }))
            .await;

        assert_eq!(
            fixture.harness.app.surfaces.top_title().as_deref(),
            Some(live.as_str()),
            "a cancelled sign-in put its challenge over the live one"
        );
        assert!(
            !challenge_lines(&fixture.harness.app)
                .join("\n")
                .contains("example.invalid"),
            "the retracted attempt's address reached the screen"
        );
    }

    #[cfg(windows)]
    #[tokio::test(flavor = "multi_thread")]
    async fn the_loop_is_never_blocked_while_an_engine_is_being_stopped_and_a_credential_written() {
        // The engine is a real child that will not go away quickly, and the
        // grace period is deliberately longer than the bound below: if the
        // shutdown or the transaction were awaited inline, handling this event
        // could not possibly return in time.
        let held = tempfile::tempdir().expect("temp");
        let held = held.path().join("engine.hold");
        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.owned_engine = Some(child_holding(&held).await);
        fixture.harness.app.shutdown_grace = Duration::from_secs(4);

        fixture.login_with_key("api-key", TEST_KEY).await;
        let event = fixture.events.recv().await.expect("the preparation reported");
        let started = std::time::Instant::now();
        fixture.harness.app.on_auth_event(event).await;
        let handling = started.elapsed();
        assert!(
            handling < Duration::from_secs(2),
            "the event loop was blocked for {handling:?} while a commit ran"
        );
        assert!(fixture.harness.app.auth.flow.is_committing(), "nothing was actually committing");

        // A cancel while the transaction runs is refused, not obeyed, and the
        // loop answers it immediately.
        fixture.harness.app.cancel_auth();
        assert!(
            fixture.said().contains("cannot be cancelled"),
            "an uncancellable commit was silently cancelled: {}",
            fixture.said()
        );
        assert!(fixture.harness.app.auth.flow.is_committing());

        // And closing down waits for it rather than leaving a late write.
        fixture.harness.app.close_auth_out().await;
        assert!(!fixture.harness.app.auth.flow.is_committing());
        assert!(
            fixture.stored().await.contains(&ProviderIdentity::AnthropicApiKey),
            "the awaited commit did not finish before teardown returned"
        );
    }

    // ── Reconnecting, and what may start an engine while a flow runs ─────────

    #[cfg(windows)]
    #[tokio::test(flavor = "multi_thread")]
    async fn adopting_an_engine_reconnects_a_session_that_had_been_disconnected() {
        // The failure: only the sign-in path marked the session connected, so
        // a `/resume` after a failed replacement built a managed engine this
        // process owned and then refused to talk to it — inbound unread,
        // `fetch` refusing, no way out but restarting Coda. The invariant
        // belongs to `adopt_engine`, which is the one place a replacement
        // becomes this session's engine.
        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.cmd_logout(None).await;
        fixture.settle_transaction().await;
        assert!(!fixture.harness.app.engine_connected(), "the sign-out did not disconnect");
        assert_eq!(fixture.harness.app.state.activity, crate::state::Activity::Disconnected);

        let (booted, _tasks, asked) = fake_booted().await;
        fixture.harness.app.adopt_engine(booted);

        assert!(
            fixture.harness.app.engine_connected(),
            "a session that adopted a running engine still refused to use it"
        );
        assert_eq!(
            fixture.harness.app.state.activity,
            crate::state::Activity::Ready,
            "the status line did not follow the connection back"
        );
        assert!(
            fixture.harness.app.restarted.is_some(),
            "the replacement was not staged for the loop to swap in"
        );

        // And a command that needs the engine now actually reaches it.
        fixture
            .harness
            .app
            .run_command(crate::commands::parse("/models --refresh").expect("a command"))
            .await;
        assert!(
            asked.lock().expect("wire").iter().any(|method| method.contains("models")),
            "the adopted engine was never asked anything: {:?}",
            asked.lock().expect("wire")
        );
    }

    #[cfg(windows)]
    #[tokio::test(flavor = "multi_thread")]
    async fn nothing_but_the_sign_out_itself_may_start_an_engine_while_it_runs() {
        // The window this closes: a sign-out stops the engine, then deletes
        // the credential on a task that cannot be cancelled. In between, the
        // composer is live — and `/resume`, or a row in the sessions browser,
        // used to boot a child that would outlive the credential it was
        // started with, on the account the user had just disconnected.
        let held = tempfile::tempdir().expect("temp");
        let held = held.path().join("engine.hold");
        let sandbox = tempfile::tempdir().expect("temp");
        let marker = sandbox.path().join("engine-was-spawned.marker");

        let mut fixture = Fixture::new(Provider::Models, false).await;
        // Long enough that the transaction is provably still running while
        // the resume is attempted, and short enough not to slow the suite.
        fixture.harness.app.shutdown_grace = Duration::from_secs(4);
        fixture.harness.app.owned_engine = Some(child_holding(&held).await);
        fixture.harness.app.engine_command = marker_command(&marker);

        fixture.harness.app.cmd_logout(None).await;
        // The profile opens, the engine is stopped, and the transaction
        // starts — all on its own task.
        assert!(fixture.pump().await, "the sign-out never started");
        assert!(
            fixture.harness.app.auth.is_active(),
            "the sign-out was not running, so this proves nothing"
        );

        // Both entry points: the command, and the sessions browser's row.
        fixture
            .harness
            .app
            .run_command(crate::commands::parse("/resume an-old-session").expect("a command"))
            .await;
        fixture
            .harness
            .app
            .apply_surface_action(crate::surface::SurfaceAction::ResumeSession(
                "another-old-session".to_owned(),
            ))
            .await;

        assert!(
            !marker.exists(),
            "an engine was started while the sign-out was removing the credential"
        );
        let said = fixture.said();
        assert!(
            said.contains("sign-in or sign-out is in progress"),
            "the refusal did not say why: {said}"
        );
        assert!(fixture.harness.app.restarted.is_none(), "a replacement was adopted anyway");

        // And it is still true once the transaction has actually landed: no
        // automatic reconnection follows a sign-out.
        fixture.settle_transaction().await;
        assert!(!marker.exists(), "an engine was started after the credential was removed");
        assert!(!fixture.harness.app.engine_connected());
        drop(fixture);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_sign_out_asks_the_engine_to_stop_over_the_live_connection_before_stopping_it() {
        // The graceful half of a sign-out, and the half a fail-fast
        // connection must not break. The engine is stopped *over the
        // connection* first — `session/shutdown` is a real round-trip it
        // answers, which is how it closes its MCP servers and flushes the
        // session — and only then is the process itself stopped and awaited.
        // Closing the connection when the stop is sent would refuse that
        // request before it reached the wire and leave the engine to be
        // killed after a grace period, with the credential deleted underneath
        // it.
        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.cmd_logout(None).await;
        fixture.settle_transaction().await;
        fixture.harness.settle_wire().await;

        let calls = fixture.harness.calls.lock().expect("calls").clone();
        assert!(
            calls.iter().any(|method| method == "shutdown"),
            "the engine was never asked to stop over the connection: {calls:?}"
        );
        assert!(!fixture.harness.app.engine_connected(), "the sign-out did not disconnect");
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_status_line_is_disconnected_after_a_sign_out_and_after_a_failed_replacement() {
        // Two ways to end up with no engine, and both used to leave a green
        // "ready" and the previous model on screen.
        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.apply(crate::state::UiEvent::ModelChanged {
            id: "claude-sonnet-4-5".into(),
            context_limit: None,
        });
        fixture.harness.app.cmd_logout(None).await;
        fixture.settle_transaction().await;
        assert_eq!(fixture.harness.app.state.activity, crate::state::Activity::Disconnected);
        assert_eq!(fixture.harness.app.state.model.as_deref(), Some("claude-sonnet-4-5"));

        // The other path: the credential lands and the replacement will not
        // start, because the harness engine command names nothing that exists.
        let mut failed = Fixture::new(Provider::Models, false).await;
        failed.login_with_key("api-key", TEST_KEY).await;
        assert!(failed.pump().await, "the preparation never reported");
        failed.drain(3).await;
        assert!(
            failed.said().contains("engine startup failed"),
            "the replacement started after all: {}",
            failed.said()
        );
        assert_eq!(
            failed.harness.app.state.activity,
            crate::state::Activity::Disconnected,
            "a session with no engine still claimed to be ready"
        );
    }

    // ── Cancelling: what a retracted flow leaves behind ──────────────────────

    #[cfg(windows)]
    #[tokio::test(flavor = "multi_thread")]
    async fn cancelling_a_sign_in_reaps_the_process_it_launched_for_the_address() {
        // `tokio::process::Child` does not kill or reap on drop, so the only
        // thing that ever collects a launcher this login started is the
        // preparation task's own cleanup. Aborting that task at its await
        // point — which is what cancelling used to do — skipped it entirely.
        //
        // The launcher here is a process this test owns which writes a file
        // when it exits, so "the cleanup ran" is a happens-before fact rather
        // than a timing guess: if the flow closed without waiting, the file is
        // not there yet.
        let sandbox = tempfile::tempdir().expect("temp");
        let reaped = sandbox.path().join("launcher-exited.marker");
        let script = format!(
            "Start-Sleep -Milliseconds 400; New-Item -ItemType File -Force -Path '{}' | \
             Out-Null",
            reaped.display()
        );

        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.auth.browser_allowed = true;
        fixture.harness.app.auth.opener = Some(Arc::new(move |_url: &str| {
            tokio::process::Command::new("powershell.exe")
                .arg("-NoProfile")
                .arg("-NonInteractive")
                .arg("-Command")
                .arg(&script)
                .stdin(std::process::Stdio::null())
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .spawn()
                .map_err(|error| error.to_string())
        }));

        // A browser sign-in, so an address is published and the opener runs.
        login_as(&mut fixture, "claude").await;
        assert!(fixture.pump().await, "no challenge was published");
        assert!(
            !authorize_url(&fixture.harness.app).is_empty(),
            "the flow published no address, so nothing was launched"
        );

        fixture.harness.app.cancel_auth();
        assert!(!reaped.exists(), "the launcher had already finished, so this proves nothing");

        // Closing down is where a retired preparation is awaited.
        fixture.harness.app.close_auth_out().await;
        assert!(
            reaped.exists(),
            "the cancelled sign-in left the process it launched unreaped"
        );
        assert!(fixture.stored().await.is_empty(), "a cancelled sign-in wrote a credential");
        assert!(
            fixture.harness.app.engine_connected(),
            "a cancelled sign-in stopped the engine that was working"
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_slow_credential_store_never_blocks_the_loop_and_a_cancelled_open_opens_nothing() {
        // Opening a profile is a keychain round-trip, a settings file and a
        // provider's endpoint resolution. Awaited on the loop, that is a
        // terminal which does not draw, does not scroll and does not answer
        // Ctrl+C — for as long as the machine takes.
        let root = tempfile::tempdir().expect("temp");
        let path = root.path().to_path_buf();
        let slow = AuthPort::from_factory(move |opening| {
            let inner = AuthPort::isolated(path.clone(), Vec::new());
            Box::pin(async move {
                tokio::time::sleep(Duration::from_millis(600)).await;
                inner.open(opening).await
            })
        });

        let mut fixture = Fixture::new(Provider::Models, false).await;
        fixture.harness.app.set_auth_port(slow);

        let started = std::time::Instant::now();
        fixture.harness.app.cmd_login(Some("api-key")).await;
        let handling = started.elapsed();
        assert!(
            handling < Duration::from_millis(200),
            "the command awaited the credential store for {handling:?}"
        );
        // And it said what it was doing rather than freezing on a blank
        // screen: the exclusive surface is up before anything is read.
        assert!(
            fixture.harness.app.surfaces.top().is_some(),
            "nothing on screen said the profile was being read"
        );
        assert!(fixture.harness.app.auth.is_active(), "a second sign-in would not be refused");

        // A second one is refused while the first is still opening, and the
        // refusal leaves the live attempt's screen exactly as it was.
        let surfaces = fixture.harness.app.surfaces.len();
        fixture.harness.app.cmd_login(Some("claude")).await;
        assert!(fixture.said().contains("already in progress"), "{}", fixture.said());
        assert_eq!(
            fixture.harness.app.surfaces.len(),
            surfaces,
            "a refused second sign-in disturbed the one that was running"
        );

        // Escape, before the store has answered.
        fixture.harness.app.cancel_auth();
        assert!(fixture.harness.app.surfaces.is_empty(), "the cancelled sign-in left a surface");
        assert!(!fixture.harness.app.auth.is_active());
        assert!(
            fixture.said().contains("Sign-in cancelled"),
            "an Escape during the read did nothing visible: {}",
            fixture.said()
        );

        // The late answer opens nothing and writes nothing.
        fixture.drain(2).await;
        assert!(
            fixture.harness.app.surfaces.is_empty(),
            "a retracted sign-in put a form on screen after the fact"
        );
        assert!(fixture.stored().await.is_empty());

        // And closing down is bounded rather than waiting out the read.
        let started = std::time::Instant::now();
        tokio::time::timeout(Duration::from_secs(5), fixture.harness.app.close_auth_out())
            .await
            .expect("teardown waited on a read it had abandoned");
        assert!(started.elapsed() < Duration::from_secs(5));
    }
}
