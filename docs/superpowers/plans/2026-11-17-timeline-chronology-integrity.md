# Timeline Chronology Integrity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A history refresh never moves an old record (completion card, notification, local output) to the bottom, and never presents it as new. Records keep their identity and original position across refreshes, restarts, compaction and rewind, or are explicitly marked historical.

**Architecture:** The engine owns a bounded presentation timeline of records. Each record has a stable item id and an anchor to committed conversation entries; the timeline is served alongside `session/getHistory` and persisted in the session transcript file. The TUI reconciles by identity: it places records and its own local overlay blocks by anchor, and puts anything unplaceable in an explicit historical area. Spec: `docs/superpowers/specs/2026-11-17-timeline-chronology-integrity-design.md`.

**Tech Stack:** Rust workspace (`coda-proto`, `coda-serve`, `coda-agent` session store, `coda-tui`), serde, tokio.

---

### Task 1: Protocol types (`coda-proto`)

**Files:**
- Create `rust/crates/coda-proto/src/presentation.rs`.
- Modify `lib.rs`.
- Modify `history.rs`: add `GetHistoryResult.presentation: Option<PresentationTimeline>` with `#[serde(default, skip_serializing_if = "Option::is_none")]`.
- Modify `events.rs`: add `event/presentationRecord` and `Event::PresentationRecord { record }`, plus parse/serialize round trip.
- Regenerate the schemas and the catalog.

```rust
pub const PRESENTATION_SCHEMA_VERSION: u32 = 1;
pub struct PresentationAnchor { pub after_entries: i64, pub turn_id: Option<String> }
pub enum PresentationRelation { Active, Compacted, Rewound, Unknown }
pub enum PresentationPayload { TaskCompletion { .. }, Notification { .. } } // tag "kind"
pub struct PresentationRecord { item_id, position_seq: u64, revision: u32, change_seq: u64,
    occurred_at: Option<String>, anchor, relation, payload }
pub struct PresentationTimeline { schema_version: u32, timeline_id: String, change_seq: u64,
    records: Vec<PresentationRecord>, evicted_before_seq: Option<u64>, durability: String }
```

- [ ] Write round-trip tests and the "unknown fields / absent field still parse" test; implement; run `cargo test -p coda-proto`; commit.

### Task 2: Engine timeline store (`coda-serve/src/state/presentation.rs`)

Pure, bounded store:
- `accept(item_id, payload, anchor, now) -> Option<PresentationRecord>`: idempotent; a content change bumps the revision.
- `resolve_turn(turn_id, committed_len, rewritten)`.
- `compacted()`, `rewound(new_len)`.
- `fork(new_timeline_id)`.
- `to_snapshot()`.
- `to_persisted()` / `from_persisted(value, loaded_len)`: corrupt → durability `corrupt`; an anchor beyond the loaded length → `unknown`; a pending turn → resolved at its recorded `after_entries`.
- Eviction at 256 records / 256 KiB, payload caps.

- [ ] Write unit tests for each spec bullet (identity, reuse after restart, identical titles, revision, eviction, compaction, rewind, fork, persistence round trip, corrupt input); implement; commit.

### Task 3: Engine wiring

- `StateInner.presentation: Arc<PresentationTimeline>`.
- `EngineState` gets:
  - `accept_presentation(...)`: transact; publishes `event/presentationRecord`; records diagnostics.
  - `presentation_alias(original_id)`.
- `end_turn` gains `history_rewritten: bool` and resolves the turn's anchors.
- `session_changed` applies compact / rewind / fork.
- `install_presentation(timeline)` on resume.
- `HistoryView.presentation` → `build_live_result` / `build_saved_result`.
- `MessageBusEventBridge::on_published` → `accept_presentation`, with notification ids taken from the alias when one is set.
- `TaskEventPublisher` gets `set_presentation(Weak<EngineState>)`; `on_terminal` accepts records for main-agent background tasks.
- `after_initialize_response` sets the alias before each republish.
- Capability `presentationTimeline` (v1).
- `SessionTranscriptStore`:
  - `save_presentation`;
  - `save_with_usage` keeps the existing `presentation` member;
  - `StoredSession.presentation`.
- Host:
  - the turn-end save includes presentation;
  - a persister task writes after accepts, rewinds and compactions;
  - resume / initialize-with-session loads and installs the timeline.
- `coda-diagnostics`: a `PresentationRecord` event.

- [ ] Write host-level tests:
  - a completion is accepted with an anchor;
  - getHistory carries it;
  - rewind and compaction change the relation;
  - a resume reloads the same ids;
  - a republished restored notification is the same item.
- [ ] Implement; commit.

### Task 4: TUI transcript placement

- `transcript.rs`: `BlockMeta`/`Placement`, a parallel vector kept in sync by `push`, insert, retain, clear and replace.
- `set_fence(epoch, length, turn_running)`, `resolve_turn_end(length)`, `upsert_record(...)`.
- `replace_conversation(conversation: Hydration-with-entry-ends, records: &[PresentationRecord], fence)` placing blocks by anchor, with the historical area.
- `api/history.rs`: `Hydration.entry_ends`.
- [ ] Write unit tests: the reproduction, repeated refresh, an older revision, paging, compaction, local overlay ordering, task-card expansion.
- [ ] Implement; commit.

### Task 5: TUI state / app wiring

- `UiState`:
  - consumes `Event::PresentationRecord`, holding records that arrive during a running turn until the turn ends, and showing a hint only for an unseen live record;
  - ignores the legacy `TaskCompleted`/`AgentMessage` for block creation when the engine advertises the capability;
  - `UiEvent::Rehydrated` carries `presentation`.
- `App`:
  - passes the capability and the fence (from `view` after each dispatch);
  - does not let pending-messages recovery synthesise blocks when the capability is present.
- `coda-diagnostics`: a `TimelineRebuild` event.
- [ ] Write tests:
  - the end-to-end reproduction through a real `ServeHost` → getHistory → TUI;
  - a stream is not split;
  - both arrival orders converge (property test);
  - zero RPCs during recovery.
- [ ] Implement; commit.

### Task 6: Docs, version, verification

- `docs/serve-protocol.md`, `docs/protocol/catalog.md`, the schemas, the README note, `version.json` = 183.
- Full verification: `cargo test --workspace`, the schema tests, the engine contract, clippy on touched files.
- PR, merge, release build and install (`rust/build.ps1 -NoBump -Deploy`), and check that `coda --version` prints 0.1.183.
