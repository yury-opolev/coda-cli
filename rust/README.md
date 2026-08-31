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
cargo run -p coda-tui -- --log-file coda.log --log-filter debug
```

Because stdout carries the protocol, engine diagnostics go to stderr and are
kept in a bounded ring for crash reporting. Use `--log-file` for client-side
tracing; logging to stdout would corrupt the display.

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
  request sequence and compare normalised responses. **Zero divergences**;
  `KNOWN_GAPS` is empty.

The differential suite covers the deterministic surface — handshake, history,
models, listings, errors, goals, effort, schedules. It excludes live model
turns on purpose: a provider's output is not reproducible, and a flaky parity
test is worse than none.

Differential testing has been worth more than its cost. Three times a Rust
unit test had pinned the *wrong* value and so agreed with a bug —
`serverInfo: "coda-serve"` where C# says `"coda"`, an absent `setEffort` note
where C# sends `""`, and a hook that approved a fail-closed gate. A test
written beside an implementation tends to encode that implementation's
assumptions; only cross-checking against the reference breaks the circularity.

### What remains

- session resume, transcript export/import, and the setup/onboarding wizard;
- five slash commands (`/compact` is wired; `/resume`, `/fork`, `/rewind`,
  `/import`, `/login`, `/logout` need session-state or auth RPCs);
- the 30 FPS frame throttle and the assistant-buffering mode, including its
  withhold-on-interrupt rule;
- session-level wiring for a few engine features that exist and are tested but
  are not yet constructed by `coda-serve`: the `ScheduleRuntime`, the
  hook-free subagent factory for `agent`-type hooks, and `SubagentRegistry`
  injection into `SubagentHost`;
- **real-model validation.** The engine has been exercised by its own tests and
  by the parity suite, not by sustained real use. That is the one gap no test
  closes.

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
- Queued messages still pending when a turn ends **never reached the model**
  and are removed rather than left in the transcript.
- Destructive keys are **two-press chords**. `Ctrl+L` is a repaint, not a
  clear; `Ctrl+D` is deliberately unbound.
- Assistant text arrives **only as coalesced deltas**; there is no full-text
  event to fall back on.
- Thinking blocks without signatures are **dropped, not serialised**. Sending
  them back earns a provider 400, and once one is in the history every later
  turn fails too.

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

