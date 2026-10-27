# Proposal: Non-blocking agent runtime — no tool call may hold a conversation hostage

- **Date:** 2026-10-27
- **Status:** Implemented. Decisions D1–D7 made (§10). **Phases 1, 2 and 3 implemented** (§8,
  §12, §13, §14).
- **Author:** Yury Opolev (design exploration, Coda)
- **Scope:** coda-agent (`agent/mod.rs` loop, `agent/tools.rs` batch executor,
  `tools/run_command.rs`, `tools/task.rs`, `tools/background_task.rs`, `tools/task_wait.rs`,
  `tools/task_recall.rs`, `tasks/task_manager.rs`, `subagents/host.rs`, `steering.rs`,
  `message/`), coda-serve (`host.rs` main-inbox pump, steer/interrupt methods, task events),
  coda-proto, coda-tui (status row, `/tasks` browser, key bindings, new `/btw`). README
  task-runtime section.
- **Verified against:** main @ `119e3a7` (line references below). Phase 1 was implemented on
  main @ `92753c1`; the references had drifted only slightly and the design held unchanged.
- **Evidence:** [evidence/2026-10-27-harness-research.md](evidence/2026-10-27-harness-research.md)
  (source-level study of Codex, Gemini CLI, OpenCode, Cline, Goose, Aider; docs/changelog study
  of Claude Code and GitHub Copilot CLI).

## 1. Summary

While Coda awaits a long tool call — a shell command, a foreground subagent, or `task_wait` —
the conversation is frozen. Messages the user types sit as `[pending]` until the tool finishes
or times out (default 600 s for shells, 300 s for `task_wait`, 30 min for the per-tool ceiling).
The same problem exists inside every subagent, because subagents run the same loop.

This proposal makes one rule hold for *every* agent loop, main and subagent alike:

> **Work may take hours; regaining control of a conversation must take seconds.**

It does so with four mechanisms, each already proven in at least one shipping harness:

1. **One execution path.** Every shell command and every subagent runs as a managed task in
   `TaskManager`. There is no separate "foreground" execution mode — foreground only means "the
   loop is currently waiting on this task".
2. **Bounded waits.** A tool call returns to the loop when the task completes, when a short
   *yield budget* expires, or *immediately* when an inbox event (user steering) arrives.
   Returning never kills the task; the model gets a task handle plus output so far.
3. **Pushed completions and parked agents.** Task completions are delivered to the owning
   agent's inbox; an agent with nothing to do *parks* (no model calls, no polling) and is woken
   by the next inbox event. `task_wait` and `task_recall` become unnecessary.
4. **User controls.** Background-all, send-now, a `/tasks` view with stop/background actions,
   and a `/btw` side question that sees live task status.

## 2. Motivating incident

A PR-review session ran one PowerShell command (fetch PR, run OWASP review) in the main loop.
The status row showed `Running tools 13:26 | … | 2 queued`; two user messages — *"for OWASP it
will be dry-run, and for deep-review? can you also run it?"* and *"are PR reviews running?"* —
stayed pending for the whole run. The user could neither ask a status question, nor add a second
review, nor change parameters, without killing the command.

Two distinct needs are visible here and the design treats them differently:

- *"Are PR reviews running?"* is an **observation**. It should be answerable instantly without
  touching the main conversation (§6.8).
- *"For OWASP it will be dry-run"* is **steering**. It must reach the agent promptly — but it
  cannot retroactively change a process that is already running. The agent must be told that,
  and offered a stop/restart (§6.9).

## 3. Current state (verified on 119e3a7)

| Concern | Today | File |
|---|---|---|
| Shell execution | `run_command` spawns PowerShell/sh and *awaits until exit*, timeout, or cancel. Not registered with `TaskManager`; no handle, no detach | `tools/run_command.rs:78-102, 214-305` |
| Shell timeout | 600 s default (`CODA_RUN_COMMAND_TIMEOUT`, per-call `timeoutSeconds`). On timeout the process tree is *killed* | `run_command.rs:24-28, 286-293` |
| Shell parameters | `command`, `timeoutSeconds` only — no background option | `run_command.rs:58-72` |
| Per-tool ceiling | 30 min for any tool; on expiry the tool is cancelled | `agent/mod.rs:1015`, `agent/tools.rs:462-500` |
| Batch execution | Strictly serial; steering is checked *between* tools only. A message arriving during a tool waits for that tool to finish; remaining tools are then skipped | `agent/tools.rs:1-5, 199-228, 300-325` |
| Foreground subagent | `task` registers a Foreground task and *awaits* the whole subagent run | `tools/task.rs:118-150` |
| Background subagent | `task_start` returns a task id immediately (subagents only) | `tools/background_task.rs:47-138` |
| Waiting for a task | `task_wait` blocks up to `timeoutSeconds` (default 300) | `tools/task_wait.rs:13, 75-93` |
| Completion → model | *Pull only.* Background completions go to a per-owner outbox (`enqueue_completion_if_background`) that the model must drain with `task_recall` | `tasks/task_manager.rs:490, 656-677`; `tools/task_recall.rs:39` |
| Loop injection seams | Steering (4b) and main-inbox drain (4c) at iteration top; comment: *"No TaskManager completion drain here — that is a separate, later seam."* | `agent/mod.rs:356-386` |
| Main-inbox pump | *Exists*: `MessageBus` items for the main context (`ask_main`, `notify_user`) start a turn automatically when the main agent is idle | `coda-serve/src/host.rs:2663, 4459-4600` |
| Steer / interrupt RPCs | `session/steer` (enqueue to inbox while a turn is active), `session/interrupt` (cancel the turn token → kills the running shell) | `host.rs:2988-3045` |
| Subagent concurrency | `MAX_CONCURRENT_SUBAGENTS = 20` | `subagents/mod.rs:46` |
| Docs drift | README describes `run_in_background`, `task_background`, `task_wait` default 600 / max 1800 — none of which exist in the Rust tool registry | `README.md:88-104, 330`; `tools/mod.rs:104-155` |

Three facts shape the design:

- **The building blocks exist.** `TaskManager` (ids, status, bounded output ring, logs, outbox,
  subscriptions), `SteeringInbox`, `MessageBus`, and the main-inbox pump already implement most
  of what is needed. The gap is wiring, not new infrastructure.
- **The block is the await, not the work.** The loop awaits the tool future directly, so it
  cannot observe anything else until the future resolves.
- **Subagents share the loop.** Any fix applied only to the main session would leave every
  subagent with the same problem; the fix must live in the shared loop and tool runtime.

## 4. How other harnesses handle this

Full citations in the evidence file. Summary:

| Harness | Long shell | User message while a tool runs | Completion delivery | Manual background | Side question |
|---|---|---|---|---|---|
| **Codex CLI** (source) | `exec_command` yields after `yield_time_ms` (default 10 s, max 30 s) with partial output + session_id; process keeps running, *no kill timeout*; up to 64 PTY sessions, survive turn end and Esc | Enter = steer, injected at next sampling boundary (≤ ~30 s because tools yield); Tab = queue | Subagents: pushed to parent mailbox. Shells: *not pushed* (model polls via `write_stdin`) | `/ps`, `/stop` | `/side` (alias `/btw`): ephemeral fork, told not to mutate |
| **Claude Code** (docs) | Foreground default; at timeout (2 min default) the command is *moved to background*, not killed | Queued, delivered after current tool calls within the turn; *Send now* backgrounds eligible running work and delivers immediately | Pushed (`<system-reminder>`), mid-turn and between turns | Ctrl+B | `/btw`: no tools, not in history, can fork to a subagent |
| **Copilot CLI** (docs + observed) | `initial_wait` then partial output + shell id; `detach` option | Enter = steer; *steering interrupts shell waits without stopping the command* (1.0.74) | Pushed automatically (0.0.422) | Ctrl+X B, `/tasks` | `/ask` (`/btw`): no tools, not in history |
| **OpenCode** (source) | `background` param; foreground killed at 2 min | Durable inbox, per-item steer/queue | *Pushed* as synthetic message; tool text: *"DO NOT poll… you will be resumed"* | `backgroundAll()` + synthetic "moved to background" note | `Session.generate` (history-free) |
| **Gemini CLI** (source) | `is_background`; inactivity timeout (300 s) *kills* | Queue by default; steering experimental | Optional inject/notify; *default silent* | Ctrl+B | none |
| **Cline** (source) | "Proceed While Running" (user or auto after 300 s) | Steer interrupts model streaming; tools finish | Not pushed (model reads log) | button | none |
| **Goose / Aider** | Kill on timeout / blocking REPL | Steer between turns / none | — | — | — |

Lessons adopted:

1. **Bound the wait, not the work** (Codex). A tool that always yields within ≤ 30 s guarantees
   steering lands quickly *without* a background timer; the process keeps running.
2. **User input must cut a wait short** (Copilot 1.0.74, Claude "send now", Codex `wait_agent`).
3. **Push completions; forbid polling** (Claude, OpenCode). Harnesses that don't push (Gemini
   default, Cline, Codex shells) force the model to poll or forget.
4. **Be predictable to the model.** Copilot *removed* its 60 s auto-backgrounding of sync `task`
   calls (1.0.35); Claude and Codex start subagents in the background unconditionally. A tool
   should not return "sometimes a result, sometimes a handle" based on a long, invisible timer —
   so the yield budget is short, documented in the tool description, and stated in every handle
   result.
5. **Ending a wait never kills work; killing is explicit** (Codex `Op::Interrupt` vs `/stop`).
6. **Side questions are separate and history-free** (all three majors).

## 5. Design principles

- **P1 — Universal.** One loop, one tool runtime, one task model for the main session and every
  subagent at every depth. No main-only special cases except "the main agent never finishes".
- **P2 — Bounded waits.** No await in an agent loop may outlive `min(yield budget, next inbox
  event)` while a task is behind it.
- **P3 — Waiting ≠ owning.** Stopping a wait (yield, steering, background) never stops the work.
  Stopping work is an explicit action.
- **P4 — Push, never poll.** Every task completion reaches its owner's inbox exactly once.
- **P5 — Single writer per conversation.** An agent's history is mutated only by its own loop;
  user messages, completions and permission answers are serialized through its inbox.
- **P6 — Sequencing preserved.** No automatic parallelism of mutating tools; detaching a tool
  skips the rest of the batch so the model re-plans with fresh information.

## 6. Design

### 6.1 One execution path: every long-running tool is a task

Introduce a small seam in the tool runtime:

```rust
/// A tool whose work can outlive the call that started it.
pub enum ToolStart {
    /// Finished synchronously (read_file, edit_file, …).
    Done(ToolResult),
    /// Work registered in TaskManager; the loop decides how long to wait.
    Task { task_id: String, handle: Arc<ManagedTask> },
}
```

- `run_command` registers a `TaskKind::Shell` task, spawns the process on the runtime, streams
  stdout/stderr into the task's output ring and log, and returns `ToolStart::Task`.
  Process-tree kill, output caps and secret redaction move from the private executor into the
  task.
- `task` and `task_start` both register a `TaskKind::Subagent` task and return
  `ToolStart::Task`. `task_start` remains as an alias for compatibility (D3).
- **Generic fallback:** any other tool (notably long MCP calls) can be wrapped by the executor —
  spawn its future as a task — so detachability is a runtime property, not something every tool
  must implement. Tools that must complete inline (file edits, `ask_user_question`,
  `exit_plan_mode`) declare `detachable() == false`; they are short by construction.

The kill-on-timeout behaviour of `run_command` is removed. The per-call `timeoutSeconds`
becomes an optional *work deadline* for the task (kill after N seconds), distinct from the wait
budget.

### 6.2 Bounded wait: the yield contract

The batch executor replaces the direct await with:

```rust
tokio::select! {
    r = task.wait_for_completion()   => Inline(result),
    _ = sleep(yield_budget)          => Yielded,     // budget expired
    _ = agent_inbox.notified()       => Yielded,     // user/parent steering arrived
    _ = user_background.notified()   => Yielded,     // "background now"
    _ = cancel.cancelled()           => Interrupted, // §6.7 semantics
}
```

| Outcome | Tool result the model sees | Task |
|---|---|---|
| Completed in budget | Normal result (exit code + output), exactly as today | Removed from the "awaiting" set |
| Yielded | `Still running as task <id> (started 25s ago). Output so far: … You will be notified when it finishes; do not poll or sleep.` | Keeps running, now owned by the agent's background set |
| Interrupted | `Interrupted by the user.` | Per §6.7 |

Defaults (D1): shell yield budget **20 s**; model may request `waitSeconds` in **[0, 30]**
(0 = "don't wait", for known long jobs such as reviews, builds, servers). Subagents: yield
budget **0** — always return a handle immediately (lesson 4). The hard upper bound of 30 s is
what makes P2 hold even if the model asks for more.

**Batch rule:** when a tool yields, the remaining calls in the batch are skipped with
`Skipped: an earlier tool is still running as task <id>; re-plan.` — the same mechanism the
steering pre-empt already uses (`agent/tools.rs:199-228`). This preserves P6: a `cargo test` the
model planned after `cargo build` never runs against an unfinished build.

### 6.3 Agent inbox and the parked state

Every agent — main or subagent — owns an **inbox** that unifies today's `SteeringInbox` and the
main `MessageBus` lane, with typed items:

| Item | Source | Delivered as |
|---|---|---|
| `UserMessage` / `ParentMessage` | user (main) / `task_send` (subagent) | user-role message, as today's steering |
| `TaskCompleted { task_id, status, summary, output_ref }` | `TaskManager` | data block, explicitly marked as tool output, not instructions |
| `PermissionAnswer`, `AskMainReply` | UI / parent | as today |

Agent states:

```text
            inbox event                      model ends turn
  PARKED ───────────────▶ RUNNING ─────────────────────────────▶ PARKED   (has live child tasks, or is main)
     ▲                       │                                  ▶ DONE     (subagent with no live child tasks)
     └───────────────────────┘ (no events: zero model calls)
```

- **Running:** the loop drains the inbox at the top of every iteration (generalizing step
  4b/4c) and the yield `select!` wakes on it mid-tool.
- **Parked:** no model call is in flight; a waker (generalized main-inbox pump,
  `host.rs:2663`) starts the next turn when an item arrives. Parking costs nothing; this
  replaces polling and `task_wait`.
- **Coalescing:** a parked agent waits a short debounce (≈ 2 s) after the first wake-up item so
  a burst of child completions produces one turn, not N.

### 6.4 Completion delivery

`TaskManager::complete/fail/stop` already compute the owner (`outbox_key(parent_id)`). Instead
of (or in addition to) the pull outbox, they post `TaskCompleted` into the *owner agent's
inbox*:

- Exactly once per task (dedupe if the loop observed the completion inline within its yield).
- Includes failure and stop (the C#-era bug where only Complete notified must not recur).
- Content: status, exit code, last N lines (capped, e.g. 4 KB), and the task log path for the
  rest. Wrapped as data (cf. Gemini's `<background_output>` framing) to blunt prompt injection
  from command output.
- User-visible: the TUI shows the completion line in the transcript and updates `/tasks`.

### 6.5 Subagent lifecycle (the universal part)

A subagent is the same actor as the main agent, with one different terminal rule:

- **The main agent never finishes**; it parks between turns.
- **A subagent finishes when it ends a turn with no live child tasks.** Its final message is its
  report, posted as `TaskCompleted` to its parent's inbox.
- **If it ends a turn with live children, it parks** and is woken by their completions — so a
  subagent orchestrating its own shell commands or children never blocks either, and the same
  mechanism works at every depth.
- **Steering a subagent** (`task_send`, or the user from `/tasks`) goes to its inbox and cuts its
  current wait short exactly like the main session.
- **Limits:** max lifetime and max parked time per subagent (D5: 2 h / 30 min). On expiry it is
  woken once with a "deadline reached" item to produce a partial report, then stopped.
- Children of a stopped subagent are stopped (existing task-tree stop), unless the parent
  detaches them explicitly (out of scope).

### 6.6 `task_wait`, `task_recall`, `task_output`

- `task_recall` becomes redundant (completions are pushed); keep as a no-op-compatible alias for
  one release, then remove (D3).
- `task_wait` is reduced to "attach to an existing task with the normal yield contract": capped
  at 30 s, cut short by inbox events, never kills. Its description tells the model that ending
  the turn is the preferred way to wait.
- `task_output` / `task_peek` remain for reading progress on demand.

### 6.7 User controls (TUI and `coda serve`)

| Action | Key / command | Effect |
|---|---|---|
| **Steer** (default Enter while busy) | Enter | Inbox item; cuts the current wait short; delivered at the next boundary (≤ yield budget, usually < 1 s) |
| **Queue** | existing queue key | Delivered after the turn ends |
| **Background now** | Ctrl+B (D6) and `b` in `/tasks` | Every task the loop is awaiting yields immediately; loop continues |
| **Send now** | Ctrl+Enter (D6; Ctrl+G fallback) | Background now + deliver queued messages immediately |
| **Interrupt turn** | Esc | Cancel the model turn. Tasks the loop was *awaiting in the foreground* are killed (preserves today's expectation that Esc stops the runaway command); already-backgrounded tasks keep running (D4) |
| **Stop a task / all tasks** | `/tasks` `x`, `/stop` | Kill task tree(s) |
| **Observe** | `/tasks`, status row `N tasks running` | Live list with output tail, owner agent, age |

Serve protocol additions: `session/backgroundAll`, `task/background {taskId}`,
`task/stop {taskId}`, `session/stopAllTasks`, `session/askAside` (§6.8); events
`event/taskStarted`, `event/taskOutput` (throttled), existing `event/taskCompleted` extended
with owner agent id.

### 6.8 `/btw` side questions

- Runs on a separate, ephemeral request concurrent with the main loop; never added to history.
- Context: the main conversation so far *plus a live `TaskManager` snapshot* (ids, owner, status,
  age, last output lines). This is what lets it answer "are PR reviews running?" truthfully —
  without the snapshot, a no-tool side question can only guess.
- No tools (Claude/Copilot model). Optional follow-up: "promote to steering" sends the question
  to the main inbox.

### 6.9 Safety and correctness

- **Steering cannot rewrite a running process.** When a steering item arrives while tasks run,
  the injected text is prefixed with the live task list so the model sees that e.g. the OWASP
  command is *already running with posting enabled*, and must decide to stop/restart rather than
  assume "dry-run" took effect. Skills that perform external side effects should split
  *generate* and *post* into separate steps (guidance, not runtime).
- **Concurrent mutation.** Backgrounded shells and new turns may touch the same working tree.
  Runtime does not serialize them; the model is told which tasks are running (above), and the
  existing `git_worktree` tool is the recommended isolation for parallel mutating work (D7).
- **Resource bounds.** Max live shell tasks (e.g. 32, mirroring Codex's 64 with LRU of exited),
  per-task log cap, `MAX_CONCURRENT_SUBAGENTS` unchanged. All tasks still die with the process.
- **Permissions.** Background subagents keep routing approval prompts to the UI (existing queued
  decisions, `app/engine.rs:152, 314`). A parked agent waiting on approval is just another inbox
  wait.
- **Prompt injection.** Completion payloads are framed as data; truncated; never executed as
  instructions.

## 7. Behaviour walkthrough (the incident, redesigned)

```text
t=0     model: run_command("…owasp-item…")          → task 7, yields at 20 s
t=20s   tool result: "Still running as task 7 …"     → model ends turn → main PARKED
t=40s   user: "for OWASP it will be dry-run, and deep-review?"
        → main RUNNING; sees live tasks: task 7 (OWASP, posting enabled, 40 s)
        → replies: "OWASP is already running with posting. Stop and restart as dry-run?"
          and starts deep-review as task 8 (waitSeconds: 0)
t=55s   user: /btw are PR reviews running?
        → side answer from snapshot: "task 7 OWASP 55 s, task 8 deep-review 15 s"
t=14m   task 7 completes → TaskCompleted → main wakes, summarizes findings
```

## 8. Implementation phases

Each phase is independently shippable and leaves the system consistent.

1. **Phase 1 — unblock the main session (fixes the incident).** `run_command` → task + yield
   contract (§6.1–6.2); inbox-cut waits; batch skip rule; push completions into the existing
   main-inbox pump (§6.4); Background-now / Send-now; `/tasks` actions; README corrected.
   **Implemented** — see §12.
2. **Phase 2 — universal.** Per-agent inbox for subagents; parked state + generalized waker;
   subagent lifecycle (§6.5); `task` returns handles; `task_wait`/`task_recall` reduced (§6.6);
   coalescing; lifetime limits. **Implemented** — see §13.
3. **Phase 3 — observation.** `/btw` with task snapshot (§6.8); throttled `event/taskOutput`;
   status-row task count; steering prefixed with live tasks (§6.9). **Implemented** — see §14.

## 9. Testing

Deterministic tests with fake slow tools and a paused tokio clock:

- Steering during a 60 s tool reaches the model within one iteration; the tool's process is still
  alive afterwards; its completion arrives later exactly once and wakes the parked agent.
- Yield at budget returns a handle and skips the rest of the batch; a short command still returns
  inline with an unchanged result shape.
- `waitSeconds` is clamped to [0, 30]; subagents always return handles.
- Esc kills awaited-foreground tasks only; backgrounded tasks survive; `/stop` kills all.
- A subagent with a live child parks, wakes on the child's completion, and finishes; its report
  reaches the parent; nesting depth 2 works end to end.
- Burst of 5 completions → one wake-up turn (coalescing).
- Failure and stop produce `TaskCompleted` (regression for the "only Complete notifies" bug).
- Windows: process-tree kill on stop and on Coda exit; output-drain without pipe deadlock (re-use
  `run_command.rs` tests). Note the known flaky
  `ShellTaskTests.StartShellBackground_Stop_KillsProcessTree` pattern — tests must not race on pid
  files.
- Serve protocol: `session/backgroundAll`, `task/stop`, `session/askAside` contract tests; a
  client that never reads `event/taskOutput` cannot grow engine memory unbounded.

## 10. Decisions

All decided on 2026-10-27.

- **D1 — Yield defaults. Decided:** shell yield budget **20 s** by default; the model may pass
  `waitSeconds` in **[0, 30]** (clamped; `0` returns the handle immediately).
- **D2 — Subagents always asynchronous. Decided:** yes (yield 0), for predictability. Phase 2.
- **D3 — Tool surface. Decided:** keep `task_start` and `task_recall` as deprecated aliases for
  one release; `task_wait` semantics change as in §6.6 (Phase 2).
- **D4 — Esc semantics. Decided:** Esc/interrupt kills only the tasks the loop was awaiting in
  the foreground; already-backgrounded tasks keep running; `/stop` kills all.
- **D5 — Subagent limits. Decided:** max lifetime **2 h**, max parked time **30 min** (Phase 2).
- **D6 — Key bindings. Decided:** **Ctrl+B** = Background now, **Ctrl+Enter** = Send now. Because
  many terminals cannot report Ctrl+Enter distinctly (they send plain Enter or Ctrl+J), **Ctrl+G**
  is accepted as an equivalent Send-now chord and documented.
- **D7 — Concurrent mutation. Decided:** advisory model in v1 (no working-tree write lock).

## 11. Out of scope

- Tasks surviving process exit / detached processes (Copilot `detach`). Tasks remain
  process-local.
- Automatic parallel execution of tool calls (Codex read/write-lock runtime). Compatible future
  work; not required to fix responsiveness.
- PTY/interactive stdin for shells (Codex `write_stdin`).
- Cross-session or remote task control.

## 12. Phase 1 implementation notes

What shipped, and where it deliberately differs from the sketches above:

- **Seam.** Instead of a new `ToolStart` enum on the `Tool` trait, `coda-tool` gained
  `ToolControl::RunningTask { task_id, wait }` (`ToolResult::running_task`). `run_command` returns
  it after registering the shell task; the batch executor (`agent/tools.rs`,
  `await_running_task`) owns the yield `select!`. Tools without a `TaskManager` in their context
  (direct callers, unit tests) still run inline, unchanged. The generic fallback for other tools
  (MCP) is not implemented yet.
- **Shell task.** `run_command` registers a `Shell` task (Foreground while awaited), streams
  stdout/stderr (UTF-8 boundaries carried across reads) into the ring and redacted log, attaches a
  process-tree kill delegate, and performs exactly one terminal transition. The full tool result
  (`exit code: N` + capped output, unredacted, identical to before) is handed to the awaiting call
  through a one-shot slot on `ManagedTask`; the registry only stores the `exit code: N` summary, so
  raw output never reaches `session/listTasks`. `timeoutSeconds` (or a positive
  `CODA_RUN_COMMAND_TIMEOUT`) is the work deadline; there is no default deadline.
- **Wait.** Ends on completion, budget, steering arrival (`SteeringInbox::wait_for_pending`),
  `ManagedTask::request_background` (Background now), or turn cancel. Yielding promotes the task
  to Background atomically; a task that finished in that same instant is reported inline.
  Interrupt stops only the awaited task (D4).
- **Who yields.** The main conversation yields by default. Subagent contexts (and headless
  `coda run`, via `CODA_RUN_COMMAND_YIELD=0`) wait for the command unless the model passes
  `waitSeconds`, because nothing can push a completion to them yet (Phase 2); their wait is still
  bounded by the per-tool ceiling (by yielding, never killing) and cut short by steering or
  Background now. Their handle text points at `task_wait` / `task_peek` instead of promising a
  notification.
- **Push.** `TaskManager` gained a `TaskCompletionListener` called once per terminal transition;
  `MainInboxCompletionPush` (installed by `coda-serve`) publishes background `Shell` tasks owned
  by the main agent onto the main inbox as `source: "taskCompletion"` with idempotency key
  `completion:<taskId>`. The injected text is a `[task-completed]` header plus a
  `<task_output>…</task_output>` data block (status, exit code, log path, ≤ 3,000-char redacted
  tail; a literal closing tag in the output is escaped). The existing pump wakes an idle main
  agent; a busy turn picks it up at its next iteration boundary. Subagent completions remain
  pull-only via `task_recall` until Phase 2. **Deviation:** stops made by the *operator*
  (`/stop`, `task/stop`, `session/stopAllTasks`) are not pushed — otherwise an idle agent would
  start a turn right after being told to stop and could restart the work. Stops by the agent's own
  `task_stop`, failures and completions are pushed. Engine `shutdown` now stops every task
  (`TaskManager::shutdown`), since backgrounded commands are no longer owned by any turn.
- **Controls.** Serve: `session/backgroundAll`, `session/stopAllTasks`, `task/background`,
  `task/stop` (initialization-gated, schemas + catalog). Background-now applies to shell tasks
  only in Phase 1 (`notBackgroundable` for a foreground subagent, which `task` still awaits
  directly). TUI: Ctrl+B, Ctrl+Enter while busy /
  Ctrl+G, `/tasks` `b` / `x`, and `/stop` (now its own command: interrupt + stop all; it used to be
  an `/interrupt` alias).
- **Not in Phase 1:** steering prefixed with the live task list (§6.9) and `event/taskStarted` /
  `event/taskOutput` (Phase 3); a cap on live shell tasks; `task_wait` still blocks up to its own
  timeout (Phase 2 reduces it).

## 13. Phase 2 implementation notes

Implemented on main @ `8489df6`. What shipped, and where it deliberately differs from the
sketches above:

- **Inbox.** Rather than a new inbox type, `SteeringInbox` gained typed items
  (`InboxItemKind::{Message, TaskCompleted, Deadline}`). Every subagent — and every scheduled
  run, which executes through the same host — gets its own inbox, attached to its
  `ManagedTask` (`attach_steering` / `steering()`), so `task_send` (and the new operator RPC)
  can reach it. Runtime items are accepted even when the inbox is sealed (the seal protects a
  user's words after the last delivery boundary; runtime items are re-checked by the owner
  after sealing). The main conversation keeps its existing pair — the session steering inbox
  plus the `MessageBus` main lane — so `ask_main`, permission answers and the serve observers
  are untouched. `PermissionAnswer` / `AskMainReply` were not turned into inbox items: they are
  already answered in place by their own seams.
- **Only messages cut waits.** A user/parent message cuts a running tool's wait short and
  pre-empts the rest of a batch, exactly as steering did. A pushed completion does **not**: it
  waits for the next iteration boundary, so an unrelated child finishing never skips planned
  work. (`wait_for_pending` / `has_pending` mean "messages"; `wait_for_any` / `has_any` mean
  any item.)
- **Push.** `TaskManager` posts a finished task's completion into its owner's inbox itself
  (`push_to_owner_inbox`) when the owner is a running agent task with an inbox — exactly once
  per terminal transition, skipped for a task the loop observed inline (still foreground), and
  including failure and every stop. **Deviation:** operator stops *are* delivered to a
  subagent owner (with `note: stopped by the user`), because the subagent may be parked on
  that very task; the main agent still does not get them (Phase 1 rule).
  `MainInboxCompletionPush` now also covers main-owned **subagent** tasks; their body carries
  the final report (bounded to the main inbox's 4,000 characters, with a pointer to `task_get`
  for the rest; 12,000 for a subagent owner). One formatter (`completion_frame`) frames both.
  A task is *settled* once its completion is in the owner's inbox; only then does it stop
  counting as live, which closes the race between "the child finished" and "its report
  arrived". The pull outbox remains for the deprecated `task_recall`, which now returns only
  completions that could not be pushed (e.g. a full main inbox).
- **Parked state.** `AgentLoopBuilder::with_park_policy` (`agent/park.rs`). At a natural stop
  a loop with a policy checks its inbox and its live tasks (`TaskManager::live_children`): an
  item → another turn; live tasks → **park** (no model call; wakes on an inbox item, a task
  settling, a deadline, or cancel); neither → finish. After the first wake-up item it waits a
  2 s debounce so a burst coalesces into one turn. The interactive main conversation has no
  policy: between turns it is parked by the serve main-inbox pump, as before.
- **Subagent lifecycle.** A subagent finishes when a turn ends with nothing of its own left
  running; **its report is its final assistant message** (previously all text it ever
  streamed). When it ends for any reason, everything it started that is still running is
  stopped (`TaskManager::stop_descendants`). A background subagent now runs under its own
  task's cancellation token, so `task_stop`, `/stop` and shutdown actually stop it (a
  `task_start` subagent used to ignore them) and it ends `stopped`, not `failed`.
- **Limits (D5).** 2 h lifetime and 30 min parked without news. Either injects one
  `[deadline]` message (naming the tasks that will be stopped) and the agent finishes at its
  next natural stop. **Addition:** an agent that keeps working more than 5 min after its
  deadline is aborted (the parent receives a failure), so a model that ignores the notice
  cannot run unbounded. The lifetime is checked at every iteration boundary, so it also
  applies to an agent that never parks.
- **`task` is always asynchronous (D2).** `task` and the deprecated `task_start` share one
  implementation and return `Started subagent task <id> …` immediately. They do *not* use the
  `RunningTask` yield seam: that would skip the rest of the batch, and several `task` calls in
  one response must all start. A context with a factory but no task manager (direct callers,
  tests) still runs the subagent inline. A parked subagent keeps its concurrency slot.
- **`task_wait` (§6.6)** returns a new control, `ToolControl::AttachTask`: the executor waits
  at most 30 s (requests are clamped), ends early on a message, returns the task's final status
  if it finishes, and never stops, promotes or otherwise touches the task, nor skips the batch.
  Its description says ending the turn is the preferred way to wait. When the *owner* is
  attached and the task ends, the completion is claimed by that wait and not pushed as well
  (exactly once). Terminal tasks are pruned from the registry only once settled, so a parked
  owner never loses sight of a child that has ended but not yet reported.
- **Who yields.** `run_command` now yields in every context that receives completions: the
  main conversation and any agent running as a task with an inbox. Only contexts without one
  (e.g. an agent-type hook's unregistered run) still wait for the command. The Phase 1
  `CODA_RUN_COMMAND_YIELD=0` override still exists but `coda run` no longer sets it.
- **Headless `coda run`.** It sets `CODA_RUN_WAIT_FOR_TASKS=1`; the engine then gives the main
  loop `ParkPolicy::headless_main()` (30 min parked limit, no lifetime limit). The single turn
  parks until the tasks it started have settled — woken by the main `MessageBus` lane, which
  gained an arrival `Notify` — and then lets the model finish, so the engine never exits with
  work still running unreported.
- **Steering a subagent (§6.5).** `task_send` reaches the subagent's inbox (it previously
  always failed: no inbox was ever attached). For the user: serve `task/send {taskId, text}`
  (initialization-gated; reasons `notFound`, `notRunning`, `notAgent`, `notAccepting`,
  `emptyText`), the TUI command `/send <task-id> <message>`, and `m` in `/tasks`, which closes
  the browser and pre-fills `/send <task-id> `.
- **Not in Phase 2:** the generic task wrapper for other long tools (MCP); `event/taskStarted`
  / `event/taskOutput`; `/btw`; steering prefixed with the live task list (all Phase 3); a cap
  on live shell tasks.

## 14. Phase 3 implementation notes

Implemented on main @ `ddc971f`. What shipped, and where it deliberately differs from the
sketches above:

- **`/btw` side questions (§6.8).** Serve `session/askAside {question}` →
  `{ok, answer?, reason?, error?}` (initialization-gated; refusals `emptyQuestion`,
  `noProvider`, `modelError`). One ephemeral request on the session's client with **no tools**,
  no effort setting and a 2,048-token cap, abandoned after 120 s. It never claims the turn slot
  and never writes the history, so it works while a turn runs. **Deviation:** the conversation
  is sent as a bounded *transcript* (newest ~48,000 characters of text, tool calls and result
  excerpts, plus the turn in flight from the live projection) inside one user message, rather
  than the replayed provider messages — a tool-less request may not carry tool-use blocks, and
  this keeps the request small however long the session is. The live task table
  (`tasks::live_brief::task_snapshot`: running tasks first, then the 8 most recently finished,
  each with kind, owning agent, status, age and the last 5 redacted output lines) follows it.
  TUI: `/btw <question>` (alias `/ask`) sends the request from its own task — neither the event
  loop nor the turn waits — and opens a right-hand side panel (`surface::aside`, built from the
  widget toolkit's `Form`/`StaticText`) that fills in when the answer arrives; `p` promotes the
  question to an ordinary message (steering while busy, a new turn otherwise), `Esc` closes. An
  answer that arrives after the panel was closed is printed as a transcript note marked as a
  side answer; it is local to the screen and never persisted.
- **Task events.** `TaskManager` gained a `TaskEventObserver` (start, output, terminal);
  coda-serve's `TaskEventPublisher` turns it into `event/taskStarted {taskId, kind,
  description, ownerTaskId?, mode}`, `event/taskOutput {taskId, text, truncated}` and an
  extended `event/taskCompleted` (+ `ownerTaskId`, `kind`, `mode`; previously declared but
  never emitted by the Rust engine). Output is throttled to **4 frames/s per task** and
  **2,000 characters per frame** (newest kept). The publisher holds no text, only a cursor per
  running task into the task's 16 KiB redacted tail; while the connection is congested — the
  transport's write loop reports a write blocked ≥ 1 s or > 256 queued frames
  (`bus::OutboundHealth`) — nothing optional is sent and nothing accumulates, so a client that
  never reads cannot grow engine memory; the first frame after recovery says `truncated`. The
  last output of a task precedes its completion.
- **Status row.** The TUI keeps the set of running task ids from those events and shows
  `N tasks running · /tasks`; task output only touches the status row (no transcript
  relayout). The "Task completed: …" notice is now limited to background completions of the
    main agent's own tasks, since a foreground task's end is already its tool result and a
    subagent's tasks are reported to that subagent. The never-read client-side outcome table that
    would otherwise have grown with every completion was removed.
- **Steering prefixed with live tasks (§6.9).** When a steering message is delivered at an
  iteration boundary and the agent (main or subagent) has running tasks it started, the
  injected text starts with a `[live-tasks] … [/live-tasks]` block (id, kind, description,
  age; at most 10 listed) telling the model those tasks keep their original parameters and
  must be stopped and restarted to change. **Addition:** a new prompt to the main agent is
  prefixed the same way (as a separate leading text block), because in the motivating incident
  the main agent was already parked when the user wrote. Pushed completions and deadlines alone
  are not prefixed; with nothing running, messages are delivered unchanged. The prefix is part
  of the history (it is what the model saw), so a rehydrated transcript shows it.
- **Shell cap.** At most **32** running shell commands (`DEFAULT_MAX_LIVE_SHELL_TASKS`); a
  further `run_command` is refused with a tool error naming the cap and how to free a slot.
  **Deviation from "LRU of exited":** finished commands never count (and are already pruned by
  the existing 256-terminal-task retention), so there is nothing to evict; a running command is
  never evicted.
- **Long MCP calls as tasks (generic fallback, §6.1).** `Tool::detachable()` (default `false`;
  `true` for `McpTool`). When the calling context receives pushed completions and has a task
  manager, the batch executor registers a `TaskKind::Tool` task, runs the call on its own task
  (the per-tool ceiling is its work deadline) and awaits it under the normal yield contract
  (20 s, cut short by steering / background-now, Esc stops it). A call that finishes in time
  returns its result unchanged; a longer one keeps running and its result (≤ 12,000
  characters) is pushed as the task's report. `tool` tasks can be backgrounded like shells.
  Other tools still run inline.
- **Not in Phase 3:** seeding the status-row count from `session/listTasks` after the client
  attaches to an engine that already has running tasks (the TUI always starts its own engine,
  so it sees every start); a configurable shell cap.
