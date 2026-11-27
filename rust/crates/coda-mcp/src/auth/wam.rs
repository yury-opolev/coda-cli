//! Windows Web Account Manager (WAM) token broker.
//!
//! Talks to the Microsoft Entra WAM plugin through
//! `Windows.Security.Authentication.Web.Core.WebAuthenticationCoreManager`.
//! Every operation runs on a blocking thread that joins the WinRT MTA and
//! polls the WinRT async operation against a deadline, cancelling it when the
//! deadline passes, so a hung broker never blocks a tokio worker or outlives
//! its time budget.

use std::cell::Cell;
use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock, PoisonError};
use std::time::{Duration, Instant};

use async_trait::async_trait;
use coda_auth::secret::Secret;
use coda_diagnostics::{DiagnosticContext, Event};
use windows::core::{RuntimeType, HSTRING};
use windows::Security::Authentication::Web::Core::{
    FindAllWebAccountsStatus, WebAuthenticationCoreManager, WebTokenRequest,
    WebTokenRequestPromptType, WebTokenRequestResult, WebTokenRequestStatus,
};
use windows::Security::Credentials::{WebAccount, WebAccountProvider};
use windows::Win32::Foundation::{HWND, RPC_E_CHANGED_MODE};
use windows::Win32::System::Com::CoIncrementMTAUsage;
use windows::Win32::System::Console::GetConsoleWindow;
use windows::Win32::System::WinRT::{
    IWebAuthenticationCoreManagerInterop, RoInitialize, RO_INIT_MULTITHREADED,
};
use windows::Win32::UI::WindowsAndMessaging::{GetAncestor, GA_ROOTOWNER};
use windows_future::{AsyncStatus, IAsyncOperation};

use super::broker::{BrokerAccount, BrokerError, BrokerRequest, BrokerToken, TokenBroker};
use super::entra::{jwt_expiry_unix, EntraAuthority};
use super::entra_broker::{INTERACTIVE_TIMEOUT, SILENT_TIMEOUT};

/// The Microsoft Entra WAM account provider id.
const ENTRA_PROVIDER_ID: &str = "https://login.microsoft.com";
const POLL_INTERVAL: Duration = Duration::from_millis(25);
const DEFAULT_LIFETIME_SECS: i64 = 3600;
/// Seconds between 1601-01-01 and 1970-01-01.
const WINDOWS_EPOCH_OFFSET_SECS: i64 = 11_644_473_600;
const MAX_PLAUSIBLE_LIFETIME_SECS: i64 = 30 * 24 * 3600;
const NO_CONSOLE: &str = "no console window is available to show the Windows sign-in dialog";

/// Token broker backed by the Windows Web Account Manager.
pub struct WamBroker {
    /// `WebAccount`s seen so far, by account id (WinRT agile objects, so they
    /// may be used from any MTA thread).
    accounts: Arc<Mutex<HashMap<String, WebAccount>>>,
    silent_budget: Duration,
    interactive_budget: Duration,
}

impl Default for WamBroker {
    fn default() -> Self {
        Self::new()
    }
}

impl WamBroker {
    pub fn new() -> Self {
        Self {
            accounts: Arc::default(),
            silent_budget: SILENT_TIMEOUT,
            interactive_budget: INTERACTIVE_TIMEOUT,
        }
    }

    async fn run<F>(
        &self,
        budget: Duration,
        mode: &'static str,
        op: F,
    ) -> Result<BrokerToken, BrokerError>
    where
        F: FnOnce(Instant, &mut CallInfo) -> Result<BrokerToken, BrokerError> + Send + 'static,
    {
        // Captured here: the blocking thread has no task-local context.
        let diagnostics = coda_diagnostics::current();
        tokio::task::spawn_blocking(move || {
            // Recorded on the blocking thread, so a caller that stops waiting
            // (a timeout) still gets the call into the log.
            let started = Instant::now();
            let mut info = CallInfo::default();
            let result = ensure_mta().and_then(|()| op(started + budget, &mut info));
            report_call(mode, started.elapsed(), &info, &result, diagnostics.as_ref());
            result
        })
        .await
        .unwrap_or_else(|e| {
            Err(BrokerError::Failed(format!(
                "Windows broker task failed: {e}"
            )))
        })
    }
}

#[async_trait]
impl TokenBroker for WamBroker {
    fn is_available(&self) -> bool {
        // Real availability is discovered per call (`Unavailable` errors).
        true
    }

    fn can_interact(&self) -> bool {
        parent_window().is_some()
    }

    async fn acquire_silent(&self, request: &BrokerRequest) -> Result<BrokerToken, BrokerError> {
        let request = request.clone();
        let accounts = Arc::clone(&self.accounts);
        let budget = self.silent_budget;
        self.run(budget, "silent", move |deadline, info| {
            silent(&request, &accounts, deadline, budget, info)
        })
        .await
    }

    async fn acquire_interactive(
        &self,
        request: &BrokerRequest,
    ) -> Result<BrokerToken, BrokerError> {
        let request = request.clone();
        let accounts = Arc::clone(&self.accounts);
        let budget = self.interactive_budget;
        self.run(budget, "interactive", move |deadline, info| {
            interactive(&request, &accounts, deadline, budget, info)
        })
        .await
    }
}

type AccountCache = Mutex<HashMap<String, WebAccount>>;

fn silent(
    request: &BrokerRequest,
    accounts: &AccountCache,
    deadline: Instant,
    budget: Duration,
    info: &mut CallInfo,
) -> Result<BrokerToken, BrokerError> {
    info.console_window = parent_window().is_some();
    let provider = find_provider(&request.authority, deadline, budget)?;
    let token_request = build_request(&provider, request)?;
    let mut account = match request.account_id.as_deref() {
        Some(id) => find_account(&provider, id, accounts, deadline, budget)?,
        None => None,
    };
    if account.is_none() {
        // Without a WebAccount, WAM signs in the Windows default account and
        // ignores the login hint, which may be an account of another tenant.
        if let Some(hint) = login_hint(request) {
            account =
                find_account_by_hint(&provider, &request.client_id, hint, accounts, deadline, budget);
        }
    }
    info.account_known = account.is_some();
    let op = match &account {
        Some(account) => WebAuthenticationCoreManager::GetTokenSilentlyWithWebAccountAsync(
            &token_request,
            account,
        ),
        None => WebAuthenticationCoreManager::GetTokenSilentlyAsync(&token_request),
    }
    .map_err(|e| win_failure("silent token request", &e))?;
    let result = wait(&op, deadline, budget)?;
    finish(&result, accounts, info)
}

fn interactive(
    request: &BrokerRequest,
    accounts: &AccountCache,
    deadline: Instant,
    budget: Duration,
    info: &mut CallInfo,
) -> Result<BrokerToken, BrokerError> {
    let Some(hwnd) = parent_window() else {
        return Err(BrokerError::InteractionRequired(NO_CONSOLE.to_owned()));
    };
    info.console_window = true;
    let provider = find_provider(&request.authority, deadline, budget)?;
    let token_request = build_request(&provider, request)?;
    let interop = windows::core::factory::<
        WebAuthenticationCoreManager,
        IWebAuthenticationCoreManagerInterop,
    >()
    .map_err(|e| win_failure("interop", &e))?;
    // SAFETY: `hwnd` is a valid window handle (the console window or its root
    // owner, which under Windows Terminal belongs to the terminal's process)
    // used only as the dialog's owner, and `token_request` is a valid
    // `WebTokenRequest`.
    let op = unsafe {
        interop.RequestTokenForWindowAsync::<_, IAsyncOperation<WebTokenRequestResult>>(
            hwnd,
            &token_request,
        )
    }
    .map_err(|e| win_failure("interactive token request", &e))?;
    let result = wait(&op, deadline, budget)?;
    finish(&result, accounts, info)
}

/// Keeps the process MTA — and with it the WAM DLLs behind the activation
/// factories `windows-core` caches process-wide — alive for the life of the
/// process.
///
/// Without it the MTA is torn down when the last blocking-pool thread that
/// joined it exits, COM unloads `Windows.Security.Authentication.Web.Core.dll`
/// about 30 s later, and the next WAM call dereferences the dangling cached
/// factory (access violation). `windows-core` gives implicit callers the same
/// guarantee; the explicit `RoInitialize` below bypasses that path.
fn keep_mta_alive() -> Result<(), BrokerError> {
    static KEPT: OnceLock<Result<(), String>> = OnceLock::new();
    KEPT.get_or_init(|| {
        // SAFETY: plain COM bookkeeping. The cookie is deliberately never
        // passed to `CoDecrementMTAUsage`.
        unsafe { CoIncrementMTAUsage() }
            .map(|_cookie| ())
            .map_err(|e| e.message().to_string())
    })
    .clone()
    .map_err(|m| BrokerError::Failed(format!("Windows broker could not keep COM initialised: {m}")))
}

/// Join the WinRT multi-threaded apartment once per (blocking pool) thread.
fn ensure_mta() -> Result<(), BrokerError> {
    keep_mta_alive()?;
    thread_local! {
        static INITIALIZED: Cell<bool> = const { Cell::new(false) };
    }
    if INITIALIZED.with(Cell::get) {
        return Ok(());
    }
    // SAFETY: plain apartment initialisation of the current thread. `S_FALSE`
    // (already initialised) is a success; an STA thread reports
    // `RPC_E_CHANGED_MODE`, where WAM's agile objects still work.
    match unsafe { RoInitialize(RO_INIT_MULTITHREADED) } {
        Ok(()) => {}
        Err(e) if e.code() == RPC_E_CHANGED_MODE => {}
        Err(e) => return Err(win_failure("WinRT initialisation", &e)),
    }
    INITIALIZED.with(|i| i.set(true));
    Ok(())
}

/// The console's root owner window (works under Windows Terminal), else the
/// console window itself.
fn parent_window() -> Option<HWND> {
    // SAFETY: both calls only read window handles.
    let console = unsafe { GetConsoleWindow() };
    if console.is_invalid() {
        return None;
    }
    let root = unsafe { GetAncestor(console, GA_ROOTOWNER) };
    Some(if root.is_invalid() { console } else { root })
}

fn find_provider(
    authority: &str,
    deadline: Instant,
    budget: Duration,
) -> Result<WebAccountProvider, BrokerError> {
    let wam_authority = authority_for(authority);
    let op = WebAuthenticationCoreManager::FindAccountProviderWithAuthorityAsync(
        &HSTRING::from(ENTRA_PROVIDER_ID),
        &HSTRING::from(wam_authority.as_str()),
    )
    .map_err(|e| win_failure("account provider lookup", &e))?;
    match wait_raw(&op, deadline, budget)? {
        Ok(provider) => Ok(provider),
        // Only "no such provider" means the broker cannot serve this authority.
        Err(e) if is_null_result(&e) => Err(BrokerError::Unavailable(format!(
            "no Microsoft account provider found for authority {wam_authority}"
        ))),
        Err(e) => Err(win_failure("account provider lookup", &e)),
    }
}

/// A remembered account, from the cache or looked up by id. `None` only when
/// the account no longer exists (the caller then uses the default account);
/// a failed lookup is an error, so a transient failure never switches
/// accounts.
fn find_account(
    provider: &WebAccountProvider,
    id: &str,
    accounts: &AccountCache,
    deadline: Instant,
    budget: Duration,
) -> Result<Option<WebAccount>, BrokerError> {
    if let Some(account) = accounts
        .lock()
        .unwrap_or_else(PoisonError::into_inner)
        .get(id)
    {
        return Ok(Some(account.clone()));
    }
    let op = WebAuthenticationCoreManager::FindAccountAsync(provider, &HSTRING::from(id))
        .map_err(|e| win_failure("account lookup", &e))?;
    match wait_raw(&op, deadline, budget)? {
        Ok(account) => {
            accounts
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .insert(id.to_owned(), account.clone());
            Ok(Some(account))
        }
        Err(e) if is_null_result(&e) => Ok(None),
        Err(e) => Err(win_failure("account lookup", &e)),
    }
}

/// The accounts WAM reports for `provider`: `FindAllAccountsResult` status
/// and accounts. `client_id` narrows the search to accounts known to the app.
fn find_all_accounts(
    provider: &WebAccountProvider,
    client_id: Option<&str>,
    deadline: Instant,
    budget: Duration,
) -> Result<(FindAllWebAccountsStatus, Vec<WebAccount>), BrokerError> {
    let op = match client_id {
        Some(id) => WebAuthenticationCoreManager::FindAllAccountsWithClientIdAsync(
            provider,
            &HSTRING::from(id),
        ),
        None => WebAuthenticationCoreManager::FindAllAccountsAsync(provider),
    }
    .map_err(|e| win_failure("account enumeration", &e))?;
    let result = wait(&op, deadline, budget)?;
    let status = result
        .Status()
        .map_err(|e| win_failure("account enumeration", &e))?;
    let accounts = if status == FindAllWebAccountsStatus::Success {
        let view = result
            .Accounts()
            .map_err(|e| win_failure("account enumeration", &e))?;
        let size = view.Size().map_err(|e| win_failure("account enumeration", &e))?;
        (0..size).filter_map(|i| view.GetAt(i).ok()).collect()
    } else {
        Vec::new()
    };
    Ok((status, accounts))
}

/// The Windows account whose user name is the configured login hint, from
/// the app's accounts first, then all of the provider's accounts. `None`
/// when there is no such account or enumeration is unsupported, denied or
/// fails; the caller then falls back to the default account plus hint.
fn find_account_by_hint(
    provider: &WebAccountProvider,
    client_id: &str,
    hint: &str,
    accounts: &AccountCache,
    deadline: Instant,
    budget: Duration,
) -> Option<WebAccount> {
    for scope in [Some(client_id), None] {
        match find_all_accounts(provider, scope, deadline, budget) {
            Ok((status, found)) => {
                let names: Vec<String> = found
                    .iter()
                    .map(|a| a.UserName().map(|u| u.to_string()).unwrap_or_default())
                    .collect();
                let index = matching_account(&names, hint);
                tracing::debug!(
                    by_client_id = scope.is_some(),
                    status = status.0,
                    accounts = found.len(),
                    matched = index.is_some(),
                    "WAM account enumeration for the configured account"
                );
                if let Some(account) = index.and_then(|i| found.into_iter().nth(i)) {
                    if let Ok(id) = account.Id() {
                        accounts
                            .lock()
                            .unwrap_or_else(PoisonError::into_inner)
                            .insert(id.to_string(), account.clone());
                    }
                    return Some(account);
                }
            }
            Err(e) => {
                tracing::debug!(
                    by_client_id = scope.is_some(),
                    timeout = matches!(e, BrokerError::Timeout(_)),
                    "WAM account enumeration failed"
                );
            }
        }
        if Instant::now() >= deadline {
            break;
        }
    }
    None
}

/// Index of the account whose user name equals `hint`, ignoring case and
/// surrounding whitespace.
fn matching_account(usernames: &[String], hint: &str) -> Option<usize> {
    let hint = hint.trim();
    if hint.is_empty() {
        return None;
    }
    usernames
        .iter()
        .position(|name| name.trim().to_lowercase() == hint.to_lowercase())
}

/// The configured account (login hint), if set.
fn login_hint(request: &BrokerRequest) -> Option<&str> {
    request
        .login_hint
        .as_deref()
        .map(str::trim)
        .filter(|h| !h.is_empty())
}

fn build_request(
    provider: &WebAccountProvider,
    request: &BrokerRequest,
) -> Result<WebTokenRequest, BrokerError> {
    let token_request = WebTokenRequest::CreateWithPromptType(
        provider,
        &HSTRING::from(request.scopes.join(" ")),
        &HSTRING::from(request.client_id.as_str()),
        WebTokenRequestPromptType::Default,
    )
    .map_err(|e| win_failure("token request", &e))?;
    let properties = token_request
        .Properties()
        .map_err(|e| win_failure("token request", &e))?;
    for (key, value) in request_properties(request) {
        properties
            .Insert(&HSTRING::from(key), &HSTRING::from(value))
            .map_err(|e| win_failure("token request", &e))?;
    }
    Ok(token_request)
}

/// WAM request properties understood by the Entra (AAD) WAM plugin.
///
/// `wam_compat=2.0` selects the v2.0 (scope-based) protocol, as MSAL does.
/// Adding `api-version=2.0` next to it makes Entra reject the request with
/// AADSTS901002 ("'wam_compat' request parameter is not supported"), so it is
/// deliberately not sent.
fn request_properties(request: &BrokerRequest) -> Vec<(&'static str, String)> {
    let mut properties = vec![
        ("wam_compat", "2.0".to_owned()),
        ("authority", request.authority.clone()),
    ];
    if let Some(hint) = login_hint(request) {
        properties.push(("LoginHint", hint.to_owned()));
    }
    if request.force_refresh {
        // MSAL's WAM AAD plugin passes `forcerefresh=true` to bypass the
        // broker's token cache. It is not formally documented for direct WAM
        // callers; if the plugin ignores it, the server's next 401 still makes
        // the provider retry.
        properties.push(("forcerefresh", "true".to_owned()));
    }
    properties
}

/// The WAM authority: `organizations` for multi-tenant work/school sign-in,
/// else the tenant authority URL.
fn authority_for(authority: &str) -> String {
    let trimmed = authority.trim().trim_end_matches('/');
    match EntraAuthority::parse(trimmed) {
        Some(a)
            if a.tenant.eq_ignore_ascii_case("organizations")
                || a.tenant.eq_ignore_ascii_case("common") =>
        {
            "organizations".to_owned()
        }
        _ => trimmed.to_owned(),
    }
}

/// Wait for a WinRT async operation until `deadline`, cancelling it then.
fn wait<T: RuntimeType + 'static>(
    op: &IAsyncOperation<T>,
    deadline: Instant,
    budget: Duration,
) -> Result<T, BrokerError> {
    wait_raw(op, deadline, budget)?.map_err(|e| win_failure("call", &e))
}

/// Like [`wait`], but hands back the raw `GetResults()` outcome of a
/// completed operation so callers can tell a null result from an error.
fn wait_raw<T: RuntimeType + 'static>(
    op: &IAsyncOperation<T>,
    deadline: Instant,
    budget: Duration,
) -> Result<windows::core::Result<T>, BrokerError> {
    loop {
        let status = op.Status().map_err(|e| win_failure("call", &e))?;
        if status == AsyncStatus::Completed {
            return Ok(op.GetResults());
        }
        if status == AsyncStatus::Canceled {
            return Err(BrokerError::Cancelled);
        }
        if status == AsyncStatus::Error {
            // `GetResults` reports the failure HRESULT with its message.
            return Err(match op.GetResults() {
                Err(e) => win_failure("call", &e),
                Ok(_) => {
                    let code = op.ErrorCode().map(|c| c.0).unwrap_or(0);
                    BrokerError::Failed(format!("Windows broker call failed: 0x{:08x}", code as u32))
                }
            });
        }
        if Instant::now() >= deadline {
            let _ = op.Cancel();
            return Err(BrokerError::Timeout(budget));
        }
        std::thread::sleep(POLL_INTERVAL);
    }
}

/// A WAM token response, read out of the WinRT objects.
#[derive(Debug, Default)]
struct WamOutcome {
    status: WebTokenRequestStatus,
    error_code: u32,
    error_message: String,
    token: Option<WamToken>,
}

struct WamToken {
    token: String,
    account: Option<BrokerAccount>,
    /// `TokenExpiresOn` response property (see [`parse_expires_on`]).
    expires_on: Option<String>,
}

impl std::fmt::Debug for WamToken {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WamToken")
            .field("account", &self.account)
            .field("expires_on", &self.expires_on)
            .finish_non_exhaustive()
    }
}

fn finish(
    result: &WebTokenRequestResult,
    accounts: &AccountCache,
    info: &mut CallInfo,
) -> Result<BrokerToken, BrokerError> {
    let outcome = read_outcome(result, accounts)?;
    info.note_response(outcome.status, outcome.error_code, &outcome.error_message);
    map_outcome(outcome, unix_now())
}

fn read_outcome(
    result: &WebTokenRequestResult,
    accounts: &AccountCache,
) -> Result<WamOutcome, BrokerError> {
    let status = result
        .ResponseStatus()
        .map_err(|e| win_failure("token response", &e))?;
    let (error_code, error_message) = match result.ResponseError() {
        Ok(err) => (
            err.ErrorCode().unwrap_or(0),
            err.ErrorMessage()
                .map(|m| m.to_string())
                .unwrap_or_default(),
        ),
        Err(_) => (0, String::new()),
    };
    let token = if matches!(
        status,
        WebTokenRequestStatus::Success | WebTokenRequestStatus::AccountSwitch
    ) {
        result
            .ResponseData()
            .and_then(|data| data.GetAt(0))
            .ok()
            .map(|response| {
                let account = response.WebAccount().ok().and_then(|account| {
                    let id = account.Id().ok()?.to_string();
                    let username = account
                        .UserName()
                        .map(|u| u.to_string())
                        .unwrap_or_default();
                    accounts
                        .lock()
                        .unwrap_or_else(PoisonError::into_inner)
                        .insert(id.clone(), account);
                    Some(BrokerAccount { id, username })
                });
                let expires_on = response
                    .Properties()
                    .and_then(|p| p.Lookup(&HSTRING::from("TokenExpiresOn")))
                    .ok()
                    .map(|v| v.to_string());
                WamToken {
                    token: response.Token().map(|t| t.to_string()).unwrap_or_default(),
                    account,
                    expires_on,
                }
            })
    } else {
        None
    };
    Ok(WamOutcome {
        status,
        error_code,
        error_message,
        token,
    })
}

/// Map a WAM response to a broker result. Never puts token text in errors.
fn map_outcome(outcome: WamOutcome, now: i64) -> Result<BrokerToken, BrokerError> {
    let WamOutcome {
        status,
        error_code,
        error_message,
        token,
    } = outcome;
    let message = error_message.trim().to_owned();
    match status {
        WebTokenRequestStatus::Success | WebTokenRequestStatus::AccountSwitch => {
            let Some(token) = token.filter(|t| !t.token.is_empty()) else {
                return Err(BrokerError::Failed("WAM returned no token".to_owned()));
            };
            let expires_at_unix = token
                .expires_on
                .as_deref()
                .and_then(|v| parse_expires_on(v, now))
                .or_else(|| jwt_expiry_unix(&token.token))
                .unwrap_or(now + DEFAULT_LIFETIME_SECS);
            Ok(BrokerToken {
                access_token: Secret::new(token.token),
                expires_at_unix,
                account: token.account,
            })
        }
        WebTokenRequestStatus::UserInteractionRequired => {
            Err(BrokerError::InteractionRequired(if message.is_empty() {
                "the Windows broker needs you to sign in".to_owned()
            } else {
                message
            }))
        }
        WebTokenRequestStatus::UserCancel => Err(BrokerError::Cancelled),
        WebTokenRequestStatus::AccountProviderNotAvailable => {
            Err(BrokerError::Unavailable(if message.is_empty() {
                "the Microsoft Entra account provider is not available".to_owned()
            } else {
                message
            }))
        }
        _ if message.is_empty() => {
            Err(BrokerError::Failed(format!("WAM error 0x{error_code:08x}")))
        }
        // A provider error that a sign-in can resolve (consent, MFA, another
        // account, an expired session) lets the caller escalate to the dialog.
        _ if needs_interaction(&message) => Err(BrokerError::InteractionRequired(format!(
            "WAM error 0x{error_code:08x}: {message}"
        ))),
        _ => Err(BrokerError::Failed(format!(
            "WAM error 0x{error_code:08x}: {message}"
        ))),
    }
}

/// `AADSTS` errors an interactive sign-in can resolve: the account is not in
/// the tenant (50020), no or an expired session (50058, 50133, 50173, 70043,
/// 700082, 160021), MFA or strong authentication (50072, 50074, 50076,
/// 50079, 50158), an expired password (50055), a device-compliance
/// remediation (53000) and consent (65001, 65004).
const INTERACTIVE_AADSTS: &[u32] = &[
    50020, 50055, 50058, 50072, 50074, 50076, 50079, 50133, 50158, 50173, 53000, 65001, 65004,
    70043, 160021, 700082,
];

/// `AADSTS` errors a sign-in cannot fix, even when the message also says
/// `invalid_grant`: blocked by Conditional Access (53003), a misconfigured
/// app registration (50011, 700016, 7000218, 901002), a user not assigned to
/// the app (50105) or an unknown tenant (90002).
const NON_INTERACTIVE_AADSTS: &[u32] = &[50011, 50105, 53003, 90002, 700016, 7000218, 901002];

/// OAuth error codes that mean "ask the user".
const INTERACTIVE_ERRORS: &[&str] =
    &["interaction_required", "consent_required", "login_required", "invalid_grant"];

/// Whether a WAM provider error message calls for an interactive sign-in.
fn needs_interaction(message: &str) -> bool {
    match parse_aadsts(message) {
        Some(code) if NON_INTERACTIVE_AADSTS.contains(&code) => false,
        Some(code) if INTERACTIVE_AADSTS.contains(&code) => true,
        _ => {
            let lower = message.to_ascii_lowercase();
            INTERACTIVE_ERRORS.iter().any(|e| lower.contains(e))
        }
    }
}

/// The number of the first `AADSTS<digits>` code in `message`.
fn parse_aadsts(message: &str) -> Option<u32> {
    message.match_indices("AADSTS").find_map(|(at, tag)| {
        let digits: String = message[at + tag.len()..]
            .chars()
            .take_while(char::is_ascii_digit)
            .collect();
        digits.parse().ok()
    })
}

/// What one WAM token call did, for diagnostics: codes and flags only.
#[derive(Debug, Default, Clone, PartialEq, Eq)]
struct CallInfo {
    /// The WAM response status, once a response was read.
    wam_status: Option<WebTokenRequestStatus>,
    provider_error_code: Option<u32>,
    aadsts: Option<u32>,
    console_window: bool,
    /// A specific `WebAccount` was used rather than the default account.
    account_known: bool,
}

impl CallInfo {
    /// Keep the numbers of a WAM response; the message itself is dropped.
    fn note_response(&mut self, status: WebTokenRequestStatus, error_code: u32, message: &str) {
        self.wam_status = Some(status);
        self.provider_error_code = (error_code != 0).then_some(error_code);
        self.aadsts = parse_aadsts(message);
    }
}

/// The closed diagnostics label of a WAM call: the WAM status when a
/// response was read, else what stopped the call.
fn call_status(info: &CallInfo, result: &Result<BrokerToken, BrokerError>) -> &'static str {
    if let Some(status) = info.wam_status {
        return match status {
            WebTokenRequestStatus::Success => "success",
            WebTokenRequestStatus::AccountSwitch => "account_switch",
            WebTokenRequestStatus::UserCancel => "user_cancel",
            WebTokenRequestStatus::UserInteractionRequired => "user_interaction_required",
            WebTokenRequestStatus::AccountProviderNotAvailable => "account_provider_not_available",
            _ => "provider_error",
        };
    }
    match result {
        Ok(_) => "success",
        Err(BrokerError::Timeout(_)) => "timeout",
        Err(BrokerError::Unavailable(_)) => "account_provider_not_available",
        Err(BrokerError::InteractionRequired(m)) if m == NO_CONSOLE => "no_console",
        Err(_) => "winrt_error",
    }
}

/// Record one WAM call in the engine log and the diagnostics log.
fn report_call(
    mode: &'static str,
    elapsed: Duration,
    info: &CallInfo,
    result: &Result<BrokerToken, BrokerError>,
    diagnostics: Option<&DiagnosticContext>,
) {
    let status = call_status(info, result);
    let duration_ms = u64::try_from(elapsed.as_millis()).unwrap_or(u64::MAX);
    if result.is_ok() {
        tracing::info!(
            mode,
            status,
            duration_ms,
            console_window = info.console_window,
            account_known = info.account_known,
            "WAM token call succeeded"
        );
    } else {
        tracing::warn!(
            mode,
            status,
            duration_ms,
            provider_error_code = info.provider_error_code,
            aadsts = info.aadsts,
            console_window = info.console_window,
            account_known = info.account_known,
            "WAM token call failed"
        );
    }
    if let Some(ctx) = diagnostics {
        ctx.record(Event::McpBrokerCall {
            mode,
            duration_ms,
            status,
            provider_error_code: info.provider_error_code,
            aadsts: info.aadsts,
            console_window: info.console_window,
            account_known: info.account_known,
        });
    }
}

/// Parse the `TokenExpiresOn` response property into unix seconds.
///
/// The Entra WAM plugin reports seconds since the Windows epoch
/// (1601-01-01); plain unix seconds are accepted too. Implausible values
/// (more than 30 days ahead, or not positive) are ignored.
fn parse_expires_on(value: &str, now: i64) -> Option<i64> {
    let raw = value.trim().parse::<i64>().ok()?;
    let unix = if raw > WINDOWS_EPOCH_OFFSET_SECS {
        raw - WINDOWS_EPOCH_OFFSET_SECS
    } else {
        raw
    };
    (unix > 0 && unix <= now + MAX_PLAUSIBLE_LIFETIME_SECS).then_some(unix)
}

/// A WinRT method returned a null object (`windows-core` reports it as an
/// error carrying `S_OK`).
fn is_null_result(e: &windows::core::Error) -> bool {
    e.code().is_ok()
}

/// `0x…` HRESULT plus the system message, if any.
fn failure_detail(e: &windows::core::Error) -> String {
    let message = e.message();
    let message = message.trim();
    let code = e.code().0 as u32;
    if message.is_empty() {
        format!("0x{code:08x}")
    } else {
        format!("0x{code:08x}: {message}")
    }
}

fn win_failure(what: &str, e: &windows::core::Error) -> BrokerError {
    BrokerError::Failed(format!("Windows broker {what} failed: {}", failure_detail(e)))
}

fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use base64::engine::general_purpose::URL_SAFE_NO_PAD;
    use base64::Engine as _;

    fn request() -> BrokerRequest {
        BrokerRequest {
            authority: "https://login.microsoftonline.com/contoso.onmicrosoft.com".into(),
            client_id: "client".into(),
            scopes: vec!["api://x/.default".into()],
            login_hint: None,
            account_id: None,
            force_refresh: false,
        }
    }

    fn success(token: &str, expires_on: Option<&str>) -> WamOutcome {
        WamOutcome {
            status: WebTokenRequestStatus::Success,
            token: Some(WamToken {
                token: token.into(),
                account: Some(BrokerAccount {
                    id: "id-1".into(),
                    username: "a@contoso.com".into(),
                }),
                expires_on: expires_on.map(str::to_owned),
            }),
            ..WamOutcome::default()
        }
    }

    fn failure(status: WebTokenRequestStatus, code: u32, message: &str) -> BrokerError {
        map_outcome(
            WamOutcome {
                status,
                error_code: code,
                error_message: message.into(),
                token: None,
            },
            0,
        )
        .unwrap_err()
    }

    #[test]
    fn authority_for_multi_tenant_is_organizations() {
        for a in [
            "https://login.microsoftonline.com/organizations",
            "https://login.microsoftonline.com/common/",
            "https://login.microsoftonline.com/Common",
        ] {
            assert_eq!(authority_for(a), "organizations", "{a}");
        }
        assert_eq!(
            authority_for("https://login.microsoftonline.com/contoso.onmicrosoft.com/"),
            "https://login.microsoftonline.com/contoso.onmicrosoft.com"
        );
        assert_eq!(
            authority_for("https://login.microsoftonline.com/consumers"),
            "https://login.microsoftonline.com/consumers"
        );
    }

    #[test]
    fn request_properties_include_hint_and_force_refresh_only_when_set() {
        let keys = |r: &BrokerRequest| {
            request_properties(r)
                .into_iter()
                .map(|(k, _)| k)
                .collect::<Vec<_>>()
        };
        let mut r = request();
        assert_eq!(keys(&r), ["wam_compat", "authority"]);
        assert_eq!(request_properties(&r)[1].1, r.authority);

        r.login_hint = Some(" a@contoso.com ".into());
        r.force_refresh = true;
        let props = request_properties(&r);
        assert!(props.contains(&("LoginHint", "a@contoso.com".to_owned())));
        assert!(props.contains(&("forcerefresh", "true".to_owned())));

        r.login_hint = Some("  ".into());
        assert!(!keys(&r).contains(&"LoginHint"));
    }

    const NOW: i64 = 1_800_000_000;

    #[test]
    fn success_uses_token_expires_on() {
        // Windows-epoch seconds, as the Entra WAM plugin reports them.
        let windows = (NOW + 3000 + WINDOWS_EPOCH_OFFSET_SECS).to_string();
        let token = map_outcome(success("opaque", Some(&windows)), NOW).unwrap();
        assert_eq!(token.access_token.expose(), "opaque");
        assert_eq!(token.expires_at_unix, NOW + 3000);
        assert_eq!(token.account.unwrap().username, "a@contoso.com");

        let unix = (NOW + 1200).to_string();
        assert_eq!(
            map_outcome(success("opaque", Some(&unix)), NOW)
                .unwrap()
                .expires_at_unix,
            NOW + 1200
        );
    }

    #[test]
    fn success_falls_back_to_jwt_exp_then_one_hour() {
        let payload = URL_SAFE_NO_PAD.encode(format!(r#"{{"exp":{}}}"#, NOW + 600));
        let jwt = format!("e30.{payload}.sig");
        let far_future = (NOW + 365 * 24 * 3600).to_string();
        for expires_on in [Some("garbage"), Some("0"), Some(far_future.as_str()), None] {
            assert_eq!(
                map_outcome(success(&jwt, expires_on), NOW)
                    .unwrap()
                    .expires_at_unix,
                NOW + 600,
                "{expires_on:?}"
            );
        }
        assert_eq!(
            map_outcome(success("opaque", None), NOW)
                .unwrap()
                .expires_at_unix,
            NOW + 3600
        );
    }

    #[test]
    fn success_without_token_fails() {
        let mut outcome = success("", None);
        assert!(matches!(
            map_outcome(outcome, 0),
            Err(BrokerError::Failed(_))
        ));
        outcome = WamOutcome {
            status: WebTokenRequestStatus::Success,
            ..WamOutcome::default()
        };
        assert!(matches!(
            map_outcome(outcome, 0),
            Err(BrokerError::Failed(_))
        ));
    }

    #[test]
    fn statuses_map_to_broker_errors() {
        assert_eq!(
            failure(
                WebTokenRequestStatus::UserInteractionRequired,
                0,
                "AADSTS50058: sign in"
            ),
            BrokerError::InteractionRequired("AADSTS50058: sign in".into())
        );
        assert!(matches!(
            failure(WebTokenRequestStatus::UserInteractionRequired, 0, ""),
            BrokerError::InteractionRequired(m) if !m.is_empty()
        ));
        assert_eq!(
            failure(WebTokenRequestStatus::UserCancel, 0, ""),
            BrokerError::Cancelled
        );
        assert!(matches!(
            failure(WebTokenRequestStatus::AccountProviderNotAvailable, 0, ""),
            BrokerError::Unavailable(_)
        ));
        assert_eq!(
            failure(
                WebTokenRequestStatus::ProviderError,
                0xcaa2000c,
                "AADSTS50011: redirect"
            ),
            BrokerError::Failed("WAM error 0xcaa2000c: AADSTS50011: redirect".into())
        );
        assert_eq!(
            failure(WebTokenRequestStatus(42), 7, " "),
            BrokerError::Failed("WAM error 0x00000007".into())
        );
    }

    #[test]
    fn provider_errors_that_need_a_sign_in_require_interaction() {
        for message in [
            "AADSTS65001: The user or administrator has not consented",
            "AADSTS50020: User account from identity provider does not exist in tenant",
            "AADSTS50076: Due to a configuration change you must use MFA",
            "AADSTS700082: The refresh token has expired due to inactivity",
            "AADSTS53000: Device is not in required device state",
            "error=interaction_required",
            "Consent_Required by the tenant",
            "invalid_grant: the grant has expired",
        ] {
            assert_eq!(
                failure(WebTokenRequestStatus::ProviderError, 0xcaa2000c, message),
                BrokerError::InteractionRequired(format!("WAM error 0xcaa2000c: {message}")),
                "{message}"
            );
        }
        for message in [
            "AADSTS53003: Access has been blocked by Conditional Access policies. invalid_grant",
            "AADSTS50011: The redirect URI does not match",
            "AADSTS700016: Application not found in the directory",
            "something else entirely",
            "",
        ] {
            assert!(
                matches!(
                    failure(WebTokenRequestStatus::ProviderError, 1, message),
                    BrokerError::Failed(_)
                ),
                "{message}"
            );
        }
    }

    #[test]
    fn aadsts_codes_are_parsed_as_numbers_only() {
        assert_eq!(parse_aadsts("AADSTS50020: User account 'x' ..."), Some(50020));
        assert_eq!(parse_aadsts("WAM error 0x1: (pii) AADSTS700082 expired"), Some(700082));
        assert_eq!(parse_aadsts("no code here"), None);
        assert_eq!(parse_aadsts("AADSTS: nothing"), None);
        assert_eq!(parse_aadsts("AADSTS99999999999999 overflow"), None);
    }

    #[test]
    fn hinted_account_matches_the_username_case_insensitively() {
        let names = ["a@contoso.com".to_owned(), "Ada@Contoso.com".to_owned()];
        assert_eq!(matching_account(&names, " ada@contoso.COM "), Some(1));
        assert_eq!(matching_account(&names, "bob@contoso.com"), None);
        assert_eq!(matching_account(&names, " "), None);
        assert_eq!(matching_account(&[], "ada@contoso.com"), None);
    }

    #[test]
    fn call_status_prefers_the_wam_status_then_the_error() {
        let with = |status| CallInfo { wam_status: Some(status), ..CallInfo::default() };
        let err = |e: BrokerError| -> Result<BrokerToken, BrokerError> { Err(e) };
        let failed = err(BrokerError::Failed("x".into()));
        assert_eq!(call_status(&with(WebTokenRequestStatus::Success), &failed), "success");
        assert_eq!(
            call_status(&with(WebTokenRequestStatus::AccountSwitch), &failed),
            "account_switch"
        );
        assert_eq!(call_status(&with(WebTokenRequestStatus::UserCancel), &failed), "user_cancel");
        assert_eq!(
            call_status(&with(WebTokenRequestStatus::UserInteractionRequired), &failed),
            "user_interaction_required"
        );
        assert_eq!(
            call_status(&with(WebTokenRequestStatus::AccountProviderNotAvailable), &failed),
            "account_provider_not_available"
        );
        assert_eq!(
            call_status(&with(WebTokenRequestStatus::ProviderError), &failed),
            "provider_error"
        );
        let none = CallInfo::default();
        assert_eq!(
            call_status(&none, &err(BrokerError::Timeout(Duration::from_secs(1)))),
            "timeout"
        );
        assert_eq!(
            call_status(&none, &err(BrokerError::Unavailable("x".into()))),
            "account_provider_not_available"
        );
        assert_eq!(
            call_status(&none, &err(BrokerError::InteractionRequired(NO_CONSOLE.into()))),
            "no_console"
        );
        assert_eq!(call_status(&none, &failed), "winrt_error");
    }

    #[test]
    fn read_codes_keep_only_numbers() {
        let mut info = CallInfo::default();
        info.note_response(
            WebTokenRequestStatus::ProviderError,
            0xcaa2_0005,
            "AADSTS50020: User account 'ada@contoso.com' does not exist",
        );
        assert_eq!(info.wam_status, Some(WebTokenRequestStatus::ProviderError));
        assert_eq!(info.provider_error_code, Some(0xcaa2_0005));
        assert_eq!(info.aadsts, Some(50020));
        let mut info = CallInfo::default();
        info.note_response(WebTokenRequestStatus::Success, 0, "");
        assert_eq!((info.provider_error_code, info.aadsts), (None, None));
    }

    #[test]
    fn null_results_are_told_apart_from_failures() {
        use windows::Win32::Foundation::E_FAIL;
        assert!(is_null_result(&windows::core::Error::empty()));
        let failure = windows::core::Error::from_hresult(E_FAIL);
        assert!(!is_null_result(&failure));
        assert!(matches!(
            win_failure("call", &failure),
            BrokerError::Failed(m) if m.starts_with("Windows broker call failed: 0x80004005")
        ));
    }

    #[test]
    fn keep_mta_alive_is_idempotent_across_threads() {
        keep_mta_alive().expect("first call");
        keep_mta_alive().expect("second call");
        std::thread::spawn(|| keep_mta_alive().expect("call on another thread"))
            .join()
            .expect("thread panicked");
        std::thread::spawn(|| ensure_mta().expect("ensure_mta keeps working"))
            .join()
            .expect("thread panicked");
    }

    #[test]
    fn debug_never_shows_the_token() {
        let outcome = success("super-secret-token", Some("1"));
        assert!(!format!("{outcome:?}").contains("super-secret-token"));
    }

    /// Regression: the WinRT factory `windows-core` caches process-wide must
    /// stay valid after the thread that first used it (and joined the MTA)
    /// exits. Before `keep_mta_alive`, the MTA was torn down with that
    /// thread, COM unloaded the WAM DLL and the second lookup crashed the
    /// process with an access violation.
    #[ignore = "talks to the real Windows broker"]
    #[test]
    fn real_wam_provider_lookup_survives_mta_thread_exit() {
        let lookup = || {
            std::thread::spawn(|| {
                ensure_mta().expect("join the MTA");
                let deadline = Instant::now() + SILENT_TIMEOUT;
                find_provider(
                    "https://login.microsoftonline.com/organizations",
                    deadline,
                    SILENT_TIMEOUT,
                )
                .map(|_| ())
            })
            .join()
            .expect("lookup thread panicked")
        };
        lookup().expect("first provider lookup");
        // COM unloads the WAM DLL roughly 30 s after the last MTA thread is
        // gone. Wait past that point (or until it happens) before the second
        // lookup, which then goes through the factory `windows-core` cached.
        let wam_dll = HSTRING::from("Windows.Security.Authentication.Web.Core.dll");
        for _ in 0..12 {
            std::thread::sleep(Duration::from_secs(5));
            // SAFETY: only queries whether a module is loaded.
            let loaded = unsafe {
                windows::Win32::System::LibraryLoader::GetModuleHandleW(&wam_dll)
            }
            .is_ok();
            assert!(loaded, "the WAM DLL was unloaded after its MTA thread exited");
        }
        lookup().expect("second provider lookup on a fresh thread");
    }

    /// Real WAM interactive acquisition (shows the Windows sign-in dialog).
    /// Run in a console window with the `CODA_WAM_TEST_*` variables and
    /// optionally `CODA_WAM_TEST_INTERACTIVE_SECS` (default 180) as the budget.
    #[ignore = "shows the Windows sign-in dialog; needs CODA_WAM_TEST_* variables"]
    #[tokio::test]
    async fn real_wam_interactive() {
        let (Ok(client_id), Ok(scope)) = (
            std::env::var("CODA_WAM_TEST_CLIENT_ID"),
            std::env::var("CODA_WAM_TEST_SCOPE"),
        ) else {
            println!("skipped: set CODA_WAM_TEST_CLIENT_ID and CODA_WAM_TEST_SCOPE");
            return;
        };
        let tenant =
            std::env::var("CODA_WAM_TEST_TENANT").unwrap_or_else(|_| "organizations".to_owned());
        let request = BrokerRequest {
            authority: format!("https://login.microsoftonline.com/{tenant}"),
            client_id,
            scopes: scope.split_whitespace().map(str::to_owned).collect(),
            login_hint: std::env::var("CODA_WAM_TEST_ACCOUNT").ok(),
            account_id: None,
            force_refresh: false,
        };
        let mut broker = WamBroker::new();
        if let Some(secs) = std::env::var("CODA_WAM_TEST_INTERACTIVE_SECS")
            .ok()
            .and_then(|s| s.parse().ok())
        {
            broker.interactive_budget = Duration::from_secs(secs);
        }
        println!("can_interact: {}", broker.can_interact());
        let started = Instant::now();
        match broker.acquire_interactive(&request).await {
            Ok(token) => println!(
                "interactive: success after {:?}, expires_in={} s",
                started.elapsed(),
                token.expires_at_unix - unix_now()
            ),
            Err(e) => println!("interactive: after {:?}: {}", started.elapsed(), variant(&e)),
        }
    }

    /// Real WAM silent acquisition. Run with
    /// `CODA_WAM_TEST_CLIENT_ID=… CODA_WAM_TEST_SCOPE=… cargo test -p coda-mcp real_wam -- --ignored --nocapture`.
    #[ignore = "needs a Windows account and CODA_WAM_TEST_* variables"]
    #[tokio::test]
    async fn real_wam_silent() {
        let (Ok(client_id), Ok(scope)) = (
            std::env::var("CODA_WAM_TEST_CLIENT_ID"),
            std::env::var("CODA_WAM_TEST_SCOPE"),
        ) else {
            println!("skipped: set CODA_WAM_TEST_CLIENT_ID and CODA_WAM_TEST_SCOPE");
            return;
        };
        let tenant =
            std::env::var("CODA_WAM_TEST_TENANT").unwrap_or_else(|_| "organizations".to_owned());
        let request = BrokerRequest {
            authority: format!("https://login.microsoftonline.com/{tenant}"),
            client_id,
            scopes: scope.split_whitespace().map(str::to_owned).collect(),
            login_hint: std::env::var("CODA_WAM_TEST_ACCOUNT").ok(),
            account_id: None,
            force_refresh: false,
        };
        let broker = WamBroker::new();
        println!("can_interact: {}", broker.can_interact());

        // Account enumeration: statuses, counts and whether the hint matches
        // — never the user names themselves. Also returns the first account
        // known to the app, to exercise the hint lookup end to end.
        let (authority, client, hint) = (
            request.authority.clone(),
            request.client_id.clone(),
            request.login_hint.clone(),
        );
        let app_account = tokio::task::spawn_blocking(move || {
            ensure_mta().expect("join the MTA");
            let deadline = Instant::now() + SILENT_TIMEOUT;
            let provider = match find_provider(&authority, deadline, SILENT_TIMEOUT) {
                Ok(p) => p,
                Err(e) => {
                    println!("provider lookup: {}", variant(&e));
                    return None;
                }
            };
            let mut app_account = None;
            for (label, scope) in [("by client id", Some(client.as_str())), ("all", None)] {
                match find_all_accounts(&provider, scope, deadline, SILENT_TIMEOUT) {
                    Ok((status, found)) => {
                        let names: Vec<String> = found
                            .iter()
                            .map(|a| a.UserName().map(|u| u.to_string()).unwrap_or_default())
                            .collect();
                        println!(
                            "FindAllAccounts {label}: status={} accounts={} hint_matches={}",
                            status.0,
                            found.len(),
                            hint.as_deref().is_some_and(|h| matching_account(&names, h).is_some())
                        );
                        if app_account.is_none() {
                            app_account = names.into_iter().find(|n| !n.is_empty());
                        }
                    }
                    Err(e) => println!("FindAllAccounts {label}: {}", variant(&e)),
                }
            }
            app_account
        })
        .await
        .expect("enumeration thread");

        let describe = |label: &str, result: &Result<BrokerToken, BrokerError>| match result {
            Ok(token) => println!(
                "{label}: success account_reported={} expires_in={} s jwt_exp_minus_expiry={}",
                token.account.is_some(),
                token.expires_at_unix - unix_now(),
                jwt_expiry_unix(token.access_token.expose())
                    .map_or("n/a".to_owned(), |exp| (exp - token.expires_at_unix)
                        .to_string()),
            ),
            Err(e) => println!("{label}: {}", variant(e)),
        };
        let first = broker.acquire_silent(&request).await;
        describe("silent (hint or default account)", &first);
        if request.login_hint.is_none() {
            if let Some(name) = app_account {
                let hinted = BrokerRequest { login_hint: Some(name), ..request.clone() };
                describe(
                    "silent, first app account as the hint",
                    &WamBroker::new().acquire_silent(&hinted).await,
                );
            }
        }
        if let Some(account) = first.ok().and_then(|t| t.account) {
            // Fresh broker: exercises FindAccountAsync + the WebAccount path.
            let request = BrokerRequest {
                account_id: Some(account.id),
                force_refresh: true,
                ..request
            };
            describe(
                "by account id, force refresh",
                &WamBroker::new().acquire_silent(&request).await,
            );
        }
    }

    /// A broker error without its message (which may name the account):
    /// the variant and any `AADSTS` number.
    fn variant(e: &BrokerError) -> String {
        let (name, message) = match e {
            BrokerError::InteractionRequired(m) => ("InteractionRequired", m.as_str()),
            BrokerError::Cancelled => ("Cancelled", ""),
            BrokerError::Unavailable(m) => ("Unavailable", m.as_str()),
            BrokerError::Timeout(_) => ("Timeout", ""),
            BrokerError::Failed(m) => ("Failed", m.as_str()),
        };
        format!("error {name} aadsts={:?}", parse_aadsts(message))
    }
}
