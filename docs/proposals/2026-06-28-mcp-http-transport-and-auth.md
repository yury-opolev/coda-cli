# Proposal: User-level MCP config, HTTP transport, and automatic OAuth for MCP servers

- **Date:** 2026-06-28
- **Status:** Draft / for review
- **Author:** design exploration (Coda)
- **Scope:** `Coda.Mcp`, `LlmAuth*`, `Coda.Tui` wiring

## 1. Summary

Coda today connects only to **stdio** MCP servers declared in a **project-level**
`.mcp.json`. This proposal adds three capabilities, in priority order:

1. **User-level MCP configuration** — a `~/.coda/.mcp.json` merged with the project file,
   so servers can be configured once per machine (mirroring how skills and `settings.json`
   already resolve user + project layers).
2. **HTTP (Streamable HTTP) transport** — connect to remote MCP servers, not just locally
   launched processes.
3. **Automatic OAuth for HTTP servers** — implement the MCP authorization flow (RFC 9728
   protected-resource metadata discovery → RFC 8414 / OIDC authorization-server metadata →
   client registration → OAuth 2.1 + PKCE with the RFC 8707 `resource` parameter), reusing
   the existing `LlmAuth` OAuth engine, loopback listener, and encrypted token store.

The three are sequenced deliberately: (1) is small and independent; (2) is a prerequisite
for (3); (3) is the largest piece and builds directly on `LlmAuth`.

## 2. Current state

| Concern | Today | File |
|---|---|---|
| Config source | Project `.mcp.json` only (`mcpServers` map) | `src/Coda.Mcp/McpConfig.cs` |
| Config shape | `{ command, args[], env{}, type? }`; non-stdio skipped | `src/Coda.Mcp/McpServerConfig.cs` |
| Transport | stdio process + newline-delimited JSON-RPC | `src/Coda.Mcp/McpStdioClient.cs`, `McpRpcConnection.cs` |
| Aggregation | `McpClientManager` owns clients, exposes `ITool`s | `src/Coda.Mcp/McpClientManager.cs` |
| Tool adapter | `McpTool` wraps `(client, serverName, toolInfo)` | `src/Coda.Mcp/McpTool.cs` |
| Wiring | `Program.cs` / `HeadlessRunner.cs` load + connect | `src/Coda.Tui/Program.cs:84` |

### Reusable assets already in the repo (`LlmAuth`)

The OAuth machinery needed for feature 3 **already exists** and is provider-agnostic:

| Asset | What it gives us | File |
|---|---|---|
| `Pkce` | RFC 7636 verifier / challenge / `state` | `src/LlmAuth/Pkce.cs` |
| `OAuth2PkceClient` | `BuildAuthorizeUrl(...)`, `PostTokenAsync(...)` | `src/LlmAuth/OAuth2PkceClient.cs` |
| `LoopbackRedirectListener` | localhost `/callback` capture of `code`/`state`/`error` | `src/LlmAuth/LoopbackRedirectListener.cs` |
| `ITokenStore` / `FileTokenStore` | AES-GCM encrypted creds under `~/.coda/credentials` | `src/LlmAuth/FileTokenStore.cs` |
| `SystemBrowser` | open the system browser to the authorize URL | `src/LlmAuth/SystemBrowser.cs` |
| `OAuthTokenResponse` | typed token endpoint response | `src/LlmAuth/OAuthTokenResponse.cs` |

The MCP auth flow is "just" a new discovery + registration front-end on top of these.

## 3. Goals & non-goals

**Goals**
- Configure MCP servers at user scope, merged with project scope.
- Connect to remote Streamable-HTTP MCP servers.
- Authenticate to HTTP servers automatically via the MCP OAuth flow with zero manual
  token copy-paste for servers that support discovery + dynamic registration.
- Reuse `LlmAuth`; do not fork a second OAuth implementation.
- Preserve current behavior: a failing/slow server is logged and skipped, never blocks startup.

**Non-goals (this proposal)**
- The legacy HTTP+SSE (two-endpoint) transport. We target **Streamable HTTP** only
  (the current spec transport); SSE is deprecated.
- Server-initiated requests / elicitation / sampling callbacks (already out of scope in Coda).
- Acting as an MCP **server** / resource server. Coda is a client only.
- OAuth **Client ID Metadata Documents** as the primary registration path (tracked as a
  future enhancement; see §6.3 and §11).
- Per-request step-up scope escalation UX beyond a single re-auth attempt (see §9).

## 4. Feature 1 — User-level MCP configuration

### Design
Mirror the skills/settings precedence model. `McpConfig.Load` gains a user layer:

- **User file:** `~/.coda/.mcp.json` (override root via existing `CODA_SETTINGS_DIR`, or a
  new `CODA_USER_MCP_DIR`, for parity/testability).
- **Project file:** `<workingDirectory>/.mcp.json` (unchanged).
- **Merge:** start with user servers, then overlay project servers **by name** (project wins),
  exactly as `SettingsLoader` overlays LSP servers (`SettingsLoader.cs:95`).

```
Load(workingDirectory, userMcpDir = null):
    user    = Parse(~/.coda/.mcp.json)         # may be empty
    project = Parse(<workingDirectory>/.mcp.json)
    merged  = user; foreach (name, cfg) in project: merged[name] = cfg
    return merged
```

Optionally also read `~/.claude.json`'s `mcpServers` block read-only (the way `SkillLoader`
reuses `~/.claude/skills`), gated behind an env opt-out. **Recommendation: defer** this to a
follow-up — `~/.claude.json` mixes many unrelated keys and its schema is not ours to track.

### Touch points
- `McpConfig.Load` signature gains `string? userMcpDir = null`.
- Callers in `Program.cs` / `HeadlessRunner.cs` pass nothing (defaults to `~/.coda`).
- Tests: a new case in `McpTests` for user+project merge precedence.

This feature is independently shippable and unblocks the rest.

## 5. Feature 2 — HTTP (Streamable HTTP) transport

### 5.1 Config schema extension
Extend `McpServerConfig` and the parser to accept HTTP servers:

```jsonc
{
  "mcpServers": {
    "filesystem": { "command": "npx", "args": ["-y", "@mcp/server-filesystem", "."] },
    "remote":     { "type": "http", "url": "https://mcp.example.com/mcp",
                    "headers": { "X-Tenant": "acme" } }
  }
}
```

- `McpConfig.Parse` currently **skips** any `type` not `null`/`"stdio"`. Change: route
  `type: "http"` (accept `"streamable-http"` as an alias) to an HTTP config branch that
  requires `url` and optionally accepts static `headers`.
- Make `McpServerConfig` a discriminated shape. **Recommendation:** introduce a small type
  hierarchy rather than overloading one record:
  - `McpServerConfig` (abstract/base or a `Kind` discriminator)
  - `McpStdioServerConfig(Command, Args, Env)`
  - `McpHttpServerConfig(Url, Headers, Auth?)` — `Auth` defined in §6.

### 5.2 Transport interface extraction
`McpClientManager`, `McpTool`, and the resource/prompt tools all currently bind to the
concrete `McpStdioClient`. Extract the client surface into an interface so HTTP and stdio
are interchangeable:

```csharp
public interface IMcpClient : IAsyncDisposable
{
    string ServerName { get; }
    Task<IReadOnlyList<McpToolInfo>> InitializeAndListToolsAsync(CancellationToken ct = default);
    Task<(string Text, bool IsError)> CallToolAsync(string toolName, JsonElement args, CancellationToken ct = default);
    Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct = default);
    Task<string> ReadResourceAsync(string uri, CancellationToken ct = default);
    Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct = default);
    Task<string> GetPromptAsync(string name, JsonNode? args, CancellationToken ct = default);
}
```

- `McpStdioClient` implements `IMcpClient` (it already has every member; this is a near-zero
  refactor).
- `McpClientManager.Clients`, `McpTool`, `ListMcpResourcesTool`, etc. switch to `IMcpClient`.
  The JSON-RPC correlation in `McpRpcConnection` is transport-agnostic and is **reused as-is**.

### 5.3 `McpHttpClient` (new)
Implements `IMcpClient` over **Streamable HTTP**:
- One `HttpClient` per server (shared handler from a factory).
- **Requests:** `POST <url>` with `Content-Type: application/json`, `Accept: application/json,
  text/event-stream`, body = a single JSON-RPC request. The server may answer with either a
  JSON object (`application/json`) or an SSE stream (`text/event-stream`) carrying the
  response; parse both. Notifications (`notifications/initialized`) are `POST` with no
  response expected (HTTP 202).
- **Session:** capture `Mcp-Session-Id` from the `initialize` response headers and echo it on
  every subsequent request, per the Streamable HTTP spec.
- **Protocol version:** send `MCP-Protocol-Version: 2025-06-18` header on post-initialize
  requests.
- **Auth hook:** an injected `IMcpAuthProvider` (default: none) supplies the
  `Authorization: Bearer …` header and is asked to handle `401`/`403` (§6).

Reuse `McpRpcConnection`'s request/response *shapes* (the `JsonObject` builders and
`McpToolInfo.ParseList` / `FormatCallResult` parsers) so stdio and HTTP produce identical
`McpToolInfo`/results. The line-framed read loop is stdio-specific and is **not** used by HTTP.

### 5.4 Connection in `McpClientManager`
`ConnectAllAsync` switches on config kind to construct `McpStdioClient` or `McpHttpClient`,
then calls the same `InitializeAndListToolsAsync`. Timeout, skip-on-failure, and tool
aggregation are unchanged.

## 6. Feature 3 — Automatic OAuth for HTTP MCP servers

This is the MCP authorization spec (draft 2025-11 / 2025-06-18) implemented client-side.
It is **only** engaged for HTTP servers, and only when the server demands it (a `401`).

### 6.1 Trigger: 401 → discovery
1. `McpHttpClient` sends the `initialize` request with no token.
2. On **HTTP 401** with a `WWW-Authenticate: Bearer …` header, parse the
   `resource_metadata` parameter (a URL) and any `scope` parameter.
3. If `resource_metadata` is absent, fall back to
   `GET <origin>/.well-known/oauth-protected-resource` (RFC 9728).

### 6.2 Discovery chain
1. **Protected Resource Metadata (RFC 9728):** fetch the metadata doc → read
   `authorization_servers[]` and `scopes_supported`. Record the canonical resource URI
   (the server `url`, normalized: lowercase scheme/host, no fragment, no trailing slash).
2. **Authorization Server Metadata:** for the chosen AS issuer, try in order:
   - `GET <issuer>/.well-known/oauth-authorization-server` (RFC 8414), then
   - `GET <issuer>/.well-known/openid-configuration` (OIDC Discovery).
   Extract `authorization_endpoint`, `token_endpoint`, `registration_endpoint`,
   `issuer`, `scopes_supported`, `authorization_response_iss_parameter_supported`.

### 6.3 Client registration
Selection priority (spec §"Client Registration"):
1. **Pre-registered `client_id`** from config (`auth.clientId`) — use directly. Simplest,
   always works; recommended default for servers that document a client ID.
2. **Dynamic Client Registration (RFC 7591):** if `registration_endpoint` is present and no
   `client_id` configured, `POST` a registration document
   (`redirect_uris: ["http://localhost:<port>/callback"]`, `grant_types:
   ["authorization_code","refresh_token"]`, `token_endpoint_auth_method: "none"`,
   `application_type: "native"`) and persist the returned `client_id` (+ secret if any).
3. **Client ID Metadata Documents** — future enhancement (§11).

DCR results are cached in the token store keyed by issuer so we register once per AS.

### 6.4 Authorization (OAuth 2.1 + PKCE + resource)
Reuse `LlmAuth` end to end:
- `Pkce.GenerateCodeVerifier/Challenge/State`.
- `LoopbackRedirectListener` for `http://localhost:<port>/callback`.
- `OAuth2PkceClient.BuildAuthorizeUrl` with: `response_type=code`, `client_id`,
  `redirect_uri`, `code_challenge`, `code_challenge_method=S256`, `state`,
  **`resource=<canonical server URI>`** (RFC 8707 — MUST be sent), and `scope` selected per
  the spec strategy (challenge `scope` → else `scopes_supported` → else omit; add
  `offline_access` when advertised, to obtain a refresh token).
- `SystemBrowser.Open(authorizeUrl)`; wait on the listener for the callback.
- **`iss` validation (RFC 9207):** compare the returned `iss` to the recorded issuer with
  exact string comparison before exchanging the code; reject on mismatch.
- `OAuth2PkceClient.PostTokenAsync(tokenEndpoint, { grant_type:"authorization_code", code,
  redirect_uri, client_id, code_verifier, resource })` → `OAuthTokenResponse`.

### 6.5 Token storage & refresh
- Persist `{ access_token, refresh_token, expires_at, scopes, issuer, client_id }` via
  `FileTokenStore` under a stable key, e.g. `mcp:<serverName>` (or keyed by canonical
  resource URI to share tokens across configs pointing at the same server).
- Before each request, if the access token is expired/near-expiry and a refresh token
  exists, refresh via `PostTokenAsync({ grant_type:"refresh_token", refresh_token,
  client_id, resource })`. Persist the rotated tokens.
- On a `401` **after** presenting a token (token revoked), discard and restart the flow once.

### 6.6 New components (`LlmAuth.Mcp` or `Coda.Mcp/Auth`)
- `IMcpAuthProvider` — `Task<string?> GetAccessTokenAsync(...)`,
  `Task<bool> HandleChallengeAsync(HttpResponseMessage, ...)` (returns true if it obtained a
  token and the request should be retried).
- `McpOAuthProvider` — orchestrates §6.1–6.5; depends on `HttpClient`, `ITokenStore`,
  `OAuth2PkceClient`, `LoopbackRedirectListener` factory, `SystemBrowser`, and an
  `IDeviceCodeLoginProvider`-style console prompter for the "open this URL" message.
- `ProtectedResourceMetadata`, `AuthorizationServerMetadata`, `ClientRegistration` — small
  records + fetchers (RFC 9728 / 8414 / 7591).
- `WwwAuthenticateChallenge.Parse(...)` — header parser for `resource_metadata`, `scope`,
  `error`.

### 6.7 Where auth is configured
```jsonc
"remote": {
  "type": "http",
  "url": "https://mcp.example.com/mcp",
  "auth": {
    "mode": "oauth",            // "oauth" | "none" | "bearer"
    "clientId": "abc123",       // optional: pre-registered client
    "scopes": ["files:read"]    // optional: override discovered scopes
  }
}
```
- `mode: "oauth"` (default when a 401 is seen) → the flow above.
- `mode: "bearer"` with a `token`/env reference → static header, no discovery.
- `mode: "none"` → never attach auth.

## 7. Component & data-flow overview

```
McpConfig.Load(user + project) ─► Dictionary<name, McpServerConfig{Stdio|Http}>
        │
        ▼
McpClientManager.ConnectAllAsync
   ├─ Stdio cfg ─► McpStdioClient ─┐
   └─ Http  cfg ─► McpHttpClient ──┤ : IMcpClient
                       │           │
                       │   InitializeAndListToolsAsync / CallTool / resources / prompts
                       ▼
                 McpOAuthProvider (HTTP only, lazy on 401)
                   401 ─► WWW-Authenticate ─► RFC9728 metadata ─► RFC8414/OIDC metadata
                       ─► (config clientId | RFC7591 DCR) ─► PKCE+resource authorize
                       ─► loopback callback ─► iss check ─► token exchange ─► FileTokenStore
        │
        ▼
   tools aggregated as ITool ─► agent (unchanged: McpTool, ListMcpResourcesTool, …)
```

## 8. Configuration precedence (final picture)

| Layer | MCP servers | Skills (existing) | Settings (existing) |
|---|---|---|---|
| Claude (read-only) | *(deferred)* | `~/.claude/skills` | — |
| User | `~/.coda/.mcp.json` | `~/.coda/skills` | `~/.coda/settings.json` |
| Project | `.mcp.json` | `.coda/skills` | `.coda/settings.json` |
| Precedence | project overrides user by name | project wins | project overrides user |

Consistent with what users already know from skills and settings.

## 9. Error handling

- **Discovery failures** (no metadata, unreachable AS): log, skip the server, continue —
  same contract as today's connect-failure path (`McpClientManager.cs:64`).
- **`iss` mismatch / `state` mismatch:** abort the flow with a clear message; never exchange
  the code (reuse `ClaudeAiLoginFlow`'s state-mismatch pattern).
- **User cancels browser login / timeout:** `LoopbackRedirectListener` raises
  `LoginCanceledException`; treat as connect failure, skip server.
- **`403 insufficient_scope`:** parse `scope`, union with previously requested scopes, retry
  the authorize flow **once**; on repeated failure, surface a permanent auth error. (Full
  step-up loop with retry tracking is a follow-up.)
- **Refresh failure:** fall back to full re-auth once, then skip.
- **Headless / non-interactive runs** (`HeadlessRunner`): no browser. If a server needs
  interactive OAuth and no valid stored token exists, log "run `coda` interactively to
  authorize `<server>`" and skip. Stored tokens from a prior interactive login still work.

## 10. Testing strategy

- **`McpConfig`:** user+project merge precedence; HTTP config parsing (`type`, `url`,
  `headers`, `auth`); invalid HTTP config (missing `url`) skipped.
- **`McpHttpClient`:** drive against an in-process `HttpMessageHandler` stub — initialize,
  session-id echo, tools/list, tools/call, both `application/json` and SSE response framing.
- **OAuth flow:** unit-test each step against stubbed handlers —
  `WwwAuthenticateChallenge.Parse`, RFC 9728 / 8414 / OIDC fetchers, DCR, authorize-URL
  construction (assert `resource`, `code_challenge_method=S256`, `state`), `iss` validation
  table (RFC 9207), token persistence + refresh. Reuse the existing `LlmAuth.Tests` patterns
  (`AuthorizeUrlTests`, `TokenExchangeTests`).
- **`LoopbackRedirectListener`** already has coverage; the MCP provider test injects a fake
  redirect result rather than a real browser.
- **Interface refactor:** existing `McpTests` continue to pass against `IMcpClient`.

## 11. Phasing / milestones

1. **M1 — User-level config.** `McpConfig.Load` user+project merge. Small, independent. _(Ship first.)_
2. **M2 — Transport interface.** Extract `IMcpClient`; `McpStdioClient` implements it; managers/tools switch to the interface. Pure refactor, no behavior change.
3. **M3 — HTTP transport (no auth).** `McpHttpServerConfig`, `McpHttpClient`, Streamable HTTP + session id. Unlocks unauthenticated/bearer-token remote servers.
4. **M4 — OAuth discovery + PKCE.** `McpOAuthProvider` with RFC 9728/8414/OIDC discovery, pre-registered `client_id`, PKCE + `resource`, loopback, `iss` check, token store + refresh.
5. **M5 — Dynamic Client Registration (RFC 7591).** Auto-register when no `client_id` configured.
6. **M6 (future).** Client ID Metadata Documents; full step-up scope escalation; optional `~/.claude.json` import.

## 12. Open decisions (recommendations inline)

1. **Token store key** — `mcp:<serverName>` vs canonical resource URI. **Recommend resource
   URI** so the same remote server shares one token across project/user configs.
2. **`McpServerConfig` shape** — discriminator field vs type hierarchy. **Recommend a small
   type hierarchy** (`McpStdioServerConfig` / `McpHttpServerConfig`) for type-safe handling.
3. **New assembly vs folder** — put MCP-auth types in `Coda.Mcp/Auth` or a new
   `LlmAuth.Mcp`. **Recommend `Coda.Mcp/Auth`** to avoid a new project; it depends on
   `LlmAuth` which is already referenced.
4. **`~/.claude.json` import** — **Recommend deferring** (M6); schema churn risk.
5. **Headless behavior** — confirm "skip with guidance" is acceptable vs. failing the run.
   **Recommend skip-with-guidance** (matches today's resilient-startup contract).

## 13. Risks

- **Spec is still draft** (2025-11) — endpoint/header details may shift. Mitigation: isolate
  all spec-specific logic in the discovery/auth components behind `IMcpAuthProvider`.
- **SSE response framing** in Streamable HTTP adds parsing surface. Mitigation: support the
  single-JSON-object response first; add SSE parsing with focused tests.
- **Interface refactor (M2)** touches several files. Mitigation: mechanical, well-covered by
  existing `McpTests`; land it isolated.

## 14. Summary of new/changed files

- `McpConfig.cs` — user+project merge; HTTP/auth parsing. *(changed)*
- `McpServerConfig.cs` → `McpStdioServerConfig.cs` + `McpHttpServerConfig.cs`. *(changed/new)*
- `IMcpClient.cs` — extracted interface. *(new)*
- `McpStdioClient.cs` — implements `IMcpClient`. *(changed)*
- `McpHttpClient.cs` — Streamable HTTP client. *(new)*
- `Auth/IMcpAuthProvider.cs`, `Auth/McpOAuthProvider.cs`, `Auth/WwwAuthenticateChallenge.cs`,
  `Auth/ProtectedResourceMetadata.cs`, `Auth/AuthorizationServerMetadata.cs`,
  `Auth/ClientRegistration.cs`. *(new)*
- `McpClientManager.cs`, `McpTool.cs`, `ListMcp*Tool.cs` — use `IMcpClient`. *(changed)*
- `Program.cs` / `HeadlessRunner.cs` — pass user MCP dir; provide auth dependencies. *(changed)*
- `tests/Engine.Tests/Mcp*` and `tests/LlmAuth.Tests/*` — new coverage. *(new)*
