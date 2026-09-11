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

## Legacy C# host

**The transport, authentication and detailed flag behavior in the Lifecycle
and MCP sections below describe the C# implementation, still available
through `publish.ps1 -Legacy`. They are not claims about the Rust engine.**

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
   toward until a judge declares it met; `--goal-max-duration <dur>` overrides the wall-clock
   budget (e.g. `30m`, `2h`, `1d`); `--goal-max-continuations <n>` overrides the turn budget;
   `--session-memory` enables the background session-memory watcher; `--max-continuations <n>`
   bounds stop-hook continuations per run (default 10).
2. Normally send `initialize` → get `{ protocolVersion: string, sessionId: string, serverInfo: string, telemetryLogPath?: string }`.
3. Send `session/prompt` to run a turn. While it runs you receive `event/*` notifications and may
   receive server-initiated `request/*` you must answer. The `session/prompt` response resolves
   when the turn completes (or is interrupted).
4. Repeat. `session/interrupt` cancels the in-flight turn. `shutdown` (or closing stdin) stops the server.

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

The reference below retains the shared legacy method vocabulary. Sections
marked **Rust** describe the additive Rust contract, not features of the
legacy C# host. Consult the Rust catalog for the complete routed inventory;
the legacy goal/startup details are not a promise of identical defaults.

| Method | Params | Result |
|---|---|---|
| `initialize` | `{ protocolVersion: string, clientInfo?: string, apiKey?: string, sessionId?: string }` | `{ protocolVersion: string, sessionId: string, serverInfo: string, telemetryLogPath?: string }` |
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
| `session/getPendingRequests` | `{}` | `{ requests: PendingRequest[], engineInstanceId }` |
| `session/resolveRequest` | `{ requestId, outcome }` | `{ ok, state, requestId, outcome }` |
| `session/cancelRequest` | `{ requestId, reason? }` | `{ ok, requestId, appliedDefault, outcome }` |
| `config/describe` | `{}` | `{ entries: ConfigEntry[] }` |
| `config/set` | `{ key, value }` | `{ ok, key, appliedAt, effective?, error? }` |
| `mcp/list` | `{}` | `{ servers: McpServer[], enabled, managerAvailable }` |

**Initialization gate.** `session/getHistory`, `session/getPendingRequests`,
`session/resolveRequest`, `session/cancelRequest` and `config/set` require a completed
`initialize` and otherwise return `-32011`. Read-only discovery — `session/listSessions`,
`config/describe`, `mcp/list`, `session/getState`, `session/getEvents` — is valid **before**
`initialize`, so a client can choose a session before resuming one. Every route that shipped
before this contract keeps exactly the behaviour it had; the gate is not retrofitted.

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
prompt to find out.

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

### `session/setGoal` (legacy C# defaults)

Mutates the session's autonomous goal settings in-place (persist-until-cleared). The goal
drives the agent to keep working turn after turn until a judge declares it met (or the budget
is exhausted). A null or empty `goal` clears the active goal.

Each call sets the **complete** goal configuration for the session (it does not merge with a
prior call). A new goal takes effect from the **next** `session/prompt`; it never disturbs a
turn already in flight (the running turn captured its options at its start).

**Params:**
- `goal` (string | null): the objective text, or null/`""` to clear the current goal.
- `maxDuration` (string, optional): wall-clock budget. Accepts suffix forms (`30m`,
  `2h`, `1d`) or `hh:mm:ss`. An explicitly-supplied but unparseable value returns a `-32602`
  error; **omitting the field reverts the budget to the configured default** (settings `goal`
  block, else 24h) rather than preserving a prior override.
- `maxContinuations` (int, optional): turn-count budget. **Omitting reverts to the configured
  default** (settings, else 60000).

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

- `outcome`: `"Met"` | `"Unmet"` (never `"None"` — that case omits the field).
- `remaining`: the judge's last "what still remains" text, or null when the goal was met.
- `continuations`: the number of forced-continue nudges issued during the run.
- `elapsedSeconds`: wall-clock seconds elapsed during the goal run.
- `escalated`: true when the budget was exhausted and an escalation question was sent.
- `extensionUsed`: true when the one bounded extension was granted.

**Resume:** pass `sessionId` to `initialize` to resume a prior conversation. On that request, serve
loads the transcript and persisted metadata before session initialization. If a transcript
exists at `<cwd>/.coda/sessions/<sessionId>.json`, its history is loaded and the session adopts
that id (subsequent turns persist back to the same file). If no transcript exists for that id,
`initialize` fails with `-32002` "session not found" rather than silently starting fresh — omit
`sessionId` to start a new session (the returned `sessionId` is then newly generated). Transcripts
are written automatically after every turn. Resumed metadata is applied before session
initialization. Although clients should send `initialize` first, the first `session/prompt` can
initialize the session if that request was omitted.

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
`event/stop {stopReason?}` · `event/usage {inputTokens, outputTokens}` ·
`event/turnComplete {stopReason?, interrupted, rootTurnId?, activityId?}`.

Assistant-text deltas arrive in order.

### Tool correlation

In **Rust**, identify tool invocations by canonical `turnId`, `batchId` and
`callId` together. Provider call IDs can repeat in different batches.
`batchId` aliases `activityId`. Preserve `rootTurnId` and `sourceId` for
legacy compatibility, but do not infer the originating agent or the whole
turn from them: Rust's legacy root IDs may be batch-scoped and source IDs
may carry provider call IDs.

The **legacy C#** identity contract for `event/toolCall`, `event/toolProgress`,
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
`stopping` and then `stopped`.

Initialization shares the exclusion slot without creating an agent turn.
It reports `initializing`, refuses concurrent initialization/work, and cannot
complete successfully after shutdown. See the catalog for repeated
initialization, failure and interruption semantics.

### Gated state events (Rust)

With `clientCapabilities.stateEvents` negotiated, a client converges on lifecycle, activity
phase, turn termination, effective config, the steering queue and session resets from the event
stream alone: `event/lifecycle`, `event/activity`, `event/turnEnded`, `event/configChanged`,
`event/steeringQueue`, `event/sessionChanged` (capability `state.turnEvents`). Tool detail and
usage totals ride on the ungated `event/toolCall` / `event/toolResult` / `event/usage` frames.
This is **not** a claim that every `StateSnapshot` field is push-based — anything else still
needs a snapshot, and `session/getEvents` + `cursor` remain the way to resynchronise.

Capability negotiation is processed before anything gated is published, so a client that resumes
a session in the same `initialize` call receives that session's own `event/sessionChanged`.

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
  "definitionId":   "a1b2c3",           // persisted schedule id
  "definitionName": "nightly backup",   // optional label, omitted when null
  "taskId":         "task-9",           // the TaskKind.Scheduled task id, omitted for a pre-launch failure
  "state":          "started",          // "started" | "completed" | "failed" | "stopped"
  "timestamp":      "2026-07-21T09:00:00Z",
  "summary":        "…"                 // optional short detail (result or error), omitted when null
}
```

`state` is the lower-case transition: `started` when a firing registers and begins,
then exactly one terminal of `completed` / `failed` / `stopped`. Optional fields are
omitted from the wire when null. The underlying `TaskKind.Scheduled` task is also
visible through the normal `task_*` tools and logs.

The runtime is authentication-gated: over **stdio** (no expected API key) it starts
at session startup, so schedule events may arrive immediately; in **API-key** mode it
does not start — and therefore emits nothing — until a valid key completes an
authenticated `initialize`.

### `event/taskCompleted`

Emitted whenever a background task reaches a terminal state (completed, failed, or
stopped). Like `event/scheduleLifecycle`, this is **not** tied to a turn — a background
subagent can finish at any time while the session is open.

```jsonc
event/taskCompleted {
  "taskId":      "task-0003",    // stable task identifier
  "status":      "completed",    // "completed" | "failed" | "stopped"
  "description": "explore docs", // human-readable task label
  "report":      "Found 42 …"   // truncated result or error text; null when not applicable
}
```

`report` is truncated to 4 000 characters (the same cap applied by the TUI injection
seam). Use `task_output` to retrieve the full log. The event is emitted for every
background completion regardless of whether `task_wait` was called; if an agent is
waiting on the task with `task_wait`, the wait call consumes the outbox entry so the
event and the `task_wait` result are not double-delivered.

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
