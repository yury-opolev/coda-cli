# Entra broker (WAM) auth mode for HTTP MCP servers — design

Source request: "REQUEST CODA broker" (2026-11-27). Decisions below were made
autonomously; the rationale is recorded with each one.

## Goal

Let an HTTP MCP server that advertises Microsoft Entra ID (RFC 9728 PRM with
`authorization_servers: ["https://login.microsoftonline.com/<tenant>/v2.0"]`)
be used with single sign-on through the Windows Web Account Manager (WAM), and
make the existing browser flow work against Entra when the broker is not
available. Existing `none` / `bearer` / `oauth` behaviour for non-Entra servers
is unchanged.

## Configuration

```jsonc
"my-entra-server": {
  "type": "http",
  "url": "https://mcp.example.com/mcp/v1",
  "auth": {
    "mode": "entra-broker",
    "clientId": "<public-client-app-id>",        // required
    "scopes": ["api://<api-app-id>/.default"],   // optional; default = PRM scopes_supported
    "tenant": "<tenant-id-or-domain>",           // optional; default = PRM authorization_servers[0]
    "account": "user@contoso.com",               // optional; default = Windows default account
    "redirectUri": "http://localhost/callback"   // optional; browser fallback only
  }
}
```

- `mode` accepts `entra-broker` (also `entra_broker`, `entrabroker`,
  case-insensitive). Unknown modes keep falling back to `oauth` as before.
- `clientId` missing in `entra-broker` mode → the server fails to connect with
  "auth.mode 'entra-broker' requires auth.clientId (Entra has no dynamic client
  registration) …".
- `redirectUri` is also honoured by `oauth` mode (it was hard-coded before).
- `McpAuthConfig` gains `tenant`, `account`, `redirect_uri` and derives
  `Default` (= the previous `oauth_default()`).

## Architecture (all in `coda-mcp/src/auth/`)

| Unit | Purpose |
|---|---|
| `entra.rs` | Pure Entra helpers: recognise an Entra v2.0 authority, extract host/tenant, build the authority for a tenant override, derive scopes (config → challenge → PRM), `ensure_offline_access`, unverified JWT `exp` reader. |
| `broker.rs` | `TokenBroker` trait (`is_available`, `acquire_silent`, `acquire_interactive`), `BrokerRequest`, `BrokerToken`, `BrokerError` (`InteractionRequired`, `Cancelled`, `Unavailable`, `Timeout`, `Failed`), `with_timeout` helper, and the process-wide `BrokerTokenCache`. |
| `wam.rs` (`cfg(windows)`) | `WamBroker`: `WebAuthenticationCoreManager` + `WebTokenRequest` through the `windows` crate. |
| `entra_broker.rs` | `EntraBrokerProvider: McpAuthProvider` — discovery, silent-first acquisition, interactive escalation, caching, 401 re-acquire, fallback delegation, status, login/logout. |
| `oauth.rs` | Browser flow with the Entra fixes. |
| `coda-auth/src/loopback.rs` | `LoopbackListener::bind_template(template)` for configurable redirect URIs. |

### Token broker trait

The provider only talks to `Arc<dyn TokenBroker>`, so tests inject a fake.
`default_broker()` returns `WamBroker` on Windows and an always-unavailable
broker elsewhere. `CODA_DISABLE_WAM=1` forces the unavailable broker on Windows
too (escape hatch and fallback testing).

### Shared token cache

`BrokerTokenCache` is process-wide (`OnceLock`), injectable for tests. Key =
`canonical resource URI` + `\n` + `account key` (configured account lower-cased,
or `"<default>"`). Each entry holds the token, its expiry, the account the
broker returned, a single-flight `Arc<tokio::sync::Mutex>`, and an
`interaction_declined` flag. Being process-wide means a restarted MCP
connection (new provider instance) reuses the token and the single-flight lock,
so two sessions in one process never show two broker dialogs. Across
processes the broker itself is the shared cache (silent calls are parallel-safe).

The account the broker picked is persisted (non-secret: account id + username)
in the credential store under `mcp-broker-account:<resource>#<account key>`
(so two configured accounts never collide) when a store is available, so restarts silently reuse a non-default account picked
interactively. Tokens are not persisted — the broker owns refresh.

### Acquisition flow

1. **Target resolution.** If `tenant` and `scopes` are both configured, the
   provider can acquire before any 401. Otherwise it waits for the first 401,
   reads `resource_metadata` from `WWW-Authenticate` (or the well-known path —
   reuses `resolve_resource_metadata_url` + `McpAuthMetadataClient`), takes
   `authorization_servers[0]` as authority and `scopes_supported` as default
   scopes. Config `tenant` overrides the tenant segment; config `scopes`
   override PRM scopes. No scopes anywhere → `<resource-origin>/.default`.
2. **Silent first** (`acquire_silent`, timeout 30 s).
3. **Interactive only when required.** On `InteractionRequired` and when
   interaction is allowed (a console window exists and the key is not marked
   declined), call `acquire_interactive` (timeout 180 s), parented to the
   console window (`GetAncestor(GetConsoleWindow(), GA_ROOTOWNER)`, which also
   works under Windows Terminal). Cancel / failure marks the key declined so
   the dialog is not re-shown on every request; `/mcp login <name>` clears it.
   The acquisition (silent and interactive) runs in a spawned task that owns
   the key's flight guard (`lock_owned`) and records the outcome in the shared
   cache; callers await its `JoinHandle`. A caller dropped mid-dialog (tool
   timeout, cancellation) therefore neither loses the token nor the declined
   flag, and no second dialog opens while the first is up.
4. **Caching.** `get_access_token` returns the cached token while it is valid
   for more than 5 minutes; inside that window it re-acquires silently and keeps
   serving the old token if silent refresh fails but the token has not expired.
   A request inside the window never waits for a refresh another task is
   already running — it gets the old token. After a failed acquisition,
   automatic refreshes are skipped for a 60 s cool-down (the unexpired token is
   served, or no token at once), so a failing or hung broker does not slow
   every request; `/mcp login` and the 401 path ignore the cool-down.
5. **401 with a token.** `handle_unauthorized` drops the cached token and
   re-acquires once with `BrokerRequest::force_refresh` set, so the broker
   bypasses its own cache instead of returning the rejected token (the HTTP
   client already retries exactly once). Simultaneous 401s re-acquire once: a
   handler that finds the cached token's value differs from the token it saw
   rejected just retries (an identical token is re-acquired with
   `force_refresh`).
6. **No hangs.** Every broker call is bounded, as are credential-store
   reads/writes (10 s) and protected-resource metadata discovery (30 s); the WAM implementation also polls
   the WinRT async op against the deadline and cancels it on expiry. Errors are
   actionable, e.g. "Windows broker sign-in for 'https://…' needs interaction
   but no console window is available. Run `/mcp login <name>` from an
   interactive coda session."
7. **Connect timeout.** An `entra-broker` server's HTTP connect budget is
   raised to `connect_timeout + interactive timeout` so the first-run consent
   dialog can complete during startup; the silent path is unaffected.

### WAM request details

- Provider: `FindAccountProviderWithAuthorityAsync("https://login.microsoft.com", authority)`
  where authority is `organizations` or the tenant authority URL.
- `WebTokenRequest(provider, scopes joined by space, clientId)` with prompt type
  `Default` (the broker shows its own picker/consent only when needed), properties: `wam_compat=2.0`
  (not `api-version=2.0`: together with `wam_compat` Entra rejects the request with AADSTS901002),
  `authority=<authority URL>`, `LoginHint=<account>` when configured.
- Silent: with a known `WebAccount` (from the cache / persisted id via
  `FindAccountAsync`) → `GetTokenSilentlyWithWebAccountAsync`; otherwise
  `GetTokenSilentlyAsync` (Windows default account).
- Interactive: `IWebAuthenticationCoreManagerInterop::RequestTokenForWindowAsync(hwnd, request)`.
- Status mapping: `Success` → token; `UserInteractionRequired` →
  `InteractionRequired`; `UserCancel` → `Cancelled`;
  `AccountProviderNotAvailable` → `Unavailable`; `ProviderError` → `Failed`
  with the provider error code/message (AADSTS text). Provider lookup: only a
  null provider result is `Unavailable`; other WinRT/RPC errors are `Failed`.
  `Unavailable` errors suggest `CODA_DISABLE_WAM=1` or `auth.mode 'oauth'`
  (`/mcp login` cannot help).
- Expiry: response property `TokenExpiresOn` (seconds since 1601-01-01, the
  Windows epoch, as observed from the Entra WAM plugin; plain unix seconds are
  accepted too) → JWT `exp` → now + 1 h.
- COM: each call runs on a `spawn_blocking` thread that initialises the MTA.

### Brokered redirect URI

Public client registrations used with the broker must have the redirect URI
`ms-appx-web://microsoft.aad.brokerplugin/<clientId>`. Documented in
`docs/mcp-auth.md`.

## Browser fallback (broker unavailable) and Entra fixes in `oauth.rs`

When `TokenBroker::is_available()` is false, `EntraBrokerProvider` delegates to
an `McpOAuthProvider` built from the same config. Entra fixes apply whenever the
authorization server is an Entra v2.0 authority (so `oauth` mode against Entra
also works; non-Entra servers are untouched):

- The RFC 8707 `resource` parameter is omitted on authorize, token and refresh
  requests. A flow counts as Entra v2.0 when either the advertised issuer or
  the discovery URL is an Entra v2.0 authority.
- Scopes come from `entra::derive_scopes` (config → challenge → PRM →
  `<resource origin>/.default`); the AS `scopes_supported` is never used, since
  that would yield a Microsoft Graph token. `offline_access` is always appended.
- A configured `tenant` rewrites the advertised Entra issuer; it must be a
  single segment of `[A-Za-z0-9.-]` (else "Invalid auth.tenant …").
- The stored token records the URL its metadata was discovered from
  (`discovery_issuer`), and refresh rediscovers from it: `common`/
  `organizations` advertise a templated `{tenantid}` issuer that cannot be
  rediscovered. Tokens stored without the field fall back to `issuer`.
- `redirectUri` template: `http://localhost/callback` → bound on a random port,
  port substituted; an explicit port is bound as given (`:80` is rejected, as
  URL parsing erases it); path is honoured by the
  listener; only `http` + loopback hosts are accepted (invalid template → clear
  error).
- Discovery already falls back to `<issuer>/.well-known/openid-configuration`;
  covered by a test with an Entra-shaped document. Callback `iss` stays
  required only when `authorization_response_iss_parameter_supported` is
  advertised (Entra does not send it) — extracted into `validate_callback_iss`
  and tested.

The engine keeps running the OAuth provider headless (no browser during tool
calls, unchanged); `/mcp login <name>` runs it interactively.

## Nice-to-haves

- **Auth status.** `McpAuthProvider` gains `status()` (default `None`),
  `login()` and `logout()` (default "not supported"). The manager keeps each
  HTTP server's provider in its slot (also when the connection failed).
  `mcp/list` adds an optional `auth` object per server:
  `{ mode, account?, expiresAtUnix?, lastError? }` — never a token. The `/mcp`
  browser shows it.
- **`/mcp login <name>` / `/mcp logout <name>`.** New RPC `mcp/auth`
  (`McpAuthParams { server, action: login|logout }` →
  `McpAuthActionResult { ok, message }`). Login: entra-broker → silent then
  interactive (clears the declined flag); oauth → interactive browser flow;
  afterwards a not-connected server is restarted. Logout: entra-broker → drop
  cached token, forget the persisted account and suppress silent auto-sign-in
  until the next login (the Windows account itself is not signed out); oauth →
  delete the stored token.
- **`coda auth status`** is *not* extended: the broker token cache lives in the
  engine process, so only the engine (`/mcp`) can report it truthfully, and
  `coda-boot` is deliberately free of MCP/runtime dependencies.

## Error handling

All failures become `McpAuthResult`/status `last_error` strings with a next
step; secrets never appear in logs, `Debug`, status or `mcp/list`.

## Testing

- Unit: config parsing (mode aliases, fields, missing clientId), Entra
  authority/scope derivation, offline_access, JWT exp, cache keying,
  interactive escalation, declined flag, timeout path, resource omission,
  redirect template parsing/binding, iss validation, OIDC fallback.
- Integration (fake broker + local HTTP fake MCP server returning 401 + PRM):
  silent success; silent → interactive; broker unavailable → fallback (OAuth
  provider runs, headless message); 401 after token → exactly one re-acquire;
  status/login/logout through the manager.
- `cfg(not(windows))`: the unavailable broker and fallback are compiled and
  tested on every platform; `cargo check --target x86_64-unknown-linux-gnu -p coda-mcp`
  when the toolchain allows.
- Real WAM: an `#[ignore]` test gated by `CODA_WAM_TEST_CLIENT_ID` /
  `CODA_WAM_TEST_SCOPE` env vars.
