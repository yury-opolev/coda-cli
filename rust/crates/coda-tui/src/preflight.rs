//! The launch preflight: the sign-in that happens *before* an engine exists.
//!
//! # Why a launcher needs one at all
//!
//! An engine started with no credential — or with an explicitly named provider
//! it has nothing for — fails closed. That is correct, and it is also the end
//! of the session: the terminal never opens, so the `/setup` that would fix it
//! is unreachable. The wizard therefore has to run *before* the engine is
//! spawned, which means it has to run without an `App`, without a
//! `Connection`, and without anything to restart.
//!
//! What it deliberately is **not** is a fake engine. Manufacturing a dead
//! `Connection` and an `App` that reports itself ready, purely so the existing
//! surfaces could be reached, would put a lie at the centre of the front-end
//! and leave every later reader to discover it. Instead the parts that are
//! genuinely shared — the surfaces, the [`LoginUi`][crate::local::login], the
//! cancellable preparation and the uncancellable commit — are used directly,
//! and the loop around them is the sixty lines below.
//!
//! # The order, and the two things it protects
//!
//! ```text
//! decide      AccessMode first. An API-only launch returns here, having
//!             opened no credential store and read no settings at all.
//!   ↓         (a healthy profile also returns here, silently)
//! enter       the terminal, only if a setup is actually needed
//!   ↓
//! choose      the same exclusive surfaces the running application uses
//!   ↓
//! prepare     cancellable; nothing written, no engine started
//!   ↓
//! commit      uncancellable, with a known outcome
//!   ↓
//! leave       the terminal is restored before anything is printed, and the
//!             launch continues with the account that was just connected
//! ```
//!
//! * **A cancelled setup starts no engine and changes nothing.** The launcher
//!   is told so and exits; it does not fall through to a spawn that would fail
//!   closed a second later with a worse message.
//! * **A committed setup is not a claim that the engine started.** The
//!   launcher boots afterwards, and reports a failure there as what it is: the
//!   credential is saved, the process did not start.
//!
//! Headless `coda run` and `coda serve` never come here: a non-interactive
//! process must fail closed rather than wait on a person who is not there.

use async_trait::async_trait;
use coda_auth::provider::copilot::CopilotDeploymentChoice;
use coda_auth::service::ProviderIdentity;
use coda_client::EngineCommand;
use coda_render::theme::{Role, Theme};
use crossterm::event::{Event, KeyCode, KeyEvent, KeyEventKind, KeyModifiers};
use ratatui::backend::Backend;
use ratatui::layout::Rect;
use ratatui::text::Line;
use ratatui::widgets::Paragraph;
use ratatui::Terminal;

use crate::local::auth::{refusal, AuthPort, Opening};
use crate::local::login::{
    committed_destination, destination_lines, disclosure, engine_command_for, FlowEvent, LoginFlow,
    Target,
};
use crate::local::AccessMode;
use crate::setup::FirstRun;
use crate::surface::auth::{AuthChallengeSurface, AuthChoiceSurface};
use crate::surface::stack::{StackOutcome, SurfaceStack};
use crate::surface::SurfaceAction;

/// What the launch found, before anything was started.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Need {
    /// Start the engine. Said for a healthy profile and for an API-only
    /// launch alike, and in both cases nothing is printed.
    None,
    /// Nothing is connected on this machine at all.
    FirstRun,
    /// A provider was named — by a flag, by the environment, or in settings —
    /// and has no usable credential.
    SelectedMissing {
        identity: ProviderIdentity,
        /// Whether *this launch* named it, as opposed to it being the profile's
        /// saved default.
        ///
        /// An explicit `--provider` (or `CODA_SERVE_PROVIDER`) is an
        /// instruction about which account this session talks to, and the
        /// wizard honours it as a condition: connect that account or leave. A
        /// saved default is not — the user did not ask for it on this launch,
        /// so switching is a perfectly good answer to "it has no credential".
        required: bool,
    },
    /// Something a sign-in would not fix and might make worse: an unreadable
    /// store, a saved provider this build does not know, two credentials and
    /// no choice between them. Reported; the engine still gets its chance to
    /// fail closed on its own terms.
    Report(String),
}

impl Need {
    /// Whether the wizard should be offered.
    pub fn wants_setup(&self) -> bool {
        matches!(self, Need::FirstRun | Need::SelectedMissing { .. })
    }

    /// The account the wizard should open on.
    fn preselect(&self) -> Option<ProviderIdentity> {
        match self {
            Need::SelectedMissing { identity, .. } => Some(*identity),
            _ => None,
        }
    }

    /// The account the wizard may *only* connect, when the launch made it a
    /// condition.
    fn required(&self) -> Option<ProviderIdentity> {
        match self {
            Need::SelectedMissing { identity, required: true } => Some(*identity),
            _ => None,
        }
    }

    fn heading(&self) -> Vec<String> {
        match self {
            Need::FirstRun => vec![
                "No account is connected on this machine yet.".to_owned(),
                "Choose one to connect now, or press Esc to leave without changing anything."
                    .to_owned(),
            ],
            Need::SelectedMissing { identity, required: true } => vec![
                format!(
                    "This launch asked for {}, and there is no usable credential for it on \
                     this machine.",
                    identity.label()
                ),
                format!(
                    "Connect {} now, or press Esc to leave without changing anything. Start \
                     Coda without --provider to choose a different account.",
                    identity.label()
                ),
            ],
            Need::SelectedMissing { identity, required: false } => vec![
                format!(
                    "This profile is set to {}, and there is no usable credential for it on \
                     this machine.",
                    identity.label()
                ),
                "Connect it now, choose a different account, or press Esc to leave without \
                 changing anything."
                    .to_owned(),
            ],
            _ => Vec::new(),
        }
    }
}

/// What the launch itself asked for, assembled by the binary that parsed it.
#[derive(Debug, Clone)]
pub struct LaunchIntent {
    /// How this process reached its engine, and therefore whether this
    /// machine's credentials are the ones that engine uses.
    pub access_mode: AccessMode,
    /// The provider this launch named, from a flag or from the environment the
    /// engine will inherit. Consulted through the shared selector, never
    /// re-interpreted here.
    pub explicit_provider: Option<String>,
    /// Whether the launch carries its own credential — `--api-key`,
    /// `CODA_SERVE_API_KEY` — in which case the engine needs nothing from the
    /// store and a wizard would be an interruption with no cause.
    pub carries_own_credential: bool,
}

impl LaunchIntent {
    /// The intent for a launch, from its flags, its engine arguments and the
    /// environment the engine will inherit.
    ///
    /// The environment is a lookup rather than a direct read so a test states
    /// the launch it means instead of mutating the process it runs in.
    pub fn from_launch(
        access_mode: AccessMode,
        provider_flag: Option<&str>,
        engine_args: &[String],
        env: impl Fn(&str) -> Option<String>,
    ) -> Self {
        let from_args = |flag: &str| -> Option<String> {
            let mut args = engine_args.iter();
            while let Some(arg) = args.next() {
                if let Some(value) = arg.strip_prefix(&format!("{flag}=")) {
                    return Some(value.to_owned());
                }
                if arg == flag {
                    return args.next().cloned();
                }
            }
            None
        };
        let explicit_provider = provider_flag
            .map(str::to_owned)
            .or_else(|| from_args("--provider"))
            .or_else(|| env("CODA_SERVE_PROVIDER"))
            .map(|value| value.trim().to_owned())
            .filter(|value| !value.is_empty());
        let carries_own_credential = from_args("--api-key").is_some()
            || env("CODA_SERVE_API_KEY").is_some_and(|value| !value.is_empty());
        Self { access_mode, explicit_provider, carries_own_credential }
    }
}

/// What this launch needs before an engine is started.
///
/// Never opens the credential store at all for an API-only launch: this
/// machine's credentials are not the ones a foreign engine uses, and probing
/// them on its behalf would read the operator's profile for a session that has
/// no business knowing what is in it.
///
/// The opening it does perform is *degraded*: deciding must keep working
/// because a saved deployment is broken, since that is exactly when a setup is
/// needed. The wizard opens the store again, strictly, when it runs — a
/// deliberate second opening rather than a reused handle, because a sign-in
/// must not proceed against a configuration that could not be fully resolved.
pub async fn decide(intent: &LaunchIntent, port: &AuthPort) -> Need {
    if refusal(intent.access_mode, "Connecting an account").is_some() {
        return Need::None;
    }
    if intent.carries_own_credential {
        // The launch brought its own credential; the store has no say in
        // whether this engine can connect.
        return Need::None;
    }
    let service = match port.open(Opening::Degraded).await {
        Ok(service) => service,
        // Not "you are signed out": a store that will not open is a fault to
        // report, and signing in over a credential that may still be
        // recoverable is exactly the wrong response to it.
        Err(message) => return Need::Report(message),
    };
    match crate::setup::launch_state(service.as_ref(), intent.explicit_provider.as_deref()).await {
        FirstRun::Ready => Need::None,
        FirstRun::NoCredentials => Need::FirstRun,
        // "Required" is decided here, from the launch's own intent, and not
        // from what the selector happened to name: with an explicit provider
        // the selector reports *that* account, so an explicit launch is by
        // construction a launch whose account is a condition.
        FirstRun::SelectedMissing { identity, .. } => Need::SelectedMissing {
            identity,
            required: intent.explicit_provider.is_some(),
        },
        FirstRun::Unusable(reason) => Need::Report(reason),
    }
}

/// What the wizard did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SetupOutcome {
    /// Nothing was written and no engine may be started.
    Cancelled,
    /// An account is connected on this machine. The launch continues.
    Connected {
        identity: ProviderIdentity,
        deployment: Option<CopilotDeploymentChoice>,
        /// Said after the fact, safely: hosts, never URLs.
        notes: Vec<String>,
    },
    /// The wizard could not run. The launch continues and the engine fails
    /// closed on its own terms, which is a better outcome than a front-end
    /// that pretends to have fixed something.
    Unavailable(String),
}

/// Where the wizard gets its keys.
///
/// A port rather than a `crossterm::EventStream` so the loop can be driven
/// from a test with a scripted sequence, against a `TestBackend`, without a
/// real terminal, a real console or a real person.
#[async_trait]
pub trait WizardInput: Send {
    async fn next(&mut self) -> Option<Event>;
}

/// The production input: this process's terminal.
pub struct TerminalInput {
    events: crossterm::event::EventStream,
}

impl Default for TerminalInput {
    fn default() -> Self {
        Self::new()
    }
}

impl TerminalInput {
    pub fn new() -> Self {
        Self { events: crossterm::event::EventStream::new() }
    }
}

#[async_trait]
impl WizardInput for TerminalInput {
    async fn next(&mut self) -> Option<Event> {
        use futures_lite::StreamExt;
        loop {
            match self.events.next().await {
                Some(Ok(event)) => return Some(event),
                // A single bad read is not a reason to abandon a sign-in that
                // has not been answered yet.
                Some(Err(_)) => continue,
                None => return None,
            }
        }
    }
}

/// A scripted input, for tests and for a launcher that has none.
pub struct ScriptedInput {
    events: std::collections::VecDeque<Event>,
}

impl ScriptedInput {
    pub fn new(events: Vec<Event>) -> Self {
        Self { events: events.into() }
    }

    pub fn keys(codes: &[KeyCode]) -> Self {
        Self::new(
            codes
                .iter()
                .map(|code| Event::Key(KeyEvent::new(*code, KeyModifiers::NONE)))
                .collect(),
        )
    }

    pub fn push(&mut self, event: Event) {
        self.events.push_back(event);
    }
}

#[async_trait]
impl WizardInput for ScriptedInput {
    async fn next(&mut self) -> Option<Event> {
        match self.events.pop_front() {
            Some(event) => Some(event),
            // Deliberately not `None`: an exhausted script must not be read as
            // "the user closed the terminal" while a task is still running.
            None => {
                std::future::pending::<()>().await;
                None
            }
        }
    }
}

/// How the wizard behaves in the environment it was started in.
#[derive(Debug, Clone, Copy)]
pub struct SetupOptions {
    /// Whether a browser may be launched for an authorization URL.
    ///
    /// A value rather than a look at the environment inside the flow: a test
    /// drives a real browser login with it off, so nothing can open a window
    /// on the machine running it, and `CODA_AUTH_NO_BROWSER` is honoured in
    /// exactly one place.
    pub open_browser: bool,
}

impl Default for SetupOptions {
    fn default() -> Self {
        Self {
            open_browser: coda_boot::auth_cli::browser_allowed(
                std::env::var_os(coda_boot::auth_cli::NO_BROWSER_ENV).as_deref(),
            ),
        }
    }
}

impl SetupOptions {
    /// The address is shown and nothing is launched.
    pub fn without_browser() -> Self {
        Self { open_browser: false }
    }
}

/// Runs the engine-less setup loop.
///
/// The flow, the surfaces and the disclosure are the running application's;
/// only the loop around them is local. Nothing here starts, stops or contacts
/// an engine, and nothing is written until a commit that was disclosed first.
pub async fn run_setup<B: Backend, I: WizardInput + ?Sized>(
    terminal: &mut Terminal<B>,
    input: &mut I,
    theme: &Theme,
    port: &AuthPort,
    need: &Need,
    options: &SetupOptions,
) -> SetupOutcome {
    let service = match port.open(Opening::Strict).await {
        Ok(service) => service,
        Err(message) => return SetupOutcome::Unavailable(message),
    };

    let mut flow: LoginFlow<FlowEvent> = LoginFlow::new();
    let mut events = flow.take_events();
    let mut stack = SurfaceStack::default();
    let mut notices: Vec<String> = Vec::new();
    let mut heading = need.heading();

    let preselect = need.preselect();
    let required = need.required();
    let saved_domain =
        service.status().await.ok().and_then(|status| status.github_enterprise_domain);
    stack.push(Box::new(
        AuthChoiceSurface::open(
            "Connect an account",
            disclosure(service.as_ref(), preselect).await,
            preselect,
            saved_domain.clone(),
        )
        .requiring(required),
    ));
    flow.hold_service(service.clone());

    let opener = options.open_browser.then(crate::local::login::system_browser);

    let outcome = loop {
        if draw(terminal, theme, &heading, &notices, &stack).is_err() {
            // The screen is gone. Nothing has been written by this point that
            // is not already settled, and continuing blind would be worse.
            flow.close().await;
            break SetupOutcome::Cancelled;
        }

        tokio::select! {
            event = input.next() => {
                let Some(event) = event else {
                    flow.close().await;
                    break SetupOutcome::Cancelled;
                };
                let key = match event {
                    Event::Key(key)
                        if matches!(key.kind, KeyEventKind::Press | KeyEventKind::Repeat) => key,
                    // Nothing else changes what is on screen; a resize is
                    // handled by the redraw at the top of the loop.
                    _ => continue,
                };
                if key.code == KeyCode::Char('c') && key.modifiers.contains(KeyModifiers::CONTROL) {
                    if flow.is_committing() {
                        notices.push(
                            "The credential is being written and cannot be cancelled; it will \
                             finish in a moment."
                                .to_owned(),
                        );
                        continue;
                    }
                    flow.cancel();
                    flow.close().await;
                    break SetupOutcome::Cancelled;
                }
                match stack.handle_key(key) {
                    StackOutcome::Action(SurfaceAction::CancelAuth) => {
                        if flow.is_committing() {
                            notices.push(
                                "The credential is being written and cannot be cancelled; it \
                                 will finish in a moment."
                                    .to_owned(),
                            );
                            continue;
                        }
                        flow.cancel();
                        flow.close().await;
                        break SetupOutcome::Cancelled;
                    }
                    StackOutcome::Action(SurfaceAction::SubmitAuthChoice) => {
                        match begin(&mut flow, &mut stack, opener.clone()) {
                            Ok(()) => heading = Vec::new(),
                            Err(reason) => notices.push(reason),
                        }
                    }
                    // Every other action belongs to a surface this loop never
                    // opens; refusing is better than dispatching it somewhere.
                    _ => {}
                }
            }
            Some(event) = events.recv() => {
                if !flow.is_current(event.generation()) {
                    if matches!(event, FlowEvent::Committed { .. } | FlowEvent::LoggedOut { .. }) {
                        flow.settle();
                    }
                    continue;
                }
                match event {
                    FlowEvent::Challenge { title, lines, .. } => {
                        if !stack.replace_top(Box::new(AuthChallengeSurface::new(
                            title, lines, true,
                        ))) {
                            flow.cancel();
                            flow.close().await;
                            break SetupOutcome::Cancelled;
                        }
                    }
                    FlowEvent::Prepared { result: None, .. } => {
                        flow.cancel();
                        flow.close().await;
                        break SetupOutcome::Cancelled;
                    }
                    FlowEvent::Prepared { result: Some(result), .. } => match *result {
                        Ok(prepared) => {
                            let replacement = if prepared.uses_environment_key() {
                                coda_boot::auth_cli::render_environment_replacement(
                                    prepared.replaces(),
                                )
                            } else {
                                coda_boot::auth_cli::render_replacement(
                                    prepared.identity(),
                                    prepared.replaces(),
                                )
                            };
                            if !replacement.is_empty() {
                                notices.push(replacement);
                            }
                            stack.replace_top(Box::new(AuthChallengeSurface::new(
                                "Saving",
                                vec![
                                    "Saving the credential.".to_owned(),
                                    String::new(),
                                    "This step is not cancellable.".to_owned(),
                                ],
                                false,
                            )));
                            // No engine has been started, so there is nothing
                            // to stop before the profile is written.
                            flow.commit(prepared, std::future::ready(()));
                        }
                        Err(failure) => {
                            flow.settle();
                            notices.push(coda_boot::auth_cli::render_prepare_failure(&failure));
                            reopen(&mut flow, &mut stack, &service, preselect, required, saved_domain.clone())
                                .await;
                        }
                    },
                    FlowEvent::Committed { outcome, .. } => {
                        flow.settle();
                        let target = flow.target().cloned();
                        notices.push(coda_boot::auth_cli::render_commit(&outcome));
                        match (*outcome, target) {
                            (
                                coda_auth::service::CommitOutcome::Committed { .. },
                                Some(target),
                            ) => {
                                let mut notes = notices;
                                notes.push(committed_destination(
                                    service.as_ref(),
                                    target.identity,
                                    target.deployment.as_ref(),
                                ));
                                if target.environment_only {
                                    notes.push(
                                        "No API key was stored: Coda will use the \
                                         ANTHROPIC_API_KEY exported in the environment it runs \
                                         in, so a shell without that variable is not signed in."
                                            .to_owned(),
                                    );
                                }
                                flow.close().await;
                                break SetupOutcome::Connected {
                                    identity: target.identity,
                                    deployment: target.deployment,
                                    notes,
                                };
                            }
                            // Nothing was written, or it was put back. The
                            // user is told, and gets the form back rather than
                            // a launch that continues on a false premise.
                            (_, _) => {
                                notices.push(
                                    "Nothing was connected. Choose an account to try again, or \
                                     press Esc to leave."
                                        .to_owned(),
                                );
                                reopen(
                                    &mut flow,
                                    &mut stack,
                                    &service,
                                    preselect,
                                    required,
                                    saved_domain.clone(),
                                )
                                .await;
                            }
                        }
                    }
                    // A launcher never signs out.
                    FlowEvent::LoggedOut { .. } => flow.settle(),
                }
            }
        }
    };

    // Whatever happened, the last frame the user saw is not left half-drawn.
    let _ = draw(terminal, theme, &[], &[], &SurfaceStack::default());
    outcome
}

/// Reads the choice off the surface and starts the preparation.
fn begin(
    flow: &mut LoginFlow<FlowEvent>,
    stack: &mut SurfaceStack,
    opener: Option<crate::local::login::BrowserOpener>,
) -> Result<(), String> {
    // Read before popping: a refused answer must leave the form exactly where
    // it was, with everything the user typed still in it.
    let choice = {
        let Some(surface) = stack.top() else {
            return Err("The sign-in form closed before it was answered.".to_owned());
        };
        let Some(form) = surface.as_any().downcast_ref::<AuthChoiceSurface>() else {
            return Err("Could not sign in: unexpected surface.".to_owned());
        };
        form.choice()?
    };
    stack.pop();
    let Some(service) = flow.service() else {
        return Err("The sign-in lost its profile before it started.".to_owned());
    };

    let identity = choice.identity;
    let environment_only = choice.use_environment_key;
    let request = match identity {
        ProviderIdentity::ClaudeAi => coda_auth::service::LoginRequest::claude_ai(),
        ProviderIdentity::GithubCopilot => {
            coda_auth::service::LoginRequest::copilot(choice.deployment.clone())
        }
        ProviderIdentity::AnthropicApiKey => {
            coda_auth::service::LoginRequest::api_key(if environment_only {
                coda_auth::service::ApiKeySource::Environment
            } else {
                coda_auth::service::ApiKeySource::Prompt
            })
        }
    };
    let key = choice
        .api_key
        .filter(|key| !key.trim().is_empty())
        .map(|key| coda_auth::Secret::new(key.trim().to_owned()));

    // Where the account that was *chosen* will be authorized, said before the
    // preparation sends anything.
    let mut lines = vec![format!("Signing in to {}.", identity.label())];
    lines.extend(destination_lines(service.as_ref(), identity, choice.deployment.as_ref()));
    lines.push(String::new());
    lines.push("Nothing has been saved yet, and no engine has been started.".to_owned());
    stack.push(Box::new(AuthChallengeSurface::new("Connecting", lines, true)));

    flow.begin(
        service,
        request,
        key,
        Target { identity, deployment: choice.deployment, environment_only },
        opener,
    );
    Ok(())
}

/// Puts the choice form back after an attempt that connected nothing.
async fn reopen(
    flow: &mut LoginFlow<FlowEvent>,
    stack: &mut SurfaceStack,
    service: &std::sync::Arc<coda_auth::service::AuthService>,
    preselect: Option<ProviderIdentity>,
    required: Option<ProviderIdentity>,
    saved_domain: Option<String>,
) {
    flow.cancel();
    while stack.top().is_some() {
        stack.pop();
    }
    stack.push(Box::new(
        AuthChoiceSurface::open(
            "Connect an account",
            disclosure(service.as_ref(), preselect).await,
            preselect,
            saved_domain,
        )
        .requiring(required),
    ));
    flow.hold_service(service.clone());
}

/// One frame: why this opened, what has happened, and the live surface.
fn draw<B: Backend>(
    terminal: &mut Terminal<B>,
    theme: &Theme,
    heading: &[String],
    notices: &[String],
    stack: &SurfaceStack,
) -> Result<(), B::Error> {
    terminal.draw(|frame| {
        let area = frame.area();
        frame.render_widget(
            ratatui::widgets::Block::default().style(theme.surface()),
            area,
        );
        let mut lines: Vec<Line<'static>> = Vec::new();
        lines.push(Line::styled("Coda — connect an account".to_owned(), theme.style(Role::Heading)));
        lines.push(Line::from(String::new()));
        for row in heading {
            lines.push(Line::from(row.clone()));
        }
        if !heading.is_empty() {
            lines.push(Line::from(String::new()));
        }
        // Bounded: a scrolling report is not what this screen is for, and the
        // most recent answer is the one that matters.
        for row in notices.iter().rev().take(6).collect::<Vec<_>>().into_iter().rev() {
            for wrapped in row.split('\n') {
                lines.push(Line::styled(
                    wrapped.to_owned(),
                    theme.style(Role::Notification),
                ));
            }
        }
        let height = (lines.len() as u16).min(area.height);
        frame.render_widget(
            Paragraph::new(lines).wrap(ratatui::widgets::Wrap { trim: false }),
            Rect { x: area.x, y: area.y, width: area.width, height },
        );
        for rendered in stack.render(area, theme) {
            crate::draw::draw_surface(frame, &rendered, theme);
        }
    })?;
    Ok(())
}

/// The launch, after the preflight has had its say.
#[derive(Debug)]
pub enum Launch {
    /// Start the engine with this command. It names the account the wizard
    /// connected, when one was connected.
    Proceed {
        command: EngineCommand,
        notes: Vec<String>,
        /// What the wizard settled, when it ran at all.
        ///
        /// Carried rather than left implicit because the launcher has options
        /// of its own to reconcile — `--model` above all — and it cannot know
        /// from the command alone whether the account it was aiming at is the
        /// one that ended up connected.
        connected: Option<Connected>,
    },
    /// Nothing was changed and no engine may be started.
    Abandoned(String),
}

/// The account a setup connected, and whether it is the one the launch meant.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Connected {
    pub identity: ProviderIdentity,
    /// Whether the launch's own account intent survived the wizard.
    ///
    /// False only when the launch was aiming at a *named* account — its saved
    /// default — and the user connected a different one instead. Options that
    /// named that account's model are then the previous account's
    /// instructions, exactly as `--provider` and `--model` in the engine
    /// arguments are, and are dropped for the same reason.
    pub kept_intent: bool,
}

impl Launch {
    /// Whether an explicit `--model` this launch carries still belongs to the
    /// account that is about to be connected.
    ///
    /// A model is named *for* a provider. When the wizard connected a
    /// different account than the launch was aiming at, re-applying the model
    /// after boot pushes one account's model onto another's session — which
    /// the engine either rejects or, worse, quietly accepts.
    pub fn keeps_model_intent(&self) -> bool {
        match self {
            Launch::Proceed { connected: Some(connected), .. } => connected.kept_intent,
            _ => true,
        }
    }
}

/// Applies a completed setup to the launch the binary had assembled.
///
/// The same rebuild an in-session switch performs: the previous account's
/// arguments and inherited variables are dropped, and an explicit Copilot
/// deployment survives a conflicting inherited domain.
pub fn apply(command: EngineCommand, outcome: &SetupOutcome) -> EngineCommand {
    match outcome {
        SetupOutcome::Connected { identity, deployment, .. } => {
            engine_command_for(&command, *identity, deployment.as_ref())
        }
        _ => command,
    }
}

/// The whole preflight, for a binary that is about to start an engine.
///
/// Enters the terminal **only** when a setup is actually needed, and restores
/// it before returning, so a cancellation or an error prints on an ordinary
/// screen instead of behind an alternate one.
pub async fn prepare_launch(
    command: EngineCommand,
    intent: &LaunchIntent,
    port: &AuthPort,
    theme: &Theme,
    capture_mouse: bool,
) -> Launch {
    let theme = theme.clone();
    prepare_launch_with(command, intent, port, move |need| async move {
        let mut guard = match crate::terminal::TerminalGuard::enter(capture_mouse) {
            Ok(guard) => guard,
            Err(error) => {
                // No terminal, no wizard. Said plainly, naming the command
                // that does the same job without one.
                return SetupOutcome::Unavailable(format!(
                    "the setup screen could not be opened here ({error}); run `coda auth login` \
                     to connect an account"
                ));
            }
        };
        let mut input = TerminalInput::new();
        let outcome = run_setup(
            guard.terminal(),
            &mut input,
            &theme,
            port,
            &need,
            &SetupOptions::default(),
        )
        .await;
        drop(guard);
        outcome
    })
    .await
}

/// [`prepare_launch`] with the terminal supplied by the caller.
///
/// The seam both binaries actually run through, and the one a test drives:
/// `setup` is only ever called when a sign-in is genuinely needed, and the
/// engine is spawned by the caller *after* this returns — so "the wizard came
/// before the child" is a property of this function rather than of a comment
/// in two `main`s.
pub async fn prepare_launch_with<F, Fut>(
    command: EngineCommand,
    intent: &LaunchIntent,
    port: &AuthPort,
    setup: F,
) -> Launch
where
    F: FnOnce(Need) -> Fut,
    Fut: std::future::Future<Output = SetupOutcome>,
{
    let need = decide(intent, port).await;
    match &need {
        Need::None => {
            return Launch::Proceed { command, notes: Vec::new(), connected: None }
        }
        // Reported, then left to the engine: signing in again over a
        // credential that may still be recoverable is the wrong response.
        Need::Report(reason) => {
            return Launch::Proceed {
                command,
                notes: vec![reason.clone()],
                connected: None,
            };
        }
        _ => {}
    }
    // What this launch was aiming at, before the wizard ran. An explicit
    // provider is a condition the wizard cannot change; a saved default is
    // one the user may walk away from, and then the launch's own
    // account-specific options no longer describe what is connected.
    let aimed_at = need.preselect();
    // And what it *required*: an account the launch named explicitly is not a
    // preference, it is the instruction. The wizard constrains itself to it
    // before it commits anything, so a setup that comes back with a different
    // account is a seam that was not honoured — not a user choice.
    let required = need.required();

    let outcome = setup(need).await;
    match outcome {
        SetupOutcome::Connected { identity, .. }
            if required.is_some_and(|wanted| wanted != identity) =>
        {
            // Reported truthfully, and as a refusal to start anything. The
            // setup may already have committed — this seam cannot un-connect
            // an account, and pretending nothing happened would be the lie —
            // so what is promised here is only what this function controls:
            // no engine is started on an account the launch did not ask for.
            let wanted = required.expect("a required account").label();
            Launch::Abandoned(format!(
                "The setup returned a different provider than this launch requires: it asked for \
                 {wanted} and {} was connected. Whatever the setup saved has been kept, but no \
                 engine was started; run `coda` again to use the account that is now connected, \
                 or `coda auth login` to connect {wanted}.",
                identity.label()
            ))
        }
        SetupOutcome::Connected { ref notes, identity, .. } => {
            let mut notes = notes.clone();
            let kept_intent = aimed_at.is_none_or(|wanted| wanted == identity);
            if !kept_intent {
                notes.push(format!(
                    "Connected {} instead of the account this launch was set to. Options that \
                     named the previous account — a model, an endpoint — were not carried \
                     over; the engine resolves them for the account you connected.",
                    identity.label()
                ));
            }
            Launch::Proceed {
                command: apply(command, &outcome),
                notes,
                connected: Some(Connected { identity, kept_intent }),
            }
        }
        SetupOutcome::Cancelled => Launch::Abandoned(
            "Setup cancelled. Nothing was saved and no engine was started; run `coda` again, or \
             `coda auth login`, when you want to connect an account."
                .to_owned(),
        ),
        // The wizard could not run at all. Saying so and letting the engine
        // fail closed on its own terms is better than a front-end that
        // pretends to have fixed something.
        SetupOutcome::Unavailable(reason) => Launch::Abandoned(format!(
            "No account is connected on this machine, and {reason}. Nothing was changed and no \
             engine was started."
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_auth::service::{AuthSettings, InMemoryAuthSettings};
    use ratatui::backend::TestBackend;
    use std::sync::Arc;

    fn env_of<'a>(pairs: &'a [(&'a str, &'a str)]) -> impl Fn(&str) -> Option<String> + 'a {
        move |key: &str| {
            pairs
                .iter()
                .find(|(name, _)| *name == key)
                .map(|(_, value)| (*value).to_owned())
        }
    }

    /// A port over a throwaway profile with an explicit environment.
    fn port(root: &std::path::Path, settings: AuthSettings, env: Vec<(String, String)>) -> AuthPort {
        let root = root.to_path_buf();
        AuthPort::from_factory(move |opening| {
            let root = root.clone();
            let settings = settings.clone();
            let env = env.clone();
            Box::pin(async move {
                let storage = coda_auth::store::open_profile_storage(
                    &coda_auth::store::Profile::isolated(&root),
                )
                .map_err(|error| error.to_string())?;
                let pairs: Vec<(&str, &str)> =
                    env.iter().map(|(k, v)| (k.as_str(), v.as_str())).collect();
                let builder = coda_auth::service::AuthService::builder(
                    Arc::clone(&storage.profile),
                    Arc::clone(&storage.coordinator),
                )
                .with_settings(Arc::new(InMemoryAuthSettings::with(settings)))
                .with_environment(Arc::new(coda_auth::service::MapEnvironment::new(&pairs))
                    as Arc<dyn coda_auth::service::AuthEnvironment>);
                match opening {
                    Opening::Strict => {
                        builder.build().await.map(Arc::new).map_err(|e| e.to_string())
                    }
                    Opening::Degraded => Ok(Arc::new(builder.build_degraded().await)),
                }
            })
        })
    }

    #[tokio::test]
    async fn an_api_only_launch_decides_without_opening_the_operators_credential_store() {
        let root = tempfile::tempdir().expect("temp");
        let port = port(root.path(), AuthSettings::default(), Vec::new());
        let intent = LaunchIntent::from_launch(
            AccessMode::ApiOnly,
            Some("github-copilot"),
            &[],
            env_of(&[]),
        );
        assert_eq!(decide(&intent, &port).await, Need::None);
        assert_eq!(port.opens(), 0, "an API-only launch probed this machine's credentials");
    }

    #[tokio::test]
    async fn a_fresh_profile_is_a_first_run_and_a_healthy_one_is_silent() {
        let root = tempfile::tempdir().expect("temp");
        let empty = port(root.path(), AuthSettings::default(), Vec::new());
        let intent = LaunchIntent::from_launch(AccessMode::TrustedLocal, None, &[], env_of(&[]));
        assert_eq!(decide(&intent, &empty).await, Need::FirstRun);

        let healthy = port(
            root.path(),
            AuthSettings::default(),
            vec![("ANTHROPIC_API_KEY".to_owned(), "sk-x".to_owned())],
        );
        assert_eq!(decide(&intent, &healthy).await, Need::None);
    }

    #[tokio::test]
    async fn a_launch_that_names_a_provider_it_has_no_credential_for_can_be_set_up() {
        let root = tempfile::tempdir().expect("temp");
        let port = port(
            root.path(),
            AuthSettings::default(),
            vec![("ANTHROPIC_API_KEY".to_owned(), "sk-x".to_owned())],
        );
        // From a flag…
        let flagged = LaunchIntent::from_launch(
            AccessMode::TrustedLocal,
            Some("github-copilot"),
            &[],
            env_of(&[]),
        );
        assert_eq!(
            decide(&flagged, &port).await,
            Need::SelectedMissing { identity: ProviderIdentity::GithubCopilot, required: true }
        );

        // …from an engine argument…
        let argued = LaunchIntent::from_launch(
            AccessMode::TrustedLocal,
            None,
            &["--provider".to_owned(), "claude".to_owned()],
            env_of(&[]),
        );
        assert_eq!(
            decide(&argued, &port).await,
            Need::SelectedMissing { identity: ProviderIdentity::ClaudeAi, required: true }
        );

        // …and from the environment the engine would inherit.
        let exported = LaunchIntent::from_launch(
            AccessMode::TrustedLocal,
            None,
            &[],
            env_of(&[("CODA_SERVE_PROVIDER", "github-copilot")]),
        );
        assert_eq!(
            decide(&exported, &port).await,
            Need::SelectedMissing { identity: ProviderIdentity::GithubCopilot, required: true }
        );
    }

    #[tokio::test]
    async fn a_launch_carrying_its_own_key_is_never_interrupted() {
        let root = tempfile::tempdir().expect("temp");
        let port = port(root.path(), AuthSettings::default(), Vec::new());
        let intent = LaunchIntent::from_launch(
            AccessMode::TrustedLocal,
            None,
            &["--api-key".to_owned(), "sk-launch".to_owned()],
            env_of(&[]),
        );
        assert_eq!(decide(&intent, &port).await, Need::None);
        assert_eq!(port.opens(), 0);
    }

    #[tokio::test]
    async fn an_unknown_provider_is_reported_rather_than_offered_a_wizard() {
        let root = tempfile::tempdir().expect("temp");
        let port = port(root.path(), AuthSettings::default(), Vec::new());
        let intent = LaunchIntent::from_launch(
            AccessMode::TrustedLocal,
            Some("not-a-provider"),
            &[],
            env_of(&[]),
        );
        match decide(&intent, &port).await {
            Need::Report(reason) => assert!(reason.contains("does not know"), "{reason}"),
            other => panic!("an unknown provider was treated as {other:?}"),
        }
    }

    #[tokio::test]
    async fn a_cancelled_setup_writes_nothing_and_starts_nothing() {
        let root = tempfile::tempdir().expect("temp");
        let port = port(root.path(), AuthSettings::default(), Vec::new());
        let mut terminal = Terminal::new(TestBackend::new(100, 40)).expect("terminal");
        let mut input = ScriptedInput::keys(&[KeyCode::Esc]);
        let outcome =
            run_setup(&mut terminal, &mut input, &Theme::default(), &port, &Need::FirstRun, &SetupOptions::without_browser()).await;
        assert_eq!(outcome, SetupOutcome::Cancelled);

        // Nothing was connected, so the same launch would ask again.
        let intent = LaunchIntent::from_launch(AccessMode::TrustedLocal, None, &[], env_of(&[]));
        assert_eq!(decide(&intent, &port).await, Need::FirstRun);
    }

    #[tokio::test]
    async fn the_wizard_says_why_it_opened_and_offers_the_account_the_launch_asked_for() {
        let root = tempfile::tempdir().expect("temp");
        let port = port(root.path(), AuthSettings::default(), Vec::new());
        let mut terminal = Terminal::new(TestBackend::new(100, 40)).expect("terminal");
        // One keystroke that changes nothing, then leave: enough for a frame.
        let mut input = ScriptedInput::keys(&[KeyCode::Tab, KeyCode::Esc]);
        let need =
            Need::SelectedMissing { identity: ProviderIdentity::GithubCopilot, required: true };
        let outcome = run_setup(&mut terminal, &mut input, &Theme::default(), &port, &need, &SetupOptions::without_browser()).await;
        assert_eq!(outcome, SetupOutcome::Cancelled);
    }

    #[test]
    fn a_connected_setup_rebuilds_the_launch_for_the_account_it_connected() {
        let command = EngineCommand::new("coda")
            .arg("serve")
            .arg("--provider")
            .arg("claude")
            .env("GH_COPILOT_ENTERPRISE_DOMAIN", "old.ghe.com");
        let outcome = SetupOutcome::Connected {
            identity: ProviderIdentity::GithubCopilot,
            deployment: Some(CopilotDeploymentChoice::Public),
            notes: Vec::new(),
        };
        let rebuilt = apply(command, &outcome);
        let args: Vec<String> =
            rebuilt.args.iter().map(|a| a.to_string_lossy().into_owned()).collect();
        assert_eq!(args, vec!["serve"], "the previous account's flag survived the setup");
        assert!(rebuilt
            .env
            .iter()
            .any(|(k, v)| k == "CODA_SERVE_PROVIDER" && v == "github-copilot"));
        assert!(
            rebuilt.env_remove.iter().any(|k| k == "GH_COPILOT_ENTERPRISE_DOMAIN"),
            "an explicitly public login left an inherited tenant in place"
        );
    }

    #[test]
    fn a_cancelled_setup_leaves_the_launch_exactly_as_it_was() {
        let command = EngineCommand::new("coda").arg("serve");
        let rebuilt = apply(command.clone(), &SetupOutcome::Cancelled);
        assert_eq!(rebuilt.env.len(), command.env.len());
        assert_eq!(rebuilt.args.len(), command.args.len());
    }

    /// A socket this test owns, standing in for the Anthropic endpoint.
    ///
    /// It exists to answer one question: did anything get *sent*? A wizard
    /// that refuses an account before it starts a preparation contacts nothing
    /// at all, and "nothing at all" is only observable against a listener that
    /// would have recorded it.
    async fn recording_endpoint() -> (String, Arc<std::sync::Mutex<Vec<String>>>) {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let port = listener.local_addr().expect("addr").port();
        let seen: Arc<std::sync::Mutex<Vec<String>>> = Arc::new(std::sync::Mutex::new(Vec::new()));
        let recorder = Arc::clone(&seen);
        tokio::spawn(async move {
            loop {
                let Ok((mut socket, _)) = listener.accept().await else { return };
                let recorder = Arc::clone(&recorder);
                tokio::spawn(async move {
                    use tokio::io::{AsyncReadExt, AsyncWriteExt};
                    let mut buffer = [0u8; 4096];
                    let read = socket.read(&mut buffer).await.unwrap_or(0);
                    let request = String::from_utf8_lossy(&buffer[..read]).into_owned();
                    recorder
                        .lock()
                        .expect("wire")
                        .push(request.lines().next().unwrap_or_default().to_owned());
                    let body = "{\"data\":[{\"id\":\"claude-test\"}]}";
                    let response = format!(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: \
                         {}\r\nConnection: close\r\n\r\n{body}",
                        body.len()
                    );
                    let _ = socket.write_all(response.as_bytes()).await;
                });
            }
        });
        (format!("http://127.0.0.1:{port}"), seen)
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn an_explicitly_named_provider_is_a_condition_the_wizard_may_not_quietly_replace() {
        // `--provider github-copilot` is an instruction about which account
        // this session talks to. The wizard used to let a different account be
        // connected and then rebuilt the launch for *that* one — after which
        // the launch's own verification failed it for connecting the account
        // the user had just chosen, and an explicit `--model` was re-applied to
        // an account it was never meant for.
        //
        // Nothing may be sent for the account that was not asked for, so the
        // discriminator is the endpoint: an exported key, a real environment
        // login selected on the form, and a socket this test owns that would
        // have recorded the probe.
        let root = tempfile::tempdir().expect("temp");
        let (endpoint, seen) = recording_endpoint().await;
        let port = port(
            root.path(),
            AuthSettings::default(),
            vec![
                ("ANTHROPIC_API_KEY".to_owned(), "sk-exported-not-real".to_owned()),
                ("ANTHROPIC_BASE_URL".to_owned(), endpoint),
            ],
        );
        let mut terminal = Terminal::new(TestBackend::new(100, 44)).expect("terminal");
        // The form opens on GitHub Copilot's deployment question. Back to the
        // account list, up to the Anthropic API key, forward to the key
        // source, choose the exported key, submit — then leave.
        let mut input = ScriptedInput::keys(&[
            KeyCode::BackTab,
            KeyCode::Up,
            KeyCode::Tab,
            KeyCode::Tab,
            KeyCode::Tab,
            KeyCode::Down,
            KeyCode::Enter,
            KeyCode::Esc,
        ]);
        let need =
            Need::SelectedMissing { identity: ProviderIdentity::GithubCopilot, required: true };
        let outcome = run_setup(
            &mut terminal,
            &mut input,
            &Theme::default(),
            &port,
            &need,
            &SetupOptions::without_browser(),
        )
        .await;

        assert_eq!(
            outcome,
            SetupOutcome::Cancelled,
            "a launch that asked for GitHub Copilot connected something else"
        );
        let asked = seen.lock().expect("wire").clone();
        assert!(
            asked.is_empty(),
            "the wizard contacted a provider for an account the launch never asked for: {asked:?}"
        );
        // And nothing was written, so the same launch would ask again.
        let intent = LaunchIntent::from_launch(
            AccessMode::TrustedLocal,
            Some("github-copilot"),
            &[],
            env_of(&[]),
        );
        assert_eq!(
            decide(&intent, &port).await,
            Need::SelectedMissing { identity: ProviderIdentity::GithubCopilot, required: true }
        );
    }

    #[tokio::test]
    async fn a_saved_default_may_be_switched_and_the_launchs_model_is_not_carried_across() {
        // The other half of the rule. A saved `defaultProvider` with no
        // credential is not something *this launch* asked for, so choosing a
        // different account is a perfectly good answer — and then `--model`,
        // which names a model *for* the account that is no longer connected,
        // is the previous account's instruction exactly as a stale
        // `--provider` in the engine arguments is.
        let root = tempfile::tempdir().expect("temp");
        let settings = AuthSettings {
            default_provider: Some("github-copilot".to_owned()),
            github_enterprise_domain: None,
        };
        let port = port(root.path(), settings, Vec::new());
        let intent = LaunchIntent::from_launch(AccessMode::TrustedLocal, None, &[], env_of(&[]));
        assert_eq!(
            decide(&intent, &port).await,
            Need::SelectedMissing {
                identity: ProviderIdentity::GithubCopilot,
                required: false,
            },
            "a saved default was treated as this launch's own instruction"
        );

        let switched = prepare_launch_with(
            EngineCommand::new("coda").arg("serve"),
            &intent,
            &port,
            |_need| async {
                SetupOutcome::Connected {
                    identity: ProviderIdentity::ClaudeAi,
                    deployment: None,
                    notes: Vec::new(),
                }
            },
        )
        .await;
        assert!(
            !switched.keeps_model_intent(),
            "an explicit --model would have been applied to an account it was not chosen for"
        );
        match &switched {
            Launch::Proceed { notes, connected, .. } => {
                assert_eq!(
                    *connected,
                    Some(Connected { identity: ProviderIdentity::ClaudeAi, kept_intent: false })
                );
                assert!(
                    notes.iter().any(|note| note.contains("were not carried over")),
                    "the switch was silent: {notes:?}"
                );
            }
            Launch::Abandoned(message) => panic!("a completed setup started nothing: {message}"),
        }

        // Connecting the account the launch was aiming at keeps everything it
        // said about that account.
        let kept = prepare_launch_with(
            EngineCommand::new("coda").arg("serve"),
            &intent,
            &port,
            |_need| async {
                SetupOutcome::Connected {
                    identity: ProviderIdentity::GithubCopilot,
                    deployment: None,
                    notes: Vec::new(),
                }
            },
        )
        .await;
        assert!(kept.keeps_model_intent(), "the launch's own model was dropped for no reason");

        // A launch that needed no setup at all is untouched.
        let healthy = Launch::Proceed {
            command: EngineCommand::new("coda"),
            notes: Vec::new(),
            connected: None,
        };
        assert!(healthy.keeps_model_intent());
    }
}

