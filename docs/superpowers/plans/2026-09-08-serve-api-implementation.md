# Coda Serve API: Implementation Plan

**Status:** Implementation underway for the approved architecture in
`2026-09-08-serve-api-contract.md`. Core/bootstrap separation is implemented;
the state/event slice is undergoing review corrections before later stages.
`2026-09-08-persistent-worker-boundary.md` remains superseded background.

**Scope reminder:** Coda delivers an independently buildable core plus a
complete, versioned `serve` contract (commands + authoritative state + events).
The TUI is an ordinary client of that contract: normal `coda` auto-launches a
local core in serve mode and has no private in-process execution path (§2.8).
Process supervision, persistent engine ownership, bridge/relay transport and
fleet orchestration are a separate application and are **not** built here.
No Azure, no browser UI, no new auth transport, no worker inside Coda.
A blocking final review by a powerful model precedes any commit or install (§6).

---

## 1. Verified source facts this plan is built on

Read at the current working tree (Rust workspace `rust/`, 13 crates).

| Fact | Location |
|---|---|
| One entry point `serve_stdio()`; `working_dir` = process cwd; ends on stdin EOF | `crates/coda-serve/src/transport.rs:31`, `:199` |
| `PromptChannel` and `ServeSink` are both built on the same `outgoing_tx` before `ServeHost::new_with_optional_client_and_mcp`; one `write_loop` task drains it | `crates/coda-serve/src/transport.rs:150-170` |
| Events are fire-and-forget notifications with no sequence, no ids, no ring | `crates/coda-serve/src/sink.rs:29-40` |
| No snapshot method exists. `ServeBackend` has 30 methods; the full routing table is `dispatch.rs:319-353` | `crates/coda-serve/src/dispatch.rs:257-290` |
| `session/history` / `session/messages` return `project_history`, i.e. `{role, content}` text only | `crates/coda-serve/src/host.rs:1520-1533`, `:2736` |
| `session/recallSteering` is the only queue read and it is destructive | `crates/coda-serve/src/host.rs:1505-1518` |
| `session/steer` returns `ok:false` with no reason when no turn is active or the inbox is sealed | `crates/coda-serve/src/host.rs:1481-1503` |
| `Agent::run` mutates a **local clone** of history and commits only after the turn | `crates/coda-serve/src/host.rs:2228`, `:2311` |
| `TurnGuard::drop` calls `close_for_turn()`, which **clears undelivered entries** and seals | `crates/coda-serve/src/host.rs:1286-1301`, `crates/coda-agent/src/steering.rs:76-80` |
| `SteeringInbox` is a plain `Mutex`, publishes nothing | `crates/coda-agent/src/steering.rs:22-30` |
| `ToolActivity::new()` regenerates **both** `root_turn_id` and `activity_id` per tool batch (`agent/mod.rs:394`), and `for_call` puts the provider call id into `source_id` | `crates/coda-agent/src/agent/mod.rs:66-85`, `:203`, `:394` |
| Proto adapter hard-codes `call_id: None` | `crates/coda-agent/src/events.rs:28-35` |
| `TurnComplete` is emitted with `root_turn_id: None, activity_id: None` | `crates/coda-serve/src/host.rs:2348` |
| `Event::parse` maps unknown methods to `Event::Unknown`; payload structs are plain `Deserialize` (unknown fields ignored) | `crates/coda-proto/src/events.rs:465`, `:652` |
| `PROTOCOL_VERSION = "1"`; `InitializeResult` has no capabilities field | `crates/coda-proto/src/messages.rs:13`, `crates/coda-serve/src/host.rs:71-80` |
| `PromptChannel::issue` has **no timeout**; failure/cancel → `None` → deny / **first option** / reject | `crates/coda-serve/src/prompts.rs:60-96`, `:130-190` |
| `coda` binary depends on `coda-tui` (diagnostics, branding, startup, terminal) and `coda-render`; `coda serve` boots through `coda_tui::diagnostics::init` | `crates/coda/Cargo.toml:16-22`, `crates/coda/src/main.rs:401-417`, `:445-465` |
| `coda_tui::diagnostics` itself needs only `coda-client` + `coda-auth` + `coda-diagnostics`; `coda_tui::startup` needs only `coda-agent` | `crates/coda-tui/src/diagnostics.rs:1-30`, `crates/coda-tui/src/startup.rs:1-12` |
| TUI derives `Activity`, `queued`, tool grouping locally in its reducer | `crates/coda-tui/src/state.rs:17-63`, `:99-106`, `:728-736`, `:897-909` |
| TUI restarts the engine process on provider/settings change and re-`initialize`s with the session id | `crates/coda-tui/src/app/engine.rs:205-275` |
| TUI writes `settings.json`, `.mcp.json`, plugin/marketplace state locally; serve only *reads* settings | `crates/coda-tui/src/config.rs:44-58`, `:187-296`, `:895`, `crates/coda-serve/src/settings.rs:26-30` |
| Saved transcripts are `{id, createdUtc, messages[, systemPromptOverride]}`; blocks include `thinking{text,signature}` and `redacted_thinking{data}` | `crates/coda-agent/src/session/store.rs:71-97`, `crates/coda-agent/src/session/message_json.rs:18-19`, `:104-118` |
| Reasoning signatures are opaque provider payloads (`{id, encrypted_content}`) that must be replayed but never surfaced | `crates/coda-llm/src/copilot/responses.rs:103-118` |
| `CODA_HOME` is the supported profile-root override for isolated test profiles | `crates/coda-auth/src/home.rs:28-47` |
| A real out-of-process harness already exists: temp `CODA_HOME`, cleared env, fake Anthropic `/v1/messages` over `--api-key`/`--endpoint`, `env!("CARGO_BIN_EXE_coda")` | `crates/coda/tests/default_diagnostics.rs:14-152` |
| Both frontends already spawn the core out-of-process: `EngineCommand::new(current_exe).arg("serve")` for interactive and for `coda run` | `crates/coda/src/main.rs:612-615`, `:762-765` |
| No in-process agent path exists today: `coda-tui` never builds an `AgentLoop`; all execution goes through `coda-client` | `crates/coda-tui/Cargo.toml`, `crates/coda-tui/src/app/engine.rs` |
| But the TUI still reads engine-private files directly: session transcripts at startup and at runtime, and `.mcp.json` | `crates/coda-tui/src/startup.rs:11`, `:144`, `crates/coda-tui/src/app/browsers.rs:166`, `crates/coda-tui/src/app/slash/session.rs:43`, `:67`, `crates/coda-tui/src/config.rs:586` |
| There is **no** MCP listing RPC (`dispatch.rs` has `skills/list` and `plugins/list` only), so the MCP manager has no API to use | `crates/coda-serve/src/dispatch.rs:342-350` |
| `coda-tui` already enforces conventions by test over its own `src/` tree | `crates/coda-tui/tests/conventions.rs:1-24` |

Corrections found by adversarial review (2026-09-08) and folded in below:

| # | Fact | Location |
|---|---|---|
| F1 | `steering_log` is a **second, undocumented queue authority**: appended on every steer, pruned only on recall, never cleared at turn end (unbounded growth of full message text) | `host.rs:1497`, `:1507`, `:1516`, `session.rs:15-35` |
| F2 | `session/fork`, `session/rewind` emit **no events**; `session/compact` replaces history wholesale. Any claim that fork/rewind "are reported by events" is false today | `host.rs:2156`, `:2175`, `:2400` |
| F3 | `Correlation.call_id` already exists in proto and `to_notification` already writes `callId`; the only defect is the agent adapter hard-coding `None` while `source_id` already holds the provider `tool_use.id` | `coda-proto/src/events.rs:685-690` vs `coda-agent/src/events.rs:30` |
| F4 | `--engine` / `CODA_ENGINE` already exists on the interactive and `run` paths; a second engine artifact needs no new launch plumbing | `coda/src/main.rs:58`, `:291` |
| F5 | `coda-serve` already links no ratatui/crossterm/arboard/png/coda-tui/coda-render; only the **`coda` binary** pulls the TUI in, via `run_serve → coda_tui::diagnostics::init` | `main.rs:410` |
| F6 | `crates/coda` has **no lib target**, so `cargo test -p coda --lib …` is invalid | `crates/coda/Cargo.toml` |
| F7 | `cargo fmt` / `cargo clippy` are not available in this environment | verified: `no such command` |
| F8 | No SSE fake provider exists; `fake_anthropic_endpoint` is a one-shot **non-streaming** JSON responder | `crates/coda/tests/default_diagnostics.rs:46-92` |
| F9 | `engine_contract.rs` drives whatever `coda` is on `PATH` and silently skips — it is not a hermetic conformance harness | `coda-tui/tests/engine_contract.rs:16-38` |
| F10 | `McpClientManager` has no per-server status / tool-count accessor | `coda-mcp/src/manager.rs` |
| F11 | `schemars` is absent workspace-wide; there are **zero** `.snap` files (insta is used inline only) | `rust/Cargo.toml`, tree scan |
| F12 | The legacy C# host **does** implement a local named-pipe/Unix-socket transport with API-key client auth, still shippable via `publish.ps1 -Legacy`; `docs/serve-protocol.md` is accurate *for that host* | `src/LlmClient/…`, `src/Coda.Sdk/Serve/…`, `publish.ps1` |
| F13 | Three of the host's mutexes are `tokio::sync::Mutex` held across `.await` (`client`, `effort_lock`, `session_services`) and cannot move into a `std::sync`-based state | `host.rs:586`, `:615`, `:639` |
| F14 | `dispatch.rs` has **no** "must initialize first" gate; every method dispatches unconditionally | `dispatch.rs:318-355` |
| F15 | `coda-agent` appears in `[dependencies]` of exactly two crates today: `coda-serve` and `coda-tui`. `coda-mcp` does **not** depend on it | `crates/*/Cargo.toml` |
| `docs/serve-protocol.md` documents a named-pipe/socket transport and API-key *client* auth that the Rust engine does not implement | `docs/serve-protocol.md:1-22` |

**Consequences that shape the design**

1. There is already exactly one outbound ordering point (`outgoing_tx` +
   `write_loop`), but `ServeSink::emit` is called from many tasks, so a
   sequence number cannot be an unsynchronised `fetch_add`: assignment and
   enqueue must happen under one lock or frames and sequence numbers disagree.
2. The engine has no place that owns "what is true now". Every new snapshot
   field needs an owner; scraping it out of `ServeHost`'s eleven separate
   mutexes at request time cannot be made consistent with the event stream.
3. Existing correlation ids are wrong as public semantics (`root_turn_id`
   regenerates per batch, `call_id` is always absent, `TurnComplete` carries
   neither). The contract must define new authoritative ids and keep the
   current fields populated only as documented legacy aliases.

---

## 2. The contract

### 2.1 Versioning and negotiation (concrete strategy)

- `protocolVersion` stays `"1"`. Legacy clients (the C#-era bridge, current
  TUI) keep working unchanged.
- `initialize` params gain **optional** `clientCapabilities`:
  `{ stateEvents?: bool, richHistory?: bool, maxEventPayloadBytes?: i64 }`
  (absent = legacy behaviour).
- `InitializeResult` gains **optional additive** fields:
  `contractVersion: "2026-09-1"`, `engineInstanceId`, `capabilities` (map of
  `name -> {supported: bool, reason?: string}`), `eventCursor` — **the
  authoritative start cursor**, valid at the moment `initialize` returned.
- **`seq` is transport-envelope metadata, not payload.** `EventBus` assigns
  `seq` and injects `seq` + `engineInstanceId` into the params object *after*
  `Event::to_notification()` produces it. `coda_proto::Event` and its
  round-trip tests are untouched; correlation ids stay in the typed payload,
  where `Correlation` already has the slots (F3).
- **A seq is burned for every event the engine produces**, gapless within one
  `engineInstanceId`, regardless of negotiation. Gating decides only whether a
  frame is *written to this connection*. Suppressed events are still numbered
  and still stored in the ring, so cursors from a snapshot and from
  `session/getEvents` always agree.
- **New `event/*` methods are emitted only when `stateEvents` was negotiated.**
  Additive *fields* (`seq`, `engineInstanceId`, and the new correlation ids) are
  added to existing events unconditionally: both the Rust proto parser and
  `System.Text.Json` ignore unknown fields, and `Event::parse` already
  round-trips unknown methods (`events.rs:652`).
- **There is no fixed "hello at seq 0."** `event/engineHello` is a gated
  convenience re-announcement with an ordinary seq; clients take their start
  cursor from `InitializeResult.eventCursor` or from `session/getState`.
- **Legacy compatibility is semantic, not byte-identical.** The testable
  invariant: *for a client negotiating no capabilities, no method is removed,
  no pre-existing field is removed, and no pre-existing field changes meaning;
  additive fields may appear.*
- New request methods are always dispatchable; a client discovers them through
  `capabilities`, never by triggering an error.

### 2.2 New RPC methods (exact names)

| Method | Params | Result |
|---|---|---|
| `session/getState` | `{ sections?: string[] }` | `StateSnapshot` |
| `session/getEvents` | `{ engineInstanceId: string (required), afterCursor: i64, limit?: i64 }` | `{ engineInstanceId, events: EventEnvelope[], nextCursor, truncated: bool, oldestAvailableCursor }`; instance mismatch → typed `instanceChanged` error carrying the current id — never a silent replay of another process's stream |
| `session/getHistory` | `{ sessionId?: string, historyEpoch?: i64, sinceIndex?: i64, limit?: i64, includeLive?: bool }` | `{ sessionId, historyEpoch, entries: HistoryEntry[], liveEntries?: HistoryEntry[], nextIndex, totalKnown, truncated: bool }`; stale `historyEpoch` → typed `staleEpoch` error, never partial garbage |
| `session/listSessions` | `{ limit?: i64 }` | `{ sessions: SessionSummaryDto[] }` |
| `session/getPendingRequests` | `{}` | `{ requests: PendingRequestDto[] }` |
| `session/resolveRequest` | `{ requestId, outcome }` | `{ ok, state: "resolved"\|"alreadyResolved"\|"unknown" }` |
| `session/cancelRequest` | `{ requestId, reason? }` | `{ ok, appliedDefault: "deny"\|"noAnswer"\|"reject" }` |
| `config/describe` | `{}` | `{ entries: ConfigEntryDto[] }` |
| `config/set` | `{ key, value }` | `{ ok, appliedAt: AppliesWhen, effective?, error? }` |
| `mcp/list` | `{}` | `{ servers: McpServerDto[] }` — read-only, backed by `coda_mcp::config::load_all` (already called at `coda-serve/src/mcp.rs`) **plus new `McpClientManager` status accessors (F10 — budget this work)**. Fields are secret-free by construction (§2.3) |

Kept as-is (documented, not redefined): `initialize`, `shutdown`,
`session/prompt`, `session/interrupt`, `session/steer`,
`session/recallSteering`, `session/history`, `session/messages`,
`session/models`, `session/setModel`, `session/setEffort`,
`model/adjustEffort`, `model/reasoningCapability`,
`session/setPermissionMode`, `session/setSystemPrompt`, `session/setGoal`,
`session/compact`, `session/fork`, `session/rewind`, `session/schedule*`,
`hooks/*`, `skills/list`, `plugins/list`.

`session/steer` result gains additive `rejectedReason:
"noActiveTurn"|"turnEnding"|"emptyText"` so `ok:false` stops being mute.

### 2.3 DTOs (`coda-proto`, camelCase, optionals omitted)

```
StateSnapshot {
  contractVersion, engineInstanceId, sessionId, workspacePath,
  cursor: i64,                       // event cursor this snapshot is exact at
  historyEpoch: i64,                 // bumped on fork/rewind/compact/resume
  lifecycle: "initializing"|"ready"|"busy"|"stopping"|"stopped",
  initialized: bool,
  lastTurnOutcome?: { turnId, endedAt, stopReason?, interrupted, error? },
  turn?: TurnState,
  steering: SteeringQueueState,
  tools: { active: ToolCallState[], recentlyCompleted: ToolCallState[] },
  requests: PendingRequestDto[],
  config: EffectiveConfig,
  usage: UsageState,
  limits: { ringEnvelopes, ringBytes, liveBytesCap, outcomesRetained,
            historyBlockBytesCap },
  capabilities: { [name]: { supported, reason? } }
}

TurnState { turnId, startedAt, phase: ActivityPhase, phaseSince,
            modelRequest?: { requestId, startedAt, observedReasoning: bool },
            batches: { batchId, startedAt, callIds: string[] }[],
            liveEntries: HistoryEntry[],          // see §2.5 live projection
            liveTruncated: bool, liveOmittedBytes: i64,
            activeConfig: ActiveConfig }          // captured at turn start

ActivityPhase = "preparing" | "waitingForModel" | "reasoning" | "responding"
              | "runningTools" | "awaitingUserInput" | "compacting"
```
`reasoning` is set **only** on a real `StreamEvent::ThinkingStarted` /
`ThinkingDelta` (`crates/coda-agent/src/agent/stream.rs:89-104`). Silence after
the request is issued is `waitingForModel`. Effort/elapsed time never imply
reasoning. Background work (tasks, schedules, MCP) is reported in
`concurrent: { backgroundTasks: n, scheduledRuns: n }` on `TurnState`, not by
overwriting the foreground phase. Bridge/transport sync state is not modelled.

```
SteeringQueueState {
  pendingCount,
  pending: { messageId, enqueuedAt, text, textLength, textTruncated: bool }[],
  outcomes: { messageId, outcome, at, turnId? }[],   // bounded ring, newest last
  outcomesTruncated: bool, retainedOutcomes: i64
}
outcome = "delivered" | "recalled" | "cancelledTurnEnded" | "rejected"

ToolCallState { callId, batchId, turnId, toolName, status, startedAt,
                elapsedMs?, endedAt?, isError?, resultSummary? }
status = "queued"|"running"|"awaitingPermission"|"completed"|"failed"
        |"cancelled"|"skipped"

PendingRequestDto { requestId, kind: "permission"|"question"|"planApproval",
                    issuedAt, turnId?, callId?, display: {...}, failClosedDefault }

EffectiveConfig {
  active: ActiveConfig?,       // captured into the running turn; None when idle
  next: ActiveConfig,          // what the next turn / next check will use
  differing: { key, active, next, appliesWhen }[]   // derived, never invented
}
ActiveConfig { providerId?, model, effort?, effortIsAuto, permissionMode,
               systemPromptSource: "default"|"sessionOverride"|"startup" }

AppliesWhen = "immediately" | "nextPermissionCheck" | "nextTurn"
            | "newEngineInstance" | "clientLocal"

McpServerDto { name, scope: "user"|"project", transport: "stdio"|"http",
               targetKind: "command"|"url", targetDisplay,   // sanitised
               enabled, status: "connected"|"failed"|"disabled"|"unknown",
               toolCount?, envVarNames: string[],
               secretRefs: { var, resolved: bool }[] }

UsageState { lastResponse?: {inputTokens, outputTokens},
             session?: {inputTokens, outputTokens},
             contextLimit?, unknownFields: string[] }

HistoryEntry { index, role, blocks: HistoryBlock[] }
HistoryBlock =
  | { kind:"text", text }
  | { kind:"reasoningSummary", text, redacted: bool }   // never a signature
  | { kind:"toolCall", callId, toolName, inputJson?, inputOmittedReason? }
  | { kind:"toolResult", callId, isError, status?, content?, omittedReason? }
  | { kind:"image", mediaType, byteLength }             // never base64 payload
```

`HistoryBlock` never carries `Content::Thinking.signature` or
`RedactedThinking.data` (`crates/coda-llm/src/message.rs:67-74`). Oversized
`inputJson`/`content` are truncated with an explicit
`omittedReason: "tooLarge"` plus `fullLength`, never silently dropped.

**Secret deny-list, binding on every DTO above.** None of these may appear in
any state / history / config / MCP DTO, under any field name: `.mcp.json` `env`
values, custom header values, `--api-key` / `CODA_SERVE_API_KEY`,
`coda-secret:` targets, OAuth or keyring tokens, `Content::Thinking.signature`,
`RedactedThinking.data`, image base64, and any URL carrying a query string,
userinfo or embedded credential (`targetDisplay` is scheme+host+path or the
bare command name). Enforced by a `coda-proto` conventions test over the new
DTO field names. User text and tool output *are* carried (an authorised UI
needs them) but remain sensitive: they are never written to diagnostics logs,
which stay metadata-only.

### 2.3a Steering text retention (explicit, because the TUI depends on it)

`pending[].text` carries the **full original steering text**, not a preview:
the TUI's recall-into-composer path (`app/queue.rs:26-56`) must be able to
reconcile against a snapshot without downgrading a user's own draft to a
truncated string. A cap exists (default 64 KiB per message) and, if exceeded,
`textTruncated: true` plus `textLength` are set so the client can say so rather
than silently mangle. `session/recallSteering` remains the authoritative
withdraw path and returns the same full text it does today.

### 2.4 New events (gated behind `stateEvents`)

`event/engineHello` (instance id, contract version, capabilities — an ordinary
seq, **not** a fixed 0), `event/turnStarted`, `event/activity` (phase
transition), `event/modelRequest` (`started|firstToken|ended`, with
`observedReasoning`), `event/toolBatch` (`started|ended`),
`event/steeringQueue` (full queue state after any transition),
`event/steeringOutcome`, `event/requestPending`, `event/requestResolved`,
`event/configChanged`, `event/usageUpdated`,
`event/sessionChanged` (`{ reason: "fork"|"rewind"|"compact"|"resume",
sessionId, historyEpoch }` — closes the F2 gap where these operations were
silent), `event/eventsDropped` (`{ fromCursor, toCursor, reason }`).

All events (old and new) carry envelope `seq` + `engineInstanceId`. Tool events
additionally gain the new correlation ids `turnId`, `batchId`, `callId`
(additive; existing `rootTurnId`/`activityId`/`sourceId` values are unchanged —
see §2.6).

### 2.5 Cursor, ring and live-turn semantics

- `cursor` is a monotonic `i64`, unique and gapless **within one
  `engineInstanceId`**, burned for every event the engine produces whether or
  not this connection is allowed to see it (§2.1).
- A snapshot's `cursor` means: every event with `seq <= cursor` is already
  reflected in this snapshot; no event with `seq > cursor` is. Snapshot
  projection and event publication are made atomic by construction (§3).
- Reconnect protocol: buffer events → call `session/getState` → discard buffered
  events with `seq <= cursor` → apply the rest in order.
- **The ring stores whole encoded frames.** A replayed payload is never
  truncated. Adjacent `assistantText` / `thinking` deltas may be *coalesced*
  only into an envelope that declares the contiguous range it covers
  (`seqFrom`, `seqTo`) with concatenated text — so sequence coverage stays
  provable. Size pressure evicts **oldest whole envelopes** and raises
  `oldestAvailableCursor`; `session/getEvents` then returns `truncated: true`
  and the client re-snapshots. Defaults: 2048 envelopes / 4 MiB, reported in
  `StateSnapshot.limits`.
- **Live-turn projection (the piece the ring cannot supply).** `Agent::run`
  mutates a *clone* of history and commits only at turn end
  (`host.rs:2229`, `:2300`, `:2314`), so `session.history` during a turn is the
  pre-turn history. `TurnState.liveEntries` therefore carries the in-flight
  turn as the same `HistoryEntry`/`HistoryBlock` DTOs, appended from the events
  the sink already emits. **Committed history + `liveEntries` = the whole
  conversation as of `snapshot.cursor`**, with no dependence on the ring.
  Capped (default 256 KiB/turn) with explicit `liveTruncated` +
  `liveOmittedBytes`; `session/getHistory{includeLive:true}` returns the same
  projection at the same `historyEpoch` so the two views cannot disagree.
- **Contract rule (testable):** *a client holding a snapshot at cursor C and
  applying all events with `seq > C` — or re-snapshotting when
  `truncated: true` — reconstructs the conversation exactly as completely as
  the advertised `limits` allow. The engine never returns a partial payload
  without an explicit marker (`omittedReason`+`fullLength`, `liveTruncated`,
  `truncated`+`oldestAvailableCursor`, or a `historyEpoch` mismatch), and never
  claims completeness beyond those advertised limits.*
- `engineInstanceId` changes on every new engine process (TUI restart, provider
  change, `/resume` respawn — `crates/coda-tui/src/app/engine.rs:219`).
  `session/getEvents` requires the client's expected `engineInstanceId` and
  fails with `instanceChanged` on mismatch.
- `session/fork`, `session/rewind`, `session/compact` and resume do **not**
  change `engineInstanceId`, so cursors stay valid, but they *do* invalidate
  history indices. They bump `historyEpoch` and emit `event/sessionChanged`;
  `sinceIndex` is meaningful only within one epoch (F2).

### 2.6 Identity model (strictly additive)

| Id | Source | Notes |
|---|---|---|
| `engineInstanceId` | uuid v4 in `ServeHost::build` | per process |
| `sessionId` | existing `current_session_id` | changes on fork |
| `turnId` | uuid v4 minted once in `session_prompt` (today only used for diagnostics, `host.rs:2228`) and stored in state | one per prompt/compaction |
| `batchId` | new third field on `ToolActivity` | equals today's per-batch `activity_id` value |
| `callId` | provider `tool_use.id` | already present as `source_id` |
| `modelRequestId` | minted per `client.stream()` attempt in `stream_with_retries` | retries produce new ids, same turn |
| `steeringMessageId` | existing inbox uuid | unchanged |
| `requestId` | `"req-" + PromptChannel::next_id` | stable per reverse request |

**Compatibility rule: no existing value changes.** `ToolActivity` is **not**
re-rooted (`for_turn` is rejected): `root_turn_id` and `activity_id` keep
producing exactly the values they do today, and `ToolActivity` gains a third
field carrying the real `turn_id`. The wire therefore grows `turnId`,
`batchId`, `callId` while `rootTurnId` / `activityId` / `sourceId` keep their
current values and are documented as deprecated aliases. `callId` is the
one-line fix in the agent adapter (`call_id: c.source_id.clone()`,
`coda-agent/src/events.rs:30`) — proto and `to_notification` already support it
(F3). `TurnComplete` gains `turnId` (purely additive; both correlation fields
are `None` there today). This removes any risk to TUI tool grouping.

### 2.7 Semantics that must be written down (and enforced)

- **Prompt** is single-flight (`try_claim_turn`, `host.rs:1273`, claimed at
  `host.rs:1439`); a second
  prompt fails with a typed error, it is not queued. No engine-side backlog.
- **Steering** is accepted only during an active turn; `delivered` means
  appended to the request history at a safe boundary
  (`agent/mod.rs:265-277`), not that the model has processed it.
  **Preflight linearization:** today `session_steer` reads `turn_active`
  (`host.rs:1484`) and then calls `enqueue` (`:1490`) under a *different* lock,
  so the busy check and the enqueue are **not** atomic and `noActiveTurn` vs
  `turnEnding` is already racy. The contract does not pretend otherwise: the
  two steps are explicitly serialized on one path (§3) so `rejectedReason` is
  exact, and until that lands the plan claims no atomicity it does not have.
- **Recall** stays atomic and destructive; it is never the way to count.
  `session/getState` / `event/steeringQueue` are the read path.
- **Turn end** currently drops undelivered entries silently
  (`close_for_turn`). It must instead emit `cancelledTurnEnded` outcomes for
  each dropped id before clearing, so an external client can reconcile without
  TUI-local memory. **Published from both** the explicit seal
  (`host.rs:2307`) **and** `TurnGuard::drop` (`:1293`), made idempotent by
  message id: the client-disconnect / cancellation path — the documented reason
  the guard exists — reaches only `Drop`. Publishing is a non-blocking
  `UnboundedSender::send`; poisoning is already handled there.
- **Reverse requests**: add an **opt-in** production timeout
  (`CODA_SERVE_REQUEST_TIMEOUT`, **default `0` = off**). `fail_all_pending`
  already covers connection loss (`transport.rs:226`) and `CancellationToken`
  covers interrupt, so an unbounded wait only persists while a healthy client is
  connected and a human is deciding; a default timeout would cancel slow humans
  for no verified benefit. On timeout, disconnect, cancel or malformed reply:
  permission → deny, planApproval → reject, question → **no answer**
  (`AnswerOutcome::NoAnswer(reason)`, *not* the first option).
  `WireUserQuestion` today silently substitutes `options.first()`
  (`prompts.rs:156-186`), which makes "the connection died" indistinguishable
  from "the user chose option 1". Every `coda-agent` caller (stop-hook
  continuation, `ask_user_question` tool) must be audited and must treat
  `NoAnswer` as a typed abort — never coerced to a string, never auto-retried.
  Outcomes are always typed (`timeout`/`disconnected`/`cancelled`/`malformed`)
  and reported in `event/requestResolved`.
- **Initialization gate.** `dispatch.rs` has no such gate today (F14).
  Read-only discovery — `session/listSessions`, `session/getState`,
  `config/describe`, `mcp/list`, `session/getEvents` — stays valid **before**
  `initialize`. Every state-mutating or turn-scoped method requires
  initialization and returns a typed `notInitialized`. Because no gate exists
  today, this is a deliberate contract change: it is applied to the **new**
  methods and to methods where the current behaviour is already undefined, the
  pre-existing legacy routes keep their current behaviour, and the boundary is
  pinned by a test rather than assumed.
- **Config scopes**: `model`, `effort`, `systemPrompt`, `goal` → `nextTurn`;
  `permissionMode` → `nextPermissionCheck`; `provider`, MCP servers, hooks,
  plugins → `newEngineInstance`; theme, tool display mode, keybindings →
  `clientLocal`. There is **no pending-change scheduler in the engine**: these
  values are read at turn-build time (`host.rs:2267-2269`, `:2283`) and a
  mid-turn `setModel` mutates the mutex immediately without affecting the
  running turn. `EffectiveConfig` therefore reports `active` (captured into
  `TurnState` at turn start) and `next` (the current mutex value), and derives
  `differing[]`; it never invents a queue. `config/set` supports only the
  session-scoped keys the engine truly owns. MCP/plugin/marketplace/appearance
  writes return `supported: false, reason` — Coda never writes a remote
  client's filesystem on its behalf, and `config/describe` never returns
  provider credentials or header values (names/redaction status only).
- **Lifecycle**: stdin EOF = connection ended = process exits (`transport.rs:199`).
  It is not daemonization. Persistence is the external owner keeping stdio open.
- `--api-key` remains an **LLM provider credential**. It is not client auth and
  is never echoed by any state or config method.

### 2.8 The TUI is an ordinary API client (explicit, enforced)

Confirmed direction: **normal `coda` auto-launches a local core in serve mode
and drives it exclusively through this public API. There is no private,
in-process execution path, and none may be added.**

- `coda` (no subcommand) and `coda run` already spawn
  `<current_exe> serve` (`main.rs:612-615`, `:762-765`); that stays the single
  launch mechanism. The child is an ordinary `coda serve` process — the same
  binary an external orchestrator would launch — not a special mode.
- The core is a child process of the frontend that started it. Coda does not
  daemonize, does not detach, and contains no worker/supervisor/relay.
  Persistent engines are obtained by the **external orchestrator** owning the
  stdio connection instead of the TUI. Closing the TUI closes the engine it
  started; that is intended, not a defect to fix inside Coda.
- Anything the TUI needs about the conversation, the queue, tools, pending
  requests or engine-owned configuration comes from the API. The remaining
  private-file reads are removed in Stage E:

  | Today | Replacement |
  |---|---|
  | `SessionTranscriptStore::list()` in the session browser (`app/browsers.rs:166`) and `/resume` (`app/slash/session.rs:43,67`) | `session/listSessions` |
  | Transcript contents for display after resume | `session/getHistory` |
  | `coda_agent::session::fork` at startup (`startup.rs:144`) | `initialize{sessionId}` + existing `session/fork` |
  | `coda_agent::BuiltInOutputStyles` enumeration (`app/slash/config.rs:22-39`) | `config/describe` entry with allowed values |
  | `coda_mcp::config::load_all` for *display* (`config.rs:586`) | `mcp/list` |

- **Local maintenance UX is kept, not deleted.** The MCP editor, plugin
  install/enable and marketplace management have **no** engine write API and
  will not get one (Coda must not write a client's filesystem). They move into
  a single TUI module `coda-tui/src/local/` — a *local maintenance adapter*
  gated on the **client's own launch mode** (this frontend started the core on
  this machine and owns these files), **not** on an engine-advertised
  `isLocal`: the engine cannot know whether its client is a local terminal or a
  remote UI, so it must not be asked to assert that. When the mode is not
  local, those editors render read-only with an explicit "managed on the engine
  host" state — never silently broken. `coda-tui` therefore **keeps
  `coda-mcp`** as a JSON-parsing dependency, scoped to `src/local/`.
- **The execution-path guarantee is `coda-agent`, not a blanket ban.** After
  Stage E, `coda-agent` appears in `[dependencies]` of **`coda-serve` only**;
  `coda-tui` keeps it as a **dev-dependency** (startup/session tests use
  `SessionTranscriptStore::save` for fixtures), so the manifest assertion must
  target `[dependencies]`. The allow-list is explicit and extensible by a
  written decision — it is not a rule that "only `coda-serve` may ever depend on
  anything" (today `coda-agent` is depended on by `coda-serve` and `coda-tui`
  only; `coda-mcp` does not depend on it — F15).
- **Bootstrap exception, stated openly:** `--resume/--continue/--fork` must pick
  a session before a session exists. The resolution order becomes: spawn the
  core → call `session/listSessions` (read-only, valid before `initialize`) →
  `initialize{sessionId}` → `session/fork` when forking. No transcript file is
  read by the frontend. **Only the pure flag parsing
  (`SessionIntent::from_flags`, `startup.rs:35-55`) moves to `coda-boot`;
  `startup::resolve()` does not move** — it reads `SessionTranscriptStore` and
  calls `coda_agent::session::fork` (`startup.rs:11`, `:144`), and moving it
  would launder private filesystem resolution into the boot crate and drag
  `coda-agent` with it. `resolve()` is deleted in Stage E, replaced by the RPC
  order above; `coda-boot` ships with **no `coda-agent` dependency**.
- The TUI keeps writing **client-local** settings only (theme, tool display
  mode, keymap, and its own copy of preferences it owns). It may continue to
  write `settings.json` keys that `config/describe` marks `clientLocal`;
  engine-owned keys are changed only through `config/set`, and keys the engine
  declares unsupported (MCP servers, plugins, marketplaces) stay explicit
  client-side operations the user performs knowingly — Coda never writes a
  remote client's filesystem on its behalf.
- What the TUI must keep doing locally: optimistic "connecting/sending"
  feedback, rendering, selection, clipboard, images, input editing. Local
  optimism is a *presentation* overlay reconciled against the snapshot; it is
  never a second source of truth.

---

## 3. Module layout, ownership and lock order

New/changed modules (no crate renames, no product rename):

```
crates/coda-proto/src/
  state.rs        (new) StateSnapshot + all state DTOs
  history.rs      (new) HistoryEntry / HistoryBlock DTOs
  events.rs       (+)   new Event variants, seq/instance/ids on payloads
  messages.rs     (+)   new method constants, capability + init additions

crates/coda-agent/src/
  steering.rs     (+)   synchronous SteeringObserver hook (no async), terminal
                        outcomes; stays the sole execution queue
  events.rs       (+)   ModelRequestStarted/Ended, ToolBatchStarted/Ended;
                        one-line call_id fix (F3)
  agent/mod.rs    (~)   ToolActivity gains a third field (turn_id); existing
                        root_turn_id/activity_id values unchanged
  agent/stream.rs (~)   emit ModelRequest* around client.stream()

crates/coda-serve/src/
  bus.rs          (new) EventBus: seq assignment + whole-frame ring + single
                        send point
  state/mod.rs    (new) EngineState: Mutex<Arc<StateInner>> (CoW) + projection
  state/activity.rs     phase machine
  state/steering.rs     queue projection + bounded outcome ring (sole owner of
                        SteeringQueueState, end to end)
  state/tools.rs        call table
  state/requests.rs     pending reverse-request registry
  state/live.rs   (new) live-turn HistoryEntry projection (§2.5)
  history.rs      (new) UI-safe projection of coda_llm::Message
  capabilities.rs (new) capability catalog
  schema.rs       (new, feature "schema") schemars emission
  host.rs         (~)   delegates state mutation to EngineState; keeps runtime
                        handles (client, effort_lock, session_services — F13)
  session.rs      (~)   steering_log removed (F1)
  sink.rs         (~)   ServeSink publishes through EventBus
  prompts.rs      (~)   opt-in timeout + registry + typed question outcome
  dispatch.rs     (+)   10 new methods + initialization gate (§2.7)

crates/coda-boot/    (new; no TUI, no coda-agent) diagnostics init/forward_env/
                     record_engine_log_path, ServeArgs, version string,
                     SessionIntent::from_flags (pure flag parsing only)
crates/coda-engine/  (new) headless binary `coda-engine` = coda-boot + serve_stdio
```

**Data ownership.** `EngineState` is the single authority for everything in
`StateSnapshot`, and it is **derived** state: written only from RPC handlers and
the `AgentSink` path. `coda-agent` never reads it and never branches on it;
`EngineState` does not move into `coda-agent`, and `coda-agent` does not depend
on `coda-serve`. Runtime handles that are *inputs* — `client`, `effort_lock`,
`session_services` (all `tokio::sync::Mutex` held across `.await`, F13), the
tool registry and the MCP manager — **stay in `ServeHost`**; `EngineState`
mirrors only derived scalars from them (e.g. `Option<i64>` background counters
that are `None` until `get_or_init_services` has run). One owner per DTO
section: `state/steering.rs` owns `SteeringQueueState` end to end (observer,
projection, events) and nothing else writes it.

`steering_log` (`session.rs:15-35`, `host.rs:1497`) is **deleted** (F1): it is a
second authority holding full message text and is never cleared at turn end.
Any timestamps it provided are kept as bounded metadata inside the steering
projection.

**Lock order (global, one direction only):**

```
INBOX (SteeringInbox::gate)  →  STATE (EngineState::inner)  →  BUS (EventBus::inner)
```

- `SteeringInbox` remains the **sole execution queue** with its proven
  atomicity (`steering.rs` `recall_and_delivery_have_exactly_one_winner`). It
  gains a **synchronous** change observer invoked **inside its gate**, so an
  enqueue / delivery / recall and the published queue state are one atomic
  step. The public queue state is a *derived projection* in `EngineState`.
- The observer takes STATE then BUS and must never call back into the inbox.
  Observer-published and `Drop`-published terminal outcomes are **idempotent by
  message id**, so the double-publish required by §2.7 cannot double-count.
- `session/getState` takes STATE, reads `bus.cursor()` (BUS inside STATE), and
  **never takes INBOX** — inversion is therefore impossible.
- Every mutation is `state.update(|s| -> Vec<Event>)`: the closure mutates and
  returns the events to publish; `update` publishes them to the bus **while
  still holding the state lock**, so the cursor is atomic with the projection
  and with publication.
- **Copy-on-write:** `EngineState.inner: Mutex<Arc<StateInner>>`; `update` uses
  `Arc::make_mut`, `getState`/`getHistory` hold STATE only for
  `(Arc::clone, bus.cursor())` and then **project and serialize DTOs outside
  the critical section**. This is *not* claimed to be O(1): `Arc::make_mut`
  clones `StateInner` whenever a reader still holds a snapshot, so `StateInner`
  is kept small and bounded — bounded metadata (ids, counters, statuses),
  bounded steering text (§2.3a), bounded live-turn bytes (§2.5), bounded
  outcome ring. Unbounded data is never stored there.
- All three are `std::sync::Mutex`. **No `await` while holding any of them; no
  observer callback is `async`.** The bus's only I/O is
  `UnboundedSender::send` (non-blocking).
- Poisoning is handled like the existing `TurnGuard` (`host.rs:1291-1301`):
  recover the inner value rather than wedging the session.
- **Steer preflight** (`turn_active` read + `enqueue`) is performed on one
  serialized path so `rejectedReason` is exact (§2.7).

**Payload policy.** The bus stores **whole encoded frames** plus `seq`; a
replayed payload is never truncated. Coalescing of adjacent text/thinking
deltas is permitted only as an envelope declaring its covered range
(`seqFrom`..`seqTo`) with concatenated text. Size pressure evicts oldest whole
envelopes and raises `oldestAvailableCursor` (an explicit gap), never silently
shortens a payload. Mid-turn content is reachable through
`TurnState.liveEntries`, not through the ring. No tool input/result is
duplicated into a state event; state events carry ids and summaries while the
existing `event/toolResult` carries content.

---

## 4. Delivery

**Order (resequenced): Stage B → Slice 0 → Stage A → C → D → E → F.**
Stage B first because it is a zero-behaviour-change refactor that unblocks a
TUI-free conformance target and stops later stages re-touching `main.rs`.
Slice 0 ships the CoW `EngineState` and the steering observer immediately, so
Stage C extends rather than rewrites them. Stage A's schemas describe shipped
DTOs, so they move after Slice 0 — except the `serve-protocol.md` split, which
happens immediately because it is a factual correction.

### Stage B — independent core boundary (first)

- New `crates/coda-boot`, with **no `coda-tui`, no `coda-render`, no
  `coda-agent`** dependency. It carries exactly:
  `diagnostics::{init, forward_env, record_engine_log_path}`, `ServeArgs`, the
  version string, and the pure `SessionIntent::from_flags`
  (`startup.rs:35-55`). `coda_tui::startup::resolve()` is **not** moved: it
  reads `SessionTranscriptStore` and calls `coda_agent::session::fork`
  (`startup.rs:11`, `:144`), and moving it would launder private filesystem
  resolution into the boot crate and re-import `coda-agent`. It stays in
  `coda-tui` until Stage E deletes it.
- `coda-tui` re-exports the moved items, so no TUI call site, flag, `--help`
  text or `--engine-arg` behaviour changes. `coda`'s `init_diagnostics` calls
  `coda_boot::diagnostics::init`, which removes the only reason the `coda`
  binary links the TUI on the serve path (F5).
- `crates/coda-engine`: `main.rs` = `ServeArgs` from `coda-boot` +
  `coda_serve::serve_stdio()`. **`coda serve` stays identical** and remains
  what the TUI spawns; `--engine` / `CODA_ENGINE` already exists (F4), so
  `coda-engine` needs no new launch plumbing and is how the conformance suite
  parameterises both binaries.
- Build is **mandatory**, packaging **additive**: `cargo build -p coda-engine`
  is wired into `rust/build.ps1`; `publish.ps1` gains `-Flavor engine` but it
  is **excluded from `all`**, because the existing publish path validates PE
  machine type and `coda --version == "coda <semVer>"` and must not change.
- Dependency rule for the core: no UI, render or clipboard crates
  (`coda-tui`, `coda-render`, `ratatui`, `crossterm`, `arboard`). This is a
  UI-dependency rule, not a blanket ban on any particular utility crate — if a
  crate like `png` were ever a genuine core need it would be judged on merit
  (it is not needed today).
- Tests: `crates/coda-engine/tests/independence.rs` walks the workspace
  `Cargo.toml` manifests transitively (plain file reads — **no external
  tooling, no skip path**) and asserts the `coda-engine` dependency closure
  contains none of the UI crates above; a `coda`-package integration test
  (implemented as `crates/coda/tests/engine_parity.rs` — the package has **no
  lib target**, F6) pins `coda serve` flag/env parity and the
  `coda` vs `coda-engine` equivalence:
  `cargo test -p coda --test engine_parity`.

### Slice 0 — first vertical slice (proves the whole spine)

Smallest end-to-end path that is genuinely useful, not a types-and-tests stub:

1. `EventBus`: envelope-layer `seq` + `engineInstanceId` injected after
   `Event::to_notification()`, whole-frame bounded ring, single publish point;
   `ServeSink` routes through it; `session/getEvents{engineInstanceId,...}`
   with the `instanceChanged` error.
2. `EngineState` as **CoW `Mutex<Arc<StateInner>>` from day one**, with
   identity + lifecycle + activity
   (`preparing/waitingForModel/reasoning/responding/runningTools`) driven by the
   new `ModelRequest*` agent events, and projection outside the lock.
3. `SteeringInbox` keeps its gate and gains the synchronous observer;
   `steering_log` is deleted and its state folded into the projection.
4. `session/getState` (identity, lifecycle, activity, steering counts, cursor,
   limits, capabilities) + `initialize` negotiation + `eventCursor`.
5. Real out-of-process conformance test: prompt a fake **streaming** provider
   (SSE fixture, see Stage F) that emits thinking then text, snapshot
   mid-stream, prove every event with `seq <= cursor` is reflected, none is
   applied twice, and no event is lost across a simulated resync.

Exit: a non-TUI process can answer "what is the engine doing right now" and
resynchronise without races. Legacy TUI unaffected.

### Stage A — contract inventory and schemas (after Slice 0)

- Classify all existing methods and events as reusable / incomplete / missing /
  UI-local; publish as `docs/protocol/catalog.md`.
- Add `schemars` to `coda-proto` behind a **`schema` feature** (new workspace
  dep + lock churn — F11); `crates/coda-proto/src/bin/emit_schemas.rs` writes
  `docs/protocol/schemas/*.json`.
- **`docs/serve-protocol.md` is split, not rewritten** (F12): the existing
  socket / API-key-client-auth section is retained and clearly labelled
  "Legacy C# host (`publish.ps1 -Legacy`) — not implemented by the Rust
  engine"; a new Rust-engine section states stdio only and `--api-key` as a
  provider credential. Capability `transport.socket: {supported: false, reason:
  "stdio only in the Rust engine; the legacy C# host implements a local socket
  transport"}`. This split happens immediately (Stage B time), not at the end.
- Tests: `cargo test -p coda-proto --features schema --lib schema::` (drift
  guard: regenerated schemas equal the checked-in files).

### Stage C — state, queue and lifecycle observability

- Remaining `EngineState` sections: steering outcomes, tool table, usage,
  `lastTurnOutcome`, `concurrent` counters (`Option<i64>`, `None` until
  `get_or_init_services` has run).
- Steering observer completed: enqueue → `pending`; delivery → `delivered`;
  recall → `recalled`; turn end → `cancelledTurnEnded` **emitted before
  clearing, from both the explicit seal and `TurnGuard::drop`, idempotent by
  message id**; sealed enqueue → `rejected` with reason (also surfaced as
  `session/steer.rejectedReason`, on the serialized preflight path).
- Additive identity: `turn_id` threaded from `session_prompt` into the loop;
  `ToolActivity` gains a third field; `call_id: c.source_id.clone()` (F3);
  `TurnComplete` gains `turnId`. **No existing value changes.**
- `historyEpoch` + `event/sessionChanged` for fork / rewind / compact / resume
  (F2 — these are silent today).
- Files: `state/**`, `bus.rs`, `steering.rs`, `agent/mod.rs`, `agent/events.rs`,
  `host.rs`, `session.rs`, `dispatch.rs`.
- Tests: `cargo test -p coda-agent --lib steering::`,
  `cargo test -p coda-serve --lib state::`, `... --lib bus::`,
  `cargo test -p coda --test conformance queue_`, `... epoch_`.

### Stage D — rich history and interaction completion

Binding safety/coverage notes from implementation investigation:
- A failed question must not become `User answered: <first option>` or an
  empty success string. Propagate a typed no-answer outcome through the
  ask-user tool and goal escalation. The failure must not automatically
  continue the model loop or grant a goal extension.
- Exercise this through a real scripted tool-use turn: when the controller
  disappears or cancels a question, prove that no follow-up provider request
  executes on a fabricated answer. Unit-testing only a string converter is
  insufficient.
- Raw reverse-request responses and the new resolve/cancel RPCs must share
  one pending-request registry and exactly-one resolution path. Validate
  request kind/outcome and engine-instance identity before mutation.
- Queued original text must survive snapshot reconciliation; a display
  preview is not a replacement for an editable original draft.
- Local maintenance capability comes from the client's launch mode, never
  from a server claim that it is local. Preserve the shipping local editors.

- `history.rs` projection + **live-turn projection** (`state/live.rs`) +
  `session/getHistory` (`includeLive`, `historyEpoch`) + `session/listSessions`
  (reads saved transcripts through `SessionTranscriptStore`, so external
  clients never parse `.coda/sessions/*.json`).
- Sanitisation per the §2.3 deny-list; images reduced to metadata; oversized
  fields explicitly truncated with `fullLength`.
- Old transcripts load unchanged; absent data is omitted, never invented.
- Pending-request registry + `session/getPendingRequests`,
  `session/resolveRequest`, `session/cancelRequest`; **opt-in**
  `CODA_SERVE_REQUEST_TIMEOUT` (default off); `AnswerOutcome::{Answered,
  NoAnswer}` propagated to **every** `coda-agent` caller as a typed abort;
  permission stays fail-closed deny, plan reject.
- `config/describe` + `config/set` with `active`/`next`/`differing` and honest
  `supported:false` for MCP/plugins/marketplaces/appearance.
- `mcp/list` incl. the **new `McpClientManager` status/tool-count accessors**
  (F10 — real work, budget it); secret-free DTO fields only.
- Tests: `cargo test -p coda-serve --lib history::`, `... --lib live::`,
  `... --lib prompts::`, `cargo test -p coda-agent --lib tools::ask_user`,
  `cargo test -p coda --test conformance history_`, `... requests_`.

### Stage E — TUI as a conformance client

- On connect, restart and after `/resume`, `/fork`, `/rewind`, the TUI calls
  `session/getState` and reconciles: engine-owned truth (queue contents,
  delivered/recalled outcomes, tool identities, permission mode, effective
  model/effort) replaces locally derived values.
- **De-privatization (per §2.8):** replace session-transcript reads and MCP
  *display* reads with `session/listSessions`, `session/getHistory`,
  `mcp/list`, `config/describe`; move `--resume/--continue/--fork` resolution to
  the spawn-then-RPC order and delete `startup::resolve()`. End state:
  `coda-tui` drops `coda-agent` from `[dependencies]` (kept as a
  **dev-dependency** for test fixtures) and keeps `coda-mcp` scoped to
  `src/local/` for the local maintenance adapter.
- **Full pending text is preserved through reconciliation**: the snapshot's
  `pending[].text` (§2.3a) is the original text, so recall-into-composer never
  degrades a user's draft to a preview. If `textTruncated` is set, the UI says
  so instead of silently substituting.
- Preserved exactly: optimistic "sending/connecting" feedback, pinned activity,
  pending previews and recall-into-composer, tool grouping (now keyed by
  `batchId`/`callId` instead of UI boundaries — group *stability* must not
  regress), reasoning replay, images/clipboard, YOLO, error surfaces,
  default privacy diagnostics.
- Client-local settings (theme, tool display, keymap) stay local and are
  reported as `clientLocal` by `config/describe`.
- **Guard by test, not by discipline** — extend the existing
  `crates/coda-tui/tests/conventions.rs` harness with scoped rules:
  `coda-tui/src/**` **outside `src/local/`** contains no `coda_agent::`, no
  `coda_mcp::`, no `SessionTranscriptStore`, and no direct `.coda/sessions`
  path construction; `src/local/**` may use `coda_mcp::config` only; plus a
  manifest assertion that `coda-tui`'s **`[dependencies]`** (not
  `[dev-dependencies]`) does not list `coda-agent`. Add the execution-path rule
  as an explicit allow-list — `coda-agent` in `[dependencies]` of `coda-serve`
  only — rather than a blanket "nothing may depend on anything" ban.
- Tests: `cargo test -p coda-tui --lib state::`,
  `cargo test -p coda-tui --test conventions`,
  `cargo test -p coda-tui --test render`, `... --test surface_integration`,
  `cargo test -p coda --test conformance tui_parity_`.

### Stage F — compatibility, conformance, handoff

- **Build a real streaming SSE fixture first (F8).** The existing
  `fake_anthropic_endpoint` (`default_diagnostics.rs:46-92`) is a one-shot
  **non-streaming** JSON responder. The conformance suite needs a scripted
  `TcpListener` SSE server that emits `message_start` / `content_block_delta`
  (thinking then text) / `message_delta` / `message_stop` with controllable
  pacing; reuse the event scripts already written in
  `crates/coda-llm/tests/anthropic_stream.rs`. This fixture is a Slice 0
  dependency, not a Stage F afterthought.
- `crates/coda/tests/conformance.rs` (new): a real non-TUI reference client
  driving `env!("CARGO_BIN_EXE_coda") serve` over stdio, with temp `CODA_HOME`,
  cleared sensitive env (`default_diagnostics.rs:34-43`) and the SSE fixture via
  `--api-key`/`--endpoint`. **`CARGO_BIN_EXE_*` is required — no PATH lookup
  and no silent skip** (that is exactly why `engine_contract.rs` is not a
  conformance suite, F9; it stays as-is and is documented as a smoke test).
  The same suite is parameterised over `coda-engine` via `--engine`.
- Legacy-client test asserts the **semantic** invariant of §2.1 (no method or
  field removed, no pre-existing field changes meaning; additive fields
  allowed) — not byte-identical frames.
- Ring/live coverage test: shrink the ring to a handful of envelopes, stream a
  long turn, resync via snapshot + `getEvents`, and assert the reconstruction is
  complete to the advertised `limits`, with every omission explicitly marked.
- `samples/serve-reference-client/` (Rust, `coda-client` + `coda-proto` only):
  the minimal example the external orchestrator project starts from.
- No new test framework: `cargo test`, `tokio::test`, raw `TcpListener` fakes.
  `insta` is **inline-only** here — there are zero `.snap` files in the tree
  (F11), so no snapshot-file workflow is introduced.

---

## 5. Capabilities: essential now vs explicitly unsupported

**Supported after this milestone:** `state.snapshot`, `state.events`,
`state.eventReplayBounded`, `steering.readOnlyQueue`, `steering.outcomes`,
`tools.stableIds`, `requests.discovery`, `requests.outOfBandResolve`,
`history.rich`, `history.savedSessions`, `config.sessionScoped`,
`config.describe`, `mcp.list`.

**Declared `supported: false` with a reason (not silently missing):**
`config.mcpWrite`, `config.pluginInstall`, `config.marketplace`,
`config.appearance`, `auth.remoteClient`, `events.durableJournal`,
`events.replayAcrossInstances`, `session.multiClientAttach`,
`session.promptBacklog`, `session.detach`,
`engine.survivesClientExit` (the core exits with the client that owns its
stdio; persistence is the external orchestrator's job),
`transport.socket`, `orchestration.fleet`.

---

## 6. Red/green, review gates and acceptance

**Per-stage loop.** Every behaviour change lands as a failing test first, then
the fix. Suggested order per stage: DTO/schema test → unit test on the owning
module → conformance test. Targeted selectors only (Section 4). After each
stage, exactly **one** review pass; critical and important findings are fixed
before the next stage starts, minor/low are recorded and deferred to the final
gate.

**Mandatory final review gate (blocking).** Before any commit, push, tag,
publish or local install:

1. **Existing cargo checks only.** `cargo build --workspace` and
   `cargo test --workspace`, plus the targeted selectors from Section 4, with
   red/green evidence per stage. `cargo fmt` / `cargo clippy` are **not
   available in this environment** (F7) and are **not** made a prerequisite:
   no tooling is installed to satisfy the gate, and no test is allowed to skip
   silently to make the gate pass. If those tools are present on another
   machine they may be run informationally.
2. **A full final review by a powerful model** (Opus-5 class — the established
   choice for reviews and planning in this repo), reviewing the entire
   accumulated diff rather than per-stage slices. It must be an *independent*
   pass: the reviewing agent is not the implementing agent and starts from the
   plan plus the diff, not from the implementer's summary. Its scope explicitly
   **includes privacy, credential handling, authorization boundaries and
   fail-closed safety** (the §2.3 deny-list, permission/question/plan behaviour
   under disconnect/timeout/cancel, and the absence of any unauthenticated or
   remote-reachable surface).
3. No separate vulnerability-audit agent is required — a dedicated security
   audit was not requested; the privacy/auth/safety scope above is carried by
   the final review.
4. Explicit verification that the deferred minor/low findings from every stage
   are either fixed or consciously accepted in writing.
5. Commit, push and install happen only after that gate and the user's
   approval; ordinary release mechanics beyond it are already authorized.

No stage is "done" on the implementer's assertion alone; the gate is the
reviewed diff plus passing targeted tests.

---

## 7. Pitfalls — resolutions (settled) and what remains open

**Settled by the 2026-09-08 adversarial review + parent decisions:**

1. **Lock order INBOX → STATE → BUS — kept as planned.** The reviewer proposed
   inverting it (pending entries into `EngineState`, `SteeringInbox` reduced to
   a façade). **Rejected:** `SteeringInbox` remains the sole execution queue
   with its proven atomicity (`steering.rs:144`), gaining a synchronous
   in-gate observer; the public queue state is a derived STATE projection and
   the snapshot never reads INBOX. `EngineState` does not move into
   `coda-agent`, and `coda-agent` never depends on `coda-serve` (§3).
2. **`cancelledTurnEnded` publishes from both the explicit seal and `Drop`**,
   idempotent by message id — the disconnect/cancel path reaches only `Drop`,
   which is exactly when a reconnecting client needs the receipt (§2.7, §3).
3. **Ring policy settled:** whole frames, never truncated; coalescing only with
   declared `seqFrom..seqTo` coverage; eviction is whole-envelope with an
   explicit `oldestAvailableCursor` gap. Mid-turn content comes from
   `TurnState.liveEntries`, not the ring (§2.5).
4. **Question fail-closed change is kept**, and widened: `AnswerOutcome::
   {Answered, NoAnswer}` must be propagated to *every* `coda-agent` caller as a
   typed abort — never coerced to a string, never auto-retried (§2.7).
5. **Reverse-request timeout defaults OFF** (`CODA_SERVE_REQUEST_TIMEOUT=0`,
   opt-in) — `fail_all_pending` and `CancellationToken` already cover the real
   failure modes; a default timeout would cancel slow humans (§2.7).
6. **`ToolActivity` is not re-rooted.** Existing `rootTurnId`/`activityId`/
   `sourceId` values are unchanged; `turnId`/`batchId`/`callId` are added
   (§2.6). TUI tool grouping therefore cannot regress.
7. **Cursor reset on engine restart** is handled by `session/getEvents`
   requiring the expected `engineInstanceId` and failing with
   `instanceChanged`; the TUI re-snapshots (§2.5).
8. **`getHistory`/`getState` cost** is handled by CoW: STATE is held only for
   `Arc::clone` + `bus.cursor()`; projection and serialization happen outside
   the lock, and no unbounded data lives in `StateInner` (§3).
9. **`coda-engine` stays.** Its justification is a provably TUI-free *process*
   (`coda-serve` is already clean — F5; only the `coda` binary links the TUI).
   Build is mandatory; packaging is additive and excluded from `-Flavor all`.
   The "maybe feature-gate instead" hedge is dropped.
10. **Gating is settled:** new event *methods* are gated; a seq is burned for
    every event regardless, and suppressed events are still ringed, so cursors
    never depend on negotiation (§2.1).
11. **`concurrent` counters** are `Option<i64>`, `None` until
    `get_or_init_services` runs — unknown, never a confident zero (§3).
12. **`docs/serve-protocol.md` is split, not rewritten.** The legacy C# host
    really does implement the socket + API-key transport and still ships via
    `publish.ps1 -Legacy` (F12) — the earlier plan had this backwards.
13. **Pre-`initialize` discovery** is allowed for read-only methods; the new
    gate applies to state-dependent methods, legacy route behaviour is
    preserved, and the boundary is pinned by a test (F14, §2.7).
14. **`coda-tui` keeps `coda-mcp`** (parsing) and drops only `coda-agent` from
    `[dependencies]`; local MCP/plugin/marketplace UX survives behind the
    client-side local maintenance adapter (§2.8).
15. **`engine.survivesClientExit: false`** is surfaced once in the UI (a line in
    the exit summary), not built into a feature.

**Still open for the implementer's judgement:**

A. Exact default limits (`ringEnvelopes`, `ringBytes`, `liveBytesCap`,
   steering text cap, `outcomesRetained`) — they are advertised in
   `StateSnapshot.limits`, so they are tunable, but the first numbers are
   guesses and should be revisited once the conformance suite can measure a
   realistic tool-heavy turn.
B. Whether `state/live.rs` reconstructs entries purely from emitted events or
   also from the agent's committed message at turn end (belt-and-braces
   reconciliation) — the latter is safer but duplicates projection logic.
C. Cost of the new `McpClientManager` status accessors (F10) — if per-server
   status proves invasive, `mcp/list` ships with `status: "unknown"` and
   `toolCount: null` rather than delaying the stage.
D. Whether `coda-boot` should also own the `--engine`/`CODA_ENGINE` resolution
   (`resolve_engine`) so both frontends and the conformance suite share one
   code path.

---

## 8. Amendment log (2026-09-08, post-review)

Amended after an adversarial review of the first draft, with parent decisions
overriding the reviewer where they conflicted:

- **Adopted from the review:** F1–F15 fact corrections; envelope-layer `seq`;
  no fixed hello-at-0; semantic (not byte-identical) legacy compatibility;
  additive-only tool ids; `historyEpoch` + `event/sessionChanged`; live-turn
  projection; whole-frame ring; CoW `EngineState`; deletion of `steering_log`;
  dual (seal + `Drop`) idempotent terminal outcomes; exact steer preflight;
  `active`/`next` config instead of an invented pending scheduler; timeout
  default off; pre-`initialize` discovery gate; `serve-protocol.md` split;
  corrected test selectors (no `-p coda --lib`); SSE fixture as real work;
  Stage B first.
- **Overridden by parent decision:** the steering inversion is **rejected** —
  `SteeringInbox` stays the sole execution queue with an in-gate synchronous
  observer and the INBOX → STATE → BUS order (the review wanted STATE-owned
  pending entries); local maintenance UX is gated on the **client's launch
  mode**, not an engine-advertised `isLocal` (the engine cannot know whether
  its client is remote); `coda-boot` is strictly diagnostics + version +
  `ServeArgs` + pure flag parsing, with no private-FS resolution laundered in;
  no rustfmt/clippy installation is required by the gate and no test may skip
  silently; no dedicated security-audit agent — privacy/auth/safety is in scope
  for the single final Opus-5-class review; the `coda-agent` dependency rule is
  an explicit allow-list rather than a blanket ban.

### 8a. Stage C corrections applied (post Stage-C review)

Findings raised against the first Stage C implementation and the shape of the
fix actually landed. Each has a mutation-verified regression.

- **C1 — snapshot/event atomicity.** `StateSink` mutated state (publishing its
  gated events under STATE) and then forwarded to `ServeSink`, which published
  the legacy frame *outside* the lock. A snapshot at cursor `N` could already
  contain text whose own event was published at `N+1`, so the documented
  reconnect protocol duplicated it. There is now **one transaction per
  `AgentEvent`** carrying the view transition and every publication it implies,
  legacy and gated, assigned and sent while STATE is held. `StateSink` owns
  publication for the turn stream; `ServeSink` keeps host-level frames. Pinned
  by a contention test that compares each observed snapshot against the fold of
  the `assistantText` payloads its own cursor covers, plus the tail.
- **C2 — wire order.** `EventBus::publish` dropped BUS before `send`, so a
  later seq could reach the connection first. Assignment, ringing and the
  hand-off are now one critical section (`send` is non-blocking).
- **C3 — abandoned tool calls.** `end_turn` now finalises every non-terminal
  call owned by that turn (`cancelled` / `failed` / `skipped`, with end time and
  elapsed), and the tool table is bounded even when calls only ever start —
  evicting terminal entries first and raising `tools.truncated` rather than
  dropping a call silently or claiming the list is complete.
- **C4 — subagent contamination (reviewer claim corrected).** The parent
  verified `TaskTool`, the scheduled runtime and the hook runner all hand a
  `NullSink` to the subagent factory, so no child stream reaches the foreground
  `StateSink`. No re-rooting refactor was performed; the invariant is pinned by
  a focused test and `concurrent` counters stay `None` (unknown) rather than a
  false zero.
- **C5 — reasoning.** Thinking bytes are charged against the live budget, an
  in-flight burst is projected (not withheld until `ThinkingComplete`), and a
  bodyless `Thinking` delta from `ThinkingStarted` enters `reasoning`
  immediately — the only reasoning signal an encrypted-reasoning provider gives.
- **C6 — busy boundary.** The public turn opens with the single-flight claim,
  before credentials/services/agent construction, so `lifecycle`/`turn.phase`
  can never disagree with the flag that refuses a second prompt. Explicit
  compaction opens a `compacting` turn. `TurnGuard::drop` finalises the public
  turn idempotently on cancellation, early failure and panic.
- **I1 — history fence.** Committing history and clearing the live turn are one
  transaction (the transcript `save().await` now follows it), and
  `StateSnapshot.historyLength` is the explicit committed/live fence.
- **I2 — cost.** Real `Arc::make_mut` CoW; live text stored as `Arc<str>`
  chunks; the wire `liveEntries` are materialised once per snapshot outside the
  lock instead of being re-cached on every delta.
- **I3 / I7 — ring honesty.** The ring stores whole encoded frames only and
  parses on replay, so the advertised byte bound covers what is really
  retained; an empty ring reports the next seq rather than pretending an
  evicted one is available; a burned seq is never reused — an unencodable frame
  occupies its seq as an explicit `event/eventsDropped` marker.
- **I4 — call identity.** Tool calls are keyed by `(batchId, callId)` and owned
  by a turn, so a reused provider id cannot complete the wrong call and a late
  result from an ended turn cannot rewrite the running turn's live entries.
- **I5 — steer linearization.** The busy check and the enqueue run on one
  serialized path (`TURN → INBOX → STATE → BUS`, the same order
  `TurnGuard::drop` takes), so `rejectedReason` is exact rather than a guess.
- **I8 — truncation metadata.** Truncated `HistoryBlock` tool inputs/results
  carry `inputFullLength` / `fullLength` alongside the omitted reason, on
  UTF-8-safe caps.
- **Honest surface.** `usage` is wired from the real `Usage` event (it was
  permanently unknown); tool payloads carry the canonical `turnId` while every
  legacy alias keeps its value; `session/getState` rejects the `sections` filter
  it does not implement instead of ignoring it; unused `event/*` constants
  (`engineHello`, `steeringOutcome`) are removed rather than advertised, and the
  capability catalog gained `state.historyFence`, `state.usage`,
  `state.sectionFilter: false` and `state.awaitingUserInput: false`.

### 8b. Stage C re-review corrections (second pass)

- **I1 — closing false idle.** `end_turn` published `ready` while the turn
  guard still held `turn_active`, so `session/getState` said the engine was
  available and `session/prompt` answered "busy". Terminal outcome and
  availability are now published separately and coherently: `end_turn` reaches
  the terminal outcome (live reset, history fence, tool finalisation,
  `event/turnComplete` + `event/turnEnded`) and leaves `lifecycle: busy`;
  `EngineState::release_turn` publishes `ready` from inside `TurnGuard::drop`,
  after clearing the runtime flag under the same TURN lock. The transcript
  write moved *before* the terminal transaction, so no `.await` sits in the
  window at all. `release_turn` is a no-op if a new turn already claimed the
  slot, so a late guard cannot clobber its successor.
- **I2 — administrative mutations own the slot.** `session/fork` and
  `session/rewind` read `turn_active` and then awaited, letting a new prompt
  race the history replacement. Both now claim the ordinary `TurnGuard` for
  their whole duration and publish an `ActivityPhase::Maintenance` turn.
- **I3 — steering during `preparing`.** `Agent::run` was the first thing to
  unseal the inbox, so a client that saw the (now much earlier) public
  `preparing` phase and steered was told `turnEnding`. A prompt claim now
  calls `open_for_turn` under TURN, before `preparing` is observable —
  unsealing only, never discarding an already-queued draft. Compaction and
  maintenance claims seal instead (only when empty), so no message is accepted
  that could never be delivered.
- **I4 — silent state mutation.** `end_turn(wire: None)` — the
  compaction/preflight/guard-drop shape — mutated state and finalised tool
  calls with no event at all, and `turn_config_resolved` replaced the
  placeholder config silently. Both are published now: gated
  `event/turnEnded` (outcome, `historyEpoch`/`historyLength` fence, the
  finalised call ids, `toolsTruncated`), `event/lifecycle` and
  `event/configChanged`. Every declared constant has a publisher; the
  `state.turnEvents` capability documents exactly which sections are push-based
  and does not claim the whole snapshot is.
- **S3 — outcome privacy.** `lastTurnOutcome.error` was the raw
  `AgentError`/provider string, pollable indefinitely. It is now a bounded
  `TurnErrorSummary { category, status?, parameter? }` built from
  `coda_llm::diagnostics`; the raw message is still returned once in the RPC
  reply. Pinned by a sentinel-token regression.
- **S1 — retention budget honesty.** The live budget charged text only, so
  ids, tool names, labels and empty blocks were retained for free. Entries,
  blocks and chunks now carry fixed structural charges and identifiers are
  charged at length, which bounds counts as well as bytes; the doc states
  plainly that this is an accounted retention budget, not a measured memory
  bound.
- **S4 — negotiation order.** `stateEvents` is negotiated at the top of
  `initialize`, so a resume's own `event/sessionChanged` is actually written to
  the client that asked for it instead of being burned unsent.
- **S5 — shutdown states.** `stopping`/`stopped` were enum values nothing ever
  set; `shutdown` now publishes both.
- **Adjacent.** `EventBus` lock acquisition handles poisoning the same way the
  rest of the stack does.

### 8c. Stage D as implemented

What actually landed, and the judgement calls made where the plan left room.

- **Typed question outcome, end to end.** `coda_tool::UserQuestion::ask` and
  `coda_agent::agent::stop::UserQuestionPrompt::ask` both return
  `AnswerOutcome::{Answered, NoAnswer(NoAnswerReason)}`. `WireUserQuestion` no
  longer substitutes `options.first()` for anything. Reasons are closed:
  `noController | disconnected | cancelled | timeout | malformed | declined`.
- **A terminal control signal, not a tool-name hack.** `ToolResult` gained
  `control: Option<ToolControl>`; `ToolControl::AbortRun { reason }` is the
  only variant. `ask_user_question` returns it on `NoAnswer`; `run_tools`
  propagates it as `ToolBatchResult.control_abort` (kept distinct from the
  hook `abort_reason`, which ends a run *successfully*); `Agent::run` records
  the tool-result block — a `tool_use` with no matching `tool_result` is not a
  valid conversation — marks any remaining calls in the batch `Skipped`, and
  returns the new `AgentError::Aborted { reason }` **without issuing a
  follow-up model request**. `session/prompt` answers `ok: false`, and
  `lastTurnOutcome.error.category` is `agent.aborted.<reason>` (a fixed token,
  never provider text).
- **SubagentHost audit result.** It previously matched only
  `AgentError::Cancelled` and returned `Ok(collected_text)` for everything
  else, so a new error variant would have been reported to the parent model as
  a finished piece of work. `Aborted` is now an explicit `Err`, and the task
  manager marks the task failed.
- **One registry, exactly once.** `state/requests.rs` owns every pending
  reverse request; `PromptChannel` and the three new RPCs both resolve through
  `resolve_*`, which peeks the entry's *kind*, validates the offered outcome,
  and only then removes it. A duplicate, a wrong-kind payload or a malformed
  payload therefore cannot consume an entry — and cannot turn a denial into a
  grant. Handles are `req-<engineInstanceId>-<n>`, so a handle from a previous
  process is a typed rejection rather than a silent re-application. An
  abandoned `issue` future withdraws its entry through an RAII guard.
  Registration → `event/requestPending` → wire frame, in that order.
- **Raw vs out-of-band malformed replies differ deliberately.** A malformed
  *response to the original `request/*`* is terminal and fail-closed (the
  client answered; the answer was unusable). A malformed
  `session/resolveRequest` payload is a correctable error and leaves the
  request outstanding.
- **`awaitingUserInput` is real.** Entered when a request goes outstanding
  during a turn, left when the last one resolves, restoring the phase the turn
  was actually in (so a question inside a tool batch returns to
  `runningTools`, not to an invented phase). The capability flipped to
  `supported` in the same change.
- **History reads share the turn-commit boundary.** `session/getHistory` takes
  `HISTORY` and then `EngineState::history_view()` — the same order and the
  same instant the commit transaction uses — so the committed prefix, the
  `historyLength` fence, `historyEpoch`, `cursor` and `liveEntries` in one
  response cannot disagree. Three optional fences (`historyEpoch`,
  `expectedHistoryLength`, `engineInstanceId`) make paging exact; each
  mismatch is a distinct typed error rather than a partially-valid page.
- **`entryKind` (open item, resolved).** A provider conversation encodes tool
  results as user-role messages. `HistoryEntry` keeps `role` exactly as the
  protocol has it and adds `entryKind` (`userPrompt`/`toolResults`/
  `assistant`), derived in `HistoryEntry::new` so no call site can forget it.
  This is the mechanical guard against a future TUI replaying tool results as
  operator prompts.
- **Open item B resolved: events, not the committed message.** `state/live.rs`
  still reconstructs purely from emitted events; committed history is
  projected separately from `coda_llm::Message` in `history.rs`. Belt-and-
  braces reconciliation was rejected as duplicated projection logic for no
  observable gain, and the two views are pinned against each other by the
  conformance test instead.
- **Open item C resolved: real accessors.** `McpClientManager::connected_status`
  was cheap (it reads what the manager already holds), so `mcp/list` ships
  with real per-server tool counts rather than `status: "unknown"`. It performs
  no connection attempt and, deliberately, **no credential-store read** — so
  `secretRefs[].resolved` is always `false`, meaning "not verified by this
  call", and is documented as such rather than discovered by probing.
- **`config/describe` publishes output styles** so the TUI can drop its
  `coda_agent::BuiltInOutputStyles` call, while honestly reporting that this
  engine does not *apply* an output style (nothing reads it today).
  `config/set` refuses immutable keys using the catalog's own reason string,
  so `describe` and `set` cannot drift apart.
- **Initialization gate scope.** Applied to the five new state-dependent
  methods only (`INITIALIZATION_GATED_METHODS`), enforced in `dispatch` as one
  visible table rather than per-handler. Pre-existing routes are untouched;
  read-only discovery stays valid before `initialize`. Pinned by tests on both
  sides of the boundary.
- **Timeout stayed off.** `CODA_SERVE_REQUEST_TIMEOUT` is parsed once at
  construction; `0`, absent and unparseable all mean "no timeout". An
  unreadable value must never silently become a short one.
- **Test shape.** The SSE fixture was factored into
  `crates/coda-engine/tests/support/mod.rs` and extended with a
  request-recording scripted provider — which is what makes "**no** follow-up
  model request was issued" directly assertable rather than inferred.
  `state_conformance.rs` was refactored onto it and re-verified.
- **Honest gap — withdrawn (see §8d).** This section previously claimed a
  genuine stdin-EOF disconnect could not be produced out of process. That was
  wrong: only `coda-client`'s `Responder` forces the write channel to stay
  open. A raw `tokio::process::Command` with piped stdio and the public
  framing primitives can half-close the engine's stdin for real, and
  `coda-engine/tests/eof_conformance.rs` now does exactly that.

### 8d. Stage D review corrections (backend/proto/conformance/docs)

Seven findings from the Stage D review, each fixed test-first (a failing
assertion before the change, not a compile error).

1. **Free text could be truncated silently.** `history.rs` capped `Text` and
   `Thinking` at `TEXT_BLOCK_CAP` and threw the original length away, so a
   64 KiB prompt or reasoning summary was indistinguishable from a complete
   one. `HistoryBlock::Text`/`ReasoningSummary` gained `omittedReason` +
   `fullLength`, and `HistoryBlock::text_capped`/`reasoning_capped` now own
   the pairing so no call site can keep the truncation without the marker.
   `RedactedThinking` deliberately carries neither: its text is empty by
   policy, not by a cap. The live accumulator was audited too and now tracks
   omissions **per block** rather than only per turn (`liveBudgetExceeded`),
   and an oversized operator prompt is capped-and-marked instead of dropped
   whole — dropping it made a turn look like it had no prompt at all.
2. **`optional()` swallowed malformed params.** It deserialised the whole
   object or fell back to `Default`, so `{"historyEpoch": "abc"}` produced an
   unfenced page 0 and `{"sections": "turn"}` produced the whole snapshot —
   defeating the exact fences those parameters exist to provide. The new
   `optional_strict` (absent/`null` may default, supplied-and-malformed is
   `-32602`) is applied to the three methods this contract introduced;
   pre-existing routes keep their tolerant parsing, and a test pins that
   boundary as deliberate. Page limits are validated rather than
   reinterpreted: `limit < 1` and negative `sinceIndex`/`expectedHistoryLength`
   /`historyEpoch` are `-32602`; over-max is clamped, with the ceiling
   published as `limits.maxHistoryPage`/`maxSessionPage`. The tests drive the
   real `dispatch()` and assert on the params the backend actually received.
3. **`historyEpoch`/`historyLength` were not atomic with the reset.**
   `fork`/`rewind`/`compact`/`resume` released the HISTORY lock before calling
   `session_changed`, so a concurrent `session/getHistory` could take HISTORY
   in the gap and be handed a page from the *new* conversation under the *old*
   epoch. Reproduced by a race test that caught it immediately (epoch 37
   accepted with a two-message page from a six-message conversation). All four
   paths now hold the HISTORY guard through the state update and the bus
   publish, as `send_turn` already did, and read the committed length from the
   guard rather than from a copy taken before an `await`. No `STATE ->
   HISTORY` inversion exists: every site takes HISTORY first.
4. **The pending-request registry published projections after releasing its
   lock.** Two concurrent operations could deliver their lists out of order,
   and the state layer assigns whatever it is handed — so a late `[]` from a
   resolution erased a request registered after it and restored the turn phase
   out of `awaitingUserInput` while the engine really was still waiting on the
   operator. `register`, `resolve_inner`, `fail_all` and the new
   `withdraw_and_publish` now hold REQUESTS across the observer callback
   (`REQUESTS -> STATE -> BUS`, no `.await`, no re-entry). `withdraw` +
   `publish_resolved` were merged precisely because a two-step API reopened
   the window. Covered by a stalling-observer test in both directions plus a
   concurrent convergence test and an exactly-once contention test.
5. **`secretRefs[].resolved: false` was a claim, not a non-answer.** It is now
   `Option<bool>` and omitted, because `mcp/list` deliberately performs no
   credential-store read and `false` states as fact that the reference does
   not resolve. Only a startup fact the engine already holds may fill it in.
   Alongside it, `config/describe` entries gained `allowedValuesFrom`, so an
   indeterminate `allowedValues` names its live authority
   (`model/reasoningCapability` for `effort`, `session/models` for `model`)
   instead of leaving a client to guess.
6. **Real stdin-EOF conformance.** `coda-engine/tests/eof_conformance.rs`
   drives the compiled binary through a raw `tokio::process::Command` with
   piped stdio, using `encode_frame`/`FrameDecoder` directly (no
   `coda-client`, no `Responder`), waits for the `request/question` frame,
   drops stdin, and waits for a bounded exit: the scripted provider must have
   served exactly one request and nothing may claim an answer. A control test
   in the same file proves the harness can complete a turn when the answer
   does arrive. The *deterministic* proof that EOF resolves the request
   fail-closed lives beside the code, in a `transport.rs` test that drives the
   real `read_loop` against a closed pipe — verified to fail (hang, then
   timeout) when `fail_all_pending` is removed.
7. **`PendingRequestDto.callId` is documented as reserved.** The
   permission/question/plan-approval seams carry no provider tool-call id, so
   the field is always omitted and a new `requests.callCorrelation`
   capability says `supported: false` with the reason. Nothing claims a
   correlation the engine cannot provide, and the wire shape still allows a
   host that *does* have the id to fill it in later.

Not changed, deliberately: the legacy `optional()` behaviour on pre-existing
routes; the safety spine (`NoAnswer` aborting the tool/goal with no second
provider request, schema validation before consumption, instance binding);
and the `awaitingUserInput` phase machine.


13. **Pre-launch session discovery.** Making `--resume/--continue/--fork` spawn
    the core *before* choosing a session adds a process start to the path that
    currently just reads a directory, and `session/listSessions` is served
    before `initialize`. Is "read-only methods are valid pre-initialize" an
    acceptable rule, or should there be an explicit discovery handshake?
14. **Dropping `coda-agent`/`coda-mcp` from `coda-tui`.** It is the strongest
    guarantee that no in-process path exists, but it forces every remaining
    shared type (session summaries, output-style names, MCP scope labels)
    through the wire contract. Some of these are static data where an RPC feels
    heavy; the alternative is a small shared display-types crate, which weakens
    the guard.
15. **Engine lifetime expectations.** Under §2.8 the engine dies with the TUI
    that started it. Users who currently expect a long turn to survive a closed
    terminal will only get that from the external orchestrator. This is a
    deliberate boundary, but it should be surfaced in the UI rather than
    discovered.
16. **`mcp/list` is a new surface with secret-adjacent data.** `.mcp.json`
    carries `env` values and `coda-secret:` references
    (`coda-tui/src/config.rs:541-559`). The DTO must expose names and
    resolution status only; the review should treat any field addition here as
    security-relevant.

### 8e. Stage E as implemented

What actually landed for "the TUI is an ordinary API client", and the
judgement calls made where the plan left room.

- **A focused module, not more of the loop.** `coda-tui/src/api/`
  (`view.rs`, `history.rs`, `requests.rs`, `boot.rs`, `mod.rs`) plus
  `src/app/serve.rs` for the App-side seam. `app/mod.rs` stayed under its
  1600-line ceiling by moving the constructors out with the bootstrap they
  now perform; the ceiling test is unchanged.
- **The fence is its own type.** `ServeView` answers exactly one question —
  "may this frame be applied?" — and owns nothing the UI draws. In-order
  frames advance the cursor; a frame at or below the snapshot cursor is a
  refused duplicate; a real gap arms a resync *and keeps the frames after the
  gap* so re-snapshotting loses nothing; a frame from a different
  `engineInstanceId` resets outright rather than reading as a catastrophic
  gap. A legacy engine (no envelope) applies everything unfenced, exactly as
  before. No snapshot is taken per delta: only on connect, on a gap and after
  an engine-owned reset.
- **State frames carry metadata; legacy frames carry content.** They are
  applied alongside each other because they cannot double anything. The one
  place both describe the same moment is the end of a turn, and there they
  say different things: `event/turnComplete` reports the last output,
  `event/turnEnded` carries the history fence, and the slot is released
  separately.
- **Busy-after-complete, and the fact that made it non-obvious.** The
  integration test caught a real bug here. The engine does **not** publish
  `event/lifecycle: busy` when a turn starts — `begin_turn` publishes the turn
  *phase*. So a client waiting for a busy frame waits forever and falls back
  to believing `turnComplete`, which is exactly the "UI says ready, engine
  says busy" failure. The client therefore treats an `event/activity` frame
  (published only while a turn is open) as the taken edge, and
  `event/lifecycle: ready` from `release_turn` as the released edge. No core
  change was made for this; the asymmetry is now documented and tested.
- **Server-monotonic clock.** `TurnProgress::rehydrate(elapsed_ms, now)`
  seeds the local monotonic origin with the engine's own duration. Not
  re-derived from `startedAt` (a remote wall clock makes the timer jump by the
  machines' skew, and can run backwards) and not reset to zero (a four-minute
  turn would claim it had just started). A rehydrated turn claims no reasoning
  it did not observe.
- **Optimistic submission is preserved and bounded.** A snapshot taken between
  a local submit and the engine hearing about it honestly says "ready".
  `optimistic_submit` is what stops that honest answer from flipping the UI
  back under a prompt the user has already sent; it clears on the first real
  acknowledgement.
- **Queue reconciliation keeps the user's text.** `pending[].text` is the full
  original, so adopting it cannot downgrade a draft. The one case where it
  could — an over-cap message the engine marked `textTruncated` — keeps the
  local copy. A locally queued message the engine has not acknowledged yet
  (no id) is kept, not dropped, and `unsent` is never touched by
  reconciliation: it is the user's own recoverable text and the engine has no
  opinion about it.
- **History hydration renders a conversation.** `entryKind` is the only
  discriminator consulted, so a user-role tool-result message is never
  replayed as an operator prompt. Results match on `(turnId, batchId, callId)`
  — a provider id reused in a later batch cannot rewrite the earlier call —
  falling back to id-only matching *only* for transcripts that carry no
  correlation at all. An orphan result is reported, never attached to the
  nearest plausible call. Omission markers are rendered with their
  `fullLength`. Nothing is invented: no timestamp on a stored message, no
  reasoning duration, no token count, and reasoning signatures are neither
  requested nor carried.
- **Grouping did not regress to one-per-batch.** Canonical `batchId`s now
  exist, and grouping by them would have turned a turn that ran twelve batches
  back to back into twelve collapsed groups. Hydration keeps the UI-boundary
  rule the live reducer uses, pinned by a test.
- **`same_call` gained turn/batch guards.** Rehydration makes it genuinely
  possible for one buffer to hold a replayed call and a live call sharing a
  provider id. `call_id` alone is no longer an identity: when both sides name
  a turn or batch, those must agree. Either side omitting them falls back to
  the previous behaviour.
- **One registry for pending decisions.** `PendingInteractions` merges the raw
  round-trip and discovery on the engine's opaque handle, which is now carried
  on the `request/*` frame as an additive optional `requestId` (proto + serve +
  docs + tests). The handle is compared, never parsed. Answering prefers the
  raw responder — the engine's own exactly-once path, so no responder is ever
  abandoned while an out-of-band resolution is in flight — and falls back to
  `session/resolveRequest` only for a request whose frame this client never
  received. A declined question goes through `session/cancelRequest`
  (`declined`), never `{"answer": ""}` (`malformed`). Concurrent requests
  queue oldest-first; answering resolves exactly the one on screen.
  Reconciling against a discovery list never drops an entry we hold a
  responder for, because the engine registers before it writes the frame.
- **De-privatisation.** `startup::resolve()` is **deleted**, not relocated:
  moving it into `coda-boot` would have satisfied a naive dependency grep
  while changing nothing about who reads the engine's files. Session listing
  is `session/listSessions`, the conversation is `session/getHistory`, output
  styles are `config/describe`, MCP display is `mcp/list`, and forking is the
  engine's own RPC. `coda-agent` moved to `[dev-dependencies]` (test fixtures
  write real saved transcripts). `coda run` shares the same bootstrap helper,
  so headless and interactive cannot drift.
- **`--continue` in an empty directory now starts a session** instead of
  failing, and says so. `--resume`/`--fork`, which name something that should
  exist, still fail loudly. An explicit `--resume <id>` goes straight to
  `initialize` — the engine's typed `sessionNotFound` is the validation, so
  there is no second round-trip.
- **Access mode is client-selected.** `AccessMode::for_launch(custom_engine)`;
  a custom `--engine`/`CODA_ENGINE` is treated as somebody else's machine.
  Never an engine-advertised `isLocal` — the engine cannot know whether its
  client is a local terminal, and a test asserts no code derives locality from
  it. Every gated write path answers "managed on the engine host" *before*
  touching the filesystem. The default launch (`coda` starting its own core)
  keeps every shipping editor working unchanged. Appearance, clipboard and
  image files are the client's own in every mode and are not gated.
- **Guarded by test.** `tests/conventions.rs` gained: no `coda_agent::` /
  `AgentLoop` / `SessionTranscriptStore` anywhere in `src/`; no `.coda/sessions`
  path construction; `coda_mcp::` only under `src/local/` and only
  `coda_mcp::config`; an allow-list manifest rule (`coda-agent` in
  `[dependencies]` of the engine crates only) rather than a blanket ban; a
  direct check that `coda-boot` neither depends on the agent nor reads session
  files — the laundering guard; and the "locality is never a server claim"
  rule.
- **Tests.** `crates/coda/tests/tui_client.rs` (14) drives the compiled
  `CARGO_BIN_EXE_coda serve` over real stdio against a streaming loopback SSE
  fixture, with temp `CODA_HOME`/cwd, `--no-mcp` and a fake credential — no
  PATH lookup, no skip, no real profile. It covers the negotiated handshake
  and fence seeding, a resumed session rendering the *actual* old
  conversation, a genuinely non-zero mid-turn server clock behind an explicit
  barrier (no `sleep` as a synchroniser), queue reconciliation and lossless
  recall, the busy-after-complete boundary, and the local-vs-API-only gate.
  The bootstrap's method sequence is captured against an in-process recording
  server driving the **real** `resolve_and_initialize` — which is why
  "`--continue` reads no files" is an assertion about the shipped code path
  rather than about a re-implementation.

**Honest remaining gaps in this stage.** The engine publishes no
`lifecycle: busy` edge (worked around client-side, documented above, not
changed in the core). The MCP browser is source-aware: a trusted-local session renders the local file (so the editor shows exactly what it is about to edit, unresolved secret references included), an API-only session renders `mcp/list` read-only with a "managed on the engine host" footer and no editing keys. That is deliberate asymmetry, not a gap, but it does mean the two modes show different columns of truth.
Steering-outcome and `event/eventsDropped` handling re-snapshot rather than
applying the frame's own payload, which is correct but coarser than it could
be. `config/set` is not yet used by any slash command; the existing validated
`session/set*` methods are still called directly.