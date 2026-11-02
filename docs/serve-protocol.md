# `coda serve` — Orchestrator Protocol

## Rust core (primary distribution)

The Rust engine uses Content-Length-framed JSON-RPC 2.0 over **stdin/stdout**.
`coda serve` and the standalone `coda-engine serve` use the same core entry
point. There is no Rust socket listener or remote-client authentication mode.
In Rust, `--api-key` / `CODA_SERVE_API_KEY` configure an **LLM-provider
credential**, and `--endpoint` configures that provider's URL; these must not
be mistaken for a control-plane password or listening address. `--endpoint`
still requires `--api-key`; an Anthropic **stored or exported** key is
redirected with the `ANTHROPIC_BASE_URL` environment variable instead, which
outranks nothing but the default host and is refused (rather than defaulted)
when invalid — for the Anthropic API-key identity alone, so a Copilot or
Claude.ai session is unaffected by it.
See the [Rust guide](../rust/README.md#anthropic-api-key-endpoint).

The caller owns the engine process and must keep its stdio connection alive.
Closing that connection is not a detach operation. Browser connectivity,
process supervision, remote authorization and relay integration belong to
the external orchestrator. See the [Rust guide](../rust/README.md) for the
current Rust CLI and operational diagnostics.
The [Rust API catalog](protocol/catalog.md) is the implementation-specific
method/event inventory, including supported limits and generated schemas.

## Transport scope

**The transport, authentication and detailed flag behaviour described below
were written against the retired C# host. The Rust engine speaks the same wire
protocol over stdio; the named-pipe/Unix-socket transport and its API-key
authentication were C#-only and are gone.** Treat anything transport-specific
here as historical unless the [Rust API catalog](protocol/catalog.md) confirms
it.

`coda serve` runs Coda as a **JSON-RPC 2.0 agent server** over either stdio or an
API-key-authenticated local named pipe/Unix socket, so an external orchestrator can drive it as a
coding subagent: send prompts, stream back results, answer permission/question/plan-approval
requests, interrupt turns, and read history.

```
orchestrator ──spawn/connect──► coda serve
        │  stdio or local pipe/Unix socket: bidirectional JSON-RPC
        └─ stdout (stdio/readiness): protocol bytes or endpoint discovery
```

- **Transport:** Content-Length-framed JSON-RPC 2.0 (same framing as LSP). Stdio uses the spawned
  process's stdin/stdout; API-key mode uses a local named pipe on Windows or Unix domain socket on
  other OSes. Stdio carries protocol bytes only; socket mode prints one readiness line to stdout,
  then carries protocol bytes on the connected endpoint. Human/debug logs go to stderr.
- **Authentication:** stdio is process-local and does not require an API key. Local pipe/socket mode
  requires the API key supplied by `--api-key` or `CODA_SERVE_API_KEY`; no unauthenticated socket
  is served. Networked transports are a future additive layer.
- **Versioning:** normally call `initialize` first; current `protocolVersion` is `"1"`. A first
  `session/prompt` can initialize when `initialize` is omitted.

## Lifecycle
1. Spawn `coda serve` (optionally `--provider`, `--model`, `--cwd`, `--permission-mode`, `--yolo`,
   `--yolo-safe`, and one of `--system-prompt <text>` or `--system-prompt-file <path>`). Permission modes: `default` (mutating tools raise `request/permission`),
   `accept-edits` (auto-accept edits), `plan` (drives `request/planApproval`), `yolo` /
   `bypass` (no prompts), and `yolo-safe` (bypass with a safety classifier that escalates risky
   actions via `request/permission`).
   Autonomous flags (all off by default): `--goal "<objective>"` sets a goal the agent works
   toward until a judge declares it met. **A goal also changes how the run handles questions**:
   `ask_user_question` is answered by a stand-in model that records its reasoning, rather than
   suspending the run against a terminal nobody is watching, and the run ends in one of four
   honest states (met, genuinely blocked, stalled, or out of budget) — never by waiting.
   The permission mode remains the autonomy envelope: a goal does **not** imply `--yolo`, and an
   action the mode forbids is recorded as a capability blocker rather than prompting.
   `--goal-max-duration <dur>` overrides the wall-clock budget (`90s`, `30m`, `2h`, `7d`, or
   `none` for no limit; default `240h`); `--goal-max-continuations <n>` overrides the turn budget
   (`none` for no limit; default `60000`); `--session-memory` enables the background
   session-memory watcher; `--max-continuations <n>` bounds stop-hook continuations per run
   (default 10).
2. Normally send `initialize` → get `{ protocolVersion: string, sessionId: string, serverInfo: string, telemetryLogPath?: string }`.
3. Send `session/prompt` to run a turn. While it runs you receive `event/*` notifications and may
   receive server-initiated `request/*` you must answer. The `session/prompt` response resolves
   when the turn completes (or is interrupted).
4. Repeat. `session/interrupt` cancels the in-flight turn. `shutdown` (or closing stdin) stops the server.
5. To replace the engine without losing its in-memory state (an upgrade), call
   `session/prepareRestart`, then `shutdown`, then start the new engine and send
   `initialize { sessionId, restore: true }` — see [Planned restart](#planned-restart-rust).

Startup ordering is deterministic: Coda starts the selected transport first and emits the local
pipe/socket readiness line after bind. It then connects configured MCP servers sequentially and
accepts the JSON-RPC connection only after MCP setup completes. On the `initialize` request, serve
authenticates when required, loads or resumes the requested transcript and metadata, and then starts
session initialization; the initialize result waits for that readiness before later prompts run.
MCP connection and provider warnings are written to `stderr`, while protocol stdout remains clean.

### Exact startup system prompt

`--system-prompt` and `--system-prompt-file` are mutually exclusive, accepted once only, and require
a separate value. Missing values, duplicates, and `--flag=value` forms fail before a session,
transport, or other startup side effect. A relative file is resolved from the process startup
directory rather than `--cwd`, read once as strict UTF-8 (an optional BOM is removed), and otherwise
keeps its whitespace, line endings, and trailing newline. These options belong to interactive Coda
and `coda serve`; `coda run` has no system-prompt option.

The supplied text is the exact complete root prompt, including explicit empty and whitespace-only
values: Coda's built-in prompt, project/`CLAUDE.md` context, output style, and provider prefix are
not added. Anthropic must omit an empty optional `system` block, while OpenAI serializes empty
values; that transport difference does not reinstate defaults.

The root override applies to scheduled root work and `/context`; subagents keep their role prompts.
For resumes, startup override wins over transcript metadata, which wins over the ordinary generated
prompt. Metadata is separate from the audited effective `systemPrompt`, and audits are never resume
input. Interactive, headless, and slash-command forks retain the metadata; session export/import
uses optional `systemPromptOverride` while remaining `coda.session/1` compatible.

## MCP servers

`coda serve` connects the same merged MCP config (`~/.coda/.mcp.json` + `<cwd>/.mcp.json`) as the
interactive TUI and `coda run`, exposing each server's tools to the session as
`mcp__<server>__<tool>` (plus the resource/prompt helper tools). This is **on by default**.

- **Disable per session:** pass `--no-mcp`, or set `CODA_SERVE_DISABLE_MCP=1` in the spawned
  process's environment (env wins over the default; `--no-mcp` and the env var are equivalent).
- **Curate the set:** set `CODA_USER_MCP_DIR` to a directory containing an orchestrator-owned
  `.mcp.json`. This replaces the user layer (`~/.coda/.mcp.json`) with a vetted set — the
  recommended way to give a programmatic session a deliberate, least-privilege tool surface
  instead of the operator's personal servers.
- **Full isolation:** add `--no-project-mcp` (or `CODA_DISABLE_PROJECT_MCP=1`) to also ignore the
  project-level `<cwd>/.mcp.json`, so a repo-local file can't override the curated user set. Combine
  with `CODA_USER_MCP_DIR` for a session that sees *only* the vetted servers. (Default: the project
  layer is loaded — full host visibility.)
- **Auth is non-interactive.** stdio servers launch normally; HTTP servers with a valid stored
  token are reused; an HTTP server needing a fresh OAuth sign-in is **skipped and logged to
  stderr** — serve never opens a browser and never blocks the handshake. Pre-authorize such
  servers once via the interactive TUI.
- **stdout stays pure.** All MCP connect/skip diagnostics go to **stderr**; no MCP output is ever
  written to stdout (the JSON-RPC protocol channel).
- **Startup timing.** Servers connect sequentially before the JSON-RPC connection is accepted. Each
  connection uses `CODA_MCP_CONNECT_TIMEOUT` in seconds (default **60**; missing, blank, or invalid
  values use the default; zero or negative disables the timeout). Many slow/hanging servers can
  therefore delay the handshake (bounded by server count × the configured timeout, and by
  shutdown/cancellation). Keep the curated set small, or use `--no-mcp` when a session needs no
  external tools.

## Orchestrator → Coda (requests)

The reference below retains the shared historical method vocabulary. Sections
marked **Rust** describe the additive Rust contract, not features of the
retired C# host. Consult the Rust catalog for the complete routed inventory;
the historical goal/startup details are not a promise of identical defaults.

| Method | Params | Result |
|---|---|---|
| `initialize` | `{ protocolVersion: string, clientInfo?: string, apiKey?: string, sessionId?: string, restore?: true\|false\|"suspended" }` | `{ protocolVersion: string, sessionId: string, serverInfo: string, telemetryLogPath?: string, restored?: RestoredSummary }` |
| `session/prompt` | `{ text?, images?: [{ mediaType, base64 }] }` | `{ ok, stopReason?, interrupted, goalStatus? }` |
| `session/interrupt` | `{}` | `{ ok }` |
| `session/history` | `{}` | `{ messages: [{ role, content }] }` |
| `session/messages` | `{ sinceIndex }` | `{ messages, nextIndex }` |
| `session/models` | `{ refresh? }` | `{ source, models: [{ id, displayName?, contextLimit? }] }` |
| `session/setGoal` | `{ goal?: string\|null, maxDuration?: string, maxContinuations?: int }` | `{ ok, goal?, maxDuration?, maxContinuations? }` |
| `shutdown` | `{}` | `{ ok }` |

`session/models` resolves the provider's model list (live endpoint → models.dev
catalog → built-in); `source` is `live` / `catalog` / `builtin`. `refresh: true`
re-fetches the catalog from models.dev first.

### Rich history, interaction and configuration (Rust)

| Method | Params | Result |
|---|---|---|
| `session/getHistory` | `{ sessionId?, historyEpoch?, expectedHistoryLength?, engineInstanceId?, sinceIndex?, limit?, includeLive? }` | `{ sessionId, engineInstanceId, isLiveSession, historyEpoch?, cursor?, historyLength, entries[], nextIndex, totalKnown, truncated, liveEntries?, liveTruncated?, liveOmittedBytes? }` |
| `session/listSessions` | `{ limit? }` | `{ sessions: [{ sessionId, createdUtc, messageCount, preview, previewTruncated, isCurrent }], totalKnown, truncated }` |
| `session/listTasks` | `{ statusFilter? }` | `{ sessionId, tasks: [{ id, parentId, kind, description, status, mode, outputTail, report, suspendRequested? }] }` |
| `session/getPendingRequests` | `{}` | `{ requests: PendingRequest[], engineInstanceId }` |
| `session/resolveRequest` | `{ requestId, outcome }` | `{ ok, state, requestId, outcome }` |
| `session/cancelRequest` | `{ requestId, reason? }` | `{ ok, requestId, appliedDefault, outcome }` |
| `config/describe` | `{}` | `{ entries: ConfigEntry[] }` |
| `config/set` | `{ key, value }` | `{ ok, key, appliedAt, effective?, error? }` |
| `mcp/list` | `{}` | `{ servers: McpServer[], enabled, managerAvailable }` |
| `session/import` | `{ path }` | `{ ok, sessionId }` |
| `session/backgroundAll` | `{}` | `{ ok, count }` |
| `session/stopAllTasks` | `{}` | `{ ok, count }` |
| `task/background` | `{ taskId }` | `{ ok, taskId, reason? }` |
| `task/stop` | `{ taskId }` | `{ ok, taskId, reason? }` |
| `task/send` | `{ taskId, text }` | `{ ok, taskId, reason? }` |
| `task/suspend` | `{ taskId }` | `{ ok, taskId, reason? }` |
| `task/resume` | `{ taskId }` | `{ ok, taskId, reason? }` |
| `session/suspendAllTasks` | `{}` | `{ ok, count }` |
| `session/resumeAllTasks` | `{}` | `{ ok, count }` |
| `session/askAside` | `{ question }` | `{ ok, answer?, reason?, error? }` |
| `session/prepareRestart` | `{ drainTimeoutMs?, reason?, deliveredCursor? }` | `{ ok, restartFile, drained, interruptedTasks, interruptedRequests, suspendedTasks }` |

**Initialization gate.** `session/getHistory`, `session/getPendingRequests`,
`session/resolveRequest`, `session/cancelRequest`, `config/set`, the nine task controls
(`session/backgroundAll`, `session/stopAllTasks`, `task/background`, `task/stop`, `task/send`,
`task/suspend`, `task/resume`, `session/suspendAllTasks`, `session/resumeAllTasks`),
`session/askAside` and `session/prepareRestart` require a completed
`initialize` and otherwise return `-32011`. Read-only discovery — `session/listSessions`,
`session/listTasks`, `config/describe`, `mcp/list`, `session/getState`, `session/getEvents` — is valid **before**
`initialize`, so a client can choose a session before resuming one. Every route that shipped
before this contract keeps exactly the behaviour it had; the gate is not retrofitted.

**Task discovery.** `session/listTasks` reads the current Rust engine's authoritative
registry in registration order, including silent running tasks and retained terminal
tasks. It is shared by local and API-only `/tasks` browsers, including `serve` mode;
clients must not infer task existence or status from local log files or completion
notifications. `kind` is `subagent`, `shell`, `scheduled` or `tool` (a long MCP tool call
the agent runs as a task); `status` is `running`, `suspended` (a subagent paused at a safe
point — not finished; see [Task suspension](#task-suspension-rust)),
`completed`, `failed`, or `stopped`; `mode` is `foreground` or `background`.
`suspendRequested: true` marks a task that was asked to suspend but has not reached a safe
point yet (its `status` is still `running`); it is omitted otherwise.
`statusFilter` (optional) narrows the listing without affecting any task: `active` returns every
unfinished task — `running` (foreground or background, with or without output, including parked
subagents) or `suspended` —, `suspended` only the suspended ones, `terminal` only `completed`,
`failed` and `stopped` ones, and `all` — the behaviour when it is omitted — everything. Any other
value is refused with `-32602` (invalid params).
The full-screen TUI's `/tasks` asks for `active`; its history view (`h`, or `/tasks all`) asks
for `all`.
The session id namespaces the registry's task ids. Ids are opaque strings, unique within a
session and never reused: the engine issues `task-00000001`, `task-00000002`, … (eight digits
at minimum, wider past `task-99999999`), so clients must not parse them or assume a length.
This is runtime discovery, not an archive of task logs from earlier engine processes.

**Task controls (non-blocking runtime).** Every `run_command` runs as a `shell` task (at most
32 run at once; another is refused with an explanatory tool error), every long MCP tool call
as a `tool` task, and
every subagent as a `subagent` task that starts in the `background` (nothing ever waits on
it in the foreground). While
the agent waits on a task it is `foreground`; once the agent stops waiting (its yield
budget ran out, or a client asked) it is `background` and keeps
running. Messages never end the wait; `session/backgroundAll` and `task/background` are
the only ways to end it early — never the
work; the agent receives a task handle and continues. `count` is how many foreground tasks
were released (`0` is not an error). `task/stop` and `session/stopAllTasks` stop tasks and
kill shell process trees (stopping a subagent also stops everything it started).
`task/send` delivers `text` to a running subagent's inbox: it is delivered at the
subagent's next boundary (a running wait is not cut short), or wakes it when it is parked (waiting, without model calls, for tasks it
started). A refused `task/*` call answers `ok: false` with `reason` `notFound`,
`notRunning`, for `task/background` only `notForeground` (already in the background — every
subagent is) / `notBackgroundable`, and for `task/send` only `notAgent` (a shell task has no
inbox), `notAccepting` (the subagent is finishing) / `emptyText`; `task/suspend` and
`task/resume` add `notSuspendable`, `notSuspended` and `suspendedByParent` (see
[Task suspension](#task-suspension-rust)). When a background shell or
subagent task owned by the main agent ends — completed, failed, or stopped by the agent —
exactly one `[task-completed]` item (for a subagent, carrying its final report) is delivered
to the main conversation through the main inbox (announced like any delivered agent message,
`source: "taskCompletion"`); an idle engine starts a turn for it. Tasks stopped by the
operator (`task/stop`, `session/stopAllTasks`) are not reported back to the main agent, so a
stop never triggers a new turn. Completions of tasks owned by a subagent go to that
subagent's own inbox and are never seen by clients as agent messages. Engine `shutdown`
stops every task. A message delivered to an agent while tasks it started are running —
a steering message, or a new prompt to the main agent — reaches the model prefixed with a
compact `[live-tasks] … [/live-tasks]` list (id, kind, description, age), so the model knows
those tasks keep their original parameters and must be stopped and restarted to change.

`outputTail` contains at most 4,000 Unicode characters of buffered, secret-redacted
output, using the same per-channel streaming redaction as task logs. Browsing does
not consume agent output cursors or completion notifications and does not require
working log storage. Incomplete secret candidates remain withheld until confirmed
or flushed at completion. `report` is the optional completion result or error,
capped at 16 KiB of UTF-8 text followed by `[report truncated]` when necessary.
Reload the browser to read updated state. RPC failures remain explicit errors;
older engines without this method are not treated as having no tasks.

**`session/import` does not touch the live session.** It is the inverse of export: `path`
names a portable `*.coda-session.json` bundle (see `coda_agent::session::bundle`), which the
engine reads, validates, and writes into its own workspace as a new session — minting a fresh
id when the bundle's own id already exists locally, so importing the same bundle twice never
clobbers the earlier import. A relative `path` resolves against the *engine's* working
directory, never this client's; an absolute path is used as-is. Unlike `fork`/`rewind`/
`compact`, it never replaces the live conversation, so it neither claims the foreground turn
slot nor bumps `historyEpoch`, and it is not gated on `initialize`. A malformed bundle, an
unreadable path or an unsupported schema version is `-32602` and leaves the sessions directory
exactly as it was — validation happens before anything is written.

**Turn duration is server-measured.** `turn.startedAt` and `turn.phaseSince` are the engine's
UTC wall clock — fine for display, wrong for measuring. A remote client's clock may be skewed,
in a different VM, or adjusted mid-turn, and re-deriving elapsed time from a UTC string makes a
reconnecting timer jump. `turn.elapsedMs` and `turn.phaseElapsedMs` are therefore measured on
the **engine's own monotonic clock** at the instant the snapshot is taken, and are what a
rehydrating or remote client seeds its timer from: no clock agreement needed, immune to
wall-clock adjustments, and never rewinding across a resync. `phaseElapsedMs` restarts on every
phase change while `elapsedMs` keeps running, so "waiting for the model for 40 s" and "this turn
started 4 minutes ago" stay separate facts. Both are omitted only by an engine that does not
report them — never a confident `0`, which would look like a turn that just started. An idle
engine omits `turn` entirely.

**`config.next.providerId` is populated whenever a client is wired.** It is absent only when
the engine genuinely has no provider yet (before `initialize` wires a credential), never because
a particular code path did not look. `session/getState`, `event/configChanged` and
`config/describe` all report the same value, so a client converging on events does not lose a
provider it already knew, and the running turn's `activeConfig.providerId` is populated from the
moment the turn opens — including for `compact`/`fork`/`rewind` turns, which never re-resolve a
client.

**History consistency.** `session/getHistory` reads the committed prefix and the in-flight turn
at the same instant `session/getState` would, so
the committed entries assembled across all fenced pages, followed by the live projection
once, represent the conversation without counting a turn twice. A single bounded page is not
the whole committed history. Entry `index` values are absolute and
stable **within one `historyEpoch`**. Three optional fences make that exact:

- `historyEpoch` — a stale epoch (fork/rewind/compact/resume happened) is `-32012`, never a
  partially-valid page.
- `expectedHistoryLength` — a turn committed between pages is `-32013`, never silently
  interleaved content from two conversation states.
- `engineInstanceId` — a different engine process is `-32010`.

`liveEntries` is present only when `includeLive: true`, this is the live session, and a turn is
actually running; it is **omitted**, never an empty array. A saved transcript has no epoch,
no cursor and no live turn, so those fields are absent rather than reported as `0`.

An assistant live entry may carry `streamingTextIndex`, the zero-based index into
`blocks` of the text block still receiving deltas at the read's cursor. Completed text
omits it, even while the turn is still running. Committed entries always omit it.
A client must not infer that text is open merely because it is in `liveEntries`:
completion events at or before the read's cursor are already reflected in this metadata.
Engines predating this field cannot identify an open text block on reconstruction.

`entryKind` (`userPrompt` / `toolResults` / `assistant`) is carried alongside `role`. A
provider conversation encodes tool results as **user-role** messages: rendering `role: "user"`
as "something the operator typed" invents prompts that never happened, and `entryKind` is how
you avoid that. `role` itself is always exactly the provider protocol role.

`steeringMessageId` is present on a live entry that **is** a queued message the engine has
delivered into the running turn, and absent everywhere else (a committed message carries no
queue id). Delivered steering is added to the live projection in the same transaction that
records the `delivered` outcome, so a read that reports the outcome always contains the text:
`session/getState` and `session/getHistory` never say a message reached the model over a
conversation with no trace of it. A client holding its own copy of a queued message should use
that id — never the text, since two messages with identical text are two messages — to decide
whether a re-read conversation already contains it.

**Internal metadata is not exposed.** History, state, config and MCP projections do not expose a
`Content::Thinking.signature`, `RedactedThinking` ciphertext, image base64, a provider
credential, an MCP `env` value, a custom header value, a `coda-secret:` target, or a URL with
userinfo/query/fragment as provider/MCP metadata. This is not content redaction:
authorized conversation text, tool results and request displays remain sensitive and
can contain arbitrary user/tool-supplied data. Reasoning is carried as a readable `reasoningSummary` (with
`redacted: true` and empty text when the provider redacted it); images are carried as
`{ mediaType, byteLength }`. Oversized fields are truncated with an explicit
`omittedReason` plus the true `fullLength` — never silently shortened. That applies to **every**
history block that can carry free text, including `text` and `reasoningSummary`, not only tool
inputs and results: a capped 64 KiB prompt or reasoning summary always says so. `omittedReason`
is `"tooLarge"` for a per-block byte cap and `"liveBudgetExceeded"` when the live-turn
retention budget cut the block; `fullLength` is the original byte length, measured before the
UTF-8-boundary backtrack, so it is never the post-cap number. A `redacted: true` reasoning block
carries neither field: its text is empty by policy, not by truncation. Both fields are omitted —
never `null` — when nothing was dropped.

**Optional params are optional; malformed ones are not.** For the methods this contract
introduces (`session/getState`, `session/getHistory`, `session/listSessions`) absent or `null`
params mean "use the defaults", but a *supplied* field of the wrong type is `-32602`. A
`{"historyEpoch": "abc"}` must never quietly become an unfenced page 0, and a
`{"sections": "turn"}` must never quietly become "send everything". Unrecognised keys are still
ignored, so forward compatibility is unaffected. Routes that shipped before this contract keep
their older, tolerant parsing — deliberately, so clients that work today keep working.

**Page limits.** `limit` must be at least 1; `0` and negative values are `-32602`, not a silent
fallback to the default page size, and so are negative `sinceIndex`, `expectedHistoryLength` and
`historyEpoch`. A `limit` **above** the ceiling is clamped rather than refused, and the ceiling
is published as `limits.maxHistoryPage` / `limits.maxSessionPage` so it is discoverable;
`nextIndex`/`truncated` already tell you there is more to fetch.

**History resets are atomic.** `fork`, `rewind`, `compact` and `resume` replace the committed
messages and bump `historyEpoch`/`historyLength` inside one critical section (lock order
`HISTORY -> STATE -> BUS`). A concurrent `session/getHistory` therefore either sees the old
conversation under the old epoch or is rejected with `-32012`; a page accepted under epoch *E*
always describes the conversation as it was at epoch *E*.

**Config scopes are what the engine implements**, not aspirations. There is no pending-change
scheduler: `model`, `effort`, `systemPrompt` and `goal` are read when the next turn is built
(`nextTurn`), `permissionMode` at the next permission decision (`nextPermissionCheck`),
`provider` and MCP/plugin state only by a new engine process (`newEngineInstance`), and theme /
tool display / keybindings are the client's own (`clientLocal`). `config/set` accepts only the
keys `describe` reports as `mutable: true` (`model`, `effort`, `permissionMode`,
`systemPrompt`), delegates each to the same validated method the dedicated RPC uses, and reports
`effective` by **reading the engine back** — never by echoing the request. An immutable key is
refused with the same reason `config/describe` gives, so the two cannot drift apart. Coda never
writes a client's `.mcp.json`, plugin or marketplace files: those stay read-only here and remain
local maintenance UX on the machine that owns them.

**`mcp/list` is read-only and performs no probe.** It reports names, scope, transport, a
sanitised target label, the *configured* state (`enabled`/`disabled`/`shadowed`) and the
*runtime* state (`connected`/`notConnected`/`notAttempted`/`unknown`) separately, because a
`.mcp.json` can be edited while the manager keeps running the servers it connected at startup.
`toolCount` is omitted when it is not known — never a confident `0`. It never reads a credential
store, so `secretRefs[].resolved` is **omitted entirely**: the field is a tri-state and its
absence means "this call did not determine it". Reporting `false` would assert as fact that the
reference does not resolve, which is a different — and unverified — claim. Only a fact the
engine already holds may ever populate it; a read must not probe an auth store or wake a keyring
prompt to find out. `lastError` is optional: the sanitised last connection error for an enabled
server that is not connected; restart it via the agent's `restart_mcp_server` tool.

**`config/describe` names the authority for open value sets.** Where the accepted values are
provider- or model-dependent, the entry carries `allowedValuesFrom` — the RPC that owns the live
list — so an absent or snapshot `allowedValues` is still actionable. `model` points at
`session/models`; `effort` points at `model/reasoningCapability`, including when the levels
happen to be known, because they are per-model and re-resolved on `session/setModel`. A key with
a genuinely closed set (`permissionMode`) carries no pointer.

**`PendingRequestDto.callId` is reserved and always omitted.** The permission, question and
plan-approval seams inside the engine do not carry the provider tool-call id, so there is
nothing truthful to put there and nothing is invented. The capability
`requests.callCorrelation` is advertised as `supported: false` for exactly this reason —
correlate on `turnId` together with the `event/toolCall` stream instead. The field stays in the
wire shape so a host that does have the id can populate it without a contract change.

**`PendingRequestDto.turnId` names the turn that actually raised the request.** The engine hands
one permission prompt to the agent loop, to subagents and to the schedule runtime, so a request
can arrive from execution that belongs to no turn at all. `turnId` is captured from the
**execution scope** of the caller, not from whichever turn happens to be running when the
request lands:

- work the turn awaits inline — tool permission checks, `ask_user_question`, plan approval, goal
  escalation, *foreground* subagents — carries the turn's own id;
- **background subagents and scheduled runs carry no `turnId` at all** — they run detached and
  outlive the turn that started them, so borrowing its identity would be a fabrication.

The phase machine follows the same rule: only a request belonging to the running turn moves it
to `awaitingUserInput`, and only that turn's own requests hold it there. A background approval
is fully listed in `requests` and published as `event/requestPending`/`event/requestResolved`,
but it never makes an unrelated foreground turn look like it is blocked on a human, and never
pins one there after its own question has been answered. Unknown origin is omitted, never a
guess.

**One turn identity.** The `turnId` is minted once for the prompt attempt, used when its
single-flight slot is claimed, and
is the same value used by the state snapshot, `event/activity`, `event/turnEnded`, the
diagnostics context and the execution scope above. There is no second, independently-minted id
for logging.

### `session/setGoal` (historical C# defaults)

Mutates the session's autonomous goal settings in-place (persist-until-cleared). The goal
drives the agent to keep working turn after turn until a judge declares it met (or the budget
is exhausted). A null or empty `goal` clears the active goal.

Each call sets the **complete** goal configuration for the session (it does not merge with a
prior call). A new goal takes effect from the **next** `session/prompt`; it never disturbs a
turn already in flight (the running turn captured its options at its start).

**Params:**
- `goal` (string | null): the objective text, or null/`""` to clear the current goal.
- `maxDuration` (string, optional): wall-clock budget. Accepts suffix forms only — `90s`,
  `30m`, `2h`, `7d` — or one of `none` / `unlimited` / `off` for **no wall-clock limit**.
  An explicitly-supplied but unparseable value (including a zero-length one) returns a
  `-32602` error; **omitting the field reverts the budget to the default, `240h`** rather
  than preserving a prior override.
- `maxContinuations` (int, optional): turn-count budget. `-1` means **no turn limit**; any
  other negative value returns `-32602`. **Omitting reverts to the default, `60000`.**

A goal is a promise to keep working until the judge declares it met, so both defaults are set
high enough that reaching one indicates a runaway rather than a merely long task. Use `0`
continuations to escalate at the very first natural stop — that is a real setting, and is not
the same as "no limit".

**Result:**
```json
{ "ok": true, "goal": "all tests pass", "maxDuration": "30m", "maxContinuations": 200 }
```
Fields are omitted when null (goal cleared / budget not set).

### `goalStatus` in the `session/prompt` result

When a goal was active and produced a non-`None` outcome, the `session/prompt` result includes
a `goalStatus` object:

```json
{
  "ok": true,
  "stopReason": "end_turn",
  "interrupted": false,
  "goalStatus": {
    "outcome": "Met",
    "remaining": null,
    "continuations": 5,
    "elapsedSeconds": 42.3,
    "escalated": false,
    "extensionUsed": false
  }
}
```

- `outcome`: `"Met"` | `"Unmet"` | `"GenuinelyBlocked"` | `"Stalled"` (never `"None"` — that
  case omits the field).
- `remaining`: the judge's last "what still remains" text, or null when the goal was met.
- `continuations`: the number of forced-continue nudges issued during the run.
- `elapsedSeconds`: wall-clock seconds elapsed during the goal run.
- `escalated`: true when the budget was exhausted and an escalation question was sent.
- `extensionUsed`: true when the one bounded extension was granted.

The two proved outcomes are reported alongside an `event/limitReached` notification whose `kind`
is `goal.genuinelyBlocked` or `goal.stalled` and whose `message` is the operator-facing report:
each parked blocker, what was tried, what a human would need to supply, and any low-confidence
decision the stand-in made on their behalf.

- `GenuinelyBlocked` — every remaining piece of work is parked behind a recorded blocker. This
  is the provable "nothing further is possible", not a guess.
- `Stalled` — the run was looping with nothing left that it could advance. This is what
  guarantees termination when the operator set no budget.

**Resume:** pass `sessionId` to `initialize` to resume a prior conversation. On that request, serve
loads the transcript and persisted metadata before session initialization. If a transcript
exists at `<cwd>/.coda/sessions/<sessionId>.json`, its history is loaded and the session adopts
that id (subsequent turns persist back to the same file). If no transcript exists for that id,
`initialize` fails with `-32002` "session not found" rather than silently starting fresh — omit
`sessionId` to start a new session (the returned `sessionId` is then newly generated). Transcripts
are written automatically after every turn. Resumed metadata is applied before session
initialization. Although clients should send `initialize` first, the first `session/prompt` can
initialize the session if that request was omitted. Adding `restore` to that `initialize` also
applies the state a previous engine saved with `session/prepareRestart` — see
[Planned restart](#planned-restart-rust).

Only one turn runs at a time — a `session/prompt` while busy returns a JSON-RPC error.

**Image input:** `images` is an optional array of base64-encoded images attached to the turn.
Supported media types: `image/png`, `image/jpeg`, `image/gif`, `image/webp`. Maximum 5 MB per image
(decoded). Images are input-only — providers do not return image content. A prompt with any invalid
image (unsupported type, non-base64 data, or oversized) is rejected with a JSON-RPC error before
any turn runs; the session remains idle and accepts the next prompt normally.

## Coda → Orchestrator (notifications — streamed during a turn)
`event/assistantText {delta}` · `event/assistantTextComplete {}` · `event/toolCall {toolName,
inputJson, rootTurnId?, activityId?, callId?, sourceId?}` · `event/toolProgress {toolName,
elapsedMs, rootTurnId?, activityId?, callId?, sourceId?}` · `event/toolResult {toolName, content,
isError, rootTurnId?, activityId?, callId?, sourceId?, status?}` · `event/error {message}` ·
`event/stop {stopReason?}` · `event/usage {inputTokens, outputTokens, cacheReadTokens?,
cacheWriteTokens?, cacheWrite5mTokens?, cacheWrite1hTokens?, totalInputTokens?}` ·
`event/turnComplete {stopReason?, interrupted, rootTurnId?, activityId?}`.

Assistant-text deltas arrive in order.

### Tool correlation

In **Rust**, identify tool invocations by canonical `turnId`, `batchId` and
`callId` together. Provider call IDs can repeat in different batches.
`batchId` aliases `activityId`. Preserve `rootTurnId` and `sourceId` for
legacy compatibility, but do not infer the originating agent or the whole
turn from them: Rust's legacy root IDs may be batch-scoped and source IDs
may carry provider call IDs.

The retired **C#** identity contract for `event/toolCall`, `event/toolProgress`,
and `event/toolResult` is:

- `rootTurnId` identifies the root turn. `event/turnComplete` carries it even when that turn made
  no tool calls.
- `activityId` identifies the tool-activity batch. It is omitted from `event/turnComplete` when
  the root turn made no tool calls.
- `callId` identifies an individual tool invocation, so same-name calls remain distinct.
- `sourceId` identifies the originating agent: `root:<rootTurnId>` for root work, or
  `subagent:<taskId>` for forwarded subagent work.
- `status` is present on correlated `event/toolResult` and is the stable `ToolCallStatus` enum
  name: `Pending`, `AwaitingApproval`, `Running`, `Succeeded`, `Failed`, `Cancelled`, or
  `Skipped`.

These fields are additive. Clients that do not need correlation may ignore them; absent fields mean
the event came from a legacy caller or does not have that identity. The protocol version remains
`"1"` and all JSON-RPC method names are unchanged.

### Envelope metadata and ordering (Rust)

Every notification — legacy `event/*` and the newer state/queue events alike — additionally
carries `seq` and `engineInstanceId` in its `params`. `seq` is gapless and strictly increasing
within one `engineInstanceId`, and frames reach the connection in `seq` order: assignment,
retention in the replay ring, and the write to the connection are one indivisible step.
This is the generated sequence: a legacy client that has not negotiated state
events does not receive those frames, so its received sequence may have holes.

A `session/getState` snapshot's `cursor` fences the state projection: transitions at or below
that cursor are covered, and later transitions are not. Buffer events during a read and avoid
reapplying covered state. A snapshot is not a complete archive of every transient notification:
errors, limits and hook/task notices may need separate handling or replay. Each projected
transition and its wire frame are published in the same transaction.

`session/getHistory.cursor` fences conversation content only. It does not replace queue,
configuration, interaction or usage state; advancing a global event cursor to a history cursor
would silently discard those unrelated transitions.

`historyLength` is the matching fence for conversation content: it counts **committed**
messages only. An in-flight turn is never included there — it lives in `turn.liveEntries` —
and the instant it becomes committed, `turn` is already omitted. Concatenating
`history[..historyLength]` with `turn.liveEntries` therefore cannot double count at any
observable instant.

If replay outruns the bounded ring, `session/getEvents` reports `truncated: true` together with
`oldestAvailableCursor` (the seq of the oldest envelope still retained; when nothing is
retained, the next seq that could be) and the client re-snapshots. Payloads are never silently
shortened: a frame that could not be encoded occupies its seq as an explicit
`event/eventsDropped` marker rather than leaving a hole or letting a later event reuse the
number.

### Terminal turn vs availability (Rust)

These are two separate facts and the engine publishes them separately.

- **The turn is terminal** when `turn` is omitted, `lastTurnOutcome` is set, `historyLength`
  has moved and every in-flight tool call has been finalised. `event/turnComplete` (legacy) and
  `event/turnEnded` (gated) are published in that same transaction.
- **The engine is available** when `lifecycle` becomes `ready`. That happens strictly later, at
  the moment the single-flight slot is actually released, and is published as `event/lifecycle`.

Between the two a snapshot has `lifecycle: busy, lastTurnOutcome: {...}` and no `turn` — "this
turn is over, the engine is not yet free". The guarantee is one-directional and exact: **if a
snapshot says `ready`, the execution slot is available.** This does not guarantee valid prompt
parameters, available credentials, or that another request will not claim the slot first.
The engine may briefly still say `busy`
after the slot frees, which only ever under-promises; it never claims to be free while it would
refuse a prompt.

`session/fork`, `session/rewind` and `session/compact` take the same slot as a prompt for their
whole duration and publish a turn in the `maintenance` / `compacting` phase while they hold it —
a prompt is refused, and a fork is refused while a prompt is running. `shutdown` publishes
`stopping` and then `stopped`. `session/prepareRestart` publishes `draining`: from then on the
engine refuses new work with `-32016` and never returns to `ready` or `busy` (see
[Planned restart](#planned-restart-rust)); `shutdown` still moves it to `stopping` and `stopped`.
The lifecycle values are therefore `initializing`, `ready`, `busy`, `draining`, `stopping` and
`stopped`.

Initialization shares the exclusion slot without creating an agent turn.
It reports `initializing`, refuses concurrent initialization/work, and cannot
complete successfully after shutdown. See the catalog for repeated
initialization, failure and interruption semantics.

### Gated state events (Rust)

With `clientCapabilities.stateEvents` negotiated, a client converges on lifecycle, activity
phase, turn termination, effective config, the steering queue and session resets from the event
stream alone: `event/lifecycle`, `event/activity`, `event/turnEnded`, `event/configChanged`,
`event/steeringQueue`, `event/sessionChanged` (capability `state.turnEvents`), and the authoritative
usage accounting on `event/usageUpdated` (see [Usage accounting](#usage-accounting-rust)). Tool detail
and per-response usage ride on the ungated `event/toolCall` / `event/toolResult` / `event/usage` frames.
This is **not** a claim that every `StateSnapshot` field is push-based — anything else still
needs a snapshot, and `session/getEvents` + `cursor` remain the way to resynchronise.

Capability negotiation is processed before anything gated is published, so a client that resumes
a session in the same `initialize` call receives that session's own `event/sessionChanged`.

### Usage accounting (Rust)

Three different quantities, which a client must keep apart:

| Quantity | Where | Meaning |
|---|---|---|
| **Context occupancy** | `usage.lastResponse.totalInputTokens` ÷ `usage.contextLimit` | Everything the most recent request sent — uncached input **plus** cache reads **plus** cache writes. Cached input still occupies the window. |
| **Cumulative traffic** | `usage.session` | Every request's tokens summed, by category. Re-sent conversation is counted each time, so it passes the window size after a few turns and says nothing about how full the window is. |
| **Estimated cost** | `usage.cost` | Each category priced at its own rate, at the price of the model that served each request. |

Token counts (`UsagePair`, used by `event/usage`, `lastResponse` and `session`):

- `inputTokens` — **uncached** input only (the meaning it always had).
- `cacheReadTokens`, `cacheWriteTokens` (= `cacheWrite5mTokens` + `cacheWrite1hTokens`, split by
  cache TTL where the provider itemises it; un-itemised writes count as five-minute), and
  `totalInputTokens` = `inputTokens + cacheReadTokens + cacheWriteTokens`.
- `outputTokens`.

Provider differences are normalised before anything reaches the wire: an API whose reported input
already includes cached tokens (OpenAI-style `prompt_tokens` / `input_tokens` with a
`cached_tokens` detail) has the cached part moved into `cacheReadTokens`, so nothing is counted
twice. The cache fields are **additive**: an engine that knows about them always sends them, zeros
included; an older engine omits them. A client that sees them absent must treat the cache counts
as **unknown** — `inputTokens` is then only a lower bound for the context, never a measured empty
window. Older clients ignore the new fields and keep reading `inputTokens` as before (and so keep
under-reporting the context of mostly-cached requests until they read `totalInputTokens`).

`event/usage` is one response's counts. The cumulative totals belong to the engine: the gated
`event/usageUpdated` carries the same `UsageState` the snapshot's `usage` section does, and a
client **assigns** it rather than adding anything, so replayed, duplicated or reconnect-snapshot
frames cannot inflate a total. `UsageState` also carries:

- `lastResponseModel` — the model that produced `lastResponse`.
- `lastResponseStale: true` — `lastResponse` is the last *known* measurement, not the current
  context: it was restored from a saved session, or the history (rewind, compaction, fork) or the
  active model changed since. The next request replaces it.
- `sessionCoverage` — `complete`, or `partial` when the session was saved by a version that did
  not record usage: the totals then cover only the requests measured since, and the real figure is
  higher. Absent from engines that predate it (coverage unknown). An engine that sends it sends its
  whole accounting in every projection, so a client may replace its copy outright.
- `byModel` — the per-model share of `session`.
- `cost` — `{totalUsd, inputUsd, cacheReadUsd, cacheWriteUsd, outputUsd, complete}`. `complete`
  is `false` when some measured tokens had no known price or the history is partial; the dollar
  figure is then a lower bound. Absent means nothing has been priced — unknown, never free. Cache
  writes are priced by TTL; where the catalogue has only one write price it is the five-minute
  rate, and one-hour writes are priced at twice the input rate.

**Persistence.** The accounting is saved with the session transcript (an optional `usage` record
older readers ignore) at every turn end, rewind and fork, and restored by assignment when
`initialize` resumes the session — before anything reports the resumed totals — so restoring,
reconnecting or replaying never double-counts. A fork starts from its parent's accounting.

### Failure reporting and retention (Rust)

`lastTurnOutcome.error` is a bounded classification — `{ category, status?, parameter? }` —
never a provider message, an `LlmError` `Display`, or a `Debug` dump. `category` is a closed set
(`llm.<class>`, `agent.other`, `engine.<reason>`, `incomplete`), `status` is the HTTP status when
there was one, and `parameter` is at most one allowlisted request-parameter path. The raw message
is still returned once, synchronously, in the `session/prompt` result; it is never retained in
state, which any client can poll for the rest of the session.

`limits.liveBytesCap` is the **accounted retention budget** for the live turn. Text, identifiers,
tool names, status labels and a fixed per-entry / per-block / per-chunk constant are all charged
against it, so nothing can be appended for free and entry/block/chunk counts are bounded by the
same number. It is not advertised as an exact process-memory bound: allocator overhead is not
measured. When it does cut a live block, that block carries
`omittedReason: "liveBudgetExceeded"` and `fullLength`, so a truncated live prompt, reply or
reasoning summary can never look complete; `liveTruncated`/`liveOmittedBytes` remain the
turn-level summary.

## Connection loss

Closing the engine's stdin is a first-class fault, not an edge case. When the read loop sees
EOF (or an unrecoverable framing error) every outstanding server-initiated request is resolved
with its fail-closed default **before** the transport tears down: `request/permission` → deny,
`request/planApproval` → reject, `request/question` → a typed no-answer
(`outcome: "noAnswer.disconnected"`), never the first option and never an empty string. A
question that is not answered aborts the run with `agent.aborted.question.noAnswer.*` and issues
**no** follow-up model request. This is verified out of process against the real binary in
`coda-engine/tests/eof_conformance.rs`, which half-closes a piped stdin and asserts the scripted
provider served exactly one request.

## Coda → Orchestrator (schedule lifecycle — out of band)

When the session is started with the schedule runtime enabled (`coda serve` sets
`EnableScheduleRuntime`), each scheduled definition that fires emits an
`event/scheduleLifecycle` notification. Unlike the `event/*` turn events above, these
are **not** tied to a `session/prompt` turn — a schedule can fire between turns — so
the orchestrator may receive them at any time while the connection is open.

```jsonc
event/scheduleLifecycle {
  "definitionId":   "a1b2c3",           // schedule id
  "definitionName": "nightly backup",   // optional label, omitted when null
  "taskId":         "task-9",           // the TaskKind.Scheduled task id, omitted for a pre-launch failure
  "state":          "started",          // "started" | "completed" | "failed" | "stopped" | "retired"
  "timestamp":      "2026-07-21T09:00:00Z",
  "summary":        "…"                 // optional short detail (result or error), omitted when null
}
```

`state` is the lower-case transition: `started` when a firing registers and begins,
then exactly one terminal of `completed` / `failed` / `stopped`. Optional fields are
omitted from the wire when null. The underlying `TaskKind.Scheduled` task is also
visible through the normal `task_*` tools and logs.

A **bounded** schedule additionally produces a `retired` transition — at most once
per definition — when it will never launch again, with a `summary` naming the
reason (`retired: completed` for a spent run budget, `expired`, `cancelled`, or
`failed`). `retired` carries no `taskId`: it is a statement about the *definition*,
not about a run, and it can arrive after the last run's own terminal transition. A
definition at its limit with work still in flight is not retired yet;
`session/scheduleList` reports it as `retiring` until that run finishes.

> **Rust engine:** the schedule runtime's lifecycle sink is currently a null sink,
> so `event/scheduleLifecycle` notifications (including `retired`) are **not
> forwarded to the client** by `coda serve`. Poll `session/scheduleList`, whose
> `state`, `runsStarted` and `retiredReason` fields are authoritative. See the
> bounded-schedules section of [`docs/protocol/catalog.md`](protocol/catalog.md)
> and the `schedules.bounds` capability.

The runtime is authentication-gated: over **stdio** (no expected API key) it starts
at session startup, so schedule events may arrive immediately; in **API-key** mode it
does not start — and therefore emits nothing — until a valid key completes an
authenticated `initialize`.

### Task events — `event/taskStarted`, `event/taskOutput`, `event/taskCompleted`

Not tied to a turn: tasks start and end at any time while the session is open. Every task
(`shell`, `subagent`, `scheduled`, `tool` — a long MCP call run as a task) produces exactly one
`event/taskStarted` and one `event/taskCompleted`; in between, `event/taskOutput` carries its
new **secret-redacted** output.

```jsonc
event/taskStarted {
  "taskId":      "task-00000003",
  "kind":        "shell",        // "shell" | "subagent" | "scheduled" | "tool"
  "description": "cargo test",
  "ownerTaskId": "task-00000001", // the agent that started it; absent for the main agent
  "mode":        "foreground"    // "foreground" (an agent waits on it) | "background"
}
event/taskOutput {
  "taskId":    "task-00000003",
  "text":      "running 12 tests\n",  // output since the previous frame (newest kept)
  "truncated": false                  // true when output before `text` was skipped
}
event/taskCompleted {
  "taskId":      "task-00000003",
  "status":      "failed",       // "completed" | "failed" | "stopped"
  "description": "cargo test",
  "report":      "exit code: 101", // result or error, ≤ 4 000 chars; absent when none
  "ownerTaskId": "task-00000001", // absent for the main agent
  "kind":        "shell",
  "mode":        "foreground"    // "foreground": the owner saw the end inline (its tool
                                 // result says so); "background": reported separately
}
```

`event/taskOutput` is throttled: at most **4 frames per second per task**, each at most
**2 000 characters** (the newest part; `truncated: true` when anything was skipped). The
engine never buffers output for a client: it keeps one cursor per running task into the
task's bounded redacted tail. While the connection is not keeping up — a write blocked for
1 s, or more than 256 frames queued — no `taskOutput` frames are sent at all; the next frame
after recovery says `truncated: true`. A client that never reads therefore cannot make engine
memory grow with task output. The last output of a task is sent before its `taskCompleted`
(unless the connection is congested). Use `session/listTasks` for the authoritative list and
the retained tail; the task log has everything.

A subagent that pauses at a safe point and continues later additionally produces
`event/taskSuspended` and `event/taskResumed` (see [Task suspension](#task-suspension-rust)).
A subagent re-created by a [planned restart](#planned-restart-rust) restore is announced by the
new engine with an `event/taskStarted` carrying `"resumed": true` under its original `taskId`;
`resumed` is omitted for every other start.

```jsonc
event/taskSuspended {
  "taskId":      "task-00000002",
  "ownerTaskId": "task-00000001", // absent for the main agent
  "kind":        "subagent",
  "reason":      "restart"        // "requested" | "parent" | "restart"
}
event/taskResumed {
  "taskId":      "task-00000002",
  "ownerTaskId": "task-00000001",
  "kind":        "subagent"
}
```

### `session/askAside` — side questions (`/btw`)

`{ question }` → `{ ok, answer?, reason?, error? }`. One separate, **tool-less** model
request answers the question from the conversation so far (committed history plus the turn in
flight, as a bounded transcript) and a live snapshot of the task table (ids, kind, owner,
status, age, last output lines). It runs concurrently with the main agent — it takes no turn
slot, so it works while a turn is running — and is **never** added to the history. Refusals:
`emptyQuestion`, `noProvider` (no model connected), `modelError` (`error` names the failure
class, never provider text; the request is abandoned after 120 s). Requires `initialize`.

## Task suspension (Rust)

A **subagent** task can be paused at a *safe point* and continued later; shell, tool and
scheduled tasks cannot (they are `notSuspendable`). A safe point is the top of an agent-loop
iteration, before the next model request, or the subagent's park (waiting, without model calls,
for tasks it started). It is never in the middle of a model response; a tool batch in progress
completes first. A suspended subagent makes no model calls, keeps its conversation, and has
`status: "suspended"` — a non-terminal status: it has **not** finished and has not reported.

| Method | Effect |
|---|---|
| `task/suspend {taskId}` | Requests suspension of one running subagent. Its descendants pause with it (inherited suspension). Idempotent: `ok: true` again for an already suspended task. |
| `task/resume {taskId}` | Clears that subagent's own suspension. Its descendants continue too, unless they were suspended explicitly themselves. |
| `session/suspendAllTasks` | Requests suspension of every live subagent; `count` is how many were newly requested. |
| `session/resumeAllTasks` | Clears every explicit suspension; `count` is how many were cleared. |

Refusals answer `ok: false` with `reason`: `notFound`; `notSuspendable` (not a subagent);
`notRunning` (already finished); for `task/resume` also `notSuspended` (neither suspended nor
asked to suspend) and `suspendedByParent` (paused only because an ancestor is suspended — resume
that ancestor). `task/resume` and `session/resumeAllTasks` are refused with `-32016` while the
engine is [draining](#planned-restart-rust).

Between the request and the safe point the task is still `running` and `session/listTasks`
reports `suspendRequested: true`. When it pauses the engine publishes `event/taskSuspended`
with `reason` `requested` (suspended itself), `parent` (inherited from an ancestor) or
`restart` (suspended by a planned-restart drain); when it continues, `event/taskResumed`.
`statusFilter: "suspended"` lists only suspended tasks, and `active` includes them.

While suspended a subagent still accepts `task/send` (delivered after it resumes), still
collects the completion reports of its own child tasks, and can be stopped with `task/stop` or
`session/stopAllTasks`; `shutdown` stops it like any task. An agent that waits on a suspended
child — `task_wait`, or a foreground wait on the task — returns at once (an in-flight wait
wakes) with a normal, non-error result:

```text
Task 'task-00000002' is suspended (paused at a safe point); it has not finished. Its report will be delivered to you automatically after it is resumed and finishes. You may end your turn or wait again.
```

The capability `tasks.suspend` advertises these methods.

## Planned restart (Rust)

Schedules, `notify_user` notifications, the `ask_main` inbox, protocol-set settings, queued
steering and running subagents live only in the engine's memory. A **planned restart** — for
example replacing the engine with a newer Coda — carries them over: the old engine drains and
writes a *restart file*, the new engine applies it once during `initialize`. The capability
`session.plannedRestart` advertises it.

```text
old engine:  session/prepareRestart → (draining…) → result { restartFile, … } → shutdown
new engine:  initialize { sessionId, restore: true } → { …, restored: { applied: true, … } }
```

### `session/prepareRestart`

| Param | Meaning |
|---|---|
| `drainTimeoutMs` | How long to wait for running work before interrupting it. Default `600000` (10 min), maximum `3600000` (1 h; larger is `-32602`). `0` interrupts immediately. |
| `reason` | Free text, saved in the restart file and quoted in the `[restart]` note. |
| `deliveredCursor` | The last `notify_user` cursor the client has already shown. Retained notifications after it are saved and re-delivered after the restart. Absent means `0`: every notification the bus still retains is re-delivered. |

The result arrives once the restart file is written:

| Field | Meaning |
|---|---|
| `ok` | `true`. |
| `restartFile` | Absolute path of the restart file. |
| `drained` | `true` when everything finished or suspended before the timeout, so nothing was interrupted. |
| `interruptedTasks` | Tasks the drain stopped (listed in the file's `interrupted.tasks`). |
| `interruptedRequests` | Open permission/question/plan requests the drain declined. |
| `suspendedTasks` | Ids of the subagents saved suspended for the next engine. |

A second call while the first is still draining is refused with `-32016`; a call after it
finished returns the same result again without draining twice. The engine **stays draining**
afterwards: send `shutdown` and start the new engine.

### Draining

`session/prepareRestart` moves the lifecycle to `draining` (published as `event/lifecycle
{ lifecycle: "draining" }` to clients that negotiated state events). From then on:

- **New work is refused** with JSON-RPC error `-32016`, message `"restart pending"`:
  `session/prompt`, `session/compact`, `session/fork`, `session/rewind`, and turns the engine
  would start itself (the idle `ask_main` delivery — queued items stay queued and are saved).
  `task/resume` and `session/resumeAllTasks` are refused the same way.
- **Running work continues.** Steering into the running turn (`session/steer`) is still
  accepted, and open requests can still be answered (`session/resolveRequest`, the raw
  `request/*` reply, `session/cancelRequest`). Reads, `task/stop` and `shutdown` work as usual.
- **Schedules stop launching.** Definitions are kept unchanged; a due run is simply not
  started.
- **Subagents suspend.** Every live subagent is asked to suspend and pauses at its next safe
  point (`event/taskSuspended`, `reason: "restart"`); a subagent started during the drain
  starts suspended.
- The engine never returns to `ready`/`busy`; `shutdown` still goes `stopping` → `stopped`.

`session/getState` reports `lifecycle: "draining"` and a `drain` section (omitted otherwise):

```jsonc
"drain": {
  "startedAt":   "2026-11-02T12:00:00Z", // RFC 3339 UTC
  "deadlineAt":  "2026-11-02T12:10:00Z", // running work is interrupted at this instant
  "remainingMs": 412000,                 // until deadlineAt, never negative
  "reason":      "upgrade",              // omitted when none was given
  "completed":   false                   // true once the restart file is written
}
```

The drain is complete when no turn holds the execution slot, every shell, tool and scheduled
task has finished, and every subagent has finished or is suspended. If that does not happen
before `drainTimeoutMs`:

1. Everything still running is recorded — each shell/tool/scheduled task with its command,
   start time and the last 2,000 characters of its secret-redacted output, each open request
   (its kind and tool name or question text) — and queued steering is withdrawn and saved.
2. The running turn is cancelled, the recorded tasks are stopped (shell process trees are
   killed), and the open requests are declined (fail-closed: deny / no answer / reject). An
   agent that was waiting on a stopped task receives the tool result
   `Interrupted by a planned restart; last output:` followed by that output tail (`(no output)`
   when empty).
3. The engine waits up to 10 s for the turn to end and then up to **30 s** (the settle window)
   for subagents to reach a safe point and suspend. A subagent still not suspended after that —
   typically one waiting on a slow model response — is stopped and recorded as an interrupted
   task of kind `subagent`, together with anything else still running.

The model is told about all of this after the restart (see the `[restart]` entry below).

### The restart file

The file is `<cwd>/.coda/sessions/<sessionId>.restart.json`. It is written atomically (a
temporary file renamed into place) and readable only by its owner (mode `0600` on Unix; on
Windows a protected DACL with a single full-control entry for the current user). It is written
**last**, after every subagent file. `session/listSessions` ignores `*.restart.json` files.
Format version `1`:

```json
{
  "version": 1,
  "codaVersion": "0.1.176",
  "sessionId": "8c1f0b2e-5d7a-4c11-9e3b-2f6a1d0c9b77",
  "workingDir": "C:\\work\\repo",
  "preparedAt": "2026-11-02T12:10:02.481Z",
  "reason": "upgrade",
  "schedules": [
    {
      "schemaVersion": 3,
      "id": "a1b2c3",
      "name": "nightly backup",
      "kind": "cron",
      "prompt": "Back up the database and report the size.",
      "cron": "0 2 * * *",
      "timeZoneId": "Europe/Berlin",
      "nextRunUtc": "2026-11-03T01:00:00Z",
      "createdAtUtc": "2026-10-30T09:00:00Z",
      "updatedAtUtc": "2026-11-02T01:00:05Z",
      "maxRuns": 30,
      "runsStarted": 4
    }
  ],
  "messages": [
    {
      "channel": "user",
      "cursor": 17,
      "id": "3f9c2a7e5b0d4e1a8c6f1b2d3e4a5c6d",
      "source": "scheduledTask",
      "label": "nightly backup",
      "text": "Backup finished: 1.2 GB.",
      "taskId": "task-00000004",
      "scheduleDefinitionId": "a1b2c3",
      "scheduleName": "nightly backup"
    },
    {
      "channel": "main",
      "cursor": 3,
      "id": "9a8b7c6d5e4f40312a1b0c9d8e7f6a5b",
      "source": "subagent",
      "label": "reviewer",
      "text": "Should I also review the migrations?",
      "taskId": "task-00000002"
    }
  ],
  "settings": {
    "model": "claude-sonnet-4-5",
    "provider": "anthropic",
    "effort": "high",
    "goal": { "goal": "Make the test suite green", "maxDuration": "2h", "maxContinuations": 40 },
    "permissionMode": "acceptEdits",
    "systemPrompt": "You are the release assistant."
  },
  "steering": ["also check the Windows build"],
  "interrupted": {
    "tasks": [
      {
        "taskId": "task-00000003",
        "kind": "shell",
        "description": "npm test",
        "command": "npm test",
        "startedAt": "2026-11-02T12:01:30Z",
        "outputTail": "  412 passing\n  3 pending\n"
      }
    ],
    "requests": [
      { "kind": "permission", "toolName": "run_command" },
      { "kind": "question", "question": "Delete the old fixtures?" }
    ]
  },
  "tasks": ["task-00000002"]
}
```

- `schedules` holds each definition exactly as `session/scheduleList` knows it (id, bounds,
  `runsStarted`, `nextRunUtc`, retirement).
- `messages` holds `notify_user` notifications (`channel: "user"`, `cursor` is the bus cursor)
  and pending `ask_main` items (`channel: "main"`, `cursor` is the main-queue sequence).
  `source` is `scheduledTask`, `subagent`, `main` or `taskCompletion`; `context`,
  `idempotencyKey` and the provenance fields are omitted when absent.
- `settings` omits what is not set; `goal` carries the remaining per-goal budgets.
- `steering` is operator steering that was queued but not yet delivered.
- `interrupted.tasks[].kind` is `shell`, `tool`, `scheduled` or `subagent`; `command` is present
  for shell tasks. `interrupted.requests[].kind` is `permission`, `question` or `planApproval`.
- `tasks` lists the subagents saved suspended; each has its own file (below).

### Subagent files

Each suspended subagent is saved to `<cwd>/.coda/sessions/<sessionId>.tasks/<taskId>.json`
with the same atomic, owner-only writer (format version `1`):

```jsonc
{
  "version": 1,
  "taskId": "task-00000002",
  "ownerTaskId": null,              // the agent that started it; null for the main agent
  "reportsTo": "main",              // "main" or the owner's task id
  "kind": "subagent",
  "description": "review the diff",
  "mode": "background",
  "spec": {                         // how it was spawned
    "agentType": "general-purpose", "prompt": "Review the diff …", "systemPrompt": "…",
    "model": "claude-sonnet-4-5", "provider": "anthropic", "effort": null, "depth": 1,
    "maxSteps": 200, "maxTokens": 16000, "foreground": false, "scheduleOrigin": null
  },
  "conversation": [ /* the subagent's messages, in the session transcript format */ ],
  "stepsUsed": 7,
  "parked": false,                  // true when it was parked waiting for its own tasks
  "steering": [{ "kind": "message", "text": "focus on the API" }], // undelivered inbox items
  "explicitlySuspended": false,     // suspended by the operator before the drain
  "startedAt": "2026-11-02T11:58:10Z",
  "suspendedAt": "2026-11-02T12:00:01Z",
  "truncated": false
}
```

A file is capped at **8 MiB**. When the conversation is larger, the oldest tool results are
replaced, one by one, by `[truncated for a planned restart: N characters omitted]` until it fits,
and `truncated` is `true`.

### Restore on `initialize`

`initialize` accepts `restore`: `true` (restore; suspended subagents continue), `"suspended"`
(restore, but keep the restored subagents suspended until `task/resume`) or `false` (restore
nothing — the default). Any other value is `-32602`. `restore` requires a `sessionId`
(`-32602` otherwise). The file is read and validated **before** the initialization is claimed:

- A file whose `version` is newer than this engine supports fails `initialize` with **`-32017`**
  (`restart file format 2 is newer than this Coda supports (1)`) and no side effects: the client
  may retry without `restore`.
- Otherwise a file that cannot be used is **ignored** and left on disk; the session resumes as
  a plain resume and `restored` says why: `missing` (no file), `unreadable` (not valid JSON or
  not a restart file), `sessionMismatch`, `workingDirMismatch` (compared case-insensitively on
  Windows), `expired` (`preparedAt` older than 24 h, or more than 5 min in the future), or
  `consumeFailed` (a valid file that could not be deleted — it is never applied without being
  consumed).
- A valid file stands in for a missing transcript: the session starts empty under that id.
  Without a transcript and without a valid file, `initialize` fails with `-32002` as usual.

A valid file is **deleted before anything is applied**, so a restore happens at most once; a
second `initialize { restore: true }` reports `ignoredReason: "missing"`. Each subagent file is
deleted once its subagent is re-created.

`coda serve --restore` makes `restore: true` the default for an `initialize` that leaves it out,
`--restore=suspended` makes it `"suspended"`, and `--session <id>` supplies the default
`sessionId` (environment equivalents: `CODA_SERVE_RESTORE` = `true` | `false` | `suspended`,
`CODA_SERVE_SESSION`). Values the client sends win.

The response carries `restored` exactly when a restore was asked for:

| Field | Meaning |
|---|---|
| `applied` | `true` when the file was applied; `false` with `ignoredReason` otherwise. |
| `ignoredReason` | One of the reasons above; omitted when applied. |
| `preparedAt`, `reason`, `fromVersion`, `toVersion` | When and why the old engine prepared the restart, and the two Coda versions. |
| `schedules`, `messages` | How many schedules and messages were restored. |
| `settings` | Names of the settings applied (`model`, `effort`, `goal`, `systemPrompt`, `permissionMode`). |
| `settingsNotRestored` | `[{ name, value?, reason }]` for each saved setting deliberately not applied. |
| `interruptedTasks`, `interruptedRequests`, `steering` | What the drain interrupted, declined and carried over. |
| `tasks` | Ids of the subagents re-created under their original ids. |
| `tasksSuspended` | `true` for `restore: "suspended"`. |

What a restore applies:

- **Schedules** keep their ids, bounds and `runsStarted`. A recurring (interval/cron) schedule
  whose `nextRunUtc` passed while the engine was down moves to its first slot after now — no
  burst and no catch-up run. A one-shot `at` schedule whose time passed stays due and fires
  **once** after the restore (at-least-once). Retired definitions come back retired.
- **Messages** are re-published **after** the `initialize` response is written, with **new**
  cursors on the new engine's bus: notifications arrive as fresh `event/agentMessage`
  notifications, and `ask_main` items are queued again and wake the idle main conversation.
  Recover them with `session/pendingMessages` from cursor `0` like any other notification.
- **Settings** are applied through the same paths as their setters, and anything refused is
  listed in `settingsNotRestored`. `bypassPermissions` is **never** restored: the mode stays
  `default` and is reported. The provider is never switched: when the new engine runs a
  different provider, `provider`, `model` and `effort` are reported instead of applied.
- **Subagents** are re-created under their original task ids with their conversation, step
  count, inbox and spawn settings, announced by `event/taskStarted { resumed: true }`, and
  continue from their safe point (or stay suspended for `restore: "suspended"`, and always when
  the operator had suspended them before the drain). Their reports reach their owners as usual.
  When no provider can be wired during `initialize`, they are re-created the first time the
  session services are built and are not listed in `tasks`.
- **One `[restart]` entry** is appended to the restored conversation as a user-role message (after
  any interrupted tool calls in the transcript are closed) and saved at once, so the model learns
  what happened on its next turn. Lines appear only when they apply:

```text
[restart] Coda was restarted for an upgrade (0.1.176 → 0.1.177).
Steering sent before the restart that was not delivered: "also check the Windows build".
Interrupted: run_command npm test (started 12:01 UTC, last output: 412 passing 3 pending).
Declined requests: permission for run_command; question "Delete the old fixtures?".
Suspended subagents restored: task-00000002.
Re-run anything still needed.
```

The first line reads `… for an upgrade (0.1.176 → 0.1.177): <reason>.` when a reason was
given, and `[restart] Coda was restarted.` / `[restart] Coda was restarted (<reason>).` when
both engines have the same version. `Re-run anything still needed.` is added only when
something was interrupted or declined.

### Error codes

| Code | Message | When |
|---|---|---|
| `-32016` | `restart pending` | The engine is draining for a planned restart and refuses new work; also a second `session/prepareRestart` while one is in progress. |
| `-32017` | `restart file format N is newer than this Coda supports (1)` | `initialize { restore }` found a newer restart file; nothing was changed, retry without `restore`. |

## Coda → Orchestrator (server-initiated requests — you MUST answer; the agent blocks)
| Method | Params | Reply |
|---|---|---|
| `request/permission` | `{ toolName, inputPreview, requestId? }` | `{ allow: bool }` |
| `request/question` | `{ question, options[], multiSelect, allowFreeText, requestId? }` | `{ answer: string }` |
| `request/planApproval` | `{ plan, requestId? }` | `{ approve: bool }` |

These are Coda's interactive host callbacks routed to the wire. If you interrupt a turn while one
is outstanding, the awaiting callback resolves as deny/decline so the turn unwinds cleanly.

### `requestId` on the raw frame (additive)

Every `request/*` params object carries `requestId`: the **same opaque handle**
`session/getPendingRequests` lists that request under. It exists because the two surfaces
describe the same request, and a client that reconciles them by guessing gets it wrong in both
directions — showing two prompts for one decision, or discarding the responder for the raw
round-trip, which **declines** it.

Rules:

- **Compare it; never parse it.** The current spelling is `req-<engineInstanceId>-<n>`, and that
  is explicitly not part of the contract. Reconstructing a handle from the numeric JSON-RPC
  request id would silently address a different request after an engine restart.
- **It is optional.** A legacy engine omits it; a client that has never heard of it ignores it.
  The JSON-RPC `id` stays numeric and keeps its meaning, so an existing client is unaffected.
- The pre-existing fields are unchanged. This is additive only.

If you hold the raw round-trip, answer *that* — it is the engine's own exactly-once path and
needs no second call. Use `session/resolveRequest` only for a request you discovered without
receiving its frame (after a reconnect, or from a second surface).

### Fail-closed outcomes (binding)

A disconnect, a cancellation, a configured timeout, an explicit JSON-RPC error reply, or a reply
with no usable value all take the safe path — never a grant, and **never a fabricated choice**:

| Request | Fail-closed outcome |
|---|---|
| `request/permission` | `allow: false` (deny) |
| `request/planApproval` | `approve: false` (reject) |
| `request/question` | **no answer at all** |

`request/question` in particular does **not** fall back to the first option. "The connection
died" and "the operator chose option 1" are different outcomes and the engine keeps them
different: the agent receives a typed no-answer, the `ask_user_question` tool aborts the run with
`agent.aborted.question.noAnswer.<reason>`, **no follow-up model request is issued on the
strength of it**, and a goal escalation grants no budget extension. `reason` is one of
`disconnected`, `cancelled`, `timeout`, `malformed`, `declined`, `noController`.

An empty or whitespace-only `answer` is treated as a malformed reply, not as a considered
"none" — use `session/cancelRequest` to decline explicitly.

**Declining a reverse request on the wire.** To dismiss a `request/*` without answering it,
reply with a JSON-RPC **error** — `REQUEST_CANCELLED` (`-32800`) is the canonical code, and any
error code takes the same path. That is classified as `declined`: a decision a human made.
Do **not** reply `{"answer": ""}` to mean "cancelled"; an empty answer is classified as
`malformed`, meaning the controller tried to answer and produced nothing usable. The three
no-answer reasons are all fail-closed but are deliberately different facts, and a client can
tell them apart from `event/requestResolved.outcome` and `lastTurnOutcome.error.category`:

| you send | reason | `lastTurnOutcome.error.category` |
|---|---|---|
| error reply (e.g. `-32800`) | `declined` | `agent.aborted.question.noAnswer.declined` |
| `{"answer": ""}` or no `answer` | `malformed` | `agent.aborted.question.noAnswer.malformed` |
| nothing — the pipe closes | `disconnected` | `agent.aborted.question.noAnswer.disconnected` |

For `request/permission` and `request/planApproval` an error reply is a deny / reject
respectively; the engine never inspects the error code to decide whether a refusal counts, and
never turns one into a grant.

### Reverse-request timeout (opt-in, default off)

`CODA_SERVE_REQUEST_TIMEOUT` is the number of **whole seconds** to wait for a reply.
`0`, absent, or unparseable all mean **no timeout**, which is the default: connection loss and
interruption are already covered, and a default timeout would cancel a slow human for no
verified benefit. On expiry the request resolves with the kind's fail-closed outcome tagged
`timeout`.

### Answering out of band

Every outstanding request is also visible through `session/getPendingRequests` and resolvable
through `session/resolveRequest` / `session/cancelRequest`. Both routes share **one** registry
and one exactly-once resolution:

- The request handle is `req-<engineInstanceId>-<n>`. A handle minted by a previous engine
  process is rejected (`-32010`), never re-applied to whatever happens to be pending now.
- The offered outcome is validated against the pending request's own **kind** *before* the entry
  is consumed. A wrong-kind or malformed payload is refused (`-32015` / `-32602`) and the
  request stays outstanding for you to correct.
- A duplicate or replayed resolution is refused (`-32014`). A denial can never be turned into a
  grant by sending the reply twice.
- `session/cancelRequest` applies the fail-closed default for that one request. It never guesses
  an answer and never closes the connection.
- Registration, its projection and `event/requestPending` / `event/requestResolved` are published
  as one critical section, so `session/getPendingRequests` and the pushed list can never
  disagree because two concurrent resolutions were announced out of order.
- `callId` on a pending request is **reserved and always omitted** — see
  `requests.callCorrelation` above.
- The raw `request/*` frame carries the same handle as `requestId`, so a client that owns the
  round-trip can recognise its own request in this list instead of prompting twice.

## Example turn (abbreviated)
```
→ initialize {protocolVersion:"1"}              ← {sessionId:"ab12…", protocolVersion:"1"}
→ session/prompt {text:"add a test for Foo"}
→ session/prompt {text:"what is in this image?", images:[{mediaType:"image/png", base64:"iVBOR…"}]}
   ← event/assistantText {delta:"I'll add"}
   ← event/toolCall {toolName:"edit_file", inputJson:"{…}"}
   ← request/permission {toolName:"edit_file", inputPreview:"…"}     (Coda waits)
→ (reply) {allow:true}
   ← event/toolResult {toolName:"edit_file", content:"…", isError:false}
   ← event/turnComplete {stopReason:"end_turn", interrupted:false}
← session/prompt result {ok:true, stopReason:"end_turn", interrupted:false}
→ session/history {}                              ← {messages:[{role:"user",…},{role:"assistant",…}]}
```
