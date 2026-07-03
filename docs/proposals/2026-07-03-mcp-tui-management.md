# Proposal: Interactive `/mcp` management in the coda TUI

- **Date:** 2026-07-03
- **Status:** Implemented (P1–P4) on branch `feature/mcp-tui-management`. `/mcp` list/info/add/edit/
  remove/enable/disable/start/stop/restart; encrypted secrets (`coda-secret:` refs + `${VAR}`).
  Deferred: redacted display of secret env/header values in `info`; deleting stored secrets on
  `remove` (the `ITokenStore` has no enumerate API).
- **Author:** Yury Opolev (design exploration, Coda)
- **Scope:** `Coda.Tui` (new `/mcp` slash command + REPL wiring), `Coda.Mcp` (config writer, live
  per-server connect/disconnect, server info capture, secret-reference resolution), `Coda.Common`
  (`SecretRedactor` reuse). Companion to `2026-07-03-mcp-in-serve-path.md` (serve-side MCP), which
  is already shipped.

## 1. Summary

coda can *connect* MCP servers (`.mcp.json`, stdio + HTTP with OAuth), but offers **no way to
manage them from inside the TUI**. There is no command to see which servers are configured, what a
server does, or which tools it exposes; and no way to add, edit, delete, or start/stop a server
without hand-editing JSON and restarting coda. This proposal adds a `/mcp` slash command that makes
MCP servers **fully manageable at runtime**:

- **Inspect** — `/mcp` (list), `/mcp info <name>` (description, transport, status, tools + their
  descriptions, resources/prompts).
- **Mutate config** — `/mcp add` (wizard), `/mcp edit <name>`, `/mcp remove <name>` — writing
  `.mcp.json` (project by default, `--user` for `~/.coda/.mcp.json`).
- **Live lifecycle** — `/mcp start <name>`, `/mcp stop <name>`, `/mcp restart [<name>]` —
  connect/disconnect a server so its tools appear or disappear from the agent **in the same
  session, from the next turn**.
- **Secrets** — secret-looking values are stored **encrypted** in coda's existing credential store;
  `.mcp.json` holds only a reference; `list`/`info` **redact** them.

## 2. Current state (verified on `e8d4c7c`)

| Concern | Today | File |
|---|---|---|
| Config read | `Parse` / `Load` merge user (`~/.coda/.mcp.json` or `CODA_USER_MCP_DIR`) + project (`<cwd>/.mcp.json`), project wins | `src/Coda.Mcp/McpConfig.cs` |
| Config **write** | **None** — editing means hand-editing JSON | — |
| Connect | `ConnectAllAsync(all)` at startup; `Tools`, `Clients`, `DisposeAsync(all)` | `src/Coda.Mcp/McpClientManager.cs` |
| Per-server start/stop | **None** — all-or-nothing | — |
| Server description | **Not captured** — `InitializeAndListToolsAsync` returns tools only; `initialize`'s `serverInfo`/`instructions` are discarded | `src/Coda.Mcp/IMcpClient.cs`, `McpStdioClient.cs`, `McpHttpClient.cs` |
| Tool metadata | `Name`, `Description`, `InputSchemaJson`, `ReadOnly` — enough for a rich `info` view | `src/Coda.Mcp/McpToolInfo.cs` |
| Slash commands | `ISlashCommand` + `SlashCommandRegistry`; `CommandContext` holds console/creds/session/providers and **`ExtraTools`** (flattened tool list) but **no MCP manager handle** | `src/Coda.Tui/Repl/*`, `Commands/*` |
| Agent tool source | `AgentRunner` holds `extraTools` from its ctor, but `BuildOptions()` runs **per turn** and re-reads it → a live source refreshes each turn | `src/Coda.Tui/Agent/AgentRunner.cs:43,84` |
| Secrets | Encrypted store `CredentialStoreFactory.Create()` (DPAPI / AES-GCM); `SecretRedactor` masks secrets in output | `src/Coda.Tui/CredentialStoreFactory.cs`, `src/Coda.Common/SecretRedactor.cs` |

**Key enabler for "live":** because `AgentRunner.BuildOptions()` is called on every turn and sets
`SessionOptions.ExtraTools` from its tool source, changing that source between turns changes the
agent's tools on the next turn — with no agent-loop surgery. Slash commands run between turns, so
`/mcp start|stop` naturally applies to the next turn.

## 3. Goals & non-goals

**Goals**
- Full lifecycle management of coda's own `.mcp.json` MCP servers from `/mcp`, with no restart.
- A rich `info` view: what the server is (description/instructions/version), its transport and
  connection status, and its tools + tool descriptions (plus resource/prompt counts).
- A safe config writer that preserves unrelated JSON and targets user vs project by an explicit flag.
- Secret values encrypted at rest and redacted in output — never plaintext in `.mcp.json`.
- Reuse everything already built: `McpConfig`, `McpClientManager`, `DefaultMcpHttpClientFactory`,
  the OAuth engine, `CredentialStoreFactory`, `SecretRedactor`.

**Non-goals**
- Editing MCP servers over the serve JSON-RPC protocol (a separate future item; `/mcp` is TUI-only).
- Elicitation / sampling / server-initiated requests (already out of scope in coda).
- Managing Cortex's Bridge-side host MCP plugin system (a separate mechanism; this is coda's own
  `.mcp.json` client).
- A GUI. `/mcp` is a terminal command with an interactive wizard.

## 4. Command surface

`/mcp <subcommand> [args] [--user]`. Unknown/absent subcommand → `list`.

| Command | Action |
|---|---|
| `/mcp` or `/mcp list` | Table of configured servers: name, scope (user/project), transport, status (connected N tools / stopped / failed), enabled/disabled. |
| `/mcp info <name>` | Server description/instructions + version, transport detail (command/args or url), scope, connection status, **tools with descriptions**, resource/prompt counts. Secrets redacted. |
| `/mcp add [<name>] [flags]` | Interactive wizard (pick transport → command/url → args → env/headers → auth), or one-line via flags. Writes `.mcp.json` (project default, `--user` for user). Offers to start it immediately. |
| `/mcp edit <name>` | Re-opens the wizard pre-filled with the server's current config; writes back to the file it came from. |
| `/mcp remove <name>` | Deletes the entry (with confirmation), stops it if running, and removes any stored secrets for it. |
| `/mcp start <name>` | **Transient**: connects the server; its tools become available next turn. |
| `/mcp stop <name>` | **Transient**: disconnects the server (disposes the process/HTTP client); its tools disappear next turn. Config unchanged — a restart reconnects it. |
| `/mcp restart [<name>]` | Stop+start one server, or reconnect all when no name is given (also picks up external `.mcp.json` edits). |
| `/mcp disable <name>` | **Persisted**: writes `"disabled": true` to the entry (and stops it) — the loader skips it on every startup until re-enabled. |
| `/mcp enable <name>` | **Persisted**: clears `disabled` (and starts it). |

Inline flags for scripting (mirroring `claude mcp add`): `--command`, `--args`, `--env KEY=VAL`
(repeatable), `--url`, `--header NAME=VAL`, `--transport stdio|http`, `--auth oauth|bearer|none`.

## 5. Architecture

### 5.1 A live MCP manager reachable from commands

Today `McpClientManager` connects everything at startup and is not reachable from `CommandContext`.
Introduce runtime lifecycle + a handle:

- **Extend `McpClientManager`** with per-server operations:
  - `Task<ConnectOutcome> ConnectServerAsync(string name, McpServerConfig config, CancellationToken)`
    — connect one server, add its tools; returns tools/description/error.
  - `Task DisconnectServerAsync(string name)` — dispose that server's client, drop its tools.
  - `IReadOnlyList<ITool> Tools` already exists and stays the live aggregate.
  - A monotonically-increasing `int Version` bumped on every connect/disconnect (see 5.2).
  Existing `ConnectAllAsync` becomes a loop over `ConnectServerAsync`.
- **Reach it from commands:** add `McpClientManager Mcp` (and the current merged config +
  scope map) to `CommandContext`, set in `Program.cs` right after the manager is built. The
  `/mcp` command drives the manager and the config writer through this handle.

### 5.2 Dynamic tool source (the "live" mechanism)

- Replace `AgentRunner`'s frozen `IReadOnlyList<ITool> extraTools` with a **live provider**:
  `Func<IReadOnlyList<ITool>> extraToolsProvider` (or an `IToolSource` with a `Version`). Default
  provider returns `[.. mcp.Tools, <the 4 resource/prompt helper tools>]` — the same composition
  the TUI already builds, but recomputed each call.
- `BuildOptions()` (already per-turn) calls the provider, so the next turn sees the current tools.
- `CommandContext.ExtraTools` becomes a computed property over the same provider (so `/context`
  token accounting stays accurate after start/stop).
- **Within a turn** the tool set is fixed (built at turn start) — correct and expected; `/mcp`
  commands run between turns. No mid-tool-call hot-swap is attempted (explicit non-goal for v1).

### 5.3 Config writer

Add `McpConfigWriter` (in `Coda.Mcp`) with `Upsert(scope, name, config)` and
`Remove(scope, name)`, where `scope ∈ {User, Project}` maps to `~/.coda/.mcp.json` (honoring
`CODA_USER_MCP_DIR`) or `<cwd>/.mcp.json`:
- Read the target file's `mcpServers` object (create the skeleton if missing), mutate exactly the
  one entry, write back with stable 2-space indentation. Round-trips unrelated servers untouched.
- Serialization mirrors the shapes `McpConfig.Parse` accepts (stdio: `command`/`args`/`env`;
  http: `type`/`url`/`headers`/`auth`), so read-after-write is loss-free.
- Writes are the single mutation point; `/mcp add|edit|remove` call it, then reconcile the live
  manager (start the added server, restart an edited one, stop a removed one).

### 5.4 Server description / info capture

`initialize` returns `serverInfo { name, version }` and optional `instructions`. Extend the connect
path to capture them:
- `IMcpClient` gains a `McpServerInfo? ServerInfo { get; }` (name, version, instructions), populated
  during `InitializeAndListToolsAsync` (both stdio and http clients already parse the `initialize`
  result — just retain these fields).
- `/mcp info` shows `instructions` as the human "what this server does", falling back to
  `name@version` when a server provides no instructions.

### 5.5 Secrets — encrypted at rest + redacted

- **Store:** reuse `CredentialStoreFactory.Create()` (DPAPI / AES-GCM under `~/.coda/credentials`) —
  the same store as provider tokens, namespaced by key. When the wizard collects a secret-looking
  value (stdio `env` value, HTTP bearer token, or an auth header), write the plaintext under a
  stable key **`mcp:<server>/<field>`** (mirroring the existing `llmauth:<provider>` convention;
  the distinct prefix avoids collision) and put the reference `"coda-secret:mcp:<server>/<field>"`
  in `.mcp.json`.
- **Resolve via a separate step (not inside `Load`):** `McpConfig.Load` stays **pure** (config
  parsing, no credential dependency). A new **`McpSecretResolver.Resolve(config, store)`**
  dereferences three string forms per secret field — `"coda-secret:<key>"` → decrypt from the
  store; `"${ENV_VAR}"` → environment expansion; anything else → literal (today's behavior,
  back-compatible). The three entry points (TUI, `coda run`, serve) call `Resolve` right after
  `Load`, so **`coda serve` inherits encrypted secrets for free** (it already builds the store) and
  `Coda.Mcp`'s parser never takes a credential dependency.
- **Redact:** `/mcp list` and `/mcp info` mask any `coda-secret:`/`${VAR}`/secret-looking value via
  `SecretRedactor` — e.g. `GITHUB_TOKEN = ***** (encrypted)` / `***** (from $VAR)`.
- **Remove:** `/mcp remove` also deletes the server's stored secrets.

### 5.6 Wizard

A guided prompt built with the same Spectre.Console primitives as `SetupWizard`: pick transport →
enter command/url → args → env/headers (KEY=VAL, blank to end) → for secret-looking keys, offer
"store encrypted (recommended) / `${ENV_VAR}` reference / literal" → auth block for HTTP. `edit`
pre-fills every step from the current config. Confirmation summary before writing.

## 6. Milestones

- **P1 — Inspect (read-only).** `/mcp` list + `/mcp info`; capture `serverInfo`/`instructions`
  (5.4); `CommandContext.Mcp` handle (5.1, read side). No writes, no lifecycle. Immediately useful.
- **P2 — Config writer + add/edit/remove.** `McpConfigWriter` (5.3) + wizard (5.6) + inline flags;
  scope handling (`--user`). Changes apply on next launch or via `/mcp restart`.
- **P3 — Live lifecycle.** Per-server `ConnectServerAsync`/`DisconnectServerAsync` (5.1), dynamic
  tool source (5.2), `/mcp start|stop|restart`. This is the "fully live" milestone.
- **P4 — Encrypted secrets.** Secret store + `coda-secret:` resolution in `McpConfig.Load` +
  redaction (5.5), wired through all three entry points. Wizard secret prompts.

Each milestone is independently shippable and testable; P1 delivers value on its own.

## 7. Testing

- **`McpConfigWriter`** — upsert adds/edits one server and round-trips unrelated entries; remove
  deletes only the target; user vs project targeting; skeleton creation; malformed file handling.
- **Live manager** — `ConnectServerAsync` adds tools + bumps `Version`; `DisconnectServerAsync`
  removes them; failed connect surfaces an error without corrupting state (use the existing
  prebuilt-client test seam / a stub `IMcpClient`).
- **Dynamic tool source** — `AgentRunner`/`BuildOptions` reflects the current tool set across a
  simulated start/stop (provider returns different sets on successive calls).
- **Secrets** — writer stores a secret and writes a `coda-secret:` ref (never plaintext);
  `McpConfig.Load` resolves `coda-secret:` / `${VAR}` / literal; `list`/`info` output is redacted;
  `remove` deletes the stored secret.
- **Command parsing/dispatch** — subcommand routing, `--user` scope, inline-flag `add`, confirmations.
- **Info rendering** — description/instructions fallback, tools + descriptions listed.

## 8. Security considerations

- **No plaintext secrets on disk** — the whole point of 5.5; secrets live only in the encrypted
  store, `.mcp.json` holds references, output is redacted.
- **stdio servers are third-party code** — `/mcp add` of a stdio server registers a local command
  that later runs. The wizard shows the exact command/args in the confirmation summary so the user
  sees what they're authorizing.
- **HTTP OAuth stays interactive-only in the TUI** — unchanged; `/mcp start` of an HTTP server may
  trigger the existing browser OAuth flow. `coda serve` remains non-interactive (skips + logs).
- **Redaction is defense-in-depth** — even literal (non-encrypted) secret-looking values are masked
  in `list`/`info`, so a shoulder-surfed terminal never shows a token.

## 9. Resolved decisions (2026-07-03)

1. **Enable/disable + start/stop — both.** In-session `start`/`stop` are transient; persisted
   `disable`/`enable` write a `"disabled": true` field the loader skips at startup (P1 loader honors
   it; P2 writer sets it; P3 wires the verbs). `list` shows a `disabled` state.
2. **Shared credential store, key `mcp:<server>/<field>`** — reuse the DPAPI/AES-GCM store; distinct
   prefix from `llmauth:` avoids collision. No dedicated store.
3. **Separate `McpSecretResolver.Resolve(config, store)` step** — `McpConfig.Load` stays pure; the
   three entry points call the resolver after `Load`. No credential dependency in the config parser.
4. **Project-default write scope + `--user`** — `add`/`edit`/`remove` write `./.mcp.json` by default;
   `--user` targets `~/.coda/.mcp.json`. The wizard always shows the exact file it will write.

## 10. Follow-up (not in this proposal)

- **Project-layer suppression for curated serve.** M4's `Curated` Bridge policy redirects only the
  user layer; `<cwd>/.mcp.json` still overrides it. A coda-side control (e.g. serve
  `--no-project-mcp` / `CODA_DISABLE_PROJECT_MCP`) would let the Bridge fully isolate the coding
  engine's MCP set. Tracked separately from `/mcp` TUI management.
