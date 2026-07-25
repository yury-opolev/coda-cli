# Scheduled-Tasks Control Surface Design

**Date:** 2026-07-25
**Status:** Approved

## Goal

Give users and programmatic clients **direct control** over scheduled tasks, without having to ask the
agent to invoke the `schedule_*` tools. Today the scheduling engine is complete — persisted store, runtime,
re-arming, and lifecycle events that already flow to the TUI `/tasks` browser and to serve's outbound
`event/scheduleLifecycle` — but the only way to *manage* definitions is through the agent tool loop.

This feature adds the missing control surface in both modes:

- **TUI:** a `/schedule` interactive browser to list, create, and delete definitions.
- **Serve:** `session/scheduleList` / `session/scheduleCreate` / `session/scheduleDelete` request methods.
- **Shared:** one control service so the agent tools, the TUI browser, and the serve RPCs cannot drift.

## Scope and architecture

The engine layer is unchanged in v1. The work sits above it as a thin, shared control layer plus two host
adapters (TUI, serve).

Bounded components:

- a shared `ScheduleControlService` owns list/create/delete over the session store + runtime view;
- `ScheduleDefinitionParser` continues to own parse/validation (already shared);
- the TUI `/schedule` browser owns interactive presentation and the create form;
- serve request handlers own the JSON-RPC surface and DTO mapping;
- the existing `ScheduledTaskStore`, `ScheduleRuntime`, and lifecycle sinks are reused unchanged.

### Why a shared service

The `schedule_*` tools are already thin over two primitives: `ScheduleDefinitionParser.TryParse(...)` for
validation and `ScheduledTaskStore.Add/Remove/GetSnapshot(...)` for persistence, with `ScheduleDisplay` for
projection. Adding two more independent callers (TUI, serve) would create three copies of the
"parse → store → project" flow. Extracting one `ScheduleControlService` keeps a single implementation; the
tools are refactored to route through it so behavior stays identical and there is one source of truth.

## Shared control service

`ScheduleControlService` wraps the session-owned `ScheduledTaskStore` + `IScheduleRuntimeView` +
`ScheduleDefinitionParser`. It is reachable from the session (e.g. a `CodaSession.Schedules` accessor) so
both the TUI command and the serve handlers use the same instance the runtime is bound to.

Operations:

- **`List()`** → a read model per definition: id, name, rule (`ScheduleDisplay.DescribeRule`), timezone,
  next run (local + UTC), runtime state (idle/running/pending) from the runtime view, active task id, last
  terminal outcome, and a prompt preview. This is the same projection `schedule_list` already builds.
- **`Create(ScheduleCreateRequest)`** → `ScheduledTask` on success, or a validation error carrying the
  parser's message. Internally: `ScheduleDefinitionParser.TryParse(request, nowUtc, zone, out draft, out
  error)` then `store.Add(draft, nowUtc)`. When no store is present (e.g. a subagent context) it returns the
  existing "no schedule store available" error.
- **`Delete(id)`** → found/not-found. Internally `store.Remove(id)`; the runtime drops the arm on the store
  signal.

`ScheduleCreateRequest` (existing) is the create input: `Name?`, `Prompt`, `Every?`, `At?`, `Cron?`,
`TimeZoneId?` — exactly one of `every`/`at`/`cron`.

## TUI: `/schedule` interactive browser

Register a new `/schedule` slash command. In the interactive Terminal.Gui shell, a bare `/schedule`
submission opens a live browser (reusing the `/tasks` browser and overlay/picker infrastructure). In the
plain, Spectre, and legacy console contexts it prints a read-only textual snapshot, mirroring how `/tasks`
behaves outside the interactive shell.

Browser contents (one row per definition): id/name, rule, timezone, next run (local + UTC), runtime state,
last outcome, prompt preview — the `ScheduleControlService.List()` read model.

Actions:

- **Delete** — with a confirmation step; calls `ScheduleControlService.Delete`.
- **Create** — opens a guided form: schedule kind (interval / at / cron) → rule value → optional timezone →
  prompt → optional name. On submit, the form calls `ScheduleControlService.Create`; validation errors are
  shown inline using the parser's message (identical to the tool's error), so an invalid cron or a
  sub-minute interval is rejected the same way everywhere.
- **Pause/Resume** — reserved for the fast-follow (see Out of scope); the browser leaves room for it.

Liveness: the browser subscribes to the same `ScheduleLifecycleChangedEvent` / `SessionRuntimeChangedEvent`
stream the `/tasks` browser already consumes (published by `TuiScheduleLifecycleSink`), so rows update as
definitions fire, complete, or fail. Publish faults during host shutdown use the existing narrow
`ObjectDisposedException` swallow.

## Serve: schedule request methods

Add three request methods under the existing `session/*` namespace (uniform with `session/prompt`,
`session/steer`, `session/models`, `session/setGoal`, …; schedules are session-owned so `session/*` reflects
ownership accurately):

- **`session/scheduleList`** → `{ schedules: ScheduledTaskDto[] }`.
- **`session/scheduleCreate`** → params mirror `ScheduleCreateRequest`
  (`name?`, `prompt`, `every?`, `at?`, `cron?`, `timeZone?`); result is the created `ScheduledTaskDto`. A
  validation failure returns a JSON-RPC error whose message is the parser's message.
- **`session/scheduleDelete`** → params `{ id }`; result indicates found/not-found (a not-found delete
  returns a JSON-RPC error rather than silently succeeding).

`ScheduledTaskDto` is a positional record with explicit camelCase `JsonPropertyName` attributes (same style
as `ScheduleLifecycleEvent`, never a ValueTuple projection): `id`, `name?`, `kind`, `prompt`, `rule`
(human-readable), `timeZone`, `nextRunUtc`, `state`, `activeTaskId?`, `lastOutcome?`. Handlers map through
`ScheduleControlService`.

The existing outbound `event/scheduleLifecycle` notification is unchanged and remains the streaming
complement to these request-side methods; a client uses the RPCs to manage definitions and the event to
observe runtime transitions.

## Data flow

- **Create** (any surface) → `ScheduleControlService.Create` → `ScheduleDefinitionParser.TryParse` →
  `store.Add` → store signal → runtime re-arms → lifecycle events stream to both surfaces.
- **Delete** (any surface) → `ScheduleControlService.Delete` → `store.Remove` → runtime drops the arm.
- **List** (any surface) → `store.GetSnapshot()` joined with the runtime view → projection.

## Error handling

- Validation errors surface **identically** (the parser's message) in the tool result, the TUI create form,
  and the RPC error — one message source.
- No schedule store in context (e.g. subagent) → the existing "no schedule store available" error.
- Delete of an unknown id → not-found result in the browser; JSON-RPC error over serve.
- Browser publish during host shutdown → existing narrow `ObjectDisposedException` swallow.

## Testing

- **Shared service:** unit tests for list/create/delete, validation passthrough, and the no-store path.
- **Serve:** protocol tests for the three method constants and request/response DTO shapes, plus a
  validation-error test asserting the parser message is returned.
- **TUI:** browser render tests (rows from the read model) and create-form tests (validation inline, delete
  confirm), mirroring the existing `/tasks` browser tests. Non-interactive snapshot rendering test.
- **Parity:** one test proving the agent tool, `/schedule`, and the serve RPC produce an identical persisted
  definition from the same request input.

## Out of scope (v1)

- **Pause/Resume** — planned fast-follow. Adds an `Enabled`/`Paused` field to `ScheduledTask`
  (SchemaVersion 2→3, with legacy migration defaulting to enabled), the runtime skipping arming of paused
  definitions, the browser Pause/Resume action, and a `session/schedulePause` / `session/scheduleResume`
  pair. The v1 read model and browser layout leave room for the state column.
- **Run-now** — not planned.
- **Editing an existing definition** — delete + recreate for now.
