# Coda Cron Scheduler Runtime Design

**Date:** 2026-07-21

## Summary

Coda already exposes `schedule_create`, `schedule_list`, and `schedule_delete`, persists
schedule definitions under the project `.coda` directory, and can calculate cron due
times. It does not currently execute those definitions.

This change adds a host-neutral in-process scheduler owned by `CodaSession`. While an
interactive Coda or `coda serve` process is open, due definitions start isolated managed
background agent tasks. Executions appear in `/tasks`, use the current session's model,
tools, working directory, MCP/LSP services, live permission mode, logs, cancellation, and
child-task capabilities. Definitions persist and resume the next time Coda starts in the
same project.

The first release deliberately does not add a daemon or OS scheduler. Nothing executes
while every Coda process for the project is closed.

## Goals

- Run a prompt at a recurring interval, at a specific time, or on a five-field cron rule.
- Start each firing as a managed background agent task visible through the shared
  `TaskManager` and `/tasks`.
- Give interactive and serve modes identical non-UI scheduling behavior.
- Persist definitions across process restarts.
- Run each overdue definition once on startup rather than replaying every missed tick.
- Prevent overlapping executions of the same definition while retaining one coalesced
  pending run.
- Use the session's live permission mode and existing permission prompt path.
- Surface concise lifecycle notices while keeping full output in task output and logs.
- Preserve backward compatibility with existing persisted cron definitions.

## Non-goals

- Running schedules while Coda is completely closed.
- A system service, daemon, Windows Task Scheduler, launchd, or systemd integration.
- Second-level scheduling; the minimum interval and cron precision is one minute.
- A dedicated schedule-management TUI in the first release.
- Pause, resume, or edit tools; callers can delete and recreate a definition.
- Replaying every occurrence missed while Coda was closed.
- Concurrent executions of one definition.
- Inserting full scheduled-run output into the main conversation.

## User-visible behavior

### Tool surface

The existing tools remain:

- `schedule_create`
- `schedule_list`
- `schedule_delete`

`schedule_create` requires a non-empty `prompt` and exactly one selector:

```json
{
  "name": "optional label",
  "prompt": "Check the deployment and report regressions.",
  "every": "3m"
}
```

```json
{
  "name": "daily report",
  "prompt": "Summarize today's repository activity.",
  "at": "2026-07-21T18:00:00+02:00"
}
```

```json
{
  "name": "weekday check",
  "prompt": "Check CI failures and open tasks.",
  "cron": "*/15 8-18 * * 1-5"
}
```

Selectors have these meanings:

- `every`: recurring duration. Supported units are `m`, `h`, and `d`; the minimum is
  one minute.
- `at`: one-shot date-time. An explicit ISO-8601 offset is authoritative. A date-time
  without an offset is interpreted in the machine's local timezone.
- `cron`: recurring standard five-field cron interpreted in the timezone stored with
  the definition. New definitions default to the machine's local timezone.

Supplying zero or multiple selectors is an error. Tool output reports the id, optional
name, normalized schedule, timezone, and next run in local and UTC forms.

`schedule_list` reports:

- id and optional name
- schedule kind and normalized rule
- timezone
- prompt preview
- next run
- runtime state: idle, running, or pending
- active managed task id, when present
- last terminal outcome and completion time, when known

`schedule_delete` removes the definition. If one of its executions is already running,
the active managed task is not implicitly stopped; it finishes under normal TaskManager
rules, but no pending or future execution starts.

### Interactive notices

The TUI adds compact transcript notices:

- scheduled task `<name-or-id>` started as task `<task-id>`
- scheduled task `<name-or-id>` completed
- scheduled task `<name-or-id>` failed/stopped

The notices do not include full output. `/tasks`, `task_output`, and the persistent task
log remain the authoritative detail surfaces.

### Serve events

Serve mode emits host-neutral schedule lifecycle notifications carrying the definition
id, managed task id, state, timestamp, and optional terminal error/result summary.
The scheduler and tools themselves remain shared engine behavior; serve only adapts the
notifications to JSON-RPC.

## Architecture

### `ScheduleRuntime`

`CodaSession` owns one `ScheduleRuntime` for its lifetime. The runtime depends on:

- `ScheduledTaskStore`
- `TaskManager`
- an injected `TimeProvider`
- a scheduled-agent execution factory
- a lifecycle-notification sink
- the session shutdown token

It starts during `CodaSession.InitializeAsync`, after the stable session collaborators
exist, in both interactive and serve modes. It stops accepting claims during session
shutdown, cancels its timer loop, and completes before `TaskManager` disposal begins.
This ordering prevents a due firing from registering after task shutdown starts.

Interactive Coda currently creates `CodaSession` lazily on the first submitted prompt.
Its startup path must therefore call a new idempotent `AgentRunner.InitializeSessionAsync`
before enabling the composer. This creates and initializes the session without running a
model turn, so persisted schedules resume as soon as Coda is ready. Serve already creates
its session eagerly. Headless one-shot mode does not start a long-lived runtime.

The runtime does not poll continuously. It waits for the earlier of:

- the next due time
- one minute, to re-evaluate wall-clock/timezone changes
- a store-change signal
- cancellation

Store add/delete/replace operations publish a change signal after committing the
mutation, so a new earlier definition wakes the runtime immediately.

### Scheduled managed tasks

Each firing registers a `TaskKind.Scheduled` managed background task before agent
execution starts. It receives:

- an isolated conversation containing the scheduled prompt
- current provider/model/effort/output-style settings
- current working directory and project context
- the shared live `PermissionModeState`
- the normal non-UI built-in, MCP, LSP, and task tools appropriate to a managed child
  agent
- task output buffering and persistent secret-redacted logging
- cancellation and bounded process-tree cleanup
- authorization to manage only its own descendants

The execution does not append to or mutate the main session conversation. This avoids
history races and lets a scheduled run execute while the main agent is busy.

The scheduled agent may create child tasks within the existing depth limit. It cannot
inspect or control unrelated session tasks. Schedule-management tools are not exposed
inside scheduled child execution in the first release, matching current subagent
behavior and preventing accidental recursive schedule creation.

### Host adapters

The shared runtime exposes lifecycle notifications through a small interface rather
than referencing TUI or JSON-RPC types.

- Interactive adapts notifications to semantic UI notice events and runtime snapshot
  refreshes.
- Serve adapts notifications to JSON-RPC events.
- Headless one-shot `coda run` does not remain alive to service schedules after its
  requested turn finishes.

This preserves the standing rule that serve exposes the same model-facing non-UI
capabilities as interactive Coda.

## Persisted model

The persisted definition becomes an explicit versioned record with:

- `id`
- `name`
- `kind`: `interval`, `at`, or `cron`
- `prompt`
- normalized rule data:
  - interval duration
  - one-shot UTC instant
  - cron expression
- `timeZoneId`
- `nextRunUtc`
- creation/update timestamps
- optional last terminal outcome metadata

Active task ids and the coalesced-pending flag are runtime state, not durable execution
recovery. A process restart reconstructs them as idle and applies overdue-startup
semantics.

The store loads legacy records shaped as:

```json
{
  "id": "...",
  "cron": "*/5 * * * *",
  "prompt": "...",
  "recurring": true,
  "nextRunUtc": "..."
}
```

Legacy recurring records become `cron` definitions. A legacy non-recurring cron record
becomes an `at` definition using its persisted `nextRunUtc` instant. The next successful
mutation writes the new schema.

Writes use a temporary file followed by atomic replacement where supported. Invalid
individual records are skipped and logged; they do not discard valid records. A wholly
unreadable document is reported and treated as no loaded definitions without crashing
session startup.

## Time and recurrence semantics

### Intervals

Intervals are fixed schedule boundaries derived from the previous scheduled due time,
not delays measured from task completion. After an overdue or long-running execution,
the runtime advances directly to the first future boundary and coalesces missed
boundaries.

### Cron

Cron expressions use five fields and minute precision. They are evaluated in the
definition's stored timezone and converted to UTC for waiting and persistence.

For daylight-saving transitions:

- a nonexistent local time during spring-forward is skipped
- an ambiguous local time during fall-back fires once, using the earlier UTC occurrence

### Specific time

`at` is one-shot. Explicit offsets are converted directly to UTC. Offset-less values use
the machine-local timezone captured when the definition is created.

### Startup catch-up

On runtime startup, each `nextRunUtc <= now` definition is claimed once immediately.
The runtime never creates one task per missed occurrence. A recurring definition then
advances to the first future boundary relative to current time.

## Execution state machine

Each definition has runtime state:

- `Idle`
- `Running(activeTaskId)`
- `RunningPending(activeTaskId)`
- `Deleted`

Transitions:

1. Due `Idle` -> atomically claim -> `Running` -> start managed task.
2. Due `Running` -> `RunningPending`.
3. Due `RunningPending` -> unchanged; additional ticks remain coalesced.
4. Terminal `Running` -> `Idle`.
5. Terminal `RunningPending` -> claim one immediate replacement -> `Running`.
6. Delete from any state -> `Deleted`; active task may finish, but terminal callbacks
   cannot re-arm the deleted definition.

The runtime advances and persists the next recurring due time as part of claiming. A
one-shot record remains persisted until its managed task reaches a terminal state. If
the process dies during that run, it remains overdue and can run once again on restart.
This is explicit at-least-once behavior.

Recurring definitions remain enabled after success, failure, cancellation, permission
denial, or launch failure. A launch failure is logged and surfaced, and the definition
waits until its next normal recurrence rather than entering a tight retry loop.

## Permissions

Scheduled executions read the session's shared live `PermissionModeState` at each
permission and sandbox decision, exactly like current running agents.

- A live `/yolo` or `/permissions` change affects scheduled runs at their next decision.
- In default mode, an approval request waits on the existing interactive prompt or serve
  request path.
- The scheduler never silently escalates permissions or captures a stale mode at
  creation time.

The separately tracked `/yolo` regression must be fixed against this same source of
truth before relying on unattended permission behavior.

## Concurrency and shutdown

- Different definitions may run concurrently as independent managed tasks.
- One definition never overlaps itself.
- The main agent and scheduled tasks may run concurrently because scheduled executions
  do not share the main conversation list.
- Store/runtime state transitions are serialized.
- Task registration occurs before execution so `/tasks` never misses a started run.
- Delete and task completion races are idempotent.
- Session shutdown cancels the timer loop before TaskManager begins bounded task
  cancellation.
- No timer callback or terminal continuation may mutate disposed session state.

## Error handling and observability

- Invalid tool input returns a clear model-facing error.
- Invalid timezone ids, impossible cron expressions, and out-of-range intervals are
  rejected at creation.
- Store persistence failures preserve in-memory state but emit structured telemetry.
- A malformed record is skipped with its id/index logged.
- Runtime launch and execution failures become lifecycle notifications and task errors.
- Scheduler-loop faults are contained, logged, and retried after a bounded delay; one
  bad definition cannot terminate the runtime.
- Task logs retain existing size, retention, redaction, and filesystem-permission rules.

## Testing strategy

All scheduling tests use injected fake time and deterministic completion seams; no test
waits on real minutes.

Required coverage:

- `schedule_create` validation for exactly one selector
- interval parsing and one-minute minimum
- local and explicit-offset `at` parsing
- cron normalization and timezone validation
- legacy JSON loading and new-schema persistence
- atomic store mutation/change signaling
- next-due selection and wakeup when an earlier definition is added
- startup overdue coalescing
- interval fixed-boundary advancement
- cron local-time and DST behavior
- one-shot at-least-once restart behavior
- task registration before execution
- no self-overlap and one pending coalesced run
- delete-vs-completion races
- different schedules running concurrently
- current model/tools/live permission state propagation
- child-task authorization/depth limits
- success, failure, stop, and permission-wait outcomes
- TUI start/completion notices
- serve lifecycle events and tool parity
- cancellation and disposal races
- full Engine, TUI, and LlmAuth regression suites

## Acceptance criteria

1. With interactive Coda or `coda serve` open, asking the main agent to check something
   every three minutes creates a persisted interval definition and starts managed
   background runs at the expected boundaries.
2. Asking for a specific future local or offset time creates a one-shot definition that
   fires once.
3. Asking for a raw cron schedule uses the stored local timezone and minute precision.
4. Scheduled executions appear in `/tasks` and their full output is available through
   task tools and persistent logs.
5. Closing Coda stops active executions through normal TaskManager shutdown; reopening
   runs each overdue definition once and resumes future scheduling.
6. A slow run never overlaps itself; at most one pending replacement is retained.
7. Interactive and serve expose the same schedule tools and execution behavior.
8. Permission decisions use the current live mode rather than the mode at creation.
9. Existing persisted cron definitions continue to load.
