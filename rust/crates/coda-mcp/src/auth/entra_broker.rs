//! `entra-broker` auth mode: Microsoft Entra tokens from the OS account broker.
//!
//! [`EntraBrokerProvider`] resolves the Entra authority and scopes (from
//! config, or from the server's protected-resource metadata on the first 401),
//! acquires tokens silently first, escalates to an interactive broker prompt
//! only when required and allowed, and caches tokens process-wide in a
//! [`BrokerTokenCache`]. When the broker is unavailable it delegates to the
//! browser-based [`McpOAuthProvider`].

use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use coda_auth::{CredentialStore, InMemoryStore, secret::Secret};
use coda_diagnostics::{DiagnosticContext, Event};
use serde::{Deserialize, Serialize};

use super::broker::{
    BrokerAccount, BrokerError, BrokerRequest, BrokerToken, BrokerTokenCache, CacheEntry,
    CachedState, TokenBroker, account_store_key, cache_key, default_broker, with_timeout,
};
use super::entra::{self, EntraAuthority};
use super::metadata_client::McpAuthMetadataClient;
use super::oauth::{
    McpOAuthProvider, is_needs_interactive_error, is_valid_tenant, resolve_resource_metadata_url,
};
use super::provider::McpAuthProvider;
use super::types::{
    McpAuthConfig, McpAuthMode, McpAuthResult, McpAuthStatus, WwwAuthenticateChallenge,
    canonical_resource_uri,
};

/// Upper bound for a silent broker call.
pub const SILENT_TIMEOUT: Duration = Duration::from_secs(30);
/// Upper bound for an interactive broker prompt.
pub const INTERACTIVE_TIMEOUT: Duration = Duration::from_secs(180);
/// A cached token is refreshed once it is valid for less than this.
pub const REFRESH_SKEW_SECS: i64 = 300;
/// Automatic refreshes are skipped for this long after a failed acquisition.
pub const FAILURE_COOL_DOWN: Duration = Duration::from_secs(60);
/// Upper bound for a credential-store read or write.
pub const STORE_TIMEOUT: Duration = Duration::from_secs(10);
/// Upper bound for protected-resource metadata discovery.
pub const DISCOVERY_TIMEOUT: Duration = Duration::from_secs(30);

const DEFAULT_ENTRA_HOST: &str = "login.microsoftonline.com";

type OpenBrowser = Arc<dyn Fn(&str) -> Result<(), String> + Send + Sync>;
type LogFn = Arc<dyn Fn(&str) + Send + Sync>;

/// Runtime wiring for an [`EntraBrokerProvider`].
pub struct EntraBrokerOptions {
    /// Server name as configured; used in user-facing hints.
    pub server_name: String,
    pub broker: Arc<dyn TokenBroker>,
    pub cache: Arc<BrokerTokenCache>,
    /// Persists the broker account (and the browser fallback's tokens).
    pub store: Option<Arc<dyn CredentialStore>>,
    /// Whether automatic (non-user-initiated) interactive prompts may be shown.
    /// Still requires `broker.can_interact()`.
    pub allow_interactive: bool,
    pub silent_timeout: Duration,
    pub interactive_timeout: Duration,
    /// Browser opener for the fallback `login()`.
    pub open_browser: Option<OpenBrowser>,
    pub log: Option<LogFn>,
}

impl EntraBrokerOptions {
    /// Global cache, platform broker, no store.
    pub fn defaults(server_name: impl Into<String>) -> Self {
        Self {
            server_name: server_name.into(),
            broker: default_broker(),
            cache: BrokerTokenCache::global(),
            store: None,
            allow_interactive: true,
            silent_timeout: SILENT_TIMEOUT,
            interactive_timeout: INTERACTIVE_TIMEOUT,
            open_browser: None,
            log: None,
        }
    }
}

/// Why a token is being acquired.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Trigger {
    /// A request needs a token; honours the cache and the failure cool-down.
    Automatic,
    /// The server rejected a request; bypasses the provider cache, and the
    /// broker cache too when `force_refresh` (a token was rejected).
    Unauthorized { force_refresh: bool },
    /// `/mcp login`; may always prompt.
    User,
}

impl Trigger {
    fn label(self) -> &'static str {
        match self {
            Trigger::Automatic => "automatic",
            Trigger::Unauthorized { .. } => "unauthorized",
            Trigger::User => "user",
        }
    }
}

/// How an acquisition ended, for diagnostics (closed labels only).
#[derive(Debug, Clone, Copy)]
struct AcquireReport {
    outcome: &'static str,
    interactive_attempted: bool,
}

/// The diagnostics outcome label of a failed broker call.
fn failure_outcome(error: &BrokerError) -> &'static str {
    match error {
        BrokerError::Cancelled => "cancelled",
        BrokerError::Timeout(_) => "timeout",
        BrokerError::Unavailable(_) => "unavailable",
        BrokerError::InteractionRequired(_) | BrokerError::Failed(_) => "failed",
    }
}

/// A broker (or sign-in) message fit for logs: user names and other
/// account identifiers are replaced by `<redacted>`, while `AADSTS` numbers,
/// `0x` codes and the rest of the text are kept.
///
/// Broker messages may name the account (e.g. AADSTS50020 "User account
/// 'ada@contoso.com' from identity provider … does not exist"). The full text
/// stays in `last_error`, which only the user sees; every log line uses this.
pub(crate) fn redact_broker_detail(msg: &str) -> String {
    static PATTERNS: std::sync::OnceLock<[(regex::Regex, &'static str); 2]> =
        std::sync::OnceLock::new();
    let patterns = PATTERNS.get_or_init(|| {
        [
            // A quoted name after an account-ish word: "User account '…'",
            // "user '…'", "login_hint \"…\"", "UPN: '…'".
            (
                regex::Regex::new(
                    r#"(?i)\b(user\s+account|account|user(?:\s*name)?|upn|login_?hint|identity)(\s*[:=]?\s*)(?:'[^']*'|"[^"]*")"#,
                )
                .expect("valid account pattern"),
                "$1$2'<redacted>'",
            ),
            // Anything that looks like an e-mail address or UPN.
            (
                regex::Regex::new(r#"[^\s'"<>(),;\[\]{}]*(?:@|%40)[^\s'"<>(),;\[\]{}]*"#)
                    .expect("valid address pattern"),
                "<redacted>",
            ),
        ]
    });
    patterns
        .iter()
        .fold(msg.to_owned(), |text, (re, with)| re.replace_all(&text, *with).into_owned())
}

type FlightGuard = tokio::sync::OwnedMutexGuard<()>;

#[derive(Debug, Clone, PartialEq, Eq)]
struct Target {
    authority: String,
    scopes: Vec<String>,
}

/// Non-secret account record persisted under `mcp-broker-account:<resource>#<account>`.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct PersistedAccount {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    username: Option<String>,
    #[serde(default)]
    signed_out: bool,
}

/// Browser OAuth used when the broker is unavailable: a headless provider for
/// requests and an interactive one for `/mcp login`, sharing one store.
struct Fallback {
    headless: McpOAuthProvider,
    interactive: McpOAuthProvider,
}

/// `McpAuthProvider` for `auth.mode = "entra-broker"`.
pub struct EntraBrokerProvider {
    inner: Arc<Inner>,
}

/// The provider's state, shared with the acquisition tasks it spawns.
struct Inner {
    metadata: McpAuthMetadataClient,
    canonical_resource: String,
    config: McpAuthConfig,
    client_id: String,
    opts: EntraBrokerOptions,
    entry: Arc<CacheEntry>,
    /// Only ever held briefly (never across an await).
    target: std::sync::Mutex<Option<Target>>,
    /// Serialises protected-resource metadata discovery.
    discovery: tokio::sync::Mutex<()>,
    /// `None` until loaded from the store.
    persisted: tokio::sync::Mutex<Option<PersistedAccount>>,
    fallback: Option<Fallback>,
    /// The diagnostics context the provider was built in; acquisitions
    /// record through it when their caller has none.
    diagnostics: Option<DiagnosticContext>,
}

impl EntraBrokerProvider {
    /// Err when `auth.clientId` is missing (Entra has no dynamic client
    /// registration) or `auth.tenant` is malformed.
    pub fn new(
        http: reqwest::Client,
        url: &reqwest::Url,
        config: McpAuthConfig,
        opts: EntraBrokerOptions,
    ) -> Result<Self, String> {
        let Some(client_id) = config
            .client_id
            .as_deref()
            .map(str::trim)
            .filter(|c| !c.is_empty())
            .map(str::to_owned)
        else {
            return Err(format!(
                "MCP server '{}' uses auth.mode 'entra-broker' but has no auth.clientId. \
                 Microsoft Entra does not support dynamic client registration: register a \
                 public client application and set its id as auth.clientId.",
                opts.server_name
            ));
        };
        if let Some(tenant) = config.tenant.as_deref() {
            if !is_valid_tenant(tenant) {
                return Err(format!(
                    "Invalid auth.tenant '{tenant}' for MCP server '{}': expected a tenant id, \
                     domain, 'organizations', 'common' or 'consumers'.",
                    opts.server_name
                ));
            }
        }

        let canonical_resource = canonical_resource_uri(url);
        let entry = opts.cache.entry(&cache_key(&canonical_resource, config.account.as_deref()));

        let target = match config.tenant.as_deref() {
            Some(tenant) if !config.scopes.is_empty() => Some(Target {
                authority: EntraAuthority {
                    host: DEFAULT_ENTRA_HOST.to_owned(),
                    tenant: tenant.to_owned(),
                }
                .authority_url(),
                scopes: config.scopes.clone(),
            }),
            _ => None,
        };

        let fallback = (!opts.broker.is_available()).then(|| {
            let store = opts
                .store
                .clone()
                .unwrap_or_else(|| Arc::new(InMemoryStore::new()) as Arc<dyn CredentialStore>);
            let provider = |interactive: bool, log: Option<LogFn>| {
                McpOAuthProvider::new(
                    http.clone(),
                    url,
                    Arc::clone(&store),
                    config.clone(),
                    interactive,
                    opts.open_browser.clone(),
                    log,
                )
            };
            // The headless provider's own hint ("run coda interactively") is
            // replaced by a `/mcp login` hint, so it gets no log callback.
            Fallback { headless: provider(false, None), interactive: provider(true, opts.log.clone()) }
        });

        Ok(Self {
            inner: Arc::new(Inner {
                metadata: McpAuthMetadataClient::new(http),
                canonical_resource,
                config,
                client_id,
                opts,
                entry,
                target: std::sync::Mutex::new(target),
                discovery: tokio::sync::Mutex::new(()),
                persisted: tokio::sync::Mutex::new(None),
                fallback,
                diagnostics: coda_diagnostics::current(),
            }),
        })
    }
}

impl Inner {
    fn name(&self) -> &str {
        &self.opts.server_name
    }

    fn display_resource(&self) -> &str {
        self.canonical_resource.split('?').next().unwrap_or(&self.canonical_resource)
    }

    fn target(&self) -> Option<Target> {
        self.target.lock().unwrap_or_else(std::sync::PoisonError::into_inner).clone()
    }

    /// Discover the target unless it is known. Discovery runs without holding
    /// the `target` lock, so requests never wait for it.
    async fn ensure_target(&self, challenge: Option<&WwwAuthenticateChallenge>) -> Result<(), String> {
        if self.target().is_some() {
            return Ok(());
        }
        let _discovery = self.discovery.lock().await;
        if self.target().is_some() {
            return Ok(());
        }
        let target = self.resolve_target(challenge).await.map_err(|msg| self.fail(msg))?;
        *self.target.lock().unwrap_or_else(std::sync::PoisonError::into_inner) = Some(target);
        Ok(())
    }

    fn log(&self, msg: &str) {
        if let Some(f) = &self.opts.log {
            f(msg);
        }
    }

    /// Record a user-facing failure (logged once per distinct message).
    fn fail(&self, msg: String) -> String {
        let changed = {
            let mut state = self.entry.state();
            let changed = state.last_error.as_deref() != Some(msg.as_str());
            state.last_error = Some(msg.clone());
            changed
        };
        tracing::debug!(server = %self.name(), "entra-broker auth: {}", redact_broker_detail(&msg));
        if changed {
            self.log(&msg);
        }
        msg
    }

    fn account_key(&self) -> String {
        account_store_key(&self.canonical_resource, self.config.account.as_deref())
    }

    /// Like [`Self::fail`], for a failure after the broker was asked: also
    /// starts the automatic-refresh cool-down.
    fn broker_failed(&self, msg: String) -> String {
        self.entry.state().last_failure_at = Some(tokio::time::Instant::now());
        self.fail(msg)
    }

    /// Load the persisted account record once, seeding the shared cache state.
    async fn load_persisted(&self) {
        let mut persisted = self.persisted.lock().await;
        if persisted.is_some() {
            return;
        }
        let record = match &self.opts.store {
            Some(store) => match tokio::time::timeout(STORE_TIMEOUT, store.get(&self.account_key()))
                .await
            {
                Ok(Ok(Some(json))) => serde_json::from_str::<PersistedAccount>(&json).ok(),
                Ok(Ok(None)) => None,
                Ok(Err(e)) => {
                    tracing::debug!(error = %e, "failed to read persisted broker account");
                    None
                }
                Err(_) => {
                    tracing::warn!(
                        server = %self.name(),
                        "credential store did not respond within {}s; continuing without the \
                         persisted broker account",
                        STORE_TIMEOUT.as_secs()
                    );
                    None
                }
            },
            None => None,
        }
        .unwrap_or_default();
        {
            let mut state = self.entry.state();
            if record.signed_out {
                state.signed_out = true;
            } else if state.account.is_none() {
                if let Some(id) = record.id.clone() {
                    state.account =
                        Some(BrokerAccount { id, username: record.username.clone().unwrap_or_default() });
                }
            }
        }
        *persisted = Some(record);
    }

    async fn persist(&self, record: PersistedAccount) {
        let mut persisted = self.persisted.lock().await;
        if persisted.as_ref() == Some(&record) {
            return;
        }
        if let Some(store) = &self.opts.store {
            let json = match serde_json::to_string(&record) {
                Ok(j) => j,
                Err(e) => {
                    tracing::debug!(error = %e, "failed to serialise broker account");
                    return;
                }
            };
            match tokio::time::timeout(STORE_TIMEOUT, store.set(&self.account_key(), &json)).await {
                Ok(Ok(())) => {}
                Ok(Err(e)) => {
                    tracing::debug!(error = %e, "failed to persist broker account");
                    return;
                }
                Err(_) => {
                    tracing::warn!(
                        server = %self.name(),
                        "credential store did not respond within {}s; the broker account was \
                         not persisted",
                        STORE_TIMEOUT.as_secs()
                    );
                    return;
                }
            }
        }
        *persisted = Some(record);
    }

    /// Discover the Entra authority and scopes from the server's
    /// protected-resource metadata.
    async fn resolve_target(
        &self,
        challenge: Option<&WwwAuthenticateChallenge>,
    ) -> Result<Target, String> {
        let name = self.name();
        let metadata_url = resolve_resource_metadata_url(challenge, &self.canonical_resource);
        let discovery = tokio::time::timeout(
            DISCOVERY_TIMEOUT,
            self.metadata.get_protected_resource_metadata(&metadata_url),
        )
        .await;
        let prm = match discovery {
            Ok(Some(prm)) => prm,
            Ok(None) => {
                return Err(format!(
                    "Could not discover OAuth protected-resource metadata for {}. Set \
                     auth.tenant and auth.scopes for MCP server '{name}'.",
                    self.display_resource()
                ));
            }
            Err(_) => {
                return Err(format!(
                    "Discovery of OAuth protected-resource metadata for {} did not complete \
                     within {}s. Set auth.tenant and auth.scopes for MCP server '{name}'.",
                    self.display_resource(),
                    DISCOVERY_TIMEOUT.as_secs()
                ));
            }
        };
        let authority = prm
            .authorization_servers
            .first()
            .and_then(|issuer| EntraAuthority::parse(issuer))
            .ok_or_else(|| {
                format!(
                    "{} does not advertise a Microsoft Entra authorization server; use \
                     auth.mode 'oauth' for MCP server '{name}'.",
                    self.display_resource()
                )
            })?;
        let authority = match self.config.tenant.as_deref() {
            Some(tenant) => authority.with_tenant(tenant),
            None => authority,
        };
        let scopes = entra::derive_scopes(
            &self.config.scopes,
            challenge.and_then(|c| c.scope.as_deref()),
            &prm.scopes_supported,
            &self.canonical_resource,
        );
        if scopes.is_empty() {
            return Err(format!(
                "Could not determine the scopes for {}. Set auth.scopes for MCP server '{name}'.",
                self.display_resource()
            ));
        }
        Ok(Target { authority: authority.authority_url(), scopes })
    }

    fn broker_failure(&self, error: &BrokerError) -> String {
        let name = self.name();
        match error {
            BrokerError::Cancelled => format!(
                "Sign-in to MCP server '{name}' was cancelled. Run `/mcp login {name}` to try again."
            ),
            BrokerError::Timeout(limit) => format!(
                "The Windows broker did not respond within {}s for MCP server '{name}'. Run \
                 `/mcp login {name}` to try again.",
                limit.as_secs()
            ),
            BrokerError::Unavailable(reason) => format!(
                "Windows broker unavailable for MCP server '{name}': {reason}. Set \
                 CODA_DISABLE_WAM=1 to use the browser sign-in instead, or use auth.mode 'oauth'."
            ),
            BrokerError::InteractionRequired(detail) | BrokerError::Failed(detail) => format!(
                "Windows broker sign-in for MCP server '{name}' failed: {detail}. Run \
                 `/mcp login {name}` to retry."
            ),
        }
    }

    /// Run [`Self::acquire_locked`] in a task that owns the flight guard, so
    /// the outcome reaches the shared cache even when the caller is dropped
    /// mid-prompt (e.g. a tool timeout), and no second prompt opens meanwhile.
    async fn acquire(
        self: &Arc<Self>,
        flight: FlightGuard,
        trigger: Trigger,
    ) -> Result<BrokerToken, String> {
        let this = Arc::clone(self);
        // The spawned task loses the caller's task-local diagnostics context,
        // so it is carried over (falling back to the construction context).
        let diagnostics = coda_diagnostics::current().or_else(|| self.diagnostics.clone());
        let task = tokio::spawn(async move {
            let run = this.acquire_locked(&flight, trigger, diagnostics.clone());
            match diagnostics {
                // Broker implementations pick the context up from the scope.
                Some(ctx) => coda_diagnostics::scope(ctx, run).await,
                None => run.await,
            }
        });
        match task.await {
            Ok(result) => result,
            Err(e) => Err(self.fail(format!(
                "Sign-in to MCP server '{}' failed unexpectedly: {e}",
                self.name()
            ))),
        }
    }

    /// [`Self::acquire_steps`], recording how it ended.
    async fn acquire_locked(
        &self,
        _flight: &FlightGuard,
        trigger: Trigger,
        diagnostics: Option<DiagnosticContext>,
    ) -> Result<BrokerToken, String> {
        let started = std::time::Instant::now();
        let mut report = AcquireReport { outcome: "failed", interactive_attempted: false };
        let result = self.acquire_steps(trigger, &mut report).await;
        let duration_ms = u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX);
        let console_window = self.opts.broker.can_interact();
        if result.is_ok() {
            tracing::info!(
                server = %self.name(),
                trigger = trigger.label(),
                outcome = report.outcome,
                interactive_attempted = report.interactive_attempted,
                duration_ms,
                console_window,
                "entra-broker token acquisition succeeded"
            );
        } else {
            tracing::warn!(
                server = %self.name(),
                trigger = trigger.label(),
                outcome = report.outcome,
                interactive_attempted = report.interactive_attempted,
                duration_ms,
                console_window,
                "entra-broker token acquisition failed"
            );
        }
        if let Some(ctx) = diagnostics {
            ctx.record(Event::McpBrokerAcquire {
                trigger: trigger.label(),
                outcome: report.outcome,
                interactive_attempted: report.interactive_attempted,
                duration_ms,
                console_window,
            });
        }
        result
    }

    /// Acquire a token: cache re-check, then silent, then interactive when
    /// required and allowed. The caller holds `entry.flight`.
    async fn acquire_steps(
        &self,
        trigger: Trigger,
        report: &mut AcquireReport,
    ) -> Result<BrokerToken, String> {
        let name = self.name().to_owned();
        let Some(target) = self.target() else {
            report.outcome = "no_target";
            return Err(self.fail(format!(
                "The Microsoft Entra authority for MCP server '{name}' is not known yet. Set \
                 auth.tenant and auth.scopes, or connect to the server first."
            )));
        };

        self.load_persisted().await;
        let user_initiated = trigger == Trigger::User;

        let (account_id, declined, signed_out) = {
            let state = self.entry.state();
            if trigger == Trigger::Automatic {
                // Another task may have refreshed the token while we waited.
                if let Some(token) = state.token.as_ref().filter(|t| is_fresh(t, unix_now())) {
                    report.outcome = "cached";
                    return Ok(token.clone());
                }
                // ...or failed, in which case the broker is not asked again yet.
                if in_cool_down(&state) {
                    report.outcome = "cool_down";
                    return Err(state.last_error.clone().unwrap_or_else(|| {
                        format!("Sign-in to MCP server '{name}' failed recently.")
                    }));
                }
            }
            (
                state.account.as_ref().map(|a| a.id.clone()),
                state.interaction_declined,
                state.signed_out,
            )
        };
        if signed_out && !user_initiated {
            report.outcome = "signed_out";
            return Err(self.fail(format!(
                "Signed out of MCP server '{name}'. Run `/mcp login {name}` to sign in again."
            )));
        }

        let request = BrokerRequest {
            authority: target.authority,
            client_id: self.client_id.clone(),
            scopes: target.scopes,
            login_hint: self.config.account.clone(),
            account_id,
            force_refresh: matches!(trigger, Trigger::Unauthorized { force_refresh: true }),
        };

        let silent =
            with_timeout(self.opts.silent_timeout, self.opts.broker.acquire_silent(&request)).await;
        match silent {
            Ok(token) => {
                report.outcome = "silent";
                return Ok(self.accept(token).await);
            }
            Err(BrokerError::InteractionRequired(detail)) => {
                tracing::debug!(
                    server = %name,
                    detail = %redact_broker_detail(&detail),
                    "broker silent sign-in needs interaction"
                );
            }
            Err(e) => {
                report.outcome = failure_outcome(&e);
                return Err(self.broker_failed(self.broker_failure(&e)));
            }
        }

        if !(user_initiated || (self.opts.allow_interactive && !declined)) {
            report.outcome = if declined { "declined" } else { "interaction_required" };
            tracing::warn!(
                server = %name,
                reason = report.outcome,
                "entra-broker sign-in needs interaction; interactive sign-in skipped"
            );
            let msg = if declined {
                format!(
                    "Sign-in to MCP server '{name}' was cancelled earlier, so coda will not prompt \
                     again. Run `/mcp login {name}` to sign in."
                )
            } else {
                format!(
                    "Windows broker sign-in for MCP server '{name}' needs interaction. Run \
                     `/mcp login {name}` to sign in."
                )
            };
            return Err(self.broker_failed(msg));
        }
        if !self.opts.broker.can_interact() {
            report.outcome = "no_console";
            tracing::warn!(
                server = %name,
                reason = report.outcome,
                "entra-broker sign-in needs interaction; interactive sign-in skipped"
            );
            return Err(self.broker_failed(format!(
                "Windows broker sign-in for '{}' needs interaction but no console window is \
                 available. Run `/mcp login {name}` from an interactive coda session.",
                self.display_resource()
            )));
        }

        self.log(&format!("Signing in to MCP server '{name}' with your Windows account…"));
        report.interactive_attempted = true;
        let interactive = with_timeout(
            self.opts.interactive_timeout,
            self.opts.broker.acquire_interactive(&request),
        )
        .await;
        match interactive {
            Ok(token) => {
                report.outcome = "interactive";
                Ok(self.accept(token).await)
            }
            Err(e) => {
                report.outcome = failure_outcome(&e);
                self.entry.state().interaction_declined = true;
                Err(self.broker_failed(self.broker_failure(&e)))
            }
        }
    }

    /// Cache a fresh token, clear failure flags and persist the account.
    async fn accept(&self, token: BrokerToken) -> BrokerToken {
        let account = {
            let mut state = self.entry.state();
            if token.account.is_some() {
                state.account = token.account.clone();
            }
            state.token = Some(token.clone());
            state.interaction_declined = false;
            state.signed_out = false;
            state.last_error = None;
            state.last_failure_at = None;
            state.account.clone()
        };
        self.persist(PersistedAccount {
            id: account.as_ref().map(|a| a.id.clone()),
            username: account.map(|a| a.username),
            signed_out: false,
        })
        .await;
        token
    }

    /// The `/mcp login` hint replacing the headless fallback's error, or
    /// `None` to keep a more specific error.
    fn fallback_hint(&self, headless_error: Option<&str>) -> Option<String> {
        if !headless_error.is_some_and(is_needs_interactive_error) {
            return None;
        }
        let name = self.name();
        let unavailable = if cfg!(windows) {
            "the Windows broker is disabled by CODA_DISABLE_WAM"
        } else {
            "the OS account broker is not available on this platform"
        };
        Some(format!(
            "MCP server '{name}' requires sign-in and {unavailable}. Run `/mcp login {name}` to \
             sign in with your browser."
        ))
    }

    async fn fallback_unauthorized(&self, fallback: &Fallback, www: Option<&str>) -> bool {
        let before = fallback.headless.status_snapshot().last_error;
        if fallback.headless.handle_unauthorized(www).await {
            return true;
        }
        let error = fallback.headless.status_snapshot().last_error;
        let msg = match self.fallback_hint(error.as_deref()) {
            Some(hint) => {
                fallback.headless.record_error(&hint);
                hint
            }
            None => match error {
                Some(e) => e,
                None => return false,
            },
        };
        if before.as_deref() != Some(msg.as_str()) {
            self.log(&msg);
        }
        false
    }
}

#[async_trait]
impl McpAuthProvider for EntraBrokerProvider {
    async fn get_access_token(&self) -> Option<Secret<String>> {
        self.inner.get_access_token().await
    }

    async fn handle_unauthorized(&self, www_authenticate: Option<&str>) -> bool {
        self.inner.handle_unauthorized(www_authenticate).await
    }

    fn status(&self) -> Option<McpAuthStatus> {
        self.inner.status()
    }

    fn required_auth_error(&self) -> Option<String> {
        self.inner.required_auth_error()
    }

    async fn login(&self) -> McpAuthResult {
        self.inner.login().await
    }

    async fn logout(&self) -> McpAuthResult {
        self.inner.logout().await
    }
}

impl Inner {
    async fn get_access_token(self: &Arc<Self>) -> Option<Secret<String>> {
        if let Some(fallback) = &self.fallback {
            return fallback.headless.get_access_token().await;
        }
        let (cached, cooling) = {
            let state = self.entry.state();
            (state.token.clone(), in_cool_down(&state))
        };
        if let Some(token) = cached.as_ref().filter(|t| is_fresh(t, unix_now())) {
            return Some(token.access_token.clone());
        }
        // Keep serving the old token until it actually expires.
        let unexpired = || {
            cached
                .as_ref()
                .filter(|t| t.expires_at_unix > unix_now())
                .map(|t| t.access_token.clone())
        };
        if cooling || self.target().is_none() {
            return unexpired();
        }
        let flight = if unexpired().is_some() {
            // Inside the refresh window: never wait for a refresh in flight.
            match Arc::clone(&self.entry.flight).try_lock_owned() {
                Ok(guard) => guard,
                Err(_) => return unexpired(),
            }
        } else {
            Arc::clone(&self.entry.flight).lock_owned().await
        };
        match self.acquire(flight, Trigger::Automatic).await {
            Ok(token) => Some(token.access_token),
            Err(_) => unexpired(),
        }
    }

    async fn handle_unauthorized(self: &Arc<Self>, www_authenticate: Option<&str>) -> bool {
        if let Some(fallback) = &self.fallback {
            return self.fallback_unauthorized(fallback, www_authenticate).await;
        }
        let (rejected, failure_seen) = {
            let state = self.entry.state();
            (state.token.as_ref().map(|t| t.access_token.clone()), state.last_failure_at)
        };
        let challenge = WwwAuthenticateChallenge::parse(www_authenticate);
        if self.ensure_target(challenge.as_ref()).await.is_err() {
            return false;
        }
        let flight = Arc::clone(&self.entry.flight).lock_owned().await;
        let had_token = {
            let mut state = self.entry.state();
            // Another task replaced the rejected token while we waited.
            if let Some(token) = &state.token {
                if rejected.as_ref().is_none_or(|r| r.expose() != token.access_token.expose()) {
                    return true;
                }
            }
            // ...or asked the broker and failed: do not ask it again right away.
            if state.last_failure_at.is_some() && state.last_failure_at != failure_seen {
                return false;
            }
            // A 401 while a token was attached means the token is no good.
            state.token.take().is_some() || rejected.is_some()
        };
        self.acquire(flight, Trigger::Unauthorized { force_refresh: had_token })
            .await
            .is_ok()
    }

    /// The last sign-in error, once the target is known (so a token is
    /// required) and no unexpired token is cached. The browser fallback keeps
    /// its own 401-driven flow and never reports one.
    fn required_auth_error(&self) -> Option<String> {
        if self.fallback.is_some() || self.target().is_none() {
            return None;
        }
        let state = self.entry.state();
        if state.token.as_ref().is_some_and(|t| t.expires_at_unix > unix_now()) {
            return None;
        }
        state.last_error.clone()
    }

    fn status(&self) -> Option<McpAuthStatus> {
        if let Some(fallback) = &self.fallback {
            let mut status = fallback.headless.status_snapshot();
            status.mode = McpAuthMode::EntraBroker;
            return Some(status);
        }
        let state = self.entry.state();
        Some(McpAuthStatus {
            mode: McpAuthMode::EntraBroker,
            account: state.account.as_ref().map(|a| a.username.clone()).filter(|u| !u.is_empty()),
            expires_at_unix: state.token.as_ref().map(|t| t.expires_at_unix),
            last_error: state.last_error.clone(),
        })
    }

    async fn login(self: &Arc<Self>) -> McpAuthResult {
        if let Some(fallback) = &self.fallback {
            let result = fallback.interactive.login().await;
            match (&result.succeeded, &result.error) {
                (true, _) => {
                    fallback.headless.clear_error();
                    // Reload so status reports the new token's expiry.
                    let _ = fallback.headless.get_access_token().await;
                }
                (false, Some(e)) => fallback.headless.record_error(e),
                (false, None) => {}
            }
            return result;
        }

        if let Err(msg) = self.ensure_target(None).await {
            return McpAuthResult { succeeded: false, error: Some(msg) };
        }
        let flight = Arc::clone(&self.entry.flight).lock_owned().await;
        self.load_persisted().await;
        {
            let mut state = self.entry.state();
            state.interaction_declined = false;
            state.signed_out = false;
        }
        match self.acquire(flight, Trigger::User).await {
            Ok(token) => {
                let who = token.account.map(|a| a.username).filter(|u| !u.is_empty());
                self.log(&match who {
                    Some(u) => format!("Signed in to MCP server '{}' as {u}.", self.name()),
                    None => format!("Signed in to MCP server '{}'.", self.name()),
                });
                McpAuthResult { succeeded: true, error: None }
            }
            Err(msg) => McpAuthResult { succeeded: false, error: Some(msg) },
        }
    }

    async fn logout(&self) -> McpAuthResult {
        if let Some(fallback) = &self.fallback {
            return fallback.headless.logout().await;
        }
        let _flight = self.entry.flight.lock().await;
        self.load_persisted().await;
        {
            let mut state = self.entry.state();
            state.token = None;
            state.account = None;
            state.signed_out = true;
            state.interaction_declined = false;
            state.last_error = None;
            state.last_failure_at = None;
        }
        self.persist(PersistedAccount { id: None, username: None, signed_out: true }).await;
        self.log(&format!(
            "Signed out of MCP server '{}'. Your Windows account itself stays signed in.",
            self.name()
        ));
        McpAuthResult { succeeded: true, error: None }
    }
}

fn in_cool_down(state: &CachedState) -> bool {
    state.last_failure_at.is_some_and(|at| at.elapsed() < FAILURE_COOL_DOWN)
}

fn is_fresh(token: &BrokerToken, now: i64) -> bool {
    token.expires_at_unix - now > REFRESH_SKEW_SECS
}

fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

#[cfg(test)]
mod tests;
