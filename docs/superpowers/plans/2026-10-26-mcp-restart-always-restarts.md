# MCP restart always restarts — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `restart_mcp_server <name>` always makes the server fresh — whatever state it is in — and never loses the server's spec.

**Architecture:** see `docs/superpowers/specs/2026-10-26-mcp-restart-always-restarts-design.md`. Process trees are owned (Windows Job Object / Unix process group); the manager keeps one slot per configured server (spec + optional live connection + last error + last-known tools); the agent registry consults a live MCP tool source; status surfaces `lastError`.

**Tech Stack:** Rust workspace in `rust/`, tokio, `windows-sys` 0.61 (already in Cargo.lock), `libc` 0.2 (unix, already in Cargo.lock).

**Conventions:** run commands from `rust/`. `main` is not fmt-clean — never run workspace `cargo fmt`; format only the lines you write. Zero new warnings (`cargo build -p <crate> --tests` must not add warnings in touched files). Conventional commits ending with the trailer `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`. TDD: write the test, see it fail, implement, see it pass.

---

### Task 1: Process tree ownership + termination report (`coda-mcp/src/process.rs`)

**Files:**
- Modify: `rust/crates/coda-mcp/Cargo.toml` (add `[target.'cfg(windows)'.dependencies] windows-sys = { version = "0.61", features = ["Win32_Foundation", "Win32_System_JobObjects", "Win32_System_Threading", "Win32_Security"] }` and `[target.'cfg(unix)'.dependencies] libc = "0.2"`)
- Modify: `rust/crates/coda-mcp/src/process.rs`
- Create: `rust/crates/coda-mcp/src/test_fixture.rs` (`#[cfg(test)] mod test_fixture;` in `lib.rs`)

**API to add (process.rs):**

```rust
/// How the old process ended.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum ProcessExit {
    /// Exited with success within the grace period.
    Graceful,
    /// Exited on its own with a failure status (crash / non-zero exit), or had already exited.
    Exited(String),
    /// Did not exit within the grace period; the tree was force-killed and the exit confirmed.
    ForceKilled,
    /// Force-kill was attempted but the exit could not be confirmed.
    Unconfirmed(String),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct TerminationReport {
    pub exit: ProcessExit,
    pub grace: std::time::Duration,
    /// Processes of the tree that were still alive and got killed (besides the
    /// direct child when it was force-killed). `None` when the platform cannot count.
    pub leftovers_killed: Option<usize>,
}

impl McpProcess {
    /// Close-stdin has already happened (the client owns stdin). Waits until
    /// `deadline` for the direct child, then force-kills the whole tree; always
    /// kills any leftover tree members afterwards. Never returns an error.
    pub async fn terminate(self, deadline: tokio::time::Instant) -> TerminationReport;
    /// Existing harness-exit API, now a mapping of `terminate`:
    /// Graceful => Ok(()), Exited(s) => Err(other("MCP server exited with {s}")),
    /// ForceKilled => Err(TimedOut, "MCP server required forced termination"),
    /// Unconfirmed => Err(TimedOut, "MCP server termination could not be confirmed").
    pub async fn shutdown(self, deadline: tokio::time::Instant) -> std::io::Result<()>;
}
```

Implementation notes:
- Introduce a private `ProcessTree` type owned by `McpProcess` (field `tree: ProcessTree`), created right after `builder.spawn()`:
  - Windows: `CreateJobObjectW(null, null)`; `SetInformationJobObject(job, JobObjectExtendedLimitInformation, &JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, .. })`; `AssignProcessToJobObject(job, child.raw_handle())`. On any failure log `tracing::warn!` and keep `job = None` + the pid for a `taskkill /T /F /PID <pid>` fallback. `kill_tree()` = query `JobObjectBasicAccountingInformation` (`JOBOBJECT_BASIC_ACCOUNTING_INFORMATION.ActiveProcesses`) then `TerminateJobObject(job, 1)`; returns `Some(active)`. `Drop` closes the job handle (kill-on-close kills the tree). Document the spawn→assign race in a comment (tokio `Command` cannot create suspended).
  - Unix: `builder.process_group(0)` before spawn; pgid = child pid. `kill_tree_gracefully()` = `libc::killpg(pgid, SIGTERM)`; `kill_tree()` = `libc::killpg(pgid, SIGKILL)` (ESRCH ignored) returning `None` (cannot count). `Drop` sends SIGKILL to the group unless `terminate` already ran.
- `terminate`: wait for the child until `deadline`. Exit success → `Graceful`; failure status → `Exited(status.to_string())`; wait error → `Exited(error)`. Timeout → (unix: SIGTERM group, wait ≤ 250 ms) → `kill_tree()` + `child.start_kill()`, wait until `deadline + 500ms` → `ForceKilled` or `Unconfirmed`. In every branch finish with a leftover sweep: `kill_tree()`; `leftovers_killed` = count reported by the sweep minus the direct child if it was still counted (Windows: `ActiveProcesses` at the sweep; if the child was force-killed in the same call, report the active count taken before the kill minus 1). Keep it simple and documented — exact counting is informational.
- `kill_on_drop(true)` stays.

**Test fixture (`test_fixture.rs`)** — the test executable doubles as a fake MCP server:

```rust
pub(crate) const MODE_ENV: &str = "CODA_MCP_FAKE_SERVER";
pub(crate) const PID_FILE_ENV: &str = "CODA_MCP_FAKE_PID_FILE";
/// Program + args that start this test binary as a fake server in `mode`.
pub(crate) fn fake_server_command(mode: &str) -> (String, Vec<String>, HashMap<String,String>);
pub(crate) fn fake_server_connectable(name: &str, mode: &str, extra_env: &[(&str, &str)]) -> McpConnectable;
pub(crate) fn is_process_alive(pid: u32) -> bool; // Windows: OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)+GetExitCodeProcess==STILL_ACTIVE(259); Unix: kill(pid,0)==0
pub(crate) async fn wait_until(timeout: Duration, f: impl Fn() -> bool) -> bool;

#[test]
#[ignore = "entry point for the fake MCP server child process"]
fn fake_mcp_server_entry() { if let Ok(mode) = std::env::var(MODE_ENV) { run(&mode); std::process::exit(0) } }
```

Args: `[current_exe, "test_fixture::fake_mcp_server_entry", "--exact", "--ignored", "--nocapture", "--test-threads=1", "-q"]` (the full test path is `test_fixture::fake_mcp_server_entry` because the module is at crate root). Env: `MODE_ENV=mode`. libtest prints a banner on stdout; the MCP transport skips non-JSON lines.

Modes (blocking std I/O, newline-delimited JSON-RPC; answer `initialize` with `{"protocolVersion":"2025-06-18","serverInfo":{"name":"fake","version":"1"},"capabilities":{}}`, `tools/list` with the mode's tools, `tools/call` with text `"pid:<pid>"`, any other request with `{}`):
- `serve`: tools `[{"name":"echo","description":"","inputSchema":{"type":"object"}}]`; exit 0 on EOF.
- `tools2`: tools `echo` and `extra`; exit 0 on EOF.
- `hang`: like `serve`, but on EOF sleep 60 s (ignores EOF).
- `crash`: like `serve`, but on EOF exit 3.
- `grandchild`: spawn a copy of this binary in mode `sleep` (stdin/stdout/stderr null), write its pid to `PID_FILE_ENV`, then behave like `serve`.
- `sleep`: sleep 60 s and exit (bounded so a failed test never leaks forever).
- `exit-now`: exit 3 immediately (for "already crashed").
Only `grandchild` writes a pid file; tests get the direct pid from `McpProcess::child.id()` or from the `pid:<n>` text a `tools/call` returns.

**Tests (process.rs `tests`):**
- keep `stdin_eof_allows_a_clean_process_exit` and `a_hung_process_is_reaped_and_reported_within_the_allowance` passing unchanged.
- `terminate_reports_a_forced_kill_as_force_killed_not_an_error`: spawn fake `hang`, drop stdin/stdout, `terminate(now+300ms)` → `exit == ForceKilled`, child pid not alive.
- `terminate_reports_a_crash_as_exited`: fake `crash`, drop stdin → `Exited(_)`.
- `terminate_kills_a_grandchild_left_behind_by_a_wrapper`: fake `grandchild` with pid file in a temp path (use `std::env::temp_dir()` + uuid); wait for the pid file; drop stdin; `terminate(now+3s)` → grandchild pid not alive within 2 s (use `wait_until`).
- `dropping_the_process_kills_its_tree`: fake `grandchild`, wait for pid file, `drop(process)` → grandchild gone within 2 s.

- [ ] Step 1: Write the fixture + failing tests. Run `cargo test -p coda-mcp process::` → new tests fail (no `terminate`).
- [ ] Step 2: Implement `ProcessTree`, `terminate`, map `shutdown`.
- [ ] Step 3: `cargo test -p coda-mcp process::` → all pass. `cargo build -p coda-mcp --tests` → no new warnings.
- [ ] Step 4: Commit `fix(mcp): own the MCP server process tree and report termination outcomes`.

---

### Task 2: Slot-based manager + restart semantics (`coda-mcp/src/manager.rs`, `management_tools.rs`)

**Files:** Modify `rust/crates/coda-mcp/src/manager.rs`, `rust/crates/coda-mcp/src/management_tools.rs`, `rust/crates/coda-mcp/src/lib.rs` (exports).

**State:**

```rust
struct ServerSlot {
    spec: ServerSpec,
    live: Option<ServerEntry>,      // ServerEntry no longer holds the spec
    last_error: Option<String>,
    tools: Vec<McpToolInfo>,        // last known tools (kept while disconnected)
    generation: u64,                // bumped on every successful (re)connect
}
servers: RwLock<HashMap<String, ServerSlot>>,
restart_locks: std::sync::Mutex<HashMap<String, Arc<tokio::sync::Mutex<()>>>>,
tool_snapshot: std::sync::RwLock<Vec<(String, McpToolInfo)>>, // (server, tool) for every slot; rebuilt whenever `servers` changes (helper `fn rebuild_snapshot(&self, map: &HashMap<String, ServerSlot>)`, sorted by server then tool name)
```

**Behaviour:**
- `connect_many` / `connect_http_many`: on success insert a connected slot; on failure insert (or update) a slot with `live: None`, `last_error: Some(error.to_string())`, `tools: vec![]`, keeping the returned `McpConnectError` list unchanged. Build the spec from the config even when connect fails (`ServerSpec::Stdio(Box::new(cfg.clone()))` / `ServerSpec::Http(Box::new(cfg.clone()), store.clone())`).
- `connected_status()` unchanged semantics (only live slots).
- New `pub async fn server_faults(&self) -> Vec<McpServerFault>` with `pub struct McpServerFault { pub name: String, pub last_error: String }` — slots with `live == None`; `last_error` defaults to `"not connected"`. Sorted by name. Export from `lib.rs`.
- `call_tool` / `get_prompt` / `read_resource` on a slot without `live`: `"MCP server '{name}' is not connected (last error: {e}). Call restart_mcp_server with server='{name}' to relaunch it."` (omit the parenthesis when no error). Unknown name: `"unknown MCP server '{name}'; configured servers: a, b"` or `"unknown MCP server '{name}'; no MCP servers are configured"` (helper `fn unknown_server(name, map)`).
- Transport loss in `call_tool`: `McpToolCallError::Transport(McpTransportError::ConnectionClosed)` or `Transport(Rpc(e))` where `e.message` contains `"session not found"` (case-insensitive). After dropping the read guard, call `self.mark_disconnected(name, generation, reason)`: under the write lock, if the slot's generation matches and it is live, take `live`, set `last_error`, rebuild the snapshot, then `tokio::spawn` the old entry's `terminate` with the 3 s grace (log the report). Error text: `"MCP tool '{tool}' on server '{server}' failed: {e}. The server connection was lost; it is now marked disconnected. Call restart_mcp_server with server='{server}' to relaunch it, then retry."`. Other transport errors keep today's text.
- `ServerEntry::shutdown(deadline)` keeps its `Result<(), String>` contract for `McpClientManager::shutdown` (harness exit); add `ServerEntry::terminate(deadline) -> Option<TerminationReport>` (client shutdown errors logged at debug; `None` for HTTP) used by restart.
- `restart_server(&self, name) -> Result<RestartReport, String>`:
  1. lock the per-server restart mutex;
  2. write-lock: unknown → `Err(unknown_server(..))`; else `let old = slot.live.take(); let spec = slot.spec.clone();` rebuild snapshot not needed (tools kept);
  3. `let old_outcome = match old { Some(e) => OldProcess::from(e.terminate(now+SHUTDOWN_GRACE).await), None => OldProcess::NotRunning }` — log `warn!` for ForceKilled/Exited/Unconfirmed;
  4. reconnect from spec (same timeouts as today);
  5. success: write-lock, set `live`, `tools = client.tools().to_vec()`, `last_error = None`, `generation += 1`, rebuild snapshot; `Ok(RestartReport { tool_count, old: old_outcome })`;
  6. failure: write-lock, `last_error = Some(e.to_string())`; `Err(format!("failed to restart '{name}': {e}. The server stays configured; fix the cause and call restart_mcp_server again."))`.
- `pub struct RestartReport { pub tool_count: usize, pub old_process: OldProcessOutcome }`, `pub enum OldProcessOutcome { NotRunning, NoProcess /*http*/, ExitedCleanly, AlreadyExited(String), ForceKilled { grace: Duration, leftovers_killed: Option<usize> }, Unconfirmed(String) }` plus `impl RestartReport { pub fn summary(&self, server: &str) -> String }` producing:
  - `Restarted MCP server 'x' (old process exited cleanly). N tool(s) available.`
  - `(old process had already exited: {status})`, `(server was not running)`, `(HTTP session reopened)`,
  - `(old process force-killed after {grace_secs}s; {k} leftover process(es) terminated)` (omit the `; …` part when `None` or 0),
  - `(warning: termination of the old process could not be confirmed: {e})`.
  Leftovers after a clean exit also mention `; {k} leftover process(es) terminated` when k > 0.
- `RestartMcpServerTool::execute` → `Ok(report) => ToolResult::ok(report.summary(server))`; description: `"Restart an MCP server: kill whatever is left of its process tree, relaunch it from its original configuration, reconnect and refresh its tools. Works whether the server is connected, disconnected, crashed or failed to start. Call it when an MCP server stops responding, then retry the failed call."`
- `tools()` (Arc wrappers) now built from the snapshot.
- New `pub fn tool_source(self: &Arc<Self>) -> Arc<dyn coda_tool::ToolSource>` is added in Task 3 (needs the trait); do not add it here.
- Update `test_support::connect_fake_server` and the `inject_client` test helper to insert slots.

**Tests (manager.rs tests; use the Task 1 fixture):**
- rewrite `restart_waits_for_old_process_cleanup_before_reconnecting`: the injected entry's spec is a fake `serve` connectable; restart must return `Ok`, and the marker file must be gone at the moment of reconnect (assert `!marker.exists()` after `Ok`, as today) — ordering without failure.
- `restart_force_kills_a_hung_server_and_reconnects` (fake `hang` via `connect_many`): `Ok(report)`, `matches!(report.old_process, OldProcessOutcome::ForceKilled{..})`, old pid dead, `call_tool("s","echo",{})` returns `pid:<new pid>` ≠ old pid. Use `tokio::time::pause`? No — real time; grace is 3 s, acceptable.
- `restart_kills_the_old_grandchild` (fake `grandchild` with pid file): after `Ok`, old grandchild pid not alive.
- `restart_of_a_crashed_server_succeeds` (fake `crash`; first drop the connection by restarting? simpler: connect fake `exit-now` → connect fails → slot disconnected; and separately connect `crash`, then kill stdin? Use: connect `crash`, then call `restart_server` — on restart stdin closes, process exits 3 → `AlreadyExited`/`Exited` → `Ok`).
- `a_failed_reconnect_keeps_the_server_restartable`: connect `serve` with name "s"; then mutate the slot spec command to a missing program (test-only access to `servers`), restart → `Err` containing "restart_mcp_server again"; `server_faults()` lists "s"; restore the real spec; restart → `Ok`.
- `restart_twice_in_a_row_succeeds`.
- `a_server_that_failed_at_startup_can_be_started_by_restart`: `connect_many([connectable with missing command])` → error; `server_faults()` has it; then fix spec (test access) and restart → `Ok`, `connected_status()` includes it.
- `restarting_an_unknown_server_lists_configured_names`: error contains `unknown MCP server 'nope'` and the configured name.
- `a_lost_connection_marks_the_server_disconnected`: connect `serve`; kill the child out-of-band (test access to `live._process.child.start_kill()`), call `call_tool` → error mentions `restart_mcp_server`; `server_faults()` lists the server; `call_tool` again → "is not connected (last error:"; restart → `Ok`.
- `call_tool_on_missing_server_returns_error` keeps passing (name still present).
- management_tools: update `an_unknown_server_is_an_error_not_an_empty_result` to expect `unknown MCP server`.

- [ ] Step 1: write failing tests; run `cargo test -p coda-mcp manager::` (fails to compile / fails).
- [ ] Step 2: implement.
- [ ] Step 3: `cargo test -p coda-mcp` all green, no new warnings.
- [ ] Step 4: Commit `fix(mcp): restart_mcp_server always restarts and never loses a server`.

---

### Task 3: Live MCP tool source in the agent registry

**Files:** `rust/crates/coda-tool/src/lib.rs` (trait), `rust/crates/coda-agent/src/tool/registry.rs`, `rust/crates/coda-agent/src/tool/mod.rs` (re-export `ToolSource`), `rust/crates/coda-agent/src/subagents/host.rs` (`resolve_child_tools`), `rust/crates/coda-agent/src/agent/tools.rs` (adapt to owned `all()` if needed), `rust/crates/coda-mcp/src/manager.rs` (`tool_source`).

```rust
// coda-tool
/// A live, changing set of tools (e.g. MCP servers that reconnect).
pub trait ToolSource: Send + Sync {
    fn tools(&self) -> Vec<Arc<dyn Tool>>;
}
```

Registry:
- fields `sources: Vec<Arc<dyn ToolSource>>`; `pub fn with_source(mut self, s: Arc<dyn ToolSource>) -> Self`.
- `resolve(name)`: static first, then the first source tool with that name.
- `all(&self) -> Vec<Arc<dyn Tool>>`: static tools in order, then source tools whose names are not static names and not already seen.
- `definitions()` / `read_only()` built on `all()` (`read_only()` returns a static snapshot).
- `pub fn filtered(&self, keep: impl Fn(&dyn Tool) -> bool + Send + Sync + 'static) -> Self`: static tools filtered now; each source wrapped in a private `FilteredSource { inner, keep: Arc<dyn Fn(&dyn Tool) -> bool + Send + Sync> }`.
- `resolve_child_tools` uses `filtered` (read-only and/or no task-management tools) or `filtered(|_| true)` so subagents keep the live MCP set.
- coda-mcp: `McpToolSource { manager: Arc<McpClientManager> }` implementing `ToolSource` by building `McpTool`s from `tool_snapshot`; `McpClientManager::tool_source(self: &Arc<Self>) -> Arc<dyn ToolSource>`.

Tests:
- registry: `a_source_tool_resolves_and_appears_in_all_and_definitions`; `a_static_tool_shadows_a_source_tool_of_the_same_name`; `source_changes_are_visible_without_rebuilding`; `filtered_applies_to_source_tools_lazily`.
- subagents: existing `resolve_child_tools` tests stay green.
- coda-mcp: `the_tool_source_reflects_a_restart_with_a_new_tool_list` — connect fake `serve` as "s" (1 tool), source.tools() has `mcp__s__echo`; change the slot spec to mode `tools2`, restart, source.tools() now has 2 tools including `mcp__s__extra`; and `a_disconnected_server_keeps_its_tools_routable` (after mark-disconnected the tool is still listed and calling it returns the restart hint).

Commands: `cargo test -p coda-agent --lib tool::registry`, `cargo test -p coda-agent --lib subagents`, `cargo test -p coda-mcp`.
Commit `feat(agent): live tool sources so MCP tools follow restarts`.

---

### Task 4: Engine wiring + status (`coda-serve`, `coda-proto`, `coda-tui`)

**Files:** `rust/crates/coda-serve/src/mcp.rs`, `rust/crates/coda-serve/src/host.rs` (registry construction ~L1305, `mcp/list` ~L4355, test `fake_mcp_bundle` ~L11829), `rust/crates/coda-serve/src/mcp_list.rs`, `rust/crates/coda-proto/src/mcp.rs`, `rust/crates/coda-tui/src/browsers.rs` (~L440).

- `McpBundle.tools` = management tools only; host builds `ToolRegistry::new(built_ins.chain(mcp_tools))` then `.with_source(manager.tool_source())` when a manager exists. Update `fake_mcp_bundle` to not add `manager.tools()` (the existing engine test then proves resolution through the source). Add host test `an_mcp_server_restarted_into_new_tools_is_callable_without_rebuilding_the_engine` only if cheap with the in-memory fake (otherwise rely on Task 3 tests).
- `McpServerDto.last_error: Option<String>` (`#[serde(default, skip_serializing_if = "Option::is_none")]`, wire `lastError`); update the allow-list test to include `lastError` and all struct literals.
- `mcp_list::build(configured, runtime, faults: &[McpServerFault], mcp_enabled)`: a NotConnected server with a fault gets `last_error: Some(sanitise_error(..))`; `sanitise_error`: replace every `scheme://…` token with `sanitise_url(token)`, replace control chars with spaces, collapse whitespace, cap 200 chars + `…`. host passes `manager.server_faults().await`. Tests: `a_disconnected_server_reports_its_sanitised_last_error` (error containing `https://u:p@h/x?token=SECRET` and a newline → no `SECRET`, no `u:p`, single line), `a_long_last_error_is_capped`.
- TUI `mcp_from_engine`: runtime `NotConnected` with `last_error` → `disconnected (last error: …)`; detail adds `restart    ask the agent to run restart_mcp_server for '<name>'` for any NotConnected enabled server. Test in browsers.rs tests.

Commands: `cargo test -p coda-proto mcp`, `cargo test -p coda-serve mcp`, `cargo test -p coda-tui browsers`.
Commit `feat(mcp): surface disconnected MCP servers and their last error`.
