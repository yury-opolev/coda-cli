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
| `coda-diff` | Differential tests asserting the C# and Rust engines answer identically. |
| `coda` | The shipping binary: interactive, `serve` and `run` modes. |

The dependency direction is strictly one way: `coda-tui → coda-render`,
`coda-tui → coda-client → coda-proto`, and `coda-agent → {coda-llm, coda-mcp}
→ coda-tool`. Nothing below `coda-tui` knows about application state, nothing
below `coda-client` performs I/O, and `coda-tool` is a leaf so that hosting a
tool never drags in the agent.

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
```

Interactive mode drives the engine over the same JSON-RPC seam, defaulting the
engine to this same executable. Running the agent in-process would be slightly
faster but would bypass the boundary the parity tests exercise, so it is
deliberately not done.

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

- session transcript export/import and the setup/onboarding wizard;
- five slash commands (`/compact` is wired; `/resume`, `/fork`, `/rewind`,
  `/import`, `/login`, `/logout` need session-state or auth RPCs);
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
  default host.
- Invalid startup values (bad `--effort`, unknown `--permission-mode`, a
  non-positive `--goal-timeout`, or a negative `--max-continuations`) fail
  startup with an error instead of being silently defaulted or clamped.
- `--log-file`/`--diagnostic-verbosity` are always available and never opt
  in the essential diagnostic log itself, which is written regardless — see
  [Operational diagnostics](#operational-diagnostics).

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
the only place it is available. A recognized missing-parameter provider error
(the `input[N].summary` shape a couple of endpoints report) may record that
exact bounded path from a structured `error.param` field; anything else is
category-and-status only.

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
- Queued messages still in flight when a turn ends **never reached the
  model**. They are never left as a bubble in the transcript; they move to a
  recoverable list and a notice says how many, and `Up` on an empty composer
  restores the most recent one without overwriting a draft or auto-sending it.
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
