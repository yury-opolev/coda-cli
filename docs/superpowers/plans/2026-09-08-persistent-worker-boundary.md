# Persistent Worker and Session Boundary: High-Level Plan

> **Superseded scope:** The revised direction keeps the orchestrator/worker
> and bridge connector in a separate application/process. Coda's first
> milestone is an independently usable core and a complete `serve` contract.
> See `2026-09-08-serve-api-contract.md` in this directory. The proposal below
> is retained as background, not the active Coda implementation plan.

**Goal:** Make running Coda sessions independent of the UI that controls them,
reusing `coda serve` and allowing a future outbound relay/browser transport
without another engine rewrite.

**Milestone boundary:** This first milestone delivers a persistent local
worker, attachable sessions, and TUI attachment. It does not deploy Azure
infrastructure or implement a browser frontend.

## 1. What "persistent" means

- Closing or losing a UI connection does not close engine stdin or kill the
  engine. The worker owns the engine process, connection, and event reader.
- A relay outage will eventually have the same semantics as a local client
  disconnect: it is not an engine shutdown instruction.
- Explicit session stop, worker shutdown, engine failure, and UI detach are
  different operations with different outcomes.
- Worker/VM restart is not transparent continuation of an in-flight tool.
  Persisted session metadata/history support explicit recovery. Interrupted
  and uncertain operations are reported, never automatically repeated.
- Existing direct `coda` and `coda serve` behavior remains available.

## 2. Target architecture

```text
Local TUI / test client              Future browser / orchestrator
          |                                      |
          | local IPC                     replaceable relay
          |                                      |
          +---------- Worker control API --------+
                             |
                      Session supervisor
                      /                \
                 Session A          Session B
                    |                  |
              existing stdio     existing stdio
               JSON-RPC            JSON-RPC
                    |                  |
               coda serve         coda serve
               workspace A        workspace B
```

The engine remains responsible for LLM calls, filesystem/shell tools, MCP,
tool permissions, steering, and conversation history. The worker owns
process lifetime, session discovery, attachment, controller ownership,
event delivery, and connection-independent request handling.

A transport adapter carries typed worker messages; it must not own engine
lifetime or bypass worker authorization. Local IPC is the first adapter.
An outbound WSS/relay connector is a later adapter, not a dependency of
the supervisor.

## 3. Delivery phases

### Phase 1 - Define ownership and contracts

Establish worker, session, engine-instance, client-attachment, command, and
event identities. Keep engine-instance epochs distinct from stable saved
session IDs so stale commands cannot target a restarted engine.

Define controller/observer roles, disconnect/approval policy, and capabilities
here, before implementing snapshots or transports. Default: the first
authorized attachment requesting control acquires it; subsequent attachments
are observers unless control is explicitly released or transferred. A bounded
reconnect grace preserves ownership temporarily, not indefinitely.
Approval answers are checked against the current controller lease/epoch;
stale-controller answers are rejected after handoff.

Capabilities declare which operations are client-local, worker-owned, or
unavailable. Filesystem locality is never inferred from a path string.

Define operations for:

- Create a session with an explicit workspace and startup options.
- List sessions and inspect status.
- Attach/detach, acquire/release control, and observe a session.
- Submit a prompt, interrupt, steer, reclaim pending messages, and answer
  permission/question/plan requests through the existing engine protocol.
- Stop a session and shut down the worker explicitly.

Lifecycle requests such as engine `initialize`, `shutdown`, and session
identity changes are worker-owned, not blindly forwarded from clients.
Fork/resume must update the registry coherently; unsupported lifecycle
operations should be rejected explicitly in the first release.

**Deliverable:** Versioned control contract and lifecycle state diagram,
reviewed before implementation.

### Phase 2 - Build the persistent local supervisor

Introduce a small UI-independent worker runtime. Reuse `coda-client` to
launch and drive one existing `coda serve` process per active session.

- Start with a one-session create/attach/detach skeleton over an in-memory
  control transport. This proves the API boundary before adding OS IPC.
- Keep engine handles and readers alive without an attached client.
- Manage multiple sessions with separate workspaces, limits, and statuses.
- Keep one prompt in flight per session; preserve existing steering,
  interruption, and tool-approval mechanisms.
- Detect engine exits and record their outcomes.
- Implement intentional session stop and graceful worker shutdown.
- Persist a private session registry containing identifiers, workspace,
  lifecycle state, and recovery references, but not provider credentials.

Initially run the worker as a normal long-lived foreground process that
can later be hosted by a Windows service or systemd. Automatic service
installation and startup registration are not part of this milestone.

**Deliverable:** A worker that owns sessions rather than a TUI owning them.

### Phase 3 - Make attachment and reconnect reliable

Add a bounded session event stream with monotonically increasing sequence
numbers and a snapshot/cursor contract.

- Build the display snapshot by folding the same canonical sequenced events
  that clients replay. Use a checkpoint plus retained event tail; a truncated
  tail alone cannot reconstruct old state. Obtain snapshot/cursor/subscription
  consistently so events cannot disappear between snapshot and live streaming.
- Return a consistent snapshot plus events after its sequence number.
- Detect stale cursors and require resynchronization rather than dropping
  events silently.
- Keep bounded buffers and enforce backpressure/resource limits for slow
  or disconnected clients.
- Give commands stable IDs and retain acknowledgments/results for safe
  retry while the worker is alive.
- Reject stale engine epochs; report an uncertain outcome after a crash
  instead of claiming exactly-once execution.

The display snapshot includes current conversation/tool/reasoning-summary
state, active work, queued steering, and pending interactive requests.
It must not expose provider credentials or encrypted reasoning payloads.
Current text-only history RPCs and operational diagnostic logs are not
sufficient substitutes for this state.

A bounded in-memory event journal is enough for UI disconnect/reconnect.
Disk-backed live-event durability across worker crashes is a separate
capability; the first release must document its recovery limits honestly.

**Deliverable:** A second client can reconnect to the same live engine
without losing the current state or resubmitting the prompt.

### Phase 4 - Add protected local IPC

Expose the control contract through a local-only transport:

- Windows named pipes for the currently supported distribution.
- Unix-domain sockets through the same abstraction when supported/tested.
- Restrict access to the owning OS user; no public TCP listener by default.
- Propagate a verified connection identity into every worker operation.
- Start with one controller per session, plus read-only observers.
- Require explicit ownership transfer; scope approval responses to the
  current controller, session, engine epoch, and pending request.

Define disconnect policy explicitly: already-permitted work may continue
under the session's existing policy. New approvals pause until a controller
answers or a configured deadline/cancellation safely resolves them.
Disconnect never grants permissions or silently enables YOLO.
Permission/plan expiry denies; a question requiring a meaningful user choice
must not be answered by guessing a default. Controller handoff rebinds
outstanding interactive requests explicitly.

The Rust `serve --api-key` flag is an LLM-provider credential, not a control
API password. Local and future relay authorization are separate concerns.

**Deliverable:** A secure local attach endpoint and in-memory transport
tests demonstrating that business logic is not tied to pipes or sockets.

### Phase 5 - Attach the existing TUI

Separate TUI initialization from engine process creation:

- Migrate normal standalone use to an ephemeral embedded supervisor through
  the same control contract. It still ends with that TUI, preserving current
  launch-and-own behavior, but does not duplicate command/approval logic.
- Add a worker-attach path consuming the session snapshot/event stream.
- Reuse current transcript rendering, progress, pending-message previews,
  recall, tool grouping, and approval controls.
- Closing an attached TUI detaches only; stopping the session is explicit.
- Make controller versus observer state visible.

Identify UI-local operations (settings, MCP/plugin files, session listing)
and classify them as client preferences or worker-owned workspace settings.
Expose required worker operations or advertise them as unavailable through
capabilities. Never silently edit client-local files as if they belonged
to a future remote workspace.

Illustrative command families, not existing commands or final syntax:

```text
coda worker run
coda worker sessions
coda attach <session-id>
```

**Deliverable:** Two independent TUI lifetimes can control the same
worker-owned engine in succession.

### Phase 6 - Validate, document, and release

Use red/green tests for lifecycle and command handling, with real subprocess
coverage as well as unit tests. Fake providers and isolated `CODA_HOME`
profiles keep automated tests independent of real accounts.

Required demonstrations:

1. Start a session, detach its UI during a turn, and confirm the engine stays
   alive under the declared execution policy.
2. Reattach and recover output, tool state, queue state, and pending approvals.
3. Retry an acknowledged command without executing it twice.
4. Reject an observer's mutation or approval response.
5. Run two sessions in different workspaces without cross-routing events,
   commands, approvals, or settings.
6. Handle a slow client, stale cursor, and full buffer predictably.
7. Stop a session explicitly without stopping unrelated sessions.
8. Restart the worker and identify interrupted sessions without automatic
   tool/command replay.
9. Keep ordinary non-worker TUI and `serve` behavior intact.
10. Exercise explicit stop and worker-failure cleanup with running tools,
    including process ownership checks; never kill unrelated reused PIDs or
    assume a tool's side effects were rolled back.

An independent review must cover the lifecycle/security boundaries,
reconnect consistency, and actual subprocess tests before packaging.
Documentation must distinguish attach, detach, stop, resume-from-disk,
and recovery after a worker/VM failure.

## 4. Keep the first milestone bounded

Not included yet:

- Browser UI, Azure Relay/Web PubSub integration, or cloud deployment.
- Multi-machine discovery and fleet/job orchestration.
- Multi-user tenancy or organization-wide identity integration.
- Multiple concurrent controllers of a single session.
- Transparent continuation after VM crashes.
- Changing provider protocols or rewriting the agent loop.

The next milestone adds an authenticated outbound relay adapter and a
minimal browser using the same control contract.

## 5. Main architectural decisions

- Keep engine process ownership in the worker, never in a UI connection.
- Live attach continuity ends at worker failure in this milestone. Mark
  affected sessions interrupted/uncertain; do not claim transparent engine
  survival or restart. Normal shutdown explicitly cleans up owned processes.
  Abrupt-crash orphan prevention/reconciliation requires platform-aware
  process ownership, not an assumption that Rust destructors always run.
- Keep execution state authoritative in the engine; the worker maintains
  an explicitly defined display/control snapshot, not a second executor.
- Give connection transport, session supervision, event delivery, and
  authorization separate interfaces.
- Treat local pipes as a secure initial adapter, not the final API shape.
- Preserve raw conversation content only where necessary for authorized
  session state, with private storage and bounded retention. Do not copy it
  into routine diagnostics.
- Resolve pending approvals and uncertain commands explicitly; never infer
  permission or successful execution from a reconnect.

## 6. Independent plan critique

The independent architecture critique approved the direction and recommended:
moving ownership/capability policy into Phase 1, proving transport independence
with an early in-memory skeleton, deriving snapshots from the canonical event
stream, sharing the control path with standalone mode, and clarifying crash
limits. These amendments are incorporated above.

The first vertical slice is deliberately single-session. Multi-session,
retention/backpressure, and command-deduplication follow after that boundary is
proven, but remain release requirements rather than optional production
hardening.
