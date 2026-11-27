# Entra broker MCP auth — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an `entra-broker` MCP auth mode (Windows WAM single sign-on) with a browser fallback that works against Entra, plus auth status and `/mcp login|logout`.

**Architecture:** A `TokenBroker` trait (WAM on Windows, unavailable elsewhere, fakes in tests) behind an `EntraBrokerProvider` implementing the existing `McpAuthProvider`. A process-wide token cache keyed by resource + account. Entra fixes live in the existing `McpOAuthProvider`. The manager keeps auth providers per server slot to expose status and login/logout over a new `mcp/auth` RPC.

**Tech Stack:** Rust 2021, tokio, reqwest, async-trait, `windows` 0.62 (WinRT `Windows.Security.Authentication.Web.Core`), schemars (proto schema feature).

Spec: `docs/superpowers/specs/2026-11-27-entra-broker-mcp-auth-design.md` (read it first).

Conventions for every task:
- Work only in `C:\Users\yurio\Documents\github\coda-cli-broker` (branch `feat/entra-broker`). Run cargo from `rust\`.
- Zero warnings. TDD: write the failing test, see it fail, implement, see it pass.
- Commit at the end of each task; message ends with a blank line and
  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
- Never commit `rust/crates/coda-proto/src/bin/emit_schemas.rs`.
- Secrets (`Secret<String>`) never appear in `Debug`, logs, status or DTOs.

---

### Task 1: Config, mode, Entra helpers

**Files:**
- Modify: `rust/crates/coda-mcp/src/auth/types.rs` (mode, config, status type)
- Modify: `rust/crates/coda-mcp/src/config.rs` (`parse_auth_config`)
- Create: `rust/crates/coda-mcp/src/auth/entra.rs`
- Modify: `rust/crates/coda-mcp/src/auth/mod.rs`, `rust/crates/coda-mcp/Cargo.toml` (`base64 = "0.22"`)
- Fix literal constructions of `McpAuthConfig` in `oauth.rs` tests and `rust/crates/coda-serve/src/mcp.rs` tests (use `..Default::default()`).

Interfaces:

```rust
// types.rs
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum McpAuthMode { None, Bearer, #[default] OAuth, EntraBroker }
impl McpAuthMode { pub fn label(self) -> &'static str } // "none" | "bearer" | "oauth" | "entra-broker"

#[derive(Debug, Clone, Default)]
pub struct McpAuthConfig {
    pub mode: McpAuthMode,
    pub client_id: Option<String>,
    pub scopes: Vec<String>,
    pub bearer_token: Option<Secret<String>>,
    pub tenant: Option<String>,
    pub account: Option<String>,
    pub redirect_uri: Option<String>,
}
// oauth_default() stays and returns Self::default().

/// Secret-free auth state for status surfaces.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpAuthStatus {
    pub mode: McpAuthMode,
    pub account: Option<String>,
    pub expires_at_unix: Option<i64>,
    pub last_error: Option<String>,
}
```

`parse_auth_config`: mode strings `entra-broker`, `entra_broker`, `entrabroker` (case-insensitive) → `EntraBroker`; read `tenant`, `account`, `redirectUri` (non-empty strings only).

```rust
// entra.rs
pub fn is_entra_host(host: &str) -> bool; // login.microsoftonline.com, login.microsoftonline.us,
    // login.chinacloudapi.cn, login.partner.microsoftonline.cn, login.microsoftonline.de,
    // login.windows.net, login.microsoft.com, login.windows-ppe.net, login.sovcloud-identity.fr …(case-insensitive)
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EntraAuthority { pub host: String, pub tenant: String }
impl EntraAuthority {
    /// `https://<entra-host>/<tenant>[/v2.0][/]` → Some. Anything else (non-https,
    /// non-Entra host, missing tenant, v1 paths with extra segments) → None.
    pub fn parse(issuer: &str) -> Option<Self>;
    pub fn with_tenant(&self, tenant: &str) -> Self;
    pub fn authority_url(&self) -> String; // https://host/tenant
    pub fn issuer_v2(&self) -> String;     // https://host/tenant/v2.0
}
/// True for an Entra v2.0 issuer (`EntraAuthority::parse` succeeds AND path ends with `/v2.0`).
pub fn is_entra_v2_issuer(issuer: &str) -> bool;
/// Scope priority: configured → challenge `scope` → PRM `scopes_supported` →
/// `<resource origin>/.default`. Never adds offline_access (broker does not need it).
pub fn derive_scopes(config: &[String], challenge_scope: Option<&str>, prm_scopes: &[String], resource: &str) -> Vec<String>;
pub fn ensure_offline_access(scopes: Vec<String>) -> Vec<String>;
/// Unverified read of the JWT `exp` claim (base64url payload). None on any error.
pub fn jwt_expiry_unix(token: &str) -> Option<i64>;
```

- [ ] Step 1: Tests in `config.rs` tests: entra-broker parses with all fields; aliases; `redirectUri` read for oauth mode; unknown mode still OAuth; existing tests unchanged.
- [ ] Step 2: Tests in `entra.rs`: parse `https://login.microsoftonline.com/<guid>/v2.0` (tenant = guid); `.../organizations/v2.0`; trailing slash; rejects `https://example.com/t/v2.0`, `http://login.microsoftonline.com/t/v2.0`, missing tenant; `is_entra_v2_issuer` false for v1 issuer `https://sts.windows.net/<t>/` and for non-Entra; `with_tenant` + `authority_url`/`issuer_v2`; `derive_scopes` priority chain and `.default` fallback (`https://mcp.example.com/mcp/v1` → `https://mcp.example.com/.default`); `ensure_offline_access` idempotent; `jwt_expiry_unix` on a hand-built `header.payload.sig` with `{"exp":1700000000}` and on garbage.
- [ ] Step 3: Implement; `cargo test -p coda-mcp` green; `cargo test -p coda-serve` compiles (literal fixes).
- [ ] Step 4: Commit `feat(mcp): entra-broker auth config and Entra helpers`.

### Task 2: Configurable loopback redirect URI (coda-auth)

**Files:** Modify `rust/crates/coda-auth/src/loopback.rs`.

```rust
impl LoopbackListener {
    /// Bind for a redirect-URI template such as `http://localhost/callback` or
    /// `http://127.0.0.1:8400/auth/cb`. Only `http` with host `localhost`,
    /// `127.0.0.1` or `[::1]` is accepted; no query/fragment/userinfo.
    /// No port → random free port, substituted into the URI. Explicit port → bound as given.
    /// Always binds the 127.0.0.1 (or ::1 for [::1]) loopback interface.
    pub async fn bind_template(template: &str) -> Result<Self, AuthError>;
}
```
Add private fields `host: String` (as written in the template) and `path: String` (default `/callback` for `bind()`); `redirect_uri()` → `http://{host}:{port}{path}`; `read_request` matches the configured path instead of the literal `/callback`. `bind()` keeps producing exactly `http://localhost:<port>/callback`.

- [ ] Step 1: Tests: `bind_template("http://localhost/callback")` → uri `http://localhost:<port>/callback` with port != 0; custom path `/auth/cb` is served (send a real GET to `/auth/cb?code=c&state=s` and get the result; `/callback` gets 404 for that listener); explicit port honoured (pick a free port by binding then dropping); rejects `https://localhost/cb`, `http://example.com/cb`, `http://localhost/cb?x=1`, `not a url`; `bind()` unchanged (existing tests).
- [ ] Step 2: Implement; `cargo test -p coda-auth loopback`.
- [ ] Step 3: Commit `feat(auth): loopback listener for redirect URI templates`.

### Task 3: Entra fixes in the browser OAuth flow

**Files:** Modify `rust/crates/coda-mcp/src/auth/oauth.rs`, `rust/crates/coda-mcp/src/auth/metadata_client.rs` (tests only).

Changes:
- Helper `fn entra_issuer(&self, as_meta_issuer: &str) -> bool` = `entra::is_entra_v2_issuer`. When true: omit `resource` from authorize, code exchange and refresh forms; `ensure_offline_access(scopes)`.
- Pure helpers (unit-testable): `pub(crate) fn authorize_params(...)`/`token_form(...)` OR a `fn resource_param(issuer, canonical) -> Option<(String,String)>` used in all three places. Use the latter (smallest change).
- Tenant override: when `config.tenant` is set and `authorization_servers[0]` parses as `EntraAuthority`, use `with_tenant(t).issuer_v2()` as the issuer candidate.
- Redirect: `match &self.config.redirect_uri { Some(t) => LoopbackListener::bind_template(t), None => LoopbackListener::bind() }`; a template error returns a failure "Invalid auth.redirectUri …".
- Extract `pub(crate) fn validate_callback_iss(received: Option<&str>, issuer: &str, advertised: bool) -> Result<(), String>` keeping current semantics (present → must equal; absent → error only when advertised).
- `McpOAuthProvider::force_reauthorize` and `McpOAuthReauthenticator` accept `McpAuthMode::EntraBroker` too (fallback path).
- Add `last_error: std::sync::Mutex<Option<String>>` + `expires_at: std::sync::Mutex<Option<i64>>` updated in `failure()` / `persist_token` / `load_token`; implement the new trait methods from Task 4 later (Task 4 adds them; here just keep the fields).

- [ ] Step 1: Tests: `resource_param` → None for `https://login.microsoftonline.com/t/v2.0`, Some for `https://as.example.com`; `select_scopes` + Entra → offline_access present even if AS meta does not list it (test via a small pure function `scopes_for_issuer(scopes, issuer)`); `validate_callback_iss` 4 cases; metadata client: local HTTP server (tokio TcpListener writing raw HTTP responses) returns 404 for `/.well-known/oauth-authorization-server` and an Entra-shaped OIDC doc for `/.well-known/openid-configuration` → parsed meta with `issuer_parameter_supported == false`.
- [ ] Step 2: Implement; `cargo test -p coda-mcp`.
- [ ] Step 3: Commit `fix(mcp): make the browser OAuth flow work with Entra v2.0`.

### Task 4: Broker trait, shared cache, EntraBrokerProvider (+ fake-broker tests)

**Files:**
- Create: `rust/crates/coda-mcp/src/auth/broker.rs`, `rust/crates/coda-mcp/src/auth/entra_broker.rs`
- Modify: `rust/crates/coda-mcp/src/auth/provider.rs` (trait extension), `oauth.rs` (implement status/login/logout), `mod.rs`, `lib.rs` exports.

Trait extension (`provider.rs`):
```rust
#[async_trait]
pub trait McpAuthProvider: Send + Sync {
    async fn get_access_token(&self) -> Option<Secret<String>>;
    async fn handle_unauthorized(&self, www_authenticate: Option<&str>) -> bool;
    fn status(&self) -> Option<McpAuthStatus> { None }
    async fn login(&self) -> McpAuthResult { unsupported() }
    async fn logout(&self) -> McpAuthResult { unsupported() }
}
```
`StaticBearerAuthProvider::status` → `Some(McpAuthStatus{mode: Bearer, ..})`. `McpOAuthProvider`: status from its fields (mode = config.mode); `login` = `force_reauthorize`; `logout` = delete stored token, clear expiry.

`broker.rs`:
```rust
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BrokerRequest { pub authority: String, pub client_id: String, pub scopes: Vec<String>,
    pub login_hint: Option<String>, pub account_id: Option<String>,
    /// Bypass the broker's token cache; true only when re-acquiring after a 401.
    pub force_refresh: bool }
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BrokerAccount { pub id: String, pub username: String }
#[derive(Debug, Clone)]
pub struct BrokerToken { pub access_token: Secret<String>, pub expires_at_unix: i64, pub account: Option<BrokerAccount> }
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum BrokerError {
    #[error("interaction required: {0}")] InteractionRequired(String),
    #[error("sign-in was cancelled")] Cancelled,
    #[error("Windows broker unavailable: {0}")] Unavailable(String),
    #[error("Windows broker did not respond within {0:?}")] Timeout(Duration),
    #[error("{0}")] Failed(String),
}
#[async_trait]
pub trait TokenBroker: Send + Sync {
    fn is_available(&self) -> bool;
    /// Whether an interactive prompt can be parented to a window right now.
    fn can_interact(&self) -> bool;
    async fn acquire_silent(&self, request: &BrokerRequest) -> Result<BrokerToken, BrokerError>;
    async fn acquire_interactive(&self, request: &BrokerRequest) -> Result<BrokerToken, BrokerError>;
}
pub struct UnavailableBroker { pub reason: String } // is_available false; acquire_* → Unavailable
pub fn default_broker() -> Arc<dyn TokenBroker>; // CODA_DISABLE_WAM truthy → Unavailable;
    // cfg(windows) → Arc::new(crate::auth::wam::WamBroker::new()) (Task 5; until then UnavailableBroker
    // behind cfg(windows) too, Task 5 swaps it); cfg(not(windows)) → Unavailable("not Windows")
pub async fn with_timeout<F: Future<Output = Result<BrokerToken, BrokerError>>>(limit: Duration, f: F) -> Result<BrokerToken, BrokerError>;
pub fn cache_key(resource: &str, account: Option<&str>) -> String; // "{resource}\n{account lowercased | <default>}"
pub fn account_store_key(resource: &str, account: Option<&str>) -> String; // "mcp-broker-account:{resource}#{account lowercased | <default>}"
pub struct BrokerTokenCache { entries: std::sync::Mutex<HashMap<String, Arc<CacheEntry>>> }
impl BrokerTokenCache { pub fn new() -> Self; pub fn global() -> Arc<Self>; pub fn entry(&self, key: &str) -> Arc<CacheEntry>; }
pub struct CacheEntry { pub flight: tokio::sync::Mutex<()>, pub state: std::sync::Mutex<CachedState> }
#[derive(Default)] pub struct CachedState { pub token: Option<BrokerToken>, pub account: Option<BrokerAccount>,
    pub interaction_declined: bool, pub signed_out: bool, pub last_error: Option<String>,
    pub last_failure_at: Option<tokio::time::Instant>, // starts the automatic-refresh cool-down
    pub generation: u64 }                              // incremented whenever a new token is cached
```

`entra_broker.rs`:
```rust
pub const SILENT_TIMEOUT: Duration = Duration::from_secs(30);
pub const INTERACTIVE_TIMEOUT: Duration = Duration::from_secs(180);
pub const REFRESH_SKEW_SECS: i64 = 300;
pub const FAILURE_COOL_DOWN: Duration = Duration::from_secs(60);
pub const STORE_TIMEOUT: Duration = Duration::from_secs(10);     // credential-store get/set
pub const DISCOVERY_TIMEOUT: Duration = Duration::from_secs(30); // PRM discovery

pub struct EntraBrokerOptions {
    pub server_name: String,
    pub broker: Arc<dyn TokenBroker>,
    pub cache: Arc<BrokerTokenCache>,
    pub store: Option<Arc<dyn CredentialStore>>,
    pub allow_interactive: bool,           // engine: true (still requires broker.can_interact())
    pub silent_timeout: Duration,
    pub interactive_timeout: Duration,
    pub open_browser: Option<Arc<dyn Fn(&str) -> Result<(), String> + Send + Sync>>, // fallback login
    pub log: Option<Arc<dyn Fn(&str) + Send + Sync>>,
}
impl EntraBrokerOptions { pub fn defaults(server_name: impl Into<String>) -> Self } // global cache, default_broker(), no store

pub struct EntraBrokerProvider { /* http, metadata, canonical_resource, config, opts,
    target: tokio::sync::Mutex<Option<Target{authority:String, scopes:Vec<String>}>>,
    fallback: Option<McpOAuthProvider> (built when !broker.is_available(); store = opts.store or InMemoryStore) */ }
impl EntraBrokerProvider {
    /// Err when clientId is missing (message names auth.clientId and explains Entra has no DCR).
    pub fn new(http: reqwest::Client, url: &reqwest::Url, config: McpAuthConfig, opts: EntraBrokerOptions) -> Result<Self, String>;
}
```
Behaviour (all through `McpAuthProvider`):
- Broker unavailable → every method delegates to `fallback` (status mode stays `EntraBroker`, last_error from fallback). On a 401 only the headless provider's "sign in interactively" error is replaced by the `/mcp login <name>` hint; more specific errors (discovery failure, invalid `redirectUri`) are kept.
- Initial target: if `config.tenant` and non-empty `config.scopes` → `authority = EntraAuthority{host:"login.microsoftonline.com", tenant}.authority_url()`, scopes = config scopes; else None until a 401.
- `resolve_target(challenge)`: PRM via `resolve_resource_metadata_url` + `McpAuthMetadataClient`; `authorization_servers[0]` must parse as `EntraAuthority` (else error "… does not advertise a Microsoft Entra authorization server; use auth.mode 'oauth'"); tenant override; scopes via `derive_scopes(config, challenge.scope, prm.scopes_supported, canonical_resource)`.
- Request: `BrokerRequest{authority, client_id, scopes, login_hint: config.account, account_id: cached/persisted account id, force_refresh: trigger == Unauthorized}`. Persisted account: store key `account_store_key(canonical_resource, config.account)` JSON `{"id","username","signedOut":bool}`. Store reads/writes are bounded by `STORE_TIMEOUT` and PRM discovery by `DISCOVERY_TIMEOUT` (clear error naming the limit).
- `acquire_locked(trigger: Automatic | Unauthorized | User)`, caller holds `entry.flight`: for `Automatic` re-check the cache after locking (another task may have filled it) and return the last error without a broker call while in the cool-down; if `signed_out && !user_initiated` → error "Signed out of … Run `/mcp login <name>`"; silent with `silent_timeout`; on `InteractionRequired`: if `user_initiated || (allow_interactive && !declined)` and `broker.can_interact()` → interactive with `interactive_timeout`; failure/cancel/timeout of interactive sets `interaction_declined = true` (unless user_initiated success). Success → store token, clear declined/signed_out/last_error, persist account. Every failure sets `last_error` (actionable text, includes `/mcp login <server_name>` hint); every failure after asking the broker also sets `last_failure_at`.
- `get_access_token`: cached token with `expires_at - now > REFRESH_SKEW_SECS` → it; no target or within `FAILURE_COOL_DOWN` of a failure → the old token if not yet expired, else None (no broker call); inside the refresh window `try_lock` the flight and return the old token immediately when a refresh is already running; without a usable token wait for the flight; acquire (`Automatic`); on failure return the old token if not yet expired, else None.
- `handle_unauthorized(www)`: record `generation`; parse challenge; resolve target when unknown; take the flight; if the generation changed and a token is cached (a concurrent 401 already re-acquired) → true; else drop the cached token (401 with token ⇒ invalid) and acquire once with `Unauthorized` (`force_refresh`, ignores the cool-down); true iff a token is now cached.
- `status`: mode EntraBroker, account username, expiry, last_error.
- `login`: if no target try `resolve_target(None)`; under the flight load the persisted account, clear declined & signed_out and acquire with `User` (ignores the cool-down); result.
- `logout`: clear token, set signed_out, persist `signedOut:true`; message notes the Windows account stays signed in.

- [ ] Step 1: Unit tests (fake broker in the test module: scripted `VecDeque<Result<BrokerToken,BrokerError>>` per method + call counters + `can_interact` flag + optional delay): cache keying (case-insensitive account, default); `with_timeout` returns `Timeout` (use `tokio::time::pause`); silent success → no interactive call; InteractionRequired → interactive called once; interactive cancel → declined, second `handle_unauthorized` does NOT call interactive again; `login()` after decline calls interactive; `can_interact=false` → no interactive, last_error mentions `/mcp login`; token near expiry triggers silent refresh, old token served when refresh fails; 401 with token → exactly one silent re-acquire; logout → `get_access_token` None and no broker call; missing clientId → `new` Err; tenant+scopes configured → token without any 401/HTTP; concurrent `get_access_token` x8 → broker silent called once (single-flight).
- [ ] Step 2: Integration tests in `rust/crates/coda-mcp/tests/entra_broker.rs` (or the crate's test module if helpers are private): a tiny tokio HTTP fake MCP server (raw TCP, HTTP/1.1, `Connection: close`) that returns 401 + `WWW-Authenticate: Bearer resource_metadata="http://127.0.0.1:<p>/.well-known/oauth-protected-resource"` unless `Authorization: Bearer good`, serves PRM `{"resource":..., "authorization_servers":["https://login.microsoftonline.com/tid/v2.0"], "scopes_supported":["api://x/.default"]}`, and answers JSON-RPC `initialize`/`tools/list`. Drive it through `McpHttpClient::connect` (crate-internal → put the test in `entra_broker.rs` `#[cfg(test)]` or expose a `pub(crate)` path) with the provider: (a) silent success → connected, broker saw authority `https://login.microsoftonline.com/tid` and scopes `["api://x/.default"]`; (b) silent InteractionRequired → interactive success → connected; (c) broker unavailable → fallback OAuth provider is used: connect fails with an auth error and the log/status says to run `/mcp login`; no browser opened (inject `open_browser` that panics); (d) server rejects first token `bad` → provider re-acquires once and gets `good` → connected; broker silent count == 2. Note: `check_ssrf` must allow 127.0.0.1 (it does for loopback http; verify, else use the existing test escape hatch in `ssrf.rs`).
- [ ] Step 3: Implement; `cargo test -p coda-mcp`; zero warnings.
- [ ] Step 4: Commit `feat(mcp): entra-broker auth provider with broker trait and shared cache`.

### Task 5: WAM broker (Windows)

**Files:** Create `rust/crates/coda-mcp/src/auth/wam.rs` (`#![cfg(windows)]` via `#[cfg(windows)] mod wam;`), modify `Cargo.toml`:
```toml
[target.'cfg(windows)'.dependencies]
windows = { version = "0.62", features = ["Security_Authentication_Web_Core", "Security_Credentials",
  "Win32_Foundation", "Win32_System_Console", "Win32_System_WinRT", "Win32_UI_WindowsAndMessaging"] }
windows-future = "0.3"   # IAsyncOperation / AsyncStatus
```
(Minimal set for 0.62, verified by building: `Foundation`/`Foundation_Collections` are not needed.)

Implementation outline:
- `WamBroker { accounts: std::sync::Mutex<HashMap<String, WebAccount>> }` (WebAccount is agile/Send; if not `Send`, store ids only and re-find with `FindAccountAsync`).
- Each op runs in `tokio::task::spawn_blocking`, calls `RoInitialize(RO_INIT_MULTITHREADED)` (ignore `RPC_E_CHANGED_MODE`/`S_FALSE`), and waits for the `IAsyncOperation` by polling `Status()` every 25 ms until the deadline; on deadline `Cancel()` and return `Timeout`. The deadline is computed before `spawn_blocking`, so time spent queued for a blocking thread counts against the budget. No inner `with_timeout` belt: the provider already wraps every broker call in `with_timeout`. On `AsyncStatus::Error`, `GetResults()` supplies the failing HRESULT and its message.
- Provider: `WebAuthenticationCoreManager::FindAccountProviderWithAuthorityAsync("https://login.microsoft.com", authority)` where authority is `organizations` when the tenant is `organizations`/`common`, else the full authority URL. Null provider → `Unavailable("no Microsoft account provider found for authority …")`.
- Request: `WebTokenRequest::CreateWithPromptType(provider, scopes.join(" "), client_id, prompt)` (`Default` for silent; interactive uses `Default` unless no account is known → `ForceAuthentication` is NOT used; keep `Default` — the broker shows its picker when needed). Properties: `wam_compat`=`2.0`, `authority`=authority URL, `LoginHint`=hint when set. Do NOT send `api-version` (with `wam_compat` Entra rejects the request with AADSTS901002).
- Silent: account known (cache by `account_id`, or `FindAccountAsync(provider, id)`) → `GetTokenSilentlyWithWebAccountAsync`; else `GetTokenSilentlyAsync`. Only a null `FindAccountAsync` result (account removed) falls back to the default account; any other lookup error is `Failed`, so a transient error never switches (and re-persists) the account. When `request.force_refresh` is set (the provider sets it only when a 401 rejected a token it had attached), ask WAM to bypass its token cache (request property `forcerefresh`=`true`, as MSAL's WAM broker does; verify against MSAL) so a token the server rejected with 401 is not returned again.
- Interactive: `HWND` = `GetAncestor(GetConsoleWindow(), GA_ROOTOWNER)` (fallback `GetConsoleWindow()`); null → `InteractionRequired("no console window …")`. `windows::core::factory::<WebAuthenticationCoreManager, IWebAuthenticationCoreManagerInterop>()?.RequestTokenForWindowAsync::<_, IAsyncOperation<WebTokenRequestResult>>(hwnd, &request)`.
- Map `WebTokenRequestStatus`: Success or AccountSwitch → token from `ResponseData().GetAt(0)` (`Token()`, `WebAccount()` → id/username, `Properties().Lookup("TokenExpiresOn")` = seconds since the Windows epoch (1601-01-01; subtract 11644473600, plain unix seconds also accepted, implausible values ignored) → else `jwt_expiry_unix` → else now+3600); UserInteractionRequired → InteractionRequired(provider error message if any); UserCancel → Cancelled; AccountProviderNotAvailable → Unavailable; ProviderError/other → Failed(`"{code:#x}: {message}"` from `ResponseError()`).
- `is_available`: true (actual availability is discovered per call → `Unavailable` error makes the provider report it); `can_interact`: console window non-null.
- `default_broker()` on Windows returns `WamBroker`.
- Ignored real test: `#[ignore] #[tokio::test] async fn real_wam_silent()` reading `CODA_WAM_TEST_CLIENT_ID`, `CODA_WAM_TEST_SCOPE`, optional `CODA_WAM_TEST_TENANT` (default `organizations`); prints only success/expiry/username, never the token.
- Unit tests (Windows only, no network): status mapping function `map_status(...)` is pure → test it; `authority_for(tenant)`.
- [ ] Step 1: tests for pure helpers; Step 2: implement; Step 3: `cargo build -p coda-mcp`, `cargo test -p coda-mcp`, try `cargo test -p coda-mcp real_wam -- --ignored` only if env vars set; Step 4: `cargo check -p coda-mcp --target x86_64-unknown-linux-gnu` (if it fails only because of a missing C cross-toolchain in dependencies, record that and instead verify with `cargo check` + a `#[cfg(not(windows))]`-equivalent test of `UnavailableBroker` + fallback that already runs on Windows); Step 5: commit `feat(mcp): Windows WAM token broker`.

### Task 6: Manager wiring, status, login/logout

**Files:** `rust/crates/coda-mcp/src/manager.rs`, `rust/crates/coda-mcp/src/lib.rs`.

- `ServerSlot.auth: Option<Arc<dyn McpAuthProvider>>` (kept on failure too; `record_connected/record_failed` take it).
- `fn build_http_auth(config: &McpHttpConnectable, store: Option<&Arc<dyn CredentialStore>>, interactive: bool) -> Result<Option<Arc<dyn McpAuthProvider>>, McpError>`: None/Bearer/OAuth exactly as today (OAuth `interactive` flag passed through); EntraBroker → `EntraBrokerProvider::new(reqwest::Client::new(), &url, auth.clone(), EntraBrokerOptions { store: store.cloned(), allow_interactive: true, ..EntraBrokerOptions::defaults(&name) })`; `new` Err → `McpError::Http(msg)`.
- `connect_one_http(config, connect_timeout, tool_timeout, auth)`; for EntraBroker the connect timeout is `connect_timeout + INTERACTIVE_TIMEOUT`. `restart_server` builds the provider the same way.
- `pub struct McpServerAuthStatus { pub name: String, pub status: McpAuthStatus }` and `pub async fn auth_status(&self) -> Vec<McpServerAuthStatus>` (sorted, from slot providers' `status()`).
- `pub async fn auth_login(&self, name: &str) -> Result<String, String>`: unknown → `unknown_server`; stdio → "… is a stdio server; it has no sign-in"; build `build_http_auth(cfg, store, true)`; none/bearer → explanatory Err; `login()`; failure → Err(error); success → replace slot.auth with the new provider only if not connected, then if not connected `restart_server(name)` and append its summary; Ok("Signed in to MCP server '<name>'. …").
- `pub async fn auth_logout(&self, name)`: uses the slot provider (or a fresh one) `logout()`.
- Tests (in manager tests): `auth_status` reports `bearer` for a bearer server built via `connect_http_many` against an unreachable URL (slot exists after failure); `auth_login` on unknown/stdio errors; entra-broker missing clientId → connect error text mentions `auth.clientId`.
- Commit `feat(mcp): per-server auth status and login/logout in the manager`.

### Task 7: Protocol + serve (`mcp/list` auth, `mcp/auth` RPC)

**Files:** `rust/crates/coda-proto/src/mcp.rs`, `messages.rs` (`MCP_AUTH = "mcp/auth"`), `schema.rs`, `schema/catalog.rs`, `docs/protocol/schemas/*.json` + `docs/protocol/catalog.json` (regenerate with a local, uncommitted `src/bin/emit_schemas.rs` copied from `C:\Users\yurio\Documents\github\coda-cli\rust\crates\coda-proto\src\bin\emit_schemas.rs`; run `cargo run -p coda-proto --features schema --bin emit_schemas`; delete or leave untracked — never commit), `docs/protocol/catalog.md` if it is generated/checked; `rust/crates/coda-serve/src/{dispatch.rs,host.rs,mcp_list.rs,capabilities.rs}`.

```rust
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct McpAuthStatusDto {
    /// "none" | "bearer" | "oauth" | "entra-broker"
    pub mode: String,
    #[serde(default, skip_serializing_if = "Option::is_none")] pub account: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")] pub expires_at_unix: Option<i64>,
    #[serde(default, skip_serializing_if = "Option::is_none")] pub last_error: Option<String>,
}
// McpServerDto: #[serde(default, skip_serializing_if = "Option::is_none")] pub auth: Option<McpAuthStatusDto>
#[derive(..)] #[serde(rename_all = "camelCase")] pub enum McpAuthAction { Login, Logout }
pub struct McpAuthParams { pub server: String, pub action: McpAuthAction }
pub struct McpAuthActionResult { pub ok: bool, pub message: String }
```
- Update the allow-list test (`the_dto_never_grows_a_value_carrying_field`) to include `auth`; add a test that `McpAuthStatusDto` keys are exactly `mode/account/expiresAtUnix/lastError`.
- `mcp_list::build` gains `auth: &[McpServerAuthStatus]`; the `auth` dto is attached by server name for non-shadowed entries; `last_error` passed through `sanitise_error`.
- Backend trait `async fn mcp_auth(&self, params: Value) -> Result<Value, RpcError>` in all implementations (`dispatch.rs` has three plus `host.rs`); route `"mcp/auth"`; no manager → error "MCP is not running in this engine"; result `McpAuthActionResult`. Capabilities entry `("mcp.auth", MCP_AUTH)`. Catalog `rpc(m::MCP_AUTH, Some("McpAuthParams"), Some("McpAuthActionResult"), S, S)` (copy the neighbouring `S` stability args) and register both schemas in `schema.rs`.
- Tests: mcp_list attaches auth; dispatch routes `mcp/auth` (follow existing `mcp/list` dispatch tests); proto schema tests pass: `cargo test -p coda-proto --features schema --lib`.
- Commit `feat(proto,serve): MCP auth status in mcp/list and mcp/auth login/logout`.

### Task 8: TUI `/mcp login|logout` and auth display

**Files:** `rust/crates/coda-tui/src/commands.rs` (mcp args `"[login|logout <server>]"`, summary mentions sign-in), `rust/crates/coda-tui/src/app/slash/mod.rs` (arm `"mcp"` with args), `rust/crates/coda-tui/src/api/mod.rs` (`mcp_auth(connection, server, action)`), `rust/crates/coda-tui/src/browsers.rs` (`mcp_from_engine` shows `auth: entra-broker · user@x · expires 12:34 · error …`), tests next to existing ones. Bare-`/mcp` interception must keep working (check `local/mcp.rs` / README note). Parsing: `/mcp login <name>`; anything else with args → usage warning `Usage: /mcp [login|logout <server>]`. Long-running: login can take up to ~3 min; use the connection request without the short `bounded` timeout (check how `self.ask` bounds requests; use an explicit 200 s bound).
- Commit `feat(tui): /mcp login and logout, show MCP auth status`.

### Task 9: Docs

**Files:** Create `docs/mcp-auth.md` (modes table; entra-broker config; behaviour; brokered redirect URI `ms-appx-web://microsoft.aad.brokerplugin/<clientId>` and how to add it in the app registration (Authentication → Add a platform → Mobile and desktop applications); scopes `/.default`; browser fallback and its Entra rules, `redirectUri`; `CODA_DISABLE_WAM`; `/mcp login|logout`; troubleshooting: interaction required with no console, AADSTS errors, timeouts). Modify `README.md` MCP section: short paragraph + link. Commit `docs: MCP authentication modes including entra-broker`.
