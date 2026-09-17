# Coda — Programmatic API

Coda's agent engine can be driven programmatically two ways:

| Interface | Process model | Audience | Best for |
|---|---|---|---|
| **`coda serve`** (JSON-RPC) | out-of-process; talk to a spawned `coda` over a transport | any language | an orchestrator driving Coda as a coding subagent |
| **`Coda.Client`** (.NET library) | out-of-process; owns a spawned `coda serve` | .NET hosts | embedding Coda in a .NET app |

Both expose the **same engine** (the agent loop, tools, providers, permission model). The wire JSON-RPC protocol is transport-agnostic and unchanged across transports.

---

## 1. `coda serve` — JSON-RPC agent server

`coda serve` runs Coda as a **JSON-RPC 2.0 server** over a duplex byte stream. The orchestrator sends requests (prompts, interrupt, history); Coda streams back **event notifications** and may send **server-initiated requests** the orchestrator must answer (permission, clarifying questions, plan approval). It is fully **bidirectional** — Coda actively reports progress and asks for input mid-turn.

Message framing is `Content-Length`-delimited JSON-RPC 2.0 (the same framing as LSP). The full wire reference, including an annotated example turn, is in **[`serve-protocol.md`](serve-protocol.md)**; the transport/auth design is in [`superpowers/specs/2026-06-02-serve-local-socket-transport-design.md`](superpowers/specs/2026-06-02-serve-local-socket-transport-design.md).

### Launching

```
coda serve [--provider id] [--model id] [--cwd path] [--permission-mode m] [--yolo]
           [--system-prompt text | --system-prompt-file path]
           [--api-key key] [--endpoint name|path]
```

| Flag | Meaning |
|---|---|
| `--provider` | `claude` (default) / `copilot` / `apikey` |
| `--model` | model id (default depends on provider) |
| `--cwd` | working directory the agent operates in |
| `--permission-mode` | `default` / `acceptedits` / `plan` / `bypass` |
| `--yolo` | shorthand for `--permission-mode bypass` |
| `--system-prompt` | exact replacement root system prompt |
| `--system-prompt-file` | read the exact replacement root system prompt from a file |
| `--api-key` | **selects the API-key-authenticated local pipe/socket transport** (see below). May also be supplied via the `CODA_SERVE_API_KEY` environment variable. |
| `--endpoint` | optional socket name/path; auto-generated if omitted |

`--system-prompt` and `--system-prompt-file` are mutually exclusive and may occur only once.
They require a separate following value: missing values, duplicates, and `--flag=value` forms fail
before session or transport side effects. Relative files resolve from the process startup directory,
not `--cwd`, are read once as strict UTF-8 (with an optional BOM removed), and preserve all remaining
whitespace, line endings, and trailing newlines. `coda run` does not accept these flags. The exact
interactive syntax is `coda [options] [--system-prompt <text> | --system-prompt-file <path>]`;
the exact serve syntax is `coda serve [serve options] [--system-prompt <text> | --system-prompt-file <path>]`.

The supplied value, including explicit empty or whitespace-only text, completely replaces the root
prompt: no built-in Coda prompt, project/`CLAUDE.md` context, output-style suffix, or provider prefix
is added. Anthropic omits explicit empty text from its optional `system` field because empty blocks
are invalid; OpenAI shapes serialize it. This wire constraint does not restore Coda defaults.

Serve starts the selected transport first; for a local pipe or Unix socket, its readiness line is
written after bind. It then loads and connects MCP servers sequentially, and accepts the JSON-RPC
connection only after MCP setup completes. On the `initialize` request, serve authenticates when
required, loads or resumes the requested transcript and metadata, then performs session
initialization; the initialize result waits for that readiness before later prompts run. MCP
diagnostics and warnings go to `stderr`; `stdout` remains protocol-only.

### Transports & authentication

The transport is chosen by whether an API key is present:

- **stdio (default, process-local).** With no `--api-key`, Coda speaks the protocol over its own
  stdin/stdout and does not require a key. Trust comes from the fact that the orchestrator spawned
  the process. `stdout` carries only protocol bytes; human/debug logs go to `stderr`.

- **Local pipe/socket (API-key authenticated).** When an API key is supplied (flag or
  `CODA_SERVE_API_KEY`), Coda listens on a **named pipe** (Windows) or **Unix domain socket**
  (other OSes) and requires the key. This is the path an orchestrator like Bridge/Cortex uses.
  - **The key selects the socket** — a socket is *never* served unauthenticated. `--endpoint` without a key, or a key that fails the strength check, exits non-zero with a message on `stderr` **before anything binds**.
  - **Endpoint discovery.** `--endpoint` is optional; if omitted Coda generates a unique one (`coda-serve-<id>`). After binding, Coda prints **one readiness line** to `stdout` so the caller knows where to connect, race-free:
    ```json
    {"transport":"pipe","endpoint":"coda-serve-1a2b3c4d5e6f","protocolVersion":"1"}
    ```
    (`"transport"` is `"pipe"` or `"unix"`; `"endpoint"` is the pipe name or socket path. The API key is never echoed.)
  - **Key strength (enforced before binding).** The key must be **≥ 64 characters, ≥ 256 bits** of charset-aware entropy, **≥ 12 distinct characters**, and not degenerate (all-same / sequential). A 64-char hex token (a 256-bit value) or a base64url token both qualify. A weak key fails fast with a specific reason.
  - **Authentication** happens in the `initialize` handshake (below): the key is compared in constant time (length-safe). A bad/missing key returns JSON-RPC error **`-32001` "unauthorized"** and the session stays locked (every subsequent request also returns `-32001`); no agent work runs.
  - **One session per process.** Run N processes (each a unique endpoint + its own key) for N parallel agents.

> **WebSocket / TLS / remote** transports are not implemented yet; the seam is designed so a remote adapter is additive (same protocol, same handshake auth).

### Protocol

**Orchestrator → Coda (requests):**

| Method | Params | Result |
|---|---|---|
| `initialize` | `{ protocolVersion: string, clientInfo?: string, apiKey?: string, sessionId?: string }` | `{ protocolVersion: string, sessionId: string, serverInfo: string, telemetryLogPath?: string }` |
| `session/prompt` | `{ text?, images?: [{ mediaType, base64 }] }` | `{ ok, stopReason?, interrupted, goalStatus? }` |
| `session/interrupt` | `{}` | `{ ok }` |
| `session/history` | `{}` | `{ messages: [{ role, content }] }` |
| `session/messages` | `{ sinceIndex }` | `{ messages, nextIndex }` |
| `session/setGoal` | `{ goal?: string\|null, maxDuration?: string, maxContinuations?: int }` | `{ ok, goal?, maxDuration?, maxContinuations? }` |
| `shutdown` | `{}` | `{ ok }` |

`initialize` normally occurs first and accepts an optional `sessionId` for resume. When the local
pipe/socket transport is used, it MUST carry `apiKey`. Only one turn runs at a time — a `session/prompt` while
busy returns a JSON-RPC error. Images are input-only, base64, ≤ 5 MB each, of type
`image/png|jpeg|gif|webp`.

#### Prompt persistence and scheduling

On a resumed session, startup prompt metadata wins over persisted metadata, which wins over the
normal generated prompt. Metadata is separate from the audited effective `systemPrompt`, and audit
records are never resume input. The root override applies equally to scheduled root turns and
context reporting; role prompts for subagents remain isolated. Session forks preserve it in
interactive, headless, and slash-command flows. Export/import carries it in the optional
`systemPromptOverride` field without changing the `coda.session/1` schema.

During `initialize`, serve applies resumed metadata before it initializes the session. On local
stdio, the first `session/prompt` can initialize a session when `initialize` is omitted.
API-key-authenticated local pipe/socket transport requires `initialize` with `apiKey` first;
prompt-first returns `-32001` and starts no agent work.

**Coda → Orchestrator (notifications, streamed during a turn):**
`event/assistantText {delta}` · `event/assistantTextComplete {}` · `event/toolCall {toolName, inputJson, rootTurnId?, activityId?, callId?, sourceId?}` · `event/toolProgress {toolName, elapsedMs, rootTurnId?, activityId?, callId?, sourceId?}` · `event/toolResult {toolName, content, isError, rootTurnId?, activityId?, callId?, sourceId?, status?}` · `event/error {message}` · `event/stop {stopReason?}` · `event/usage {inputTokens, outputTokens}` · `event/turnComplete {stopReason?, interrupted, rootTurnId?, activityId?}`.

Tool identity fields are optional and additive: `rootTurnId` identifies the root turn,
`activityId` its tool batch, `callId` a single invocation, and `sourceId` the origin
(`root:<rootTurnId>` or `subagent:<taskId>`). `event/turnComplete` carries `rootTurnId`; its
`activityId` is omitted when no tools ran. A correlated `event/toolResult` includes `status` as
the stable `ToolCallStatus` enum name (`Pending`, `AwaitingApproval`, `Running`, `Succeeded`,
`Failed`, `Cancelled`, or `Skipped`). Clients can ignore absent or unknown optional fields.
This does not change protocol version `"1"` or any JSON-RPC method name.

**Coda → Orchestrator (server-initiated requests — the agent blocks until you answer):**

| Method | Params | Reply |
|---|---|---|
| `request/permission` | `{ toolName, inputPreview }` | `{ allow: bool }` |
| `request/question` | `{ question, options[], multiSelect }` | `{ answer: string }` |
| `request/planApproval` | `{ plan }` | `{ approve: bool }` |

If you `session/interrupt` while one of these is outstanding, the awaiting callback resolves as deny/decline so the turn unwinds cleanly.

**Error codes:** `-32001` unauthorized · `-32002` session not found (resume `sessionId` has no persisted transcript) · `-32601` method not found · `-32603` internal error (also used for `busy` and bad-input rejections, with a descriptive message).

### Example (authenticated socket)

```
$ coda serve --api-key "$KEY" --cwd /repo
{"transport":"pipe","endpoint":"coda-serve-1a2b3c4d5e6f","protocolVersion":"1"}   ← stdout, after bind
# orchestrator connects to the named pipe / socket, then:
→ initialize    {protocolVersion:"1", apiKey:"<KEY>"}        ← {protocolVersion:"1", sessionId:"ab12…", serverInfo:"coda"}
→ session/prompt {text:"add a test for Foo"}
   ← event/assistantText {delta:"I'll add"}
   ← event/toolCall {toolName:"edit_file", inputJson:"{…}"}
   ← request/permission {toolName:"edit_file", inputPreview:"…"}      (Coda waits)
→ (reply) {allow:true}
   ← event/toolResult {toolName:"edit_file", content:"…", isError:false}
   ← event/turnComplete {stopReason:"end_turn", interrupted:false}
← session/prompt result {ok:true, stopReason:"end_turn", interrupted:false}
```

---

## 2. `Coda.Client` — drive the engine from .NET

`Coda.Client` is a .NET SDK for the protocol above. It launches and owns a
`coda serve` child process and speaks JSON-RPC to it, so a .NET host gets the
agent **out-of-process** — the engine is the same native `coda` binary everyone
else runs, not a second implementation compiled into your app.

```csharp
using Coda.Client;

await using var client = await CodaClient.StartAsync(
    EngineCommand.Default
        .WithWorkingDirectory(repoRoot)
        .WithProvider("github-copilot"));

await using var session = await client.CreateSessionAsync();

await foreach (var evt in session.PromptAsync("Add a retry policy to the HTTP client."))
{
    switch (evt)
    {
        case AssistantDelta d: Console.Write(d.Text); break;
        case ToolCall t:       Console.WriteLine($"[{t.Name}]"); break;
    }
}
```

Permission requests, clarifying questions and plan approvals arrive as
server-initiated requests that the host answers; see
[`serve-protocol.md`](serve-protocol.md) for the full set and their payloads.

> **In-process embedding is no longer offered.** The previous `Coda.Sdk` compiled
> a second, C# implementation of the agent into the host process. It was retired
> with the rest of the C# engine — there is now one engine, in Rust, and one way
> to embed it. The retired code is reachable at the `csharp-final` tag.

---

## Appendix — agent capabilities (built-in tools)

The model can call these built-in tools (subject to the permission mode). MCP servers, LSP, subagents, and tool-search add more at runtime.

| Tool | Read-only? | Purpose |
|---|---|---|
| `read_file`, `list_dir`, `glob`, `grep` | yes (auto-run) | inspect files/tree, find by glob, search content |
| `edit_file`, `write_file` | no (gated) | modify / create files (sandboxed to the working dir) |
| `run_command` | no (gated) | run a shell command (optionally `run_in_background`, then poll with `task_output`) |
| `web_fetch`, `web_search` | yes | fetch a URL as text; DuckDuckGo search |
| `todo_write` | yes | maintain a live session checklist |
| `ask_user_question`, `exit_plan_mode` | — | ask the host a question; submit a plan for approval |
| `schedule_create`, `schedule_list`, `schedule_delete` | mixed | create/list/delete scheduled tasks (`every`/`at`/`cron`, optionally bounded by `maxRuns` and `expiresAt`/`expiresIn`); each firing runs as a `TaskKind.Scheduled` background agent while the session is open. Main-agent only, except that a scheduled run sees its own definition in `schedule_list`. |
| `schedule_cancel_self` | no | a scheduled run retires **its own** schedule once its job is done. Takes no schedule id or task id. Only the scheduled root run may call it; `stopRunning: true` also ends the current run, otherwise the run finishes normally. |
| `task_start`, `task_output`, `task_stop`, `sleep` | mixed | long-running background jobs + polling |
| `task_list`, `task_get`, `task_peek`, `task_send` | yes | list all tasks, read one task, peek recent output, steer a running agent task (subagent or scheduled) |
| `task_wait`, `task_background`, `task_remove` | yes | wait for a task to finish (optional `timeout_seconds`, default 600, max 1800; timeout leaves it running), move a running foreground shell to the background, remove a finished task (log preserved) |
| `notebook_edit` | no (gated) | edit Jupyter notebook cells |
| `git_worktree` | no (gated) | list/add/remove git worktrees |
| `task` | — | delegate a self-contained subtask to a subagent |
| `lsp` | yes | code intelligence (when language servers are configured) |
| `mcp__<server>__<tool>` | varies | tools advertised by configured MCP servers |
| `list_mcp_resources`, `read_mcp_resource`, `list_mcp_prompts`, `get_mcp_prompt` | yes | browse and read resources/prompts exposed by connected MCP servers |
| `restart_mcp_server` | yes (auto-run) | stop and restart one configured, **enabled** MCP server to recover it when it hangs or stops responding; refuses unknown, disabled, or never-started servers, and relaunches only the configuration this session already connected with |
