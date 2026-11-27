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
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use async_trait::async_trait;
use coda_auth::secret::Secret;
use windows::core::{RuntimeType, HSTRING};
use windows::Security::Authentication::Web::Core::{
    WebAuthenticationCoreManager, WebTokenRequest, WebTokenRequestPromptType,
    WebTokenRequestResult, WebTokenRequestStatus,
};
use windows::Security::Credentials::{WebAccount, WebAccountProvider};
use windows::Win32::Foundation::{HWND, RPC_E_CHANGED_MODE};
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

    async fn run<F>(&self, budget: Duration, op: F) -> Result<BrokerToken, BrokerError>
    where
        F: FnOnce(Instant) -> Result<BrokerToken, BrokerError> + Send + 'static,
    {
        let deadline = Instant::now() + budget;
        tokio::task::spawn_blocking(move || {
            ensure_mta()?;
            op(deadline)
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
        self.run(budget, move |deadline| {
            silent(&request, &accounts, deadline, budget)
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
        self.run(budget, move |deadline| {
            interactive(&request, &accounts, deadline, budget)
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
) -> Result<BrokerToken, BrokerError> {
    let provider = find_provider(&request.authority, deadline, budget)?;
    let token_request = build_request(&provider, request)?;
    let account = match request.account_id.as_deref() {
        Some(id) => find_account(&provider, id, accounts, deadline, budget)?,
        None => None,
    };
    let op = match &account {
        Some(account) => WebAuthenticationCoreManager::GetTokenSilentlyWithWebAccountAsync(
            &token_request,
            account,
        ),
        None => WebAuthenticationCoreManager::GetTokenSilentlyAsync(&token_request),
    }
    .map_err(|e| win_failure("silent token request", &e))?;
    let result = wait(&op, deadline, budget)?;
    finish(&result, accounts)
}

fn interactive(
    request: &BrokerRequest,
    accounts: &AccountCache,
    deadline: Instant,
    budget: Duration,
) -> Result<BrokerToken, BrokerError> {
    let Some(hwnd) = parent_window() else {
        return Err(BrokerError::InteractionRequired(NO_CONSOLE.to_owned()));
    };
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
    finish(&result, accounts)
}

/// Join the WinRT multi-threaded apartment once per (blocking pool) thread.
fn ensure_mta() -> Result<(), BrokerError> {
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
    if let Some(hint) = request
        .login_hint
        .as_deref()
        .map(str::trim)
        .filter(|h| !h.is_empty())
    {
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
) -> Result<BrokerToken, BrokerError> {
    let outcome = read_outcome(result, accounts)?;
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
        _ => Err(BrokerError::Failed(format!(
            "WAM error 0x{error_code:08x}: {message}"
        ))),
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
                "AADSTS65001: consent"
            ),
            BrokerError::Failed("WAM error 0xcaa2000c: AADSTS65001: consent".into())
        );
        assert_eq!(
            failure(WebTokenRequestStatus(42), 7, " "),
            BrokerError::Failed("WAM error 0x00000007".into())
        );
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
    fn debug_never_shows_the_token() {
        let outcome = success("super-secret-token", Some("1"));
        assert!(!format!("{outcome:?}").contains("super-secret-token"));
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
        let describe = |label: &str, result: &Result<BrokerToken, BrokerError>| match result {
            Ok(token) => println!(
                "{label}: success username={:?} expires_in={} s jwt_exp_minus_expiry={}",
                token.account.as_ref().map(|a| a.username.as_str()),
                token.expires_at_unix - unix_now(),
                jwt_expiry_unix(token.access_token.expose())
                    .map_or("n/a".to_owned(), |exp| (exp - token.expires_at_unix)
                        .to_string()),
            ),
            Err(e) => println!("{label}: error {e:?}"),
        };
        let first = broker.acquire_silent(&request).await;
        describe("default account", &first);
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
}
