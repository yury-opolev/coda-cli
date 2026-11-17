# Timeline chronology integrity — design

Date: 2026-11-17. Scope: sections 2–10, 13.1 and 14 of the upstream request
"preserve timeline chronology and multiline input in Coda TUI" (the timeline
half; the paste half shipped as PR #220).

## Problem

A history refresh moved old task-completion cards (and other records that
are not part of the model conversation) to the bottom of the transcript.

Reproduction: a user asks for a background task; "Task completed: X" appears;
the user starts unrelated work; the assistant answers. Any authoritative
refresh (`UiEvent::Rehydrated`) then renders:

1. the request, 2. the unrelated work, 3. the answer, 4. **Task completed: X**.

Every further exchange plus a refresh moves the same card to the bottom
again. There is still exactly one card, and no new completion happened.

### Root cause (confirmed in source)

* `Transcript::replace_conversation` (`coda-tui/src/transcript.rs`) keeps the
  client-owned blocks that precede the first conversation block. It then
  inserts the rebuilt conversation and **appends every other retained block
  after it** (before a streaming tail). So the position of a retained block
  relative to the conversation is thrown away on every rebuild.
* `Block::is_client_owned` puts engine facts (task completion cards, passive
  agent notifications) in the same class as terminal-local output. Being
  absent from model history was mistaken for being owned by the terminal.
* `upsert_task_card` deduplicates by `(session, task id)` but records no
  relationship to the conversation, and task ids are process-local numbers
  that a restarted engine reuses.
* Nothing engine-side remembers these records. A restarted engine shows no
  completions at all, and notifications come back only through the volatile
  message-bus ring.

## Decisions

| Option | Verdict |
| --- | --- |
| Local anchors on the mixed `Vec<Block>` only | Rejected as the whole fix: it cannot survive restart or compaction. Kept as the legacy fallback and for genuinely local artifacts. |
| **Engine-owned bounded presentation records plus an anchored client overlay** | **Chosen.** |
| Full durable event log | Rejected: storage/privacy cost far beyond the defect. |

### 1. Presentation records (engine)

`coda-serve/src/state/presentation.rs` adds a bounded `PresentationTimeline`
per live session, held in `StateInner` behind an `Arc`, so it is copied on
write and mutated only inside `EngineState::transact`. A record
(`coda_proto::presentation::PresentationRecord`) has:

* `itemId`: stable occurrence identity.
  * Task completion: `task:<engineInstanceId>:<taskId>`. The engine-instance
    UUID scopes the process-local task number, so a reused `t1` after a
    restart is a different item.
  * Notification: `notification:<messageId>`. Bus ids are UUIDs.
* `positionSeq`: immutable acceptance order within the timeline.
* `revision` and `changeSeq`: the content version and the latest mutation, in
  a separate counter.
* `anchor`: `{ afterEntries, turnId? }`. The record sits after the first
  `afterEntries` committed conversation entries. While the turn it happened in
  is still running, `turnId` names that turn; when the turn commits, the
  anchor resolves to the committed length at the end of that turn. A record
  that occurs during a turn belongs at that turn's boundary. It is never
  placed in the middle of a streaming reply.
* `relation`: `active`, `compacted` (its anchor range was replaced by a
  compaction), `rewound` (its anchor range was removed by a rewind) or
  `unknown` (the anchor does not fit the conversation it was restored with).
* `occurredAt`: RFC 3339 time at acceptance (explanatory only; never a sort
  key).
* `payload`: bounded, already-UI-safe content.
  * `taskCompletion`: task id, status, kind, title, instructions (capped at
    4 KiB).
  * `notification`: label, text, context, source, task id, schedule id (the
    bus's own caps).

Accepting the same `itemId` again is idempotent. Identical content changes
nothing. Different content bumps `revision`/`changeSeq` and keeps
`positionSeq` and the anchor. Bounds: 256 records and 256 KiB of payload; the
oldest records are evicted first and `evictedBeforeSeq` says so.

Sources:

* `TaskEventPublisher::on_terminal` covers main-agent background tasks only,
  the same filter the TUI applied: no foreground tasks, no owner.
* `MessageBusEventBridge::on_published` covers passive notifications. Lock
  order stays `BUS -> STATE`.
* Planned-restart republication of saved notifications maps the new bus
  message to the saved id, so a restored notification is the same item, not
  a new publication.

Anchor maintenance happens in the same transaction as the event that moves
the conversation:

* `end_turn` resolves the turn's pending anchors. If the commit was not a pure
  append (auto-compaction inside the turn), earlier active anchors become
  `compacted` first.
* `session_changed("compact")` marks active anchors `compacted`.
* `session_changed("rewind")` marks anchors beyond the new length `rewound`.
* `session_changed("fork")` starts a new, empty timeline (new `timelineId`).
  The fork does not inherit records, unread state or running work.
* Resume installs the timeline loaded with the session.

### 2. Read contract (protocol, additive)

* `GetHistoryResult.presentation` (optional): `{ schemaVersion: 1,
  timelineId, changeSeq, records, evictedBeforeSeq?, durability }`. It is read
  under the same HISTORY → STATE locks as the page, so the conversation and
  records describe one generation. Saved-transcript reads return the stored
  records.
* `event/presentationRecord` carries a record whenever it is accepted or
  changed, in the same state transaction as the change.
* Capability `presentationTimeline` (version 1) in `initialize.capabilities`.
* The legacy `event/taskCompleted` / `event/agentMessage` are unchanged for
  older clients. A capable client takes cards and notifications only from
  records and never synthesises a second card from a legacy event.

### 3. Persistence

Records persist inside the session transcript file as a `presentation` member,
in the same atomic write as the messages. The checkpoint is one file, so the
messages and their presentation metadata always come from the same commit.

* A turn-end save writes both.
* Accepting a record while idle, a rewind and a compaction write
  `presentation` alone through `SessionTranscriptStore::save_presentation`
  (read-modify-write of the same file under the store's single writer lock).
* Saves that do not pass presentation keep the existing member, as usage
  already is.
* A corrupt member loads as an empty timeline with `durability: "corrupt"`,
  reported to the client. It is never presented as an empty history.
* Records whose anchor exceeds the loaded conversation become `unknown`.
* An older Coda build that rewrites the file drops the member (documented
  downgrade behaviour; the newer build then shows no records for it).

### 4. Client reconciliation (TUI)

`Transcript` keeps a parallel `Vec<BlockMeta>`; every block gets a placement:

* `Conversation`: rebuilt from history on every read.
* `Record { itemId, positionSeq, anchor, relation }`: engine records, and also
  live legacy cards, which get a client-derived anchor.
* `Local { anchor, localSeq, afterRecordSeq }`: banner, notices, command
  output, diffs, permission and question records, session boundaries. The
  anchor is captured from the client's fence (`historyEpoch`,
  `historyLength`, turn running) when the block is created. An anchor taken
  during a turn resolves when that turn's `event/turnEnded` reports the new
  length.

`replace_conversation` builds a fresh order from the hydrated conversation:

* `api::history::hydrate` now reports where each committed entry ends.
* Records and local blocks are inserted after the entries their anchors name.
  At one anchor, the order is records by `positionSeq`, with local blocks
  interleaved by what they had already seen.
* Anything whose anchor is unknown, or whose anchor is outside the loaded
  page, rewound, compacted or from another epoch, goes into an explicit
  **historical area** at the top. It sits under a marker ("Earlier activity
  — its original position is not in the loaded conversation"). It is never
  appended after the newest content.
* The generic "append retained blocks after history" fallback is removed.

The same card arriving live and through a snapshot is one block, keyed by
`itemId`. A record already present keeps its index and position; a higher
revision updates content in place; a lower or equal revision is ignored.

**Streams.** A record that arrives while a turn is running is not inserted
into the streaming transcript. The client shows a transient hint ("Task
completed: …") and materialises the card when the turn ends, at the turn
boundary its anchor names. So a notification can never split an assistant
response or take over the streaming tail.

**Freshness.**
* Only a live record event the client has not seen before produces a hint.
* Records installed from a snapshot are silent.
* Recovery never calls an RPC that publishes, delivers to the model,
  recreates a schedule or starts a task. A test asserts that installing
  snapshots makes zero engine requests.

**View state.** Task-card expansion stays keyed by `(session, task id)`
(PR #216). Tool-group folds are still index-based and are dropped on a
rebuild, as before.

### 5. Diagnostics (metadata only)

* `presentation_record { phase: accepted|amended|resolved|evicted|restored, kind, relation }`
  (engine).
* `timeline_rebuild { records, placed, historical, local, generation }` (TUI).

No bodies, labels or titles are logged.

## Acceptance coverage (tests)

| Scenario | Test |
| --- | --- |
| Old completion, newer exchange, repeated refresh | `tests/timeline_chronology.rs::an_old_completion_stays_where_it_happened_across_refreshes`. It runs through the real chain: a serve host accepts a task completion → `session/getHistory` → TUI hydrate → `replace_conversation`. It asserts the full order, exactly one card, and no hint. |
| Notification during an open assistant/reasoning block | `state.rs::a_record_during_a_stream_waits_for_the_turn_boundary` |
| Snapshot before event and event before snapshot | `timeline` property test plus `snapshot_and_live_converge_in_either_order` |
| Duplicate / older revision | `transcript::an_older_revision_never_rolls_back_or_moves_a_record` |
| Update to an old item after the cursor advanced | `transcript::a_revised_old_record_updates_in_place` |
| Identical labels from different runs | `presentation::identical_titles_from_different_runs_are_distinct` |
| Task number reused after restart | `presentation::a_reused_task_number_in_a_new_engine_is_a_new_item` |
| Compaction removes the anchor | `presentation::compaction_marks_records_compacted` + `transcript::compacted_records_go_to_the_historical_area` |
| Paged history omits the anchor | `transcript::an_anchor_outside_the_loaded_page_is_historical` |
| Rewind / fork | `presentation::rewind_marks_later_records_rewound`, `fork_starts_an_empty_timeline` |
| Restart | `host` test: records persist in the transcript and reload with the same ids, order and relation |
| Corrupt / truncated checkpoint | `store::a_corrupt_presentation_member_is_reported_not_emptied` |
| Display-only recovery makes no requests | `app::serve` test: installing records makes zero engine requests |
| Folds and expansion survive a refresh | `transcript::task_card_expansion_survives_rebuild_with_records` |

## Deferred / limitations

* **Conversation entry identity is `(history generation, index)`.** Coda's
  history is append-only between rewrites, and every rewrite (compaction,
  rewind, fork, resume) bumps the epoch, so within a generation an index is
  stable. Stable per-message ids carried through provider serialization were
  not added.
* **Records anchored inside a compacted range are shown together** in the
  historical area under a "before compaction" marker. Anchors that pointed
  into the verbatim tail the compaction kept are also treated as compacted.
  Remapping them would need message identities, and the alternative is
  matching text, which the request forbids.
* **Client-local artifacts created during a running turn** (permission
  outcomes, local notices) are anchored to that turn's end. Live they appear
  inline. On the first rebuild after the turn they appear at the turn
  boundary, and they never move after that.
* **No independent pagination for records.** A snapshot carries every
  retained record (at most 256). There is no snapshot token for paging
  records separately from the conversation page.
* **No per-client unread state.** Coda has no unread indicator. "Fresh" means
  a live, unseen event and only drives a transient hint.
* **A fork does not carry records over.** Its inherited conversation shows no
  cards from before the fork.
* **Older builds that rewrite a transcript drop its presentation member.**
* **Legacy engines without the capability** get client-derived anchors for
  live cards. Placement survives refreshes in the same TUI process, not a
  client restart.
