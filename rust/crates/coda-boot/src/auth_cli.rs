//! `coda auth` / `coda-engine auth` — host-local provider maintenance.
//!
//! # Why this is a command and not an RPC
//!
//! Signing in is *machine* maintenance: it touches this profile's credential
//! store and this user's browser. An external application that supervises Coda
//! owns the engine's lifecycle, not its keychain, so there is deliberately no
//! serve-API method for it. Both binaries expose the same three commands
//! through this one runner, so `coda auth status` and `coda-engine auth status`
//! cannot drift.
//!
//! # Shape
//!
//! ```text
//! plan(args, console)      synchronous, before any profile is opened:
//!                          option validation, the provider picker, and the
//!                          masked key prompt
//!      ↓
//! execute(plan)            asynchronous: open the profile, build the one
//!                          AuthService, run status / login / logout
//! ```
//!
//! Splitting them is what keeps a person standing at a terminal prompt off the
//! async runtime entirely: every blocking read happens before the runtime is
//! built, so no prompt can hold a worker thread or stall teardown.
//!
//! # Cancellation, in three parts
//!
//! Interruption means different things at different points, and the command
//! says which one happened rather than papering over the difference:
//!
//! * **At a prompt** it is the terminal's own cancellation ([`crate::console`]),
//!   which never reaches the runtime at all.
//! * **While preparing a login** it races the preparation. The preparation
//!   future owns the loopback listener and the device poller, so dropping it
//!   closes them; nothing has been written, and the exit code is 130.
//! * **While committing** it is deliberately ignored. A commit that is dropped
//!   half-written could leave a profile this process then reported as
//!   unchanged, so the transaction always runs to a terminal outcome.
//! * **While verifying** it is honoured again, because verification is a
//!   read-only probe that can take a long time — but the credential is already
//!   saved by then, so the report says exactly that instead of implying the
//!   sign-in was undone.
//!
//! [`CancelSignal`] is a port so all of that is testable without signals.
//!
//! # Exit codes
//!
//! | code | meaning |
//! |------|---------|
//! | 0    | the command did what it said |
//! | 1    | an operational failure (store, settings, network, provider, commit) |
//! | 2    | invalid usage (a rejected option, a missing provider, bad input) |
//! | 130  | the user cancelled before anything was written |
//!
//! # What this never does
//!
//! * It never writes an authorization URL or a device code to the diagnostic
//!   log: an auth command does not open the diagnostic log at all. Challenges
//!   are printed to the explicit, ephemeral CLI surface and nowhere else.
//! * It never starts, stops, repoints or reconnects an engine, and never
//!   reports what a running engine is using — that engine holds its own
//!   connection and this process cannot see it.
//! * It never renders an [`coda_auth::error::AuthError`] directly, and never
//!   interpolates provider- or file-supplied text without
//!   [`sanitize`]-ing it first.

use std::sync::{Arc, Mutex};
use std::time::Duration;

use async_trait::async_trait;
use coda_auth::error::AuthError;
use coda_auth::failure::AuthFailure;
use coda_auth::provider::copilot::{CopilotDeployment, CopilotDeploymentChoice};
use coda_auth::provider::DeviceCodePrompt;
use coda_auth::service::{
    ApiKeySource, AnthropicEndpoint, AuthService, AuthSettingsPort, AuthStatus, CommitOutcome,
    CopilotContext, CredentialOrigin, EndpointError, LoginRequest, LoginUi, LogoutFailure,
    LogoutReport, PrepareFailure, PreparedLogin, ProviderIdentity, Selection, SelectionError,
    SelectionSource, StoredState, ANTHROPIC_BASE_URL_ENV,
};
use coda_auth::store::{open_profile_storage, Profile};
use coda_auth::Secret;
use coda_llm::{CredentialSource, LlmClient};

use crate::auth_args::{AuthArgs, AuthCommand, AuthLoginArgs};
use crate::settings_store::SettingsFile;

pub use crate::console::PromptError;

/// The command did what it said.
pub const EXIT_OK: i32 = 0;
/// An operational failure: the store, the settings, the network, the provider.
pub const EXIT_FAILED: i32 = 1;
/// Invalid usage: a rejected option, a missing provider, unusable input.
pub const EXIT_USAGE: i32 = 2;
/// The user cancelled before anything was written.
pub const EXIT_CANCELLED: i32 = 130;

/// Set to a non-empty value to keep this command from launching a browser.
///
/// For hosts and test harnesses that drive a login against a loopback fixture:
/// the challenge is still printed, so the flow is fully drivable, but no
/// window opens on the machine running it.
pub const NO_BROWSER_ENV: &str = "CODA_AUTH_NO_BROWSER";

// ── The console port ─────────────────────────────────────────────────────────

/// The host's terminal, as this command needs it.
///
/// A port rather than direct I/O so the picker, the key prompt and every
/// disclosure can be tested without a terminal — and so the engine binary gets
/// the same behaviour without linking a renderer.
pub trait AuthConsole: Send + Sync {
    /// Whether a person can be prompted (both stdin and stderr are terminals).
    fn is_interactive(&self) -> bool;
    /// Whether **stdin itself** is a terminal.
    ///
    /// Separate from [`Self::is_interactive`] because "may I read a redirected
    /// key from stdin?" depends only on stdin: asked to read one while stdin
    /// is the keyboard, this command must refuse rather than read the key in
    /// clear text with echo on.
    fn stdin_is_terminal(&self) -> bool {
        self.is_interactive()
    }
    /// The command's result, on stdout.
    fn report(&self, text: &str);
    /// A prompt, a challenge, or a failure, on stderr.
    fn notice(&self, text: &str);
    /// One visible line (a menu choice — never a secret).
    fn read_line(&self, prompt: &str) -> Result<String, PromptError>;
    /// One secret line, not echoed.
    fn read_masked(&self, prompt: &str) -> Result<Secret<String>, PromptError>;
    /// One secret line from an explicitly redirected stdin.
    fn read_piped_api_key(&self) -> Result<Secret<String>, PromptError>;
}

/// The real terminal.
#[derive(Debug, Clone, Copy, Default)]
pub struct SystemConsole;

impl AuthConsole for SystemConsole {
    fn is_interactive(&self) -> bool {
        crate::console::is_interactive()
    }

    fn stdin_is_terminal(&self) -> bool {
        crate::console::stdin_is_terminal()
    }

    fn report(&self, text: &str) {
        println!("{text}");
    }

    fn notice(&self, text: &str) {
        eprintln!("{text}");
    }

    fn read_line(&self, prompt: &str) -> Result<String, PromptError> {
        crate::console::read_line(prompt)
    }

    fn read_masked(&self, prompt: &str) -> Result<Secret<String>, PromptError> {
        crate::console::read_masked_line(prompt)
    }

    fn read_piped_api_key(&self) -> Result<Secret<String>, PromptError> {
        let stdin = std::io::stdin();
        crate::secret_input::read_api_key_line(stdin.lock()).map_err(PromptError::Invalid)
    }
}

// ── The cancellation port ────────────────────────────────────────────────────

/// Something the user can do to stop a long operation.
///
/// A port because the two places that honour it — preparing a login and
/// verifying one — have to be provable without sending a real signal, and
/// because a process-wide signal handler is not something a library may
/// install on a caller's behalf without saying so.
#[async_trait]
pub trait CancelSignal: Send + Sync {
    /// Resolves when the user asks to stop. Awaited more than once per run.
    async fn cancelled(&self);
}

/// The console interrupt (`Ctrl-C`).
///
/// # A caveat worth knowing
///
/// The first await installs a process-wide handler that stays for the life of
/// the process, and from then on an interrupt no longer terminates it by
/// default. An interrupt that arrives while nothing is awaiting — during the
/// commit, which is deliberately uninterruptible — is therefore absorbed
/// rather than acted on. The commit is short and always reaches a terminal
/// outcome, which is the property that matters; the alternative, letting the
/// default action kill the process mid-transaction, is the one this trades
/// away.
#[derive(Debug, Clone, Copy, Default)]
pub struct InterruptSignal;

#[async_trait]
impl CancelSignal for InterruptSignal {
    async fn cancelled(&self) {
        if tokio::signal::ctrl_c().await.is_err() {
            // The handler could not be installed. Never resolving is right:
            // this must not look like a cancellation nobody asked for.
            std::future::pending::<()>().await
        }
    }
}

/// A signal that never fires — for a host that has its own cancellation, and
/// for tests of the uninterrupted path.
#[derive(Debug, Clone, Copy, Default)]
pub struct NeverCancelled;

#[async_trait]
impl CancelSignal for NeverCancelled {
    async fn cancelled(&self) {
        std::future::pending::<()>().await
    }
}

// ── Safe rendering of untrusted text ─────────────────────────────────────────

/// Render an untrusted value on one line, with nothing in it that can move a
/// cursor or forge a line of this report.
///
/// Account labels come from a provider, `defaultProvider` and
/// `githubEnterpriseDomain` come from a file anyone can edit, and a device
/// code comes off the wire. A `\r`, an `\x1b[2J`, or an embedded newline in
/// any of them repaints or fabricates output that the user reads as this
/// command's own words. Control characters and bidirectional overrides are
/// therefore replaced — never dropped, because dropping silently joins
/// `"a\nb"` into `"ab"` — and the result is bounded.
pub fn sanitize(value: &str, max_chars: usize) -> String {
    let unsafe_char = |ch: char| {
        ch.is_control()
            || matches!(ch,
                '\u{200e}' | '\u{200f}'
                | '\u{202a}'..='\u{202e}'
                | '\u{2066}'..='\u{2069}')
    };
    let mut out = String::new();
    for (index, ch) in value.chars().enumerate() {
        if index == max_chars {
            out.push('…');
            break;
        }
        out.push(if unsafe_char(ch) { '\u{fffd}' } else { ch });
    }
    if out.is_empty() {
        "(empty)".to_owned()
    } else {
        out
    }
}

/// The bound for one interpolated value: long enough for a real host name or
/// account, short enough that nothing can push the rest of the report away.
const SANITIZE_LIMIT: usize = 200;

fn safe(value: &str) -> String {
    sanitize(value, SANITIZE_LIMIT)
}

// ── The plan ─────────────────────────────────────────────────────────────────

/// What a login will do, once the terminal has been consulted.
pub struct LoginPlan {
    /// The account to sign in to.
    pub identity: ProviderIdentity,
    /// An explicitly named Copilot deployment, when one was given.
    pub deployment: Option<CopilotDeploymentChoice>,
    /// The key collected from the terminal or from an explicit stdin.
    ///
    /// Never printed, never in `Debug`, and never an argument: a literal key
    /// on a command line would land in the shell history and the process list.
    pub api_key: Option<Secret<String>>,
    /// Use the exported `ANTHROPIC_API_KEY` rather than storing a new key.
    pub use_env: bool,
}

impl std::fmt::Debug for LoginPlan {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LoginPlan")
            .field("identity", &self.identity)
            .field("deployment", &self.deployment)
            .field("api_key", &self.api_key.as_ref().map(|_| "[REDACTED]"))
            .field("use_env", &self.use_env)
            .finish()
    }
}

/// What the command will do.
#[derive(Debug)]
pub enum AuthPlan {
    Status,
    Logout(Option<ProviderIdentity>),
    Login(LoginPlan),
}

/// Why no plan could be made. Each variant carries its own exit code.
#[derive(Debug)]
pub enum PlanError {
    /// The invocation itself is wrong.
    Usage(String),
    /// The user cancelled at a prompt.
    Cancelled,
    /// The terminal could not be used.
    Failed(String),
}

impl PlanError {
    pub fn exit_code(&self) -> i32 {
        match self {
            Self::Usage(_) => EXIT_USAGE,
            Self::Cancelled => EXIT_CANCELLED,
            Self::Failed(_) => EXIT_FAILED,
        }
    }
}

impl std::fmt::Display for PlanError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Usage(message) | Self::Failed(message) => f.write_str(message),
            Self::Cancelled => f.write_str("Cancelled; nothing was changed."),
        }
    }
}

/// Turn a prompt failure into a plan failure.
///
/// Cancellation, unusable input, a terminal that cannot hide a secret and a
/// terminal that stopped answering are four different things a user needs told
/// apart — and only one of them is their own decision.
fn from_prompt(error: PromptError, missing: &str) -> PlanError {
    match error {
        PromptError::Cancelled => PlanError::Cancelled,
        PromptError::NotInteractive => PlanError::Usage(missing.to_owned()),
        PromptError::EchoUnavailable => PlanError::Usage(ECHO_UNAVAILABLE.to_owned()),
        PromptError::Invalid(invalid) => PlanError::Usage(invalid.to_string()),
        PromptError::EndOfInput => {
            PlanError::Failed("the input ended before an answer was given".to_owned())
        }
        PromptError::Io(kind) => {
            PlanError::Failed(format!("the terminal could not be read ({kind:?})"))
        }
    }
}

const PROVIDER_REQUIRED: &str = "a provider is required when there is no terminal to choose on: \
     `auth login <claude|copilot|api-key>`";
const KEY_SOURCE_REQUIRED: &str = "reading an API key without a terminal requires --api-key-stdin, \
     so a key is never taken from an argument or the environment by accident";
/// Never suggests typing the key anywhere it would be visible.
const ECHO_UNAVAILABLE: &str = "this terminal cannot hide typed input, so the key was not read. \
     Send it through a pipe instead — for example \
     `type keyfile | coda auth login api-key --api-key-stdin` on Windows, or \
     `cat keyfile | coda auth login api-key --api-key-stdin` elsewhere — or run the command from \
     a terminal that supports hidden input";
const STDIN_IS_A_TERMINAL: &str = "--api-key-stdin reads the key from a redirected stdin, but \
     stdin is a terminal here: typed input would be echoed in clear text. Pipe the key in, or \
     omit --api-key-stdin and answer the hidden prompt";

/// Decide everything that can be decided before the profile is opened.
///
/// Provider-inapplicable options are refused here — before a credential store
/// or a settings file is touched — so a rejected invocation cannot have any
/// effect at all.
pub fn plan(args: &AuthArgs, console: &dyn AuthConsole) -> Result<AuthPlan, PlanError> {
    args.validate().map_err(|error| PlanError::Usage(error.to_string()))?;
    match &args.command {
        AuthCommand::Status => Ok(AuthPlan::Status),
        AuthCommand::Logout(logout) => Ok(AuthPlan::Logout(logout.provider)),
        AuthCommand::Login(login) => plan_login(login, console).map(AuthPlan::Login),
    }
}

fn plan_login(login: &AuthLoginArgs, console: &dyn AuthConsole) -> Result<LoginPlan, PlanError> {
    // Refused before the picker runs: an option that cannot be honoured must
    // not first make someone answer a question.
    if login.api_key_stdin && console.stdin_is_terminal() {
        return Err(PlanError::Usage(STDIN_IS_A_TERMINAL.to_owned()));
    }

    let identity = match login.provider {
        Some(identity) => identity,
        None => pick_provider(console)?,
    };

    // `validate` has already established that these belong to Copilot.
    let deployment = if login.public {
        Some(CopilotDeploymentChoice::Public)
    } else {
        login
            .enterprise_domain
            .as_ref()
            .map(|domain| CopilotDeploymentChoice::Enterprise(domain.clone()))
    };

    let needs_key = identity == ProviderIdentity::AnthropicApiKey && !login.use_env;
    let api_key = if !needs_key {
        None
    } else if login.api_key_stdin {
        Some(console.read_piped_api_key().map_err(|e| from_prompt(e, KEY_SOURCE_REQUIRED))?)
    } else if console.is_interactive() {
        Some(
            console
                .read_masked("Anthropic API key (not shown): ")
                .map_err(|e| from_prompt(e, KEY_SOURCE_REQUIRED))?,
        )
    } else {
        return Err(PlanError::Usage(KEY_SOURCE_REQUIRED.to_owned()));
    };

    Ok(LoginPlan { identity, deployment, api_key, use_env: login.use_env })
}

/// The three accounts, offered in the shared fixed order — and only to someone
/// who can answer. A non-interactive invocation is told to name one instead of
/// having one chosen for it.
fn pick_provider(console: &dyn AuthConsole) -> Result<ProviderIdentity, PlanError> {
    if !console.is_interactive() {
        return Err(PlanError::Usage(PROVIDER_REQUIRED.to_owned()));
    }
    let mut menu = String::from("Which account do you want to connect?\n");
    for (index, identity) in ProviderIdentity::ALL.iter().enumerate() {
        menu.push_str(&format!("  {}) {}\n", index + 1, identity.label()));
    }
    console.notice(&menu);

    let answer = console
        .read_line("Provider [1-3, or a name]: ")
        .map_err(|e| from_prompt(e, PROVIDER_REQUIRED))?;
    if answer.trim().is_empty() {
        return Err(PlanError::Cancelled);
    }
    if let Ok(index) = answer.trim().parse::<usize>() {
        if let Some(identity) = index.checked_sub(1).and_then(|i| ProviderIdentity::ALL.get(i)) {
            return Ok(*identity);
        }
    }
    // The one alias table, so a name accepted here means the same thing to the
    // engine. Anything else is refused rather than substituted.
    ProviderIdentity::parse(&answer).ok_or_else(|| {
        PlanError::Usage("that is not one of this product's providers".to_owned())
    })
}

// ── Reporting ────────────────────────────────────────────────────────────────

/// The full status report.
///
/// `copilot` is the deployment the service actually resolved. Pass `None`
/// when it could not be resolved: inventing "public github.com" for an
/// enterprise user is exactly the claim this report must not make.
pub fn render_status(status: &AuthStatus, copilot: Option<&CopilotContext>) -> String {
    let mut lines = vec!["Stored authentication for this profile:".to_owned(), String::new()];

    for provider in &status.providers {
        let state = match &provider.state {
            StoredState::Absent => "not signed in".to_owned(),
            StoredState::Present(summary) => {
                let mut text = "signed in".to_owned();
                if let Some(account) = summary.account_label() {
                    // Provider-supplied: sanitized, like everything that is
                    // not this command's own words.
                    text.push_str(&format!(" as {}", safe(account)));
                }
                if summary.is_expired() == Some(true) {
                    text.push_str(" (the access token has expired; it is refreshed on use)");
                }
                text
            }
            // Never "not signed in": a slot that cannot be read may still hold
            // the credential this machine is signed in with.
            StoredState::Unreadable(failure) => {
                format!("a stored credential could not be read: {failure}")
            }
        };
        lines.push(format!("  {:<24} {state}", provider.identity.label()));
    }

    lines.push(String::new());
    lines.push(render_selection(&status.selection));

    match &status.saved_default {
        Some(saved) => lines.push(format!("  Saved provider choice: {}", safe(saved))),
        None => lines.push("  Saved provider choice: none".to_owned()),
    }
    if let Some(domain) = &status.github_enterprise_domain {
        lines.push(format!("  Saved GitHub Copilot tenant: {}", safe(domain)));
    }
    if let Some(context) = copilot {
        lines.push(format!("  {}", render_deployment(context)));
    }
    lines.push(format!(
        "  ANTHROPIC_API_KEY: {}",
        if status.environment_api_key {
            "set in this environment (availability only — it is not necessarily the account in use)"
        } else {
            "not set in this environment"
        }
    ));
    lines.push(format!("  {}", render_anthropic_endpoint(&status.anthropic_endpoint)));

    if let Some(failure) = &status.settings_error {
        lines.push(String::new());
        lines.push(format!(
            "  The saved provider settings could not be read: {failure}. The stored credentials \
             above are still accurate."
        ));
    }
    if let Some(failure) = &status.provider_context_error {
        lines.push(String::new());
        lines.push(format!(
            "  A provider's saved deployment could not be resolved: {failure}. That provider \
             cannot be signed in to, and its credential cannot be refreshed, until the \
             configuration is corrected — neither may fall back to the public defaults."
        ));
    }

    lines.push(String::new());
    lines.push(
        "This is what is stored on this machine, read without refreshing anything. Engines \
         already running in other processes keep their own connection, nothing here reconnects \
         them, and no credential was revoked at any provider."
            .to_owned(),
    );
    lines.join("\n")
}

fn render_selection(selection: &Result<Selection, SelectionError>) -> String {
    match selection {
        Ok(selected) => {
            let why = match selected.source {
                SelectionSource::Explicit => "named for this invocation",
                SelectionSource::SavedDefault => "the saved provider choice",
                SelectionSource::SoleStored => "the only credential stored",
                SelectionSource::AmbientEnvKey => "an exported ANTHROPIC_API_KEY, nothing stored",
            };
            // Where the credential comes from is part of the verdict: a saved
            // choice backed by an exported variable is not a stored account,
            // and a shell without that variable is not signed in.
            let origin = match selected.origin {
                CredentialOrigin::Stored => "",
                CredentialOrigin::Environment => {
                    "; authenticating with the exported ANTHROPIC_API_KEY — no key is stored for it"
                }
            };
            format!("  Selected provider: {} ({why}{origin})", selected.identity.engine_id())
        }
        Err(SelectionError::NeedsLogin { identity, .. }) => format!(
            "  Selected provider: {} — chosen, but it has no usable credential; sign in to it \
             rather than to something else",
            identity.engine_id()
        ),
        Err(SelectionError::UnknownProvider { .. }) => {
            "  Selected provider: the saved choice names a provider this build does not know"
                .to_owned()
        }
        Err(SelectionError::Ambiguous { stored }) => {
            let names: Vec<&str> = stored.iter().map(|identity| identity.label()).collect();
            format!(
                "  Selected provider: ambiguous — credentials are stored for {}; sign out, or \
                 choose one explicitly",
                names.join(" and ")
            )
        }
        Err(SelectionError::NoCredentials) => {
            "  Selected provider: none — nothing is stored and nothing is configured".to_owned()
        }
        // Deliberately not "signed out": the credential may be perfectly
        // valid and simply unreadable from here.
        Err(SelectionError::Unavailable { failure }) => format!(
            "  Selected provider: could not be determined ({failure}); do not sign in again until \
             this is understood, or a recoverable credential may be overwritten"
        ),
    }
}

/// Where Anthropic API-key requests go from this process — for `auth status`,
/// which must be able to *diagnose* a broken override without refreshing
/// anything or contacting anyone.
///
/// Host only, never the configured URL: a base URL may carry a path, and a
/// path may carry a tenant id or a token-shaped segment.
pub fn render_anthropic_endpoint(
    endpoint: &Result<AnthropicEndpoint, EndpointError>,
) -> String {
    match endpoint {
        Ok(endpoint) if endpoint.is_default() => format!(
            "Anthropic API-key endpoint: {} (the default; {ANTHROPIC_BASE_URL_ENV} is not set)",
            safe(endpoint.host())
        ),
        Ok(endpoint) => format!(
            "Anthropic API-key endpoint: {} (from {ANTHROPIC_BASE_URL_ENV} in this environment; \
             it applies to this process only, and to Anthropic API keys only — a Claude \
             subscription and GitHub Copilot are unaffected)",
            safe(endpoint.host())
        ),
        Err(reason) => format!(
            "Anthropic API-key endpoint: {ANTHROPIC_BASE_URL_ENV} is set to a value that was \
             refused ({reason}). Signing in with an API key, and any engine started with this \
             variable, will fail closed rather than fall back to Anthropic's own host. Correct \
             the variable, or unset it."
        ),
    }
}

/// The deployment in force, named truthfully, from the service's own resolved
/// configuration rather than a second look at the environment.
pub fn render_deployment(context: &CopilotContext) -> String {
    render_deployment_of(&context.deployment, &context.config.api_base_url, &[])
}

/// The same wording for a deployment that has been *resolved but not yet
/// used*: what a sign-in with a given choice would contact, disclosed before
/// the device code is requested.
///
/// `overrides` names the environment variables that redirected an individual
/// endpoint. They are named — never their values — because "public github.com"
/// on a machine where `GH_COPILOT_TOKEN_URL` points elsewhere is not the whole
/// truth about where a credential is going.
pub fn render_deployment_of(
    deployment: &CopilotDeployment,
    api_base_url: &str,
    overrides: &[&'static str],
) -> String {
    let where_it_points = match deployment {
        CopilotDeployment::Public => "public github.com".to_owned(),
        CopilotDeployment::Enterprise { domain } => {
            format!("the GitHub Enterprise deployment {}", safe(domain))
        }
        CopilotDeployment::Custom { auth_host } => format!(
            "a custom deployment at {} (endpoint overrides are in force in this environment)",
            safe(auth_host)
        ),
    };
    let mut line = match host_of(api_base_url) {
        Some(host) => {
            format!("GitHub Copilot deployment: {where_it_points}; inference endpoint {host}")
        }
        None => format!("GitHub Copilot deployment: {where_it_points}"),
    };
    if !overrides.is_empty() {
        line.push_str(&format!(
            ". Redirected in this environment by {}",
            overrides.join(", ")
        ));
    }
    line
}

/// Where an OAuth sign-in will send the browser, by **host**.
///
/// Never the URL: an authorize URL carries the CSRF state and the loopback
/// redirect the provider will call back on, and neither belongs anywhere but
/// the live challenge surface.
pub fn render_authorization_host(label: &str, authorize_url: &str) -> String {
    match host_of(authorize_url) {
        Some(host) => format!("{label} sign-in host: {host}"),
        None => format!(
            "{label} sign-in host: the configured authorization endpoint could not be read as a \
             URL; nothing has been sent."
        ),
    }
}

/// Only the host of a configured URL is ever shown: a full URL could carry a
/// query string, and nothing here needs one.
fn host_of(url: &str) -> Option<String> {
    url::Url::parse(url).ok().and_then(|url| url.host_str().map(safe))
}

/// What a login is about to remove, before it removes it.
///
/// Coda keeps one saved account, so signing in to a second one deletes the
/// first. That is a decision, and it is disclosed by name before the commit
/// rather than reported afterwards.
pub fn render_replacement(identity: ProviderIdentity, replaces: &[ProviderIdentity]) -> String {
    if replaces.is_empty() {
        return String::new();
    }
    let names: Vec<&str> = replaces.iter().map(|identity| identity.label()).collect();
    format!(
        "Completing this sign-in to {} will remove the stored credential for {} on this machine. \
         Coda keeps one saved account at a time; nothing is revoked at the provider, and other \
         processes are not signed out.",
        identity.label(),
        names.join(" and "),
    )
}

/// What an environment login is about to remove, before it removes it.
///
/// Selecting the exported key also deletes the *stored* console key, if there
/// is one: after this, the only thing that signs this profile in is the
/// variable. That is a bigger decision than "switching accounts", so it is
/// disclosed as its own sentence rather than folded into the usual wording.
pub fn render_environment_replacement(replaces: &[ProviderIdentity]) -> String {
    if replaces.is_empty() {
        return String::new();
    }
    let names: Vec<&str> = replaces.iter().map(|identity| identity.label()).collect();
    format!(
        "Completing this sign-in will use the ANTHROPIC_API_KEY exported in this environment, and \
         will store no key. The stored credential for {} will be removed from this machine; \
         nothing is revoked at the provider, and other processes are not signed out. A shell \
         without that variable will then have no credential to fall back on.",
        names.join(" and "),
    )
}

/// The commit outcome, in the service's own safe wording.
pub fn render_commit(outcome: &CommitOutcome) -> String {
    outcome.to_string()
}

/// Why a login could not be prepared.
///
/// The service's own wording for an unverifiable credential offers a choice to
/// "continue without checking". This command does not have that option — it
/// will not store a credential it could not check — so it says what it will
/// actually do instead of describing a door that is not there.
pub fn render_prepare_failure(failure: &PrepareFailure) -> String {
    match failure {
        PrepareFailure::ValidationUnavailable { reason, displaces_a_working_account } => {
            let kept = if *displaces_a_working_account {
                "The account you were signed in to was left connected."
            } else {
                "Nothing was changed."
            };
            format!(
                "The credential could not be checked ({reason}), so it was not saved. {kept} \
                 Retry when the provider can be reached; this command will not store a \
                 credential it could not check."
            )
        }
        other => other.to_string(),
    }
}

/// What a logout did — and what it deliberately did not do.
pub fn render_logout(report: &LogoutReport) -> String {
    report.to_string()
}

/// A logout that failed, including one whose rollback also failed.
pub fn render_logout_failure(failure: &LogoutFailure) -> String {
    failure.to_string()
}

/// Only a committed login is a success. An interrupted one is not a failure
/// either, but it is certainly not a zero.
pub fn exit_code_for_commit(outcome: &CommitOutcome) -> i32 {
    if outcome.engine_may_start() {
        EXIT_OK
    } else {
        EXIT_FAILED
    }
}

/// A cancelled preparation is the user's decision, not an error.
pub fn exit_code_for_prepare(failure: &PrepareFailure) -> i32 {
    match failure {
        PrepareFailure::Cancelled => EXIT_CANCELLED,
        _ => EXIT_FAILED,
    }
}

pub fn exit_code_for_logout(_failure: &LogoutFailure) -> i32 {
    EXIT_FAILED
}

// ── The login UI ─────────────────────────────────────────────────────────────

/// The host side of an interactive login.
///
/// Authorization URLs and device codes are printed here, on the explicit CLI
/// surface, and nowhere else — this process never opens the diagnostic log.
struct CliLoginUi<'a> {
    console: &'a dyn AuthConsole,
    /// Whether a browser may be launched at all.
    open_browser: bool,
    /// Collected before the runtime existed; taken exactly once.
    api_key: Mutex<Option<Secret<String>>>,
    /// Launcher processes started for this login, to be reaped when it ends.
    launchers: Mutex<Vec<tokio::process::Child>>,
}

impl<'a> CliLoginUi<'a> {
    fn new(
        console: &'a dyn AuthConsole,
        api_key: Option<Secret<String>>,
        open_browser: bool,
    ) -> Self {
        Self {
            console,
            open_browser,
            api_key: Mutex::new(api_key),
            launchers: Mutex::new(Vec::new()),
        }
    }

    /// Try to open a URL that has already been shown.
    ///
    /// A launcher that starts is *not* evidence that a browser opened, let
    /// alone that anyone authorized anything, which is why the caller prints
    /// the address first and unconditionally.
    fn open(&self, url: &str) {
        if !self.open_browser {
            return;
        }
        match crate::browser::launch_authorization_url(url) {
            Ok(child) => self.launchers.lock().expect("launchers").push(child),
            Err(error) => self.console.notice(&format!(
                "  (a browser could not be started here: {error} — open the address above \
                 yourself)"
            )),
        }
    }

    /// Reap the launchers this login started.
    ///
    /// Called once the login future has been dropped, so the loopback listener
    /// and the device poller are already closed. All launchers share one grace
    /// period rather than waiting for each in turn, and one still running when
    /// it expires is left alone: on some desktops it *is* the browser, and
    /// killing it would close the window the user is typing into.
    async fn release(&self) {
        let launchers = {
            let mut held = self.launchers.lock().expect("launchers");
            std::mem::take(&mut *held)
        };
        if launchers.is_empty() {
            return;
        }
        let reaped = tokio::time::timeout(Duration::from_secs(2), async {
            let mut failures = 0usize;
            for mut child in launchers {
                if child.wait().await.is_ok_and(|status| !status.success()) {
                    failures += 1;
                }
            }
            failures
        })
        .await;
        if reaped.is_ok_and(|failures| failures > 0) {
            self.console.notice(
                "  (the browser launcher exited without opening a page — open the address above \
                 yourself)",
            );
        }
    }
}

#[async_trait]
impl LoginUi for CliLoginUi<'_> {
    async fn api_key(&self) -> Result<Secret<String>, AuthError> {
        self.api_key
            .lock()
            .expect("api key")
            .take()
            .ok_or_else(|| AuthError::LoginCancelled("no API key was collected".into()))
    }

    async fn authorization_url(&self, url: &str) -> Result<(), AuthError> {
        self.console
            .notice(&format!("Open this address to finish signing in:\n  {}", safe(url)));
        self.open(url);
        Ok(())
    }

    async fn device_code(&self, prompt: DeviceCodePrompt) -> Result<(), AuthError> {
        // Both of these come off the wire: shown, but never trusted to be
        // printable.
        self.console.notice(&format!(
            "Enter the code {} at:\n  {}",
            safe(&prompt.user_code),
            safe(&prompt.verification_uri),
        ));
        let target =
            prompt.verification_uri_complete.as_deref().unwrap_or(&prompt.verification_uri);
        self.open(target);
        Ok(())
    }

    async fn copilot_deployment(
        &self,
        _saved_domain: Option<&str>,
    ) -> Result<Option<CopilotDeploymentChoice>, AuthError> {
        // A deployment named on the command line already travelled in the
        // request. With none given, the configured default stands — this
        // command never invents a tenant.
        Ok(None)
    }
}

// ── The runner ───────────────────────────────────────────────────────────────

/// Run an `auth` command and return its exit code.
pub fn run(args: AuthArgs) -> i32 {
    run_with(args, &SystemConsole)
}

/// Whether a browser may be launched, given the raw opt-out value.
///
/// A pure predicate so the rule is the same one production reads and a test
/// can exercise: only a *non-empty* value suppresses the browser, so
/// `CODA_AUTH_NO_BROWSER=` (a common way to clear a variable in a shell
/// script) does not silently disable it.
pub fn browser_allowed(opt_out: Option<&std::ffi::OsStr>) -> bool {
    match opt_out {
        Some(value) => value.is_empty(),
        None => true,
    }
}

/// [`run`], against an explicit console.
pub fn run_with(args: AuthArgs, console: &dyn AuthConsole) -> i32 {
    let opt_out = std::env::var_os(NO_BROWSER_ENV);
    run_with_options(args, console, &InterruptSignal, browser_allowed(opt_out.as_deref()))
}

/// [`run`], with the console, the cancellation source and the browser policy
/// all supplied — the entry point a test or an embedding host uses.
pub fn run_with_options(
    args: AuthArgs,
    console: &dyn AuthConsole,
    cancel: &dyn CancelSignal,
    open_browser: bool,
) -> i32 {
    // Everything interactive happens here, on this thread, before a runtime
    // exists: a person at a prompt can never hold a worker or stall teardown.
    let plan = match plan(&args, console) {
        Ok(plan) => plan,
        Err(error) => {
            console.notice(&error.to_string());
            return error.exit_code();
        }
    };

    let runtime = match tokio::runtime::Builder::new_multi_thread().enable_all().build() {
        Ok(runtime) => runtime,
        Err(_) => {
            console.notice("the async runtime could not be started");
            return EXIT_FAILED;
        }
    };
    let code = runtime.block_on(execute(plan, console, cancel, open_browser));
    // A browser launcher may outlive the command; it is not ours to wait for.
    runtime.shutdown_timeout(Duration::from_millis(250));
    code
}

async fn execute(
    plan: AuthPlan,
    console: &dyn AuthConsole,
    cancel: &dyn CancelSignal,
    open_browser: bool,
) -> i32 {
    let settings_file = SettingsFile::for_user();
    // Read once, here, so a settings fault is named as a settings fault. The
    // service classifies it into `AuthFailure::Store`, which renders as
    // "credential store error" — true of the port, and thoroughly misleading
    // about which file the user has to fix.
    let settings_fault = settings_file.read().err().map(|error| error.safe_summary());

    if let Some(fault) = &settings_fault {
        if !matches!(plan, AuthPlan::Status) {
            console.notice(&format!(
                "the saved provider settings could not be read: {}. Edit that file to valid JSON, \
                 or delete it to start from defaults, then run the command again. Nothing was \
                 changed: signing out while the saved provider choice cannot be read would leave \
                 the credentials and that choice disagreeing.",
                // The path comes from CODA_HOME, which is an environment
                // variable like any other.
                sanitize(fault, 400)
            ));
            return EXIT_FAILED;
        }
    }

    let profile = Profile::from_env();
    let storage = match open_profile_storage(&profile) {
        Ok(storage) => storage,
        Err(error) => {
            console.notice(&format!(
                "the credential store could not be opened: {}",
                AuthFailure::classify(&error)
            ));
            return EXIT_FAILED;
        }
    };
    let settings: Arc<dyn AuthSettingsPort> = Arc::new(settings_file);

    match plan {
        // Reporting and signing out must work even when a saved deployment
        // cannot be resolved — that is exactly when a user needs them.
        AuthPlan::Status => {
            let service = AuthService::from_storage_degraded(&storage, settings).await;
            run_status(&service, console, settings_fault.as_deref()).await
        }
        AuthPlan::Logout(identity) => {
            let service = AuthService::from_storage_degraded(&storage, settings).await;
            run_logout(&service, identity, console).await
        }
        // Signing in must not: a device authorization sent to guessed
        // endpoints is a token handed to the wrong host.
        AuthPlan::Login(login) => match AuthService::from_storage(&storage, settings).await {
            Ok(service) => run_login(&service, login, console, cancel, open_browser).await,
            Err(error) => {
                console.notice(&format!(
                    "signing in is not possible until the saved provider configuration is \
                     corrected: {}",
                    AuthFailure::classify(&error)
                ));
                EXIT_FAILED
            }
        },
    }
}

async fn run_status(
    service: &AuthService,
    console: &dyn AuthConsole,
    settings_fault: Option<&str>,
) -> i32 {
    let status = match service.status().await {
        Ok(status) => status,
        Err(failure) => {
            console.notice(&format!("the authentication status could not be read: {failure}"));
            return EXIT_FAILED;
        }
    };
    // A service whose provider context is broken holds a placeholder context;
    // reporting it would name a deployment nobody configured.
    let context = status.provider_context_error.is_none().then(|| service.copilot_context());
    let mut report = render_status(&status, context.as_deref());
    if let Some(fault) = settings_fault {
        report.push_str(&format!(
            "\n\nThe settings file itself: {}. Edit it to valid JSON, or delete it to start from \
             defaults.",
            sanitize(fault, 400)
        ));
    }
    console.report(&report);
    EXIT_OK
}

async fn run_logout(
    service: &AuthService,
    identity: Option<ProviderIdentity>,
    console: &dyn AuthConsole,
) -> i32 {
    match service.logout(identity).await {
        Ok(report) => {
            console.report(&render_logout(&report));
            EXIT_OK
        }
        Err(failure) => {
            console.notice(&render_logout_failure(&failure));
            exit_code_for_logout(&failure)
        }
    }
}

async fn run_login(
    service: &AuthService,
    plan: LoginPlan,
    console: &dyn AuthConsole,
    cancel: &dyn CancelSignal,
    open_browser: bool,
) -> i32 {
    let identity = plan.identity;
    // Resolved and disclosed *before* the key is sent anywhere: the user is
    // told which host their key is about to go to while it is still possible
    // for them to stop, and a refused override stops the login here — with
    // nothing sent, nothing probed and nothing changed. (The key itself is
    // already in hand: `plan_login` reads stdin or the prompt before this
    // runs. What this precedes is the network, and the commit.)
    let endpoint = match service.anthropic_endpoint() {
        Ok(endpoint) => endpoint,
        Err(reason) => {
            if identity == ProviderIdentity::AnthropicApiKey {
                console.notice(&render_prepare_failure(&PrepareFailure::EndpointRejected {
                    reason,
                }));
                return EXIT_FAILED;
            }
            // Another provider's login does not use this endpoint at all, and
            // must not be blocked by it. It is still reported, because the
            // variable is set and the user may not expect it to be ignored.
            console.notice(&format!(
                "{ANTHROPIC_BASE_URL_ENV} is set to a value that was refused ({reason}). It \
                 applies to Anthropic API keys only, so this sign-in is unaffected."
            ));
            // No endpoint to carry: this login never builds an Anthropic
            // API-key client.
            return run_login_for(service, plan, console, cancel, open_browser, None).await;
        }
    };
    if identity == ProviderIdentity::AnthropicApiKey && !endpoint.is_default() {
        console.notice(&endpoint.describe());
    }
    run_login_for(service, plan, console, cancel, open_browser, Some(endpoint)).await
}

async fn run_login_for(
    service: &AuthService,
    plan: LoginPlan,
    console: &dyn AuthConsole,
    cancel: &dyn CancelSignal,
    open_browser: bool,
    endpoint: Option<AnthropicEndpoint>,
) -> i32 {
    let identity = plan.identity;
    let request = match identity {
        ProviderIdentity::ClaudeAi => LoginRequest::claude_ai(),
        ProviderIdentity::GithubCopilot => LoginRequest::copilot(plan.deployment),
        // An environment login is validated like any other: it must prove
        // itself before it is allowed to displace a saved account.
        ProviderIdentity::AnthropicApiKey => LoginRequest::api_key(if plan.use_env {
            ApiKeySource::Environment
        } else {
            ApiKeySource::Prompt
        }),
    };
    let ui = CliLoginUi::new(console, plan.api_key, open_browser);

    // Cancellation before the commit is free, and it must actually free
    // things: when the user wins, the preparation future — which owns the
    // loopback listener and the device poller — is dropped at the end of this
    // expression, and the launcher processes are reaped right after.
    let prepared = tokio::select! {
        biased;
        _ = cancel.cancelled() => None,
        result = service.prepare_login(request, &ui) => Some(result),
    };
    ui.release().await;

    let Some(prepared) = prepared else {
        console.notice("Cancelled; nothing was changed and nothing was stored.");
        return EXIT_CANCELLED;
    };
    let prepared: PreparedLogin = match prepared {
        Ok(prepared) => prepared,
        Err(failure) => {
            console.notice(&render_prepare_failure(&failure));
            return exit_code_for_prepare(&failure);
        }
    };

    let replacement = if prepared.uses_environment_key() {
        render_environment_replacement(prepared.replaces())
    } else {
        render_replacement(prepared.identity(), prepared.replaces())
    };
    if !replacement.is_empty() {
        console.notice(&replacement);
    }
    // Read before the commit consumes the prepared login: what the user is
    // told afterwards, and what the connection is checked with, both depend on
    // whether anything was saved at all.
    let environment_only = prepared.uses_environment_key();

    // From here the transaction owns the outcome. It is started on its own
    // task the moment `commit_login` is called and is never cancelled: a
    // dropped commit could leave a half-written profile while this process
    // claimed nothing had changed.
    let outcome = service.commit_login(prepared).await;
    service.await_pending_commit().await;
    console.report(&render_commit(&outcome));

    if !outcome.engine_may_start() {
        return exit_code_for_commit(&outcome);
    }
    if environment_only {
        console.report(ENVIRONMENT_SELECTION_SAVED);
    }
    if identity == ProviderIdentity::GithubCopilot {
        console.report(&render_deployment(&service.copilot_context()));
    }
    // Said again after the commit, next to what was saved: what the profile
    // now points at is part of what was just decided, and the check that
    // follows goes to exactly this host.
    if identity == ProviderIdentity::AnthropicApiKey {
        if let Some(endpoint) = endpoint.as_ref().filter(|endpoint| !endpoint.is_default()) {
            console.report(&endpoint.describe());
        }
    }
    verify_connection(service, identity, environment_only, endpoint.as_ref(), console, cancel).await
}

/// What an environment login actually persisted — and what it did not.
///
/// Said plainly, because the difference decides what happens on the next
/// machine, in the next shell, and in CI: there is no saved key to fall back
/// on.
const ENVIRONMENT_SELECTION_SAVED: &str =
    "Anthropic was saved as the provider for this profile; no API key was stored. Coda will use \
     the ANTHROPIC_API_KEY exported in the environment it runs in, so a shell or a machine \
     without that variable is not signed in — export it there, or run `coda auth login api-key` \
     to save a key instead.";

/// Check the connection the way the service defines it: one uncached model
/// listing, never a completion, so verification costs nothing and writes
/// nothing.
///
/// Three outcomes, three different statements:
///
/// * the provider answered — verified;
/// * the provider refused the credential — signed in, but it does not work;
/// * anything else, including a store this machine could not read and a user
///   who stopped waiting — **signed in, unverified**. The credential is
///   already saved at this point, so none of these may be reported as a login
///   that did not happen.
async fn verify_connection(
    service: &AuthService,
    identity: ProviderIdentity,
    environment_only: bool,
    endpoint: Option<&AnthropicEndpoint>,
    console: &dyn AuthConsole,
    cancel: &dyn CancelSignal,
) -> i32 {
    let Some((client, source)) = probe_client(service, identity, environment_only, endpoint) else {
        console.report(if environment_only {
            "ANTHROPIC_API_KEY is no longer readable from this process, so the connection could \
             not be checked; run `auth status` to see what this profile is set to."
        } else {
            "The stored credential could not be checked from here; run `auth status` to see what \
             is stored."
        });
        return EXIT_OK;
    };

    // Read-only and potentially slow, so it is interruptible — unlike the
    // commit that has already happened.
    let report = tokio::select! {
        biased;
        _ = cancel.cancelled() => {
            console.report(
                "The credential is saved and the sign-in is complete. The connection check was \
                 cancelled, so the connection has not been verified — run `auth status`, or \
                 start a session, when you want to confirm it.",
            );
            return EXIT_OK;
        }
        report = service.verify_with_source(client.as_ref(), source.as_ref()) => report,
    };

    console.report(&report.to_string());
    if report.is_rejected() {
        EXIT_FAILED
    } else {
        EXIT_OK
    }
}

/// Check a committed login the way every host must: one uncached model
/// listing, through a credential source built for this probe alone.
///
/// `None` means no client could be built at all — this machine could not
/// produce a credential to check with, which is emphatically *not* a
/// rejection. Shared rather than reimplemented so the terminal front-end and
/// this command cannot drift on what "verified" means, on which host the
/// probe is sent to, or on the
/// [`CredentialSource::last_failure_was_local`] rule that keeps a local store
/// failure from being reported as a provider refusal.
pub async fn verify_login(
    service: &AuthService,
    identity: ProviderIdentity,
    environment_only: bool,
    endpoint: Option<&AnthropicEndpoint>,
) -> Option<coda_auth::service::VerificationReport> {
    let (client, source) = probe_client(service, identity, environment_only, endpoint)?;
    Some(service.verify_with_source(client.as_ref(), source.as_ref()).await)
}

/// The client to probe with, and the source it authenticates through.
///
/// The source is built **for this probe only**. Its
/// [`CredentialSource::last_failure_was_local`] answer describes the last
/// attempt it made, so sharing one with anything else issuing requests would
/// let another request's outcome decide how this credential is reported. A
/// host adding a "test this connection" action must do the same.
///
/// `endpoint` is the resolved Anthropic API-key endpoint. It is applied to the
/// two console-key clients and to neither of the others: a Claude.ai
/// subscription token and a Copilot token belong to different hosts, and
/// sending one of them to a gateway configured for an API key would hand a
/// credential to a server that has no business seeing it.
pub fn probe_client(
    service: &AuthService,
    identity: ProviderIdentity,
    environment_only: bool,
    endpoint: Option<&AnthropicEndpoint>,
) -> Option<(Arc<dyn LlmClient>, Arc<dyn CredentialSource>)> {
    let base_url = endpoint.map(|endpoint| endpoint.base_url().to_owned());
    // A login that stored nothing has nothing in the store to check: probing
    // through the manager would report "no credential on this machine" about a
    // profile that is working exactly as asked. The key is checked where it
    // actually lives, through a source built for this probe alone.
    if environment_only && identity == ProviderIdentity::AnthropicApiKey {
        let source = service.environment_api_key_source()?;
        let mut config = coda_llm::anthropic::AnthropicConfig::api_key("")
            .with_identity(identity.engine_id())
            .with_credential_source(Arc::clone(&source));
        if let Some(base_url) = &base_url {
            config = config.with_base_url(base_url.clone());
        }
        return coda_llm::anthropic::AnthropicClient::new(config)
            .ok()
            .map(|client| (Arc::new(client) as Arc<dyn LlmClient>, source));
    }
    match identity {
        ProviderIdentity::GithubCopilot => {
            // Endpoints and credential source from one snapshot: a client
            // whose base URL belongs to a superseded tenant must fail closed
            // rather than carry the new account's token.
            let connection = service.copilot_connection();
            let resolved = &connection.context.config;
            let config = coda_llm::CopilotConfig::with_token("")
                .with_credential_source(Arc::clone(&connection.source))
                .with_base_url(resolved.api_base_url.clone())
                .with_header("editor-version", resolved.editor_version.clone())
                .with_header("editor-plugin-version", resolved.editor_plugin_version.clone())
                .with_header("copilot-integration-id", resolved.integration_id.clone())
                .with_header("user-agent", resolved.user_agent.clone())
                .with_header("x-github-api-version", "2026-06-01");
            coda_llm::CopilotClient::new(config)
                .ok()
                .map(|client| (Arc::new(client) as Arc<dyn LlmClient>, connection.source))
        }
        ProviderIdentity::ClaudeAi => {
            let source = Arc::new(coda_auth::CredentialManagerSource::new(
                service.manager(),
                identity.stored_id(),
            )) as Arc<dyn CredentialSource>;
            let mut config = coda_llm::anthropic::AnthropicConfig::api_key("")
                .with_identity(identity.engine_id())
                .with_credential_source(Arc::clone(&source));
            config.extra_headers.push((
                "anthropic-beta".into(),
                coda_auth::provider::claude_ai::OAUTH_BETA_HEADER.into(),
            ));
            coda_llm::anthropic::AnthropicClient::new(config)
                .ok()
                .map(|client| (Arc::new(client) as Arc<dyn LlmClient>, source))
        }
        ProviderIdentity::AnthropicApiKey => {
            let source = Arc::new(coda_auth::CredentialManagerSource::new(
                service.manager(),
                identity.stored_id(),
            )) as Arc<dyn CredentialSource>;
            let mut config = coda_llm::anthropic::AnthropicConfig::api_key("")
                .with_identity(identity.engine_id())
                .with_credential_source(Arc::clone(&source));
            if let Some(base_url) = &base_url {
                config = config.with_base_url(base_url.clone());
            }
            coda_llm::anthropic::AnthropicClient::new(config)
                .ok()
                .map(|client| (Arc::new(client) as Arc<dyn LlmClient>, source))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_auth::coordination::LocalCoordinator;
    use coda_auth::service::{AuthEnvironment, InMemoryAuthSettings, MapEnvironment};
    use coda_auth::store::{open_profile_storage, Profile};

    /// A console that records everything and answers nothing.
    #[derive(Default)]
    struct SilentConsole {
        written: Mutex<Vec<String>>,
    }

    impl SilentConsole {
        fn text(&self) -> String {
            self.written.lock().unwrap().join("\n")
        }
    }

    impl AuthConsole for SilentConsole {
        fn is_interactive(&self) -> bool {
            false
        }
        fn report(&self, text: &str) {
            self.written.lock().unwrap().push(text.to_owned());
        }
        fn notice(&self, text: &str) {
            self.written.lock().unwrap().push(text.to_owned());
        }
        fn read_line(&self, _: &str) -> Result<String, PromptError> {
            Err(PromptError::NotInteractive)
        }
        fn read_masked(&self, _: &str) -> Result<Secret<String>, PromptError> {
            Err(PromptError::NotInteractive)
        }
        fn read_piped_api_key(&self) -> Result<Secret<String>, PromptError> {
            Err(PromptError::NotInteractive)
        }
    }

    /// Cancellation that has already happened.
    struct AlreadyCancelled;

    #[async_trait]
    impl CancelSignal for AlreadyCancelled {
        async fn cancelled(&self) {}
    }

    /// A service over a throwaway profile: no `CODA_HOME`, no process
    /// environment, nothing shared with another test.
    async fn isolated_service(root: &std::path::Path) -> AuthService {
        let storage = open_profile_storage(&Profile::isolated(root)).expect("profile opens");
        AuthService::builder(
            Arc::clone(&storage.profile),
            Arc::new(LocalCoordinator::new()),
        )
        .with_settings(Arc::new(InMemoryAuthSettings::new()))
        .build()
        .await
        .expect("builds")
    }

    /// The same, with an explicit environment map — never the developer's.
    async fn isolated_service_with_env(root: &std::path::Path, env: &[(&str, &str)]) -> AuthService {
        let storage = open_profile_storage(&Profile::isolated(root)).expect("profile opens");
        AuthService::builder(
            Arc::clone(&storage.profile),
            Arc::new(LocalCoordinator::new()),
        )
        .with_settings(Arc::new(InMemoryAuthSettings::new()))
        .with_environment(Arc::new(MapEnvironment::new(env)) as Arc<dyn AuthEnvironment>)
        .build()
        .await
        .expect("builds")
    }

    // ── The environment selection, on this host's surface ────────────────────

    #[test]
    fn a_missing_environment_key_names_the_variable_without_claiming_rejection() {
        let failure = PrepareFailure::EnvironmentKeyMissing;
        let text = render_prepare_failure(&failure);
        assert!(text.contains("ANTHROPIC_API_KEY is not set"), "{text}");
        assert!(!text.contains("refused") && !text.contains("not valid"), "{text}");
        assert_eq!(exit_code_for_prepare(&failure), EXIT_FAILED);
    }

    /// A login that stored nothing has nothing in the store to check. The
    /// probe must read the key where it actually is, or a working profile is
    /// reported as having no credential on this machine.
    #[tokio::test]
    async fn an_environment_login_is_checked_against_the_variable_not_the_store() {
        let root = tempfile::tempdir().expect("temp profile");
        let service =
            isolated_service_with_env(root.path(), &[("ANTHROPIC_API_KEY", " \r\nsk-ant-exported \n")])
                .await;

        let (_client, source) =
            probe_client(&service, ProviderIdentity::AnthropicApiKey, true, None).expect("a probe");
        let headers = source
            .auth_headers()
            .await
            .expect("the exported key authenticates")
            .expect("headers");
        assert_eq!(headers, vec![("x-api-key".to_owned(), "sk-ant-exported".to_owned())]);
        assert!(!source.last_failure_was_local());

        // The stored-credential probe is exactly what must not be used here:
        // there is nothing stored, and it says so.
        let (_client, stored) =
            probe_client(&service, ProviderIdentity::AnthropicApiKey, false, None).expect("a probe");
        assert!(stored.auth_headers().await.is_err(), "nothing is stored, by design");
        assert!(stored.last_failure_was_local());
    }

    /// No variable, no probe: the command says the key could not be read
    /// rather than checking something else or claiming a stored credential.
    #[tokio::test]
    async fn an_environment_probe_needs_the_variable_to_exist() {
        let root = tempfile::tempdir().expect("temp profile");
        let service = isolated_service_with_env(root.path(), &[]).await;
        assert!(probe_client(&service, ProviderIdentity::AnthropicApiKey, true, None).is_none());
    }

    /// Two sources for two probes: `last_failure_was_local` describes the last
    /// attempt the source it is read from made, so they must not be shared.
    #[tokio::test]
    async fn every_environment_probe_gets_its_own_source() {
        let root = tempfile::tempdir().expect("temp profile");
        let service =
            isolated_service_with_env(root.path(), &[("ANTHROPIC_API_KEY", "sk-ant-exported")])
                .await;
        let first = service.environment_api_key_source().expect("a source");
        let second = service.environment_api_key_source().expect("a source");
        assert!(!Arc::ptr_eq(&first, &second));
    }

    /// The disclosure before an environment commit must name the stored key it
    /// deletes — including the console key of the very identity being selected
    /// — and must not suggest anything is being saved.
    #[test]
    fn the_environment_disclosure_names_the_key_it_removes() {
        let text = render_environment_replacement(&[
            ProviderIdentity::AnthropicApiKey,
            ProviderIdentity::GithubCopilot,
        ]);
        assert!(text.contains("Anthropic API key"), "{text}");
        assert!(text.contains("GitHub Copilot"), "{text}");
        assert!(text.contains("ANTHROPIC_API_KEY"), "{text}");
        assert!(text.contains("store no key"), "{text}");
        assert!(render_environment_replacement(&[]).is_empty(), "nothing to remove, nothing said");
    }

    /// What the user is told after the commit: a saved *choice*, not a saved
    /// key, and what that means in a shell without the variable.
    #[test]
    fn the_environment_confirmation_never_claims_a_key_was_stored() {
        let text = ENVIRONMENT_SELECTION_SAVED;
        assert!(text.contains("no API key was stored"), "{text}");
        assert!(text.contains("ANTHROPIC_API_KEY"), "{text}");
        assert!(text.contains("not signed in"), "{text}");
    }

    /// The four codes are a contract shared with every caller and with the
    /// engine binary; they are not free to drift.
    #[test]
    fn the_exit_codes_are_the_documented_ones() {
        assert_eq!((EXIT_OK, EXIT_FAILED, EXIT_USAGE, EXIT_CANCELLED), (0, 1, 2, 130));
    }

    #[test]
    fn a_prompt_failure_keeps_cancellation_usage_and_breakage_apart() {
        assert!(matches!(from_prompt(PromptError::Cancelled, "x"), PlanError::Cancelled));
        assert!(matches!(from_prompt(PromptError::NotInteractive, "x"), PlanError::Usage(_)));
        assert!(matches!(from_prompt(PromptError::EndOfInput, "x"), PlanError::Failed(_)));
        assert!(matches!(
            from_prompt(PromptError::Invalid(crate::secret_input::SecretInputError::Empty), "x"),
            PlanError::Usage(_)
        ));
    }

    /// A terminal that cannot hide input must produce an instruction that does
    /// not involve typing the key where it would be seen.
    #[test]
    fn an_unmaskable_terminal_is_told_how_to_pipe_the_key_instead() {
        let PlanError::Usage(message) = from_prompt(PromptError::EchoUnavailable, "x") else {
            panic!("usage");
        };
        assert!(message.contains("--api-key-stdin"), "{message}");
        let lowered = message.to_lowercase();
        assert!(lowered.contains("pipe"), "{message}");
        assert!(!lowered.contains("type the key"), "{message}");
    }

    #[test]
    fn only_the_host_of_a_configured_endpoint_is_ever_shown() {
        assert_eq!(
            host_of("https://api.githubcopilot.com/v1?token=private"),
            Some("api.githubcopilot.com".to_owned())
        );
        assert_eq!(host_of("not a url"), None);
    }

    /// Cancelling a login before the commit must free the flow and leave the
    /// profile exactly as it was — not "probably", but observably.
    #[tokio::test]
    async fn a_cancelled_login_writes_nothing_and_reports_the_cancellation_code() {
        let root = tempfile::tempdir().expect("temp profile");
        let service = isolated_service(root.path()).await;
        let console = SilentConsole::default();

        let code = run_login(
            &service,
            LoginPlan {
                identity: ProviderIdentity::ClaudeAi,
                deployment: None,
                api_key: None,
                use_env: false,
            },
            &console,
            &AlreadyCancelled,
            false,
        )
        .await;

        assert_eq!(code, EXIT_CANCELLED);
        let status = service.status().await.expect("status");
        assert!(status.stored().is_empty(), "a cancelled login must store nothing");
        assert!(console.text().to_lowercase().contains("nothing was stored"), "{}", console.text());
    }

    /// The commit has already happened by the time verification runs, so a
    /// user who stops waiting must be told the credential is saved — never
    /// that the sign-in did not happen.
    #[tokio::test]
    async fn cancelling_the_check_reports_a_saved_credential_rather_than_an_undone_login() {
        let root = tempfile::tempdir().expect("temp profile");
        let service = isolated_service(root.path()).await;
        let console = SilentConsole::default();

        let code = verify_connection(
            &service,
            ProviderIdentity::AnthropicApiKey,
            false,
            None,
            &console,
            &AlreadyCancelled,
        )
        .await;

        assert_eq!(code, EXIT_OK, "the sign-in itself succeeded");
        let text = console.text().to_lowercase();
        assert!(text.contains("saved"), "{}", console.text());
        assert!(text.contains("not been verified"), "{}", console.text());
        assert!(!text.contains("rejected"), "{}", console.text());
    }

    /// Nothing untrusted may carry an escape sequence, a carriage return or a
    /// newline into a report the user reads as this command's own words.
    #[test]
    fn untrusted_text_cannot_repaint_the_terminal_or_forge_a_line() {
        let hostile = "octocorp\u{1b}[2J\r\nSelected provider: github-copilot (verified)";
        let rendered = safe(hostile);
        assert!(!rendered.contains('\u{1b}'));
        assert!(!rendered.contains('\r'));
        assert!(!rendered.contains('\n'));
        assert_eq!(rendered.lines().count(), 1);
        // Replaced, never dropped: silently joining the two halves would hide
        // that anything was there.
        assert!(rendered.contains('\u{fffd}'));

        // Bidirectional overrides reorder a line without a control character.
        assert!(!safe("evil\u{202e}moc.elpmaxe").contains('\u{202e}'));
        // Bounded, so nothing can push the rest of the report out of view.
        assert!(safe(&"x".repeat(10_000)).chars().count() <= SANITIZE_LIMIT + 1);
        // An empty value is named rather than rendered as nothing at all.
        assert_eq!(safe(""), "(empty)");
    }

    /// Cancellation that fires once the flow has really started polling the
    /// provider — not before it, which is what makes this an *in-flight*
    /// cancellation rather than a select that never polled the login.
    struct CancelAfterPolls {
        polls: Arc<std::sync::atomic::AtomicUsize>,
        minimum: usize,
    }

    #[async_trait]
    impl CancelSignal for CancelAfterPolls {
        async fn cancelled(&self) {
            loop {
                if self.polls.load(std::sync::atomic::Ordering::SeqCst) >= self.minimum {
                    return;
                }
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
        }
    }

    /// A loopback GitHub device-code endpoint that never authorizes.
    ///
    /// Blocking `std` sockets on their own thread: this crate's tokio features
    /// deliberately do not include `net`, and the fixture needs nothing an
    /// async listener would give it.
    struct DeviceFixture {
        base: String,
        device_hits: Arc<std::sync::atomic::AtomicUsize>,
        token_hits: Arc<std::sync::atomic::AtomicUsize>,
    }

    impl DeviceFixture {
        fn start() -> Self {
            use std::io::{BufRead, BufReader, Write};
            use std::sync::atomic::Ordering;

            let listener =
                std::net::TcpListener::bind("127.0.0.1:0").expect("bind the fixture");
            let base = format!("http://{}", listener.local_addr().expect("addr"));
            let device_hits = Arc::new(std::sync::atomic::AtomicUsize::new(0));
            let token_hits = Arc::new(std::sync::atomic::AtomicUsize::new(0));
            let devices = Arc::clone(&device_hits);
            let tokens = Arc::clone(&token_hits);

            std::thread::spawn(move || {
                for stream in listener.incoming() {
                    let Ok(mut stream) = stream else { return };
                    let mut reader = BufReader::new(stream.try_clone().expect("clone"));
                    let mut request = String::new();
                    let mut length = 0usize;
                    loop {
                        let mut line = String::new();
                        if reader.read_line(&mut line).unwrap_or(0) == 0 {
                            break;
                        }
                        if let Some(value) = line
                            .to_ascii_lowercase()
                            .strip_prefix("content-length:")
                            .map(str::trim)
                        {
                            length = value.parse().unwrap_or(0);
                        }
                        if line == "\r\n" {
                            break;
                        }
                        request.push_str(&line);
                    }
                    if length > 0 {
                        let mut body = vec![0u8; length];
                        use std::io::Read;
                        let _ = reader.read_exact(&mut body);
                    }

                    let body = if request.contains("/device") {
                        devices.fetch_add(1, Ordering::SeqCst);
                        r#"{"device_code":"dc-fixture","user_code":"CODA-TEST","verification_uri":"https://example.invalid/device","expires_in":900,"interval":1}"#
                    } else {
                        tokens.fetch_add(1, Ordering::SeqCst);
                        // Never authorized: the login stays in flight until
                        // the user stops it.
                        r#"{"error":"authorization_pending"}"#
                    };
                    let response = format!(
                        "HTTP/1.1 200 OK\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{body}",
                        body.len()
                    );
                    let _ = stream.write_all(response.as_bytes());
                    let _ = stream.flush();
                }
            });

            Self { base, device_hits, token_hits }
        }

        fn polls(&self) -> usize {
            self.token_hits.load(std::sync::atomic::Ordering::SeqCst)
        }
    }

    /// The real thing: a device-code login that has started, shown its
    /// challenge and begun polling, then cancelled. It must exit 130, leave
    /// the profile and the settings untouched, and stop polling.
    #[tokio::test]
    async fn cancelling_a_login_in_flight_stops_the_poller_and_writes_nothing() {
        let fixture = DeviceFixture::start();
        let root = tempfile::tempdir().expect("temp profile");
        let storage = open_profile_storage(&Profile::isolated(root.path())).expect("profile");
        let settings = Arc::new(InMemoryAuthSettings::new());
        let environment = Arc::new(MapEnvironment::new(&[
            ("GH_COPILOT_DEVICE_CODE_URL", &format!("{}/device", fixture.base)),
            ("GH_COPILOT_TOKEN_URL", &format!("{}/token", fixture.base)),
            ("GH_COPILOT_API_BASE_URL", &fixture.base),
        ]));
        let service = AuthService::builder(
            Arc::clone(&storage.profile),
            Arc::new(LocalCoordinator::new()),
        )
        .with_settings(Arc::clone(&settings) as Arc<dyn AuthSettingsPort>)
        .with_environment(Arc::clone(&environment) as Arc<dyn AuthEnvironment>)
        .build()
        .await
        .expect("builds against the fixture");

        let console = SilentConsole::default();
        let cancel =
            CancelAfterPolls { polls: Arc::clone(&fixture.token_hits), minimum: 2 };

        let code = run_login(
            &service,
            LoginPlan {
                identity: ProviderIdentity::GithubCopilot,
                deployment: None,
                api_key: None,
                use_env: false,
            },
            &console,
            &cancel,
            // Never open a browser from a test.
            false,
        )
        .await;

        assert_eq!(code, EXIT_CANCELLED, "an interrupted login exits 130: {}", console.text());
        // The flow really ran: the challenge was shown and the poller polled.
        assert_eq!(
            fixture.device_hits.load(std::sync::atomic::Ordering::SeqCst),
            1,
            "the device-code request must have been made"
        );
        assert!(
            fixture.polls() >= 2,
            "the poller must have run across at least one interval before being cancelled"
        );
        assert!(console.text().contains("CODA-TEST"), "the challenge was never shown");

        // Nothing was written, anywhere.
        let status = service.status().await.expect("status");
        assert!(status.stored().is_empty(), "a cancelled login must store nothing");
        assert_eq!(settings.applied_count(), 0, "a cancelled login must not touch settings");
        assert_eq!(settings.current().default_provider, None);

        // And the poller is gone: dropping the preparation closed it.
        let after_cancel = fixture.polls();
        tokio::time::sleep(Duration::from_millis(2_500)).await;
        assert_eq!(
            fixture.polls(),
            after_cancel,
            "the device poller kept running after the login was cancelled"
        );
    }

    #[test]
    fn the_browser_opt_out_only_counts_when_it_has_a_value() {
        use std::ffi::OsStr;
        assert!(browser_allowed(None), "unset means a browser may open");
        assert!(browser_allowed(Some(OsStr::new(""))), "an empty value is not an opt-out");
        assert!(!browser_allowed(Some(OsStr::new("1"))));
        assert!(!browser_allowed(Some(OsStr::new("0"))), "any value at all opts out");
    }
}
