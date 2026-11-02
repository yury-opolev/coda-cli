# Planned restart (session/prepareRestart) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a front end restart `coda serve` (e.g. to upgrade Coda) without losing schedules, undelivered notifications, protocol-set settings, queued steering, or running subagents: `session/prepareRestart` drains and writes `.coda/sessions/<id>.restart.json` (+ per-subagent files), `initialize {sessionId, restore}` / `coda serve --restore` brings it all back once.

**Architecture:** Three layers. (1) `coda-agent` gains a user-only atomic file writer, a suspend/resume control in `TaskManager` (new `Suspended` status, explicit vs inherited suspension), a cooperative *safe-point gate* in the agent loop and parker that snapshots the conversation and pauses, and a serde `SubagentCheckpoint` + `SubagentHost::restore`. (2) `coda-proto` gains the wire types (methods, params/results, `draining` lifecycle, `drain` snapshot section, task suspend events, error codes). (3) `coda-serve` gains a draining state with a single turn-claim gate, task suspend/resume RPCs, a `restart` module that builds/writes/reads the restart file, the `prepareRestart` drain orchestration and restore-on-initialize; `coda-boot` adds `--restore`/`--session` to `coda serve`.

**Tech Stack:** Rust (tokio, serde, chrono, tokio-util CancellationToken), windows-sys (owner-only DACL), schemars (feature `schema`).

**Source request:** e-mail "Change request: planned restarts that keep Coda's in-memory state (session/prepareRestart)" (push-agent-bridge). Its wording on formats/fields is authoritative; deviations are listed at the end.

---

## Conventions for every task

- Work in `C:\Users\yurio\Documents\github\coda-cli\.worktrees\planned-restart` (branch `feat/planned-restart`). Cargo commands run from `rust\`.
- TDD: write the failing test, see it fail, implement, see it pass. Tests live in the existing `#[cfg(test)] mod tests` of the file you change (no `tests/` dirs exist).
- Do NOT `cargo fmt` whole crates (main is not fmt-clean); keep new code rustfmt-style by hand. No new compiler warnings.
- Test commands: `cargo test -p coda-agent <filter>`, `cargo test -p coda-serve <filter>`, `cargo test -p coda-proto --features schema <filter>`.
- Commit after each task: conventional commit, body ends with `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
- Paths below are relative to `rust\crates\`.

## Design decisions (binding for all tasks)

| Topic | Decision |
|---|---|
| Error code for refused work while draining | **-32016** `RESTART_PENDING`, message `"restart pending"`. (-32012 suggested in the request is already `STALE_EPOCH`.) |
| Newer restart-file format | initialize fails with **-32017** `RESTART_FILE_UNSUPPORTED` *before* initialization is claimed (client may retry without `restore`). |
| Lifecycle | new `EngineLifecycle::Draining` (`"draining"`); once draining the engine never returns to `ready`/`busy`; `shutdown` still goes `stopping`→`stopped`. |
| Drain timeout | `drainTimeoutMs` default 600000, max 3600000 (larger → -32602), 0 allowed (= interrupt immediately). |
| What drain waits for | main turn released, every non-subagent task (shell, tool, scheduled run) terminal, every `Subagent` task terminal **or suspended**. |
| Settle window | after the timeout interrupts work, subagents get `RESTART_SETTLE = 30s` to reach a safe point; ones still not suspended are cancelled and recorded as interrupted (`kind: "subagent"`). |
| New work during drain | `try_claim_turn_if` refuses (prompt, main-inbox pump, compact, fork, rewind); schedule runtime stops *launching* (pause flag); new subagents spawned during drain start suspended (TaskManager `suspend_new`). Steering into the running turn still accepted. `task/resume`/`session/resumeAllTasks` refused with -32016 while draining. |
| Undelivered notifications | `notify_user` ring entries with `cursor > deliveredCursor` (new optional prepareRestart param, default 0 = everything still retained) **plus** pending main-inbox (`ask_main`) items. On restore: re-published (new cursors; `event/agentMessage` for user ones; main ones re-queued and wake the pump). |
| Schedules | full `ScheduledTask` JSON (keeps id, `runsStarted`, `nextRunUtc`, `maxRuns`, `expiresAtUtc`). Restore keeps the id; recurring (interval/cron) with `nextRunUtc <= now` → `ScheduleRecurrence::advance_recurring_past(def, now)` (first slot after restart, no burst, no catch-up run). One-shot `at` schedules whose time passed are kept due and fire once (at-least-once, documented). Retired ones restored as-is. |
| Settings | `model`, `provider`, `effort`, `goal` (+`maxDuration`,`maxContinuations` = remaining per-turn budgets), `permissionMode`, `systemPrompt`. Restore applies via the same commit paths as the setters. `bypassPermissions` → mode stays `default`, reported in `settingsNotRestored`. A stored provider different from the running one is not switched (reported in `settingsNotRestored`). |
| `[restart]` entry | one `Message::user` appended to the restored history (after `recover_interrupted_history`) and persisted immediately; wording: `[restart] Coda was restarted for an upgrade (0.1.176 → 0.1.177).` (or `… was restarted (<reason>).` when versions equal / `for: <reason>`), then `Steering sent before the restart that was not delivered: "<t1>"; "<t2>".`, then `Interrupted: run_command npm test (started 12:01 UTC, last output: …); …`, `Declined requests: permission for run_command; question "…".`, `Suspended subagents restored: task-00000002 (…).`, then `Re-run anything still needed.` |
| Restart file | `.coda/sessions/<id>.restart.json`, written atomically, owner-only (Unix 0600; Windows protected DACL with a single full-control ACE for the current user SID). `SessionTranscriptStore::list()` skips `*.restart.json`. |
| Subagent files | `.coda/sessions/<id>.tasks/<taskId>.json` (same writer), listed by id in the restart file's `tasks`. Size cap 8 MiB: oldest tool-result texts replaced by `[truncated for a planned restart: N characters omitted]` until it fits; `truncated: true`. |
| Restore validity | file must exist (`missing`), parse (`unreadable`), `version <= 1` (else -32017), `sessionId` match (`sessionMismatch`), `workingDir` match case-insensitively on Windows (`workingDirMismatch`), `preparedAt` within 24h and not in the future by >5 min (`expired`). Ignored files are left on disk. Valid file: **deleted before applying** (delete failure → ignored with `consumeFailed`), task files deleted after their subagents are re-created. |
| `restore` param | `initialize.restore`: `true` \| `false` \| `"suspended"` (anything else -32602). `restore` without `sessionId` → -32602. Transcript missing but restart file present → session starts empty under that id. `coda serve --restore[=suspended]` makes `restore` default to that value; `coda serve --session <id>` makes `sessionId` default. |
| Task status | `TaskRunStatus::Suspended` (`"suspended"`), non-terminal. `statusFilter`: `active` = running+suspended, new `suspended` = suspended only, `terminal`, `all`. `TaskSummaryDto` gains `suspendRequested?: bool`. |
| Suspension semantics | each task has an `explicit` flag; *effective* suspension = explicit on itself or on any ancestor, or manager `suspend_new` for tasks created while draining. Suspend(id) sets explicit; Resume(id) clears explicit (children resume unless they are explicitly suspended themselves). suspendAll sets explicit on every running subagent; resumeAll clears all explicit flags. Only `TaskKind::Subagent` can be suspended; shell/tool/scheduled → `notSuspendable`. |
| Safe point | top of each agent-loop iteration (`agent\mod.rs` "1. Cooperative pause gate" seam) and inside the parker's wait. Never mid-stream; a tool batch in progress completes (or its tasks are interrupted at the timeout) first. |
| Events | `event/taskSuspended {taskId, ownerTaskId?, kind, reason: "requested"\|"parent"\|"restart"}`, `event/taskResumed {taskId, ownerTaskId?, kind}`, `event/taskStarted` gains `resumed?: true` for restored subagents. |
| task_wait on suspended child | returns immediately (and an in-flight wait wakes) with `Task '<id>' is suspended (paused at a safe point); it has not finished. Its report will be delivered to you automatically after it is resumed and finishes. You may end your turn or wait again.` (not an error). |
| Interrupted tool result | a shell/tool task stopped by the drain timeout gets the stop note `planned restart`; a foreground tool call awaiting it records `Interrupted by a planned restart; last output:\n<tail>` (tail ≤ 2000 chars, `(no output)` if empty). |

## File structure

**coda-agent**
- Create `coda-agent\src\session\private_file.rs` — `write_private_atomic(path, bytes)` (async) + `restrict_to_owner` (unix/windows) + tests.
- Modify `coda-agent\src\session\store.rs` — `list()` skips `.restart.json`; pub helpers `restart_file_path(id)`, `tasks_dir(id)`, `sessions_dir()`.
- Modify `coda-agent\src\session\mod.rs` — export.
- Modify `coda-agent\Cargo.toml` — `[target.'cfg(windows)'.dependencies] windows-sys = { version = "0.61", features = ["Win32_Foundation","Win32_Security","Win32_Security_Authorization","Win32_System_Threading","Win32_System_Memory"] }`.
- Modify `coda-agent\src\tasks\managed_task.rs` — `Suspended` status, suspend fields, checkpoint slot, spec slot, stop note, `resumed` flag.
- Modify `coda-agent\src\tasks\task_manager.rs` — suspend API, `register_restored`, observer hooks, filters, owner-inbox for suspended owners.
- Modify `coda-agent\src\agent\mod.rs`, `park.rs`, `tools.rs` — safe-point gate, start-parked, steps offset, attached-wait suspended wake, interrupted wording.
- Modify `coda-agent\src\tools\task_wait.rs` — suspended result.
- Create `coda-agent\src\subagents\checkpoint.rs` — `SubagentSpec`, `SubagentCheckpoint`, size cap, file IO.
- Modify `coda-agent\src\subagents\host.rs` — record spec at spawn, `restore()`.

**coda-proto**
- Modify `messages.rs` (method consts, error codes, InitializeParams.restore, InitializeResponse.restored), `requests.rs` (params, filter, gated list), `responses.rs` (results), `state.rs` (Draining, DrainState), `events.rs` (TaskSuspended/TaskResumed, TaskStarted.resumed), `schema.rs`, `schema\catalog.rs`.

**coda-serve**
- Create `coda-serve\src\restart.rs` — `RestartFile` v1 serde model, build/validate/read, `[restart]` text, constants.
- Modify `state\mod.rs` (draining), `host.rs` (gate, handlers, prepare/restore orchestration), `dispatch.rs` (routes + trait + fakes), `task_events.rs` (events), `capabilities.rs`, `lib.rs`.
- Modify `coda-agent\src\scheduling\runtime.rs` (launch pause), `scheduled_task_store.rs` (`restore`).
- Modify `coda-agent\src\message\mod.rs` — serde snapshot of retained/pending messages + re-publish helper.

**coda-boot / coda**: `coda-boot\src\serve.rs` (`--restore`, `--session` → env), host `StartupOptions`.

**Docs**: `docs\serve-protocol.md`, `docs\protocol\catalog.md`, `docs\protocol\catalog.json`, `docs\protocol\schemas\*.json`, `README.md` (serve flags), `version.json` (177).

---

### Task 1: Owner-only atomic file writer + store helpers

**Files:** Create `coda-agent\src\session\private_file.rs`; modify `session\mod.rs`, `session\store.rs`, `coda-agent\Cargo.toml`.

- [ ] **Step 1: failing tests** in `private_file.rs`:
  - `writes_atomically_and_replaces_existing` — write "a" then "b" to `dir/x.json`; content is "b"; no `.*.tmp` left in dir.
  - `creates_parent_directories` — path `dir/a/b/x.json`.
  - `#[cfg(unix)] file_is_mode_0600` — `metadata.permissions().mode() & 0o777 == 0o600`.
  - `#[cfg(windows)] file_dacl_is_protected_and_owner_only` — run `icacls <path>`; output lines with `:(` ACEs: exactly one, and it does not contain `Everyone`, `Users`, `Authenticated Users`.
  - in `store.rs`: `list_skips_restart_files` — save a transcript `s1`, write `s1.restart.json` with `{}`; `list()` returns only `s1`. `restart_file_path_and_tasks_dir_layout` — `restart_file_path("abc")` ends with `.coda/sessions/abc.restart.json`, `tasks_dir("abc")` ends with `.coda/sessions/abc.tasks`.
- [ ] **Step 2:** `cargo test -p coda-agent private_file store::` → fail (missing module).
- [ ] **Step 3: implement**
  ```rust
  //! Atomic, owner-only file writes for state that must not be readable by
  //! other local users (planned-restart files).
  pub async fn write_private_atomic(path: &Path, bytes: &[u8]) -> std::io::Result<()>
  ```
  Create parent dirs; temp `parent/.{file_name}.{uuid}.tmp` created with `OpenOptions::new().write(true).create_new(true)` (+ `.mode(0o600)` on unix via `std::os::unix::fs::OpenOptionsExt`); on windows call `restrict_to_owner(&temp)` right after creation and before writing; write all, `sync_all()`, drop the handle, `std::fs::rename(temp, path)` (Windows needs the file closed); on any error remove the temp. Run the blocking body in `tokio::task::spawn_blocking`. Also expose `pub fn write_private_atomic_blocking(path, bytes)` (used by tests and sync callers).
  Windows `restrict_to_owner(path)`: `OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY)`, `GetTokenInformation(TokenUser)` (two-call size pattern), build `EXPLICIT_ACCESS_W { grfAccessPermissions: GENERIC_ALL, grfAccessMode: SET_ACCESS, grfInheritance: NO_INHERITANCE, Trustee: TRUSTEE_W { TrusteeForm: TRUSTEE_IS_SID, TrusteeType: TRUSTEE_IS_USER, ptstrName: sid as *mut u16, .. } }`, `SetEntriesInAclW(1, &ea, null, &mut acl)`, `SetNamedSecurityInfoW(wide_path, SE_FILE_OBJECT, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, null, null, acl, null)`, `LocalFree(acl)`, `CloseHandle(token)`. Return `io::Error::from_raw_os_error(code)` on failure. Keep every `unsafe` block with a `// SAFETY:` line.
  `store.rs`: make `sessions_dir()` pub, add `pub fn restart_file_path(&self, id: &str) -> PathBuf` (`<id>.restart.json`), `pub fn tasks_dir(&self, id: &str) -> PathBuf` (`<id>.tasks`); in `list()` skip names ending `.restart.json`.
- [ ] **Step 4:** tests pass.
- [ ] **Step 5:** commit `feat(agent): owner-only atomic writer and restart file paths`.

### Task 2: TaskManager suspension model

**Files:** `coda-agent\src\tasks\managed_task.rs`, `task_manager.rs`, `coda-proto\src\requests.rs` (filter only if `TaskStatusFilter` lives there — it does), every `match` on `TaskRunStatus` (grep `TaskRunStatus::` across the workspace, incl. coda-serve, coda-tui).

Behaviour:
- `TaskRunStatus::Suspended` (serde/as_str `"suspended"`), `is_terminal() == false`.
- `ManagedTask` new state: `suspend_explicit: AtomicBool`, `stop_note: Mutex<Option<String>>`, `resumed: AtomicBool` (restored task), `checkpoint: Mutex<Option<TaskCheckpoint>>` where `pub struct TaskCheckpoint { pub history: Vec<coda_llm::Message>, pub steps_used: usize, pub parked: bool, pub suspended_at: SystemTime }`, `spec: OnceLock<serde_json::Value>` (opaque subagent spec, filled by Task 4).
- `TaskManager` API (all pub):
  - `fn is_suspension_requested(&self, id) -> bool` — effective rule (explicit on self or any ancestor via `parent_id`, or created while `suspend_new` set and not yet explicitly resumed: implement by setting explicit=true at registration when `suspend_new` is on).
  - `fn suspension_reason(&self, id) -> Option<&'static str>` — `"restart"` if `suspend_new`/restart flag set, `"requested"` if explicit on self, `"parent"` if inherited.
  - `fn request_suspend(&self, id) -> TaskActionResult` — NotFound / InvalidState (terminal) / Rejected (kind != Subagent) / Ok; sets explicit; notifies.
  - `fn request_resume(&self, id) -> TaskActionResult` — NotFound / Rejected (not subagent) / InvalidState (terminal, or not explicitly suspended and not suspended at all) / Ok; clears explicit; notifies.
  - `fn suspend_all(&self) -> usize` (running/suspended subagents newly set explicit), `fn resume_all(&self) -> usize` (explicit flags cleared).
  - `fn set_suspend_new(&self, on: bool)` and `fn set_restart_mode(&self, on)` (reason "restart").
  - `fn suspend_signal(&self) -> &tokio::sync::Notify` — `notify_waiters()` on every flag/status change.
  - `fn mark_suspended(&self, id, checkpoint: TaskCheckpoint)` — Running→Suspended, store checkpoint, bump version, post `TaskChange`, `observer.on_suspended(&snapshot, reason)`, notify.
  - `fn mark_resumed(&self, id)` — Suspended→Running, clear checkpoint, `observer.on_resumed(&snapshot)`, notify.
  - `fn checkpoint(&self, id) -> Option<TaskCheckpoint>` (clone).
  - `fn stop_with_note(&self, id, note: &str)` — like `stop_task` (operator stop + kill shell) and records `stop_note`; `ManagedTask::stop_note()` getter.
  - `fn register_restored(&self, id: &str, kind, description, parent: Option<&str>, mode, started_at: SystemTime, suspended_explicit: bool) -> Result<Arc<ManagedTask>, String>` — rejects invalid id format (`task-` + digits) or duplicate; `next_id = max(next_id, n)` so new ids never collide; sets `resumed=true`, `started_at`; fires `on_started` (whose snapshot carries `resumed`).
- `TaskSnapshot` gains `resumed: bool`, `suspend_requested: bool`.
- `TaskEventObserver` gains default no-op `fn on_suspended(&self, task: &TaskSnapshot, reason: &str) {}` and `fn on_resumed(&self, task: &TaskSnapshot) {}`.
- Allowed transitions: complete/fail/stop accept Suspended as source. `send_message` accepts Suspended (inbox keeps it). `push_to_owner_inbox` accepts Suspended owners. `live_children` includes Suspended. `stop_all`/`shutdown` stop suspended tasks too (cancel token; Task 3's gate wakes on cancel).
- `TaskStatusFilter` gains `Suspended` (`"suspended"`); change `admits(terminal: bool)` to `admits_status(&TaskRunStatus-like str)` or add `admits_suspended`: `Active` admits running+suspended, `Suspended` only suspended, `Terminal` terminal, `All` all. Update `list_with_output_matching` and its callers.

- [ ] **Step 1: failing tests** (task_manager.rs tests): `suspend_request_is_inherited_by_descendants_and_resume_respects_explicit_children` (A→B→C; suspend B, suspend A, resume A → B,C still requested; resume B → none); `suspend_rejects_non_subagent_and_terminal_tasks`; `mark_suspended_and_resumed_round_trip_status_and_observer` (recording observer sees on_suspended reason "requested", then on_resumed); `register_restored_keeps_id_and_advances_counter` (restore `task-00000007`, next `register` gives `task-00000008`; duplicate id errors; snapshot.resumed true); `suspend_new_marks_tasks_created_while_draining` (reason "restart"); `active_filter_includes_suspended_and_suspended_filter_only_suspended`; `owner_inbox_receives_child_completion_while_owner_suspended`; `stop_with_note_records_note_and_stops`.
- [ ] **Step 2:** run → fail.
- [ ] **Step 3:** implement; fix every exhaustive `match` on `TaskRunStatus` (TUI task browser: show `suspended` like running with a "paused" label if it switches on status).
- [ ] **Step 4:** `cargo test -p coda-agent tasks::` and `cargo build -p coda-serve -p coda-tui` green.
- [ ] **Step 5:** commit `feat(agent): suspended task status with inherited suspension`.

### Task 3: Safe-point gate in the agent loop

**Files:** `coda-agent\src\agent\mod.rs`, `park.rs`, `tools.rs`, `tools\task_wait.rs`.

Behaviour:
- New private fn `async fn safe_point(&self, history: &Vec<Message>, steps_used: usize, parked: bool, cancel: &CancellationToken) -> Result<(), AgentError>` on `AgentLoop`. Active only when `self.task_manager` and `self.caller_task_id` are set **and** that task's kind is `Subagent`. If `is_suspension_requested(id)`: `mark_suspended(id, TaskCheckpoint{history: history.clone(), steps_used, parked, suspended_at: now})`, then loop `{ let n = mgr.suspend_signal().notified(); pin+enable; if !requested { break } select!{ cancel → return Err(Cancelled), n → {} } }`, then `mark_resumed(id)` and reopen steering.
- Call sites: (a) loop top at the "1. Cooperative pause gate" seam with `steps_used = self.steps_offset + iteration`, `parked = false`; (b) inside `Parker::at_natural_stop`'s select, add a branch on the suspend signal: when requested → call the same gate with `parked = true` (pass a closure/trait so `park.rs` does not depend on AgentLoop), then reset `parked_at = Instant::now()` and `continue` the park loop (no model call).
- Builder: `with_steps_offset(usize)` (iteration cap uses `self.steps_offset + iteration >= self.max_iterations`), `with_start_parked(bool)` (on the first iteration, before any model call, run `parker.at_natural_stop` and `continue`/finish accordingly; Finish → loop ends with the existing final text).
- `tools.rs` `await_attached_task`: also wake on `mgr.suspend_signal()`; if the awaited task's status is `Suspended` → return the suspended text (see design table). `task_wait.rs`: if the target is Suspended at call time → same text, not an error.
- `tools.rs` `finished_result` (Stopped branch): if `task.stop_note() == Some("planned restart")` → `Interrupted by a planned restart; last output:\n{tail}` where tail = `redacted_peek(2000)` or `(no output)`.

- [ ] **Step 1: failing tests** (agent\mod.rs tests, MockLlmClient + `task_manager_for_test()`):
  - `suspended_subagent_pauses_before_next_model_request_and_resumes` — register Subagent task; loop with caller_task_id; script: tool_use(probe) then text. Request suspend before running; run in a spawned task; wait until status Suspended; assert `client` saw 0 requests... (better: request suspend from inside the probe tool so the gate fires after the first response's tool results) → status Suspended, checkpoint history ends with the tool-result message, model request count == 1; resume → second request includes the prior tool result; run completes.
  - `cancel_while_suspended_returns_cancelled`.
  - `gate_is_inactive_for_main_context_and_non_subagent_tasks`.
  - `parked_subagent_suspends_without_a_model_call` — subagent with a live child shell task parks; request suspend → status Suspended with `parked: true`, no extra model request; resume → keeps parking.
  - `steps_offset_counts_toward_iteration_cap`.
  - `start_parked_skips_the_first_model_call_until_inbox_has_work`.
  - `task_wait_reports_suspended_child` (task_wait.rs tests) and `attached_wait_wakes_when_child_suspends` (tools.rs tests).
  - `stop_with_planned_restart_note_yields_interrupted_tool_result` (foreground run_command style task in tools tests).
- [ ] **Step 2–4:** fail → implement → `cargo test -p coda-agent agent:: tools::` green.
- [ ] **Step 5:** commit `feat(agent): cooperative safe-point suspension for subagents`.

### Task 4: Subagent checkpoint and restore

**Files:** create `coda-agent\src\subagents\checkpoint.rs`; modify `subagents\mod.rs`, `subagents\host.rs`.

Types (serde camelCase):
```rust
pub struct SubagentSpec { pub agent_type: String, pub prompt: String, pub system_prompt: String,
  pub model: String, pub provider: Option<String>, pub effort: Option<String>, pub depth: u32,
  pub max_steps: usize, pub max_tokens: u32, pub foreground: bool,
  pub schedule_origin: Option<serde_json::Value> }
pub struct SubagentCheckpoint { pub version: u32 /*1*/, pub task_id: String,
  pub owner_task_id: Option<String>, pub reports_to: String /* "main" | owner id */,
  pub kind: String /* "subagent" */, pub description: String, pub mode: String,
  pub spec: SubagentSpec, pub conversation: serde_json::Value /* message_json */,
  pub steps_used: usize, pub parked: bool, pub steering: Vec<CheckpointInboxItem>,
  pub explicitly_suspended: bool, pub started_at: DateTime<Utc>, pub suspended_at: DateTime<Utc>,
  pub truncated: bool }
pub struct CheckpointInboxItem { pub kind: String, pub text: String }
pub const SUBAGENT_CHECKPOINT_VERSION: u32 = 1;
pub const CHECKPOINT_SIZE_CAP: usize = 8 * 1024 * 1024;
```
Functions: `SubagentCheckpoint::from_task(mgr, id) -> Option<Self>` (needs Suspended status + checkpoint + spec; steering via a new **non-destructive** `SteeringInbox::snapshot_items()`), `fn to_json_capped(&self, cap) -> (Vec<u8>, bool)` (truncate oldest `ToolResult` text blocks first, marker text above), `pub async fn write(&self, dir: &Path)` (→ `dir/<taskId>.json` via `write_private_atomic`), `pub fn read(path) -> Result<Self, String>` (refuse `version > 1`).
`SubagentHost`:
- at spawn (`run_inner`, once final system prompt/model/budget are known) store `SubagentSpec` JSON into the task's `spec` slot; add effort when the host has one (`effort: None` today – keep field).
- `pub async fn restore(self: &Arc<Self>, cp: SubagentCheckpoint, keep_suspended: bool) -> Result<String, String>`: `register_restored(cp.task_id, Subagent, description, owner, Background, started_at, keep_suspended || cp.explicitly_suspended)`; attach steering inbox and re-push `cp.steering` items; acquire semaphore permit like a background spawn (if none free → still run; restored work must not be dropped — use `acquire_owned().await` in the spawned task); spawn the same completion wiring as background spawn but run the loop with `history = deserialize_messages(&cp.conversation)`, the stored system prompt/model/budget, `with_steps_offset(cp.steps_used)`, `with_start_parked(cp.parked)`. Factor the shared part of `run_inner` into a helper taking `Option<RestoredRun>` so there is one code path.

- [ ] **Step 1: failing tests** (host.rs tests with `ScriptedClient`, `host_with`, `manager`):
  - `checkpoint_round_trips_through_json` (spec, conversation incl. tool_use/tool_result, steering).
  - `oversized_checkpoint_truncates_oldest_tool_outputs_first` (cap 4 KiB, three tool results of 2 KiB; first truncated, last intact, `truncated == true`).
  - `nested_subagents_suspend_and_restore_under_same_ids` — parent spawns child (task tool) whose script blocks on a gated tool; suspend parent → both reach Suspended; build checkpoints; new `TaskManager` + new host with a fresh `ScriptedClient` capturing requests; restore parent then child (depth order); both run under the same ids; the first request of each contains the prior conversation (assert a text from before suspension appears); `event` observer saw `resumed: true`.
  - `restore_keep_suspended_waits_for_resume`.
  - `restored_parent_receives_child_report_after_resume` — parent parked/awaiting; child resumed later completes → parent inbox gets the completion; parent finishes with a report mentioning it.
- [ ] **Step 2–4:** fail → implement → `cargo test -p coda-agent subagents::` green.
- [ ] **Step 5:** commit `feat(agent): serializable subagent checkpoints and restore under the same task id`.

### Task 5: Wire types (coda-proto)

**Files:** `coda-proto\src\messages.rs`, `requests.rs`, `responses.rs`, `state.rs`, `events.rs`, `schema.rs`, `schema\catalog.rs`.

- Methods: `PREPARE_RESTART = "session/prepareRestart"`, `TASK_SUSPEND = "task/suspend"`, `TASK_RESUME = "task/resume"`, `SUSPEND_ALL_TASKS = "session/suspendAllTasks"`, `RESUME_ALL_TASKS = "session/resumeAllTasks"` — all initialization-gated.
- Error codes: `RESTART_PENDING = -32016`, `RESTART_FILE_UNSUPPORTED = -32017`.
- `PrepareRestartParams { drain_timeout_ms: Option<u64>, reason: Option<String>, delivered_cursor: Option<u64> }`; `PrepareRestartResult { ok: bool, restart_file: String, drained: bool, interrupted_tasks: usize, interrupted_requests: usize, suspended_tasks: Vec<String> }`.
- `RestoreMode` (`#[serde(untagged)]`-style custom: `true`→`Resume`, `false`→`Off`, `"suspended"`→`Suspended`; serializes back to the same JSON) used in `InitializeParams.restore: Option<RestoreMode>` and documented as `boolean | "suspended"` in the schema.
- `RestoredSummary { applied: bool, ignored_reason: Option<String>, prepared_at: Option<String>, reason: Option<String>, from_version: Option<String>, to_version: Option<String>, schedules: usize, messages: usize, settings: Vec<String>, settings_not_restored: Vec<SettingNotRestored{name, value: Option<String>, reason}>, interrupted_tasks: usize, interrupted_requests: usize, steering: usize, tasks: Vec<String>, tasks_suspended: bool }` on `InitializeResponse.restored: Option<_>` (and the lenient `InitializeResult`).
- `EngineLifecycle::Draining`; `StateSnapshot.drain: Option<DrainState { started_at: String, deadline_at: String, remaining_ms: u64, reason: Option<String>, completed: bool }>` (`skip_serializing_if = Option::is_none`).
- Events `event/taskSuspended` → `TaskSuspended { task_id, owner_task_id, kind, reason }`, `event/taskResumed` → `TaskResumed { task_id, owner_task_id, kind }`; `TaskStarted.resumed: Option<bool>` (skip if none).
- `TaskSummaryDto.suspend_requested: Option<bool>`; `TaskStatusFilter::Suspended` (if not already done in Task 2).
- Catalog rows: `rpc(m::PREPARE_RESTART, Some("PrepareRestartParams"), Some("PrepareRestartResult"), S, S)`, `rpc(m::TASK_SUSPEND, Some("TaskIdParams"), Some("TaskControlResult"), …)`, `TASK_RESUME` same, `SUSPEND_ALL_TASKS`/`RESUME_ALL_TASKS` → `TaskBulkControlResult`; event rows for the two events; schema list entries for every new type.

- [ ] **Step 1: failing tests**: serde round trips (`restore_mode_accepts_true_false_and_suspended_only`, `prepare_restart_params_camel_case`, `lifecycle_draining_serializes_as_draining`, `task_suspended_event_shape`).
- [ ] **Step 2–4:** fail → implement → `cargo test -p coda-proto --features schema` (the catalog drift test will fail until Task 10 regenerates `catalog.json`; mark that expected — regenerate now with the temporary stand-in emitter described in Task 10 if convenient, without committing the emitter).
- [ ] **Step 5:** commit `feat(proto): planned-restart and task suspension wire types`.

### Task 6: Draining state and the turn gate (coda-serve)

**Files:** `coda-serve\src\state\mod.rs`, `host.rs`, `dispatch.rs` (RpcError factory), `coda-agent\src\scheduling\runtime.rs`.

- `EngineState::begin_draining(reason, deadline: DateTime<Utc>) -> Result<(), AlreadyDraining>`: sets `draining = Some(DrainInfo{started_at, deadline_at, reason, completed:false})`, lifecycle `Draining`, emits `event/lifecycle {lifecycle:"draining", initialized}`. `drain_completed()`; `is_draining()`; `begin_turn` refuses with a new `TurnRefusal::Draining`; `release_turn` keeps `Draining`; `project()` fills `drain` with `remaining_ms = max(0, deadline - now)`; shutdown transitions unchanged.
- `ClaimRefused::Draining` → `RpcError::restart_pending()` (`code -32016`, message `"restart pending"`); `PromptRunFailure::Draining`; `main_pump_step` maps it to `Wait`.
- `ScheduleRuntime::pause_launches(bool)` (AtomicBool checked in the due loop before `claim_and_launch`; paused → no launch, definitions untouched).
- `task/resume` / `session/resumeAllTasks` refuse with -32016 while draining (Task 7 wires them).

- [ ] **Step 1: failing tests** (host.rs tests):
  - `draining_refuses_new_prompts_with_restart_pending` — `begin_draining` via a test helper; `dispatch("session/prompt")` → `Err(code -32016, "restart pending")`; `session/getState` → `lifecycle == "draining"`, `drain.remainingMs > 0`; events include `event/lifecycle {lifecycle:"draining"}`.
  - `draining_still_lets_an_open_request_be_answered` — start a turn that raises a permission request (existing permission test pattern), begin draining, answer via `session/resolveRequest` → turn completes, lifecycle stays `draining`, a new prompt is still refused.
  - `steering_is_accepted_while_draining`.
  - runtime.rs: `paused_runtime_does_not_launch_due_definitions`.
- [ ] **Step 2–4:** fail → implement → `cargo test -p coda-serve draining` + `cargo test -p coda-agent scheduling::runtime` green.
- [ ] **Step 5:** commit `feat(serve): draining lifecycle that refuses new work with restart pending`.

### Task 7: task/suspend, task/resume, suspendAll/resumeAll + events

**Files:** `dispatch.rs` (trait methods + routes + FakeBackend/ErrorBackend stubs), `host.rs`, `task_events.rs`, `capabilities.rs` (`tasks.suspend` supported, `session.plannedRestart` supported).

- `task_suspend(TaskIdParams)` → `TaskControlResult` with reasons `notFound`, `notSuspendable` (non-subagent), `notRunning` (terminal); ok true otherwise (idempotent). `task_resume` → reasons `notFound`, `notSuspendable`, `notSuspended`, `notRunning`; -32016 while draining. `session_suspend_all_tasks` / `session_resume_all_tasks` → `TaskBulkControlResult{ok, count}` (resumeAll -32016 while draining).
- `TaskEventPublisher::on_suspended/on_resumed` publish the new events; `on_started` adds `resumed: true` when `snapshot.resumed`.
- `session/listTasks`: status `"suspended"`, `suspendRequested` when requested but not yet paused; filters per design.

- [ ] **Step 1: failing tests**: `task_suspend_and_resume_without_restart` (host with ScriptedClient driving a background subagent through `task` tool, gated probe tool; suspend → `event/taskSuspended` reason `requested`, listTasks shows `suspended`; resume → `event/taskResumed`, task completes); `task_suspend_rejects_shell_tasks`; `suspend_all_and_resume_all_counts`; dispatch routing tests in `dispatch.rs` for the four methods.
- [ ] **Step 2–4** → `cargo test -p coda-serve task_suspend suspend_all dispatch` green.
- [ ] **Step 5:** commit `feat(serve): task/suspend, task/resume and bulk suspension`.

### Task 8: Restart file model + prepareRestart orchestration

**Files:** create `coda-serve\src\restart.rs`; modify `host.rs`, `dispatch.rs`, `lib.rs`, `coda-agent\src\message\mod.rs`, `coda-agent\src\steering.rs` (non-destructive snapshot if not added in Task 4).

`restart.rs` model (serde camelCase, `RESTART_FILE_VERSION = 1`):
```rust
pub struct RestartFile { pub version: u32, pub coda_version: String, pub session_id: String,
  pub working_dir: String, pub prepared_at: DateTime<Utc>, pub reason: Option<String>,
  pub schedules: Vec<serde_json::Value /* ScheduledTask */>,
  pub messages: Vec<SavedMessage>, pub settings: SavedSettings, pub steering: Vec<String>,
  pub interrupted: Interrupted, pub tasks: Vec<String> }
pub struct SavedMessage { pub channel: String /* "user" | "main" */, pub cursor: u64, pub id: String,
  pub source: String /* scheduledTask|subagent|main|taskCompletion */, pub label: String,
  pub text: String, pub context: Option<String>, pub task_id: Option<String>,
  pub schedule_definition_id: Option<String>, pub schedule_name: Option<String>,
  pub idempotency_key: Option<String> }
pub struct SavedSettings { pub model: Option<String>, pub provider: Option<String>,
  pub effort: Option<String>, pub goal: Option<SavedGoal>, pub permission_mode: Option<String>,
  pub system_prompt: Option<String> }
pub struct SavedGoal { pub goal: String, pub max_duration: Option<String>, pub max_continuations: Option<i32> }
pub struct Interrupted { pub tasks: Vec<InterruptedTask>, pub requests: Vec<InterruptedRequest> }
pub struct InterruptedTask { pub task_id: String, pub kind: String, pub description: String,
  pub command: Option<String>, pub started_at: DateTime<Utc>, pub output_tail: String }
pub struct InterruptedRequest { pub kind: String, pub tool_name: Option<String>, pub question: Option<String> }
pub enum RestartFileCheck { Valid(RestartFile), Ignored(&'static str /* missing|unreadable|sessionMismatch|workingDirMismatch|expired */), Unsupported(u32) }
pub fn check(bytes: Option<&[u8]>, session_id, working_dir, now) -> RestartFileCheck
pub fn restart_note(file: &RestartFile, to_version: &str) -> String   // "[restart] …" text
```
`message\mod.rs`: `UserMessage`/`MainMessage` → `SavedMessage` conversion helpers (or a `pub fn snapshot_user_after(&self, cursor) -> Vec<UserMessage>` + `pub fn take_all_pending_main(&self) -> Vec<MainMessage>`), and `MessageSource::from_saved(kind, task_id, label, def_id, def_name)`.

`ServeHost::session_prepare_restart(p)`:
1. validate timeout (≤ 3_600_000 else -32602); if a previous prepare finished → return the cached result; if one is in progress → -32016.
2. `engine_state.begin_draining(reason, now+timeout)`; `task_manager.set_suspend_new(true)` + restart reason; `schedule_runtime.pause_launches(true)` (if services built); `task_manager.suspend_all()`.
3. Wait loop (poll every 100 ms or on `settle_signal`/`suspend_signal`/turn-release notify) until drained (definition in design table) or deadline.
4. If not drained at the deadline: snapshot running tasks (`list_with_output`) → `InterruptedTask` for every non-terminal non-suspended **non-subagent** task (shell `command = description`); snapshot `prompt_channel.registry().list()` → `InterruptedRequest` (`toolName` from `display.toolName`, `question` from `display.question` text); `steering = session.steering.recall_all()` texts; cancel the main turn (`current_cancel`); `stop_with_note(id, "planned restart")` for those tasks; `registry().fail_all(NoAnswerReason::Declined)`; wait ≤ 10 s for the turn slot to release; then settle window (`RESTART_SETTLE`, 30 s; tests override via a `#[cfg(test)]` field) for running subagents to suspend; remaining running subagents → recorded as `InterruptedTask{kind:"subagent"}` and `stop_with_note`.
   If drained before the deadline, still capture `steering` that is queued (normally empty).
5. Build `RestartFile` (schedules from `schedule_store.items()` serialized; messages from bus; settings from runtime/goal/permission; `tasks` = suspended subagent ids) and write every `SubagentCheckpoint` to `tasks_dir(session)` then the restart file via `write_private_atomic` (restart file last). `engine_state.drain_completed()`.
6. Return `PrepareRestartResult{ ok:true, restartFile: <absolute path>, drained, interruptedTasks, interruptedRequests, suspendedTasks }`. Engine stays draining.

- [ ] **Step 1: failing tests** (`restart.rs` unit + host.rs):
  - `check_ignores_missing_mismatched_expired_and_refuses_newer` (four cases + 24h boundary).
  - `restart_note_lists_steering_interrupted_command_and_versions` (snapshot the exact text).
  - `prepare_restart_writes_owner_only_file_with_all_sections` — host with schedule (`session/scheduleCreate`), goal (`session/setGoal`), model (`set_model_for_test` or `session/setModel`), notify_user message published on the host's bus, timeout 0 → file exists, parses, has 1 schedule, 1 message, settings.model/goal, `drained == true` (nothing running).
  - `prepare_restart_interrupts_a_long_running_command_and_saves_queued_steering` — ScriptedClient turn: `run_command` with a long sleep (`ping -n 30 127.0.0.1` on Windows / `sleep 30` unix via cfg), drainTimeoutMs 300, steer "also check X" while running → result `drained:false, interruptedTasks: 1`; file `interrupted.tasks[0].command` contains the command; `steering == ["also check X"]`.
  - `prepare_restart_records_declined_requests` — open permission request at timeout → `interrupted.requests[0] == {kind:"permission", toolName:"…"}`.
  - `prepare_restart_twice_returns_cached_result`, `prepare_restart_rejects_timeout_above_max`.
- [ ] **Step 2–4** → `cargo test -p coda-serve restart prepare_restart` green.
- [ ] **Step 5:** commit `feat(serve): session/prepareRestart drains and writes the restart file`.

### Task 9: Restore on initialize + `coda serve --restore`

**Files:** `dispatch.rs` (`InitParams.restore`), `host.rs` (initialize), `restart.rs` (apply helpers), `coda-agent\src\scheduling\scheduled_task_store.rs` (`pub fn restore(&self, task: ScheduledTask) -> bool` — inserts with its own id unless one exists), `coda-boot\src\serve.rs` (`--restore[=suspended]` → `CODA_SERVE_RESTORE`, `--session <id>` → `CODA_SERVE_SESSION`), `StartupOptions` (`restore`, `session_id`).

initialize flow with `restore` (`Resume`|`Suspended`, possibly defaulted from startup options; `sessionId` defaulted from `--session`):
1. Before `try_claim_initialization`: validate params (`restore` needs a `sessionId` → -32602), read `<id>.restart.json`; `check(...)`; `Unsupported(v)` → `Err(RpcError{code:-32017, message:"restart file format {v} is newer than this Coda supports (1)"})`.
2. Resume the transcript as today; if no transcript but `Valid` → accept the id with empty history.
3. `Valid(file)`: delete the file first (failure → summary `ignored_reason: "consumeFailed"`), then apply:
   - schedules: deserialize each, `next_run_utc = advance_recurring_past(def, now)` for interval/cron when `<= now`, `schedule_store.restore(task)`.
   - settings: model via `commit_config("model", …)` (+effort), effort, goal (`goal_params`), system prompt, permission mode unless `bypassPermissions` (→ `settings_not_restored`), provider compare.
   - history: `recover_interrupted_history` (make it `pub` if needed) then push `Message::user(restart_note(&file, coda_boot::version()))`, persist via `save_with_usage`, `engine_state.session_changed(...)`.
   - subagents: read each `tasks_dir/<id>.json` in `file.tasks` (skip unreadable ones, counting them as interrupted), sort by depth/owner order, `SubagentHost::restore(cp, mode == Suspended)` — this requires session services/subagent host: build services eagerly if a client is wired (same path as the first prompt); if no client is wired, keep checkpoints pending and restore them when services are first built. Remove the tasks dir after all are re-created.
   - after `initialization.finish()`: re-publish messages (user → `publish_user` with rebuilt source and same context/idempotency; main → `publish_main`), so `event/agentMessage` follows the initialize response.
   - ensure the schedule runtime runs (build services when a client is wired).
4. Response `restored: RestoredSummary`.

- [ ] **Step 1: failing tests** (host.rs; temp working dir):
  - **Acceptance scenario** `restart_round_trip_restores_schedules_messages_settings_and_reports_interruption`: engine A (ScriptedClient) — create two schedules (`every: "1h"`, `cron: "*/30 * * * *"`), set goal, set model, permission mode `bypassPermissions`, publish notify_user, start a prompt whose model calls `run_command` (long), steer, `prepareRestart{drainTimeoutMs: 300}`, `shutdown`. Rewind schedule `nextRunUtc` in the written file by 3 h (simulate a long restart window). Engine B (new host, same dir, CountingClient script that records requests) — `initialize{sessionId, restore:true}` → `restored.applied`, `schedules: 2`, `messages: 1`, `settings ⊇ ["model","goal"]`, `settingsNotRestored[0].name == "permissionMode"`; `scheduleList` shows both with original `runsStarted`, `nextRunUtc > now`, no scheduled run launched within 500 ms; `event/agentMessage` re-delivered with the same text and a new cursor; `session/getState` shows the model, permission mode `default`; goal params restored; then `session/prompt "continue"` → the recorded first request contains `[restart]` and the command text; the restart file is gone; a second engine C `initialize{restore:true}` → `applied:false, ignoredReason:"missing"`.
  - `restore_ignores_file_older_than_24h` and `restore_ignores_file_for_another_session` (summary reason; file left on disk).
  - `restore_refuses_newer_format_and_allows_retry` (-32017, then initialize without restore succeeds).
  - **Subagent acceptance** `subagents_suspend_on_prepare_and_continue_after_restore`: engine A main turn spawns a background subagent that spawns a nested one; nested one is in `run_command` (long); `prepareRestart{drainTimeoutMs: 300}` → both suspended, nested tool result is the interrupted text, two task files exist; engine B `restore:true` → `event/taskStarted{resumed:true}` for both ids, their next requests contain the prior conversation and `Interrupted by a planned restart`; `restore:"suspended"` variant → `listTasks` shows both `suspended` until `task/resume`.
  - `parent_task_wait_sees_suspended_child_then_gets_report` (no restart: suspend child, parent's `task_wait` tool result says suspended; resume child; parent receives report).
  - coda-boot: `serve_args_restore_and_session_set_env` (pattern of existing serve.rs tests).
- [ ] **Step 2–4** → `cargo test -p coda-serve restore restart_round_trip subagents_suspend` + `cargo test -p coda-boot serve` green.
- [ ] **Step 5:** commit `feat(serve): restore schedules, messages, settings and subagents on initialize`.

### Task 10: Docs, schemas, version

- `docs\serve-protocol.md`: new "Planned restart" section (prepareRestart params/result, draining semantics + -32016, drain rules, file location/permissions/format v1 with a full JSON example, subagent files, restore params/result/ignored reasons/-32017, `--restore`/`--session`, task suspension methods/events/statuses, task_wait behaviour); fix any "nothing survives a restart" statements to point at the new section.
- `docs\protocol\catalog.md`: method + event rows, error codes table, revise the in-memory-only paragraphs (schedules, notify_user, ask_main) to say they survive a *planned* restart only.
- Regenerate `docs\protocol\catalog.json` and `docs\protocol\schemas\*.json`: create an untracked stand-in `rust\crates\coda-proto\src\bin\emit_schemas.rs` that writes `coda_proto::schema::documents()` to `docs/protocol/schemas/<name>` (pretty JSON + trailing newline, matching existing files) and `catalog_document()` to `docs/protocol/catalog.json`; run `cargo run -p coda-proto --features schema --bin emit_schemas`; verify `git diff --stat` only touches expected files; delete the stand-in.
- `README.md`: `coda serve --restore` / `--session` in the serve flags list (if serve flags are listed).
- `version.json`: build 176 → 177.
- [ ] `cargo test -p coda-proto --features schema` green (catalog drift test). Commit `docs: planned restart protocol, catalog and schemas` and `chore: bump version to 0.1.177 for planned restarts`.

### Task 11: Full verification, review, PR

- [ ] `cargo build -p coda`; `cargo test -p coda-agent -p coda-serve -p coda-tui -p coda-proto -p coda-render -p coda-boot` (record counts); `cargo test -p coda-proto --features schema`.
- [ ] code-review subagent over `git diff main...HEAD`; fix real issues; re-run affected tests.
- [ ] push, PR with acceptance mapping, merge `--merge --delete-branch`, update main, remove worktree.

## Deviations from the request (to report)
- Error code -32016 instead of -32012 (taken by `STALE_EPOCH`).
- `deliveredCursor` optional param added (no ack channel exists; default re-delivers every retained notification).
- One-shot `at` schedules whose time passed during the restart fire once after restore.
- Scheduled runs are drained/interrupted rather than suspended (only `subagent` tasks suspend).
- Provider is not switched on restore if the new engine runs another provider (reported).
- Subagents still mid model call 30 s after the drain timeout are stopped and listed as interrupted.
