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
| `coda-tui` | State reducer, composer, viewport, keymap, drawing and the application loop. |
| `coda-tool` | Leaf crate: the `Tool` trait, `ToolContext` and the path sandbox. Depends on nothing, so tool hosts need not pull in the engine. |
| `coda-llm` | Neutral chat model, SSE decoding, Anthropic and Copilot clients, retry policy, and the `CredentialSource` seam. |
| `coda-agent` | The agent loop, 30 built-in tools, permissions, tasks, scheduling, hooks, subagents, compaction and the LSP client. |
| `coda-mcp` | MCP stdio client, server manager, and the shared `.mcp.json` config. |
| `coda-auth` | OAuth/PKCE and device-code flows, keyring storage with an encrypted-file fallback, single-flight refresh. |

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

## Status

**The TUI ships and works.** It is the binary you run, and it drives the .NET
engine over `serve`. The engine crates are complete, tested libraries but are
not yet wired to a Rust `serve` entrypoint — see "What remains" below.

Ported and working, in the shipping TUI:

- the wire protocol, transport and engine supervision;
- transcript rendering: markdown, unified diffs, syntax highlighting for eight
  languages, tool display modes, the warm-ember and cool-dark themes;
- the state reducer, composer, viewport, keymap and drawing;
- the application loop, slash commands, and permission/question/plan prompts;
- browser overlays for models, schedules, skills, plugins, hooks, MCP servers
  and background tasks, on a shared list/detail browser with filtering, paging
  and reload;
- switching model, and toggling or updating plugins and MCP servers.

Ported as libraries, complete with tests, not yet hosted by a Rust binary:

- **providers** (`coda-llm`): the neutral chat model, streaming, Anthropic and
  Copilot clients;
- **agent core** (`coda-agent`): the loop, goals, events, tool contract and
  permission gates;
- **tools** (`coda-agent`): the 30 built-in file, search, shell, web and agent
  tools, over the `coda-tool` sandbox;
- **integrations** (`coda-mcp`): MCP client and manager, and the LSP client;
- **runtime** (`coda-agent`): tasks, scheduling, subagents, hooks, compaction
  and output styles;
- **auth** (`coda-auth`): OAuth/PKCE, device code and credential storage.

### What remains

- **A Rust `serve` binary.** The engine crates are not yet mounted behind the
  JSON-RPC surface, so the shipping TUI still spawns the .NET engine. This is
  the natural next phase: the crates exist, the protocol types exist, and the
  contract tests already pin the surface they must satisfy.
- session resume, transcript export/import, and the setup/onboarding wizard;
- around 20 of the 40 slash commands, most of which are local-file or
  session-state operations rather than engine calls;
- the 30 FPS frame throttle and the assistant-buffering mode, including its
  withhold-on-interrupt rule.

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
- **Sandbox containment folds case only on case-insensitive platforms.**
  Folding unconditionally would treat a case-variant sibling as inside the
  root on a case-sensitive filesystem.
- **Everything rendered from an untrusted source is sanitized** — model prose
  as well as code blocks, tool output, diffs and command output.

