# Coda Core and Serve API: High-Level Plan

**Status:** Revised architecture proposal; no implementation started.

**Goal:** Make `coda serve` the complete, well-defined interface to the Coda
engine. A separate orchestrator owns processes and relay connections; TUI,
browser, and desktop clients consume the same engine contract.

This replaces the earlier proposal to build the persistent worker and its
local attach service inside Coda.

## 1. Separation of responsibilities

```text
Coda TUI ------------------------------+
                                      |
Browser/desktop <-> bridge <-> external orchestrator
                                      |
                             Coda serve protocol
                                      |
                            Core engine on machine
```

**Coda core / serve owns:**
- Agent execution, provider interaction, tools, MCP and tool permissions.
- Session and active-turn state, steering inbox, and interactive requests.
- Authoritative tool/run lifecycle, usage information and conversation data.
- Versioned commands, read-only state/history queries, and structured events.

**The external orchestrator owns:**
- Starting/stopping and containing engine processes; one process per active
  core session remains an acceptable starting point.
- Keeping engine stdio open while browser/desktop clients disconnect.
- Machine/session discovery, fleet scheduling, outbound relay transport.
- Remote authentication, authorization, controller ownership, and routing.
- Client-generated operation IDs, command deduplication, remote replay and
  reconnect policy. Unknown outcomes after failures are not silently retried.

**Each UI owns:** rendering, selection, clipboard, input editing and other
client-only preferences. It must not invent engine queue or execution state.

The orchestrator is not a blind public RPC tunnel: lifecycle and
privilege-changing operations must be explicitly authorized there.

## 2. The core contract: commands + state + events

### Commands

Retain the existing prompt, steer, recall, interrupt, model/effort/permission,
question/approval, compaction and session operations where applicable.
Specify preconditions, errors, concurrency and when changes take effect.

Distinguish:
- **Prompt:** starts a turn; currently single-flight, not a backlog queue.
- **Steering:** queues input for the active turn's safe delivery boundary.
- **Recall:** atomically withdraws only still-pending steering.
- **Future jobs/prompts:** queued by the external orchestrator unless a
  separate engine feature is explicitly introduced later.

Queue inspection must be read-only. `session/recallSteering` is destructive
and must never be used merely to count or display pending messages.

### Authoritative state

Add a consistent snapshot operation (working name `session/getState`, not
an existing method) covering:

| Area | Required information |
|---|---|
| Identity | Protocol/capabilities, engine instance, session, workspace, active turn |
| Lifecycle | Initializing/ready/stopping/stopped; last turn outcome separately |
| Foreground activity | Preparing, waiting for model, observed reasoning, responding, running tools, awaiting user input, compacting |
| Steering queue | Pending count and entries with stable IDs/order; reconciliable terminal outcomes |
| Tool activity | Stable call/batch/source identities, names, statuses and available elapsed times |
| Interactive requests | Pending permissions/questions/plans, IDs and sufficient display context to answer |
| Configuration | Effective provider/model/effort/permission mode and changes scheduled for a later boundary |
| Usage | Reported counters with explicit response/turn/session scope; unknown values remain unknown |
| Continuity | Snapshot revision/event cursor scoped to the engine instance |

Concurrent background work must not be squeezed into a single exclusive
foreground phase; expose active operations separately where supported.

Distinguish transport synchronization from execution. A bridge can be
reconnecting or syncing while the core is reasoning or running a tool.
Bridge connection state belongs to the orchestrator, not a fabricated
engine `syncing` phase.

### Events

Publish structured changes for activity, queue lifecycle, tools, interactive
requests, usage, errors and turn completion. Preserve streaming text and
readable reasoning summaries.

Snapshot and event ordering must agree. An external client should be able
to buffer incoming events, request state, and apply only events after that
snapshot's cursor without races or double application. Sequence numbers
reset only with an explicit new engine-instance identity.

The external orchestrator may journal/fan out events for remote UIs.
Coda does not need a relay service or a disk-durable UI event journal for
this milestone, but must report explicit resynchronization/availability
limits instead of implying lost events can be replayed.

## 3. Semantics that must be explicit

- Reasoning is confirmed only by provider evidence. While the provider is
  silent, report waiting/unknown rather than inferring reasoning from
  elapsed time or configured effort.
- Define stable session/turn/model-request/tool-call IDs. Existing Rust
  per-batch root IDs and call-ID-as-source metadata must not become the
  undocumented public semantics of the new contract.
- Define steering transitions: accepted/pending, delivered to the turn,
  recalled, rejected or cancelled. "Delivered to the turn" does not claim
  the remote model has already processed it. Retain enough outcomes to
  reconcile missed updates, with explicit retention limits.
- Define settings as client-local, session-scoped, or spawn-scoped.
  State whether each change applies immediately, at the next permission
  check, at the next turn, or requires a new engine instance.
- Expose capabilities for unsupported operations rather than requiring a
  browser/orchestrator to discover them by failure.
- Losing the control connection never grants permission or guesses an
  answer to a user question. Specify safe cancellation/failure semantics
  for permission, question and plan requests.
- State how stdin EOF, explicit shutdown, interruption and engine failure
  affect execution. Detachment/persistence is achieved by the external
  owner keeping the core connection alive, not by changing EOF into a
  hidden daemonization mechanism.
- The existing Rust `--api-key` is an LLM-provider credential, not remote
  client authentication. Provider credentials stay machine-side.
- Rich history/state returned to UIs must exclude opaque reasoning
  signatures, provider credentials and internal-only payloads. User text
  and tool output can still be sensitive; they need authorized handling
  and are not equivalent to privacy-safe diagnostic logs.

## 4. Delivery phases

### Phase A - Contract inventory and specification

Audit existing methods/events and classify each as reusable, incomplete,
missing or UI-local. Define state/queue/activity semantics and schema
versioning/capabilities before adding endpoints.

**Deliverable:** A documented protocol catalog and machine-readable schema
strategy suitable for a non-Rust client.

### Phase B - Independent core/serve boundary

Make the core/serve build and bootstrap independent of terminal rendering
and clipboard libraries. Move shared non-UI initialization out of TUI
helpers where needed. Preserve the existing `coda serve` command contract
and ordinary TUI launch behavior; exact artifact names are an implementation
decision, not a reason for another product rename.

**Deliverable:** The core can be run and driven without loading the TUI.

### Phase C - State and lifecycle observability

Implement the authoritative snapshot, read-only queue inspection, explicit
activity transitions, stable identities and consistent event cursors.
Preserve atomic recall/delivery/cancellation behavior.

**Deliverable:** A small external client can accurately answer "what is
Coda doing, what is pending, and what is it waiting for?"

### Phase D - Rich history and interaction completion

Provide a UI-safe structured history/state API for saved and active
sessions, including tool and readable reasoning information where
available. Do not make external consumers parse engine-private session
files. Preserve old saved sessions and omit unavailable historical data.

Complete pending-interaction and configuration capabilities needed by an
independent UI. Enforce safe question/approval failure behavior.

**Deliverable:** A client starting without previous TUI state can reconstruct
the conversation and current controls using only the serve API.

### Phase E - Make the TUI a conformance client

Adopt the same authoritative state/events for the current TUI while
preserving immediate local "connecting/sending" feedback, pinned activity,
pending previews/recall, tool grouping, images and clipboard UX.
Client-owned appearance preferences remain local.

**Deliverable:** The TUI no longer contains unique knowledge required to
understand engine state or mutate machine-side configuration.

### Phase F - Compatibility, conformance and handoff

Use red/green unit tests plus a real non-TUI client driving the executable
over stdio with fake providers and isolated profiles. Cover:

1. Idle and running state; waiting versus observed reasoning.
2. Multiple queued steers; count/order, delivery, recall and cancellation races.
3. Stable tool identities, progress and terminal outcomes.
4. Pending interactive requests and safe disconnect/error behavior.
5. Snapshot/event ordering while output and queue changes occur concurrently.
6. Rich state/history after resume, without exposing opaque signatures.
7. Invalid/out-of-phase commands and capability/version compatibility.
8. Existing TUI behavior against the public contract.

**Deliverable:** Versioned protocol documentation, schemas and a small
headless reference client/example for the separate orchestrator project.
Final independent review precedes release.

## 5. Explicitly outside this Coda milestone

- A worker daemon or embedded supervisor inside the TUI.
- Browser/desktop UI implementation, bridge/relay service, Azure deployment.
- Fleet scheduling, VM provisioning, remote tenancy or controller leases.
- Transparent adoption of live engines after orchestrator/VM restart.
- Provider protocol rewrites unrelated to exposing the serve contract.

The separate orchestrator can subsequently own existing `coda serve`
processes and translate this contract to any approved bridge transport.
