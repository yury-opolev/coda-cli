# Coda — Rust front-end

A Rust rewrite of the Coda TUI, built on [ratatui](https://ratatui.rs) and
[crossterm](https://github.com/crossterm-rs/crossterm).

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

The dependency direction is strictly one way: `coda-tui → coda-render`,
`coda-tui → coda-client → coda-proto`. Nothing below `coda-tui` knows about
application state, and nothing below `coda-client` performs I/O.

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

Ported and working:

- the wire protocol, transport and engine supervision;
- transcript rendering: markdown, unified diffs, syntax highlighting for eight
  languages, tool display modes, the warm-ember and cool-dark themes;
- the state reducer, composer, viewport, keymap and drawing;
- the application loop, slash commands, and permission/question/plan prompts;
- browser overlays for models, schedules, skills, plugins, hooks, MCP servers
  and background tasks, on a shared list/detail browser with filtering, paging
  and reload;
- switching model, and toggling or updating plugins and MCP servers.

Not yet ported:

- session resume, transcript export/import, and the setup/onboarding wizard;
- around 20 of the 40 slash commands, most of which are local-file or
  session-state operations rather than engine calls;
- the 30 FPS frame throttle and the assistant-buffering mode, including its
  withhold-on-interrupt rule;
- the engine itself, which still runs as .NET.

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

