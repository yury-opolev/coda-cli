# Task-completion provenance: keep the model frame out of the user transcript

Date: 2026-11-25 · Status: approved (owner away; decisions made autonomously)

## Problem

A main-agent background task that finishes is delivered to the model as a
`[task-completed] … <task_output>…</task_output>` frame. The engine appends it
as a provider-role **user** message (`Message::user`), the live projection and
the committed projection both classify it as `HistoryEntryKind::UserPrompt`,
and the TUI renders it as `Block::User` — the full frame, output tail
included, styled as something the operator typed. The compact task card
(presentation record `task:<engine>:<task>`) is a separate, correct path.

## Approaches considered

1. **Trusted provenance (chosen).** Record where the message came from when
   the runtime creates it, carry it through persistence and projection, and
   let the client consult it. Exact; no guessing from text.
2. **Prefix heuristic in the TUI.** Hide user entries that start with
   `[task-completed]`. Rejected: an operator can type or paste that text.
3. **New `HistoryEntryKind` value.** Rejected: older clients fail to
   deserialize an unknown enum value.

## Design

### Engine (coda-llm / coda-agent)

- `coda_llm::Message` gains `origin: Option<MessageOrigin>`, set only by
  trusted runtime code. `MessageOrigin { kind: MessageOriginKind::TaskCompletion,
  message_id, task_id, presentation_item_id }`. Providers ignore it; the model
  still receives the unchanged frame.
- `MessageBus` gets an optional presentation scope (the serve host sets it to
  its engine instance id). A `taskCompletion` `MainMessage` then carries the
  presentation item id `task:<scope>:<taskId>` — minted by the same helper the
  presentation record uses (`tasks::completion_push::task_presentation_item_id`).
- `MainMessage::origin()` builds the origin; the agent loop appends
  `Message::user(item.injected_text()).with_origin(item.origin())`.
- `session::message_json` writes an optional `"origin"` object and reads it
  back; unknown kinds or malformed objects read as no origin. Old transcripts
  load unchanged. Compaction keeps tail messages by clone, fork copies the
  history, restart re-reads the transcript, so the origin survives all three.
- Compaction's exchange segmentation also treats a message with a trusted
  origin as a runtime injection (in addition to its prefix heuristic).

### Protocol (coda-proto / coda-serve)

- `HistoryEntry.runtimeOrigin?: { kind, messageId?, taskId?, presentationItemId? }`,
  additive and omitted when absent. `kind` is a string so future kinds never
  break a client. `role` and `entryKind` are unchanged (`user` / `userPrompt`),
  so an old client behaves exactly as before.
- Committed projection copies `Message.origin`; the live accumulator receives
  the structured `MainMessage` origin instead of text alone
  (`push_main_message(id, text, origin)`).

### TUI

- `hydrate_with(entries, live, truncated, covered)`: an entry whose
  `runtimeOrigin.kind == "taskCompletion"` never becomes `Block::User`.
  If its `presentationItemId` is in `covered` (the record ids of the read's
  presentation timeline, or of the records already shown when the engine sent
  no timeline), it produces **no block** — the existing card is the visible
  completion, keeping its own position, revision and expansion. Otherwise it
  produces one `Block::TaskCompletionFallback`: an explicitly historical,
  collapsed one-row header ("Task completion report (card unavailable)
  [task-id]") that expands to the original frame. No title or instructions are
  invented.
- Layout bookkeeping is unchanged: `entry_ends` is recorded for every
  committed entry whether or not it emitted a block, so anchors and coverage
  fences do not shift.
- Foreground and child-owned tasks are untouched: they never reach the main
  inbox, so they never carry this origin.

### Legacy policy

Entries without `runtimeOrigin` (transcripts written before this change, or
from older engines) are rendered exactly as before. Text alone cannot tell an
injected frame from an operator pasting one, so nothing is hidden or
collapsed by heuristic. Only `taskCompletion` origins are acted on; other
runtime frames are out of scope.

## Testing

- Engine: origin set on delivery; frame unchanged for the model; JSON
  round-trip; old JSON loads; malformed/unknown origin ignored.
- Serve: committed and live projections expose `runtimeOrigin`; old DTO
  without it deserializes; end-to-end chain from a real background shell task
  through `MainInboxCompletionPush` → inbox drain → live history → commit →
  save/load.
- TUI: covered entry emits no block and keeps indices; uncovered entry emits
  one bounded fallback; operator prompt with the prefix / a full pasted frame
  stays a user block; identical titles and reused task ids across engines stay
  distinct; refresh does not append another card; existing card tests stay.
