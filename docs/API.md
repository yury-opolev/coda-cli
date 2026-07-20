# Coda — Programmatic API

Coda's agent engine can be driven programmatically two ways:

| Interface | Process model | Audience | Best for |
|---|---|---|---|
| **`coda serve`** (JSON-RPC) | out-of-process; talk to a spawned `coda` over a transport | any language | an orchestrator driving Coda as a coding subagent |
| **`Coda.Sdk`** (.NET library) | in-process; reference the assemblies | .NET hosts | embedding the engine directly |

Both expose the **same engine** (the agent loop, tools, providers, permission model). The wire JSON-RPC protocol is transport-agnostic and unchanged across transports.

---

## 1. `coda serve` — JSON-RPC agent server

`coda serve` runs Coda as a **JSON-RPC 2.0 server** over a duplex byte stream. The orchestrator sends requests (prompts, interrupt, history); Coda streams back **event notifications** and may send **server-initiated requests** the orchestrator must answer (permission, clarifying questions, plan approval). It is fully **bidirectional** — Coda actively reports progress and asks for input mid-turn.

Message framing is `Content-Length`-delimited JSON-RPC 2.0 (the same framing as LSP). The full wire reference, including an annotated example turn, is in **[`serve-protocol.md`](serve-protocol.md)**; the transport/auth design is in [`superpowers/specs/2026-06-02-serve-local-socket-transport-design.md`](superpowers/specs/2026-06-02-serve-local-socket-transport-design.md).

### Launching

```
coda serve [--provider id] [--model id] [--cwd path] [--permission-mode m] [--yolo]
           [--api-key key] [--endpoint name|path]
```

| Flag | Meaning |
|---|---|
| `--provider` | `claude` (default) / `copilot` / `apikey` |
| `--model` | model id (default depends on provider) |
| `--cwd` | working directory the agent operates in |
| `--permission-mode` | `default` / `acceptedits` / `plan` / `bypass` |
| `--yolo` | shorthand for `--permission-mode bypass` |
| `--api-key` | **selects the authenticated local-socket transport** (see below). May also be supplied via the `CODA_SERVE_API_KEY` environment variable. |
| `--endpoint` | optional socket name/path; auto-generated if omitted |

### Transports & authentication

The transport is chosen by whether an API key is present:

- **stdio (default, unauthenticated).** With no `--api-key`, Coda speaks the protocol over its own stdin/stdout. Trust comes from the fact that the orchestrator spawned the process. `stdout` carries only protocol bytes; human/debug logs go to `stderr`.

- **Local socket (authenticated).** When an API key is supplied (flag or `CODA_SERVE_API_KEY`), Coda listens on a **named pipe** (Windows) or **Unix domain socket** (other OSes) and requires the key. This is the path an orchestrator like Bridge/Cortex uses.
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
| `initialize` | `{ protocolVersion, clientInfo?, apiKey? }` | `{ protocolVersion, sessionId, serverInfo }` |
| `session/prompt` | `{ text?, images?: [{ mediaType, base64 }] }` | `{ ok, stopReason?, interrupted, goalStatus? }` |
| `session/interrupt` | `{}` | `{ ok }` |
| `session/history` | `{}` | `{ messages: [{ role, content }] }` |
| `session/messages` | `{ sinceIndex }` | `{ messages, nextIndex }` |
| `session/setGoal` | `{ goal?: string\|null, maxDuration?: string, maxContinuations?: int }` | `{ ok, goal?, maxDuration?, maxContinuations? }` |
| `shutdown` | `{}` | `{ ok }` |

`initialize` must be first. When the socket transport is used, it MUST carry `apiKey`. Only one turn runs at a time — a `session/prompt` while busy returns a JSON-RPC error. Images are input-only, base64, ≤ 5 MB each, of type `image/png|jpeg|gif|webp`.

**Coda → Orchestrator (notifications, streamed during a turn):**
`event/assistantText {delta}` · `event/assistantTextComplete {}` · `event/toolCall {toolName, inputJson}` · `event/toolResult {toolName, content, isError}` · `event/error {message}` · `event/stop {stopReason?}` · `event/usage {inputTokens, outputTokens}` · `event/turnComplete {stopReason?, interrupted}`.

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

## 2. `Coda.Sdk` — embed the engine in-process (.NET 10)

Reference `Coda.Sdk` (which pulls in `Coda.Agent`, `Coda.Mcp`, `LlmClient`, `LlmAuth*`). The entry point is **`CodaSession`** — the callable engine that wires the provider client, tools, subagents, LSP, and permission policy, and keeps the conversation across calls.

### `CodaSession`

```csharp
public sealed class CodaSession : IDisposable
{
    public CodaSession(
        CredentialManager credentials,
        SessionOptions options,
        ClientFingerprint? fingerprint = null,
        HttpClient? httpClient = null,
        List<ChatMessage>? history = null,
        string? sessionId = null);

    public string SessionId { get; }
    public SessionOptions Options { get; set; }          // provider/model/mode may change between runs
    public IReadOnlyList<ChatMessage> History { get; }
    public TokenUsage SessionUsage { get; }              // cumulative across runs
    public TodoStore Todos { get; }
    public ScheduledTaskStore Schedules { get; }
    public BackgroundTaskRunner BackgroundTasks { get; }

    public Task InitializeAsync(CancellationToken ct = default);            // starts configured LSP servers (no-op if none)
    public Task<RunResult> RunAsync(string prompt, IAgentSink? sink = null, CancellationToken ct = default);
    public Task<RunResult> RunAsync(IReadOnlyList<ContentBlock> userContent, IAgentSink? sink = null, CancellationToken ct = default); // text + images
    public Task CompactAsync(CancellationToken ct = default);              // summarize history in place
    public void Reset();                                                   // clear the conversation
    public void Dispose();                                                 // tears down LSP, owned HttpClient
}
```

`RunAsync` runs **one user turn**: it streams the assistant reply (with tool use) to the optional `sink`, keeps the conversation in `History`, and returns a `RunResult`. On failure the turn is rolled back so history never corrupts. Call `InitializeAsync` once after construction if you configured LSP servers.

### `SessionOptions`

```csharp
public sealed record SessionOptions
{
    public required string ProviderId { get; init; }     // e.g. ClaudeAiProvider.Id, ApiKeyProvider.Id, GitHubCopilotProvider.Id
    public required string Model { get; init; }          // e.g. "claude-sonnet-4-6"
    public required string WorkingDirectory { get; init; }

    public PermissionMode PermissionMode { get; init; } = PermissionMode.Default;
    public IReadOnlyList<ITool> ExtraTools { get; init; } = [];   // e.g. MCP tools, on top of the built-ins
    public IPermissionPrompt? InteractivePrompt { get; init; }    // null = headless (an "Ask" decision denies)
    public IUserQuestionPrompt? UserQuestionPrompt { get; init; } // null = headless
    public IPlanApprover? PlanApprover { get; init; }             // null = headless

    public int MaxIterations { get; init; } = 20;
    public bool EnableSessionMemory { get; init; }               // background notes file after work-bearing turns
    public bool EnableBypassClassifier { get; init; }            // vet each mutating action in bypass mode
    public string? Goal { get; init; }                           // autonomous goal: keep working until a judge agrees
    public TimeSpan? GoalMaxDuration { get; init; }             // wall-clock budget override (null → settings/default 24h)
    public int? GoalMaxContinuations { get; init; }             // turn budget override (null → settings/default 60000)
    public int MaxStopContinuations { get; init; } = 10;
    public int AutoCompactTokenThreshold { get; init; } = 100_000; // 0 disables auto-compaction
    public string? OutputStyle { get; init; }                    // persona, e.g. "concise"
}
```

### `RunResult`

```csharp
public sealed record RunResult(
    bool Success,
    string FinalText,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    string? StopReason,
    string? Error)
{
    public TokenUsage Usage { get; init; }         // Zero when the provider didn't report usage
    public GoalStatus? Goal { get; init; }         // Non-null when a goal was active; Outcome != None when goal ran
}

public sealed record ToolCallRecord(string Name, string Input, string? Result, bool IsError);
```

### Streaming: `IAgentSink`

Pass an `IAgentSink` to `RunAsync` to observe the turn live (or `null` to just await the `RunResult`).

```csharp
public interface IAgentSink
{
    void OnAssistantText(string delta);
    void OnAssistantTextComplete();
    void OnToolCall(string toolName, string inputJson);
    void OnToolResult(string toolName, ToolResult result);
    void OnError(string message);
    void OnStopReason(string? stopReason) { }   // optional (default no-op)
    void OnUsage(TokenUsage usage) { }           // optional (default no-op)
}
```

Provided implementations in `Coda.Sdk`:
- `PlainTextSink(TextWriter output, TextWriter error)` — human-readable headless output.
- `JsonStreamSink(TextWriter writer)` — newline-delimited JSON events; call `EmitResult(RunResult)` once at the end.
- `RecordingSink(IAgentSink? inner = null)` — captures final text, tool calls, stop reason, and usage (and forwards to `inner`).

### Minimal example

```csharp
using Coda.Agent;            // PermissionMode
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Storage.Windows;

using var claude = new ClaudeAiProvider();
var credentials = new CredentialManager(new DpapiTokenStore(), [claude, new ApiKeyProvider()]);
// Sign in once (opens the browser + loopback listener); see the README "Usage" section:
// await credentials.LoginAsync(ClaudeAiProvider.Id);

var options = new SessionOptions
{
    ProviderId = ClaudeAiProvider.Id,
    Model = "claude-sonnet-4-6",
    WorkingDirectory = Directory.GetCurrentDirectory(),
    PermissionMode = PermissionMode.AcceptEdits,   // or Default + an InteractivePrompt to approve interactively
};

using var session = new CodaSession(credentials, options);
await session.InitializeAsync();                   // starts LSP servers if configured

RunResult result = await session.RunAsync(
    "Add a unit test for Foo.Bar and run the suite.",
    new PlainTextSink(Console.Out, Console.Error));

Console.WriteLine(result.Success ? $"\n✓ {result.StopReason}" : $"\n✗ {result.Error}");
```

For headless automation, leave `InteractivePrompt`/`UserQuestionPrompt`/`PlanApprover` null and use `PermissionMode.AcceptEdits` or `BypassPermissions` (optionally with `EnableBypassClassifier = true`).

---

## Appendix — agent capabilities (built-in tools)

The model can call these built-in tools (subject to the permission mode). MCP servers, LSP, subagents, and tool-search add more at runtime.

| Tool | Read-only? | Purpose |
|---|---|---|
| `read_file`, `list_dir`, `glob`, `grep` | yes (auto-run) | inspect files/tree, find by glob, search content |
| `edit_file`, `write_file` | no (gated) | modify / create files (sandboxed to the working dir) |
| `run_command` | no (gated) | run a shell command |
| `web_fetch`, `web_search` | yes | fetch a URL as text; DuckDuckGo search |
| `todo_write` | yes | maintain a live session checklist |
| `ask_user_question`, `exit_plan_mode` | — | ask the host a question; submit a plan for approval |
| `schedule_create`, `schedule_list`, `schedule_delete` | mixed | cron-style scheduled tasks |
| `background_task_start`, `_output`, `_stop`, `sleep` | mixed | long-running background jobs + polling |
| `notebook_edit` | no (gated) | edit Jupyter notebook cells |
| `git_worktree` | no (gated) | list/add/remove git worktrees |
| `task` | — | delegate a self-contained subtask to a subagent |
| `lsp` | yes | code intelligence (when language servers are configured) |
| `mcp__<server>__<tool>` | varies | tools advertised by configured MCP servers |
