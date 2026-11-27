# MCP server authentication

HTTP MCP servers (`"type": "http"` in `.mcp.json`) authenticate through an `auth` block.
Stdio servers never use it. This page covers every mode, with the most detail on
`entra-broker`: Microsoft Entra ID tokens from the Windows account broker (WAM), so you
get single sign-on with the account you already use in Windows.

## Modes

| `auth.mode` | What Coda sends | Sign-in | Use it for |
|---|---|---|---|
| `none` | No `Authorization` header. | — | Public servers, or servers behind your own network auth. |
| `bearer` | `Authorization: Bearer <token>` from `auth.token`. | — (change the token in the config) | Servers that issue static API tokens. |
| `oauth` (default) | A token from the MCP OAuth flow. | Your browser, on the first 401 in the TUI or with `/mcp login <name>`. | Any OAuth 2.1 MCP server, including Entra-protected servers without the broker. |
| `entra-broker` | A Microsoft Entra token from the OS account broker. | Silent with your Windows account; the broker's own dialog only when Entra needs it. | Entra-protected servers on Windows. Falls back to the browser elsewhere. |

The mode is case-insensitive; `entra-broker` is also accepted as `entra_broker` and
`entrabroker`. An unknown mode, or no `auth` block at all, means `oauth`.

```json
{ "mcpServers": {
  "public":  { "type": "http", "url": "https://mcp.example.com/mcp", "auth": { "mode": "none" } },
  "tokened": { "type": "http", "url": "https://api.example.com/mcp",
               "auth": { "mode": "bearer", "token": "coda-secret:store/example-token" } },
  "oauthed": { "type": "http", "url": "https://mcp.example.com/mcp",
               "auth": { "mode": "oauth", "clientId": "optional-preregistered-id", "scopes": ["files:read"] } },
  "entra":   { "type": "http", "url": "https://mcp.contoso.com/mcp",
               "auth": { "mode": "entra-broker", "clientId": "00000000-0000-0000-0000-000000000000" } }
} }
```

`auth.token` is resolved like every other secret value: `coda-secret:…` from the encrypted
credential store, `${ENV_VAR}` from the environment, anything else literally. The `oauth`
flow itself (RFC 9728 / RFC 8414 discovery, dynamic client registration, PKCE, stored and
refreshed tokens) is described in the README's
[MCP servers](../README.md#mcp-servers) section.

## `entra-broker` configuration

```jsonc
"contoso-docs": {
  "type": "http",
  "url": "https://mcp.contoso.com/mcp",
  "auth": {
    "mode": "entra-broker",
    "clientId": "<public-client-app-id>",          // required
    "scopes": ["api://<api-app-id>/.default"],     // optional
    "tenant": "contoso.onmicrosoft.com",           // optional
    "account": "user@contoso.com",                 // optional
    "redirectUri": "http://localhost/callback"     // optional; browser fallback only
  }
}
```

| Field | Required | Meaning |
|---|---|---|
| `clientId` | **yes** | The application (client) id of a *public client* app registration. Entra has no dynamic client registration, so without it the server does not connect and says why. |
| `scopes` | no | Scopes to request. Default: the `scope` of the server's 401 challenge, else the protected-resource metadata's `scopes_supported`, else `<server origin>/.default`. Use the API's `.default` scope, e.g. `api://<api-app-id>/.default`. |
| `tenant` | no | Tenant id (GUID), domain, `organizations`, `common` or `consumers`. Default: the tenant of the first authorization server the server advertises. Anything else (e.g. a value containing `/`) is rejected. |
| `account` | no | The account to prefer (a login hint, e.g. your UPN). Default: the Windows default account, or the account you picked earlier. |
| `redirectUri` | no | Loopback redirect for the [browser fallback](#browser-fallback) only; also honoured by `oauth` mode. |

When **both** `tenant` and `scopes` are set, Coda can ask for a token before the first
request. Otherwise it discovers them from the server first (below).

## How `entra-broker` behaves

1. **Discovery.** Unless `tenant` and `scopes` are both configured, the first request goes
   out without a token. On the server's 401, Coda reads the protected-resource metadata
   (RFC 9728) — from the `resource_metadata` URL in `WWW-Authenticate`, or the well-known
   path — takes the first advertised authorization server as the Entra authority (a
   configured `tenant` replaces its tenant) and derives the scopes as described above.
   Discovery is bounded at 30 s. A server that advertises no Entra authorization server is
   refused with a hint to use `auth.mode: "oauth"`.
2. **Silent first.** Coda asks the broker for a token without any UI (bounded at **30 s**),
   using the remembered account if there is one, else the configured `account` hint, else
   the Windows default account.
3. **Interactive only when needed.** Only when Entra says interaction is required (first
   consent, MFA, a new account) does Coda show the broker's own sign-in dialog, parented
   to the console window (its root owner, so it also works under Windows Terminal),
   bounded at **180 s**. If that dialog is cancelled or fails, Coda does **not** show it
   again automatically: further requests fail with a hint until you run
   `/mcp login <name>`. Without a console window (no terminal attached) it never prompts
   and tells you to run `/mcp login <name>` from an interactive session. The sign-in runs
   in the background, so if the request that started it gives up (e.g. a tool call times
   out), the dialog's outcome is still recorded and no second dialog opens. Because the very
   first connection may wait for this dialog, an `entra-broker` server's connect timeout
   is extended by those 180 s.
4. **Caching.** Tokens are cached in memory, process-wide, keyed by the server's canonical
   resource URI plus the configured `account` (lower-cased, or the default account), so a
   restarted connection reuses the token and two connections never show two dialogs. A
   token is refreshed silently once it is valid for less than **5 minutes**; while that
   refresh runs, or if it fails, requests keep using the old token until it actually
   expires.
5. **401 with a token.** If the server rejects a token, Coda drops it and re-acquires
   **once**, asking the broker to bypass its own cache; simultaneous 401s share that one
   re-acquisition.
6. **Cool-down.** After a failed acquisition, automatic refreshes are skipped for **60 s**
   (an unexpired token is still used), so a failing broker does not slow every request.
   `/mcp login` and the 401 path ignore the cool-down.
7. **What is stored.** Only the account the broker returned (its id and username — no
   tokens) is written to Coda's credential store, under
   `mcp-broker-account:<resource>#<account>`, so a restart silently reuses an account you
   picked interactively. The broker owns the tokens and their refresh. Credential-store
   reads and writes are bounded at 10 s.

## App registration

`entra-broker` needs a **public client** app registration in Microsoft Entra ID (it can
be your own; the MCP server's API is usually a separate registration):

1. **Brokered redirect URI.** In the Entra portal open *App registrations →* your app *→
   Authentication → Add a platform → Mobile and desktop applications*, and add the custom
   redirect URI

   ```
   ms-appx-web://microsoft.aad.brokerplugin/<clientId>
   ```

   with your app's client id. Without it the broker refuses the request (`AADSTS50011`).
2. **Public client flows.** On the same page, set *Allow public client flows* to **Yes**.
3. **API permission and consent.** Under *API permissions*, add the MCP server's API
   permission (the scope you request, e.g. `api://<api-app-id>/<scope>`), and grant admin
   consent if your tenant requires it. Otherwise users are asked to consent on first
   sign-in, or are blocked (`AADSTS65001`).
4. For the [browser fallback](#browser-fallback), also add `http://localhost/callback` as
   a *Mobile and desktop applications* redirect URI (or whatever `redirectUri` you
   configure).

**Scopes.** Request the API's `.default` scope — `api://<api-app-id>/.default` — to get a
token for everything the app has been granted. Do not add `offline_access` yourself: the
broker manages refresh, and the browser flow adds it automatically.

## Browser fallback

The broker is used on Windows. On other platforms — or on Windows with
`CODA_DISABLE_WAM=1` (also `true`, `yes`, `on`) — `entra-broker` uses the browser OAuth flow
instead, with the same configuration. Against Entra that flow follows Entra's rules (they
also apply to `auth.mode: "oauth"` whenever the authorization server is an Entra v2.0
authority):

- no RFC 8707 `resource` parameter (Entra rejects it; the resource is carried by the
  scopes);
- scopes are derived as above, never from Entra's generic `scopes_supported` (which would
  produce a Microsoft Graph token), and `offline_access` is always added so Entra issues a
  refresh token;
- a configured `tenant` replaces the tenant of the advertised issuer;
- the token is stored encrypted in Coda's credential store and refreshed from it.

**Redirect URI.** By default Coda listens on `http://localhost:<random port>/callback`.
Register `http://localhost/callback` as a *Mobile and desktop applications* redirect URI —
Entra ignores the port of a `localhost` redirect. `redirectUri` changes the template:
`http://localhost/callback` binds a random free port and substitutes it; an explicit port
(`http://127.0.0.1:8400/cb`) binds exactly that port; the path is honoured. Only `http`
with `localhost`, `127.0.0.1` or `[::1]` is accepted, and an explicit port `80` is
rejected (omit the port instead).

**Signing in.** The engine's own connections are headless: they reuse a stored token but
never open a browser. A server that needs a sign-in reports
"… requires sign-in and the Windows broker is disabled by CODA_DISABLE_WAM" (on other
platforms: "… the OS account broker is not available on this platform") followed by
"Run `/mcp login <name>` …";
`/mcp login <name>` then opens your browser and waits up to 5 minutes for the callback.

## `/mcp login` and `/mcp logout`

```
/mcp login <name>     sign in to one HTTP server (oauth or entra-broker)
/mcp logout <name>    sign out of it
```

- **`/mcp login`** — `entra-broker`: re-enables automatic prompts, then asks the broker
  silently and shows its dialog if needed (even after an earlier cancel, and ignoring the
  cool-down). `oauth` and the browser fallback: runs the browser flow. On success a server
  that is connected picks the new token up; one that is not connected is restarted. It can
  take a few minutes — the TUI stays usable and reports the outcome when it is done.
- **`/mcp logout`** — `entra-broker`: forgets the cached token and the remembered account
  and pauses automatic sign-in until the next `/mcp login`. It does **not** sign you out of
  Windows or remove the account from the broker. `oauth`: removes the stored token. The live
  connection is kept; its next request needs a new sign-in.
- `none` and `bearer` servers have nothing to sign in to and say so.

## Status in `/mcp`

The `/mcp` browser's detail view shows each HTTP server's authentication — never a token:

```
auth       entra-broker · user@contoso.com · expires 14:05
auth error Windows broker sign-in for MCP server 'contoso-docs' failed: … Run `/mcp login contoso-docs` to retry.
sign in    /mcp login contoso-docs
```

The expiry is local time (a date is added when it is more than a day away); `expired`
marks a token past its expiry. The account is shown when the broker reported one.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "… needs interaction but no console window is available" | Coda runs without a console (e.g. started by another program), so the broker dialog has nothing to attach to. Run `/mcp login <name>` from an interactive `coda` session in a terminal. |
| "… needs interaction. Run `/mcp login <name>`" or "… was cancelled earlier, so coda will not prompt again" | A dialog was cancelled or failed, or sign-in was paused by `/mcp logout`. Run `/mcp login <name>`. |
| `AADSTS50011` (redirect URI mismatch) | Broker: add `ms-appx-web://microsoft.aad.brokerplugin/<clientId>` under *Mobile and desktop applications*. Browser fallback: add `http://localhost/callback` (or your `redirectUri`). |
| `AADSTS65001` (consent required) | Add the API permission to the app registration and grant consent, or ask an administrator to. |
| `AADSTS901002` (the `resource` parameter is not supported) | An Entra v2.0 endpoint received an RFC 8707 `resource` parameter. Coda omits it whenever the advertised issuer or the discovery URL is an Entra v2.0 authority (`https://login.microsoftonline.com/<tenant>/v2.0`); if you still see it, the server advertises an issuer without `/v2.0`. On Windows use `auth.mode: "entra-broker"` (the broker does not use this flow); otherwise ask the server's owner to advertise the v2.0 issuer. |
| `AADSTS7000218` (a client secret or assertion is required) | Browser flow: the app registration is not a public client. Set *Allow public client flows* to **Yes**. |
| "… did not respond within 30s/180s" | The broker hung or the dialog was left open. Run `/mcp login <name>` to try again. |
| "Could not discover OAuth protected-resource metadata …" / "Could not determine the scopes …" | The server does not publish RFC 9728 metadata. Set `auth.tenant` and `auth.scopes`. |
| "… does not advertise a Microsoft Entra authorization server" | The server is not Entra-protected; use `auth.mode: "oauth"`. |
| "Windows broker unavailable … Set CODA_DISABLE_WAM=1 …" | The Entra account provider could not be found (e.g. an unusual Windows edition or policy); `/mcp login` cannot help. Set `CODA_DISABLE_WAM=1` to use the browser sign-in instead, or use `auth.mode: "oauth"`. |
| You want the browser flow on Windows | Set `CODA_DISABLE_WAM=1` before starting Coda. |
