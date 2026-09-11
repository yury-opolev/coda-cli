# Coda — Rust port

A Rust rewrite of Coda: the TUI, built on [ratatui](https://ratatui.rs) and
[crossterm](https://github.com/crossterm-rs/crossterm), plus the engine
subsystems beneath it.

## Approach

The rewrite follows a **strangler pattern** rather than a big-bang rewrite. The
`.NET` engine already exposes itself as a JSON-RPC 2.0 server over stdio
(`coda serve`, LSP-style `Content-Length` framing), so the Rust front-end
spawns that engine and drives it over the wire.

That means:

- the UI can be replaced and shipped independently of the engine;
- every wire assumption is checked against the real engine by the contract
  tests in `crates/coda-tui/tests/engine_contract.rs`, so a protocol change
  fails a test instead of silently breaking the UI;
- engine subsystems can be ported to Rust crate by crate underneath a UI that
  already works.

```
┌────────────┐   JSON-RPC 2.0 over stdio   ┌──────────────────┐
│  coda-tui  │ ──────────────────────────► │ coda serve (.NET)│
│  (Rust)    │ ◄────────────────────────── │  engine          │
└────────────┘   event/* notifications     └──────────────────┘
                 request/* prompts
```

## Crates

| Crate | Responsibility |
|---|---|
| `coda-proto` | Framing codec, JSON-RPC envelopes, and typed payloads for every `serve` method, event and server-initiated request. I/O free. |
| `coda-client` | Engine process supervision, duplex transport, request correlation, drop-safe responders. |
| `coda-render` | Text measurement, the theme, markdown, unified diffs, syntax highlighting, tool display modes. Terminal-agnostic. |
| `coda-tui` | State reducer, composer, viewport, keymap, the `Surface` abstraction and its stack, reusable form controls, drawing, and the application loop. |
| `coda-tool` | Leaf crate: the `Tool` trait, `ToolContext` and the path sandbox. Depends on nothing, so tool hosts need not pull in the engine. |
| `coda-llm` | Neutral chat model, SSE decoding, Anthropic and Copilot clients, retry policy, reasoning-capability resolution, and the `CredentialSource` seam. |
| `coda-agent` | The agent loop, 30 built-in tools, permissions, tasks, scheduling, hooks, subagents, compaction and the LSP client. |
| `coda-mcp` | MCP stdio client, server manager, and the shared `.mcp.json` config. |
| `coda-auth` | OAuth/PKCE and device-code flows, DPAPI/keyring/encrypted-file stores, single-flight refresh. |
| `coda-serve` | The engine host: pure method dispatch, the event bridge, server-initiated prompts, and the stdio transport. |
| `coda-diagnostics` | Leaf crate: the bounded, rotating JSONL diagnostic writer and the scoped async `DiagnosticContext`. Depends on nothing engine-specific (no `coda-auth`/`coda-agent`), so any process can wire it in at its own entry point. |
| `coda-boot` | Headless bootstrap shared by `coda serve` and `coda-engine`: diagnostics init/forward, product version, `ServeArgs` parsing + startup env translation, pure session-intent resolution, the cooperative `settings.json` writer, and the host-local `auth` command runner (provider picker, masked key prompt, safe browser launch). No TUI, rendering, clipboard, or agent-runtime dependency — `crates/coda-engine/tests/independence.rs` guards that with a `cargo tree` check. |
| `coda-diff` | Differential tests asserting the C# and Rust engines answer identically. |
| `coda` | The shipping binary: interactive, `serve`, `run` and `auth` modes. |
| `coda-engine` | Standalone, TUI-free binary: `coda-engine serve` (or a bare invocation) runs the same JSON-RPC-over-stdio core `coda serve` does. Not a second supported UX — `--engine`/`CODA_ENGINE` can point at it instead of `coda`, and both share `coda-boot`'s `ServeArgs`/`prepare` byte-for-byte. Also offers the same host-local `coda-engine auth` commands through the shared `coda-boot` runner. Does not daemonize, detach, or supervise; an external orchestrator/worker/bridge owns the persistent process it becomes. |

The dependency direction is strictly one way: `coda-tui → coda-render`,
`coda-tui → coda-client → coda-proto`, and `coda-agent → {coda-llm, coda-mcp}
→ coda-tool`. Nothing below `coda-tui` knows about application state, nothing
below `coda-client` performs I/O, and `coda-tool` is a leaf so that hosting a
tool never drags in the agent. `coda-boot` is one-way too: `coda-tui` and
`coda-engine` both depend on it (for diagnostics/version/`ServeArgs`), but it
never depends on `coda-tui` or `coda-serve` — a binary chains `coda_boot::
serve::prepare(&args)` with its own `coda_serve::serve_stdio()` call rather
than `coda-boot` calling the transport itself.

## Building and testing

```powershell
cargo build                       # build everything
cargo test                        # unit + integration tests
cargo test -p coda-render         # one crate
cargo run -p coda-tui --example preview   # render a sample session to stdout
```

The workspace uses cargo's MSRV-aware resolver (`resolver = "3"`) so
dependency resolution respects the declared `rust-version` (1.86).

Contract tests spawn a real `coda serve`. They **skip** when no engine is on
`PATH`, so the suite still runs on a machine without Coda installed. Point them
at a specific build with `CODA_ENGINE`:

```powershell
$env:CODA_ENGINE = "C:\path\to\coda.exe"; cargo test -p coda-tui --test engine_contract
```

## Running

```powershell
cargo run -p coda-tui                      # uses `coda` from PATH
cargo run -p coda-tui -- --engine ./coda.exe -C C:\some\repo
cargo run -p coda-tui -- --log-file coda.log --diagnostic-verbosity debug
```

An explicit `--engine`/`CODA_ENGINE` names somebody else's binary or a proxy,
so that session runs **API-only**: the engine-adjacent files on this machine
(MCP servers, plugins, marketplaces, and the engine-owned values in
`settings.json` — provider, per-provider model, per-model effort, permission
mode, custom headers, telemetry) are not the ones that engine reads, and are
therefore left alone rather than written with a claim that they took effect.
Session-scoped changes — `session/setModel`, `session/setPermissionMode`,
`session/setEffort` — are ordinary RPCs and keep working; only the promise
that they survive a restart is withheld, and said so. The default launch,
where this process starts its own core, behaves exactly as before.

Because stdout carries the protocol, engine diagnostics go to stderr and are
kept in a bounded ring for crash reporting, never persisted routinely. Every
launch — `coda`, `coda run`, `coda serve`, and standalone `coda-tui` — also
writes a bounded, privacy-safe operational diagnostic log by default, with no
flags required; see [Operational diagnostics](#operational-diagnostics) below
for the full contract.

## Design notes

**All state changes go through one reducer** (`state::UiState::apply`). UI
behaviour is therefore testable without a terminal, an engine, or any async
machinery: feed events in, assert on the state that comes out.

**Rendering is separated from layout.** `coda-render` produces `RenderLine`
values — text plus colour roles in *cell* coordinates — and `draw` maps those
onto ratatui spans. Width is measured per grapheme cluster and clamped to
`[1, 2]`, because summing per-character widths mismeasures ZWJ emoji.

**Colour is named, never literal.** Rendering code names a `Role`; the theme
resolves it to 24-bit or 16-colour depending on terminal capability.

**Keys resolve as a pure function** of the event plus UI context, so every
binding has a test.

**Every interactive overlay is a `Surface`** (`surface::Surface`). A surface
turns keys into a `SurfaceOutcome` and renders to `Line`s, and it **cannot
reach the engine** — no `App`, no async, no RPC, no I/O. That constraint is
what makes each one testable with a key event and an assertion; work that
needs the engine or the filesystem is requested as a `SurfaceAction` and
performed by `App::apply_surface_action`, the only bridge.

`SurfaceStack` routes keys to the top surface and renders bottom-up, so a
detail view sits over the list that opened it. A key the top surface declines
falls through to the global keymap, which is what keeps `Ctrl+C` working while
a surface is open.

**Placement is declared by the surface, not chosen by the caller** —
`Modal`, `Full`, `Split` or `Inline` — and it *degrades rather than clips*: a
split too narrow for two columns becomes a modal, a modal too small for its
chrome becomes full screen. A cramped terminal shows a usable surface instead
of a truncated one.

**A surface scrolls itself**, keyed off the focused element's row range rather
than the caret. A switch and a radio group have no caret, so a caret-based
scroll loses them exactly when they take focus.

**Glyphs live in one table** (`render::glyphs`), including composite forms such
as `(●)` and `"❯ "`. Assembling those at the call site is how a raw glyph gets
reintroduced. Two tests in `tests/conventions.rs` enforce this and the
Role-only colour rule; both are written to catch the escaped *and* the raw
spelling, because closing only one leaves the convention merely looking
enforced.

**Focus is three layered signals**: a background band across the focused
control (primary), an accent label, and a `❯` gutter marker (the fallback that
survives a terminal with no colour). Inversion is reserved for the *selected
row* inside a list, so focus and selection stay legible at the same time.

## Status

**`coda.exe` is a standalone binary that no longer needs .NET.** It ships the
TUI, the engine, and a headless mode:

```
coda                  interactive TUI
coda serve            JSON-RPC engine over stdio
coda run -p "<task>"  headless one-shot
coda auth <cmd>       host-local provider sign-in (status | login | logout)
```

Interactive mode drives the engine over the same JSON-RPC seam, defaulting the
engine to this same executable. Running the agent in-process would be slightly
faster but would bypass the boundary the parity tests exercise, so it is
deliberately not done.

`auth` is deliberately *not* a serve-API method. Connecting an account touches
this machine's credential store and this user's browser, which an external
application supervising Coda does not own. `coda auth` and `coda-engine auth`
are the same runner (`coda-boot`'s `auth_cli`), so both binaries offer the same
commands, the same disclosures and the same exit codes:

```
0    the command did what it said
1    an operational failure (store, network, provider, commit)
2    invalid usage (a rejected option, a missing provider, unusable input)
130  the user cancelled
```

An API key is never a command-line argument: it is read from a masked terminal
prompt, or — when there is no terminal — only from an explicitly requested
`--api-key-stdin`. Asking for `--api-key-stdin` *while stdin is a terminal* is
refused rather than honoured, because typing into it would echo the key. A
terminal that reports itself as one but cannot suppress echo (mintty/msys) is
also refused, with a pipe-based alternative rather than an instruction to type
the key somewhere visible.

`auth login api-key` always checks the key against the provider before it
commits, and `auth login api-key --use-env` selects the exported
`ANTHROPIC_API_KEY` without storing anything. Both send the key to whatever
`ANTHROPIC_BASE_URL` resolves to — disclosed by host before the key is sent
anywhere, again after the commit, and in `auth status`. See
[Anthropic API-key endpoint](#anthropic-api-key-endpoint).

Authorization URLs and device codes are printed to the ephemeral CLI surface;
an `auth` command never opens the diagnostic log. Set `CODA_AUTH_NO_BROWSER` to
a non-empty value to keep it from launching a browser (the challenge is still
printed, so a fixture-driven login is fully drivable).

Cancellation means three different things and the command says which one
happened. At a prompt, `Esc`/`Ctrl-C` cancel and the terminal mode is restored
— `crates/coda-boot/tests/console_real_terminal.rs` proves that against a real
console, by measuring the console mode before, during and after. While a login
is being prepared, cancelling drops the flow (closing the loopback listener and
the device poller) and exits `130` with nothing written. The **commit** is
deliberately uninterruptible, so it always reaches a terminal outcome. The
verification that follows is interruptible again — and if it is interrupted,
the report says the credential is saved and simply unverified, never that the
sign-in did not happen.

### Parity with the C# engine

Two independent checks, both green:

- **Contract tests** (`coda-tui/tests/engine_contract.rs`) run against either
  engine via `CODA_ENGINE`. Six tests, identical assertions, both pass.
- **Differential tests** (`coda-diff`) drive both engines with an identical
  request sequence and compare normalised responses. **Zero undeclared
  divergences** on the shared protocol; `KNOWN_GAPS` is empty. The one
  capability-dependent behavioural difference (`session/setEffort` on an
  indeterminate model, below) is pinned explicitly, not swept into an exclusion.

The differential suite covers the deterministic surface — handshake, history,
models, listings, errors, goals, effort, schedules. It excludes live model
turns on purpose: a provider's output is not reproducible, and a flaky parity
test is worse than none.

#### Hermetic isolation (no real profile, no auth, no network)

The comparison is only honest if it is hermetic, and `serve` reads far more than
its working directory: both engines probe `~/.coda` (settings, credentials, the
model cache, skills, plugins, hooks) and, given a credential, fetch models over
the network. On Windows setting `USERPROFILE` does **not** redirect these — both
the Rust `directories` crate and C# `SpecialFolder.UserProfile` resolve the
profile from the user token, not the environment (verified empirically). So the
harness closes every seam with explicit application-level overrides:

- **Home / settings / credentials / cache / skills / plugins** → an empty temp
  root via `CODA_HOME` (the Rust engine's profile-root override) and
  `CODA_SETTINGS_DIR` (the C# settings seam). The credential directory it
  creates is empty, so neither engine finds a credential or builds a live
  client, and no OS keyring is probed.
- **Model catalogue** → pinned to a small deterministic fixture via
  `CODA_MODELS_PATH` (honored by both engines), with the background models.dev
  refresh disabled via `CODA_DISABLE_MODELS_FETCH`. With no credential the model
  list resolves from that fixture, so both report `source: "catalog"` with the
  same ids — never a live, network-derived list.
- **Inherited credentials/config** (`ANTHROPIC_API_KEY`, `CODA_SERVE_API_KEY`
  and every other `CODA_SERVE_*`) are **removed** from the child environment —
  not blanked, because an empty variable is still present.

The C# engine resolves its provider only from `--provider` or a stored
credential (a settings `defaultProvider` is not a selector), so under the
credential-free profile it is started with `serve --provider github-copilot`;
this is offline-safe (with an empty credential store the copilot client throws
locally before any HTTP call). The Rust engine needs no such flag and is not
given one, so it never runs the provider credential probe.

Isolation is then **verified, not assumed**: `session/models` must report
`source: "catalog"`, the sentinel fixture model, the fixture provider, and
exactly the fixture catalogue's ids on *both* engines — a leak into the real
`~/.coda`, a live client, or a network fetch would change one of those.

#### Running the differential suite

The installed `coda` on `PATH` is now the **Rust** engine, so the C# reference
must be built and named explicitly — resolving it from `PATH` would compare the
Rust engine against itself. Both engines share `version.json`, so an equal
`--version` is expected and is never used to tell them apart; identity is
established by artifact (a .NET `Coda.Tui` metadata sibling, plus a
path/content guard that rejects the *same* binary on both sides).

```powershell
# 1. Build the legacy C# reference into an isolated directory. This does NOT
#    bump the version and leaves the primary publish/tool outputs untouched
#    (avoid `.\build.ps1 -Legacy`, which bumps unless given -NoBump):
dotnet publish src\Coda.Tui\Coda.Tui.csproj -c Release -o artifacts\legacy-parity

# 2. Build the Rust engine:
cd rust; cargo build --release -p coda

# 3. Unit tests (normalisation, extension projection, identity guard) run in the
#    routine suite; the cross-engine comparison is a separate opt-in:
cargo test -p coda-diff                       # unit tests; parity test reported "ignored"
$env:CODA_CSHARP_ENGINE = "..\artifacts\legacy-parity\Coda.Tui.exe"
cargo test -p coda-diff --test parity -- --ignored --nocapture
```

`CODA_RUST_ENGINE` overrides the Rust binary (default: `target/release/coda.exe`);
`CODA_CSHARP_ENGINE` accepts the `Coda.Tui.exe` apphost or the `Coda.Tui.dll`
(run through `dotnet`). The opt-in test is `#[ignore]` so a routine
`cargo test --workspace` never runs it, and it **fails loudly** — never
skips-with-success — when opted in without a valid reference.

**Rust protocol extensions.** The Rust engine is a *superset* of the C# contract
on three methods: `session/models` rows carry `reasoningLevels`/`inputCost`/
`outputCost`/`effort`; `model/reasoningCapability` adds `current`/
`indeterminate`/`model`/`providerId`; `session/setEffort` adds the effective
`current` level. `session/setSystemPrompt` is Rust-only (the C# engine returns
`-32601`). These are declared explicitly in `RUST_EXTENSIONS`, projected out of
the common comparison so the shared contract is compared for exact equality, and
independently shape-checked by the extension schema tests — they are *verified*,
not blanket-ignored. The active `model`/`providerId` on `session/models` are
now sent by both engines and are compared as common fields.

**One documented directional divergence.** Applying a *valid* effort level
(`session/setEffort` with e.g. `medium`) requires the active model's reasoning
capability. Under the isolated profile the `github-copilot` model is
*indeterminate* (no live model list confirms its levels), and the engines
legitimately differ: the C# engine will not apply a level it cannot verify
(`ok:false`), while the Rust engine is optimistic under indeterminacy
(`ok:true`). This depends on a live model list, so — like live model output — it
is out of scope for the deterministic comparison; the parity test pins both
directions (`assert_indeterminate_effort_divergence`) so a change on either side
fails loudly rather than being absorbed by a blanket exclusion.

Differential testing has been worth more than its cost. Three times a Rust
unit test had pinned the *wrong* value and so agreed with a bug —
`serverInfo: "coda-serve"` where C# says `"coda"`, an absent `setEffort` note
where C# sends `""`, and a hook that approved a fail-closed gate. A test
written beside an implementation tends to encode that implementation's
assumptions; only cross-checking against the reference breaks the circularity.

### What remains

- session transcript export/import;
- two slash commands (`/import`, and `/rewind`'s server-side truncation);
- the 30 FPS frame throttle and the assistant-buffering mode, including its
  withhold-on-interrupt rule;
- **real-model validation.** The engine has been exercised by its own tests and
  by the parity suite, not by sustained real use. That is the one gap no test
  closes.

**Not yet supported (flags accepted-and-rejected, never silently ignored):**

- `--yolo-safe` — classifier-gated bypass mode is not wired; the flag aborts
  with an explicit error message rather than silently mapping to unrestricted bypass.
- `--image` — image input is not yet implemented.
- Session telemetry, transport extras beyond what the serve protocol carries.

### MCP editor (`/mcp add` / `/mcp edit`)

The editor stores servers in `.mcp.json` for the chosen scope (user or project).
Fields are driven by transport — stdio shows Command, Arguments and Environment;
HTTP shows URL and Environment — so the form never offers fields the loader
would discard.

Existing UTF-8 `.mcp.json` and TUI settings files are accepted with or without
a leading byte-order mark (BOM), including files written by the C# version.
Loading does not rewrite the file; malformed JSON still reports an error.

**Arguments** are edited as a JSON array (e.g. `["-y", "server"]`). This
preserves arguments that contain spaces, quotes, backslashes or Unicode exactly,
without any shell-quoting heuristics that would silently corrupt Windows paths.

**Environment** is edited as a JSON object mapping names to string values, e.g.
`{ "API_KEY": "coda-secret:store/key" }`. A JSON object is used rather than
`KEY=VALUE` lines so values containing `=`, leading/trailing whitespace,
newlines or Unicode round-trip exactly. The editor shows the *unresolved*
values as written in `.mcp.json` — `coda-secret:store/key` references appear
as-is and are never resolved to actual secrets in the UI. Names must be
non-empty and free of whitespace, `=` and NUL; on Windows they are treated
case-insensitively. Press `Ctrl+Enter` (or `Tab` out of a text box, then
`Enter`) to save.

**Saving** is keyed to the exact `(scope, name)` the editor opened on, so a
user entry shadowed by a project entry of the same name is never confused for
its neighbour. Within one scope the write is atomic; a move across scopes
writes the destination first and only then removes the source, reporting the
half-done state if that removal fails. Adding, renaming or moving onto a name
that already exists in the target scope is rejected as a collision. Unknown
fields (auth, headers, future settings) are preserved from the exact original
across every save, edit and move, and the transport marker (`type`) is
rewritten to match the chosen transport so a switched server cannot keep a
contradictory one.

Restart the engine after saving to connect the new or updated server.

### Thinking display

The chat shows `Thinking... 9s` (then `Thinking... 1:05`) as soon as the
provider announces a reasoning block, without waiting for summary text.
Completion changes it to a foldable `Thought` header when visible text exists.
Encrypted-only reasoning remains a header without an empty expand control.
Copilot Responses requests with an explicit effort also request the provider's
reasoning summary; encrypted reasoning content is never displayed.

### CLI reference (Rust parity flags)

Normal turns allow up to 500 tool-use iterations, matching C# Coda. This is a
runaway-loop backstop, not a limit of 500 individual tool calls: one iteration
can execute several calls. Reaching it ends the turn with a recoverable notice.
Goal-driven runs retain their separate budget controls.

YOLO (`--yolo` or `bypassPermissions`) also permits built-in file tools to
access paths outside the working directory, matching C# Coda. The main agent
and subagents share the live mode: returning to Default, Plan, or AcceptEdits
restores the outside-directory restriction for subsequent tool calls.
Explicit tool restrictions remain in force.

All startup overrides are session-only and never written to `settings.json`.

```
coda [--model <ID>] [--provider <ID>] [--effort <LEVEL>]
     [--permission-mode <MODE>] [--yolo]
     [--goal <TEXT>] [--goal-timeout <DUR>] [--max-continuations <N>]
     [--system-prompt <TEXT>] [--system-prompt-file <FILE>]
     [--continue|-c] [--resume|-r [<ID>]] [--fork|-f [<ID>]]
     [--log-file <FILE>] [--diagnostic-verbosity normal|debug|trace]

coda run -p "<task>" [--model <ID>] [--provider <ID>] [--effort <LEVEL>]
     [--permission-mode <MODE>] [--yolo]
     [--goal <TEXT>] [--goal-timeout <DUR>] [--max-continuations <N>]
     [--system-prompt <TEXT>] [--system-prompt-file <FILE>]
     [--continue|-c] [--resume|-r [<ID>]] [--fork|-f [<ID>]]
     [--json] [--cwd <DIR>]
     [--log-file <FILE>] [--diagnostic-verbosity normal|debug|trace]

coda serve [--model <ID>] [--provider <ID>] [--effort <LEVEL>]
     [--permission-mode <MODE>] [--yolo]
     [--goal <TEXT>] [--goal-timeout <DUR>] [--max-continuations <N>]
     [--system-prompt <TEXT>] [--system-prompt-file <FILE>]
     [--api-key <KEY>] [--endpoint <URL>]
     [--cwd <DIR>] [--no-mcp] [--no-project-mcp]
     [--log-file <FILE>] [--diagnostic-verbosity normal|debug|trace]
```

`--endpoint` requires `--api-key`; using it without one is rejected at parse
time. `--goal-timeout` and `--max-continuations` require `--goal`. `--yolo`
and `--permission-mode` are mutually exclusive.

Behaviour notes:

- `--provider <ID>` selects the credential for that account at engine startup
  and **fails closed** if it is unavailable — it never silently connects a
  different provider. Aliases resolve to the canonical `coda_auth` ids:
  `anthropic` / `api-key` → Anthropic console key, `claude` / `subscription`
  → `claude-ai`, `copilot` / `github` → `github-copilot`. Without `--model`
  the saved default model for that provider is used.
- `--effort` is applied **after** the model is settled (it is recorded
  per-model, so applying it before a model switch would silently drop it).
  `--effort auto` explicitly clears effort rather than falling back to the
  saved per-model preference.
- `--system-prompt` / `--system-prompt-file` **fully replace** the engine's
  built-in system prompt for the session; the text is not appended to it.
- `--endpoint` is retained for the whole session: a later `initialize(apiKey)`
  rebuilds the client at the configured endpoint rather than reverting to the
  default host. It still requires `--api-key`; to redirect a *stored* or
  *exported* key, set `ANTHROPIC_BASE_URL` instead — see
  [Anthropic API-key endpoint](#anthropic-api-key-endpoint).
- Invalid startup values (bad `--effort`, unknown `--permission-mode`, a
  non-positive `--goal-timeout`, a negative `--max-continuations`, or an
  invalid `--endpoint`) fail startup with an error instead of being silently
  defaulted or clamped. An `ANTHROPIC_BASE_URL` that fails validation fails
  startup only for a session that would spend an Anthropic API key, and is
  never quietly replaced by the default host — see
  [Anthropic API-key endpoint](#anthropic-api-key-endpoint).
- `--log-file`/`--diagnostic-verbosity` are always available and never opt
  in the essential diagnostic log itself, which is written regardless — see
  [Operational diagnostics](#operational-diagnostics).

### Anthropic API-key endpoint

Anthropic console-key requests go to `https://api.anthropic.com` unless
`ANTHROPIC_BASE_URL` says otherwise — the variable the Anthropic ecosystem
already uses, for a gateway, a corporate proxy or a compatible self-hosted
deployment.

One resolver (`coda_auth::service::endpoint`) decides this for the whole
product, so the login's mandatory pre-commit credential check, the post-commit
connection check and the engine that follows all send the key to the *same*
host. Precedence, highest first:

1. `coda serve --endpoint <URL>` (still requires `--api-key`);
2. `ANTHROPIC_BASE_URL` from the process environment;
3. `https://api.anthropic.com`.

```powershell
$env:ANTHROPIC_BASE_URL = "https://anthropic.gateway.internal"
coda auth login api-key      # the key is validated against the gateway
coda serve                   # and spent there
```

**Scope, and what it is not**

- **Anthropic API keys only.** A Claude.ai subscription and GitHub Copilot
  resolve their own endpoints and never see this variable; routing one
  provider's token to another's host is exactly what this must not do.
- **This process only.** It configures the process that reads it. It is not
  persisted, it does not persist a key, and it does not travel — a CLI, an
  engine and a TUI that must all reach the same gateway each need it exported
  in their own environment.
- **Not a place to put a secret.** A query string, a fragment and embedded
  `user:password@` credentials are all refused, and only the host is ever
  printed (`coda auth login`'s disclosure, `coda auth status`, diagnostics) —
  never the configured URL, whose path could carry a tenant id.

**Validation**

`https` to any host, `http` only to a literal loopback address (`localhost`,
`127.0.0.0/8`, `::1`) — plaintext to anywhere else would put the API key on the
wire in clear. Backslashes, control characters and any other scheme are
refused. A trailing slash is normalised away; a base path is kept.

A non-empty value that fails validation is an **error**, never a fallback to
`https://api.anthropic.com`: `coda auth login api-key` refuses before anything
is sent or written, `coda serve` refuses to start *when it would spend an
Anthropic API key* (`--provider anthropic`, an explicit `--api-key`, or a
`initialize(apiKey)` handed to it later), and `coda auth status` reports the
fault without contacting anyone. A user who pointed Coda at a gateway must
never discover their key went to Anthropic instead.

The refusal is scoped the same way the variable is. A session signed in to
GitHub Copilot or Claude.ai starts, and runs, normally — those providers
resolve their own endpoints — exactly as `coda auth login copilot` succeeds
with the same value set. The refusal is not forgotten in that case: it is kept
for the life of the process, so an engine that started as Copilot still refuses
a later `initialize(apiKey)` rather than sending that key to the default host.

`coda auth status` always names the host in force, so "which endpoint am I
actually configured for?" is answerable without starting a session.

### GitHub Enterprise Copilot

Enterprise tenants serve device-code auth, token exchange, and inference from
their own hosts. The engine resolves the full endpoint set from a single domain
name; no per-URL configuration is needed.

**Setting the domain**

The domain can be set in two ways, with the environment variable taking
precedence if both are present:

```powershell
# 1. Process environment — wins over the saved setting when non-blank:
$env:GH_COPILOT_ENTERPRISE_DOMAIN = "octocorp.ghe.com"
coda

# 2. Persisted in settings — read at startup on every run:
#    Write "githubEnterpriseDomain": "octocorp.ghe.com" to ~/.coda/settings.json
#    The exact key is camelCase; an existing C# profile needs no migration.
```

The Rust engine reads `githubEnterpriseDomain` directly from
`~/.coda/settings.json` at startup (BOM-tolerant, honors `CODA_HOME`). No
C# layer or environment mutation is involved; the resolver passes a combined
lookup to `AuthCopilotConfig::from_env_lookup`: env wins if non-blank,
otherwise the saved setting is used.
Unreadable or malformed settings stop Copilot client construction rather than
silently selecting public endpoints.

**What the domain configures**

| Derived URL | Value |
|---|---|
| Device-code request | `https://<domain>/login/device/code` |
| OAuth token endpoint | `https://<domain>/login/oauth/access_token` |
| Copilot token exchange | `https://api.<domain>/copilot_internal/v2/token` |
| Inference base URL | `https://copilot-api.<domain>` |

The client id and editor identity headers (`editor-version`, `editor-plugin-version`,
`copilot-integration-id`, `user-agent`) are shared with the public github.com
configuration and do not need to be overridden for most enterprise tenants.

**Per-endpoint overrides**

A tenant that proxies only one endpoint can override it without restating the
rest:

```powershell
$env:GH_COPILOT_ENTERPRISE_DOMAIN = "octocorp.ghe.com"
$env:GH_COPILOT_API_BASE_URL      = "https://proxy.internal/copilot"
```

Full override variables: `GH_COPILOT_API_BASE_URL`, `GH_COPILOT_COPILOT_TOKEN_URL`,
`GH_COPILOT_DEVICE_CODE_URL`, `GH_COPILOT_TOKEN_URL`, `GH_COPILOT_CLIENT_ID`,
`GH_COPILOT_EDITOR_VERSION`, `GH_COPILOT_PLUGIN_VERSION`,
`GH_COPILOT_INTEGRATION_ID`, `GH_COPILOT_USER_AGENT`.
Set `GH_COPILOT_USE_EXCHANGE=false` to skip the Copilot token exchange and use
the raw GitHub OAuth token directly (reduces model entitlement on most tenants).

**Domain validation**

The domain must be a bare `host[:port]` — no scheme, path, query, fragment, or
embedded credentials. A value like `evil.com/@octocorp.ghe.com` or
`octocorp.ghe.com/path` is rejected at startup with an explicit error rather
than falling back to the public github.com default, which would silently send
the enterprise token to the wrong host.

If you accidentally paste the inference host (`copilot-api.octocorp.ghe.com`)
instead of the GHE host, the `copilot-api.` prefix is stripped automatically
so every derived URL stays consistent.

**Startup credential diagnostics**

Unlike a missing credential (which is silent — "not signed in"), a
token-exchange failure or keyring error at startup is reported as a
provider-specific message on the first prompt:

```
GitHub Copilot credential error: token refresh failed (HTTP 401).
Check credentials and provider configuration, then restart Coda.
```

Response bodies, raw store/transport error text, and credential-bearing URLs
are never included. Messages identify the failure category and HTTP status
when available. To recover, check credentials, configuration, and connectivity,
then restart Coda.

The diagnostic is scoped to the current engine instance — a failed probe on
one session does not contaminate another.

## Operational diagnostics

Every ordinary launch — `coda` (interactive), `coda run`, `coda serve`, and
standalone `coda-tui` — writes a small, bounded, privacy-safe JSONL log by
default. This is **not** opt-in and is not controlled by the legacy
`telemetry.enabled` setting: essential lifecycle and failure records are
always written, because a session that fails silently is worse than one that
leaves bounded operational metadata. It lives in the `coda-diagnostics`
crate (a leaf with no `coda-auth`/`coda-agent` dependency), which every
process wires in at its own entry point.

**What it is not**: a tracing sink. The previous behavior — an opt-in
`tracing_subscriber` file writer enabled by `--log-file` — has been replaced
outright, not layered underneath. Existing `tracing::warn!`/`debug!` call
sites in the transport/retry/stderr code this work touched had their payload
fields (raw frame bytes, provider error text, stderr content) scrubbed, but
`tracing` itself has no default subscriber and is not a diagnostics channel.

**Default location**: `<CODA_HOME or OS home>/.coda/logs/diagnostics/`. Each
process creates its own uniquely-named file,
`coda-<utc-timestamp>-<pid>-<uuid>.jsonl`; two processes never share a file.

**Bounds**: each segment is capped at 5 MiB, with at most 4 segments retained
per process stream (rotated like `logrotate`: `.1`, `.2`, `.3`); each JSON
record is capped at 8 KiB, enforced *before* writing — an oversized record is
replaced with a minimal, still-valid `record_dropped` marker rather than
truncated JSON. Inactive default-directory files are swept on startup and
rotation: older
than 7 days, or oldest-first once the inactive total exceeds 100 MiB. An
OS-backed advisory lease (a `.lock` sidecar, via `fs2`) protects a long-idle
but still-live stream from being mistaken for garbage; retention only ever
touches recognized `coda-*.jsonl` filenames, never a symlink or an unrelated
file.
New Unix diagnostic directories are created with mode `0700`; new data and
lease files use `0600`. Existing parent-directory permissions are not changed.

**Verbosity**: `--diagnostic-verbosity normal|debug|trace` on every entry
point. The legacy `--log-filter`/`CODA_LOG` (a `tracing` `EnvFilter` string)
is still accepted as a **compatibility hint** — only its loudest named level
(`trace` > `debug` > anything else) maps to a verbosity default, and it never
enables arbitrary raw tracing output. An explicit `--diagnostic-verbosity`
always wins.
Debug and Trace add model-request start/end records; essential lifecycle,
HTTP failure/retry, and error records remain present at Normal verbosity.

**Explicit destinations**: `--log-file <path>` still works, but its contract
changed to match a real log file's: it *appends* rather than truncates, and a
second writer pointed at the same path fails clearly (an OS-backed exclusive
lock on a stable `<path>.lock` sidecar, released automatically even on a
crash). An invalid explicit destination is a hard startup error. An
unavailable *default* directory is the opposite — nonfatal: the app stays
usable, logging enters a degraded state, and a one-time, payload-free warning
is printed (never silently swallowed).

**Parent/child correlation**: a frontend (`coda`/`coda-tui`) that launches an
engine child forwards its run id, its resolved directory (an explicit
destination's own directory, or the default directory — never the parent's
exact file), and its verbosity via `CODA_DIAG_RUN_ID`/`CODA_DIAG_DIR`/
`CODA_DIAG_VERBOSITY`. The child always writes its own distinct file there,
sharing the run id so the two can be correlated after the fact. The engine
reports its own resolved log path back over the wire
(`InitializeResult.telemetryLogPath`, previously always `null`); `/log` shows
both the frontend's own path/mode/verbosity/health and the engine's reported
path — real state, not just the legacy telemetry settings (which `/log`
still shows separately, clearly labeled, since they do not control this).

**What is recorded**: a fixed envelope (schema version, UTC time, product
version, process role/PID, run id, and optional session/turn/internal-request
id, canonical provider/model) around a closed set of typed events — process
and engine start/end, session initialized/resumed, turn start/end/failure,
HTTP attempt/result/retry/recovery, and streamed/transport/protocol failure.
HTTP metadata (status, a validated bounded provider request id, duration) is
captured in the shared retry loop *before* the response body is consumed —
the only place it is available. Structured error fields are allowlisted;
unknown provider text is never copied into the log.

**Request shape, routing provenance, and structured error detail** (added
for the model-specific-HTTP-400 diagnostic gap): at Normal verbosity (not
gated to Debug/Trace), every physical HTTP attempt against a provider now
also records:

- `request_shape` — recorded immediately before the request is executed,
  keyed by the envelope's own `request_id` plus a `dispatch: u32` (which
  physical endpoint/body choice — a Copilot chat-completions mismatch
  reroute is `dispatch: 2`) and the legacy per-dispatch `attempt` counter.
  Carries the fixed wire `protocol` (`anthropic_messages`,
  `copilot_chat_completions`, `copilot_responses`, `copilot_messages`), a
  fixed `route_source` explaining *why* that endpoint was chosen
  (`fixed_provider_default`, `model_metadata`, `metadata_missing_default`,
  `metadata_unrecognized_default`, `reroute_after_mismatch`), and a
  `RequestShape`: bounded counts (`message_count` — serialized `messages`/
  `input` wire items, not a claim about user turns; `tools_count`, both
  saturating at 10 000) and boolean presence flags
  (`system_present`, `stream_requested`, `reasoning_present`,
  `max_tokens_present`, `max_output_tokens_present`,
  `max_completion_tokens_present`, `temperature_present`,
  `tool_choice_present`, `parallel_tool_calls_present`,
  `response_format_present`) computed from the *actual final serialized
  JSON* passed to the HTTP client — never re-derived from the caller's own
  request object, and never a value or key from the user's own
  messages/tools. `body_bytes` is the exact length of the already-built
  wire body (via `reqwest::Body::as_bytes`, not a second
  `serde_json::to_string`); it is `None` — never an invented `0` — only when
  the request itself failed to build (no bytes ever existed to measure). A
  retried attempt within the same dispatch gets its own `request_shape`
  record with the identical shape/bytes (the retried body did not change); a
  reroute gets a fresh one for its new endpoint/body. For Chat Completions,
  `system_present` also detects system-role items inside `messages`, and
  `message_count` includes those items. Presence flags describe the serialized
  body, not requested capabilities: options not emitted by a builder remain
  false even when the schema reserves a flag for them.
- `http_failure_details` — recorded once a non-2xx response's body has been
  read, keyed by the same `dispatch`/`attempt`. Carries a bounded,
  allowlisted structured extraction of the error body (`ErrorBodyDetail`):
  `body_kind` (`json`/`non_json`/`empty`/`unreadable`/`oversized` — a
  genuine body-*read* failure is `unreadable`, never conflated with an
  ordinary empty body), and, when the body is valid JSON, three
  independently-classified fields — `error_type`, `error_code`, `parameter`
  — each a `{state, value?}` pair: `recognized` (value is one of the fixed
  allowlists below), `unrecognized` (present, valid JSON, but not on the
  allowlist — the actual value is never recorded), `missing` (valid JSON,
  field absent), `omitted` (present but excluded for exceeding a 64-byte
  bound — the same bound the older `parameter`-only extraction already
  used), or `unavailable` (the body itself was not JSON/was empty/could not
  be read, so the field cannot even be evaluated). The whole body is capped
  at 64 KiB for this extraction (`oversized` beyond that; the full,
  unbounded body is still kept only in memory for existing
  retry/`LlmError` behavior, unaffected by this cap). Recognized `type`
  values: `invalid_request_error`, `authentication_error`,
  `permission_error`, `not_found_error`, `rate_limit_error`,
  `overloaded_error`, `api_error`, `server_error`, `model_error`,
  `billing_error`, `insufficient_quota`. Recognized `code` values:
  `model_not_found`, `context_length_exceeded`, `unsupported_parameter`,
  `unsupported_value`, `invalid_value`, `missing_required_parameter`,
  `invalid_api_key`, `rate_limit_exceeded`, `insufficient_quota`,
  `content_filter`, `tool_use_failed`. `parameter` reuses the existing
  bounded, pattern-validated allowlist (top-level names plus the
  `input[N].{summary,id,encrypted_content}` shape). The provider's free-text
  `message` is never extracted, at any state. The writer revalidates error
  fields against the same allowlists, including directly constructed events;
  the older `stream_failure.parameter` field uses the same 64 KiB body cap.
- `stream_opened` — recorded once a physical attempt's 2xx response/headers
  are accepted, immediately before streaming/decoding begins, carrying the
  same `dispatch`/`protocol`/`route_source`. Any failure recorded after this
  point for the same context is necessarily post-headers (an inline
  provider error event, a truncated/invalid stream, a mid-stream transport
  drop) — never the provider rejecting the request outright.
- `request_failure` — replaces what used to be misfiled as `stream_failure`
  for a failure that happens *before* any `stream_opened` was ever recorded
  for the context: the shared HTTP retry policy already exhausted every
  physical attempt (or the request could not be built/sent) without ever
  seeing a 2xx. Carries `category`/`status` plus the same optional
  `ErrorBodyDetail` as `http_failure_details`. If no `http_attempt`/
  `request_shape` appears at all under the same `request_id`, the failure
  happened locally (e.g. a credential lookup) rather than as a provider
  refusal — this crate does not attempt to distinguish that further.
- `stream_failure` (existing event, extended) now also carries an optional
  `detail: ErrorBodyDetail`, populated from the same bounded extractor —
  including for a failure that arrived as an *inline* SSE `error`/
  `response.failed` event rather than a non-2xx HTTP status (the Anthropic
  Messages and Responses decoders now preserve that inline event's raw JSON
  in the error's `body` for exactly this purpose; the free-text `message`
  they already exposed is unchanged).

**Known, honest limitations of this diagnostic layer**: it identifies
*which* structured error shape a provider returned and exactly what was
sent — it does not, and cannot, diagnose the underlying cause of any
specific model's HTTP 400. An unrecognized `type`/`code`/`param` is recorded
as `unrecognized`, not resolved to a friendly label. `GET /models`
(preflight/auth/model-discovery) requests are outside the inference retry
loop and are **not** covered by `request_shape`/`http_failure_details`/
`stream_opened` — only an actual inference dispatch is. Raw request/response
bodies are never persisted, at any verbosity, including Trace: there is no
raw-dump mode. A test client that bypasses the shared HTTP retry loop (as
several `coda-agent` unit tests do, on purpose, to isolate the agent's own
retry arms) correctly produces no `request_shape`/`stream_opened` at all —
an unknown/fake transport is not fabricated a route. This work does not
change `LlmError::from_model_discovery_status`'s existing 403-vs-401
model-listing/auth behavior (unchanged since 0.1.147) in any way.

**What is never recorded, at any verbosity**: prompts, system prompts, tool
names/arguments/results, response/request bodies or headers, encrypted
reasoning, raw `stderr`, or any error's `Display`/`Debug` text — several of
those can carry credential-bearing URLs or arbitrary provider text, which is
exactly why they are excluded structurally (typed fields only, no free-text
`message`) rather than filtered by log level.

**Not the same as a content audit.** A content-audit sidecar (when and where
one exists) is a deliberately separate mechanism for capturing conversation
content; this crate does not wire one up, and no session is guaranteed to
have one. Operational logs exclude conversation content, but still contain
local paths and session/provider identifiers; review them before sharing.

## The two seams

`coda serve` is not the only interface to the engine. Much of what the TUI
needs also lives in JSON under `~/.coda` that both processes share, and using
both seams is what makes the front-end genuinely useful rather than read-only:

| Seam | Used for |
|---|---|
| `serve` JSON-RPC | turns, streaming, tool events, prompts, models, schedules, skills, plugins, hooks |
| Local files | MCP configuration, task logs, settings, plugin state |
| Host-local maintenance | signing in, switching account, signing out — the credential store and the two auth-owned settings keys |

Authentication is deliberately *not* on the protocol. Signing in touches this
profile's credential store and this user's browser, so an external application
supervising the engine owns its lifecycle but never its keychain. `/login`,
`/provider <id>`, `/logout` and `/setup` therefore go through the same
`coda-auth` service `coda auth` uses, and refuse outright — **before** opening
a credential store — when the front-end was pointed at an engine it did not
start (`--engine`/`CODA_ENGINE`), naming `coda auth` on the engine host
instead.

A credential change is a lifecycle operation, not a settings edit. The order is
fixed: disclose where the *chosen* account will authorize (host only, from the
service's own resolved configuration), prepare (cancellable, writes nothing,
the running engine stays up), stop **and await** the engine this process owns,
commit the credential and settings in one transaction, then start a fresh
engine told explicitly which account and which Copilot deployment to use and
resume the same session. Merely writing `defaultProvider` and restarting —
which is what `/provider` used to do — changes nothing about which credential
the engine can find.

Every slow step runs on a task and reports back to the loop, so the terminal
keeps drawing and `Ctrl+C` keeps working while a browser is open, a device code
is being polled, an engine is going away or a credential is being written. The
transaction itself is never cancelled, and closing the application awaits it
rather than leaving a half-written profile. Work reported by a sign-in the user
cancelled is dropped rather than adopted by whatever started next.

**Before the engine exists.** A first run — or a launch whose explicitly named
provider (`--provider`, `--engine-arg --provider`, `CODA_SERVE_PROVIDER`) has
no usable credential — is offered the same setup screen *before any engine is
spawned*, by both `coda` and the standalone `coda-tui`. It runs without an
engine, a connection or an application: the surfaces and the flow are the ones
`/login` uses, and the loop around them is the launcher's. Cancelling starts no
engine and changes nothing; connecting an account rebuilds the launch to name
it, and a launch that then fails to start says exactly that — "credentials
saved; engine startup failed" — rather than reporting a failed sign-in. A
session pointed at somebody else's engine (`--engine`/`CODA_ENGINE`) skips all
of this without opening a credential store at all, and `coda run` and
`coda serve` remain fail-closed: a non-interactive process must not wait on a
person who is not there.

Settings are read once at engine start, so changing one only takes effect
across a restart. `initialize` accepts a session id, so the front-end restarts
the engine in place and resumes the same conversation — which is how switching
model works without a protocol addition.

Writers preserve keys they do not model. The engine stores settings this
front-end knows nothing about, and a round-trip through a typed struct would
silently delete them.

### Behaviours worth knowing

Several rules were reconstructed from the C# implementation because the wire
protocol does not imply them, and getting them wrong is silently wrong:

- A tool batch may only be **extended while it is still the last block**. Text
  between two tool calls opens a new batch, so a result must be routed back to
  the batch that owns its `(sourceId, callId)`.
- Finalising a batch **resolves unfinished calls** (pending becomes skipped,
  running becomes cancelled), otherwise an interrupted turn shows tools
  apparently still running.
- In Summary mode, adjacent completed tool batches within a UI turn share
  one **Ran N tools** header. Click it to expand their individual arguments
  and results. Failed, cancelled and skipped calls remain visible in the
  summary. Running batches and intervening messages/reasoning/approval
  blocks stay separate; `/tools compact` and `/tools full` retain their
  detailed views.
- Pending message text is shown in a bounded `[pending]` preview area above
  the composer, separate from streaming replies and the pinned activity row.
  **Up on an empty composer** atomically reclaims all still-pending messages
  from the engine for editing. Already-delivered messages cannot be reclaimed;
  recovered drafts are never submitted automatically.
- Queued messages still in flight when a turn ends **never reached the
  model**. They are never left as a bubble in the transcript; they move to a
  recoverable list and a notice says how many, and `Up` on an empty composer
  restores the most recent one without overwriting a draft or auto-sending it.
  The engine seals and clears undelivered entries at turn end, including
  interruption, so they cannot silently reappear in a later turn.
- A steering message delivered while the reply is still streaming is
  **deferred, not inserted immediately**: it appears only once that reply's
  block actually closes, so a mid-stream delivery can never split one reply
  into two.
- Destructive keys are **two-press chords**. `Ctrl+L` is a repaint, not a
  clear; `Ctrl+D` is deliberately unbound.
- Assistant text arrives **only as coalesced deltas**; there is no full-text
  event to fall back on.
- Thinking blocks without signatures are **dropped, not serialised**. Sending
  them back earns a provider 400, and once one is in the history every later
  turn fails too.
- The pinned activity row above the composer and the status bar's activity
  label **share one spinner**, never two: the row owns it, the status bar
  never animates its own.
- The header's session id is **its own selection target**, not part of the
  transcript. Selecting it and copying always copies the full id, even when
  the header clips its on-screen display.

### Bounded schedules

A schedule can stop itself. `schedule_create` (and `session/scheduleCreate`)
accept two optional bounds on top of the usual `every` / `at` / `cron`
selector:

- **`maxRuns`** — a positive integer run budget.
- **`expiresAt`** — an absolute ISO-8601 deadline — **or** `expiresIn`, a
  relative one (`"30m"`, `"2h"`, `"7d"`) using the same unit spelling as
  `every`. The two are mutually exclusive, and a relative value is resolved to
  an absolute instant **once, at creation**, so it cannot drift.

Both are optional and both default to absent. **A schedule with neither field
is unlimited and behaves exactly as it did before** — it runs until deleted.

The exact semantics, because the obvious reading of each is wrong in a way that
matters:

- **`maxRuns` counts accepted launch attempts, not successes.** An attempt is
  charged as soon as the runner accepts it and a task exists for it, including
  a run that later fails in the model, in a tool, or while waiting for a
  concurrency slot. Only a launch refused outright — no task registered,
  nothing executed — is uncounted. Counting successes instead would let an
  unreliable environment retry a "run it seven times" job forever. The counter
  is monotonic: advancing the recurrence boundary, reconciling, or a
  cancellation landing mid-run never rolls it back.
- **The deadline is exclusive.** An occurrence due exactly at `expiresAt` does
  not run. A definition expires when the clock *reaches* the deadline, not when
  its next boundary happens to fall beyond it, so an hourly monitor with a
  Friday deadline is reported expired on Friday rather than at its last
  Thursday tick — the runtime wakes at the deadline itself even when the next
  occurrence is far away or a run is in flight.
- **Expiry does not kill work already running.** By default the in-flight run
  finishes; only *future* launches are stopped. While that last run is
  executing the definition reports `retiring`, never `completed` — the work is
  not done, and saying otherwise is a lie a reader acts on.
- **Retirement and the last run's outcome are separate facts.** A definition
  carries a retirement record (`completed` for a spent budget, `expired`,
  `cancelled`, `failed`) *and*, independently, the outcome of its most recent
  run. A budget can be spent by a run that failed.

`schedule_list`, `session/scheduleList` and the `/schedules` browser report a
derived state — `idle`, `running`, `pending`, `retiring`, `completed`,
`expired`, `cancelled` or `failed` — together with `runsStarted`, the
configured bounds and the retirement reason. The browser's `runs` and `until`
columns stay blank for an unbounded schedule rather than rendering a
placeholder that would imply a limit.

**Everything here is in memory only.** The engine's schedule store is
constructed with no persistence path, so definitions, run counters and
retirements exist for the lifetime of the process and are **lost at restart**.
There is no restart continuation and no new on-disk state. Live run status is
kept in a separate ephemeral side table that is never serialized, so a store
reloaded from anywhere can never claim to own runs it does not have.

`session/scheduleDelete` is unchanged: it forgets a definition and stops future
runs, and does not interrupt a run already executing.

Clients must check the **`schedules.bounds`** capability in
`InitializeResult.capabilities` before relying on a bound. An engine without it
accepts `maxRuns` and ignores it, which would silently turn "seven runs" into an
unbounded schedule.

#### Calendar months and time zones

`every` is an absolute interval and is stored in UTC; it is not re-interpreted
in a calendar zone, so `24h` drifts by an hour across a DST transition. For
wall-clock meaning use `cron` with an IANA `timeZone` — `0 9 * * *` in
`America/New_York` stays at 09:00 local through the transition. There is no
"every month" interval: express a calendar month with an explicit `expiresAt`
deadline or a `cron` rule, not with a day count.

#### Self-cancellation

A scheduled run can retire its own schedule with **`schedule_cancel_self`**,
which is how a watcher ends itself when the thing it was watching for finally
happens. The tool takes **no schedule id and no task id**: there is nothing in
its arguments that could point it at another definition, at its parent, or at a
sibling. The identity it acts on comes from the trusted `ScheduleOrigin` the
runtime stamped on the run plus the caller's own registered task.

Only the **root** agent of a scheduled run may call it — the task the schedule
runtime registered (`TaskKind::Scheduled`, no parent). A nested subagent inside
a scheduled job inherits the origin so it can see its own definition, but it is
not the run; it returns its conclusion to the run that spawned it instead.
Every refusal uses identical wording so a caller cannot use the error to probe
whether some other definition exists.

By default self-cancellation stops only future runs and leaves the current one
to finish its turn. `stopRunning: true` also ends the current run, via a narrow
self-stop capability on `TaskManager` rather than by granting the run
main-agent privileges — `request_stop` deliberately excludes self, and that
stays true. Repeated calls are idempotent, and the first recorded reason
stands.

`schedule_create` and `schedule_delete` remain main-agent only. That is what
stops a bounded run from cloning itself into an unbounded watch to escape its
own budget.

### Security invariants

These are load-bearing. Each is pinned by a test, and each was chosen because
the obvious alternative is exploitable:

- **A hook's scope is stamped by the loader, never read from JSON.** Scope
  decides whether a hook's shell command runs without a prompt, so a hostile
  repository must not be able to claim a trusted scope in its own
  `.coda/settings.json`. The field is `#[serde(skip)]`, and `Default` is the
  *untrusted* scope so an unstamped value fails safe. This mirrors C#
  `SettingsLoader`, which force-overwrites scope by source file.
- **An MCP server cannot waive its own approval.** `readOnlyHint` comes from
  the server, and read-only tools skip the permission chain, so trusting it
  would let a server mark a destructive tool read-only and execute unprompted.
  `McpTool::is_read_only()` is always `false`; the hint is display metadata.
- **Permission gates fail closed.** Only post-hoc hooks, which cannot prevent
  anything, fail open.
- **The permission prompt is an `Exclusive` surface.** The engine is blocked
  until it is answered, so nothing may open above it and `Esc` *denies* rather
  than dismissing — closing without answering would leave the turn waiting
  forever on a responder that never receives a reply. Stray keys are swallowed
  rather than ignored, since an ignored key would reach the stack's own `Esc`
  handling and pop a prompt the turn depends on. This replaced an ordering
  rule implied by the sequence of `if` statements in `on_key`: exclusivity is
  now a property of the prompt rather than a convention about branch order.
- **The prompt surface and the reducer are kept in lockstep.** The engine
  clears `state.prompt` when a turn ends or is interrupted, without the prompt
  being answered; an `Exclusive` surface left behind would be undismissable
  and would wedge the interface. `App::apply` retires the surface whenever the
  reducer has no prompt.
- **Sandbox containment folds case only on case-insensitive platforms.**
  Folding unconditionally would treat a case-variant sibling as inside the
  root on a case-sensitive filesystem.
- **Everything rendered from an untrusted source is sanitized** — model prose
  as well as code blocks, tool output, diffs and command output.
- **A task may only be acted on by an ancestor.** `task_output` and `task_stop`
  check the caller against the task tree: the main agent has full authority, a
  subagent only over its strict descendants, neither over itself. Denied and
  not-found return identical wording so a caller cannot probe for the existence
  of tasks it may not touch.
- **A scheduled run's self-cancel carries no target.** `schedule_cancel_self`
  accepts no schedule id and no task id, so there is no argument a model can
  forge to redirect it. Its identity comes from the trusted `ScheduleOrigin`
  and the caller's own registered task, and only the scheduled *root* run
  qualifies. Its `stopRunning` option uses a narrow `request_self_stop`
  capability rather than widening `request_stop`, which still denies a task
  that names itself.
- **A plugin may not point outside its own directory.** A plugin-declared LSP
  server path is rejected if absolute or if it resolves outside the plugin
  root, since it names an executable to launch. A *project-scoped* plugin may
  also not set `model:`, because the project directory is attacker-controlled
  and model choice is a cost lever.
- **An undispatchable hook has not approved anything.** Handler types this
  build cannot run take the event's fail-open policy rather than parsing as a
  silent success, so an `agent` hook on a fail-closed `PreToolUse` gate blocks
  rather than allows.
- **`allowedTools` from multiple hooks is intersected, not unioned.** Union
  would grant a tool that an individual hook intended to block. A hook that
  omits the field has *no opinion* and must not narrow the set to empty.
- **An enterprise domain must be a bare host.** It is interpolated into the
  OAuth token-exchange URL, so a path, query, fragment or userinfo component
  could redirect a durable token elsewhere. A hostile value fails the whole
  configuration rather than falling back to the public default, which would
  silently route enterprise traffic to github.com.
- **The turn slot is released by a `Drop` guard.** A serve task is cancellable;
  releasing only on the `Ok`/`Err` paths would leave the slot claimed forever
  after a client disconnects mid-turn, refusing every later prompt as busy.
