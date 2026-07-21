# Coda

**Coda is a native C# / .NET 10 agentic coding assistant for the terminal.** You
talk to it in plain language; it reads and edits your code, runs commands, searches
the web, and drives multi-step work to completion through an **agentic tool-use
loop** — with you in control of what it's allowed to do. It connects to Claude
(via Claude.ai subscription OAuth or an Anthropic API key) and to GitHub Copilot,
talking to the Anthropic Messages API natively.

Coda ships two front-ends over one engine: an **interactive TUI** for humans, and a
**programmatic `coda serve` API** so an orchestrator can drive Coda as a coding
subagent. The same engine is also embeddable in-process as a .NET library
(`Coda.Sdk`).

> **API reference:** the programmatic interface — the `coda serve` JSON-RPC
> protocol, its transports/authentication, and the embeddable `Coda.Sdk` — is
> documented in **[`docs/API.md`](docs/API.md)** (wire-level protocol details in
> [`docs/serve-protocol.md`](docs/serve-protocol.md)).

## Quick start

> **Prerequisites:** Windows (the TUI uses DPAPI for credential storage) and the
> [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
# 1. Clone and install as a global tool named `coda`:
git clone https://github.com/yury-opolev/coda-cli.git
cd coda-cli
./publish.ps1 -Flavor tool
dotnet tool install --global --add-source ./publish/tool Coda.Cli

# 2. Run it (first launch walks you through provider sign-in):
coda
```

`coda` is now on your PATH (via `%USERPROFILE%\.dotnet\tools`). On first launch the
**setup wizard** has you pick a provider (Claude.ai subscription, an Anthropic API
key, or GitHub Copilot), signs you in, and verifies the connection. Then just type.

To upgrade later, bump + repack + update:

```powershell
./build.ps1                  # bump the build number
./publish.ps1 -Flavor tool
dotnet tool update --global --add-source ./publish/tool Coda.Cli
```

**Prefer a standalone exe** (no .NET runtime needed)? Build a self-contained binary
and put it on your PATH instead:

```powershell
./publish.ps1 -Flavor self-contained   # -> publish/self-contained/coda.exe (~36 MB)
```

Check the version of any install with `coda --version`. Coda keeps all its own
state under **`~/.coda/`** (settings, sessions, credentials, …), separate from the
Claude CLI's `~/.claude/` — though it will read your existing `CLAUDE.md` and
`.mcp.json` if present. See [Configuration & storage](#configuration--storage).

## What Coda can do

- **Agentic tool loop** — the model plans and acts using built-in tools:
  - read-only, auto-run: `read_file`, `list_dir`, `glob`, `grep`
  - permission-gated: `edit_file`, `write_file`, `run_command` (all file tools are
    sandboxed to the working directory, symlink-aware)
  - `web_fetch` / `web_search` (DuckDuckGo), `notebook_edit`, `git_worktree`,
    `todo_write` (a live checklist), plan mode, `schedule_*` **scheduled tasks**
    (see [Scheduled tasks](#scheduled-tasks)), and the
    unified task tools (`task_start` / `task_output` / `task_stop` plus
    `task_list` / `task_get` / `task_peek` / `task_send` / `task_wait` /
    `task_background` / `task_remove`) for long-running jobs.
- **Unified task runtime** — one in-process `TaskManager` owns every long-running
  job in the session (background **subagents** and shell commands alike). It gives
  each task a shared id/status/depth model, a bounded ring of recent output, and a
  persistent, secret-redacted log under `~/.coda/task-logs`. `run_command` accepts
  `run_in_background` (then poll with `task_output`). Beyond inspection, the agent
  can `task_wait` for a task to finish (optional `timeout_seconds`, default **600**,
  maximum **1800** — a timeout reports *still running* and never stops the task),
  `task_background` a running foreground shell so it stops awaiting it, and
  `task_remove` a finished task from the list (its log file is preserved). The
  interactive TUI adds a live `/tasks` browser over the same manager. Tasks are
  **process-local** — they do **not** survive across processes, and the manager
  stops every task when Coda exits.
- **Subagents** — delegate a self-contained subtask with `task`, which runs a nested
  agent loop in its own context with a scoped toolset. Nesting is bounded (main
  agent at depth 0, subagents at depth 1 and 2); the main agent can inspect and
  steer every task, while a subagent can only see and act on its own descendants.
- **Code intelligence (LSP)** — when language servers are configured, an `lsp` tool
  provides definitions/references/hover/symbols/diagnostics.
- **MCP** — drop a `.mcp.json` in the working dir and Coda connects the servers,
  exposing their tools (`mcp__<server>__<tool>`), resources, and prompts to the agent.
- **Providers** — Claude.ai subscriber OAuth, Anthropic API key, and GitHub Copilot
  (device flow), with automatic token refresh.
- **You stay in control** — permission modes (`default` / `acceptEdits` / `plan` /
  `bypass`), allow/deny rules and lifecycle hooks from settings files, and an
  interactive approve/deny prompt for risky actions.
- **Autonomy helpers (opt-in)** — a background **session-memory** notes file, a
  **safety classifier** that vets actions in bypass mode, and an autonomous **goal
  loop** that keeps working until a judge says the goal is met. Plus automatic
  history **compaction**, output-style personas, and a plugin/skills marketplace.
- **Programmatic & embeddable** — `coda serve` exposes the agent over JSON-RPC
  (bidirectional: it streams progress and can ask the caller permission/clarification
  questions), over stdio or an **API-key-authenticated local socket**; or embed the
  engine directly via `Coda.Sdk`.

Coda is its own product, independent of any vendor's official CLI.

> **Platform note:** the engine is cross-platform, but the TUI and `coda serve`
> currently require **Windows** at runtime because the default credential store uses
> DPAPI.

## Coda — the interactive TUI

The terminal front-end targets **Terminal.Gui v2** and follows the **Warm Ember** interaction model.
After the compatibility matrix and acceptance thresholds pass, **full-screen mode is the default
interactive engine on a supported terminal**: a scrollable, virtualized transcript fills the **full
width** of the screen, an **operational status row** (turn, tool, waiting, approval, and key-hint state) sits
**directly above** the composer, and a **dynamic composer** starts at **three rows** and grows up to a
**capped height** as you type while staying pinned near the bottom. A separate, **stable metadata row**
(model, effort, context, usage, services, git, and cwd) occupies the **final row**. **Focus** stays on the composer while you
type — keyboard shortcuts drive the transcript, overlays, and completion menu without pulling focus away
from your prompt. **Inline mode** uses the same retained, scrollable layout in the terminal's primary
buffer and remains available as an **explicit compatibility** choice via `--tui=inline`.
**Spectre.Console remains a migration fallback** for environments where Terminal.Gui is not yet accepted,
and a **plain** renderer is always available.

```powershell
# Build (bumps the version), then run the TUI:
./build.ps1
dotnet run --project src/Coda.Tui -c Release

# Choose the interactive engine explicitly:
dotnet run --project src/Coda.Tui -- --tui=fullscreen   # retained, virtualized full-screen transcript
dotnet run --project src/Coda.Tui -- --tui=inline       # optional: same retained transcript, primary buffer (terminal history)
dotnet run --project src/Coda.Tui -- --tui=auto         # default: full-screen on a supported terminal, else plain
dotnet run --project src/Coda.Tui -- --plain            # plain output (screen readers, CI, redirection)
dotnet run --project src/Coda.Tui -- --no-mouse         # keyboard-only; leave the mouse to the terminal
```

**Keys (Warm Ember):** `Enter` submits · `Shift+Enter` (or `Ctrl+Enter`/`Ctrl+J` as terminal-compatible fallbacks) inserts a newline · `Up`/`Down` move the composer
cursor between the lines of a multi-line prompt · `Ctrl+Up`/`Ctrl+Down` step through prompt history ·
`Esc` dismisses the active menu or overlay, or clears a selection, and never exits Coda ·
`Ctrl+C` copies the current transcript selection and, with nothing selected,
exits on a **second** press · `/exit` (or `/quit`) exits — there is **no `Ctrl+D`** binding · `F2`
switches between full-screen and inline · `Ctrl+B` sends the selected (or latest) running **foreground
shell** to the background — and, inside the `/tasks` browser, releases an output attachment — without ever
opening the browser. Typing a `/` shows a slash-command completion menu directly
above the composer (Up/Down select, Tab completes, a single Esc dismisses).

**Mouse:** in the **transcript**, **left-drag** selects text and `Ctrl+C` copies it. In the **composer**,
**left-drag** selects text; when a selection exists, `Ctrl+C`, a **left-click**, or a **right-click**
copies it and clears the selection; a **right-click** with no selection pastes at the clicked caret;
`Ctrl+V` remains a direct paste; and a **middle-click** opens the editor context menu. **`Shift`-drag**
hands native selection and copy to the terminal where supported. `--no-mouse` leaves selection and copy
native to the terminal, and every action stays reachable from the keyboard. Full-screen has **no permanent sidebar**
and uses a **virtualized transcript** (context, pickers, permissions, help, and diffs all use
keyboard-driven overlays). **Plain mode** is recommended for screen readers, CI, output/input
redirection, and terminals that Terminal.Gui does not support.

> **Compatibility:** the terminal matrix is a *reproducible checklist*, not a claim that every terminal
> has already passed. See [`docs/terminal-gui-compatibility.md`](docs/terminal-gui-compatibility.md)
> for the acceptance thresholds and how to run the spike + PTY smoke script to record results.

Inside the REPL:

```
/login [claude|copilot]   sign in (Claude.ai browser / Copilot device code)
/status                   sign-in state for every provider
/tasks                    open the live task browser (prints a textual snapshot in plain/Spectre)
/provider [id]            show or switch the active provider
/model [id]               show or set the chat model
/effort [low|medium|high|max|auto]  show or set reasoning effort (Claude only)
/context                  show context-window usage broken down by category
/goal [<text> | off]      set/clear the autonomous goal (--timeout, --max-turns)
/log [<level> | stderr on|off]  show or set telemetry logging level
/headers                  the outgoing request headers (secrets redacted)
/logout [provider]        sign out
/help [command]           list commands, or show detailed help for one
/version   /clear   /exit
```

> **Tip:** append `--help` (or `-h`) to any command for its usage and examples, e.g. `/model --help`.

### `/tasks` — the live task browser

In the full-screen and inline Terminal.Gui shells, `/tasks` opens a **focused, full-overlay
live browser** over the session's in-process `TaskManager`. The bare `/tasks` submission is
intercepted **before** the dispatch/startup guard, so the browser opens even while an agent
turn is running. Plain and Spectre modes instead print a **read-only textual snapshot** of the
same tasks (no interactive actions). Before the first turn there is no session yet, so the
browser/snapshot shows an empty list.

The list shows **active tasks first as a parent/child hierarchy**, then **recent completed,
failed, and stopped** tasks, each with a status glyph, description, kind (subagent/shell),
foreground/background mode, and duration. Selection is stable by task id.

- **List:** `Up`/`Down`, `PageUp`/`PageDown`, `Home`/`End` navigate · `Enter` opens the detail
  page · `x` twice within ~1.5 s stops a running task · `r` dismisses a **terminal** task from the
  in-memory list (its **persistent log is preserved**) · `Esc` closes.
- **Detail:** `s` steers a running **subagent** (modal editor: `Enter` queues, `Shift+Enter` /
  `Ctrl+Enter` / `Ctrl+J` insert a newline, `Esc` cancels; messages are delivered FIFO at the
  subagent's next safe loop boundary) · `a` **attaches** a running **background shell**'s output
  and pauses the main agent · `Ctrl+B` releases a UI attachment (or backgrounds an originally
  foreground shell) · `x` twice stops · `r` dismisses a terminal task · `l` toggles between recent
  live output and the persistent **log** tail · `End` jumps to newest and restores **auto-follow**
  · `Esc` returns to the list (releasing any attachment first).

**Attachment is output-only** — there is **no shell stdin** in this phase. Attaching requests a
pause through a reference-counted execution gate: if the main agent is idle the pause is immediate,
otherwise it waits for the next **safe loop boundary** or turn completion (the page shows
`pausing main agent…`). The pause lease is released on `Esc`, `Ctrl+B`, shell completion, stop,
mode switch, or shutdown, and the main agent resumes. UI attachment is presentation-only and never
leaks into the model-facing `task_list`/`task_get`. Everything here is **process-local** and
**stops when Coda exits**.

`coda serve` has the **same non-UI task capabilities** — the `task_*` model tools, including
`task_wait` / `task_background` / `task_remove`, are registered identically for interactive and
serve pipelines — but serve has **no slash commands and no TUI**, so there is no `/tasks` browser
in serve.

### Model, effort & context

- **`/model [id]`** shows or sets the chat model. With no argument it lists the
  models the **active provider actually grants** — fetched live from the provider
  (Anthropic `GET /v1/models`, Copilot `GET /models`) and cached for the session —
  falling back to a built-in list when the provider exposes no such endpoint or the
  call fails (e.g. Claude.ai OAuth, or offline). Each model is annotated with its
  display name and context window from the **model catalog** (see below).
  `/model refresh` re-fetches the live list *and* refreshes the catalog. Run
  interactively with no argument, `/model` opens a picker that marks the active
  model as **Current** and opens with it selected.
- **`/effort [low|medium|high|max|auto]`** sets the reasoning effort level. It is
  sent to the Anthropic API as `output_config.effort` (with the
  `effort-2025-11-24` beta) and is honored only by models that support it
  (`opus-4-8`, `sonnet-4-6`); `max` is Opus-only and clamps to `high` elsewhere.
  Effort is session-scoped; `auto` clears it (model default). GitHub Copilot has
  no effort equivalent, so the setting is ignored there.
- **`/context`** shows how the model's 200k context window is being used, broken
  down into **System prompt / System tools / MCP tools / Messages / Autocompact
  buffer / Free space** with a grid visualization and per-category token counts.
  Counts
  come from the Anthropic **count-tokens API** when available
  (`POST /v1/messages/count_tokens`), falling back to a local estimate otherwise
  (e.g. Copilot or offline). The window size per model and the `/cost` price table
  come from the **model catalog** (below), falling back to 200k / built-in prices.

### Model per provider (persisted)

The provider is resolved automatically from the credential you're signed in to
(`coda auth login <id>`) — there is no persisted "default provider". A **model always belongs to
a provider** (there is no provider-agnostic default model), and **your model choice sticks per
provider**:

- **`/model <id>`** persists the model **for the active provider**.

Persisted under `~/.coda/settings.json` as `modelByProvider`, which you can also edit by hand:

```json
{ "modelByProvider": { "github-copilot": "claude-opus-4.8" } }
```

Model precedence is **`CODA_MODEL` env → the provider's `modelByProvider` entry → the provider's
built-in default**. A project-level `<project>/.coda/settings.json` overrides the user file per
provider. (`CODA_SETTINGS_DIR` relocates only `settings.json`, not the rest of `~/.coda`.)

### Model catalog (metadata)

Display names, context-window sizes, and pricing come from a **models.dev**-shaped
catalog. Source order: an explicit file (`CODA_MODELS_PATH`), the on-disk cache
(`~/.coda/cache/models.json`), then a **bundled snapshot** committed in the repo —
so it works fully offline with no third-party call by default.

The catalog is refreshed automatically: on TUI startup a **background, staleness-gated
refresh** (default: if the cache is older than 12h) fetches the latest from models.dev
without blocking; `/model refresh` forces it. Override the host with `CODA_MODELS_URL`,
or disable all fetching with `CODA_DISABLE_MODELS_FETCH=1`. The in-repo snapshot is
regenerated with `./scripts/update-models-snapshot.ps1`.

For GitHub Copilot request routing, the authenticated `GET /models` response is
authoritative: Coda reads each model's `supported_endpoints` and uses `/responses`,
`/v1/messages`, or `/chat/completions` as advertised. This lets newly released models
work without a hard-coded model-name list.

The headless equivalent of the `/model` listing is **`coda models`**:

```powershell
coda models                      # active provider's models (text)
coda models --provider copilot --json --refresh
```

### Headless help (`coda help`)

`coda help` prints command metadata without starting a session — no credentials
required, no sign-in needed:

```powershell
coda help                        # list all commands (name + summary)
coda help <command>              # usage, arguments, and examples for one command
coda help <command> --json       # structured JSON — for an orchestrating agent
coda help --json                 # full command list as JSON
```

> `--json` is intended for a main or orchestrating agent that needs to discover
> the available commands and their argument schemas programmatically.  The output
> shape is `{ "commands": [{ "name", "aliases", "summary" }] }` for the list, and
> `{ "name", "aliases", "summary", "usage", "description", "options", "examples" }`
> for a single command.

All product-facing names live in `src/Coda.Tui/Branding.cs` (one place to rename).

### Setup & chatting

On first launch (no stored credentials) Coda runs a **setup wizard**: pick a
provider → sign in → it verifies the connection with a tiny real completion.
Re-run it any time with `/setup`.

Once connected, just type a message. Coda streams the assistant reply and runs an
**agentic tool-use loop**:
- read-only (automatic): `read_file`, `list_dir`, `glob`, `grep`
- permission-gated: `edit_file`, `write_file`, `run_command`
- `task` — delegate a self-contained subtask to a **subagent** (a nested agent loop
  with the file/command tools but not `task`; it streams its work and reports back)
- any tools from configured **MCP servers** (below), as `mcp__<server>__<tool>`

All file tools are sandboxed to the working directory (symlink-aware). `/model`
shows or sets the model; `/clear` resets the conversation.

### MCP servers

Coda connects MCP servers declared in `.mcp.json` and exposes their tools to the agent
(and subagents). Two layers are merged, like skills and settings: a **user** file at
`~/.coda/.mcp.json` and a **project** file at `<workdir>/.mcp.json` (project entries override
user entries by name). All three entry points — the interactive TUI, `coda run`, and
`coda serve` — load the same merged config.

**Manage servers from the TUI with `/mcp`** — no hand-editing or restart required:

| Command | Action |
|---|---|
| `/mcp` · `/mcp info <name>` | list servers (scope, transport, status) · inspect one (description + tools) |
| `/mcp add <name> [flags]` · `/mcp edit <name>` | add/change a server via a wizard, or inline flags (`--command`, `--args`, `--env`, `--url`, `--header`, `--auth`) |
| `/mcp remove <name>` | delete a server from its config file |
| `/mcp start\|stop\|restart <name>` | connect/disconnect **live** — tools appear/disappear from the next turn |
| `/mcp enable\|disable <name>` | persistently toggle a server (`"disabled": true`, survives restart) |

Writes default to the project file; add `--user` to target `~/.coda/.mcp.json`. Secret values
(tokens, keys) the wizard collects can be **stored encrypted** in coda's credential store — only a
`coda-secret:<key>` reference is written to `.mcp.json`. Values are resolved at load: `coda-secret:…`
decrypts from the store, `${ENV_VAR}` expands from the environment, anything else is literal.

**Stdio** (a locally launched process):

```json
{ "mcpServers": { "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."] } } }
```

**HTTP** (a remote Streamable-HTTP server). Add `"type": "http"` and a `url`; optional
static `headers` are sent on every request:

```json
{ "mcpServers": { "remote": { "type": "http", "url": "https://mcp.example.com/mcp" } } }
```

HTTP servers authenticate automatically via the **MCP OAuth flow**: on a 401 challenge Coda
performs RFC 9728 → RFC 8414/OIDC discovery, registers a client (configured id or RFC 7591
dynamic registration), runs an OAuth 2.1 + PKCE login in your browser (with the RFC 8707
`resource` parameter and RFC 9207 `iss` validation), then stores and refreshes the token
(encrypted, under `~/.coda/credentials`, keyed by the server's canonical URI). Control it
with an `auth` block:

```json
{ "mcpServers": { "remote": {
  "type": "http", "url": "https://mcp.example.com/mcp",
  "auth": { "mode": "oauth", "clientId": "optional-preregistered-id", "scopes": ["files:read"] }
} } }
```

`mode` is `oauth` (default), `bearer` (static `"token"`), or `none`. Headless runs
(`coda run` and `coda serve`) reuse stored tokens but never open a browser; a server needing
fresh sign-in is skipped with a note to stderr. Pre-authorize such servers once via the TUI or
`coda run`; the encrypted token is then reused by every process automatically.

**Client id for direct HTTP OAuth.** The flow needs a client id from one of two places: a
configured `auth.clientId`, or RFC 7591 dynamic client registration when the authorization server
advertises a registration endpoint. Coda tells the two failure modes apart so the fix is
unambiguous:

- the authorization server **does not advertise** dynamic registration → set `auth.clientId` to a
  pre-registered id (below);
- registration **was attempted and failed** (the registration endpoint erred) → the message names
  that endpoint; retry or fall back to a configured id.

```json
{ "mcpServers": { "remote": {
  "type": "http", "url": "https://mcp.example.com/mcp",
  "auth": { "mode": "oauth", "clientId": "your-preregistered-client-id", "scopes": ["files:read"] }
} } }
```

When a generic HTTP client can't complete OAuth at all, front the server with an **authenticated
local stdio adapter/proxy** — a locally launched command that holds the credential and speaks plain
MCP over stdio — and point Coda at that instead of the HTTP endpoint:

```json
{ "mcpServers": { "remote": { "command": "your-mcp-proxy", "args": ["--upstream", "https://mcp.example.com/mcp"] } } }
```

**Startup timeout & failure diagnostics.** Connecting a server (the `initialize` then `tools/list`
handshake) is bounded by a connect timeout so one slow server never blocks the others:

- `CODA_MCP_CONNECT_TIMEOUT` sets the window in **whole seconds** (default **60**).
- A missing, blank, or non-numeric value uses the default.
- `0` or a negative value **disables** the timeout (waits indefinitely); a value larger than the
  platform timer limit is treated the same way (disabled/infinite) rather than erroring.

When a server fails to start it is skipped with a message that names the failed phase and cause:

- a timeout or a caller cancellation names the phase it was in (`initialize` or `tools/list`), and
  the two are reported distinctly;
- a child process that exits reports its phase, **exit code**, and a bounded tail of its
  **sanitized `stderr`** when available — secrets are redacted, so raw tokens or keys never appear
  in the diagnostics.

A failed connection is atomic: the server's tools, connected client, and version counter are left
exactly as they were, so a slow or broken server can never half-register.

**`coda serve` MCP controls.** MCP loads by default under serve (parity with the TUI and
`coda run`). Disable it per session with `--no-mcp` (or `CODA_SERVE_DISABLE_MCP=1`). Point serve
at an orchestrator-curated config with `CODA_USER_MCP_DIR` — the cleanest way to give a
programmatic session a deliberate MCP set instead of the operator's personal `~/.coda/.mcp.json`.
Serve never writes MCP diagnostics to stdout (that is the JSON-RPC protocol channel); they go to
stderr.

> The chat path uses the native **Anthropic Messages API** (Claude.ai OAuth +
> Anthropic API key). GitHub Copilot chat uses a different, OpenAI-shaped API.

## Auth specifics

- **Claude.ai subscription** — OAuth 2.0 Authorization Code + PKCE (S256): a
  cryptographically random verifier/challenge/state, a system-browser loopback
  redirect, and automatic token refresh near expiry.
- **Anthropic API key** — the simplest path; bring your own `ANTHROPIC_API_KEY`
  (via `ApiKeyProvider`).
- **GitHub Copilot** — the OAuth Device Authorization Grant (enter a code at
  github.com); the short-lived Copilot token is refreshed from the stored GitHub token.

Provider endpoints and request details are configurable via environment variables
and the provider config classes (`ClaudeAiOAuthConfig`, `GitHubCopilotConfig`).

## Projects

| Project | TFM | Purpose |
|---|---|---|
| `src/LlmAuth` | `net10.0` | Core: abstractions, PKCE, OAuth engine, loopback listener, identity, `CredentialManager`. |
| `src/LlmAuth.Providers.ClaudeAi` | `net10.0` | Claude.ai OAuth provider + config + API-key provider. |
| `src/LlmAuth.Providers.GitHubCopilot` | `net10.0` | GitHub Copilot device-flow provider. |
| `src/LlmAuth.Storage.Windows` | `net10.0` | DPAPI-encrypted token store (DPAPI is Windows-only at runtime). |
| `src/LlmClient` | `net10.0` | Anthropic Messages streaming client + client fingerprint. |
| `src/Coda.Agent` | `net10.0` | Agent loop + tools (read/list/glob/grep/edit/write/run + task/subagents). |
| `src/Coda.Mcp` | `net10.0` | MCP stdio client (JSON-RPC) + tool bridge. |
| `src/Coda.Tui` | `net10.0` | The **Coda** interactive TUI (Terminal.Gui v2; Spectre.Console/plain fallbacks). Requires Windows at runtime (DPAPI). |
| `samples/LlmAuth.Sample` | `net10.0-windows` | Console demo. |
| `tests/LlmAuth.Tests` | `net10.0` | xUnit unit tests (auth). |
| `tests/Coda.Tui.Tests` | `net10.0` | xUnit unit tests (TUI). |
| `tests/Engine.Tests` | `net10.0` | xUnit unit tests (SSE parser, agent loop, tools). |

## Usage

High layer (batteries-included loopback login):

```csharp
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Storage.Windows;

using var claude = new ClaudeAiProvider();
var manager = new CredentialManager(new DpapiTokenStore(), [claude, new ApiKeyProvider()]);

// Opens the system browser + a localhost loopback listener, captures the redirect:
Credential cred = await manager.LoginAsync(ClaudeAiProvider.Id);

// Everyday use — refreshes automatically when near expiry:
AuthHeaders headers = await manager.GetAuthHeadersAsync(ClaudeAiProvider.Id);
```

Low layer (host-driven; headless / manual paste):

```csharp
ILoginFlow flow = claude.BeginLogin(new LoginOptions { RedirectMode = RedirectMode.Manual });
Console.WriteLine(flow.AuthorizeUrl);            // host opens this however it likes
// …host captures the redirect and reads ?code & ?state…
Credential cred = await flow.CompleteAsync(code, state);
```

Custom browser hook (the "calls back to the host" model):

```csharp
await manager.LoginAsync(ClaudeAiProvider.Id, new LoginOptions
{
    OpenBrowser = (url, ct) => { /* show the URL / open an embedded view */ return Task.CompletedTask; }
});
```

## GitHub Copilot (device flow)

Copilot uses the OAuth **Device Authorization Grant** — the library calls back to
your host with a code to enter at github.com:

```csharp
using var copilot = new GitHubCopilotProvider();
var manager = new CredentialManager(new DpapiTokenStore(), [copilot]);

Credential cred = await manager.LoginWithDeviceCodeAsync(
    GitHubCopilotProvider.Id,
    (prompt, ct) =>
    {
        Console.WriteLine($"Open {prompt.VerificationUri} and enter {prompt.UserCode}");
        return Task.CompletedTask;
    });

// Bearer Copilot token + Editor-Version / Copilot-Integration-Id headers; the
// short-lived Copilot token auto-refreshes from the stored GitHub token.
AuthHeaders headers = await manager.GetAuthHeadersAsync(GitHubCopilotProvider.Id);
```

> Copilot endpoints/headers are configurable (`GitHubCopilotConfig` / `GH_COPILOT_*`
> env vars). Using the Copilot API outside official editor integrations is subject to
> GitHub's Terms of Service.

### GitHub Enterprise (data residency)

`/login copilot` (and first-run setup) asks whether you're on **public github.com** or
a **GitHub Enterprise Cloud** data-residency tenant (a `*.ghe.com` subdomain, e.g.
`octocorp.ghe.com`). Enterprise sign-in runs the device flow against your host and uses
the raw device-flow token directly at `copilot-api.<domain>` (no dotcom token exchange),
so the credential is durable — **you sign in once and are not re-prompted** on later
sessions. Public github.com keeps the standard token exchange.

Your choice is persisted to `~/.coda/settings.json` as `githubEnterpriseDomain`:

```json
{ "defaultProvider": "github-copilot", "githubEnterpriseDomain": "octocorp.ghe.com" }
```

On startup this hydrates `GH_COPILOT_ENTERPRISE_DOMAIN` (which an explicit env var still
overrides) so both the auth provider and the chat client target the same host.

## Versioning & build

Semantic version lives in [`version.json`](version.json) (starts at **0.1**). The
build entry point **bumps the build number on every run** and stamps the version
into every assembly:

```powershell
./build.ps1                 # bump build number, Release build
./build.ps1 -Test           # bump, build, run tests
./build.ps1 -Configuration Debug
./build.ps1 -NoBump         # build without bumping
```

Plain `dotnet build LlmAuth.slnx` / `dotnet test tests/LlmAuth.Tests` still work
(they use the last-stamped version and do **not** bump).

### Publishing

`publish.ps1` publishes the **current** version (no bump) of the `coda` CLI into
`./publish` in three flavors:

```powershell
./publish.ps1                          # all three flavors (win-x64, Release)
./publish.ps1 -Flavor self-contained   # standalone coda.exe (bundles the runtime)
./publish.ps1 -Flavor framework-dependent  # smaller coda.exe (needs .NET 10 runtime)
./publish.ps1 -Flavor tool             # .NET global tool package (Coda.Cli)
./publish.ps1 -Runtime win-arm64       # target Windows on ARM
```

| Flavor | Output | Needs .NET 10 installed? |
|---|---|---|
| `self-contained` | `publish/self-contained/coda.exe` (~36 MB) | No |
| `framework-dependent` | `publish/framework-dependent/coda.exe` (~2 MB) | Yes |
| `tool` | `publish/tool/Coda.Cli.<version>.nupkg` | Yes (SDK) |

Install / upgrade the global tool (command: `coda`):

```powershell
dotnet tool install --global --add-source ./publish/tool Coda.Cli
dotnet tool update  --global --add-source ./publish/tool Coda.Cli
```

Check the version of any flavor with `coda --version` (or `coda --help`).

## Supervisor features (opt-in)

Coda can run background supervisors alongside the agent. All are off by default
and enabled via `SessionOptions`:

| Option | Effect |
|---|---|
| `EnableSessionMemory = true` | After work-bearing turns, a background forked agent refreshes `.coda/SESSION_MEMORY.md` — a running, structured notes file about the session — without touching the main conversation. |
| `EnableBypassClassifier = true` | In bypass ("yolo") mode, every mutating tool action is first classified by a separate model call; safe actions auto-run, risky ones (e.g. `rm -rf`, force-push) are escalated to you (or denied when headless). Fails closed. |
| `Goal = "<objective>"` | The **autonomous goal loop**: a `GoalSupervisor` keeps the agent working until an isolated judge declares the goal met. When it tries to finish, the judge rules `DONE` / `CONTINUE`; on `CONTINUE` the agent is nudged with what remains. A transient judge error retries with backoff then **fails closed** (keeps working) — never silently ends an unfinished run. Bounded by a **time + turn budget** (default **24h / 60000 turns**, set via `GoalMaxDuration` / `GoalMaxContinuations` or the `goal` settings block). At the bound it **escalates one question** to the operator (via the `ask_user_question` channel / serve `request/question`); an answer grants **one bounded extension**, otherwise it stops. The outcome is reported as `RunResult.GoalStatus` (`Met` / `Unmet` + what remains). Long runs are kept within the context window by **in-loop compaction**. |

These build on the agent's hook buses: post-sampling "observe" hooks (`IPostSamplingHook`)
run after each turn, and stop hooks (`IStopHook`) can refuse to let the agent finish. The
goal loop is a first-class `GoalSupervisor` consulted by the agent loop (it replaced the
earlier `GoalStopHook`).

Goal budget defaults are configurable in `~/.coda/settings.json` (project file overrides
user), e.g.:

```json
{ "goal": { "maxDuration": "1.00:00:00", "maxContinuations": 60000, "autoCompact": true, "extensionFraction": 0.25 } }
```

Per-goal overrides (CLI `--goal-timeout` / `--max-continuations`, serve params, or TUI
`/goal --timeout`/`--max-turns`) take precedence over settings, which take precedence over
the built-in defaults.

### Headless CLI flags

All supervisor features are also reachable from `coda run`:

| Flag | Effect |
|---|---|
| `--yolo` | Blanket-allow bypass — every mutating action runs without a prompt. |
| `--yolo-safe` | Bypass + classifier — risky actions are escalated instead of blindly allowed. Prefer over `--yolo` when running unattended. |
| `--goal "<objective>"` | Enable the autonomous goal loop; the agent works until the judge decides the goal is met (or the budget is exhausted). The goal status prints to stderr (and to the `--json` result as `goalStatus`); an unmet goal yields a non-zero exit code. |
| `--goal-timeout <duration>` | Wall-clock budget for the goal run: `30m`, `2h`, `1d`, or `hh:mm:ss` (requires `--goal`; default 24h). A bare integer is rejected — use a unit. |
| `--session-memory` | Enable the background SessionMemory watcher. |
| `--max-continuations <n>` | Turn backstop. For a goal run it sets the goal turn budget (default 60000); otherwise it bounds non-goal stop-hook continuations (default 10). |
| `--effort <level>` | Reasoning effort (`low`/`medium`/`high`/`max`/`auto`). Claude-only; `max` is Opus-only. |

Example:

```powershell
coda run -p "refactor all tests to use xUnit v3 assertions" --yolo-safe --goal "all tests pass" --goal-timeout 2h --session-memory
```

Driven over `coda serve`, the goal is set on session create or dynamically with
`session/setGoal` (persist-until-cleared), and `session/prompt` results carry `goalStatus`;
the at-bound escalation reaches the orchestrator via `request/question`. See
[`docs/serve-protocol.md`](docs/serve-protocol.md).

## Scheduled tasks

Coda can run a prompt on a schedule. The agent creates a schedule with
`schedule_create`, inspects them with `schedule_list`, and removes them with
`schedule_delete`. Each firing runs as an **isolated background agent** — its own
one-message conversation, never the interactive history.

### Creating a schedule

`schedule_create` takes a `prompt` plus **exactly one** selector, and optional
`name` and `timeZone`:

- **`every`** — a repeating interval: `"3m"`, `"2h"`, `"1d"` (`m`inutes / `h`ours /
  `d`ays). The **minimum interval is one minute**.
- **`at`** — a one-shot ISO-8601 date-time, with or without an explicit offset
  (e.g. `"2026-07-21T09:00:00"` or `"2026-07-21T09:00:00-04:00"`).
- **`cron`** — a repeating **standard five-field cron** rule
  (`minute hour day-of-month month day-of-week`, e.g. `"0 9 * * 1-5"`).

```jsonc
// every 30 minutes
{ "prompt": "check the build queue and summarize failures", "every": "30m" }

// one-shot at a local wall-clock time
{ "prompt": "post the standup reminder", "at": "2026-07-21T09:00:00", "name": "standup" }

// weekdays at 09:00 in a named zone
{ "prompt": "email me the overnight error report", "cron": "0 9 * * 1-5", "timeZone": "America/New_York" }
```

### Timezones and DST

Offset-less `at` values and `cron` rules are interpreted in the **machine-local
timezone** by default. Provide `timeZone` (an IANA id like `"America/New_York"`, a
Windows id, or a fixed offset like `"UTC-05:00"`) to override it; an `at` value with
an explicit offset is honored as written. Across DST transitions a nonexistent
spring-forward local time is **skipped**, and an ambiguous fall-back local time
resolves to the **earlier** UTC instant (it is not run twice).

### When schedules run

Schedules execute **only while an interactive Coda session or `coda serve` is
open** — there is **no background daemon and no OS scheduler**. Definitions are
persisted in `<project>/.coda/scheduled_tasks.json` and **resume on the next
startup**. On startup an **overdue** schedule runs **once immediately** (not once
per missed tick — missed ticks are **coalesced**), then advances to its next future
boundary.

A single definition **never overlaps itself**: while one firing is running, at most
**one** replacement run is held pending (further due ticks only advance the next
time, they don't queue up), and it starts after the running one reaches a terminal
state. **Different** definitions may run **concurrently**.

### Observing and steering runs

Each firing is a `TaskKind.Scheduled` background agent in the unified task runtime,
so it is visible in the interactive **`/tasks`** browser and to the `task_*` tools
(`task_list`, `task_get`, `task_peek`, `task_send`, `task_stop`) and its
secret-redacted log. The interactive TUI also shows concise **notices** as a run
starts, completes, fails, or is stopped; `coda serve` forwards the same transitions
as `event/scheduleLifecycle` JSON-RPC notifications (see
[`docs/serve-protocol.md`](docs/serve-protocol.md)).

`schedule_delete` prevents any **future** and **pending** run of a definition, but
does **not** stop a firing that is already executing (stop that with `task_stop`).

Every scheduled run uses the session's **live** permission mode, current model, and
MCP / LSP / tool configuration at the moment it fires — a mid-session change is
observed by the next run. Scheduled agents run isolated at depth 1: they **cannot**
create, list, or delete schedules (`schedule_*` is unavailable to them) and can only
inspect and steer their **own** descendant tasks.

### Failure and crash semantics

A run that throws is recorded as **Failed**, a cancelled/stopped run as **Stopped**,
and the outcome (with a short summary) is surfaced in `schedule_list` and the
lifecycle notice/event; recurring definitions keep their next boundary and run
again. One-shot `at` schedules are **at-least-once**: if the process crashes mid-run
the record survives and reruns on the next startup, and it is **removed only after**
it reaches a terminal state. Failures are also written to the telemetry log when
logging is enabled.

In `coda serve`, a stdio peer is authenticated immediately so the runtime starts at
startup; an **API-key** peer's runtime does **not** start until a valid key
completes an authenticated `initialize`. Headless `coda run` does **not** keep the
scheduler alive — the runtime is disabled outside interactive and serve sessions.

## Configuration & storage

Coda keeps **all of its own state under `~/.coda/`**, kept deliberately separate
from the Claude CLI's `~/.claude/`. It will, however, *read* a couple of shared
project files if they exist (it never writes to them).

| What | Location | Notes |
|---|---|---|
| User settings | `~/.coda/settings.json` | allow/deny rules, hooks, LSP servers |
| Project settings | `<project>/.coda/settings.json` | overrides user settings |
| Credentials | `~/.coda/credentials/` | DPAPI-encrypted (Windows) / AES-GCM (other OS) |
| Session transcripts | `<project>/.coda/sessions/<id>.json` | for `/resume` |
| Scheduled tasks | `<project>/.coda/scheduled_tasks.json` | persisted definitions; resume on next startup |
| Plugins | `~/.coda/`, `<project>/.coda/plugins/` | |
| Skills | `~/.coda/skills/`, `<project>/.coda/skills/` (+ read-only `~/.claude/skills/`) | `SKILL.md` per skill |
| MCP servers | `~/.coda/.mcp.json`, `<project>/.mcp.json` | stdio + HTTP; project overrides user |
| Session memory | `<project>/.coda/SESSION_MEMORY.md` | when enabled |
| Telemetry logs | `~/.coda/logs/coda-<timestamp>-<pid>.log` | JSON-lines; opt-in; secrets redacted |

**Shared (read-only) with the Claude CLI:** `CLAUDE.md` project instructions
(including `~/.claude/CLAUDE.md`), `<project>/.mcp.json` MCP server config, and
`~/.claude/skills/` (lowest precedence — your `~/.coda/skills` and project skills
override by name). Override the skill source dirs with `CODA_CLAUDE_SKILLS_DIR` /
`CODA_USER_SKILLS_DIR` (point at a missing path to opt out).

> **Credential migration:** credentials previously lived under `%APPDATA%\LlmAuth`
> (Windows) / `~/.config/LlmAuth` (other OS). On first run after upgrading, Coda
> moves them to `~/.coda/credentials/` automatically and removes the old folder.
> DPAPI is keyed to the user (not the file path), so migrated tokens still decrypt.

### Telemetry & logging

Structured logging is **off by default**. When enabled, Coda writes one JSON-lines
file per session to `~/.coda/logs/`.

#### Enabling

**Settings block** (`~/.coda/settings.json`; a project `.coda/settings.json` block
replaces the user block wholesale, rather than merging field by field):

```json
{
  "telemetry": {
    "enabled": true,
    "level": "debug",
    "stderr": false,
    "retainedFiles": 7,
    "maxFileSizeMb": 20,
    "maxRunParts": 10
  }
}
```

| Key | Type | Default | Description |
|---|---|---|---|
| `enabled` | bool | `false` | Master switch |
| `level` | `trace`\|`debug`\|`info`\|`warn`\|`error` | `info` | Minimum level written |
| `stderr` | bool | `false` | Echo every log line to stderr as well |
| `retainedFiles` | int | `7` | How many past runs to keep on startup (0 = keep all) |
| `maxFileSizeMb` | int | `20` | Roll to a new part file when the current part exceeds this (0 = no cap) |
| `maxRunParts` | int | `10` | Max part files per run; oldest parts are dropped (ring buffer). 0 = unbounded |
| `directory` | string | `~/.coda/logs` | Override the logs directory |

**TUI command** — `/log` shows current state and the log directory; `/log <level>` sets
the level and enables logging (persists to user settings); `/log off` disables it;
`/log stderr on|off` toggles stderr echo. Changes apply to the next session.

**Headless flag** — `coda run --log-level <trace|debug|info|warn|error|off>` overrides
settings for that run.

**Environment overrides** (highest precedence; apply for one run only):

| Variable | Values | Effect |
|---|---|---|
| `CODA_LOG_LEVEL` | `trace`…`error` or `off` | Set level (or disable) |
| `CODA_LOG_STDERR` | `1`, `true`, `yes`, `on` | Echo to stderr |
| `CODA_LOG_FILE` | directory path | Override the logs directory |

#### Log files

Each session writes to `~/.coda/logs/coda-<yyyyMMdd-HHmmss>-<pid>.log`. When a part
exceeds `maxFileSizeMb` it rolls to `coda-….<n>.log`. At startup, runs beyond
`retainedFiles` (newest-first) are deleted. A single long run is bounded by
`maxRunParts`: once the limit is reached the oldest part is dropped (ring buffer).

Each line is a JSON object:

```json
{"ts":"2026-06-04T12:00:00.0000000+00:00","level":"Information","category":"LlmClient","message":"..."}
```

Fields: `ts` (round-trip UTC), `level`, `category`, `message`. When an exception is
logged, `exceptionType` and `exception` are added; the `stack` field is included only
at `debug` or `trace`.

#### Verbosity ladder

| Level | What is logged |
|---|---|
| `error` / `warn` | Failures — including the real API error message on a failed model request |
| `info` | Lifecycle events: session start, each model request start and completion |
| `debug` | As above, plus metadata (token counts, model id, elapsed time) |
| `trace` | As above, plus full request and response bodies |

#### Redaction

Secrets (auth tokens, API keys) are **always redacted** at every level. Auth headers
are never logged. Full request/response bodies appear only at `trace`.

> Even with telemetry **off**, a failed model request now surfaces the API's real
> reason (e.g. `Model request failed: … (HTTP 400): The requested model is not
> supported.`) instead of a bare HTTP status code.

## Try the sample

```bash
# Claude.ai
dotnet run --project samples/LlmAuth.Sample -- authurl   # print the exact authorize URL (no network)
dotnet run --project samples/LlmAuth.Sample -- login     # interactive Claude.ai sign-in (opens browser)
dotnet run --project samples/LlmAuth.Sample -- headers    # show the auth + identity headers
dotnet run --project samples/LlmAuth.Sample -- logout

# GitHub Copilot (device flow)
dotnet run --project samples/LlmAuth.Sample -- copilot-login    # prints a code to enter at github.com/login/device
dotnet run --project samples/LlmAuth.Sample -- copilot-headers   # show the Copilot bearer + editor headers
dotnet run --project samples/LlmAuth.Sample -- copilot-logout
```

## Notes & caveats

- **Windows-first.** The OAuth flow is cross-platform; only `DpapiTokenStore` is
  Windows-only. Other OSes implement `ITokenStore` (Keychain / libsecret) — stubbed
  for later.
- **Authorization:** the Claude.ai subscription sign-in is subject to Anthropic's
  Terms of Service. Using your own `ANTHROPIC_API_KEY` (via `ApiKeyProvider`) is the
  most straightforward option and is unaffected.
- GitHub Copilot and OpenAI providers are planned as additional `ICredentialProvider`s.
