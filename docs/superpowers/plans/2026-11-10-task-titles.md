# Task Titles and Compact Completion Cards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every new task carries a validated short title, carried end-to-end, and the TUI shows task completions as compact, foldable cards instead of prompt dumps.

**Architecture:** One validation gate (`validate_task_title`) enforced by `TaskManager::register(kind, title, description, parent, mode)`; producers supply a title (the model via the `task` tool, internal producers via `derive_task_title`). The title flows through snapshots, checkpoints, wire events and `session/listTasks`. The TUI gains a client-owned `Block::TaskCompletion` whose expansion state lives in `Transcript` keyed by `(session, task id)`.

**Tech Stack:** Rust workspace under `rust/` (coda-agent, coda-proto, coda-serve, coda-tui, coda-render), ratatui, serde.

Spec: `docs/superpowers/specs/2026-11-10-task-titles-design.md` (decisions D1–D18).

Worktree: `C:\Users\yurio\Documents\github\coda-titles` (branch `feat/task-titles`). All cargo commands run from `rust\`. Zero warnings. Every commit ends with the trailer `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.

---

## File map

- Create `rust/crates/coda-agent/src/tasks/title.rs` — `MAX_TASK_TITLE_CHARS`, `validate_task_title`, `derive_task_title` + unit tests.
- Modify `rust/crates/coda-agent/src/tasks/{mod.rs,managed_task.rs,task_manager.rs,completion_push.rs,live_brief.rs}` — `title` field, `register`/`register_restored` signatures, model-facing labels.
- Modify producers: `tools/task.rs`, `tools/background_task.rs`, `subagents/mod.rs` (`SubagentRequest.title`, `with_title`), `subagents/host.rs`, `subagents/checkpoint.rs`, `tools/run_command.rs`, `agent/tools.rs`, `scheduling/runtime.rs`, `tools/task_list.rs`, `tools/task_get.rs`, `tools/task_recall.rs`.
- Modify every test call of `register(` / `register_restored(` across coda-agent and coda-serve (mechanical).
- Modify `rust/crates/coda-proto/src/{events.rs,responses.rs}`; `rust/crates/coda-serve/src/{task_events.rs,host.rs}`; regenerate `docs/protocol/catalog.json` + `docs/protocol/schemas/*.json`.
- Modify `rust/crates/coda-tui/src/{transcript.rs,state.rs,app/clipboard.rs,browsers.rs,commands.rs}` (+ test literals in `app/task_controls.rs`, `render/draw.rs`).
- Docs: `docs/serve-protocol.md`, `docs/protocol/catalog.md` (if it lists fields), `README.md` task section, `version.json` → 0.1.179.

---

### Task 1: Title validation module

**Files:** Create `rust/crates/coda-agent/src/tasks/title.rs`; modify `rust/crates/coda-agent/src/tasks/mod.rs` (add `mod title; pub use title::{validate_task_title, derive_task_title, MAX_TASK_TITLE_CHARS};`).

- [ ] **Step 1: Write the failing tests** (in `title.rs` `#[cfg(test)] mod tests`):

```rust
#[test] fn accepts_and_trims_a_short_label() {
    assert_eq!(validate_task_title("  Review authentication flow ").unwrap(), "Review authentication flow");
}
#[test] fn rejects_missing_blank_and_whitespace() {
    for t in ["", "   ", "\u{3000}"] { assert!(validate_task_title(t).unwrap_err().contains("Missing required 'title'")); }
}
#[test] fn rejects_line_breaks_and_controls() {
    for t in ["a\nb", "a\rb", "a\tb", "a\u{1b}[2Jb", "a\u{7f}b", "a\u{85}b", "a\u{2028}b", "a\u{2029}b", "a\u{202e}b", "a\u{2066}b"] {
        assert!(validate_task_title(t).unwrap_err().contains("single line"), "{t:?}");
    }
}
#[test] fn bound_is_80_characters_not_bytes() {
    assert!(validate_task_title(&"é".repeat(80)).is_ok());
    assert!(validate_task_title(&"界".repeat(80)).is_ok());
    let err = validate_task_title(&"x".repeat(81)).unwrap_err();
    assert!(err.contains("81") && err.contains("80"), "{err}");
}
#[test] fn derive_takes_first_line_collapses_and_bounds() {
    assert_eq!(derive_task_title("\n  cargo   test -p x\nsecond"), "cargo test -p x");
    assert_eq!(derive_task_title("a\u{1b}b"), "a b");
    let long = derive_task_title(&"y".repeat(200));
    assert_eq!(long.chars().count(), 80);
    assert!(long.ends_with('…'));
    assert!(validate_task_title(&long).is_ok());
    assert_eq!(derive_task_title("   "), "Untitled task");
}
```

- [ ] **Step 2: Run** `cargo test -p coda-agent tasks::title` → FAIL (module missing).

- [ ] **Step 3: Implement** `title.rs`:

```rust
//! Task titles: short single-line labels identifying a task (not its prompt).

/// Longest accepted title, in Unicode scalar values after trimming.
pub const MAX_TASK_TITLE_CHARS: usize = 80;

fn is_forbidden(c: char) -> bool {
    c.is_control()
        || matches!(c, '\u{2028}' | '\u{2029}' | '\u{202A}'..='\u{202E}' | '\u{2066}'..='\u{2069}')
}

/// Validates a task title, returning it trimmed.
pub fn validate_task_title(raw: &str) -> Result<String, String> {
    let title = raw.trim();
    if title.is_empty() {
        return Err("Missing required 'title': give the task a short single-line label \
                    (3-8 words, at most 80 characters), for example \"Review authentication flow\"."
            .to_owned());
    }
    if title.chars().any(is_forbidden) {
        return Err("Invalid task title: it must be a single line without control characters.".to_owned());
    }
    let count = title.chars().count();
    if count > MAX_TASK_TITLE_CHARS {
        return Err(format!(
            "Invalid task title: it is {count} characters; the limit is {MAX_TASK_TITLE_CHARS}. \
             Use a short label (3-8 words) and keep details in the prompt."
        ));
    }
    Ok(title.to_owned())
}

/// A valid title derived deterministically from `text` for internal producers.
pub fn derive_task_title(text: &str) -> String {
    let line = text.lines().map(str::trim).find(|l| !l.is_empty()).unwrap_or("");
    let cleaned: String = line.chars().map(|c| if is_forbidden(c) { ' ' } else { c }).collect();
    let collapsed = cleaned.split_whitespace().collect::<Vec<_>>().join(" ");
    if collapsed.is_empty() {
        return "Untitled task".to_owned();
    }
    if collapsed.chars().count() <= MAX_TASK_TITLE_CHARS {
        return collapsed;
    }
    let mut out: String = collapsed.chars().take(MAX_TASK_TITLE_CHARS - 1).collect();
    out = out.trim_end().to_owned();
    out.push('…');
    out
}
```

Note `"a\u{1b}b"` → ESC is replaced by a space → `"a b"`.

- [ ] **Step 4: Run** `cargo test -p coda-agent tasks::title` → PASS.
- [ ] **Step 5: Commit** `feat(tasks): task title validation and derivation`.

### Task 2: Title on ManagedTask / TaskSnapshot / register

**Files:** `tasks/managed_task.rs`, `tasks/task_manager.rs`, all `register(`/`register_restored(` call sites (coda-agent and coda-serve, production and tests).

- [ ] **Step 1: Failing tests** in `task_manager.rs` tests:

```rust
#[test]
fn register_rejects_an_invalid_title_before_issuing_an_id() {
    let m = TaskManager::with_defaults("titles");
    for bad in ["", "  ", "two\nlines"] {
        assert!(m.register(TaskKind::Subagent, bad, "prompt", None, TaskExecutionMode::Background).is_err());
    }
    assert!(m.list().is_empty());
    let ok = m.register(TaskKind::Subagent, " Review auth ", "full\nprompt", None, TaskExecutionMode::Background).unwrap();
    assert_eq!(ok.id, "task-00000001", "rejected attempts consumed no id");
    let snap = m.get(&ok.id).unwrap();
    assert_eq!(snap.title.as_deref(), Some("Review auth"));
    assert_eq!(snap.description, "full\nprompt");
}

#[test]
fn duplicate_titles_are_distinct_tasks() {
    let m = TaskManager::with_defaults("dups");
    let a = m.register(TaskKind::Shell, "build", "a", None, TaskExecutionMode::Background).unwrap();
    let b = m.register(TaskKind::Shell, "build", "b", None, TaskExecutionMode::Background).unwrap();
    assert_ne!(a.id, b.id);
}

#[test]
fn restored_task_without_title_is_legacy_and_invalid_stored_title_is_ignored() {
    let m = TaskManager::with_defaults("legacy");
    let t = m.register_restored("task-00000005", TaskKind::Subagent, None, "old prompt", None,
        TaskExecutionMode::Background, std::time::SystemTime::now(), false).unwrap();
    assert_eq!(m.get(&t.id).unwrap().title, None);
    let u = m.register_restored("task-00000006", TaskKind::Subagent, Some("bad\ntitle"), "p", None,
        TaskExecutionMode::Background, std::time::SystemTime::now(), false).unwrap();
    assert_eq!(m.get(&u.id).unwrap().title, None);
    let v = m.register_restored("task-00000007", TaskKind::Subagent, Some("Kept title"), "p", None,
        TaskExecutionMode::Background, std::time::SystemTime::now(), false).unwrap();
    assert_eq!(m.get(&v.id).unwrap().title.as_deref(), Some("Kept title"));
}
```
(Check `with_defaults` id format; if ids start elsewhere, assert against the first id issued by a fresh manager.)

- [ ] **Step 2: Implement.**
  - `ManagedTask`: add `pub title: Option<String>` after `kind`; `new`/`new_started_at` take `title: Option<String>` right after `kind`. `TaskSnapshot`: add `pub title: Option<String>`; `to_snapshot` copies it. Also `TaskManager::snapshot_of` if it builds snapshots separately.
  - `TaskCompletionEntry`: add `pub title: Option<String>` (filled from `task.title.clone()` where built, ~line 1231).
  - `register(&self, kind, title: &str, description: impl Into<String>, parent_task_id, mode)`: first statement `let title = super::validate_task_title(title)?;` then pass `Some(title)`.
  - `register_restored(&self, id, kind, title: Option<&str>, description, ...)`: `let title = title.and_then(|t| super::validate_task_title(t).ok());`.
  - Production call sites:
    - `agent/tools.rs` `start_tool_task`: `.register(TaskKind::Tool, &crate::tasks::derive_task_title(tool.name()), tool.name(), ...)`.
    - `tools/run_command.rs` `start_shell_task`: `.register(TaskKind::Shell, &crate::tasks::derive_task_title(command), describe(command), ...)`.
    - `scheduling/runtime.rs` `TaskManagerRunner::start`: `.register(TaskKind::Scheduled, &crate::tasks::derive_task_title(&run.description), &run.description, ...)`.
    - `subagents/host.rs` spawn: `.register(TaskKind::Subagent, &request.title, &request.prompt, ...)` (Task 3 adds `title`); `prepare_restore`: `register_restored(&cp.task_id, TaskKind::Subagent, cp.title.as_deref(), cp.description.clone(), ...)` (Task 3 adds `cp.title`).
  - Test call sites: mechanical rewrite with a throwaway Python script (not committed) turning `.register(<kind>, <expr>,` into `.register(<kind>, <expr>, <expr>,` where `<expr>` is a string literal; fix the remaining compile errors by hand (`format!(..)` → bind to a local and pass `&s, s.clone()`); `register_restored(id, kind, desc, ...)` → `register_restored(id, kind, Some(desc), desc, ...)` unless the test is about legacy. Any test description longer than 80 chars or containing newlines must use a short title instead.
- [ ] **Step 3:** `cargo test -p coda-agent` and `cargo test -p coda-serve` (compile + pass).
- [ ] **Step 4: Commit** `feat(tasks): carry a validated title on every registered task`.

### Task 3: Subagent producers, checkpoint, model-facing labels

**Files:** `subagents/mod.rs`, `tools/task.rs`, `tools/background_task.rs`, `subagents/host.rs`, `subagents/checkpoint.rs`, `tasks/completion_push.rs`, `tasks/live_brief.rs`, `tools/task_list.rs`, `tools/task_get.rs`, `tools/task_recall.rs`.

- [ ] **Step 1: Failing tests**
  - `tools/task.rs`:
```rust
#[tokio::test]
async fn task_tool_requires_a_valid_title_before_spawning() {
    let seen = Arc::new(std::sync::Mutex::new(Vec::new()));
    let ctx = ToolContext::new(".").with_subagent_factory(Arc::new(RecordingFactory { seen: Arc::clone(&seen) }));
    for input in [json!({"prompt": "p"}), json!({"prompt": "p", "title": "  "}),
                  json!({"prompt": "p", "title": "a\nb"}), json!({"prompt": "p", "title": "x".repeat(81)})] {
        let r = TaskTool.execute(&input, &ctx, CancellationToken::new()).await;
        assert!(r.is_error && r.content.contains("title"), "{}", r.content);
    }
    assert!(seen.lock().unwrap().is_empty());
    let r = TaskTool.execute(&json!({"prompt": "long\nprompt", "title": "Review auth"}), &ctx, CancellationToken::new()).await;
    assert!(!r.is_error, "{}", r.content);
    let req = seen.lock().unwrap()[0].clone();
    assert_eq!(req.title, "Review auth");
    assert_eq!(req.prompt, "long\nprompt", "prompt passes through unchanged");
}
#[test]
fn task_schemas_require_title() {
    for schema in [TaskTool.input_schema_json(), crate::tools::background_task::BackgroundTaskStartTool.input_schema_json()] {
        let v: serde_json::Value = serde_json::from_str(schema).unwrap();
        assert!(v["required"].as_array().unwrap().iter().any(|r| r == "title"));
        assert_eq!(v["properties"]["title"]["maxLength"], 80);
    }
}
```
  Existing task.rs tests that pass `{"prompt": ...}` and expect success must add `"title": "..."`.
  - `subagents/host.rs`: a background spawn of `SubagentRequest` with empty `title` returns `Err` containing `"title"` and registers nothing (`mgr.list().is_empty()`); with a title the registered snapshot has it. Existing background-spawn tests built from `SubagentRequest::foreground(..)` + `foreground = false` must add `.with_title("…")`.
  - `subagents/checkpoint.rs`: `from_task` copies `title`; serde round-trip keeps it; a JSON without `"title"` deserializes with `title: None` (legacy) and restore registers `title: None`.
  - `completion_push.rs`/`task_list.rs`: titled task shows the title in the frame label / list row, not the prompt's first line.
- [ ] **Step 2: Implement**
  - `SubagentRequest`: `pub title: String` (doc: "Short task label; required for a background spawn, validated by the task manager"); `foreground()` sets `String::new()`; `pub fn with_title(mut self, title: impl Into<String>) -> Self`.
  - `start_subagent`: right after the prompt check: `let title = match crate::tasks::validate_task_title(input.get("title").and_then(Value::as_str).unwrap_or("")) { Ok(t) => t, Err(e) => return ToolResult::error(e) };` and set `title` in the request literal.
  - Schemas: add to `TaskTool` and `BackgroundTaskStartTool` properties `"title": {"type":"string","maxLength":80,"description":"Required. A short single-line label for this task (3-8 words, at most 80 characters), e.g. \"Review authentication flow\". Shown in task lists and completion notices; put the details in prompt."}` and `"required": ["prompt","title"]`. Mention in `TaskTool::description`: "Give every task a short title."
  - Host `prepare_restore`: request `title: cp.title.clone().unwrap_or_default()`.
  - `SubagentCheckpoint`: `#[serde(default, skip_serializing_if = "Option::is_none")] pub title: Option<String>` after `kind`; `from_task` sets `title: task.title.clone()`. Update any struct literals in tests.
  - Model-facing labels: helper on `TaskSnapshot`/`ManagedTask`: `pub fn label(&self) -> &str { self.title.as_deref().unwrap_or(&self.description) }`. Use it in `completion_push.rs` (`label: task.label().to_owned()`, `completion_frame(&task.id, task.label(), ..)`), `live_brief.rs` (both `one_line(&s.description, ..)` → `one_line(s.label(), ..)`), `task_list.rs` row, `task_recall.rs` (`e.title.as_deref().unwrap_or(&e.description)`). `task_get.rs`: insert `format!("title: {}", snap.title.as_deref().unwrap_or("(none)"))` before the description line.
- [ ] **Step 3:** `cargo test -p coda-agent` → PASS.
- [ ] **Step 4: Commit** `feat(tasks): require titles from the task tool and carry them through checkpoints`.

### Task 4: Wire protocol and serve

**Files:** `coda-proto/src/events.rs`, `coda-proto/src/responses.rs`, `coda-serve/src/task_events.rs`, `coda-serve/src/host.rs` (~5249 DTO), schemas.

- [ ] **Step 1: Failing tests**
  - events.rs: round-trip `TaskCompleted`/`TaskStarted` with `title: Some("Review auth")` and with `None`; JSON `{"taskId":"t","status":"completed","description":"x"}` parses to `title: None`; serialized `None` omits `"title"`.
  - task_events.rs: a registered titled task publishes `taskStarted.params.title == "deep review"` and `taskCompleted.params.title`; restored legacy task has no `title` key.
  - host.rs: listTasks row carries `title`.
- [ ] **Step 2: Implement**
  - `Event::TaskCompleted { .., title: Option<String>, .. }` and `Event::TaskStarted { .., title: Option<String>, .. }` (doc: "Short task label; absent for legacy tasks and older senders"). Payload structs gain `#[serde(default)] title: Option<String>`; parse copies; `to_notification` uses `put_opt(&mut map, "title", title)`.
  - `TaskSummaryDto`: `#[serde(default, skip_serializing_if = "Option::is_none")] pub title: Option<String>` after `kind`.
  - Publisher: `title: task.title.clone()` in both events. Host DTO: `title: snap.title.clone()`.
  - Fix every struct literal/pattern in coda-tui (`render/draw.rs` tests, `app/task_controls.rs`) and coda-proto tests (`title: None`).
  - Regenerate schemas (procedure in `docs/superpowers/plans/2026-11-02-planned-restart.md` Task 10): temporary untracked `rust/crates/coda-proto/src/bin/emit_schemas.rs` writing `coda_proto::schema::documents()` to `docs/protocol/schemas/<name>` and `catalog_document()` to `docs/protocol/catalog.json` (pretty JSON + trailing newline); `cargo run -p coda-proto --features schema --bin emit_schemas`; delete the emitter; `cargo test -p coda-proto --features schema` passes.
- [ ] **Step 3:** `cargo test -p coda-proto --features schema; cargo test -p coda-serve -p coda-proto; cargo test -p coda-tui --no-run`.
- [ ] **Step 4: Commit** `feat(proto): carry task titles on task events and listTasks rows`.

### Task 5: TUI completion card

**Files:** `coda-tui/src/transcript.rs`, `coda-tui/src/state.rs`, `coda-tui/src/app/clipboard.rs`.

- [ ] **Step 1: Failing tests** (transcript.rs tests unless noted):
```rust
fn card(title: Option<&str>, instructions: &str) -> Block {
    Block::TaskCompletion { session_id: Some("s1".into()), task_id: "task-00000007".into(),
        status: "completed".into(), kind: Some("subagent".into()),
        title: title.map(str::to_owned), instructions: instructions.into() }
}
#[test] fn titled_card_with_long_multiline_prompt_is_one_collapsed_row() {
    let prompt = "Line one of a very long prompt\n".repeat(50);
    let rows = card(Some("Review authentication flow"), &prompt).render_card(80, false);
    assert_eq!(rows.len(), 1);
    let text = &rows[0].text;
    assert!(text.contains("Task completed: Review authentication flow") && text.contains("[task-00000007]"), "{text}");
    assert!(!text.contains("Line one"));
}
#[test] fn narrow_widths_keep_status_and_id_and_never_exceed_width() {
    let c = card(Some("界界界 Review authentication flow with a long title"), "p");
    for w in [1usize, 5, 12, 20, 30, 44, 60] {
        let rows = c.render_card(w, false);
        assert_eq!(rows.len(), 1);
        assert!(coda_render::text::width(&rows[0].text) <= w, "w={w}");
    }
    let r30 = &c.render_card(30, false)[0].text;
    assert!(r30.contains("completed") && r30.contains("task-00000007"), "{r30}");
    let r44 = &c.render_card(44, false)[0].text;
    assert!(r44.contains('…') && r44.contains("[task-00000007]"), "{r44}");
}
#[test] fn expanded_card_shows_labelled_full_instructions() {
    let rows = card(Some("Review auth"), "step one\nstep two").render_card(60, true);
    let texts: Vec<_> = rows.iter().map(|r| r.text.trim().to_owned()).collect();
    assert!(texts.contains(&"Instructions".to_owned()));
    assert!(texts.contains(&"step one".to_owned()) && texts.contains(&"step two".to_owned()));
}
#[test] fn legacy_card_is_at_most_three_rows_with_ellipsis() {
    let long = "word ".repeat(200);
    let rows = card(None, &long).render_card(40, false);
    assert_eq!(rows.len(), 3);
    assert!(rows[0].text.contains("Task completed") && rows[0].text.contains("[task-00000007]"));
    assert!(rows[2].text.trim_end().ends_with('…'));
    assert!(rows.iter().all(|r| coda_render::text::width(&r.text) <= 40));
    let short = card(None, "fix it").render_card(40, false);
    assert_eq!(short.len(), 2);
    assert!(!short[1].text.contains('…'));
    assert!(card(None, &long).render_card(40, true).len() > 3);
}
#[test] fn statuses_are_explicit_words() {
    for s in ["completed", "failed", "stopped"] {
        let mut c = card(Some("T"), "p");
        if let Block::TaskCompletion { status, .. } = &mut c { *status = s.into(); }
        assert!(c.render_card(60, false)[0].text.contains(&format!("Task {s}: T")));
    }
}
#[test] fn expansion_is_keyed_by_session_and_task_and_survives_rebuilds() {
    let mut t = Transcript::new();
    t.push(card(Some("Same title"), "first"));
    let mut other = card(Some("Same title"), "second");
    if let Block::TaskCompletion { task_id, .. } = &mut other { *task_id = "task-00000008".into(); }
    t.push(other);
    assert!(t.toggle_task_card(0));
    t.replace_conversation(vec![Block::Assistant { text: "hi".into(), complete: true }]);
    let rows = t.render(60, ToolDisplayMode::Summary);
    let all: String = rows.iter().map(|r| r.text.clone()).collect::<Vec<_>>().join("\n");
    assert!(all.contains("first"), "first card stays expanded after rehydration");
    assert!(!all.contains("second"), "the other card with the same title stays collapsed");
}
#[test] fn copying_a_collapsed_card_excludes_marker_and_hidden_instructions() {
    let rows = card(Some("Review auth"), "secret details").render_card(60, false);
    let copied = crate::selection::copy_rows_text(&rows); // use the same copy helper selection uses; adapt name
    assert!(!copied.contains("secret") && !copied.contains('▸') && copied.contains("Task completed: Review auth"));
}
```
  state.rs tests: background main-agent `TaskCompleted` with `title` pushes one `Block::TaskCompletion` keyed by current `session_id`; foreground mode and `owner_task_id: Some` push nothing (existing suppression tests keep passing after adapting their expectations from `Notice` to the card); a repeated `TaskCompleted` for the same task updates in place (one block); toggling one card via `UiEvent::TaskCardFoldToggled { block }` does not change the other; a card survives `UiEvent::Rehydrated` with its expansion.
  clipboard.rs: `header_block_at` + `is_task_card` → a click on the card header toggles, a click on an instructions row does not (selection begins).

- [ ] **Step 2: Implement**
  - `Block::TaskCompletion { session_id: Option<String>, task_id: String, status: String, kind: Option<String>, title: Option<String>, instructions: String }`; listed in `is_client_owned`.
  - `Transcript.expanded_task_cards: BTreeSet<(String, String)>` (key `(session_id.unwrap_or_default(), task_id)`); `clear()` empties it; `replace_conversation` leaves it; `pub fn toggle_task_card(&mut self, index) -> bool`; `pub fn is_task_card(&self, index) -> bool`; `pub fn upsert_task_card(&mut self, block: Block)` (replace an existing card with the same key in place, else `push_notice`-style insert before an open tail).
  - `Block::render` for `TaskCompletion` renders collapsed (the per-block render has no transcript state); `render_pass` calls `block.render_card(width, expanded)` for task cards with `expanded` looked up from the set. `render_card(&self, width, expanded) -> Vec<RenderLine>`:
```rust
fn task_card_header(status: &str, title: Option<&str>, id: &str, budget: usize) -> String {
    use coda_render::text::{truncate_with_ellipsis, width as cells};
    let tag = format!("[{id}]");
    match title {
        Some(title) => {
            let lead = format!("Task {status}: ");
            let fixed = cells(&lead) + 2 + cells(&tag);
            if budget >= fixed + 4 {
                let t = truncate_with_ellipsis(title, budget - fixed);
                return format!("{lead}{t}  {tag}");
            }
        }
        None => {
            let full = format!("Task {status}  {tag}");
            if cells(&full) <= budget { return full; }
        }
    }
    truncate_with_ellipsis(&format!("Task {status} {tag}"), budget)
}
```
    Header: marker `glyphs::FOLD_EXPANDED`/`FOLD_COLLAPSED`; `budget = width.saturating_sub(3)` (disclosure prefix ` ▸ ` is 3 cells); `coda_render::line::disclosure_header(&headline, marker, width, role)` and keep only its first row (headline already fits). Role: `Role::Error`-equivalent of `NoticeLevel::Error.role()` for failed, else `NoticeLevel::Info.role()`.
    Legacy preview (collapsed, `title == None`, non-empty instructions): `flat = instructions.split_whitespace().join(" ")`, `content = width.saturating_sub(3).max(1)`, `lines = text::wrap(&flat, content)`; take 2; if `lines.len() > 2` the 2nd becomes `truncate_with_ellipsis(&format!("{} …", lines[1]) …)` — precisely: `let mut second = lines[1].clone(); second.push_str(" …"); truncate_with_ellipsis(&second, content)` but guarantee it ends with `…` (if the push overflowed, `truncate_with_ellipsis` already ends with `…`). Rows use `Gutter::Continuation` and `Role::Notification`.
    Expanded: header, then a `RenderLine::new("Instructions", Role::ThinkingHeader)` with `Gutter::Continuation`, then for each `instructions.lines()` → `text::wrap_preformatted(&text::sanitize(line), content)` rows with `Role::Notification` + `Gutter::Continuation` (`Gutter::None` and full width when `width <= 3`). Empty instructions → `(no instructions recorded)`.
  - state.rs `Event::TaskCompleted`: keep the early return on foreground/owner; then `self.transcript.upsert_task_card(Block::TaskCompletion { session_id: self.session_id.clone(), task_id, status, kind, title: title.filter(|t| !t.trim().is_empty()), instructions: description })`. Add `UiEvent::TaskCardFoldToggled { block: usize }` handled by `self.transcript.toggle_task_card(block)` and listed with the other fold events wherever `ThinkingFoldToggled` is listed.
  - clipboard.rs `toggle_fold_at_click`: before the tool-group branch, `else if self.state.transcript.is_task_card(index) { UiEvent::TaskCardFoldToggled { block: index } }`. Confirm `header_block_at` only matches the block's first row.
- [ ] **Step 3:** `cargo test -p coda-tui -p coda-render` → PASS, zero warnings.
- [ ] **Step 4: Commit** `feat(tui): compact foldable task completion cards`.

### Task 6: /tasks details, help, docs, version

**Files:** `coda-tui/src/browsers.rs`, `coda-tui/src/commands.rs`, `docs/serve-protocol.md`, `README.md`, `version.json`.

- [ ] **Step 1: Failing test** in browsers.rs: a `TaskSummaryDto` with `title: Some("Review auth")` and a multi-line description → row cells contain `"Review auth"`; detail contains `"title       Review auth"`, a line `"Instructions"` followed by every description line; a legacy row (`title: None`) shows the description's first line in the list and `title       (none)` in detail.
- [ ] **Step 2: Implement**: column `"title"`; cell `task.title.clone().unwrap_or_else(|| first non-empty description line)`; detail replaces `description …` with `title       …`, then after parent: blank, `Instructions`, description lines; footer `Enter details` instead of `Enter output`. commands.rs `/tasks` summary: `"Browse active tasks (all: include finished ones). Enter on a task shows its title, full instructions and report."`.
- [ ] **Step 3: Docs**: `docs/serve-protocol.md` — `title` on `event/taskStarted`, `event/taskCompleted`, listTasks rows; a "Task titles" subsection stating the creation contract (required for every new task; rules D1–D2; error text; `task`/`task_start` require `title`; legacy records may lack it, creation never may). README task section: one paragraph on titles and completion cards (`/tasks all` → Enter for instructions). `version.json` build → 179.
- [ ] **Step 4:** `cargo test -p coda-tui`, then `cargo test --workspace`; build `cargo build --release -p coda` and run `$env:CODA_ENGINE="<rust\target\release\coda.exe>"; cargo test -p coda-tui --test engine_contract`.
- [ ] **Step 5: Commit** `feat(tui): task titles and instructions in /tasks details; docs; bump to 0.1.179`.
