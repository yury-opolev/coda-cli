# Task-completion provenance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A background task's model-facing `[task-completed]` frame is never rendered as an operator prompt; the compact task card is the visible completion.

**Architecture:** Trusted `MessageOrigin` on `coda_llm::Message`, persisted by `message_json`, exposed as additive `HistoryEntry.runtimeOrigin`, consulted by the TUI hydrator. Spec: `docs/superpowers/specs/2026-11-25-task-completion-provenance-design.md`.

**Tech Stack:** Rust workspace under `rust/` (coda-llm, coda-agent, coda-proto, coda-serve, coda-tui).

---

### Task 1: `MessageOrigin` on the neutral message (coda-llm)

**Files:** Modify `rust/crates/coda-llm/src/message.rs`, `rust/crates/coda-llm/src/lib.rs` (re-export).

- [ ] Add:

```rust
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MessageOriginKind { TaskCompletion }
impl MessageOriginKind { pub fn as_str(self) -> &'static str { "taskCompletion" } pub fn parse(s:&str)->Option<Self> }

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MessageOrigin {
    pub kind: MessageOriginKind,
    pub message_id: Option<String>,
    pub task_id: Option<String>,
    pub presentation_item_id: Option<String>,
}
pub struct Message { pub role: Role, pub content: Vec<Content>, pub origin: Option<MessageOrigin> }
impl Message { pub fn with_origin(mut self, origin: Option<MessageOrigin>) -> Self }
```

- [ ] Constructors set `origin: None`. `cargo build --workspace` must stay green.

### Task 2: Origin on the main-inbox completion (coda-agent)

**Files:** `rust/crates/coda-agent/src/message/mod.rs`, `tasks/completion_push.rs`, `agent/mod.rs`, `compaction/tail.rs`.

- [ ] Test (message/tests.rs): bus with `set_presentation_scope("eng-1")`; publish `TaskCompletion{task_id:"task-0001"}`; drained item's `origin()` == `{TaskCompletion, message_id: item.id, task_id:"task-0001", presentation_item_id:"task:eng-1:task-0001"}`; a `Subagent` source has `origin() == None`; without scope, `presentation_item_id == None`.
- [ ] Implement `pub fn task_presentation_item_id(scope:&str, task_id:&str) -> String { format!("task:{scope}:{task_id}") }` in completion_push.rs; `MessageBus::set_presentation_scope(&self, scope)` (OnceLock); `MainMessage.presentation_item_id: Option<String>` filled in `publish_main`; `MainMessage::origin()`.
- [ ] Agent loop step 4c: `history.push(Message::user(item.injected_text()).with_origin(item.origin()))`. Test in agent tests: the pushed message carries the origin and its text equals `injected_text()` (model frame unchanged).
- [ ] tail.rs `is_runtime_injection`: also true when `msg.origin.is_some()`.

### Task 3: Persist origin (coda-agent session/message_json.rs)

- [ ] Tests: round-trip with origin; message without origin writes no `"origin"` key; old JSON without origin loads `None`; unknown `kind` → `None`.
- [ ] Implement `"origin": {"kind","messageId","taskId","presentationItemId"}` (absent optional fields omitted).

### Task 4: Protocol DTO (coda-proto history.rs) + docs/schemas

- [ ] `pub struct RuntimeOrigin { kind: String, message_id/task_id/presentation_item_id: Option<String> }` (camelCase, optional fields skipped); `HistoryEntry.runtime_origin: Option<RuntimeOrigin>` with `#[serde(default, skip_serializing_if)]`; `HistoryEntry::with_runtime_origin`.
- [ ] Tests: absent when None; old JSON without it deserializes; `entryKind` stays `userPrompt`.
- [ ] Regenerate `docs/protocol/schemas/*.json` via `cargo run -p coda-proto --features schema --bin emit_schemas` (not committed bin); update `docs/serve-protocol.md`, `docs/protocol/catalog.md`.

### Task 5: Serve projections (coda-serve history.rs, state/live.rs, state/mod.rs, host.rs, task_events.rs)

- [ ] `project_message` copies `message.origin` → `runtime_origin`.
- [ ] `LiveTurnAccumulator::push_main_message(id, text, origin: Option<RuntimeOrigin>)` replacing `push_main_message_text`; `main_messages_delivered` passes `m.origin()` converted.
- [ ] host.rs: `message_bus.set_presentation_scope(bus.engine_instance_id())`; task_events.rs uses `task_presentation_item_id`.
- [ ] Tests: live entry has origin; committed projection has origin; end-to-end test in host.rs tests: real background shell task → main inbox → turn → `session/getHistory` live and committed entries carry `runtimeOrigin.presentationItemId` equal to the presentation record's `itemId`, text still the full frame; reload after save.

### Task 6: TUI hydration + fallback block (coda-tui)

**Files:** `api/history.rs`, `transcript.rs`, `app/serve.rs`, any exhaustive `Block` matches.

- [ ] `hydrate_with(entries, live, truncated, covered: &HashSet<String>)`; `hydrate` = `hydrate_with(.., &HashSet::new())`.
- [ ] Trusted task-completion entry: covered → no block; not covered → `Block::TaskCompletionFallback { key, task_id, text }` (conversation-owned, foldable via the task-card expansion map, collapsed = one header row).
- [ ] serve.rs: `covered` = record ids in `history.presentation`, or when absent, record ids already in the transcript.
- [ ] Tests: covered hides & keeps `entry_ends`; uncovered → one fallback row collapsed, expanded shows the frame; operator prompt with prefix / full pasted frame stays `Block::User`; two completions same title, reused task id, different engines stay distinct cards with one card each after repeated rehydrate; existing card tests unchanged.

### Task 7: Version, docs, verification

- [ ] `version.json` build 187; README note near the card section.
- [ ] `cargo test --workspace`, `cargo test -p coda-proto --features schema --lib`, release build + engine_contract; zero warnings.
