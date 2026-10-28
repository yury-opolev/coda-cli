# Coda

**Coda is a native Rust/ratatui agentic coding assistant for the terminal.** You
talk to it in plain language; it reads and edits your code, runs commands, searches
the web, and drives multi-step work to completion through an **agentic tool-use
loop** — with you in control of what it's allowed to do. It connects to Claude
(via Claude.ai subscription OAuth or an Anthropic API key) and to GitHub Copilot,
talking to the Anthropic Messages API natively.

Coda ships two front-ends over one engine: an **interactive TUI** (built with
[ratatui](https://ratatui.rs)) for humans, and a **programmatic `coda serve` API**
so an orchestrator can drive Coda as a coding subagent.

> The engine is written in Rust. A .NET client SDK (`Coda.Client`) drives it
> out-of-process over the `coda serve` protocol.

> **API reference:** the programmatic interface — the `coda serve` JSON-RPC
> protocol, its transports/authentication, and the .NET `Coda.Client` SDK — is
> documented in **[`docs/API.md`](docs/API.md)** (wire-level protocol details in
> [`docs/serve-protocol.md`](docs/serve-protocol.md)).

## Quick start

> **Prerequisites:** Windows (the TUI uses DPAPI for credential storage). A
> [.NET 10 SDK](https://dotnet.microsoft.com/download) is only needed if you
> install via the `.NET global tool` method; the standalone binary has no
> runtime dependency.

```powershell
# Option A — install as a .NET global tool named `coda` (needs .NET 10 SDK):
git clone https://github.com/yury-opolev/coda-cli.git
cd coda-cli
./rust/build.ps1 -NoBump      # build the Rust binary (requires Rust toolchain)
./publish.ps1 -Flavor tool    # pack the launcher nupkg
dotnet tool install --global --add-source ./publish/tool Coda.Cli

# Option B — standalone native binary (no .NET needed at all):
git clone https://github.com/yury-opolev/coda-cli.git
cd coda-cli
./rust/build.ps1 -NoBump
./publish.ps1 -Flavor self-contained   # -> publish/self-contained/coda.exe
# Add publish/self-contained to your PATH, or copy coda.exe somewhere on PATH.

# 2. Run it (first launch walks you through provider sign-in):
coda
```

`coda` is now on your PATH. On first launch the **setup wizard** has you pick a
provider (Claude.ai subscription, an Anthropic API key, or GitHub Copilot), signs
you in, and verifies the connection. Then just type.

To upgrade later (option A):

```powershell
./rust/build.ps1 -NoBump     # rebuild the Rust binary
./publish.ps1 -Flavor tool   # repack
dotnet tool update --global --add-source ./publish/tool Coda.Cli
```

> **Note:** the `.NET global tool` package (`Coda.Cli`) contains a thin .NET
> launcher that locates and runs the bundled native Rust binary. The .NET runtime
> is used only for that launcher shim — the TUI, engine, and `coda serve` are all
> native Rust with no .NET runtime dependency.

Check the version of any install with `coda --version`. Coda keeps all its own
state under **`~/.coda/`** (settings, sessions, credentials, …), separate from the
Claude CLI's `~/.claude/` — though it will read your existing `CLAUDE.md` and
`.mcp.json` if present. See [Configuration & storage](#configuration--storage).

## Diagnostic logs (Rust)

Rust Coda enables local operational diagnostics automatically for interactive,
headless, and engine processes. Logs live under `~/.coda/logs/diagnostics`
(respecting `CODA_HOME`); `/log` shows the frontend and engine log paths.
`--log-file <path>` overrides the destination, and
`--diagnostic-verbosity normal|debug|trace` controls additional safe detail.
Logs rotate and expire; prompts, tool payloads, credentials, and encrypted
reasoning are excluded. Conversation transcripts and content-audit sidecars
are separate. See [the Rust diagnostics guide](rust/README.md#operational-diagnostics)
for limits, failure behavior, and compatibility with older logging flags.

## What Coda can do

- **Agentic tool loop** — the model plans and acts using built-in tools:
  - read-only, auto-run: `read_file`, `list_dir`, `glob`, `grep`
  - permission-gated: `edit_file`, `write_file`, `run_command` (all file tools are
    sandboxed to the working directory, symlink-aware)
  - `web_fetch` / `web_search` (DuckDuckGo), `notebook_edit`, `git_worktree`,
    `todo_write` (a live checklist), plan mode, `schedule_*` **scheduled tasks**
    (see [Scheduled tasks](#scheduled-tasks)), and the
    task tools (`task` starts a subagent in the background, plus `task_list` /
    `task_get` / `task_peek` / `task_output` / `task_send` / `task_wait` / `task_stop` /
    `task_remove`; `task_start` and `task_recall` are deprecated aliases kept for one
    release) for long-running jobs.
- **Non-blocking task runtime** — one in-process `TaskManager` owns every long-running
  job in the session (**subagents** and **shell commands** alike). It gives each task a
  shared id/status/depth model, a bounded ring of recent output, and a persistent,
  secret-redacted log under `~/.coda/task-logs`.
  - **Every `run_command` is a task.** The agent waits for it under a short, bounded
    *yield budget*: `waitSeconds` (default **20**, clamped to **0–30**; `0` returns at
    once — meant for known long jobs such as builds, reviews or servers). A command that
    finishes in time returns exactly the usual `exit code: N` + output. One that does not
    **keeps running**: the model gets `Still running as task <id> … Output so far: …`, and
    any tool calls planned after it in the same batch are skipped so the model re-plans.
    Waiting less **never kills** the command; `timeoutSeconds` is a separate, optional
    *work deadline* that kills the process tree after N seconds (no default).
  - **Messages you send while a tool runs do not interrupt it** — your message reaches the
    model at the next boundary: when the command finishes or yields at its budget (at most
    30 s). To stop waiting immediately, background it explicitly with `Ctrl+B`.
  - **Completions are pushed — to every agent.** When a backgrounded command or a
    subagent completes, fails or is stopped, the agent that started it receives one
    `[task-completed]` data block (status; for a command its exit code, log path and last
    output, for a subagent its final report) — and an idle session starts a turn by
    itself. The model is told not to poll. Stops *you* make (`/stop`, `/tasks` → `x`) are
    not reported back to the main agent, so they never start a turn; a subagent is told,
    because it may be waiting on that very task.
  - **Agents park instead of waiting.** Every agent — the main session and each
    subagent, at every depth — has an inbox. A subagent whose turn ends while tasks it
    started are still running **parks**: no model calls, no polling, until a completion
    or a message arrives; a burst of completions within ~2 s produces one turn. It
    finishes when a turn ends with nothing of its own left running, and its final message
    is its report. Limits: **2 h** lifetime and **30 min** parked without news; either
    wakes it once to write a partial report, after which it stops (and so does anything
    it started).
  - **You stay in control:** backgrounding is always explicit — `Ctrl+B` moves whatever
    the agent is waiting on to the background (once a tool has run for 5 s the activity
    row shows `Ctrl+B to background`), `/tasks` → `b` / `x` backgrounds / stops one task
    (only shell commands and long tool calls are ever waited on, so only they can be
    backgrounded), `/tasks` → `m` or `/send <task-id> <message>` sends a message to a
    running subagent (delivered at its next boundary, or it wakes it when parked), and `/stop` interrupts the
    turn and stops every task. `Esc` (interrupt) stops only the command the agent was
    waiting on; commands and subagents already in the background keep running.
  - **Ask without interrupting:** `/btw <question>` (alias `/ask`) answers a side
    question — *"are the reviews still running?"* — from the conversation so far and a
    live snapshot of the task table, in a side panel, while the agent keeps working. It
    uses no tools and is never added to the conversation; `p` in the panel sends the
    question to the agent as an ordinary message after all. The status row shows
    `N tasks running · /tasks` whenever anything runs.
  - **Messages cannot rewrite running work.** A message you send while tasks the agent
    started are running reaches the model prefixed with a compact `[live-tasks]` list
    (id, kind, command, age), so it knows those processes are already running with their
    original parameters and must stop and restart one to apply a change.
  - **Long MCP tool calls are tasks too:** a remote MCP call is awaited under the same
    yield budget as a shell command and keeps running in the background when it takes
    longer. At most **32** shell commands run at once; another is refused with a clear
    error until one finishes or is stopped.
  - The agent can still inspect and steer work with `task_list` / `task_get` /
    `task_peek` / `task_output` / `task_send` / `task_stop`, and `task_remove` a finished
    task (its log file is preserved). `task_wait` attaches to a task for at most **30 s**
    (never cut short by a message, never stops the task); ending the turn is the preferred
    way to wait. `task_recall` only returns a completion that could not be pushed.
  - Headless `coda run` sets `CODA_RUN_WAIT_FOR_TASKS=1`: its single turn parks until the
    tasks it started have finished and reported (at most 30 min without news), then lets
    the model finish. Tasks are **process-local** — they do **not** survive across
    processes, and the manager stops every task when Coda exits.
- **Subagents** — delegate a self-contained subtask with `task`, which starts a nested
  agent loop in its own context with a scoped toolset and returns its task id at once;
  the report arrives as a pushed completion. Nesting and fan-out are
  bounded (by default the main agent at depth 0 with subagents at depth 1 and 2, and
  8 subagents running at once), both configurable via the
  [`subagents` settings block](#subagent-limits--prompts). The main agent can inspect
  and steer every task, while a subagent can only see and act on its own descendants.
- **Code intelligence (LSP)** — when language servers are configured, an `lsp` tool
  provides definitions/references/hover/symbols/diagnostics.
- **MCP** — drop a `.mcp.json` in the working dir and Coda connects the servers,
  exposing their tools (`mcp__<server>__<tool>`), resources, and prompts to the agent.
- **Providers** — Claude.ai subscriber OAuth, Anthropic API key, and GitHub Copilot
  (device flow), with automatic token refresh.
- **You stay in control** — permission modes (`default` / `acceptEdits` / `plan` /
  `bypass`), allow/deny rules and [lifecycle hooks](docs/hooks.md) from settings files, and an
  interactive approve/deny prompt for risky actions.
- **Autonomy helpers (opt-in)** — a **safety classifier** that vets actions in
  bypass mode, and an autonomous **goal
  loop** that keeps working until a judge says the goal is met. Plus automatic
  history **compaction**, output-style personas, and a plugin/skills marketplace.
  See [`docs/skills-and-plugins.md`](docs/skills-and-plugins.md) for authoring skills and plugins.
- **Programmatic access** — Rust `coda serve` exposes the agent over bidirectional
  JSON-RPC on stdio, streaming progress and requesting permission or clarification.
  The .NET [`Coda.Client`](src/Coda.Client) SDK drives it out-of-process.

Coda is its own product, independent of any vendor's official CLI.

> **Platform note:** the Rust engine and TUI use DPAPI credential storage on
> Windows and encrypted-file storage on other platforms.

## Coda — the interactive TUI

The primary TUI is built with **[ratatui](https://ratatui.rs)** (Rust). Run the
Rust binary directly:

```powershell
# Build (without bumping version) and run:
./rust/build.ps1 -NoBump
.\rust\target\release\coda.exe
```

The TUI follows the **Warm Ember** interaction model.
**Full-screen mode is the default interactive engine on a supported terminal**: a scrollable,
virtualized transcript fills the **full
width** of the screen, an **operational status row** (turn, tool, waiting, approval, and key-hint state) sits
**directly above** the composer, and a **dynamic composer** starts at **three rows** and grows up to a
**capped height** as you type while staying pinned near the bottom. A separate, **stable metadata row**
(model, effort, permission mode, context, usage, cost, services, git, and cwd) occupies the **final row**;
fields are shed from the right as the terminal narrows. **Focus** stays on the composer while you
type — keyboard shortcuts drive the transcript, overlays, and completion menu without pulling focus away
from your prompt. A **plain** renderer is always available.

```powershell
# Start in a permission mode, the same modes /permissions offers. These compose with
# --continue / --resume <id> / --fork [id] in any order.
coda --yolo                      # bypass: every tool runs without asking
coda --permission-mode plan
```

### Exact startup system prompts

The interactive TUI, `coda run`, and `coda serve` accept one root-prompt source:

```text
coda [interactive options] [--system-prompt <text> | --system-prompt-file <path>]
coda serve [serve options] [--system-prompt <text> | --system-prompt-file <path>]
coda run -p <task> [--system-prompt <text> | --system-prompt-file <path>]
```

The options are mutually exclusive. A missing value or unreadable/non-UTF-8 file
fails before the first prompt. The supplied text replaces the root system prompt
for this session and is not written to settings.

An explicit value is a complete replacement for the root system prompt: Coda's built-in prompt,
`CLAUDE.md`/project context, output-style suffix, and provider prefix are not appended. Empty and
whitespace-only values are valid exact replacements. Anthropic omits an explicitly empty optional
`system` field because its empty text blocks are invalid; OpenAI request shapes serialize the empty
value. Neither provider behavior restores Coda defaults. Interactive paths using Claude.ai OAuth may show
a non-blocking compatibility warning, but the supplied text is not changed; serve does not promise
that warning.

The replacement is used for the root conversation and scheduled root turns (including `/context`);
subagents retain their role prompts. On resume, precedence is startup override, then the persisted
override, then the normal generated prompt. Persisted metadata is distinct from the audited
effective `systemPrompt`; audit data is never a resume source. Forks preserve the override for
interactive, headless, and slash-command flows. Export/import carries it as the optional
`systemPromptOverride` field while keeping the `coda.session/1` bundle schema unchanged.

**Keys (Warm Ember):** `Enter` submits — while the slash-command completion menu is open and the highlighted
command differs from what you typed, it accepts that command into the prompt instead, so press it again to
send · `Shift+Enter` (or `Ctrl+Enter`, `Alt+Enter` or `Ctrl+J`, as terminal-compatible fallbacks) inserts a newline · while an agent is busy, ordinary submissions are delivered at its next safe boundary — while a command runs, that is when it finishes or yields at its budget (at most 30 s); press `Ctrl+B` to stop waiting on it sooner · `Up` on an empty first composer line recalls queued messages into the draft (otherwise it navigates history) · `Up`/`Down` move the composer
cursor between the lines of a multi-line prompt · `Ctrl+Up`/`Ctrl+Down` step through prompt history ·
`Esc` dismisses the active menu or overlay, or clears a selection, and never exits Coda; pressing `Esc`
twice within 1.5 s while the agent is working asks to stop the current turn — `Enter` then stops
it and `Esc` keeps it going ·
`Ctrl+C` copies the current transcript selection and, with nothing selected,
exits on a **second** press · `/exit` (or `/quit`) exits — there is **no `Ctrl+D`** binding · `Ctrl+L` repaints the screen (use it if a tool that wrote to the terminal directly has left stray
characters behind) · `F2`
switches between full-screen and inline · `Ctrl+B` **backgrounds now**: the agent stops waiting on the
command or task it is running (which keeps running and reports back when it finishes) · `Ctrl+End` jumps to the newest transcript output (the scrollbar shows position).
Typing a `/` shows a slash-command completion menu directly
above the composer (Up/Down select, Tab or Enter completes, a single Esc dismisses). The menu opens for a `/`
at the start of the prompt **or after whitespace**, so a command can be picked mid-sentence — a `/`
inside a word, such as a path or URL, never opens it. The menu only ever **edits the prompt**: accepting a
suggestion splices the command name in and sends nothing, so `Enter` over an open menu never runs a command
you merely highlighted. What runs is always what the composer shows, and the next `Enter` sends it. The one
exception is a highlight you have already typed out in full, where accepting could not change which command
runs — `/model` then `Enter` submits straight away, while `/mo` or `/MODEL` is completed first so you see the
command before it runs. Only a command at the **start** of the prompt is executed as a command; one accepted
mid-sentence stays literal text and is sent to the model together with the prose around it.

**Shutdown:** once `/exit`, `/quit`, or the second exit-arming `Ctrl+C` is accepted,
the TUI shows an animated shutdown status until cleanup finishes. Further `Ctrl+C`
acknowledges cleanup; it does not abandon an in-flight credential commit or start
new work. Cleanup failures and forced engine stops are reported after restoring
the terminal, rather than followed by a success summary. See the
[shutdown and Windows acceptance notes](rust/README.md#interactive-shutdown).

**Mouse:** in the **transcript**, **left-drag** selects text; `Ctrl+C` **or a right-click** copies the
selection (the role markers and tree connectors in the gutter are never copied). A selection is kept if
the clipboard is unavailable, so you can retry. In the **composer**, **left-drag** selects text; when a
selection exists, `Ctrl+C` **or a right-click** copies it and clears the selection; a **right-click**
with no selection pastes at the clicked caret. `Alt+V`, `Ctrl+V`, or `Shift+Insert`
attaches a copied **image** as an `[Image N]` placeholder, falling back to text when
no image is available. Use `Alt+V` if the terminal intercepts `Ctrl+V`: terminal
paste events carry text only. Use `/image <path>` to attach a file. Images are capped
at 5 MiB before base64 encoding. Removing a placeholder excludes that image from
the next prompt; drafts with images must wait until the current turn finishes.
The **session id** in the header, and the body of the modal browsers
(`/model`, `/skills`, `/mcp`, `/tasks`, `/schedule`, `/plugins`), can be selected with a **left-drag**
and copied the same way. In each browser the **list pane** uses a TableView widget that highlights the selected row; drag-select with `Ctrl+C` is available in the **detail pane** where a full `SelectableTextView` is rendered. **`Shift`-drag** hands native selection and copy to the terminal where
supported. `--no-mouse` leaves selection and copy native to the terminal, and every action stays
reachable from the keyboard. Full-screen has **no permanent sidebar**
and uses a **virtualized transcript** (context, pickers, permissions, help, and diffs all use
keyboard-driven overlays). **Plain mode** is recommended for screen readers, CI, output/input
redirection, and terminals that the full-screen renderer does not support.

The transcript is either **Following** (pinned to the newest row) or **Detached**. Detaching stores a
stable `(blockId, wrappedRowOffset)` anchor, so reflow and replacement do not create false unseen counts.
While detached, streaming growth contributes to an unseen-row counter; each new visible top-level block
increments the unseen-message count once. Reaching the bottom atomically clears both counters and returns
to Following. The centered labels are `Jump to bottom (Ctrl+End) v`, or `1 new message (Ctrl+End) v`
with pluralization; only the rendered label cells are clickable. `Ctrl+End` is shell-global, but modal
prompt/task/MCP overlays own the key and block the jump. The scrollbar supports track paging and thumb
dragging, and is inert when mouse input is disabled. User timestamps use local `HH:mm`; the layout
reserves one cell before the timestamp and one trailing cell before the scrollbar. Resumed messages
without a timestamp omit the annotation.

Inside the REPL:

```
/login [claude|copilot|api-key]   sign in (Claude.ai browser / Copilot device code / API key)
/status                   sign-in state for every provider
/tasks [all]              open the live task browser: active work; `all` opens the history view
/btw <question>           ask a side question about the session and its tasks (alias /ask)
/provider [id]            show the connected account, or connect a different one
/setup                    the first-run wizard: choose an account and sign in
/model [id]               show or set the chat model
/effort [low|medium|high|xhigh|max|auto]  choose or set model-specific reasoning effort
/context                  show context-window usage broken down by category
/goal [<text> | off]      set/clear the autonomous goal (--timeout, --max-turns)
/log [<level> | stderr on|off]  show or set telemetry logging level
/headers                  the outgoing request headers (secrets redacted)
/logout [provider]        sign out
/help [command]           list commands, or show detailed help for one
/version   /clear   /exit
```

Run a shell command yourself with a leading `!`:

```
!git status               runs immediately, output goes in the transcript
!cargo test -p coda-tui
!!literal                 a doubled bang escapes, so the text is sent to the model
```

`!` is *your* command, not the agent's: it runs straight away with no model
round-trip and costs no tokens, it is not gated by the permission mode — that
governs what the agent may do on your behalf, and approving a command you just
typed would be theatre — and its output is shown to you rather than silently
added to the model's context. The agent has its own `run_command` tool for when
it needs to run something. The shell matches that tool's: PowerShell on
Windows, `sh` elsewhere.

> **Tip:** append `--help` (or `-h`) to any command for its usage and examples, e.g. `/model --help`.

### `/tasks` — the task browser

In the full-screen TUI, `/tasks` opens a content-sized browser over the session's
`TaskManager`, read through the engine's `session/listTasks` (so it works the same in local,
API-only and serve-backed sessions, and shows tasks that have produced no output yet). Each row
shows a status glyph, the task id, status, kind (subagent/shell/tool/scheduled) and description.

By default it shows **active work only** — every task still running, foreground or background,
including subagents parked waiting for their own tasks — titled `Tasks — active (N)`, or
`No active tasks` when there are none. Finished (completed, failed, stopped) tasks are not
discarded: `h` switches to the **history** view (`Tasks — history (N)`), which lists every task
the session still retains with its report and output tail, and `h` again switches back;
`/tasks all` opens straight into history. The list follows the engine's task start and end
events while it is open (no polling): a task that ends leaves the active view, the selection
stays on the same task where it is still listed, and otherwise moves to a remaining one.

Task ids are `task-00000001`, `task-00000002`, … — unique within the session, never reused,
and simply wider past `task-99999999`.

- **List:** `Up`/`Down` or `k`/`j` move · `Enter` opens the detail page (mode, parent, report,
  secret-redacted output tail) · `b` **backgrounds** the selected task (the agent stops waiting on
  it; it keeps running) · `x` **stops** it (and its process tree) · `m` starts a **message** to
  a running subagent (closes the browser and fills in `/send <task-id> `) · `h` toggles
  active/history · `r` reloads · `/` filters · `Esc` or `q` closes. On a finished task `b`, `x`
  and `m` are refused by the engine ("already finished").

`/send <task-id> <message>` delivers a message to a running subagent's inbox: it is delivered
at the subagent's next boundary (a wait on a running command ends within 30 s at most), or
wakes it if it is parked. `/stop` interrupts the running turn and stops **every** running task. Everything here is
**process-local** and **stops when Coda exits**.

`coda serve` exposes the same controls to any client: `session/listTasks` (optional
`statusFilter`: `active`, `terminal` or `all`, the default),
`session/backgroundAll`, `task/background {taskId}`, `task/stop {taskId}`,
`task/send {taskId, text}`, `session/stopAllTasks` and `session/askAside {question}`, plus the
`event/taskStarted`, throttled `event/taskOutput` and `event/taskCompleted` (with the owning
agent) notifications (see [docs/serve-protocol.md](docs/serve-protocol.md)).

### Model, effort & context

- **`/model [id]`** shows or sets the chat model. With no argument it lists the
  models the **active provider actually grants** — fetched live from the provider
  (Anthropic `GET /v1/models`, Copilot `GET /models`) and cached for the session —
  falling back to a built-in list when the provider exposes no such endpoint or the
  call fails (e.g. Claude.ai OAuth, or offline). Each model is annotated with its
  display name and context window from the **model catalog** (see below).
  `/model refresh` re-fetches the live list *and* refreshes the catalog. Run
  interactively with no argument, `/model` opens a **model browser** (see below).

### Browsers — `/model`, `/mcp`, `/skills`, `/plugin`, `/tasks`, `/schedule`

In the full-screen TUI these open focused, full-overlay browsers. They share one set of
bindings, one status vocabulary, and one look.

**Status glyphs.** Each row carries a glyph coloured by state, with an ASCII fallback for terminals
that cannot draw geometric characters:

| Glyph | ASCII | Meaning |
|---|---|---|
| `●` | `*` | healthy — connected, running |
| `○` | `o` | enabled but idle or disconnected |
| `⊘` | `x` | disabled |
| `✗` | `!` | error |
| `!` | `!` | needs attention — untrusted, expiring credentials |
| `↑` | `^` | overridden by a higher-precedence scope (row dimmed) |

**List bindings**, the same in every browser: `Esc` or `q` closes · `Up`/`Down` or `k`/`j` move ·
`PageUp`/`PageDown`, `Home`/`End` page and jump · `Enter` opens the detail page · `Space` toggles
enabled where the browser supports it · `r` reloads · `/` filters by name.

**Filter mode** narrows rows as you type. `Esc` leaves the filter and returns to the full list; a
second `Esc` closes the browser.

**Detail bindings:** `Esc` or `q` returns to the list, with the same movement and paging keys.

The **model browser** shows each model's id, display name, context window and current
effort. The active model has a bold `current` marker independent of the highlighted
navigation row. Left/Right adjusts and saves the highlighted model's effort in
the arrow-flanked effort column without switching models; Enter selects the model.
Only known supported levels are offered, and the list keeps your selection and
filter after an edit. The header states where the list came from — live from the provider, the
models.dev catalog, or the built-in fallback — warning when it is the built-in list. `/model <id>`
still applies a model directly without opening the browser.

The **MCP browser** offers add, edit, enable/disable, and remove actions. In its
editor, arguments are a JSON array such as `["-y", "server"]`, and environment
variables are a JSON object such as `{ "TOKEN": "coda-secret:store/key" }`.
This preserves spaces, quotes, empty arguments, and multiline environment values.
Use Tab/Shift+Tab to move between fields and Ctrl+Enter to save from a multiline
field. Secret references stay unresolved; browser summaries show variable names,
not values. Existing headers, auth, and unknown settings survive edits and scope
moves, and name collisions are rejected rather than overwriting another server.
Transport changes rewrite the command/URL shape. Restart the engine to apply
configuration changes to its live MCP connections.

- **`/effort`** opens a horizontal Faster / Smarter picker for the active model.
  Left/Right changes the selection, Enter applies and saves a per-provider/model
  preference, `s` applies for this session only, `a` selects automatic, and Esc
  cancels. Only supported levels are selectable; unknown capability is labelled
  explicitly. `/effort <level>` applies and saves directly; `auto` clears the
  explicit preference. In the model browser, `e` activates the highlighted model
  and opens its effort picker. `--effort <level>` is a session-only startup override.
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

All product-facing names live in `rust/crates/coda-tui/src/branding.rs` (one place to rename).

### Setup & chatting

On first launch (no stored credentials) Coda runs a **setup wizard**: pick a
provider → sign in → it verifies the connection with a tiny real completion.
Re-run it any time with `/setup`.

Once connected, just type a message. Coda streams the assistant reply and runs an
**agentic tool-use loop**:
- read-only (automatic): `read_file`, `list_dir`, `glob`, `grep`
- permission-gated: `edit_file`, `write_file`, `run_command`
- `task` — delegate a self-contained subtask to a **subagent** (a nested agent loop
  with the file/command tools but not `task`). It starts in the background and the call
  returns its task id at once; its final report is pushed back when it finishes.
  `system_prompt_append` adds standing instructions for it — see
  [Subagent limits & prompts](#subagent-limits--prompts)
- any tools from configured **MCP servers** (below), as `mcp__<server>__<tool>`

All file tools are sandboxed to the working directory (symlink-aware). `/model`
shows or sets the model; `/clear` resets the conversation.

### MCP servers

Coda connects MCP servers declared in `.mcp.json` and exposes their tools to the agent
(and subagents). Two layers are merged, like skills and settings: a **user** file at
`~/.coda/.mcp.json` and a **project** file at `<workdir>/.mcp.json` (project entries override
user entries by name). All three entry points — the interactive TUI, `coda run`, and
`coda serve` — load the same merged config.

**Manage servers from the TUI with `/mcp`** — no hand-editing or restart required. In the full-screen TUI
fullscreen and inline shells, the exact bare `/mcp` is intercepted before dispatch (even while a turn
is busy) and opens the browser without starting a prompt. `/MCP`, `/mcp list`, and `/mcp x` are not
intercepted. When no MCP browser provider exists, bare `/mcp` remains textual. Plain mode
surfaces use the textual list/detail fallback (`/mcp`, `/mcp info <name>`, and the existing add/edit/
remove/start/stop/restart/enable/disable forms).

**The agent restarts a stuck server itself.** A stdio server that stops responding (its process is
alive but every call hangs) is usually fixed by a restart, so the model gets a `restart_mcp_server`
tool and calls it **autonomously, with no permission prompt** — the breakage shows up mid-turn when
you are not watching, so it disconnects the server (killing its process tree), launches it again and
retries. Restarting is not a privileged action: it re-launches a process the session already started.
Two rules keep it to exactly that. The server must be **present in `.mcp.json` and enabled** at the
moment of the call, so a `/mcp disable` or a removal takes effect immediately; and the relaunch
reuses the configuration this session already connected that server with, so a restart never doubles
as "apply my `.mcp.json` edit" (use `/mcp restart` for that). A server this session never started is
refused (use `/mcp start`). The tool stays available even when every server failed to connect, which
is when you need it most, and it is present in all three entry points — the TUI, `coda run`, and
`coda serve`.

The browser lists both physical scopes — user `~/.coda/.mcp.json` and project `<cwd>/.mcp.json` —
separately. Each list row shows the name, scope, stdio/HTTP transport, enabled/disabled state,
connection state, effective/overridden state, and any error; the source path appears in detail.
Effective project entries remain visible alongside the overridden user entry. List keys are arrows,
`PageUp`/`PageDown`, `Home`/`End`, `Enter` (detail), `a` (add), `e` (edit), `Space` (toggle),
`u` (reauthenticate), `Delete` (remove), and `Esc` (close). Detail keys scroll with arrows/
PageUp/PageDown/Home/End and support `e`, `Space`, `u`, `Delete`, and `Esc`. Editor keys are
`Tab`/`Shift+Tab`, focus-sensitive `Enter`, `Esc`, Backspace/Delete, `Ctrl+N`/`Ctrl+R` to add and
remove collection rows, and `Alt+Up`/`Alt+Down` to reorder them; `Up`/`Down` move between fields and
rows, and `Tab` moves between the name and value halves of an environment or header row. Printable
action letters insert text in fields.

In the browser, add/edit save directly, toggle does not confirm, and delete/reauthenticate prompt
with a default of No. A newly added server persists enabled but is deliberately not connected until
an explicit start/restart or other reconciliation-triggering action. A busy turn leaves browsing
and detail available, but management mutations are read-only until an idle/configuration lease is
available.

Reauthentication is ownership-aware: OAuth starts the browser flow, managed stored secrets prompt for
replacement, environment-owned credentials must be changed in the referenced variable, and literal or
unmanaged credentials are unavailable from this screen. After persistence, runtime state is reconciled
immediately. Renames stop the old runtime identity and start/reconcile the new one. If reconnect/start/
stop fails, the saved configuration remains authoritative and is reported as saved with a runtime error.

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
fresh sign-in is skipped with a note to stderr. Pre-authorize such servers once via the interactive
TUI; the encrypted token is then reused by every process automatically.

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

**Malformed tool schemas.** MCP servers are untrusted third-party input: they are installed
casually (`npx -y <pkg>@latest`), update without warning, and a published build can simply be
broken. A tool whose `inputSchema` is missing `"type": "object"` is rejected by the model APIs in a
way that fails the **entire request**, so — left alone — one bad tool would break every turn in the
session rather than just calls to that tool. Coda therefore guarantees that *a misbehaving server
costs you that server's tools, never your session*:

- **At ingestion**, an unusable schema is repaired least-destructively: every advertised key is
  preserved and only the missing `type` (and, if absent, `properties`) is added. A tool that may
  not accept arguments correctly is strictly better than one that cannot be used at all.
- **At the wire**, the request serializers enforce the same invariant independently, so a bad
  schema from *any* source (built-in tool, skill, subagent) can never reach a provider.
- **On rejection**, if a provider refuses a tool definition anyway, coda names the offending tool,
  quarantines it for the rest of the session, tells you, and retries the turn without it — up to
  five tools per turn — instead of failing.
- **Reporting**: a repaired server is called out at connect time and in `/mcp info <name>`, where
  affected tools are marked `[!] coerced schema`. When *every* tool is affected the message says so,
  since that almost always means a broken package version rather than one quirky tool.

Choose stricter handling with `mcpSchemaPolicy` in the **user** settings file
(`~/.coda/settings.json`; a project file cannot set it, so a cloned repo cannot disable the MCP
servers you rely on):

```json
{ "mcpSchemaPolicy": "coerce" }
```

| Value | Effect |
|---|---|
| `"coerce"` (default) | Repair the schema and keep the tool; warn. |
| `"skip"` | Drop the affected tools; the server's other tools still work. Reported in `/mcp info`, since a dropped tool otherwise leaves no trace. |
| `"strict"` | Refuse to register the server at all, with a hard error at connect time. |

An unrecognized value falls back to `"coerce"` — a typo must not silently disable a server.

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

Coda is Rust. The crates live under [`rust/crates`](rust/README.md); the two
remaining .NET projects exist to *drive* or *ship* that Rust binary, not to
reimplement it.

| Project | Language | Purpose |
|---|---|---|
| `rust/crates/coda` | Rust | The `coda` binary: interactive TUI, `coda serve`, `coda run`. |
| `rust/crates/coda-engine` | Rust | Standalone, TUI-free engine binary. |
| `rust/crates/coda-agent` | Rust | Agent loop, tools, permissions, scheduling, subagents. |
| `rust/crates/coda-tui` | Rust | The interactive terminal front-end (ratatui). |
| `rust/crates/coda-auth` | Rust | Providers, OAuth/device flow, credential stores. |
| `rust/crates/coda-llm` | Rust | Anthropic and Copilot streaming clients. |
| `src/Coda.Client` | `net10.0` | .NET SDK that drives `coda serve` out-of-process over JSON-RPC. |
| `src/Coda.Launcher` | `net10.0` | Thin shim that packs the native Rust binary as the `Coda.Cli` global tool. |
| `tests/Coda.Client.Tests` | `net10.0` | xUnit tests for the .NET client SDK. |

See [`rust/README.md`](rust/README.md) for the full crate list and the dependency
rules between them.

## Embedding Coda from .NET

`Coda.Client` launches and owns a `coda serve` child process and drives it over
the JSON-RPC protocol, so a .NET host gets the agent without hosting one:

```csharp
using Coda.Client;

await using var client = await CodaClient.StartAsync(
    EngineCommand.Default.WithWorkingDirectory(repoRoot));

await using var session = await client.CreateSessionAsync();
await foreach (var evt in session.PromptAsync("Add a retry policy to the HTTP client."))
{
    Console.WriteLine(evt);
}
```

The wire protocol it speaks is specified in
[`docs/serve-protocol.md`](docs/serve-protocol.md); any language that can spawn a
process and read stdio can do the same thing.

## GitHub Copilot (device flow)

Copilot signs in with the OAuth **Device Authorization Grant**: `coda` prints a
code, you enter it at github.com, and the short-lived Copilot token is refreshed
automatically from the stored GitHub token.

```powershell
coda auth login --provider github-copilot
```

> Copilot endpoints and headers are configurable through `GH_COPILOT_*` environment
> variables. Using the Copilot API outside official editor integrations is subject to
> GitHub's Terms of Service.

### GitHub Enterprise (data residency)

`/login copilot` (and first-run setup) asks whether you're on **public github.com** or
a **GitHub Enterprise Cloud** data-residency tenant (a `*.ghe.com` subdomain, e.g.
`octocorp.ghe.com`). Enterprise sign-in runs the device flow against your host, then
exchanges the durable device-flow token for a short-lived Copilot token at
`api.<domain>` — the same exchange public github.com uses — so Enterprise accounts get
full model entitlement rather than the legacy subset the raw token grants. The short-lived
token auto-refreshes from the stored durable token, the same way it does on github.com.
If the exchange endpoint turns out to be absent on a given host (or unreachable — DNS
failure, timeout, 5xx), sign-in and refresh fall back to using the raw device-flow token
directly as the bearer instead of failing outright; this is retried at most once per
process and re-checked automatically on the next restart.

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

Plain `dotnet build Coda.slnx` / `dotnet test Coda.slnx` still work
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

Rust `coda run` supports these controls (see [`rust/README.md`](rust/README.md)
for the full startup flag set):

| Flag | Effect |
|---|---|
| `--yolo` | Blanket-allow bypass — every mutating action runs without a prompt. |
| `--permission-mode <mode>` | Select the session's permission mode without changing saved defaults. |
| `--goal "<objective>"` | Enable the autonomous goal loop, subject to its duration and continuation budgets. |
| `--goal-timeout <duration>` | Positive wall-clock budget such as `30m` or `2h`; requires `--goal`. Alias: `--goal-max-duration`. |
| `--max-continuations <n>` | Nonnegative goal continuation budget; requires `--goal`. Alias: `--goal-max-continuations`. |
| `--effort <level>` | Session-only reasoning effort (`low`/`medium`/`high`/`xhigh`/`max`/`auto`), validated against model capability. |

Legacy-only options such as `--yolo-safe` and `--session-memory` are not supported
by the Rust CLI; they are rejected rather than silently enabled.

Example:

```powershell
coda run -p "refactor the assertions" --permission-mode acceptEdits --goal "consistent assertions" --goal-timeout 2h
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

A schedule can also **stop itself**: give it a run budget or a deadline when you
create it, or let the scheduled agent end it with `schedule_cancel_self` when the
job is done. See [Bounding a schedule](#bounding-a-schedule).

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

### Bounding a schedule

A schedule can stop itself. Two optional bounds may be combined with any
selector; **omit both and the schedule is unlimited**, exactly as before.

- **`maxRuns`** — a positive whole number of runs.
- **`expiresAt`** — an absolute ISO-8601 deadline — **or** `expiresIn`, a
  relative one (`"30m"`, `"2h"`, `"7d"`). The two are mutually exclusive, and a
  relative value is resolved to an absolute instant **once, at creation**.

```jsonc
// hourly, for the next week
{ "prompt": "check the incident queue", "every": "1h", "expiresIn": "7d" }

// daily at 09:00, seven times — "daily for a week" is a run budget, not a duration
{ "prompt": "post the standup reminder", "cron": "0 9 * * *", "maxRuns": 7 }
```

Exact semantics, because the obvious reading of each is wrong in a way that
matters:

- **`maxRuns` counts runs that were *started*, not runs that succeeded.** A run
  that later fails still consumes one. Only a launch that was refused outright,
  with nothing executed, is uncounted. Counting successes instead would let a
  flaky environment retry a "seven times" job forever.
- **The deadline is exclusive**: a run due exactly at `expiresAt` does not
  start. The schedule expires when the clock reaches the deadline, not at its
  last tick before it.
- **Expiry does not stop a run already in progress.** The in-flight run
  finishes; only future runs are prevented. While it finishes the schedule is
  listed as **retiring**, not completed.
- A retired schedule keeps a reason — **completed** (run budget spent),
  **expired**, **cancelled** or **failed** — *separately* from the outcome of
  its last run, because a budget can be spent by a run that failed.

`schedule_list` and the **`/schedule`** browser show the run budget, the
deadline, and a truthful state: `idle`, `running`, `pending`, `retiring`,
`completed`, `expired`, `cancelled` or `failed`.

For a **calendar month**, use an explicit `expiresAt` deadline or a `cron` rule
— there is no month-long interval, and `"30d"` is not a month. `every` is
absolute elapsed time and does not track wall-clock hours across a DST
transition; a `cron` rule with an IANA `timeZone` does.

Over `coda serve`, check the **`schedules.bounds`** capability before relying on
a bound: an engine without it accepts `maxRuns` and silently ignores it.

### A schedule that ends itself

A running scheduled agent can retire its own schedule with
**`schedule_cancel_self`** — the natural end for a watcher whose condition has
finally been met. It takes **no schedule id**: it always acts on the schedule
that started it and cannot reach another one. By default the current run
finishes normally and only future runs are cancelled; `stopRunning: true` also
ends the current run. Repeated calls are harmless.

Only the scheduled run itself may do this. A nested subagent inside a scheduled
job is refused and returns its conclusion to the run that started it, and
`schedule_create` / `schedule_delete` stay main-agent only — which is also what
stops a bounded run from recreating itself to escape its own budget.

### Timezones and DST

Offset-less `at` values and `cron` rules are interpreted in the **machine-local
timezone** by default. Provide `timeZone` (an IANA id like `"America/New_York"`, a
Windows id, or a fixed offset like `"UTC-05:00"`) to override it; an `at` value with
an explicit offset is honored as written. Across DST transitions the two schedule
kinds treat a nonexistent spring-forward local time differently: an offset-less
one-shot `at` value that lands in the gap is **rejected with an error** (there is
no such instant to resolve), while `cron` **skips** the nonexistent local minutes
and fires at the next valid occurrence. An ambiguous fall-back local time resolves
to the **earlier** UTC instant for both kinds (it is not run twice).

### When schedules run

Schedules execute **only while an interactive Coda session or `coda serve` is
open** — there is **no background daemon and no OS scheduler**.

> **The Rust engine keeps schedules in memory only.** Definitions, run counters
> and retirements live for the lifetime of the process and are **lost at
> restart**: nothing is written to disk and nothing resumes. The legacy C#
> engine persists them in `<project>/.coda/scheduled_tasks.json` and resumes on
> the next startup; where the two differ, the paragraphs below describe the C#
> behaviour.

On startup (C# engine) an **overdue** schedule runs **once immediately** (not
once per missed tick — missed ticks are **coalesced**), then advances to its
next future boundary. A clock jump produces one catch-up run, not a storm, in
both engines.

> **One session per project.** The schedule runtime is **in-process**: each open
> Coda session runs its own independent scheduler with no cross-process lease or
> de-duplication. If multiple interactive or `coda serve` sessions are open for
> the same project at the same time, **each session will fire the same persisted
> schedules independently**. To avoid duplicate runs, keep at most one
> scheduler-enabled session open per project.

A single definition **never overlaps itself**: while one firing is running, at most
**one** replacement run is held pending (further due ticks only advance the next
time, they don't queue up), and it starts after the running one reaches a terminal
state. **Different** definitions may run **concurrently**.

### Observing and steering runs

Each firing is a `TaskKind.Scheduled` background agent in the unified task runtime,
so it is visible in the interactive **`/tasks`** browser and to the `task_*` tools
(`task_list`, `task_get`, `task_peek`, `task_send`, `task_stop`) and its
secret-redacted log. `schedule_list` and the **`/schedule`** browser report each
definition's live state, run budget and deadline.

In the C# engine the interactive TUI also shows concise **notices** as a run starts,
completes, fails or is stopped, and `coda serve` forwards the same transitions as
`event/scheduleLifecycle` JSON-RPC notifications (see
[`docs/serve-protocol.md`](docs/serve-protocol.md)). **The Rust engine does not
forward these notifications yet** — its schedule runtime is wired to a null
lifecycle sink — so poll `schedule_list` / `session/scheduleList` instead.

`schedule_delete` prevents any **future** and **pending** run of a definition, but
does **not** stop a firing that is already executing (stop that with `task_stop`).
It is still a delete, not a soft cancel: the definition is forgotten entirely and
does not appear in `schedule_list` afterwards. A schedule that retires itself —
by reaching `maxRuns`, passing `expiresAt`, or calling `schedule_cancel_self` —
stays listed with its retirement reason instead.

Every scheduled run uses the session's **live** permission mode, current model, and
MCP / LSP / tool configuration at the moment it fires — a mid-session change is
observed by the next run. Scheduled agents run isolated at depth 1: they **cannot**
create or delete schedules (`schedule_create` / `schedule_delete` are unavailable
to them), see only their **own** definition in `schedule_list`, and can only
inspect and steer their **own** descendant tasks. The one schedule action they
do have is the targetless `schedule_cancel_self` described above.

### Failure and crash semantics

A run that throws is recorded as **Failed**, a cancelled/stopped run as **Stopped**,
and the outcome (with a short summary) is surfaced in `schedule_list` and the
lifecycle notice/event; recurring definitions keep their next boundary and run
again — a failed run consumes one unit of a `maxRuns` budget but does not retire
the definition by itself. One-shot `at` schedules are **at-least-once** in the C#
engine: if the process crashes mid-run the record survives and reruns on the next
startup, and it is **removed only after** it reaches a terminal state. A one-shot
whose launch is *refused outright* is retired with a **failed** reason rather than
retried. Failures are also written to the telemetry log when logging is enabled.

In `coda serve`, the stdio runtime starts immediately; an **API-key** peer's runtime does
**not** start until a valid key
completes an authenticated `initialize`. Headless `coda run` does **not** keep the
scheduler alive — the runtime is disabled outside interactive and serve sessions.

## Configuration & storage

Coda keeps **all of its own state under `~/.coda/`**, kept deliberately separate
from the Claude CLI's `~/.claude/`. It will, however, *read* a couple of shared
project files if they exist (it never writes to them).

| What | Location | Notes |
|---|---|---|
| User settings | `~/.coda/settings.json` | allow/deny rules, hooks, LSP servers, subagent limits, auto-compaction threshold, `mcpSchemaPolicy` |
| Project settings | `<project>/.coda/settings.json` | overrides user settings |
| Credentials | `~/.coda/credentials/` | DPAPI-encrypted (Windows) / AES-GCM (other OS) |
| Session transcripts | `<project>/.coda/sessions/<id>.json` | for `/resume` |
| Scheduled tasks | `<project>/.coda/scheduled_tasks.json` | C# engine only: persisted definitions, resume on next startup. The Rust engine keeps schedules **in memory only** and writes nothing here. |
| Plugins | `~/.coda/`, `<project>/.coda/plugins/` | |
| Skills | `~/.coda/skills/`, `<project>/.coda/skills/` (+ read-only `~/.claude/skills/`) | `SKILL.md` per skill |
| MCP servers | `~/.coda/.mcp.json`, `<project>/.mcp.json` | stdio + HTTP; project overrides user |
| Session memory | `<project>/.coda/SESSION_MEMORY.md` | when enabled |
| Telemetry logs | `~/.coda/logs/coda-<timestamp>-<pid>.log` | JSON-lines; opt-in; secrets redacted |

### Agent tool filter

> **This is a workflow control, NOT a security boundary.** Subagents, scheduled roots,
> and hook-spawned agents keep their full tool sets regardless of this setting. See the
> note at the end of this section.

`agent.tools` restricts which tools the **main agent** may use directly. Subagent registries
are built independently and are never filtered here, so a delegation-only workflow can be
enforced while the agents doing the actual work retain full capability:

```json
{
  "agent": {
    "tools": {
      "allow": ["task", "task_start"],
      "deny":  ["run_command"]
    }
  }
}
```

| Field | Effect |
|---|---|
| `allow` | When present, the main agent gets ONLY the tools in this list. Absent (or the key not present) means no allowlist — today's behaviour. An empty array (`[]`) is honoured literally and blocks every tool, which trips the inert-agent guard (see below). |
| `deny` | Removes the listed tools AFTER the allowlist step. Deny wins when a name appears in both lists. |

Matching is by `ITool.Name`, case-insensitive, so built-in, MCP, and plugin tools are all
addressable by the same rule. Unknown tool names in either list are silently ignored (inert).

**Merge rules** (user + project files):

- `allow` is **intersected** — a project file can restrict further but can never widen what
  the user file permitted.
- `deny` is **unioned** — either file's denials are always honoured.

**Inert-agent guard:** if the resolved filter would leave the main agent with neither `task`
nor `task_start`, Coda refuses to start and reports the offending settings file by path.
This prevents a configuration that silently cannot do anything useful.

**Not a security boundary.** The `agent.tools` filter is applied only to the main-agent
composition in `TurnPipelineBuilder.BuildParentTools`. Subagents, scheduled roots, and
hook-spawned agents each have their own independently built registries and are never
filtered by this setting. If the main agent can launch a subagent (`task` passes the
filter), that subagent can still run any tool it has — including ones the main agent cannot
call directly. This is intentional: `agent.tools` shapes _workflow_ (what the root turn
does), not _capability_ (what the session can ultimately do). Do not use it as an access
control mechanism.

### Auto-compaction

When the conversation grows past a threshold, a forked summariser replaces the older
history with a summary and the session continues. The most recent exchange is carried
through verbatim rather than summarised, so the message you just sent still reaches the
model, and no tool call is ever separated from its result. The threshold defaults to **80% of
the active model's context window**, so it scales with the model rather than being a
fixed number that suits only one of them.

Why 80% and not higher: the trigger is compared against an estimate of the *message
history* only — it cannot see the system prompt, the tool definitions, or the MCP tool
schemas, all of which are charged on every request and can run to tens of thousands of
tokens. The estimate is therefore a lower bound on the real prompt, and the remaining
20% is the margin that covers what it cannot measure.

Configurable in `~/.coda/settings.json`:

```json
{
  "compactAtPercent": 60,
  "compactByModel": {
    "github-copilot/claude-opus-5.5": { "tokens": 120000 },
    "claude-ai/claude-sonnet-4-6": { "percent": 70 },
    "gpt-5": { "freeTokens": 40000 }
  }
}
```

| Field | Default | Effect |
|---|---|---|
| `compactAtPercent` | `80` | How full the context window may get, as a percentage, before compaction fires. Values outside `0 < p <= 100` are ignored and the default applies. |
| `compactFreeTokens` | *(none)* | The same decision expressed as headroom: compact once fewer than this many tokens remain. **Wins over `compactAtPercent`** when both are set, being the more specific of the two. Ignored (falling back to the percentage) if it would leave less than 8,000 tokens to work in — which can happen without you typing a silly number, since the window is re-derived from whichever model is active. |
| `compactByModel` | *(none)* | Per-model overrides keyed `"<provider>/<model>"` (as `effortByModel`) or by bare model id, tried in that order; keys match exactly. Each entry takes `tokens` (an absolute threshold), `freeTokens` and/or `percent`, with the same meaning and validation as the global keys; within an entry `tokens` wins over `freeTokens`, which wins over `percent`. The first entry with a valid field replaces the global settings for that model; an entry with none is ignored. An entry's `freeTokens` that would leave less than 8,000 tokens falls back to the entry's own `percent`, or to 80% — not to the global settings. |

Every form — including a per-model `tokens` — is floored at 8,000 tokens and capped so the trigger always leaves room for a
reply (the [output budget](#output-budget) the turn requests). The cap matters only on
windows small enough that the percentage would not — an 8k window at 80% leaves 1.6k,
which is not a reply, so with a 4,096-token budget the threshold drops to 3,904.

`/compact` compacts on demand at any time, regardless of these settings.

### Output budget

Each response may be up to `max_tokens` long. The engine derives that from the active
model's real output limit — the provider's live model list when it reports one, otherwise
the bundled models.dev catalogue — and requests **the model's limit, capped at 32,000**
(32,000 when the limit is unknown). Subagents get the budget of the model they actually
run on. Near the end of the context window the budget shrinks so prompt plus reply still
fit, but never below 4,096.

Configurable in `~/.coda/settings.json`:

```json
{
  "maxOutputTokens": 64000,
  "maxOutputTokensByModel": { "github-copilot/claude-opus-4.8": 64000 }
}
```

| Field | Default | Effect |
|---|---|---|
| `maxOutputTokens` | *(none)* | Output budget for every model. Clamped to the model's own limit and raised to at least 4,096. |
| `maxOutputTokensByModel` | *(none)* | Per-model budget keyed `"<provider>/<model>"` (as `effortByModel`). Wins over `maxOutputTokens`. |

Values that are not positive integers are ignored. The budget used and where it came from
(`setting`, `live`, `catalog` or `fallback`) are recorded as `output_budget` in the
diagnostics log, and `turn_end` records the last response's `output_tokens`.

### Subagent limits & prompts

How deep and how wide a session may fan out into subagents is configurable in
`~/.coda/settings.json` (project file overrides user, field by field):

```json
{
  "subagents": {
    "maxDepth": 2,
    "maxConcurrent": 20,
    "allowSystemPromptReplacement": false,
    "model": "claude-haiku-4-5",
    "modelByType": { "explore": "claude-haiku-4-5" }
  }
}
```

| Field | Default | Effect |
|---|---|---|
| `maxDepth` | `2` | Deepest subagent nesting. The main agent is depth 0, so `2` permits a subagent and a grandchild. A subagent at the limit gets no task-management tools at all, so it cannot spawn or inspect anything. Clamped to `1`–`10`. |
| `maxConcurrent` | `20` | How many subagents may run at once across the session, foreground and background together. The budget is session-wide and counts ancestors, so a running parent still holds a slot while its child asks for one. A launch that cannot get a slot is **refused, never queued** — the tool returns an error naming the limit so the model can wait, work inline, or explain why it cannot. Clamped to `1`–`64`. |
| `allowSystemPromptReplacement` | `false` | Whether the `system_prompt` tool parameter (below) may replace a subagent's own instructions outright. **User file only** — a project file cannot set it in either direction, because `<project>/.coda/settings.json` belongs to whoever wrote the repo you cloned, and this is the one field that would hand a prompt-injected model the subagent's own instructions. |
| `model` | *(session model)* | Default model for every subagent. **User file only** (see below). |
| `modelByType` | *(none)* | Per-subagent-type model, keyed by type name (case-insensitive). **User file only.** |

Both limits are **clamped on load**: a settings file can raise one within reason but
never remove it. None of the three is reachable from tool input — the model cannot
widen its own budget or ungate its own prompt.

> **Fan-out is a fleet budget, not a per-level one.** The slot count is session-wide,
> so a leader that dispatches 20 workers holds every slot and those workers cannot then
> spawn children of their own. If you want depth *and* width, raise `maxConcurrent`.
> Note also that `task` returns at once and the subagent runs in the background, so
> several `task` calls in one response run in parallel; a parked subagent keeps its slot.

#### Choosing a subagent's model

`task` and `task_start` accept an optional `model`, so an expensive orchestrator can
dispatch cheap workers. The model must be one the **session's provider** serves —
this selects a model, not a provider.

Resolution order, first match wins:

1. the `model` argument on the tool call
2. `subagents.modelByType[<type>]`
3. `subagents.model`
4. a model declared by a plugin's agent definition
5. the session model

`subagents.model` and `subagents.modelByType` are **user-settings-file only**, and a
plugin-declared model is honoured only for **user-scoped** plugins. Both restrictions
exist for the same reason as `allowSystemPromptReplacement`: a project settings file
and a project-scoped plugin both belong to whoever wrote the repository you cloned, and
model choice is a spending lever. Operator settings outrank a plugin's declaration at
every level, so a plugin can only supply a model when you have expressed no opinion.

Model ids are **not validated** — Coda keeps no handcrafted model list, so an unknown id
surfaces as the provider's own error on the subagent's first call, which the agent can
read and correct. `task_get` reports the model a subagent actually resolved to.

#### Completion notifications

A **background** subagent that reaches a terminal state — completed, failed *or*
stopped — delivers its report into its owner's context at the next model call, instead
of the owner having to poll. Reports are truncated and the batch is capped, so a wide
fan-out cannot flood the context window; `task_output` still has the full log. A
`task_wait` that returns terminal consumes the entry it reports, so you never see the
same completion twice. Foreground `task` calls are unaffected — their report is already
the tool result. `coda serve` receives the same events as `event/taskCompleted`.

#### Influencing a subagent's system prompt

`task` and `task_start` accept two optional parameters that let the delegating agent
pass on standing instructions the subagent definition cannot know about:

| Parameter | Effect |
|---|---|
| `system_prompt_append` | Appended behind whatever the system prompt body is. It never replaces anything itself, so on its own the subagent type's own instructions stay in front. Always available. |
| `system_prompt` | Replaces the subagent type's own instructions entirely. Honoured **only** when `allowSystemPromptReplacement` is enabled; otherwise it is appended like `system_prompt_append` and a warning is logged. |

The assembly order is fixed: **subagent definition → caller text → `SubagentStart`
hook text**. That ordering is a security boundary, not formatting. Both parameters
carry model-written text, and the subagent definition is what stands between that
text and the subagent's tools — so caller text can only ever *add to* the
definition's instructions, never get in front of them, and an operator's
`SubagentStart` hook always has the final word.

Replacement is off by default for the same reason: it hands that leading position to
the caller. When it is enabled, the working-directory block still survives (factual
context, not a guardrail). Either way this only ever changes **text** — the child's
tool set, read-only status, depth and slot budget come from the subagent definition
and settings, never from the prompt, so an `explore` subagent whose prompt has been
replaced is still read-only.

### Tool display mode

Set `toolDisplayMode` only in the user settings file (`~/.coda/settings.json`); project settings cannot
override it. Existing explicit values remain honored. Manual changes take effect when you start the next
interactive session; Coda does not rewrite or migrate this setting. The default is `"summary"`:

```json
{ "toolDisplayMode": "summary" }
```

| Mode | Display |
|---|---|
| `"verbose"` | All tool details and results. |
| `"compact"` | Per-call status without full results. |
| `"summary"` (default) | One cumulative root activity block per root turn, spanning all model/root/subagent batches, with up to five running leaves, then one final line. Plain interactive output is final-only. |
| `"tiny"` | Tool activity is hidden. |

Each root turn has one activity block. The full underlying engine, history, audit, serve, task, and telemetry
data is retained regardless of display mode. An invalid nonblank value is reported safely at startup and falls
back to `"summary"`.

**Shared (read-only) with the Claude CLI:** `CLAUDE.md` project instructions
(including `~/.claude/CLAUDE.md`), `<project>/.mcp.json` MCP server config, and
`~/.claude/skills/` (lowest precedence — your `~/.coda/skills` and project skills
override by name). Override the skill source dirs with `CODA_CLAUDE_SKILLS_DIR` /
`CODA_USER_SKILLS_DIR` (point at a missing path to opt out). Coda also reads foreign
`.agents/skills/`, `~/.claude/agents/`, `~/.claude/commands/`, and `.claude-plugin/plugin.json`
sources read-only. See [`docs/skills-and-plugins.md`](docs/skills-and-plugins.md) for the full
authoring reference.

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
{"ts":"2026-06-04T12:00:00.0000000+00:00","level":"Information","category":"coda_llm","message":"..."}
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

## Notes & caveats

- **Windows-first.** The OAuth flow is cross-platform; only `DpapiTokenStore` is
  Windows-only. Other OSes implement `ITokenStore` (Keychain / libsecret) — stubbed
  for later.
- **Authorization:** the Claude.ai subscription sign-in is subject to Anthropic's
  Terms of Service. Using your own `ANTHROPIC_API_KEY` (via `ApiKeyProvider`) is the
  most straightforward option and is unaffected.
- OpenAI is planned as an additional `ICredentialProvider`. GitHub Copilot is
  already implemented (device flow, with automatic token refresh).
