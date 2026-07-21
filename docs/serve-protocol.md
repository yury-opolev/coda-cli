# `coda serve` — Orchestrator Protocol

`coda serve` runs Coda as a **JSON-RPC 2.0 agent server over stdio**, so an external
orchestrator can drive it as a coding subagent: send prompts, stream back results, answer
permission/question/plan-approval requests, interrupt turns, and read history.

```
orchestrator ──spawn──► coda serve
        │  stdin  : requests + replies to server-requests
        └─ stdout : event notifications + server-initiated requests
```

- **Transport:** newline/Content-Length-framed JSON-RPC 2.0 over the child process's stdin/stdout
  (same framing as LSP). **stdout carries only protocol bytes**; human/debug logs go to stderr.
- **Trust:** local-spawn only (no auth). Networked transports are a future additive layer.
- **Versioning:** call `initialize` first; current `protocolVersion` is `"1"`.

## Lifecycle
1. Spawn `coda serve` (optionally `--provider`, `--model`, `--cwd`, `--permission-mode`, `--yolo`,
   `--yolo-safe`). Permission modes: `default` (mutating tools raise `request/permission`),
   `accept-edits` (auto-accept edits), `plan` (drives `request/planApproval`), `yolo` /
   `bypass` (no prompts), and `yolo-safe` (bypass with a safety classifier that escalates risky
   actions via `request/permission`).
   Autonomous flags (all off by default): `--goal "<objective>"` sets a goal the agent works
   toward until a judge declares it met; `--goal-max-duration <dur>` overrides the wall-clock
   budget (e.g. `30m`, `2h`, `1d`); `--goal-max-continuations <n>` overrides the turn budget;
   `--session-memory` enables the background session-memory watcher; `--max-continuations <n>`
   bounds stop-hook continuations per run (default 10).
2. Send `initialize` → get `{ protocolVersion, sessionId, serverInfo }`.
3. Send `session/prompt` to run a turn. While it runs you receive `event/*` notifications and may
   receive server-initiated `request/*` you must answer. The `session/prompt` response resolves
   when the turn completes (or is interrupted).
4. Repeat. `session/interrupt` cancels the in-flight turn. `shutdown` (or closing stdin) stops the server.

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
  servers once via the TUI or `coda run`.
- **stdout stays pure.** All MCP connect/skip diagnostics go to **stderr**; no MCP output is ever
  written to stdout (the JSON-RPC protocol channel).
- **Startup timing.** Servers connect sequentially before the JSON-RPC connection is accepted, each
  with a 20s connect cap. Many slow/hanging servers can therefore delay the handshake (bounded by
  server count × 20s, and by shutdown/cancellation). Keep the curated set small, or use `--no-mcp`
  when a session needs no external tools.

## Orchestrator → Coda (requests)
| Method | Params | Result |
|---|---|---|
| `initialize` | `{ protocolVersion, clientInfo?, apiKey?, sessionId? }` | `{ protocolVersion, sessionId, serverInfo }` |
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

### `session/setGoal`

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

**Resume:** pass `sessionId` to `initialize` to resume a prior conversation. If a transcript
exists at `<cwd>/.coda/sessions/<sessionId>.json`, its history is loaded and the session adopts
that id (subsequent turns persist back to the same file). If no transcript exists for that id,
`initialize` fails with `-32002` "session not found" rather than silently starting fresh — omit
`sessionId` to start a new session (the returned `sessionId` is then newly generated). Transcripts
are written automatically after every turn.

Only one turn runs at a time — a `session/prompt` while busy returns a JSON-RPC error.

**Image input:** `images` is an optional array of base64-encoded images attached to the turn.
Supported media types: `image/png`, `image/jpeg`, `image/gif`, `image/webp`. Maximum 5 MB per image
(decoded). Images are input-only — providers do not return image content. A prompt with any invalid
image (unsupported type, non-base64 data, or oversized) is rejected with a JSON-RPC error before
any turn runs; the session remains idle and accepts the next prompt normally.

## Coda → Orchestrator (notifications — streamed during a turn)
`event/assistantText {delta}` · `event/assistantTextComplete {}` · `event/toolCall {toolName,
inputJson}` · `event/toolResult {toolName, content, isError}` · `event/error {message}` ·
`event/stop {stopReason?}` · `event/usage {inputTokens, outputTokens}` ·
`event/turnComplete {stopReason?, interrupted}`.

Assistant-text deltas arrive in order.

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

## Coda → Orchestrator (server-initiated requests — you MUST answer; the agent blocks)
| Method | Params | Reply |
|---|---|---|
| `request/permission` | `{ toolName, inputPreview }` | `{ allow: bool }` |
| `request/question` | `{ question, options[], multiSelect }` | `{ answer: string }` |
| `request/planApproval` | `{ plan }` | `{ approve: bool }` |

These are Coda's interactive host callbacks routed to the wire. If you interrupt a turn while one
is outstanding, the awaiting callback resolves as deny/decline so the turn unwinds cleanly.

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
