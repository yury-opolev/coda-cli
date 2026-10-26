# MCP restart always restarts — design

Source: "REQUEST CODA bugreport 4" (restart_mcp_server permanently loses a
server after a forced termination). Base: `main` @ d080673.

## Root causes (confirmed in code)

1. `McpClientManager::restart_server` (`coda-mcp/src/manager.rs`) removes the
   entry from the map, then `?`-returns on any shutdown error. The spec lived
   only in that entry, so it is dropped and every later restart answers
   "is not connected". The reconnect-failure branch drops it the same way.
2. `McpProcess::shutdown` (`process.rs`) reports a forced kill as
   `Err(TimedOut, "MCP server required forced termination")`, and
   `ServerEntry::shutdown` reports a non-zero exit as an error. For a restart
   both mean "the old process is gone" — success.
3. Only the direct child is killed (`kill_on_drop` / `start_kill`). Wrapper
   launchers (`agency`, `npx`, `cmd /c`, `uvx`) leave the real server running
   as a grandchild.
4. A server that failed at startup is never in the map, so it has no restart
   path although it was configured and approved at startup.
5. The agent tool registry is a static snapshot built at engine start
   (`coda-serve/src/host.rs`), so a server that failed at startup would have no
   callable `mcp__<server>__*` tools even after a successful restart, and a
   changed tool list after a restart is never reflected.
6. Test `restart_waits_for_old_process_cleanup_before_reconnecting` asserts the
   restart *fails*, locking the bug in.

## Approaches considered

- **A. Patch `restart_server` only** (keep spec on error, ignore forced-kill
  errors). Fixes symptom 3 of the report but leaves grandchildren alive,
  startup failures unrestartable and the registry static. Rejected.
- **B. Slot-based manager + process tree ownership + dynamic tool source
  (chosen).** The manager owns one slot per configured server (spec + optional
  live connection + last error + last-known tools). Processes are owned as a
  tree (Job Object / process group). The registry consults a live MCP tool
  source. Covers every acceptance criterion with contained changes.
- **C. Supervisor task per server with automatic restarts.** Most robust long
  term but a much larger lifecycle change and surprising behaviour (silent
  relaunches). Out of scope; auto-restart is a follow-up.

## Design

### Manager state (coda-mcp `manager.rs`)

`servers: RwLock<HashMap<String, ServerSlot>>` where

```text
ServerSlot { spec: ServerSpec, live: Option<ServerEntry>, last_error: Option<String>,
             tools: Vec<McpToolInfo> /* last known */, generation: u64 }
```

- `connect_many` / `connect_http_many` record a slot for every server they
  were asked to connect — connected or failed (failed: `live: None`,
  `last_error: Some(..)`). A server that failed at startup is therefore known
  and restartable. The spec is never dropped afterwards.
- Per-server restart serialization: a `std::sync::Mutex<HashMap<String,
  Arc<tokio::sync::Mutex<()>>>>` so two concurrent restarts of one server do
  not race; different servers restart independently.

### Restart semantics — "make this server fresh, whatever state it is in"

1. Unknown name → `Err("unknown MCP server '<name>'; configured servers: a, b")`
   (or "no MCP servers are configured").
2. Take the live connection out of the slot (slot and spec stay).
3. If there was a live connection: close stdin (client shutdown), wait the
   3 s grace, then force-kill the whole process tree; afterwards kill any
   leftovers of the tree even if the direct child exited by itself. The outcome
   is a report, never an abort: graceful exit, crash / non-zero exit, and
   "exited after forced kill" are all success (warn log for the latter two).
   "Termination could not be confirmed" is logged and noted in the result text;
   the restart still proceeds.
4. Relaunch from the retained spec. Success → slot gets the new connection,
   new tool list, `last_error = None`, `generation += 1`; return a
   `RestartReport`. Failure → slot keeps the spec, `last_error = Some(e)`,
   error text says the server stays configured and can be restarted again once
   the cause is fixed.

Result text (management tool), e.g.:
`Restarted MCP server 'teams' (old process force-killed after 3s; 2 leftover process(es) terminated). 52 tool(s) available.`
Variants: "old process exited cleanly", "old process had already exited
(exit code 1)", "server was not running", "termination of the old process
could not be confirmed".

**Strict mode: not added.** There is no configuration surface for it in
coda-mcp, the owner's stated position is that restart is lifecycle management
without ceremony, and the Job Object / process group guarantee leftovers die
with the harness anyway. Documented as a possible follow-up.

### Process tree ownership (coda-mcp `process.rs`)

- **Windows:** right after spawn, create a Job Object with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and assign the child to it
  (`windows-sys` 0.61, already in `Cargo.lock` via tokio; only features are
  added). Force kill = query `ActiveProcesses` (reported count) then
  `TerminateJobObject`. The job handle is closed on drop, so dropping the
  process (or the harness exiting) kills the tree. Race: a grandchild started
  between `spawn` and `AssignProcessToJobObject` escapes the job — tokio's
  `Command` cannot spawn suspended. The window is microseconds while a wrapper
  needs far longer to start its child; accepted and documented. If assignment
  fails (e.g. a restrictive parent job), fall back to
  `taskkill /T /F /PID <pid>`.
- **Unix:** spawn with `process_group(0)`; force kill = `killpg(SIGTERM)`,
  short wait, then `killpg(SIGKILL)`; leftovers after a graceful exit get
  `killpg(SIGKILL)`; drop sends `killpg(SIGKILL)` unless the tree was already
  reaped. `libc` is added as a unix-only dependency (already in the lock).
- New `McpProcess::terminate(deadline) -> TerminationReport`
  `{ exit: Graceful | Exited(status) | ForceKilled | Unconfirmed, leftovers: Option<usize> }`.
  The existing `shutdown(deadline) -> io::Result<()>` stays as a thin mapping
  used by the harness-exit path, so `McpClientManager::shutdown` behaviour is
  unchanged.

### Transport loss → truthful status

When `call_tool` gets `McpTransportError::ConnectionClosed`, or an RPC error
whose message contains "Session not found", the manager marks the slot
disconnected (only if its `generation` still matches, so a concurrent restart
is never undone), records `last_error`, and terminates the old connection in
the background. The tool error tells the model: "MCP server 'x' lost its
connection (…). It is now marked disconnected; call restart_mcp_server with
server='x' to relaunch it, then retry."

**Automatic restart + retry: not implemented** (follow-up). It needs a user
setting and a policy for non-idempotent tool calls (a retried `tools/call`
might execute twice); the explicit, model-driven restart above already
recovers in one step.

Calls to a known-but-disconnected server (`call_tool`, `get_prompt`,
`read_resource`) answer: "MCP server 'x' is not connected (last error: …).
Call restart_mcp_server with server='x' to relaunch it." Unknown names get
the "unknown MCP server" text.

### Live tool registry

- `coda-tool`: new `ToolSource` trait (`fn tools(&self) -> Vec<Arc<dyn Tool>>`).
- `coda-agent` `ToolRegistry`: optional dynamic sources
  (`with_source`); `resolve` checks static tools then sources; `all()` returns
  an owned `Vec` (static tools + source tools not shadowed by a static name);
  `definitions()` and `read_only()` build on it; new `filtered(pred)` keeps
  sources (filtered lazily) so subagent registries stay live.
- `coda-mcp`: the manager keeps a synchronous snapshot of `(server, tool)` for
  every slot (last-known tools, so a disconnected server's tools still route to
  the actionable "restart it" error instead of "unknown tool").
  `McpClientManager::tool_source(&Arc<Self>)` returns the source.
- `coda-serve`: the registry is built with the MCP source; the bundle carries
  only the management tools. Tools of a server that came up via restart, and
  tool-list changes after a restart, are visible from the next model request.

### Status display

- `McpClientManager::server_faults()` returns `{ name, last_error }` for known
  servers without a live connection; `connected_status()` is unchanged.
- `coda-proto` `McpServerDto` gains optional `lastError` (omitted when
  absent). `coda-serve` `mcp_list::build` fills it, sanitised at the wire
  boundary: URLs reduced via `sanitise_url`, control characters/newlines
  collapsed, capped at 200 chars.
- `coda-tui` engine MCP browser: runtime reads
  `disconnected (last error: …)` and the detail adds
  `restart    ask the agent to run restart_mcp_server for '<name>'`. A
  dedicated `mcp/restart` RPC and TUI key are a follow-up (the engine has no
  MCP write API today).

## Testing

Real-process tests reuse the test executable itself as a fake MCP server: an
`#[ignore]` test entry point that, only when `CODA_MCP_FAKE_SERVER` is set,
speaks newline-delimited JSON-RPC (initialize / tools/list / tools/call) on
stdio. Modes: `serve`, `hang` (ignores stdin EOF), `crash` (exits 3 on EOF),
`grandchild` (spawns a second copy in `sleep` mode, writes its PID to a file,
then serves), `tools2` (advertises a different tool list). libtest's banner on
stdout is skipped by the transport's non-JSON tolerance.

Acceptance tests (coda-mcp):

- hang → `restart_server` Ok; report says force-killed; new process serves.
- grandchild via wrapper → after restart the old grandchild PID is gone
  (Windows: `OpenProcess`/`GetExitCodeProcess`; Unix: `kill(pid, 0)`).
- already crashed (non-zero exit) → Ok.
- reconnect fails (command missing) → Err; slot retained; after the cause is
  fixed a second restart succeeds.
- restart twice in a row → both Ok.
- failed at startup → restart starts it.
- unknown name → distinct error listing configured names.
- `restart_waits_for_old_process_cleanup_before_reconnecting` rewritten to
  assert ordering with a successful restart.
- transport loss marks the server disconnected; status reports the error.
- tool source reflects a restart's new tool list.

Plus focused tests in `coda-agent` (registry sources), `coda-serve`
(`mcp_list` last error + sanitisation, engine resolves MCP tools through the
source) and `coda-tui` (browser shows the disconnected line).
