# Rust serve API catalog

This catalog describes the Rust `coda serve` / `coda-engine serve` contract,
not the legacy C# socket host. The wire protocol version remains `1`; the
additive state contract is `2026-09-1`. Read `initialize.capabilities` rather
than inferring support from a product version or a method name.

## Ownership and transport

The engine executes work and owns sessions, history, permissions, provider
configuration and tool connections on its host. Its caller owns the process
and one bidirectional, Content-Length-framed JSON-RPC connection over stdio.
Keep stdout exclusively for protocol frames; diagnostics use stderr/files.

An external application may own that connection for the lifetime of a VM
worker and multiplex browser users itself. Coda does not implement a relay,
network listener, multi-client attachment, durable event journal, control
leases or remote-user authentication. `--api-key` is an LLM credential, not
a password for control clients. Closing the engine's stdin is not detach.

The normal TUI launches its engine automatically and uses this same public
API. A custom `--engine` or `CODA_ENGINE` is API-only: a client must not treat
its own settings files or credential store as that engine's configuration.
Default local launch can explicitly authorize local maintenance. An engine
cannot grant that authority by claiming it is local.

## Request inventory

**Reusable** means a routed operation with real behavior. **Limited** means
the route exists but does not provide the richer behavior an orchestrator
might infer from its name. Unsupported operations are listed separately.

| Method | Class | Contract / limitation |
|---|---|---|
| `initialize` | Reusable | Negotiate the connection, optionally resume a validated saved session; returns session/engine identity, capabilities and baseline event cursor. Clients initialize before running work. |
| `shutdown` | Reusable | Stop this engine; not a user/browser detach operation. |
| `session/prompt` | Reusable | Run one foreground turn; response arrives when execution ends. A concurrent prompt is refused, not queued. |
| `session/interrupt` | Reusable | Request cancellation of the active turn. Observe terminal state; a request acknowledgement alone is not completion. |
| `session/steer` | Reusable | Add text to the active turn's inbox; returns a message ID or a rejection reason. No future-job backlog. |
| `session/recallSteering` | Reusable | Atomically withdraw pending inbox text. Destructive; never use for queue inspection. |
| `session/getState` | Reusable | Authoritative snapshot with identity, cursor, lifecycle, phase, queue, tools, requests, configuration, usage and limits. `sections` filtering is unsupported and rejected. |
| `session/getEvents` | Reusable | Bounded replay for a matching engine instance. Ring eviction and process replacement are explicit, not silent success. |
| `session/getHistory` | Reusable | Rich committed history plus optional live projection, with instance/epoch/length fences and bounded pages. |
| `session/listSessions` | Reusable | Bounded saved-session discovery within the engine's workspace. Available before initialize. |
| `session/history` | Limited | Legacy text-only projection. Use `getHistory` for reasoning, images and tool structure. |
| `session/messages` | Limited | Legacy indexed text projection; not a rich-history consistency fence. |
| `session/fork` | Reusable | Branch committed history into a new session; owns the foreground slot and changes the history epoch. |
| `session/rewind` | Reusable | Remove later committed history; owns the foreground slot and changes the history epoch. |
| `session/compact` | Reusable | Compact history with the configured provider; busy while running, with a new history epoch on replacement. |
| `session/getPendingRequests` | Reusable | Read outstanding permission/question/plan requests without resolving them. |
| `session/resolveRequest` | Reusable | Resolve an instance-bound request handle exactly once, with kind-specific validated outcome. A stale or consumed handle is an error. |
| `session/cancelRequest` | Reusable | Apply deny / no-answer / reject to one outstanding request. Does not shut down the connection. |
| `session/models` | Reusable | Engine-owned model inventory and current model/provider information where known. Not a frontend settings lookup. |
| `session/setModel` | Reusable | Select the model for subsequent turns; no persistent settings write. |
| `session/setEffort` | Reusable | Validate and set session reasoning effort for subsequent turns. |
| `model/adjustEffort` | Reusable | Adjust a specified model's effort without implicitly activating that model. |
| `model/reasoningCapability` | Reusable | Live authority for supported effort levels, auto/current state and indeterminate capability. Unknown is not unsupported. |
| `session/setPermissionMode` | Reusable | Update the mode read by subsequent permission checks; not a persistent preference. |
| `session/setSystemPrompt` | Reusable | Set/clear the session prompt override for subsequent turns. |
| `session/setGoal` | Reusable | Configure the session goal and budgets; not fleet scheduling. |
| `config/describe` | Reusable | Describe ownership, mutability, applicability and safe effective values. Available before initialize. |
| `config/set` | Reusable | Delegate supported session settings to their validated setters. Refuse startup-only, file-owned and client-local settings. |
| `mcp/list` | Reusable | Read-only, secret-free configuration/runtime inventory on the engine host. Available before initialize. |
| `session/scheduleList` | Reusable | List this engine's scheduled-task definitions; not fleet inventory. Reports live `state`, `runsStarted`, configured bounds and any retirement reason. |
| `session/scheduleCreate` | Reusable | Create a scheduled-task definition in the engine's scope. Accepts the optional `maxRuns` / `expiresAt` / `expiresIn` bounds only when `schedules.bounds` is advertised. |
| `session/scheduleDelete` | Reusable | Delete a scheduled-task definition in the engine's scope. Stops future runs; does not interrupt a run already executing. |
| `hooks/list` | Reusable | List hook configuration with engine-side trust information. |
| `hooks/info` | Reusable | Describe a hook selected by the method's parameters. |
| `hooks/trust` | Reusable | Apply the engine's hook trust operation; caller authorization must be supplied by the external control plane. |
| `skills/list` | Reusable | Discover skills on the engine host. |
| `skills/trust` | Unsupported | Always refused in serve mode. |
| `plugins/list` | Limited | Compatibility route currently returns an empty list; not an authoritative installed-plugin inventory. |

The new `session/getHistory`, `session/getPendingRequests`,
`session/resolveRequest`, `session/cancelRequest` and `config/set` methods
require completed initialization. Read-only discovery does not. Legacy
routes retain their compatibility parsing; new query routes reject malformed
recognized fields instead of silently dropping fences. Unknown additive
fields are tolerated.

Initialization shares the foreground exclusion slot with prompts and history
mutations. A concurrent initialize or an initialize during an active operation
is refused before it changes capabilities, session history or credentials.
Idle repeated initialization remains supported for compatibility. While an
accepted handshake is running the lifecycle is `initializing`, not a
synthetic model turn; readiness is published only after the slot is released.
Stopping/stopped engines refuse initialization.
An in-flight handshake cannot subsequently complete successfully after
shutdown. `session/interrupt` refuses a handshake rather than arming
cancellation of a future prompt; use `shutdown` to stop the engine.
Use the `initialized` flag, not lifecycle `ready`, to determine whether the
handshake completed: legacy prompt calls may run without initialization.

An accepted handshake is not a rollback transaction. A resume or provider
change already applied before a later startup failure can remain applied,
and capability negotiation is one-way for the connection. After such an
error, re-read authoritative state before retrying, or replace the engine.

## Events and reverse requests

Every engine event has an instance-scoped `seq`. New state event methods are
sent only when `clientCapabilities.stateEvents` is true. Legacy event methods
remain available with additive metadata; unnegotiated state events can leave
holes in a legacy client's received sequence. Replay sequence allocation is
independent of negotiation.

`clientCapabilities.maxEventPayloadBytes` is reserved; it does not impose an
outgoing frame-size limit. `events.payloadLimitNegotiation` is explicitly
unsupported. The snapshot's retention/projection limits describe the
actual bounds; do not treat a client preference as an acknowledged limit.

`clientCapabilities.richHistory` is also a reserved reader hint, not a
format switch. `history.rich` reports the rich-history API's availability;
`history.richNegotiation` is unsupported and `session/getHistory` returns
the same structured projection regardless of that hint.

| Event methods | Meaning |
|---|---|
| `event/assistantText`, `event/assistantTextComplete` | Assistant text deltas and completion. |
| `event/thinking`, `event/thinkingComplete` | Provider-confirmed reasoning lifecycle / available summary. Silence is not evidence of reasoning; encrypted reasoning is not exposed. |
| `event/toolCall`, `event/toolProgress`, `event/toolResult` | Tool lifecycle, input/results and timing. Match canonical turn + batch + call IDs; provider IDs alone may repeat. |
| `event/turnComplete`, `event/stop` | Legacy turn termination notifications. Turn completion is distinct from the engine becoming ready for another prompt. |
| `event/usage`, `event/streamProgress` | Reported response usage and streaming progress; do not silently interpret last-response usage as cumulative. |
| `event/error`, `event/limitReached` | Execution errors and enforced limits. |
| `event/steeringDelivered` | Inbox entries injected at a safe boundary, not proof of provider receipt or execution. |
| `event/taskCompleted`, `event/scheduleLifecycle` | Existing background lifecycle vocabulary. Do not infer complete fleet or background counters from availability of these names. |
| `event/promptRewritten`, `event/responseRewritten` | Hook rewrite notifications. |
| `event/toolInputModified`, `event/toolResultModified` | Hook changes to tool content. |
| `event/permissionDecided`, `event/permissionsUpdated` | Permission hook/control notifications. |
| `event/subagentBlocked`, `event/subagentResultModified` | Subagent hook decisions/content changes. |
| `event/compactionCancelled`, `event/postCompactContextInjected` | Compaction hook notifications. |
| `event/lifecycle`, `event/activity` | **Gated:** engine availability and foreground activity transitions. |
| `event/turnEnded`, `event/sessionChanged` | **Gated:** authoritative turn outcome and history/session reset fences. |
| `event/configChanged`, `event/steeringQueue` | **Gated:** effective configuration and authoritative queue state/outcomes. |
| `event/requestPending`, `event/requestResolved` | **Gated:** interaction registry changes and actual resolution outcomes. |
| `event/eventsDropped` | **Gated:** explicit loss of retained event coverage; obtain an authoritative snapshot/history instead of guessing missing deltas. |

`request/permission`, `request/question` and `request/planApproval` are
server-initiated JSON-RPC requests, not notifications. Reply using their RPC
ID, or resolve the canonical `requestId` through the session API. Both paths
share one registry: a second resolution never grants twice. Never answer a
new engine using a previous instance's numeric RPC ID.

Permission requests currently have no `callId`: the permission callback does
not receive the tool-call identity. Use the request's own identity and display
context; do not infer a correlation with a concurrent tool call.

There is no default question answer. Decline, disconnect, timeout or malformed
answer aborts required question handling; it must not choose the first option,
manufacture an empty answer, or authorize a subsequent model request.
Permission and plan failure defaults are deny and reject. Reverse-request
timeouts are off by default. A resolved-event outcome can confirm an external
answer without containing that answer's text; clients must not invent it.

## Reconstructing a view

Take the identity/cursor from initialize or `session/getState`, buffer events
during reads, discard events already covered by the snapshot, and apply later
events in sequence. Detect both explicit loss events and client-observed gaps.
On a gap, replay retained events or re-read authoritative state and history.
On instance replacement, retire old interactions and discard old cursors.

For rich-history paging, carry the returned engine instance, live history
epoch and committed history length as fences. Restart a read on a moved
fence; do not combine pages from different histories. Append the live
projection once, not once per page, and do not treat user-role tool-result
entries as user prompts: use `entryKind`.

A delivered steering message appears in the live projection tagged with `steeringMessageId`,
in the same read that reports its `delivered` outcome. A client that queued it holds the
original text and should match on that id rather than on the text, which is not an identity.

Honor advertised bounds and omission metadata. Missing elapsed time,
credentials resolution, provider identity or background counts means unknown,
not zero or false. Server elapsed durations are authoritative baselines;
clients can advance them with a local monotonic clock without backdating a
local instant using a remote wall clock.

Queue outcomes are bounded. Preserve original editable drafts separately if
recovery is needed; a message missing from both pending and retained outcomes
has an unknown disposition. Never automatically submit it again.

## Bounded schedules

`schedules.bounds` is a capability, not a version check, and clients must read
it before relying on a schedule bound: an engine without it accepts `maxRuns`,
`expiresAt` and `expiresIn` on `session/scheduleCreate` and silently ignores
them, turning "seven runs" into an unbounded schedule with no error. Manage the
limit client-side when it is absent.

When advertised, `session/scheduleCreate` validates bounds with the same code
the engine's `schedule_create` tool uses: `maxRuns` must be an integer in
`1..=4294967295`; `expiresAt` (absolute ISO-8601 with offset) and `expiresIn`
(relative, `30m` / `2h` / `7d`) are mutually exclusive; a deadline at or before
"now" is refused; and a definition whose first occurrence already falls outside
its deadline is refused rather than created dead.

`session/scheduleList` then reports `maxRuns`, `expiresAtUtc`, `runsStarted`,
`retiredReason` and `retiredAtUtc`, plus a derived `state`. Read these
literally:

- `runsStarted` counts **accepted launch attempts**, including runs that later
  failed. Only a launch refused outright, with nothing executed, is uncounted.
- `expiresAtUtc` is **exclusive**: no run starts at or after it. Expiry does not
  interrupt a run already executing.
- `state` is one of `idle`, `running`, `pending`, `retiring`, `completed`,
  `expired`, `cancelled`, `failed`. **`retiring` means work is still in
  flight** for a schedule that will not start another run — it is not a
  finished schedule, and `completed` specifically means the run budget was
  spent, not that the last run succeeded. The last run's own outcome remains a
  separate field (`lastOutcome`).
- Absent bound fields mean *unlimited*, never zero.

Schedules are engine-scoped and **in memory only** in the Rust engine: nothing
survives an engine restart, and no schedule state is written to disk. Do not
build resumption on top of `session/scheduleList`.

## Unsupported and client-local surfaces

MCP writes, plugin installation, marketplace maintenance and persistent
engine settings are not remotely writable through this contract. Provider
changes require a new engine instance. Appearance is client-owned.
`config/describe` states each key's scope and refuses unsupported setters.
Output-style discovery is not a claim that the engine applies a style.

No durable replay, cross-instance replay, prompt backlog, built-in detach,
multi-client attachment, socket listener or remote-client authentication is
advertised. The external orchestrator must implement authorization, ownership,
supervision and any bridge transport.

## Non-TUI reference client

From the `rust` workspace, run:

```powershell
cargo run -p coda-client --example serve_api -- .\target\debug\coda-engine.exe C:\work\project
```

Append a quoted prompt to run one turn. The example uses public RPCs only,
prints metadata rather than transcript content, disables MCP for this demo,
and interrupts/fails interactive requests rather than guessing answers.
It owns and shuts down its child; it is not a persistent-worker supervisor.

## Generated schemas

The `schemas` directory contains JSON Schema 2020-12 documents derived from
the public Rust DTOs, not separately maintained field definitions.
Schema generation is an optional `coda-proto` feature; normal engine/client
builds do not need the generator or its dependency.

The generated [machine-readable index](catalog.json) links schemas for every
routed method, server-initiated request and declared event. It identifies
shared server/parser DTOs versus legacy client writer/reader contracts,
initialization gates and state-event negotiation. Routes and declared
method names are checked against this index to detect coverage drift.

From the `rust` workspace:

```powershell
cargo run -p coda-proto --features schema --bin emit_schemas
cargo test -p coda-proto --features schema --lib schema::
```

An optional positional argument to `emit_schemas` chooses the protocol output
directory; it receives `catalog.json` and a `schemas` subdirectory.

Coverage includes initialization, state/replay/history, configuration and
MCP inventory, model/hook/session mutations, legacy client request/result
contracts, and all emitted event payloads. Event documents describe
notification **params**, including Rust's bus-injected `seq` and
`engineInstanceId`, not the outer JSON-RPC frame. Older engines can omit
this metadata; clients must negotiate/read their capabilities.

Shared request schemas describe the engine's actual parser types; the engine
additionally validates numeric bounds, identity fences and kind-specific
request outcomes.

`InitializeParams.json` describes the client writer shape; the legacy
engine parser is more tolerant. `InitializeResult.json` describes the
legacy-compatible client reader, not the current engine's stricter response:
the current engine always emits `contractVersion`, `engineInstanceId`,
`eventCursor` and `capabilities`. Reader schemas may allow omission of
nullable fields that the current server emits, including cancellation
`reason: null`. `InitializeResponse.json` is derived from the current
server's response type and requires all four contract metadata fields.

Legacy client contracts describe the writer/reader types used by the client,
not stricter validation of every tolerated legacy request. A successful
JSON-RPC response can still contain `ok: false`; check the operation result
before reporting a change. Schema presence does not make a limited or
unsupported operation functional: for example, `plugins/list` remains an
empty compatibility inventory and `skills/trust` always errors.

Reserved but undeclared event names have no schema. `EventEnvelope.params` remains arbitrary JSON
rather than pretending every event has the same payload.
